# -*- coding: utf-8 -*-
"""Compose one AIXML file, load it in LabVIEW over COM and print ExecState plus every line
LabVIEW's log wrote about it - the inner loop of finding out why a composed VI is broken.

    try_vi.py <in.xml> [--run "a=1;b=2" --outputs "x;y"] [--out DIR]

Each call writes a fresh file name (LabVIEW keeps a loaded VI in memory by name)."""
import glob
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import selftest  # noqa: E402


def log_lines(name):
    out = []
    for log in glob.glob(os.path.join(os.environ.get("TEMP", ""), "LabVIEW_*_cur.txt")):
        with open(log, encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
        for i, l in enumerate(lines):
            if name in l:
                out.extend(lines[max(0, i - 1):i + 3])
    return out


def main(argv):
    src = argv[0]
    out_dir = argv[argv.index("--out") + 1] if "--out" in argv else r"C:\temp\aixml-no-grpc\try"
    os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(src))[0] + "_" + time.strftime("%H%M%S")
    vi = os.path.join(out_dir, base + ".vi")
    code, log = selftest.compose(src, vi)
    print("\n".join(l for l in log.splitlines() if "Warning" not in l and "does not match" not in l))
    if code != 0:
        return code
    run = "--run" in argv
    inputs = argv[argv.index("--run") + 1] if run else ""
    outputs = argv[argv.index("--outputs") + 1].split(";") if "--outputs" in argv else []
    state, got, raw = selftest.lvcom(vi, "", [], run=False)
    print("ExecState", state)
    if run and state == "1":
        state, got, raw = selftest.lvcom(vi, inputs, outputs)
        for k in outputs:
            print("  %s = %s" % (k, got.get(k)))
    seen = set()
    for l in log_lines(os.path.basename(vi)):
        if l not in seen and not l.startswith("#"):
            seen.add(l)
            print("LOG", l[:300])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
