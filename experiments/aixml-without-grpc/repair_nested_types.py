# -*- coding: utf-8 -*-
"""Repair parts whose typedef types still carry their DONOR's type ids.

    repair_nested_types.py <parts dir> [<parts dir> ...] [--dry-run]

Until 2026-10-10 lvheap.Bundle.tree() inlined a type's children one level deep only. A typedef
writes its wrapped type INLINE (Nested="True"), so that wrapped type's own children stayed
`<TypeDesc TypeID="n"/>` - n an index into the DONOR's flat type list - and a part harvested that
way carried those numbers into every VI composed from it, where they named unrelated types:
LVPositionTypeDef.ctl's Left and Top became two strings and the VI loaded broken (measured on
lvbd_graft_paste.xml). 72 of the 1167 committed parts were affected.

Each affected part is repaired by extracting its donor VI (the part's `source`) once and resolving
the stale ids against the donor's own list, which is exactly what the fixed reader would have
written. A part whose donor is no longer on disk is reported and left as it is; the composer
refuses it by name.
"""
import hashlib
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import lvheap  # noqa: E402

CACHE = os.path.join(os.environ.get("TEMP", HERE), "LabVIEWMCP", "repair-nested")


def stale(nested):
    return nested.get("Nested") == "True" and any(c.tag == "TypeDesc" and c.get("TypeID") is not None
                                                 for c in nested)


def donor(source, cache={}):
    if source not in cache:
        out = os.path.join(CACHE, hashlib.sha1(source.encode("utf-8")).hexdigest()[:16])
        main = os.path.join(out, os.path.splitext(os.path.basename(source))[0] + ".xml")
        if not os.path.exists(main):
            main = lvheap.extract(source, out)
        cache[source] = lvheap.Bundle(main)
    return cache[source]


def repair(path, dry):
    tree = ET.parse(path)
    root = tree.getroot()
    types = root.find("types")
    if types is None:
        return "no-types"
    hits = []
    for t in types:
        parent = {c: p for p in t.iter() for c in p}
        for n in list(t.iter("TypeDesc")):
            if stale(n):
                hits.append((parent.get(n), n))
    if not hits:
        return "clean"
    source = root.get("source") or ""
    if not os.path.exists(source):
        return "donor missing: " + source
    if dry:
        return "would repair %d" % len(hits)
    b = donor(source)
    for p, n in hits:
        fixed = b._inline(n)
        idx = list(p).index(n)
        p.remove(n)
        p.insert(idx, fixed)
    tree.write(path, encoding="utf-8", xml_declaration=True)
    return "repaired %d" % len(hits)


def main(argv):
    dry = "--dry-run" in argv
    dirs = [a for a in argv if not a.startswith("--")]
    totals = {}
    for d in dirs:
        for name in sorted(os.listdir(d)):
            if not name.endswith(".xml"):
                continue
            path = os.path.join(d, name)
            try:
                with open(path, encoding="utf-8") as f:
                    if "Nested=\"True\"" not in f.read():
                        continue
                res = repair(path, dry)
            except Exception as ex:  # one unreadable donor must not stop the rest
                res = "error: %s" % str(ex).splitlines()[0][:200]
            kind = res.split(" ")[0].rstrip(":")
            totals[kind] = totals.get(kind, 0) + 1
            if kind not in ("clean", "no-types"):
                print("%-60s %s" % (name, res))
    print("totals:", totals)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
