# -*- coding: utf-8 -*-
"""Load VIs one at a time over COM and print how many DWarn events LabVIEW's log gained per VI,
with the messages - to find what fills the log (it stops being written at ~100 events).

    dwarn_delta.py <vi> [<vi> ...]
"""
import glob
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import selftest  # noqa: E402


def events():
    out = []
    for log in glob.glob(os.path.join(os.environ.get("TEMP", ""), "LabVIEW_*_cur.txt")):
        with open(log, encoding="utf-8", errors="replace") as f:
            for l in f:
                # NI writes each event twice; the second copy starts with source\...cpp(N)
                if "DWarn" in l and not l.startswith("source"):
                    out.append(re.sub(r"0x[0-9A-F]{8}", "", l.strip())[:150])
    return out


for vi in sys.argv[1:]:
    before = len(events())
    state, _, _ = selftest.lvcom(vi, "", [], run=False)
    ev = events()
    new = ev[before:]
    print("%-50s ExecState %s  +%d DWarn" % (os.path.basename(vi), state, len(new)))
    for l in sorted(set(new)):
        print("    %dx %s" % (new.count(l), l))
