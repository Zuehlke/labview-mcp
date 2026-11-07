# -*- coding: utf-8 -*-
"""Drill into one gap bucket of a compare_corpus.py --out JSONL file.

    corpus_gap.py <cmp.jsonl> <kind> <regex on the bucket text> [--n 20] [--extra]

Prints example facts of that bucket (missing by default, --extra for the reader's surplus), with
the VI they come from, and a count per node name where the fact names one.
"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_corpus import bucket  # noqa: E402


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    path, kind, rx = argv[0], argv[1], re.compile(argv[2])
    n = int(argv[argv.index("--n") + 1]) if "--n" in argv else 20
    side = "extra" if "--extra" in argv else "missing"
    shown = 0
    per = collections.Counter()
    for line in open(path, encoding="utf-8"):
        r = json.loads(line)
        if side not in r:
            continue
        for f in r[side][kind]:
            b = bucket(kind, f, r["tags"])
            if not rx.search(b):
                continue
            per[f[2] if kind == "terms" else (f[2] if kind == "attrs" else b)] += 1
            if shown < n:
                shown += 1
                print("%s | %s" % (os.path.basename(r["vi"]), str(f)[:220]))
    print("--- most common")
    for k, c in per.most_common(30):
        print("  %5d  %s" % (c, str(k)[:120]))


if __name__ == "__main__":
    main(sys.argv[1:])
