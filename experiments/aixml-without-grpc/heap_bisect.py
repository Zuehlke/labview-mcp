# -*- coding: utf-8 -*-
"""Which heap round trip breaks a VI? Copy an untouched tree, round-trip ONLY the given files in the
copy (pylabview extract -> rebuild over itself), restart LabVIEW and load the given targets.

    heap_bisect.py <untouched tree> <work dir> --targets a.vi;b.vi --roundtrip x.vi;y.ctl [--all-in DIR]

Paths after --targets / --roundtrip are relative to the tree; `--all-in DIR` round-trips every
.vi/.vim/.ctl under that sub-folder. Prints each target's ExecState from lv-com.ps1. The census
that found the breaks: census_heap.py; docs/aixml-backends.md, "The heap route".
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview")))
import lvheap  # noqa: E402

EXT = (".vi", ".vim", ".ctl")


def roundtrip(path):
    tmp = tempfile.mkdtemp(prefix="hb_")
    try:
        main_xml = lvheap.extract(path, tmp)
        out = os.path.join(tmp, "rebuilt" + os.path.splitext(path)[1])
        lvheap.rebuild(main_xml, out)
        os.chmod(path, 0o666)
        shutil.copyfile(out, path)
        return "ok"
    except Exception as ex:
        return "FAIL %s" % str(ex)[-200:]
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main(argv):
    src, work = argv[0], argv[1]
    def opt(name):
        return argv[argv.index(name) + 1] if name in argv else ""
    targets = [t for t in opt("--targets").split(";") if t]
    files = [f for f in opt("--roundtrip").split(";") if f]
    if opt("--all-in"):
        base = os.path.join(src, opt("--all-in"))
        files += [os.path.relpath(os.path.join(dp, f), src) for dp, _, fn in os.walk(base)
                  for f in fn if f.lower().endswith(EXT)]
    # LabVIEW refuses a path that mixes / and \ (File not found), so normalise once here
    src, work = os.path.normpath(src), os.path.normpath(work)
    tree = os.path.join(work, "tree")
    shutil.rmtree(tree, ignore_errors=True)
    shutil.copytree(src, tree)
    for f in files:
        print("roundtrip %-70s %s" % (f[-70:], roundtrip(os.path.join(tree, f))))
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                    os.path.join(HERE, "lv-restart.ps1")], capture_output=True, timeout=300,
                   stdin=subprocess.DEVNULL)
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-com.ps1"), "-ViPath",
                        "|".join(os.path.join(tree, t) for t in targets)],
                       capture_output=True, text=True, timeout=600, stdin=subprocess.DEVNULL)
    for vi, state, fail in re.findall(r"=== (.*)\r?\n\s+(?:ExecState = (\S+)|GetVIReference FAILED: (.*))", r.stdout):
        print("TARGET %-70s %s" % (os.path.relpath(vi, tree)[-70:], state or ("open failed: " + fail[:120])))


if __name__ == "__main__":
    main(sys.argv[1:])
