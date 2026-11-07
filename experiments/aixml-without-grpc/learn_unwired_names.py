# -*- coding: utf-8 -*-
"""Learn the primitive terminal names the tables lack - UNWIRED ones included - from a corpus of
NI exports written by `LabVIEWMCP.exe --corpus` (2026-11-06).

learn_names_full.py pairs a ?parmN with NI's name through a WIRE both sides agree on, so a slot
that is never wired was never learned. NI lists every terminal of a primitive, an unwired one as
`name:`, so a slot can be named by ELIMINATION: on one node, the names NI lists that the reader
does not, against the slots the reader could only call ?parmN, side by side. Where exactly one of
each is left, they pair. A slot is written only when every observation agrees.

    learn_unwired_names.py <corpus dir> [--jobs 8] [--write]
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
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(1, COMPOSER)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402
from compare_corpus import export_of  # noqa: E402

LEARNED = os.path.join(COMPOSER, "terminal-names-learned.tsv")


def observe(vi, ref_path):
    """[(rid, slot, side, NI's name)] for one VI."""
    tmp = tempfile.mkdtemp(prefix="lun_")
    try:
        ref = ET.parse(ref_path).getroot()
        reader = vi2aixml.Reader(lvheap.extract(vi, tmp), vi_path=vi)
        reader.read()
    except Exception:
        return []
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    ni = {e.get("uid"): e for e in ref.iter("Node")}
    per_node = collections.defaultdict(list)
    slot_of = {}
    for node in reader.b.bd.iter("SL__arrayElement"):
        if node.get("uid") in getattr(reader, "prim_rid", {}) and node.find("termList") is not None:
            for t in node.find("termList"):
                if t.find("dco") is not None:
                    p = aixml2vi.term_slot(t.find("dco"), node.get("class"))
                    slot_of[t.get("uid")] = int(p) if p.isdigit() else 999
    for tu in reader.term_owner.order:
        o = reader.term_owner.get(tu)
        if o is None or o is vi2aixml.IGNORED:
            continue
        el, nm, sink = o
        if el.tag == "Node" and el.get("uid") in getattr(reader, "prim_rid", {}):
            per_node[el.get("uid")].append((nm, sink, reader.term_net.get(tu), slot_of.get(tu)))
    out = []
    for uid, terms in per_node.items():
        n = ni.get(uid)
        if n is None:
            continue
        rid = reader.prim_rid[uid]
        for sink, attr in ((True, "inputs"), (False, "outputs")):
            ni_names = [aixml2vi.unescape(t) for t, _ in aixml2vi.pairs(n.get(attr))]
            mine = [nm for nm, s, _, _ in terms if s == sink]
            known = collections.Counter(nm for nm in mine if not nm.startswith("?"))
            left = collections.Counter(ni_names) - known
            unknown = [nm for nm in mine if nm.startswith("?parm")]
            if len(unknown) == 1 and sum(left.values()) == 1:
                out.append((str(rid), unknown[0][len("?parm"):], attr, next(iter(left))))
                continue
            # several unknown: NI lists inputs in DESCENDING slot order and outputs ASCENDING
            # (314 and 86 of 382/128 fully named nodes, check_term_order.py) - not always, so the
            # order is trusted only on a node where every KNOWN name sits where it says
            if len(unknown) >= 2 and len(mine) == len(ni_names):
                ordered = sorted(((p if p is not None else 999), nm) for nm, s, _, p in terms if s == sink)
                if sink:
                    ordered.reverse()
                names_in_order = [nm for _, nm in ordered]
                if all(a == b for a, b in zip(names_in_order, ni_names) if not a.startswith("?")):
                    for a, b in zip(names_in_order, ni_names):
                        if a.startswith("?parm"):
                            out.append((str(rid), a[len("?parm"):], attr, b))
                    continue
                # LOCAL alignment: an unknown whose known neighbours on both sides sit where NI
                # lists them (TDMS Open's byte order, between `operation` and `error in`); the
                # next pass names the rest by elimination
                for i, a in enumerate(names_in_order):
                    if not a.startswith("?parm") or ni_names[i] in known:
                        continue
                    nb = [j for j in (i - 1, i + 1) if 0 <= j < len(names_in_order)]
                    if nb and all(not names_in_order[j].startswith("?") and names_in_order[j] == ni_names[j] for j in nb):
                        out.append((str(rid), a[len("?parm"):], attr, ni_names[i]))
                continue
            if not sink:
                continue
            # several unknown INPUTS: a wired one pairs with the NI input fed by the same source
            # node, where exactly one NI input left over is fed by it (Join Numbers' hi and lo)
            ni_src = collections.defaultdict(list)
            for t, net in aixml2vi.pairs(n.get(attr)):
                t = aixml2vi.unescape(t)
                if net and left.get(t):
                    ni_src[net.split(".")[0]].append(t)
            for nm, s, net, _ in terms:
                if s and nm.startswith("?parm") and net:
                    cands = ni_src.get(net.split(".")[0], [])
                    if len(cands) == 1:
                        out.append((str(rid), nm[len("?parm"):], attr, cands[0]))
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
                    fh.write(json.dumps(o, ensure_ascii=False) + "\n")
        return
    tmp = tempfile.mkdtemp(prefix="luns_")
    procs = []
    for k in range(jobs_n):
        part = os.path.join(tmp, "p%d.jsonl" % k)
        procs.append((subprocess.Popen([sys.executable, os.path.abspath(__file__), corpus,
                                        "--shard", "%d/%d" % (k, jobs_n), "--out", part]), part))
    obs = collections.defaultdict(collections.Counter)
    for p, part in procs:
        p.wait()
        if os.path.exists(part):
            for line in open(part, encoding="utf-8"):
                rid, slot, attr, name = json.loads(line)
                obs[(rid, slot)][name] += 1
    shutil.rmtree(tmp, ignore_errors=True)
    _, known = aixml2vi.load_names()
    good, clash = [], []
    for (rid, slot), names in sorted(obs.items()):
        if (rid, slot) in known:
            continue
        (good if len(names) == 1 else clash).append((rid, slot, names))
    print("learned %d slots, %d ambiguous" % (len(good), len(clash)))
    for rid, slot, names in good:
        print("  %s/%s  %s" % (rid, slot, dict(names)))
    for rid, slot, names in clash:
        print("  AMBIGUOUS %s/%s  %s" % (rid, slot, dict(names)))
    if "--write" in argv and good:
        node_names = {}
        with open(os.path.join(aixml2vi.TABLES, "primitive-names.tsv"), encoding="utf-8") as f:
            for row in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t"):
                if row and row[0] != "primResID":
                    node_names[row[0]] = row[1]
        with open(LEARNED, "a", encoding="utf-8", newline="\n") as fh:
            fh.write("# learned by learn_unwired_names.py over NI's exports of the 1 784 shipping examples\n"
                     "# (2026-11-06): an unwired slot, named by elimination against NI's own listing\n")
            for rid, slot, names in good:
                fh.write("%s\t%s\t%s\t%s\n" % (rid, node_names.get(rid, "?"), slot, vi2aixml.escape(next(iter(names)))))


if __name__ == "__main__":
    main(sys.argv[1:])
