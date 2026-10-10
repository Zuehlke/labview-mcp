# -*- coding: utf-8 -*-
"""VI -> AIXML with NO LabVIEW and NO NI gRPC service: read the heap through pylabview.

    vi2aixml.py <in.vi | bundle main.xml> [out.xml]

The reverse of aixml2vi.py, and the same knowledge: primitive names from primitive-names.tsv,
terminal names from terminal-names.tsv, types from VCTP, wiring from the heap's `signal`s.
uids are the heap's own, which are also the uids LabVIEW's own AIXML export uses for the same
objects (FINDINGS.md section 3.6) - so an export from here can be diffed against NI's.

What it reads: Control, Indicator (name, type, default value, conIdx, connection),
Constant (name, type, value), Node for every primitive the name tables know, and the nets
between them. What it does not read yet is written as an XML comment naming the heap class,
so nothing is dropped in silence.
"""
import os
import struct
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # the bundle's ._pth leaves it off
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402

TYPE_TO_AIXML = {v[0]: k for k, v in aixml2vi.SCALARS.items()}
TYPE_TO_AIXML["MeasureData.TimeStamp"] = "timestamp"
INT_FMT = aixml2vi.INT_FMT
SINK = 0x8000          # objFlags bit on a terminal that RECEIVES a wire (measured, see FINDINGS)


def escape(s):
    """AIXML's own escaping for names inside inputs=/outputs=: , and : and \\ as \\XX."""
    return "".join("\\%02X" % ord(c) if c in ",:\\" else c for c in s)


_REF_BY_FLAGS = None


def ref_class_name(flags):
    global _REF_BY_FLAGS
    if _REF_BY_FLAGS is None:
        _REF_BY_FLAGS = {}
        tsv = os.path.join(os.path.dirname(os.path.abspath(__file__)), "parts", "refnum-classes.tsv")
        for line in open(tsv, encoding="utf-8"):
            row = line.rstrip("\r\n").split("\t")
            if line.startswith("#") or row[0] == "class" or len(row) < 2:
                continue
            _REF_BY_FLAGS.setdefault(row[1].upper(), row[0])
    return _REF_BY_FLAGS.get((flags or "").upper())


def _sub(tree):
    """A nested type as NI writes it inside a compound one: the type, then .label if labelled."""
    t = aixml_type(tree)
    if t is None:
        return None
    lab = tree.get("Label")
    return t + ("." + lab.replace(",", "\\2C") if lab else "")


def aixml_type(tree):
    """The AIXML type literal, as NI's export writes it: scalars, enums uint16{a,b}, a ring the
    bare integer, array{T.label} / array.2{T}, cluster{T.a,T.b}, timestamp, doublewaveform,
    variant, ref{LV.<Class>} (a strictly typed control refnum adds {T}), ref{UDClassInst},
    ref{Visa.Instr}, ref{DataLog}{T}, ref{Notifier}{T}, ref{Queue}{T}. None when unknown."""
    if tree is None:
        return None
    t = tree.get("Type")
    kids = [c for c in tree if c.tag == "TypeDesc"]
    if t == "TypeDef":
        # NI's export FLATTENS a typedef to the type it wraps - the .ctl identity is lost
        # there (CLAUDE.md, "a placeholder loses every typedef"), so it is lost here too.
        return aixml_type(kids[0]) if kids else None
    if t == "Array":
        dims = len(tree.findall("Dimension"))
        inner = _sub(kids[0]) if kids else None
        return None if inner is None else "array%s{%s}" % ("" if dims <= 1 else ".%d" % dims, inner)
    if t == "Cluster":
        parts = [_sub(k) for k in kids]
        return None if any(x is None for x in parts) else "cluster{%s}" % ",".join(parts)
    if t == "MeasureData":
        return {"TimeStamp": "timestamp", "Float64Waveform": "doublewaveform",
                "Dynamicdata": "dynamicdata"}.get(tree.get("Flavor"))
    if t == "Picture":
        return "picture"
    if t == "Tag":
        n = {v: k for k, v in aixml2vi.TAG_TYPES.items()}.get(tree.get("TagType"))
        return "tag{%s}" % n if n else None
    if t == "Refnum":
        rt = tree.get("RefType")
        if rt == "LVObjCtl":
            cls = ref_class_name(tree.get("CtlFlags"))
            if cls is None:
                return None
            if kids:
                inner = aixml_type(kids[0])
                return "ref{%s}{%s}" % (cls, inner) if inner else "ref{%s}" % cls
            return "ref{%s}" % cls
        if rt == "UDClassInst":
            return "ref{UDClassInst}"
        if rt == "VisaRef":
            return "ref{Visa.%s}" % (tree.get("Ident") or "Instr")
        if rt in ("DataLog", "Notifier", "Queue", "UserEvent", "EventReg", "Occurrence"):
            inner = _sub(kids[0]) if kids else None
            return "ref{%s}" % rt + ("{%s}" % inner if inner else "")
        if rt == "UsrDefndTag":
            return "ref{GenClassTag.%s.%s}" % (tree.get("Ident") or "", tree.get("TypeName") or "")
        return None
    k = lvheap.type_key(tree)
    if k and k.startswith("Unit"):
        base = TYPE_TO_AIXML.get("Num" + k[4:])
        if base is None or tree.find("PhysUnit") is not None:
            return None
        labels = [e.text or "" for e in tree.findall("EnumLabel")]
        if labels:
            return "%s{%s}" % (base, ",".join(escape_value(l) for l in labels))
        return base
    return TYPE_TO_AIXML.get(k)


def escape_value(v):
    """NI's escaping inside value= and type literals, counted over 1 584 cached exports:
    control characters, ',' ']' and the backslash become a backslash plus two hex digits;
    ':' and ';' stay raw."""
    return "".join("\\%02X" % ord(c) if ord(c) < 0x20 or c in ",]\\" else c for c in v)


def fmt_float(v):
    if v != v:
        return "NaN"
    if v in (float("inf"), float("-inf")):
        return "Inf" if v > 0 else "-Inf"
    return "%.17g" % v


def decode_value(tree, hexstr):
    try:
        return _decode_value(tree, hexstr)
    except (struct.error, IndexError, ValueError):
        return None   # an encoding this reader does not know; the caller notes it


def _unquad(b):
    """IEEE binary128, big-endian -> float (the inverse of the composer's _quad)."""
    if len(b) < 16 or not any(b):
        return 0.0
    hi = int.from_bytes(b[:2], "big")
    sign, exp = hi >> 15, hi & 0x7FFF
    frac = int.from_bytes(b[2:16], "big")
    if exp == 0x7FFF:
        return float("-inf") if sign else float("inf")
    v = (1 + frac / float(1 << 112)) * 2.0 ** (exp - 16383)
    return -v if sign else v


def _fmt_complex(re_, im):
    if im == 0:
        return fmt_float(re_)
    return "%s%s%si" % (fmt_float(re_), "+" if im >= 0 else "", fmt_float(im))


