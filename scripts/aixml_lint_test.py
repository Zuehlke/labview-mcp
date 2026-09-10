#!/usr/bin/env python3
"""Acceptance suite for aixml_lint.py - run against REAL AIXML, not invented files.

    python aixml_lint_test.py

Positive cases are the ATM Generator documents, all of which were generated
successfully and several of which were executed. They must produce ZERO errors.

Negative cases are produced by MUTATING those documents, one fault at a time, so
the suite is repeatable rather than hand-made. Fixtures are written to a throwaway
temp directory and never into the corpus.

The corpus lives OUTSIDE this repository and is edited by whoever is generating
VIs, so nothing here names a file or asserts an exact count: each mutation
DISCOVERS a document carrying the shape it needs, and reports a skip when the
corpus no longer has one. Written against 8 documents, run the next day against
13 with several renamed.

Each negative case is judged by DIFFERENCE against a control: the same document
round-tripped through ElementTree without any mutation. Comparing against the
control rather than against the pristine file means the serialiser's own habits
cannot be mistaken for a finding, and the assertion is exactly "this mutation adds
precisely this check and nothing else".
"""

from __future__ import annotations

import copy
import glob
import os
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import aixml_lint as lint  # noqa: E402

CORPUS = r"C:\Temp\cld-sample-exams\ATM (100928D-01)\Generator"

#: Fewest documents worth calling a corpus. Deliberately not an exact count: this corpus
#: lives OUTSIDE the repository and is edited by whoever is generating VIs. It was 8
#: documents when this suite was written and 13 a day later, with files renamed under it -
#: an exact-count assertion and a hardcoded file name both failed for a reason that had
#: nothing to do with the linter. Targets are DISCOVERED by shape from here on.
MIN_CORPUS_FILES = 5

_results: list[tuple[bool, str, str]] = []


def corpus_files() -> list[str]:
    return sorted(glob.glob(os.path.join(CORPUS, "*.xml")))


def helper_files() -> list[str]:
    """The shipped AIXML helpers - in this repository, so they always exist."""
    return sorted(glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)), "*.xml")))


def mutation_pool() -> list[str]:
    """Where a mutation looks for a document carrying the shape it needs.

    IN-REPO HELPERS FIRST, and that is the point: they carry every shape the mutations want -
    two Structures with a CaseFrame, frame tunnels, an Indicator with conIdx and connection -
    and unlike the external corpus they cannot be cleaned up under the suite. Pointing these
    at the corpus alone left all four mutation cases permanently skipped the moment that tree
    was reset, which is regression coverage lost for four checks.
    """
    return helper_files() + corpus_files()


def pick(predicate, why: str) -> str | None:
    """First document in the pool whose parsed root satisfies predicate, else None.

    Returns None rather than raising so that a pool which has lost the shape a mutation
    needs is reported as a skip, not as a failure of the linter.
    """
    for path in mutation_pool():
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError:
            continue
        if predicate(root):
            return path
    print(f"  SKIP  no corpus document {why}")
    return None


def check(ok: bool, name: str, detail: str = "") -> None:
    _results.append((bool(ok), name, detail))
    print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    if detail and not ok:
        print(f"        {detail}")


# --------------------------------------------------------------------------
# 1. The parsing trap
# --------------------------------------------------------------------------


