# -*- coding: utf-8 -*-
"""Learn primitive terminal names the name tables lack, from LabVIEW's own exports of WHOLE VIs:
where vi2aixml can only say ?parmN, the wire that ends there has a partner both sides agree on,
and NI's export names the terminal. Unambiguous names (one name per slot) are appended to
terminal-names-learned.tsv.

    learn_names_full.py [--max N] [--write]
"""
import collections
import glob
import json
import os
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def edges(root):
    """[(sink uid, sink term, src uid, src term)] of an AIXML tree."""
    src_of = {}
    els = [e for e in root.iter() if isinstance(e.tag, str) and e is not root]
    for el in els:
        for t, n in aixml2vi.pairs(el.get("outputs")):
            if n:
                src_of.setdefault(n, (el.get("uid"), t))
    out = []
    for el in els:
        for t, n in aixml2vi.pairs(el.get("inputs")):
            if n and n in src_of:
                out.append((el.get("uid"), t) + src_of[n])
    return out


def main(argv):
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10 ** 9
    votes = collections.defaultdict(collections.Counter)
    names = {}
    n = 0
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize):
        meta = x[:-4] + ".json"
        if not os.path.exists(meta):
            continue
        vp = json.load(open(meta, encoding="utf-8")).get("ViPath")
        if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        tmp = tempfile.mkdtemp(prefix="learn_")
        try:
            ref = ET.parse(x).getroot()
            r = vi2aixml.Reader(lvheap.extract(vp, tmp))
            mine = r.read()
        except Exception:
            continue
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        rid_of = getattr(r, "prim_rid", {})
        theirs = edges(ref)
        by_sink_src = collections.defaultdict(set)
        by_src_sink = collections.defaultdict(set)
        for su, st, ou, ot in theirs:
            by_sink_src[(su, ou, ot)].add(st)
            by_src_sink[(ou, su, st)].add(ot)
        for su, st, ou, ot in edges(mine):
            m = re.fullmatch(r"\?parm(\w+)", st)
            if m and su in rid_of:
                cand = by_sink_src.get((su, ou, ot), set())
                if len(cand) == 1:
                    votes[(rid_of[su], m.group(1))][next(iter(cand))] += 1
            m = re.fullmatch(r"\?parm(\w+)", ot)
            if m and ou in rid_of:
                cand = by_src_sink.get((ou, su, st), set())
                if len(cand) == 1:
                    votes[(rid_of[ou], m.group(1))][next(iter(cand))] += 1
        n += 1
        if n >= limit:
            break
    prim_name = vi2aixml.Reader.__new__(vi2aixml.Reader)
    rows = []
    for (rid, pidx), c in sorted(votes.items()):
        if len(c) != 1:
            continue          # two names for one slot: ambiguous, left out
        nm, k = c.most_common(1)[0]
        rows.append((rid, pidx, nm, k))
    print("%d VIs read, %d slots learned" % (n, len(rows)))
    for r_ in rows[:40]:
        print("  %s %s %s (%d)" % r_)
    if "--write" in argv:
        path = os.path.join(HERE, "terminal-names-learned.tsv")
        have = set()
        for line in open(path, encoding="utf-8"):
            parts = line.rstrip("\r\n").split("\t")
            if len(parts) >= 3:
                have.add((parts[0], parts[2]))
        names_tsv = {}
        for row in aixml2vi._tsv(os.path.join(aixml2vi.TABLES, "primitive-names.tsv")):
            names_tsv.setdefault(row[0], row[1])
        added = 0
        with open(path, "a", encoding="utf-8", newline="\n") as f:
            for rid, pidx, nm, k in rows:
                if (rid, pidx) in have:
                    continue
                f.write("%s\t%s\t%s\t%s\n" % (rid, names_tsv.get(rid, "?"), pidx, nm))
                added += 1
        print("appended %d rows to %s" % (added, path))


if __name__ == "__main__":
    main(sys.argv[1:])
