# -*- coding: utf-8 -*-
"""Diff two heap/main XML files after normalising: whitespace dropped, comments dropped,
uids renumbered in order of first appearance. Shows what really differs between a composed
bundle and LabVIEW's own."""
import difflib, re, sys
import xml.etree.ElementTree as ET


def lines(path):
    root = ET.parse(path).getroot()
    seen = {}

    def u(v):
        return seen.setdefault(v, "U%d" % len(seen))
    out = []

    def walk(e, d):
        attrs = " ".join('%s="%s"' % (k, u(v) if k == "uid" else v) for k, v in sorted(e.attrib.items())
                         if k not in ("File", "Name"))
        t = (e.text or "").strip()
        out.append("%s<%s %s>%s" % ("  " * d, e.tag, attrs, t))
        for c in e:
            walk(c, d + 1)
    walk(root, 0)
    return out


a, b = lines(sys.argv[1]), lines(sys.argv[2])
sys.stdout.writelines(l + "\n" for l in difflib.unified_diff(a, b, sys.argv[1], sys.argv[2], n=1))
