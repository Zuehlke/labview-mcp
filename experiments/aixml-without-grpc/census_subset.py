# -*- coding: utf-8 -*-
"""Re-run census_examples.py's read -> compose for a LIST of source VIs (one path per line), so a
family of composer defects can be re-measured after a fix without the whole 7-minute census.

    census_subset.py <list.txt> <out dir>

Writes <out>\\rows.tsv and <out>\\composed.txt (composed vi <TAB> source vi) for cov_load.py; the
composed VIs keep their census index as c_NNNN.vi when the list line is "<index><TAB><path>".
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import census_examples as ce  # noqa: E402


def main(argv):
    lst, out = argv[0], argv[1]
    ce.init()
    for d in ("aixml", "vis"):
        os.makedirs(os.path.join(out, d), exist_ok=True)
    rows = []
    for n, line in enumerate(open(lst, encoding="utf-8")):
        line = line.strip()
        if not line:
            continue
        idx, vi = (int(line.split("\t")[0]), line.split("\t")[1]) if "\t" in line else (n, line)
        rows.append(ce.one((idx, os.path.normpath(vi), out)))
    with open(os.path.join(out, "rows.tsv"), "w", encoding="utf-8") as f:
        for idx, st, stage, msg, vi, dt in rows:
            f.write("\t".join([str(idx), st, stage, msg.replace("\t", " "), vi, "%.2f" % dt]) + "\n")
    with open(os.path.join(out, "composed.txt"), "w", encoding="utf-8") as f:
        for idx, st, stage, msg, vi, dt in rows:
            if st == "OK":
                f.write(os.path.join(out, "vis", "c_%04d.vi" % idx) + "\t" + vi + "\n")
    from collections import Counter
    print(Counter(r[1] for r in rows))


if __name__ == "__main__":
    main(sys.argv[1:])