def test_splitter() -> None:
    print("\n[1] escape-aware splitting of inputs/outputs/fields")

    # The Select primitive's output. 15 occurrences in ATM State Machine.xml.
    pairs = lint.parse_terminal_list(r"s? t\3Af:970.value")
    check(pairs == [("s? t:f", "970.value")], "Select output 's? t\\3Af' stays one terminal", str(pairs))

    # 8 occurrences in scripts/lvdqmh_dlg_keyfocus.xml.
    pairs = lint.parse_terminal_list(r"error in (no error):20.error out")
    check(pairs == [("error in (no error)", "20.error out")], "'error in (no error)' keeps its parentheses", str(pairs))

    # An escaped comma inside a default value must not split the entry.
    pairs = lint.parse_terminal_list(r"max queue size (-1\2C unlimited):5.value,element:6.value")
    check(
        pairs == [("max queue size (-1, unlimited)", "5.value"), ("element", "6.value")],
        "escaped comma does not split the entry",
        str(pairs),
    )

    # A Property Node fields value carries an escaped colon.
    parts = [lint.unescape(p) for p in lint.split_unescaped(r"write+Front Panel Window\3AState", ",")]
    check(parts == ["write+Front Panel Window:State"], "Property Node field keeps its colon", str(parts))

    # array type (2D Dbl) - from ATM Read Accounts.xml.
    pairs = lint.parse_terminal_list(r"array type (2D Dbl):25.value")
    check(pairs == [("array type (2D Dbl)", "25.value")], "'array type (2D Dbl)' survives", str(pairs))

    # An empty net means deliberately unwired and must be dropped from both sets.
    pairs = lint.parse_terminal_list("reference out:,error out:461.error out")
    check(
        pairs == [("reference out", ""), ("error out", "461.error out")],
        "an empty net parses as unwired",
        str(pairs),
    )

    # Unescaping BEFORE splitting is the actual trap: prove the wrong order breaks.
    naive = lint.unescape(r"s? t\3Af:970.value").split(":")
    check(len(naive) == 3, "unescape-then-split really does produce phantom pieces", str(naive))


# --------------------------------------------------------------------------
# 2. Positive cases
# --------------------------------------------------------------------------


def test_positives() -> None:
    print("\n[2] every ATM Generator document must be error-free")
    files = corpus_files()
    check(len(files) >= MIN_CORPUS_FILES, f"corpus has {len(files)} documents (need {MIN_CORPUS_FILES}+)")
    for path in files:
        findings = lint.lint_file(path)
        errors = [f for f in findings if f.severity == "error"]
        detail = "; ".join(f"{f.code} uid={f.uid}" for f in errors)
        check(not errors, f"no errors in {os.path.basename(path)}", detail)


# --------------------------------------------------------------------------
# 3. Negative cases, by mutation
# --------------------------------------------------------------------------


def _write(tree: ET.ElementTree, directory: str, name: str) -> str:
    path = os.path.join(directory, name)
    tree.write(path, encoding="utf-8", xml_declaration=True)
    return path


def _codes(path: str) -> list[str]:
    return [f.code for f in lint.lint_file(path)]


def _added(control: str, mutant: str) -> set[str]:
    """Check codes the mutant has that the untouched control does not."""
    return set(_codes(mutant)) - set(_codes(control))


def _control(src: str, directory: str) -> tuple[ET.ElementTree, str]:
    tree = ET.parse(src)
    control = _write(copy.deepcopy(tree), directory, "control.xml")
    return tree, control


def test_mutation_wrong_parent(directory: str) -> None:
    print("\n[3a] A - a CaseFrame's uid_parent bent to a different, EXISTING Structure")

    def usable(root):
        structs = [e for e in root.iter("Structure") if e.get("uid")]
        return len(structs) >= 2 and any(s.findall("CaseFrame") for s in structs)

    src = pick(usable, "carrying two Structures and a CaseFrame")
    if src is None:
        return
    tree, control = _control(src, directory)
    root = tree.getroot()

    structures = [e for e in root.iter("Structure") if e.get("uid")]
    victim = donor = None
    for struct in structures:
        for frame in struct.findall("CaseFrame"):
            other = next((s for s in structures if s.get("uid") != struct.get("uid")), None)
            if other is not None:
                victim, donor = frame, other
                break
        if victim is not None:
            break

    if victim is None:
        check(False, "found a CaseFrame and a second Structure to point it at")
        return
    original = victim.get("uid_parent")
    victim.set("uid_parent", donor.get("uid"))
    mutant = _write(tree, directory, "mutant_parent.xml")

    added = _added(control, mutant)
    check(
        added == {"parent-mismatch"},
        f"uid_parent {original} -> {donor.get('uid')} raises exactly parent-mismatch",
        f"added={sorted(added)}",
    )
    hit = [f for f in lint.lint_file(mutant) if f.code == "parent-mismatch"]
    check(
        len(hit) == 1 and hit[0].uid == victim.get("uid"),
        f"the finding names the moved CaseFrame uid={victim.get('uid')}",
        str([(f.uid, f.message[:60]) for f in hit]),
    )


