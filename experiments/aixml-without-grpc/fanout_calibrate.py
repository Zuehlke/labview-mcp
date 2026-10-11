# -*- coding: utf-8 -*-
"""Calibrate the FAN-OUT wire table: one constant feeding several indicators placed at chosen
offsets from its hotpoint; LabVIEW loads, repairs and saves a copy, and the table it wrote is printed
beside each sink's offset - the raw material for decoding the tree format.

    fanout_calibrate.py <work dir> "dx,dy;dx,dy[;...]" [more specs ...]

One VI per spec (each spec one wire with 1 + len(spec) ends). Offsets are from the constant's
hotpoint to each indicator terminal's hotpoint, centre rule (wire_route.py). Needs a running
LabVIEW over COM (lv-save.ps1).
"""
import glob
import os
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import lvheap  # noqa: E402

PY = sys.executable


def centre(b):
    t, l, bb, r = b
    return (t + bb) // 2, (l + r) // 2


def one(work, idx, offs):
    tag = "fan%d_%d" % (idx, abs(hash(tuple(offs))) % 100000)
    lines = ['<VI _name="%s.vi" description="fan-out calibration">' % tag,
             '  <Constant _name="c" type="double" uid="4200" uid_parent="root" value="1" outputs="value:4200.value"/>']
    for i, _ in enumerate(offs):
        lines.append('  <Indicator _name="i%d" type="double" uid="%d" uid_parent="root" value="0" inputs="value:4200.value"/>'
                     % (i, 4210 + 10 * i))
    lines.append("</VI>")
    src = os.path.join(work, tag + ".xml")
    open(src, "w", encoding="utf-8").write("\n".join(lines))
    vi = os.path.join(work, tag + ".vi")
    bundle = os.path.join(work, tag + ".bundle")
    subprocess.run([PY, os.path.join(HERE, "aixml2vi.py"), src, vi, "--keep", bundle], check=True,
                   capture_output=True, env=dict(os.environ, AIXML2VI_GENERIC_WIRES="1"))
    bd_path = glob.glob(os.path.join(bundle, "*_BDHb.xml"))[0]
    fp_path = glob.glob(os.path.join(bundle, "*_FPHb.xml"))[0]
    bd, fp = ET.parse(bd_path), ET.parse(fp_path)
    fpdco_of_ddo = {f.find("ddo").get("uid"): f.get("uid") for f in fp.getroot().iter("SL__arrayElement")
                    if f.get("class") == "fPDCO"}
    fpterm_of = {t.find("dco").get("uid"): t for t in bd.getroot().iter("SL__arrayElement") if t.get("class") == "fPTerm"}
    const = next(d for d in bd.getroot().iter("ddo") if d.get("uid") == "4200")
    lvheap.move_to(const, 200, 40)
    cy, cx = centre(lvheap.parse_bounds(const.findtext("bounds")))
    for i, (dx, dy) in enumerate(offs):
        term = fpterm_of[fpdco_of_ddo[str(4210 + 10 * i)]]
        th, tw = lvheap.size_of(term)
        # the terminal's centre at the constant's hotpoint + offset
        lvheap.move_to(term, cy + dy - th // 2, cx + dx - tw // 2)
    bd.write(bd_path, encoding="utf-8", xml_declaration=True)
    main_xml = [f for f in glob.glob(os.path.join(bundle, "*.xml")) if not f.endswith(("_FPHb.xml", "_BDHb.xml"))][0]
    lvheap.rebuild(main_xml, vi)
    saved = os.path.join(work, tag + "_saved.vi")
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-save.ps1"), "-ViPath", vi, "-CopyPath", saved],
                       capture_output=True, text=True)
    if "saved" not in r.stdout:
        print(tag, "SAVE FAILED", r.stdout.strip(), r.stderr.strip()[:200])
        return
    sb = lvheap.extract(saved, os.path.join(work, tag + "_saved.bundle"))
    sbd = ET.parse(glob.glob(os.path.join(os.path.dirname(sb) if not os.path.isdir(sb) else sb, "*_BDHb.xml"))[0]).getroot()
    # the saved geometry: constant hotpoint and each sink's, as LabVIEW kept them
    sconst = next(d for d in sbd.iter("ddo") if d.get("uid") == "4200")
    scy, scx = centre(lvheap.parse_bounds(sconst.findtext("bounds")))
    sinks = []
    for t in sbd.iter("SL__arrayElement"):
        if t.get("class") == "fPTerm":
            y, x = centre(lvheap.parse_bounds(t.findtext("bounds")))
            sinks.append((t.get("uid"), y - scy, x - scx))
    for s in sbd.iter("SL__arrayElement"):
        if s.get("class") == "signal":
            order = [x.get("uid") for x in s.find("termList")]
            rel = {u: (dy, dx) for u, dy, dx in sinks}
            print("%s offs %-30s table %-40s ends in order (dy,dx): %s" % (
                tag, ";".join("%d,%d" % o for o in offs), s.findtext("compressedWireTable"),
                [rel.get(u, "src") for u in order]))


def main(argv):
    work = os.path.normpath(os.path.abspath(argv[0]))
    os.makedirs(work, exist_ok=True)
    for i, spec in enumerate(argv[1:]):
        offs = [tuple(int(v) for v in p.split(",")) for p in spec.split(";") if p]
        one(work, i, offs)


if __name__ == "__main__":
    main(sys.argv[1:])
