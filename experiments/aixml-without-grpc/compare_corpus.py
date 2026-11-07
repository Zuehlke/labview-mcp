# -*- coding: utf-8 -*-
"""Compare vi2aixml.py with NI's ConvertVIToAIXML over a CORPUS written by `LabVIEWMCP.exe
--corpus` (roundtrip.tsv + xml\\), on EVERY attribute NI writes - the measure of "the reader at
NI's level" (2026-11-06). compare_full.py compares a chosen subset over the export cache; this one
leaves nothing out but the uid bookkeeping, and counts each difference by (tag, attribute).

    compare_corpus.py <corpus dir> [--jobs N] [--only REGEX] [--show N] [--out FILE]

Facts per VI, each a set:

  elements  (tag, uid, uid_parent)
  attrs     (uid, tag, attribute, value) for every attribute but uid, uid_parent, inputs, outputs;
            a net name (selectin, selectout) is replaced by its SOURCE, as compare_full does
  terms     (uid, in|out, terminal name) for every terminal listed, wired or not
  edges     (sink uid, sink terminal) -> (source uid, source terminal)

A VI is IDENTICAL when all four match. --out writes one JSON line per VI with its differences.
"""
import collections
import csv
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(0, COMPOSER)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

SKIP = {"uid", "uid_parent", "inputs", "outputs"}
# attributes that NAME a net: an input one (selectin, maxin, count, a tunnel's cond) is compared by the
# SOURCE of that net, an output one (selectout, maxout) only for being there - net names are
# arbitrary tokens (`570.value` against `570.value2`, 2026-11-06)
NETS_IN = {"selectin", "maxin"}
NETS_OUT = {"selectout", "maxout", "count"}
NETS = NETS_IN | NETS_OUT
KINDS = ("elements", "attrs", "terms", "edges")


def facts(root):
    els, attrs, terms, edges = set(), set(), set(), set()
    src_of = {}
    all_els = [e for e in root.iter() if isinstance(e.tag, str) and e is not root]
    for el in all_els:
        for term, net in aixml2vi.pairs(el.get("outputs")):
            if net:
                src_of.setdefault(net, (el.get("uid"), term))
        for a in NETS_OUT:
            for net in (el.get(a) or "").split(","):
                if net:
                    src_of.setdefault(net, (el.get("uid"), "#" + a))
    for a, v in root.attrib.items():
        attrs.add(("VI", "VI", a, aixml2vi.unescape(v)))
    for el in all_els:
        uid = el.get("uid")
        els.add((el.tag, uid, el.get("uid_parent")))
        for a, v in el.attrib.items():
            if a in SKIP:
                continue
            if a in NETS_OUT:
                v = "defined" if v else ""
            elif a in NETS or (a == "cond" and v not in ("true", "false")):
                # a net name is looked up as WRITTEN (escaped), as the outputs that define it
                v = ",".join("%s.%s" % src_of[n] if n in src_of else "?" for n in v.split(",") if n)
            v = aixml2vi.unescape(v)
            attrs.add((uid, el.tag, a, v))
        for side in ("inputs", "outputs"):
            for term, net in aixml2vi.pairs(el.get(side)):
                terms.add((uid, side[:-4] if side == "inputs" else "out", aixml2vi.unescape(term)))
                if side == "inputs" and net and net in src_of:
                    edges.add((uid, aixml2vi.unescape(term), src_of[net]))
    return els, attrs, terms, edges


def export_of(xml_dir, vi):
    h = hashlib.md5(vi.lower().encode("utf-8")).hexdigest().upper()[:8]
    for f in os.listdir(xml_dir):
        if f.endswith("." + h + ".xml"):
            return os.path.join(xml_dir, f)
    return None


def one(job):
    vi, ref_path = job
    tmp = tempfile.mkdtemp(prefix="cc_")
    try:
        ref = ET.parse(ref_path).getroot()
        if sum(1 for e in ref.iter() if e.get("uid")) == 0:
            return {"vi": vi, "skip": "empty NI export (password)"}
        mine = vi2aixml.Reader(lvheap.extract(vi, tmp), vi_path=vi).read()
    except Exception as ex:
        return {"vi": vi, "error": str(ex).splitlines()[0][:200] if str(ex) else type(ex).__name__}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    diagram = ("Node", "Call", "Structure", "Constant")
    if not any(e.tag in diagram for e in ref.iter()) and any(e.tag in diagram for e in mine.iter()):
        return {"vi": vi, "skip": "locked (NI exported no diagram)"}
    a, b = facts(ref), facts(mine)
    tag_of = {f[1]: f[0] for f in a[0]}
    tag_of.update({f[1]: f[0] for f in b[0] if f[1] not in tag_of})
    out = {"vi": vi, "counts": {}, "missing": {}, "extra": {}}
    for k, fa, fb in zip(KINDS, a, b):
        out["counts"][k] = [len(fa & fb), len(fa)]
        out["missing"][k] = sorted([list(map(str, f)) for f in fa - fb])
        out["extra"][k] = sorted([list(map(str, f)) for f in fb - fa])
    out["tags"] = tag_of
    return out


