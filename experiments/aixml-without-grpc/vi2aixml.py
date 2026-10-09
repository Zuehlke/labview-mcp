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


def aixml_type(tree):
    """The AIXML type literal, as NI's export writes it: an enum is uint16{a,b}, a ring the
    bare uint16, a unit double{m} (not produced yet - the unit text is not decoded)."""
    if tree.get("Type") == "TypeDef":
        # NI's export FLATTENS a typedef to the type it wraps - the .ctl identity is lost
        # there (CLAUDE.md, "a placeholder loses every typedef"), so it is lost here too.
        inner = tree.find("TypeDesc")
        return aixml_type(inner) if inner is not None else None
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
    control characters, ',' ']' and the backslash become \XX; ':' and ';' stay raw."""
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


def _decode_value(tree, hexstr):
    if tree.get("Type") == "TypeDef" and tree.find("TypeDesc") is not None:
        return _decode_value(tree.find("TypeDesc"), hexstr)
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
    if t == "String":
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
            return t or ""
    return ""


class Reader:
    def __init__(self, main_xml):
        self.b = lvheap.Bundle(main_xml)
        _, self.term_names = aixml2vi.load_names()
        self.prim_name = {}
        import csv, html
        with open(os.path.join(aixml2vi.TABLES, "primitive-names.tsv"), encoding="utf-8") as f:
            for row in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t"):
                if row and row[0] != "primResID":
                    self.prim_name[row[0]] = html.unescape(row[1])
        self.out = ET.Element("VI")
        self.term_owner = {}    # heap term uid -> (aixml element, terminal name, is_sink)
        self.notes = []

    def tid_tree(self, el):
        e = lvheap.typeid_elements(el)
        if not e:
            return None
        return self.b.heap_type_tree(int(lvheap.TYPEID_RE.fullmatch(e[0].text.strip()).group(1)))

    def read(self):
        b = self.b
        self.out.set("_name", b.main.find("LVSR/Section").get("Name"))
        self.out.set("description", b.main.findtext("STRG/Section/String") or "")
        pane = self.pane_flags()
        fp_by_uid = {}
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
            el.set("uid_parent", "root")
            dd = fpdco.findtext("DefaultData")
            el.set("value", self.default_value(tree, dd) if t else "")
            con = fpdco.findtext("conNum")
            con = "0" if con is None else con
            if con != "-1" and con in pane:
                el.set("conIdx", con)
                el.set("connection", pane[con])
            fp_by_uid[fpdco.get("uid")] = el
        diag = b.bd.find("root")
        nodes = []
        # A node is often DEFINED in the diagram's zPlaneList and only REFERENCED from nodeList
        # as a bare <SL__arrayElement uid=".."/> (FINDINGS 3.5 item 5) - resolve the reference.
        defs = {e.get("uid"): e for e in diag.find("zPlaneList") if e.get("class")}
        for ref in diag.find("nodeList"):
            node = ref if ref.get("class") else defs.get(ref.get("uid"))
            if node is None:
                self.notes.append("node %s referenced but not defined on this diagram" % ref.get("uid"))
                continue
            cls = node.get("class")
            if cls == "sRN":
                for term in node.find("termList"):
                    tc = term.get("class")
                    if tc == "fPTerm":
                        el = fp_by_uid.get(term.find("dco").get("uid"))
                        if el is not None:
                            self.term_owner[term.get("uid")] = (el, "value", el.tag == "Indicator")
                    elif tc == "term" and term.find("dco").get("class") == "bDConstDCO":
                        nodes.append(self.constant(term))
                    else:
                        self.notes.append("root terminal of class %s not read" % (term.find("dco").get("class")))
            elif cls == "prim":
                nodes.append(self.prim(node))
            else:
                self.notes.append("diagram node of class %s (uid %s) not read" % (cls, node.get("uid")))
        on_root = {el.get("uid") for el, _, _ in self.term_owner.values()}
        for el in fp_by_uid.values():
            if el.get("uid") not in on_root:
                # its diagram terminal sits inside a structure this reader does not walk yet
                el.set("uid_parent", "?")
                self.notes.append("%s %r: terminal is inside a structure" % (el.tag, el.get("_name")))
            self.out.append(el)
        for el in nodes:
            if el is not None:
                self.out.append(el)
        self.wire(diag)
        for n in self.notes:
            self.out.append(ET.Comment(" vi2aixml: " + n + " "))
        return self.out

    def default_value(self, tree, dd):
        if dd is None:
            return {"Boolean": "false", "String": "", "Path": ""}.get(tree.get("Type"), "0")
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

    def constant(self, term):
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
        el.set("uid_parent", "root")
        v = decode_value(tree, dco.findtext("ConstValue") or "") if t else None
        el.set("value", v if v is not None else "")
        if v is None:
            self.notes.append("constant %s: value of type %s not decoded" % (ddo.get("uid"), lvheap.type_key(tree)))
        self.term_owner[term.get("uid")] = (el, "value", False)
        return el

    def prim(self, node):
        rid = node.findtext("primResID")
        name = self.prim_name.get(rid)
        el = ET.Element("Node", {"_name": name or "?primResID %s" % rid,
                                 "uid": node.get("uid"), "uid_parent": "root"})
        if name is None:
            self.notes.append("primitive primResID %s has no name in the table" % rid)
        for term in node.find("termList"):
            dco = term.find("dco")
            pidx = dco.findtext("parmIndex") or "out"
            tname = self.term_names.get((rid, pidx), "?parm%s" % pidx)
            sink = bool(int(term.findtext("objFlags") or 0) & SINK)
            self.term_owner[term.get("uid")] = (el, tname, sink)
        return el

    def wire(self, diag):
        ins, outs = {}, {}
        for sig in diag.find("signalList"):
            ends = [self.term_owner.get(t.get("uid")) for t in sig.find("termList")]
            if any(e is None for e in ends):
                self.notes.append("signal %s touches an unread terminal" % sig.get("uid"))
                continue
            srcs = [e for e in ends if not e[2]]
            if len(srcs) != 1:
                self.notes.append("signal %s has %d sources" % (sig.get("uid"), len(srcs)))
                continue
            src_el, src_term, _ = srcs[0]
            net = "%s.%s" % (src_el.get("uid"), escape(src_term))
            outs.setdefault(id(src_el), []).append((src_term, net))
            for el, term, sink in ends:
                if sink:
                    ins.setdefault(id(el), []).append((term, net))
        for el in self.out:
            if not isinstance(el.tag, str):
                continue
            i = ins.get(id(el), [])
            o = outs.get(id(el), [])
            if el.tag == "Indicator":
                el.set("inputs", "value:" + (i[0][1] if i else ""))
            elif el.tag in ("Control", "Constant"):
                el.set("outputs", "value:" + (o[0][1] if o else ""))
            elif el.tag == "Node":
                if i:
                    el.set("inputs", ",".join("%s:%s" % (escape(t), n) for t, n in i))
                if o:
                    el.set("outputs", ",".join("%s:%s" % (escape(t), n) for t, n in o))

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
