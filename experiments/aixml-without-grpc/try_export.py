# -*- coding: utf-8 -*-
"""Compose ONE cached NI export as coverage.py does - callees resolved beside the ORIGINAL VI -
into a VI of our own, for a load check.

    try_export.py <original .vi path> <out.vi>
"""
import glob
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def main(argv):
    vp, out = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    src = None
    for j in glob.glob(os.path.join(CACHE, "*.json")):
        meta = json.load(open(j, encoding="utf-8"))
        if os.path.normcase(meta.get("ViPath", "")) == os.path.normcase(vp):
            src = j[:-5] + ".xml"
            break
    if src is None:
        print("no cached export of %s" % vp)
        return 1
    c = aixml2vi.Composer(os.path.join(HERE, "parts"))
    c.search_dirs = [os.path.dirname(vp)]
    c.caller_file = out
    try:
        c.compose(src)
    except aixml2vi.Unsupported as ex:
        print("UNSUPPORTED: %s" % ex)
        return 2
    tmp = tempfile.mkdtemp(prefix="tryexp_")
    try:
        lvheap.rebuild(c.write(tmp, os.path.splitext(os.path.basename(out))[0]), out)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print("wrote %s from %s" % (out, src))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
