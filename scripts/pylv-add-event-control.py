# -*- coding: utf-8 -*-
"""Add a boolean front-panel control to a VI and register its Value Change
event on an existing Event Structure frame - through pylabview, with NO
LabVIEW running.

This exists because AIXML cannot author event registration at all: NI lists
Event Structure as unsupported for generation, and an event structure's own
untouched export is refused by ValidateAIXML with "One or more event cases
have no events defined". pylabview is the only route.

MEASURED 2026-09-10 on NI's ProducerConsumerEvents.vit:
  execState 1, no linker errors, and LabVIEW's own AIXML export reports the
  compound selector ' "Start Measurement"\\2C "Reset"\\3A Value Change '.

FOUR PLACES MUST CHANGE TOGETHER. Each was established by a failure:

  1. FPHb    a cloned fPDCO/ddo subtree, plus the fPDCO uid in ddoList and
             the pane's zPlaneList count.
  2. main    a VCTP FlatTypeID, TWO VCTP/TopLevel entries (fPDCO and ddo each
             reference one), the DTHP TypeDescSlice Count, and MUID.
             Doing 1+2 only gives "load error code 6: Could not load block
             diagram" - a front-panel control is not a front-panel-only object.
  3. BDHb    an fPTerm in the target diagram's sRN termList, and that fPTerm's
             uid in the diagram's zPlaneList. An fPTerm placed directly in a
             diagram zPlaneList makes LabVIEW DAbort:
               Insane Front Panel Terminal(N): {graphics}(0x80) {class}(0x8)
               DAbort 0x1A7102DF ... source\\panel\\fpsane.cpp(560)
             LabVIEW sanity-checks the heap and aborts deliberately; it does
             not limp on. Omitting only the zPlaneList uid loads fine but the
             terminal is not drawn and cannot be wired.
  4. BDHb    an EventSpec in EventNodeEvents. type 1073741826 is Value Change,
             source 3 is a front-panel control, ddoUID is the DDO (not the
             fPDCO). Several EventSpecs may share one diagramIdx, which is how
             a new event joins an EXISTING frame - no new diagram needed.

An EventSpec binds its control by ddoUID, never by name, so renaming a control
afterwards cannot break its event.

LIMIT: only a boolean is cloned, because the donor must already be on the
panel. A numeric needs a stdNum donor, which a template with no numeric
control cannot supply.

usage:
  pylv-add-event-control.py <bundle> <base> <donorLabel> <newLabel> \\
                            <diagUid> <srnUid> <uidBase> [termBounds]

Run pylv_extract first and pylv_rebuild after; the target .vi must NOT be
loaded in LabVIEW, or LabVIEW keeps serving its stale in-memory copy.
"""
import io, re, sys


def rd(p):
    return io.open(p, encoding='utf-8').read()


def wr(p, s):
    io.open(p, 'w', encoding='utf-8', newline='').write(s)


FPTERM = (
    '{i}  <SL__arrayElement class="fPTerm" uid="{t}">\n'
    '{i}    <objFlags>131136</objFlags>\n'
    '{i}    <dco uid="{d}" />\n'
    '{i}    <bounds>{b}</bounds>\n'
    '{i}    <label class="label" uid="{L}">\n'
    '{i}      <objFlags>1507650</objFlags>\n'
    '{i}      <partID>16</partID>\n'
    '{i}      <howGrow>4096</howGrow>\n'
    '{i}      <bounds>(-17, 0, 0, 95)</bounds>\n'
    '{i}      <image class="Image">\n'
    '{i}        <ImageResID>-619</ImageResID>\n'
    '{i}        </image>\n'
    '{i}      <fgColor>01000000</fgColor>\n'
    '{i}      <bgColor>01000000</bgColor>\n'
    '{i}      <textRec class="textHair">\n'
    '{i}        <flags>33280</flags>\n'
    '{i}        <mode>17412</mode>\n'
    '{i}        <bgColor>01000000</bgColor>\n'
    '{i}        </textRec>\n'
    '{i}      </label>\n'
    '{i}    </SL__arrayElement>\n')

