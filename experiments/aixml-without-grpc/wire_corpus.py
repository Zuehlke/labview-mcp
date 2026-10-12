# -*- coding: utf-8 -*-
"""Collect TWO-ENDED wires (with --fanout: those of THREE or more ends) from VIs LabVIEW itself saved (NI's shipping examples), with their
compressedWireTable and the geometry of both ends - the data the real wire geometry is decoded from.

    wire_corpus.py <out.jsonl> [--max N] [--root <examples dir>]

One line per wire whose two ends sit on the SAME diagram and are node terminals (a primitive, a
subVI call, a bundle, ...), a constant or a front-panel terminal: the table, and per end the owner's
class and bounds (in its diagram), the DCO's termBounds and the term's/dco's objFlags. No LabVIEW.
"""
import glob
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402

DCO_TAGS = ("dco", "loopIndexDCO", "loopTestDCO")
SKIP_OWNERS = {"sRN", None}   # tunnels' inner terminals: a diagram of their own, decoded later


def ends_of(bd):
    """term uid -> geometry of the terminal, for the kinds this corpus collects."""
    parent = {c: p for p in bd.iter() for c in p}
    out = {}
    # a tunnel's DCO by uid: an INNER terminal refers to it (<dco uid=.../> with no class) and sits
    # in the frame's own diagram, whose origin is the structure's top-left - so the inner rectangle
    # IS the tunnel's termBounds (to be confirmed by the corpus: kind "inner")
    # (a loop's `i` and stop terminals are DCOs under their own tags)
    tun = {d.get("uid"): d for d in bd.iter() if d.tag in DCO_TAGS and d.get("class") and d.find("termBounds") is not None}
    for t in bd.iter("SL__arrayElement"):
        cls = t.get("class")
        if cls == "fPTerm":
            out[t.get("uid")] = {"kind": "fPTerm", "owner": "fPTerm", "bounds": t.findtext("bounds"),
                                 "term": None, "tflags": int(t.findtext("objFlags") or 0), "dflags": 0,
                                 "diag": id(parent.get(parent.get(t)))}
            continue
        if cls != "term":
            continue
        dco = t.find("dco")
        if dco is None:
            continue
        if dco.get("class") == "bDConstDCO":
            ddo = dco.find("ddo")
            out[t.get("uid")] = {"kind": "const", "owner": "const", "bounds": ddo.findtext("bounds") if ddo is not None else None,
                                 "term": None, "tflags": int(t.findtext("objFlags") or 0),
                                 "ddo": ddo.get("class") if ddo is not None else None,
                                 "dflags": int(dco.findtext("objFlags") or 0), "diag": None}
            continue
        # the owning node: the nearest ancestor element with bounds that is not a term
        x = parent.get(t)
        while x is not None and not (x.tag == "SL__arrayElement" and x.get("class") not in (None, "term")
                                     and x.find("bounds") is not None):
            x = parent.get(x)
        if dco.get("class") is None and dco.get("uid") in tun:
            ref = tun[dco.get("uid")]
            pn = parent.get(ref)            # the node the referenced DCO belongs to
            while pn is not None and not (pn.tag == "SL__arrayElement" and pn.get("class") not in (None, "term")
                                          and pn.find("bounds") is not None):
                pn = parent.get(pn)
            if pn is not None and pn is x:
                # a second terminal on its own node's DCO (Build Waveform's): the node's terminal
                tl = x.find("termList")
                out[t.get("uid")] = {"kind": "node", "owner": x.get("class"), "bounds": x.findtext("bounds"),
                                     "term": ref.findtext("termBounds"), "dclass": ref.get("class"), "pidx": None,
                                     "pos": list(tl).index(t) if tl is not None and t in list(tl) else None,
                                     "tflags": int(t.findtext("objFlags") or 0),
                                     "dflags": int(ref.findtext("objFlags") or 0), "hot": _hot(ref),
                                     "diag": None, "prim": x.findtext("primResID")}
                continue
            if x is not None and x.get("class") not in SKIP_OWNERS:
                # a STRUCTURE's own terminal naming a DCO of a node inside its frame - an In Place
                # element pair, whose node straddles the border: the structure's top-left plus the
                # pair node's (frame coordinates) plus the DCO's termBounds
                if pn is None:
                    continue
                st, sl = lvheap.parse_bounds(x.findtext("bounds"))[:2]
                nt, nl = lvheap.parse_bounds(pn.findtext("bounds"))[:2]
                a, b, c, d = lvheap.parse_bounds(ref.findtext("termBounds"))
                out[t.get("uid")] = {"kind": "pair", "owner": "pair:%s" % ref.get("class"),
                                     "bounds": "(%d, %d, %d, %d)" % (st + nt + a, sl + nl + b, st + nt + c, sl + nl + d),
                                     "term": None, "dclass": ref.get("class"), "tflags": int(t.findtext("objFlags") or 0),
                                     "dflags": int(ref.findtext("objFlags") or 0), "hot": _hot(ref), "diag": None}
                continue
            out[t.get("uid")] = {"kind": "inner", "owner": "inner:%s" % ref.get("class"), "bounds": ref.findtext("termBounds"),
                                 "term": None, "dclass": ref.get("class"), "tflags": int(t.findtext("objFlags") or 0),
                                 "dflags": int(ref.findtext("objFlags") or 0) ^ 1, "hot": _hot(ref), "diag": None}
            continue
        if x is None or x.get("class") in SKIP_OWNERS:
            continue
        diag = parent.get(parent.get(x))     # nodeList's parent diagram
        tl = x.find("termList")
        pos = list(tl).index(t) if tl is not None and t in list(tl) else None
        e = {"kind": "node", "owner": x.get("class"), "bounds": x.findtext("bounds"),
             "term": dco.findtext("termBounds"), "dclass": dco.get("class"),
             "pidx": dco.findtext("parmIndex"), "pos": pos,
             "tflags": int(t.findtext("objFlags") or 0), "dflags": int(dco.findtext("objFlags") or 0),
             "hot": _hot(dco), "diag": id(diag), "prim": x.findtext("primResID")}
        if x.get("class") == "hiddenFBNode":
            # a Feedback Node's hidden half keeps its terminals in DIAGRAM coordinates
            e["bounds"], e["term"] = dco.findtext("termBounds"), None
        out[t.get("uid")] = e
    return out


