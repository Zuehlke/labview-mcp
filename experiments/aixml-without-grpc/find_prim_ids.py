# -*- coding: utf-8 -*-
"""Find the primResID of named diagram nodes by joining NI's cached AIXML exports with the
pylabview extraction of the same VI on uid - for names primitive-names.tsv lacks.

    find_prim_ids.py "Greater?" "Equal To 0?" ... [--max-vis N]
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
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402


def main(argv):
    limit = int(argv[argv.index("--max-vis") + 1]) if "--max-vis" in argv else 40
    want = [a for a in argv if not a.startswith("--") and not a.isdigit()]
    found = collections.defaultdict(collections.Counter)
    parm = collections.defaultdict(dict)
    files = sorted(glob.glob(os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml", "*.xml")),
                   key=os.path.getsize)
    done = 0
    for f in files:
        try:
            r = ET.parse(f).getroot()
        except ET.ParseError:
            continue
        nodes = {e.get("uid"): e for e in r.iter("Node") if aixml2vi.unescape(e.get("_name") or "") in want}
        if not nodes:
            continue
        meta = f[:-4] + ".json"
        vp = json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else None
        if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        try:
            b = lvheap.Bundle(lvheap.extract(vp, tempfile.mkdtemp(prefix="harvest_")))
        except Exception:
            continue
        for e in b.bd.iter("SL__arrayElement"):
            if e.get("class") == "prim" and e.get("uid") in nodes:
                n = aixml2vi.unescape(nodes[e.get("uid")].get("_name"))
                found[n][e.findtext("primResID")] += 1
        done += 1
        if done >= limit or all(w in found for w in want):
            break
    for w in want:
        print("%-28s %s" % (w, dict(found.get(w, {}))))


if __name__ == "__main__":
    main(sys.argv[1:])
