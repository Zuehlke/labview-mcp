# -*- coding: utf-8 -*-
"""Learn ONE hotpoint offset per PRIMITIVE from LabVIEW-saved fan-outs (wire_corpus.py --fanout).

    learn_prim_origins.py <fanouts.jsonl> [<fanouts.jsonl> ...] [--write]

A primitive is stored as a 32 x 32 cell whatever its real size, and its terminals span a smaller
box inside that cell. The offset the centre rule needs is therefore the same for every terminal of
one primitive (measured 2026-11-21: 159 primitives of 159 on NI's examples) - so it is keyed by
the primResID alone, and a terminal no corpus ever showed in a fan-out is placed with the rest.

Three steps:

1. A primitive whose EXACT (primResID, position) keys all carry one offset has that offset.
2. Bootstrapping, as learn_hotpoints_fanout.py does per terminal: a fan-out all of whose other
   ends are placed exactly shows the offset of the one primitive end left over; a primitive is
   learned from at least 3 such wires with one offset in >= 90 % of them, until a round learns none.
3. Validation: over the fan-outs whose every end is placed, with each primitive's ONE offset used
   for all of its terminals, a primitive stays only where >= 90 % of at least 3 wires through it
   are reproduced.

--write adds the survivors to parts/hotpoints.json and parts/hotpoints_exact.json under the key
"<primResID>", and then drops what they make redundant - nothing the router answers changes:

- a (primResID, position) key of a primitive that has its own key, where the position is not
  exact (Hotpoints.of never reads it then) or carries the primitive's own offset;
- an EXACT key of any other kind whose offset is (0, 0): the base rule gives the same point, and
  hotpoints_exact.json still says it is exact. A NON-exact key stays even at (0, 0) - Hotpoints.exact
  stops at it instead of falling back to a coarser exact key.

The kinds learned from the suite's own saves stay as they are otherwise.
"""
import collections
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(1, COMPOSER)
import learn_hotpoints_fanout as lf  # noqa: E402
import wire_decode as wd  # noqa: E402
import wire_fit as wf  # noqa: E402
import wire_route  # noqa: E402
import wire_tree as wt  # noqa: E402

MIN_WIRES, SHARE = 3, 0.9


def prune(corr, exact):
    """-> (corr, exact) without the keys the per-primitive ones and the base rule make redundant
    (module docstring); wire_route.Hotpoints answers the same for every terminal."""
    corr = {k: tuple(v) for k, v in corr.items()}
    exact = set(exact)
    prims = {k for k in corr if k.isdigit()}
    for k in list(corr):
        head = k.split("/")[0]
        if "/" in k and head in prims:
            if k not in exact or corr[k] == corr[head]:
                del corr[k]
                if head in exact:
                    exact.discard(k)
        elif not k.isdigit() and k in exact and corr[k] == (0, 0):
            del corr[k]
    return {k: list(v) for k, v in corr.items()}, exact


def prim_of(e):
    return e.get("prim") if e["owner"] == "prim" else None