EVENTSPEC = (
    '\n{i}  <SL__arrayElement class="EventSpec">\n'
    '{i}    <diagramIdx>{d}</diagramIdx>\n'
    '{i}    <source>3</source>\n'
    '{i}    <regFlags>0</regFlags>\n'
    '{i}    <eSource>0</eSource>\n'
    '{i}    <type>1073741826</type>\n'
    '{i}    <eFlags>4</eFlags>\n'
    '{i}    <ddoUID>{u}</ddoUID>\n'
    '{i}    <menuTag />\n'
    '{i}    <dynIndex>0</dynIndex>\n'
    '{i}    </SL__arrayElement>')


def patch_main(path, new_label, uid_ceiling):
    """VCTP FlatTypeID + two TopLevel entries + DTHP slice + MUID."""
    m = rd(path)

    tl = m.index('      <TopLevel>')
    flat = [int(x) for x in re.findall(r'<!-- FlatTypeID (\d+):', m[:tl])]
    new_flat = max(flat) + 1
    m = (m[:tl]
         + '      <!-- FlatTypeID %d: Type Descriptor with Boolean data -->\n' % new_flat
         + '      <TypeDesc Type="Boolean" Format="inline" Label="%s" />\n' % new_label
         + m[tl:])

    idx = [int(x) for x in re.findall(r'<TypeDesc Index="(\d+)" FlatTypeID="\d+" />', m)]
    n1, n2 = max(idx) + 1, max(idx) + 2
    close = m.index('        </TopLevel>')
    m = (m[:close]
         + '        <TypeDesc Index="%d" FlatTypeID="%d" />\n' % (n1, new_flat)
         + '        <TypeDesc Index="%d" FlatTypeID="%d" />\n' % (n2, new_flat)
         + m[close:])

    sl = re.search(r'<TypeDescSlice IndexShift="(\d+)" Count="(\d+)" />', m)
    assert sl, "DTHP TypeDescSlice not found"
    shift, cnt = int(sl.group(1)), int(sl.group(2))
    m = (m[:sl.start()]
         + '<TypeDescSlice IndexShift="%d" Count="%d" />' % (shift, cnt + 2)
         + m[sl.end():])

    mu = re.search(r'(<Section Index="0" Value=")(\d+)(" Format="inline" />\s*</MUID>)', m)
    assert mu, "MUID section not found"
    m = (m[:mu.start()] + mu.group(1)
         + str(max(int(mu.group(2)), uid_ceiling)) + mu.group(3) + m[mu.end():])

    wr(path, m)
    # heap TypeID n corresponds to consolidated index (n - 1 + shift)
    return n1 - shift + 1, n2 - shift + 1


def patch_fp(path, donor_label, new_label, uids):
    """Clone the donor fPDCO subtree; register it in ddoList and zPlaneList."""
    FPDCO, DDO, LBL, MLBL, COSM, ANNEX, heap1, heap2 = uids
    f = rd(path)

    tag = '        <SL__arrayElement class="fPDCO" uid="'
    starts = [mm.start() for mm in re.finditer(re.escape(tag), f)]
    assert starts, "no fPDCO in FPHb"
    donor = d_end = None
    for i, st in enumerate(starts):
        end = starts[i + 1] if i + 1 < len(starts) else f.index('        </zPlaneList>', st)
        if '"%s"' % donor_label in f[st:end]:
            donor, d_end = f[st:end], end
            break
    assert donor, "donor control %r not found in FPHb" % donor_label

    old = re.findall(r'uid="(\d+)"', donor)
    assert len(old) == 6, "expected 6 uids in the donor subtree, got %r" % (old,)
    new = donor
    for o, nw in zip(old, [FPDCO, DDO, LBL, MLBL, COSM, ANNEX]):
        new = new.replace('uid="%s"' % o, 'uid="@%d@"' % nw, 1)
    new = re.sub(r'uid="@(\d+)@"', r'uid="\1"', new)

    tds = re.findall(r'<typeDesc>TypeID\((\d+)\)</typeDesc>', new)
    assert len(tds) == 2, "expected 2 typeDesc in the donor, got %r" % (tds,)
    new = new.replace('<typeDesc>TypeID(%s)</typeDesc>' % tds[0],
                      '<typeDesc>TypeID(%d)</typeDesc>' % heap1, 1)
    new = new.replace('<typeDesc>TypeID(%s)</typeDesc>' % tds[1],
                      '<typeDesc>TypeID(%d)</typeDesc>' % heap2, 1)
    new = new.replace('"%s"' % donor_label, '"%s"' % new_label)

    db = re.search(r'<bounds>\((\d+), (\d+), (\d+), (\d+)\)</bounds>', new)
    t, l, b, r = (int(x) for x in db.groups())
    h = b - t + 12
    new = (new[:db.start()]
           + '<bounds>(%d, %d, %d, %d)</bounds>' % (t + h, l, b + h, r)
           + new[db.end():])

    f = f[:d_end] + new + f[d_end:]

    zp = re.search(r'<zPlaneList elements="(\d+)">', f)
    f = f[:zp.start()] + '<zPlaneList elements="%d">' % (int(zp.group(1)) + 1) + f[zp.end():]

    dl = re.search(r'( *)<ddoList elements="(\d+)">\n', f)
    f = (f[:dl.start()]
         + '%s<ddoList elements="%d">\n' % (dl.group(1), int(dl.group(2)) + 1)
         + '%s  <SL__arrayElement uid="%d" />\n' % (dl.group(1), FPDCO)
         + f[dl.end():])
    wr(path, f)


