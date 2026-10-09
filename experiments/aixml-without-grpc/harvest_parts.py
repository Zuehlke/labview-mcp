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


def shift_register_pair(loop):
    """One shift register as FOUR terminals in a <wrap>: right outer, left outer, right inner,
    left inner. Measured on Get ID.vi: the rSR DCO's termList names [inner, outer] and its
    lsrDCOList the matching lSR, whose rsrDCO points back; the inner terminals sit in the
    inner diagram's sRN with a bare <dco uid=..> reference, outside its zPlaneList."""
    outer = {t.find("dco").get("uid"): t for t in loop.find("termList")
             if t.find("dco") is not None and t.find("dco").get("class") in ("rSR", "lSR")}
    rsr = next((t for t in outer.values() if t.find("dco").get("class") == "rSR"), None)
    if rsr is None:
        return None
    lsr_uid = rsr.find("dco/lsrDCOList/SL__arrayElement").get("uid")
    lsr = outer.get(lsr_uid)
    inner = loop.find("diagramList/SL__arrayElement[@class='diag']/nodeList/SL__arrayElement[@class='sRN']/termList")
    ins = {t.find("dco").get("uid"): t for t in inner if t.find("dco") is not None and len(t.find("dco")) == 0}
    r_in, l_in = ins.get(rsr.find("dco").get("uid")), ins.get(lsr_uid)
    if lsr is None or r_in is None or l_in is None:
        return None
    wrap = ET.Element("wrap")
    for t in (rsr, lsr, r_in, l_in):
        wrap.append(copy.deepcopy(t))
    return wrap


def case_tunnels(case):
    """Each tunnel of a Case Structure as a <wrap> of [outer, inner per frame...]. A selTun
    DCO's termList names the inner terminal of every frame, in frame order, then the outer
    one (measured on Multiply Force.vi). An input tunnel's DCO has objFlags 2048, an output's
    has bit 0 set."""
    frames = case.find("diagramList")
    inner = {}
    for f in frames:
        for t in f.find("nodeList/SL__arrayElement[@class='sRN']/termList"):
            d = t.find("dco")
            if d is not None and len(d) == 0:
                inner[t.get("uid")] = t
    out = []
    for t in case.find("termList"):
        dco = t.find("dco")
        if dco is None or dco.get("class") != "selTun":
            continue
        uids = [x.get("uid") for x in dco.find("termList")]
        if len(uids) != len(frames) + 1 or uids[-1] != t.get("uid") or not all(u in inner for u in uids[:-1]):
            continue
        wrap = ET.Element("wrap")
        wrap.append(copy.deepcopy(t))
        for u in uids[:-1]:
            wrap.append(copy.deepcopy(inner[u]))
        out.append(("out" if int(dco.findtext("objFlags") or 0) & 1 else "in", wrap))
    return out


def plain_tunnels(loop):
    """Each PLAIN (non-indexing) tunnel of a loop as a <wrap> of [outer, inner] terminal.
    A plain input tunnel's lpTun DCO has objFlags 2048, a plain output's 2049 (bit 0 = output);
    an indexing one carries TunnelType and an innerLpTunDCO and is left out."""
    inner = loop.find("diagramList/SL__arrayElement[@class='diag']/nodeList/SL__arrayElement[@class='sRN']/termList")
    ins = {t.find("dco").get("uid"): t for t in inner if t.find("dco") is not None and len(t.find("dco")) == 0}
    out = []
    for t in loop.find("termList"):
        dco = t.find("dco")
        if dco is None or dco.get("class") != "lpTun" or dco.find("innerLpTunDCO") is not None \
                or dco.find("TunnelType") is not None or dco.get("uid") not in ins:
            continue
        wrap = ET.Element("wrap")
        wrap.append(copy.deepcopy(t))
        wrap.append(copy.deepcopy(ins[dco.get("uid")]))
        out.append(("out" if int(dco.findtext("objFlags") or 0) & 1 else "in", wrap))
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
            elif cls == "select":
                # a Case Structure; only a TWO-frame BOOLEAN one is cut for now, so frame order
                # and selector type are known: SelectorRange start 0 is False, 1 is True
                frames = e.find("diagramList")
                sel = next((t for t in e.find("termList") if t.find("dco") is not None
                            and t.find("dco").get("class") == "caseSel"), None)
                if frames is not None and len(frames) == 2 and sel is not None:
                    st = resolve_types(b, sel)
                    if st and st[0].get("Type") == "Boolean":
                        self.save("struct", "struct/select/bool", source, e, resolve_types(b, e))
                        for direction, tun in case_tunnels(e):
                            self.save("seltun", "seltun/bool/%s" % direction, source, tun,
                                      resolve_types(b, tun))
            elif cls in ("whileLoop", "forLoop"):
                # a whole loop with its tunnels, shift registers and inner diagram; the
                # composer keeps the shell and one template of each terminal kind
                self.save("struct", "struct/%s" % cls, source, e, resolve_types(b, e))
                pair = shift_register_pair(e)
                if pair is not None:
                    self.save("srpair", "srpair/%s" % cls, source, pair, resolve_types(b, pair))
                for direction, tun in plain_tunnels(e):
                    self.save("tunnel", "tunnel/%s/%s" % (cls, direction), source, tun,
                              resolve_types(b, tun))
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
