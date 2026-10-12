# -*- coding: utf-8 -*-
"""Learn hotpoint offsets in BOTH axes from LabVIEW-saved FAN-OUTS (wire_corpus.py --fanout): a
fan-out table gives every end's position relative to the source exactly (wire_tree.walk), so once
the other ends of a wire are known, the one left over shows its own offset from the router's base
rule (wire_route.Hotpoints: the rectangle's centre, a constant's distance from its right edge).

    learn_hotpoints_fanout.py <fanouts.jsonl> [<fanouts.jsonl> ...] [--write]

Several corpora are read as one - NI's examples and the suite's own LabVIEW-routed saves
(suite_corpus.py), which carry the terminal kinds the composer makes and the examples do not.

Bootstrapped from the front-panel terminal alone, whose hotpoint is the centre of its bounds: each
round learns every terminal kind that is the ONE unknown end of at least 3 wires with one offset in
>= 80 % of them, until a round learns nothing. A kind is keyed as finely as the data allows - a
primitive by (primResID, terminal position), another node by (owner, DCO class, position) and by
(owner, DCO class), a tunnel's inner side by the tunnel's class - and the router looks the finest
key up first. Constants are NOT learned: NI's examples size a constant to its text and the composer
does not, so their hotpoints follow const_calibrate.py (wire_route.Hotpoints.CONST_EXACT).

Then every learned kind is VALIDATED: over the wires all of whose ends are known, the share whose
every end the model puts exactly where the table does. The kinds at >= 90 % of at least 3 wires are
EXACT - a fan-out's table spells out every length, so the router routes a fan-out only where all of
its ends are exact (wire_route.route_heap).

--write merges the offsets into parts/hotpoints.json (a fan-out's both axes replace the two-ended
learner's one) and writes the exact kinds to parts/hotpoints_exact.json.
"""
import collections
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import wire_decode as wd  # noqa: E402
import wire_fit as wf  # noqa: E402
import wire_route  # noqa: E402
import wire_tree as wt  # noqa: E402

MIN_WIRES, MIN_SHARE, EXACT_SHARE = 3, 0.8, 0.9


def kind(e):
    if e["kind"] == "fPTerm":
        return "fPTerm"
    if e["kind"] == "const":
        return "const:%s" % e.get("ddo")
    if e["kind"] == "inner":
        return "inner:%s" % e.get("dclass")
    if e["kind"] == "pair":
        return "pair:%s" % e.get("dclass")
    return "node:%s/%s" % (e["owner"], e.get("dclass"))


def keys(e):
    return wire_route.Hotpoints.keys(e.get("prim") if e["owner"] == "prim" else None, e.get("pos"), kind(e))


