# -*- coding: utf-8 -*-
"""Harvest the VI Server names that WORKING AIXML uses, into docs/vi-server-aixml-names.tsv.

    python scripts/harvest_aixml_vi_server_names.py [--cache DIR] [--out FILE]

WHY A SECOND LIST. docs/vi-server-properties.tsv and docs/vi-server-methods.tsv are LabVIEW's
catalogue by DISPLAY name, harvested from collector VIs that held what somebody happened to
place. AIXML wants the names LabVIEW's exporter writes, and the two differ: measured 2026-09-09,
94 of the 240 Property/Invoke names in our own shipped helpers are absent from the catalogue
(`Ctrl Val.Set` is a call; `Set Control Value [Variant]` is the catalogue row), and measured
2026-10-10 over NI's 2 500 cached exports, 207 property fields and 71 method targets miss it -
plus every `{LV.Project}` and `{LV.Library}` node, classes the catalogue does not list at all.
A name-check against the catalogue alone would accuse working code.

So this lists every (class, property|method, name) that occurs in AIXML known to WORK:
  - NI's own exports of the installation, cached by lvai_convert_vi_to_aixml under
    %USERPROFILE%\\.labviewmcp\\cache\\aixml (LabVIEW wrote them, so every name is real);
  - the helper AIXML shipped in scripts/ (each one is generated and run by a tool).
AixmlCheck.cs and scripts/aixml_lint.py treat a name as known when it is in the catalogue OR
here. Re-run after a LabVIEW upgrade or after the cache has grown; the output is sorted and
deterministic, so a re-run shows as a diff.
"""
import argparse
import collections
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, os.pardir))
BACKSLASH = chr(92)


def unescape(text):
    """AIXML's \\XX escapes (\\3A is a colon) to the characters they stand for."""
    return re.sub(re.escape(BACKSLASH) + r"([0-9A-Fa-f]{2})", lambda m: chr(int(m.group(1), 16)), text)


def split_fields(text):
    """`fields` split on its commas; a comma inside a name is escaped (\\2C), never literal."""
    return [f for f in (text or "").split(",") if f]


def names_in(path):
    """[(class, kind, name)] of the Property and Invoke Nodes in one AIXML file."""
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError):
        return []
    out = []
    for node in root.iter("Node"):
        cls = node.get("type") or ""
        if not cls.startswith("{"):
            continue
        kind = node.get("_name")
        if kind == "Property Node":
            for field in split_fields(node.get("fields")):
                if "+" in field:
                    out.append((cls, "property", unescape(field.split("+", 1)[1])))
        elif kind == "Invoke Node" and node.get("target"):
            out.append((cls, "method", unescape(node.get("target"))))
    return out


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml"))
    ap.add_argument("--out", default=os.path.join(REPO, "docs", "vi-server-aixml-names.tsv"))
    args = ap.parse_args(argv)

    seen = collections.Counter()
    files = sorted(glob.glob(os.path.join(args.cache, "*.xml")))
    helpers = sorted(glob.glob(os.path.join(REPO, "scripts", "**", "*.xml"), recursive=True))
    for path in files + helpers:
        for row in set(names_in(path)):
            seen[row] += 1
    if not seen:
        print("no names found - is the cache at %s?" % args.cache, file=sys.stderr)
        return 1
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("class\tkind\tname\tfiles\n")
        for (cls, kind, name), n in sorted(seen.items()):
            fh.write("%s\t%s\t%s\t%d\n" % (cls, kind, name, n))
    print("%d names from %d exports and %d helpers -> %s" % (len(seen), len(files), len(helpers), args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