def _hot(dco):
    """LabVIEW's own offset of a terminal's hotpoint from the centre of its rectangle, (dx, dy) -
    <termHotPoint> on the DCO, where LabVIEW writes one (shift registers, Feedback Nodes, some
    array nodes); None elsewhere."""
    h = dco.findtext("termHotPoint")
    return list(lvheap.parse_bounds(h)) if h else None


def collect(vi, w, fanout=False):
    tmp = tempfile.mkdtemp(prefix="wc_")
    try:
        try:
            b = lvheap.Bundle(lvheap.extract(vi, os.path.join(tmp, "b"), timeout=60))
        except Exception:
            return 0
        bd = b.bd
        ends = ends_of(bd)
        n = 0
        for s in bd.iter("SL__arrayElement"):
            if s.get("class") != "signal":
                continue
            tl = s.find("termList")
            if tl is None or (len(tl) < 3 if fanout else len(tl) != 2):
                continue
            e = [ends.get(x.get("uid")) for x in tl]
            if None in e or any(x["bounds"] is None for x in e):
                continue
            w.write(json.dumps({"vi": vi, "table": s.findtext("compressedWireTable"),
                                "ends": [{k: v for k, v in x.items() if k != "diag"} for x in e]}) + "\n")
            n += 1
        return n
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main(argv):
    out = argv[0]
    mx = int(argv[argv.index("--max") + 1]) if "--max" in argv else 200
    root = argv[argv.index("--root") + 1] if "--root" in argv else \
        r"C:\Program Files (x86)\National Instruments\LabVIEW 2026\examples"
    vis = sorted(glob.glob(os.path.join(root, "**", "*.vi"), recursive=True))[:mx]
    total = 0
    with open(out, "w", encoding="utf-8") as w:
        for i, vi in enumerate(vis):
            total += collect(vi, w, "--fanout" in argv)
            if i % 25 == 0:
                print("%d/%d VIs, %d wires" % (i, len(vis), total), flush=True)
    print("%d wires from %d VIs" % (total, len(vis)))


if __name__ == "__main__":
    main(sys.argv[1:])
