# -*- coding: utf-8 -*-
"""What does a pylabview round trip change in a VI? Extract the ORIGINAL and the REBUILT file with
pylabview and compare the two bundles file by file (XML text, binary sidecars). Identical bundles
mean the change is below what pylabview models: heap_rawdiff.py compares the decoded blocks.

    heap_diff.py <original.vi> <rebuilt.vi> [--show N]
"""
import difflib
import filecmp
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview")))
import lvheap  # noqa: E402


def main(argv):
    a, b = argv[0], argv[1]
    show = int(argv[argv.index("--show") + 1]) if "--show" in argv else 40
    ta, tb = tempfile.mkdtemp(prefix="hdA_"), tempfile.mkdtemp(prefix="hdB_")
    ma, mb = lvheap.extract(a, ta), lvheap.extract(b, tb)
    fa, fb = sorted(os.listdir(ta)), sorted(os.listdir(tb))
    ra = {f.replace(os.path.splitext(os.path.basename(ma))[0], "X"): f for f in fa}
    rb = {f.replace(os.path.splitext(os.path.basename(mb))[0], "X"): f for f in fb}
    print("only in original:", sorted(set(ra) - set(rb)))
    print("only in rebuilt :", sorted(set(rb) - set(ra)))
    for k in sorted(set(ra) & set(rb)):
        pa, pb = os.path.join(ta, ra[k]), os.path.join(tb, rb[k])
        if filecmp.cmp(pa, pb, shallow=False):
            continue
        if k.endswith(".xml"):
            la = open(pa, encoding="utf-8", errors="replace").read().splitlines()
            lb = open(pb, encoding="utf-8", errors="replace").read().splitlines()
            d = [l for l in difflib.unified_diff(la, lb, lineterm="", n=1)]
            print("== %s: %d diff lines" % (k, len(d)))
            for l in d[:show]:
                print("   " + l[:220])
        else:
            sa, sb = os.path.getsize(pa), os.path.getsize(pb)
            da, db = open(pa, "rb").read(), open(pb, "rb").read()
            first = next((i for i in range(min(sa, sb)) if da[i] != db[i]), min(sa, sb))
            print("== %s: binary differs, sizes %d -> %d, first difference at %d" % (k, sa, sb, first))
    print("file sizes: %d -> %d" % (os.path.getsize(a), os.path.getsize(b)))
    print("bundles:", ta, tb)


if __name__ == "__main__":
    main(sys.argv[1:])
