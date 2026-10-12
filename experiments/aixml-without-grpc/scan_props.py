# -*- coding: utf-8 -*-
"""Find the VIs of a tree that use a VI Server PROPERTY - no LabVIEW, pylabview only - so its row
can be harvested from a VI of the installation itself (harvest_parts.py --props) rather than from
NI's converter's build of our own AIXML.

    scan_props.py <out.jsonl> <dir> [<dir> ...] --name "Update While Typing?" [--code N]
                  [--workers N] [--limit N]

One JSON line per VI whose block diagram holds a property node naming one of the properties: the
VI, and per match the property name, its code and whether it is read or written. Built 2026-10-12
for `{LV.String}` `Update While Typing?`, which none of the cached NI exports uses.
"""
import json
import os
import queue
import shutil
import sys
import tempfile
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import lvheap  # noqa: E402


def scan(vi, names, codes):
    work = tempfile.mkdtemp(prefix="scanprop_")
    try:
        copy = os.path.join(work, "x" + os.path.splitext(vi)[1])
        shutil.copyfile(vi, copy)
        try:
            lvheap.extract(copy, work, timeout=120)
        except Exception as ex:  # an unreadable VI is skipped, not fatal
            return {"vi": vi, "error": str(ex).splitlines()[0][:200]}
        bd = os.path.join(work, "x_BDHb.xml")
        if not os.path.exists(bd):
            return None
        text = open(bd, encoding="utf-8", errors="replace").read()
        if not any(n in text for n in names) and not any(str(c) in text for c in codes):
            return None
        import xml.etree.ElementTree as ET
        root = ET.parse(bd).getroot()
        hits = []
        for info in root.iter("SL__arrayElement"):
            name = (info.findtext("PropItemName") or "").strip('"')
            code = info.findtext("PropItemCode")
            if name in names or (code and code.isdigit() and int(code) in codes):
                hits.append({"name": name, "code": code})
        return {"vi": vi, "hits": hits} if hits else None
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main(argv):
    out_path = argv[0]
    opts = {"--workers", "--limit", "--name", "--code"}
    workers = int(argv[argv.index("--workers") + 1]) if "--workers" in argv else 8
    limit = int(argv[argv.index("--limit") + 1]) if "--limit" in argv else None
    names = {argv[i + 1] for i, a in enumerate(argv) if a == "--name"}
    codes = {int(argv[i + 1]) for i, a in enumerate(argv) if a == "--code"}
    skip = {i + 1 for i, a in enumerate(argv) if a in opts}
    dirs = [a for i, a in enumerate(argv) if i > 0 and not a.startswith("--") and i not in skip]
    vis = []
    for d in dirs:
        for root, _, files in os.walk(d):
            vis += [os.path.join(root, f) for f in files if f.lower().endswith((".vi", ".vit", ".vim", ".ctl"))]
    vis = vis[:limit] if limit else vis
    # threads, not processes: each extraction is a pylabview SUBPROCESS, so threads run them in parallel
    work = queue.Queue()
    for v in vis:
        work.put(v)
    lock = threading.Lock()
    state = {"done": 0, "hits": 0, "errors": 0}
    f = open(out_path, "w", encoding="utf-8")

    def worker():
        while True:
            try:
                v = work.get_nowait()
            except queue.Empty:
                return
            r = scan(v, names, codes)
            with lock:
                state["done"] += 1
                if r is not None and "error" in r:
                    state["errors"] += 1
                elif r is not None:
                    state["hits"] += 1
                    f.write(json.dumps(r) + "\n")
                    f.flush()
                    print("HIT %s" % v, flush=True)
                if state["done"] % 500 == 0:
                    print("%d/%d scanned, %d hits, %d unreadable" % (state["done"], len(vis), state["hits"],
                                                                   state["errors"]), flush=True)

    threads = [threading.Thread(target=worker) for _ in range(workers)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    f.close()
    print("done: %d VIs, %d using the property, %d unreadable" % (len(vis), state["hits"], state["errors"]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
