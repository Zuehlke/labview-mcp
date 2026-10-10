# -*- coding: utf-8 -*-
"""Why is a composed VI broken? Split its AIXML into the independent pieces of its root diagram
(elements joined by nets; a structure with everything inside it is one unit), compose each piece
as a VI of its own, load it over COM and report the pieces that are broken.

    bisect_vi.py <export.xml> [--out DIR] [--near ORIGINAL.vi]

--near resolves the pieces' calls beside the original VI, as coverage.py composes it.
"""
import os
import sys
import time
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402
import selftest  # noqa: E402


def nets_of(el):
    out = set()
    for e in el.iter():
        for a in ("inputs", "outputs"):
            for _, n in aixml2vi.pairs(e.get(a)):
                if n:
                    out.add(n)
        for a in ("selectin", "selectout", "count", "maxin", "maxout"):
            if e.get(a):
                out.add(e.get(a))
    return out


def main(argv):
    src = argv[0]
    out = argv[argv.index("--out") + 1] if "--out" in argv else r"C:\temp\aixml-no-grpc\bisect"
    near = argv[argv.index("--near") + 1] if "--near" in argv else None
    os.makedirs(out, exist_ok=True)
    root = ET.parse(src).getroot()
    units = [e for e in root if isinstance(e.tag, str) and e.tag != "FreeLabel"]
    nets = [nets_of(u) for u in units]
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
    for i in range(len(units)):
        groups.setdefault(find(i), []).append(units[i])
    stamp = time.strftime("%H%M%S")
    print("%d units in %d pieces" % (len(units), len(groups)))
    for k, (g, members) in enumerate(sorted(groups.items())):
        vi_el = ET.Element("VI", {"_name": "bisect_%s_%d.vi" % (stamp, k), "description": ""})
        for m in members:
            vi_el.append(m)
        x = os.path.join(out, "bisect_%s_%d.xml" % (stamp, k))
        ET.ElementTree(vi_el).write(x, encoding="utf-8")
        vi = x[:-4] + ".vi"
        if near:
            import subprocess
            r = subprocess.run([selftest.PY, os.path.join(HERE, "aixml2vi.py"), x, vi, "--search", os.path.dirname(near)],
                               capture_output=True, text=True, stdin=subprocess.DEVNULL)
            code, log = r.returncode, (r.stdout + r.stderr).strip()
        else:
            code, log = selftest.compose(x, vi)
        names = ", ".join(sorted({(m.get("_name") or m.get("target") or m.tag)[:30] for m in members}))[:150]
        if code != 0:
            print("piece %d  REFUSED  %s  [%s]" % (k, log.splitlines()[-1][:120] if log else "", names))
            continue
        state, _, _ = selftest.lvcom(vi, "", [], run=False)
        print("piece %d  ExecState %s  [%s]" % (k, state, names))


if __name__ == "__main__":
    main(sys.argv[1:])
