# -*- coding: utf-8 -*-
"""Composer census over NI's shipping examples: every .vi under <root> is READ by vi2aixml.py and
the reader's AIXML COMPOSED by aixml2vi.py into <out>\\vis\\c_NNNN.vi. No LabVIEW, no NI service.

    census_examples.py <examples root> <out dir> [k/n]

Run n shards (k = 0..n-1) as separate processes with the BUNDLED python.exe, which has no
multiprocessing. Each shard writes <out>\\rows.<k>.tsv (index, status, stage, message, source,
seconds). Concatenate them into rows.tsv and write composed.txt (composed vi <TAB> source vi) for
cov_load.py; then census_buckets.py <out>\\rows.tsv groups the refusals and
census_owners.py <examples root> <out>\\load.txt splits the load verdicts by owner.
Measured 2026-11-06: docs/aixml-backends.md, "The composer over NI's shipping examples".
"""
import io
import os
import shutil
import sys
import tempfile
import time
import traceback
import contextlib

COMPOSER = r"C:\Projects\LabVIEWMCP\scripts\aixml-pylabview"


def init():
    sys.path.insert(0, COMPOSER)
    sys.setrecursionlimit(20000)


def one(job):
    idx, vi, out = job
    import vi2aixml, aixml2vi, lvheap
    xml = os.path.join(out, "aixml", "c_%04d.xml" % idx)
    target = os.path.join(out, "vis", "c_%04d.vi" % idx)
    t0 = time.time()
    # READ
    err = io.StringIO()
    try:
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
            rc = vi2aixml.main([vi, xml])
        if rc not in (None, 0) or not os.path.exists(xml):
            return (idx, "READFAIL", "read", (err.getvalue().strip().splitlines() or ["rc %s" % rc])[-1][:400], vi, time.time() - t0)
    except SystemExit as ex:
        return (idx, "READFAIL", "read", str(ex.code)[:400], vi, time.time() - t0)
    except Exception as ex:
        return (idx, "READFAIL", "read", "%s: %s" % (type(ex).__name__, str(ex)[:300]), vi, time.time() - t0)
    # COMPOSE, resolving calls as from the original's place (coverage.py's rule)
    try:
        c = aixml2vi.Composer(os.path.join(COMPOSER, "parts"))
        c.search_dirs = [os.path.dirname(vi)]
        c.caller_file = target
        with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
            c.compose(xml)
            tmp = tempfile.mkdtemp(prefix="cen_")
            try:
                lvheap.rebuild(c.write(tmp, "c_%04d" % idx), target)
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
        return (idx, "OK", "compose", "", vi, time.time() - t0)
    except aixml2vi.Unsupported as ex:
        return (idx, "REFUSED", "compose", str(ex).replace("\n", " ")[:400], vi, time.time() - t0)
    except Exception as ex:
        tb = traceback.extract_tb(ex.__traceback__)[-1]
        return (idx, "ERROR", "compose", "%s: %s @%s:%d" % (type(ex).__name__, str(ex).replace("\n", " ")[:250],
                                                           os.path.basename(tb.filename), tb.lineno), vi, time.time() - t0)


def main(argv):
    root, out = argv[0], argv[1]
    k, n = (int(x) for x in argv[2].split("/")) if len(argv) > 2 else (0, 1)
    init()
    for d in ("aixml", "vis"):
        os.makedirs(os.path.join(out, d), exist_ok=True)
    vis = []
    for dp, dn, fn in os.walk(root):
        for f in fn:
            if f.lower().endswith(".vi"):
                vis.append(os.path.join(dp, f))
    vis.sort()
    t0 = time.time()
    with open(os.path.join(out, "rows.%d.tsv" % k), "w", encoding="utf-8") as f:
        for i, v in enumerate(vis):
            if i % n != k:
                continue
            idx, st, stage, msg, vi, dt = one((i, v, out))
            f.write("\t".join([str(idx), st, stage, msg.replace("\t", " "), vi, "%.2f" % dt]) + "\n")
            f.flush()
    print("shard %d done %.0fs" % (k, time.time() - t0))


if __name__ == "__main__":
    main(sys.argv[1:])
