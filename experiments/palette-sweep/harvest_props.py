# -*- coding: utf-8 -*-
"""Harvest Property/Invoke node rows (harvest_parts.py --props) from every NI example into COPIES
of the shipped parts, in shards, and report the rows they would add - nothing shipped is touched
until --apply merges them into scripts/aixml-pylabview/parts/vi-server-props.json.

    <bundle>\\python.exe harvest_props.py <workdir> [shards]          harvest and report
    <bundle>\\python.exe harvest_props.py <workdir> --apply           merge the new rows
"""
import glob
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
PARTS = os.path.join(COMPOSER, "parts")
sys.path.insert(0, HERE)
CACHE = os.path.join(os.environ["USERPROFILE"], ".labviewmcp", "cache", "aixml")


def example_vis():
    import sweep
    return sweep.example_paths()


def new_rows(work, shards):
    base = json.load(open(os.path.join(PARTS, "vi-server-props.json"), encoding="utf-8"))
    add = {"classes": {}, "props": {}, "methods": {}}
    for i in range(shards):
        p = json.load(open(os.path.join(work, "s%d" % i, "parts", "vi-server-props.json"), encoding="utf-8"))
        for c, v in p.get("classes", {}).items():
            if c not in base["classes"]:
                add["classes"].setdefault(c, v)
        for k in ("props", "methods"):
            for c, rows in p.get(k, {}).items():
                for n, r in rows.items():
                    if n not in base.get(k, {}).get(c, {}):
                        add[k].setdefault(c, {}).setdefault(n, r)
    return base, add


def main(argv):
    work = os.path.abspath(argv[0])
    if "--apply" in argv:
        shards = len(glob.glob(os.path.join(work, "s*")))
        base, add = new_rows(work, shards)
        for c, v in add["classes"].items():
            base["classes"][c] = v
        for k in ("props", "methods"):
            for c, rows in add[k].items():
                base.setdefault(k, {}).setdefault(c, {}).update(rows)
        json.dump(base, open(os.path.join(PARTS, "vi-server-props.json"), "w", encoding="utf-8", newline="\n"),
                  indent=1, sort_keys=True, ensure_ascii=False)
        # the invoke parts a method row needs (the copies started empty, so only NEW keys count)
        copied = 0
        pattern = "*.xml" if os.path.exists(os.path.join(work, "FULL")) else "invoke__*.xml"
        for f in sorted(glob.glob(os.path.join(work, "s*", "parts", pattern))):
            dst = os.path.join(PARTS, os.path.basename(f))
            if not os.path.exists(dst):
                shutil.copy(f, dst)
                copied += 1
        print("invoke parts added: %d" % copied)
        print("merged: %d classes, %d props, %d methods" % (
            len(add["classes"]), sum(map(len, add["props"].values())), sum(map(len, add["methods"].values()))))
        return
    full = "--all" in argv
    argv = [a for a in argv if a != "--all"]
    shards = int(argv[1]) if len(argv) > 1 else 6
    vis = example_vis()
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    procs = []
    for i in range(shards):
        d = os.path.join(work, "s%d" % i)
        if full:
            # the whole shipped set, so a part is written only where its KEY is new
            shutil.copytree(PARTS, os.path.join(d, "parts"))
        else:
            os.makedirs(os.path.join(d, "parts"))
            # only the table: --props writes nothing else
            shutil.copy(os.path.join(PARTS, "vi-server-props.json"), os.path.join(d, "parts"))
            for f in ("wiretables.json", "denylist.json"):
                if os.path.exists(os.path.join(PARTS, f)):
                    shutil.copy(os.path.join(PARTS, f), os.path.join(d, "parts"))
        lst = os.path.join(d, "list.txt")
        open(lst, "w", encoding="utf-8").write("\n".join(vis[i::shards]))
        procs.append(subprocess.Popen([sys.executable, "-I", os.path.join(COMPOSER, "harvest_parts.py"),
                                       os.path.join(d, "parts")] + ([] if full else ["--props"]) + ["@" + lst],
                                      stdout=open(os.path.join(d, "out.txt"), "w", encoding="utf-8"),
                                      stderr=open(os.path.join(d, "err.txt"), "w", encoding="utf-8")))
    for p in procs:
        p.wait()
    if full:
        open(os.path.join(work, "FULL"), "w").close()
        base = set(os.listdir(PARTS))
        new = sorted({f for i in range(shards) for f in os.listdir(os.path.join(work, "s%d" % i, "parts"))
                      if f.endswith(".xml") and f not in base})
        open(os.path.join(work, "new-parts.txt"), "w", encoding="utf-8").write("\n".join(new))
        print("%d new part files (new-parts.txt)" % len(new))
    _, add = new_rows(work, shards)
    print("%d VIs; new classes: %s" % (len(vis), sorted(add["classes"])))
    for k in ("props", "methods"):
        for c, rows in sorted(add[k].items()):
            print("%s %s: %d (%s)" % (k, c, len(rows), ", ".join(sorted(rows)[:6])))


if __name__ == "__main__":
    main(sys.argv[1:])
