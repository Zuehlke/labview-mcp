# -*- coding: utf-8 -*-
"""Decode a TWO-ENDED compressedWireTable into its joints, and test hotpoint models against the
corpus wire_corpus.py collected.

    wire_decode.py <wires.jsonl>

Format (FINDINGS section 4, extended here): [n joints] 08 [n-2 direction bytes] [n-2 lengths],
a length >= 255 written FF + big-endian 16 bit. Segments alternate, the first horizontal; a
direction byte belongs to segments 2..n-1 (01 = left/up, 00 = right/down); the first segment's
direction is implied, and so is the last segment's length - it ends on the sink.
"""
import collections
import json
import sys


def parse_table(hexs):
    b = bytes.fromhex(hexs)
    n = b[0]
    if n < 2 or b[1] != 0x08:
        return None
    dirs = list(b[2:2 + n - 2])
    rest = b[2 + n - 2:]
    lens = []
    i = 0
    while i < len(rest):
        if rest[i] == 0xFF:
            lens.append(int.from_bytes(rest[i + 1:i + 3], "big"))
            i += 3
        else:
            lens.append(rest[i])
            i += 1
    if len(lens) != n - 2:
        return None
    return n, dirs, lens


def bounds(s):
    return [int(v) for v in s.strip("()").split(",")]


# DCO classes whose output flag says nothing about the wire: a Feedback Node's input carries
# bit 0 like an output, so every wire into one read as having two sources
SINK_CLASSES = ("rightFeedback",)


def is_source(e):
    if e["kind"] == "const":
        return True
    if e["kind"] == "fPTerm":
        return not (e["tflags"] & 0x8000)          # an indicator's terminal is a sink
    if e.get("dclass") in SINK_CLASSES:
        return False
    return bool(e["dflags"] & 1)


def hot(e, model):
    """(y, x) of the end's hotpoint under a model."""
    t, l, b, r = bounds(e["bounds"])
    src = is_source(e)
    if e["kind"] == "node" and e["term"]:
        tt, tl, tb, tr = bounds(e["term"])
        t, l, b, r = t + tt, l + tl, t + tb, l + tr
    y = {"mid": (t + b) // 2, "midup": (t + b - 1) // 2}[model["y"]]
    if src:
        x = r + model["xout"]
    else:
        x = l + model["xin"]
    return y, x


def main(argv):
    wires = [json.loads(l) for l in open(argv[0], encoding="utf-8")]
    shapes = collections.Counter()
    for w in wires:
        p = parse_table(w["table"])
        shapes[(p[0] if p else None)] += 1
    print("joint counts:", sorted(shapes.items(), key=lambda kv: -kv[1])[:8])
    models = [{"y": y, "xout": xo, "xin": xi} for y in ("mid", "midup") for xo in (-1, 0) for xi in (0, -1, 1)]
    for m in models:
        ok_y = tot_y = 0
        ok_x = tot_x = 0
        bad = collections.Counter()
        for w in wires:
            p = parse_table(w["table"])
            if not p:
                continue
            n, dirs, lens = p
            a, bnd = w["ends"]
            if is_source(a) == is_source(bnd):
                continue
            s, k = (a, bnd) if is_source(a) else (bnd, a)
            (sy, sx), (ky, kx) = hot(s, m), hot(k, m)
            # walk: segment 1 horizontal right, then alternate; directions for 2..n-1
            vert = 0
            horiz = lens[0] if lens else None
            for i in range(1, n - 1):
                d = -1 if dirs[i - 1] else 1
                L = lens[i] if i < len(lens) else None
                if i % 2 == 1:      # vertical segment
                    if L is not None:
                        vert += d * L
                else:
                    if L is not None:
                        horiz += d * L
            # the last segment: implied - vertical when n-1 is even
            last_vertical = (n - 1) % 2 == 0
            if not last_vertical:
                tot_y += 1
                if ky - sy == vert:
                    ok_y += 1
                else:
                    bad[(s["owner"], k["owner"])] += 1
            else:
                tot_x += 1
                if kx - sx == horiz:
                    ok_x += 1
        print("model %-40s y %4d/%4d  x %3d/%3d  worst %s" % (m, ok_y, tot_y, ok_x, tot_x, bad.most_common(3)))


if __name__ == "__main__":
    main(sys.argv[1:])
