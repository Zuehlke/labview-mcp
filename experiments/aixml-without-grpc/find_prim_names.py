# -*- coding: utf-8 -*-
"""The reverse of find_prim_ids.py: NAME a primResID the name tables lack, from NI's own exports.

    find_prim_names.py <out.tsv> <primResID> [...]

For every cached export (%USERPROFILE%\\.labviewmcp\\cache\\aixml, smallest first) the VI is
extracted with pylabview and each `prim` carrying a wanted primResID is joined on uid with the
export's <Node>: its `_name` is NI's name for the primitive. An id seen under exactly one name is
written in primitive-names-extra.tsv's columns (name kept AIXML-escaped, as the table keeps it);
two names stay out, because then it would be a guess. A donor that is not cached yet is exported
first with lvai_convert_vis_to_aixml - installation VIs are cached by that tool.

Measured 2026-10-13 (FINDINGS §7b): 26 of 42 ids named, among them Flatten To String, Swap Bytes
and Date/Time To Seconds, which the 258-name table of the pylabview bundle simply does not list.
"""
import collections
import glob
import json
import os
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview")))
import lvheap  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def main(argv):
    out, want = argv[0], set(argv[1:])
    found = collections.defaultdict(collections.Counter)
    tmp = tempfile.mkdtemp(prefix="pnames_")
    for i, f in enumerate(sorted(glob.glob(os.path.join(CACHE, "*.xml")), key=os.path.getsize)):
        meta = f[:-4] + ".json"
        if not os.path.exists(meta):
            continue
        vp = json.load(open(meta, encoding="utf-8")).get("ViPath") or ""
        if not os.path.exists(vp) or ".llb\\" in vp.lower():
            continue
        try:
            nodes = {e.get("uid"): e for e in ET.parse(f).getroot().iter("Node")}
        except ET.ParseError:
            continue
        if not nodes:
            continue
        d = os.path.join(tmp, "b%d" % i)
        try:
            b = lvheap.Bundle(lvheap.extract(vp, d))
        except Exception:
            shutil.rmtree(d, ignore_errors=True)
            continue
        if b.bd is not None:
            for e in b.bd.iter("SL__arrayElement"):
                rid = e.findtext("primResID")
                if e.get("class") == "prim" and rid in want and e.get("uid") in nodes:
                    found[rid][nodes[e.get("uid")].get("_name") or ""] += 1
        shutil.rmtree(d, ignore_errors=True)
    shutil.rmtree(tmp, ignore_errors=True)
    rows = []
    for rid in sorted(want, key=int):
        names = found.get(rid, {})
        print(rid, dict(names))
        if len(names) == 1:
            n, c = next(iter(names.items()))
            rows.append("%s\t%s\t%d\t" % (rid, n, c))
    open(out, "w", encoding="utf-8").write("\n".join(rows) + ("\n" if rows else ""))
    print("%d of %d named unambiguously" % (len(rows), len(want)))


if __name__ == "__main__":
    main(sys.argv[1:])
