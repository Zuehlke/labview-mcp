# -*- coding: utf-8 -*-
"""Learn per-primitive hotpoint corrections from LabVIEW-saved wires (wire_corpus.py's jsonl).

    learn_hotpoints.py <wires.jsonl> [--write]

A wire whose one end is an ANCHOR - a kind the centre rule fits (constant, front-panel terminal,
subVI call, property/invoke item) - fixes the other end's offset from its centre along the axis
the table constrains: y for an even joint count (the last segment horizontal), x for an odd one.
A primitive terminal (primResID, position in its termList) keeps a correction only when one value
holds for >= 80 % of at least 2 wires; the rest stay on the centre rule. --write puts the result
into parts/hotpoints.json, where wire_route.Hotpoints reads it.
"""
import collections
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import wire_decode as wd  # noqa: E402
import wire_fit as wf  # noqa: E402

ANCHORS = ("const", "fPTerm", "iUse", "propNode", "invokeNode")


def key(e):
    if e["kind"] == "node" and e["owner"] == "prim" and e.get("prim"):
        return "%s/%s" % (e["prim"], e.get("pos"))
    return None


def main(argv):
    seen = collections.defaultdict(lambda: {"y": collections.Counter(), "x": collections.Counter()})
    anchor_ok = collections.Counter()
    for l in open(argv[0], encoding="utf-8"):
        w = json.loads(l)
        p = wd.parse_table(w["table"])
        a, b = w["ends"]
        if not p or wd.is_source(a) == wd.is_source(b):
            continue
        s, k = (a, b) if wd.is_source(a) else (b, a)
        vert, horiz, last_v = wf.offsets(p)
        st, sl, sb, sr = wf.rect(s)
        kt, kl, kb, kr = wf.rect(k)
        if last_v:
            axis, resid = "x", horiz - ((kl + kr) // 2 - (sl + sr) // 2)
        else:
            axis, resid = "y", vert - ((kt + kb) // 2 - (st + sb) // 2)
        sa, ka = s["owner"] in ANCHORS, k["owner"] in ANCHORS
        if sa and ka:
            anchor_ok[resid == 0] += 1
        elif sa and key(k):
            seen[key(k)][axis][resid] += 1          # the sink's hotpoint is centre + resid
        elif ka and key(s):
            seen[key(s)][axis][-resid] += 1
    print("anchor-to-anchor wires the centre rule fits: %d of %d" % (anchor_ok[True], sum(anchor_ok.values())))
    corr = {}
    for kk, ax in sorted(seen.items()):
        out = []
        for axis in ("y", "x"):
            c = ax[axis]
            n = sum(c.values())
            if n >= 2:
                v, m = c.most_common(1)[0]
                if m / n >= 0.8:
                    out.append(v)
                    continue
            out.append(None)
        if any(v for v in out):
            corr[kk] = [out[0] or 0, out[1] or 0]
            print("  %-14s y %-28s x %-20s -> %s" % (kk, dict(ax["y"]), dict(ax["x"]), corr[kk]))
    print("%d primitive terminals with a correction" % len(corr))
    if "--write" in argv:
        json.dump(corr, open(os.path.join(HERE, "parts", "hotpoints.json"), "w", encoding="utf-8"), indent=1, sort_keys=True)
        print("wrote parts/hotpoints.json")


if __name__ == "__main__":
    main(sys.argv[1:])
