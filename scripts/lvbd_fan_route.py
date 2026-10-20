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

A second pass takes every other two-ended wire LabVIEW's router drew the long way - a segment
west, or at least DETOUR px longer than the plain step between its ends - and gives it the plain
step where one exists: out, one vertical, in, past no object of its diagram, crossing other wires
if it must. The user's rule of 2026-10-19: rather a crossing than a wire running back. In DQMH's
Bank Main.vi the `wait for reply` wires ran down to the frame's bottom border and back up, the
passed-through refnums jumped over a node, and the Message Queue went round a Bundle By Name.

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
# a wire this much longer than the plain step between its ends went round something
DETOUR = 40
# how far a rerouted wire keeps off every object of its diagram
MARGIN = 3

E, S, W, N = (0, 1), (1, 0), (0, -1), (-1, 0)          # (dy, dx), as lvbd_backward_wires.py
START = {0x08: E, 0x02: W, 0x01: N, 0x04: S}


def _segments(hexs):
    """[(direction, length or None)] of a TWO-ended table, or None."""
    try:
        b = bytes.fromhex(hexs)
    except ValueError:
        return None
    if len(b) < 2 or b[1] not in START:
        return None
    n, d = b[0], START[b[1]]
    dirs = list(b[2:2 + n - 2])
    rest, lens, i = b[2 + n - 2:], [], 0
    while i < len(rest):
        if rest[i] == 0xFF:
            lens.append(int.from_bytes(rest[i + 1:i + 3], "big"))
            i += 3
        else:
            lens.append(rest[i])
            i += 1
    if len(lens) != n - 2:
        return None
    segs = []
    for k in range(n - 1):
        if k > 0 and k - 1 < len(dirs):
            if d in (E, W):
                d = S if dirs[k - 1] == 0 else N
            else:
                d = E if dirs[k - 1] == 0 else W
        segs.append((d, lens[k] if k < len(lens) else None))
    return segs


def _bounds_of(x):
    b = x.findtext("bounds")
    if not b and x.find("ddo") is not None:
        b = x.find("ddo").findtext("bounds")
    return wire_route._bounds(b) if b else None


def detours(root, geo, hot, skip):
    """{signal uid: table} for every two-ended wire drawn the long way that a plain step reaches."""
    parent = {c: p for p in root.iter() for c in p}
    tables = {}
    # by class: the top-level diagram is no SL__arrayElement in a composed heap
    for diag in root.iter():
        if diag.get("class") != "diag":
            continue
        sl = diag.find("signalList")
        if sl is None:
            continue
        # LabVIEW keeps a diagram's objects in zPlaneList, the composer in nodeList
        objects = []
        for lst in (diag.find("zPlaneList"), diag.find("nodeList")):
            for x in lst if lst is not None else []:
                b = _bounds_of(x) if x.get("class") not in (None, "signal") else None
                if b is not None and all(x is not o for o, _ in objects):
                    objects.append((x, b))
        for sig in sl:
            uid = sig.get("uid")
            tl = sig.find("termList")
            cw = sig.find("compressedWireTable")
            if uid in skip or tl is None or len(tl) != 2 or cw is None or not (cw.text or "").strip():
                continue
            segs = _segments(cw.text.strip())
            ends = [geo.rect(x.get("uid")) for x in tl]
            if not segs or None in ends:
                continue
            (sy, sx), (ky, kx) = hot.of(*ends[0]), hot.of(*ends[1])
            known = sum(L for _, L in segs if L is not None)
            west = any(d == W for d, _ in segs)
            if not west and known - (abs(kx - sx) + abs(ky - sy)) < DETOUR:
                continue
            if kx - sx < 2 * MARGIN + 6:
                continue
            # the ends' own objects - a wire leaves its source and enters its sink there - are
            # the ones holding an end's terminal
            def holds(b, r):
                return b[0] - 2 <= r[0] and b[1] - 2 <= r[1] and r[2] <= b[2] + 2 and r[3] <= b[3] + 2
            boxes = [b for x, b in objects if not any(holds(b, e[0]) for e in ends)]

            def blocked(x1, y1, x2, y2):
                lo_x, hi_x, lo_y, hi_y = min(x1, x2), max(x1, x2), min(y1, y2), max(y1, y2)
                return any(t - MARGIN < hi_y and lo_y < bb + MARGIN and l - MARGIN < hi_x and lo_x < r + MARGIN
                           for t, l, bb, r in boxes)
            if ky == sy:
                if not blocked(sx + 1, sy, kx - 1, sy):
                    tables[uid] = "0208"
                continue
            mid = (sx + kx) // 2
            best = None
            for vx in sorted(range(sx + MARGIN + 3, kx - MARGIN - 2), key=lambda v: abs(v - mid)):
                if not blocked(sx + 1, sy, vx, sy) and not blocked(vx, sy, vx, ky) and not blocked(vx, ky, kx - 1, ky):
                    best = vx
                    break
            dy = ky - sy
            if best is not None:
                tables[uid] = "0408%02X00%s%s" % (1 if dy < 0 else 0, wire_route._len(best - sx), wire_route._len(abs(dy)))
                continue
            # two verticals: out, to a height between, along it, to the sink's height, in - the
            # least vertical travel first
            lo, hi = min(sy, ky) - 80, max(sy, ky) + 80
            found = None
            for ym in sorted(range(lo, hi + 1, 2), key=lambda y: (abs(sy - y) + abs(y - ky), abs(y - (sy + ky) // 2))):
                if ym in (sy, ky):
                    continue
                v1 = next((v for v in range(sx + MARGIN + 3, kx - MARGIN - 6)
                           if not blocked(sx + 1, sy, v, sy) and not blocked(v, sy, v, ym)), None)
                if v1 is None:
                    continue
                v2 = next((v for v in range(kx - MARGIN - 2, v1 + 3, -1)
                           if not blocked(v1, ym, v, ym) and not blocked(v, ym, v, ky) and not blocked(v, ky, kx - 1, ky)), None)
                if v2 is None:
                    continue
                found = (v1, ym, v2)
                break
            if found is None:
                continue
            v1, ym, v2 = found
            # only where it is shorter than what LabVIEW drew: over a column of nodes LabVIEW's
            # own route is the short way (Apply ATM UI State, 2026-10-20)
            if (kx - sx) + abs(ym - sy) + abs(ky - ym) > known - DETOUR:
                continue
            tables[uid] = "0608%02X00%02X00%s%s%s%s" % (1 if ym < sy else 0, 1 if ky < ym else 0,
                                                       wire_route._len(v1 - sx), wire_route._len(abs(ym - sy)),
                                                       wire_route._len(v2 - v1), wire_route._len(abs(ky - ym)))
    return tables


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
    detoured = detours(root, geo, hot, set(tables))
    tables.update(detoured)
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
    print("detours %d" % len(detoured))
    for uid, table in tables.items():
        print("  signal %s -> %s" % (uid, table))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
