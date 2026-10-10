# -*- coding: utf-8 -*-
"""Cut the `Register For Events` node (eventRegNode) out of a VI into a part.

    harvest_event_reg.py <parts dir> <vi> [<source note>]

The harvester proper does not know the class. The part this repository carries came from
LabVIEW's own SAVE of aixml-skeletons/user-event-two-loops.xml as finished by NI's converter,
lvai_wire_dynamic_events and lvai_set_event_data_fields (2026-10-10) - a node LabVIEW wrote itself.
"""
import os
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import harvest_parts  # noqa: E402
import lvheap  # noqa: E402


def main(argv):
    parts_dir, vi = argv[0], argv[1]
    note = argv[2] if len(argv) > 2 else vi
    work = tempfile.mkdtemp(prefix="evreg_")
    try:
        copy = os.path.join(work, "x.vi")
        shutil.copyfile(vi, copy)
        b = lvheap.Bundle(lvheap.extract(copy, work))
        node = next((n for n in b.bd.iter("SL__arrayElement") if n.get("class") == "eventRegNode"), None)
        if node is None:
            print("no eventRegNode in %s" % vi)
            return 1
        key = "node/eventReg"
        part = harvest_parts.part_xml("node", key, note, node, harvest_parts.resolve_types(b, node))
        path = os.path.join(parts_dir, key.replace("/", "__").replace(".", "_") + ".xml")
        ET.ElementTree(part).write(path, encoding="utf-8", xml_declaration=True)
        print("wrote", path)
        return 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
