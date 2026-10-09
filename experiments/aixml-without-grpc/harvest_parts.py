# -*- coding: utf-8 -*-
"""Cut reusable heap PARTS out of existing VIs, so a VI can later be composed with no LabVIEW.

    harvest_parts.py <parts-dir> <vi-or-main.xml> [...]

A part is one heap subtree - a front-panel control, a diagram terminal, a constant, a
primitive - with every type it references resolved into a self-contained tree, so it can be
dropped into a different VI with a different type table. The first sighting of a key wins
unless --replace is given; keys are printed so a run says what it found.

Part files are plain XML:

    <part kind="prim" key="prim/1050" source="...">
      <types><TypeDesc .../> ...</types>     one per <typeDesc>TypeID(n)</typeDesc>, in order
      <heap> ...the subtree... </heap>
    </part>
"""
import copy
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # the bundle's ._pth leaves it off
import lvheap  # noqa: E402


def part_xml(kind, key, source, el, types, extra=None):
    p = ET.Element("part", {"kind": kind, "key": key, "source": source})
    # A CLEAN part comes from a VI LabVIEW generated from AIXML: default style, no captions,
    # no default data. Parts cut from NI's examples carry the example's styling and were
    # measured breaking a VI on their own (denylist.json), so the composer prefers clean ones.
    if extra:
        for k, v in extra.items():
            p.set(k, str(v))
    t = ET.SubElement(p, "types")
    for tree in types:
        t.append(tree)
    h = ET.SubElement(p, "heap")
    h.append(copy.deepcopy(el))
    return p


def resolve_types(bundle, el):
    out = []
    for e in lvheap.typeid_elements(el):
        tid = int(lvheap.TYPEID_RE.fullmatch(e.text.strip()).group(1))
        out.append(bundle.heap_type_tree(tid))
    return out


def safe(key):
    return key.replace("/", "__").replace(".", "_")


class Harvester:
    def __init__(self, parts_dir, replace=False, clean=False):
        self.dir = parts_dir
        self.replace = replace
        self.clean = clean
        os.makedirs(parts_dir, exist_ok=True)
        self.found = []
        # Wire tables by number of ends. A signal's compressedWireTable is its routing, and
        # LabVIEW repairs the GEOMETRY of a foreign table ("Wiring insanity found and fixed")
        # but not its SHAPE: a two-ended table on a three-ended signal is a broken VI, and no
        # table at all crashes LabVIEW. So the composer needs one table per end count.
        self.wt_path = os.path.join(parts_dir, "wiretables.json")
        self.wiretables = json.load(open(self.wt_path)) if os.path.exists(self.wt_path) else {}
        self.wt_new = 0
        dl = os.path.join(parts_dir, "denylist.json")
        self.deny = set(k for k in (json.load(open(dl)) if os.path.exists(dl) else {}) if not k.startswith("$"))

    def save(self, kind, key, source, el, types, extra=None):
        path = os.path.join(self.dir, safe(key) + ".xml")
        if key in self.deny or (os.path.exists(path) and not self.replace):
            return
        if self.clean:
            extra = dict(extra or {}, clean="1")
        ET.ElementTree(part_xml(kind, key, source, el, types, extra)).write(
            path, encoding="utf-8", xml_declaration=True)
        self.found.append(key)

    def harvest(self, main_xml, source):
        b = lvheap.Bundle(main_xml)
        fpdco_ind = {}
        if b.fp is not None:
            for fpdco in b.fp.iter("SL__arrayElement"):
                if fpdco.get("class") != "fPDCO":
                    continue
                ddo = fpdco.find("ddo")
                flags = int(fpdco.findtext("objFlags") or 0)
                ind = "ind" if flags & 1 else "ctl"
                fpdco_ind[fpdco.get("uid")] = ind
                types = resolve_types(b, fpdco)
                if not types:
                    continue
                tk = lvheap.type_key(types[0])
                self.save("fpdco", "fpdco/%s/%s/%s" % (ddo.get("class"), ind, tk),
                          source, fpdco, types)
        if b.bd is None:
            return
        for e in b.bd.iter("SL__arrayElement"):
            cls = e.get("class")
            if cls == "fPTerm":
                ind = fpdco_ind.get(e.find("dco").get("uid"))
                if ind:
                    self.save("fpterm", "fpterm/%s" % ind, source, e, [])
            elif cls == "term":
                dco = e.find("dco")
                if dco is not None and dco.get("class") == "bDConstDCO":
                    ddo = dco.find("ddo")
                    types = resolve_types(b, e)
                    tk = lvheap.type_key(types[0])
                    self.save("const", "const/%s/%s" % (ddo.get("class"), tk), source, e, types)
            elif cls == "signal":
                n = str(len(e.find("termList")))
                wt = e.findtext("compressedWireTable")
                if wt and (n not in self.wiretables or len(wt) < len(self.wiretables[n])):
                    self.wiretables[n] = wt
                    self.wt_new += 1
            elif cls == "prim":
                rid = e.findtext("primResID")
                if rid is None:
                    continue
                types = resolve_types(b, e)
                self.save("prim", "prim/%s" % rid, source, e, types)


def main(argv):
    replace = "--replace" in argv
    clean = "--clean" in argv
    args = [a for a in argv if a not in ("--replace", "--clean")]
    parts_dir, inputs = args[0], args[1:]
    h = Harvester(parts_dir, replace, clean)
    tmp = tempfile.mkdtemp(prefix="harvest_")
    for i, src in enumerate(inputs):
        try:
            main_xml = src if src.lower().endswith(".xml") else \
                lvheap.extract(src, os.path.join(tmp, "b%d" % i))
            h.harvest(main_xml, src)
        except Exception as ex:  # one bad VI must not stop a sweep
            print("SKIP %s: %s" % (src, ex), file=sys.stderr)
    json.dump(dict(sorted(h.wiretables.items(), key=lambda kv: int(kv[0]))),
              open(h.wt_path, "w"), indent=1)
    for k in sorted(h.found):
        print(k)
    print("wire tables for %d end counts (%d improved)" % (len(h.wiretables), h.wt_new), file=sys.stderr)
    print("%d new part(s) in %s" % (len(h.found), parts_dir), file=sys.stderr)


if __name__ == "__main__":
    main(sys.argv[1:])