def _flat_decode(tree, b, pos):
    """One flattened value -> (AIXML text, next position): the inverse of the composer's
    _flat - an array is an int32 size per dimension then its elements, a cluster its fields one
    after the other; written as NI writes compound values: [a,b], nested, booleans lower case."""
    t = tree.get("Type")
    kids = [c for c in tree if c.tag == "TypeDesc"]
    if t == "TypeDef":
        return _flat_decode(kids[0], b, pos)
    if t == "Array":
        dims = len(tree.findall("Dimension")) or 1
        sizes = list(struct.unpack(">%di" % dims, b[pos:pos + 4 * dims]))
        pos += 4 * dims
        total = 1
        for n in sizes:
            total *= n
        flat = []
        for _ in range(total):
            v, pos = _flat_decode(kids[0], b, pos)
            flat.append(v)

        def nest(items, ds):
            if len(ds) == 1:
                return "[%s]" % ",".join(items)
            step = len(items) // ds[0] if ds[0] else 0
            return "[%s]" % ",".join(nest(items[i * step:(i + 1) * step], ds[1:]) for i in range(ds[0]))
        return ("[]" if total == 0 else nest(flat, sizes)), pos
    if t == "Cluster":
        parts = []
        for k in kids:
            v, pos = _flat_decode(k, b, pos)
            parts.append(v)
        return "[%s]" % ",".join(parts), pos
    if t and t.startswith("Unit"):
        t = "Num" + t[4:]
    if t == "Boolean":
        return ("true" if b[pos] else "false"), pos + 1
    if t == "NumFloat64":
        return fmt_float(struct.unpack(">d", b[pos:pos + 8])[0]), pos + 8
    if t == "NumFloat32":
        return fmt_float(struct.unpack(">f", b[pos:pos + 4])[0]), pos + 4
    if t == "NumFloatExt":
        return fmt_float(_unquad(b[pos:pos + 16])), pos + 16
    if t in ("NumComplex128", "NumComplex64"):
        n = 8 if t == "NumComplex128" else 4
        f = ">d" if n == 8 else ">f"
        re_, im = struct.unpack(f, b[pos:pos + n])[0], struct.unpack(f, b[pos + n:pos + 2 * n])[0]
        return _fmt_complex(re_, im), pos + 2 * n
    if t in INT_FMT:
        n = struct.calcsize(INT_FMT[t])
        return str(struct.unpack(INT_FMT[t], b[pos:pos + n])[0]), pos + n
    if t == "String":
        n = struct.unpack(">I", b[pos:pos + 4])[0]
        return escape_value(b[pos + 4:pos + 4 + n].decode("mac_roman")), pos + 4 + n
    if t == "Path":
        n = struct.unpack(">I", b[pos + 4:pos + 8])[0]
        return _decode_value(tree, b[pos:pos + 8 + n].hex()), pos + 8 + n
    raise ValueError("no flattened form known for %s" % t)


def _decode_value(tree, hexstr):
    if tree.get("Type") == "TypeDef" and tree.find("TypeDesc") is not None:
        return _decode_value(tree.find("TypeDesc"), hexstr)
    if tree.get("Type") in ("Array", "Cluster", "NumFloatExt", "NumComplex128", "NumComplex64"):
        return _flat_decode(tree, bytes.fromhex(hexstr) if hexstr else b"", 0)[0]
    t = tree.get("Type")
    if t and t.startswith("Unit"):
        t = "Num" + t[4:]   # a ring or enum holds its base number
    b = bytes.fromhex(hexstr) if hexstr else b""
    size = {"NumFloat64": 8, "NumFloat32": 4}.get(t) or (struct.calcsize(INT_FMT[t]) if t in INT_FMT else None)
    if size and len(b) > size:
        b = b[:size]
    if t == "NumFloat64":
        # LabVIEW's own export writes 17 significant digits: 0.2 is "0.20000000000000001"
        return fmt_float(struct.unpack(">d", b)[0]) if b else "0"
    if t == "NumFloat32":
        return fmt_float(struct.unpack(">f", b)[0]) if b else "0"
    if t in INT_FMT:
        return str(struct.unpack(INT_FMT[t], b)[0]) if b else "0"
    if t == "Boolean":
        return "true" if b and b[0] else "false"
    if t in ("String", "Tag"):
        n = struct.unpack(">I", b[:4])[0] if b else 0
        return escape_value(b[4:4 + n].decode("mac_roman"))
    if t == "Path":
        if len(b) < 12:
            return ""
        kind, count = struct.unpack(">HH", b[8:12])
        comps, i = [], 12
        for _ in range(count):
            n = b[i]
            comps.append(b[i + 1:i + 1 + n].decode("mac_roman"))
            i += 1 + n
        if kind == 0 and comps:
            return escape_value(comps[0] + ":\\" + "\\".join(comps[1:]))
        return escape_value("\\".join(comps))
    return None


def label_text(ddo):
    parts = ddo.find("partsList")
    for part in (parts if parts is not None else []):
        if part.get("class") == "label" and part.findtext("partID") == "16":
            t = part.findtext("textRec/text")
            if t and t.startswith('"') and t.endswith('"'):
                return t[1:-1].replace('\\"', '"').replace("\\\\", "\\")
            # pylabview writes an unquoted [NULL] for a label with no text at all
            return "" if t in (None, "[NULL]") else t
    return ""


INT_MIN, INT_MAX = -2 ** 31, 2 ** 31 - 1


def unquote(t):
    if t and t.startswith('"') and t.endswith('"'):
        return t[1:-1].replace('\\"', '"').replace("\\\\", "\\")
    return t or ""


def esc_attr_items(items):
    """Case selector items as NI writes them: joined by ', ' with the comma escaped."""
    return "\\2C ".join(items)


# heap class of a fixed-shape node -> its AIXML name; the terminal names come from the
# composer's template parts (parts/tnode__*.xml), which name every terminal (harvest_templates.py)
TEMPLATE_CLASSES = {"aDelete": "Delete From Array", "aInit": "Initialize Array", "aInsert": "Insert Into Array",
                    "aReplace": "Replace Array Subset", "aReshape": "Reshape Array",
                    "cABuild": "Index & Bundle Cluster Array", "concat": "Concatenate Strings",
                    "cpdArith": "Compound Arithmetic", "interLeave": "Interleave 1D Arrays",
                    "mergeErrors": "Merge Errors", "printf": "Format Into String", "scanf": "Scan From String",
                    "subset": "Array Subset"}
NODE_READERS = {"nMux": "nmux", "mux": "mux", "demux": "demux", "aIndx": "aindx", "aBuild": "abuild",
                "propNode": "propnode", "invokeNode": "invokenode", "eventDataNode": "eventdata",
                "statVIRef": "statvi", "ctlRefConst": "ctlref", "gRef": "gref", "hiddenFBNode": "feedback",
                "exprNode": "expr"}
CA_OPS = {0: "Add", 1: "Multiply", 2: "AND", 3: "OR", 4: "XOR"}
# a bDFixed constant's `cnst` code -> its AIXML FixedConst name (the composer's own table, reversed)
FIXED_BY_CODE = {v.upper(): k for k, v in aixml2vi.Composer.FIXED.items()}
WAVEFORM_FIELDS = ["t0", "dt", "Y", "error", "attributes"]
_TEMPLATES = None


def template_names(cls):
    """Every template of a node class as its list of terminal names, by term position."""
    global _TEMPLATES
    if _TEMPLATES is None:
        import glob
        _TEMPLATES = {}
        for f in glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)), "parts", "tnode__*.xml")):
            try:
                node = ET.parse(f).getroot().find("heap")[0]
            except Exception:
                continue
            _TEMPLATES.setdefault(node.get("class"), []).append([t.get("aixml") or "?" for t in node.find("termList")])
    return _TEMPLATES.get(cls, [])


def names_for(cls, n):
    """Terminal names for a node of class `cls` with `n` terminals: a template of exactly that
    size, else the longest one extended - a trailing `input N` counts on, anything else repeats."""
    import re as _re
    ts = template_names(cls)
    exact = [t for t in ts if len(t) == n]
    if exact:
        return exact[0]
    if not ts:
        return None
    t = list(max(ts, key=len))
    while len(t) < n:
        m = _re.fullmatch(r"(.*?)(\d+)", t[-1])
        t.append("%s%d" % (m.group(1), int(m.group(2)) + 1) if m else t[-1])
    return t[:n]


class _Owners(dict):
    """term uid -> (element, terminal name, is sink), remembering the order terminals were
    registered in - which is their termList order, the order a node's repeated terminal names
    (element, value, string) are matched in by the composer."""
    def __init__(self):
        super().__init__()
        self.order = []

    def __setitem__(self, k, v):
        super().__setitem__(k, v)
        self.order.append(k)


