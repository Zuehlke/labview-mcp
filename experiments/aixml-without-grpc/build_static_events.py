# -*- coding: utf-8 -*-
"""Turn static_event_survey.py's output into parts/static-events.json - the STATIC events the
composer writes beyond its hand-measured EVENT_SPECS (2026-10-10).

    build_static_events.py <static_events.json> <out.json>

Per event name: the EventSpec fields (the commonest variant; regFlags and eFlags are per-frame
options and every variant seen is listed), the source kind the spec names (3 a control, 5 the VI,
4 the application), the label of the data cluster's reference field (CtlRef, VIRef, AppRef) and the
fields AFTER it - Source, Type and Time are the Event Data Node donor's own. Where the fields after
the reference differ between source classes, the event is kept per class (`classDependent`).

An event a control's frame can hold that the survey never saw STATIC is taken from
control-events.json - its dynamic twin carries the same type number (Value Change, Mouse Down and
Mouse Up read alike both ways) - with the static spec's source 3, regFlags 1, eSource 0, eFlags 4,
and marked `"from": "dynamic"`: probe_events.py has to name it before it is trusted.
"""
import collections
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
from aixml2vi import type_sig  # noqa: E402


def fields_after_ref(data_xml):
    d = ET.fromstring(data_xml)
    fs = [c for c in d if c.tag == "TypeDesc"]
    return fs[3].get("Label"), fs[4:]


def main(argv):
    survey = json.load(open(argv[0], encoding="utf-8"))
    out = {}
    for ev, e in survey.items():
        if ev == "Value Change":
            continue                          # EVENT_SPECS has it, measured per control
        spec_counts = collections.Counter(e["specs"])
        spec = json.loads(spec_counts.most_common(1)[0][0])
        regs = collections.Counter()
        efs = collections.Counter()
        for k, n in e["specs"].items():
            s = json.loads(k)
            regs[s["regFlags"]] += n
            efs[s["eFlags"]] += n
        per_class = {}
        for cls, x in e["data"].items():
            if x["data"]:
                per_class[cls] = fields_after_ref(x["data"])
        if not per_class:
            continue
        ref_labels = {lab for lab, _ in per_class.values()}
        sigs = {tuple(repr(type_sig(f)) + (f.get("Label") or "") for f in fs) for _, fs in per_class.values()}
        first_cls = sorted(per_class)[0]
        row = {"spec": spec, "regFlagsSeen": dict(regs), "eFlagsSeen": dict(efs),
               "sourceKind": {"3": "control", "5": "vi", "4": "application"}.get(spec["source"], spec["source"]),
               "refLabel": per_class[first_cls][0] if len(ref_labels) == 1 else None,
               "classes": dict(e["classes"]), "vi": e["vis"][0], "from": "static"}
        if len(sigs) == 1 and len(ref_labels) == 1:
            row["fields"] = [ET.tostring(f, encoding="unicode") for f in per_class[first_cls][1]]
        else:
            row["classDependent"] = True
            row["fieldsByClass"] = {c: [ET.tostring(f, encoding="unicode") for f in fs] for c, (_, fs) in per_class.items()}
        out[ev] = row
    # control events the survey never saw static: from the dynamic table
    ce = json.load(open(os.path.join(HERE, "parts", "control-events.json"), encoding="utf-8"))
    for key, r in ce["kinds"].items():
        cls, ev = key.split("|", 1)
        if ev in out or ev == "Value Change" or not r.get("data") or not r["code"].startswith("0C66"):
            continue
        lab, fs = fields_after_ref(r["data"])
        out[ev] = {"spec": {"source": "3", "regFlags": "1", "eSource": "0", "type": r["spec"]["type"], "eFlags": "4"},
                   "sourceKind": "control", "refLabel": "CtlRef", "classes": {cls: 0}, "vi": r["vi"],
                   "from": "dynamic", "fields": [ET.tostring(f, encoding="unicode") for f in fs]}
    json.dump(out, open(argv[1], "w", encoding="utf-8"), indent=1, sort_keys=True)
    for ev in sorted(out):
        r = out[ev]
        print("  %-32s %-11s type %-11s %s ref %s %s" % (ev, r["sourceKind"], r["spec"]["type"], r["from"], r["refLabel"],
                                                       "classDependent" if r.get("classDependent") else "%d fields" % len(r["fields"])))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
