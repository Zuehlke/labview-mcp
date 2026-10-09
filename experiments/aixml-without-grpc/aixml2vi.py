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
        # the diagram terminal goes where the AIXML puts the control: a control read INSIDE a
        # loop has its terminal in the loop's diagram, and so does its wire
        term, _, _ = self.instantiate("fpterm/%s" % want)
        term.find("dco").set("uid", fpdco.get("uid"))
        diag.z.append(ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
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
        diag.z.append(ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
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
        diag.z.append(ET.Element("SL__arrayElement", {"uid": prim.get("uid")}))
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
        kind = {"While Loop": "whileLoop"}.get(el.get("_name"))
        if kind is None:
            raise Unsupported("structure %r is not composable yet" % el.get("_name"))
        parent = self.diag_of(el)
        loop, donor_types, src = self.instantiate("struct/%s" % kind, own_uid=el.get("uid"))
        for i, e in enumerate(lvheap.typeid_elements(loop)):
            e.set("_t", str(i))     # so pruning below cannot misalign donor types
        inner = Diagram(loop.find("diagramList/SL__arrayElement[@class='diag']"), owner=el)
        lcnt, ltst = loop.find("loopIndexDCO"), loop.find("loopTestDCO")
        keep = {lcnt.find("termList")[0].get("uid"), ltst.find("termList")[0].get("uid")}
        inner_by_dco = {t.find("dco").get("uid"): t for t in inner.srn_terms
                        if t.find("dco") is not None}
        self.tunnel_tpl = getattr(self, "tunnel_tpl", {})
        for ot in loop.find("termList"):
            dco = ot.find("dco")
            if dco.get("class") != "lpTun" or dco.find("innerLpTunDCO") is not None \
                    or dco.find("TunnelType") is not None:
                continue
            d = "out" if int(dco.findtext("objFlags") or 0) & 1 else "in"
            it = inner_by_dco.get(dco.get("uid"))
            if d not in self.tunnel_tpl and it is not None:
                self.tunnel_tpl[d] = (copy.deepcopy(ot), copy.deepcopy(it))
        for lst in (loop.find("termList"), loop.find("tunnelList"), loop.find("srDCOList"),
                    inner.signals):
            for c in list(lst):
                lst.remove(c)
        for c in list(inner.srn_terms):
            if c.get("uid") not in keep:
                inner.srn_terms.remove(c)
        for c in list(inner.nodes):
            if c.get("class") != "sRN":
                inner.nodes.remove(c)
        for c in list(inner.z):
            if c.get("uid") not in keep:
                inner.z.remove(c)
        # the counter and the stop terminal keep their donor types (int32, boolean)
        for e in lvheap.typeid_elements(loop):
            tree = plain(lvheap.strip_label(donor_types[int(e.get("_t"))]))
            e.text = "TypeID(%d)" % self.heap_tid(tree)
            del e.attrib["_t"]
        parent.nodes.append(loop)
        parent.z.append(ET.Element("SL__arrayElement", {"uid": loop.get("uid")}))
        parent.placed.append((loop, el))
        self.diagrams[el.get("uid")] = inner
        self.loops.append((loop, inner, parent))
        self.ends[(el.get("uid"), "count")] = (lcnt.find("termList")[0].get("uid"), inner)
        self.ends[(el.get("uid"), "stop")] = (ltst.find("termList")[0].get("uid"), inner)
        count = el.get("count") or ""
        if count:
            self.net_type[count] = ET.Element("TypeDesc", {"Type": "NumInt32", "Prop1": "0", "Format": "inline"})
        self.report.append("Structure %r <- %s" % (el.get("_name"), src))

    def add_tunnel(self, el, loop_el):
        d = "in" if el.get("_id", "").startswith("In") else "out"
        if el.get("mode"):
            raise Unsupported("Tunnel %s: mode=%r (indexing) is not composable yet" % (el.get("uid"), el.get("mode")))
        tpl = getattr(self, "tunnel_tpl", {}).get(d)
        if tpl is None:
            raise Unsupported("no %s-tunnel template harvested" % d)
        loop, inner, parent = next(x for x in self.loops if x[1].owner is loop_el)
        wrap = ET.Element("wrap")
        wrap.append(copy.deepcopy(tpl[0]))
        wrap.append(copy.deepcopy(tpl[1]))
        lvheap.renumber(wrap, self.alloc)
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
        if el.tag == "Node":
            return ([(n, (uid, t)) for t, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, t)) for t, n in pairs(el.get("inputs")) if n])
        if el.tag in ("Tunnel", "Left", "Right"):
            return ([(n, (uid, "out")) for _, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, "in")) for _, n in pairs(el.get("inputs")) if n])
        if el.tag == "Condition":
            return [], [(n, (el.get("_loop"), "stop")) for _, n in pairs(el.get("inputs")) if n]
        if el.tag == "Structure":
            return ([(el.get("count"), (uid, "count"))] if el.get("count") else []), []
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
        self._place(self.root, lv, sizes, 40, 40)
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
                tb = dco.find("termBounds")
                if tb is not None:
                    tb.text = lvheap.fmt_bounds((h - 26, x, h - 10, x + 16))
            slot = {0: 0, 1: 0}
            for c in loop.find("termList"):
                tb = c.find("dco/termBounds")
                if tb is None:
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
            if el.tag == "Structure":
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
                          "ShiftReg", "Left", "Right"):
                elements.append(el)
            elif el.tag in ("FreeLabel",):
                self.report.append("skipped %s (comments are not composed yet)" % el.tag)
            else:
                raise Unsupported("element <%s> is not composable yet" % el.tag)
        by_uid = {e.get("uid"): e for e in elements}
        # structures first, outermost first, so every diagram exists before anything lands in it
        for el in [e for e in elements if e.tag == "Structure"]:
            self.add_loop(el)
        row = {"Control": 0, "Indicator": 0}
        for el in [e for e in elements if e.tag in ("Control", "Indicator")]:
            self.add_fp(el, row[el.tag])
            row[el.tag] += 1
        for el in [e for e in elements if e.tag == "Constant"]:
            self.add_const(el)
        # nodes and tunnels in dependency order, so every input type is known when it is placed
        pending = [e for e in elements if e.tag in ("Node", "Tunnel", "ShiftReg")]

        def inputs_of(e):
            if e.tag == "ShiftReg":   # a register's type comes from its initializer
                left = next(c for c in e if isinstance(c.tag, str) and c.tag == "Left")
                return pairs(left.get("inputs"))
            return pairs(e.get("inputs"))
        while pending:
            ready = [e for e in pending if all(net in self.net_type or net is None
                                               for _, net in inputs_of(e))]
            if not ready:
                ready = pending[:1]
            for e in ready:
                if e.tag == "Node":
                    self.add_node(e)
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
        for lst in grown:
            lst.set("elements", str(len([c for c in lst if c.tag == "SL__arrayElement"])))
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
