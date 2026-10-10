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


INT_MIN, INT_MAX = -2 ** 31, 2 ** 31 - 1


def unquote(t):
    if t and t.startswith('"') and t.endswith('"'):
        return t[1:-1].replace('\\"', '"').replace("\\\\", "\\")
    return t or ""


def esc_attr_items(items):
    """Case selector items as NI writes them: joined by ', ' with the comma escaped."""
    return "\\2C ".join(items)


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
        self.term_owner = {}    # heap term uid -> (aixml element, terminal name, is_sink)
        self.notes = []
        self.call_targets = self._call_targets()
        self.diags = []         # every diagram walked, for wiring

    # ---- helpers ---------------------------------------------------------------------------
    def tid_tree(self, el):
        e = lvheap.typeid_elements(el)
        if not e:
            return None
        return self.b.heap_type_tree(int(lvheap.TYPEID_RE.fullmatch(e[0].text.strip()).group(1)))

    def _call_targets(self):
        """node uid -> AIXML target of a subVI call, from the diagram's IUVI link records."""
        out = {}
        bdhp = self.b.main.find("LIbd/Section/BDHP")
        for rec in (bdhp if bdhp is not None else []):
            qual = [x.text or "" for x in rec.findall("LinkSaveQualName/String")]
            if not qual:
                continue
            name = ":".join(qual)
            for off in rec.findall("LinkOffsetList/Offset"):
                try:
                    out[str(int(off.text, 16))] = (rec.tag, name)
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
            elif cls in ("iUse", "polyIUse"):
                el = self.call(node, parent_uid)
            else:
                self.notes.append("diagram node of class %s (uid %s) not read" % (cls, node.get("uid")))
            if el is not None:
                container.append(el)

    # ---- elements ----------------------------------------------------------------------------
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
        kind, target = self.call_targets.get(node.get("uid"), (None, None))
        el = ET.Element("Call", {"uid": node.get("uid"), "uid_parent": parent_uid})
        if target is None:
            self.notes.append("call node %s: no link record names it" % node.get("uid"))
            target = "?"
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
            for k, lu in enumerate(lefts):
                lterm = by_dco.get(lu)
                left = ET.SubElement(reg, "Left", {"uid": lu, "uid_parent": d.get("uid")})
                if lterm is not None:
                    self.term_owner[lterm.get("uid")] = (left, "value", True)
                for t in inner_terms.get(lu, []):
                    self.term_owner[t.get("uid")] = (left, "value" if k == 0 else "value%d" % (k + 1), False)
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

    def case(self, node, parent_uid):
        el = ET.Element("Structure", {"_name": "Case Structure", "uid": node.get("uid"), "uid_parent": parent_uid})
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
            frame_els.append(ET.Element("CaseFrame", {"selector": "", "selectout": "",
                                                      "uid": fr.get("uid"), "uid_parent": node.get("uid")}))
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
                if out and f & 0x300000:
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
        for i, f in enumerate(frame_els):
            f.set("selector", esc_attr_items(items.get(i, [])))
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
                self._add(src_el, "outputs", src_term, net)
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
