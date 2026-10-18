#!/usr/bin/env python3
"""lvbd_fan_route.py - give the wires of a CONSTANT FAN their own non-crossing geometry.

    python -I lvbd_fan_route.py <BDHb.xml> [<parts dir>]

A constant fan is two or more two-ended wires from constants into one node - the column
constants of an Index Array, an assert's Expected and Label. Constants stand 17 px and more
apart while the terminals they feed are 8 px apart, so every wire needs a vertical of its own,
and LabVIEW's Clean Up Wire lays each one out alone and may put them in an order that crosses
(Find Account's column 2 and column 3, 2026-10-18). This writes each wire's compressedWireTable
as east, vertical, east, the verticals staggered across the gap: where the fan rises the lower
constant turns farther right, where it falls the upper one does.

Run it AFTER lvai_layout_diagram has saved the VI - the layout's own Clean Up Wire replaces every
table - on a heap extracted from a VI LabVIEW does not hold, then rebuild. Hotpoints and the
table format are the composer's (wire_route.py, FINDINGS section 4). Edits table text only.
"""
import collections
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "aixml-pylabview"))
import xml.etree.ElementTree as ET  # noqa: E402
import wire_route  # noqa: E402

MIN_STEP = 4
# the first vertical stands this far right of the constants' hotpoints (7 px in from their edge):
# at the hotpoint itself column 1's wire turned up against its constant's border
CLEAR = 6


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    heap_path = argv[1]
    parts = argv[2] if len(argv) > 2 else os.path.join(HERE, "aixml-pylabview", "parts")
    root = ET.parse(heap_path).getroot()
    geo = wire_route.Geometry(root)
    hot = wire_route.Hotpoints(parts)
    fans = collections.defaultdict(list)          # sink owner uid -> [(signal uid, src hp, snk hp)]
    for s in root.iter("SL__arrayElement"):
        if s.get("class") != "signal" or s.find("termList") is None or len(s.find("termList")) != 2:
            continue
        ends = [x.get("uid") for x in s.find("termList")]
        rects = [geo.rect(u) for u in ends]
        if None in rects:
            continue
        # which end is the constant: its kind says const:
        kinds = [r[3] for r in rects]
        if not any(k.startswith("const:") for k in kinds):
            continue
        ci = 0 if kinds[0].startswith("const:") else 1
        si = 1 - ci
        sink_owner = geo.owner(geo.terms[ends[si]])
        if sink_owner is None:
            continue
        fans[sink_owner.get("uid")].append((s.get("uid"), hot.of(*rects[ci]), hot.of(*rects[si])))
    tables = {}
    for owner, wires in fans.items():
        if len(wires) < 2:
            continue
        for rising in (True, False):
            group = [w for w in wires if (w[2][0] < w[1][0]) == rising and w[2][0] != w[1][0]]
            if not group:
                continue
            # the lower constant turns farther right where the fan rises, the upper where it falls
            group.sort(key=lambda w: w[1][0], reverse=not rising)
            left = max(w[1][1] for w in group) + CLEAR
            right = min(w[2][1] for w in group)
            step = (right - left) // (len(group) + 1)
            if step < MIN_STEP:
                continue
            for k, (uid, src, snk) in enumerate(group):
                xj = left + step * (k + 1)
                first = xj - src[1]
                dy = snk[0] - src[0]
                tables[uid] = "0408%02X00%s%s" % (1 if dy < 0 else 0, wire_route._len(first), wire_route._len(abs(dy)))
        for uid, src, snk in wires:
            if snk[0] == src[0] and snk[1] > src[1]:
                tables[uid] = "0208"
    if not tables:
        print("fans %d, wires rewritten 0" % sum(1 for w in fans.values() if len(w) >= 2))
        return 0
    text = open(heap_path, encoding="utf-8", newline="").read()
    for uid, table in tables.items():
        text, n = re.subn(r'(<SL__arrayElement class="signal" uid="%s">.*?<compressedWireTable>)[0-9A-Fa-f]*(</compressedWireTable>)' % uid,
                          lambda m: m.group(1) + table + m.group(2), text, count=1, flags=re.S)
        if n != 1:
            raise SystemExit("signal %s: table not found" % uid)
    open(heap_path, "w", encoding="utf-8", newline="").write(text)
    print("fans %d, wires rewritten %d" % (sum(1 for w in fans.values() if len(w) >= 2), len(tables)))
    for uid, table in tables.items():
        print("  signal %s -> %s" % (uid, table))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
