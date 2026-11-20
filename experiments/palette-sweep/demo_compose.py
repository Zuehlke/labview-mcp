# -*- coding: utf-8 -*-
"""Compose the demo's exported AIXML (lvai_convert_vis_to_aixml output) into its group folders with
the composer the server runs, each with its original's folder to resolve callees in.

    <bundle>\\python.exe demo_compose.py <demo dir> [name filter ...]
"""
import contextlib
import glob
import io
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sweep  # noqa: E402


def main(demo, filters):
    src = {}
    for line in open(os.path.join(demo, "sources.tsv"), encoding="utf-8"):
        group, path = line.rstrip("\n").split("\t")
        src[os.path.splitext(os.path.basename(path))[0]] = (group, path)
    for x in sorted(glob.glob(os.path.join(demo, "aixml", "*.xml"))):
        stem = os.path.basename(x).rsplit(".", 2)[0]
        if filters and not any(f.lower() in stem.lower() for f in filters):
            continue
        group, original = src[stem]
        out = os.path.join(demo, group, stem + ".vi")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        so = io.StringIO()
        with contextlib.redirect_stdout(so):
            ok, msg = sweep.compose(x, out, [os.path.dirname(original)])
        notes = [l for l in so.getvalue().splitlines() if "XNode" in l or "not composed" in l]
        print("%-40s %s" % (stem, "OK " + out if ok else "REFUSED " + msg[:250]))
        for n in notes[:3]:
            print("    " + n[:160])


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
