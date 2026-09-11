# -*- coding: utf-8 -*-
"""Repoint one EventSpec of an Event Structure at a front-panel control's
Value Change - in a pylabview bundle, no LabVIEW running.

WHY THIS EXISTS. AIXML can author an Event Structure shell, but ONLY with a
Timeout frame: a non-Timeout frame needs an event registration, which AIXML
cannot express (measured - `Register For Events: No event selected`, and the
template's own export is refused with "no event cases have no events defined").
A Timeout frame needs no registration, so it comes through intact.

That makes the whole division of labour work: AIXML authors the entire VI -
controls, numerics, queue, both loops, the subVI call - with ONE Timeout frame,
and pylabview then turns that frame into a real front-panel event and clones it
for the rest. Nothing has to be composed from nothing.

MEASURED shapes on LabVIEW 2026:

    Timeout        source 4   type 1073741825   eFlags 1   ddoUID 0
    Value Change   source 3   type 1073741826   eFlags 4   ddoUID <the DDO>

`ddoUID` is the ddo, NOT the fPDCO, and the binding is by id - so renaming the
control afterwards cannot break the event.

**`selString` MUST be written too, and assuming LabVIEW regenerates it was
wrong.** It caches the DISPLAYED frame's label as text, and a frame whose spec
was written after conversion carries `" [2]  "` - the index with an EMPTY
selector. `Print.VI To HTML` redraws it correctly, so an AIXML export, a render
and `execState` are ALL GREEN while the IDE shows a frame with no event
assigned - which looks exactly like the work not having been done. Measured
2026-09-10 against NI's own file, which carries
`" [1] "Button 1": Value Change "`.

So pass the control's `label` and this writes that cached text and points `dIdx`
at the frame. Call the frames in order; the last one becomes the displayed one.

usage:
  pylv-set-event-spec.py <bundle> <base> <diagramIdx> <controlLabel-or-ddoUid> [label]
  pylv-set-event-spec.py <bundle> <base> <diagramIdx> --user-event <Name> [dynIndex]

A CONTROL LABEL is the normal argument - it is what the event selector spells,
and this resolves it to the ddoUID itself, so no caller has to read the heap.

**A USER EVENT FRAME IS WRITABLE TOO, and this file used to say it was not.**
Measured 2026-09-11 on a from-scratch producer/consumer whose registration
refnum reached the structure as an AIXML `<Tunnel>` branched onto the dynamic
terminal by `lvai_wire_dynamic_events`:

    User Event     source 1   regFlags 1   eSource 25   type 1000
                   eFlags 0   ddoUID 0     dynIndex 1

took the VI from `execState 0` to **1**, LabVIEW's own AIXML export then read
the frame as ` <Data Event>A User Event `, and a LabVIEW save left all of it
untouched - so the spec is LabVIEW's INPUT here, not only its output.

TWO PRECONDITIONS, both outside this script. The dynamic event terminal must be
WIRED - `type 1000` alone on an unwired structure is what earlier measurements
recorded as inert - and `ddoUID` stays 0 because a user event has no front-panel
ddo to bind to.

WHAT IS NOT ISOLATED: which of `regFlags`, `type` and `dynIndex` is decisive.
They were changed together from `0 / 0 / 2`. `type 1000` is `0x03E8` and equals
the `eventRegItem`'s own `code`, so it is the candidate; this writes the whole
row rather than guessing. `dynIndex` defaults to 1 and only 1 is measured - it
is the registration item's position, so a structure fed several user events
needs the real one.
"""
import io, re, sys

VALUE_CHANGE = (('source', '3'), ('type', '1073741826'), ('eFlags', '4'))
USER_EVENT = (('source', '1'), ('regFlags', '1'), ('eSource', '25'),
              ('type', '1000'), ('eFlags', '0'), ('ddoUID', '0'))


