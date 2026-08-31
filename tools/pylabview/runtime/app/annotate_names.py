#!/usr/bin/env python3
"""Write primitive names into an extracted pylabview bundle, without patching pylabview.

pylabview's heap XML says <primResID>1050</primResID> and never that 1050 is `Add`. The names
exist - primitive-names.tsv beside this script has 251 of them, harvested by harvest_prim_names.py.
This inserts them as XML comments:

    <primResID>1050</primResID><!-- Add -->
    <parmIndex>3</parmIndex><!-- error in -->

Terminal names come from terminal-names.tsv (harvest_terminal_names.py). A name seen fewer than
three times in the corpus is marked with a trailing '?', because 218 of the 574 pairs rest on a
single sighting. The owning node is found by walking the tree, not by text proximity: primResID
sits AFTER termList in the document, so a terminal cannot see its own node in the raw bytes.

An XML comment is the one annotation that needs NO upstream change: pylabview's reader skips
comments, and a rebuild from an annotated bundle is byte-identical to a rebuild from the
un-annotated one (measured). The alternative, an attribute, is rejected by LVheap.initWithXML's
whitelist until "name" is added to its ignore tuple at LVheap.py:1657 - one word, but a fork.

The comment is decoration for whoever reads or diffs the XML. It is NOT read back, so editing
the comment changes nothing; edit the number.

Usage:
    python annotate_names.py <extracted-bundle-dir> [--strip]
"""

import argparse
import pathlib
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = pathlib.Path(__file__).resolve().parent
# <primResID>1050</primResID> optionally already followed by our own comment
RES = re.compile(rb"(<primResID>(\d+)</primResID>)(<!-- [^>]*? -->)?")
PARM = re.compile(rb"(<parmIndex>(\d+)</parmIndex>)(<!-- [^>]*? -->)?")
MINE = re.compile(rb"<!-- [^>]*? -->")


def load_table(path):
    table = {}
    for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines():
        if line.startswith("#") or not line.strip():
            continue
        parts = line.split("\t")
        if len(parts) < 2 or parts[0] == "primResID":
            continue
        table[parts[0].encode()] = parts[1].encode()
    return table


def load_terminals(path):
    """(primResID, parmIndex) -> terminal name, plus the observation count."""
    table = {}
    if not os.path.isfile(path):
        return table
    for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines():
        if line.startswith("#") or not line.strip():
            continue
        p = line.split("\t")
        if len(p) < 5 or p[0] == "primResID":
            continue
        table[(p[0], p[2])] = (p[3], int(p[4]))
    return table


def owning_prim_per_parmindex(heap_path):
    """For each <parmIndex> in document order, the primResID of the prim that owns it.

    Needed because primResID sits AFTER termList in the document, so a terminal cannot see its
    own node by text proximity. ElementTree preserves document order, so the k-th parmIndex the
    parser sees is the k-th one the regex will find.
    """
    try:
        tree = ET.parse(heap_path)
    except ET.ParseError:
        return []
    root = tree.getroot()
    parent = {c: p for p in root.iter() for c in p}
    owners = []
    for el in root.iter():
        if el.tag != "parmIndex":
            continue
        cur, res = parent.get(el), None
        while cur is not None:
            if cur.get("class") == "prim":
                pr = cur.find("primResID")
                res = pr.text if pr is not None else None
                break
            cur = parent.get(cur)
        owners.append((el.text, res))
    return owners


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("bundle", help="directory holding an extracted *_BDHb.xml")
    ap.add_argument("--table", default=str(HERE / "primitive-names.tsv"))
    ap.add_argument("--terminals", default=str(HERE / "terminal-names.tsv"))
    ap.add_argument("--strip", action="store_true", help="remove annotations instead of adding")
    args = ap.parse_args()

    table = load_table(args.table)
    terminals = load_terminals(args.terminals)
    heaps = sorted(pathlib.Path(args.bundle).glob("*_BDHb.xml"))
    if not heaps:
        print("no *_BDHb.xml in %s" % args.bundle)
        return 1
    print("%d node names, %d terminal names, %d diagram heap(s) to process"
          % (len(table), len(terminals), len(heaps)))

    for h in heaps:
        raw = h.read_bytes()
        named = unknown = 0

        def sub(m):
            nonlocal named, unknown
            if args.strip:
                return m.group(1)
            name = table.get(m.group(2))
            if name is None:
                unknown += 1
                return m.group(1)
            named += 1
            # keep XML-comment-safe: no "--" and no ">" inside a comment
            safe = name.replace(b"--", b"-").replace(b">", b"")
            return m.group(1) + b"<!-- " + safe + b" -->"

        before = len(MINE.findall(raw))
        out = RES.sub(sub, raw)

        # terminals: the owning node is not textually adjacent, so walk the tree for the mapping
        tnamed = tweak = 0
        if terminals:
            owners = owning_prim_per_parmindex(h)
            seq = iter(range(len(owners)))

            def tsub(m):
                nonlocal tnamed, tweak
                if args.strip:
                    return m.group(1)
                try:
                    k = next(seq)
                except StopIteration:
                    return m.group(1)
                val, res = owners[k] if k < len(owners) else (None, None)
                if val != m.group(2).decode() or res is None:
                    tweak += 1                      # parser and regex disagreed - leave it alone
                    return m.group(1)
                hit = terminals.get((res, m.group(2).decode()))
                if hit is None:
                    return m.group(1)
                name, seen = hit
                safe = name.replace("--", "-").replace(">", "")
                mark = "" if seen >= 3 else "?"      # single sightings are not established
                tnamed += 1
                return m.group(1) + ("<!-- %s%s -->" % (safe, mark)).encode()

            out = PARM.sub(tsub, out)

        h.write_bytes(out)
        if args.strip:
            print("  %-34s %d annotations stripped" % (h.name, before))
        else:
            print("  %-34s %d nodes, %d terminals%s%s" % (
                h.name, named, tnamed,
                ", %d primResID not in table" % unknown if unknown else "",
                ", %d skipped on sequence mismatch" % tweak if tweak else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
