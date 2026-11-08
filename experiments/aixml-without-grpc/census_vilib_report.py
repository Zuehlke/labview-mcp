# -*- coding: utf-8 -*-
"""Report of a census_vilib.py run: status per area, the first composer blocker per VI (property,
invoke and class nodes apart), and EVERY reader note (`<!-- vi2aixml: ... -->`) over all the
AIXML the reader wrote.

    census_vilib_report.py <out dir> > report.txt"""
import collections
import glob
import os
import re
import sys

OUT = sys.argv[1]
EXP = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(EXP, "census_buckets.py"), encoding="utf-8").read()
ns = {}
exec("import re, os, collections\n" + src[src.index("def bucket"):src.index("kinds = collections")], ns)
bucket = ns["bucket"]

rows = []
for f in glob.glob(os.path.join(OUT, "rows.*.tsv")):
    for line in open(f, encoding="utf-8"):
        p = line.rstrip("\n").split("\t")
        if len(p) >= 7:
            rows.append(p)

IGNORE = re.compile(r"Property Node|Invoke Node|class|Class|dynamic dispatch|UDClass", re.I)


def area_of(a, vi):
    if a != "vi.lib":
        return a
    rest = vi.split("vi.lib\\", 1)[-1]
    return "vi.lib/" + rest.split("\\")[0]


st_area = collections.defaultdict(collections.Counter)
first = collections.Counter()
first_area = collections.defaultdict(collections.Counter)
ex = collections.defaultdict(list)
for idx, st, stage, msg, vi, dt, a in rows:
    ar = area_of(a, vi)
    st_area[ar][st] += 1
    if st == "OK":
        continue
    if st == "REFUSED":
        b = bucket(msg)
        if "LOST STRUCTURE" in msg:
            b = "Reader lost structure (see reader notes)"
    else:
        b = st + ": " + re.sub(r"\d+", "N", msg)[:90]
    first[b] += 1
    first_area[b][ar] += 1
    if len(ex[b]) < 3:
        ex[b].append(os.path.basename(vi) + " :: " + msg[:220])

# reader notes over every AIXML the reader wrote
NOTE = re.compile(r"<!--\s*vi2aixml:\s*(.*?)\s*-->", re.S)
notes = collections.Counter()
notes_vis = collections.Counter()
note_ex = collections.defaultdict(list)
vi_of = {"c_%04d" % int(r[0]): r[4] for r in rows}


def norm(n):
    n = " ".join(n.split())
    n = re.sub(r"\(uid \S+\)|uid \S+", "", n)
    n = re.sub(r"signal \S+", "signal", n)
    n = re.sub(r"(constant|node|pair|Structure|Disable|structure|Sequence|Node|call node|XNode) [0-9a-fx]+\b", r"\1 #", n)
    n = re.sub(r"'[^']*'|\"[^\"]*\"", "'X'", n)
    n = re.sub(r"frame \d+", "frame N", n)
    n = re.sub(r"has \d+ sources", "has N sources", n)
    n = re.sub(r"primResID \d+", "primResID N", n)
    return n.strip()


for f in glob.glob(os.path.join(OUT, "aixml", "*.xml")):
    try:
        text = open(f, encoding="utf-8", errors="replace").read()
    except OSError:
        continue
    seen = set()
    for m in NOTE.finditer(text):
        k = norm(m.group(1))
        notes[k] += 1
        if k not in seen:
            seen.add(k)
            notes_vis[k] += 1
            if len(note_ex[k]) < 2:
                note_ex[k].append(os.path.basename(vi_of.get(os.path.basename(f)[:-4], f)) + " :: " + " ".join(m.group(1).split())[:200])

tot = collections.Counter(r[1] for r in rows)
print("TOTAL", len(rows), dict(tot))
print("\n== status by area (top 45)")
for a, c in sorted(st_area.items(), key=lambda kv: -sum(kv[1].values()))[:45]:
    t = sum(c.values())
    print("%-40s %5d ok %5d (%3.0f%%) refused %4d readfail %3d error %3d" % (a[:40], t, c["OK"], 100.0 * c["OK"] / t, c["REFUSED"], c["READFAIL"], c["ERROR"]))
for title, keep in (("first blocker per VI - FOCUS (no property/invoke/class)", lambda b: not IGNORE.search(b)),
                    ("first blocker per VI - IGNORED kinds", lambda b: bool(IGNORE.search(b)))):
    print("\n== " + title)
    for b, n in first.most_common():
        if not keep(b):
            continue
        top = ", ".join("%s %d" % (k[-22:], v) for k, v in first_area[b].most_common(3))
        print("%5d  %-66s [%s]" % (n, b[:66], top))
        for e in ex[b][:2]:
            print("          e.g. " + e[:200])
print("\n== reader notes (VIs carrying it / occurrences)")
for k, n in sorted(notes_vis.items(), key=lambda kv: -kv[1]):
    print("%5d %6d  %s" % (n, notes[k], k[:120]))
    for e in note_ex[k][:2]:
        print("              e.g. " + e[:200])
