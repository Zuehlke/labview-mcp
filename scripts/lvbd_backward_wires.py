#!/usr/bin/env python3
"""lvbd_backward_wires.py - count the block-diagram wire segments that run BACKWARDS (west).

    python -I lvbd_backward_wires.py [--min N] <file.vi | folder> ...

Reads each VI's block-diagram heap through pylabview (no LabVIEW) and decodes every wire's
compressedWireTable - the two-ended format and the fan-out tree, both decoded in
experiments/aixml-without-grpc/FINDINGS.md section 4 - into segments. A HORIZONTAL segment
heading west at least --min px long (default 8) counts as backward; a wire with one is a
backward wire. Wires inside an XNode are left out - its diagram is generated and never
shown. The table is what LabVIEW last SAVED, so measure after the layout's save.

Prints one line per VI and a total; a table it cannot decode is counted under `undecoded`.
--detours N lists every two-ended wire whose drawn length exceeds the plain step between its
ends (|dx| + |dy| of their rectangles' centres) by N px or more - a wire taken round something,
west or not, such as one run along a frame's bottom border.
--list adds one line per backward wire: its ends' kinds and rectangles (top, left, bottom,
right) in their diagram, as the composer's wire_route.Geometry reads them.
Python 3 standard library plus the bundled pylabview's readRSRC.py.
"""
import glob
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

E, S, W, N = (0, 1), (1, 0), (0, -1), (-1, 0)          # (dy, dx)
START = {0x08: E, 0x02: W, 0x01: N, 0x04: S}
# heap classes whose inner diagram LabVIEW never draws
HIDDEN = {"xNode"}
BRANCH = {0x04: [N, S, E], 0x05: [S, E], 0x06: [N, E], 0x07: [N, S]}


def lengths(rest):
    out, i = [], 0
    while i < len(rest):
        if rest[i] == 0xFF:
            out.append(int.from_bytes(rest[i + 1:i + 3], "big"))
            i += 3
        else:
            out.append(rest[i])
            i += 1
    return out


def turn(d, code):
    if d in (E, W):
        return S if code == 0 else N
    return E if code == 0 else W


def segments(hexs):
    """-> [(direction, length or None)] or None when the table is not understood."""
    b = bytes.fromhex(hexs)
    if len(b) < 2:
        return None
    n = b[0]
    if b[1] in START:                                   # two-ended
        d = START[b[1]]
        dirs = list(b[2:2 + n - 2])
        lens = lengths(b[2 + n - 2:])
        if len(lens) != n - 2:
            return None
        segs = []
        for k in range(n - 1):                          # n joints, n-1 segments
            if k > 0:
                d = turn(d, dirs[k - 1]) if k - 1 < len(dirs) else d
            segs.append((d, lens[k] if k < len(lens) else None))
        return segs
    if b[1] == 0x00 and len(b) > 2 and b[2] in START:  # fan-out tree
        codes = list(b[3:3 + n - 2])
        lens = lengths(b[3 + n - 2:])
        if len(lens) != n - 1:
            return None
        d, stack, segs, li = START[b[2]], [], [], 0
        for code in codes + [None]:
            segs.append((d, lens[li]))
            li += 1
            if code is None:
                break
            if code == 0x03:
                while stack and not stack[-1]:
                    stack.pop()
                if not stack:
                    return None
                d = stack[-1].pop(0)
            elif code in BRANCH:
                dirs = list(BRANCH[code])
                d = dirs.pop(0)
                stack.append(dirs)
            elif code in (0x00, 0x01):
                d = turn(d, code)
            else:
                return None
        return segs
    return None


def readrsrc():
    here = os.path.dirname(os.path.abspath(__file__))
    for base in (here, os.path.dirname(here)):
        for p in glob.glob(os.path.join(base, "**", "pylabview", "app", "pylabview", "readRSRC.py"), recursive=True):
            return p
    raise SystemExit("readRSRC.py of the bundled pylabview not found")


