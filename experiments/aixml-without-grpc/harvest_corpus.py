# -*- coding: utf-8 -*-
"""Pick donor VIs from the AIXML export cache and harvest their parts.

    harvest_corpus.py [--max N] [--dry]

The cache (%USERPROFILE%\\.labviewmcp\\cache\\aixml, written by lvai_convert_vi_to_aixml) is
used ONLY as an index - it says which installation VI contains which primitive and which
control type, without opening anything. For every primitive name and every scalar control,
indicator and constant type, the SMALLEST VI that contains it is chosen as the donor, so a
part is cut from the least cluttered diagram available. The parts themselves come from the
VI file through pylabview; no LabVIEW and no gRPC call is made here.
"""
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402
import harvest_parts  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def main(argv):
    dry = "--dry" in argv
    prim_by_name, _ = aixml2vi.load_names()
    wants = {}   # want-key -> (size, vi path)
    for xml_path in glob.glob(os.path.join(CACHE, "*.xml")):
        meta = xml_path[:-4] + ".json"
        if not os.path.exists(meta):
            continue
        vi_path = json.load(open(meta, encoding="utf-8")).get("ViPath") or ""
        if ".llb\\" in vi_path.lower() or not vi_path.lower().endswith((".vi", ".vit", ".ctl")):
            continue
        if not os.path.exists(vi_path):
            continue
        try:
            root = ET.parse(xml_path).getroot()
        except ET.ParseError:
            continue
        size = os.path.getsize(xml_path)
        for el in root.iter():
            keys = []
            if el.tag == "Node" and el.get("_name") in prim_by_name:
                keys.append("prim:" + el.get("_name"))
            elif el.tag in ("Control", "Indicator", "Constant") and el.get("type") in aixml2vi.SCALARS:
                keys.append("%s:%s" % (el.tag, el.get("type")))
            for k in keys:
                if k not in wants or size < wants[k][0]:
                    wants[k] = (size, vi_path)
    donors = sorted({v for _, v in wants.values()})
    print("%d wanted keys, %d donor VIs" % (len(wants), len(donors)), file=sys.stderr)
    if dry:
        for k in sorted(wants):
            print("%-40s %6d %s" % (k, wants[k][0], wants[k][1]))
        return
    harvest_parts.main([os.path.join(HERE, "parts")] + donors)


if __name__ == "__main__":
    main(sys.argv[1:])