def resolve_control(bundle, base, name):
    """A front-panel control's ddoUID, by its owned label.

    The name the caller has is the control's LABEL - it is what the event
    selector spells and what a person reads. The spec needs the `ddo` uid.
    Measured across three real bundles (stdBool, stdNum, stdString): the owned
    label is the `label` part with **partID 16** inside the ddo's `partsList`,
    and the ddo is the child of the `fPDCO`. Anchoring on partID is what makes
    this reliable - a boolean also carries a `multiLabel` (partID 22) holding
    its BUTTON FACE text, which is a different string and often equal to the
    name, so matching on "some label inside the control" picks the wrong one
    half the time.

    Returns (ddoUid, ddoClass) or raises with every label found, because a
    misspelling is the likely cause and the list is the useful answer.
    """
    p = "%s/%s_FPHb.xml" % (bundle, base)
    s = io.open(p, encoding='utf-8').read()
    found = []
    for m in re.finditer(r'class="fPDCO" uid="\d+">.*?<ddo class="(\w+)" uid="(\d+)">', s, re.S):
        nxt = s.find('class="fPDCO"', m.end())
        seg = s[m.end(): nxt if nxt != -1 else len(s)]
        lab = re.search(r'class="label" uid="\d+">.*?<partID>16</partID>.*?<text>"([^"]*)"</text>',
                        seg, re.S)
        if lab is None:
            continue
        found.append((lab.group(1), m.group(2), m.group(1)))
        if lab.group(1) == name:
            return m.group(2), m.group(1)
    raise SystemExit(
        "no front-panel control labelled %r in %s.\n  labels present: %s"
        % (name, p, ", ".join("%s (%s)" % (n, c) for n, _, c in found) or "(none)"))