def describe(root, signal):
    here = os.path.dirname(os.path.abspath(__file__))
    sys.path.insert(0, os.path.join(here, "aixml-pylabview"))
    import wire_route
    geo = wire_route.Geometry(root)
    out = []
    tl = signal.find("termList")
    for x in tl if tl is not None else []:
        r = geo.rect(x.get("uid"))
        out.append("?" if r is None else "%s %s" % (r[3], r[0]))
    return " | ".join(out)


def centre(r):
    t, l, b, rr = r
    return ((t + b) / 2.0, (l + rr) / 2.0)


def detours(root, tables, signal_of, extra):
    here = os.path.dirname(os.path.abspath(__file__))
    sys.path.insert(0, os.path.join(here, "aixml-pylabview"))
    import wire_route
    geo = wire_route.Geometry(root)
    found = 0
    for i, t in enumerate(tables):
        sig = signal_of[i]
        tl = sig.find("termList") if sig is not None else None
        if tl is None or len(tl) != 2:
            continue
        rects = [geo.rect(x.get("uid")) for x in tl]
        if None in rects:
            continue
        segs = segments(t)
        if not segs:
            continue
        known = sum(L for _, L in segs if L is not None)
        (y1, x1), (y2, x2) = centre(rects[0][0]), centre(rects[1][0])
        plain = abs(x2 - x1) + abs(y2 - y1)
        # the last segment's length is implied: the drawn length is at least what the table spells
        if known - plain >= extra:
            found += 1
            print("    detour +%d px %s: %s" % (known - plain, t, describe(root, sig)))
    return found


def measure(vi, rd, minimum, listing=False, extra=None):
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([sys.executable, "-I", rd, "-x", "-i", vi, "-m", os.path.join(tmp, "x.xml")],
                       capture_output=True, check=False)
        heaps = glob.glob(os.path.join(tmp, "*_BDHb.xml"))
        if not heaps:
            return None
        root = ET.parse(heaps[0]).getroot()
    # a wire inside an XNode belongs to its generated diagram, which LabVIEW never shows: the
    # Error Ring of every DQMH --error.vi carried two backward wires nobody can see
    parent = {c: p for p in root.iter() for c in p}
    tables = []
    signal_of = {}
    for e in root.iter("compressedWireTable"):
        p, hidden = parent.get(e), False
        while p is not None:
            if p.get("class") in HIDDEN:
                hidden = True
                break
            p = parent.get(p)
        if not hidden and e.text:
            tables.append(e.text.strip())
            signal_of[len(tables) - 1] = parent.get(e)
    if extra is not None:
        detours(root, tables, signal_of, extra)
    wires = back_wires = back_segs = undecoded = 0
    for i, t in enumerate(tables):
        wires += 1
        segs = segments(t)
        if segs is None:
            undecoded += 1
            continue
        west = [L for d, L in segs if d == W and (L is None or L >= minimum)]
        back_segs += len(west)
        back_wires += 1 if west else 0
        if west and listing:
            print("    back %s: %s" % (t, describe(root, signal_of[i])))
    return wires, back_wires, back_segs, undecoded


def main(argv):
    minimum = 8
    extra = None
    if "--detours" in argv:
        i = argv.index("--detours")
        extra = int(argv[i + 1])
        del argv[i:i + 2]
    listing = "--list" in argv
    if listing:
        argv.remove("--list")
    if "--min" in argv:
        i = argv.index("--min")
        minimum = int(argv[i + 1])
        del argv[i:i + 2]
    files = []
    for a in argv[1:]:
        files += sorted(glob.glob(os.path.join(a, "**", "*.vi"), recursive=True)) if os.path.isdir(a) else [a]
    if not files:
        print(__doc__)
        return 2
    rd = readrsrc()
    tot = [0, 0, 0, 0]
    for vi in files:
        r = measure(vi, rd, minimum, listing, extra)
        if r is None:
            print("  ?  %s (no diagram heap)" % vi)
            continue
        tot = [a + b for a, b in zip(tot, r)]
        if r[1] or r[3]:
            print("%4d backward wires (%d segments) of %4d, undecoded %d  %s" % (r[1], r[2], r[0], r[3], vi))
    print("TOTAL: %d backward wires (%d segments) of %d wires in %d VIs, undecoded %d"
          % (tot[1], tot[2], tot[0], len(files), tot[3]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
