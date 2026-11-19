# -*- coding: utf-8 -*-
"""The colours a VI's or control's FRONT PANEL objects are drawn in, read from the file with no
LabVIEW - the LOOK check of lvai_replace_controls and of lvai_generate_vi's `controlLook` step.

    panel_colours.py <file.vi|file.ctl> [...]

Prints one JSON object: {path: [colour, ...]} - every fgColor and bgColor of the front-panel
heap as pylabview writes it (8 hex digits), sorted; {path: "error ..."} where a file cannot be
read. A Replace with a CUSTOM control (Fuse Design System's NXG_controls, which are not typedefs)
leaves no typedef link to check, so the evidence that it took is the look itself: the colours
the .ctl carries and the VI did not, present in the saved VI afterwards (measured 2026-11-19 on
Numeric Control, LED, Error In and String Control NI_NXG.ctl: five NXG-only colours appeared).
"""
import glob
import json
import os
import re
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # the bundle's ._pth leaves it off
import lvheap  # noqa: E402

COLOUR = re.compile(r"<(?:fg|bg)Color>([0-9A-Fa-f]{8})<")


def colours(path):
    work = tempfile.mkdtemp(prefix="colours_")
    try:
        lvheap.extract(path, work, timeout=60)
        heaps = glob.glob(os.path.join(work, "*_FPHb.xml"))
        if not heaps:
            return []
        with open(heaps[0], encoding="utf-8", errors="replace") as f:
            return sorted({c.upper() for c in COLOUR.findall(f.read())})
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main(paths):
    out = {}
    for p in paths:
        try:
            out[p] = colours(p)
        except Exception as ex:  # noqa: BLE001 - reported per file, the others still answer
            out[p] = "error " + str(ex).splitlines()[0][:300]
    print(json.dumps(out))


if __name__ == "__main__":
    main(sys.argv[1:])
