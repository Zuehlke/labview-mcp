# -*- coding: utf-8 -*-
"""Every STATIC event NI's VIs handle, with what LabVIEW wrote for it - the table static events
are composed from (2026-10-10).

    static_event_survey.py <out.json> [--cache DIR] [--workers N]

For each cached AIXML export with an Event Structure: the frames handling exactly ONE static event
(`"Graph": Plot Attribute Change`, ` Panel Close `) are joined, by structure uid and frame index,
with the VI's own heap (event_frame_data.scan) - the frame's EventSpec, its source control's ddo
class and type, and the event data cluster. Aggregated per event name: every spec variant seen,
the source classes, and one data cluster per source ddo class (the CtlRef in it is the source's).
"""
import collections
import glob
import json
import os
import queue
import sys
import threading
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
from aixml2vi import event_items, unescape  # noqa: E402
import event_frame_data  # noqa: E402

CACHE = os.path.join(os.environ.get("USERPROFILE", ""), ".labviewmcp", "cache", "aixml")


def static_name(item):
    """'"Graph": Plot Attribute Change' -> ('Graph', 'Plot Attribute Change'); 'Panel Close' ->
    (None, 'Panel Close'); None for a user, dynamic or timeout event."""
    if item.strip() == "Timeout":
        return None
    if ":" not in item:
        return (None, item.strip())
    src, ev = item.rsplit(":", 1)
    src, ev = src.strip(), ev.strip()
    if src.startswith("<") or ev == "User Event":
        return None
    return (src.strip('"'), ev)


def main(argv):
    out_path = argv[0]
    cache = argv[argv.index("--cache") + 1] if "--cache" in argv else CACHE
    workers = int(argv[argv.index("--workers") + 1]) if "--workers" in argv else 8
    todo = []
    for x in glob.glob(os.path.join(cache, "*.xml")):
        try:
            root = ET.parse(x).getroot()
        except ET.ParseError:
            continue
        frames = {}
        for st in root.iter("Structure"):
            if st.get("_name") == "Event Structure":
                frames[st.get("uid")] = [event_items(unescape(f.get("selector") or "")) for f in st if f.tag == "CaseFrame"]
        if not any(len(it) == 1 and static_name(it[0]) for fr in frames.values() for it in fr):
            continue
        meta = x[:-4] + ".json"
        try:
            vi = json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else None
        except ValueError:
            vi = None
        if vi and os.path.exists(vi):
            todo.append((vi, frames))
    print("%d VIs with single static event frames" % len(todo), flush=True)
    q = queue.Queue()
    for t in todo:
        q.put(t)
    lock = threading.Lock()
    rows = []

    def worker():
        while True:
            try:
                vi, frames = q.get_nowait()
            except queue.Empty:
                return
            try:
                recs = event_frame_data.scan(vi, False)
            except Exception as ex:  # an unreadable VI is skipped
                print("UNREADABLE", vi, str(ex).splitlines()[0][:120], flush=True)
                continue
            got = []
            for rec in recs:
                fr = frames.get(rec["uid"])
                if fr is None:
                    continue
                for f in rec["frames"]:
                    items = fr[f["idx"]] if f["idx"] < len(fr) else []
                    if len(items) != 1 or len(f["specs"]) != 1 or not static_name(items[0]):
                        continue
                    src, ev = static_name(items[0])
                    sp = f["specs"][0]
                    so = f["sources"].get(sp.get("ddoUID"))
                    got.append({"vi": vi, "event": ev, "source": src,
                                "spec": {k: sp[k] for k in ("source", "regFlags", "eSource", "type", "eFlags")},
                                "class": so["class"] if so else None, "srcType": so["type"] if so else None,
                                "data": f["data"]})
            with lock:
                rows.extend(got)

    threads = [threading.Thread(target=worker) for _ in range(workers)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    by = collections.defaultdict(lambda: {"specs": collections.Counter(), "classes": collections.Counter(),
                                          "data": {}, "vis": set()})
    for r in rows:
        e = by[r["event"]]
        e["specs"][json.dumps(r["spec"], sort_keys=True)] += 1
        e["classes"][r["class"]] += 1
        e["data"].setdefault(r["class"] or "-", {"data": r["data"], "srcType": r["srcType"], "vi": r["vi"]})
        e["vis"].add(r["vi"])
    out = {ev: {"specs": {k: n for k, n in v["specs"].items()}, "classes": dict(v["classes"]),
                "data": v["data"], "vis": sorted(v["vis"])} for ev, v in by.items()}
    json.dump(out, open(out_path, "w", encoding="utf-8"), indent=1, sort_keys=True)
    print("%d frames, %d static event kinds" % (len(rows), len(out)))
    for ev in sorted(out, key=lambda e: -sum(out[e]["classes"].values())):
        print("  %-32s %4d frames  classes %s  specs %s" % (ev, sum(out[ev]["classes"].values()),
                                                            dict(out[ev]["classes"]), list(out[ev]["specs"].values())))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