def test_mutation_net_typo(directory: str) -> None:
    print("\n[3b] B - a frame tunnel's produced net name changed by one")

    def usable(root):
        return any(t.get("outputs") for f in root.iter("CaseFrame") for t in f.findall("Tunnel"))

    src = pick(usable, "carrying a frame tunnel that produces a net")
    if src is None:
        return
    tree, control = _control(src, directory)
    root = tree.getroot()
    raw = open(src, encoding="utf-8").read()

    produced, consumed = lint.collect_nets(lint._walk(root))
    target_el = target_net = new_net = None
    for frame in root.iter("CaseFrame"):
        for tunnel in frame.findall("Tunnel"):
            outs = tunnel.get("outputs")
            if not outs:
                continue
            for _term, net in lint.parse_terminal_list(outs):
                if not net or net not in consumed:
                    continue
                head, _, tail = net.partition(".")
                if not head.isdigit():
                    continue
                candidate = f"{int(head) + 1}.{tail}"
                if candidate in produced or candidate in consumed or candidate in raw:
                    continue
                target_el, target_net, new_net = tunnel, net, candidate
                break
            if target_net:
                break
        if target_net:
            break

    if target_net is None:
        check(False, "found a frame tunnel whose produced net is read elsewhere")
        return
    target_el.set("outputs", target_el.get("outputs").replace(target_net, new_net))
    mutant = _write(tree, directory, "mutant_net.xml")

    added = _added(control, mutant)
    check(
        added == {"net-typo"},
        f"net {target_net} -> {new_net} raises exactly net-typo",
        f"added={sorted(added)}",
    )
    hit = [f for f in lint.lint_file(mutant) if f.code == "net-typo"]
    check(
        len(hit) == 1 and new_net in hit[0].message and target_net in hit[0].message,
        "the finding names both the unread and the unwritten net",
        str([f.message[:110] for f in hit]),
    )


def test_mutation_duplicate_uid(directory: str) -> None:
    print("\n[3c] a duplicated uid")

    src = pick(
        lambda r: len([e for e in r.iter() if e.get("uid") and e.get("uid") != lint.SENTINEL_UID]) >= 2,
        "carrying two elements with uids",
    )
    if src is None:
        return
    tree, control = _control(src, directory)
    root = tree.getroot()

    withuid = [e for e in root.iter() if e.get("uid") and e.get("uid") != lint.SENTINEL_UID]
    if len(withuid) < 2:
        check(False, "found two elements carrying a uid")
        return
    keeper, victim = withuid[0], withuid[-1]
    victim.set("uid", keeper.get("uid"))
    mutant = _write(tree, directory, "mutant_uid.xml")

    added = _added(control, mutant)
    check(
        "uid-duplicate" in added,
        f"duplicating uid={keeper.get('uid')} raises uid-duplicate",
        f"added={sorted(added)}",
    )


def test_mutation_missing_connection(directory: str) -> None:
    print("\n[3d] an Indicator with conIdx loses its connection attribute")

    src = pick(
        lambda r: any(e.get("conIdx") and e.get("connection") for e in r.iter("Indicator")),
        "carrying an Indicator with conIdx and connection",
    )
    if src is None:
        return
    tree, control = _control(src, directory)
    root = tree.getroot()

    victim = next(
        (e for e in root.iter("Indicator") if e.get("conIdx") and e.get("connection")),
        None,
    )
    if victim is None:
        check(False, "found an Indicator carrying conIdx and connection")
        return
    del victim.attrib["connection"]
    mutant = _write(tree, directory, "mutant_conn.xml")

    added = _added(control, mutant)
    check(
        added == {"conn-missing-output"},
        f"dropping connection on Indicator '{victim.get('_name')}' raises "
        f"exactly conn-missing-output",
        f"added={sorted(added)}",
    )
    hit = [f for f in lint.lint_file(mutant) if f.code == "conn-missing-output"]
    check(
        len(hit) == 1 and hit[0].severity == "error",
        "and it is an ERROR, because a required output breaks every caller",
        str([(f.severity, f.uid) for f in hit]),
    )


