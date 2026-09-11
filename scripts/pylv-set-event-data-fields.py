# -*- coding: utf-8 -*-
"""Point an Event Structure frame's EVENT DATA NODE rows at the fields of the
event it handles, and move the wires onto them - in a pylabview bundle, with no
LabVIEW running.

Driven by `lvai_set_event_data_fields`, which runs the whole extract / strip /
edit / rebuild / verify cycle. Call it directly only to debug a bundle.

WHY THIS EXISTS. `ConvertAIXMLToVI` DISCARDS an Event Data Node's field
selection. Measured 2026-09-11: AIXML authored
`fields="Source,Type,Time,Message,Value"` with both payload fields wired into a
`Bundle By Name`, and the VI came back with a node of `Source,Type,Time` and the
two wires GONE - silently, `errorCode 0`. The cause is the order the event route
runs in: at conversion time the frame carries no event registration, so the only
fields LabVIEW can offer are the three every event has.

AIXML is NOT the limitation. LabVIEW's own export of a corrected VI reads exactly
what was authored, so the dialect expresses it and the importer drops it. And
there is no second AIXML pass to put it back: `ApplyAIXMLToVI` is gated for
third-party clients and `Apply code changes.vi` is a no-op.

NEITHER IS VI SERVER. `{LV.EventStructure}` `Diagrams[]` hands back one reference
per frame and the objects on them cannot be read at all - frame 0 returns 13
object references on which EVERY property read answers `Error 1055`, the rest
return nothing. Identical through the IDE's own application instance and through
a plain `Open VI Reference`. So there is no reference to the node: `Resize`
cannot grow it and `Connect Wire` cannot wire it.

WHAT IT DOES, AND WHY IT IS ONLY SUBSTITUTION. In the heap a data node row is a
`term` whose `nmxDCO` carries two things that matter: `<i>` - the index of the
event-data field it shows - and a `typeDesc`. Both are plain text. So this
REPURPOSES rows LabVIEW already created instead of adding any, and it MOVES an
existing wire instead of composing one:

    row 1  <i>1</i>  (Type)  ->  <i>4</i>  (the payload), typeDesc from the sink
    signal [<placeholder's term>, <sink>]  ->  [<row 1's term>, <sink>]

No heap object is CREATED and no geometry is invented. That is deliberate:
composing a `signal` means inventing a `compressedWireTable`, and the one time
this repository added heap objects (`pylv-conpane.py --reindex`) it killed
LabVIEW twice on files that re-extracted cleanly.

THE FIELD INDEX: 0 Source, 1 Type, 2 Time, 3 UsrEvtRef, 4 the FIRST PAYLOAD ITEM.
Measured 2026-09-11 by repointing a row to 3 and reading LabVIEW's own export
back: `fields="Source,UsrEvtRef,Time"`, an event-refnum terminal, and the VI eBad
because a refnum met a boolean sink. Index 4 on the same probe gave
`fields="Source,Tick,Time"` and `execState 1`. 4 is the first payload item
whether the payload is a CLUSTER (4 and 5 on a two-element one) or a SCALAR.
An earlier revision of this file called 3 "the cluster itself"; that was wrong.

THE TYPE COMES FROM THE SINK, never from the source. The wire's other end already
carries the type LabVIEW wants on that wire, so copying its `typeDesc` makes the
wire agree by construction. Copying the placeholder's own type is what it looks
like you should do and it is wrong: measured, a front-panel control's `gRefDCO`
type for the same String left the VI `execState 0` while the sink's gave 1.

THE SINK MUST BE A PRIM TERMINAL. A front-panel indicator terminal and a tunnel
both carry NO `typeDesc` in the block-diagram heap - measured, `the sink terminal
4573 carries no typeDesc` - so the placeholder must be wired into a primitive or
subVI input, not straight into an indicator or across a structure border.

WHETHER THE PLACEHOLDER IS DELETED IS NOT A CHOICE - it follows from what it is,
and this script decides it rather than asking:

  * a NODE (Local Variable, primitive) is listed in a `nodeList` as well as in
    the `zPlaneList`, and both entries go. An unwired Local Variable is a BROKEN
    VI, not an untidy one: `Local Variable: This variable is not connected to
    anything. Either wire it or delete it.`
  * a diagram CONSTANT is a bare `term` in the `zPlaneList` ALONE. It is KEPT.
    Deleting one was measured as `LabVIEW load error code 6: Could not load block
    diagram` - the file will not open at all - while keeping it gives
    `execState 1`, because an unwired constant is legal.

A constant is therefore the better placeholder: it needs no front-panel control,
and leaving it costs nothing. Label it in the AIXML (`_name="Payload
Placeholder"`) so the diagram says what it is.

THE PRICE of not growing the node: the rows are FINITE. LabVIEW gives a converted
frame three - Source, Type, Time - and Source carries no `<i>` element to
rewrite, so TWO payload fields per frame is the ceiling, and `Type` and `Time`
stop being displayed.

HOW THE CALLER NAMES THINGS: by the uid written in the AIXML. For a node that is
also its heap uid; for a CONSTANT it is not - LabVIEW keeps it on the `ddo`
inside the `term`, and the `term` gets a fresh number. This resolves both, so the
caller never reads the heap.

usage:
  pylv-set-event-data-fields.py <bundle> <base> <dataNodeUid> <uid>:<fieldIndex> ...
"""
import io, re, sys


