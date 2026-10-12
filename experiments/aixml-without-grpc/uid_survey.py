# -*- coding: utf-8 -*-
"""Which heap object carries the uid of each element of LabVIEW's own AIXML export? The reader
must give every element the same uid NI's export does - this tallies the convention per AIXML
element kind over the export cache.

    uid_survey.py [--max N]

Prints, per (AIXML tag, _name), the heap context found most often:
  heap tag[class] / dco class / parent tag[class]
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
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 150
    tally = collections.defaultdict(collections.Counter)
    n = 0
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize):
        meta = x[:-4] + ".json"
        if not os.path.exists(meta):
            continue
        vp = json.load(open(meta, encoding="utf-8")).get("ViPath")
        if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        try:
            ref = ET.parse(x).getroot()
            b = lvheap.Bundle(lvheap.extract(vp, tempfile.mkdtemp(prefix="uids_")))
        except Exception:
            continue
        if sum(1 for _ in ref.iter()) < 3:
            continue
        idx = collections.defaultdict(list)
        for heap, hname in ((b.bd, "BD"), (b.fp, "FP")):
            if heap is None:
                continue
            parent = {c: p for p in heap.iter() for c in p}
            for e in heap.iter():
                if e.get("uid") and (e.get("class") or len(e)):
                    p = parent.get(e)
                    pp = parent.get(p) if p is not None else None
                    d = e.find("dco")
                    idx[e.get("uid")].append("%s %s[%s] dco=%s in %s[%s]" % (
                        hname, e.tag, e.get("class"), d.get("class") if d is not None else "-",
                        p.tag if p is not None else "-", (pp.get("class") if pp is not None else "-")))
        for el in ref.iter():
            if not isinstance(el.tag, str) or el is ref or el.get("uid") is None:
                continue
            key = (el.tag, el.get("_name") or "")
            hits = idx.get(el.get("uid"))
            tally[key][hits[0] if hits else "NOT FOUND"] += 1
        n += 1
        if n >= limit:
            break
    for key in sorted(tally, key=lambda k: -sum(tally[k].values())):
        c = tally[key]
        print("%-45s %4d  %s" % ("%s %s" % key, sum(c.values()), "; ".join("%s (%d)" % kv for kv in c.most_common(3))))


if __name__ == "__main__":
    main(sys.argv[1:])