# --------------------------------------------------------------------------
# 4. Synthetic checks that need no corpus counterpart
# --------------------------------------------------------------------------


def test_type_checks(directory: str) -> None:
    print("\n[4] enum label defaults and the nested array form")
    # The type spelling is the MEASURED one: an enum is an integer type carrying its items,
    # uint8{Init,Wait Card,...}, as it appears 12 times in the ATM corpus. This fixture said
    # enum{...} until 2026-09-09, which occurs in no real document - so the checks below were
    # dead code on real files and passed only because the fixture agreed with the bug.
    doc = (
        '<VI _name="probe.vi">'
        '<Constant _name="state" type="uint8{Idle,Reading,Writing}" value="Reading"'
        ' uid="4200" uid_parent="root" outputs="value:4200.value"/>'
        '<Constant _name="grid" type="array{array{double}}" value="[]"'
        ' uid="4210" uid_parent="root" outputs="value:4210.value"/>'
        '<Constant _name="over" type="uint16{A,B}" value="7"'
        ' uid="4220" uid_parent="root" outputs="value:4220.value"/>'
        "</VI>"
    )
    path = os.path.join(directory, "types.xml")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(doc)

    findings = {f.code: f for f in lint.lint_file(path)}
    check("enum-label" in findings, "an enum default given as a LABEL is an error")
    check(
        "enum-label" in findings and "index is 1" in findings["enum-label"].message,
        "and the message says which index the label would have been",
        findings.get("enum-label").message if "enum-label" in findings else "",
    )
    check("array-nested" in findings, "array{array{...}} is an error")
    check("enum-out-of-range" in findings, "an enum index past the last item is a warning")

    # uid_parent pointing at a Constant can never be right.
    doc2 = (
        '<VI _name="probe2.vi">'
        '<Constant _name="c" type="string" value="" uid="4200" uid_parent="root"'
        ' outputs="value:4200.value"/>'
        '<Node _name="String Length" inputs="string:4200.value"'
        ' outputs="length:4230.length" uid="4230" uid_parent="4200"/>'
        '<Indicator _name="n" type="int32" value="0" inputs="value:4230.length"'
        ' uid="4240" uid_parent="root"/>'
        "</VI>"
    )
    path2 = os.path.join(directory, "badparent.xml")
    with open(path2, "w", encoding="utf-8") as fh:
        fh.write(doc2)
    codes = _codes(path2)
    check("parent-not-a-container" in codes, "uid_parent naming a Constant is an error", str(codes))


def test_escapes_and_required_outputs(directory: str) -> None:
    print("\n[5] a raw backslash in a value, and an explicitly required output")

    # A raw backslash is Error 42 and loses the WHOLE file; the \5C form is correct.
    doc = (
        '<VI _name="probe.vi">'
        r'<Constant _name="bad" type="string" value="Bicycle\Test.vi"'
        ' uid="4200" uid_parent="root" outputs="value:4200.value"/>'
        r'<Constant _name="good" type="string" value="Bicycle\5CTest.vi"'
        ' uid="4210" uid_parent="root" outputs="value:4210.value"/>'
        '<Indicator _name="out" type="string" conIdx="4" connection="required"'
        ' inputs="value:4200.value" uid="4220" uid_parent="root" value=""/>'
        '<Indicator _name="fine" type="string" conIdx="5" connection="recommended"'
        ' inputs="value:4210.value" uid="4230" uid_parent="root" value=""/>'
        "</VI>"
    )
    path = os.path.join(directory, "escapes.xml")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(doc)

    hits = [f for f in lint.lint_file(path) if f.code == "value-raw-backslash"]
    check(len(hits) == 1, "the raw backslash is an error", str([(f.uid, f.code) for f in hits]))
    check(
        hits and hits[0].uid == "4200",
        "and it names the offending constant, not the correctly escaped one",
        str([f.uid for f in hits]),
    )

    req = [f for f in lint.lint_file(path) if f.code == "conn-required-output"]
    check(len(req) == 1 and req[0].uid == "4220", "connection=required on an Indicator is an error", str([(f.uid, f.severity) for f in req]))
    check(all(f.severity == "error" for f in req), "and it is an ERROR - it breaks every caller")


