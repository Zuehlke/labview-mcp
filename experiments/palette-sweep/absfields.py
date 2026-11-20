# -*- coding: utf-8 -*-
"""List the fields of a front-panel part that look like ABSOLUTE panel coordinates: a rectangle or
point that lies inside the part's own ddo bounds on the donor's panel, rather than in the ddo's
relative frame. Prints path, tag and value.

    python absfields.py <part.xml>
"""
import re
import sys
import xml.etree.ElementTree as ET

RECT = re.compile(r"^\((-?\d+), (-?\d+), (-?\d+), (-?\d+)\)$")
PAIR = re.compile(r"^\((-?\d+), (-?\d+)\)$")


def main(path):
    heap = ET.parse(path).getroot().find("heap")
    ddo = next(f.find("ddo") for f in heap.iter("SL__arrayElement") if f.get("class") == "fPDCO")
    T, L, B, R = map(int, RECT.match(ddo.findtext("bounds")).groups())
    print("ddo", (T, L, B, R))
    parent = {c: p for p in ddo.iter() for c in p}

    def path_of(e):
        out = []
        while e is not None and e is not ddo:
            out.append("%s[%s]" % (e.tag, e.get("class") or ""))
            e = parent.get(e)
        return "/".join(reversed(out))

    for e in ddo.iter():
        if e is ddo or e.tag == "bounds" and parent.get(e) is ddo:
            continue
        t = (e.text or "").strip()
        m = RECT.match(t)
        if m:
            a, b, c, d = map(int, m.groups())
            if T <= a <= B and L <= b <= R and a >= 40 and b >= 40:
                print("RECT ", path_of(e), t)
            continue
        m = PAIR.match(t)
        if m:
            a, b = map(int, m.groups())
            if (T <= a <= B and L <= b <= R) or (L <= a <= R and T <= b <= B):
                print("PAIR ", path_of(e), t)
            continue
        if re.fullmatch(r"-?\d+", t) and e.tag not in ("partID", "objFlags", "howGrow", "masterPart", "ImageResID",
                                                        "ImageInternalsResID", "partOrder", "mode", "typeCode"):
            v = int(t)
            if (T + 5 <= v <= B) or (L + 5 <= v <= R):
                print("INT  ", path_of(e), t)


if __name__ == "__main__":
    main(sys.argv[1])
