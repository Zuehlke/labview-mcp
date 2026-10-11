# -*- coding: utf-8 -*-
"""Find STATIC event specs in a tree of VIs that the composer's table does not cover - pylabview
only, no LabVIEW (2026-10-11).

    scan_static_specs.py <out.jsonl> <dir> [<dir> ...] [--workers N] [--limit N]

For every VI with an Event Structure: each EventSpec whose source is the APPLICATION (4) or whose
type number parts/static-events.json does not know, with the frame's event data and filter cluster
(event_frame_data.scan). Built because NI's 768 cached exports hold ONE application event (VI
Activation) and the event-type enum names three more - App Instance Close, App Instance Close? and
NI Security User Change (App) - whose type numbers are not derivable: the enum is alphabetical,
the numbers are LabVIEW's own. NI's exporter then names a hit's frames.
"""
import json
import os
import queue
import sys
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import event_frame_data  # noqa: E402

KNOWN = {1073741826, 1073741825, 1073741839, 1073741838, 1000}


def known_types():
    t = json.load(open(os.path.join(HERE, "parts", "static-events.json"), encoding="utf-8"))
    return KNOWN | {int(r["spec"]["type"]) for r in t.values() if r.get("spec")}


def main(argv):
    out_path = argv[0]
    dirs = [a for a in argv[1:] if not a.startswith("--") and not a.isdigit()]
    workers = int(argv[argv.index("--workers") + 1]) if "--workers" in argv else 8
    limit = int(argv[argv.index("--limit") + 1]) if "--limit" in argv else None
    known = known_types()
    vis = []
    for d in dirs:
        for root, _, files in os.walk(d):
            vis += [os.path.join(root, f) for f in files if f.lower().endswith((".vi", ".vit"))]
    if limit:
        vis = vis[:limit]
    print("%d VIs" % len(vis), flush=True)
    q = queue.Queue()
    for v in vis:
        q.put(v)
    lock = threading.Lock()
    done = [0]
    out = open(out_path, "w", encoding="utf-8")

    def worker():
        while True:
            try:
                vi = q.get_nowait()
            except queue.Empty:
                return
            try:
                recs = event_frame_data.scan(vi, False)
            except Exception:
                recs = []
            hits = []
            for rec in recs:
                for f in rec["frames"]:
                    for s in f["specs"]:
                        typ = int(s.get("type") or 0)
                        if s.get("source") == "4" or typ not in known:
                            hits.append({"vi": vi, "uid": rec["uid"], "frame": f["idx"], "spec": s,
                                         "data": f["data"], "filter": f.get("filter")})
            with lock:
                for h in hits:
                    out.write(json.dumps(h) + "\n")
                out.flush()
                done[0] += 1
                if done[0] % 500 == 0:
                    print("  %d of %d" % (done[0], len(vis)), flush=True)

    threads = [threading.Thread(target=worker) for _ in range(workers)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    out.close()
    print("wrote", out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
