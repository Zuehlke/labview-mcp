# -*- coding: utf-8 -*-
"""One probe VI per harvested graph/chart part (parts/fpdco__stdGraphNNNNN__*.xml): the control or
indicator alone, typed with the part's own data type and styled graphNNNNN, so the composer takes
exactly that part. Listed for cov_load.py; a probe that does not load executable names a part whose
panel LabVIEW cannot load (`Cannot load front panel`, 1013).

    graph_part_probes.py <out dir>
"""
import html
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
PARTS = os.path.join(COMPOSER, "parts")
PY = os.path.normpath(os.path.join(HERE, "..", "..", "tools", "pylabview", "runtime", "python.exe"))
sys.path.insert(0, COMPOSER)
import vi2aixml  # noqa: E402


def main(argv):
    out = argv[0]
    os.makedirs(os.path.join(out, "vis"), exist_ok=True)
    rows = []
    for f in sorted(os.listdir(PARTS)):
        m = re.fullmatch(r"fpdco__stdGraph(\d+)__(ind|ctl)__(.+)\.xml", f)
        if not m:
            continue
        num, direction = m.group(1), m.group(2)
        root = ET.parse(os.path.join(PARTS, f)).getroot()
        tree = root.find("types/TypeDesc")
        t = vi2aixml.aixml_type(tree) if tree is not None else None
        if not t:
            print("no aixml type for", f)
            continue
        name = "g%s_%s_%s" % (num, direction, re.sub(r"\W+", "_", m.group(3)))
        tag = "Indicator" if direction == "ind" else "Control"
        io = 'inputs="value:"' if direction == "ind" else 'outputs="value:"'
        x = os.path.join(out, name + ".xml")
        open(x, "w", encoding="utf-8").write(
            '<VI _name="%s.vi" description="x">\n  <%s _name="G" type="%s" style="graph%s" uid="4201" '
            'uid_parent="root" %s />\n</VI>\n' % (name, tag, html.escape(t, quote=True), num, io))
        vi = os.path.join(out, "vis", name + ".vi")
        r = subprocess.run([PY, "-W", "ignore::SyntaxWarning", os.path.join(COMPOSER, "aixml2vi.py"), x, vi],
                           capture_output=True, text=True, stdin=subprocess.DEVNULL)
        log = r.stdout + r.stderr
        used = "not composed" not in log
        if os.path.exists(vi):
            rows.append("%s\t%s | %s | %s" % (os.path.normpath(vi), f, t, "part used" if used else "DEGRADED"))
        else:
            print("refused", f, log.strip().splitlines()[-1][:120] if log.strip() else "")
    with open(os.path.join(out, "composed.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(rows) + "\n")
    print(len(rows), "probes")


if __name__ == "__main__":
    main(sys.argv[1:])
