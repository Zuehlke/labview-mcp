# -*- coding: utf-8 -*-
"""Find every DYNAMIC event registration in a tree of VIs - no LabVIEW, pylabview only - and record
what LabVIEW wrote for it: each Register For Events node's items (source type, code) and each Event
Structure's EventSpecs, dynamic-terminal registration type and per-frame event data cluster.

    scan_event_regs.py <out.jsonl> <dir> [<dir> ...] [--workers N] [--limit N]
    scan_event_regs.py <out.jsonl> --list <file of VI paths> --enum-labels

One JSON line per VI that has an eventRegNode. The event NAMES are not in the heap; the frames
are numbered (diagramIdx) and NI's AIXML export of the VI names them, in the same order.
Built 2026-10-10 to measure control references as event sources: the item codes cannot be derived.
"""
import json
import queue
import os
import shutil
import sys
import tempfile
import threading
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402


ENUM_LABELS = False


def flat_xml(b, td_text):
    m = lvheap.TYPEID_RE.fullmatch((td_text or "").strip())
    if not m:
        return None
    t = b.heap_type_tree(int(m.group(1)))
    # enum item names are dropped from the survey (they dwarf everything else) and kept with
    # --enum-labels, which the event table needs: an Event Data cluster carries enum fields
    for e in t.iter("TypeDesc") if not ENUM_LABELS else ():
        for c in [c for c in e if c.tag == "EnumLabel"]:
            e.remove(c)
    return ET.tostring(t, encoding="unicode")


def scan(vi):
    work = tempfile.mkdtemp(prefix="scanreg_")
    try:
        copy = os.path.join(work, "x" + os.path.splitext(vi)[1])
        shutil.copyfile(vi, copy)
        try:
            main = lvheap.extract(copy, work, timeout=120)
        except Exception as ex:  # an unreadable VI is skipped, not fatal
            return {"vi": vi, "error": str(ex).splitlines()[0][:200]}
        bd = os.path.join(work, "x_BDHb.xml")
        if not os.path.exists(bd) or "eventRegNode" not in open(bd, encoding="utf-8", errors="replace").read():
            return None
        b = lvheap.Bundle(main)
        out = {"vi": vi, "regs": [], "structs": []}
        for n in b.bd.iter("SL__arrayElement"):
            if n.get("class") == "eventRegNode":
                items = []
                for t in n.find("termList"):
                    d = t.find("dco")
                    if d is not None and d.get("class") == "eventRegItem":
                        # the item dco's own objFlags differ per event: 0x20000 on a Picture's
                        # Mouse Down item, none on its Mouse Up (Dynamic Event Generation.vi)
                        items.append({"code": d.findtext("code"), "type": flat_xml(b, d.findtext("typeDesc")),
                                      "dcoFlags": d.findtext("objFlags") or "0"})
                out["regs"].append({"uid": n.get("uid"), "items": items})
            elif n.get("class") == "eventStruct":
                specs = [{c.tag: (c.text or "").strip() for c in sp} for sp in n.find("EventNodeEvents")]
                dyn = [flat_xml(b, t.find("dco").findtext("typeDesc")) for t in n.find("termList")
                       if t.find("dco") is not None and t.find("dco").get("class") == "eventDynDCO"]
                data = {}
                dl = [c.get("uid") for c in n.find("dataNodeList")]
                for i, du in enumerate(dl):
                    # the NODE, not the first element carrying its uid - a z-order list names it too,
                    # and in a composed VI that entry comes first
                    node = next((x for x in b.bd.iter("SL__arrayElement") if x.get("uid") == du and x.get("class")), None)
                    if node is not None and node.find("termList") is not None:
                        agg = node.find("termList")[0].find("dco")
                        data[str(i)] = flat_xml(b, agg.findtext("typeDesc")) if agg is not None else None
                out["structs"].append({"uid": n.get("uid"), "specs": specs, "dyn": dyn, "data": data})
        return out
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main(argv):
    out_path = argv[0]
    workers = int(argv[argv.index("--workers") + 1]) if "--workers" in argv else 8
    limit = int(argv[argv.index("--limit") + 1]) if "--limit" in argv else None
    global ENUM_LABELS
    ENUM_LABELS = "--enum-labels" in argv
    skip = {i for i, a in enumerate(argv) if a in ("--workers", "--limit", "--list")}
    dirs = [a for i, a in enumerate(argv[1:], 1) if not a.startswith("--") and i - 1 not in skip]
    vis = []
    if "--list" in argv:
        vis += [ln.strip() for ln in open(argv[argv.index("--list") + 1], encoding="utf-8") if ln.strip()]
    for d in dirs:
        for root, _, files in os.walk(d):
            vis += [os.path.join(root, f) for f in files if f.lower().endswith((".vi", ".vit", ".vim"))]
    vis = vis[:limit] if limit else vis
    # threads, not processes: the bundled Python has no multiprocessing, and each extraction is a
    # pylabview SUBPROCESS anyway, so threads run them in parallel
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
            r = scan(v)
            with lock:
                state["done"] += 1
                if r is not None and "error" in r:
                    state["errors"] += 1
                elif r is not None:
                    state["hits"] += 1
                    f.write(json.dumps(r) + "\n")
                    f.flush()
                if state["done"] % 500 == 0:
                    print("%d/%d scanned, %d hits, %d unreadable" % (state["done"], len(vis), state["hits"],
                                                                   state["errors"]), flush=True)

    threads = [threading.Thread(target=worker) for _ in range(workers)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    f.close()
    print("done: %d VIs, %d with a Register For Events, %d unreadable" % (len(vis), state["hits"], state["errors"]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
