#!/usr/bin/env python3
"""lvbd_fit_view.py - put the block diagram window's view over the diagram's code.

    python -I lvbd_fit_view.py <BDHb.xml>

pBounds is the region of the diagram the window shows when the VI opens, in diagram coordinates.
A VI whose code lies outside it opens on an EMPTY diagram - every composed VI did, with the
skeleton's view at (243, 336) (the user's screenshot, 2026-10-31). The view starts at 0/0, the
user's wish, while the code starts within the first quarter of the window, else where the code
starts; its size is kept. lvheap.fit_window_view has the rule.

Run it AFTER lvai_layout_diagram has saved the VI - the layout moves the code, LabVIEW's save
keeps the view as it was - on a heap extracted from a VI LabVIEW does not hold, then rebuild.
Prints "view fitted" or "view unchanged".
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "aixml-pylabview"))
import xml.etree.ElementTree as ET  # noqa: E402
import lvheap  # noqa: E402


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    path = argv[1]
    tree = ET.parse(path)
    if not lvheap.fit_window_view(tree.getroot()):
        print("view unchanged")
        return 0
    text = open(path, encoding="utf-8", newline="").read()
    old = tree.getroot().find("pBounds")
    # edit the one line, so nothing else of the heap is re-serialised; the heap's own pBounds
    # follows its root object, after anything nested
    start = text.rindex("<pBounds>")
    end = text.index("</pBounds>", start)
    text = text[:start] + "<pBounds>" + old.text + text[end:]
    open(path, "w", encoding="utf-8", newline="").write(text)
    print("view fitted %s" % old.text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
