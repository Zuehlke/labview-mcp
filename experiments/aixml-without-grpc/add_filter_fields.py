# -*- coding: utf-8 -*-
"""Give every FILTER event of parts/static-events.json the cluster its Event Filter Node carries
(2026-10-11).

    add_filter_fields.py [--dry-run]

A filter event - a type with the 0x80000000 bit: Panel Close?, Key Down?, Drag Starting?, and the
drag events Drag Enter and Drop - has a second eventDataNode in its frame, the Event Filter Node,
whose cluster is called "Event Filter Data". It cannot be derived from the data cluster: Drag
Starting?'s Data and Drag Mode carry no bit on either node (read AND set), while its Coords is
hidden on the filter node (Bit7). So it is read off the VI the survey took the event from - the
frame handling exactly that one event - and written as `filterFields`, the fields after the
reference, like `fields`.
"""
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import event_frame_data  # noqa: E402

TABLE = os.path.join(HERE, "parts", "static-events.json")


def main(argv):
    table = json.load(open(TABLE, encoding="utf-8"))
    by_vi = {}
    for name, row in table.items():
        if int(row["spec"]["type"]) < 0 and row.get("vi"):
            by_vi.setdefault(row["vi"], []).append(name)
    found = {}
    for vi, names in sorted(by_vi.items()):
        try:
            recs = event_frame_data.scan(vi, False)
        except Exception as ex:
            print("UNREADABLE", vi, str(ex).splitlines()[0][:120])
            continue
        for name in names:
            typ = table[name]["spec"]["type"]
            for rec in recs:
                for f in rec["frames"]:
                    if len(f["specs"]) == 1 and f["specs"][0].get("type") == typ and f.get("filter") and name not in found:
                        fs = [c for c in ET.fromstring(f["filter"]) if c.tag == "TypeDesc"]
                        found[name] = (vi, fs)
    for name in sorted(n for ns in by_vi.values() for n in ns):
        if name not in found:
            print("  %-28s no Event Filter Node in %s" % (name, os.path.basename(table[name]["vi"])))
            continue
        vi, fs = found[name]
        ref = fs[3]
        ref_label = ref.get("Label") or (ref.find("TypeDesc").get("Label") if ref.find("TypeDesc") is not None else None)
        table[name]["filterFields"] = [ET.tostring(c, encoding="unicode") for c in fs[4:]]
        table[name]["filterRefLabel"] = ref_label
        labels = []
        for c in fs[4:]:
            inner = c.find("TypeDesc") if c.get("Type") == "TypeDef" else c
            bits = "".join("+" + k for k, v in sorted((inner if inner is not None else c).attrib.items()) if k.startswith("Bit") and v == "1")
            labels.append((c.get("Label") or (inner.get("Label") if inner is not None else "?")) + bits)
        print("  %-28s %s" % (name, labels))
    if "--dry-run" not in argv:
        json.dump(table, open(TABLE, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
        open(TABLE, "a", encoding="utf-8", newline="\n").write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