def test_case_tunnel_completeness(directory: str) -> None:
    print("\n[6] case tunnels: OUT must be in every frame, IN need not be")

    def build(name: str, frame_two_tunnels: str) -> str:
        doc = (
            '<VI _name="probe.vi">'
            '<Constant _name="c" type="string" value="" uid="4200" uid_parent="root"'
            ' outputs="value:4200.value"/>'
            '<Constant _name="sel" type="bool" value="true" uid="4210" uid_parent="root"'
            ' outputs="value:4210.value"/>'
            '<Structure _name="Case Structure" selectin="4210.value" uid="4300" uid_parent="root">'
            '<Tunnel _id="In1" inputs="value:4200.value" uid="4310" uid_parent="4300"/>'
            '<Tunnel _id="Out1" outputs="value:4320.value" uid="4320" uid_parent="4300"/>'
            '<CaseFrame selector="True" selectout="" uid="4330" uid_parent="4300">'
            '<Tunnel _id="In1" outputs="value:4331.value" uid="4331" uid_parent="4330"/>'
            '<Tunnel _id="Out1" inputs="value:4331.value" uid="4332" uid_parent="4330"/>'
            "</CaseFrame>"
            '<CaseFrame selector="False" selectout="" uid="4340" uid_parent="4300">'
            f"{frame_two_tunnels}"
            "</CaseFrame>"
            "</Structure>"
            '<Indicator _name="o" type="string" inputs="value:4320.value" uid="4400"'
            ' uid_parent="root" value=""/>'
            "</VI>"
        )
        p = os.path.join(directory, name)
        with open(p, "w", encoding="utf-8") as fh:
            fh.write(doc)
        return p

    # The shape the two shipped helpers really have: the second frame omits the unused IN
    # tunnel and keeps OUT. This must stay clean - it is the false positive that a literal
    # reading of "every frame must declare every tunnel" would produce on working code.
    ok_path = build(
        "case_ok.xml",
        '<Tunnel _id="Out1" inputs="value:4200.value" uid="4342" uid_parent="4340"/>',
    )
    codes = _codes(ok_path)
    check(
        not [c for c in codes if c.startswith("case-frame")],
        "a frame omitting only an unused IN tunnel is NOT flagged",
        str(codes),
    )

    # Drop the OUT tunnel instead: that case's output is unwired and the VI is broken.
    bad_path = build(
        "case_bad.xml",
        '<Tunnel _id="In1" outputs="value:4341.value" uid="4341" uid_parent="4340"/>',
    )
    codes = _codes(bad_path)
    check("case-frame-missing-out-tunnel" in codes, "a frame missing an OUT tunnel is an error", str(codes))

    # An _id the structure never declared connects to nothing outside.
    orphan_path = build(
        "case_orphan.xml",
        '<Tunnel _id="Out1" inputs="value:4200.value" uid="4342" uid_parent="4340"/>'
        '<Tunnel _id="Out9" inputs="value:4200.value" uid="4343" uid_parent="4340"/>',
    )
    codes = _codes(orphan_path)
    check("case-frame-orphan-tunnel" in codes, "a frame declaring an undeclared _id is an error", str(codes))


