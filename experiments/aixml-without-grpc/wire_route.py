# -*- coding: utf-8 -*-
"""Real wire geometry for a composed diagram: where each terminal attaches (its HOTPOINT), and the
compressedWireTable of an orthogonal route between two of them - so LabVIEW finds a valid wire
on load instead of repairing a generic one ("Wiring insanity found and fixed").

Measured over the two-ended wires of the VIs LabVIEW itself saved among its shipping examples
(wire_corpus.py, wire_fit.py, 2026-10-11): the hotpoint is the CENTRE of the terminal's rectangle
in its diagram - (t + b) // 2, (l + r) // 2 - for constants, front-panel terminals, subVI calls,
property and invoke nodes and most primitives; a primitive whose LabVIEW-drawn terminals do not
follow the stored termBounds gets a correction per (primResID, terminal position), learned from
the same corpus (parts/hotpoints.json).

Table format, two ends (FINDINGS section 4): [n joints] 08 [n-2 direction bytes] [n-2 lengths];
segments alternate, the first horizontal and rightwards; a direction byte belongs to segments
2..n-1 (01 = left/up); a length >= 255 is FF + big-endian 16 bit; the last segment's length is
implied by the sink. A wire with three or more ends is a depth-first tree (wire_tree.py), routed
only where every end's hotpoint is EXACT - the kinds learn_hotpoints_fanout.py found reproduced in
LabVIEW's own fan-outs (parts/hotpoints_exact.json), and the calibrated constants.
"""
import json
import os

import wire_tree

HERE = os.path.dirname(os.path.abspath(__file__))


def _len(v):
    return "%02X" % v if v < 255 else "FF%04X" % v


