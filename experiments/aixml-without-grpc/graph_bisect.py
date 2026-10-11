# -*- coding: utf-8 -*-
"""Which composed graph object leaves LabVIEW's COM server hanging after a load? One VI per
indicator of a graph probe, each loaded in a FRESH LabVIEW (lv-restart.ps1), then a second COM call
with a deadline - the hang shows there, as graph_probe.py measures it.

    graph_bisect.py [probes/pgraph.xml] [--only REGEX]
"""
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
sys.path.insert(0, HERE)
import graph_probe  # noqa: E402


def restart():
    subprocess.run(["powershell", "-ExecutionPolicy", "Bypass", "-File", os.path.join(HERE, "lv-restart.ps1")],
                   capture_output=True, text=True)


def main(argv):
    src = next((a for a in argv if a.endswith(".xml")), os.path.join(HERE, "probes", "pgraph.xml"))
    only = re.compile(argv[argv.index("--only") + 1]) if "--only" in argv else None
    vi_el = ET.parse(src).getroot()
    out = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "graph-bisect")
    os.makedirs(out, exist_ok=True)
    env = dict(os.environ, AIXML2VI_GRAPHS="1")
    for ind in [e for e in vi_el if e.tag == "Indicator"]:
        name = ind.get("_name")
        if only and not only.search(name):
            continue
        doc = ET.Element("VI", vi_el.attrib)
        doc.append(ind)
        xml = os.path.join(out, re.sub(r"\W+", "_", name) + ".xml")
        ET.ElementTree(doc).write(xml, encoding="utf-8")
        vi = xml[:-4] + ".vi"
        r = subprocess.run([PY, os.path.join(HERE, "aixml2vi.py"), xml, vi], capture_output=True, text=True, env=env)
        if not os.path.exists(vi):
            print("%-10s %-24s COMPOSE FAILED %s" % (name, ind.get("style"), r.stderr.strip()[-120:]), flush=True)
            continue
        restart()
        first = graph_probe.com_state(vi, 60)
        time.sleep(2)
        second = graph_probe.com_state(vi, 30)
        print("%-10s %-12s %-40s load=%s again=%s" % (name, ind.get("style"), ind.get("type"), first, second), flush=True)
    restart()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
