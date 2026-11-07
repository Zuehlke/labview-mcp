# -*- coding: utf-8 -*-
"""Heap round trip IN PLACE over a COPY of a tree: every .vi/.vim/.ctl is extracted by pylabview
to XML and rebuilt over itself, so owners (.lvclass/.lvlib) and relative links stay intact. The
counterpart of census_examples.py, which goes through AIXML instead of the heap.

    census_heap.py <tree copy> <out dir> [k/n]

Run n shards with the BUNDLED python.exe. Each writes <out>\\heap.<k>.tsv (status, message, path,
seconds). Load the .vi files afterwards with cov_load.py (composed.txt: path <TAB> path).
Measured 2026-11-06: docs/aixml-backends.md, "The heap route over the same examples".
"""
import os
import shutil
import sys
import tempfile
import time

COMPOSER = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                                         "scripts", "aixml-pylabview"))
sys.path.insert(0, COMPOSER)
import lvheap  # noqa: E402

EXT = (".vi", ".vim", ".ctl")


def main(argv):
    root, out = argv[0], argv[1]
    k, n = (int(x) for x in argv[2].split("/")) if len(argv) > 2 else (0, 1)
    os.makedirs(out, exist_ok=True)
    files = sorted(os.path.join(dp, f) for dp, _, fn in os.walk(root) for f in fn
                   if f.lower().endswith(EXT))
    with open(os.path.join(out, "heap.%d.tsv" % k), "w", encoding="utf-8") as log:
        for i, f in enumerate(files):
            if i % n != k:
                continue
            t0 = time.time()
            tmp = tempfile.mkdtemp(prefix="heap_")
            status, msg = "OK", ""
            try:
                main_xml = lvheap.extract(f, tmp)
                rebuilt = os.path.join(tmp, "rebuilt" + os.path.splitext(f)[1])
                lvheap.rebuild(main_xml, rebuilt)
                shutil.copyfile(rebuilt, f)
            except Exception as ex:
                status, msg = "FAIL", ("%s: %s" % (type(ex).__name__, ex)).replace("\n", " ").replace("\t", " ")[-300:]
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
            log.write("\t".join([status, msg, f, "%.2f" % (time.time() - t0)]) + "\n")
            log.flush()


if __name__ == "__main__":
    main(sys.argv[1:])
