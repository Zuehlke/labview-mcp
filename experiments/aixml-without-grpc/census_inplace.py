# -*- coding: utf-8 -*-
"""Composer census IN PLACE: every OWNED VI (a .lvclass or .lvlib member) of <examples root> is
READ by vi2aixml.py from the installation and COMPOSED by aixml2vi.py over its own copy in
<tree copy>, so its owner lists it, its private data and scope apply and its relative links hold -
the fair test census_examples.py cannot give members, which it composes into a scratch folder
outside their owner (all 280 class members broken there, 2026-11-07).

    census_inplace.py <examples root> <tree copy> <out dir> [k/n]

<tree copy> is a copy of <examples root> made beforehand (cp -r). Run n shards with the BUNDLED
python.exe. Each writes <out>\\rows.<k>.tsv (index, status, stage, message, source, seconds) with
the COPY's path in a sixth column; write_composed.py is not used - composed.txt here is
`<copy path> <TAB> <source>`, written by the last step of run_inplace.sh.
"""
import contextlib
import io
import os
import shutil
import sys
import tempfile
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(0, COMPOSER)
sys.path.insert(1, HERE)
sys.setrecursionlimit(20000)


def one(idx, vi, target, out):
    import aixml2vi
    import lvheap
    import vi2aixml
    xml = os.path.join(out, "aixml", "c_%04d.xml" % idx)
    t0 = time.time()
    err = io.StringIO()
    try:
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
            rc = vi2aixml.main([vi, xml])
        if rc not in (None, 0) or not os.path.exists(xml):
            return "READFAIL", "read", (err.getvalue().strip().splitlines() or ["rc %s" % rc])[-1][:400], time.time() - t0
    except Exception as ex:
        return "READFAIL", "read", "%s: %s" % (type(ex).__name__, str(ex)[:300]), time.time() - t0
    try:
        c = aixml2vi.Composer(os.path.join(COMPOSER, "parts"))
        c.search_dirs = [os.path.dirname(target)]
        c.caller_file = target
        with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
            c.compose(xml)
            tmp = tempfile.mkdtemp(prefix="cin_")
            try:
                built = os.path.join(tmp, "built.vi")
                lvheap.rebuild(c.write(tmp, "c_%04d" % idx), built)
                shutil.copyfile(built, target)
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
        return "OK", "compose", "", time.time() - t0
    except aixml2vi.Unsupported as ex:
        return "REFUSED", "compose", str(ex).replace("\n", " ")[:400], time.time() - t0
    except Exception as ex:
        tb = traceback.extract_tb(ex.__traceback__)[-1]
        return "ERROR", "compose", "%s: %s @%s:%d" % (type(ex).__name__, str(ex).replace("\n", " ")[:250],
                                                    os.path.basename(tb.filename), tb.lineno), time.time() - t0


def main(argv):
    root, copy, out = argv[0], argv[1], argv[2]
    k, n = (int(x) for x in argv[3].split("/")) if len(argv) > 3 else (0, 1)
    from census_owners import owners
    own = owners(root)
    os.makedirs(os.path.join(out, "aixml"), exist_ok=True)
    vis = sorted(p for p in own if p.endswith(".vi") and os.path.exists(p))
    with open(os.path.join(out, "rows.%d.tsv" % k), "w", encoding="utf-8") as f:
        for i, v in enumerate(vis):
            if i % n != k:
                continue
            target = os.path.join(copy, os.path.relpath(v, os.path.normcase(os.path.normpath(root))))
            st, stage, msg, dt = one(i, v, target, out)
            f.write("\t".join([str(i), st, stage, msg.replace("\t", " "), v, "%.2f" % dt, target]) + "\n")
            f.flush()


if __name__ == "__main__":
    main(sys.argv[1:])
