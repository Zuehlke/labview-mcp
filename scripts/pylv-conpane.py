"""Read and REPAIR a VI's connector pane in a pylabview-extracted bundle - no LabVIEW.

WHY THIS EXISTS. `lvai_connector_pane` states the rule and measures a VI, but the only way it
offers to act on the answer is "write these conIdx values into the AIXML and regenerate". That
is not always available: a VI reached through the pylabview route is being edited precisely
because regenerating it is not an option, and regenerating a subVI also re-links every caller.
So the rule could be checked but not applied. This applies it.

THE FINDING THAT MADE IT SMALL. A pane has TWO independent halves, and the repository only ever
thought about one of them:

  * WHICH TERMINAL sits at which `conIdx`  - the assignment, in the front-panel heap's
    `conPane/cons` array and mirrored in the pane's own Function type descriptor.
  * WHICH PATTERN the `conIdx` numbers refer to - `<conId>`, a single number, plus the two-byte
    `Pattern` code on that same Function type descriptor.

`WriteWaveformsToCSV.vi` was generated with an assignment cloned terminal-for-terminal from a
style-compliant NI VI, and `lvai_connector_pane` still reported five violations - because the
generator had stamped it `conId` 4833 (the station's `DefaultConPane`) while the assignment it
carried was a 4815 one. Nothing was wrong with the assignment. Changing 4833 -> 4815 and
touching no terminal turned "5 violations" into "Nothing to change", measured 2026-08-24.

That is the whole repair this script performs, and it leaves every `conIdx` untouched, so **no
caller has to change**.

MOVING TERMINALS IS NOT OFFERED, AND THAT IS A MEASUREMENT. An earlier version had `--reindex`,
which permuted the assignment into NI's order, and `--follow`, which permuted a caller's
`paramIdx` values to match. Both produced files that re-extracted cleanly and read back exactly
as intended. Both **killed LabVIEW on load** - `LabVIEW.exe` gone from the process table, twice,
on 2026-08-24:

  1. a standalone VI re-indexed on pattern 4833, on the probe that measured it;
  2. a subVI re-indexed on 4815 with its caller followed, again on the first probe.

Between those two, dozens of `--pattern` changes, subVI retargets, comment placements and full
runs went through untouched, so the correlation is with the permutation and nothing else. The
first occurrence was written off as circumstantial; the second settled it. What in a permuted
`cons` array LabVIEW cannot survive is NOT established - the array is re-rendered by the same code
path in both modes, and the only difference is that the mapping is not the identity.

So: an assignment that is genuinely wrong is fixed by regenerating from AIXML with corrected
`conIdx` values, which `lvai_connector_pane` prints ready to paste. That route is proven and
costs a regeneration. This script does the half that regeneration CANNOT do.

usage:
  pylv-conpane.py <bundle|main.xml> --show
  pylv-conpane.py <bundle|main.xml> --pattern <conId>      change pattern, keep every conIdx

`--show` is read-only. `--pattern` rewrites the bundle in place; run `pylv_rebuild` afterwards,
and remember that `pylv_rebuild` reporting `ok` verifies nothing - measure the result with
`lvai_connector_pane` once LabVIEW has loaded it.

HOW FAR EACH MODE IS VERIFIED, 2026-08-24:

  --show     PROVEN on two panes. It returns `lvai_connector_pane`'s findings and its corrected
             assignment, terminal for terminal, on a failing pane and on a clean one.
  --pattern  PROVEN end to end, twice. 4833 -> 4815 on `WriteWaveformsToCSV.vi`, rebuilt, loaded
             by LabVIEW, measured "Nothing to change" - and the VI then ran and wrote its CSV.
"""

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
GEOMETRY_TSV = os.path.join(HERE, "..", "docs", "connector-pane-patterns.tsv")
PATTERN_TSV = os.path.join(HERE, "..", "docs", "connector-pane-typecodes.tsv")

IS_OUTPUT = 0x0100          # the only bit in a pane terminal's Flags that names its direction
REQUIRED = 0x1000
RECOMMENDED = 0x0800


# --------------------------------------------------------------------------- tables

