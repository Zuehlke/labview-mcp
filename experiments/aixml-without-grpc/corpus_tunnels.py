# -*- coding: utf-8 -*-
"""How NI's exports write a Tunnel's two sides: per parent tag and _id kind, which of inputs/
outputs are present, and whether wired or empty. corpus_tunnels.py <corpus xml dir>"""
import collections
import glob
import os
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts", "aixml-pylabview"))
import aixml2vi  # noqa: E402


def state(el, a):
    v = el.get(a)
    if v is None:
        return "-"
    nets = [n for _, n in aixml2vi.pairs(v)]
    return "wired" if any(nets) else "empty"


c = collections.Counter()
for f in glob.glob(os.path.join(sys.argv[1], "*.xml")):
    try:
        root = ET.parse(f).getroot()
    except ET.ParseError:
        continue
    parent = {ch: p for p in root.iter() for ch in p}
    for t in root.iter("Tunnel"):
        p = parent.get(t)
        ptag = p.tag if p is not None else "?"
        if ptag == "Structure":
            ptag += ":" + (p.get("_name") or "")
        kind = (t.get("_id") or "?").rstrip("0123456789")
        c[(ptag, kind, t.get("mode") or "", state(t, "inputs"), state(t, "outputs"))] += 1
for k, v in sorted(c.items(), key=lambda kv: -kv[1])[:60]:
    print("%6d  %s" % (v, k))