def read(bundle, base, suffix):
    p = "%s/%s_%s.xml" % (bundle, base, suffix)
    return p, io.open(p, encoding='utf-8', newline='').read()


def node_block(s, uid, cls=None):
    """The text span of one heap object, from its opening tag to the start of
    the next sibling at the same indentation.

    Anchored on the OPENING TAG and cut at the next `<SL__arrayElement` that is
    indented no further, because these objects nest and a naive match to the
    first `</SL__arrayElement>` stops inside the first child.
    """
    pat = r'<SL__arrayElement class="%s" uid="%s">' % (cls or r'\w+', uid)
    m = re.search(pat, s)
    if m is None:
        raise SystemExit("no heap object with uid %s%s"
                         % (uid, " of class %s" % cls if cls else ""))
    indent = len(s[:m.start()].split('\n')[-1])
    for nxt in re.finditer(r'\n( *)<SL__arrayElement ', s[m.end():]):
        if len(nxt.group(1)) <= indent:
            return m.start(), m.end() + nxt.start()
    return m.start(), len(s)


def resolve_uid(s, uid):
    """The heap uid for a uid written in the AIXML.

    A NODE keeps its number: `<SL__arrayElement class="prim" uid="4215">`. A
    diagram CONSTANT does not - LabVIEW puts the authored uid on the `ddo` nested
    inside a `term` that carries a fresh number:

        <SL__arrayElement class="term" uid="4598">
          <dco class="bDConstDCO" uid="4596">
            <ddo class="stdBool" uid="4280">      <- the AIXML uid

    Measured 2026-09-11 on a boolean constant authored as uid 4280, which reached
    the heap as term 4598. So a caller passing what they wrote finds nothing
    until this looks one level down.
    """
    if re.search(r'<SL__arrayElement class="\w+" uid="%s">' % uid, s):
        return uid
    ddo = re.search(r'<ddo class="\w+" uid="%s">' % uid, s)
    if ddo is None:
        raise SystemExit(
            "nothing in the heap carries uid %s - not as an object and not as a "
            "`ddo`. Pass the uid your AIXML gave the placeholder; the generator "
            "keeps it." % uid)
    head = s[:ddo.start()]
    for m in reversed(list(re.finditer(
            r'<SL__arrayElement class="term" uid="(\d+)">', head))):
        return m.group(1)
    raise SystemExit("uid %s sits on a `ddo` with no enclosing term" % uid)


