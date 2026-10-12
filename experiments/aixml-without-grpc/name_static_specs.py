# -*- coding: utf-8 -*-
"""Name the event types scan_static_specs.py found, with NI's exporter (2026-10-11).

    name_static_specs.py <scan.jsonl> <out.json> [--max-vis N]

For each (type, source) the scan saw, one VI that handles it is exported through the built server
(lvai_convert_vi_to_aixml, backend grpc) and the frame holding the spec is read: its selector's
items are in EventSpec order, so the spec's position in the frame names it. A type that two VIs
name differently is reported, not resolved.
"""
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
from aixml2vi import event_items, unescape  # noqa: E402
import event_frame_data  # noqa: E402
import helper_parity as hp  # noqa: E402


def main(argv):
    hits = [json.loads(l) for l in open(argv[0], encoding="utf-8")]
    out_path = argv[1]
    by_key = {}
    for h in hits:
        s = h["spec"]
        by_key.setdefault((s["type"], s["source"]), []).append(h)
    names = {}
    server = hp.Server()
    tmp = tempfile.mkdtemp(prefix="namespecs_")
    try:
        for (typ, src), hs in sorted(by_key.items()):
            got = {}
            for h in hs[:2]:
                x = os.path.join(tmp, "e%d.xml" % len(os.listdir(tmp)))
                body = server.tool("lvai_convert_vi_to_aixml", {"viPath": h["vi"], "aiXmlFilePath": x,
                                                                "returnContent": False, "backend": "grpc"})
                if body.get("errorCode") != 0 or not os.path.exists(x):
                    continue
                root = ET.parse(x).getroot()
                st = next((e for e in root.iter("Structure") if e.get("_name") == "Event Structure"
                           and e.get("uid") == h["uid"]), None)
                if st is None:
                    continue
                frames = [f for f in st if f.tag == "CaseFrame"]
                if h["frame"] >= len(frames):
                    continue
                items = event_items(unescape(frames[h["frame"]].get("selector") or ""))
                # the spec's position among ALL the frame's specs is its item - the scan kept only
                # the unknown ones, so the frame is read again
                specs = next((f["specs"] for rec in event_frame_data.scan(h["vi"], False) if rec["uid"] == h["uid"]
                              for f in rec["frames"] if f["idx"] == h["frame"]), [])
                pos = [i for i, s in enumerate(specs) if s.get("type") == typ and s.get("source") == src]
                item = items[pos[0]] if pos and pos[0] < len(items) else (items[0] if len(items) == 1 else " | ".join(items))
                got[item.split(":")[-1].strip() if ":" in item else item.strip()] = os.path.basename(h["vi"])
            names["%s/%s" % (typ, src)] = got
            print("0x%08X src %s -> %s" % (int(typ) & 0xFFFFFFFF, src, got), flush=True)
    finally:
        server.p.terminate()
    json.dump(names, open(out_path, "w", encoding="utf-8"), indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
