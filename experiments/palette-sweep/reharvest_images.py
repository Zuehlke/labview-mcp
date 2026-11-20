# -*- coding: utf-8 -*-
"""Re-cut the shipped parts whose embedded images (positive ImageResIDs) are not in the part.

Parts harvested before 2026-11-13 read images from DSIM only; a donor saved by LabVIEW 2023+ keeps
them in MNGI, so those parts came without them and a composed VI holding one answers `Cannot load
front panel` (1013) - measured 2026-11-19 on a slide, a knob and a tab control (palette sweep).
Each part is cut again from its own donor into a scratch dir; it replaces the shipped file only
when the new cut carries every image it names and is the same part otherwise.

    <bundle>\\python.exe reharvest_images.py <workdir> [--apply]
"""
import glob
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
PARTS = os.path.join(COMPOSER, "parts")
sys.path.insert(0, COMPOSER)
import lvheap  # noqa: E402
import resolve  # noqa: E402


def missing_images(text):
    ids = {int(x) for x in re.findall(r"<ImageResID>(\d+)</ImageResID>", text) if int(x) > 0}
    have = {int(x) for x in re.findall(r'<image index="(\d+)"', text)}
    return ids - have


def donor_path(src):
    root = resolve.Resolver().lv_root if hasattr(resolve.Resolver(), "lv_root") else \
        r"C:\Program Files (x86)\National Instruments\LabVIEW 2026"
    return src.replace("&lt;", "<").replace("&gt;", ">").replace("<labview>", root)


def main(argv):
    work = os.path.abspath(argv[0])
    apply = "--apply" in argv
    todo = {}
    for f in glob.glob(os.path.join(PARTS, "*.xml")):
        t = open(f, encoding="utf-8", errors="replace").read()
        if missing_images(t):
            m = re.search(r'source="([^"]*)"', t)
            if m:
                todo.setdefault(donor_path(m.group(1)), []).append(os.path.basename(f))
    shutil.rmtree(work, ignore_errors=True)
    out = os.path.join(work, "parts")
    os.makedirs(out)
    donors = [d for d in todo if os.path.exists(d)]
    for d in todo:
        if d not in donors:
            print("donor missing: %s (%s)" % (d, ", ".join(todo[d])))
    subprocess.run([sys.executable, "-I", os.path.join(COMPOSER, "harvest_parts.py"), out] + donors,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    fixed = 0
    for d, files in todo.items():
        for name in files:
            new = os.path.join(out, name)
            if not os.path.exists(new):
                print("not re-cut: %s" % name)
                continue
            t = open(new, encoding="utf-8", errors="replace").read()
            if missing_images(t):
                print("still without images: %s %s" % (name, sorted(missing_images(t))))
                continue
            print("re-cut with images: %s" % name)
            fixed += 1
            if apply:
                shutil.copy(new, os.path.join(PARTS, name))
    print("%d of %d parts re-cut%s" % (fixed, sum(map(len, todo.values())), " and replaced" if apply else ""))


if __name__ == "__main__":
    main(sys.argv[1:])