def main(bundle, base, diagram_idx, control, label=None):
    # THREE argument forms, distinguished here so the rest of the function is
    # shared: a control LABEL (the normal one), a bare ddoUID, and the
    # --user-event flag, whose next argument is the USER EVENT's name.
    user_event = None
    dyn_index = None
    if control == '--user-event':
        if label is None:
            raise SystemExit("--user-event needs the event's name")
        user_event, label = label, None
        ddo = '0'
    elif re.fullmatch(r'\d+', control):
        ddo = control
    else:
        ddo, kind = resolve_control(bundle, base, control)
        label = control if label is None else label
        print("   %r -> ddo %s (%s)" % (control, ddo, kind))

    p = "%s/%s_BDHb.xml" % (bundle, base)
    s = io.open(p, encoding='utf-8').read()

    es = s.index('class="eventStruct"')
    en = s.index('<EventNodeEvents', es)
    end = s.index('</EventNodeEvents>', en)

    # find the EventSpec whose diagramIdx matches
    target = None
    pos = en
    while True:
        m = re.compile(r'<SL__arrayElement class="EventSpec">').search(s, pos, end)
        if not m:
            break
        close = s.index('</SL__arrayElement>', m.end()) + len('</SL__arrayElement>')
        blk = s[m.start():close]
        if re.search(r'<diagramIdx>%s</diagramIdx>' % diagram_idx, blk):
            target = (m.start(), close, blk)
            break
        pos = close
    if not target:
        # No spec for this frame yet - APPEND one. This is the case after a
        # round trip through ConvertAIXMLToVI: the frames, their data nodes and
        # their filter entries all survive (diagramList / dataNodeList /
        # filterNodeList keep their count) but EventNodeEvents is collapsed to
        # the single Timeout spec, so the other frames have no event at all and
        # LabVIEW reports "An event specifier must be defined for each event
        # handling case". Adding the missing entries restores them - no frame
        # has to be cloned, because the frame is already there.
        proto = re.search(r'( *)<SL__arrayElement class="EventSpec">.*?</SL__arrayElement>',
                          s[en:end], re.S)
        assert proto, "no EventSpec to use as a shape"
        ind = proto.group(1)
        rows = [u'<SL__arrayElement class="EventSpec">',
                u'  <diagramIdx>%s</diagramIdx>' % diagram_idx,
                u'  <source>0</source>',
                u'  <regFlags>0</regFlags>',
                u'  <eSource>0</eSource>',
                u'  <type>0</type>',
                u'  <eFlags>0</eFlags>',
                u'  <ddoUID>0</ddoUID>',
                u'  <menuTag />',
                u'  <dynIndex>0</dynIndex>',
                u'  </SL__arrayElement>']
        blk = u'\n'.join(ind + r for r in rows)
        # insert before the closing tag, keeping list order = diagramIdx order
        ins = s.rindex('\n', 0, end) + 1
        s = s[:ins] + blk + '\n' + s[ins:]
        m = re.compile(r'<EventNodeEvents elements="(\d+)">').search(s, es)
        s = s[:m.start()] + '<EventNodeEvents elements="%d">' % (int(m.group(1)) + 1) + s[m.end():]
        print("   appended an EventSpec for diagramIdx %s (%s -> %s entries)"
              % (diagram_idx, m.group(1), int(m.group(1)) + 1))
        start = s.index(blk)
        close = start + len(blk)
    else:
        start, close, blk = target
    before = dict(re.findall(r'<(source|type|eFlags|ddoUID)>(-?\d+)</\1>', blk))
    new = blk
    if user_event is not None:
        fields = USER_EVENT + (('dynIndex', str(dyn_index or 1)),)
    else:
        fields = VALUE_CHANGE + (('ddoUID', str(ddo)),)
    for tag, val in fields:
        new, n = re.subn(r'<%s>-?\d+</%s>' % (tag, tag), '<%s>%s</%s>' % (tag, val, tag), new)
        assert n == 1, "expected one <%s> in the EventSpec, got %d" % (tag, n)

    s = s[:start] + new + s[close:]

    # The CACHED frame label. Without this the IDE shows " [N]  " - an event
    # case with no event - however correct the EventSpec is.
    if user_event is not None:
        # The cached text for a user-event frame. LabVIEW's own AIXML export
        # spells the selector ` <Name>A User Event `, so the displayed form
        # puts the event NAME IN ANGLE BRACKETS where a control label carries
        # double quotes. Written for the same reason as the static case: the IDE
        # reads this text and not the spec, so without it the frame shows
        # " [N]  " however correct the spec is.
        want = '" [%s] <%s>: User Event "' % (diagram_idx, user_event)
        m = re.compile(r'(<selString class="selLabel".*?<text>)(.*?)(</text>)', re.S).search(s, es)
        assert m, "no selString on this event structure"
        s = s[:m.start(2)] + want + s[m.end(2):]
        m = re.compile(r'<dIdx>(\d+)</dIdx>').search(s, es)
        assert m, "no dIdx on this event structure"
        s = s[:m.start()] + '<dIdx>%s</dIdx>' % diagram_idx + s[m.end():]
        print("   selString -> %r, dIdx -> %s" % (want, diagram_idx))
    elif label is not None:
        # The stored text CONTAINS raw double quotes - NI's own reads
        #   <text>" [1] "Button 1": Value Change "</text>
        # so the content must be taken up to </text>, never "to the next quote".
        # Matching to the next quote stops inside the label and every further
        # call then APPENDS instead of replacing, which built up
        #   " [2] "Button 2": Value Change "Button 1": Value Change "stop": ... "
        want = '" [%s] "%s": Value Change "' % (diagram_idx, label)
        m = re.compile(r'(<selString class="selLabel".*?<text>)(.*?)(</text>)', re.S).search(s, es)
        assert m, "no selString on this event structure"
        s = s[:m.start(2)] + want + s[m.end(2):]
        m = re.compile(r'<dIdx>(\d+)</dIdx>').search(s, es)
        assert m, "no dIdx on this event structure"
        s = s[:m.start()] + '<dIdx>%s</dIdx>' % diagram_idx + s[m.end():]
        print("   selString -> %r, dIdx -> %s" % (want, diagram_idx))

    io.open(p, 'w', encoding='utf-8', newline='').write(s)
    was = ('Timeout' if before.get('type') == '1073741825'
           else 'type ' + before.get('type', '?'))
    if user_event is not None:
        print("OK diagramIdx %s: %s -> User Event <%s>, dynIndex %s"
              % (diagram_idx, was, user_event, dyn_index or 1))
    else:
        print("OK diagramIdx %s: %s -> Value Change on ddo %s" % (diagram_idx, was, ddo))


if __name__ == '__main__':
    main(*sys.argv[1:])
