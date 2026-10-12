# -*- coding: utf-8 -*-
"""Calibrate compressedWireTable: compose a VI of independent constant -> indicator pairs at
chosen offsets, let LabVIEW load it, repair the wires and save a copy (lv-save.ps1), then print
each pair's offset beside the table LabVIEW wrote.

    wire_calibrate.py <work dir> "dx,dy;dx,dy;..."
"""
import glob
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402

PY = sys.executable


def main(argv):
    work, spec = argv[0], argv[1]
    offs = [tuple(int(v) for v in p.split(",")) for p in spec.split(";") if p]
    os.makedirs(work, exist_ok=True)
    tag = "cal%d" % (abs(hash(spec)) % 100000)
    lines = ['<VI _name="%s.vi" description="wire calibration">' % tag]
    for i, _ in enumerate(offs):
        cu, iu = 4200 + 20 * i, 4210 + 20 * i
        lines.append('  <Constant _name="c%d" type="double" uid="%d" uid_parent="root" value="%d" outputs="value:%d.value"/>' % (i, cu, i + 1, cu))
        lines.append('  <Indicator _name="i%d" type="double" uid="%d" uid_parent="root" value="0" inputs="value:%d.value"/>' % (i, iu, cu))
    lines.append("</VI>")
    src = os.path.join(work, tag + ".xml")
    open(src, "w", encoding="utf-8").write("\n".join(lines))
    vi = os.path.join(work, tag + ".vi")
    bundle = os.path.join(work, tag + ".bundle")
    subprocess.run([PY, os.path.join(COMPOSER, "aixml2vi.py"), src, vi, "--keep", bundle], check=True,
                   capture_output=True, stdin=subprocess.DEVNULL)
    # place each pair: constant at (40 + 120*i, 40), indicator's terminal at constant + offset
    bd_path = glob.glob(os.path.join(bundle, "*_BDHb.xml"))[0]
    fp_path = glob.glob(os.path.join(bundle, "*_FPHb.xml"))[0]
    bd = ET.parse(bd_path)
    fp = ET.parse(fp_path)
    fpdco_of_ddo = {}
    for f in fp.getroot().iter("SL__arrayElement"):
        if f.get("class") == "fPDCO":
            fpdco_of_ddo[f.find("ddo").get("uid")] = f.get("uid")
    fpterm_of = {}
    for t in bd.getroot().iter("SL__arrayElement"):
        if t.get("class") == "fPTerm":
            fpterm_of[t.find("dco").get("uid")] = t
    const_ddo = {}
    for d in bd.getroot().iter("ddo"):
        const_ddo[d.get("uid")] = d
    for i, (dx, dy) in enumerate(offs):
        cu, iu = str(4200 + 20 * i), str(4210 + 20 * i)
        y = 40 + 120 * i
        lvheap.move_to(const_ddo[cu], y, 40)
        h, w = lvheap.size_of(const_ddo[cu])
        term = fpterm_of[fpdco_of_ddo[iu]]
        th, tw = lvheap.size_of(term)
        # the constant's output sits on its right edge, mid height; the indicator's input on the
        # terminal's left edge, mid height - aim the second at the first plus the offset
        lvheap.move_to(term, y + h // 2 + dy - th // 2, 40 + w + dx)
    bd.write(bd_path, encoding="utf-8", xml_declaration=True)
    main_xml = [f for f in glob.glob(os.path.join(bundle, "*.xml")) if not f.endswith(("_FPHb.xml", "_BDHb.xml"))][0]
    lvheap.rebuild(main_xml, vi)
    saved = os.path.join(work, tag + "_saved.vi")
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-save.ps1"), "-ViPath", vi, "-CopyPath", saved],
                       capture_output=True, text=True, stdin=subprocess.DEVNULL)
    print(r.stdout.strip())
    sb = lvheap.Bundle(lvheap.extract(saved, os.path.join(work, tag + "_saved.bundle")))
    # map each signal back to its pair through the constant's ddo uid
    for s in sb.bd.iter("SL__arrayElement"):
        if s.get("class") != "signal":
            continue
        uids = [x.get("uid") for x in s.find("termList")]
        pair = None
        for t in sb.bd.iter("SL__arrayElement"):
            if t.get("class") == "term" and t.get("uid") in uids and t.find("dco/ddo") is not None:
                d = t.find("dco/ddo")
                pair = (int(d.get("uid")) - 4200) // 20
                cb = d.findtext("bounds")
        if pair is None:
            continue
        ft = [t for t in sb.bd.iter("SL__arrayElement") if t.get("class") == "fPTerm" and t.get("uid") in uids][0]
        print("pair %2d off=%-10s const=%-22s term=%-22s table=%s" % (
            pair, "%d,%d" % offs[pair], cb, ft.findtext("bounds"), s.findtext("compressedWireTable")))


if __name__ == "__main__":
    main(sys.argv[1:])
