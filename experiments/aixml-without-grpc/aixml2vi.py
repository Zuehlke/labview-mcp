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
import html
import json
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
# LabVIEW's type codes; a signal's lastSignalKind is 0x100 | code for a scalar - measured
# 266 on double wires, 259 on int32, 289 on boolean in NI's own VIs.
TYPE_CODE = {"NumInt8": 0x01, "NumInt16": 0x02, "NumInt32": 0x03, "NumInt64": 0x04,
             "NumUInt8": 0x05, "NumUInt16": 0x06, "NumUInt32": 0x07, "NumUInt64": 0x08,
             "NumFloat32": 0x09, "NumFloat64": 0x0A, "NumFloatExt": 0x0B,
             "NumComplex64": 0x0C, "NumComplex128": 0x0D, "NumComplexExt": 0x0E,
             "Boolean": 0x21, "String": 0x30, "Path": 0x32}
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


NUM_RANK = ["NumUInt8", "NumInt8", "NumUInt16", "NumInt16", "NumUInt32", "NumInt32",
            "NumUInt64", "NumInt64", "NumFloat32", "NumFloat64", "NumFloatExt",
            "NumComplex64", "NumComplex128", "NumComplexExt"]


def plain(tree):
    """A donor type without units or enum labels: UnitFloat64+PhysUnit -> NumFloat64."""
    t = copy.deepcopy(tree)
    if (t.get("Type") or "").startswith("Unit"):
        t.set("Type", "Num" + t.get("Type")[4:])
        for c in list(t):
            if c.tag in ("PhysUnit", "EnumLabel"):
                t.remove(c)
    return t


def numeric_join(types):
    """The type LabVIEW coerces mixed numeric inputs to: the widest representation wins.
    Non-numeric groups are returned as the first member - they are not coerced."""
    ranked = [t for t in types if t.get("Type") in NUM_RANK]
    if len(ranked) != len(types):
        return types[0]
    return max(ranked, key=lambda t: NUM_RANK.index(t.get("Type")))


INT_RANGE = {"NumInt8": (-2 ** 7, 2 ** 7 - 1), "NumInt16": (-2 ** 15, 2 ** 15 - 1),
             "NumInt32": (-2 ** 31, 2 ** 31 - 1), "NumInt64": (-2 ** 63, 2 ** 63 - 1),
             "NumUInt8": (0, 2 ** 8 - 1), "NumUInt16": (0, 2 ** 16 - 1),
             "NumUInt32": (0, 2 ** 32 - 1), "NumUInt64": (0, 2 ** 64 - 1)}
FLOAT_RANGE = {"NumFloat64": ("-inf (0xFFF0000000000000)", "inf (0x7FF0000000000000)", "0 (0x0000000000000000)"),
               "NumFloat32": ("-inf (0xFF800000)", "inf (0x7F800000)", "0 (0x00000000)")}


def adapt_numeric(ddo, t):
    """Make a numeric control part carry representation t. StdNumMin/Max/Inc are typed by the
    control's own type (pylabview reads them as data of that type), so a double part's
    '-inf (0x...)' would not parse as an int32. Format strings follow what LabVIEW wrote on
    AIXML-generated controls: %#_g for floating point, %.0f for integers."""
    if ddo.get("class") != "stdNum" or t not in NUM_RANK:
        return
    if t in INT_RANGE:
        lo, hi = INT_RANGE[t]
        vals, fmt = (str(lo), str(hi), "0"), '"%.0f"'
    elif t in FLOAT_RANGE:
        vals, fmt = FLOAT_RANGE[t], '"%#_g"'
    else:
        raise Unsupported("numeric representation %s is not adaptable yet" % t)
    for tag, v in zip(("StdNumMin", "StdNumMax", "StdNumInc"), vals):
        e = ddo.find(tag)
        if e is not None:
            e.text = v
    for part in ddo.find("partsList"):
        if part.get("class") == "numLabel" and part.find("format") is not None:
            part.find("format").text = fmt


def vi_label(ddo):
    for part in ddo.find("partsList") if ddo.find("partsList") is not None else []:
        if part.get("class") == "label" and part.findtext("partID") == "16":
            t = part.findtext("textRec/text") or ""
            return t[1:-1] if t.startswith('"') and t.endswith('"') else t
    return ""


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
    """A constant's ConstValue as LabVIEW writes it in the heap: big-endian flattened data,
    and ONE EXTRA ZERO BYTE when the data is all zero. Measured over 120 NI VIs, every
    numeric, boolean and string constant: a default value carries exactly one more byte than
    its type, a non-default one never does - 0 as int32 is 0000000000, false is 0000, the
    empty string 0000000000. Without that byte a zero constant breaks the VI on its own."""
    h = _const_value(tree, value)
    return h + "00" if set(h) <= {"0"} else h


def _const_value(tree, value):
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
            # LabVIEW's empty path, as NI's own constants carry it: type 0, no components
            body = struct.pack(">HH", 0, 0)
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
            rid, name = row[0], html.unescape(row[1])  # the tables keep AIXML's escaping
            seen = int(row[2]) if len(row) > 2 and row[2].isdigit() else 0
            if name not in prim_by_name or seen > prim_by_name[name][1]:
                prim_by_name[name] = (rid, seen)
    terms = {}
    with open(os.path.join(TABLES, "terminal-names.tsv"), encoding="utf-8") as f:
        for row in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t"):
            if not row or row[0] == "primResID":
                continue
            terms[(row[0], row[2])] = unescape(html.unescape(row[3]))
    learned = os.path.join(HERE, "terminal-names-learned.tsv")
    if os.path.exists(learned):
        with open(learned, encoding="utf-8") as f:
            for row in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t"):
                if row and row[0] != "primResID":
                    terms.setdefault((row[0], row[2]), unescape(row[3]))
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


def load_pane_geometry():
    """conId -> {slot: (top, left, bottom, right)} from docs/connector-pane-patterns.tsv.
    The table writes a slot as left,top,right,bottom; a heap termBounds is top,left,bottom,right
    - checked against a call node on 4815, whose slot 1 reads 24,16,32,24 in the table and
    (16, 24, 24, 32) in the heap."""
    geo = {}
    with open(os.path.join(DOCS, "connector-pane-patterns.tsv"), encoding="utf-8") as f:
        for l in f:
            c = l.rstrip("\n").split("\t")
            if l.startswith("#") or len(c) < 9 or not c[0].isdigit():
                continue
            slots = {}
            for item in c[8].split(";"):
                if ":" in item:
                    i, b = item.split(":")
                    x0, y0, x1, y1 = [int(v) for v in b.split(",")]
                    slots[int(i)] = (y0, x0, y1, x1)
            geo[int(c[0])] = slots
    return geo


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

    def clean(self, key):
        p = os.path.join(self.d, key.replace("/", "__").replace(".", "_") + ".xml")
        with open(p, encoding="utf-8") as f:
            head = f.read(600)
        return 'clean="1"' in head