class Reader:
    """Heap -> AIXML. uids follow LabVIEW's own export (uid_survey.py over 200 exports): a
    control/constant is its ddo, a node its own uid, a Structure the structure node, a CaseFrame
    the frame's diag, a Tunnel the OUTER terminal (a case frame's half: the frame's inner
    terminal), a Condition the lTst DCO, a ShiftReg the rSR DCO, its Left the lSR DCO and its
    Right the rSR's inner terminal, a Call the iUse node."""

    def __init__(self, main_xml):
        self.b = lvheap.Bundle(main_xml)
        _, self.term_names = aixml2vi.load_names()
        self.prim_name = {}
        import csv, html
        with open(os.path.join(aixml2vi.TABLES, "primitive-names.tsv"), encoding="utf-8") as f:
            for row in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t"):
                if row and row[0] != "primResID":
                    self.prim_name[row[0]] = html.unescape(row[1])
        extra = os.path.join(os.path.dirname(os.path.abspath(__file__)), "primitive-names-extra.tsv")
        if os.path.exists(extra):
            for row in aixml2vi._tsv(extra):
                self.prim_name.setdefault(row[0], aixml2vi.unescape(html.unescape(row[1])))
        self.out = ET.Element("VI")
        self.term_owner = _Owners()    # heap term uid -> (aixml element, terminal name, is_sink)
        self.notes = []
        self.call_targets = self._call_targets()
        self.diags = []         # every diagram walked, for wiring
        self.ordered_inputs = {}   # element -> its inputs in TERMINAL order, unwired ones listed
        self.ordered_ports = []    # (element, attr, [(term uid, name)]) rewritten after wiring
        self.term_net = {}

    # ---- helpers ---------------------------------------------------------------------------
    def tid_tree(self, el):
        e = lvheap.typeid_elements(el)
        if not e:
            return None
        return self.b.heap_type_tree(int(lvheap.TYPEID_RE.fullmatch(e[0].text.strip()).group(1)))

    def _call_targets(self):
        """node uid -> {link record tag: qualified name} from the diagram's link records: IUVI
        names the VI a call node runs, PUPV the polymorphic VI it was dropped as. A record whose
        qualified name is the bare VI name but whose VILSPathRef names a library gets the
        library in front, as NI's export writes it (LV AI Plugins.lvlib:...)."""
        out = {}
        bdhp = self.b.main.find("LIbd/Section/BDHP")
        for rec in (bdhp if bdhp is not None else []):
            qual = [x.text or "" for x in rec.findall("LinkSaveQualName/String")]
            if not qual:
                continue
            lib = [x.text or "" for x in rec.findall("VILSPathRef/String")]
            if len(qual) == 1 and lib and lib[-1].lower().endswith((".lvlib", ".lvclass", ".xctl", ".lvlibp")):
                qual = [lib[-1]] + qual
            name = ":".join(qual)
            for off in rec.findall("LinkOffsetList/Offset"):
                try:
                    out.setdefault(str(int(off.text, 16)), {})[rec.tag] = name
                except (TypeError, ValueError):
                    pass
        return out

    @staticmethod
    def node_defs(diag):
        """The nodes of a diagram, in nodeList order, each resolved to its DEFINITION (often in
        zPlaneList, referenced from nodeList by a bare uid)."""
        defs = {}
        for lst in ("zPlaneList", "nodeList"):
            for e in (diag.find(lst) if diag.find(lst) is not None else []):
                if e.get("class"):
                    defs[e.get("uid")] = e
        out = []
        for ref in (diag.find("nodeList") if diag.find("nodeList") is not None else []):
            n = ref if ref.get("class") else defs.get(ref.get("uid"))
            if n is not None:
                out.append(n)
        # a Flat Sequence is defined in the z list only - the nodeList names its FRAMES, which
        # are defined inside it - so it is picked up from the z list
        seen = {id(n) for n in out}
        for e in (diag.find("zPlaneList") if diag.find("zPlaneList") is not None else []):
            if e.get("class") == "flatSequence" and id(e) not in seen:
                out.append(e)
        return out

    # ---- the whole VI ------------------------------------------------------------------------
    def read(self):
        b = self.b
        self.out.set("_name", b.main.find("LVSR/Section").get("Name"))
        self.out.set("description", b.main.findtext("STRG/Section/String") or "")
        pane = self.pane_flags()
        self.fp_by_uid = {}
        for fpdco in b.fp.iter("SL__arrayElement"):
            if fpdco.get("class") != "fPDCO":
                continue
            ddo = fpdco.find("ddo")
            ind = int(fpdco.findtext("objFlags") or 0) & 1
            tree = self.tid_tree(fpdco)
            t = aixml_type(tree)
            el = ET.Element("Indicator" if ind else "Control", {"_name": label_text(ddo)})
            if t is None:
                el.set("type", "?" + (lvheap.type_key(tree) or ""))
                self.notes.append("front-panel %s %r: type %s not mapped" % (ddo.get("class"), label_text(ddo), lvheap.type_key(tree)))
            else:
                el.set("type", t)
            el.set("uid", ddo.get("uid"))
            el.set("uid_parent", "?")
            dd = fpdco.findtext("DefaultData")
            el.set("value", self.default_value(tree, dd) if t else "")
            con = fpdco.findtext("conNum")
            con = "0" if con is None else con
            if con != "-1" and con in pane:
                el.set("conIdx", con)
                el.set("connection", pane[con])
            self.fp_by_uid[fpdco.get("uid")] = el
        self.fp_placed = set()
        self.ctl_by_ddo = {el.get("uid"): el for el in self.fp_by_uid.values()}
        # a feedback node is two heap nodes; inside a loop its initializer is the LOOP's
        self.slaves = {e.get("uid"): e for e in b.bd.iter("SL__arrayElement") if e.get("class") == "slaveFBInputNode"}
        self.fb_inits = {}
        for e in b.bd.iter("SL__arrayElement"):
            d = e.find("dco")
            if e.get("class") == "term" and d is not None and d.get("class") == "initFeedback" and d.find("rsrDCO") is not None:
                self.fb_inits[d.find("rsrDCO").get("uid")] = e
        root = b.bd.find("root")
        self.walk(root, "root", self.out)
        for uid, el in self.fp_by_uid.items():
            if uid not in self.fp_placed:
                # its terminal sits in a construct this reader does not walk yet
                el.set("uid_parent", "root")
                self.out.insert(0, el)
                self.notes.append("%s %r: its terminal was not found on a walked diagram" % (el.tag, el.get("_name")))
        for d in self.diags:
            self.wire(d)
        # a node's inputs and outputs in TERMINAL order: signal order put Build Array's two
        # `element`s the wrong way round (a round trip of t66 built [2.5,7] for [7,2.5])
        per_el = {}
        seen = set()
        for tu in self.term_owner.order:
            if tu in seen:
                continue
            seen.add(tu)
            el, nm, sink = self.term_owner[tu]
            if el.tag in ("Node", "Call") and not nm.startswith("#"):
                per_el.setdefault(id(el), (el, []))[1].append((tu, nm, sink))
        for el, terms in per_el.values():
            ins = ["%s:%s" % (escape(nm), self.term_net[tu]) for tu, nm, sink in terms if sink and tu in self.term_net]
            outs = ["%s:%s" % (escape(nm), self.term_net[tu]) for tu, nm, sink in terms if not sink and tu in self.term_net]
            for attr, items in (("inputs", ins), ("outputs", outs)):
                if items:
                    el.set(attr, ",".join(items))
                elif attr in el.attrib:
                    del el.attrib[attr]
        for el, order in self.ordered_inputs.values():
            el.set("inputs", ",".join("%s:%s" % (escape(nm), self.term_net.get(u, "")) for u, nm in order))
        for el, attr, order in self.ordered_ports:
            if any(self.term_net.get(u) for u, _ in order):
                el.set(attr, ",".join("%s:%s" % (escape(nm), self.term_net.get(u, "")) for u, nm in order))
            elif attr in el.attrib:
                del el.attrib[attr]
        for n in self.notes:
            self.out.append(ET.Comment(" vi2aixml: " + n + " "))
        return self.out

    def walk(self, diag, parent_uid, container):
        """One diagram: its sRN terminals (front-panel terminals, constants), then every node;
        a structure recurses into its diagrams with the structure (or frame) as parent."""
        self.diags.append(diag)
        for node in self.node_defs(diag):
            cls = node.get("class")
            if cls == "sRN":
                for term in node.find("termList"):
                    tc = term.get("class")
                    d = term.find("dco")
                    if tc == "fPTerm":
                        el = self.fp_by_uid.get(d.get("uid"))
                        if el is not None:
                            if d.get("uid") not in self.fp_placed:
                                el.set("uid_parent", parent_uid)
                                container.append(el)
                                self.fp_placed.add(d.get("uid"))
                            self.term_owner[term.get("uid")] = (el, "value", el.tag == "Indicator")
                    elif tc == "term" and d is not None and d.get("class") == "bDConstDCO" and \
                            d.find("ddo") is not None and d.find("ddo").get("class") == "bDFixed":
                        code = d.find("ddo").findtext("cnst") or "00"
                        name = FIXED_BY_CODE.get(code.upper(), "?fixed " + code)
                        c = ET.Element("FixedConst", {"_name": name, "uid": d.find("ddo").get("uid"), "uid_parent": parent_uid})
                        self.term_owner[term.get("uid")] = (c, "value", False)
                        container.append(c)
                    elif tc == "term" and d is not None and d.get("class") == "bDConstDCO":
                        c = self.constant(term, parent_uid)
                        container.append(c)
                continue
            el = None
            if cls == "prim":
                el = self.prim(node, parent_uid)
            elif cls in ("whileLoop", "forLoop"):
                el = self.loop(node, parent_uid)
            elif cls == "select":
                el = self.case(node, parent_uid)
            elif cls == "eventStruct":
                el = self.case(node, parent_uid, event=True)
            elif cls == "flatSequence":
                el = self.flatseq(node, parent_uid)
            elif cls == "decomposeRecomposeStructure":
                el = self.inplace(node, parent_uid)
            elif cls == "decomposeMatchNode":
                continue        # read with its In Place Element Structure
            elif cls == "commentNode":
                el = self.disable(node, parent_uid)
            elif cls in ("iUse", "polyIUse"):
                el = self.call(node, parent_uid)
            elif cls in TEMPLATE_CLASSES:
                el = self.template_node(node, parent_uid)
            elif cls in NODE_READERS:
                el = getattr(self, NODE_READERS[cls])(node, parent_uid)
            elif cls in ("slaveFBInputNode",):
                continue        # read with its hiddenFBNode
            else:
                self.notes.append("diagram node of class %s (uid %s) not read" % (cls, node.get("uid")))
            if el is not None:
                container.append(el)

    # ---- elements ----------------------------------------------------------------------------
    def default_value(self, tree, dd):
        if dd is None:
            # NI's exports write nothing for a type with no literal: 261 refnums, 34 waveforms,
            # 30 dynamic data, 27 timestamps, 9 variants, 7 pictures in the cache, all value=""
            return {"Boolean": "false"}.get(tree.get("Type"), "" if tree.get("Type") in (
                "String", "Path", "Refnum", "Picture", "MeasureData", "LVVariant", "Tag") else "0")
        # pylabview renders DefaultData as MacRoman text with &#x..; escapes for unprintables
        raw = dd.strip('"')
        out = bytearray()
        i = 0
        while i < len(raw):
            if raw.startswith("&#x", i) and ";" in raw[i:i + 8]:
                j = raw.index(";", i)
                out.append(int(raw[i + 3:j], 16))
                i = j + 1
            else:
                out.extend(raw[i].encode("mac_roman"))
                i += 1
        return decode_value(tree, out.hex()) or ""

    def constant(self, term, parent_uid):
        dco = term.find("dco")
        ddo = dco.find("ddo")
        tree = self.tid_tree(term)
        t = aixml_type(tree)
        el = ET.Element("Constant")
        name = label_text(ddo)
        if name:
            el.set("_name", name)
        el.set("type", t or "?" + lvheap.type_key(tree))
        el.set("uid", ddo.get("uid"))
        el.set("uid_parent", parent_uid)
        v = decode_value(tree, dco.findtext("ConstValue") or "") if t else None
        el.set("value", v if v is not None else "")
        if v is None:
            self.notes.append("constant %s: value of type %s not decoded" % (ddo.get("uid"), lvheap.type_key(tree)))
        self.term_owner[term.get("uid")] = (el, "value", False)
        return el

    def prim(self, node, parent_uid):
        rid = node.findtext("primResID")
        self.prim_rid = getattr(self, "prim_rid", {})
        self.prim_rid[node.get("uid")] = rid
        name = self.prim_name.get(rid)
        el = ET.Element("Node", {"_name": name or "?primResID %s" % rid,
                                 "uid": node.get("uid"), "uid_parent": parent_uid})
        if name is None:
            self.notes.append("primitive primResID %s has no name in the table" % rid)
        for term in node.find("termList"):
            dco = term.find("dco")
            pidx = dco.findtext("parmIndex") or "out"
            tname = self.term_names.get((rid, pidx), "?parm%s" % pidx)
            sink = bool(int(term.findtext("objFlags") or 0) & SINK)
            self.term_owner[term.get("uid")] = (el, tname, sink)
        return el

    def call(self, node, parent_uid):
        """A subVI call: target from the diagram's link record naming this node, terminal names
        from the LABELS of the terminal types (a call node's terminals carry the callee's
        labelled pane types)."""
        recs = self.call_targets.get(node.get("uid"), {})
        el = ET.Element("Call", {"uid": node.get("uid"), "uid_parent": parent_uid})
        target = recs.get("PUPV") or recs.get("IUVI") or next(iter(recs.values()), None)
        if target is None:
            self.notes.append("call node %s: no link record names it" % node.get("uid"))
            target = "?"
        if recs.get("PUPV") and recs.get("IUVI"):
            # a polymorphic call: NI writes the polymorphic VI as target, the instance it
            # adapted to as instance, and adapt="true"
            el.set("adapt", "true")
            el.set("instance", recs["IUVI"])
        el.set("target", target)
        for term in node.find("termList"):
            d = term.find("dco")
            tree = self.tid_tree(term)
            if tree is None or tree.get("Type") == "Void":
                continue
            out = int(d.findtext("objFlags") or 0) & 1
            self.term_owner[term.get("uid")] = (el, tree.get("Label") or "?", not out)
        return el

    def loop(self, node, parent_uid):
        cls = node.get("class")
        el = ET.Element("Structure", {"_name": "While Loop" if cls == "whileLoop" else "For Loop",
                                      "uid": node.get("uid"), "uid_parent": parent_uid})
        el.set("count", "")
        if cls == "forLoop":
            el.set("maxin", "")
            el.set("maxout", "")
        inner = node.find("diagramList/SL__arrayElement")
        srn = next((n for n in self.node_defs(inner) if n.get("class") == "sRN"), None) if inner is not None else None
        inner_terms = {}
        for t in (srn.find("termList") if srn is not None else []):
            d = t.find("dco")
            if d is not None and t.get("class") == "term":
                inner_terms.setdefault(d.get("uid"), []).append(t)
        # loop i and the stop condition are DCOs of the loop with a terminal inside
        idx = node.find("loopIndexDCO")
        if idx is not None:
            for t in inner_terms.get(idx.get("uid"), []):
                self.term_owner[t.get("uid")] = (el, "#count", False)
        tst = node.find("loopTestDCO")
        if tst is not None:
            cond = ET.Element("Condition", {"uid": tst.get("uid"), "uid_parent": node.get("uid"), "value": "stop"})
            for t in inner_terms.get(tst.get("uid"), []):
                self.term_owner[t.get("uid")] = (cond, "value", True)
            self._pending_children = getattr(self, "_pending_children", [])
        n_in = n_out = 0
        sr = []
        children = []
        for term in node.find("termList"):
            d = term.find("dco")
            if d is None:
                continue
            dc = d.get("class")
            if dc == "lpTun":
                out = int(d.findtext("objFlags") or 0) & 1
                if out:
                    n_out += 1
                else:
                    n_in += 1
                tun = ET.Element("Tunnel", {"_id": ("Out%d" % n_out) if out else ("In%d" % n_in),
                                            "uid": term.get("uid"), "uid_parent": node.get("uid")})
                if d.find("innerLpTunDCO") is not None or d.findtext("TunnelType"):
                    tun.set("mode", "index")
                self.term_owner[term.get("uid")] = (tun, "value", not out)
                for t in inner_terms.get(d.get("uid"), []):
                    self.term_owner[t.get("uid")] = (tun, "value", bool(out))
                inner_dco = d.find("innerLpTunDCO")
                if inner_dco is not None:
                    for t in inner_terms.get(inner_dco.get("uid"), []):
                        self.term_owner[t.get("uid")] = (tun, "value", bool(out))
                children.append(tun)
            elif dc == "lMax":
                self.term_owner[term.get("uid")] = (el, "#maxin", True)
                for t in inner_terms.get(d.get("uid"), []):
                    self.term_owner[t.get("uid")] = (el, "#maxout", False)
            elif dc == "rSR":
                sr.append((term, d))
        by_dco = {t.find("dco").get("uid"): t for t in node.find("termList") if t.find("dco") is not None}
        for term, d in sr:
            reg = ET.Element("ShiftReg", {"uid": d.get("uid"), "uid_parent": node.get("uid")})
            lefts = [x.get("uid") for x in d.findall("lsrDCOList/SL__arrayElement")]
            # ONE Left for all elements of a stacked register (NI: inputs value,value,value -
            # the initializers - and outputs value,value,value - element 0 first, as the rSR's
            # lsrDCOList lists them), each position kept even when unwired
            left = ET.SubElement(reg, "Left", {"uid": lefts[0] if lefts else d.get("uid"), "uid_parent": d.get("uid")})
            l_ins, l_outs = [], []
            for k, lu in enumerate(lefts):
                lterm = by_dco.get(lu)
                if lterm is not None:
                    self.term_owner[lterm.get("uid")] = (left, "value", True)
                    l_ins.append((lterm.get("uid"), "value"))
                for t in inner_terms.get(lu, [])[:1]:
                    self.term_owner[t.get("uid")] = (left, "value", False)
                    l_outs.append((t.get("uid"), "value"))
            self.ordered_ports.append((left, "inputs", l_ins))
            self.ordered_ports.append((left, "outputs", l_outs))
            rin = inner_terms.get(d.get("uid"), [])
            right = ET.SubElement(reg, "Right", {"uid": rin[0].get("uid") if rin else d.get("uid"),
                                                 "uid_parent": d.get("uid")})
            for t in rin:
                self.term_owner[t.get("uid")] = (right, "value", True)
            self.term_owner[term.get("uid")] = (right, "value", False)
            children.append(reg)
        if inner is not None:
            self.walk(inner, node.get("uid"), el)
        for c in children:
            el.append(c)
        if tst is not None:
            el.append(cond)
        return el

    EVENT_KINDS = {1073741826: "Value Change", 1073741825: "Timeout", -2147483645: "Panel Close?",
                   1073741839: "Mouse Enter", 1073741838: "Mouse Leave", 1000: "User Event"}

    def event_selectors(self, node, n):
        """Frame index -> the AIXML selector of an Event Structure frame, from its EventSpecs:
        ' "Stop": Value Change ', ' Timeout ', ' Panel Close? '."""
        items = {i: [] for i in range(n)}
        for sp in node.findall("EventNodeEvents/SL__arrayElement"):
            di = int(sp.findtext("diagramIdx") or 0)
            kind = self.EVENT_KINDS.get(int(sp.findtext("type") or 0), "?")
            ddo = sp.findtext("ddoUID") or "0"
            ctl = self.ctl_by_ddo.get(ddo) if ddo != "0" else None
            items.setdefault(di, []).append('"%s": %s' % (ctl.get("_name"), kind) if ctl is not None else kind)
        return {i: " %s " % ", ".join(v) for i, v in items.items()}

    def case(self, node, parent_uid, event=False):
        el = ET.Element("Structure", {"_name": "Event Structure" if event else "Case Structure",
                                      "uid": node.get("uid"), "uid_parent": parent_uid})
        if not event:
            el.set("selectin", "")
        frames = list(node.find("diagramList"))
        frame_srn_terms = []
        for fr in frames:
            srn = next((n for n in self.node_defs(fr) if n.get("class") == "sRN"), None)
            ts = {}
            for t in (srn.find("termList") if srn is not None else []):
                d = t.find("dco")
                if d is not None and t.get("class") == "term":
                    ts.setdefault(d.get("uid"), []).append(t)
            frame_srn_terms.append(ts)
        sel_strings = [bytes.fromhex(x.text).decode("mac_roman") if x.text else ""
                       for x in node.findall("SelectStringArray/SL__arrayElement")]
        sel_tree = None
        frame_els = []
        for i, fr in enumerate(frames):
            attrs = {"selector": "", "uid": fr.get("uid"), "uid_parent": node.get("uid")}
            if not event:
                attrs["selectout"] = ""
            frame_els.append(ET.Element("CaseFrame", attrs))
        n_in = n_out = 0
        outer_tunnels = []
        for term in node.find("termList"):
            d = term.find("dco")
            if d is None:
                continue
            dc = d.get("class")
            if dc == "caseSel":
                sel_tree = self.tid_tree(term)
                self.term_owner[term.get("uid")] = (el, "#selectin", True)
                for i, ts in enumerate(frame_srn_terms):
                    for t in ts.get(d.get("uid"), []):
                        self.term_owner[t.get("uid")] = (frame_els[i], "#selectout", False)
            elif dc == "selTun":
                f = int(d.findtext("objFlags") or 0)
                out = f & 1
                if out:
                    n_out += 1
                else:
                    n_in += 1
                tid = ("Out%d" % n_out) if out else ("In%d" % n_in)
                tun = ET.Element("Tunnel", {"_id": tid, "uid": term.get("uid"), "uid_parent": node.get("uid")})
                if f & 0x200000:
                    # cond="true" is the 0x200000 bit, on input and output tunnels alike
                    tun.set("cond", "true")
                self.term_owner[term.get("uid")] = (tun, "value", not out)
                outer_tunnels.append(tun)
                for i, ts in enumerate(frame_srn_terms):
                    for t in ts.get(d.get("uid"), []):
                        half = ET.Element("Tunnel", {"_id": tid, "uid": t.get("uid"), "uid_parent": frames[i].get("uid")})
                        self.term_owner[t.get("uid")] = (half, "value", bool(out))
                        frame_els[i].append(half)
        # selectors
        items = {i: [] for i in range(len(frames))}
        st = (sel_tree.get("Type") if sel_tree is not None else "") or ""
        labels = [e.text or "" for e in sel_tree.findall("EnumLabel")] if sel_tree is not None else []
        for r in node.findall("SelectRangeArray32/SL__arrayElement"):
            di = int(r.findtext("diagramIdx") or 0)
            s, e = int(r.findtext("start") or 0), int(r.findtext("end") or 0)
            stt, ett = int(r.findtext("startRangeType") or 0), int(r.findtext("endRangeType") or 0)
            if st == "Boolean":
                txt = "True" if s else "False"
            elif st == "Cluster":
                txt = "No Error" if e == INT_MIN else "Error .."
            else:
                def val(v):
                    if st == "String":
                        return '"%s"' % sel_strings[v] if 0 <= v < len(sel_strings) else str(v)
                    if labels:
                        return labels[v] if 0 <= v < len(labels) else str(v)
                    return str(v)
                if stt == 0 and ett == 0:
                    txt = val(s)
                else:
                    txt = "%s..%s" % (val(s) if stt != 3 else "", val(e) if ett != 3 else "")
            items.setdefault(di, []).append(txt)
        dflt = node.findtext("SelectDefaultCase")
        if dflt:
            try:
                di = int(dflt, 16)
                if di != 0xFF:
                    items.setdefault(di, []).append("Default")
            except ValueError:
                pass
        ev = self.event_selectors(node, len(frames)) if event else None
        for i, f in enumerate(frame_els):
            f.set("selector", ev.get(i, " ? ") if event else esc_attr_items(items.get(i, [])))
        for t in outer_tunnels:
            if t.get("_id").startswith("In"):
                el.append(t)
        for i, fr in enumerate(frames):
            self.walk(fr, fr.get("uid"), frame_els[i])
            el.append(frame_els[i])
        for t in outer_tunnels:
            if t.get("_id").startswith("Out"):
                el.append(t)
        return el

    def flatseq(self, node, parent_uid):
        """A Flat Sequence: one Structure ("Flat Sequence Frame") holding one Diagram per frame,
        each frame's seqTun tunnels inside its Diagram with both sides on one element."""
        el = ET.Element("Structure", {"_name": "Flat Sequence Frame", "uid": node.get("uid"), "uid_parent": parent_uid})
        seql = node.find("sequenceList")
        for fr in (seql if seql is not None else []):
            diag = fr.find("diagramList/SL__arrayElement")
            if diag is None:
                continue
            d_el = ET.SubElement(el, "Diagram", {"uid": diag.get("uid"), "uid_parent": node.get("uid")})
            srn = next((n for n in self.node_defs(diag) if n.get("class") == "sRN"), None)
            inner = {}
            for t in (srn.find("termList") if srn is not None else []):
                d = t.find("dco")
                if d is not None:
                    inner.setdefault(d.get("uid"), []).append(t)
            n_in = n_out = 0
            tunnels = []
            for term in (fr.find("termList") if fr.find("termList") is not None else []):
                d = term.find("dco")
                if d is None or d.get("class") != "seqTun":
                    continue
                out = int(d.findtext("objFlags") or 0) & 1
                if out:
                    n_out += 1
                else:
                    n_in += 1
                tun = ET.Element("Tunnel", {"_id": ("Out%d" % n_out) if out else ("In%d" % n_in),
                                            "uid": term.get("uid"), "uid_parent": diag.get("uid")})
                self.term_owner[term.get("uid")] = (tun, "value", not out)
                for t in inner.get(d.get("uid"), []):
                    self.term_owner[t.get("uid")] = (tun, "value", bool(out))
                tunnels.append(tun)
            self.walk(diag, diag.get("uid"), d_el)
            for t in tunnels:
                d_el.append(t)
        return el

    def inplace(self, node, parent_uid):
        """An In Place Element Structure: one Structure; each in/out pair is two Nodes named
        "In Place In / Out Element", the right one naming the left one in _id. A pair is the
        matchPoserInterface joining two decomposeMatchNodes; a match node's decomposeDCO names
        the structure's OUTER terminal in its termList, its own term is the inner end."""
        el = ET.Element("Structure", {"_name": "In Place Element Structure", "uid": node.get("uid"),
                                      "uid_parent": parent_uid})
        diag = node.find("diagramList/SL__arrayElement")
        outer = {}
        for t in (node.find("termList") if node.find("termList") is not None else []):
            d = t.find("dco")
            if d is not None:
                outer[d.get("uid")] = t
        match = {n.get("uid"): n for n in self.node_defs(diag)
                 if n.get("class") == "decomposeMatchNode"} if diag is not None else {}
        rights = []
        posers = node.find("multiSegPipeFlange2Size")
        for poser in (posers if posers is not None else []):
            ld, rd = poser.find("multiSegPipeFlange1Depth"), poser.find("multiSegPipeFlange2Depth")
            l = match.get(ld.get("uid")) if ld is not None else None
            r = match.get(rd.get("uid")) if rd is not None else None
            if l is None or r is None:
                self.notes.append("In Place pair %s not read" % poser.get("uid"))
                continue
            lt, rt = list(l.find("termList")), list(r.find("termList"))
            left = ET.SubElement(el, "Node", {"_name": "In Place In / Out Element", "uid": l.get("uid"),
                                              "uid_parent": node.get("uid")})
            right = ET.Element("Node", {"_id": l.get("uid"), "_name": "In Place In / Out Element",
                                        "uid": r.get("uid"), "uid_parent": node.get("uid")})
            o = outer.get(lt[0].find("dco").get("uid"))
            if o is not None:
                self.term_owner[o.get("uid")] = (left, "value", True)
            self.term_owner[lt[1].get("uid")] = (left, "value", False)
            self.term_owner[rt[0].get("uid")] = (right, "value", True)
            o = outer.get(rt[1].find("dco").get("uid"))
            if o is not None:
                self.term_owner[o.get("uid")] = (right, "value", False)
            rights.append(right)
        if diag is not None:
            self.walk(diag, node.get("uid"), el)
        for r in rights:
            el.append(r)
        return el

    def disable(self, node, parent_uid):
        """Diagram / Conditional Disable Structure: a Node holding one Diagram per frame, the
        selector " Enabled "/" Disabled " or the condition; commentTun tunnels like a case's."""
        diagram = node.find("commentMode") is not None
        el = ET.Element("Node", {"_name": "Diagram Disable Structure" if diagram else "Conditional Disable Structure",
                                 "uid": node.get("uid"), "uid_parent": parent_uid})
        frames = list(node.find("diagramList"))
        active = node.findtext("activeDiag")
        try:
            active = int(active) if active is not None and active.strip().lstrip("-").isdigit() else None
        except ValueError:
            active = None
        conds = []
        for info in node.findall("commentSelInfoArray/SL__arrayElement"):
            # SelectorInfoElement > activeDiag (ExpressionInfo) > Tag0000 (the strings) and
            # activeDiag (the tree, its root's activePlot the operator)
            expr = info.find("activeDiag")
            strs = [bytes.fromhex(x.text).decode("mac_roman") if x.text else ""
                    for x in (expr.findall("Tag0000/SL__arrayElement") if expr is not None else [])]
            root_plot = (expr.findtext("activeDiag/SL__arrayElement/activePlot") if expr is not None else None) or ""
            op = "!=" if root_plot.upper() == "FE" else "=="
            conds.append("%s%s%s" % (strs[0], op, strs[1]) if len(strs) == 2 else "Default")
        d_els = []
        for i, fr in enumerate(frames):
            if diagram:
                sel = " Enabled " if (active is not None and i == active) or (active is None and i == len(frames) - 1) else " Disabled "
            else:
                sel = " %s " % (conds[i] if i < len(conds) else "Default")
            d_els.append(ET.SubElement(el, "Diagram", {"selector": sel, "uid": fr.get("uid"), "uid_parent": node.get("uid")}))
        frame_terms = []
        for fr in frames:
            srn = next((n for n in self.node_defs(fr) if n.get("class") == "sRN"), None)
            ts = {}
            for t in (srn.find("termList") if srn is not None else []):
                d = t.find("dco")
                if d is not None:
                    ts.setdefault(d.get("uid"), []).append(t)
            frame_terms.append(ts)
        n_in = n_out = 0
        for term in (node.find("termList") if node.find("termList") is not None else []):
            d = term.find("dco")
            if d is None or d.get("class") != "commentTun":
                continue
            out = int(d.findtext("objFlags") or 0) & 1
            if out:
                n_out += 1
            else:
                n_in += 1
            tid = ("Out%d" % n_out) if out else ("In%d" % n_in)
            tun = ET.Element("Tunnel", {"_id": tid, "uid": term.get("uid"), "uid_parent": node.get("uid")})
            self.term_owner[term.get("uid")] = (tun, "value", not out)
            el.append(tun)
            for i, ts in enumerate(frame_terms):
                for t in ts.get(d.get("uid"), []):
                    half = ET.SubElement(d_els[i], "Tunnel", {"_id": tid, "uid": t.get("uid"), "uid_parent": frames[i].get("uid")})
                    self.term_owner[t.get("uid")] = (half, "value", bool(out))
        for i, fr in enumerate(frames):
            self.walk(fr, fr.get("uid"), d_els[i])
        return el

    # ---- nodes of their own classes ------------------------------------------------------------
    @staticmethod
    def _is_out(term):
        d = term.find("dco")
        return bool(int(d.findtext("objFlags") or 0) & 1) if d is not None else False

    def _node_el(self, node, parent_uid, name, **attrs):
        el = ET.Element("Node", {"_name": name, "uid": node.get("uid"), "uid_parent": parent_uid})
        for k, v in attrs.items():
            el.set(k, v)
        return el

    def template_node(self, node, parent_uid):
        cls = node.get("class")
        el = self._node_el(node, parent_uid, TEMPLATE_CLASSES[cls])
        terms = list(node.find("termList"))
        names = names_for(cls, len(terms))
        if names is None:
            self.notes.append("node %s: no template names its terminals" % TEMPLATE_CLASSES[cls])
            names = ["?"] * len(terms)
        for t, nm in zip(terms, names):
            # a template names a repeated terminal value, value#1 ... - the #n is the composer's
            # own key, AIXML repeats the plain name
            # direction from the TERMINAL's sink bit (0x8000), which every WIRED input carries:
            # Scan From String's outputs do not set bit 0 on their DCO
            sink = bool(int(t.findtext("objFlags") or 0) & SINK)
            self.term_owner[t.get("uid")] = (el, nm.split("#")[0], sink)
        if cls == "cpdArith":
            # the operation is objFlags 0x70000 only - 0x80000 is set beside it (0x90000 is a
            # Multiply), exactly the mask the composer writes
            op = (int(node.findtext("objFlags") or 0) >> 16) & 0x7
            el.set("operation", CA_OPS.get(op, "Add"))
            inv = []
            outs = [t for t in terms if self._is_out(t)]
            ins = [t for t in terms if not self._is_out(t)]
            for t in outs[:1] + ins:
                inv.append("i" if int(t.find("dco").findtext("objFlags") or 0) & 0x10000 else " ")
            el.set("inversions", ",".join(inv))
        return el

    def nmux(self, node, parent_uid):
        f = int(node.findtext("objFlags") or 0)
        unbundle, wave = bool(f & 0x10000), bool(f & 0x80000)
        name = ("Get Waveform Components" if unbundle else "Build Waveform") if wave else \
            ("Unbundle By Name" if unbundle else "Bundle By Name")
        el = self._node_el(node, parent_uid, name)
        terms = list(node.find("termList"))
        agg = terms[0]
        ct = self.tid_tree(agg)
        labels = WAVEFORM_FIELDS if wave else [c.get("Label") or "" for c in (aixml2vi.fields(aixml2vi.typedef_inner(ct)) if ct is not None else [])]
        fields = []
        for t in terms:
            d = t.find("dco")
            if t is agg:
                if unbundle:
                    self.term_owner[t.get("uid")] = (el, "waveform" if wave else "input cluster", True)
                else:
                    self.term_owner[t.get("uid")] = (el, "output waveform" if wave else "output cluster", False)
                continue
            if d is not None and d.get("class") is None:
                # Bundle By Name's cluster input: a bare reference to the aggregate DCO
                self.term_owner[t.get("uid")] = (el, "waveform" if wave else "input cluster", True)
                continue
            i = int(d.findtext("i") or 0) if d is not None else 0
            nm = labels[i] if i < len(labels) else "?"
            fields.append(nm)
            self.term_owner[t.get("uid")] = (el, nm, not self._is_out(t))
        el.set("fields", ",".join(escape(x) for x in fields))
        return el

    def mux(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Bundle")
        terms = list(node.find("termList"))
        agg, cref = terms[0], terms[1]
        ct = self.tid_tree(agg)
        labels = [c.get("Label") or "element" for c in (aixml2vi.fields(aixml2vi.typedef_inner(ct)) if ct is not None else [])]
        self.term_owner[agg.get("uid")] = (el, "output cluster", False)
        self.term_owner[cref.get("uid")] = (el, "cluster", True)
        for k, t in enumerate(terms[2:]):
            self.term_owner[t.get("uid")] = (el, labels[k] if k < len(labels) else "element", True)
        return el

    def demux(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Unbundle")
        terms = list(node.find("termList"))
        agg = terms[0]
        ct = self.tid_tree(agg)
        labels = [c.get("Label") or "element" for c in (aixml2vi.fields(aixml2vi.typedef_inner(ct)) if ct is not None else [])]
        self.term_owner[agg.get("uid")] = (el, "cluster", True)
        for k, t in enumerate(terms[1:]):
            self.term_owner[t.get("uid")] = (el, labels[k] if k < len(labels) else "element", False)
        return el

    def aindx(self, node, parent_uid):
        """Index Array: [array, output, its indices..., output, its indices...] (the composer's
        measurement); a 2D array names its indices "index (row)"/"index (col)", a disabled one
        (DCO 0x10000) "disabled index (..)" - listed even unwired, which is how the dimension
        that stays is said - and its output is then a subarray."""
        el = self._node_el(node, parent_uid, "Index Array")
        terms = list(node.find("termList"))
        src = terms[0]
        at = self.tid_tree(src)
        n = len(at.findall("Dimension")) if at is not None and at.get("Type") == "Array" else 1
        if n > 1:
            el.set("dimensions", str(n))
        self.term_owner[src.get("uid")] = (el, "array", True)
        order = [(src.get("uid"), "array")]
        d_names = ["index (row)", "index (col)"] if n == 2 else ["index"] * n
        k = 0
        for t in terms[1:]:
            if self._is_out(t):
                tree = self.tid_tree(t)
                nm = "subarray" if tree is not None and tree.get("Type") == "Array" else "element"
                self.term_owner[t.get("uid")] = (el, nm, False)
                k = 0
            else:
                f = int(t.find("dco").findtext("objFlags") or 0)
                nm = d_names[k % n] if n > 1 else "index"
                if f & 0x10000:
                    nm = "disabled " + nm
                self.term_owner[t.get("uid")] = (el, nm, True)
                order.append((t.get("uid"), nm))
                k += 1
        self.ordered_inputs[id(el)] = (el, order)
        return el

    def abuild(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Build Array")
        terms = list(node.find("termList"))
        out = next((t for t in terms if self._is_out(t)), None)
        ot = self.tid_tree(out) if out is not None else None
        # inputs in termList order, which is the order LabVIEW builds in (a probe with 1 then 2
        # built [1,2]; reading them reversed broke the round trip of t66)
        for t in terms:
            if t is out:
                self.term_owner[t.get("uid")] = (el, "appended array", False)
            else:
                tt = self.tid_tree(t)
                same = tt is not None and ot is not None and aixml_type(lvheap.strip_label(tt)) == aixml_type(lvheap.strip_label(ot))
                self.term_owner[t.get("uid")] = (el, "array" if same else "element", True)
        return el

    HGROW = {0x10000: ("reference", True), 0x40001: ("reference out", False),
             0x20000: ("error in (no error)", True), 0x80001: ("error out", False)}

    def _vi_server_class(self, node):
        for t in node.find("termList"):
            d = t.find("dco")
            if d is not None and d.get("class") == "hGrowCItem" and int(d.findtext("objFlags") or 0) == 0x10000:
                tree = self.tid_tree(t)
                if tree is not None and tree.get("RefType") == "LVObjCtl":
                    return ref_class_name(tree.get("CtlFlags"))
                if tree is not None and tree.get("RefType") == "UDClassInst":
                    return "UDClassInst"
        return None

    def propnode(self, node, parent_uid):
        cls = self._vi_server_class(node) or "?"
        el = self._node_el(node, parent_uid, "Property Node")
        fields = []
        for t in node.find("termList"):
            d = t.find("dco")
            if d is None:
                continue
            if d.get("class") == "hGrowCItem":
                role = self.HGROW.get(int(d.findtext("objFlags") or 0))
                if role:
                    self.term_owner[t.get("uid")] = (el, role[0], role[1])
            elif d.get("class") == "propItem":
                infos = d.findall("propList/SL__arrayElement")
                name = ".".join(unquote(i.findtext("PropItemName") or "") for i in infos)
                out = bool(int(d.findtext("objFlags") or 0) & 1)
                fields.append(("read+" if out else "write+") + escape(name))
                self.term_owner[t.get("uid")] = (el, name, not out)
        el.set("fields", ",".join(fields))
        ddo = node.find("ddo")
        if ddo is not None and ddo.get("uid") in self.ctl_by_ddo:
            el.set("link", self.ctl_by_ddo[ddo.get("uid")].get("_name"))
        el.set("type", "{%s}" % cls)
        return el

    def invokenode(self, node, parent_uid):
        cls = self._vi_server_class(node) or "?"
        el = self._node_el(node, parent_uid, "Invoke Node", target=unquote(node.findtext("methName") or ""),
                           type="{%s}" % cls)
        for t in node.find("termList"):
            d = t.find("dco")
            if d is None:
                continue
            if d.get("class") == "hGrowCItem":
                role = self.HGROW.get(int(d.findtext("objFlags") or 0))
                if role:
                    self.term_owner[t.get("uid")] = (el, role[0], role[1])
            elif d.get("class") == "invokeItem":
                tree = self.tid_tree(t)
                if tree is None or tree.get("Type") == "Void":
                    continue
                out = bool(int(d.findtext("objFlags") or 0) & 1)
                self.term_owner[t.get("uid")] = (el, tree.get("Label") or "?", not out)
        return el

    def eventdata(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Event Data Node")
        fields = []
        for t in list(node.find("termList"))[1:]:
            tree = self.tid_tree(t)
            nm = (tree.get("Label") if tree is not None else None) or "?"
            fields.append(nm)
            self.term_owner[t.get("uid")] = (el, nm, False)
        el.set("fields", ",".join(escape(x) for x in fields))
        return el

    def statvi(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Static VI Reference")
        recs = self.call_targets.get(node.get("uid"), {})
        el.set("target", recs.get("SVVI") or next(iter(recs.values()), "?"))
        for t in node.find("termList"):
            self.term_owner[t.get("uid")] = (el, "value", False)
        return el

    def ctlref(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "VI Server Reference")
        ddo = node.find("ddo")
        if ddo is not None:
            ctl = self.ctl_by_ddo.get(ddo.get("uid"))
            name = ctl.get("_name") if ctl is not None else "?"
            el.set("element", name)
        else:
            name = unquote(node.findtext("label/textRec/text") or "This VI")
            tree = self.tid_tree(node.find("termList/SL__arrayElement"))
            cls = ref_class_name(tree.get("CtlFlags")) if tree is not None else None
            el.set("type", "{%s}" % (cls or "LV.VI"))
        for t in node.find("termList"):
            self.term_owner[t.get("uid")] = (el, name, False)
        return el

    def gref(self, node, parent_uid):
        t = node.find("termList/SL__arrayElement")
        tree = self.tid_tree(t) if t is not None else None
        name = (tree.get("Label") if tree is not None else None) or "?"
        if node.find("connectorTM") is not None:
            # a GLOBAL variable: the same gRef with the global VI's pane and a call link
            recs = self.call_targets.get(node.get("uid"), {})
            el = self._node_el(node, parent_uid, "Global Variable", element=name,
                               target=recs.get("IUVI") or next(iter(recs.values()), "?"))
        else:
            el = self._node_el(node, parent_uid, "Local Variable", element=name)
        if t is not None:
            self.term_owner[t.get("uid")] = (el, name, not self._is_out(t))
        return el

    def feedback(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Feedback Node")
        for t in node.find("termList"):
            d = t.find("dco")
            nm = {"initFeedback": "initializer", "leftFeedback": "previous value"}.get(d.get("class") if d is not None else "")
            if nm:
                self.term_owner[t.get("uid")] = (el, nm, nm == "initializer")
        su = node.find("SlaveFBInputNode")
        slave = self.slaves.get(su.get("uid")) if su is not None else None
        if slave is not None:
            for t in slave.find("termList"):
                d = t.find("dco")
                if d is not None and d.get("class") == "rightFeedback":
                    self.term_owner[t.get("uid")] = (el, "next value", True)
                    init = self.fb_inits.get(d.get("uid"))
                    if init is not None:
                        self.term_owner[init.get("uid")] = (el, "initializer", True)
        return el

    def expr(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Expression Node",
                           text=unquote(node.findtext("textNodeLabel/textRec/text") or ""))
        terms = list(node.find("termList"))
        for t in terms:
            self.term_owner[t.get("uid")] = (el, "output" if self._is_out(t) else "input", not self._is_out(t))
        return el

    # ---- wiring --------------------------------------------------------------------------------
    def wire(self, diag):
        sl = diag.find("signalList")
        for sig in (sl if sl is not None else []):
            ends = [self.term_owner.get(t.get("uid")) for t in sig.find("termList")]
            if any(e is None for e in ends):
                self.notes.append("signal %s touches an unread terminal" % sig.get("uid"))
                continue
            srcs = [e for e in ends if not e[2]]
            if len(srcs) != 1:
                self.notes.append("signal %s has %d sources" % (sig.get("uid"), len(srcs)))
                continue
            src_el, src_term, _ = srcs[0]
            if src_term.startswith("#"):
                net = "%s.value" % src_el.get("uid")
                if src_term == "#count":
                    src_el.set("count", net)
                elif src_term == "#maxout":
                    src_el.set("maxout", net)
                elif src_term == "#selectout":
                    src_el.set("selectout", net)
            else:
                net = "%s.%s" % (src_el.get("uid"), escape(src_term))
                # two outputs of one name (Index Array's element, element) must not share a net
                # name - the second wire then read the first one (round trip of t78)
                self.used_nets = getattr(self, "used_nets", set())
                base, k = net, 2
                while net in self.used_nets:
                    net = "%s%d" % (base, k)
                    k += 1
                self.used_nets.add(net)
                self._add(src_el, "outputs", src_term, net)
            for tu in sig.find("termList"):
                self.term_net[tu.get("uid")] = net
            for el, term, sink in ends:
                if sink:
                    if term == "#maxin":
                        el.set("maxin", net)
                    elif term == "#selectin":
                        el.set("selectin", net)
                    else:
                        self._add(el, "inputs", term, net)
        return None

    @staticmethod
    def _add(el, attr, term, net):
        cur = el.get(attr)
        item = "%s:%s" % (escape(term), net)
        el.set(attr, item if not cur else cur + "," + item)

    def pane_flags(self):
        """conIdx (as text) -> connection, read off the pane's Function type."""
        out = {}
        cons = int(self.b.main.find("CONP/Section/TypeDesc").get("TypeID"))
        func = self.b.flat[self.b.flat_of_consolidated(cons)]
        for i, c in enumerate([c for c in func if c.tag == "TypeDesc"]):
            f = int(c.get("Flags"), 16)
            if c.get("TypeID") is not None and f:
                out[str(i)] = "required" if f & 0x1000 else "recommended" if f & 0x0800 else "optional"
        return out


def main(argv):
    src = argv[0]
    if src.lower().endswith(".xml"):
        main_xml = src
    else:
        main_xml = lvheap.extract(src, tempfile.mkdtemp(prefix="vi2aixml_"))
    root = Reader(main_xml).read()
    ET.indent(root, "  ")
    text = ET.tostring(root, encoding="unicode")
    if len(argv) > 1:
        open(argv[1], "w", encoding="utf-8").write(text + "\n")
    else:
        print(text)


if __name__ == "__main__":
    main(sys.argv[1:])