def enclosing_list(s, pos, tag):
    """Span of the `elements="N"` attribute of the innermost <tag> still open at
    `pos`, plus N. Counted by walking the opening and closing tags rather than
    by taking the nearest one backwards: these lists nest, so the nearest
    `<zPlaneList` before a node can belong to a structure that already closed.
    """
    stack = []
    for m in re.finditer(r'<%s elements="(\d+)">|</%s>' % (tag, tag), s[:pos]):
        if m.group(0).startswith('</'):
            if stack:
                stack.pop()
        else:
            stack.append((m.start(1), m.end(1), int(m.group(1))))
    if not stack:
        return None
    return stack[-1]


def data_rows(block):
    """(termUid, dcoSpan, currentFieldIndex) for each row that carries an `<i>`.

    The row showing `Source` has no `<i>` element and is therefore skipped: this
    script rewrites values and never inserts elements, so a row without one is
    not a row it can repurpose. The first term of the list is the node's
    aggregate output (`dcoAgg`) and carries no `<i>` either, so the same rule
    excludes it without having to recognise it.
    """
    rows = []
    for m in re.finditer(
            r'<SL__arrayElement class="term" uid="(\d+)">\s*'
            r'(?:<objFlags>\d+</objFlags>\s*)?'
            r'<dco class="nmxDCO" uid="\d+">(.*?)</dco>', block, re.S):
        if '<i>' in m.group(2):
            rows.append((m.group(1), m.start(2), m.end(2), m.group(2)))
    return rows


def node_list_entry(s, src_uid, after):
    """The `<SL__arrayElement uid="N" />` reference to this object that sits in a
    `nodeList`, or None when there is none.

    EVERY reference is tried, not the first: a uid is named from several lists,
    and a wire's own `termList` names exactly this terminal. Deleting THAT entry
    would dangle the wire the whole operation exists to keep - measured
    2026-09-11, where the first match for a constant was the signal's termList.
    None means the object is a diagram CONSTANT, which lives in the zPlaneList
    alone and must be kept.
    """
    for ref in re.finditer(r'\n *<SL__arrayElement uid="%s" />' % src_uid,
                           s[after:]):
        at = after + ref.start()
        lst = enclosing_list(s, at + 1, 'nodeList')
        if lst is not None:
            return at, after + ref.end(), lst
    return None


