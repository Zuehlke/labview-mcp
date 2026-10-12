# -*- coding: utf-8 -*-
"""Learn the property names NI's export WRITES from the names the heap STORES, by pairing the
two for the same VI: the export's Property Node `fields`, in order, against the same node's
propItem terminals in the heap. The heap keeps the name the node was saved with - "BDWin.Open",
"Diagram", "Panel" - while the export writes LabVIEW's catalogue name - "Block Diagram
Window:Open", "Block Diagram", "Front Panel". Each pair becomes an ALIAS row in
parts/vi-server-props.json: the catalogue name, carrying the heap's row and its `heapName`, so
the composer accepts what NI exports and writes into the heap what LabVIEW stores.

    alias_props.py [--max N] [--write] [--cache <dir>] [--parts <dir>]

METHODS TOO, since Phase 3: NI's export writes an Invoke Node's `target` as AIXML spells it
("Save VI Icon to File"), the heap stores `methName` as LabVIEW does ("VI Icon.Save to File"), and
invoke parts are filed under the heap name - so a composer looking up the AIXML spelling found
nothing. Each pair becomes an alias row in `methods`, carrying the heap row and its `heapName`.
--cache reads export/VI pairs from another folder laid out like the export cache; --parts writes
another parts folder's table.
"""
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

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def heap_items(node):
    """[(heap name, code)] of a propNode's propItem terminals, top to bottom."""
    out = []
    for t in node.find("termList"):
        d = t.find("dco")
        if d is None or d.get("class") != "propItem":
            continue
        infos = d.findall("propList/SL__arrayElement")
        name = ".".join((i.findtext("PropItemName") or "").strip('"') for i in infos)
        code = infos[-1].findtext("PropItemCode") if infos else None
        out.append((name, int(code) if code and code.lstrip("-").isdigit() else 0))
    return out


def main(argv):
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else None
    pairs, seen_vis, mism = {}, 0, 0
    method_pairs = {}
    cache = argv[argv.index("--cache") + 1] if "--cache" in argv else CACHE
    for j in sorted(glob.glob(os.path.join(cache, "*.json"))):
        xml = j[:-5] + ".xml"
        if not os.path.exists(xml):
            continue
        text = open(xml, encoding="utf-8", errors="replace").read()
        if '_name="Property Node"' not in text and '_name="Invoke Node"' not in text:
            continue
        vp = json.load(open(j, encoding="utf-8")).get("ViPath")
        if not vp or not os.path.exists(vp):
            continue
        if limit is not None and seen_vis >= limit:
            break
        seen_vis += 1
        try:
            exp = ET.fromstring(text)
        except ET.ParseError:
            continue
        want = {}
        want_methods = {}
        for n in exp.iter("Node"):
            if n.get("_name") == "Invoke Node" and n.get("target") and n.get("type", "").startswith("{LV."):
                want_methods[n.get("uid")] = (n.get("type")[1:-1], aixml2vi.unescape(n.get("target")))
            if n.get("_name") == "Property Node" and n.get("fields") and n.get("type", "").startswith("{LV."):
                want[n.get("uid")] = (n.get("type")[1:-1],
                                      [aixml2vi.unescape(f.split("+", 1)[1]) for f in aixml2vi.split_top(n.get("fields")) if "+" in f])
        tmp = tempfile.mkdtemp(prefix="alias_")
        try:
            main_xml = lvheap.extract(vp, tmp)
            bd = [f for f in os.listdir(tmp) if f.endswith("_BDHb.xml")]
            if not bd:
                continue
            heap = ET.parse(os.path.join(tmp, bd[0])).getroot()
        except Exception:  # noqa: BLE001 - a VI pylabview cannot read teaches nothing
            continue
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        for node in heap.iter("SL__arrayElement"):
            if node.get("class") == "invokeNode" and node.get("uid") in want_methods:
                cls, target = want_methods[node.get("uid")]
                stored = (node.findtext("methName") or "").strip('"')
                if stored and stored != target:
                    method_pairs.setdefault((cls, target), set()).add(stored)
                continue
            if node.get("class") != "propNode" or node.get("uid") not in want:
                continue
            cls, longs = want[node.get("uid")]
            items = heap_items(node)
            if len(items) != len(longs):
                mism += 1
                continue
            for long_name, (short, code) in zip(longs, items):
                if long_name != short:
                    pairs.setdefault((cls, long_name), set()).add((short, code))
    print("%d VIs read, %d node(s) with a field count that did not match" % (seen_vis, mism))
    parts_dir = argv[argv.index("--parts") + 1] if "--parts" in argv else os.path.join(COMPOSER, "parts")
    path = os.path.join(parts_dir, "vi-server-props.json")
    table = json.load(open(path, encoding="utf-8"))
    props = table["props"]
    added = 0
    for (cls, long_name), cands in sorted(pairs.items()):
        if len(cands) != 1:
            print("ambiguous %s %s: %s" % (cls, long_name, sorted(cands)))
            continue
        short, code = next(iter(cands))
        row = props.get(cls, {}).get(short)
        if row is None:
            row = next((r for rows in props.values() for n, r in rows.items() if n == short and r.get("code") == code), None)
        if row is None or row.get("code") != code:
            print("no row for %s %s (heap %s, code %s)" % (cls, long_name, short, code))
            continue
        have = props.setdefault(cls, {}).get(long_name)
        if have is not None:
            # a name already in the table was harvested off a heap that STORES it, so LabVIEW
            # takes it as it is ("VI Name" from our own probes, where NI's VIs often say "Name")
            if have.get("code") != code:
                print("conflict %s %s: table code %s, heap %s" % (cls, long_name, have.get("code"), code))
            continue
        props[cls][long_name] = dict(row, heapName=short)
        added += 1
        print("alias %s %r -> %r (%s)" % (cls, long_name, short, code))
    print("%d alias row(s)" % added)
    methods = table.setdefault("methods", {})
    m_added = 0
    for (cls, target), stored in sorted(method_pairs.items()):
        if len(stored) != 1:
            print("ambiguous method %s %s: %s" % (cls, target, sorted(stored)))
            continue
        heap_name = next(iter(stored))
        row = methods.get(cls, {}).get(heap_name)
        if row is None:
            print("no method row for %s %s (heap %s)" % (cls, target, heap_name))
            continue
        if target in methods.setdefault(cls, {}):
            continue
        methods[cls][target] = dict(row, heapName=heap_name)
        m_added += 1
        print("method alias %s %r -> %r" % (cls, target, heap_name))
    print("%d method alias row(s)" % m_added)
    if "--write" in argv:
        json.dump(table, open(path, "w", encoding="utf-8"), indent=1, sort_keys=True)
        print("wrote %s" % path)


if __name__ == "__main__":
    main(sys.argv[1:])
