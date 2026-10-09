# -*- coding: utf-8 -*-
"""What an AIXML export uses: node names, structure kinds, control/constant type families.
Used to compare the composed NI VIs that load broken with the ones that work.

    features.py <composed.txt> <cov_load output>   (lines "  cov_NNNN.vi  STATE  path")
"""
import collections
import re
import sys
import xml.etree.ElementTree as ET


def family(t):
    t = t or ""
    if t.startswith("array"):
        return "array{%s}" % family(re.sub(r"^array(\.\d+)?\{(.*)\}$", r"\2", t).split(".")[0] if "{" in t else "")
    if t.startswith("cluster"):
        return "cluster"
    if "{" in t:
        return t.split("{")[0] + "{enum}"
    return t


def feats(xml):
    out = set()
    for e in ET.parse(xml).getroot().iter():
        if e.tag in ("Node", "Structure", "Call"):
            out.add("%s:%s" % (e.tag, e.get("_name") or e.get("target")))
        elif e.tag in ("Control", "Indicator", "Constant"):
            out.add("%s:%s" % (e.tag, family(e.get("type"))))
        elif e.tag == "Tunnel" and e.get("mode"):
            out.add("Tunnel:" + e.get("mode"))
    return out


def main(argv):
    composed = dict(l.rstrip("\n").split("\t") for l in open(argv[0], encoding="utf-8") if l.strip())
    broken = set(re.findall(r"(cov_\d+\.vi)\s+0\s", open(argv[1], encoding="utf-8").read()))
    good_f, bad_f = collections.Counter(), collections.Counter()
    for vi, src in composed.items():
        name = vi.split("\\")[-1]
        (bad_f if name in broken else good_f).update(feats(src))
    print("features seen ONLY in broken VIs (count):")
    for f, n in bad_f.most_common():
        if f not in good_f:
            print("  %3d  %s" % (n, f))


if __name__ == "__main__":
    main(sys.argv[1:])
