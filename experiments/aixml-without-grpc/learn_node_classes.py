# -*- coding: utf-8 -*-
"""Learn the VI Server CLASS NI's exporter writes on a property or invoke node (`type="{LV.XYGraph}"`,
`{GenClassTag.DAQmx Channel.NIDAQ}`, `{ActiveX}`), against what the heap holds: the reference
input's refnum (RefType, CtlFlags, Ident, TypeName) and the node's own nodeName.

    learn_node_classes.py [--max N] [--write]

Prints every (heap evidence -> NI's class) pair with its count. --write stores the CtlFlags ->
class rows no refnum-classes.tsv row has (each agreed by every pair) in parts/class-ids-learned.json,
which vi2aixml.py reads beside that table.
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
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")
OUT = os.path.join(HERE, "parts", "class-ids-learned.json")


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10 ** 9
    seen = collections.Counter()
    by_flags = collections.defaultdict(collections.Counter)
    by_name = collections.defaultdict(collections.Counter)
    n = 0
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize):
        meta = x[:-4] + ".json"
        if not os.path.exists(meta):
            continue
        m = json.load(open(meta, encoding="utf-8"))
        vp = m.get("ViPath")
        if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        try:
            ref = ET.parse(x).getroot()
        except ET.ParseError:
            continue
        nodes = {e.get("uid"): e.get("type") for e in ref.iter("Node")
                 if e.get("_name") in ("Property Node", "Invoke Node") and e.get("type")}
        if not nodes:
            continue
        tmp = tempfile.mkdtemp(prefix="lnc_")
        try:
            r = vi2aixml.Reader(lvheap.extract(vp, tmp), vi_path=vp)
        except Exception:
            continue
        finally:
            pass
        try:
            for nd in r.b.bd.iter("SL__arrayElement"):
                if nd.get("class") not in ("propNode", "invokeNode") or nd.get("uid") not in nodes:
                    continue
                rt = None
                for t in nd.find("termList"):
                    d = t.find("dco")
                    if d is not None and d.get("class") == "hGrowCItem" and int(d.findtext("objFlags") or 0) == 0x10000:
                        rt = r.tid_tree(t)
                key = (rt.get("RefType") if rt is not None else None,
                       rt.get("CtlFlags") if rt is not None else None,
                       rt.get("Ident") if rt is not None else None,
                       rt.get("TypeName") if rt is not None else None,
                       (nd.findtext("nodeName") or "").strip('"') or None,
                       nd.find("ddo") is not None)
                seen[(key, nodes[nd.get("uid")])] += 1
                if key[0] == "LVObjCtl" and key[1]:
                    by_flags[key[1].upper()][nodes[nd.get("uid")]] += 1
                if key[0] is None and key[4]:
                    # no reference input - an implicit node on a control, This VI, the app: the
                    # node's own name is all the heap says (`WFChart (strict)` -> LV.WaveformChart)
                    by_name[key[4].replace(" (strict)", "")][nodes[nd.get("uid")]] += 1
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        n += 1
        if n >= limit:
            break
    for (key, cls), c in sorted(seen.items(), key=lambda kv: -kv[1]):
        if key[0] != "LVObjCtl" or vi2aixml.ref_class_name(key[1]) is None or "{%s}" % vi2aixml.ref_class_name(key[1]) != cls:
            print("%4d  %-60s -> %s" % (c, key, cls))
    learned = {}
    for flags, cnt in by_flags.items():
        if len(cnt) == 1 and vi2aixml.ref_class_name(flags) is None:
            cls = next(iter(cnt)).strip("{}")
            if cls.startswith("LV."):
                learned[flags] = cls
    names = {nm: next(iter(cnt)).strip("{}") for nm, cnt in by_name.items() if len(cnt) == 1}
    print("%d VIs; CtlFlags learned beyond refnum-classes.tsv: %s" % (n, learned))
    print("node names of reference-less nodes: %s; disputed: %s" % (
        names, {nm: dict(c) for nm, c in by_name.items() if len(c) > 1}))
    if "--write" in argv:
        json.dump({"_doc": "VI Server class of a property/invoke node, learned by learn_node_classes.py from "
                           "NI's exports: by CtlFlags for classes refnum-classes.tsv lacks, and by the "
                           "node's own name where it has no reference input",
                   "classes": dict(sorted(learned.items())),
                   "nodeNames": dict(sorted(names.items()))},
                  open(OUT, "w", encoding="utf-8", newline="\n"), indent=1)
        print("wrote", OUT)


if __name__ == "__main__":
    main(sys.argv[1:])
