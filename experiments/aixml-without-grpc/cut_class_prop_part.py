# -*- coding: utf-8 -*-
"""Cut a Property Node on a LabVIEW CLASS wire - an LVOOP property, a class's Property Definition
read or written through its accessor VI - as part node/udclass/prop.

    cut_class_prop_part.py <parts-dir> <host vi> [--replace]

Cut off NI's SFTP List Directory.vi (2026-11-24): a propNode typed by a UDClassInst refnum
(File Attributes.lvclass), nodeName the class name, oMId 000D, each propItem carrying a
udClassPropItemPrivInfo. Issue #153.
"""
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(0, COMPOSER)
import harvest_parts  # noqa: E402
import lvheap  # noqa: E402


def main(argv):
    parts_dir, vi = argv[0], argv[1]
    b = lvheap.Bundle(lvheap.extract(vi, tempfile.mkdtemp(prefix="classprop_")))
    h = harvest_parts.Harvester(parts_dir, replace="--replace" in argv)
    h.bundle = b
    source = lvheap.portable_source(vi)
    for e in b.bd.iter("SL__arrayElement"):
        if e.get("class") == "propNode" and any(
                p.get("class") == "udClassPropItemPrivInfo" for p in e.iter("SL__arrayElement")):
            h.save("node", "node/udclass/prop", source, e, harvest_parts.resolve_types(b, e))
            break
    for k in h.found:
        print(k)


if __name__ == "__main__":
    main(sys.argv[1:])