def main(argv):
    shipped = wire_route.Hotpoints(os.path.join(COMPOSER, "parts"))
    # the model BEFORE any per-primitive key: what the per-terminal keys alone say
    shipped.corr = {k: v for k, v in shipped.corr.items() if "/" in k or not k.isdigit()}
    shipped.exact_keys = {k for k in shipped.exact_keys if "/" in k or not k.isdigit()}
    base = wire_route.Hotpoints(os.path.join(HERE, "nonexistent-parts"))
    base.corr, base.exact_keys = {}, set()

    def args(e):
        return wf.rect(e), prim_of(e), e.get("pos"), lf.kind(e), tuple(e["hot"]) if e.get("hot") else None

    def centre(e):
        r, _, _, k, h = args(e)
        return base.of(r, None, None, k, h)

    wires = []
    for f in (a for a in argv if not a.startswith("--")):
        for l in open(f, encoding="utf-8"):
            try:
                w = json.loads(l)
            except ValueError:
                continue
            ends = w["ends"]
            srcs = [i for i, e in enumerate(ends) if wd.is_source(e)]
            got = wt.walk(w["table"]) if w.get("table") else None
            if len(srcs) != 1 or not got or len(got) != len(ends) - 1:
                continue
            if any(e["kind"] == "const" for e in ends):
                continue            # NI sizes its constants to their text; the composer's differ
            s = ends[srcs[0]]
            wires.append((s, [e for e in ends if e is not s], got))
    print("%d fan-outs with one source, a walkable table and no constant end" % len(wires))

    # step 1: primitives whose exact per-terminal keys agree
    by = collections.defaultdict(set)
    for k in shipped.exact_keys:
        p = k.split("/")[0]
        if p.isdigit() and k in shipped.corr:
            by[p].add(tuple(shipped.corr[k]))
    origin = {p: v.pop() for p, v in by.items() if len(v) == 1}
    print("step 1: %d primitives with one offset over their exact terminals, %d with several"
          % (len(origin), sum(1 for v in by.values() if len(v) > 1)))

    def at(e):
        """(y, x) of an end that is placed exactly, else None."""
        p = prim_of(e)
        if p is not None:
            if p in origin:
                y, x = centre(e)
                return y + origin[p][0], x + origin[p][1]
            return None
        return shipped.of(*args(e)) if shipped.exact(*args(e)) else None

    # step 2: bootstrap the primitives no exact key covers
    for rnd in range(20):
        votes = collections.defaultdict(collections.Counter)
        for s, sinks, got in wires:
            sp = at(s)
            kp = [at(k) for k in sinks]
            unknown = [i for i, p in enumerate(kp) if p is None]
            if sp is not None and len(unknown) == 1 and prim_of(sinks[unknown[0]]):
                left = collections.Counter((sp[0] + g[0], sp[1] + g[1]) for g in got)
                for p in kp:
                    if p is not None:
                        left[p] -= 1
                rest = [p for p, c in left.items() for _ in range(c) if c > 0]
                if len(rest) == 1 and all(c >= 0 for c in left.values()):
                    k = sinks[unknown[0]]
                    y, x = centre(k)
                    votes[prim_of(k)][(rest[0][0] - y, rest[0][1] - x)] += 1
            elif sp is None and not unknown and prim_of(s):
                cand = collections.Counter((p[0] - g[0], p[1] - g[1]) for p in kp for g in got)
                best = [c for c, n in cand.items() if n == len(sinks)]
                if len(best) == 1:
                    y, x = centre(s)
                    votes[prim_of(s)][(best[0][0] - y, best[0][1] - x)] += 1
        new = 0
        for p, c in votes.items():
            n = sum(c.values())
            v, m = c.most_common(1)[0]
            if p not in origin and n >= MIN_WIRES and m / n >= SHARE:
                origin[p] = v
                new += 1
        print("step 2, round %d: %d primitives learned, %d in all" % (rnd, new, len(origin)))
        if not new:
            break

    # step 3: validate each primitive's one offset over ALL of its terminals
    hit, seen = collections.Counter(), collections.Counter()
    for s, sinks, got in wires:
        sp = at(s)
        kp = [at(k) for k in sinks]
        if sp is None or None in kp:
            continue
        ok = sorted((p[0] - sp[0], p[1] - sp[1]) for p in kp) == sorted(got)
        for p in set(prim_of(e) for e in [s] + sinks) - {None}:
            seen[p] += 1
            hit[p] += ok
    keep = {p: origin[p] for p in origin if seen[p] >= MIN_WIRES and hit[p] / seen[p] >= SHARE}
    print("step 3: %d primitives reproduce >= %d %% of at least %d fan-outs; dropped: %s"
          % (len(keep), int(SHARE * 100), MIN_WIRES,
             sorted("%s (%d/%d)" % (p, hit[p], seen[p]) for p in origin if p not in keep)))

    if "--write" in argv:
        p = os.path.join(COMPOSER, "parts", "hotpoints.json")
        cur = json.load(open(p, encoding="utf-8"))
        cur = {k: v for k, v in cur.items() if "/" in k or not k.isdigit()}
        cur.update({k: list(v) for k, v in keep.items()})
        ex = os.path.join(COMPOSER, "parts", "hotpoints_exact.json")
        exact = set(k for k in json.load(open(ex, encoding="utf-8")) if "/" in k or not k.isdigit()) | set(keep)
        n_corr, n_exact = len(cur), len(exact)
        cur, exact = prune(cur, exact)
        json.dump(cur, open(p, "w", encoding="utf-8"), indent=1, sort_keys=True)
        json.dump(sorted(exact), open(ex, "w", encoding="utf-8"), indent=1)
        print("wrote %d per-primitive keys; hotpoints.json %d -> %d keys, hotpoints_exact.json %d -> %d"
              % (len(keep), n_corr, len(cur), n_exact, len(exact)))
    else:
        print("offsets by number of primitives: %s" % collections.Counter(keep.values()).most_common(12))


if __name__ == "__main__":
    main(sys.argv[1:])
