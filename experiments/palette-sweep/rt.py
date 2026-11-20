# -*- coding: utf-8 -*-
"""Re-read NI examples with the CURRENT reader and compose them - a quick check of a fix.

    <bundle>\\python.exe rt.py <outdir> <name or substring of the example's file name> ...

Prints per VI: composed, or the first refusal; plus the composer's `not composed` notes.
"""
import glob
import io
import contextlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sweep  # noqa: E402


def find(names):
    return [p for p in sweep.example_paths() if any(n.lower() in os.path.basename(p).lower() for n in names)]


def main():
    outdir = os.path.abspath(sys.argv[1])
    os.makedirs(outdir, exist_ok=True)
    names = sys.argv[2:]
    if len(names) == 1 and names[0].startswith("@"):
        names = [l.strip() for l in open(names[0][1:], encoding="utf-8") if l.strip()]
    for vi in find(names):
        stem = os.path.splitext(os.path.basename(vi))[0]
        work = os.path.join(outdir, stem)
        os.makedirs(work, exist_ok=True)
        xml, why = sweep.fresh_read(vi, work)
        if xml is None:
            print("%s: READ %s" % (stem, why))
            continue
        out = os.path.join(outdir, stem + ".vi")
        so = io.StringIO()
        with contextlib.redirect_stdout(so):
            ok, msg = sweep.compose(xml, out, [os.path.dirname(vi)])
        notes = [l for l in so.getvalue().splitlines() if "not composed" in l]
        print("%s: %s" % (stem, "composed -> " + out if ok else msg[:300]))
        for n in notes[:4]:
            print("    " + n[:200])


if __name__ == "__main__":
    main()
