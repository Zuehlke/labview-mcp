# -*- coding: utf-8 -*-
"""Learn the PROPERTY rows of IVI instrument drivers (niRFSA, niRFSG ...) for the composer, from
NI's exports of whole VIs paired with their heaps.

An IVI property is named two ways and NI uses both: the heap keeps the name the node was SHOWN
with (`Model`, `Advanced:NI 5601:Downconverter Center Frequency`), NI's export writes either that
or the long catalogue path (`Inherent IVI Attributes:Instrument Identification:Model`) - for the
same code, in different VIs. NI's CONVERTER takes either and writes it verbatim as PropItemName,
resolving the code itself (probes/pivi-props.xml, 2026-10-30). So a row is keyed by EVERY name
seen for its code, heap and export alike; a name seen with two codes is dropped - one of NI's
own VIs carries a heap item `Frequency Settling Units` with Downconverter Center Frequency's code.

Items are paired with the export's fields by position, top to bottom; that order agreed with the
heap's termList order on every IVI node of the niRFSA examples.

    learn_ivi_props.py [--write]

--write replaces the IVI.* classes in parts/vi-server-props.json and leaves every other row.
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
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(1, COMPOSER)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")
OUT = os.path.join(COMPOSER, "parts", "vi-server-props.json")


def type_xml(tree):
    return ET.tostring(lvheap.strip_label(tree), encoding="unicode")


def heap_tree(b, text):
    m = lvheap.TYPEID_RE.fullmatch((text or "").strip())
    return b.heap_type_tree(int(m.group(1))) if m else None


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    codes = collections.defaultdict(lambda: collections.defaultdict(collections.Counter))   # cls -> name -> code
    facts = collections.defaultdict(dict)       # (cls, code) -> label/type/dataType/access/source
    classes = {}
    vis = 0
    for meta in sorted(glob.glob(os.path.join(CACHE, "*.json"))):
        m = json.load(open(meta, encoding="utf-8"))
        vp, x = m.get("ViPath"), meta[:-5] + ".xml"
        if not vp or not os.path.isfile(vp) or not os.path.exists(x) or "{IVI." not in open(x, encoding="utf-8").read():
            continue
        if m.get("ViSize") not in (None, os.stat(vp).st_size):
            continue
        nodes = {e.get("uid"): e for e in ET.parse(x).getroot().iter("Node")
                 if e.get("_name") == "Property Node" and (e.get("type") or "").startswith("{IVI.")}
        tmp = tempfile.mkdtemp(prefix="livi_")
        try:
            b = lvheap.Bundle(lvheap.extract(vp, tmp))
        except Exception as ex:  # an unreadable VI is skipped
            print("SKIP %s: %s" % (os.path.basename(vp), str(ex).splitlines()[0][:100]), file=sys.stderr)
            continue
        finally:
            pass
        try:
            vis += 1
            for node in (b.bd.iter("SL__arrayElement") if b.bd is not None else []):
                el = nodes.get(node.get("uid"))
                if node.get("class") != "propNode" or el is None:
                    continue
                cls = el.get("type")[1:-1]
                classes.setdefault(cls, {"nodeName": (node.findtext("nodeName") or "").strip('"'),
                                         "oMId": node.findtext("oMId")})
                names = [aixml2vi.unescape(f.split("+", 1)[1]) for f in aixml2vi.split_top(el.get("fields")) if f]
                items = []
                for t in node.find("termList"):
                    d = t.find("dco")
                    if d is None or d.get("class") != "propItem" or d.find("propList") is None:
                        continue
                    if len(d.find("propList")) != 1:
                        continue
                    items.append((lvheap.parse_bounds(d.findtext("termBounds"))[0], d))
                items.sort(key=lambda i: i[0])
                if len(items) != len(names):
                    continue
                for (_, d), exp in zip(items, names):
                    info = d.find("propList")[0]
                    code = int(info.findtext("PropItemCode") or 0)
                    heap = (info.findtext("PropItemName") or "").strip('"')
                    term = heap_tree(b, d.findtext("typeDesc"))
                    data = heap_tree(b, info.findtext("dataTypeDesc"))
                    if not code or term is None:
                        continue
                    for nm in {heap, exp}:
                        codes[cls][nm][code] += 1
                    f = facts[(cls, code)]
                    f.setdefault("labels", collections.Counter())[term.get("Label") or ""] += 1
                    f.setdefault("type", type_xml(term))
                    if data is not None and type_xml(data) != type_xml(term):
                        f.setdefault("dataType", type_xml(data))
                    f.setdefault("access", set()).add("r" if int(d.findtext("objFlags") or 0) & 1 else "w")
                    f.setdefault("source", os.path.basename(vp))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
    table = {}
    dropped = []
    for cls, names in codes.items():
        rows = table.setdefault(cls, {})
        for nm, cnt in sorted(names.items()):
            if len(cnt) != 1:
                dropped.append((cls, nm, dict(cnt)))
                continue
            code = next(iter(cnt))
            f = facts[(cls, code)]
            row = {"code": code, "label": f["labels"].most_common(1)[0][0], "type": f["type"],
                   "declaredBy": cls, "access": "".join(sorted(f["access"])), "source": f["source"]}
            if "dataType" in f:
                row["dataType"] = f["dataType"]
            rows[nm] = row
    print("%d VIs; %s" % (vis, ", ".join("%s: %d names" % (c, len(r)) for c, r in sorted(table.items()))))
    for d in dropped:
        print("  dropped (two codes):", d)
    if "--write" in argv:
        props = json.load(open(OUT, encoding="utf-8"))
        for cls in [c for c in props["classes"] if c.startswith("IVI.")]:
            props["classes"].pop(cls)
            props["props"].pop(cls, None)
        for cls, row in classes.items():
            if table.get(cls):
                props["classes"][cls] = row
                props["props"][cls] = table[cls]
        json.dump(props, open(OUT, "w", encoding="utf-8", newline="\n"), indent=1, sort_keys=True, ensure_ascii=False)
        print("wrote", OUT)


if __name__ == "__main__":
    main(sys.argv[1:])
