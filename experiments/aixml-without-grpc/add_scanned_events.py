# -*- coding: utf-8 -*-
"""Rows for the events the installation scan found and NI's exporter named, which the table has
not got yet (2026-10-11).

    add_scanned_events.py <scan.jsonl> [--write]

For each named type (parts/event-type-names.json) not in parts/static-events.json by NUMBER: a frame
of LabVIEW's own that handles exactly that ONE event is read again (event_frame_data.scan) - its
spec, the source kind (3 a control, 4 the application, 5 the VI), the reference's label, the
fields after it, the filter fields, and for a control the class of its source (the ddo class).
Without --write it only reports; with it the rows join the table, keyed by NI's name, or by
"<name> [<class>]" where that name is already a row of another number (Double Click).
"""
import collections
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer and its parts, since 2026-10-12
sys.path.insert(0, HERE)
import event_frame_data  # noqa: E402

TABLE = os.path.join(COMPOSER, "parts", "static-events.json")
NAMES = os.path.join(COMPOSER, "parts", "event-type-names.json")
KINDS = {"3": "control", "4": "application", "5": "vi"}


def labels(xml):
    out = []
    for c in [c for c in ET.fromstring(xml) if c.tag == "TypeDesc"]:
        inner = c.find("TypeDesc") if c.get("Type") == "TypeDef" else c
        out.append(c.get("Label") or (inner.get("Label") if inner is not None else "?"))
    return out


def after_ref(xml):
    return [ET.tostring(c, encoding="unicode") for c in [c for c in ET.fromstring(xml) if c.tag == "TypeDesc"][4:]]


