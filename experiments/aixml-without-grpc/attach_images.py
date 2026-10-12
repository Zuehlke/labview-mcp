# -*- coding: utf-8 -*-
"""Give the existing parts the embedded images (DSIM) they reference: for every part with a
positive ImageResID and no <images>, re-extract its donor VI and copy the sections in.

    attach_images.py [parts-dir]
"""
import glob
import os
import re
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import harvest_parts  # noqa: E402
import lvheap  # noqa: E402

PROBES = r"C:\temp\aixml-no-grpc\probes"


def donor_path(source):
    m = re.match(r"AIXML-generated probe: (.+)$", source or "")
    if m:
        name = m.group(1)
        name = name[:-4] + ".vi" if name.endswith(".xml") else name
        return os.path.join(PROBES, name)
    return source


def main(argv):
    d = argv[0] if argv else os.path.join(COMPOSER, "parts")
    bundles, fixed, missing = {}, 0, []
    for p in sorted(glob.glob(os.path.join(d, "*.xml"))):
        root = ET.parse(p).getroot() if p.endswith(".xml") else None
        if root is None or root.tag != "part" or root.find("images") is not None:
            continue
        ids = harvest_parts.image_ids(root.find("heap"))
        if not ids:
            continue
        src = donor_path(root.get("source"))
        if not src or not os.path.exists(src):
            missing.append((os.path.basename(p), src))
            continue
        if src not in bundles:
            try:
                bundles[src] = lvheap.Bundle(lvheap.extract(src, tempfile.mkdtemp(prefix="harvest_")))
            except Exception as ex:
                missing.append((os.path.basename(p), "%s (%s)" % (src, ex)))
                continue
        images = harvest_parts.dsim_images(bundles[src], ids)
        if set(images) != set(ids):
            missing.append((os.path.basename(p), "%s lacks DSIM %s" % (src, sorted(set(ids) - set(images)))))
            continue
        im = ET.SubElement(root, "images")
        for i, hx in sorted(images.items()):
            ET.SubElement(im, "image", {"index": str(i)}).text = hx
        ET.ElementTree(root).write(p, encoding="utf-8", xml_declaration=True)
        fixed += 1
    print("%d part(s) given their images" % fixed)
    for name, why in missing:
        print("  NOT FIXED %s: %s" % (name, why))


if __name__ == "__main__":
    main(sys.argv[1:])
