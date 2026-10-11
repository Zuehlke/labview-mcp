# -*- coding: utf-8 -*-
"""Add the APPLICATION events the installation scan found to parts/static-events.json (2026-10-11).

    add_app_events.py <scan.jsonl>

NI's cached exports hold one application event (VI Activation); LabVIEW's own dialogs and tools
under resource\\ and project\\ hold more, found by scan_static_specs.py and named by NI's exporter
(name_static_specs.py). For each, a frame handling exactly that ONE event is read again
(event_frame_data.scan) - a frame with several carries only their common fields - and its spec,
data and filter cluster become the row; the regFlags kept is the commonest seen.
"""
import collections
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import event_frame_data  # noqa: E402

TABLE = os.path.join(HERE, "parts", "static-events.json")
# number -> NI's exporter's name (name_static_specs.py over the scan, 2026-10-11)
EVENTS = {"1073741850": "Application Instance Close", "-2147483644": "Application Instance Close?",
          "1073741888": "Application Activation", "1073741906": "Bookmark Info Change"}


def after_ref(xml):
    return [ET.tostring(c, encoding="unicode") for c in [c for c in ET.fromstring(xml) if c.tag == "TypeDesc"][4:]]


def main(argv):
    hits = [json.loads(l) for l in open(argv[0], encoding="utf-8")]
    table = json.load(open(TABLE, encoding="utf-8"))
    for typ, name in EVENTS.items():
        hs = [h for h in hits if h["spec"]["type"] == typ and h["spec"]["source"] == "4"]
        regs = collections.Counter(h["spec"]["regFlags"] for h in hs)
        row = None
        for vi in sorted({h["vi"] for h in hs}):
            for rec in event_frame_data.scan(vi, False):
                for f in rec["frames"]:
                    if len(f["specs"]) == 1 and f["specs"][0]["type"] == typ and f["data"]:
                        ref = [c for c in ET.fromstring(f["data"]) if c.tag == "TypeDesc"][3]
                        row = {"classes": {"null": len(hs)}, "eFlagsSeen": {"1": len(hs)},
                               "fields": after_ref(f["data"]), "from": "static",
                               "refLabel": ref.get("Label"), "regFlagsSeen": dict(regs), "sourceKind": "application",
                               "spec": {"eFlags": "1", "eSource": "0", "regFlags": regs.most_common(1)[0][0],
                                        "source": "4", "type": typ},
                               "vi": vi, "measuredOn": vi + " (one frame with this one event; named by NI's exporter)"}
                        if f.get("filter"):
                            row["filterFields"] = after_ref(f["filter"])
                        break
                if row:
                    break
            if row:
                break
        if row is None:
            print("  %-30s no single-event frame among %d hits" % (name, len(hs)))
            continue
        table[name] = row
        labs = lambda xs: [ET.fromstring(x).get("Label") for x in xs]  # noqa: E731
        print("  %-30s ref %s fields %s filter %s regFlags %s" % (name, row["refLabel"], labs(row["fields"]),
                                                                labs(row.get("filterFields", [])), dict(regs)))
    json.dump(table, open(TABLE, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
    open(TABLE, "a", encoding="utf-8", newline="\n").write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
