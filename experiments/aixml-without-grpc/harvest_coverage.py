# -*- coding: utf-8 -*-
"""Which part keys would a harvest over a set of VIs ADD to the shipped parts? A dry run.

    harvest_coverage.py <work-dir> <list-file> [shards]

<list-file> names one VI per line. Each shard harvests its slice into its own COPY of the shipped
parts dir, so nothing shipped is touched and the shards never race on vi-server-props.json; the
union of the keys the copies gained is the answer, written to <work-dir>/report.json with one
copy of each new part under <work-dir>/new. Measured 2026-10-13 (FINDINGS §7b): 800 cache donors
in 2 min on 8 shards, the whole LabVIEW 2026 tree (36 418 VIs) in 56 min on 10.

A FIRST SIGHTING WINS in harvest_parts.py, so a key the shipped parts already hold is never
reported - this measures what is MISSING, not whether a shipped part is any good (§7b's implicit
invoke parts were found by a load probe, probe_vi_server.py, not here).
"""
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
PARTS = os.path.join(COMPOSER, "parts")


def main(argv):
    root, listfile = os.path.abspath(argv[0]), argv[1]
    shards = int(argv[2]) if len(argv) > 2 else 6
    vis = [l.strip() for l in open(listfile, encoding="utf-8") if l.strip()]
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(root)
    procs = []
    for i in range(shards):
        d = os.path.join(root, "s%d" % i)
        shutil.copytree(PARTS, os.path.join(d, "parts"))
        lst = os.path.join(d, "list.txt")
        open(lst, "w", encoding="utf-8").write("\n".join(vis[i::shards]))
        procs.append(subprocess.Popen([sys.executable, "-I", os.path.join(COMPOSER, "harvest_parts.py"),
                                       os.path.join(d, "parts"), "@" + lst],
                                      stdout=open(os.path.join(d, "out.txt"), "w", encoding="utf-8"),
                                      stderr=open(os.path.join(d, "err.txt"), "w", encoding="utf-8")))
    for p in procs:
        p.wait()
    base = set(os.listdir(PARTS))
    basep = json.load(open(os.path.join(PARTS, "vi-server-props.json"), encoding="utf-8"))

    def rows(d, k):
        return {(c, n) for c, v in d.get(k, {}).items() for n in v}
    new, newrows, skips = {}, {"props": set(), "methods": set()}, 0
    for i in range(shards):
        d = os.path.join(root, "s%d" % i, "parts")
        for f in os.listdir(d):
            if f not in base and f.endswith(".xml"):
                new.setdefault(f, os.path.join(d, f))
        p = json.load(open(os.path.join(d, "vi-server-props.json"), encoding="utf-8"))
        for k in newrows:
            newrows[k] |= rows(p, k) - rows(basep, k)
        skips += sum(1 for l in open(os.path.join(root, "s%d" % i, "err.txt"), encoding="utf-8", errors="replace")
                     if l.startswith("SKIP"))
    by_kind = {}
    for f in new:
        by_kind[f.split("__")[0]] = by_kind.get(f.split("__")[0], 0) + 1
    report = {"vis": len(vis), "skipped": skips, "newParts": len(new), "byKind": dict(sorted(by_kind.items())),
              "newRows": {k: len(v) for k, v in newrows.items()}, "parts": sorted(new)}
    json.dump(report, open(os.path.join(root, "report.json"), "w", encoding="utf-8"), indent=1)
    os.makedirs(os.path.join(root, "new"), exist_ok=True)
    for f, src in new.items():
        shutil.copy(src, os.path.join(root, "new", f))
    print(json.dumps({k: v for k, v in report.items() if k != "parts"}, indent=1))


if __name__ == "__main__":
    main(sys.argv[1:])
