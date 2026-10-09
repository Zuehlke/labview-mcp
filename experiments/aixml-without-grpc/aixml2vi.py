# -*- coding: utf-8 -*-
"""AIXML -> .vi with NO LabVIEW and NO NI gRPC service: compose the heap with pylabview.

    aixml2vi.py <in.xml> <out.vi> [--parts DIR] [--keep DIR]

The VI is assembled from PARTS (harvest_parts.py): a skeleton bundle for the empty VI, plus one
heap subtree per front-panel control, diagram terminal, constant and primitive, each carrying
its types as self-contained trees. The composer renumbers every part, gives it its AIXML name,
type and value, wires the nets as heap `signal`s, writes the VCTP type table, the heap type
slice and the connector pane, and hands the bundle to pylabview's own writer.

Source-only VIs carry no compiled code, so LabVIEW recompiles from the diagram on load
(experiments/pylabview/FINDINGS.md section 3.7): geometry and scheduling fields are free, only
identity, topology and types are load-bearing.

SCOPE (what this prototype composes): Control, Indicator, Constant of the scalar types
double/single/intN/uintN/bool/string/path; Node for any primitive whose part has been
harvested and whose name is in primitive-names.tsv; conIdx/connection on the connector pane.
Structures, subVI calls, clusters and arrays are refused by name rather than half-written.
"""
import copy
import csv
import os
import random
import shutil
import struct
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # the bundle's ._pth leaves it off
import lvheap  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
TABLES = os.path.join(lvheap.REPO, "experiments", "pylabview")
DOCS = os.path.join(lvheap.REPO, "docs")

INDEX_SHIFT = 3   # consolidated 1 and 2 are the two connector-pane Function types

SCALARS = {
    "double": ("NumFloat64", "stdNum"), "single": ("NumFloat32", "stdNum"),
    "int8": ("NumInt8", "stdNum"), "int16": ("NumInt16", "stdNum"),
    "int32": ("NumInt32", "stdNum"), "int64": ("NumInt64", "stdNum"),
    "uint8": ("NumUInt8", "stdNum"), "uint16": ("NumUInt16", "stdNum"),
    "uint32": ("NumUInt32", "stdNum"), "uint64": ("NumUInt64", "stdNum"),
    "bool": ("Boolean", "stdBool"), "string": ("String", "stdString"),
    "path": ("Path", "stdPath"),
}
INT_FMT = {"NumInt8": ">b", "NumInt16": ">h", "NumInt32": ">i", "NumInt64": ">q",
           "NumUInt8": ">B", "NumUInt16": ">H", "NumUInt32": ">I", "NumUInt64": ">Q"}


class Unsupported(Exception):
    pass


def type_tree(aixml_type, label=None):
    if aixml_type not in SCALARS:
        raise Unsupported("type %r is not composable yet" % aixml_type)
    t, _ = SCALARS[aixml_type]
    el = ET.Element("TypeDesc", {"Type": t})
    if t not in ("Boolean",):
        el.set("Prop1", "0xFFFFFFFF" if t in ("String", "Path") else "0")
    el.set("Format", "inline")
    if label:
        el.set("Label", label)
    return el


def unescape(s):
    """AIXML escapes a few characters in names as \\XX hex."""
    out, i = [], 0
    while i < len(s):
        if s[i] == "\\" and i + 2 < len(s) + 1 and all(c in "0123456789ABCDEFabcdef" for c in s[i + 1:i + 3]):
            out.append(chr(int(s[i + 1:i + 3], 16)))
            i += 3
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def pairs(attr):
    """'x:5.value,y:7.out' -> [('x', '5.value'), ('y', '7.out')]; an empty net is None."""
    out = []
    for item in (attr or "").split(","):
        if not item:
            continue
        term, _, net = item.rpartition(":")
        out.append((unescape(term), net or None))
    return out


