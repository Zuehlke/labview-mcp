# -*- coding: utf-8 -*-
"""Reader + composer census over vi.lib and the LVAddons a 32-bit LabVIEW 2026 loads: every
STRIDE-th VI (and every STRIDE-th member of an .llb container FILE) is READ by vi2aixml.py and
COMPOSED by aixml2vi.py, as census_examples.py does for the examples. No LabVIEW, no NI service.

    census_vilib.py <out dir> k/n [stride]

Run n shards (k = 0..n-1) as separate processes with the BUNDLED python.exe; each writes
<out>\\rows.<k>.tsv (index, status, stage, message, source, seconds, area). Then
census_vilib_report.py <out> groups the first composer blocker per VI and EVERY reader note.
Measured 2026-11-08 (stride 3, 14 shards, about 65 min): docs/aixml-backends.md, "The reader and
composer over vi.lib and the LVAddons". LV and ADDONS below name this station's 32-bit 2026.
"""
import os
import sys
import time

EXP = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(EXP, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(0, EXP)
sys.path.insert(0, COMPOSER)
import census_examples  # noqa: E402
import resolve  # noqa: E402

LV = r"C:\Program Files (x86)\National Instruments\LabVIEW 2026"
ADDONS = r"C:\Program Files\NI\LVAddons"


def roots():
    out = [("vi.lib", os.path.join(LV, "vi.lib"))]
    for name, vdir in resolve.select_addons(ADDONS, (26, 3), 32):
        for t in resolve.addon_trees(vdir, 32):
            out.append(("addon:" + name, t))
    return out


def listing():
    vis, llbs = [], []
    seen = set()
    for area, r in roots():
        for dp, dn, fn in os.walk(r):
            if os.path.normcase(dp) in seen:
                continue
            seen.add(os.path.normcase(dp))
            # Targets\win32 is walked as its own root; skip it inside the version folder
            dn[:] = [d for d in dn if d.lower() != "targets"]
            for f in fn:
                p = os.path.join(dp, f)
                if f.lower().endswith(".vi"):
                    vis.append((area, p))
                elif f.lower().endswith(".llb"):
                    llbs.append((area, p))
    vis.sort(key=lambda t: t[1].lower())
    llbs.sort(key=lambda t: t[1].lower())
    return vis, llbs


def main(argv):
    out = argv[0]
    k, n = (int(x) for x in argv[1].split("/"))
    stride = int(argv[2]) if len(argv) > 2 else 3
    census_examples.init()
    for d in ("aixml", "vis"):
        os.makedirs(os.path.join(out, d), exist_ok=True)
    vis, llbs = listing()
    jobs = [(i, a, p) for i, (a, p) in enumerate(vis) if i % stride == 0]
    if k == 0:
        with open(os.path.join(out, "inventory.txt"), "w", encoding="utf-8") as f:
            f.write("vis %d llbs %d sampled-vis %d\n" % (len(vis), len(llbs), len(jobs)))
    base = 1000000
    t0 = time.time()
    with open(os.path.join(out, "rows.%d.tsv" % k), "w", encoding="utf-8") as f:
        def do(idx, area, path):
            r = census_examples.one((idx, path, out))
            f.write("\t".join([str(r[0]), r[1], r[2], r[3].replace("\t", " "), r[4], "%.2f" % r[5], area]) + "\n")
            f.flush()
        for j, (i, area, p) in enumerate(jobs):
            if j % n == k:
                do(i, area, p)
        # .llb containers: shard owns container c when c % n == k; every stride-th member
        for c, (area, llb) in enumerate(llbs):
            if c % n != k:
                continue
            try:
                members = sorted(resolve.llb_members(llb))
            except Exception as ex:  # noqa: BLE001
                f.write("\t".join([str(base + c * 1000), "LLBFAIL", "list", str(ex)[:200], llb, "0", area]) + "\n")
                continue
            for m_i, m in enumerate(members):
                if not m.endswith(".vi") or m_i % stride:
                    continue
                do(base + c * 1000 + m_i, area, os.path.join(llb, m))
    print("shard %d done %.0fs" % (k, time.time() - t0))


if __name__ == "__main__":
    main(sys.argv[1:])
