# -*- coding: utf-8 -*-
"""The FAN-OUT wire table: a tree, walked depth first from the source.

    [n joints] 00 08 [n-2 codes] [n-1 lengths]

decoded with fanout_calibrate.py on LabVIEW's own saves (2026-10-11): `08` starts east from the
source's hotpoint; each later joint but the last carries a code - a BEND gives the direction of
its next segment, perpendicular to the last (00 right/down, 01 left/up, as the two-ended format's
direction bytes), a BRANCH POINT the absolute directions leaving it (04 up+down+right, 05
down+right, 06 up+right, 07 up+down), explored in the order up, down, right; 03 ends a branch at a
terminal and the walk returns to the last branch point with a direction left. The lengths are the
segments in walk order, >= 255 written FF + 16 bit; the last joint is the last terminal's.
"""

E, S, W, N = (0, 1), (1, 0), (0, -1), (-1, 0)      # (dy, dx)
BRANCH = {0x04: [N, S, E], 0x05: [S, E], 0x06: [N, E], 0x07: [N, S]}
CODE_OF = {tuple(v): k for k, v in BRANCH.items()}
# the direction of the FIRST segment: east almost always; a Feedback Node's output leaves west
# (02, as in a two-ended table) or north (01)
START = {0x08: E, 0x02: W, 0x01: N, 0x04: S}


def parse(hexs):
    b = bytes.fromhex(hexs)
    n = b[0]
    if n < 3 or b[1] != 0x00 or b[2] not in START:
        return None
    codes = list(b[3:3 + n - 2])
    rest = b[3 + n - 2:]
    lens, i = [], 0
    while i < len(rest):
        if rest[i] == 0xFF:
            lens.append(int.from_bytes(rest[i + 1:i + 3], "big"))
            i += 3
        else:
            lens.append(rest[i])
            i += 1
    return n, codes, lens if len(lens) == n - 1 else None


def walk(hexs):
    """-> the terminal ends' offsets (dy, dx) from the source hotpoint, in walk order, or None."""
    p = parse(hexs)
    if not p or p[2] is None:
        return None
    n, codes, lens = p
    pos, d = (0, 0), START[bytes.fromhex(hexs)[2]]
    stack = []          # (position, remaining directions) of branch points
    ends = []
    li = 0
    for code in codes + [None]:
        # the segment into this joint
        if li >= len(lens):
            return None
        L = lens[li]
        li += 1
        pos = (pos[0] + d[0] * L, pos[1] + d[1] * L)
        if code is None or code == 0x03:
            ends.append(pos)
            if code is None:
                break
            while stack and not stack[-1][1]:
                stack.pop()
            if not stack:
                return None
            pos, dirs = stack[-1]
            d = dirs.pop(0)
        elif code in BRANCH:
            dirs = list(BRANCH[code])
            d = dirs.pop(0)
            stack.append((pos, dirs))
        elif code in (0x00, 0x01):
            if d in (E, W):
                d = S if code == 0 else N
            else:
                d = E if code == 0 else W
        else:
            return None
    return ends if li == len(lens) else None


def encode(sinks, junction_x):
    """A comb from the source hotpoint (0, 0): east to junction_x, a vertical spine through every
    sink's y, and from the spine east into each sink - (dy, dx) offsets, each sink right of the
    spine. Written in walk order: up, then down, then straight on; along a spine the walk goes on
    to the farthest level first and takes each level's arm on its way back.
    -> the table's hex, or None where that shape does not fit (a sink left of the spine, two sinks
    on one row)."""
    if junction_x <= 0 or any(dx <= junction_x for _, dx in sinks):
        return None
    ys = sorted({dy for dy, _ in sinks})
    if len(ys) != len(sinks):
        return None        # two sinks on one row: they need a detour, not composed
    by_y = {dy: dx for dy, dx in sinks}
    up = [y for y in ys if y < 0][::-1]       # nearest first
    down = [y for y in ys if y > 0]
    at0 = 0 in ys
    dirs = ([N] if up else []) + ([S] if down else []) + ([E] if at0 else [])
    if dirs == [E]:
        return None        # a single sink straight on is a two-ended wire
    if len(dirs) == 1:
        trunk = 0x01 if dirs[0] == N else 0x00     # a bend, not a branch point
    else:
        trunk = CODE_OF[tuple(dirs)]
    joints = [(junction_x, trunk)]             # (length of the segment into it, its code)

    def spine(levels, direction, prev):
        y = levels[0]
        if len(levels) == 1:
            joints.append((abs(y - prev), 0x00))                  # bend east
        else:
            joints.append((abs(y - prev), 0x05 if direction == S else 0x06))   # on + east
            spine(levels[1:], direction, y)
        joints.append((by_y[y] - junction_x, 0x03))           # the arm, taken on the way back

    if up:
        spine(up, N, 0)
    if down:
        spine(down, S, 0)
    if at0:
        joints.append((by_y[0] - junction_x, 0x03))
    joints[-1] = (joints[-1][0], None)          # the last terminal's end is implied
    lens = [L for L, _ in joints]
    codes = [c for _, c in joints[:-1]]
    if any(v <= 0 for v in lens):
        return None

    def ln(v):
        return "%02X" % v if v < 255 else "FF%04X" % v
    return "%02X0008%s%s" % (len(codes) + 2, "".join("%02X" % c for c in codes), "".join(ln(v) for v in lens))