def main(argv):
    if len(argv) < 4:
        raise SystemExit(__doc__)
    bundle, base, data_uid = argv[0], argv[1], argv[2]
    takes = []
    for spec in argv[3:]:
        if ':' not in spec:
            raise SystemExit("expected <uid>:<fieldIndex>, got %r" % spec)
        a, b = spec.rsplit(':', 1)
        try:
            takes.append((a.strip(), int(b)))
        except ValueError:
            raise SystemExit("the field index in %r is not a number" % spec)

    path, s = read(bundle, base, 'BDHb')

    data_uid = resolve_uid(s, data_uid)
    start, end = node_block(s, data_uid, 'eventDataNode')
    rows = data_rows(s[start:end])
    if len(rows) < len(takes):
        raise SystemExit(
            "the Event Data Node uid %s has %d rewritable row(s) and %d were "
            "asked for. A converted frame has three rows - Source, Type, Time - "
            "and Source carries no field index to rewrite, so two is the "
            "ceiling. Split the fields over more frames, or read the rest from "
            "the payload cluster downstream." % (data_uid, len(rows), len(takes)))

    edits = []          # (absolute span in s, replacement)
    deletes = []        # spans removed outright
    counts = []         # ((start, end, value) of an elements="N", delta)
    kept = 0
    for (given_uid, field), row in zip(takes, rows):
        term_uid, dco_from, dco_to, dco = row
        src_uid = resolve_uid(s, given_uid)

        src_start, src_end = node_block(s, src_uid)
        src_term = re.search(r'<SL__arrayElement class="term" uid="(\d+)">',
                             s[src_start:src_end])
        if src_term is None:
            raise SystemExit("uid %s has no terminal" % given_uid)
        src_term = src_term.group(1)

        sig = re.search(
            r'<SL__arrayElement class="signal" uid="\d+">\s*'
            r'(?:<objFlags>\d+</objFlags>\s*)?'
            r'<termList elements="2">\s*'
            r'<SL__arrayElement uid="%s" />\s*'
            r'<SL__arrayElement uid="(\d+)" />' % src_term, s)
        if sig is None:
            raise SystemExit(
                "no two-ended wire leaves uid %s (terminal %s). This takes over "
                "an EXISTING wire; it cannot make one. Wire the placeholder into "
                "the node that should consume the field." % (given_uid, src_term))
        sink_uid = sig.group(1)

        sink_type = re.search(
            r'<SL__arrayElement class="term" uid="%s">.*?<typeDesc>(TypeID\(\d+\))'
            r'</typeDesc>' % sink_uid, s, re.S)
        if sink_type is None:
            raise SystemExit(
                "the sink terminal %s carries no typeDesc, so there is no type to "
                "give the row. A front-panel terminal and a tunnel both lack one - "
                "wire the placeholder into a PRIMITIVE or subVI input instead."
                % sink_uid)
        sink_type = sink_type.group(1)

        # the row: field index and the type the SINK expects
        new_dco = re.sub(r'<i>\d+</i>', '<i>%d</i>' % field, dco)
        new_dco = re.sub(r'<typeDesc>TypeID\(\d+\)</typeDesc>',
                         '<typeDesc>%s</typeDesc>' % sink_type, new_dco)
        edits.append((start + dco_from, start + dco_to, new_dco))

        # the wire: its source end moves onto that row
        edits.append((sig.start(), sig.end(),
                      sig.group(0).replace('uid="%s" />' % src_term,
                                           'uid="%s" />' % term_uid, 1)))

        entry = node_list_entry(s, src_uid, src_end)
        if entry is None:
            # a diagram CONSTANT - zPlaneList only. Keeping it is not tidiness:
            # deleting one gives `load error code 6: Could not load block diagram`.
            kept += 1
            print("   row term %s -> field %d, type %s, wire from %s (term %s) "
                  "-> sink %s; placeholder KEPT (a constant, unwired and legal)"
                  % (term_uid, field, sink_type, given_uid, src_term, sink_uid))
            continue

        src_block = s[src_start:src_end]
        others = [t for t in re.findall(
            r'<SL__arrayElement class="term" uid="(\d+)">', src_block)
            if t != src_term]
        if others:
            raise SystemExit(
                "uid %s has %d terminal(s) besides the one taken over (%s). A "
                "NODE placeholder is deleted, because LabVIEW treats an unwired "
                "Local Variable as an error rather than a warning - and deleting "
                "a node that still carries wires would dangle them. Give a "
                "placeholder with one terminal, or use a constant."
                % (given_uid, len(others), ", ".join(others)))

        zp = enclosing_list(s, src_start, 'zPlaneList')
        if zp is None:
            raise SystemExit("uid %s sits in no zPlaneList" % given_uid)
        deletes.append((src_start, src_end, ''))
        counts.append((zp, -1))
        ref_from, ref_to, nl = entry
        deletes.append((ref_from, ref_to, ''))
        counts.append((nl, -1))
        print("   row term %s -> field %d, type %s, wire from %s (term %s) "
              "-> sink %s; placeholder DELETED (a node)"
              % (term_uid, field, sink_type, given_uid, src_term, sink_uid))

    # every list whose length changed, folded so two deletions from one list
    # subtract two rather than each writing "minus one" over the other
    totals = {}
    for (a, b, value), delta in counts:
        cur = totals.get((a, b), [value, 0])
        cur[1] += delta
        totals[(a, b)] = cur
    for (a, b), (value, delta) in totals.items():
        edits.append((a, b, str(value + delta)))

    for a, b, text in sorted(edits + deletes, reverse=True):
        s = s[:a] + text + s[b:]

    io.open(path, 'w', encoding='utf-8', newline='').write(s)
    print("OK event data node %s: %d row(s) repointed, %d placeholder(s) kept, "
          "%d deleted" % (data_uid, len(takes), kept, len(takes) - kept))


if __name__ == '__main__':
    main(sys.argv[1:])
