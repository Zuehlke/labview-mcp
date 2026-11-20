# -*- coding: utf-8 -*-
"""Join the usage census with the sweep results into one prioritised gap list.

    python analyze.py <outdir> <census.json> <gaps.json>

Priority = in how many NI example VIs the element occurs (census `vis`); for an element the
example sweep refused, also how many examples it was the FIRST refusal of (`blocks`), the one
number that is free of cut artefacts.
"""
import collections
import glob
import json
import os
import re
import sys

REASONS = [
    ("crash", r"^CRASH"),
    ("notInstalled", r"neither near the caller nor in the installation"),
    ("propertyNotHarvested", r"no (property|invoke|method) node on|property .* not harvested|harvested yet"),
    ("notInDocument", r"not in this document|no connector pane|outside a class member"),
    ("placement", r"only at the top level|not a composable diagram|sits in|sits outside"),
    ("unknownPrimitive", r"is not a primitive this prototype knows"),
    ("noPart", r"has no harvested part|no part harvested|no .* part for"),
    ("xnode", r"is an XNode"),
    ("typeNotComposable", r"type .* is not composable|not composable yet|no constant encoding|no data object"),
    ("calleeUnreadable", r"cannot be read|no such \.lvclass|could not be read"),
    ("placement", r"only at the top level|not a composable diagram|sits in"),
    ("terminal", r"has no terminal|has no input|has no output|terminal"),
    ("mode", r"mode .* is not composable"),
    ("wiring", r"no typed wire|wire|net "),
    ("literal", r"value .* does not fit|literal"),
]


def reason(msg):
    for k, rx in REASONS:
        if re.search(rx, msg or "", re.I):
            return k
    return "other"


def main(outdir, census_path, out):
    census = {r["key"]: r for r in json.load(open(census_path, encoding="utf-8"))["rows"]}
    items = {}

    def item(key):
        if key not in items:
            c = census.get(key, {})
            items[key] = {"key": key, "vis": c.get("vis", 0), "instances": c.get("instances", 0),
                          "blocks": 0, "refusedIn": 0, "isolated": None, "msgs": collections.Counter(),
                          "examples": set()}
        return items[key]

    ex_total = ex_ok = 0
    for f in glob.glob(os.path.join(outdir, "examples-*.jsonl")):
        for l in open(f, encoding="utf-8"):
            r = json.loads(l)
            ex_total += 1
            if not r["steps"]:
                ex_ok += r["ok"]
                continue
            for i, s in enumerate(r["steps"]):
                k = s.get("key") or "unattributed"
                it = item(k)
                if i == 0:
                    it["blocks"] += 1
                it["refusedIn"] += 1
                it["msgs"][re.sub(r"\b\d+\b", "#", s["msg"])[:220]] += 1
                if len(it["examples"]) < 3:
                    it["examples"].add(r["vi"])
    iso_ok = iso_bad = 0
    for f in glob.glob(os.path.join(outdir, "calls-*.jsonl")) + glob.glob(os.path.join(outdir, "nodes-*.jsonl")):
        for l in open(f, encoding="utf-8"):
            r = json.loads(l)
            it = item(r["key"])
            it["isolated"] = r["ok"]
            if r["ok"]:
                iso_ok += 1
            else:
                iso_bad += 1
                it["msgs"][re.sub(r"\b\d+\b", "#", r["msg"])[:220]] += 1
    gaps = []
    for it in items.values():
        if it["refusedIn"] == 0 and it["isolated"] is not False:
            continue
        top = it["msgs"].most_common(1)[0][0] if it["msgs"] else ""
        it["reason"] = reason(top)
        it["message"] = top
        it["msgs"] = dict(it["msgs"].most_common(3))
        it["examples"] = sorted(it["examples"])
        gaps.append(it)
    gaps.sort(key=lambda g: (-g["blocks"], -g["vis"], -g["instances"], g["key"]))
    json.dump({"examples": ex_total, "examplesComposed": ex_ok, "isolatedOk": iso_ok,
               "isolatedRefused": iso_bad, "gaps": gaps}, open(out, "w", encoding="utf-8"), indent=1)
    print("examples %d, composed with no cut %d; isolated ok %d refused %d; gaps %d"
          % (ex_total, ex_ok, iso_ok, iso_bad, len(gaps)))
    print(collections.Counter(g["reason"] for g in gaps))


if __name__ == "__main__":
    main(*sys.argv[1:4])
