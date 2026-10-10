# -*- coding: utf-8 -*-
"""Compare vi2aixml.py's export of a VI with LabVIEW's OWN AIXML export of it - the WHOLE VI,
nested structures included (compare_with_nigel.py compares only the top-level subset).

    compare_full.py [--max N] [--show N] [--only REGEX]

Facts compared per VI, each as a set, so order does not matter:

  elements   (tag, uid, uid_parent, name-ish, type)   name-ish is _name, else target, else _id
  attrs      (uid, attribute, value) for the attributes that carry meaning on their own:
             selector, fields, target, text, element, link, type on nodes, count/maxin/maxout
             presence, cond, mode
  values     (uid, value) for constants
  edges      (sink uid, sink terminal) -> (source uid, source terminal)

A VI counts as IDENTICAL when all four sets match. The cache entry is used only while its
recorded file size and timestamp still match the VI on disk.
"""
import glob
import json
import os
import re
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")
ATTRS = ("selector", "fields", "target", "text", "element", "link", "cond", "mode", "selectin")


def facts(root):
    els, attrs, vals, edges = set(), set(), set(), set()
    src_of = {}
    all_els = [e for e in root.iter() if isinstance(e.tag, str) and e is not root]
    for el in all_els:
        for term, net in aixml2vi.pairs(el.get("outputs")):
            if net:
                src_of.setdefault(net, (el.get("uid"), term))
    for el in all_els:
        if el.tag == "FreeLabel":
            continue
        name = aixml2vi.unescape(el.get("_name") or el.get("target") or el.get("_id") or "")
        typ = el.get("type") if el.tag in ("Control", "Indicator", "Constant", "Node") else None
        els.add((el.tag, el.get("uid"), el.get("uid_parent"), name, typ))
        for a in ATTRS:
            if el.get(a) is not None:
                attrs.add((el.get("uid"), a, aixml2vi.unescape(el.get(a))))
        if el.tag == "Constant":
            vals.add((el.get("uid"), el.get("value")))
        for term, net in aixml2vi.pairs(el.get("inputs")):
            if net and net in src_of:
                edges.add((el.get("uid"), term, src_of[net]))
    return els, attrs, vals, edges


def main(argv):
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10 ** 9
    show = int(argv[argv.index("--show") + 1]) if "--show" in argv else 0
    only = re.compile(argv[argv.index("--only") + 1]) if "--only" in argv else None
    tot = {k: [0, 0] for k in ("elements", "attrs", "values", "edges")}
    same = n = 0
    shown = 0
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize):
        meta = x[:-4] + ".json"
        if not os.path.exists(meta):
            continue
        m = json.load(open(meta, encoding="utf-8"))
        vp = m.get("ViPath")
        if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        if only and not only.search(vp):
            continue
        st = os.stat(vp)
        if m.get("ViSize") not in (None, st.st_size):
            continue
        try:
            ref = ET.parse(x).getroot()
            mine = vi2aixml.Reader(lvheap.extract(vp, tempfile.mkdtemp(prefix="cmp_"))).read()
        except Exception as ex:   # an unreadable VI is counted, not fatal
            print("SKIP %s: %s" % (os.path.basename(vp), str(ex).splitlines()[0][:100]), file=sys.stderr)
            continue
        a, b = facts(ref), facts(mine)
        ok = True
        for k, fa, fb in zip(("elements", "attrs", "values", "edges"), a, b):
            tot[k][0] += len(fa & fb)
            tot[k][1] += len(fa)
            if fa != fb:
                ok = False
        same += ok
        n += 1
        if not ok and shown < show:
            shown += 1
            print("== %s" % vp)
            for k, fa, fb in zip(("elements", "attrs", "values", "edges"), a, b):
                for f in sorted(fa - fb, key=str)[:6]:
                    print("   missing %s %s" % (k, f))
                for f in sorted(fb - fa, key=str)[:4]:
                    print("   extra   %s %s" % (k, f))
        if n >= limit:
            break
    print("%d of %d VIs identical in every fact" % (same, n))
    for k, (hit, all_) in tot.items():
        print("  %-9s %d of %d (%.1f %%)" % (k, hit, all_, 100.0 * hit / max(1, all_)))


if __name__ == "__main__":
    main(sys.argv[1:])
