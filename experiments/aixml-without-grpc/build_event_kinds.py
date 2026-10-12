# -*- coding: utf-8 -*-
"""Turn scan_event_regs.py's records into the table of DYNAMIC control events the composer needs:
(VI Server class, event name) -> the registration item's code, the item dco's objFlags, the frame's
EventSpec fields and the frame's event data cluster - each read off a VI LabVIEW saved itself.

    build_event_kinds.py <scan.jsonl> <out.json> [--export-dir DIR]

The event NAME is not in the heap. A spec's dynIndex names the registration item (the structure's
dynamic event terminal is typed with the registration, one child per item, each carrying the
item's code as CField2:CField4, its label the source's); NI's AIXML export of the same VI names
each frame's events in its selector - `<Picture>: Mouse Down`, `<#5>: Mouse Up` for an unlabelled
item at position 5. A spec is matched to the selector item whose source is the child's label.
Exports come from the AIXML cache (%USERPROFILE%\\.labviewmcp\\cache\\aixml) or --export-dir.

regFlags and eFlags are NOT constant per (class, event): Mouse Down on a Picture reads regFlags 1
on one item and -2147483647 (the 0x80000000 bit added) on another of the same VI, and Mouse Move
eFlags 2 in Dynamic Event Generation.vi against 4 in the icon editor - per-frame options. The table
keeps the value seen most often and lists every variant under regFlagsSeen / eFlagsSeen. A frame
handling one event (several sources of it included) gives that event's data cluster.
"""
import collections
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
from aixml2vi import event_items, unescape  # noqa: E402

CACHE = os.path.join(os.environ.get("USERPROFILE", ""), ".labviewmcp", "cache", "aixml")


def classes_by_flags():
    out = {}
    with open(os.path.join(HERE, "parts", "refnum-classes.tsv"), encoding="utf-8") as f:
        for line in f:
            row = line.rstrip("\r\n").split("\t")
            if line.startswith("#") or row[0] == "class" or len(row) < 2:
                continue
            out.setdefault(row[1].upper(), row[0])
    return out


def class_parents():
    """{class: parent class}: a refnum's CtlFlags IS LabVIEW's class id (LV.Control 0x0006,
    LV.GObject 0x0024 - refnum-classes.tsv), and docs/vi-server-classes.tsv gives each class's
    parent by that id (Control -> 36, Boolean -> 6, Digital -> 17 = Numeric)."""
    ids = {}
    for line in open(os.path.join(HERE, "parts", "refnum-classes.tsv"), encoding="utf-8"):
        row = line.rstrip("\r\n").split("\t")
        if not line.startswith("#") and len(row) >= 2 and row[0].startswith("LV."):
            ids[int(row[1], 16)] = row[0]
    out = {}
    docs = os.path.join(HERE, "..", "..", "docs", "vi-server-classes.tsv")
    for line in open(docs, encoding="utf-8"):
        row = line.rstrip("\r\n").split("\t")
        if len(row) >= 2 and row[0] != "class" and row[1].strip():
            parent = ids.get(int(row[1]))
            if parent:
                out["LV." + row[0]] = parent
    return out


def cache_index():
    idx = {}
    for meta in glob.glob(os.path.join(CACHE, "*.json")):
        try:
            vp = json.load(open(meta, encoding="utf-8")).get("ViPath") or ""
        except ValueError:
            continue
        idx[os.path.normcase(vp)] = meta[:-5] + ".xml"
    return idx


def frames_of(export_xml):
    """{struct uid: [[selector item, ...] per frame in order]}"""
    root = ET.parse(export_xml).getroot()
    out = {}
    for st in root.iter("Structure"):
        if st.get("_name") != "Event Structure":
            continue
        out[st.get("uid")] = [event_items(unescape(fr.get("selector") or "")) for fr in st if fr.tag == "CaseFrame"]
    return out


def code_of(c):
    c2, c4 = (c.get("ref.CField2") or "0x0000")[2:], (c.get("ref.CField4") or "0x0000")[2:]
    return c4 if c2 == "0000" else c2 + c4


