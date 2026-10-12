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
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
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


def unlabelled(tree):
    t = ET.fromstring(ET.tostring(tree))
    for e in t.iter():
        e.attrib.pop("Label", None)
        for k in [k for k in e.attrib if k.startswith("ref.")]:
            del e.attrib[k]
    return ET.tostring(t)


def donor_has_property(b, name, tree):
    """Does the donor's diagram hold a property item `name` whose data type is `tree`? The item
    stores its data type BARE - LVBoundsTypeDef.ctl's cluster, without the typedef - while the
    table records the terminal's type, the typedef around it; either matches."""
    want = {unlabelled(tree)}
    if tree.get("Type") == "TypeDef":
        inner = next((c for c in tree if c.tag == "TypeDesc"), None)
        if inner is not None:
            inner = ET.fromstring(ET.tostring(inner))
            inner.attrib.pop("Nested", None)
            want.add(unlabelled(inner))
    for heap in (b.bd, b.fp):
        if heap is None:
            continue
        for info in heap.iter("SL__arrayElement"):
            if info.get("class") != "propItemInfo" or (info.findtext("PropItemName") or "").strip('"') != name:
                continue
            m = lvheap.TYPEID_RE.fullmatch((info.findtext("dataTypeDesc") or "").strip())
            if m and unlabelled(b.heap_type_tree(int(m.group(1)))) in want:
                return True
    return False


def repair_props(path, dry):
    """vi-server-props.json: every row's `type` string was written by the same reader, so a
    typedef in it carries its donor's ids as well - 109 of 1130 typed rows in the committed table
    (Bounds, Position ...). Resolved against the row's own donor (`source`), like a part."""
    import json
    table = json.load(open(path, encoding="utf-8"))
    counts = {}
    for section in ("props", "methods"):
        for cls, rows in table.get(section, {}).items():
            for name, row in rows.items():
                v = row.get("type")
                if not isinstance(v, str) or 'Nested="True"' not in v:
                    continue
                tree = ET.fromstring(v)
                parent = {c: p for p in tree.iter() for c in p}
                hits = [n for n in tree.iter("TypeDesc") if stale(n)]
                if not hits:
                    continue
                source = row.get("source") or ""
                if not os.path.exists(source):
                    counts["donor missing"] = counts.get("donor missing", 0) + 1
                    print("%-50s donor missing: %s" % ("%s %s" % (cls, name), source))
                    continue
                b = donor(source)
                for n in hits:
                    fixed = b._inline(n)
                    if n is tree:
                        tree = fixed
                    else:
                        p = parent[n]
                        i = list(p).index(n)
                        p.remove(n)
                        p.insert(i, fixed)
                # Verified, not trusted: the donor must hold this property with exactly this type,
                # or the row's `source` is not the VI its type was read from and the ids would
                # resolve against the wrong list. Such a row is left as it is and reported.
                if section == "props" and not donor_has_property(b, row.get("heapName") or name, tree):
                    counts["unverified"] = counts.get("unverified", 0) + 1
                    print("%-50s left: %s holds no %r of that type" % ("%s %s" % (cls, name),
                                                                     os.path.basename(source), name))
                    continue
                counts["repaired"] = counts.get("repaired", 0) + 1
                if not dry:
                    row["type"] = ET.tostring(tree, encoding="unicode")
    if counts.get("repaired") and not dry:
        # the format harvest_parts.py writes it in
        json.dump(table, open(path, "w", encoding="utf-8", newline="\n"), indent=1, sort_keys=True,
                  ensure_ascii=False)
    print("%s: %s" % (path, counts or "clean"))


def main(argv):
    dry = "--dry-run" in argv
    dirs = [a for a in argv if not a.startswith("--")]
    totals = {}
    for d in dirs:
        props = os.path.join(d, "vi-server-props.json")
        if os.path.exists(props):
            try:
                repair_props(props, dry)
            except Exception as ex:  # report and go on with the parts
                print("%s: error %s" % (props, str(ex).splitlines()[0][:200]))
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
