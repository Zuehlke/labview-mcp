# -*- coding: utf-8 -*-
"""Primitive NAMES the reader writes differently from NI's export, over a --corpus folder
(2026-11-06): per primResID, the reader's name against NI's `_name` on the same node uid.

    learn_prim_names.py <corpus dir> [--jobs 8]

Prints, per primResID, both names and how often; a primResID whose NI name is unanimous and that
the reader could only call `?primResID n` is a row for primitive-names-extra.tsv.
"""
import collections
import csv
import json
import os
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(1, os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview")))
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402
from compare_corpus import export_of  # noqa: E402


def observe(vi, ref_path):
    tmp = tempfile.mkdtemp(prefix="lpn_")
    try:
        ref = ET.parse(ref_path).getroot()
        reader = vi2aixml.Reader(lvheap.extract(vi, tmp), vi_path=vi)
        mine = reader.read()
    except Exception:
        return []
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    ni = {e.get("uid"): e.get("_name") for e in ref.iter("Node")}
    me = {e.get("uid"): e.get("_name") for e in mine.iter("Node")}
    out = []
    for uid, rid in getattr(reader, "prim_rid", {}).items():
        if uid in ni:
            out.append((str(rid), me.get(uid), ni[uid]))
    # template/class nodes too: keyed by the heap class
    for node in reader.b.bd.iter("SL__arrayElement"):
        uid = node.get("uid")
        if uid in ni and uid in me and uid not in getattr(reader, "prim_rid", {}) and me[uid] != ni[uid]:
            out.append(("class:" + (node.get("class") or "?"), me[uid], ni[uid]))
    return out


def main(argv):
    corpus = argv[0]
    jobs_n = int(argv[argv.index("--jobs") + 1]) if "--jobs" in argv else 8
    xml_dir = os.path.join(corpus, "xml")
    jobs = []
    with open(os.path.join(corpus, "roundtrip.tsv"), encoding="utf-8") as fh:
        for row in csv.DictReader(fh, delimiter="\t"):
            if row["exportCode"] == "0" and os.path.exists(row["viPath"]):
                ref = export_of(xml_dir, row["viPath"])
                if ref:
                    jobs.append((row["viPath"], ref))
    if "--shard" in argv:
        k, n_ = map(int, argv[argv.index("--shard") + 1].split("/"))
        with open(argv[argv.index("--out") + 1], "w", encoding="utf-8") as fh:
            for vi, ref in jobs[k::n_]:
                for o in observe(vi, ref):
                    fh.write(json.dumps(o + (os.path.basename(vi),), ensure_ascii=False) + "\n")
        return
    tmp = tempfile.mkdtemp(prefix="lpns_")
    procs = []
    for k in range(jobs_n):
        part = os.path.join(tmp, "p%d.jsonl" % k)
        procs.append((subprocess.Popen([sys.executable, os.path.abspath(__file__), corpus,
                                        "--shard", "%d/%d" % (k, jobs_n), "--out", part]), part))
    obs = collections.defaultdict(collections.Counter)
    where = {}
    for p, part in procs:
        p.wait()
        if os.path.exists(part):
            for line in open(part, encoding="utf-8"):
                key, mine, ni, vi = json.loads(line)
                obs[key][(mine, ni)] += 1
                where.setdefault((key, mine, ni), vi)
    shutil.rmtree(tmp, ignore_errors=True)
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    for key, c in sorted(obs.items()):
        bad = {k: v for k, v in c.items() if k[0] != k[1]}
        if bad:
            good = sum(v for k, v in c.items() if k[0] == k[1])
            print("%s  right %d  wrong %s  e.g. %s" % (key, good, dict(bad), where[(key,) + next(iter(bad))]))


if __name__ == "__main__":
    main(sys.argv[1:])
