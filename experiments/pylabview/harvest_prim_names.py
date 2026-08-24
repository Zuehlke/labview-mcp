#!/usr/bin/env python3
"""Harvest a primIndex -> primitive-name table by joining the two XML views of one VI.

The problem this solves: pylabview writes a diagram node as <primIndex>103</primIndex> and
nothing anywhere says that 103 is `Add`. pylabview has no such table (it names 1013 other tag
ids, just not this one), and the number is not in the VI file either.

The fix is a join, not a guess. LabVIEW's own AIXML export names every node AND numbers it with
a `uid`; pylabview's extraction carries `primIndex` under the SAME `uid`. Measured on
`User Event Generation.vi`: 10 of 10 heap prims matched their AIXML uid exactly. So exporting a
VI both ways and joining on uid yields named primitives, with no generation and no guessing.

Input is the AIXML export cache that LabVIEWMCP already maintains (about 1200 installation VIs
on this station), so the AIXML half costs nothing and needs no running LabVIEW. Only the
pylabview half has to be computed.

Two tables come out. Keyed on primIndex alone, some entries are ambiguous - one number carrying
two names. Keyed on the (primIndex, primResID) pair they separate, so the pair is what a
pylabview patch should carry.

Usage:
    python harvest_prim_names.py --pylabview <repo> [--limit N] [--out table.tsv]
"""

import argparse
import collections
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

DEFAULT_CACHE = pathlib.Path(os.path.expanduser("~")) / ".labviewmcp" / "cache" / "aixml"


def heap_prims(bdhb_path):
    """uid -> (primIndex, primResID) for every class="prim" object in a diagram heap."""
    out = {}
    try:
        root = ET.parse(bdhb_path).getroot()
    except ET.ParseError:
        return out
    for el in root.iter():
        if el.get("class") == "prim" and el.get("uid"):
            pi, pr = el.find("primIndex"), el.find("primResID")
            if pi is not None and pi.text:
                out[el.get("uid")] = (pi.text, pr.text if pr is not None else "")
    return out


def aixml_names(aixml_path):
    """uid -> (tag, _name) for every named element in an AIXML export."""
    out = {}
    text = pathlib.Path(aixml_path).read_text(encoding="utf-8", errors="replace")
    for m in re.finditer(r"<(\w+)\b([^>]*)>", text):
        attrs = m.group(2)
        u = re.search(r'\buid="(\d+)"', attrs)
        n = re.search(r'_name="([^"]*)"', attrs)
        if u and n:
            out[u.group(1)] = (m.group(1), n.group(1))
    return out


def extract(pylabview_root, vi_path, work):
    """Run pylabview -x on one VI; return the path of its BDHb.xml or None."""
    main = pathlib.Path(work) / "h.xml"
    try:
        subprocess.run(
            [sys.executable, os.path.join("pylabview", "readRSRC.py"),
             "-x", "-i", str(vi_path), "-m", str(main)],
            cwd=pylabview_root, capture_output=True, timeout=120, check=False)
    except subprocess.TimeoutExpired:
        return None
    bdhb = pathlib.Path(work) / "h_BDHb.xml"
    return bdhb if bdhb.is_file() else None


def write_table(path, header, rows):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\t".join(header) + "\n")
        for row in rows:
            fh.write("\t".join(str(c) for c in row) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pylabview", required=True, help="pylabview repo root")
    ap.add_argument("--cache", default=str(DEFAULT_CACHE), help="LabVIEWMCP AIXML export cache")
    ap.add_argument("--limit", type=int, default=0, help="stop after N VIs (0 = all)")
    ap.add_argument("--out", default="prim_names.tsv")
    args = ap.parse_args()

    entries = []
    for j in sorted(pathlib.Path(args.cache).glob("*.json")):
        x = j.with_suffix(".xml")
        if not x.is_file():
            continue
        try:
            vi = json.loads(j.read_text(encoding="utf-8", errors="replace")).get("ViPath")
        except (ValueError, OSError):
            continue
        if vi and pathlib.Path(vi).is_file():
            entries.append((pathlib.Path(vi), x))
    if args.limit:
        entries = entries[: args.limit]
    print("candidate VIs with a cached AIXML export: %d" % len(entries), flush=True)

    # Counters, not plain assignments, so a disagreement stays visible instead of silently won
    table = collections.defaultdict(collections.Counter)   # primIndex -> names
    resid = collections.defaultdict(collections.Counter)   # primIndex -> primResIDs
    pairs = collections.defaultdict(collections.Counter)   # (primIndex, primResID) -> names
    stats = collections.Counter()

    work = tempfile.mkdtemp(prefix="primharvest_")
    try:
        for i, (vi, aix) in enumerate(entries, 1):
            for f in pathlib.Path(work).iterdir():
                f.unlink()
            bdhb = extract(args.pylabview, vi, work)
            if bdhb is None:
                stats["extract failed"] += 1
                continue
            prims = heap_prims(bdhb)
            if not prims:
                stats["no prims in diagram"] += 1
                continue
            names = aixml_names(aix)
            matched = 0
            for uid, (pi, pr) in prims.items():
                if uid in names:
                    name = names[uid][1]
                    table[pi][name] += 1
                    pairs[(pi, pr)][name] += 1
                    if pr:
                        resid[pi][pr] += 1
                    matched += 1
            stats["VIs joined"] += 1
            stats["prims seen"] += len(prims)
            stats["prims named"] += matched
            if i % 20 == 0 or i == len(entries):
                print("  %4d/%d  %d distinct primIndex so far" % (i, len(entries), len(table)),
                      flush=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    write_table(
        args.out,
        ["primIndex", "name", "primResID", "seen", "competing_names"],
        [(pi,
          table[pi].most_common(1)[0][0],
          resid[pi].most_common(1)[0][0] if resid[pi] else "",
          table[pi].most_common(1)[0][1],
          ";".join(n for n, _ in table[pi].most_common()[1:]))
         for pi in sorted(table, key=int)])

    pair_out = args.out.replace(".tsv", "_pairs.tsv")
    write_table(
        pair_out,
        ["primIndex", "primResID", "name", "seen", "competing_names"],
        [(pi, pr,
          pairs[(pi, pr)].most_common(1)[0][0],
          pairs[(pi, pr)].most_common(1)[0][1],
          ";".join(n for n, _ in pairs[(pi, pr)].most_common()[1:]))
         for pi, pr in sorted(pairs, key=lambda k: (int(k[0]), int(k[1] or 0)))])

    print()
    for k, v in sorted(stats.items()):
        print("  %-22s %d" % (k, v))
    print()
    print("  primIndex alone        : %4d keys, %4d unambiguous  -> %s"
          % (len(table), sum(1 for k in table if len(table[k]) == 1), args.out))
    print("  (primIndex, primResID) : %4d keys, %4d unambiguous  -> %s"
          % (len(pairs), sum(1 for k in pairs if len(pairs[k]) == 1), pair_out))


if __name__ == "__main__":
    main()
