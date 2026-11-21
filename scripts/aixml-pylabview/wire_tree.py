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

A branch never runs straight back along the segment that reached its branch point: a direction
that is the exact reverse of the heading the walk arrives with is taken NORTH off a horizontal
segment and WEST off a vertical one (`leaving`). Measured 2026-11-21 over NI's examples and a
quarter of vi.lib: the two readings place the ends differently on 10 fan-outs of about 6 500, 9
fit only with the turn, none only without. It is how a tree carries a branch west.
"""

E, S, W, N = (0, 1), (1, 0), (0, -1), (-1, 0)      # (dy, dx)
BRANCH = {0x04: [N, S, E], 0x05: [S, E], 0x06: [N, E], 0x07: [N, S]}
CODE_OF = {tuple(v): k for k, v in BRANCH.items()}
REVERSE = {E: W, W: E, N: S, S: N}


def leaving(code, heading):
    """The directions leaving a branch point with this code, in walk order, for a walk that
    arrives with `heading`: the code's absolute directions, the one that would double back turned
    north (horizontal arrival) or west (vertical arrival)."""
    turned = N if heading in (E, W) else W
    return [turned if d == REVERSE[heading] else d for d in BRANCH[code]]
# the direction of the FIRST segment: east almost always; a Feedback Node's output leaves west
# (02, as in a two-ended table) or north (01)
START = {0x08: E, 0x02: W, 0x01: N, 0x04: S}


def starting(mask):
    """The directions leaving the SOURCE, in walk order, or None. The start byte is a bit mask
    (01 north, 02 west, 04 south, 08 east); with several bits the source is itself a branch point,
    and the walk takes its directions in rising bit order. Measured 2026-11-21: 163 fan-outs of
    LabVIEW's own saves start with 03, 05, 06, 09, 0A, 0C or 0D, and that reading puts every end of
    the 93 of them whose ends are all exact where the hotpoints are - the only order that does."""
    dirs = [START[bit] for bit in (0x01, 0x02, 0x04, 0x08) if mask & bit]
    return dirs if dirs and not mask & ~0x0F else None


def parse(hexs):
    """-> (n joints, codes, lengths, start mask, marked joints) or None.

    [n] [k] [k marks] [start mask] [n-2 codes] [n-1 lengths]: k is 0 in the common table. With
    k = 1 the mark is the index of a joint (0 = the first after the source) where a terminal sits
    ON the wire - a sink it passes on a straight run (code 02, straight on) or at a bend - besides
    the ends the walk reaches. Measured 2026-11-21: 223 of the 223 such tables of LabVIEW's own saves
    whose ends are all exact read that way; k > 1 (11 of them) does not, and is refused."""
    b = bytes.fromhex(hexs)
    if len(b) < 3:
        return None
    n, k = b[0], b[1]
    if n < 3 or k > 1 or len(b) < 3 + k or starting(b[2 + k]) is None:
        return None
    marks = list(b[2:2 + k])
    o = 3 + k
    codes = list(b[o:o + n - 2])
    rest = b[o + n - 2:]
    lens, i = [], 0
    while i < len(rest):
        if rest[i] == 0xFF:
            lens.append(int.from_bytes(rest[i + 1:i + 3], "big"))
            i += 3
        else:
            lens.append(rest[i])
            i += 1
    return n, codes, lens if len(lens) == n - 1 else None, b[2 + k], marks


def walk(hexs):
    """-> the terminal ends' offsets (dy, dx) from the source hotpoint, in walk order, or None."""
    p = parse(hexs)
    if not p or p[2] is None:
        return None
    n, codes, lens, mask, marks = p
    first = starting(mask)
    pos, d = (0, 0), first[0]
    stack = [((0, 0), first[1:])] if len(first) > 1 else []   # (position, remaining directions) of branch points
    ends = []
    li = 0
    for joint, code in enumerate(codes + [None]):
        # the segment into this joint
        if li >= len(lens):
            return None
        L = lens[li]
        li += 1
        pos = (pos[0] + d[0] * L, pos[1] + d[1] * L)
        if code is not None and joint in marks:
            ends.append(pos)                # a terminal the wire passes through
        if code == 0x02:
            continue                        # straight on
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
            dirs = leaving(code, d)
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


# a sink that shares its row with a NEARER sink is reached over a detour track this far out
# (above for rows above or at the source, below for rows below), and enters from JOG_IN px west
JOG, JOG_IN = 12, 8


def encode(sinks, junction_x):
    """A comb from the source hotpoint (0, 0): east to junction_x, a vertical spine through every
    sink's row, and from the spine east into each sink - (dy, dx) offsets, each sink right of the
    spine. Two sinks on ONE row: the nearer is reached straight, every farther one over a detour
    track JOG px further out, east to JOG_IN px before it, back to its row and in.
    Written in walk order: up, then down, then straight on; along a spine the walk goes on to the
    farthest track first and takes each track's arm on its way back.
    -> the table's hex, or None where that shape does not fit (a sink left of the spine)."""
    if junction_x <= 0 or any(dx <= junction_x for _, dx in sinks):
        return None
    rows = {}
    for dy, dx in sinks:
        rows.setdefault(dy, []).append(dx)
    used = set(rows)
    tracks = {}                                 # track y -> (sink y, sink x)
    for y in sorted(rows):
        xs = sorted(rows[y])
        tracks[y] = (y, xs[0])
        step = -JOG if y <= 0 else JOG
        t = y
        for prev, x in zip(xs, xs[1:]):
            if x - JOG_IN <= max(prev, junction_x):
                return None                     # the jog would come down on the nearer sink
            t += step
            while t in used:
                t += step
            used.add(t)
            tracks[t] = (y, x)
    ys = sorted(tracks)
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

    def arm(t):
        """east from the spine at track t into its sink, over the detour where t is one"""
        ky, kx = tracks[t]
        if ky == t:
            joints.append((kx - junction_x, 0x03))
        else:
            joints.append((kx - JOG_IN - junction_x, 0x00 if ky > t else 0x01))   # turn to the row
            joints.append((abs(ky - t), 0x00))                                   # turn east
            joints.append((JOG_IN, 0x03))

    def spine(levels, direction, prev):
        y = levels[0]
        if len(levels) == 1:
            joints.append((abs(y - prev), 0x00))                  # bend east
        else:
            joints.append((abs(y - prev), 0x05 if direction == S else 0x06))   # on + east
            spine(levels[1:], direction, y)
        arm(y)                                                  # taken on the way back

    if up:
        spine(up, N, 0)
    if down:
        spine(down, S, 0)
    if at0:
        arm(0)
    joints[-1] = (joints[-1][0], None)          # the last terminal's end is implied
    lens = [L for L, _ in joints]
    codes = [c for _, c in joints[:-1]]
    if any(v <= 0 for v in lens):
        return None

    def ln(v):
        return "%02X" % v if v < 255 else "FF%04X" % v
    return "%02X0008%s%s" % (len(codes) + 2, "".join("%02X" % c for c in codes), "".join(ln(v) for v in lens))