def const_value(tree, value):
    t = tree.get("Type")
    if t == "NumFloat64":
        return struct.pack(">d", float(value or 0)).hex().upper()
    if t == "NumFloat32":
        return struct.pack(">f", float(value or 0)).hex().upper()
    if t in INT_FMT:
        return struct.pack(INT_FMT[t], int(float(value or 0))).hex().upper()
    if t == "Boolean":
        return "01" if str(value).lower() in ("true", "1") else "00"
    if t == "String":
        b = unescape(value or "").encode("mac_roman")
        return (struct.pack(">I", len(b)) + b).hex().upper()
    if t == "Path":
        v = unescape(value or "")
        if not v:
            # LabVIEW's empty path: type 1 (relative), no components
            body = struct.pack(">HH", 1, 0)
        else:
            absolute = len(v) > 1 and v[1] == ":"
            comps = [c for c in v.replace("/", "\\").split("\\") if c]
            body = struct.pack(">HH", 0 if absolute else 1, len(comps))
            for c in comps:
                cb = c.encode("mac_roman")
                body += struct.pack(">B", len(cb)) + cb
        return (b"PTH0" + struct.pack(">I", len(body)) + body).hex().upper()
    raise Unsupported("no constant encoding for %s" % t)


def load_names():
    prim_by_name = {}
    with open(os.path.join(TABLES, "primitive-names.tsv"), encoding="utf-8") as f:
        for row in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t"):
            if not row or row[0] == "primResID":
                continue
            rid, name = row[0], row[1]
            seen = int(row[2]) if len(row) > 2 and row[2].isdigit() else 0
            if name not in prim_by_name or seen > prim_by_name[name][1]:
                prim_by_name[name] = (rid, seen)
    terms = {}
    with open(os.path.join(TABLES, "terminal-names.tsv"), encoding="utf-8") as f:
        for row in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t"):
            if not row or row[0] == "primResID":
                continue
            terms[(row[0], row[2])] = row[3]
    return {n: r for n, (r, _) in prim_by_name.items()}, terms


def load_pane_tables():
    codes, geo = {}, {}
    with open(os.path.join(DOCS, "connector-pane-typecodes.tsv"), encoding="utf-8") as f:
        for l in f:
            if l.startswith("#") or not l.strip():
                continue
            c = l.rstrip("\n").split("\t")
            codes[int(c[0])] = (int(c[1], 16), int(c[2]))
    return codes


class Parts:
    def __init__(self, d):
        self.d = d

    def get(self, key):
        p = os.path.join(self.d, key.replace("/", "__").replace(".", "_") + ".xml")
        if not os.path.exists(p):
            return None
        root = ET.parse(p).getroot()
        types = list(root.find("types"))
        heap = copy.deepcopy(list(root.find("heap"))[0])
        return heap, types, root.get("source")