def bucket(kind, f, tags):
    if kind == "elements":
        return "element %s" % f[0]
    if kind == "attrs":
        return "attr %s.%s" % (f[1], f[2])
    if kind == "terms":
        return "term %s %s" % (tags.get(f[0], "?"), f[1])
    return "edge into %s" % tags.get(f[0], "?")


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    corpus = argv[0]
    jobs_n = int(argv[argv.index("--jobs") + 1]) if "--jobs" in argv else 8
    only = re.compile(argv[argv.index("--only") + 1]) if "--only" in argv else None
    show = int(argv[argv.index("--show") + 1]) if "--show" in argv else 0
    out_path = argv[argv.index("--out") + 1] if "--out" in argv else None
    xml_dir = os.path.join(corpus, "xml")
    jobs = []
    with open(os.path.join(corpus, "roundtrip.tsv"), encoding="utf-8") as fh:
        for row in csv.DictReader(fh, delimiter="\t"):
            vi = row["viPath"]
            if row["exportCode"] != "0" or (only and not only.search(vi)) or not os.path.exists(vi):
                continue
            ref = export_of(xml_dir, vi)
            if ref:
                jobs.append((vi, ref))
    if "--shard" in argv:
        # a worker: its share of the jobs, one JSON line each, to --out
        k, n_ = map(int, argv[argv.index("--shard") + 1].split("/"))
        with open(out_path, "w", encoding="utf-8") as fh:
            for job in jobs[k::n_]:
                fh.write(json.dumps(one(job), ensure_ascii=False) + "\n")
        return
    # the bundled Python has no multiprocessing: run N copies of this script on shards
    tmp = tempfile.mkdtemp(prefix="ccs_")
    procs = []
    for k in range(jobs_n):
        part = os.path.join(tmp, "part%d.jsonl" % k)
        cmd = [sys.executable, os.path.abspath(__file__), corpus, "--shard", "%d/%d" % (k, jobs_n), "--out", part]
        if only:
            cmd += ["--only", only.pattern]
        procs.append((subprocess.Popen(cmd), part))
    results = []
    for p, part in procs:
        p.wait()
        if os.path.exists(part):
            with open(part, encoding="utf-8") as fh:
                results += [json.loads(line) for line in fh if line.strip()]
    shutil.rmtree(tmp, ignore_errors=True)
    tot = {k: [0, 0] for k in KINDS}
    missing, extra = collections.Counter(), collections.Counter()
    vis_missing = collections.Counter()
    same = n = skipped = errors = 0
    shown = 0
    for r in results:
        if "skip" in r:
            skipped += 1
            continue
        if "error" in r:
            errors += 1
            print("ERROR %s: %s" % (os.path.basename(r["vi"]), r["error"]), file=sys.stderr)
            continue
        n += 1
        ok = True
        seen = set()
        for k in KINDS:
            tot[k][0] += r["counts"][k][0]
            tot[k][1] += r["counts"][k][1]
            if r["missing"][k] or r["extra"][k]:
                ok = False
            for f in r["missing"][k]:
                b = bucket(k, f, r["tags"])
                missing[b] += 1
                seen.add(b)
            for f in r["extra"][k]:
                extra[bucket(k, f, r["tags"])] += 1
        for b in seen:
            vis_missing[b] += 1
        same += ok
        if not ok and shown < show:
            shown += 1
            print("== %s" % r["vi"])
            for k in KINDS:
                for f in r["missing"][k][:5]:
                    print("   missing %s %s" % (k, str(f)[:200]))
                for f in r["extra"][k][:3]:
                    print("   extra   %s %s" % (k, str(f)[:200]))
    if out_path:
        with open(out_path, "w", encoding="utf-8") as fh:
            for r in results:
                fh.write(json.dumps(r, ensure_ascii=False) + "\n")
    print("MISSING (NI writes, the reader does not) - facts, VIs")
    for b, c in missing.most_common(60):
        print("  %6d %5d  %s" % (c, vis_missing[b], b))
    print("EXTRA (the reader writes, NI does not)")
    for b, c in extra.most_common(30):
        print("  %6d  %s" % (c, b))
    print("%d of %d VIs identical in every fact (%d skipped, %d reader errors)" % (same, n, skipped, errors))
    for k, (hit, all_) in tot.items():
        print("  %-9s %d of %d (%.2f %%)" % (k, hit, all_, 100.0 * hit / max(1, all_)))


if __name__ == "__main__":
    main(sys.argv[1:])
