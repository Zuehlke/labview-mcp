# -*- coding: utf-8 -*-
"""Add the NOTIFY key events - Key Down, Key Repeat, Key Up - to parts/static-events.json, on a
control and (`viVariant`) on the VI (2026-10-11).

    add_key_notify.py

NI's cached exports handle them in ONE frame: `"String Control": Key Down; "String Control": Key
Repeat` in Handling Common User Interface Events.vi, whose EventSpecs give the numbers in selector
order - Key Down 1073741847 (0x40000017), Key Repeat 1073741848 - and whose data is the fields the
two share: Char, VKey, ScanCode, Mods, PlatMods, FocusObj. Key Up is in no cached export; the
installation scan (scan_static_specs.py + name_static_specs.py) found it in picktime.vi as 1073741836
(0x4000000C), named by NI's exporter - NOT the 1073741849 after Key Repeat that was tried first,
which NI's exporter named Panel Close. picktime.vi handles it beside other events, so its fields
were measured on LabVIEW's own save of a probe composed with Key Down's: LabVIEW keeps ScanCode,
Mods, PlatMods and FocusObj and drops Char and VKey - on a control and on the VI alike. The VI's variant (source 5, eFlags 2)
is measured the same way.
"""
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import event_frame_data  # noqa: E402

TABLE = os.path.join(HERE, "parts", "static-events.json")
SOURCE = os.path.join(r"C:\Program Files (x86)\National Instruments\LabVIEW 2026", "examples", "Structures",
                      "Event Structure", "Handling Common User Interface Events.vi")
TYPES = {"Key Down": "1073741847", "Key Repeat": "1073741848", "Key Up": "1073741836"}


def main(argv):
    table = json.load(open(TABLE, encoding="utf-8"))
    frame = next(f for rec in event_frame_data.scan(SOURCE, False) for f in rec["frames"]
                 if [s["type"] for s in f["specs"]] == [TYPES["Key Down"], TYPES["Key Repeat"]])
    fields = [ET.tostring(c, encoding="unicode") for c in list(ET.fromstring(frame["data"]))[4:] if c.tag == "TypeDesc"]
    up = [x for x in fields if (ET.fromstring(x).get("Label") or
                                (ET.fromstring(x).find("TypeDesc").get("Label") if ET.fromstring(x).find("TypeDesc") is not None else ""))
          not in ("Char", "VKey")]
    for name, typ in TYPES.items():
        fs = up if name == "Key Up" else fields
        table[name] = {
            "classes": {"string": 1},
            "eFlagsSeen": {"4": 1},
            "fields": fs,
            "from": "static",
            "refLabel": "CtlRef",
            "regFlagsSeen": {"1": 1},
            "sourceKind": "control",
            "spec": {"eFlags": "4", "eSource": "0", "regFlags": "1", "source": "3", "type": typ},
            "viVariant": {"spec": {"eFlags": "2", "eSource": "0", "regFlags": "1", "source": "5", "type": typ},
                          "sourceKind": "vi", "refLabel": "VIRef", "fields": fs},
            "measuredOn": SOURCE + " (one frame with Key Down and Key Repeat)" if name != "Key Up" else
                          "number: resource picktime.vi, named by NI's exporter; fields: LabVIEW's own save of tests/t137_key_notify_events.xml",
        }
    json.dump(table, open(TABLE, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
    open(TABLE, "a", encoding="utf-8", newline="\n").write("\n")
    print("rows:", list(TYPES), "fields:", [ET.fromstring(x).get("Label") for x in fields])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
