# -*- coding: utf-8 -*-
"""Merge property/invoke harvests made in parallel into one parts directory.

    merge_props.py <parts-dir> <chunk-dir> [...]

A sweep over thousands of VIs runs as several `harvest_parts.py <chunk-dir> @list --props`
processes, each with its own directory, because each one rewrites vi-server-props.json whole at
its end. This folds them together: classes and methods first sighting wins, a property row first
sighting wins with the ACCESS seen anywhere added to it, and invoke/ parts the target lacks are
copied in.
"""
import json
import os
import shutil
import sys


def main(argv):
    target, chunks = argv[0], argv[1:]
    tp = os.path.join(target, "vi-server-props.json")
    t = json.load(open(tp, encoding="utf-8")) if os.path.exists(tp) else {"classes": {}, "props": {}, "methods": {}}
    added = {"classes": 0, "props": 0, "methods": 0, "parts": 0}
    for c in chunks:
        cp = os.path.join(c, "vi-server-props.json")
        if os.path.exists(cp):
            d = json.load(open(cp, encoding="utf-8"))
            for cls, row in d.get("classes", {}).items():
                if cls not in t["classes"] and row:
                    t["classes"][cls] = row
                    added["classes"] += 1
            for cls, props in d.get("props", {}).items():
                mine = t["props"].setdefault(cls, {})
                for name, row in props.items():
                    if name not in mine:
                        mine[name] = row
                        added["props"] += 1
                    else:
                        acc = set(mine[name].get("access", "")) | set(row.get("access", ""))
                        mine[name]["access"] = "".join(sorted(acc))
            for cls, meths in d.get("methods", {}).items():
                mine = t["methods"].setdefault(cls, {})
                for name, row in meths.items():
                    if name not in mine:
                        mine[name] = row
                        added["methods"] += 1
        for f in os.listdir(c):
            if f.startswith("invoke__") and f.endswith(".xml") and not os.path.exists(os.path.join(target, f)):
                shutil.copy(os.path.join(c, f), os.path.join(target, f))
                added["parts"] += 1
    json.dump(t, open(tp, "w", encoding="utf-8", newline="\n"), indent=1, sort_keys=True, ensure_ascii=False)
    print("merged: %s" % added)
    print("now: %d classes, %d properties, %d methods" % (
        len(t["classes"]), sum(len(v) for v in t["props"].values()), sum(len(v) for v in t["methods"].values())))


if __name__ == "__main__":
    main(sys.argv[1:])
