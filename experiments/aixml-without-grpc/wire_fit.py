# -*- coding: utf-8 -*-
"""Fit hotpoint models per end KIND against the decoded tables of LabVIEW-saved wires.

    wire_fit.py <wires.jsonl>

Constraints a table gives (wire_decode.parse_table): with the last segment horizontal (n even) the
vertical offset sink - source is the signed sum of the vertical segments; with it vertical (n odd)
the horizontal offset is the signed sum of the horizontal ones. Each end kind (owner class, DCO
class, in/out) gets its own candidate rule, scored by the wires both of whose ends it explains.
"""
import collections
import json
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
import wire_decode as wd  # noqa: E402


def rect(e):
    t, l, b, r = wd.bounds(e["bounds"])
    if e["kind"] == "node" and e["term"]:
        tt, tl, tb, tr = wd.bounds(e["term"])
        return t + tt, l + tl, t + tb, l + tr
    return t, l, b, r


def kind(e):
    return "%s/%s/%s" % (e["owner"], e.get("dclass") or "-", "out" if wd.is_source(e) else "in")


Y_RULES = {"mid": lambda t, b: (t + b) // 2, "midup": lambda t, b: (t + b - 1) // 2,
           "top": lambda t, b: t, "bot": lambda t, b: b - 1}
X_RULES = {"l": lambda l, r: l, "r": lambda l, r: r, "r-1": lambda l, r: r - 1, "mid": lambda l, r: (l + r) // 2,
           "l+1": lambda l, r: l + 1}


def offsets(p):
    n, dirs, lens = p
    vert, horiz = 0, (lens[0] if lens else 0)
    for i in range(1, n - 1):
        d = -1 if dirs[i - 1] else 1
        L = lens[i] if i < len(lens) else None
        if L is None:
            continue
        if i % 2 == 1:
            vert += d * L
        else:
            horiz += d * L
    return vert, horiz, (n - 1) % 2 == 0      # last segment vertical?


def main(argv):
    wires = []
    for l in open(argv[0], encoding="utf-8"):
        w = json.loads(l)
        p = wd.parse_table(w["table"])
        a, b = w["ends"]
        if not p or wd.is_source(a) == wd.is_source(b):
            continue
        s, k = (a, b) if wd.is_source(a) else (b, a)
        wires.append((s, k, offsets(p), w))
    # y: per kind, which rule makes the most wires consistent (holding the partner at "mid")
    for axis, rules, idx in (("y", Y_RULES, 0), ("x", X_RULES, 1)):
        score = collections.defaultdict(collections.Counter)
        total = collections.Counter()
        for s, k, (vert, horiz, last_v), w in wires:
            if axis == "y" and last_v:
                continue
            if axis == "x" and not last_v:
                continue
            want = vert if axis == "y" else horiz
            st, sl, sb, sr = rect(s)
            kt, kl, kb, kr = rect(k)
            for rs in rules:
                for rk in rules:
                    if axis == "y":
                        got = rules[rk](kt, kb) - rules[rs](st, sb)
                    else:
                        got = rules[rk](kl, kr) - rules[rs](sl, sr)
                    if got == want:
                        score[(kind(s), kind(k))][(rs, rk)] += 1
            total[(kind(s), kind(k))] += 1
        print("== %s: best rule pair per (source kind, sink kind)" % axis)
        for pair, n in total.most_common(25):
            best = score[pair].most_common(3)
            print("  %4d %-55s %s" % (n, "%s -> %s" % pair, best))


if __name__ == "__main__":
    main(sys.argv[1:])
