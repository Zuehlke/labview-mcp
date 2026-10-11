# -*- coding: utf-8 -*-
"""Compose NI's own exports that carry GRAPHS (style="graphNNNNN") and load each over COM: does it
load, is it executable, and does COM still answer afterwards - the failure composed charts had
(graph_probe.py). LabVIEW is restarted only after a hang or a crash.

    graph_sample.py [--max N] [--only REGEX]
"""
import glob
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
sys.path.insert(0, HERE)
import graph_probe  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def restart():
    subprocess.run(["powershell", "-ExecutionPolicy", "Bypass", "-File", os.path.join(HERE, "lv-restart.ps1")],
                   capture_output=True, text=True)


def main(argv):
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 30
    only = re.compile(argv[argv.index("--only") + 1]) if "--only" in argv else None
    out = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "graph-sample")
    os.makedirs(out, exist_ok=True)
    picked = []
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize):
        text = open(x, encoding="utf-8", errors="replace").read()
        if not re.search(r'style="graph\d+"', text):
            continue
        meta = x[:-4] + ".json"
        name = json.load(open(meta, encoding="utf-8")).get("ViPath", x) if os.path.exists(meta) else x
        if only and not only.search(name):
            continue
        picked.append((x, name))
        if len(picked) >= limit:
            break
    counts = {"loads": 0, "broken": 0, "hang": 0, "refused": 0}
    for i, (x, name) in enumerate(picked):
        vi = os.path.join(out, "g%03d.vi" % i)
        r = subprocess.run([PY, os.path.join(HERE, "aixml2vi.py"), x, vi], capture_output=True, text=True)
        graphs = len(re.findall(r"Indicator .* <- .*stdGraph|Control .* <- .*stdGraph", r.stdout))
        if not os.path.exists(vi):
            counts["refused"] += 1
            print("%3d REFUSED  %s" % (i, os.path.basename(name)), flush=True)
            continue
        first = graph_probe.com_state(vi, 60)
        second = graph_probe.com_state(vi, 30)
        state = " ".join(first)
        if "TIMEOUT" in " ".join(second) or "TIMEOUT" in state or "remote procedure" in state:
            counts["hang"] += 1
            verdict = "HANG"
            restart()
        elif "ExecState = 1" in state:
            counts["loads"] += 1
            verdict = "ok"
        else:
            counts["broken"] += 1
            verdict = "BROKEN"
        print("%3d %-6s %-60s %s" % (i, verdict, os.path.basename(name)[:60], state[:60]), flush=True)
    print("SUMMARY", counts)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
