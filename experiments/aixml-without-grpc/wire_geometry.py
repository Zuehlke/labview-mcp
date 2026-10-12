# -*- coding: utf-8 -*-
"""Print every signal of a VI with its compressedWireTable and the geometry of the terminals it
joins - the raw material for decoding the table.

    wire_geometry.py <main.xml of a bundle>

For each terminal: the owner object's bounds (absolute in its diagram), the DCO's termBounds
(relative to the owner) and termHotPoint when present."""
import os
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
COMPOSER = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402

b = lvheap.Bundle(sys.argv[1])
bd = b.bd
parent = {c: p for p in bd.iter() for c in p}
term_of = {}
for t in bd.iter("SL__arrayElement"):
    if t.get("class") in ("term", "fPTerm"):
        term_of[t.get("uid")] = t


def owner_of(t):
    x = parent.get(t)
    while x is not None and not (x.tag == "SL__arrayElement" and x.get("class") not in (None, "term")
                                 and x.find("bounds") is not None and x.get("class") != "fPTerm"):
        x = parent.get(x)
    return x


def geom(t):
    if t.get("class") == "fPTerm":
        return "fPTerm bounds=%s" % t.findtext("bounds")
    dco = t.find("dco")
    if dco is not None and dco.get("class") == "bDConstDCO":
        return "const ddo=%s" % dco.findtext("ddo/bounds")
    o = owner_of(t)
    ob = o.findtext("bounds") if o is not None else None
    if dco is not None and dco.get("class") is None:
        return "inner ref dco=%s (owner %s %s)" % (dco.get("uid"), o.get("class") if o is not None else "?", ob)
    return "%s#%s bounds=%s termBounds=%s hot=%s" % (
        o.get("class") if o is not None else "?", o.get("uid") if o is not None else "?", ob,
        dco.findtext("termBounds") if dco is not None else None,
        dco.findtext("termHotPoint") if dco is not None else None)


for s in bd.iter("SL__arrayElement"):
    if s.get("class") != "signal":
        continue
    print("signal %s table=%s state=%s" % (s.get("uid"), s.findtext("compressedWireTable"), s.findtext("state")))
    for x in s.find("termList"):
        t = term_of.get(x.get("uid"))
        flags = int(t.findtext("objFlags") or 0) if t is not None else 0
        print("   %s %s %s" % (x.get("uid"), "in " if flags & 0x8000 else "   ", geom(t) if t is not None else "?"))
