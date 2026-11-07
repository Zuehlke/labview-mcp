# -*- coding: utf-8 -*-
"""composed.txt for cov_load.py from a census rows.tsv: `<out>\\vis\\c_NNNN.vi <TAB> source`
for every row composed OK.  write_composed.py <census out dir>"""
import csv
import os
import sys

out = os.path.abspath(sys.argv[1])
with open(os.path.join(out, "rows.tsv"), encoding="utf-8") as fh, \
        open(os.path.join(out, "composed.txt"), "w", encoding="utf-8") as f:
    for r in csv.reader(fh, delimiter="\t"):
        if len(r) > 4 and r[1] == "OK":
            f.write("%s\t%s\n" % (os.path.join(out, "vis", "c_%04d.vi" % int(r[0])), os.path.normpath(r[4])))