def table(src, snk):
    """The table of a wire from hotpoint src (y, x) to snk, or None where a two-bend route
    cannot reach it - a sink left of its source needs the six-joint route LabVIEW draws round
    the nodes, which is not composed (LabVIEW repairs the generic table there, as before)."""
    (sy, sx), (ky, kx) = src, snk
    dx, dy = kx - sx, ky - sy
    if dx <= 0:
        return None
    if dy == 0:
        return "0208"
    first = max(1, dx // 2)
    if first >= dx:
        return None
    # four joints: right `first`, then down/up |dy|, then right to the sink
    return "0408%02X00%s%s" % (1 if dy < 0 else 0, _len(first), _len(abs(dy)))


def _bounds(s):
    return tuple(int(v) for v in s.strip("()").split(","))


class Geometry:
    """Each terminal's rectangle in its diagram, read off a composed (or any) BD heap.

    - a node's terminal: the node's bounds' top-left plus the DCO's termBounds - a structure's
      tunnel on its OUTER side included, the structure being the node in the parent diagram;
    - a tunnel's INNER terminal (<dco uid=.../> naming the tunnel's DCO): the tunnel's termBounds as
      they are, the frame's diagram counting from the structure's top-left - measured on NI's
      examples, 27 of 27 inner case-tunnel -> property-node wires;
    - a constant: its ddo's bounds; a front-panel terminal: its own bounds.
    None where the heap does not say (a Bundle/Unbundle By Name keeps no termBounds)."""

    def __init__(self, bd, absolute_owners=()):
        # a Feedback Node's hidden node keeps its terminals in DIAGRAM coordinates (aixml2vi's
        # fb_pairs): its termBounds are not offset by the node's bounds
        self.absolute = set(absolute_owners)
        self.parent = {c: p for p in bd.iter() for c in p}
        self.terms = {}
        for t in bd.iter("SL__arrayElement"):
            if t.get("class") in ("term", "fPTerm"):
                self.terms[t.get("uid")] = t
        # a loop's `i` and stop terminals are DCOs under tags of their own
        self.tunnel = {d.get("uid"): d for d in bd.iter() if d.tag in ("dco", "loopIndexDCO", "loopTestDCO")
                       and d.get("class") and d.find("termBounds") is not None}

    def owner(self, t):
        x = self.parent.get(t)
        while x is not None and not (x.tag == "SL__arrayElement" and x.get("class") not in (None, "term")
                                     and x.find("bounds") is not None):
            x = self.parent.get(x)
        return x

    def rect(self, uid):
        """(rect, primResID or None, position in the owner's termList or None, kind) - kind names
        what the hotpoint rule and its exactness depend on: fPTerm, const:<ddo class>,
        inner:<tunnel DCO class>, node:<owner class>/<DCO class>"""
        t = self.terms.get(uid)
        if t is None:
            return None
        if t.get("class") == "fPTerm":
            b = t.findtext("bounds")
            return (_bounds(b), None, None, "fPTerm") if b else None
        dco = t.find("dco")
        if dco is None:
            return None
        if dco.get("class") == "bDConstDCO":
            ddo = dco.find("ddo")
            b = ddo.findtext("bounds") if ddo is not None else None
            return (_bounds(b), None, None, "const:%s" % ddo.get("class")) if b else None
        if dco.get("class") is None:
            # a tunnel's INNER side - only in a frame's own diagram (sRN); an In Place element
            # pair's terminals refer to a DCO too, on the structure's border, and are not this
            o = self.owner(t)
            ref = self.tunnel.get(dco.get("uid"))
            if ref is None or o is None or o.get("class") != "sRN":
                return None
            return _bounds(ref.findtext("termBounds")), None, None, "inner:%s" % ref.get("class")
        tb = dco.findtext("termBounds")
        o = self.owner(t)
        if not tb or o is None or o.get("class") == "sRN":
            return None
        ot, ol, _, _ = _bounds(o.findtext("bounds")) if o.get("uid") not in self.absolute else (0, 0, 0, 0)
        a, b, c, d = _bounds(tb)
        prim = o.findtext("primResID") if o.get("class") == "prim" else None
        tl = o.find("termList")
        pos = list(tl).index(t) if tl is not None and t in list(tl) else None
        return (ot + a, ol + b, ot + c, ol + d), prim, pos, "node:%s/%s" % (o.get("class"), dco.get("class"))


def junction_for(min_dx):
    """Where a fan-out's trunk ends: 22 px before the nearest sink, as LabVIEW puts it when the
    sinks line up (fanout_calibrate.py) - halfway on a short trunk."""
    return min_dx - 22 if min_dx > 30 else min_dx // 2


def route_heap(bd, hotpoints, source_first=True, absolute_owners=()):
    """Replace the generic table of every signal whose ends' geometry is known with the route
    between their hotpoints - a TWO-ended wire wherever its ends are located, a FAN-OUT only where
    every end's hotpoint is EXACT (a fan-out's table spells out every length, so one end a pixel
    off is a wire LabVIEW repairs). The source is the first end of a signal the composer wrote.
    -> (signals routed, signals left generic)"""
    g = Geometry(bd, absolute_owners)
    done = left = 0
    for s in bd.iter("SL__arrayElement"):
        if s.get("class") != "signal":
            continue
        tl = s.find("termList")
        if tl is None or len(tl) < 2 or not source_first:
            continue
        ends = [g.rect(x.get("uid")) for x in tl]
        if None in ends:
            left += 1
            continue
        hp = [hotpoints.of(*e) for e in ends]
        if len(tl) == 2:
            tab = table(hp[0], hp[1])
        elif all(hotpoints.exact(*e) for e in ends):
            sy, sx = hp[0]
            sinks = [(ky - sy, kx - sx) for ky, kx in hp[1:]]
            min_dx = min(dx for _, dx in sinks)
            tab = wire_tree.encode(sinks, junction_for(min_dx)) if min_dx >= 3 else None
        else:
            tab = None
        if tab is None:
            left += 1
            continue
        s.find("compressedWireTable").text = tab
        done += 1
    return done, left


class Hotpoints:
    """Where a terminal attaches: the base rule (the rectangle's centre; a constant's distance from
    its right edge) plus a learned (dy, dx) per terminal kind, and which kinds are known EXACTLY in
    both axes. A kind is looked up from its finest key: a primitive's (primResID, position), another
    node's (owner, DCO class, position) then (owner, DCO class), else the kind itself."""

    # a COMPOSED constant's output: vertically centred, this many px left of its right edge -
    # const_calibrate.py on LabVIEW's own saves, which keep a composed constant's size (2026-10-11),
    # and the suite's LabVIEW-routed fan-outs agree (stdNum 10 of 10, stdString 2 of 2, stdBool 1
    # of 1). NOT learned from NI's examples: there a numeric constant's output sits 7 to 14 px in
    # from the right, 75 of 236 at 7 - LabVIEW sizes ITS constants to their text. A cluster's and a
    # timestamp's sit elsewhere; an array's y moves with its height. None of those is exact.
    CONST_FROM_RIGHT = {"stdNum": 7, "stdString": 7, "stdBool": 8, "stdPath": 10, "indArr": 10}
    CONST_EXACT = ("stdNum", "stdString", "stdBool", "stdPath")

    def __init__(self, parts_dir):
        # harvested parts elsewhere (LABVIEWMCP_AIXML_PARTS) carry no hotpoints: this folder's own
        # then - the offsets are LabVIEW's, not a donor's
        def find(name):
            p = os.path.join(parts_dir, name)
            return p if os.path.exists(p) else os.path.join(HERE, "parts", name)
        self.corr = {}
        p = find("hotpoints.json")
        if os.path.exists(p):
            for k, v in json.load(open(p, encoding="utf-8")).items():
                self.corr[k] = tuple(v)
        p = find("hotpoints_exact.json")
        self.exact_keys = set(json.load(open(p, encoding="utf-8"))) if os.path.exists(p) else set()

    @staticmethod
    def keys(prim, pos, kind):
        if prim is not None:
            return ["%s/%s" % (prim, pos)]
        if kind and kind.startswith("node:"):
            return ["%s/%s" % (kind[5:], pos), kind[5:]]
        return [kind]

    def of(self, rect, prim=None, pos=None, kind=None):
        t, l, b, r = rect
        y, x = (t + b) // 2, (l + r) // 2
        if kind and kind.startswith("const:"):
            if kind[6:] in self.CONST_FROM_RIGHT:
                x = r - self.CONST_FROM_RIGHT[kind[6:]]
            return y, x
        for k in self.keys(prim, pos, kind):
            if k in self.corr:
                dy, dx = self.corr[k]
                return y + dy, x + dx
        return y, x

    def exact(self, rect, prim=None, pos=None, kind=None):
        """True where the learned model put this kind's ends exactly where LabVIEW's own fan-out
        tables do (learn_hotpoints_fanout.py) - decided by the finest key the model knows; a
        constant by the calibration above."""
        if kind and kind.startswith("const:"):
            return kind[6:] in self.CONST_EXACT
        for k in self.keys(prim, pos, kind):
            if k in self.exact_keys:
                return True
            if k in self.corr:
                return False
        return False
