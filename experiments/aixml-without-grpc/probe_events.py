# -*- coding: utf-8 -*-
"""Close the loop on composed EVENTS with LabVIEW's own answers - no guess survives it.

    probe_events.py <probe.xml> [--out DIR] [--keep-names]

1. compose the AIXML (aixml2vi.py);
2. load it over COM - ExecState must be 1;
3. have LabVIEW SAVE a copy: LabVIEW recomputes every frame's event data on load (a composed plain
   refnum came back strict, 2026-10-10), so the saved copy is what LabVIEW itself writes - compare
   each frame's data cluster and EventSpecs with the composed VI;
4. have NI's EXPORTER (lvai_convert_vi_to_aixml over gRPC) read the saved copy and name each
   frame's events - the type numbers are right exactly when the names come back as the probe
   asked for them.

Prints one line per frame and a verdict; exits 1 when anything differs. Needs LabVIEW running with
its AI service up (step 4).
"""
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
from aixml2vi import event_items, unescape  # noqa: E402
import event_frame_data  # noqa: E402

PY = sys.executable


def frames_by_order(root):
    """[[items per frame] per Event Structure, in document order]."""
    return [[sorted(event_items(unescape(f.get("selector") or ""))) for f in st if f.tag == "CaseFrame"]
            for st in root.iter("Structure") if st.get("_name") == "Event Structure"]


def heap_frames(vi):
    out = []
    for rec in event_frame_data.scan(vi, False):
        # the FILTER cluster too (a filter event's Event Filter Node, None elsewhere) - it is a
        # cluster LabVIEW writes on load like the data cluster (2026-10-11)
        out.append({f["idx"]: (f["data"], [(s["type"], s["source"], s["regFlags"], s["eSource"], s["eFlags"], s["dynIndex"])
                                           for s in f["specs"]], f.get("filter")) for f in rec["frames"]})
    return out


def com(script, *args):
    r = subprocess.run(["powershell", "-ExecutionPolicy", "Bypass", "-File", os.path.join(HERE, script)] + list(args),
                       capture_output=True, text=True)
    return r.stdout


def main(argv):
    src = argv[0]
    out = argv[argv.index("--out") + 1] if "--out" in argv else os.path.join(os.environ["TEMP"], "LabVIEWMCP", "probes")
    os.makedirs(out, exist_ok=True)
    base = os.path.splitext(os.path.basename(src))[0]
    n = 1
    while os.path.exists(os.path.join(out, "%s %d.vi" % (base, n))):
        n += 1
    vi = os.path.join(out, "%s %d.vi" % (base, n))
    saved = os.path.join(out, "%s %d saved.vi" % (base, n))
    r = subprocess.run([PY, os.path.join(COMPOSER, "aixml2vi.py"), src, vi], capture_output=True, text=True)
    if not os.path.exists(vi):
        print("NOT COMPOSED:", (r.stderr or r.stdout).strip().splitlines()[-1:])
        return 1
    st = re.search(r"ExecState = (\d)", com("lv-com.ps1", "-ViPath", vi))
    print("composed %s - ExecState %s" % (os.path.basename(vi), st.group(1) if st else "?"))
    com("lv-save.ps1", "-ViPath", vi, "-CopyPath", saved)
    bad = 0 if st and st.group(1) == "1" else 1
    a, b = heap_frames(vi), heap_frames(saved)
    for si, (fa, fb) in enumerate(zip(a, b)):
        for k in sorted(set(fa) | set(fb)):
            same = fa.get(k) == fb.get(k)
            bad += not same
            print("  structure %d frame %d: data/specs/filter %s" % (si, k, "SAME" if same else "DIFFER"))
            if not same:
                print("     composed:", fa.get(k))
                print("     labview :", fb.get(k))
    # NI's exporter names the events of LabVIEW's own save
    import helper_parity as hp
    server = hp.Server()
    try:
        xml = saved[:-3] + ".xml"
        body = server.tool("lvai_convert_vi_to_aixml", {"viPath": saved, "aiXmlFilePath": xml,
                                                        "returnContent": False, "backend": "grpc"})
    finally:
        server.p.terminate()
    if body.get("errorCode") != 0 or not os.path.exists(xml):
        print("  EXPORT FAILED:", body.get("errorMessage") or body.get("error"))
        return 1
    want, got = frames_by_order(ET.parse(src).getroot()), frames_by_order(ET.parse(xml).getroot())
    norm = lambda fr: [[[re.sub(r"\s+", " ", x) for x in f] for f in st] for st in fr]  # noqa: E731
    for si, (w, g) in enumerate(zip(norm(want), norm(got))):
        for k, (wf, gf) in enumerate(zip(w, g)):
            ok = wf == gf
            bad += not ok
            print("  structure %d frame %d: names %s %s" % (si, k, "SAME" if ok else "DIFFER", "" if ok else "%s -> %s" % (wf, gf)))
    print("VERDICT:", "all as LabVIEW writes and names them" if not bad else "%d difference(s)" % bad)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