def load_geometry():
    """conId -> {conIdx: (left, top, right, bottom)}, from the harvested pane table.

    The geometry is the authority, never the pattern id: a pane can be rotated, and a written-down
    map of "which conIdx is where" has been wrong three times in this repository.
    """
    if not os.path.exists(GEOMETRY_TSV):
        raise SystemExit(
            "cannot find %s.\n"
            "This table is DATA, not documentation - it is read at run time and there is no "
            "fallback, because a guessed slot map is what put two inputs on the output edge in the "
            "first place. It must sit at <this script>\\..\\docs, which is where both the repository "
            "and the plugin staging tree put it." % GEOMETRY_TSV)

    out = {}
    with open(GEOMETRY_TSV, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            cols = line.rstrip("\n").split("\t")
            # the table carries a bare column-name row, not commented out
            if len(cols) < 9 or not cols[0].isdigit() or cols[8] in ("", "-"):
                continue
            slots = {}
            for field in cols[8].split(";"):
                idx, rect = field.split(":", 1)
                slots[int(idx)] = tuple(int(v) for v in rect.split(","))
            out[int(cols[0])] = slots
    return out


def load_pattern_codes():
    """conId -> the two-byte `Pattern` code that the pane's Function TypeDesc carries.

    MEASURED, not derived. Nothing in the file relates the two numbers - 4815 pairs with 0x78 and
    4833 with 0x108 - so a conId missing from this table is refused rather than guessed.
    """
    out = {}
    if not os.path.exists(PATTERN_TSV):
        return out
    with open(PATTERN_TSV, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            cols = line.rstrip("\n").split("\t")
            if len(cols) < 2:
                continue
            out[int(cols[0])] = int(cols[1], 0)
    return out


# --------------------------------------------------------------------------- bundle

class Bundle:
    """The two files a connector pane is spread across, held as TEXT.

    Deliberately not an XML tree. pylabview's XML round trip is byte-exact only if it writes the
    file itself; editing through a parser reflows attributes and line endings, and the LF endings
    matter (see CLAUDE.md). Every edit here is an anchored substring replacement.
    """

    def __init__(self, path):
        if os.path.isdir(path):
            mains = [n for n in os.listdir(path)
                     if n.endswith(".xml") and not re.search(r"_(BDHb|FPHb)\.xml$", n)]
            if len(mains) != 1:
                raise SystemExit("cannot tell which is the main XML in %s: %s" % (path, mains))
            self.main_path = os.path.join(path, mains[0])
        else:
            self.main_path = path
        self.stem = os.path.splitext(os.path.basename(self.main_path))[0]
        self.dir = os.path.dirname(os.path.abspath(self.main_path))
        self.fp_path = os.path.join(self.dir, self.stem + "_FPHb.xml")
        self.bd_path = os.path.join(self.dir, self.stem + "_BDHb.xml")
        self.main = read(self.main_path)
        self.fp = read(self.fp_path) if os.path.exists(self.fp_path) else None
        self.bd = read(self.bd_path) if os.path.exists(self.bd_path) else None

    def save(self):
        write(self.main_path, self.main)
        if self.fp is not None:
            write(self.fp_path, self.fp)
        if self.bd is not None:
            write(self.bd_path, self.bd)


def read(path):
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write(path, text):
    # newline="" keeps the LF endings pylabview wrote. Text mode would turn them into CRLF on
    # Windows and every line of a 20 000-line heap would then differ, hiding the one that changed.
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


# --------------------------------------------------------------------------- reading the pane

CONS_RE = re.compile(
    r'(?P<head><cons elements="(?P<count>\d+)">)(?P<body>.*?)(?P<tail></cons>)', re.S)
CONS_ELEM_RE = re.compile(
    r'<SL__arrayElement class="ConpaneConnection"(?: index="(?P<index>\d+)")?>\s*'
    r'<ConnectionDCO uid="(?P<uid>\d+)" />\s*</SL__arrayElement>', re.S)


def read_conpane(fp_text):
    """(conId, {conIdx: dcoUid}, slotCount) out of the front-panel heap.

    The array is SPARSE in pylabview's rendering: an element carries `index=` only when it jumps,
    otherwise it continues from the previous one. An unlisted index is an unconnected slot.
    """
    con_id = int(re.search(r"<conId>(\d+)</conId>", fp_text).group(1))
    block = CONS_RE.search(fp_text)
    if not block:
        raise SystemExit("no <cons> array in the front-panel heap")
    cursor, cons = 0, {}
    for m in CONS_ELEM_RE.finditer(block.group("body")):
        if m.group("index") is not None:
            cursor = int(m.group("index"))
        cons[cursor] = int(m.group("uid"))
        cursor += 1
    return con_id, cons, int(block.group("count"))


def read_ddo_order(fp_text):
    """The panel's `ddoList`, which is the order the controls were CREATED.

    That matters because NI's style guide places terminals in the author's order, and the pane
    itself cannot supply it - reading the slots top to bottom re-sorts a wrong pane by its own
    wrongness. Checked against the AIXML this VI was generated from: the ten `ddoList` uids come
    out in exactly the order the ten `Control`/`Indicator` elements were written.
    """
    block = re.search(r'<ddoList elements="\d+">(.*?)</ddoList>', fp_text, re.S)
    if not block:
        return []
    return [int(m) for m in re.findall(r'<SL__arrayElement uid="(\d+)" />', block.group(1))]


def read_dco_names(fp_text):
    """fPDCO uid -> the terminal's label.

    The label wanted is the one on the DCO's OWN parts list. A cluster carries its fields' labels
    further down, so the search stops at the first `partID` 16 after the fPDCO opens.
    """
    names = {}
    for m in re.finditer(r'<SL__arrayElement class="fPDCO" uid="(\d+)">', fp_text):
        uid = int(m.group(1))
        window = fp_text[m.end():m.end() + 4000]
        label = re.search(r"<partID>16</partID>.*?<text>\"(.*?)\"</text>", window, re.S)
        names[uid] = label.group(1) if label else "?"
    return names


FUNC_RE = re.compile(
    r'(?P<open><TypeDesc Type="Function"[^>]*Pattern="(?P<pattern>0x[0-9A-Fa-f]+)"[^>]*>)'
    r'(?P<body>.*?)(?P<close></TypeDesc>)', re.S)
CHILD_RE = re.compile(r'<TypeDesc TypeID="(?P<tid>\d+)" Flags="(?P<flags>0x[0-9A-Fa-f]+)" />')


def find_pane_type(main_text):
    """The VI's own pane Function TypeDesc: (start, end, pattern, [(typeId, flags), ...]).

    Reached the way LabVIEW reaches it - CONP names a consolidated TypeID, VCTP's TopLevel maps
    that to a FlatTypeID, and the flat entry is the function type whose children ARE the slots, in
    conIdx order. Guessing "the Function type with the most children" would pick a subVI's pane
    just as often.
    """
    conp = re.search(r"<CONP>.*?<TypeDesc TypeID=\"(\d+)\"", main_text, re.S)
    if not conp:
        raise SystemExit("no CONP block - this VI has no connector pane type")
    top = re.search(r'<TypeDesc Index="%s" FlatTypeID="(\d+)" />' % conp.group(1), main_text)
    if not top:
        raise SystemExit("CONP names consolidated type %s, which VCTP's TopLevel does not list"
                         % conp.group(1))
    return locate_flat_function(main_text, int(top.group(1)))


def locate_flat_function(main_text, flat_id):
    marker = re.search(r"<!-- FlatTypeID %d:[^>]*-->" % flat_id, main_text)
    if not marker:
        raise SystemExit("VCTP has no FlatTypeID %d" % flat_id)
    func = FUNC_RE.search(main_text, marker.end())
    if not func or func.start() > marker.end() + 200:
        raise SystemExit("FlatTypeID %d is not a Function type" % flat_id)
    children = [(int(c.group("tid")), int(c.group("flags"), 0))
                for c in CHILD_RE.finditer(func.group("body"))]
    return func.start(), func.end(), int(func.group("pattern"), 0), children


def void_type_id(main_text):
    m = re.search(r'<!-- FlatTypeID (\d+): [^>]*-->\s*<TypeDesc Type="Void"', main_text)
    if not m:
        raise SystemExit("no Void type in VCTP - cannot describe an empty pane slot")
    return int(m.group(1))


class Pane:
    def __init__(self, bundle):
        self.bundle = bundle
        self.con_id, self.cons, self.slot_count = read_conpane(bundle.fp)
        self.names = read_dco_names(bundle.fp)
        self.ddo_order = read_ddo_order(bundle.fp)
        self.start, self.end, self.pattern, self.children = find_pane_type(bundle.main)
        if len(self.children) != self.slot_count:
            raise SystemExit("pane disagrees with itself: %d heap slots, %d type slots"
                             % (self.slot_count, len(self.children)))

    def terminals(self):
        """[(conIdx, name, isOutput, typeId, flags)] for the ASSIGNED slots, in AUTHOR order.

        Author order is `ddoList` order; a control that is somehow not in it falls to the back by
        conIdx, so a hand-edited heap still produces an answer rather than an exception.
        """
        rank = {uid: n for n, uid in enumerate(self.ddo_order)}
        out = []
        for idx in sorted(self.cons, key=lambda i: (rank.get(self.cons[i], 10_000), i)):
            tid, flags = self.children[idx]
            out.append((idx, self.names.get(self.cons[idx], "?"),
                        bool(flags & IS_OUTPUT), tid, flags))
        return out


# --------------------------------------------------------------------------- the style guide

def edges(slots, indices):
    """Left edge, right edge and middles for a pattern, each top to bottom.

    Same classification as `ConnectorPane.cs`: a slot touching x=0 is on the input edge, one
    touching the pane's width is on the output edge, and a slot touching both (the single-column
    patterns) belongs to neither - the guide has nowhere to land there.
    """
    width = max(slots[i][2] for i in indices)
    left, right, middle = [], [], []
    for i in indices:
        l, t, r, _ = slots[i]
        if l == 0 and r >= width:
            middle.append((t, l, i))
        elif l == 0:
            left.append((t, i))
        elif r >= width:
            right.append((t, i))
        else:
            middle.append((t, l, i))
    return ([i for _, i in sorted(left)],
            [i for _, i in sorted(right)],
            [i for _, _, i in sorted(middle)])


def is_error_in(name):
    return "error in" in name.lower()


def is_error_out(name):
    return "error out" in name.lower()


def suggest(slots, terminals):
    """name -> conIdx, as NI's style guide asks for on exactly this pattern.

    Mirrors `ConnectorPane.Suggest`: inputs down the left edge, outputs down the right, the error
    terminals reserved into the bottom corners FIRST so a VI with as many inputs as edge slots
    cannot push `error in` off the pane, and the overflow into the middle columns.
    """
    left, right, middle = edges(slots, sorted(slots))
    # `terminals` already arrives in author order - do not re-sort it by the pane, or a wrong pane
    # gets tidied into a different wrong order.
    inputs = [t for t in terminals if not t[2] and not is_error_in(t[1])]
    outputs = [t for t in terminals if t[2] and not is_error_out(t[1])]
    err_in = next((t for t in terminals if not t[2] and is_error_in(t[1])), None)
    err_out = next((t for t in terminals if t[2] and is_error_out(t[1])), None)

    left, right = list(left), list(right)
    in_slot = out_slot = None
    if err_in is not None and left:
        in_slot = left.pop()
    if err_out is not None and right:
        out_slot = right.pop()

    spare = list(middle)
    assignment = {}

    def place(items, edge):
        for n, t in enumerate(items):
            if n < len(edge):
                assignment[t[1]] = edge[n]
            elif spare:
                assignment[t[1]] = spare.pop(0)
            # else: more terminals than slots - left unassigned, and reported as such.

    place(inputs, left)
    place(outputs, right)
    if in_slot is not None:
        assignment[err_in[1]] = in_slot
    if out_slot is not None:
        assignment[err_out[1]] = out_slot
    return assignment


def review(slots, terminals, assignment):
    """Everything wrong with the pane, in the same cases and the same order as `ConnectorPane.cs`.

    The middle-column case is the one that is easy to get subtly wrong, and did: a terminal in a
    middle column is only a WARNING when its own edge still has a FREE slot. Warning whenever the
    suggestion differs from where it sits instead turns "the four secondary inputs are in the
    middle columns in a different order than the guide would have chosen" into four findings on a
    pane NI itself would call correct. Measured against the C# tool on two panes.
    """
    findings = []
    left, right, middle = edges(slots, sorted(slots))
    occupied = {idx for idx, *_ in terminals}
    err_in_slot = left[-1] if left else None
    err_out_slot = right[-1] if right else None

    seen = {}
    for idx, name, *_ in terminals:
        seen.setdefault(idx, []).append(name)
    for idx, names in seen.items():
        if len(names) > 1:
            findings.append(("violation", " + ".join(names), idx,
                             "%d terminals share conIdx %d; a slot holds one terminal"
                             % (len(names), idx), None))

    for idx, name, is_out, _, _ in terminals:
        want = assignment.get(name)
        if idx not in slots:
            findings.append(("violation", name, idx,
                             "conIdx %d is not a slot on this pattern" % idx, want))
        elif not is_out and idx in right:
            findings.append(("violation", name, idx,
                             "an INPUT sitting on the output edge", want))
        elif is_out and idx in left:
            findings.append(("violation", name, idx,
                             "an OUTPUT sitting on the input edge", want))
        elif not is_out and is_error_in(name) and err_in_slot is not None and idx != err_in_slot:
            findings.append(("violation", name, idx,
                             "`error in` is not in the bottom-left corner, which is conIdx %d"
                             % err_in_slot, err_in_slot))
        elif is_out and is_error_out(name) and err_out_slot is not None and idx != err_out_slot:
            findings.append(("violation", name, idx,
                             "`error out` is not in the bottom-right corner, which is conIdx %d"
                             % err_out_slot, err_out_slot))
        elif idx in middle:
            own = right if is_out else left
            if any(s not in occupied for s in own):
                findings.append(("warning", name, idx,
                                 "in a middle column while its own edge still has a free slot",
                                 want))

    return sorted(findings, key=lambda f: 0 if f[0] == "violation" else 1)


# --------------------------------------------------------------------------- writing

def render_cons(cons, slot_count):
    lines = ['<cons elements="%d">' % slot_count]
    cursor = 0
    for idx in sorted(cons):
        attr = "" if idx == cursor else ' index="%d"' % idx
        lines.append('        <SL__arrayElement class="ConpaneConnection"%s>' % attr)
        lines.append('          <ConnectionDCO uid="%d" />' % cons[idx])
        lines.append("          </SL__arrayElement>")
        cursor = idx + 1
    lines.append("        </cons>")
    return "\n      ".join([lines[0]]) + "\n" + "\n".join(lines[1:])


def write_pane(bundle, pane, cons, children, con_id, pattern_code):
    block = CONS_RE.search(bundle.fp)
    body = render_cons(cons, len(children))
    bundle.fp = bundle.fp[:block.start()] + body + bundle.fp[block.end():]
    bundle.fp = re.sub(r"<conId>\d+</conId>", "<conId>%d</conId>" % con_id, bundle.fp, count=1)

    old = bundle.main[pane.start:pane.end]
    head = old.split("\n", 1)[0]
    head = re.sub(r'Pattern="0x[0-9A-Fa-f]+"', 'Pattern="0x%X"' % pattern_code, head)
    rendered = [head]
    for tid, flags in children:
        rendered.append('        <TypeDesc TypeID="%d" Flags="0x%04X" />' % (tid, flags))
    rendered.append("        </TypeDesc>")
    bundle.main = bundle.main[:pane.start] + "\n".join(rendered) + bundle.main[pane.end:]


# --------------------------------------------------------------------------- reporting

def show(pane, slots):
    terms = pane.terminals()
    assignment = suggest(slots, terms)
    findings = review(slots, terms, assignment)

    print("%s - connector pane pattern %d, %d slots, %d assigned."
          % (pane.bundle.stem, pane.con_id, pane.slot_count, len(terms)))
    left, right, middle = edges(slots, sorted(slots))
    print("  input edge  (top to bottom): %s" % ", ".join(str(i) for i in left))
    print("  output edge (top to bottom): %s" % ", ".join(str(i) for i in right))
    print("  middle columns:              %s" % (", ".join(str(i) for i in middle) or "-"))
    print()
    for idx, name, is_out, _, flags in terms:
        where = "left" if idx in left else "right" if idx in right else "middle"
        need = "required" if flags & REQUIRED else \
               "recommended" if flags & RECOMMENDED else "optional"
        print("  conIdx %-3d %-8s %-11s %-9s %s"
              % (idx, "output" if is_out else "input", where, need, name))
    print()
    if not findings:
        print("VERDICT: the pane follows NI's style guide. Nothing to change.")
        return assignment, findings
    print("VERDICT: %d finding(s)." % len(findings))
    for severity, name, idx, problem, want in findings:
        fix = "move it to conIdx %s" % want if want is not None else "no free slot for it"
        print("  [%s] %s (conIdx %d): %s - %s" % (severity, name, idx, problem, fix))
    print()
    print("CORRECTED ASSIGNMENT")
    for idx, name, _, _, _ in terms:
        want = assignment.get(name)
        mark = "" if want == idx else "   (was %d)" % idx
        print("  conIdx %-3s %s%s" % (want if want is not None else "-", name, mark))
    return assignment, findings


# --------------------------------------------------------------------------- main

def main(argv):
    ap = argparse.ArgumentParser(add_help=True, description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundle")
    ap.add_argument("--show", action="store_true")
    ap.add_argument("--pattern", type=int)
    args = ap.parse_args(argv)

    geometry = load_geometry()
    bundle = Bundle(args.bundle)

    pane = Pane(bundle)
    target = args.pattern or pane.con_id
    if target not in geometry:
        raise SystemExit("pattern %d has no measured geometry - see %s"
                         % (target, GEOMETRY_TSV))
    slots = geometry[target]

    if args.show or not args.pattern:
        show(pane, geometry.get(pane.con_id, slots))
        return 0

    codes = load_pattern_codes()
    pattern_code = pane.pattern
    if args.pattern and args.pattern != pane.con_id:
        if args.pattern not in codes:
            raise SystemExit(
                "no measured Pattern code for conId %d. The two numbers are unrelated - 4815 pairs "
                "with 0x78, 4833 with 0x108 - so this is refused rather than guessed. Add the pair "
                "to %s once a real VI has been measured." % (args.pattern, PATTERN_TSV))
        pattern_code = codes[args.pattern]

    terms = pane.terminals()
    # The assignment is carried over UNCHANGED. Moving terminals is what `--reindex` used to do,
    # and it is gone - see the module docstring.
    assignment = {name: idx for idx, name, _, _, _ in terms}

    missing = [t[1] for t in terms if t[1] not in assignment]
    if missing:
        raise SystemExit("no slot on pattern %d for: %s" % (target, ", ".join(missing)))
    outside = sorted(i for i in assignment.values() if i not in slots)
    if outside:
        raise SystemExit("pattern %d has no slot %s" % (target, outside))

    void = void_type_id(bundle.main)
    slot_count = len(slots)
    cons, children = {}, [(void, 0x0000)] * slot_count
    children = list(children)
    permutation = {}
    for idx, name, _, tid, flags in terms:
        new = assignment[name]
        permutation[idx] = new
        cons[new] = pane.cons[idx]
        children[new] = (tid, flags)

    write_pane(bundle, pane, cons, children, target, pattern_code)
    bundle.save()

    # The assignment is the identity by construction, so this can only ever be zero. Asserted
    # rather than assumed: a permuted `cons` array is what killed LabVIEW twice, so if a future
    # edit ever reintroduces one, it should stop here rather than reach a rebuild.
    moved = {o: n for o, n in permutation.items() if o != n}
    if moved:
        raise SystemExit(
            "REFUSED: this would move %d terminal(s) - %s. Permuting a pane's assignment produced "
            "a file that killed LabVIEW on load, twice, so this script only ever changes the "
            "PATTERN. Fix a wrong assignment by regenerating from AIXML with the conIdx values "
            "lvai_connector_pane prints." % (len(moved), moved))

    print("pattern %d -> %d, %d slots, no conIdx changed - no caller has to be touched."
          % (pane.con_id, target, slot_count))
    print("Now pylv_rebuild, then measure with lvai_connector_pane - a rebuild verifies nothing.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