class Composer:
    def __init__(self, parts_dir):
        self.parts = Parts(parts_dir)
        self.skel = os.path.join(parts_dir, "skeleton")
        self.types = lvheap.TypeTable()
        self.types.toplevel = [None, None]   # consolidated 1, 2: filled in by the pane
        self.next_uid = 20000
        self.prim_by_name, self.term_names = load_names()
        self.pane_codes = load_pane_tables()
        self.fp = ET.parse(os.path.join(self.skel, "skeleton_FPHb.xml")).getroot()
        self.bd = ET.parse(os.path.join(self.skel, "skeleton_BDHb.xml")).getroot()
        self.main = ET.parse(os.path.join(self.skel, "skeleton.xml")).getroot()
        self.used_uids = set(lvheap.all_uids(self.fp)) | set(lvheap.all_uids(self.bd))
        # empty the skeleton's own two controls
        root = self.fp.find("root")
        self.ddo_list = root.find("ddoList")
        self.pane_z = root.find("paneHierarchy/zPlaneList")
        self.cons = root.find("conPane/cons")
        self.con_id = int(root.findtext("conPane/conId"))
        for lst in (self.ddo_list, self.pane_z, self.cons):
            for c in list(lst):
                lst.remove(c)
        diag = self.bd.find("root")
        self.diag_z = diag.find("zPlaneList")
        self.node_list = diag.find("nodeList")
        self.srn = self.node_list.find("SL__arrayElement[@class='sRN']")
        self.srn_terms = self.srn.find("termList")
        self.signals = diag.find("signalList")
        for lst in (self.diag_z, self.srn_terms, self.signals):
            for c in list(lst):
                lst.remove(c)
        self.used_uids = set(lvheap.all_uids(self.fp)) | set(lvheap.all_uids(self.bd))
        self.terminal_uid = {}       # (aixml uid, terminal name) -> heap term uid
        self.net_type = {}           # net -> inlined type tree (unlabelled)
        self.report = []

    # ---- uids and types ------------------------------------------------------------------
    def alloc(self):
        while self.next_uid in self.used_uids:
            self.next_uid += 1
        self.used_uids.add(self.next_uid)
        return self.next_uid

    def claim(self, uid):
        uid = int(uid)
        if uid in self.used_uids:
            return self.alloc()
        self.used_uids.add(uid)
        return uid

    def heap_tid(self, tree):
        fid = self.types.register(tree)
        cons = self.types.use(fid)
        return cons - INDEX_SHIFT + 1

    def instantiate(self, key, own_uid=None, own_path=None):
        got = self.parts.get(key)
        if got is None:
            return None
        heap, types, source = got
        lvheap.renumber(heap, self.alloc)
        if own_uid is not None:
            target = heap if own_path is None else heap.find(own_path)
            old = target.get("uid")
            new = self.claim(own_uid)
            target.set("uid", str(new))
            self.used_uids.discard(int(old))
        return heap, types, source

    # ---- elements ------------------------------------------------------------------------
    def add_fp(self, el, row):
        name = unescape(el.get("_name"))
        is_ind = el.tag == "Indicator"
        aixml_type = el.get("type")
        t, ddo_class = SCALARS.get(aixml_type, (None, None))
        if t is None:
            raise Unsupported("%s %r has type %r" % (el.tag, name, aixml_type))
        want, other = ("ind", "ctl") if is_ind else ("ctl", "ind")
        key = "fpdco/%s/%s/%s" % (ddo_class, want, t)
        got = self.instantiate(key)
        flipped = False
        if got is None:
            # any representation of the same class will do; the type is rewritten anyway
            for k in [k for k in self._keys("fpdco/%s/%s/" % (ddo_class, want))] + \
                     [k for k in self._keys("fpdco/%s/%s/" % (ddo_class, other))]:
                got = self.instantiate(k)
                flipped = "/%s/" % other in k
                break
        if got is None:
            raise Unsupported("no front-panel part for %s (%s)" % (ddo_class, t))
        fpdco, _, src = got
        ddo = fpdco.find("ddo")
        old = int(ddo.get("uid"))
        ddo.set("uid", str(self.claim(el.get("uid"))))
        self.used_uids.discard(old)
        if flipped:
            for e in (fpdco, ddo):
                f = e.find("objFlags")
                v = int(f.text) if f is not None else 0
                v = v | 1 if is_ind else v & ~1
                if f is None:
                    f = ET.Element("objFlags")
                    e.insert(0, f)
                f.text = str(v)
        self._set_label(ddo, name)
        tree = type_tree(aixml_type, label=name)
        tids = [self.heap_tid(tree) for _ in lvheap.typeid_elements(fpdco)]
        lvheap.set_typeids(fpdco, tids)
        con = el.get("conIdx")
        conn = fpdco.find("conNum")
        if conn is None:
            conn = ET.SubElement(fpdco, "conNum")
        conn.text = con if con is not None else "-1"
        col = 1 if is_ind else 0
        lvheap.move_to(ddo, 16 + 56 * row, 16 + 300 * col)
        self.pane_z.append(fpdco)
        ET.SubElement(self.ddo_list, "SL__arrayElement", {"uid": fpdco.get("uid")})
        if con is not None:
            c = ET.SubElement(self.cons, "SL__arrayElement", {"class": "ConpaneConnection"})
            if con != "0":
                c.set("index", con)
            ET.SubElement(c, "ConnectionDCO", {"uid": fpdco.get("uid")})
            self.pane_terms.append((int(con), tree, is_ind, el.get("connection") or "required"))
        # diagram terminal
        term, _, _ = self.instantiate("fpterm/%s" % want)
        term.find("dco").set("uid", fpdco.get("uid"))
        self.diag_z.append(ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
        self.srn_terms.append(term)
        self.terminal_uid[(el.get("uid"), "value")] = term.get("uid")
        self.placed.append((term, el))
        if not is_ind:
            for _, net in pairs(el.get("outputs")):
                if net:
                    self.net_type[net] = type_tree(aixml_type)
        self.report.append("%s %r <- %s" % (el.tag, name, src if not flipped else src + " (flipped)"))

    def add_const(self, el):
        aixml_type = el.get("type")
        t, ddo_class = SCALARS.get(aixml_type, (None, None))
        if t is None:
            raise Unsupported("Constant %r has type %r" % (el.get("_name"), aixml_type))
        got = self.instantiate("const/%s/%s" % (ddo_class, t))
        if got is None:
            keys = self._keys("const/%s/" % ddo_class)
            got = self.instantiate(keys[0]) if keys else None
        if got is None:
            raise Unsupported("no constant part for %s" % t)
        term, _, src = got
        dco = term.find("dco")
        ddo = dco.find("ddo")
        old = int(ddo.get("uid"))
        ddo.set("uid", str(self.claim(el.get("uid"))))
        self.used_uids.discard(old)
        name = unescape(el.get("_name") or "")
        self._set_label(ddo, name)
        tree = type_tree(aixml_type, label=name or None)
        lvheap.set_typeids(term, [self.heap_tid(tree) for _ in lvheap.typeid_elements(term)])
        dco.find("ConstValue").text = const_value(tree, el.get("value"))
        self.diag_z.append(ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
        self.srn_terms.append(term)
        self.terminal_uid[(el.get("uid"), "value")] = term.get("uid")
        self.placed.append((ddo, el))
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = type_tree(aixml_type)
        self.report.append("Constant %r <- %s" % (name, src))

    def add_node(self, el):
        name = unescape(el.get("_name"))
        rid = self.prim_by_name.get(name)
        if rid is None:
            raise Unsupported("Node %r is not a primitive this prototype knows" % name)
        got = self.instantiate("prim/%s" % rid, own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("Node %r (primResID %s) has no harvested part" % (name, rid))
        prim, donor_types, src = got
        terms = [t for t in prim.find("termList") if t.get("class") == "term"]
        by_name = {}
        for t in terms:
            pidx = t.find("dco").findtext("parmIndex") or "out"
            tname = self.term_names.get((rid, pidx))
            if tname is None:
                raise Unsupported("Node %r: no name for parmIndex %s" % (name, pidx))
            by_name[tname] = t
        ins, outs = pairs(el.get("inputs")), pairs(el.get("outputs"))
        for tname, _ in ins + outs:
            if tname not in by_name:
                raise Unsupported("Node %r has no terminal %r (known: %s)"
                                  % (name, tname, ", ".join(sorted(by_name))))
        # the type each term will carry: wired input -> the net's type; output -> the donor's,
        # unless the donor output had the same type as a donor input (polymorphic arithmetic),
        # in which case it follows what is wired to that input.
        idx_of = {id(t): i for i, t in enumerate(terms)}
        tel = lvheap.typeid_elements(prim)
        donor = [lvheap.strip_label(x) for x in donor_types]
        new_types = list(donor)
        canon = [ET.tostring(x) for x in donor]
        wired_in = {}
        for tname, net in ins:
            if net and net in self.net_type:
                i = idx_of[id(by_name[tname])]
                new_types[i] = self.net_type[net]
                wired_in[i] = net
        for tname, net in outs:
            i = idx_of[id(by_name[tname])]
            for j, jnet in wired_in.items():
                if canon[j] == canon[i]:
                    new_types[i] = new_types[j]
                    break
            if net:
                self.net_type[net] = new_types[i]
        # unwired terms of the same donor type follow too (e.g. the other operand)
        lvheap.set_typeids(prim, [self.heap_tid(new_types[i]) for i in range(len(tel))])
        for tname, t in by_name.items():
            self.terminal_uid[(el.get("uid"), tname)] = t.get("uid")
        self.node_list.append(prim)
        self.diag_z.append(ET.Element("SL__arrayElement", {"uid": prim.get("uid")}))
        self.placed.append((prim, el))
        self.report.append("Node %r = primResID %s <- %s" % (name, rid, src))

    def _keys(self, prefix):
        pre = prefix.replace("/", "__").replace(".", "_")
        return sorted(f[:-4].replace("__", "/") for f in os.listdir(self.parts.d)
                      if f.startswith(pre) and f.endswith(".xml"))

    @staticmethod
    def _set_label(ddo, name):
        for part in ddo.find("partsList"):
            if part.get("class") == "label" and part.findtext("partID") == "16":
                tr = part.find("textRec")
                txt = tr.find("text")
                if txt is None:
                    txt = ET.Element("text")
                    tr.insert(1, txt)
                txt.text = '"%s"' % name.replace("\\", "\\\\").replace('"', '\\"')
                return

    # ---- wiring, layout, pane -------------------------------------------------------------
    def wire(self, elements):
        nets = {}
        for el in elements:
            for tname, net in pairs(el.get("outputs")):
                if net:
                    nets.setdefault(net, {"src": None, "sinks": []})["src"] = (el, tname)
            for tname, net in pairs(el.get("inputs")):
                if net:
                    nets.setdefault(net, {"src": None, "sinks": []})["sinks"].append((el, tname))
        for net, ends in nets.items():
            if ends["src"] is None:
                raise Unsupported("net %r has no source" % net)
            if not ends["sinks"]:
                continue
            uids = []
            for el, tname in [ends["src"]] + ends["sinks"]:
                key = (el.get("uid"), "value" if el.tag in ("Control", "Indicator", "Constant") else tname)
                uids.append(self.terminal_uid[key])
            sig = ET.SubElement(self.signals, "SL__arrayElement", {"class": "signal", "uid": str(self.alloc())})
            tl = ET.SubElement(sig, "termList", {"elements": str(len(uids))})
            for u in uids:
                ET.SubElement(tl, "SL__arrayElement", {"uid": u})
            ET.SubElement(sig, "state").text = "1"
            ET.SubElement(sig, "compressedWireTable").text = "0208"
            ET.SubElement(sig, "lastSignalKind").text = "266"
        return nets

    def layout(self, elements, nets):
        level = {}
        src_of = {}
        for net, ends in nets.items():
            for sink, _ in ends["sinks"]:
                src_of.setdefault(sink.get("uid"), []).append(ends["src"][0].get("uid"))

        def lv(uid, stack=()):
            if uid in level:
                return level[uid]
            if uid in stack:
                return 0
            level[uid] = 1 + max([lv(s, stack + (uid,)) for s in src_of.get(uid, [])] or [-1])
            return level[uid]
        for el in elements:
            lv(el.get("uid"))
        rows = {}
        for obj, el in self.placed:
            L = level.get(el.get("uid"), 0)
            r = rows.get(L, 0)
            rows[L] = r + 1
            lvheap.move_to(obj, 40 + 70 * r, 40 + 160 * L)

    def connector_pane(self):
        code, slots = self.pane_codes.get(self.con_id, (0x108, 16))
        void = 0
        for func_flags, idx in ((0x200, 0), (0x0, 1)):
            f = ET.Element("TypeDesc", {"Type": "Function", "FuncFlags": "0x%X" % func_flags,
                                        "Pattern": "0x%X" % code, "HasThrall": "0", "Format": "inline"})
            children = [(void, 0)] * slots
            for con, tree, is_ind, need in self.pane_terms:
                flags = {"required": 0x1000, "recommended": 0x0800}.get(need, 0)
                if is_ind:
                    flags |= 0x0100
                children[con] = (self.types.register(tree), flags)
            for fid, flags in children:
                ET.SubElement(f, "TypeDesc", {"TypeID": str(fid), "Flags": "0x%04X" % flags})
            self.types.toplevel[idx] = self.types.add_flat(f)

    # ---- the whole thing -----------------------------------------------------------------
    def compose(self, aixml_path):
        vi = ET.parse(aixml_path).getroot()
        self.vi_name = unescape(vi.get("_name") or "Composed.vi")
        self.placed, self.pane_terms = [], []
        elements = []
        for el in vi:
            if el.tag in ("Control", "Indicator", "Constant", "Node"):
                elements.append(el)
            elif el.tag in ("FreeLabel",):
                self.report.append("skipped %s (comments are not composed yet)" % el.tag)
            else:
                raise Unsupported("element <%s> is not composable yet" % el.tag)
        row = {"Control": 0, "Indicator": 0}
        for el in [e for e in elements if e.tag in ("Control", "Indicator")]:
            self.add_fp(el, row[el.tag])
            row[el.tag] += 1
        for el in [e for e in elements if e.tag == "Constant"]:
            self.add_const(el)
        # nodes in dependency order, so a node's input types are known when it is placed
        pending = [e for e in elements if e.tag == "Node"]
        while pending:
            ready = [e for e in pending if all(net in self.net_type or net is None
                                               for _, net in pairs(e.get("inputs")))]
            if not ready:
                ready = pending[:1]
            for e in ready:
                self.add_node(e)
                pending.remove(e)
        nets = self.wire(elements)
        self.layout(elements, nets)
        self.connector_pane()
        self.description = vi.get("description") or ""

    def write(self, out_dir, base):
        os.makedirs(out_dir, exist_ok=True)
        for f in os.listdir(self.skel):
            if f.endswith(".bin") or f.endswith(".png"):
                shutil.copy(os.path.join(self.skel, f), os.path.join(out_dir, f.replace("skeleton", base)))
        # Only the lists this composer grows get a fresh count. `elements` is NOT always a child
        # count: the connector pane's `cons` is a sparse array whose `elements` is the SLOT count
        # (16 on 4833) with one child per wired slot, each carrying its own `index`. Recounting
        # it to its children gave LabVIEW "Con pane not pre-sized correctly" and a broken VI.
        for lst in (self.ddo_list, self.pane_z, self.diag_z, self.srn_terms, self.node_list,
                    self.signals):
            lst.set("elements", str(len(lst)))
        self.cons.set("elements", str(self.pane_codes.get(self.con_id, (0x108, 16))[1]))
        ET.ElementTree(self.fp).write(os.path.join(out_dir, base + "_FPHb.xml"),
                                      encoding="utf-8", xml_declaration=True)
        ET.ElementTree(self.bd).write(os.path.join(out_dir, base + "_BDHb.xml"),
                                      encoding="utf-8", xml_declaration=True)
        m = self.main
        for sec in m.iter():
            if sec.get("File"):
                sec.set("File", sec.get("File").replace("skeleton", base))
        m.find("LVSR/Section").set("Name", self.vi_name)
        m.find("RTSG/Section/Guid").text = "%032x" % random.getrandbits(128)
        m.find("LVSR/Section/Instrument").set("Signature", "%032x" % random.getrandbits(128))
        m.find("STRG/Section/String").text = self.description
        m.find("CONP/Section/TypeDesc").set("TypeID", "1")
        m.find("CPC2/Section/TypeDesc").set("TypeID", "2")
        vctp = m.find("VCTP")
        vctp.remove(vctp.find("Section"))
        vctp.append(self.types.vctp_section())
        heap_count = len(self.types.toplevel) - (INDEX_SHIFT - 1)
        sl = m.find("DTHP/Section/TypeDescSlice")
        sl.set("IndexShift", str(INDEX_SHIFT))
        sl.set("Count", str(heap_count))
        m.find("MUID/Section").set("Value", str(max(self.used_uids)))
        main_xml = os.path.join(out_dir, base + ".xml")
        ET.ElementTree(m).write(main_xml, encoding="utf-8", xml_declaration=True)
        return main_xml


def main(argv):
    parts = os.path.join(HERE, "parts")
    keep = None
    args = []
    i = 0
    while i < len(argv):
        if argv[i] == "--parts":
            parts = argv[i + 1]; i += 2
        elif argv[i] == "--keep":
            keep = argv[i + 1]; i += 2
        else:
            args.append(argv[i]); i += 1
    src, out_vi = args
    c = Composer(parts)
    try:
        c.compose(src)
    except Unsupported as ex:
        print("UNSUPPORTED: %s" % ex, file=sys.stderr)
        return 2
    base = os.path.splitext(os.path.basename(out_vi))[0]
    bdir = keep or tempfile.mkdtemp(prefix="aixml2vi_")
    main_xml = c.write(bdir, base)
    lvheap.rebuild(main_xml, out_vi)
    for r in c.report:
        print(r)
    print("wrote %s (%d bytes), bundle %s" % (out_vi, os.path.getsize(out_vi), bdir))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
