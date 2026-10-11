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
implied by the sink.
"""
import json
import os

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
        self.tunnel = {d.get("uid"): d for d in bd.iter("dco") if d.get("class") and d.find("termBounds") is not None}

    def owner(self, t):
        x = self.parent.get(t)
        while x is not None and not (x.tag == "SL__arrayElement" and x.get("class") not in (None, "term")
                                     and x.find("bounds") is not None):
            x = self.parent.get(x)
        return x

    def rect(self, uid):
        """(rect, primResID or None, position in the owner's termList or None)"""
        t = self.terms.get(uid)
        if t is None:
            return None
        if t.get("class") == "fPTerm":
            b = t.findtext("bounds")
            return (_bounds(b), None, None) if b else None
        dco = t.find("dco")
        if dco is None:
            return None
        if dco.get("class") == "bDConstDCO":
            b = dco.findtext("ddo/bounds")
            return (_bounds(b), None, None) if b else None
        if dco.get("class") is None:
            ref = self.tunnel.get(dco.get("uid"))
            if ref is None:
                return None
            return _bounds(ref.findtext("termBounds")), None, None
        tb = dco.findtext("termBounds")
        o = self.owner(t)
        if not tb or o is None or o.get("class") == "sRN":
            return None
        ot, ol, _, _ = _bounds(o.findtext("bounds")) if o.get("uid") not in self.absolute else (0, 0, 0, 0)
        a, b, c, d = _bounds(tb)
        prim = o.findtext("primResID") if o.get("class") == "prim" else None
        tl = o.find("termList")
        pos = list(tl).index(t) if prim is not None and tl is not None and t in list(tl) else None
        return (ot + a, ol + b, ot + c, ol + d), prim, pos


def route_heap(bd, hotpoints, source_first=True, absolute_owners=()):
    """Replace the generic table of every TWO-ended signal whose ends' geometry is known with the
    route between their hotpoints. The source is the first end of a signal the composer wrote.
    -> (signals routed, signals left generic)"""
    g = Geometry(bd, absolute_owners)
    done = left = 0
    for s in bd.iter("SL__arrayElement"):
        if s.get("class") != "signal":
            continue
        tl = s.find("termList")
        if tl is None or len(tl) != 2:
            continue
        ends = [g.rect(x.get("uid")) for x in tl]
        if None in ends:
            left += 1
            continue
        hp = [hotpoints.of(r, prim, pos) for r, prim, pos in ends]
        tab = table(hp[0], hp[1]) if source_first else None
        if tab is None:
            left += 1
            continue
        s.find("compressedWireTable").text = tab
        done += 1
    return done, left


class Hotpoints:
    """Per (primResID, terminal position) corrections to the centre rule, (dy, dx)."""

    def __init__(self, parts_dir):
        # harvested parts elsewhere (LABVIEWMCP_AIXML_PARTS) carry no hotpoints.json: this
        # folder's own then - the offsets are LabVIEW's, not a donor's
        p = os.path.join(parts_dir, "hotpoints.json")
        if not os.path.exists(p):
            p = os.path.join(HERE, "parts", "hotpoints.json")
        self.corr = {}
        if os.path.exists(p):
            for k, v in json.load(open(p, encoding="utf-8")).items():
                self.corr[k] = tuple(v)

    def of(self, rect, prim=None, pos=None):
        t, l, b, r = rect
        y, x = (t + b) // 2, (l + r) // 2
        if prim is not None:
            dy, dx = self.corr.get("%s/%s" % (prim, pos), (0, 0))
            y, x = y + dy, x + dx
        return y, x
