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


def image_ids(el):
    """Positive ImageResIDs: images EMBEDDED in the donor VI's DSIM block (negative ones are
    LabVIEW's built-in images). Measured: a waveform control without them is "Could not load
    the image" (fpinit.cpp) and a broken VI."""
    out = set()
    for e in el.iter("ImageResID"):
        try:
            v = int(e.text)
        except (TypeError, ValueError):
            continue
        if v > 0:
            out.add(v)
    return sorted(out)


def dsim_images(bundle, ids):
    """{index: hex} for the DSIM sections a part needs, read from the donor bundle."""
    out = {}
    blk = bundle.main.find("DSIM")
    if blk is None:
        return out
    for sec in blk.findall("Section"):
        i = int(sec.get("Index"))
        if i in ids and sec.get("File"):
            with open(os.path.join(bundle.dir, sec.get("File")), "rb") as f:
                out[i] = f.read().hex().upper()
    return out


def part_xml(kind, key, source, el, types, extra=None, images=None):
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
    if images:
        im = ET.SubElement(p, "images")
        for i, hx in sorted(images.items()):
            ET.SubElement(im, "image", {"index": str(i)}).text = hx
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


def case_tunnels(case, dco_class="selTun"):
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
        if dco is None or dco.get("class") != dco_class:
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


def plain_tunnels(loop, indexing=False):
    """Each PLAIN (non-indexing) tunnel of a loop as a <wrap> of [outer, inner] terminal.
    A plain input tunnel's lpTun DCO has objFlags 2048, a plain output's 2049 (bit 0 = output);
    an indexing one carries an innerLpTunDCO (its inner terminal's own DCO, typed with the
    ELEMENT) and is returned instead when indexing=True."""
    inner = loop.find("diagramList/SL__arrayElement[@class='diag']/nodeList/SL__arrayElement[@class='sRN']/termList")
    by_uid = {t.get("uid"): t for t in inner}
    out = []
    for t in loop.find("termList"):
        dco = t.find("dco")
        if dco is None or dco.get("class") != "lpTun":
            continue
        ilt = dco.find("innerLpTunDCO")
        if (ilt is not None) != indexing:
            continue
        # the inner terminal is the first entry of the tunnel DCO's termList
        uids = [x.get("uid") for x in dco.find("termList")] if dco.find("termList") is not None else []
        it = by_uid.get(uids[0]) if uids else None
        if it is None:
            continue
        wrap = ET.Element("wrap")
        wrap.append(copy.deepcopy(t))
        wrap.append(copy.deepcopy(it))
        out.append(("out" if int(dco.findtext("objFlags") or 0) & 1 else "in", wrap))
    return out


def safe(key):
    return key.replace("/", "__").replace(".", "_")


_REF_CLASS_BY_FLAGS = None


def ref_class(tree):
    """A VI Server refnum type -> its class name (LV.VI ...) from parts/refnum-classes.tsv, or
    None for any other refnum."""
    global _REF_CLASS_BY_FLAGS
    if tree is None or tree.get("Type") != "Refnum" or tree.get("RefType") != "LVObjCtl":
        return None
    if _REF_CLASS_BY_FLAGS is None:
        _REF_CLASS_BY_FLAGS = {}
        tsv = os.path.join(os.path.dirname(os.path.abspath(__file__)), "parts", "refnum-classes.tsv")
        for line in open(tsv, encoding="utf-8"):
            row = line.rstrip("\r\n").split("\t")
            if line.startswith("#") or row[0] == "class" or len(row) < 2:
                continue
            _REF_CLASS_BY_FLAGS.setdefault(row[1].upper(), row[0])
    return _REF_CLASS_BY_FLAGS.get((tree.get("CtlFlags") or "").upper())


def meth_key(name):
    """A method name as a part-file-safe key component: anything but letters, digits, space,
    _ and - becomes %XX (method names carry dots, colons, brackets)."""
    import re
    return re.sub(r"[^A-Za-z0-9 _-]", lambda m: "%%%02X" % ord(m.group()), name)


def fields_of(tree):
    return [c for c in tree if c.tag == "TypeDesc"]


def type_xml(tree):
    return ET.tostring(lvheap.strip_label(tree), encoding="unicode")


