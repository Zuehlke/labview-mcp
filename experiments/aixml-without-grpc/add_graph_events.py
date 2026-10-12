# -*- coding: utf-8 -*-
"""Add the GRAPH events to parts/static-events.json - the six NI's Graph Events.vi handles in ONE
frame, which static_event_survey.py skips (it takes frames with exactly one static event).

    add_graph_events.py [--fields <probe_events output>]

Without --fields: rows with the specs read off that frame (in its selector's order) and only the
base fields, for a probe whose LabVIEW save then says what each event's data really is. With
--fields: each event's fields taken from LabVIEW's save in that probe's output. The type numbers
are trusted only once NI's exporter names every frame of the probe as asked (probe_events.py).
"""
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer and its parts, since 2026-10-12
TABLE = os.path.join(COMPOSER, "parts", "static-events.json")
SOURCE = "examples/Controls and Indicators/Graphs and Charts/Graphs - General/Graph Events.vi"

# frame 0's EventSpecs in the order of its selector (event_frame_data.scan)
SPECS = [("Autoscale Range Change", "1073741900", "1"),
         ("Plot Attribute Change", "1073741862", "1"),
         ("Scale Range Change", "1073741861", "1"),
         ("Cursor Grab", "1073741864", "1"),
         ("Cursor Move", "1073741865", "1"),
         ("Cursor Release", "1073741866", "-2147483647")]


def fields_from_probe(text):
    """{frame index: [field TypeDesc xml after CtlRef]} from LabVIEW's side of probe_events output."""
    out = {}
    frame = None
    for line in text.splitlines():
        s = line.strip()
        m = re.match(r"structure \d+ frame (\d+):", s)
        if m:
            frame = int(m.group(1))
            continue
        m = re.match(r"labview\s*:\s*\('(.*?)', \[", s)
        if m and frame is not None:
            root = ET.fromstring(m.group(1).replace("\\n", "\n").replace("\\'", "'"))
            kids = [c for c in root if c.tag == "TypeDesc"]
            labels = [c.get("Label") or (c.find("TypeDesc").get("Label") if c.find("TypeDesc") is not None else "") for c in kids]
            after = kids[labels.index("CtlRef") + 1:] if "CtlRef" in labels else kids[3:]
            out[frame] = [ET.tostring(c, encoding="unicode") for c in after]
    return out


def main(argv):
    table = json.load(open(TABLE, encoding="utf-8"))
    fields = {}
    if "--fields" in argv:
        fields = fields_from_probe(open(argv[argv.index("--fields") + 1], encoding="utf-8", errors="replace").read())
    for i, (name, typ, reg) in enumerate(SPECS):
        table[name] = {
            "classes": {"stdGraph": 1},
            "eFlagsSeen": {"4": 1},
            "fields": fields.get(i, []),
            "from": "static",
            "refLabel": "CtlRef",
            "regFlagsSeen": {reg: 1},
            "sourceKind": "control",
            "spec": {"eFlags": "4", "eSource": "0", "regFlags": reg, "source": "3", "type": typ},
            "measuredOn": SOURCE + " (one frame with all six); fields from LabVIEW's save of "
                          "tests/t130_graph_specific_events.xml" if fields else SOURCE,
        }
    json.dump(table, open(TABLE, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
    open(TABLE, "a", encoding="utf-8", newline="\n").write("\n")
    print("rows:", [s[0] for s in SPECS], "fields per frame:", {k: len(v) for k, v in fields.items()})


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
