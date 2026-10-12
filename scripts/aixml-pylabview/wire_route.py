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

import lvheap
import wire_tree



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


# terminals a wire does not leave or enter eastwards - read off LabVIEW's own routes for the
# composer's Feedback Nodes (suite_corpus.py, 2026-10-11): the hidden node's output on its LEFT edge
# starts west (byte 1 of the table is 02, not 08: "04 02 01 00 08 0C" is west 8, up 12, east into
# the sink), its initializer at the BOTTOM is entered from below ("05 08 00 00 01 0D 16 46": east,
# down past it, east, up into it)
EXIT_WEST = ("node:hiddenFBNode/leftFeedback",)
ENTER_FROM_BELOW = ("node:hiddenFBNode/initFeedback",)


def table_west(src, snk):
    """From a terminal that is left WESTWARDS: west, up or down, east into the sink - past the sink
    first where it lies west too. None where both share a row (that needs a detour)."""
    (sy, sx), (ky, kx) = src, snk
    dx, dy = kx - sx, ky - sy
    if dy == 0:
        return None
    first = 8 if dx >= 0 else 8 - dx            # end 8 px west of the sink either way
    return "0402%02X00%s%s" % (1 if dy < 0 else 0, _len(first), _len(abs(dy)))


def table_from_below(src, snk):
    """Into a terminal entered from BELOW: east then up into it, or - where it is not above the
    source - east, down past it, east, and up 10 px into it."""
    (sy, sx), (ky, kx) = src, snk
    dx, dy = kx - sx, ky - sy
    if dx <= 0:
        return None
    if dy < 0:
        return "030801%s" % _len(dx)
    first = max(1, dx // 2)
    return "05080000%s%s%s" % ("01", _len(first), _len(dy + 10) + _len(dx - first)) if dx - first > 0 else None


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
        """(rect, primResID or None, position in the owner's termList or None, kind, hot) - kind
        names what the hotpoint rule and its exactness depend on: fPTerm, const:<ddo class>,
        inner:<tunnel DCO class>, pair:<DCO class>, node:<owner class>/<DCO class>; hot is the
        DCO's own <termHotPoint> (dx, dy), or None."""
        t = self.terms.get(uid)
        if t is None:
            return None
        if t.get("class") == "fPTerm":
            b = t.findtext("bounds")
            return (_bounds(b), None, None, "fPTerm", None) if b else None
        dco = t.find("dco")
        if dco is None:
            return None
        if dco.get("class") == "bDConstDCO":
            ddo = dco.find("ddo")
            b = ddo.findtext("bounds") if ddo is not None else None
            return (_bounds(b), None, None, "const:%s" % ddo.get("class"), _value_box_centre(ddo)) if b else None
        o = self.owner(t)
        if dco.get("class") is None:
            ref = self.tunnel.get(dco.get("uid"))
            if ref is None or o is None:
                return None
            if o.get("class") == "sRN":
                # a tunnel's INNER side, in the frame's own diagram: the tunnel's termBounds
                return _bounds(ref.findtext("termBounds")), None, None, "inner:%s" % ref.get("class"), _hot(ref)
            pn = self.owner(ref)
            if pn is o:
                # a second terminal on its own node's DCO (Build Waveform's): an ordinary one
                dco = ref
            elif pn is not None:
                # a structure's own terminal naming a DCO of a node in its frame - an In Place
                # element pair, its node straddling the border: the structure's top-left, the
                # node's (frame coordinates) and the DCO's termBounds
                st, sl, _, _ = _bounds(o.findtext("bounds"))
                nt, nl, _, _ = _bounds(pn.findtext("bounds"))
                a, b, c, d = _bounds(ref.findtext("termBounds"))
                return ((st + nt + a, sl + nl + b, st + nt + c, sl + nl + d), None, None,
                        "pair:%s" % ref.get("class"), _hot(ref))
            else:
                return None
        tb = dco.findtext("termBounds")
        if not tb or o is None or o.get("class") == "sRN":
            return None
        # a Feedback Node's hidden half keeps its terminals in DIAGRAM coordinates
        absolute = o.get("uid") in self.absolute or o.get("class") == "hiddenFBNode"
        ot, ol, _, _ = _bounds(o.findtext("bounds")) if not absolute else (0, 0, 0, 0)
        a, b, c, d = _bounds(tb)
        prim = o.findtext("primResID") if o.get("class") == "prim" else None
        tl = o.find("termList")
        pos = list(tl).index(t) if tl is not None and t in list(tl) else None
        return (ot + a, ol + b, ot + c, ol + d), prim, pos, "node:%s/%s" % (o.get("class"), dco.get("class")), _hot(dco)


def _hot(dco):
    """LabVIEW's own offset of a terminal's hotpoint from the centre of its rectangle, (dx, dy):
    <termHotPoint> on the DCO, which LabVIEW writes for shift registers, Feedback Nodes and some
    array nodes - and which the offsets learned for those kinds turned out to be."""
    h = dco.findtext("termHotPoint")
    return _bounds(h) if h else None


def _value_box_centre(ddo):
    """A constant's value box (its cosm part 9) centre, in px from the constant's LEFT edge - the
    output of a string or numeric constant sits there; None when the constant has no such part."""
    parts = ddo.find("partsList")
    for p in parts if parts is not None else []:
        if p.get("class") == "cosm" and p.findtext("partID") == "9" and p.findtext("bounds"):
            _, cl, _, cr = _bounds(p.findtext("bounds"))
            return (cl + cr) // 2
    return None


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
            if ends[0][3] in EXIT_WEST:
                tab = table_west(hp[0], hp[1])
            elif ends[1][3] in ENTER_FROM_BELOW:
                tab = table_from_below(hp[0], hp[1])
            else:
                tab = table(hp[0], hp[1])
        elif any(e[3] in EXIT_WEST + ENTER_FROM_BELOW for e in ends):
            tab = None                  # a comb leaves and enters eastwards only
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
    # from the right, 75 of 236 at 7 - LabVIEW sizes ITS constants to their text.
    CONST_FROM_RIGHT = {"stdNum": 7, "stdString": 7, "stdBool": 8, "stdPath": 10, "indArr": 10}
    # A composed CLUSTER constant's output is 18 px below its top and 28 px in from its LEFT edge,
    # whatever its height (69 to 132 measured) and width; an ARRAY constant's 11 px below its top
    # (23 and 33 tall) - read off LabVIEW's repaired tables in the ATM build's saves, 46 of the 64
    # wires it rewrote there started at one of the two (2026-10-12). Not exact: their x is two
    # samples (cluster) or the old rule (array).
    CONST_FROM_TOP = {"stdClust": 18, "indArr": 11}
    CONST_FROM_LEFT = {"stdClust": 28}
    CONST_EXACT = ("stdNum", "stdString", "stdBool", "stdPath")

    def __init__(self, parts_dir):
        # harvested parts elsewhere (LABVIEWMCP_AIXML_PARTS) carry no hotpoints: this folder's own
        # then - the offsets are LabVIEW's, not a donor's
        def find(name):
            p = os.path.join(parts_dir, name)
            return p if os.path.exists(p) else os.path.join(lvheap.own_parts(), name)
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

    def of(self, rect, prim=None, pos=None, kind=None, hot=None):
        t, l, b, r = rect
        y, x = (t + b) // 2, (l + r) // 2
        if kind and kind.startswith("const:"):
            cls = kind[6:]
            if cls in self.CONST_FROM_RIGHT:
                # the middle of the VALUE box when the constant says where it is - a constant sized
                # to its text (aixml2vi._fit_const) has a wider one; the donor's 13 px box gives
                # the calibrated 7 - and NI's own text-sized numerics sit 7 to 14 in (see above)
                x = l + hot if cls in ("stdNum", "stdString") and isinstance(hot, int) \
                    else r - self.CONST_FROM_RIGHT[cls]
            if cls in self.CONST_FROM_LEFT:
                x = l + self.CONST_FROM_LEFT[cls]
            if cls in self.CONST_FROM_TOP:
                y = t + self.CONST_FROM_TOP[cls]
            return y, x
        if hot:
            x, y = x + hot[0], y + hot[1]
        for k in self.keys(prim, pos, kind):
            if k in self.corr:
                dy, dx = self.corr[k]
                return y + dy, x + dx
        if prim is not None:
            # a position the corpora never showed takes the offset every KNOWN position of the
            # same primitive agrees on: Greater Or Equal?'s output had none and sat 6 px off its
            # inputs' (6, 6) - the one wire LabVIEW rewrote in the ATM's Apply Transaction
            fam = self._family(prim)
            if fam is not None:
                return y + fam[0], x + fam[1]
        return y, x

    def _family(self, prim):
        fams = self.__dict__.setdefault("_fams", {})
        if prim not in fams:
            seen = {v for k, v in self.corr.items() if k.split("/")[0] == prim}
            fams[prim] = seen.pop() if len(seen) == 1 else None
        return fams[prim]

    def exact(self, rect, prim=None, pos=None, kind=None, hot=None):
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