def main(argv):
    scan, out_path = argv[0], argv[1]
    exp_dir = argv[argv.index("--export-dir") + 1] if "--export-dir" in argv else None
    flags = classes_by_flags()
    cache = cache_index()
    table, missing, conflicts = {}, [], []
    reg_seen = collections.defaultdict(collections.Counter)
    ef_seen = collections.defaultdict(collections.Counter)
    for line in open(scan, encoding="utf-8"):
        rec = json.loads(line)
        vi = rec["vi"]
        exp = cache.get(os.path.normcase(vi))
        if exp_dir:
            hit = glob.glob(os.path.join(exp_dir, glob.escape(os.path.splitext(os.path.basename(vi))[0]) + "*.xml"))
            exp = hit[0] if hit else exp
        if exp is None:
            if any("LVObjCtl" in (d or "") for st in rec["structs"] for d in st["dyn"]):
                missing.append(vi)
            continue
        frames = frames_of(exp)
        for st in rec["structs"]:
            fr = frames.get(st["uid"])
            if fr is None or not st["dyn"] or not st["dyn"][0]:
                continue
            children = [c for c in ET.fromstring(st["dyn"][0]) if c.tag == "TypeDesc"]
            codes = [code_of(c) for c in children]
            reg_node = next((r for r in rec["regs"] if [it["code"] for it in r["items"]] == codes), None)
            used = collections.Counter()
            for sp in st["specs"]:
                dyn = int(sp.get("dynIndex") or 0)
                if dyn <= 0 or dyn > len(children):
                    continue
                child = children[dyn - 1]
                if child.get("RefType") != "LVObjCtl":
                    continue
                di = int(sp["diagramIdx"])
                name = child.get("Label") or "#%d" % dyn
                cands = [x for x in (fr[di] if di < len(fr) else []) if re.fullmatch(r"<%s>\s*:.*" % re.escape(name), x)]
                k = used[(di, name)]
                used[(di, name)] += 1
                if k >= len(cands):
                    continue
                event = cands[k].split(":")[-1].strip()
                cls = flags.get((child.get("CtlFlags") or "").upper(), "CtlFlags " + (child.get("CtlFlags") or "?"))
                key = "%s|%s" % (cls, event)
                reg_seen[key][sp["regFlags"]] += 1
                ef_seen[key][sp["eFlags"]] += 1
                row = {"code": codes[dyn - 1],
                       "spec": {k_: sp[k_] for k_ in ("source", "regFlags", "eSource", "type", "eFlags")},
                       "dcoFlags": reg_node["items"][dyn - 1].get("dcoFlags", "0") if reg_node else "0",
                       "data": st["data"].get(str(di)) if len({x.split(":")[-1].strip() for x in fr[di]}) == 1 else None,
                       "vi": vi}
                old = table.get(key)
                if old is None:
                    table[key] = row
                elif old["code"] != row["code"] or old["spec"]["type"] != row["spec"]["type"]:
                    conflicts.append((key, old["code"], row["code"], vi))
                elif old["data"] is None and row["data"] is not None:
                    # a frame handling one event carries that event's own data cluster; one
                    # handling several carries their common fields - prefer the former
                    old["data"], old["vi"] = row["data"], vi
    for key, seen in reg_seen.items():
        table[key]["spec"]["regFlags"] = seen.most_common(1)[0][0]
        table[key]["regFlagsSeen"] = dict(seen)
        table[key]["spec"]["eFlags"] = ef_seen[key].most_common(1)[0][0]
        table[key]["eFlagsSeen"] = dict(ef_seen[key])
    # which class DECLARES the events of one code family (CField2): the measured class every other
    # class measured with that family descends from - 0C66 is LV.Control's (Value Change read on an
    # LV.Control reference itself, and the same family on Array, Digital, Picture), 3903 LV.VI's.
    # A family with no such class declares nothing beyond the classes it was read on.
    parents = class_parents()

    def ancestors(c):
        out = []
        while c in parents and parents[c] not in out:
            c = parents[c]
            out.append(c)
        return out
    fams = collections.defaultdict(set)
    for key, r in table.items():
        if len(r["code"]) == 8:
            fams[r["code"][:4]].add(key.split("|")[0])
    declared = {}
    for fam, clss in fams.items():
        top = [c for c in clss if all(o == c or c in ancestors(o) for o in clss)]
        if top:
            declared[fam] = top[0]
    json.dump({"kinds": table, "declaredBy": declared, "parents": parents, "missingExports": missing,
               "conflicts": conflicts}, open(out_path, "w", encoding="utf-8"), indent=1, sort_keys=True)
    print("code families declared by: %s" % declared)
    print("%d (class, event) pairs; %d VIs with control sources and no export; %d conflicts"
          % (len(table), len(missing), len(conflicts)))
    for k in sorted(table):
        r = table[k]
        print("  %-44s code %s type %s regFlags %s eFlags %s data %s" % (k, r["code"], r["spec"]["type"], r.get("regFlagsSeen"), r.get("eFlagsSeen"),
                                                             "yes" if r["data"] else "NO"))
    for m in missing:
        print("  no export:", m)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
