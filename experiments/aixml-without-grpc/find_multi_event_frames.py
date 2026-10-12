# -*- coding: utf-8 -*-
"""Find Event Structure frames that handle SEVERAL events, in NI's cached AIXML exports - the corpus
for measuring what LabVIEW gives such a frame as its event data (2026-10-10).

    find_multi_event_frames.py <out.json> [--cache DIR]

For every cached export: each Event Structure frame whose selector names more than one (source,
event) pair, with the pairs, the Event Data Node's fields as the export names them, and the VI path
(from the cache's .json sidecar). Prints a tally by event-kind combination.
"""
import collections
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
from aixml2vi import event_items, unescape  # noqa: E402

CACHE = os.path.join(os.environ.get("USERPROFILE", ""), ".labviewmcp", "cache", "aixml")


def kind_of(item):
    """'"Stop": Value Change' -> 'static:Value Change'; '<Tick>: User Event' -> 'user'; '<P>: Mouse
    Down' -> 'dyn:Mouse Down'; 'Timeout' -> 'Timeout'."""
    if ":" not in item:
        return item
    src, ev = item.rsplit(":", 1)
    src, ev = src.strip(), ev.strip()
    if ev == "User Event":
        return "user"
    if src.startswith("<"):
        return "dyn:" + ev
    return "static:" + ev


def main(argv):
    out_path = argv[0]
    cache = argv[argv.index("--cache") + 1] if "--cache" in argv else CACHE
    hits, tally = [], collections.Counter()
    for x in glob.glob(os.path.join(cache, "*.xml")):
        try:
            root = ET.parse(x).getroot()
        except ET.ParseError:
            continue
        meta = x[:-4] + ".json"
        vi = ""
        if os.path.exists(meta):
            try:
                vi = json.load(open(meta, encoding="utf-8")).get("ViPath") or ""
            except ValueError:
                pass
        for st in root.iter("Structure"):
            if st.get("_name") != "Event Structure":
                continue
            frames = [f for f in st if f.tag == "CaseFrame"]
            for i, f in enumerate(frames):
                items = event_items(unescape(f.get("selector") or ""))
                if len(items) < 2:
                    continue
                edn = next((n for n in f if n.tag == "Node" and n.get("_name") == "Event Data Node"), None)
                kinds = sorted({kind_of(it) for it in items})
                tally[" + ".join(kinds)] += 1
                hits.append({"vi": vi, "export": x, "struct": st.get("uid"), "frame": i, "items": items,
                             "fields": edn.get("fields") if edn is not None else None})
    json.dump(hits, open(out_path, "w", encoding="utf-8"), indent=1)
    print("%d multi-event frames in %d VIs" % (len(hits), len({h["vi"] for h in hits})))
    for k, n in tally.most_common():
        print("  %4d  %s" % (n, k))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