def test_measured_against_labview(directory: str) -> None:
    """Each shape below was put through the REAL ValidateAIXML on 2026-09-09.

    The verdict recorded here is LabVIEW's, not an opinion, and the accepted shapes matter
    more than the refused ones: four of the seven rules this check set was specified from
    turned out to describe documents LabVIEW accepts, and shipping them would have accused
    working code. Two of those false positives were already in the repository -
    `outputs="reference out:,error out:,"` in two shipped helpers.
    """
    print("\n[7] every shape below carries LabVIEW's own verdict")

    NODE = '<Node _name="Current VI\'s Path" {attr} uid="4200" uid_parent="root"/>'
    CASES = [
        # name, body, LabVIEW verdict, the check expected to fire (or None)
        ("base types all valid",
         "".join(f'<Constant _name="c{i}" type="{t}" value="" uid="{4200+i*10}" '
                 f'uid_parent="root" outputs="value:{4200+i*10}.value"/>'
                 for i, t in enumerate(sorted(lint.BASE_TYPES))),
         "clean", None),
        # A .ctl exports to AIXML as an empty <VI/> with errorCode 0 - measured on a
        # production control, 57 bytes, no cluster and no field. Answering "clean" for that
        # is a bill of health for something never examined.
        ("an empty document must not pass as clean", "", "warning", "empty-document"),
        ("a typo in a cluster field type",
         '<Constant _name="r" type="cluster{int82.Record Index,string.Name}" value="[0,]" '
         'uid="4200" uid_parent="root" outputs="value:4200.value"/>',
         "error", "type-grammar"),
        ("the same cluster spelled right",
         '<Constant _name="r" type="cluster{int32.Record Index,string.Name}" value="[0,]" '
         'uid="4200" uid_parent="root" outputs="value:4200.value"/>',
         "clean", None),
        # A WARNING since 2026-09-09: a field name or enum label may contain a RAW '{' while
        # '}' is escaped as D, so no brace counter can tell a name from a nesting level.
        # Two of LabVIEW's own examples do exactly that - a cluster field called
        # `Power Spectrum FFT {X}` and an ASCII enum whose labels include both braces.
        ("unbalanced braces",
         '<Constant _name="c" type="array{string" value="[]" uid="4200" uid_parent="root" '
         'outputs="value:4200.value"/>',
         "warning", "type-braces"),
        ("a cluster field with no .Name - ACCEPTED by LabVIEW",
         '<Constant _name="c" type="cluster{bool,int32}" value="[false,0]" uid="4200" '
         'uid_parent="root" outputs="value:4200.value"/>',
         "clean", None),
        # CLEAN. link= is the working way to bind an implicit Property Node, used 28 times
        # in a verified ten-VI build and written by LabVIEW's own exporter on 275 nodes.
        # This probe fails in LabVIEW only because its control name and class do not match,
        # which is a fault in the fixture and not in the attribute - so the linter must be
        # silent about it.
        ("link= on a Property Node",
         '<Control _name="U" type="string" value="" uid="4200" uid_parent="root" '
         'outputs="value:4200.value"/>'
         '<Node _name="Property Node" link="U" fields="write+Disabled" '
         'inputs="Disabled:4200.value" uid="4300" uid_parent="root"/>',
         "clean", None),
        ("an empty inputs= attribute",
         NODE.format(attr='inputs="" outputs="path:4200.path"') +
         '<Indicator _name="p" type="path" value="" inputs="value:4200.path" uid="4300" '
         'uid_parent="root"/>',
         "error", "terminal-list-empty"),
        ("a doubled comma",
         NODE.format(attr='outputs="path:4200.path,,"') +
         '<Indicator _name="p" type="path" value="" inputs="value:4200.path" uid="4300" '
         'uid_parent="root"/>',
         "error", "terminal-list-malformed"),
        ("a trailing comma - ACCEPTED, and in two shipped helpers",
         NODE.format(attr='outputs="path:4200.path,"') +
         '<Indicator _name="p" type="path" value="" inputs="value:4200.path" uid="4300" '
         'uid_parent="root"/>',
         "clean", None),
        ("a leading comma - ACCEPTED",
         NODE.format(attr='outputs=",path:4200.path"') +
         '<Indicator _name="p" type="path" value="" inputs="value:4200.path" uid="4300" '
         'uid_parent="root"/>',
         "clean", None),
    ]

    for name, body, verdict, code in CASES:
        path = os.path.join(directory, "m_" + re.sub(r"\W+", "_", name)[:40] + ".xml")
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(f'<VI _name="probe.vi" description="probe">{body}</VI>')
        errors = [f.code for f in lint.lint_file(path) if f.severity == "error"]
        if verdict == "clean":
            check(not errors, f"LabVIEW accepts it, so the linter must too: {name}", str(errors))
        elif verdict == "warning":
            warns = [f.code for f in lint.lint_file(path) if f.severity == "warning"]
            check(code in warns and not errors,
                  f"reported as a warning, not an error: {name}", str(errors + warns))
        else:
            check(code in errors, f"LabVIEW refuses it, and {code} fires: {name}", str(errors))


