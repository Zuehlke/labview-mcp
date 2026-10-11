# -*- coding: utf-8 -*-
"""Give Key Down? its CONTROL variant in parts/static-events.json (2026-10-11).

    add_key_events.py                          provisional: the VI's fields, a control's spec
    add_key_events.py --from-probe <out> <n>   frame <n>'s data and filter cluster from
                                               LabVIEW's save in a probe_events.py output

Key Down? is an event of the VI AND of a control, under one name. NI's cached exports handle it
static only on the VI (NewProjectDialog.vi), and the dynamic table has no key event at all, so
the control's row cannot be surveyed. It is MEASURED instead: a probe composed with the VI's
fields and a control's spec (source 3, eFlags 4 - every control event NI's VIs carry), then the
data and filter clusters LabVIEW itself writes when it loads that probe - it rewrites a frame's
event data on load - and NI's exporter naming the event back, which settles the type number.
The row's `controlVariant` is what the composer uses when the selector names a source.
"""
import ast
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
TABLE = os.path.join(HERE, "parts", "static-events.json")


def labview_frame(text, n):
    """(data xml, specs, filter xml) LabVIEW's save carries for frame n of a probe_events output -
    printed for a frame that DIFFERS; None when the frame was the same as composed."""
    frame = None
    for line in text.splitlines():
        m = re.match(r"\s*structure \d+ frame (\d+):", line)
        if m:
            frame = int(m.group(1))
            continue
        m = re.match(r"\s*labview\s*:\s*(\(.*\))\s*$", line)
        if m and frame == n:
            return ast.literal_eval(m.group(1))
    return None


def after_ref(xml):
    fs = [c for c in ET.fromstring(xml) if c.tag == "TypeDesc"]
    return [ET.tostring(c, encoding="unicode") for c in fs[4:]]


def main(argv):
    table = json.load(open(TABLE, encoding="utf-8"))
    row = table["Key Down?"]
    if "--from-probe" in argv:
        i = argv.index("--from-probe")
        got = labview_frame(open(argv[i + 1], encoding="utf-8", errors="replace").read(), int(argv[i + 2]))
        if got is None:
            print("frame %s is the same as composed - nothing to take" % argv[i + 2])
            return 0
        data, specs, filt = got
        cv = row["controlVariant"]
        cv["fields"] = after_ref(data)
        if filt:
            cv["filterFields"] = after_ref(filt)
        cv["measuredOn"] = ("LabVIEW's own save of tests/t134_control_key_down.xml (a string control); "
                            "NI's exporter named the frame back")
        print("specs LabVIEW wrote:", specs)
    else:
        row["controlVariant"] = {
            "spec": {"source": "3", "regFlags": "1", "eSource": "0", "type": row["spec"]["type"], "eFlags": "4"},
            "sourceKind": "control", "refLabel": "CtlRef",
            "fields": list(row["fields"]), "filterFields": list(row.get("filterFields", [])),
            "measuredOn": "provisional - the VI's fields; replace with --from-probe"}
    for k in ("fields", "filterFields"):
        labs = [re.search(r'Label="([^"]*)"', x).group(1) for x in row["controlVariant"][k]]
        print(k, labs)
    json.dump(table, open(TABLE, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
    open(TABLE, "a", encoding="utf-8", newline="\n").write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
