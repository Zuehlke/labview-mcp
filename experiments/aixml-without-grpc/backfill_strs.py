# -*- coding: utf-8 -*-
"""Give parts harvested before 2026-10-10 the STR sections their <variantIndex> names
(harvest_parts.py keeps them since then), read again from the donor named in source=.

    backfill_strs.py [parts-dir]
"""
import os
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import harvest_parts  # noqa: E402
import lvheap  # noqa: E402


def donor_main(source, cache):
    if source in cache:
        return cache[source]
    main = None
    if source.lower().endswith(".xml") and os.path.exists(source):
        main = ET.parse(source).getroot()
    elif source.lower().endswith(".vi") and os.path.exists(source):
        d = tempfile.mkdtemp(prefix="backfill_")
        try:
            main = ET.parse(lvheap.extract(source, d)).getroot()
        except Exception as e:  # noqa: BLE001 - a donor pylabview cannot read stays unfilled
            print("  cannot read %s: %s" % (source, e))
        finally:
            shutil.rmtree(d, ignore_errors=True)
    cache[source] = main
    return main


def main(argv):
    d = argv[0] if argv else os.path.join(COMPOSER, "parts")
    cache = {}
    for fn in sorted(os.listdir(d)):
        if not fn.endswith(".xml"):
            continue
        p = os.path.join(d, fn)
        root = ET.parse(p).getroot()
        if root.tag != "part" or root.find("strs") is not None:
            continue
        ids = harvest_parts.variant_ids(root.find("heap"))
        if not ids:
            continue
        m = donor_main(root.get("source"), cache)
        blk = m.find("STR") if m is not None else None
        secs = [s for s in (blk.findall("Section") if blk is not None else []) if int(s.get("Index")) in ids]
        if not secs:
            print("%s: no STR section %s in its donor" % (fn, ids))
            continue
        st = ET.SubElement(root, "strs")
        for s in secs:
            st.append(s)
        ET.ElementTree(root).write(p, encoding="utf-8", xml_declaration=True)
        print("%s: STR %s" % (fn, [s.get("Index") for s in secs]))


if __name__ == "__main__":
    main(sys.argv[1:])
