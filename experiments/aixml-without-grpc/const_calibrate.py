# -*- coding: utf-8 -*-
"""Where a COMPOSED constant's output attaches, per type: a constant feeding two indicators, saved by
LabVIEW, its fan-out table walked back from the indicators' exact hotpoints (wire_tree.walk).

    const_calibrate.py <work dir> "type|value" ["type|value" ...]

Prints, per constant, its composed and saved rectangles and the hotpoint relative to both - so the
rule (centre, or k px left of the right edge) can be read off for the composer's own parts.
"""
import glob
import os
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import lvheap  # noqa: E402
import wire_tree  # noqa: E402

PY = sys.executable


def const_bounds(bd_root):
    for d in bd_root.iter("ddo"):
        if d.get("uid") == "4200":
            return lvheap.parse_bounds(d.findtext("bounds"))


def main(argv):
    work = os.path.normpath(os.path.abspath(argv[0]))
    os.makedirs(work, exist_ok=True)
    for i, spec in enumerate(argv[1:]):
        typ, value = spec.split("|", 1)
        tag = "cc%d" % i
        vi = os.path.join(work, tag + ".vi")
        src = os.path.join(work, tag + ".xml")
        open(src, "w", encoding="utf-8").write(
            '<VI _name="%s.vi" description="constant calibration">\n'
            '  <Constant _name="c" type="%s" uid="4200" uid_parent="root" value="%s" outputs="value:4200.value"/>\n'
            '  <Indicator _name="a" type="%s" uid="4210" uid_parent="root" value="%s" inputs="value:4200.value"/>\n'
            '  <Indicator _name="b" type="%s" uid="4220" uid_parent="root" value="%s" inputs="value:4200.value"/>\n'
            '</VI>\n' % (tag, typ, value, typ, value, typ, value))
        bundle = os.path.join(work, tag + ".bundle")
        r = subprocess.run([PY, os.path.join(HERE, "aixml2vi.py"), src, vi, "--keep", bundle], capture_output=True, text=True,
                           env=dict(os.environ, AIXML2VI_GENERIC_WIRES="1"))
        if not os.path.exists(vi):
            print(spec, "NOT COMPOSED", (r.stdout + r.stderr).strip().splitlines()[-1:])
            continue
        comp = const_bounds(ET.parse(glob.glob(os.path.join(bundle, "*_BDHb.xml"))[0]).getroot())
        saved = os.path.join(work, tag + "_saved.vi")
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-save.ps1"), "-ViPath", vi, "-CopyPath", saved], capture_output=True, text=True)
        sb = lvheap.extract(saved, os.path.join(work, tag + "_saved.bundle"))
        sbd = ET.parse(glob.glob(os.path.join(os.path.dirname(sb), "*_BDHb.xml"))[0]).getroot()
        sav = const_bounds(sbd)
        sinks = []
        for t in sbd.iter("SL__arrayElement"):
            if t.get("class") == "fPTerm":
                a, b, c, d = lvheap.parse_bounds(t.findtext("bounds"))
                sinks.append(((a + c) // 2, (b + d) // 2))
        sig = next(s for s in sbd.iter("SL__arrayElement") if s.get("class") == "signal")
        ends = wire_tree.walk(sig.findtext("compressedWireTable"))
        if not ends:
            print(spec, "table not a tree:", sig.findtext("compressedWireTable"))
            continue
        hy, hx = [(k[0] - g[0], k[1] - g[1]) for g, k in zip(sorted(ends), sorted(sinks))][0]
        print("%-28s composed %-22s saved %-22s hotpoint (%d,%d): from saved right %d, bottom %d; "
              "from composed right %d, bottom %d" % (
                  spec, comp, sav, hy, hx, sav[3] - hx, sav[2] - hy, comp[3] - hx, comp[2] - hy), flush=True)


if __name__ == "__main__":
    main(sys.argv[1:])
