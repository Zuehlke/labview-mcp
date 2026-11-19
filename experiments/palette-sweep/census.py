# -*- coding: utf-8 -*-
"""Usage census of diagram and panel elements over NI's shipping examples.

Reads every cached AIXML export of an example (%USERPROFILE%\\.labviewmcp\\cache\\aixml, filled
by the server's ReaderPrewarm) and counts, per element KEY, in how many example VIs it occurs
and how often in total. A key is:
  node:<_name>            a primitive Node
  call:<target>           a subVI Call
  struct:<tag>            a structure element (While Loop, Case Structure, ...)
  fixed:<_name>           a FixedConst
  panel:<style>           a Control/Indicator style (no style: panel:<type kind>)
  type:<kind>             a Control/Indicator/Constant type kind (array, cluster, ref{...} class)

    python census.py <out.json>
"""
import collections
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

CACHE = os.path.join(os.environ["USERPROFILE"], ".labviewmcp", "cache", "aixml")
LEAF = {"Control", "Indicator", "Constant", "Node", "Call", "FixedConst", "FreeLabel", "VI",
        "Tunnel", "ShiftRegister", "Terminal"}


def type_kind(t):
    t = t or ""
    m = re.match(r"(ref|array|cluster)\{([^}.]*)", t)
    if m and m.group(1) == "ref":
        return "ref{%s}" % m.group(2)
    if m:
        return m.group(1)
    return t.split(".")[0]


def main(out):
    vis = collections.Counter()
    inst = collections.Counter()
    where = collections.defaultdict(list)
    n = 0
    for meta in glob.glob(os.path.join(CACHE, "*.json")):
        try:
            m = json.load(open(meta, encoding="utf-8"))
        except ValueError:
            continue
        p = m.get("ViPath", "")
        if "\\examples\\" not in p.lower():
            continue
        xml = meta[:-5] + ".xml"
        try:
            root = ET.parse(xml).getroot()
        except (ET.ParseError, OSError):
            continue
        n += 1
        seen = collections.Counter()
        for el in root.iter():
            tag = el.tag
            if tag == "Node":
                k = "node:" + (el.get("_name") or "?")
            elif tag == "Call":
                k = "call:" + (el.get("target") or el.get("_name") or "?")
            elif tag == "FixedConst":
                k = "fixed:" + (el.get("_name") or "?")
            elif tag in ("Control", "Indicator"):
                st = el.get("style")
                seen["type:" + type_kind(el.get("type"))] += 1
                k = "panel:" + (re.sub(r"\d+$", "", st) if st else type_kind(el.get("type")))
            elif tag == "Constant":
                k = "type:" + type_kind(el.get("type"))
            elif tag == "Structure":
                k = "struct:" + (el.get("_name") or "?")
            elif tag not in LEAF:
                k = "sub:" + tag
            else:
                continue
            seen[k] += 1
        for k, c in seen.items():
            vis[k] += 1
            inst[k] += c
            if len(where[k]) < 3:
                where[k].append(p)
    rows = [{"key": k, "vis": vis[k], "instances": inst[k], "examples": where[k]}
            for k in sorted(vis, key=lambda k: (-vis[k], -inst[k], k))]
    json.dump({"exampleVis": n, "rows": rows}, open(out, "w", encoding="utf-8"), indent=1)
    print("%d example VIs, %d keys" % (n, len(rows)))


if __name__ == "__main__":
    main(sys.argv[1])