def main(argv):
    base = wire_route.Hotpoints(os.path.join(HERE, "nonexistent-parts"))
    base.corr, base.exact_keys = {}, set()            # the base rule alone

    def hot(e):
        # the base rule: the centre, LabVIEW's own <termHotPoint> where the DCO has one
        return base.of(wf.rect(e), None, None, kind(e), tuple(e["hot"]) if e.get("hot") else None)

    wires = []
    lines = (l for f in argv if not f.startswith("--") for l in open(f, encoding="utf-8"))
    for l in lines:
        try:
            w = json.loads(l)
        except ValueError:
            continue
        ends = w["ends"]
        srcs = [i for i, e in enumerate(ends) if wd.is_source(e)]
        got = wt.walk(w["table"])
        if len(srcs) != 1 or not got or len(got) != len(ends) - 1:
            continue
        s = ends[srcs[0]]
        wires.append((s, [e for e in ends if e is not s], got))
    print("%d fan-outs with one source and a walkable table" % len(wires))

    known = {"fPTerm": (0, 0)}

    def offset(e):
        for k in keys(e):
            if k in known:
                return known[k]
        return None

    def at(e):
        o = offset(e)
        if o is None:
            return None
        y, x = hot(e)
        return y + o[0], x + o[1]

    for rnd in range(20):
        votes = collections.defaultdict(collections.Counter)
        for s, sinks, got in wires:
            sp = at(s)
            kp = [at(k) for k in sinks]
            unknown = [i for i, p in enumerate(kp) if p is None]
            if sp is not None and len(unknown) == 1:
                # the walked end no known sink takes is the unknown one's
                left = collections.Counter((sp[0] + g[0], sp[1] + g[1]) for g in got)
                for i, p in enumerate(kp):
                    if p is not None:
                        left[p] -= 1
                rest = [p for p, c in left.items() for _ in range(c) if c > 0]
                if len(rest) == 1 and all(c >= 0 for c in left.values()):
                    k = sinks[unknown[0]]
                    y, x = hot(k)
                    for key in keys(k):
                        votes[key][(rest[0][0] - y, rest[0][1] - x)] += 1
            elif sp is None and not unknown:
                # the source sits where EVERY sink, less its walked offset, puts it
                cand = collections.Counter((p[0] - g[0], p[1] - g[1]) for p in kp for g in got)
                best = [c for c, n in cand.items() if n == len(sinks)]
                if len(best) == 1:
                    y, x = hot(s)
                    for key in keys(s):
                        votes[key][(best[0][0] - y, best[0][1] - x)] += 1
        new = 0
        for key, c in votes.items():
            # a constant is the router's calibration, not this corpus's: NI's examples size their
            # constants to the text, the composer does not (wire_route.Hotpoints.CONST_EXACT)
            if key in known or key.startswith("const:"):
                continue
            n = sum(c.values())
            v, m = c.most_common(1)[0]
            if n >= MIN_WIRES and m / n >= MIN_SHARE:
                known[key] = v
                new += 1
        print("round %d: %d kinds learned, %d in all" % (rnd, new, len(known)))
        if not new:
            break

    # validation: per kind, the wires all of whose ends are known, reproduced exactly or not
    hit, seen = collections.Counter(), collections.Counter()
    for s, sinks, got in wires:
        sp = at(s)
        kp = [at(k) for k in sinks]
        if sp is None or None in kp:
            continue
        ok = sorted((p[0] - sp[0], p[1] - sp[1]) for p in kp) == sorted(got)
        used = set()
        for e in [s] + sinks:
            used.add(next(k for k in keys(e) if k in known))
        for k in used:
            seen[k] += 1
            hit[k] += ok
    exact = sorted(k for k in seen if seen[k] >= MIN_WIRES and hit[k] / seen[k] >= EXACT_SHARE)
    print("%d kinds validated on >= %d wires, %d exact" % (sum(1 for k in seen if seen[k] >= MIN_WIRES), MIN_WIRES, len(exact)))
    for k in sorted(seen, key=lambda k: -seen[k])[:40]:
        print("  %-36s %-10s %4d of %4d %s" % (k, known[k], hit[k], seen[k], "EXACT" if k in exact else ""))
    if "--write" in argv:
        p = os.path.join(COMPOSER, "parts", "hotpoints.json")
        cur = json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}
        # every learned kind, (0, 0) included: a fine key's zero must shadow a coarse key's offset
        learned = {k: list(v) for k, v in known.items()}
        changed = sum(1 for k, v in learned.items() if cur.get(k) != v)
        cur.update(learned)
        json.dump(cur, open(p, "w", encoding="utf-8"), indent=1, sort_keys=True)
        print("merged into parts/hotpoints.json: %d new or changed, %d in all" % (changed, len(cur)))
        ex = os.path.join(COMPOSER, "parts", "hotpoints_exact.json")
        json.dump(exact, open(ex, "w", encoding="utf-8"), indent=1)
        print("wrote parts/hotpoints_exact.json: %d kinds" % len(exact))


if __name__ == "__main__":
    main(sys.argv[1:])
