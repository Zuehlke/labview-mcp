# -*- coding: utf-8 -*-
"""Does NI's export list a primitive's terminals in its slot (parmIndex) order? Over nodes whose
every terminal the reader names, compare NI's listed order with the order of the reader's slots
for the same names. check_term_order.py <corpus dir> [--max N]"""
import collections
import csv
import os
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(1, os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview")))
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402
from compare_corpus import export_of  # noqa: E402


def main(argv):
    corpus = argv[0]
    mx = int(argv[argv.index("--max") + 1]) if "--max" in argv else 300
    xml_dir = os.path.join(corpus, "xml")
    c = collections.Counter()
    bad = []
    n = 0
    for row in csv.DictReader(open(os.path.join(corpus, "roundtrip.tsv"), encoding="utf-8"), delimiter="\t"):
        if row["exportCode"] != "0" or not os.path.exists(row["viPath"]):
            continue
        ref = export_of(xml_dir, row["viPath"])
        if not ref:
            continue
        n += 1
        if n > mx:
            break
        tmp = tempfile.mkdtemp()
        try:
            r = vi2aixml.Reader(lvheap.extract(row["viPath"], tmp), vi_path=row["viPath"])
            r.read()
        except Exception:
            continue
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        ni = {e.get("uid"): e for e in ET.parse(ref).getroot().iter("Node")}
        slots = collections.defaultdict(list)
        for node in r.b.bd.iter("SL__arrayElement"):
            if node.get("uid") not in getattr(r, "prim_rid", {}) or node.find("termList") is None:
                continue
            for t in node.find("termList"):
                o = r.term_owner.get(t.get("uid"))
                if o is None or o is vi2aixml.IGNORED:
                    continue
                pidx = aixml2vi.term_slot(t.find("dco"), node.get("class"))
                slots[node.get("uid")].append((o[2], o[1], int(pidx) if pidx.isdigit() else 999))
        for uid, ts in slots.items():
            e = ni.get(uid)
            if e is None or any(nm.startswith("?") for _, nm, _ in ts):
                continue
            for sink, attr in ((True, "inputs"), (False, "outputs")):
                ni_order = [aixml2vi.unescape(t) for t, _ in aixml2vi.pairs(e.get(attr))]
                mine = sorted([(p, nm) for s, nm, p in ts if s == sink])
                if sorted(nm for _, nm in mine) != sorted(ni_order) or len(ni_order) < 2:
                    continue
                ok = "asc" if [nm for _, nm in mine] == ni_order else ("desc" if [nm for _, nm in mine][::-1] == ni_order else False)
                c[(attr, ok)] += 1
                if not ok and len(bad) < 8:
                    bad.append((e.get("_name"), attr, ni_order, [nm for _, nm in mine]))
    print(c)
    for b in bad:
        print(b)


if __name__ == "__main__":
    main(sys.argv[1:])
