# -*- coding: utf-8 -*-
"""Which part of a composed VI kills LabVIEW on load? bisect_vi.py's split - the independent pieces
of the root diagram (elements joined by nets; a structure with everything inside it is one unit) -
composed each as a VI of its own, the reader's trailer kept for the piece's uids (call links, panel
classes), and listed for cov_load.py, which survives the crash and restarts LabVIEW.

    crash_pieces.py <out dir> <export.xml>=<original.vi> [...]

Then: cov_load.py <out dir>. A piece that CRASHes holds the construct; split it further by hand.
"""
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(0, HERE)
import bisect_vi  # noqa: E402

PY = os.path.normpath(os.path.join(HERE, "..", "..", "tools", "pylabview", "runtime", "python.exe"))


def pieces(src):
    root = ET.parse(src).getroot()
    units = [e for e in root if isinstance(e.tag, str) and e.tag != "FreeLabel"]
    nets = [bisect_vi.nets_of(u) for u in units]
    parent = list(range(len(units)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    for i in range(len(units)):
        for j in range(i):
            if nets[i] & nets[j]:
                parent[find(i)] = find(j)
    groups = {}
    for i, u in enumerate(units):
        groups.setdefault(find(i), []).append(u)
    return list(groups.values())


def main(argv):
    out = argv[0]
    os.makedirs(out, exist_ok=True)
    rows = []
    for pair in argv[1:]:
        src, near = pair.split("=", 1)
        tail = open(src, encoding="utf-8").read().rsplit("</VI>", 1)[1]
        pis = re.findall(r"<\?lvmcp[^\n]*\?>", tail)
        tag = re.sub(r"\W+", "", os.path.basename(near))[:16]
        for k, members in enumerate(pieces(src)):
            uids = {e.get("uid") for m in members for e in m.iter() if e.get("uid")}
            keep = [p for p in pis if (re.search(r'uid="(\d+)"', p) or [None, None])[1] in uids
                    or not re.search(r'uid="', p)]
            vi_el = ET.Element("VI", {"_name": "%s_p%d.vi" % (tag, k), "description": "x"})
            for m in members:
                vi_el.append(m)
            x = os.path.join(out, "%s_p%d.xml" % (tag, k))
            ET.ElementTree(vi_el).write(x, encoding="utf-8")
            with open(x, "a", encoding="utf-8") as f:
                f.write("\n" + "\n".join(keep) + "\n")
            vi = x[:-4] + ".vi"
            r = subprocess.run([PY, "-W", "ignore::SyntaxWarning", os.path.join(COMPOSER, "aixml2vi.py"),
                                x, vi, "--search", os.path.dirname(near)], capture_output=True, text=True,
                               stdin=subprocess.DEVNULL)
            names = ", ".join(sorted({(m.get("_name") or m.get("target") or m.tag)[:24] for m in members}))[:140]
            if not os.path.exists(vi):
                print("%s_p%d REFUSED %s" % (tag, k, (r.stdout + r.stderr).strip().splitlines()[-1][:100]))
                continue
            print("%s_p%d [%s]" % (tag, k, names))
            rows.append(vi + "\t" + x)
    with open(os.path.join(out, "composed.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(rows) + "\n")


if __name__ == "__main__":
    main(sys.argv[1:])
