# -*- coding: utf-8 -*-
"""Compose every suite test and every shipped helper document, with no LabVIEW, and list what the
TYPE GUARD refuses (aixml2vi.py, _guard_terminal_types).

    guard_sweep.py [out_dir]

A guard that refuses a document LabVIEW loads fine is worse than none, so this is the false-alarm
check: run it after any change to the guard and read every `type guard` line it prints.
"""
import concurrent.futures
import glob
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
COMPOSER = os.path.join(REPO, "scripts", "aixml-pylabview", "aixml2vi.py")
sys.path.insert(0, HERE)
from selftest import PY  # noqa: E402

out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ.get("TEMP", "."), "guard-sweep")
os.makedirs(out_dir, exist_ok=True)
expect = json.load(open(os.path.join(HERE, "tests", "expect.json"), encoding="utf-8"))

jobs = []
for src in sorted(glob.glob(os.path.join(HERE, "tests", "t*.xml"))):
    name = os.path.splitext(os.path.basename(src))[0]
    search = [a for d in expect.get(name, {}).get("search", ()) for a in ("--search", os.path.join(HERE, "tests", d))]
    jobs.append((src, search))
for src in sorted(glob.glob(os.path.join(REPO, "scripts", "*.xml"))):
    jobs.append((src, []))


def compose(job):
    src, search = job
    vi = os.path.join(out_dir, os.path.splitext(os.path.basename(src))[0] + ".vi")
    r = subprocess.run([PY, COMPOSER, src, vi] + search, capture_output=True, text=True, stdin=subprocess.DEVNULL)
    text = (r.stdout + r.stderr)
    # a refusal and a WARNING both count: a warning is a type the composer got wrong too
    guard = [l for l in text.splitlines() if "type guard" in l]
    wrote = "wrote " in text
    return os.path.basename(src), wrote, guard


with concurrent.futures.ThreadPoolExecutor(8) as pool:
    results = list(pool.map(compose, jobs))

wrote = sum(1 for _, w, _ in results if w)
print("%d documents, %d composed" % (len(results), wrote))
hits = [(n, g) for n, _, g in results if g]
for n, g in hits:
    print("GUARD %-45s %s" % (n, g[0][:220]))
print("type guard refusals: %d" % len(hits))
