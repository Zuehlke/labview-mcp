# -*- coding: utf-8 -*-
"""Collect EventSpec rows from NI's VIs with event structures, beside the AIXML selector of the
frame they register - to learn how each event kind is encoded.

    event_specs.py [--max N]
"""
import collections
import glob
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def main(argv):
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 30
    rows = collections.Counter()
    n = 0
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize):
        try:
            root = ET.parse(x).getroot()
        except ET.ParseError:
            continue
        evs = [e for e in root.iter("Structure") if e.get("_name") == "Event Structure"]
        if not evs:
            continue
        meta = x[:-4] + ".json"
        vp = json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else None
        if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        try:
            b = lvheap.Bundle(lvheap.extract(vp, tempfile.mkdtemp(prefix="harvest_")))
        except Exception:
            continue
        for ev in evs:
            heap = next((e for e in b.bd.iter("SL__arrayElement") if e.get("uid") == ev.get("uid")
                         and e.get("class") == "eventStruct"), None)
            if heap is None:
                continue
            frames = [f for f in ev if f.tag == "CaseFrame"]
            for spec in heap.find("EventNodeEvents"):
                di = int(spec.findtext("diagramIdx") or 0)
                sel = frames[di].get("selector") if di < len(frames) else "?"
                kind = sel.split("\\3A")[-1].strip() if "\\3A" in sel else sel.strip()
                rows[(kind, spec.findtext("source"), spec.findtext("regFlags"), spec.findtext("eSource"),
                      spec.findtext("type"), spec.findtext("eFlags"), "ddo" if spec.findtext("ddoUID") != "0" else "0")] += 1
        n += 1
        if n >= limit:
            break
    for k, c in rows.most_common():
        print(c, k)


if __name__ == "__main__":
    main(sys.argv[1:])
