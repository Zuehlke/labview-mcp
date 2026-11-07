# -*- coding: utf-8 -*-
"""Which VIs became identical, and which stopped being identical, between two compare_corpus.py
--out runs, and the fact buckets of the ones that stopped.  diff_runs.py <old.jsonl> <new.jsonl>"""
import collections
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_corpus import bucket  # noqa: E402


def load(p):
    d = {}
    for line in open(p, encoding="utf-8"):
        r = json.loads(line)
        if "missing" in r:
            d[r["vi"]] = r
    return d


def ident(r):
    return not any(r[s][k] for s in ("missing", "extra") for k in r[s])


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    a, b = load(argv[0]), load(argv[1])
    lost = [v for v in a if ident(a[v]) and v in b and not ident(b[v])]
    gained = [v for v in b if ident(b[v]) and v in a and not ident(a[v])]
    print("lost %d, gained %d" % (len(lost), len(gained)))
    c = collections.Counter()
    for v in lost:
        r = b[v]
        for s in ("missing", "extra"):
            for k in r[s]:
                for f in r[s][k]:
                    c[s[0] + " " + bucket(k, f, r["tags"])] += 1
    for k, n in c.most_common(15):
        print(n, k)
    for v in lost[:5]:
        print("  ", v[-80:])


if __name__ == "__main__":
    main(sys.argv[1:])
