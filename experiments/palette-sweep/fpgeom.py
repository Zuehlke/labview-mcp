# -*- coding: utf-8 -*-
"""Print the geometry of a front-panel object: the ddo's bounds and every part's partID, class and
bounds - from a VI (by label) or from a part file.

    <bundle>\\python.exe fpgeom.py vi <file.vi> <label>
    <bundle>\\python.exe fpgeom.py part <part.xml>
"""
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts", "aixml-pylabview"))
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402


def dump(ddo, depth=0):
    pad = "  " * depth
    print("%sddo %s bounds=%s" % (pad, ddo.get("class"), ddo.findtext("bounds")))
    for p in (ddo.find("partsList") if ddo.find("partsList") is not None else []):
        img = p.find("image")
        print("%s  part %-6s %-14s bounds=%-24s %s" % (pad, p.findtext("partID"), p.get("class"), p.findtext("bounds"),
                                                       ("img=" + (img.findtext("ImageResID") or "")) if img is not None else ""))


def main(argv):
    if argv[0] == "vi":
        b = lvheap.Bundle(lvheap.extract(argv[1], tempfile.mkdtemp()))
        for f in b.fp.iter("SL__arrayElement"):
            if f.get("class") == "fPDCO" and f.find("ddo") is not None and aixml2vi.vi_label(f.find("ddo")) == argv[2]:
                dump(f.find("ddo"))
    else:
        heap = ET.parse(argv[1]).getroot().find("heap")
        for f in heap.iter("SL__arrayElement"):
            if f.get("class") == "fPDCO" and f.find("ddo") is not None:
                dump(f.find("ddo"))
                break


if __name__ == "__main__":
    main(sys.argv[1:])
