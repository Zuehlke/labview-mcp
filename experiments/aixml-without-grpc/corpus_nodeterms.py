# -*- coding: utf-8 -*-
"""Missing Node terminals from a compare_corpus.py --out JSONL, counted per node NAME (read from
NI's export) and terminal, split into unwired (NI writes `name:`) and wired.

    corpus_nodeterms.py <cmp.jsonl> <corpus dir> [--n 40]"""
import collections
import json
import os
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(1, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts", "aixml-pylabview"))
import aixml2vi  # noqa: E402
from compare_corpus import export_of  # noqa: E402


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    n = int(argv[argv.index("--n") + 1]) if "--n" in argv else 40
    xml_dir = os.path.join(argv[1], "xml")
    per = collections.Counter()
    nodes_with = collections.Counter()
    for line in open(argv[0], encoding="utf-8"):
        r = json.loads(line)
        miss = [f for f in r.get("missing", {}).get("terms", []) if r["tags"].get(f[0]) == "Node"]
        if not miss:
            continue
        root = ET.parse(export_of(xml_dir, r["vi"])).getroot()
        by_uid = {e.get("uid"): e for e in root.iter() if e.get("uid")}
        seen = set()
        for uid, side, term in miss:
            el = by_uid.get(uid)
            if el is None:
                continue
            nets = dict((aixml2vi.unescape(t), net) for t, net in aixml2vi.pairs(el.get("inputs" if side == "in" else "outputs")))
            name = el.get("_name") or "?"
            per[(name, "wired" if nets.get(term) else "unwired")] += 1
            if (uid, name) not in seen:
                seen.add((uid, name))
                nodes_with[name] += 1
    print("terminals missing, per node name and wired/unwired:")
    for k, c in per.most_common(n):
        print("  %6d  %s  (%d nodes)" % (c, k, nodes_with[k[0]]))


if __name__ == "__main__":
    main(sys.argv[1:])