class Diagram:
    """One block diagram: the VI's root, or the inside of a structure. Every diagram has its
    own sRN (the node that owns constants and front-panel terminals), its own z-order list,
    node list and signal list - a wire never crosses a diagram, a tunnel splits it in two."""

    def __init__(self, diag_el, owner=None):
        self.el = diag_el
        self.z = diag_el.find("zPlaneList")
        self.nodes = diag_el.find("nodeList")
        self.srn = self.nodes.find("SL__arrayElement[@class='sRN']")
        self.srn_terms = self.srn.find("termList")
        self.signals = diag_el.find("signalList")
        self.owner = owner          # the AIXML <Structure>, None for the root
        self.placed = []            # (heap object with bounds, AIXML element)

    def lists(self):
        return (self.z, self.nodes, self.srn_terms, self.signals)


class Composer:
    def __init__(self, parts_dir):
        self.parts = Parts(parts_dir)
        self.skel = os.path.join(parts_dir, "skeleton")
        self.types = lvheap.TypeTable()
        self.types.toplevel = [None, None]   # consolidated 1, 2: filled in by the pane
        self.next_uid = 20000
        self.prim_by_name, self.term_names = load_names()
        self.pane_codes = load_pane_tables()
        self.pane_geometry = load_pane_geometry()
        wt = os.path.join(parts_dir, "wiretables.json")
        self.wiretables = json.load(open(wt)) if os.path.exists(wt) else {}
        self.wiretables["2"] = "0208"   # measured on every two-ended wire; the harvest's "01" is not
        self.fp = ET.parse(os.path.join(self.skel, "skeleton_FPHb.xml")).getroot()
        self.bd = ET.parse(os.path.join(self.skel, "skeleton_BDHb.xml")).getroot()
        self.main = ET.parse(os.path.join(self.skel, "skeleton.xml")).getroot()
        # empty the skeleton's own two controls
        root = self.fp.find("root")
        self.ddo_list = root.find("ddoList")
        self.pane_z = root.find("paneHierarchy/zPlaneList")
        self.cons = root.find("conPane/cons")
        self.con_id = int(root.findtext("conPane/conId"))
        for lst in (self.ddo_list, self.pane_z, self.cons):
            for c in list(lst):
                lst.remove(c)
        self.root = Diagram(self.bd.find("root"))
        for lst in (self.root.z, self.root.srn_terms, self.root.signals):
            for c in list(lst):
                lst.remove(c)
        self.diagrams = {"root": self.root}     # AIXML uid of a structure -> its Diagram
        self.used_uids = set(lvheap.all_uids(self.fp)) | set(lvheap.all_uids(self.bd))
        self.ends = {}               # (aixml uid, terminal) -> (heap term uid, Diagram)
        self.net_type = {}           # net -> inlined type tree (unlabelled)
        self.loops = []              # (loop heap element, its inner Diagram, parent Diagram)
        self.report = []
        self.search_dirs = []        # where a <Call> target is looked for; the output dir first
        self.links = {}              # callee path -> {"name", "flat", "cons", "offsets": [uid]}

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

    def diag_of(self, el):
        d = self.diagrams.get(el.get("uid_parent") or "root")
        if d is None:
            raise Unsupported("%s %s sits in %s, which is not a composable diagram"
                              % (el.tag, el.get("uid"), el.get("uid_parent")))
        return d

    # ---- elements ------------------------------------------------------------------------
    def add_fp(self, el, row):
        name = unescape(el.get("_name"))
        is_ind = el.tag == "Indicator"
        aixml_type = el.get("type")
        t, ddo_class = SCALARS.get(aixml_type, (None, None))
        if t is None:
            raise Unsupported("%s %r has type %r" % (el.tag, name, aixml_type))
        diag = self.diag_of(el)
        want, other = ("ind", "ctl") if is_ind else ("ctl", "ind")
        key, flipped = self.pick("fpdco/%s/" % ddo_class, want, other, t)
        if key is None:
            raise Unsupported("no front-panel part for %s (%s)" % (ddo_class, t))
        fpdco, _, src = self.instantiate(key)
        ddo = fpdco.find("ddo")
        adapt_numeric(ddo, t)
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
        # conNum 0 is written by OMITTING the field, as LabVIEW's own files do; an explicit
        # <conNum>0</conNum> loads but LabVIEW logs the control as insane ({flags} 0x1000)
        conn = fpdco.find("conNum")
        if conn is None:
            conn = ET.SubElement(fpdco, "conNum")
        conn.text = con if con is not None else "-1"
        if conn.text == "0":
            fpdco.remove(conn)
        if con is not None:
            # A control ON the pane carries fPDCO objFlags 0x10000, plus 0x200 when recommended
            # and 0x80 when required - read off an AIXML-generated stub. Without it LabVIEW logs
            # the control as insane ({flags} 0x1000).
            need = el.get("connection") or "required"
            f = fpdco.find("objFlags")
            if f is None:
                f = ET.Element("objFlags")
                fpdco.insert(0, f)
            f.text = str(int(f.text or 0) | 0x10000 | {"recommended": 0x200, "required": 0x80}.get(need, 0))
        col = 1 if is_ind else 0
        lvheap.move_to(ddo, 16 + 56 * row, 16 + 300 * col)
        # z-order lists run NEWEST FIRST in LabVIEW's own files (ddoList and termList run
        # oldest first); appending made the first control LabVIEW's insane one ({flags} 0x1000)
        self.pane_z.insert(0, fpdco)
        ET.SubElement(self.ddo_list, "SL__arrayElement", {"uid": fpdco.get("uid")})
        if con is not None:
            c = ET.SubElement(self.cons, "SL__arrayElement", {"class": "ConpaneConnection"})
            if con != "0":
                c.set("index", con)
            ET.SubElement(c, "ConnectionDCO", {"uid": fpdco.get("uid")})
            self.pane_terms.append((int(con), tree, is_ind, el.get("connection") or "required"))
        # the diagram terminal goes where the AIXML puts the control: a control read INSIDE a
        # loop has its terminal in the loop's diagram, and so does its wire
        term, _, _ = self.instantiate("fpterm/%s" % want)
        term.find("dco").set("uid", fpdco.get("uid"))
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
        diag.srn_terms.append(term)
        self.ends[(el.get("uid"), "value")] = (term.get("uid"), diag)
        diag.placed.append((term, el))
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
        diag = self.diag_of(el)
        key, _ = self.pick("const/%s/" % ddo_class, None, None, t)
        if key is None:
            raise Unsupported("no constant part for %s" % t)
        term, _, src = self.instantiate(key)
        dco = term.find("dco")
        ddo = dco.find("ddo")
        adapt_numeric(ddo, t)
        old = int(ddo.get("uid"))
        ddo.set("uid", str(self.claim(el.get("uid"))))
        self.used_uids.discard(old)
        name = unescape(el.get("_name") or "")
        self._set_label(ddo, name)
        tree = type_tree(aixml_type, label=name or None)
        lvheap.set_typeids(term, [self.heap_tid(tree) for _ in lvheap.typeid_elements(term)])
        dco.find("ConstValue").text = const_value(tree, el.get("value"))
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
        diag.srn_terms.append(term)
        self.ends[(el.get("uid"), "value")] = (term.get("uid"), diag)
        diag.placed.append((ddo, el))
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = type_tree(aixml_type)
        self.report.append("Constant %r <- %s" % (name, src))

    def add_node(self, el):
        name = unescape(el.get("_name"))
        rid = self.prim_by_name.get(name)
        if rid is None:
            raise Unsupported("Node %r is not a primitive this prototype knows" % name)
        diag = self.diag_of(el)
        got = self.instantiate("prim/%s" % rid, own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("Node %r (primResID %s) has no harvested part" % (name, rid))
        prim, donor_types, src = got
        # `inplace` is a compiler hint from the DONOR's last compile: Add's output reusing an
        # input buffer. Carried into a node whose inputs now differ in type it is wrong, and a
        # source-only VI is recompiled anyway (FINDINGS 3.7), so it goes.
        for d in prim.iter("dco"):
            for e in d.findall("inplace"):
                d.remove(e)
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
        # One typeDesc per term, in term order - checked rather than assumed, because the
        # whole type assignment below is indexed by it.
        tel = lvheap.typeid_elements(prim)
        term_td = [lvheap.typeid_elements(t) for t in terms]
        if len(tel) != len(terms) or any(len(x) != 1 for x in term_td):
            raise Unsupported("Node %r: part has %d typeDescs over %d terms" % (name, len(tel), len(terms)))
        idx_of = {id(t): i for i, t in enumerate(terms)}
        # Donor types lose their decorations first: a part cut from "Numeric with Units.vi"
        # carries UnitFloat64 with a PhysUnit, one from an enum example an EnumLabel list.
        donor = [plain(lvheap.strip_label(x)) for x in donor_types]
        new_types = list(donor)
        canon = [ET.tostring(x) for x in donor]
        wired_in = {}
        for tname, net in ins:
            if net and net in self.net_type:
                i = idx_of[id(by_name[tname])]
                new_types[i] = self.net_type[net]
                wired_in[i] = net
        # A polymorphic primitive ADAPTS: every terminal sharing a donor type (Add's x, y and
        # x+y) takes the coerced type of what is wired into that group - int32 + double makes
        # all three double. MEASURED on NI's Continuous Decimating.vi (Multiply, int32 into y):
        # a coerced input keeps the WIRED type on its terminal, and the terminal carries
        # objFlags bit 0x40000 - the coercion dot - with dsw 1024; the adapted type sits on
        # the other members. Typing the coerced terminal with the adapted type was BROKEN.
        joined = list(new_types)
        for i in range(len(terms)):
            group = [j for j in range(len(terms)) if canon[j] == canon[i]]
            wired = [new_types[j] for j in group if j in wired_in]
            if wired:
                joined[i] = numeric_join(wired)
        for i, t in enumerate(terms):
            if i in wired_in and ET.tostring(joined[i]) != ET.tostring(new_types[i]):
                f = t.find("objFlags")
                if f is None:
                    f = ET.Element("objFlags")
                    t.insert(0, f)
                f.text = str(int(f.text or 0) | 0x40000)
                dco = t.find("dco")
                dsw = dco.find("dsw")
                if dsw is None:
                    dsw = ET.Element("dsw")
                    dco.insert(list(dco).index(dco.find("typeDesc")) + 1, dsw)
                dsw.text = "1024"
            elif i not in wired_in:
                new_types[i] = joined[i]
        for tname, net in outs:
            if net:
                self.net_type[net] = new_types[idx_of[id(by_name[tname])]]
        lvheap.set_typeids(prim, [self.heap_tid(new_types[i]) for i in range(len(terms))])
        for tname, t in by_name.items():
            self.ends[(el.get("uid"), tname)] = (t.get("uid"), diag)
        diag.nodes.append(prim)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": prim.get("uid")}))
        diag.placed.append((prim, el))
        self.report.append("Node %r = primResID %s <- %s" % (name, rid, src))

    # ---- structures ----------------------------------------------------------------------
    def add_loop(self, el):
        """A While Loop: the harvested shell keeps its counter `i` (lCnt) and its stop
        terminal (lTst); every tunnel, shift register and inner node of the DONOR is removed,
        one plain input and one plain output tunnel are kept as templates.

        Measured on NI's BUIP_Get_ProjItemRef_From_Path.vi: a tunnel is ONE lpTun DCO whose
        termList names [inner terminal, outer terminal]; the outer terminal sits in the loop's
        own termList, the inner one in the inner diagram's sRN with a bare <dco uid=lpTun/>.
        A plain input tunnel's DCO has objFlags 2048, a plain output's 2049 (bit 0 = output);
        an INDEXING tunnel adds TunnelType 01 and an innerLpTunDCO and is not composed yet."""
        kind = {"While Loop": "whileLoop", "For Loop": "forLoop"}.get(el.get("_name"))
        if kind is None:
            raise Unsupported("structure %r is not composable yet" % el.get("_name"))
        parent = self.diag_of(el)
        loop, donor_types, src = self.instantiate("struct/%s" % kind, own_uid=el.get("uid"))
        for i, e in enumerate(lvheap.typeid_elements(loop)):
            e.set("_t", str(i))     # so pruning below cannot misalign donor types
        inner = Diagram(loop.find("diagramList/SL__arrayElement[@class='diag']"), owner=el)
        # A While Loop has a stop terminal (lTst); a For Loop has none and counts to N instead:
        # its loopLimitDCO names an lMax DCO whose termList is [inner N, outer N], the outer
        # terminal sitting on the loop's border beside the tunnels (TDMS Advanced Synchronous
        # Write.vi). Both keep their counter `i` (lCnt).
        lcnt, ltst = loop.find("loopIndexDCO"), loop.find("loopTestDCO")
        keep = {lcnt.find("termList")[0].get("uid")}
        if ltst is not None:
            keep.add(ltst.find("termList")[0].get("uid"))
        lmax_outer = lmax_inner = None
        if loop.find("loopLimitDCO") is not None:
            lmax_uid = loop.find("loopLimitDCO").get("uid")
            for t in loop.find("termList"):
                if t.find("dco") is not None and t.find("dco").get("uid") == lmax_uid:
                    lmax_outer = t
            if lmax_outer is None:
                raise Unsupported("For Loop shell has no N terminal on its border")
            inner_n, _ = [x.get("uid") for x in lmax_outer.find("dco/termList")]
            keep.add(inner_n)
            lmax_inner = inner_n
        inner_by_dco = {t.find("dco").get("uid"): t for t in inner.srn_terms
                        if t.find("dco") is not None}
        for lst in (loop.find("termList"), loop.find("tunnelList"), loop.find("srDCOList"),
                    inner.signals):
            for c in list(lst):
                if c is not lmax_outer:
                    lst.remove(c)
        for c in list(inner.srn_terms):
            if c.get("uid") not in keep:
                inner.srn_terms.remove(c)
        for c in list(inner.nodes):
            if c.get("class") != "sRN":
                inner.nodes.remove(c)
        for c in list(inner.z):
            if c.get("uid") not in keep and c.get("class") != "attachment":
                inner.z.remove(c)
        # the counter and the stop terminal keep their donor types (int32, boolean)
        for e in lvheap.typeid_elements(loop):
            tree = plain(lvheap.strip_label(donor_types[int(e.get("_t"))]))
            e.text = "TypeID(%d)" % self.heap_tid(tree)
            del e.attrib["_t"]
        parent.nodes.append(loop)
        parent.z.insert(0, ET.Element("SL__arrayElement", {"uid": loop.get("uid")}))
        parent.placed.append((loop, el))
        self.diagrams[el.get("uid")] = inner
        self.loops.append((loop, inner, parent))
        self.ends[(el.get("uid"), "count")] = (lcnt.find("termList")[0].get("uid"), inner)
        if ltst is not None:
            self.ends[(el.get("uid"), "stop")] = (ltst.find("termList")[0].get("uid"), inner)
        if lmax_outer is not None:
            self.ends[(el.get("uid"), "N")] = (lmax_outer.get("uid"), parent)
            self.ends[(el.get("uid"), "maxout")] = (lmax_inner, inner)
            if el.get("maxout"):
                self.net_type[el.get("maxout")] = ET.Element("TypeDesc", {"Type": "NumInt32", "Prop1": "0", "Format": "inline"})
        count = el.get("count") or ""
        if count:
            self.net_type[count] = ET.Element("TypeDesc", {"Type": "NumInt32", "Prop1": "0", "Format": "inline"})
        self.report.append("Structure %r <- %s" % (el.get("_name"), src))

    def add_call(self, el):
        """A subVI call, composed from the CALLEE'S OWN FILE - no LabVIEW, no gRPC.

        The callee is read through pylabview: its pane Function type (CONP), its pattern
        (conPane/conId) and, per slot, the label of the control wired there. Measured on NI's
        Acquire Array of Waveforms (simulated).vi: the caller holds an IDENTICAL copy of the
        callee's CONP type; the iUse node has one terminal per pane slot, typed with the
        callee's labelled slot type (Void where unused), an input carrying term objFlags 0x8000
        and dco objFlags 0x200, an output dco objFlags 0x201; connectorTM is the pane type.
        The link lives twice - VIVI in the VI's link block and IUVI in the diagram's, the
        latter naming the node by uid in LinkOffsetList - with a path relative to the CALLER
        FILE: ["", "<name>.vi"] for a sibling, TpVal 1."""
        target = unescape(el.get("target") or "")
        if "\\" in target or ":" in target or "/" in target:
            raise Unsupported("Call %s: only a sibling VI by file name is composable (%r)" % (el.get("uid"), target))
        path = next((os.path.join(d, target) for d in self.search_dirs
                     if os.path.exists(os.path.join(d, target))), None)
        if path is None:
            raise Unsupported("Call %s: %r not found beside the output VI" % (el.get("uid"), target))
        diag = self.diag_of(el)
        callee = lvheap.Bundle(lvheap.extract(path, tempfile.mkdtemp(prefix="callee_")))
        conp = int(callee.main.find("CONP/Section/TypeDesc").get("TypeID"))
        pane = callee.tree(callee.flat_of_consolidated(conp))
        slots = [c for c in pane if c.tag == "TypeDesc"]
        con_id = int(callee.fp.findtext("root/conPane/conId"))
        geo = self.pane_geometry.get(con_id, {})
        name_at = {}
        for fpdco in callee.fp.iter("SL__arrayElement"):
            if fpdco.get("class") == "fPDCO":
                con = fpdco.findtext("conNum")
                con = 0 if con is None else int(con)
                if con >= 0:
                    name_at[con] = vi_label(fpdco.find("ddo"))
        got = self.instantiate("iuse/node", own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("no call-node part harvested")
        node, _, src = got
        tl = node.find("termList")
        tpl = {}
        for t in tl:
            d = t.find("dco")
            f = int(d.findtext("objFlags") or 0)
            kind = "out" if f & 1 else "in" if t.findtext("objFlags") == "32768" else "none"
            tpl.setdefault(kind, t)
        for kind in ("in", "out", "none"):
            if kind not in tpl:
                raise Unsupported("call-node part lacks a %s terminal" % kind)
        for c in list(tl):
            tl.remove(c)
        by_name = {}
        for i, slot in enumerate(slots):
            flags = int(slot.get("ref.Flags") or "0", 16)
            kind = "none" if slot.get("Type") == "Void" else "out" if flags & 0x100 else "in"
            t = copy.deepcopy(tpl[kind])
            lvheap.renumber(t, self.alloc)
            dco = t.find("dco")
            pi = dco.find("paramIdx")
            if i == 0 and pi is not None:
                dco.remove(pi)
            elif i > 0:
                if pi is None:
                    pi = ET.SubElement(dco, "paramIdx")
                pi.text = str(i)
            if i in geo and dco.find("termBounds") is not None:
                dco.find("termBounds").text = lvheap.fmt_bounds(geo[i])
            slot_type = copy.deepcopy(slot)
            for k in [k for k in slot_type.attrib if k.startswith("ref.")]:
                del slot_type.attrib[k]
            dco.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(slot_type)
            tl.append(t)
            if kind != "none" and i in name_at:
                by_name[name_at[i]] = (t, lvheap.strip_label(slot_type))
        # one terminal per slot of the CALLEE'S pattern: 16 on 4833 - the count must follow, or
        # the node keeps the donor's 12 and LabVIEW calls the caller broken (measured)
        tl.set("elements", str(len(tl)))
        node.find("connectorTM").text = "TypeID(%d)" % self.heap_tid(pane)
        # LabVIEW's own call node (AIXML-generated caller of a 4833 VI) carries objFlags 512;
        # the 4815 donor's 640 adds a bit LabVIEW did not write there
        node.find("objFlags").text = "512"
        for part in node.iter("label"):
            if part.find("textRec/text") is not None:
                part.find("textRec/text").text = '"%s"' % target
                break
        for tname, net in pairs(el.get("inputs")) + pairs(el.get("outputs")):
            if tname not in by_name:
                raise Unsupported("Call %r has no terminal %r on its pane (has: %s)"
                                  % (target, tname, ", ".join(sorted(by_name))))
        for tname, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = by_name[tname][1]
        for tname, (t, _) in by_name.items():
            self.ends[(el.get("uid"), tname)] = (t.get("uid"), diag)
        link = self.links.setdefault(path, {"name": target, "pane": pane, "offsets": []})
        link["offsets"].append(int(node.get("uid")))
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Call %r <- %s (pane %d, %d slots)" % (target, src, con_id, len(slots)))

    def write_links(self):
        """VIVI into LIvi/LVIN and IUVI into LIbd/BDHP, one pair per callee."""
        lvin = self.main.find("LIvi/Section/LVIN")
        bdhp = self.main.find("LIbd/Section/BDHP")
        attrs = {"LinkSaveFlag": "0", "VILinkLibVersion": "1", "VILinkFieldA": "0", "VILinkField4": "1",
                 "VILinkFieldB": "", "VILinkFieldC": "", "VILinkFieldD": "0", "TypedLinkFlags": "0"}
        for path, link in self.links.items():
            cons = self.types.use(self.types.register(link["pane"]))
            for tag, holder in (("VIVI", lvin), ("IUVI", bdhp)):
                rec = ET.SubElement(holder, tag, dict(attrs))
                ET.SubElement(ET.SubElement(rec, "LinkSaveQualName"), "String").text = link["name"]
                pr = ET.SubElement(rec, "LinkSavePathRef", {"Ident": "PTH0", "TpVal": "1"})
                ET.SubElement(pr, "String")
                ET.SubElement(pr, "String").text = link["name"]
                ET.SubElement(rec, "TypeDesc", {"TypeID": str(cons) if tag == "VIVI" else "0"})
                if tag == "IUVI":
                    ol = ET.SubElement(rec, "LinkOffsetList")
                    for uid in link["offsets"]:
                        ET.SubElement(ol, "Offset").text = "0x%04X" % uid
                    ET.SubElement(rec, "VILSPathRef", {"Ident": "PTH0", "TpVal": "0", "ZeroFill": "True"})
                    ET.SubElement(rec, "IUseStr")

    def add_case(self, el):
        """A Case Structure with a BOOLEAN selector and frames "True" and "False".

        Measured on NI's Multiply Force.vi: the selector is one caseSel DCO whose termList
        names one inner terminal per frame, then the outer one; each frame is a diagram of its
        own, mapped to a case value by SelectRangeArray32 (start 0 is the False frame, 1 the
        True one); a tunnel is one selTun DCO with an inner terminal in EVERY frame."""
        frames_el = [f for f in el if isinstance(f.tag, str) and f.tag == "CaseFrame"]
        if sorted(f.get("selector") for f in frames_el) != ["False", "True"]:
            raise Unsupported("Case Structure %s: only a boolean True/False case is composable yet"
                              % el.get("uid"))
        parent = self.diag_of(el)
        case, donor_types, src = self.instantiate("struct/select/bool", own_uid=el.get("uid"))
        for i, e in enumerate(lvheap.typeid_elements(case)):
            e.set("_t", str(i))
        diags = list(case.find("diagramList"))
        value_of = {}
        for r in case.find("SelectRangeArray32"):
            value_of[int(r.findtext("diagramIdx") or 0)] = int(r.findtext("start") or 0)
        sel = next(t for t in case.find("termList") if t.find("dco").get("class") == "caseSel")
        sel_inner = [x.get("uid") for x in sel.find("dco/termList")][:-1]
        for c in list(case.find("termList")):
            if c is not sel:
                case.find("termList").remove(c)
        for c in list(case.find("tunnelList")):
            case.find("tunnelList").remove(c)
        self.cases = getattr(self, "cases", [])
        frame_diags = []
        for idx, d in enumerate(diags):
            fd = Diagram(d)
            for c in list(fd.srn_terms):
                if c.get("uid") not in sel_inner:
                    fd.srn_terms.remove(c)
            for c in list(fd.nodes):
                if c.get("class") != "sRN":
                    fd.nodes.remove(c)
            for lst in (fd.z, fd.signals):
                for c in list(lst):
                    lst.remove(c)
            frame_diags.append(fd)
            want = "True" if value_of.get(idx) == 1 else "False"
            frame = next(f for f in frames_el if f.get("selector") == want)
            fd.owner = frame
            self.diagrams[frame.get("uid")] = fd
            self.ends[(frame.get("uid"), "selectout")] = (sel_inner[idx], fd)
            if frame.get("selectout"):
                self.net_type[frame.get("selectout")] = ET.Element("TypeDesc", {"Type": "Boolean", "Format": "inline"})
        for e in lvheap.typeid_elements(case):
            tree = plain(lvheap.strip_label(donor_types[int(e.get("_t"))]))
            e.text = "TypeID(%d)" % self.heap_tid(tree)
            del e.attrib["_t"]
        parent.nodes.append(case)
        parent.z.insert(0, ET.Element("SL__arrayElement", {"uid": case.get("uid")}))
        parent.placed.append((case, el))
        self.cases.append((case, frame_diags, parent))
        self.ends[(el.get("uid"), "sel")] = (sel.get("uid"), parent)
        self.report.append("Structure 'Case Structure' (boolean) <- %s" % src)

    def add_case_tunnel(self, outer_el, case_el, halves):
        """One case tunnel: its outside half (`outer_el`, on the structure) and one inside half
        per frame (`halves`: frame uid -> Tunnel element), as AIXML writes it."""
        d = "in" if outer_el.get("_id", "").startswith("In") else "out"
        case, frame_diags, parent = next(x for x in self.cases if x[0].get("uid") == case_el.get("uid"))
        got = self.instantiate("seltun/bool/%s" % d)
        if got is None:
            raise Unsupported("no %s case-tunnel part harvested" % d)
        wrap = got[0]
        parts = list(wrap)
        outer, inner_terms = parts[0], parts[1:]
        if d == "in":
            nets = [n for _, n in pairs(outer_el.get("inputs")) if n]
        else:
            nets = [n for h in halves.values() for _, n in pairs(h.get("inputs")) if n]
        tree = next((self.net_type[n] for n in nets if n in self.net_type), None)
        if tree is None:
            raise Unsupported("case tunnel %s: no typed wire reaches it" % outer_el.get("_id"))
        lvheap.set_typeids(wrap, [self.heap_tid(tree) for _ in lvheap.typeid_elements(wrap)])
        case.find("termList").append(outer)
        ET.SubElement(case.find("tunnelList"), "SL__arrayElement", {"uid": outer.find("dco").get("uid")})
        self.ends[(outer_el.get("uid"), "in" if d == "in" else "out")] = (outer.get("uid"), parent)
        for fd, it in zip(frame_diags, inner_terms):
            fd.srn_terms.append(it)
            h = halves.get(fd.owner.get("uid"))
            if h is not None:
                self.ends[(h.get("uid"), "out" if d == "in" else "in")] = (it.get("uid"), fd)
                for _, n in pairs(h.get("outputs")):
                    if n:
                        self.net_type[n] = tree
        for _, n in pairs(outer_el.get("outputs")):
            if n:
                self.net_type[n] = tree
        self.report.append("Case tunnel %s (%s)" % (outer_el.get("_id"), d))

    def add_tunnel(self, el, loop_el):
        d = "in" if el.get("_id", "").startswith("In") else "out"
        if el.get("mode"):
            raise Unsupported("Tunnel %s: mode=%r (indexing) is not composable yet" % (el.get("uid"), el.get("mode")))
        loop, inner, parent = next(x for x in self.loops if x[1].owner is loop_el)
        kind = loop.get("class")
        got = None
        for k in (kind, "whileLoop", "forLoop"):   # a plain lpTun is the same on both loops
            got = self.instantiate("tunnel/%s/%s" % (k, d))
            if got is not None:
                break
        if got is None:
            raise Unsupported("no plain %s-tunnel part harvested" % d)
        wrap = got[0]
        outer, innert = list(wrap)
        dco = outer.find("dco")
        src_net = pairs(el.get("inputs"))[0][1] if pairs(el.get("inputs")) else None
        tree = self.net_type.get(src_net)
        if tree is None:
            raise Unsupported("Tunnel %s: the type of %r is not known" % (el.get("uid"), src_net))
        lvheap.set_typeids(outer, [self.heap_tid(tree) for _ in lvheap.typeid_elements(outer)])
        for e in wrap.iter():
            e.attrib.pop("_t", None)   # the shell's bookkeeping travelled with the template
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = tree
        n = len(loop.find("tunnelList"))
        tb = dco.find("termBounds")
        if tb is not None:
            t, l, b, r = lvheap.parse_bounds(tb.text)
            tb.text = lvheap.fmt_bounds((20 + 16 * n, l, 20 + 16 * n + (b - t), r))
        loop.find("termList").append(outer)
        ET.SubElement(loop.find("tunnelList"), "SL__arrayElement", {"uid": dco.get("uid")})
        inner.srn_terms.append(innert)
        outer_end, inner_end = (outer.get("uid"), parent), (innert.get("uid"), inner)
        self.ends[(el.get("uid"), "in")] = outer_end if d == "in" else inner_end
        self.ends[(el.get("uid"), "out")] = inner_end if d == "in" else outer_end
        self.report.append("Tunnel %s (%s)" % (el.get("_id"), d))

    def add_shiftreg(self, el, loop_el):
        """AIXML <ShiftReg><Left/><Right/></ShiftReg> onto the harvested four-terminal group:
        Left takes the initializer OUTSIDE and supplies the previous value INSIDE; Right
        collects the next value INSIDE and delivers the final one OUTSIDE. Only an
        INITIALIZED register is composed - an uninitialized one takes its type from the inner
        diagram, a cycle this composer does not resolve."""
        left = next((c for c in el if isinstance(c.tag, str) and c.tag == "Left"), None)
        right = next((c for c in el if isinstance(c.tag, str) and c.tag == "Right"), None)
        init = [n for _, n in pairs(left.get("inputs"))] if left is not None else []
        if not init or init[0] is None or init[0] not in self.net_type:
            raise Unsupported("ShiftReg %s: only an initialized register is composable" % el.get("uid"))
        tree = self.net_type[init[0]]
        loop, inner, parent = next(x for x in self.loops if x[1].owner is loop_el)
        kind = loop.get("class")
        got = self.instantiate("srpair/%s" % kind)
        if got is None:
            raise Unsupported("no shift register template harvested for %s" % kind)
        wrap, _, src = got
        r_out, l_out, r_in, l_in = list(wrap)
        lvheap.set_typeids(wrap, [self.heap_tid(tree) for _ in lvheap.typeid_elements(wrap)])
        loop.find("termList").append(r_out)
        loop.find("termList").append(l_out)
        ET.SubElement(loop.find("srDCOList"), "SL__arrayElement", {"uid": r_out.find("dco").get("uid")})
        inner.srn_terms.append(r_in)
        inner.srn_terms.append(l_in)
        n = len(loop.find("srDCOList"))
        for t in (r_out, l_out):
            tb = t.find("dco/termBounds")
            if tb is not None:
                a, l, b, r = lvheap.parse_bounds(tb.text)
                tb.text = lvheap.fmt_bounds((60 + 20 * n, l, 60 + 20 * n + (b - a), r))
        self.ends[(left.get("uid"), "in")] = (l_out.get("uid"), parent)
        self.ends[(left.get("uid"), "out")] = (l_in.get("uid"), inner)
        self.ends[(right.get("uid"), "in")] = (r_in.get("uid"), inner)
        self.ends[(right.get("uid"), "out")] = (r_out.get("uid"), parent)
        for side in (left, right):
            for _, net in pairs(side.get("outputs")):
                if net:
                    self.net_type[net] = tree
        self.report.append("ShiftReg %s <- %s" % (el.get("uid"), src))

    def pick(self, prefix, want, other, t):
        """The best part for a control/constant of type t: (key, flipped).

        Order, each rank measured or reasoned in the docs: a CLEAN part (cut from an
        AIXML-generated VI) beats an exact type match from an NI example, because two example
        parts broke even a pass-through VI (denylist.json). A numeric part of another
        representation is adapted (adapt_numeric), and a part of the other direction is flipped
        - an LED indicator flipped to a control was measured working."""
        best = None
        for k in self._keys(prefix):
            parts = k.split("/")
            direction = parts[2] if want else None
            kt = parts[-1].replace("_", ".")
            if want and direction not in (want, other):
                continue
            exact = kt == t
            adaptable = exact or (kt in NUM_RANK and t in NUM_RANK)
            if not adaptable:
                continue
            clean = self.parts.clean(k)
            rank = (0 if clean else 4) + (0 if exact else 1) + (0 if direction == want else 2)
            if best is None or rank < best[0]:
                best = (rank, k, direction != want)
        return (best[1], best[2]) if best else (None, False)

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
    def endpoints(self, el):
        """(sources, sinks) of one AIXML element, as [(net, endpoint key)]."""
        uid = el.get("uid")
        if el.tag in ("Control", "Indicator", "Constant"):
            return ([(n, (uid, "value")) for _, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, "value")) for _, n in pairs(el.get("inputs")) if n])
        if el.tag in ("Node", "Call"):
            return ([(n, (uid, t)) for t, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, t)) for t, n in pairs(el.get("inputs")) if n])
        if el.tag in ("Tunnel", "Left", "Right"):
            return ([(n, (uid, "out")) for _, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, "in")) for _, n in pairs(el.get("inputs")) if n])
        if el.tag == "Condition":
            return [], [(n, (el.get("_loop"), "stop")) for _, n in pairs(el.get("inputs")) if n]
        if el.tag == "CaseFrame":
            return ([(el.get("selectout"), (uid, "selectout"))] if el.get("selectout") else []), []
        if el.tag == "Structure" and el.get("_name") == "Case Structure":
            return [], ([(el.get("selectin"), (uid, "sel"))] if el.get("selectin") else [])
        if el.tag == "Structure":
            srcs = [(el.get("count"), (uid, "count"))] if el.get("count") else []
            if el.get("maxout"):
                srcs.append((el.get("maxout"), (uid, "maxout")))
            sinks = [(el.get("maxin"), (uid, "N"))] if el.get("maxin") else []
            return srcs, sinks
        return [], []

    def wire(self, elements):
        nets = {}
        for el in elements:
            srcs, sinks = self.endpoints(el)
            for net, key in srcs:
                nets.setdefault(net, {"src": None, "sinks": []})["src"] = (el, key)
            for net, key in sinks:
                nets.setdefault(net, {"src": None, "sinks": []})["sinks"].append((el, key))
        for net, ends in nets.items():
            if ends["src"] is None:
                raise Unsupported("net %r has no source" % net)
            if not ends["sinks"]:
                continue
            resolved = [self.ends[key] for _, key in [ends["src"]] + ends["sinks"]]
            diags = {id(d) for _, d in resolved}
            if len(diags) != 1:
                raise Unsupported("net %r crosses a diagram boundary without a tunnel" % net)
            diag = resolved[0][1]
            uids = [u for u, _ in resolved]
            sig = ET.SubElement(diag.signals, "SL__arrayElement", {"class": "signal", "uid": str(self.alloc())})
            tl = ET.SubElement(sig, "termList", {"elements": str(len(uids))})
            for u in uids:
                ET.SubElement(tl, "SL__arrayElement", {"uid": u})
            ET.SubElement(sig, "state").text = "1"
            # The table's SHAPE must match the end count - LabVIEW repairs foreign geometry
            # but a two-ended table on a fan-out is a broken VI, and no table crashes LabVIEW.
            table = self.wiretables.get(str(len(uids)))
            if table is None:
                raise Unsupported("net %r fans out to %d ends; no wire table of that shape harvested"
                                  % (net, len(uids)))
            ET.SubElement(sig, "compressedWireTable").text = table
            kind = self.net_type.get(net)
            code = TYPE_CODE.get(kind.get("Type")) if kind is not None else None
            ET.SubElement(sig, "lastSignalKind").text = str(0x100 | code) if code else "266"
        return nets

    def layout(self, nets):
        """Left to right by dependency depth, inner diagrams first so a loop can be sized
        around what it holds. Geometry is not load-bearing (FINDINGS 3.7) - this is for
        whoever opens the VI."""
        src_of = {}
        for net, ends in nets.items():
            for sink, _ in ends["sinks"]:
                src_of.setdefault(sink.get("uid"), []).append(ends["src"][0].get("uid"))
        level = {}

        def lv(uid, stack=()):
            if uid in level:
                return level[uid]
            if uid in stack:
                return 0
            level[uid] = 1 + max([lv(s, stack + (uid,)) for s in src_of.get(uid, [])] or [-1])
            return level[uid]
        sizes = {}
        for loop, inner, parent in reversed(self.loops):
            w, h = self._place(inner, lv, sizes, 30, 40)
            side = {0: 0, 1: 0}
            for c in loop.find("termList"):
                side[int(c.find("dco").findtext("objFlags") or 0) & 1] += 1
            border = 20 + 24 * max(side.values()) + 40     # room for the stacked terminals
            sizes[loop.get("uid")] = (max(h + 40, border, 120), max(w + 40, 200))
        for case, frame_diags, parent in reversed(getattr(self, "cases", [])):
            dims = [self._place(fd, lv, sizes, 30, 30) for fd in frame_diags]
            w = max(x for x, _ in dims)
            h = max(y for _, y in dims)
            n = max(1, len(case.find("tunnelList")))
            # room for every tunnel slot plus the selector terminal below them
            sizes[case.get("uid")] = (max(h + 40, 30 + 24 * (n + 1) + 30, 120), max(w + 40, 200))
        self._place(self.root, lv, sizes, 40, 40)
        for case, frame_diags, parent in getattr(self, "cases", []):
            t, l, b, r = lvheap.parse_bounds(case.find("bounds").text)
            cr = case.find("contRect")
            if cr is not None:
                cr.text = lvheap.fmt_bounds((t + 6, l + 6, b - 6, r - 6))
            for fd in frame_diags:
                sb = fd.srn.find("bounds")
                if sb is not None:
                    sb.text = lvheap.fmt_bounds((-t, -l, -t, -l))
            slot = {0: 0, 1: 0}
            for c in case.find("termList"):
                dco = c.find("dco")
                tb = dco.find("termBounds")
                if tb is None or dco.get("class") == "caseSel":
                    continue
                out = int(dco.findtext("objFlags") or 0) & 1
                tt, tl, tbb, tr = lvheap.parse_bounds(tb.text)
                y = 30 + 24 * slot[out]
                slot[out] += 1
                x = (r - l) - (tr - tl) if out else 0
                tb.text = lvheap.fmt_bounds((y, x, y + (tbb - tt), x + (tr - tl)))
            # The selector terminal keeps the donor's coordinates otherwise - y 230 in a donor
            # 273 px tall - and outside a smaller case LabVIEW logs it insane ({graphics} 0x80).
            # It goes on the left edge below the input tunnels.
            sel = next(c for c in case.find("termList") if c.find("dco").get("class") == "caseSel")
            stb = sel.find("dco/termBounds")
            if stb is not None:
                tt, tl, tbb, tr = lvheap.parse_bounds(stb.text)
                y = 30 + 24 * slot[0] + 6
                stb.text = lvheap.fmt_bounds((y, 0, y + (tbb - tt), tr - tl))
        for loop, inner, parent in self.loops:
            t, l, b, r = lvheap.parse_bounds(loop.find("bounds").text)
            cr = loop.find("contRect")
            if cr is not None:
                cr.text = lvheap.fmt_bounds((t + 6, l + 6, b - 6, r - 6))
            sb = inner.srn.find("bounds")
            if sb is not None:
                sb.text = lvheap.fmt_bounds((-t, -l, -t, -l))
            # Border terminals must not OVERLAP: measured, a shift register placed on the
            # donor's y of the counter `i` gave a broken VI exactly when `i` was wired (t26),
            # while either alone worked. So `i` and the stop terminal go to the bottom corners
            # and every border terminal gets its own slot - inputs down the left edge, outputs
            # down the right.
            h, w = b - t, r - l
            for dco, x in ((loop.find("loopIndexDCO"), 11), (loop.find("loopTestDCO"), w - 27)):
                if dco is None:
                    continue
                tb = dco.find("termBounds")
                if tb is not None:
                    tb.text = lvheap.fmt_bounds((h - 26, x, h - 10, x + 16))
            slot = {0: 1 if loop.find("loopLimitDCO") is not None else 0, 1: 0}
            for c in loop.find("termList"):
                tb = c.find("dco/termBounds")
                if tb is None or c.find("dco").get("class") == "lMax":
                    continue
                out = int(c.find("dco").findtext("objFlags") or 0) & 1
                tt, tl, tbb, tr = lvheap.parse_bounds(tb.text)
                y = 20 + 24 * slot[out]
                slot[out] += 1
                x = w - (tr - tl) if out else 0
                tb.text = lvheap.fmt_bounds((y, x, y + (tbb - tt), x + (tr - tl)))

    def _place(self, diag, lv, sizes, top0, left0):
        rows, right, bottom = {}, 0, 0
        for obj, el in diag.placed:
            L = lv(el.get("uid"))
            y = top0 + rows.get(L, 0)
            x = left0 + 160 * L
            if el.tag == "Structure":
                h, w = sizes.get(obj.get("uid"), (120, 200))
                obj.find("bounds").text = lvheap.fmt_bounds((y, x, y + h, x + w))
            else:
                lvheap.move_to(obj, y, x)
                h, w = lvheap.size_of(obj)
            rows[L] = rows.get(L, 0) + max(h, 32) + 30
            right, bottom = max(right, x + w), max(bottom, y + h)
        return right, bottom

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
    def flatten(self, parent_el):
        """Every element below parent_el, structures' children included. A Condition is
        tagged with its loop's uid; a Left/Right inside a ShiftReg is refused for now."""
        out = []
        for el in parent_el:
            if not isinstance(el.tag, str):
                continue
            if el.tag in ("Structure", "CaseFrame"):
                out.append(el)
                for c in self.flatten(el):
                    if c.tag == "Condition":
                        c.set("_loop", el.get("uid"))
                    out.append(c)
            elif el.tag == "ShiftReg":
                out.append(el)
                for side in el:
                    if isinstance(side.tag, str) and side.tag in ("Left", "Right"):
                        side.set("_sr", el.get("uid"))
                        out.append(side)
            else:
                out.append(el)
        return out

    def compose(self, aixml_path):
        vi = ET.parse(aixml_path).getroot()
        self.vi_name = unescape(vi.get("_name") or "Composed.vi")
        self.pane_terms = []
        elements = []
        for el in self.flatten(vi):
            if el.tag in ("Control", "Indicator", "Constant", "Node", "Structure", "Tunnel", "Condition",
                          "ShiftReg", "Left", "Right", "CaseFrame", "Call"):
                elements.append(el)
            elif el.tag in ("FreeLabel",):
                self.report.append("skipped %s (comments are not composed yet)" % el.tag)
            else:
                raise Unsupported("element <%s> is not composable yet" % el.tag)
        by_uid = {e.get("uid"): e for e in elements}
        # structures first, outermost first, so every diagram exists before anything lands in it
        for el in [e for e in elements if e.tag == "Structure"]:
            if el.get("_name") == "Case Structure":
                self.add_case(el)
            else:
                self.add_loop(el)
        # a case tunnel is written as one outside half on the structure and one inside half
        # per frame, sharing an _id; it is scheduled as one item, by its outside half
        case_halves = {}
        for e in elements:
            if e.tag == "Tunnel":
                p = by_uid.get(e.get("uid_parent"))
                if p is not None and p.tag == "CaseFrame":
                    case_halves.setdefault((p.get("uid_parent"), e.get("_id")), {})[p.get("uid")] = e
        row = {"Control": 0, "Indicator": 0}
        for el in [e for e in elements if e.tag in ("Control", "Indicator")]:
            self.add_fp(el, row[el.tag])
            row[el.tag] += 1
        for el in [e for e in elements if e.tag == "Constant"]:
            self.add_const(el)
        # nodes and tunnels in dependency order, so every input type is known when it is placed
        def is_case(uid):
            p = by_uid.get(uid)
            return p is not None and p.tag == "Structure" and p.get("_name") == "Case Structure"
        pending = [e for e in elements if e.tag in ("Node", "ShiftReg", "Call")
                   or (e.tag == "Tunnel" and by_uid.get(e.get("uid_parent")) is not None
                       and by_uid[e.get("uid_parent")].tag == "Structure")]

        def inputs_of(e):
            if e.tag == "ShiftReg":   # a register's type comes from its initializer
                left = next(c for c in e if isinstance(c.tag, str) and c.tag == "Left")
                return pairs(left.get("inputs"))
            if e.tag == "Tunnel" and is_case(e.get("uid_parent")) and e.get("_id", "").startswith("Out"):
                halves = case_halves.get((e.get("uid_parent"), e.get("_id")), {}).values()
                ins = [x for h in halves for x in pairs(h.get("inputs")) if x[1]]
                return ins[:1] if any(n in self.net_type for _, n in ins) else ins
            return pairs(e.get("inputs"))
        while pending:
            ready = [e for e in pending if all(net in self.net_type or net is None
                                               for _, net in inputs_of(e))]
            if not ready:
                ready = pending[:1]
            for e in ready:
                if e.tag == "Node":
                    self.add_node(e)
                elif e.tag == "Call":
                    self.add_call(e)
                elif e.tag == "Tunnel" and is_case(e.get("uid_parent")):
                    self.add_case_tunnel(e, by_uid[e.get("uid_parent")],
                                         case_halves.get((e.get("uid_parent"), e.get("_id")), {}))
                elif e.tag == "Tunnel":
                    self.add_tunnel(e, by_uid[e.get("uid_parent")])
                else:
                    self.add_shiftreg(e, by_uid[e.get("uid_parent")])
                pending.remove(e)
        nets = self.wire(elements)
        self.layout(nets)
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
        grown = [self.ddo_list, self.pane_z]
        for d in self.diagrams.values():
            grown += list(d.lists())
        for loop, _, _ in self.loops:
            grown += [loop.find("termList"), loop.find("tunnelList"), loop.find("srDCOList")]
        for case, _, _ in getattr(self, "cases", []):
            grown += [case.find("termList"), case.find("tunnelList")]
        for lst in grown:
            lst.set("elements", str(len([c for c in lst if c.tag == "SL__arrayElement"])))
        self.cons.set("elements", str(self.pane_codes.get(self.con_id, (0x108, 16))[1]))
        # a ConpaneConnection without an index attribute means 'previous + 1' - so the list
        # must be in slot order (out of order: Connector Pane insane {index})
        conns = sorted(list(self.cons), key=lambda c: int(c.get("index", "0")))
        for c in list(self.cons):
            self.cons.remove(c)
        for c in conns:
            self.cons.append(c)
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
        # The heap slice is fixed BEFORE the link types are added: a callee's pane type is a
        # consolidated type of the VI, not a heap type - LabVIEW's own callers keep it outside
        # the DTHP slice (consolidated index 1 in an AIXML-generated caller).
        heap_count = len(self.types.toplevel) - (INDEX_SHIFT - 1)
        self.write_links()
        m.find("CONP/Section/TypeDesc").set("TypeID", "1")
        m.find("CPC2/Section/TypeDesc").set("TypeID", "2")
        vctp = m.find("VCTP")
        vctp.remove(vctp.find("Section"))
        vctp.append(self.types.vctp_section())
        sl = m.find("DTHP/Section/TypeDescSlice")
        sl.set("IndexShift", str(INDEX_SHIFT))
        sl.set("Count", str(heap_count))
        m.find("MUID/Section").set("Value", str(max(self.used_uids)))
        # NO COMPILED CODE, EVER - the user's rule of 2026-10-09: a composed VI is source-only,
        # so it carries less data and LabVIEW compiles it in memory on load. Asserted rather
        # than assumed, because a part or skeleton cut from a compiled VI could bring it in.
        compiled = [b for b in ("VICD", "TM80", "DFDS", "CCST", "LIds", "BNID", "NUID", "SUID",
                                "GCDI", "CNST") if m.find(b) is not None]
        if compiled or m.find("LVSR/Section/Execution2").get("SourceOnly") != "1":
            raise RuntimeError("composed VI would carry compiled code: %s" % (compiled or "SourceOnly != 1"))
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
    c.search_dirs = [os.path.dirname(os.path.abspath(out_vi))]
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