class Harvester:
    def __init__(self, parts_dir, replace=False, clean=False, only_props=False):
        self.dir = parts_dir
        self.replace = replace
        self.clean = clean
        # --props: a corpus sweep for property and invoke nodes only, so no other part an NI
        # example happens to carry slips into the set the composer picks from
        self.only_props = only_props
        self.props_path = os.path.join(parts_dir, "vi-server-props.json")
        self.props = json.load(open(self.props_path, encoding="utf-8")) if os.path.exists(self.props_path) \
            else {"classes": {}, "props": {}, "methods": {}}
        self.props_new = 0
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
        images = dsim_images(self.bundle, image_ids(el)) if getattr(self, "bundle", None) is not None else None
        ET.ElementTree(part_xml(kind, key, source, el, types, extra, images)).write(
            path, encoding="utf-8", xml_declaration=True)
        self.found.append(key)

    def array_elements(self, b, holder, where, source):
        """The element of an array object as a part of its own, arrelem/<where>/<class>, and the
        fields of a cluster element likewise: inside an array CONSTANT these are frameless
        objects with no label (objFlags 0x20000) that no standalone part can stand in for -
        measured as `Insane Array Constant {graphics}`."""
        for arr in holder.iter("ddo"):
            if arr.get("class") != "indArr":
                continue
            el = arr.find("ddo")
            if el is None:
                continue
            self.save("arrelem", "arrelem/%s/%s" % (where, el.get("class")), source, el, resolve_types(b, el))
            if el.get("class") == "stdClust":
                for f in el.find("paneHierarchy/zPlaneList"):
                    self.save("arrelem", "arrelem/%s/%s" % (where, f.get("class")), source, f,
                              resolve_types(b, f))

    def vi_server_node(self, b, node, source):
        """A Property Node or an Invoke Node on a VI Server class. A property node is composed
        item by item, so what is kept is a TABLE: per class its oMId and nodeName, per property
        its PropItemCode (absent = 0), the short label its terminal type carries, the data type,
        the class that declares it (omidTypeDesc) and whether it was seen read or written.
        An invoke node's parameter rows are kept whole, as a part per class and method."""
        terms = list(node.find("termList"))
        ref = next((t for t in terms if t.find("dco") is not None and t.find("dco").get("class") == "hGrowCItem"
                    and int(t.find("dco").findtext("objFlags") or 0) == 0x10000), None)
        if ref is None:
            return
        rt = resolve_types(b, ref)
        cname = ref_class(rt[0] if rt else None)
        if cname is None:
            return
        if node.get("class") == "propNode":
            # nodeName is the PROPERTY node's caption ("VI"); an invoke node on the same class
            # carries another ("Instr"), so the class row is read off property nodes only
            cl = self.props["classes"].setdefault(cname, {})
            if not cl:
                cl.update({"oMId": node.findtext("oMId"), "nodeName": (node.findtext("nodeName") or "").strip('"')})
                self.props_new += 1
            # an IMPLICIT node (linked to a front-panel object) names that object's ddo and
            # carries a label; its reference terminals are hidden (term objFlags 0x800000)
            implicit = node.find("ddo") is not None
            self.save("node", "node/propNode/implicit" if implicit else "node/propNode", source, node,
                      resolve_types(b, node))
            table = self.props["props"].setdefault(cname, {})
            for t in terms:
                dco = t.find("dco")
                if dco is None or dco.get("class") != "propItem":
                    continue
                plist = dco.find("propList")
                if plist is None or len(plist) != 1:
                    continue        # a multi-level item - not tabled yet
                info = plist[0]
                name = (info.findtext("PropItemName") or "").strip('"')
                dt = resolve_types(b, dco)[0]
                m = lvheap.TYPEID_RE.fullmatch((info.findtext("omidTypeDesc") or "").strip())
                omid = b.heap_type_tree(int(m.group(1))) if m else None
                decl = ref_class(fields_of(omid)[0]) if omid is not None and fields_of(omid) else None
                access = "r" if int(dco.findtext("objFlags") or 0) & 1 else "w"
                row = table.get(name)
                if row is None:
                    table[name] = row = {"code": int(info.findtext("PropItemCode") or 0), "label": dt.get("Label") or "",
                                         "type": type_xml(dt), "declaredBy": decl, "access": "", "source": source}
                    self.props_new += 1
                if access not in row["access"]:
                    row["access"] = "".join(sorted(row["access"] + access))
        else:
            meth = (node.findtext("methName") or "").strip('"')
            if not meth:
                return
            self.save("invoke", "invoke/%s/%s" % (cname, meth_key(meth)), source, node, resolve_types(b, node))
            mt = self.props["methods"].setdefault(cname, {})
            if meth not in mt:
                mt[meth] = {"code": int(node.findtext("methCode") or 0), "source": source}
                self.props_new += 1

    def harvest(self, main_xml, source):
        b = lvheap.Bundle(main_xml)
        self.bundle = b     # save() reads the donor's embedded images (DSIM) from it
        fpdco_ind = {}
        if b.fp is not None and not self.only_props:
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
                self.array_elements(b, fpdco, "fpdco", source)
        if b.bd is None:
            return
        for e in b.bd.iter("SL__arrayElement"):
            cls = e.get("class")
            if cls in ("propNode", "invokeNode"):
                self.vi_server_node(b, e, source)
                continue
            if self.only_props:
                continue
            if cls == "fPTerm":
                ind = fpdco_ind.get(e.find("dco").get("uid"))
                if ind:
                    self.save("fpterm", "fpterm/%s" % ind, source, e, [])
            elif cls == "term":
                dco = e.find("dco")
                if dco is not None and dco.get("class") == "bDConstDCO" and dco.find("ddo").get("class") == "bDFixed":
                    # a palette FIXED constant (Empty String, Pi, End of Line ...): which one is
                    # the ddo's `cnst` code, absent for 0 (Carriage Return)
                    ddo = dco.find("ddo")
                    self.save("fixed", "fixed/%s" % (ddo.findtext("cnst") or "00"), source, e, resolve_types(b, e))
                elif dco is not None and dco.get("class") == "bDConstDCO":
                    ddo = dco.find("ddo")
                    types = resolve_types(b, e)
                    tk = lvheap.type_key(types[0])
                    self.save("const", "const/%s/%s" % (ddo.get("class"), tk), source, e, types)
                    self.array_elements(b, e, "const", source)
            elif cls == "signal":
                n = str(len(e.find("termList")))
                wt = e.findtext("compressedWireTable")
                if wt and (n not in self.wiretables or len(wt) < len(self.wiretables[n])):
                    self.wiretables[n] = wt
                    self.wt_new += 1
            elif cls == "iUse":
                # a subVI call node; the composer keeps the node and one terminal of each kind
                # (input, output, unused) and rebuilds the termList from the callee's own pane
                self.save("iuse", "iuse/node", source, e, resolve_types(b, e))
            elif cls == "select":
                # a Case Structure. A TWO-frame BOOLEAN one keeps its own key, so frame order and
                # selector type are known: SelectorRange start 0 is False, 1 is True. Any other
                # selector is cut as select/<kind>; the composer rebuilds its frames and ranges.
                frames = e.find("diagramList")
                sel = next((t for t in e.find("termList") if t.find("dco") is not None
                            and t.find("dco").get("class") == "caseSel"), None)
                if frames is not None and sel is not None:
                    st = resolve_types(b, sel)
                    kind = {"Boolean": "bool", "String": "string", "Cluster": "error"}.get(
                        st[0].get("Type") if st else None, "int")
                    if kind == "bool" and len(frames) != 2:
                        kind = None
                    if kind:
                        self.save("struct", "struct/select/%s" % kind, source, e, resolve_types(b, e))
                        for direction, tun in case_tunnels(e):
                            self.save("seltun", "seltun/%s/%s" % ("bool" if kind == "bool" else "n", direction),
                                      source, tun, resolve_types(b, tun))
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
                for direction, tun in plain_tunnels(e, indexing=True):
                    self.save("tunnel", "tunnel/%s/%s-index" % (cls, direction), source, tun,
                              resolve_types(b, tun))
            elif cls in ("mux", "demux", "aIndx", "aBuild"):
                # Bundle, Unbundle, Index Array, Build Array: nodes of their own class, not prims;
                # the composer rebuilds their terminal lists from the wired types
                self.save("node", "node/%s" % cls, source, e, resolve_types(b, e))
            elif cls == "eventStruct":
                # an Event Structure: dynamic-event and timeout terminals, selTun tunnels,
                # EventNodeEvents (one EventSpec per registration), dataNodeList per frame
                self.save("struct", "struct/event", source, e, resolve_types(b, e))
                for direction, tun in case_tunnels(e):
                    self.save("seltun", "seltun/event/%s" % direction, source, tun, resolve_types(b, tun))
            elif cls == "eventDataNode":
                # the left-hand node of an event frame: an Unbundle By Name over "Event Data"
                self.save("node", "node/eventData", source, e, resolve_types(b, e))
            elif cls == "flatSequence":
                # a Flat Sequence: sequenceFrame structures side by side, each with seqTun
                # tunnels whose DCO termList is [inner, outer] - as a loop's
                self.save("struct", "struct/flatSequence", source, e, resolve_types(b, e))
                for fr in e.find("sequenceList"):
                    srn = fr.find("diagramList/SL__arrayElement/nodeList/SL__arrayElement[@class='sRN']/termList")
                    by_uid = {t.get("uid"): t for t in srn} if srn is not None else {}
                    for t in fr.find("termList"):
                        dco = t.find("dco")
                        if dco is None or dco.get("class") != "seqTun":
                            continue
                        uids = [x.get("uid") for x in dco.find("termList")]
                        inner = by_uid.get(uids[0])
                        if inner is None:
                            continue
                        wrap = ET.Element("wrap")
                        wrap.append(copy.deepcopy(t))
                        wrap.append(copy.deepcopy(inner))
                        d = "out" if int(dco.findtext("objFlags") or 0) & 1 else "in"
                        self.save("seqtun", "seqtun/%s" % d, source, wrap, resolve_types(b, wrap))
            elif cls == "commentNode":
                # Diagram Disable (commentMode) and Conditional Disable structures
                kind = "diagram" if e.find("commentMode") is not None else "conditional"
                self.save("struct", "struct/disable/%s" % kind, source, e, resolve_types(b, e))
                for direction, tun in case_tunnels(e, "commentTun"):
                    self.save("commenttun", "commenttun/%s" % direction, source, tun, resolve_types(b, tun))
            elif cls == "gRef":
                # a Local Variable: one terminal, an input when it writes (term objFlags 0x8000)
                t = e.find("termList/SL__arrayElement")
                rw = "write" if int(t.findtext("objFlags") or 0) & 0x8000 else "read"
                self.save("node", "node/gRef/%s" % rw, source, e, resolve_types(b, e))
            elif cls == "hiddenFBNode":
                # a Feedback Node is TWO heap nodes naming each other, saved as one <wrap>
                su = e.find("SlaveFBInputNode").get("uid") if e.find("SlaveFBInputNode") is not None else None
                slave = next((x for x in b.bd.iter("SL__arrayElement") if x.get("uid") == su
                              and x.get("class") == "slaveFBInputNode"), None)
                if slave is not None:
                    wrap = ET.Element("wrap")
                    wrap.append(copy.deepcopy(e))
                    wrap.append(copy.deepcopy(slave))
                    self.save("node", "node/feedback", source, wrap, resolve_types(b, wrap))
            elif cls == "nMux":
                # Bundle By Name and Unbundle By Name share a class; objFlags 0x10000 marks the
                # unbundling one (measured: 0x50000 against 0x40000 on an AIXML-generated pair)
                kind = "unbundle" if int(e.findtext("objFlags") or 0) & 0x10000 else "bundle"
                self.save("node", "node/nMux/%s" % kind, source, e, resolve_types(b, e))
            elif cls == "prim":
                rid = e.findtext("primResID")
                if rid is None:
                    continue
                types = resolve_types(b, e)
                self.save("prim", "prim/%s" % rid, source, e, types)


def main(argv):
    replace = "--replace" in argv
    clean = "--clean" in argv
    only_props = "--props" in argv
    args = [a for a in argv if a not in ("--replace", "--clean", "--props")]
    parts_dir, inputs = args[0], args[1:]
    if len(inputs) == 1 and inputs[0].startswith("@"):
        inputs = [l.strip() for l in open(inputs[0][1:], encoding="utf-8") if l.strip()]   # a list file
    h = Harvester(parts_dir, replace, clean, only_props)
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
    if h.props_new:
        json.dump(h.props, open(h.props_path, "w", encoding="utf-8", newline="\n"), indent=1, sort_keys=True,
                  ensure_ascii=False)
        print("vi-server-props.json: %d new entries" % h.props_new)
    for k in sorted(h.found):
        print(k)
    print("wire tables for %d end counts (%d improved)" % (len(h.wiretables), h.wt_new), file=sys.stderr)
    print("%d new part(s) in %s" % (len(h.found), parts_dir), file=sys.stderr)


if __name__ == "__main__":
    main(sys.argv[1:])