def patch_bd(path, diag, srn, fpdco, ddo, term, tlbl, bounds):
    """fPTerm into the sRN termList, its uid onto the diagram, then EventSpec."""
    s = rd(path)

    i = s.index('<SL__arrayElement class="sRN" uid="%s">' % srn)
    tm = re.compile(r'<termList elements="(\d+)">').search(s, i)
    assert tm and tm.start() - i < 400, "termList not found just after sRN %s" % srn
    ind = ' ' * (tm.start() - s.rindex('\n', 0, tm.start()) - 1)
    s = (s[:tm.start()] + '<termList elements="%d">\n' % (int(tm.group(1)) + 1)
         + FPTERM.format(i=ind, t=term, d=fpdco, L=tlbl, b=bounds).rstrip('\n')
         + s[tm.end():])

    j = s.index('<SL__arrayElement class="diag" uid="%s">' % diag)
    zm = re.compile(r'( *)<zPlaneList elements="(\d+)">\n').search(s, j)
    assert zm and zm.start() - j < 200, "zPlaneList not found just after diag %s" % diag
    s = (s[:zm.start()]
         + '%s<zPlaneList elements="%d">\n%s  <SL__arrayElement uid="%d" />\n'
         % (zm.group(1), int(zm.group(2)) + 1, zm.group(1), term)
         + s[zm.end():])

    em = re.search(r'( *)<EventNodeEvents elements="(\d+)">', s)
    assert em, "EventNodeEvents not found"
    first_end = s.index('</SL__arrayElement>', em.end()) + len('</SL__arrayElement>')
    di = re.search(r'<diagramIdx>(\d+)</diagramIdx>', s[em.end():first_end]).group(1)
    s = (s[:em.start()] + em.group(1)
         + '<EventNodeEvents elements="%d">' % (int(em.group(2)) + 1)
         + s[em.end():first_end]
         + EVENTSPEC.format(i=em.group(1), d=di, u=ddo)
         + s[first_end:])
    wr(path, s)
    return di


def main(bundle, base, donor_label, new_label, diag, srn, uidbase,
         bounds="(115, 22, 131, 54)"):
    u = int(uidbase)
    FPDCO, DDO, LBL, MLBL, COSM, ANNEX, TERM, TLBL = (u, u + 1, u + 2, u + 3,
                                                      u + 4, u + 5, u + 6, u + 7)
    heap1, heap2 = patch_main("%s/%s.xml" % (bundle, base), new_label, u + 50)
    patch_fp("%s/%s_FPHb.xml" % (bundle, base), donor_label, new_label,
             (FPDCO, DDO, LBL, MLBL, COSM, ANNEX, heap1, heap2))
    di = patch_bd("%s/%s_BDHb.xml" % (bundle, base), diag, srn,
                  FPDCO, DDO, TERM, TLBL, bounds)
    print("OK %r: fPDCO %d / ddo %d, heap TypeID %d+%d, fPTerm %d in sRN %s, "
          "Value Change registered on diagramIdx %s"
          % (new_label, FPDCO, DDO, heap1, heap2, TERM, srn, di))


if __name__ == '__main__':
    main(*sys.argv[1:])