def test_nested_property_separator(directory: str) -> None:
    """A nested property is authorable - with a DOT. Measured both ways on {LV.Control}."""
    print("\n[8] read+Label:Text is refused, read+Label.Text validates")

    def build(field: str, name: str) -> str:
        doc = (
            '<VI _name="probe.vi" description="probe">'
            '<Constant _name="r" type="ref{LV.Control}" value="" uid="4200" '
            'uid_parent="root" outputs="value:4200.value"/>'
            f'<Node _name="Property Node" type="{{LV.Control}}" fields="read+{field}" '
            'inputs="reference:4200.value" '
            f'outputs="{field}:4300.t,reference out:4300.r,error out:4300.e" '
            'uid="4300" uid_parent="root"/>'
            '<Indicator _name="t" type="string" value="" inputs="value:4300.t" uid="4400" '
            'uid_parent="root"/></VI>'
        )
        p = os.path.join(directory, name)
        with open(p, "w", encoding="utf-8") as fh:
            fh.write(doc)
        return p

    colon = [f.code for f in lint.lint_file(build("Label\\3AText", "prop_colon.xml"))]
    check("propnode-nested-colon" in colon, "the colon form is reported", str(colon))

    dot = [f.code for f in lint.lint_file(build("Label.Text", "prop_dot.xml"))]
    check("propnode-nested-colon" not in dot, "the dot form is NOT reported", str(dot))

    hit = [f for f in lint.lint_file(build("Label\\3AText", "prop_colon2.xml"))
           if f.code == "propnode-nested-colon"]
    check(hit and "Label.Text" in hit[0].message,
          "and the message names the spelling that works", str([f.message[:80] for f in hit]))


def test_no_regression_on_shipped_helpers() -> None:
    print("\n[9] the shipped helpers stay error-free under the new checks")
    helpers = sorted(glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)), "*.xml")))
    check(len(helpers) >= 30, f"found {len(helpers)} shipped helper documents")
    offenders = []
    for path in helpers:
        errors = [f.code for f in lint.lint_file(path) if f.severity == "error"]
        if errors:
            offenders.append(f"{os.path.basename(path)}: {','.join(errors)}")
    check(not offenders, "no shipped helper reports an error", "; ".join(offenders))


# --------------------------------------------------------------------------


def main() -> int:
    """Run everything that does not need the external corpus, then what does.

    A MISSING CORPUS IS NOT A FAILURE, and making it one was wrong: this suite returned
    exit 2 and ran nothing at all the moment that directory was cleaned up, including the
    unit tests for the splitter and every check that uses only in-repo fixtures. For a
    linter whose whole premise is "runs with no LabVIEW, in CI, on a machine that has
    none", a suite that needs an absolute path under C:\\Temp to run at all is the wrong
    shape. The in-repo `scripts\\*.xml` helpers are the positive corpus that always exists.
    """
    directory = tempfile.mkdtemp(prefix="aixml_lint_fixtures_")
    print(f"fixtures in {directory}")
    try:
        test_splitter()
        test_type_checks(directory)
        test_escapes_and_required_outputs(directory)
        test_case_tunnel_completeness(directory)
        test_measured_against_labview(directory)
        test_nested_property_separator(directory)
        test_no_regression_on_shipped_helpers()

        # These mutate whatever document in the pool carries the right shape, and the pool
        # starts with the in-repo helpers - so they run everywhere, corpus or no corpus.
        test_mutation_wrong_parent(directory)
        test_mutation_net_typo(directory)
        test_mutation_duplicate_uid(directory)
        test_mutation_missing_connection(directory)

        if corpus_files():
            test_positives()
        else:
            print(f"\n[corpus] SKIPPED - no AIXML documents at {CORPUS}")
            print("         Only the external positive corpus needs it; everything else")
            print("         above runs from the repository alone.")
    finally:
        shutil.rmtree(directory, ignore_errors=True)

    failed = [name for ok, name, _ in _results if not ok]
    print(f"\n{len(_results) - len(failed)}/{len(_results)} checks passed")
    if failed:
        print("FAILED:")
        for name in failed:
            print(f"  - {name}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
