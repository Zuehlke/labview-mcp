# -*- coding: utf-8 -*-
"""SUPERSEDED 2026-09-10 - DO NOT REACH FOR THIS. Use
`lvai_generate_vi_with_events`, or `pylv-set-event-spec.py` directly.

This exists because "AIXML cannot author a second event frame" was believed, and
that was wrong: `ConvertAIXMLToVI` creates one frame per `CaseFrame` in the
document and only strips the REGISTRATION, which `pylv-set-event-spec.py` writes
back. So no frame ever has to be cloned, and cloning one is strictly worse - on
a generated VI it produces `Error 47, Unknown heap` for reasons that were never
established, while the supported route is `execState 1`.

Kept, not deleted, because that unexplained failure is the only evidence of it
anyone has. Do not build on this.

Add a STANDALONE event case to an Event Structure - a new frame of its own,
not another event on an existing frame - through pylabview, no LabVIEW running.

Companion to pylv-add-event-control.py, which registers an extra event on an
EXISTING frame (several EventSpecs may share one diagramIdx). That is cheap but
puts both controls in the same case. This script clones a whole frame instead.

MEASURED 2026-09-10 on NI's ProducerConsumerEvents.vit.

A frame is a `diag` under the eventStruct's `diagramList`, and cloning it means
remapping every uid inside it and appending to THREE parallel index lists that
are addressed by frame position:

    diagramList    the frame itself
    dataNodeList   its Event Data Node
    filterNodeList uid 0 for a non-filter event
    EventNodeEvents an EventSpec whose diagramIdx is the new frame's index

WHAT MUST NOT BE COPIED BLINDLY. Every frame's `sRN` termList carries its OWN
`term` objects that reference the structure's SHARED tunnel dcos - measured
identical across both donor frames: {177, 269, 273, 275, 289, 298, 307}. Those
dco uids must stay pointing at the originals; only the `term` uids are new.

And the donor's WIRES are frame-specific. In ProducerConsumerEvents the stop
frame wires its button into dco 177, which is the Out1 tunnel feeding the loop's
stop condition - copying that signal would make the new control stop the loop.
Only the In2 -> Out2 error passthrough is cloned. Out1 is left unwired, which is
safe here because the other frame already leaves it unwired (the tunnel uses its
default).

`selString` is NOT touched: it holds only the currently displayed frame's label,
which LabVIEW regenerates.

usage:
  pylv-add-event-frame.py <bundle> <base> <donorDiagUid> <ctlFpdcoUid>
                          <ctlDdoUid> <uidBase>
"""
import io, re, sys


def rd(p):
    return io.open(p, encoding='utf-8').read()


def wr(p, s):
    io.open(p, 'w', encoding='utf-8', newline='').write(s)


def slice_element(s, start):
    """Return (text, end) for the SL__arrayElement beginning at `start`,
    matched by indentation of its closing tag."""
    ind = start - s.rindex('\n', 0, start) - 1
    # this dialect closes a container two spaces deeper than it opens
    close = '\n' + ' ' * (ind + 2) + '</SL__arrayElement>'
    end = s.index(close, start) + len(close)
    return s[start:end], end


