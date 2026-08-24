#!/usr/bin/env python3
"""Census a LabVIEW tree against NI's not-yet-supported list for the Coding Agent.

NI publishes that list qualitatively - "Event Structure", "VIs that depend on user VIs outside the
supported node catalog", "Timed Loop", and so on. What it does not say is how much of a real
codebase falls into each entry, and that number is what decides whether a gap is a footnote or the
main obstacle. This measures it.

Every detector is STRUCTURAL and needs no LabVIEW: pylabview extracts the file, and the heap object
classes plus the link records answer the question. So the census can run on a build agent, on a
checkout, with no licence.

Detectors, by the class names pylabview uses (LVheap.py SL_SYSTEM_TAGS):
  eventStruct                  Event Structure                     - not generatable
  timeLoop, timeLoopExtNode    Timed Loop                          - not generatable
  dynPolyIUse                  a call into a polymorphic VI
  xStructure, externalStructNode  XNode-ish structures
  IUVI link records            static subVI calls, split by whether the callee's stored path is
                               rooted at a LabVIEW symbolic root (<vilib>, <userlib>, ...) or is a
                               relative/absolute path into the project's own code. The second kind
                               is exactly NI's "user VIs outside the supported node catalog", which
                               AIXML answers with Error 53.

PRIVACY: prints aggregates only. No file name, path, label, library name or subVI name leaves the
process - the interesting trees are customer trees and a document written from one must not carry
the customer's vocabulary.

Usage:
    python survey_gaps.py --bundle <pylabview bundle> --root <tree> [--limit N]
"""

import argparse
import collections
import os
import pathlib
import re
import shutil
import subprocess
import tempfile

HEAP_CLASS = re.compile(r'class="(\w+)"')
IUVI = re.compile(r'<IUVI\b.*?</IUVI>', re.S)
PATHREF = re.compile(r'<LinkSavePathRef[^>]*TpVal="(\d)"[^>]*>(.*?)</LinkSavePathRef>', re.S)
FIRST_SEG = re.compile(r'<String>([^<]*)</String>')
QUALNAME = re.compile(r'<LinkSaveQualName>(.*?)</LinkSaveQualName>', re.S)

SYMBOLIC_ROOTS = {"&lt;vilib&gt;", "&lt;userlib&gt;", "&lt;instrlib&gt;",
                  "&lt;resource&gt;", "&lt;bldsupport&gt;", "&lt;templates&gt;"}

GAPS = {
    "Event Structure": ("eventStruct",),
    "Timed Loop": ("timeLoop", "timeLoopExtNode"),
    "call into polymorphic VI": ("dynPolyIUse",),
    "XNode-ish structure": ("xStructure", "externalStructNode"),
    "In Place Element Structure": ("decomposeRecomposeStructure",),
    "Property Node": ("propNode",),
    "Local Variable / global ref": ("gRef",),
    "Event Data Node": ("eventDataNode",),
    "Event registration node": ("eventRegNode",),
}


def extract(bundle, path, work):
    main = os.path.join(work, "g.xml")
    try:
        subprocess.run([os.path.join(bundle, "python.exe"),
                        os.path.join("app", "pylabview", "readRSRC.py"),
                        "-x", "-i", str(path), "-m", main],
                       cwd=bundle, capture_output=True, timeout=90, check=False)
    except subprocess.TimeoutExpired:
        return None, None
    bd = os.path.join(work, "g_BDHb.xml")
    return (main if os.path.isfile(main) else None,
            bd if os.path.isfile(bd) else None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", required=True)
    ap.add_argument("--root", required=True)
    ap.add_argument("--limit", type=int, default=0)
    args = ap.parse_args()

    files = sorted(pathlib.Path(args.root).rglob("*.vi"))
    total_found = len(files)
    if args.limit:
        step = max(1, total_found // args.limit)
        files = files[::step][: args.limit]      # spread the sample over the tree, not the first N
    print("VIs in tree: %d, sampling %d" % (total_found, len(files)), flush=True)

    stats = collections.Counter()
    gap_hits = collections.Counter()
    callee_kind = collections.Counter()
    own_call_counts = collections.Counter()
    combos = collections.Counter()

    work = tempfile.mkdtemp(prefix="gapsurvey_")
    try:
        for i, path in enumerate(files, 1):
            for f in pathlib.Path(work).iterdir():
                f.unlink()
            main_xml, bd_xml = extract(args.bundle, path, work)
            if main_xml is None:
                stats["extract failed"] += 1
                continue
            stats["inspected"] += 1
            main_text = pathlib.Path(main_xml).read_text(encoding="utf-8", errors="replace")
            heap = (pathlib.Path(bd_xml).read_text(encoding="utf-8", errors="replace")
                    if bd_xml else "")

            classes = set(HEAP_CLASS.findall(heap))
            present = []
            for label, names in GAPS.items():
                if any(n in classes for n in names):
                    gap_hits[label] += 1
                    present.append(label)

            # subVI calls, split by where the callee lives
            own = 0
            for record in IUVI.findall(main_text):
                m = PATHREF.search(record)
                if not m:
                    callee_kind["no path record"] += 1
                    continue
                segs = FIRST_SEG.findall(m.group(2))
                first = segs[0] if segs else ""
                if first in SYMBOLIC_ROOTS:
                    callee_kind["LabVIEW installation (symbolic root)"] += 1
                elif m.group(1) == "1":
                    callee_kind["project's own code (relative path)"] += 1
                    own += 1
                else:
                    callee_kind["absolute or unrooted path"] += 1
                    own += 1
                q = QUALNAME.search(record)
                if q and ".lvclass" in q.group(1):
                    callee_kind["  of those, a class member"] += 1
                elif q and ".lvlib" in q.group(1):
                    callee_kind["  of those, a library member"] += 1
            own_call_counts[min(own, 10)] += 1
            if own:
                gap_hits["depends on the project's own VIs"] += 1
                present.append("own VIs")
            if not present:
                stats["no gap construct found"] += 1
            combos[tuple(sorted(present))] += 1

            if i % 100 == 0:
                print("  %4d/%d" % (i, len(files)), flush=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    n = max(stats["inspected"], 1)
    print()
    for k, v in sorted(stats.items()):
        print("  %-30s %d" % (k, v))

    print("\nPREVALENCE of each gap, over %d VIs" % n)
    for label, hits in gap_hits.most_common():
        print("  %-38s %5d  %5.1f %%" % (label, hits, 100.0 * hits / n))

    print("\nSUBVI CALLS by where the callee lives")
    for k, v in callee_kind.most_common():
        print("  %-38s %5d" % (k, v))

    print("\nCALLS INTO OWN CODE per VI (10 = ten or more)")
    for k in sorted(own_call_counts):
        print("  %-4s %5d" % (k, own_call_counts[k]))

    print("\nMOST COMMON GAP COMBINATIONS")
    for combo, v in combos.most_common(8):
        print("  %-58s %5d" % (", ".join(combo) or "(none)", v))


if __name__ == "__main__":
    main()