def main(argv):
    hits = [json.loads(l) for l in open(argv[0], encoding="utf-8")]
    table = json.load(open(TABLE, encoding="utf-8"))
    names = json.load(open(NAMES, encoding="utf-8"))
    # a row this script wrote before is written again - dropped first, so a rerun keys it as the
    # first run did; any other row's number is taken
    table = {k: r for k, r in table.items() if r.get("by") != "add_scanned_events"}
    known = {int(r["spec"]["type"]) for r in table.values() if r.get("spec")}
    by_type = collections.defaultdict(list)
    for h in hits:
        by_type[h["spec"]["type"]].append(h)
    rows = {}
    for typ, nm in names.items():
        name = nm["name"]
        if int(typ) in known or name == "Timeout":
            continue
        hs = by_type.get(typ, [])
        regs = collections.Counter(h["spec"]["regFlags"] for h in hs)
        srcs = collections.Counter(h["spec"]["source"] for h in hs)
        found = None
        for vi in sorted({h["vi"] for h in hs}):
            for rec in event_frame_data.scan(vi, False):
                for f in rec["frames"]:
                    if len(f["specs"]) == 1 and f["specs"][0]["type"] == typ and f["data"]:
                        found = (vi, f)
                        break
                if found:
                    break
            if found:
                break
        if not found:
            # PROVISIONAL, for a probe whose LabVIEW save then gives the real fields: the spec as
            # the scan saw it, the fields of the nearest relative (Drag Over: Drag Enter's - the same
            # 0xA... drag family) or none
            src = srcs.most_common(1)[0][0] if srcs else None
            twin = {"Drag Over": "Drag Enter"}.get(name)
            t = table.get(twin) if twin else None
            if src is None:
                print("  %-32s 0x%08X  NO single-event frame and no hit" % (name, int(typ) & 0xFFFFFFFF))
                continue
            eflags = {"3": "4", "4": "1", "5": "2"}[src]
            rows[name] = {"classes": {"null": len(hs)}, "eFlagsSeen": {eflags: len(hs)},
                          "fields": list(t["fields"]) if t else [], "from": "provisional",
                          "refLabel": {"3": "CtlRef", "4": "AppRef", "5": "VIRef"}[src], "regFlagsSeen": dict(regs),
                          "sourceKind": KINDS[src],
                          "spec": {"eFlags": eflags, "eSource": "0", "source": src, "type": typ,
                                   "regFlags": regs.most_common(1)[0][0]},
                          "measuredOn": "PROVISIONAL - %s; its fields replaced from LabVIEW's own save of a probe"
                                        % ("%s's fields" % twin if t else "no fields")}
            if t and t.get("filterFields"):
                rows[name]["filterFields"] = list(t["filterFields"])
            print("  %-32s 0x%08X  PROVISIONAL (%d hits, sources %s)%s" % (name, int(typ) & 0xFFFFFFFF, len(hs), dict(srcs),
                                                                         " fields from " + twin if t else ""))
            continue
        vi, f = found
        sp = f["specs"][0]
        src = (f["sources"] or {}).get(sp.get("ddoUID")) if sp.get("ddoUID") not in (None, "", "0") else None
        lab = labels(f["data"])
        row = {"classes": {(src or {}).get("class") or "null": len(hs)}, "eFlagsSeen": {sp["eFlags"]: len(hs)},
               "fields": after_ref(f["data"]), "from": "static", "refLabel": lab[3] if len(lab) > 3 else None,
               "regFlagsSeen": dict(regs), "sourceKind": KINDS.get(sp["source"], sp["source"]),
               "spec": {k: sp[k] for k in ("eFlags", "eSource", "source", "type")} | {"regFlags": regs.most_common(1)[0][0]},
               "vi": vi, "measuredOn": vi + " (one frame with this one event; named by NI's exporter)"}
        if f.get("filter"):
            row["filterFields"] = after_ref(f["filter"])
        key = name if name not in table else "%s [%s]" % (name, (src or {}).get("class"))
        rows[key] = row
        print("  %-32s 0x%08X  %-11s class %-14s ref %-8s fields %s%s" % (
            key, int(typ) & 0xFFFFFFFF, row["sourceKind"], (src or {}).get("class"), row["refLabel"], lab[4:],
            "  filter %s" % labels(f["filter"])[4:] if f.get("filter") else ""))
    # what LabVIEW's own load said of each, one probe VI apiece (probe_scanned_events.py,
    # 2026-10-11): an event bound to a class is refused on any other, an event no plain VI or
    # AIXML-expressible source can carry is refused outright - LabVIEW breaks the VI otherwise
    guards = {
        "Operate Menu Dismissed": {"onlyClasses": ["stdRing"]},     # ring 1, string 0
        "Edit Cell?": {"onlyClasses": ["listbox"]},                 # table 0; AIXML has no listbox
        "Item Close": {"onlyClasses": ["treeControl"]},             # tree events - AIXML has no tree
        "Item Open?": {"onlyClasses": ["treeControl"]},
        "Item Close?": {"onlyClasses": ["treeControl"]},
        "Pane Size": {"refused": "its source is a PANE, which an AIXML selector cannot name"},
        # the XControl events: valid in a FACADE only - the composer writes one when the VI is
        # named "<X>.xctl:<Facade>.vi" - and their VIRef is of class 0x004F there, not the plain
        # VI's 0x0002 (NI's Simple Dual Mode Thermometer Facade.vi, LabVIEW's own save of the
        # composed one)
        **{k: {"onlyInFacade": True, "refClass": "0x004F"}
           for k in ("Data Change", "Direction Change", "Display State Change", "Exec State Change")},
    }
    confirmed = {"Drag Over": "Drag Enter's fields, confirmed identical on LabVIEW's own save of a probe on a waveform graph",
                 "Application Activation": "no fields of its own, confirmed identical on LabVIEW's own save of a probe"}
    for r in rows.values():
        r["by"] = "add_scanned_events"
    for k, g in guards.items():
        if k in rows:
            rows[k].update(g)
    for k, why in confirmed.items():
        if k in rows:
            rows[k]["from"] = "measured"
            rows[k]["measuredOn"] = "number: LabVIEW's own VIs, named by NI's exporter; " + why
    if "Double Click" in table:
        table["Double Click"]["onlyClasses"] = ["listbox"]          # table 0 (measured)
    if "--write" in argv:
        table.update(rows)
        json.dump(table, open(TABLE, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
        open(TABLE, "a", encoding="utf-8", newline="\n").write("\n")
        print("wrote %d rows" % len(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