def main(bundle, base, donor_diag, fpdco, ddo, uidbase):
    p = "%s/%s_BDHb.xml" % (bundle, base)
    s = rd(p)
    u = int(uidbase)

    d_start = s.index('<SL__arrayElement class="diag" uid="%s">' % donor_diag)
    d_start = s.rindex('\n', 0, d_start) + 1          # keep leading indent
    donor, d_end = slice_element(s, s.index('<SL__arrayElement', d_start))
    donor = s[d_start:d_end]

    # ---- remap every uid defined inside the donor frame ------------------
    defined = re.findall(r'class="[A-Za-z_]+" uid="(\d+)"', donor)
    # the frame's own control terminal points at the DONOR control; retarget it
    # A donor frame need NOT contain a control terminal. An AIXML-authored
    # Timeout frame has none - the control terminals sit outside the structure
    # and the EventSpec binds by ddoUID - so this is optional.
    donor_fpterm = re.search(r'class="fPTerm" uid="(\d+)">\s*<objFlags>\d+</objFlags>\s*'
                             r'<dco uid="(\d+)" />', donor)
    old_fpterm = old_ctl = None
    if donor_fpterm:
        old_fpterm, old_ctl = donor_fpterm.group(1), donor_fpterm.group(2)
        lbl = re.search(r'class="fPTerm" uid="%s">.*?<label class="label" uid="(\d+)">'
                        % old_fpterm, donor, re.S)
        defined = list(dict.fromkeys(defined + [lbl.group(1)]))

    mapping = {}
    for k, old in enumerate(defined):
        mapping[old] = str(u + k)

    new = donor
    # uid="..." definitions and bare <SL__arrayElement uid="..."/> references
    def sub_uid(mm):
        return 'uid="%s"' % mapping.get(mm.group(1), mm.group(1))
    # protect the shared tunnel dcos: they are <dco uid="N" /> and must NOT move,
    # except the control terminal's own dco which we retarget explicitly.
    if old_ctl is not None:
        new = new.replace('<dco uid="%s" />' % old_ctl, '<dco uid="@CTL@" />', 1)
    shared = set(re.findall(r'<dco uid="(\d+)" />', new))
    placeholders = {}
    for k, d in enumerate(sorted(shared)):
        ph = '@S%d@' % k
        placeholders[ph] = d
        new = new.replace('<dco uid="%s" />' % d, '<dco uid="%s" />' % ph)

    new = re.sub(r'uid="(\d+)"', sub_uid, new)

    for ph, d in placeholders.items():
        new = new.replace('<dco uid="%s" />' % ph, '<dco uid="%s" />' % d)
    if old_ctl is not None:
        new = new.replace('<dco uid="@CTL@" />', '<dco uid="%s" />' % fpdco)

    new_diag = mapping[donor_diag]
    new_dnode = mapping[re.search(r'class="eventDataNode" uid="(\d+)"', donor).group(1)]
    new_fpterm = mapping[old_fpterm] if old_fpterm is not None else None

    # an unwired terminal: use the objFlags of a frame that leaves it unwired
    if new_fpterm is not None:
        new = re.sub(r'(class="fPTerm" uid="%s">\s*<objFlags>)\d+' % new_fpterm,
                     r'\g<1>131136', new)

    # ---- drop frame-specific wires, keep only In2 -> Out2 ----------------
    # dco 177 is the Out1 tunnel (loop stop condition). Any signal touching the
    # control terminal is donor-specific and must go.
    sig_open = new.index('<signalList elements=')
    sig_ind = ' ' * (sig_open - new.rindex('\n', 0, sig_open) - 1)
    sig_close = new.index('\n' + sig_ind + '  </signalList>')
    body = new[new.index('>', sig_open) + 1:sig_close]
    kept = []
    pos = 0
    while True:
        mm = re.compile(r'( *)<SL__arrayElement class="signal" uid="\d+">').search(body, pos)
        if not mm:
            break
        # mm.start() sits on the captured indent; slice_element needs the '<'
        text, end = slice_element(body, mm.start() + len(mm.group(1)))
        refs = re.findall(r'<SL__arrayElement uid="(\d+)" />', text)
        if new_fpterm not in refs:
            kept.append(text)
        pos = end
    new = (new[:new.index('>', sig_open) + 1]
           + ('\n' + '\n'.join(kept) if kept else '')
           + new[sig_close:])
    new = re.sub(r'<signalList elements="\d+">',
                 '<signalList elements="%d">' % len(kept), new, count=1)

    # APPEND the clone at the END of diagramList - a frame's POSITION in that
    # list IS its diagramIdx. Inserting it next to the donor silently reorders
    # the frames, and when the donor is not the last one LabVIEW aborts with
    #   Insane Event Data Node(359) ... {list order}(0x80000): owner=Diagram(342)
    #   Insane Event Dynamic Registration(269) ... {list order}(0x80000)
    # because every parallel list (dataNodeList, filterNodeList, EventSpec
    # diagramIdx) is addressed by that position.
    _es = s.index('class="eventStruct"')
    _dl_close = s.index('</diagramList>', _es)
    _ins = s.rindex('\n', 0, _dl_close) + 1
    s = s[:_ins] + new + s[_ins:]

    # ---- register the new frame's terminals WITH the shared dcos ---------
    # The link is bidirectional. Each tunnel / dynamic-registration / timeout
    # dco keeps its own termList of [frame0 term, frame1 term, ..., the
    # structure's own outside term] - one entry per frame plus one. Adding a
    # frame without extending these gives, on load:
    #   Insane Tunnel(307) ... {index}(0x2): owner=Event Structure(267)
    #   DAbort 0x1A7102DF: Fatal insanities(0x00080022)
    # The new entry goes BEFORE the last one, which is the outside terminal.
    pairs = re.findall(r'class="term" uid="(\d+)">\s*<objFlags>\d+</objFlags>\s*'
                       r'<dco uid="(\d+)" />', new)
    for term_uid, dco_uid in pairs:
        anchor = s.rindex('<dco class="', 0, s.index('" uid="%s">' % dco_uid) + 5)
        tl = re.compile(r'( *)<termList elements="(\d+)">').search(s, anchor)
        close = s.index('</termList>', tl.end())
        last = s.rindex('<SL__arrayElement uid=', tl.end(), close)
        last = s.rindex('\n', 0, last) + 1
        s = (s[:tl.start()]
             + '%s<termList elements="%d">' % (tl.group(1), int(tl.group(2)) + 1)
             + s[tl.end():last]
             + '%s  <SL__arrayElement uid="%s" />\n' % (tl.group(1), term_uid)
             + s[last:])

    # ---- the three index lists + the EventSpec ---------------------------
    # The new frame's index is its POSITION IN diagramList - never the number of
    # EventSpecs. Those differ as soon as one frame handles two events, and an
    # EventSpec whose diagramIdx names no frame is not caught by any XML check.
    _off = s.index('class="eventStruct"')
    new_frame_idx = int(re.compile(r'<diagramList elements="(\d+)">').search(s, _off).group(1))

    for tag, extra in (('diagramList', None),
                       ('dataNodeList', new_dnode),
                       ('filterNodeList', '0')):
        base_off = s.index('class="eventStruct"')
        mm = re.compile(r'( *)<%s elements="(\d+)">' % tag).search(s, base_off)
        n = int(mm.group(2))
        if tag == 'diagramList':
            s = (s[:mm.start()] + '%s<%s elements="%d">' % (mm.group(1), tag, n + 1)
                 + s[mm.end():])
        else:
            close = s.index('</%s>' % tag, mm.end())
            ins = s.rindex('\n', 0, close) + 1
            s = (s[:mm.start()] + '%s<%s elements="%d">' % (mm.group(1), tag, n + 1)
                 + s[mm.end():ins]
                 + '%s  <SL__arrayElement uid="%s" />\n' % (mm.group(1), extra)
                 + s[ins:])

    em = re.compile(r'( *)<EventNodeEvents elements="(\d+)">').search(
        s, s.index('class="eventStruct"'))
    n = int(em.group(2))
    close = s.index('</EventNodeEvents>', em.end())
    ins = s.rindex('\n', 0, close) + 1
    spec = (
        '{i}  <SL__arrayElement class="EventSpec">\n'
        '{i}    <diagramIdx>{d}</diagramIdx>\n'
        '{i}    <source>3</source>\n'
        '{i}    <regFlags>0</regFlags>\n'
        '{i}    <eSource>0</eSource>\n'
        '{i}    <type>1073741826</type>\n'
        '{i}    <eFlags>4</eFlags>\n'
        '{i}    <ddoUID>{u}</ddoUID>\n'
        '{i}    <menuTag />\n'
        '{i}    <dynIndex>0</dynIndex>\n'
        '{i}    </SL__arrayElement>\n').format(i=em.group(1), d=new_frame_idx, u=ddo)
    s = (s[:em.start()] + '%s<EventNodeEvents elements="%d">' % (em.group(1), n + 1)
         + s[em.end():ins] + spec + s[ins:])

    wr(p, s)
    print("OK: frame %s cloned from %s as diagramIdx %d "
          "(eventDataNode %s, fPTerm %s -> fPDCO %s, ddoUID %s), %d wire(s) kept"
          % (new_diag, donor_diag, new_frame_idx, new_dnode, new_fpterm, fpdco, ddo, len(kept)))


if __name__ == '__main__':
    main(*sys.argv[1:])
