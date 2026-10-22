# -*- coding: utf-8 -*-
"""Learn the VI Server class of a CONTROL REFNUM from NI's exports: NI writes a front-panel
refnum's type as ref{LV.<Class>} (`ref{LV.LVObjectRefNum}{ref{LV.SceneWindow}}`), the heap holds
its CtlFlags. learn_node_classes.py learns classes off property and invoke nodes only, so a class
that appears only as a control's type - inside the 3D Math Plots' Display State, 0x003B, 0x0054,
0x004F - stayed unknown and the reader could not write the type at all (the XControl sweep,
2026-10-22).

    learn_ref_classes.py [--write]

Only cached NI exports naming a class neither refnum-classes.tsv nor class-ids-learned.json
knows are read. A control's ref{LV.X} occurrences and its heap type's LVObjCtl refnums are both
taken depth first, outer before inner, and paired only when their counts agree; a CtlFlags is
written only when every pair agrees on its class.
"""
import collections
import glob
import json
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(0, COMPOSER)
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")
OUT = os.path.join(COMPOSER, "parts", "class-ids-learned.json")
CTL_RX = re.compile(r'<(?:Control|Indicator) _name="([^"]*)"[^>]*?type="([^"]*)"')
REF_RX = re.compile(r"ref\{(LV\.[A-Za-z0-9_]+)\}")


def known():
    names = set()
    with open(os.path.join(COMPOSER, "parts", "refnum-classes.tsv"), encoding="utf-8") as f:
        for line in f:
            row = line.rstrip("\r\n").split("\t")
            if row and row[0].startswith("LV."):
                names.add(row[0])
    learned = json.load(open(OUT, encoding="utf-8")) if os.path.exists(OUT) else {"classes": {}}
    names.update(learned.get("classes", {}).values())
    return names, learned


def objctl_flags(tree):
    """CtlFlags of every LVObjCtl refnum in `tree`, depth first, outer before inner."""
    out = []
    if tree is None:
        return out
    if tree.get("Type") == "Refnum" and tree.get("RefType") == "LVObjCtl":
        out.append((tree.get("CtlFlags") or "").upper())
    for k in tree:
        if k.tag == "TypeDesc":
            out += objctl_flags(k)
    return out


def main(argv):
    names, learned = known()
    pairs = collections.defaultdict(collections.Counter)
    seen = set()
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml"))):
        meta = x[:-4] + ".json"
        try:
            vi = json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else None
        except ValueError:
            continue
        if not vi or not os.path.exists(vi) or vi in seen:
            continue
        text = open(x, encoding="utf-8", errors="replace").read()
        ctls = [(n, t) for n, t in CTL_RX.findall(text) if any(c not in names for c in REF_RX.findall(t))]
        if not ctls:
            continue
        seen.add(vi)
        work = tempfile.mkdtemp(prefix="learnref_")
        try:
            b = lvheap.Bundle(lvheap.extract(vi, work))
            r = vi2aixml.Reader.__new__(vi2aixml.Reader)
            r.b = b
            heap = {}
            for fpdco in b.fp.iter("SL__arrayElement"):
                if fpdco.get("class") == "fPDCO" and fpdco.find("ddo") is not None:
                    heap.setdefault(vi2aixml.name_attr(vi2aixml.label_text(fpdco.find("ddo"))),
                                    objctl_flags(vi2aixml.Reader.tid_tree(r, fpdco)))
        except Exception as ex:  # noqa: BLE001 - an unreadable VI is skipped
            print("skip", vi, str(ex).splitlines()[0][:100], file=sys.stderr)
            continue
        finally:
            shutil.rmtree(work, ignore_errors=True)
        for n, t in ctls:
            ni, hp = REF_RX.findall(t), heap.get(n)
            if hp is not None and len(ni) == len(hp):
                for cls, flags in zip(ni, hp):
                    pairs[flags][cls] += 1
    new = {}
    for flags, c in sorted(pairs.items()):
        print(flags, dict(c))
        if len(c) == 1:
            cls = next(iter(c))
            if cls not in names and flags not in learned.get("classes", {}):
                new[flags] = cls
    print("new:", new)
    if "--write" in argv and new:
        learned.setdefault("classes", {}).update(new)
        json.dump(learned, open(OUT, "w", encoding="utf-8"), indent=1, sort_keys=True)
        print("written to", OUT)


if __name__ == "__main__":
    main(sys.argv[1:])
