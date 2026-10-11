# -*- coding: utf-8 -*-
"""Does a composed VI with REAL wire tables load without LabVIEW's wire repairs?

    validate_routes.py <work dir> <test> [<test> ...]

Per suite test: compose it, keep the bundle, write a second VI from the same bundle with every
wire routed (wire_route.route_heap) under its own VI name; on a FRESH LabVIEW load the
generic one, then the routed one, counting the DWarns each adds - `Wiring insanity found and fixed`
is LabVIEW repairing a table - and have LabVIEW save a copy of the routed one, to see whether it
keeps our tables (it rewrites a table it repaired).
"""
import glob
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import lvheap  # noqa: E402
import selftest as stt  # noqa: E402
import wire_route  # noqa: E402

PY = sys.executable


def tables(bundle_bd):
    root = ET.parse(bundle_bd).getroot()
    return {s.get("uid"): (len(s.find("termList")), s.findtext("compressedWireTable"))
            for s in root.iter("SL__arrayElement") if s.get("class") == "signal"}


def main(argv):
    # backslashes: LabVIEW's COM opens nothing at a C:/... path (Git Bash hands over forward ones)
    work, tests = os.path.normpath(os.path.abspath(argv[0])), argv[1:]
    os.makedirs(work, exist_ok=True)
    hp = wire_route.Hotpoints(os.path.join(HERE, "parts"))
    for name in tests:
        src = os.path.join(HERE, "tests", name + ".xml")
        gen_vi = os.path.join(work, name + " generic.vi")
        bundle = os.path.join(work, name + ".bundle")
        shutil.rmtree(bundle, ignore_errors=True)
        # the composer routes by itself now: the generic VI is composed with that switched off
        r = subprocess.run([PY, os.path.join(HERE, "aixml2vi.py"), src, gen_vi, "--keep", bundle],
                           capture_output=True, text=True, env=dict(os.environ, AIXML2VI_GENERIC_WIRES="1"))
        if not os.path.exists(gen_vi):
            print("%-34s NOT COMPOSED %s" % (name, (r.stdout + r.stderr).strip().splitlines()[-1:]))
            continue
        bd_path = glob.glob(os.path.join(bundle, "*_BDHb.xml"))[0]
        main_xml = [f for f in glob.glob(os.path.join(bundle, "*.xml"))
                    if not f.endswith(("_FPHb.xml", "_BDHb.xml"))][0]
        generic = tables(bd_path)
        tree = ET.parse(bd_path)
        routed, left = wire_route.route_heap(tree.getroot(), hp)
        tree.write(bd_path, encoding="utf-8", xml_declaration=True)
        m = ET.parse(main_xml)
        sec = m.getroot().find("LVSR/Section")
        sec.set("Name", name + " routed.vi")
        m.write(main_xml, encoding="utf-8", xml_declaration=True)
        rt_vi = os.path.join(work, name + " routed.vi")
        lvheap.rebuild(main_xml, rt_vi)
        stt.restart_labview()
        counts = {}
        for tag, vi in (("generic", gen_vi), ("routed", rt_vi)):
            b = len(stt.dwarn_lines())
            state, _, raw = stt.lvcom(vi, "", [], run=False)
            new = stt.dwarn_lines()[b:]
            counts[tag] = (state, sum("Wiring insanity" in l for l in new), len(new))
        saved = os.path.join(work, name + " routed saved.vi")
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-save.ps1"), "-ViPath", rt_vi, "-CopyPath", saved],
                       capture_output=True, text=True)
        # per wire kind - two ends, or a fan-out the router wrote (not a generic table) - how many
        # of our tables LabVIEW's save kept, and the fan-outs it rewrote
        kept, total, rewritten = {2: 0, 3: 0}, {2: 0, 3: 0}, []
        if os.path.exists(saved):
            sb = lvheap.extract(saved, os.path.join(work, name + " saved.bundle"))
            ours = tables(bd_path)
            sb = sb if os.path.isdir(sb) else os.path.dirname(sb)    # extract answers the main XML
            theirs = tables(glob.glob(os.path.join(sb, "*_BDHb.xml"))[0])
            for uid, (n, t) in ours.items():
                if uid not in theirs or (n > 2 and generic.get(uid, (0, ""))[1] == t):   # fan-out left generic
                    continue
                k = min(n, 3)
                total[k] += 1
                if theirs[uid][1] == t:
                    kept[k] += 1
                elif k == 3:
                    rewritten.append("%s: ours %s, LabVIEW's %s" % (uid, t, theirs[uid][1]))
        print("%-34s routed %2d left %2d | generic ExecState %s repairs %3d (DWarns %3d) | routed ExecState %s "
              "repairs %3d (DWarns %3d) | LabVIEW's save kept %d of %d two-ended, %d of %d fan-out tables" % (
                  name, routed, left, counts["generic"][0], counts["generic"][1], counts["generic"][2],
                  counts["routed"][0], counts["routed"][1], counts["routed"][2], kept[2], total[2], kept[3], total[3]),
              flush=True)
        for r in rewritten:
            print("    rewritten", r)


if __name__ == "__main__":
    main(sys.argv[1:])
