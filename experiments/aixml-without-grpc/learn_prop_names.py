# -*- coding: utf-8 -*-
"""Learn the PROPERTY names NI's exporter writes, by code, from its exports of whole VIs.

A property node's heap keeps the name the node was SHOWN with - a short name (`HasConditionalTerm`,
`App.ContextName`, `TSFlatSeq`) or the long one - and its code; NI's export always writes the long
catalogue form (`Has Conditional Terminal?`, `Application:Context Name`, `Embedded Flat Sequence`).
Pairing each property node of the heap with the same uid's `fields` in NI's export, item by item,
gives code path -> NI's name. A code path is kept only when every pair agrees on one name.

    learn_prop_names.py [--max N] [--write]

--write stores the table in parts/property-export-names.json, which vi2aixml.py reads first.
"""
import collections
import glob
import json
import os
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")
OUT = os.path.join(HERE, "parts", "property-export-names.json")


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10 ** 9
    seen = collections.defaultdict(collections.Counter)
    n = pairs = 0
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize):
        meta = x[:-4] + ".json"
        if not os.path.exists(meta):
            continue
        m = json.load(open(meta, encoding="utf-8"))
        vp = m.get("ViPath")
        if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        if m.get("ViSize") not in (None, os.stat(vp).st_size):
            continue
        try:
            ref = ET.parse(x).getroot()
        except ET.ParseError:
            continue
        nodes = {e.get("uid"): e for e in ref.iter("Node") if e.get("_name") == "Property Node" and e.get("fields")}
        if not nodes:
            continue
        tmp = tempfile.mkdtemp(prefix="lpn_")
        try:
            r = vi2aixml.Reader(lvheap.extract(vp, tmp), vi_path=vp)
            r.read()
        except Exception as ex:  # an unreadable VI is skipped
            print("SKIP %s: %s" % (os.path.basename(vp), str(ex).splitlines()[0][:100]), file=sys.stderr)
            continue
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        n += 1
        for uid, codes in r.prop_items.items():
            el = nodes.get(uid)
            if el is None:
                continue
            names = [f.split("+", 1)[-1] for f in el.get("fields").split(",")]
            if len(names) != len(codes):
                continue
            for (cls, c), nm in zip(codes, names):
                if "?" in c or not all(p.lstrip("-").isdigit() for p in c.split("/")):
                    continue
                if all(int(p) > 0xFFFF for p in c.split("/")):
                    # LabVIEW's own property codes (104013829) are global
                    key = c
                elif cls.startswith(("GenClassTag.", "GenClass.", "LV.", "Visa.")):
                    # a small code is unique only within its class: a DAQmx Timing property's, or
                    # {LV.Application}'s old `Project.ActiveProject`, 146
                    key = "%s|%s" % (cls, c)
                else:
                    # an ActiveX or .NET dispatch id - 572 is {LV.VI}'s Block Diagram in 17 pairs
                    # and an Excel Workbooks in 2, and "ActiveX" names no interface
                    continue
                seen[key][aixml2vi.unescape(nm)] += 1
                pairs += 1
        if n >= limit:
            break
    agreed = {c: next(iter(cnt)) for c, cnt in seen.items() if len(cnt) == 1}
    split = {c: dict(cnt) for c, cnt in seen.items() if len(cnt) > 1}
    print("%d VIs, %d property items paired, %d code paths: %d agreed, %d disputed" % (
        n, pairs, len(seen), len(agreed), len(split)))
    for c, cnt in list(split.items())[:10]:
        print("  disputed", c, cnt)
    if "--write" in argv:
        json.dump({"_doc": "property code path -> the name NI's exporter writes for it, learned by "
                           "learn_prop_names.py off its exports of whole VIs; only code paths every "
                           "pair agreed on", "names": dict(sorted(agreed.items()))},
                  open(OUT, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
        print("wrote", OUT)


if __name__ == "__main__":
    main(sys.argv[1:])
