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
import html
import json
import math
import os
import re
import shutil
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
LOOP_STOP_IF_TRUE = aixml2vi.LOOP_STOP_IF_TRUE


def escape(s):
    """AIXML's own escaping for names inside inputs=/outputs=: , and : and \\ as \\XX."""
    return "".join("\\%02X" % ord(c) if c in ",:\\" else c for c in s)


_REF_BY_FLAGS = None


def ref_class_name(flags):
    global _REF_BY_FLAGS
    if _REF_BY_FLAGS is None:
        _REF_BY_FLAGS = {}
        tsv = os.path.join(aixml2vi.OWN_PARTS, "refnum-classes.tsv")
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
    inner = tree
    while not lab and inner is not None and inner.get("Type") == "TypeDef":
        # a typedef'd field carries its label on the type it wraps (an Alignment cluster's
        # `uint16{Left,Center,Right}.Horizontal`, which NI's export keeps and the wrapper lost)
        inner = next((c for c in inner if c.tag == "TypeDesc"), None)
        lab = inner.get("Label") if inner is not None else None
    return t + ("." + lab.replace(",", "\\2C") if lab else "")


def class_names(tree):
    """The qualified class of every ref{UDClassInst} aixml_type writes for `tree`, in the order
    it writes them ("3D Plot.lvlib:3D Bar.lvclass"). AIXML names no class - NI's export neither -
    so the reader hands them to the composer after </VI> (the XControl sweep, 2026-10-22)."""
    if tree is None:
        return []
    t = tree.get("Type")
    kids = [c for c in tree if c.tag == "TypeDesc"]
    if t == "Refnum" and tree.get("RefType") == "UDClassInst":
        return [":".join(i.get("Text") or "" for i in tree.findall("Item"))]
    if t == "TypeDef":
        return class_names(kids[0]) if kids else []
    if t in ("Array", "Cluster") or (t == "Refnum" and tree.get("RefType") in
                                     ("LVObjCtl", "DataLog", "Notifier", "Queue", "UserEvent", "EventReg", "Occurrence")):
        return [n for k in kids for n in class_names(k)]
    return []


def xctl_items(tree):
    """For every VI Server refnum aixml_type writes for `tree` (ref{LV.<Class>}), in its order:
    the XCONTROL it is typed to - its Item texts joined by ';' (`3DPC_SurfacePlot.xctl;
    3DPC_SurfacePlot`, HasItem 1, ItmIdent LVXC) - or '' for a plain one. AIXML writes both as
    ref{LV.Control}; a plain refnum wired into an XControl-typed terminal is a "Class conflict"
    (Gaussian surface data.vi, 2026-10-22), so the reader hands them on after </VI>."""
    if tree is None:
        return []
    t = tree.get("Type")
    kids = [c for c in tree if c.tag == "TypeDesc"]
    if t == "Refnum" and tree.get("RefType") == "LVObjCtl":
        own = ";".join(i.get("Text") or "" for i in tree.findall("Item")) if tree.get("ItmIdent") == "LVXC" else ""
        return [own] + [n for k in kids for n in xctl_items(k)]
    if t in ("TypeDef", "Array", "Cluster", "Function", "Refnum"):
        return [n for k in kids for n in xctl_items(k)]
    return []


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
    if t == "Function":
        # a connector pane, as NI's export writes Call By Reference's type=: the pattern and each
        # slot's flags in hex, then the slots' types - function{58,900,...{cluster{..}.error out,..}}
        parts = ["void" if k.get("Type") == "Void" else _sub(k) for k in kids]
        if any(x is None for x in parts):
            return None
        flags = ["%x" % int(k.get("ref.Flags") or "0", 16) for k in kids]
        return "function{%s{%s}}" % (",".join(["%x" % int(tree.get("Pattern") or "0", 16)] + flags), ",".join(parts))
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
        if rt == "AutoRef":
            return "ref{ActiveX}"       # an ActiveX refnum, whatever its interface (Excel._Application)
        if rt == "LVObjCtl":
            cls = ref_class_name(tree.get("CtlFlags")) or \
                Reader.learned_classes().get("classes", {}).get((tree.get("CtlFlags") or "").upper())
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
        if rt == "Menu":
            return "ref{Menu}"
        if rt == "EventReg" and len(kids) > 1:
            # every registered event, in order - the composer reads the list back
            parts = [_sub(k) for k in kids]
            return None if any(p is None for p in parts) else "ref{EventReg}{%s}" % ",".join(parts)
        if rt in ("DataLog", "Notifier", "Queue", "UserEvent", "EventReg", "Occurrence"):
            inner = _sub(kids[0]) if kids else None
            return "ref{%s}" % rt + ("{%s}" % inner if inner else "")
        if rt == "UsrDefndTag":
            return "ref{GenClassTag.%s.%s}" % (tree.get("Ident") or "", tree.get("TypeName") or "")
        return None
    k = lvheap.type_key(tree)
    if t == "FixedPoint":
        return "fixed"      # NI's export writes a fixed-point number bare, its word lengths dropped
    if k and k.startswith("Unit"):
        base = TYPE_TO_AIXML.get("Num" + k[4:])
        if base is None:
            return None
        units = tree.findall("PhysUnit")
        if units:
            spelt = unit_text(units)
            return "%s{%s}" % (base, spelt) if spelt else None
        labels = [e.text or "" for e in tree.findall("EnumLabel")]
        if labels:
            return "%s{%s}" % (base, ",".join(escape_value(l) for l in labels))
        return base
    return TYPE_TO_AIXML.get(k)


# a numeric's PHYSICAL UNIT: one <PhysUnit Val1=base Val2=exponent> per SI base unit, the exponent
# a signed 16-bit number (65533 is -3). NI's export spells the product `m^-3 kg` - in base order,
# separated by a space, exponent 1 bare. Measured 2026-10-15 on the cache's units: 2 = s, 3 = m,
# 4 = kg (double{s}, double{m}, double{m^-3 kg}); the rest is LabVIEW's base-unit order and is
# NOT measured here, so an index outside the three reads as "unknown unit" rather than a guess.
PHYS_UNITS = {2: "s", 3: "m", 4: "kg"}


def unit_text(units):
    parts = []
    for u in sorted(units, key=lambda u: int(u.get("Val1") or 0)):
        name = PHYS_UNITS.get(int(u.get("Val1") or -1))
        if name is None:
            return None
        exp = int(u.get("Val2") or 1)
        exp = exp - 0x10000 if exp >= 0x8000 else exp
        parts.append(name if exp == 1 else "%s^%d" % (name, exp))
    return " ".join(parts)


def empty_value(tree):
    """The value NI's export writes for a type that carries no DefaultData.

    NI's exports write nothing for a type with no literal: 261 refnums, 34 waveforms, 30 dynamic
    data, 27 timestamps, 9 variants, 7 pictures in the cache, all value="". A TYPEDEF is its
    wrapped type and a CLUSTER its elements' literals - this answered "0" for both until
    2026-10-14, where NI writes [0,0] for a typedef'd cluster of two doubles.
    """
    t = tree.get("Type")
    kids = [c for c in tree if c.tag == "TypeDesc"]
    if t == "TypeDef":
        return empty_value(kids[0]) if kids else "0"
    if t == "Cluster":
        return "[%s]" % ",".join(empty_value(k) for k in kids)
    if t == "Array":
        return "[]"         # NI's exports: 356 array value=[] in the cache
    return {"Boolean": "false"}.get(t, "" if t in (
        "String", "Path", "Refnum", "Picture", "MeasureData", "LVVariant", "Tag") else "0")


def string_block_text(el):
    """The text of a pylabview string block such as STRG's description. A MULTI-LINE one is
    written as <Chunk> lines joined by its EOLN attribute ("LF", "CRLF"), a chunk possibly as
    Storage="hex" - LVblock.initWithXMLSingleString. Reading only the element's own text gave the
    indentation between the chunks, so every multi-line VI description came back as whitespace
    (15 of 40 cached NI examples, 2026-10-14)."""
    if el is None:
        return ""

    def one(c):
        if c.get("Storage") == "hex":
            return bytes.fromhex(c.text or "").decode("mac_roman")
        return c.text or ""
    chunks = el.findall("Chunk")
    if not chunks:
        return one(el)
    eoln = (el.get("EOLN") or "LF").replace("CR", "\r").replace("LF", "\n")
    return eoln.join(one(c) for c in chunks)


def escape_description(s):
    """NI's escaping inside description=, counted over the cached exports: ',' and ':' and the
    control characters as a backslash plus two hex digits (909 \\2C, 163 \\3A, 866 \\0A on VI
    descriptions), a backslash DOUBLED, everything else raw - ']' included, unlike a value."""
    return "".join("\\\\" if c == "\\" else ("\\%02X" % ord(c) if ord(c) < 0x20 or c in ",:" else c) for c in s)


def escape_value(v):
    """NI's escaping inside value= and type literals, counted over 1 584 cached exports:
    control characters, ',' and ']' become a backslash plus two hex digits; ':' and ';' stay raw.
    A BACKSLASH is DOUBLED - this said `\\5C` until 2026-10-12, and NI's exports hold 0 values with
    `\\5C` against 100 with `\\\\` (76 paths `..\\\\lvcanvas.html`, 21 strings `[\\\\t\\\\n...`), which
    aixml2vi.unescape reads back as one backslash."""
    return "".join("\\\\" if c == "\\" else ("\\%02X" % ord(c) if ord(c) < 0x20 or c in ",]" else c) for c in v)


def fmt_float(v):
    # lower case, as NI's exporter writes them: 8 nan and 18 inf/-inf in the cached exports and
    # not one NaN or Inf (2026-10-15)
    if v != v:
        return "-nan" if math.copysign(1.0, v) < 0 else "nan"   # NI keeps the sign bit
    if v in (float("inf"), float("-inf")):
        return "inf" if v > 0 else "-inf"
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
        return escape_value(lvheap.value_text(b[pos + 4:pos + 4 + n])), pos + 4 + n
    if t == "Path":
        n = struct.unpack(">I", b[pos + 4:pos + 8])[0]
        return _decode_value(tree, b[pos:pos + 8 + n].hex()), pos + 8 + n
    # Measured 2026-10-15 on the cluster constants the reader left undecoded (63 in the cache): a
    # REFNUM in a constant is four bytes and NI writes it empty; a PICTURE is length-prefixed like
    # a string; an EMPTY variant is the 20 bytes below and NI writes it empty too. A variant that
    # holds data is not decoded - its layout was not measured.
    if t == "Refnum":
        if len(b) < pos + 4:
            raise ValueError("refnum past the end")
        return "", pos + 4
    if t == "Picture":
        n = struct.unpack(">I", b[pos:pos + 4])[0]
        return escape_value(lvheap.value_text(b[pos + 4:pos + 4 + n])), pos + 4 + n
    if t == "LVVariant":
        if b[pos + 4:pos + 20] == EMPTY_VARIANT_TAIL:
            return "", pos + 20
        raise ValueError("a variant holding data")
    raise ValueError("no flattened form known for %s" % t)


# an EMPTY variant flattened, after its 4-byte version word (0x26008000 in LabVIEW 2026): one type,
# void (`0004 0000`), then `0001 0000`, no data and no attributes
EMPTY_VARIANT_TAIL = bytes.fromhex("00000001000400000001000000000000")


def _decode_value(tree, hexstr):
    if tree.get("Type") == "TypeDef" and tree.find("TypeDesc") is not None:
        return _decode_value(tree.find("TypeDesc"), hexstr)
    if tree.get("Type") in ("Array", "Cluster", "NumFloatExt", "NumComplex128", "NumComplex64", "Picture"):
        return _flat_decode(tree, bytes.fromhex(hexstr) if hexstr else b"", 0)[0]
    t = tree.get("Type")
    if t and t.startswith("Unit"):
        t = "Num" + t[4:]   # a ring or enum holds its base number
    b = bytes.fromhex(hexstr) if hexstr else b""
    if t == "Refnum" and not any(b):
        # a REFNUM constant on its own, as inside a cluster: NI writes it empty - five zero bytes
        # in webdoc.llb's Size Strings.vi, where the cluster rule had only ever seen four
        return ""
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
        return escape_value(lvheap.value_text(b[4:4 + n]))
    if t == "Path":
        if len(b) < 12:
            return ""
        kind, count = struct.unpack(">HH", b[8:12])
        comps, i = [], 12
        for _ in range(count):
            n = b[i]
            comps.append(lvheap.value_text(b[i + 1:i + 1 + n]))
            i += 1 + n
        if kind == 0 and comps:
            return escape_value(comps[0] + ":\\" + "\\".join(comps[1:]))
        # a relative path's EMPTY component is one level up, which NI writes `..`
        # (`..\\support\\sample.wav`, Excel - Macro.vi)
        return escape_value("\\".join(c if c else ".." for c in comps))
    return None


# DyOM is a DYNAMIC DISPATCH call (a dynIUse node): its offset names the node's dynLink child
LINK_TAGS = (b"IUVI", b"PUPV", b"SVVI", b"IUCI", b"CCCI", b"DNDP", b"DyOM")

# every record identifier pylabview knows (LVlinkinfo.py), less the path tags inside a record -
# where one record ends is where the next one's identifier starts
RECORD_TAGS = tuple(t.encode() for t in (
    "AP2A AP2C AP2I AXDT AXTL AXVT BSVR C2Pr C2VI C2vi CBMS CCCC CCLO CILO CLIB CMem DDPI DNDA "
    "DNVA DOPI DRPI DSCB DSCN DSDS DSEF DSSC DSSV DSVl DSVr DyOM EiVr FBIV FPHP FPPI GIAY GINT "
    "GIVI GUGV H2CC H2LD HEIR HOPI HOXF HVIR HXCI HXNI HpCI HpEr HpEx HpFN HpFl HpVI HpVL HpVr "
    "IUIV IUPR IUVI IVGV IVOV LIBR LVCC LVIN LVPR LVSB LVXC MNMS MSIM MUDF NCIN NEXF NSCR OBDR "
    "OVIR OXTR PNOM POLY PUPV RCFL RFil RVPI SCVI SFTB SVVI TDCC UDPI V2CC V2LD VIAV VICC VICI "
    "VIDS VIFN VIFl VIGV VIIV VILB VIMS VIPI VIPR VIPV VIRC VIVI VIVl VIVr VIXC VIXF VIXN VRPI "
    "VTVN XCXI XFil XFun XHPR XIMD XINT XIPY XNDI XNEF XNFP XNVI XNXI XPLO XVPR IUCI CCCI DNDP"
).split())


def _pascal_names(data, j, n):
    """n Pascal strings from j: ([bytes], index after them), or (None, j) when they overrun."""
    names = []
    for _ in range(n):
        if j >= len(data):
            return None, j
        ln = data[j]
        if j + 1 + ln > len(data):
            return None, j
        names.append(data[j + 1:j + 1 + ln])
        j += 1 + ln
    return names, j


def _expand_nested(name):
    """A DyOM record's method name is itself a qualified name - b'\\x01\\x0eWrite Width.vi' is
    one Pascal string - so a name that parses EXACTLY as a count and that many strings is
    replaced by them."""
    if len(name) >= 2 and 1 <= name[0] <= 4:
        inner, end = _pascal_names(name, 1, name[0])
        if inner is not None and end == len(name) and all(inner):
            return inner
    return [name]


def _nested_text(text):
    """A qualified-name string as pylabview writes it - unprintable bytes as literal `&#xNN;` -
    back to its bytes, then expanded like a binary record's nested name."""
    raw = re.sub(r"&#x([0-9A-Fa-f]{2});", lambda m: chr(int(m.group(1), 16)), text)
    return [x.decode("mac_roman", "replace") for x in _expand_nested(raw.encode("mac_roman", "replace"))]


def binary_link_records(data):
    """[(node uid, record tag, qualified name)] read straight out of a binary LIbd block, for
    the records that name diagram nodes. A record is its tag, a uint16 count of Pascal strings
    (the qualified name) two to six bytes later, a PTH0 path, more fields, and the
    LinkOffsetList - a uint32 count and that many uint32 uids. Read off LUnit's Test
    Failing.vi, where the count sat at tag+8, and off a LabVIEW-saved Caraya test calling class
    accessors (2026-10-11), where it sat at tag+6 - and at tag+7 for a record LabVIEW wrote at
    an ODD offset, so the count is found, not assumed. The offset list sits right before the
    record's second PTH0 (the library path) for a static call; a DyOM record has no second path
    and ends with it, before the next record's identifier. Heuristic by construction: a record
    whose layout differs yields nothing rather than a wrong name."""
    out = []
    for tag in LINK_TAGS:
        start = 0
        while True:
            i = data.find(tag, start)
            if i < 0:
                break
            start = i + 4
            names = j = None
            for s in range(i + 6, i + 12):
                n = int.from_bytes(data[s - 2:s], "big")
                if not 1 <= n <= 6:
                    continue
                cand, end = _pascal_names(data, s, n)
                if not cand or not cand[-1].lower().endswith((b".vi", b".vim", b".ctl", b".vit")):
                    continue
                p = data.find(b"PTH0", end)
                if 0 <= p and p - end <= 8:
                    names, j = cand, end
                    break
            if names is None:
                continue
            names = [x.decode("mac_roman", "replace") for nm in names for x in _expand_nested(nm)]
            p1 = data.find(b"PTH0", j)
            ln = int.from_bytes(data[p1 + 4:p1 + 8], "big")
            path_end = p1 + 8 + ln
            # LinkSaveFlag: the uint32 right after the path (pylabview's BasicLinkSaveInfo) - 2
            # means the name is relative to the CALLER's library
            flag = int.from_bytes(data[path_end:path_end + 4], "big") if path_end + 4 <= len(data) else None
            path = _path_components(data[p1:path_end])
            nxt = min([k for k in (data.find(t, i + 4) for t in RECORD_TAGS) if k > 0] or [len(data)])
            p2 = data.find(b"PTH0", path_end)
            if 0 <= p2 < nxt:
                end, floor = p2, p1
            else:
                # no library path in this record: the offset list ends it, before the uint16
                # that precedes the next identifier
                end = nxt - 2 if data[nxt - 2:nxt] == b"\x00\x02" else nxt
                floor = path_end
            for k in range(1, 64):
                at = end - 4 * k - 4
                if at <= floor:
                    break
                if int.from_bytes(data[at:at + 4], "big") == k:
                    offs = [int.from_bytes(data[at + 4 + 4 * m:at + 8 + 4 * m], "big") for m in range(k)]
                    out.extend((o, tag.decode(), ":".join(names), flag, path) for o in offs)
                    break
    return out


def _path_components(b):
    """A flattened PTH0 path -> (type, [components]): type 0 absolute (an anchor such as `<vilib>`
    first), 1 relative to the caller FILE with "" for each level up. None if it is not one."""
    if len(b) < 12 or b[:4] != b"PTH0":
        return None
    kind, count = struct.unpack(">HH", b[8:12])
    comps, i = [], 12
    for _ in range(count):
        if i >= len(b):
            return None
        n = b[i]
        comps.append(b[i + 1:i + 1 + n].decode("mac_roman", "replace"))
        i += 1 + n
    return kind, comps


_RESOLVER = None
_CALLEE_SLOTS = {}


def link_file(path, caller_file):
    """A link's path -> the callee's file on this machine, or None: an anchored one through the
    installation's and the LVAddons' trees (resolve.py), a relative one from the caller's file."""
    global _RESOLVER
    if not path:
        return None
    kind, comps = path
    if not comps:
        return None
    if comps[0].startswith("<") and comps[0].endswith(">"):
        if _RESOLVER is None:
            import resolve
            _RESOLVER = resolve.Resolver()
        import resolve
        for anchor, d in _RESOLVER.roots:
            if anchor == comps[0]:
                # a path through an .llb CONTAINER (every DAQmx link) is read from its extracted member
                p = resolve.materialize(os.path.join(d, *comps[1:]))
                if p:
                    return p
        return None
    if kind == 0:
        p = os.path.join(comps[0] + "\\", *comps[1:]) if len(comps[0]) == 2 and comps[0][1] == ":" else None
        return p if p and os.path.exists(p) else None
    if not caller_file:
        return None
    p = caller_file
    for c in comps:
        p = os.path.dirname(p) if c == "" else os.path.join(p, c)
    if os.path.isfile(p):
        return p
    # a sibling inside the same .llb container file as a caller read from one
    import resolve
    m = resolve.materialize(p)
    return m if m and os.path.isfile(m) else None


def callee_slots(path):
    """A VI file -> {conIdx: terminal label}, read off its front panel heap: the pane's `cons`
    array (sparse - an element carries `index=` only where it jumps) names each slot's fPDCO, and
    the fPDCO's own label is the terminal's name. Cached per file; {} when it cannot be read."""
    if path in _CALLEE_SLOTS:
        return _CALLEE_SLOTS[path]
    out = {}
    tmp = tempfile.mkdtemp(prefix="callee_")
    try:
        cp = os.path.join(tmp, "c" + os.path.splitext(path)[1])
        real = path
        if not os.path.exists(real):
            # a member of an .llb CONTAINER file, its path running through the .llb
            import resolve
            real = resolve.materialize(path) or path
        shutil.copyfile(real, cp)
        b = lvheap.Bundle(lvheap.extract(cp, tmp))
        dcos = {e.get("uid"): e for e in b.fp.iter("SL__arrayElement") if e.get("class") == "fPDCO"}
        cons = next(b.fp.iter("cons"), None)
        cursor = 0
        for c in (cons if cons is not None else []):
            if c.get("index") is not None:
                cursor = int(c.get("index"))
            dco = c.find("ConnectionDCO")
            f = dcos.get(dco.get("uid")) if dco is not None else None
            if f is not None and f.find("ddo") is not None:
                out[cursor] = label_text(f.find("ddo"))
            cursor += 1
    except Exception:
        out = {}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    _CALLEE_SLOTS[path] = out
    return out


def label_text(ddo):
    parts = ddo.find("partsList")
    for part in (parts if parts is not None else []):
        if part.get("class") == "label" and part.findtext("partID") == "16":
            t = part.findtext("textRec/text")
            if t and len(t) > 1 and t.startswith('"') and t.endswith('"'):
                t = t[1:-1]          # raw inside the quotes - pylabview escapes nothing
            # pylabview writes an unquoted [NULL] for a label with no text at all, and a NUL
            # BYTE as the literal text `&#x00;` - NI's export of DW Test Change Info.vi has an
            # empty label there
            return "" if t in (None, "[NULL]") else t.replace("&#x00;", "")
    return ""


INT_MIN, INT_MAX = -2 ** 31, 2 ** 31 - 1
IGNORED, MISSING = object(), object()     # term_owner sentinels: an end AIXML has no terminal for


def sublabel_text(diag):
    """A subdiagram's LABEL (Visible Items > Subdiagram Label): a `label` of partID 8024 in the
    diagram's own zPlaneList. None when the diagram shows none - NI writes `label=""` only for a
    label that is visible and empty."""
    z = diag.find("zPlaneList") if diag is not None else None
    for o in (z if z is not None else []):
        if o.get("class") == "label" and o.findtext("partID") == "8024":
            t = o.findtext("textRec/text")
            if t and len(t) > 1 and t.startswith('"') and t.endswith('"'):
                t = t[1:-1]
            return "" if t in (None, "[NULL]") else t.replace("&#x00;", "")
    return None


def set_sublabel(el, diag):
    """`label=` as NI's exporter writes it: , : \\ and control characters as \\XX."""
    t = sublabel_text(diag)
    if t is not None:
        el.set("label", "".join("\\%02X" % ord(c) if c in ",:\\" or ord(c) < 0x20 else c for c in t))


def name_attr(s):
    """A label as a `_name` attribute, as NI's exporter writes one: a backslash DOUBLED - it writes
    `delimiter (\\\\t)` for the label `delimiter (\\t)` - so the composer's unescape cannot read a
    backslash and two hex digits that follow it as one character. And ':' and ',' as \\3A and \\2C:
    counted 2026-10-15 over 869 cached NI exports, 85 of 85 control and indicator names holding a
    colon carry \\3A and 23 of 23 holding a comma carry \\2C. Raw, JKI's `attribute? (F: entity)`
    reached lvai_vi_terminals as a name no `inputs=` string can carry, ':' being its separator."""
    return s.replace("\\", "\\\\").replace(":", "\\3A").replace(",", "\\2C")


_LABEL_ESCAPES = {"\\\\": "\\", "\\3A": ":", "\\2C": ","}


def label_of(name):
    """The label behind a `_name` written by name_attr - for the places that hand a control's name
    on as something else: a terminal name (escaped again by `escape`), a `link=` (NI writes it RAW,
    `link="PV:"`) or an event selector (escaped by its own rule). Measured as a regression the day
    name_attr learned \\3A: a VI Server Reference's output came out `SP\\5C3A`."""
    return re.sub(r"\\\\|\\3A|\\2C", lambda m: _LABEL_ESCAPES[m.group(0)], name)


def unquote(t):
    """A heap string's text: RAW between its quotes - pylabview escapes nothing inside them."""
    if t and len(t) > 1 and t.startswith('"') and t.endswith('"'):
        return t[1:-1]
    return t or ""


def esc_attr_items(items):
    """Case selector items as NI writes them: joined by ', ' with the comma escaped."""
    return "\\2C ".join(items)


def esc_case_text(s):
    """A string or enum case item's TEXT as NI writes it inside the quotes: ',' as \\2C - NI's own
    `"N-waveforms\\2C N-filters"`, which unescaped raw would split into two items - control
    characters as \\XX, and a backslash DOUBLED so aixml2vi.unescape cannot read what follows it as
    hex. A raw control character is not legal XML 1.0 at all: the LabVIEW Icon Editor's KeyDown.vi
    has a Backspace case `"\\x08"`, and the export was unreadable from that byte on (2026-10-15)."""
    return "".join("\\\\" if c == "\\" else ("\\%02X" % ord(c) if ord(c) < 0x20 or c == "," else c) for c in s)


# what XML 1.0 forbids outright; \t \n \r are legal and ElementTree writes them as references
_XML_ILLEGAL = re.compile("[\x00-\x08\x0b\x0c\x0e-\x1f]")


def escape_illegal_xml_chars(root):
    """The last line of defence before serialising: an attribute or text holding a character XML
    1.0 forbids makes the WHOLE export unparseable, so it is written as \\XX - AIXML's own escape -
    and a comment names each place, because a path that reaches here was not escaped on purpose."""
    hits = []
    for el in root.iter():
        for k, v in list(el.attrib.items()):
            if _XML_ILLEGAL.search(v):
                el.set(k, _XML_ILLEGAL.sub(lambda m: "\\%02X" % ord(m.group()), v))
                hits.append("%s %s=" % (el.tag, k) + (" uid %s" % el.get("uid") if el.get("uid") else ""))
        if el.text and _XML_ILLEGAL.search(el.text):
            el.text = _XML_ILLEGAL.sub(lambda m: "\\%02X" % ord(m.group()), el.text)
            hits.append("%s text" % el.tag)
    for h in hits:
        root.append(ET.Comment(" vi2aixml: control character written as \\XX in %s " % h))
    return hits


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
                "eventRegNode": "eventreg",
                "exprNode": "expr", "fBox": "fbox",
                "callByRefNode": "callbyref", "scriptNode": "scriptnode",
                "decimate": "decimate"}
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
        for f in glob.glob(os.path.join(aixml2vi.OWN_PARTS, "tnode__*.xml")):
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

    def __init__(self, main_xml, vi_path=None):
        self.b = lvheap.Bundle(main_xml)
        # the VI's own file: a callee linked RELATIVE to it is found from here, to name a call's
        # terminals its link carries no labels for
        self.vi_path = vi_path
        self.call_paths = {}
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
        # the libraries the VI itself belongs to (LIBN), outermost first: a link record saved
        # RELATIVE to them (LinkSaveFlag 2) names its target without them
        self.caller_libs = [l.text.strip() for l in self.b.main.findall("LIBN/Section/Library") if (l.text or "").strip()]
        self.call_targets = self._call_targets()
        self.instance_names = self._express_instances()
        self.prop_items = {}     # property node uid -> its items' code paths, in field order
        self.diags = []         # every diagram walked, for wiring
        self.attachments = None  # comment attachment objects by uid (comments())
        self.anchored = {}       # anchored comments: object uid -> text, set as comment= in read()
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
        sec = self.b.main.find("LIbd/Section")
        if sec is not None and sec.get("Format") == "bin" and sec.get("File"):
            # pylabview leaves the block BINARY when one record is of a kind it cannot parse -
            # a call into a CLASS (LUnit's Test Case.lvclass:Pass If.vi) was "?" for that
            data = open(os.path.join(self.b.dir, sec.get("File")), "rb").read()
            for off, tag, name, flag, path in binary_link_records(data):
                name = ":".join(self._relative_to_caller(name.split(":"), str(flag)))
                out.setdefault(str(off), {})[tag] = name
                if path and (tag == "IUVI" or str(off) not in self.call_paths):
                    self.call_paths[str(off)] = path
            return out
        bdhp = self.b.main.find("LIbd/Section/BDHP")
        for rec in (bdhp if bdhp is not None else []):
            # a DyOM's method name is a NESTED qualified name, which pylabview writes as literal
            # text (`&#x01;&#x0E;Write Width.vi`) - decoded and expanded as in the binary path
            qual = [x for s in rec.findall("LinkSaveQualName/String")
                    for x in _nested_text(s.text or "")]
            if not qual:
                continue
            lib = [x.text or "" for x in rec.findall("VILSPathRef/String")]
            if len(qual) == 1 and lib and lib[-1].lower().endswith((".lvlib", ".lvclass", ".xctl", ".lvlibp")):
                qual = [lib[-1]] + qual
            else:
                qual = self._relative_to_caller(qual, rec.get("LinkSaveFlag"))
            name = ":".join(qual)
            pref = rec.find("LinkSavePathRef")
            path = (int(pref.get("TpVal") or 0), [s.text or "" for s in pref.findall("String")]) if pref is not None else None
            # a class link record (DyOM, FPPI, DDPI) keeps its uids in APILinkCacheList - read
            # once pylabview parses the link cache's tail (patches.json, class-link-tail)
            for off in rec.findall("LinkOffsetList/Offset") + rec.findall("APILinkCacheList/Offset"):
                try:
                    k = str(int(off.text, 16))
                except (TypeError, ValueError):
                    continue
                out.setdefault(k, {})[rec.tag] = name
                if path and (rec.tag == "IUVI" or k not in self.call_paths):
                    self.call_paths[k] = path
        return out

    def _relative_to_caller(self, qual, flag):
        """A link record's qualified name, completed by the caller's own libraries (LIBN) where
        its LinkSaveFlag says it was saved RELATIVE to them:
        2 - to the caller's whole library chain: Caraya's Run Tests.vi calls `Run Test
            (Object).vi`, NI exports `Caraya.lvlib:Run Test (Object).vi`; Pry.vi in Lever.lvclass
            calls `Multiply Force.vi`, NI exports `Lever.lvclass:Multiply Force.vi`;
        3 - to its OUTERMOST library only: Send Normal Stop.vi (Actor Framework.lvlib, Stop
            Msg.lvclass) calls `Message Enqueuer.lvclass:Enqueue.vi`, NI exports `Actor
            Framework.lvlib:Message Enqueuer.lvclass:Enqueue.vi` - Message Maker's the same.
        The flag is per record: Pry.vi's calls into vi.lib carry 0."""
        if not self.caller_libs or not qual:
            return qual
        if flag == "2" and len(qual) == 1:
            return self.caller_libs + qual
        if flag == "3":
            return self.caller_libs[:1] + qual
        return qual

    def _express_instances(self):
        """Instance name (`1`, a GUID) -> the name NI's export gives its call. The VI's VINS
        block: section 0 is an index - a uint32 count, then each instance's name as a Pascal
        string padded with zeros - and section k+1 holds instance k, an embedded VI of its own.
        - An EXPRESS VI's instance holds the path of the template it was made from, followed by
          `Localized`: `<vilib>\\express\\...\\SampleCompressionBlock.llb\\Ex_Inst_Sample
          Compression.vi`, `...\\FileDialogBlock.llb\\File Dialog Source.vi` - and NI names the
          call after that file (Express VI - Sample Compression.vi, Manage DataPlugins.vi).
        - A MALLEABLE VI's instance links to its .vim FIRST among its VI-level links (LIvi) -
          `Pass If Equal.vim` before the `Compare Variants.vim` its diagram calls in turn, which
          only its LIbd names - and NI names the call after the .vim."""
        secs = {int(s.get("Index") or 0): s.get("File") for s in self.b.main.findall("VINS/Section")
                if s.get("Format") == "bin" and s.get("File")}
        if 0 not in secs:
            return {}
        idx = open(os.path.join(self.b.dir, secs[0]), "rb").read()
        count = int.from_bytes(idx[:4], "big")
        names, pos = [], 4
        for _ in range(count):
            while pos < len(idx) and idx[pos] == 0:
                pos += 1
            if pos >= len(idx):
                break
            ln = idx[pos]
            names.append(idx[pos + 1:pos + 1 + ln].decode("mac_roman", "replace"))
            pos += 1 + ln
        out = {}
        for k, nm in enumerate(names):
            f = secs.get(k + 1)
            if not f:
                continue
            data = open(os.path.join(self.b.dir, f), "rb").read()
            m = re.search(rb"Ex_Inst_[^\x00-\x1f]{1,120}?\.vi", data)
            if m:
                out[nm] = m.group(0).decode("mac_roman", "replace")
                continue
            # else the template path, then seven zero bytes and a Pascal string: `Localized` after
            # most (File Dialog Source.vi), `NI.LV.AL...` after Dual Channel Spectral Measurement
            # Source.vi, `_Conversion` after Collector's DDT to 1D Dbl Automatic.vi - tried in that
            # order of certainty - and never the express VI's `Configure <X>.vi` dialog, whose path
            # stands earlier in some instances
            found = None
            for after in (rb"\tLocalized", rb"[\x01-\x40][A-Za-z]", rb"[\x01-\x40]_"):
                for m in re.finditer(rb"([^\x00-\x1f][^\x00]{0,120}?\.vi)\x00{7}" + after, data):
                    # the path's last component, a Pascal string: its length byte precedes it
                    tail = m.group(1)
                    for i in range(len(tail) - 4, -1, -1):
                        if m.start(1) + i > 0 and data[m.start(1) + i - 1] == len(tail) - i:
                            name = tail[i:].decode("mac_roman", "replace")
                            if not name.startswith("Configure "):
                                found = name
                            break
                    if found:
                        break
                if found:
                    break
            if found:
                out[nm] = found
                continue
            vim = self._malleable_source(data)
            if vim:
                out[nm] = vim
        return out

    @staticmethod
    def _malleable_source(data):
        """The qualified name of the .vim a malleable instance was made from: the first
        qualified name ending in .vim among the embedded VI's VI-level links (LIvi)."""
        tmp = tempfile.mkdtemp(prefix="vins_")
        try:
            p = os.path.join(tmp, "i.vi")
            with open(p, "wb") as fh:
                fh.write(data)
            try:
                b = lvheap.Bundle(lvheap.extract(p, tmp))
            except Exception:
                return None
            sec = b.main.find("LIvi/Section")
            if sec is None:
                return None
            if sec.get("Format") != "bin":
                for rec in sec.iter():
                    q = [x for s in rec.findall("LinkSaveQualName/String") for x in _nested_text(s.text or "")]
                    if q and q[-1].lower().endswith(".vim"):
                        return ":".join(q)
                return None
            raw = open(os.path.join(b.dir, sec.get("File")), "rb").read()
            for s in range(2, len(raw) - 4):
                n = int.from_bytes(raw[s - 2:s], "big")
                if not 1 <= n <= 4:
                    continue
                cand, end = _pascal_names(raw, s, n)
                if cand and cand[-1].lower().endswith(b".vim") and 0 <= raw.find(b"PTH0", end) - end <= 8:
                    return ":".join(x.decode("mac_roman", "replace") for nm_ in cand for x in _expand_nested(nm_))
            return None
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    @staticmethod
    def _tunnel_ranks(node, dco_class):
        """term uid -> its tunnel number as NI's export counts it: inputs and outputs separately,
        in the order their INNER halves stand in the FIRST frame's sRN term list - not the order
        of the structure's own term list, and not their position. MGI's Read Number of
        children.vi lists the outer tunnels 164, 176, 348 (tops 203, 31, 116) and NI numbers them
        Out2, Out1, Out3: frame 0's sRN lists their halves 174 (176's), 162 (164's), 340 (348's).
        Empty when that list cannot be read; the caller then keeps the heap order."""
        dl = node.find("diagramList")
        if dl is None or not len(dl):
            return {}
        # the frame's OWN sRN - a nested structure's comes first in document order sometimes
        srn = next((n for n in Reader.node_defs(dl[0]) if n.get("class") == "sRN"), None)
        if srn is None or srn.find("termList") is None:
            return {}
        pos, by_dco = {}, {}
        for i, t in enumerate(srn.find("termList")):
            pos[t.get("uid")] = i
            if t.find("dco") is not None:
                # a half may name its tunnel's DCO instead of being listed by it (commentTun)
                by_dco.setdefault(t.find("dco").get("uid"), i)
        tuns = []
        for term in node.find("termList") if node.find("termList") is not None else []:
            d = term.find("dco")
            if d is None or d.get("class") != dco_class:
                continue
            halves = [pos[x.get("uid")] for x in (d.find("termList") if d.find("termList") is not None else [])
                      if x.get("uid") in pos]
            if d.get("uid") in by_dco:
                halves.append(by_dco[d.get("uid")])
            if not halves:
                return {}
            tuns.append((int(d.findtext("objFlags") or 0) & 1, min(halves), term.get("uid")))
        ranks = {}
        for out in (0, 1):
            for n, t in enumerate(sorted(x for x in tuns if x[0] == out), 1):
                ranks[t[2]] = n
        return ranks

    def dco_def(self, d):
        """A DCO as its DEFINITION. A term's <dco> is often a bare `<dco uid="402"/>` whose class
        and body live elsewhere in the heap - a shift register's rSR, for one, is defined NESTED in
        its lSR (`<dco class="lSR"> ... <rsrDCO class="rSR" uid="402">`), so the loop's own term for
        it carries no class. Read as classless, 53 of 58 missing shift registers in the cached NI
        exports went missing that way (2026-10-15)."""
        if d is None or d.get("class"):
            return d
        if getattr(self, "_dco_index", None) is None:
            self._dco_index = {}
            for e in self.b.bd.iter():
                if e.get("class") and e.get("uid"):
                    self._dco_index.setdefault(e.get("uid"), e)
        return self._dco_index.get(d.get("uid"), d)

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
    def _note_classes(self, el, tree):
        """Remember which classes `el`'s ref{UDClassInst}s are, for main() to write after </VI>."""
        names = class_names(tree)
        if names:
            self.class_refs = getattr(self, "class_refs", [])
            self.class_refs.append((el, names))
        items = xctl_items(tree)
        if any(items):
            self.xctl_refs = getattr(self, "xctl_refs", [])
            self.xctl_refs.append((el, items))

    def read(self):
        b = self.b
        # LabVIEW loads a file under its FILE name, and so names it NI's export: a class's private
        # data control saved as pdc.ctl still says Channel.ctl inside (2026-10-14). The stored
        # name stays where the two differ only in CASE - a caller may spell the path in any case
        # on Windows (`test Skipped.vi` for Test Skipped.vi, which NI's export kept)
        name = b.main.find("LVSR/Section").get("Name")
        if self.vi_path and os.path.basename(self.vi_path).lower() != (name or "").lower():
            name = os.path.basename(self.vi_path)
        # a library member's name is QUALIFIED, as NI's exporter writes it - "X.xctl:Facade.vi",
        # "Special Monitor.lvclass:Monitor Data Changed.vi" - the owning library from LIBN
        libs = [l.text.strip() for l in b.main.findall("LIBN/Section/Library") if (l.text or "").strip()]
        self.out.set("_name", ":".join(libs + [name]))
        try:
            self.pane_id = int(b.fp.findtext("root/conPane/conId"))
        except (TypeError, ValueError):
            self.pane_id = None
        # and the pane's slot FLAGS, which `connection` only partly carries: dynamic dispatch
        # (0x8000, NI's class members write 0x9000) and an output's in-place partner (0x0400
        # plus the partner slot - error out 0x0D04 beside error in at 4)
        self.pane_slot_flags = {}
        try:
            func = b.flat[b.flat_of_consolidated(int(b.main.find("CONP/Section/TypeDesc").get("TypeID")))]
            if func.get("Type") == "Function":
                for i, c in enumerate([c for c in func if c.tag == "TypeDesc"]):
                    f = int(c.get("Flags") or "0", 16)
                    if c.get("TypeID") is not None and f:
                        self.pane_slot_flags[i] = f
        except (TypeError, ValueError, AttributeError, IndexError):
            pass
        self.out.set("description", escape_description(self.description_text()))
        pane = self.pane_flags()
        self.fp_by_uid = {}
        for fpdco in b.fp.iter("SL__arrayElement"):
            if fpdco.get("class") != "fPDCO":
                continue
            ddo = fpdco.find("ddo")
            ind = int(fpdco.findtext("objFlags") or 0) & 1
            tree = self.tid_tree(fpdco)
            t = aixml_type(tree)
            el = ET.Element("Indicator" if ind else "Control", {"_name": name_attr(label_text(ddo))})
            if int(fpdco.findtext("objFlags") or 0) & 0x1000 and t and "UDClassInst" in t:
                # a DYNAMIC DISPATCH terminal (fPDCO objFlags 0x1000) - AIXML writes a dynamic
                # OUTPUT `recommended`, so the composer learns it from the trailer
                self.dynamic_fp = getattr(self, "dynamic_fp", [])
                self.dynamic_fp.append(el)
            if t is None:
                el.set("type", "?" + (lvheap.type_key(tree) or ""))
                self.notes.append("front-panel %s %r: type %s not mapped" % (ddo.get("class"), label_text(ddo), lvheap.type_key(tree)))
            else:
                el.set("type", t)
                self._note_classes(el, tree)
            # style, as NI's exports write it: latched (a boolean's mechanical action, ddo
            # objFlags 0x20000 - NI's Stop buttons carry 0x60000), Ring (a stdRing on a plain
            # number, items from its multiLabel buf) and Table (a tableControl)
            cls = ddo.get("class")
            if cls == "stdBool" and not ind and int(ddo.findtext("objFlags") or 0) & 0x20000:
                el.set("style", "latched")
            elif cls == "stdRing" and t is not None and "{" not in t:
                buf = next((p.findtext("buf") for p in ddo.iter("SL__arrayElement")
                            if p.get("class") == "multiLabel" and p.find("buf") is not None), None)
                labels = re.findall(r'"([^"]*)"', buf or "")
                el.set("items", ",".join(escape(x).replace(",", "\\2C") for x in labels))
                el.set("style", "Ring")
            elif cls == "tableControl":
                el.set("style", "Table")
            elif cls == "stdGraph":
                rid = next((pt.findtext("rsrcID") for pt in (ddo.find("partsList") if ddo.find("partsList") is not None else [])
                            if pt.get("class") == "annex" and pt.findtext("rsrcID")), None)
                if rid:
                    el.set("style", "graph" + rid)
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
        is_control_file = name.lower().endswith((".ctl", ".ctt"))
        for uid, el in self.fp_by_uid.items():
            if uid not in self.fp_placed:
                if is_control_file:
                    # a .ctl whose control has NO terminal on its diagram - the usual case for one
                    # LabVIEW saved: NI's export writes no element for it (18 of 46 compared,
                    # 2026-10-14), so neither does this; the note keeps it visible
                    self.notes.append("%s %r has no terminal on the control's diagram, and NI's "
                                      "export writes no element for it" % (el.tag, el.get("_name")))
                    continue
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
            if self.term_owner[tu] is IGNORED:
                continue
            el, nm, sink = self.term_owner[tu]
            if el.tag in ("Node", "Call") and not nm.startswith("#"):
                per_el.setdefault(id(el), (el, []))[1].append((tu, nm, sink))
        for el, terms in per_el.values():
            # A CALL lists every terminal of its callee's pane, an unwired one with an empty net, as
            # NI's exporter writes it (5 686 of 15 655 call inputs and 4 278 of 10 402 outputs in
            # the cached exports are `name:`). Wired-only left a polymorphic VI's instances - whose
            # calls are never wired - with no terminals at all, and lvai_vi_terminals on the
            # pylabview backend then had nothing to name (Trim Whitespace.vi, 2026-10-15). A name
            # the reader could not establish (`?`) stays out rather than going in as a terminal.
            # Primitives keep wired-only: their unwired names come from a table with gaps.
            every = el.tag == "Call"
            ins = ["%s:%s" % (escape(nm), self.term_net.get(tu, "")) for tu, nm, sink in terms
                   if sink and (tu in self.term_net or (every and not nm.startswith("?")))]
            outs = ["%s:%s" % (escape(nm), self.term_net.get(tu, "")) for tu, nm, sink in terms
                    if not sink and (tu in self.term_net or (every and not nm.startswith("?")))]
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
        # an UNWIRED terminal still carries its attribute with an empty net, as NI writes it on
        # every one of its 404 unwired controls and 79 indicators - 0 of ~5 500 omit it - and as
        # the schema requires (CLAUDE.md, cold-build-shakerrig.md section 2)
        for el in self.fp_by_uid.values():
            attr = "inputs" if el.tag == "Indicator" else "outputs"
            if not el.get(attr):
                el.set(attr, "value:")
        if self.anchored:
            by_uid = {}
            for e in self.out.iter():
                if isinstance(e.tag, str) and e.get("uid"):
                    by_uid.setdefault(e.get("uid"), e)
            # a comment anchored to a TERMINAL names its term or its dco - a control's terminal on
            # the diagram, a tunnel's inner side (Map Collection - Comparing Test Results.vi,
            # Replicate Core.vi): its owner is the element that terminal belongs to
            term_of_dco = {}
            for t in self.b.bd.iter("SL__arrayElement"):
                if t.get("class") == "term" and t.find("dco") is not None and t.find("dco").get("uid"):
                    term_of_dco.setdefault(t.find("dco").get("uid"), t.get("uid"))
            for uid, text in self.anchored.items():
                e = by_uid.get(uid)
                if e is None:
                    owner = self.term_owner.get(uid) if uid in self.term_owner else \
                        self.term_owner.get(term_of_dco.get(uid)) if term_of_dco.get(uid) in self.term_owner else None
                    if owner is not None and owner is not IGNORED and owner is not MISSING:
                        e = owner[0]
                if e is None:
                    self.notes.append("comment anchored to object %s, which has no element here: %r"
                                      % (uid, text[:60]))
                else:
                    e.set("comment", escape_description(text))
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
            if cls in aixml2vi.PRIM_CLASSES:
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
            elif cls in ("decomposeMatchNode", "decomposeArrayNode", "decomposeClusterNode"):
                continue        # read with its In Place Element Structure
            elif cls == "commentNode":
                el = self.disable(node, parent_uid)
            elif cls in ("iUse", "polyIUse", "dynIUse"):
                el = self.call(node, parent_uid)
            elif cls == "callParentDynIUse":
                # CALL PARENT CLASS METHOD, as NI's export writes it - a Node, no target, the
                # terminals named by this call's own pane labels (`Panel Actor in`, `Actor out`);
                # the composer finds the parent's method itself (the XControl sweep, 2026-10-22)
                el = self.call(node, parent_uid)
                el.tag = "Node"
                el.attrib = dict([("_name", "Call Parent Class Method")] +
                                 [(k, v) for k, v in el.attrib.items() if k not in ("target", "instance", "adapt")])
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
        self.comments(diag, parent_uid, container)

    def comments(self, diag, parent_uid, container):
        """The diagram's comments, as NI's exporter writes them. A comment is a `label` with no
        partID straight in the diagram's zPlaneList (a control's or node's own label sits in a
        partsList, a subdiagram label carries partID 8024), and each points at an `attachment`.
        An attachment holding an `attachedObject` is a comment ANCHORED to that object: NI writes
        it as `comment=` on the object's element (Call, Node, Constant, Structure ... 178 of them
        in the cached exports), collected here and set once every element exists. One without is
        a free comment: `<FreeLabel comment="..." uid=<the label's uid>/>` in this diagram -
        2 603 in the cached exports, escaped like a description."""
        if self.attachments is None:
            self.attachments = {e.get("uid"): e for e in self.b.bd.iter("SL__arrayElement")
                                if e.get("class") == "attachment"}
        z = diag.find("zPlaneList")
        for o in (z if z is not None else []):
            if o.get("class") != "label" or o.find("textRec") is None or o.findtext("partID") is not None:
                continue
            t = o.findtext("textRec/text")
            # pylabview writes a byte with no printable form as the literal text `&#xNN;` - a CR
            # or a NUL in a comment - and NI keeps both (`\0D`, `\00`), so they are decoded,
            # not dropped (DW Test Copy.vi, the TLS example, 2026-10-21)
            text = "" if t in (None, "[NULL]") else \
                re.sub(r"&#x([0-9A-Fa-f]{2});", lambda m: chr(int(m.group(1), 16)), unquote(t))
            # and it renders the bytes as MacRoman where LabVIEW on Windows wrote CP1252: NI's
            # `devices’ sample` came back `devicesí sample` (Analog Input - Synchronization.vi)
            try:
                text = text.encode("mac_roman").decode("cp1252")
            except (UnicodeEncodeError, UnicodeDecodeError):
                pass
            a = o.find("attachment")
            att = self.attachments.get(a.get("uid")) if a is not None else None
            owner = att.find("attachedObject") if att is not None else None
            if owner is not None and owner.get("uid"):
                self.anchored.setdefault(owner.get("uid"), text)
                continue
            container.append(ET.Element("FreeLabel", {"comment": escape_description(text),
                                                      "uid": o.get("uid"), "uid_parent": parent_uid}))

    # ---- elements ----------------------------------------------------------------------------
    def default_value(self, tree, dd):
        if dd is None:
            return empty_value(tree)
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
            el.set("_name", name_attr(name))
        el.set("type", t or "?" + lvheap.type_key(tree))
        if t:
            self._note_classes(el, tree)
        el.set("uid", ddo.get("uid"))
        el.set("uid_parent", parent_uid)
        if dco.find("ConstValue") is None:
            # NOT a default: measured 2026-10-15 over the cache, every constant of a file saved by
            # LabVIEW 11 or later carries ConstValue (5 803 of 5 803), and only the LabVIEW 9.0
            # files lack it - their values live in the compiled data space, which pylabview does
            # not decode, and 64 of their 78 are not the default. Decoding "" as 0/false/"" made
            # Dictionary Example.vi's 57 constants plausible and wrong.
            el.set("value", "")
            self.notes.append("constant %s: value not stored in the block diagram heap (a file from "
                              "before LabVIEW 11 keeps it in compiled code) - not decoded" % ddo.get("uid"))
            self.term_owner[term.get("uid")] = (el, "value", False)
            return el
        v = decode_value(tree, dco.findtext("ConstValue") or "") if t else None
        el.set("value", v if v is not None else "")
        if v is None:
            self.notes.append("constant %s: value of type %s not decoded" % (ddo.get("uid"), lvheap.type_key(tree)))
        self.term_owner[term.get("uid")] = (el, "value", False)
        return el

    def prim(self, node, parent_uid):
        rid = aixml2vi.prim_res_id(node)     # a stream Read/Write node's too (aixml2vi.PRIM_CLASSES)
        self.prim_rid = getattr(self, "prim_rid", {})
        self.prim_rid[node.get("uid")] = rid
        name = self.prim_name.get(rid)
        el = ET.Element("Node", {"_name": name or "?primResID %s" % rid,
                                 "uid": node.get("uid"), "uid_parent": parent_uid})
        if name is None:
            self.notes.append("primitive primResID %s has no name in the table" % rid)
        for attr, bit in aixml2vi.Composer.PRIM_MODE_BITS.get(name, {}).items():
            if int(node.findtext("objFlags") or 0) & bit:
                el.set(attr, "true")
        if name == "Array To Cluster":
            # its cluster SIZE is the output's field count, which NI's exporter writes as
            # elements="N" on every such node; left out, the composer takes LabVIEW's default
            # of nine and a round trip of a three-field node came back broken
            for term in node.find("termList"):
                if term.find("dco").findtext("parmIndex") is None:
                    tree = self.tid_tree(term.find("dco"))
                    if tree is not None and tree.get("Type") == "Cluster":
                        el.set("elements", str(len(aixml2vi.fields(tree))))
        slots = sorted(int(t.find("dco").findtext("parmIndex")) for t in node.find("termList")
                       if t.find("dco") is not None and t.find("dco").findtext("parmIndex"))
        compact = self.COMPACT_FORMS.get(int(rid)) if slots == list(range(1, len(slots) + 1)) else None
        for term in node.find("termList"):
            dco = term.find("dco")
            pidx = aixml2vi.term_slot(dco, node.get("class"))
            tname = self.term_names.get((rid, pidx), "?parm%s" % pidx)
            if compact is not None:
                tname = compact.get(int(dco.findtext("parmIndex") or 0), tname)
            sink = bool(int(term.findtext("objFlags") or 0) & SINK)
            self.term_owner[term.get("uid")] = (el, tname, sink)
        return el

    def call(self, node, parent_uid):
        """A subVI call: target from the diagram's link record naming this node, terminal names
        from the LABELS of the terminal types (a call node's terminals carry the callee's
        labelled pane types)."""
        recs = self.call_targets.get(node.get("uid"), {})
        link = node.find("dynLink")
        if not recs and link is not None:
            # a DYNAMIC DISPATCH call (dynIUse): its DyOM record names the dynLink child
            recs = self.call_targets.get(link.get("uid"), {})
        el = ET.Element("Call", {"uid": node.get("uid"), "uid_parent": parent_uid})
        target = recs.get("PUPV") or recs.get("IUVI") or next(iter(recs.values()), None)
        if target is None:
            self.notes.append("call node %s: no link record names it" % node.get("uid"))
            target = "?"
        elif target.startswith("Instance:") and target[len("Instance:"):] in self.instance_names:
            target = self.instance_names[target[len("Instance:"):]]    # an Express VI
        if recs.get("PUPV") and recs.get("IUVI"):
            # a polymorphic call: NI writes the polymorphic VI as target, the instance it
            # adapted to as instance, and adapt="true"
            el.set("adapt", "true")
            el.set("instance", recs["IUVI"])
        el.set("target", target)
        slots = None
        for i, term in enumerate(node.find("termList")):
            d = term.find("dco")
            tree = self.tid_tree(term)
            if tree is None or tree.get("Type") == "Void":
                continue
            # a typedef'd terminal is labelled on the type it wraps (Read Plot Array.vi's `Plot
            # Property Array` - the XControl sweep, 2026-10-22)
            name = self._field_name(tree)
            if not name:
                # the link's pane type carries no label for this terminal: the CALLEE's own
                # panel names it, by slot (the call's terms stand in pane order, Void for an
                # unconnected slot) - Traverse for GObjects.vi's `Traverse Target`
                if slots is None:
                    f = link_file(self.call_paths.get(node.get("uid")) or
                                  (self.call_paths.get(link.get("uid")) if link is not None else None), self.vi_path)
                    slots = callee_slots(f) if f else {}
                # an EMPTY label is a name too: lvStorage.llb's _L_localizedStrings.vi labels
                # neither terminal, and NI's export writes them as ""
                name = slots[i] if i in slots else "?"
            out = int(d.findtext("objFlags") or 0) & 1
            self.term_owner[term.get("uid")] = (el, name, not out)
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
        set_sublabel(el, inner)
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
            # value="stop" only for Stop if True: the lTst DCO carries 0x10000 then (objFlags 65537),
            # and Continue if True clears it (objFlags 1) - where NI's exporter writes NO value at
            # all. Measured 2026-10-15 on SB_WriteINIVal.vi, picktime.vi and Add Abstract Message
            # to Actor.vi, all three objFlags 1 and value-less in NI's export, against 428 NI
            # "stop" conditions; this wrote "stop" for every loop until then.
            cond = ET.Element("Condition", {"uid": tst.get("uid"), "uid_parent": node.get("uid")})
            if int(tst.findtext("objFlags") or 0) & LOOP_STOP_IF_TRUE:
                cond.set("value", "stop")
            for t in inner_terms.get(tst.get("uid"), []):
                self.term_owner[t.get("uid")] = (cond, "value", True)
            self._pending_children = getattr(self, "_pending_children", [])
        n_in = n_out = 0
        sr = []
        children = []
        rank = self._tunnel_ranks(node, "lpTun")
        for term in node.find("termList"):
            d = self.dco_def(term.find("dco"))
            if d is None:
                continue
            dc = d.get("class")
            if dc == "lpTun":
                out = int(d.findtext("objFlags") or 0) & 1
                if out:
                    n_out += 1
                else:
                    n_in += 1
                tun = ET.Element("Tunnel", {"_id": ("Out%d" % rank.get(term.get("uid"), n_out)) if out
                                            else ("In%d" % rank.get(term.get("uid"), n_in)),
                                            "uid": term.get("uid"), "uid_parent": node.get("uid")})
                tt = (d.findtext("TunnelType") or "").strip()
                if tt and tt.isalnum() and int(tt, 16) == 2 and d.find("innerLpTunDCO") is None:
                    # a CONCATENATING output: no inner DCO, TunnelType 02 (the composer's own
                    # finding, NI's TDMS example) - read as indexing it broke t117's round trip
                    tun.set("mode", "concat")
                elif d.find("innerLpTunDCO") is not None or tt:
                    tun.set("mode", "index")
                self.term_owner[term.get("uid")] = (tun, "value", not out)
                for t in inner_terms.get(d.get("uid"), []):
                    self.term_owner[t.get("uid")] = (tun, "value", bool(out))
                inner_dco = d.find("innerLpTunDCO")
                if inner_dco is not None:
                    for t in inner_terms.get(inner_dco.get("uid"), []):
                        self.term_owner[t.get("uid")] = (tun, "value", bool(out))
                cdco = d.find("LpTunConditionDCO")
                if cdco is not None:
                    # a CONDITIONAL tunnel: the net wired into its condition terminal (inside)
                    # becomes cond="<net>"
                    for t in inner_terms.get(cdco.get("uid"), []):
                        self.term_owner[t.get("uid")] = (tun, "#cond", True)
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
    _event_kinds = None

    @classmethod
    def event_kinds(cls):
        """EventSpec type -> event name: the six above plus every STATIC event the composer knows
        (parts/static-events.json, one type number per name - no two rows share one). Until
        2026-10-11 the reader had only the six, so every mouse, key and graph event read back as
        '?' - NI's Graph Events.vi as six of them in one frame."""
        if cls._event_kinds is None:
            kinds = dict(cls.EVENT_KINDS)
            p = os.path.join(aixml2vi.OWN_PARTS, "static-events.json")
            if os.path.exists(p):
                with open(p, encoding="utf-8") as f:
                    for name, row in json.load(f).items():
                        spec = row.get("spec") if isinstance(row, dict) else None
                        if spec and spec.get("type"):
                            kinds.setdefault(int(spec["type"]), name)
            # and the numbers the installation scan found in LabVIEW's own VIs, named by NI's
            # exporter (scan_static_specs.py, name_static_specs.py) - events the composer cannot
            # write yet (Mouse Down?, Drag Over, Pane Size, ...) but the reader can name
            p = os.path.join(aixml2vi.OWN_PARTS, "event-type-names.json")
            if os.path.exists(p):
                with open(p, encoding="utf-8") as f:
                    for typ, row in json.load(f).items():
                        kinds.setdefault(int(typ), row["name"])
            cls._event_kinds = kinds
        return cls._event_kinds

    def nested_by_ddo(self):
        """uid -> label of every labelled data object of the panel that is not a control of its
        own (a cluster's field, an array inside a cluster)."""
        if getattr(self, "_nested", None) is None:
            self._nested = {}
            parent = {c: p for p in self.b.fp.iter() for c in p}

            def own_label(x):
                pl = x.find("partsList")
                for part in pl if pl is not None else []:
                    if part.get("class") == "label" and part.findtext("partID") == "16" and part.findtext("textRec/text"):
                        lab = unquote(part.findtext("textRec/text")).replace("&#x00;", "").strip()
                        return lab if lab and lab != "[NULL]" else None
                return None
            for e in self.b.fp.iter():
                uid, cls = e.get("uid"), e.get("class")
                if not uid or not cls or cls in ("label", "cosm", "pane", "fPDCO", "Image", "textHair") or uid in self.ctl_by_ddo:
                    continue
                lab = own_label(e)
                if not lab:
                    continue
                # NI's export names it by its PATH from the control that holds it -
                # `"Customized Plot Legend.Color Pair Array": Mouse Down` (2D Error Bar facade)
                path, x = [lab], e
                while x in parent:
                    x = parent[x]
                    if x.get("uid") in self.ctl_by_ddo:
                        path.insert(0, label_of(self.ctl_by_ddo[x.get("uid")].get("_name")).strip())
                        break
                    if x.get("class") not in (None, "pane", "fPDCO") and x.tag in ("ddo", "SL__arrayElement") and own_label(x):
                        path.insert(0, own_label(x))
                self._nested[uid] = ".".join(path)
        return self._nested

    def panes_by_ddo(self):
        """uid -> label of every PANE of the front panel (paneHierarchy, a splitter's panes)."""
        if getattr(self, "_panes", None) is None:
            self._panes = {}
            for e in self.b.fp.iter():
                if e.get("class") == "pane" and e.get("uid"):
                    for part in e.find("partsList") if e.find("partsList") is not None else []:
                        if part.get("class") == "label" and part.findtext("textRec/text"):
                            lab = unquote(part.findtext("textRec/text")).replace("&#x00;", "").strip()
                            if lab and lab != "[NULL]":
                                self._panes[e.get("uid")] = lab
                            break
        return self._panes

    def event_selectors(self, node, n):
        """Frame index -> the AIXML selector of an Event Structure frame, from its EventSpecs:
        ' "Stop": Value Change ', ' Timeout ', ' Panel Close? '."""
        items = {i: [] for i in range(n)}
        # a DYNAMIC event's source is the registration's child its EventSpec's dynIndex (1-based)
        # points at, named by that child's label in angle brackets: `<Tick>: User Event`,
        # `<File>: Mouse Down`, `<Actor.Stopped>: User Event` - the registration is the type of
        # the structure's dynamic event terminal
        dyn = []
        for t in node.find("termList"):
            d = t.find("dco")
            if d is not None and d.get("class") == "eventDynDCO":
                tree = self.tid_tree(t)
                if tree is not None and tree.get("RefType") == "EventReg":
                    dyn = [c.get("Label") or "" for c in aixml2vi.fields(tree)]
                    break
        if node.find("EventNodeEvents") is None:
            # a file from before LabVIEW 2009 SP1 or so keeps the registrations out of the heap,
            # like its constants' values (MGI's Configure Dialog.vi, a LabVIEW 9.0 file)
            self.notes.append("Event Structure %s: no EventNodeEvents in the heap (an old file keeps its "
                              "registrations in compiled code) - its frames name no event" % node.get("uid"))
        for sp in node.findall("EventNodeEvents/SL__arrayElement"):
            di = int(sp.findtext("diagramIdx") or 0)
            kind = self.event_kinds().get(int(sp.findtext("type") or 0), "?")
            ddo = sp.findtext("ddoUID") or "0"
            ctl = self.ctl_by_ddo.get(ddo) if ddo != "0" else None
            # NI's selector names a control TRIMMED: `"Stop Button"` for a label padded with blanks
            src = '"%s"' % label_of(ctl.get("_name")).strip() if ctl is not None else None
            if src is None and ddo != "0" and ddo in self.panes_by_ddo():
                # a PANE's event, named by the pane's label as NI writes it (`"Pane": Mouse Up`,
                # `"Tree pane": Pane Size`) - the 3D Math Plots' facades handle the panel pane's
                # Shortcut Menu Activation? (the XControl sweep, 2026-10-22)
                src = '"%s"' % self.panes_by_ddo()[ddo]
            if src is None and ddo != "0" and ddo in self.nested_by_ddo():
                # an object INSIDE a control - the 2D Math Plots' facades handle Mouse Down on the
                # `Color Pair Array` of their legend cluster - named by its own label
                src = '"%s"' % self.nested_by_ddo()[ddo]
            k = int(sp.findtext("dynIndex") or 0)
            if src is None and 0 < k <= len(dyn) and dyn[k - 1]:
                src = "<%s>" % dyn[k - 1]
            lst = items.setdefault(di, [])
            if src and lst and lst[-1][1] == kind and lst[-1][0]:
                # the same event on the next control: ONE item listing its sources, as NI writes
                # it (' "Numeric Control", "Stop", "Waveform": Mouse Enter ', Handling Mouse
                # Events.vi) and aixml2vi.event_items reads it
                lst[-1][0].append(src)
            else:
                lst.append(([src] if src else [], kind))
        # events are separated by ';' as NI's exporter writes them - a ',' separates SOURCES of
        # one event (aixml2vi.event_items), so joining with ',' made NI's Graph Events.vi frame
        # read back as one source list ending in 'Autoscale Range Change: Cursor Release'
        # ...and ';' with TWO spaces after it (Graph Events.vi, `t125`'s user events)
        # ESCAPED as NI writes a selector: ':' is \3A and ',' is \2C, ';' stays raw - counted over
        # the cached exports, 443 Value Change selectors and every one `"Search"\3A Value Change`.
        # This wrote them raw until 2026-10-15, and lvai_graft_diagram's EventFrames, which matches
        # NI's `\3A`, then found NO frame on the pylabview backend, skipped the re-registration and
        # left the grafted VI eBad ("one or more event cases have no events defined").
        return {i: escape_description(" %s " % ";  ".join(("%s: %s" % (", ".join(src), kind)) if src else kind
                                                          for src, kind in v))
                for i, v in items.items()}

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
        sel_strings = [lvheap.value_text(bytes.fromhex(x.text)) if x.text else ""
                       for x in node.findall("SelectStringArray/SL__arrayElement")]
        sel_tree = None
        frame_els = []
        for i, fr in enumerate(frames):
            attrs = {"selector": "", "uid": fr.get("uid"), "uid_parent": node.get("uid")}
            if not event:
                attrs["selectout"] = ""
            frame_els.append(ET.Element("CaseFrame", attrs))
            set_sublabel(frame_els[-1], fr)
        n_in = n_out = 0
        outer_tunnels = []
        rank = self._tunnel_ranks(node, "selTun")
        for term in node.find("termList"):
            d = self.dco_def(term.find("dco"))
            if d is None:
                continue
            dc = d.get("class")
            if dc == "eventDynDCO":
                # the dynamic event terminals: no AIXML terminal, so their wire ends are dropped -
                # but which net feeds the LEFT one is written after </VI> (`dynreg`), for a
                # registration that reaches the structure with no tunnel of its own
                self.term_owner[term.get("uid")] = IGNORED
                if not int(d.findtext("objFlags") or 0) & 1:
                    self.dyn_terms = getattr(self, "dyn_terms", {})
                    self.dyn_terms[term.get("uid")] = el
                for ts in frame_srn_terms:
                    for t in ts.get(d.get("uid"), []):
                        self.term_owner[t.get("uid")] = IGNORED
                continue
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
                tid = ("Out%d" % rank.get(term.get("uid"), n_out)) if out else ("In%d" % rank.get(term.get("uid"), n_in))
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
        while sel_tree is not None and sel_tree.get("Type") == "TypeDef" and sel_tree.find("TypeDesc") is not None:
            # a typedef'd enum (State Machine Fundamentals' States): its items are inside, and read
            # off the wrapper alone the frames came out numbered "2", "3" where NI writes the items
            sel_tree = sel_tree.find("TypeDesc")
        st = (sel_tree.get("Type") if sel_tree is not None else "") or ""
        labels = [e.text or "" for e in sel_tree.findall("EnumLabel")] if sel_tree is not None else []
        ranges = node.findall("SelectRangeArray32/SL__arrayElement")
        # an ERROR case has two encodings (error_case_survey.py over 73 VIs, no exception): on the
        # STATUS - ranges 0 and 1, "No Error" / "Error" - and on the CODE, marked by INT_MIN or
        # INT_MAX somewhere: "No Error" is INT_MIN..INT_MIN, "Error .." INT_MIN..INT_MAX open at
        # both ends, "Error 1..5" a closed range, "Error 6, 10, 20" three single codes with the
        # prefix written ONCE (Case Structure - Selector Data Types.vi)
        on_code = any(int(r.findtext(k) or 0) in (INT_MIN, INT_MAX) for r in ranges for k in ("start", "end"))
        code_items = {}
        for r in ranges:
            di = int(r.findtext("diagramIdx") or 0)
            s, e = int(r.findtext("start") or 0), int(r.findtext("end") or 0)
            stt, ett = int(r.findtext("startRangeType") or 0), int(r.findtext("endRangeType") or 0)
            if st == "Boolean":
                txt = "True" if s else "False"
            elif st == "Cluster":
                if (on_code and e == INT_MIN) or (not on_code and stt == ett == 0 and s == e == 0):
                    txt = "No Error"
                elif not on_code and stt == ett == 0 and s == e == 1:
                    txt = "Error"
                else:
                    code_items.setdefault(di, []).append(
                        str(s) if stt == ett == 0 else "%s..%s" % (str(s) if stt != 3 else "", str(e) if ett != 3 else ""))
                    continue
            else:
                def val(v):
                    if st == "String":
                        return '"%s"' % esc_case_text(sel_strings[v]) if 0 <= v < len(sel_strings) else str(v)
                    if labels:
                        # QUOTED, as NI's exporter writes an enum item (`"State 1"`, t97)
                        return '"%s"' % esc_case_text(labels[v]) if 0 <= v < len(labels) else str(v)
                    return str(v)
                if stt == 0 and ett == 0:
                    txt = val(s)
                else:
                    txt = "%s..%s" % (val(s) if stt != 3 else "", val(e) if ett != 3 else "")
            items.setdefault(di, []).append(txt)
        for di, ci in code_items.items():
            items.setdefault(di, []).extend(["Error " + ci[0]] + ci[1:])
        if st == "Cluster" and not ranges and len(frames) == 2:
            # an ERROR case may carry no range list either (MGI's Store.vi). It decides on the
            # STATUS, so it reads like a boolean case without one (OpenG's Message and Response
            # Example.vi, below): frame 0 is No Error, frame 1 Error. Read as no selector at all,
            # the composer refused "an integer case on a Cluster wire" (2026-10-22). dIdx is no
            # help - Store.vi's reads 11 for two frames.
            items[0], items[1] = ["No Error"], ["Error"]
        if st == "Boolean" and not ranges and len(frames) == 2:
            # a boolean case may carry NO range list at all: frame 0 is False, frame 1 True in
            # NI's export (OpenG's Message and Response Example.vi)
            items[0], items[1] = ["False"], ["True"]
        dflt = node.findtext("SelectDefaultCase")
        if dflt is None and st not in ("Boolean", "Cluster"):
            # ABSENT means frame 0: LabVIEW writes 01, 02, 03 or FF (no default) and never 00 -
            # NI's Simple Dual Mode Thermometer Facade.vi has none, NI's export calls its frame 0
            # "Default", and read as "no default" the string case composed without one and broke
            # the VI (2026-10-11)
            dflt = "00"
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
        frames_ = list(seql) if seql is not None else []
        for fr in frames_:
            diag = fr.find("diagramList/SL__arrayElement")
            if diag is None:
                continue
            if len(frames_) == 1:
                # ONE frame: NI writes no <Diagram>, the frame's content sits on the structure itself
                # (Simple Serial.vi), which aixml2vi's seq_frame reads as the frame
                d_el, owner = el, node.get("uid")
            else:
                d_el = ET.SubElement(el, "Diagram", {"uid": diag.get("uid"), "uid_parent": node.get("uid")})
                owner = diag.get("uid")
            set_sublabel(d_el, diag)
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
                # seqTun: a tunnel to the OUTSIDE; flatSeqTun: one from frame to frame - its out
                # half on frame k, its in half (0x200800) on frame k+1, joined by a wire on the
                # diagram around the sequence (VI Execution Properties.vi: 9 of them were missing)
                if d is None or d.get("class") not in ("seqTun", "flatSeqTun"):
                    continue
                out = int(d.findtext("objFlags") or 0) & 1
                if out:
                    n_out += 1
                else:
                    n_in += 1
                tun = ET.Element("Tunnel", {"_id": ("Out%d" % n_out) if out else ("In%d" % n_in),
                                            "uid": term.get("uid"), "uid_parent": owner})
                self.term_owner[term.get("uid")] = (tun, "value", not out)
                for t in inner.get(d.get("uid"), []):
                    self.term_owner[t.get("uid")] = (tun, "value", bool(out))
                tunnels.append(tun)
            self.walk(diag, owner, d_el)
            for t in tunnels:
                d_el.append(t)
        return el

    def _inplace_array_pair(self, el, node, l, r, outer):
        """An Array Index / Replace Elements pair, as NI's export writes it (Channel
        Probe_Background Updater.vi): the left node `inputs="array:..,index:.."
        outputs="element:.."`, the right one naming it in _id, `inputs="element:.."
        outputs="array:..,index:.."`. In the heap two decomposeArrayNodes joined by an
        arrayPoserInterface; each has [array, element, index] terms - the element one the
        dcoAgg, inside the structure; array and index reach OUTSIDE through the structure's
        terms, which name their DCO (left: array 0x800000, index 0xE00000; right 0x800001,
        0xE00001)."""
        left = ET.SubElement(el, "Node", {"_name": "Array Index / Replace Elements", "uid": l.get("uid"),
                                          "uid_parent": node.get("uid")})
        right = ET.Element("Node", {"_id": l.get("uid"), "_name": "Array Index / Replace Elements",
                                    "uid": r.get("uid"), "uid_parent": node.get("uid")})
        for half, sink_outer in ((l, True), (r, False)):
            owner = left if half is l else right
            agg = half.findtext("dcoAgg") or (half.find("dcoAgg").get("uid") if half.find("dcoAgg") is not None else None)
            for t in half.find("termList"):
                d = t.find("dco")
                if d is None:
                    continue
                if d.get("uid") == agg:
                    self.term_owner[t.get("uid")] = (owner, "element", not sink_outer)
                    continue
                f = int(d.findtext("objFlags") or 0)
                # the array 0x800000; an index adds its dimension bits - both on a 1D array's
                # (0xE00000), 0x200000 the row and 0x400000 the column of a 2D one (NI's Queue
                # Stack Maze Solver.vi), which NI's export names `index (row)` / `index (col)`
                role = {0x600000: "index", 0x200000: "index (row)", 0x400000: "index (col)"}.get(f & 0x600000, "array")
                o = outer.get(d.get("uid"))
                if o is not None:
                    self.term_owner[o.get("uid")] = (owner, role, sink_outer)
        return right

    def _inplace_cluster_pair(self, el, node, l, r, outer):
        """An Unbundle / Bundle Elements pair, as NI's export writes it (Channel
        Probe_Background Updater.vi): the left node `fields="elt" inputs="input cluster:.."
        outputs="elt:.."`, the right naming it in _id, `inputs="elt:.." outputs="output
        cluster:.."`. In the heap two decomposeClusterNodes joined by a clusterPoserInterface:
        the dcoAgg is the cluster, reaching outside through the structure's term (0x800000 in,
        0x800001 out); each field is a decomposeClusterDCO whose <index> counts like a Bundle By
        Name's <i> (field_paths), absent for 0."""
        left = ET.SubElement(el, "Node", {"_name": "Unbundle / Bundle Elements", "uid": l.get("uid"),
                                          "uid_parent": node.get("uid")})
        right = ET.Element("Node", {"_id": l.get("uid"), "_name": "Unbundle / Bundle Elements",
                                    "uid": r.get("uid"), "uid_parent": node.get("uid")})
        fields = []
        for half, sink_outer in ((l, True), (r, False)):
            owner = left if half is l else right
            agg_el = half.find("dcoAgg")
            agg = agg_el.get("uid") if agg_el is not None else None
            agg_term = next((t for t in half.find("termList") if t.find("dco") is not None
                             and t.find("dco").get("uid") == agg), None)
            ct = self.tid_tree(agg_term.find("dco")) if agg_term is not None else None
            labels = [p for p, _ in aixml2vi.field_paths(ct)] if ct is not None else []
            for t in half.find("termList"):
                d = t.find("dco")
                if d is None:
                    continue
                if d.get("uid") == agg:
                    o = outer.get(d.get("uid"))
                    if o is not None:
                        self.term_owner[o.get("uid")] = (owner, "input cluster" if sink_outer else "output cluster", sink_outer)
                    continue
                # the field is <index> on the DCO (NI's Increase Count.vi: 5 and 4), absent for 0
                i = int(d.findtext("index") or 0)
                nm = labels[i] if i < len(labels) else "?"
                if half is l:
                    fields.append(nm)
                self.term_owner[t.get("uid")] = (owner, nm, not sink_outer)
        left.set("fields", ",".join(escape(x) for x in fields))
        return right

    def inplace(self, node, parent_uid):
        """An In Place Element Structure: one Structure; each in/out pair is two Nodes named
        "In Place In / Out Element", the right one naming the left one in _id. A pair is the
        matchPoserInterface joining two decomposeMatchNodes; a match node's decomposeDCO names
        the structure's OUTER terminal in its termList, its own term is the inner end."""
        el = ET.Element("Structure", {"_name": "In Place Element Structure", "uid": node.get("uid"),
                                      "uid_parent": parent_uid})
        diag = node.find("diagramList/SL__arrayElement")
        set_sublabel(el, diag)
        outer = {}
        for t in (node.find("termList") if node.find("termList") is not None else []):
            d = t.find("dco")
            if d is not None:
                outer[d.get("uid")] = t
        match = {n.get("uid"): n for n in self.node_defs(diag)
                 if n.get("class") in ("decomposeMatchNode", "decomposeArrayNode", "decomposeClusterNode")} if diag is not None else {}
        rights = []
        posers = node.find("multiSegPipeFlange2Size")
        for poser in (posers if posers is not None else []):
            ld, rd = poser.find("multiSegPipeFlange1Depth"), poser.find("multiSegPipeFlange2Depth")
            l = match.get(ld.get("uid")) if ld is not None else None
            r = match.get(rd.get("uid")) if rd is not None else None
            if l is None or r is None:
                self.notes.append("In Place pair %s not read" % poser.get("uid"))
                continue
            if poser.get("class") == "arrayPoserInterface":
                rights.append(self._inplace_array_pair(el, node, l, r, outer))
                continue
            if poser.get("class") == "clusterPoserInterface":
                rights.append(self._inplace_cluster_pair(el, node, l, r, outer))
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
        # plain tunnels: one <Tunnel> with both sides, uid = the outer terminal (NI's export)
        srn = next((n for n in self.node_defs(diag) if n.get("class") == "sRN"), None) if diag is not None else None
        inner_terms = {}
        for t in (srn.find("termList") if srn is not None else []):
            if t.find("dco") is not None:
                inner_terms.setdefault(t.find("dco").get("uid"), []).append(t)
        tunnels, n_in, n_out = [], 0, 0
        for term in (node.find("termList") if node.find("termList") is not None else []):
            dd = term.find("dco")
            if dd is None or dd.get("class") != "decomposeRecomposeTunnel":
                continue
            out = int(dd.findtext("objFlags") or 0) & 1
            n_out, n_in = n_out + out, n_in + (1 - out)
            tun = ET.Element("Tunnel", {"_id": ("Out%d" % n_out) if out else ("In%d" % n_in),
                                        "uid": term.get("uid"), "uid_parent": node.get("uid")})
            self.term_owner[term.get("uid")] = (tun, "value", not out)
            for t in inner_terms.get(dd.get("uid"), []):
                self.term_owner[t.get("uid")] = (tun, "value", bool(out))
            tunnels.append(tun)
        if diag is not None:
            self.walk(diag, node.get("uid"), el)
        for r in rights + tunnels:
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
            set_sublabel(d_els[-1], fr)
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
        rank = self._tunnel_ranks(node, "commentTun")
        for term in (node.find("termList") if node.find("termList") is not None else []):
            d = term.find("dco")
            if d is None or d.get("class") != "commentTun":
                continue
            out = int(d.findtext("objFlags") or 0) & 1
            if out:
                n_out += 1
            else:
                n_in += 1
            tid = ("Out%d" % rank.get(term.get("uid"), n_out)) if out else ("In%d" % rank.get(term.get("uid"), n_in))
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
        if cls == "aReplace":
            # REPLACE ARRAY SUBSET by its DCO flags, which say what each terminal is in any
            # number of element groups: 0x20000 the array, 0x40000 an element, 0x200000 the row
            # index and 0x400000 the column index (both: a 1D array's index), 0x10000 disabled.
            # A fixed-size template named prep.vi's three groups as a 5D array (2026-10-22)
            names = []
            for t in terms:
                f = int(t.findtext("dco/objFlags") or 0)
                if self._is_out(t):
                    nm = "output array"
                elif f & 0x20000:
                    nm = "array"
                elif f & 0x40000:
                    nm = "new element/subarray"
                elif f & 0x600000 == 0x600000:
                    nm = "index"
                elif f & 0x600000:
                    nm = "index (row)" if f & 0x200000 else "index (col)"
                else:
                    nm = "?"
                names.append(("disabled " if f & 0x10000 and nm.startswith("index") else "") + nm)
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
        if el.get("_name") in ("Array Subset", "Replace Array Subset"):
            # NI writes the number of dimensions when it is more than one (dimensions="2"):
            # one index terminal per dimension
            n = sum(1 for t in terms if "index" in (self.term_owner.get(t.get("uid"), (None, ""))[1] or ""))
            if node.findtext("nDims"):
                # several element groups each carry their own indices: the heap says how many
                # dimensions (prep.vi's three groups on a 1D array)
                n = int(node.findtext("nDims"), 16)
            if n > 1:
                el.set("dimensions", str(n))
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
        # <i> counts every element depth-first, a nested cluster's fields included, and NI's
        # export names a nested one by its dotted path (`Description.Name`)
        labels = WAVEFORM_FIELDS if wave else [p for p, _ in (aixml2vi.field_paths(ct) if ct is not None else [])]
        if not wave and ct is not None and aixml2vi.typedef_inner(ct).get("RefType") == "UDClassInst":
            # a CLASS wire inside a member: the fields are the class's private data, named by
            # their paths as NI's export writes them (MGI's Restore.vi: P.Style, P.Size, P.Font)
            try:
                pdata = aixml2vi.class_private_data(aixml2vi.typedef_inner(ct),
                                                    [os.path.dirname(self.vi_path)] if self.vi_path else [])
                labels = [p for p, _ in aixml2vi.field_paths(pdata)]
            except aixml2vi.Unsupported:
                pass
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
            nm = labels[i] if i < len(labels) else None
            if nm is None:
                # a CLASS's private data: the wire is a class refnum with no fields to read, but each
                # element terminal carries the field's own labelled type (`Levels to Spawn`)
                tt = self.tid_tree(t)
                nm = (self._field_name(tt) if tt is not None else None) or "?"
            fields.append(nm)
            self.term_owner[t.get("uid")] = (el, nm, not self._is_out(t))
        el.set("fields", ",".join(escape(x) for x in fields))
        return el

    def mux(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Bundle")
        terms = list(node.find("termList"))
        agg, cref = terms[0], terms[1]
        ct = self.tid_tree(agg)
        # a typedef'd field is labelled on the type it wraps (Control Ref Queue in the Math Plots)
        labels = [self._field_name(c) or "element" for c in (aixml2vi.fields(aixml2vi.typedef_inner(ct)) if ct is not None else [])]
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
        # a typedef'd field is labelled on the type it wraps (Control Ref Queue in the Math Plots)
        labels = [self._field_name(c) or "element" for c in (aixml2vi.fields(aixml2vi.typedef_inner(ct)) if ct is not None else [])]
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
        # a TYPEDEF'd array counts its own dimensions (interpolate_color.vi's 2D colour table)
        at = aixml2vi.typedef_inner(at) if at is not None else None
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

    _LEARNED_CLASSES = None

    @classmethod
    def learned_classes(cls):
        """parts/class-ids-learned.json (learn_node_classes.py): CtlFlags -> class for the classes
        refnum-classes.tsv lacks (XYGraph 0x0053, Scrollbar 0x005A, Pane 0x004B ...), and the
        class of a node with NO reference input by its own name (`WFChart` -> LV.WaveformChart)."""
        if cls._LEARNED_CLASSES is None:
            p = os.path.join(aixml2vi.OWN_PARTS, "class-ids-learned.json")
            cls._LEARNED_CLASSES = json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}
        return cls._LEARNED_CLASSES

    def _hgrow_name(self, term, default):
        """A property or invoke node's fixed terminal by its role (`reference`, `error out`) - but
        a USER-DEFINED refnum's reference terminals by their type's label, as NI's export names
        them: a DAQmx node's `task out` in and out, a System Configuration node's `System
        Hardware` / `System Hardware out`. Not a LabVIEW object refnum's: its label is the
        SOURCE's (`reference out` on an invoke node's input), and NI writes `reference` there -
        taking it broke t91's round trip."""
        if not default.startswith("reference"):
            return default
        tree = self.tid_tree(term)
        if tree is not None and tree.get("RefType") in ("UsrDefndTag", "UsrDefined") and tree.get("Label"):
            return tree.get("Label")
        return default

    def _vi_server_class(self, node):
        """The class NI's export writes on a property or invoke node, read off its reference
        input's refnum - each rule taken from NI's exports by learn_node_classes.py: a user-
        defined TAG refnum is `GenClassTag.<Ident>.<TypeName>` (`GenClassTag.NetworkStream.
        DataExchange`) - but `GenClassTag.<node name>.<TypeName>` for DAQmx (a Task's Trigger
        properties: `GenClassTag.DAQmx Trigger.NIDAQ`), a user-defined refnum
        `GenClass.<Ident>.<TypeName>`, ActiveX `ActiveX`, .NET `DotNet`, .NET Core (refnum type
        39) `DotNetCore`, VISA `Visa.<Ident>`."""
        learned = self.learned_classes()
        node_name = (node.findtext("nodeName") or "").strip('"')
        for t in node.find("termList"):
            d = t.find("dco")
            if d is not None and d.get("class") == "hGrowCItem" and int(d.findtext("objFlags") or 0) == 0x10000:
                tree = self.tid_tree(t)
                if tree is None:
                    return None
                rt = tree.get("RefType")
                if rt == "LVObjCtl":
                    return ref_class_name(tree.get("CtlFlags")) or \
                        learned.get("classes", {}).get((tree.get("CtlFlags") or "").upper())
                if rt == "UDClassInst":
                    return "UDClassInst"
                if rt == "VisaRef":
                    # a VISA session's properties: NI exports the node as type="{Visa.Instr}"
                    # (Simple Serial.vi), the class from the refnum's Ident
                    return "Visa." + (tree.get("Ident") or "Instr")
                if rt == "UsrDefndTag" and aixml2vi.tag_class(tree, node_name):
                    return aixml2vi.tag_class(tree, node_name)     # DAQmx by node, else by Ident
                if rt == "UsrDefined" and tree.get("Ident") and tree.get("TypeName"):
                    return "GenClass.%s.%s" % (tree.get("Ident"), tree.get("TypeName"))
                return {"AutoRef": "ActiveX", "DotNet": "DotNet", "39": "DotNetCore"}.get(rt)
        # no reference input: an implicit node on a control, This VI, the application
        return learned.get("nodeNames", {}).get(node_name.replace(" (strict)", ""))

    _PROP_NAMES = None
    _EXPORT_PROP_NAMES = None

    @classmethod
    def export_prop_names(cls):
        """property code path ('104013829' or 'a/b' for a nested one) -> the name NI's exporter
        writes for it, learned off its exports of whole VIs (parts/property-export-names.json,
        learn_prop_names.py) - only codes every pair agreed on."""
        if cls._EXPORT_PROP_NAMES is None:
            p = os.path.join(aixml2vi.OWN_PARTS, "property-export-names.json")
            cls._EXPORT_PROP_NAMES = json.load(open(p, encoding="utf-8")).get("names", {}) if os.path.exists(p) else {}
        return cls._EXPORT_PROP_NAMES

    @classmethod
    def catalogue_name(cls, klass, name, code):
        """The property name as NI's export writes it. The heap keeps the name the node was
        saved with - "Diagram", "Panel", "FP.Open" - while LabVIEW's export writes the long
        catalogue name: 18 "Block Diagram" against 0 "Diagram" on {LV.VI} in the cache, the
        heap of NI's Using Traverse.vi saying "Diagram". A name the catalogue does not list for
        the class is replaced by the one catalogued name harvested with the same code."""
        if cls._PROP_NAMES is None:
            cat = set()
            with open(os.path.join(aixml2vi.DOCS, "vi-server-properties.tsv"), encoding="utf-8") as f:
                for line in f:
                    row = line.rstrip("\r\n").split("\t")
                    if len(row) > 1 and row[0].startswith("{LV."):
                        cat.add((row[0][1:-1], row[1]))
            path = os.path.join(aixml2vi.OWN_PARTS, "vi-server-props.json")
            props = json.load(open(path, encoding="utf-8"))["props"] if os.path.exists(path) else {}
            by_code = {}
            for k, rows in props.items():
                for n, r in rows.items():
                    if (k, n) in cat:
                        by_code.setdefault((k, r.get("code")), set()).add(n)
            cls._PROP_NAMES = (cat, by_code)
        cat, by_code = cls._PROP_NAMES
        if (klass, name) in cat or code is None:
            return name
        names = by_code.get((klass, code), set())
        return next(iter(names)) if len(names) == 1 else name

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
                    self.term_owner[t.get("uid")] = (el, self._hgrow_name(t, role[0]), role[1])
            elif d.get("class") == "propItem":
                infos = d.findall("propList/SL__arrayElement")
                name = ".".join(unquote(i.findtext("PropItemName") or "") for i in infos)
                code = infos[-1].findtext("PropItemCode") if infos else None
                codepath = "/".join(i.findtext("PropItemCode") or "?" for i in infos)
                self.prop_items.setdefault(node.get("uid"), []).append((cls, codepath))
                # a small code is unique only within its class (a DAQmx Timing property's 12760):
                # learned under "<class>|<codes>", a LabVIEW one under its codes alone
                learned = self.export_prop_names().get(codepath) or \
                    self.export_prop_names().get("%s|%s" % (cls, codepath))
                if learned:
                    # the name NI's exporter writes for these codes, learned off its own exports
                    # (learn_prop_names.py): `HasConditionalTerm` is `Has Conditional Terminal?`
                    name = learned
                else:
                    name = self.catalogue_name(cls, name, int(code) if code and code.lstrip("-").isdigit() else 0)
                out = bool(int(d.findtext("objFlags") or 0) & 1)
                fields.append(("read+" if out else "write+") + escape(name))
                self.term_owner[t.get("uid")] = (el, name, not out)
        el.set("fields", ",".join(fields))
        ddo = node.find("ddo")
        if ddo is not None and ddo.get("uid") in self.ctl_by_ddo:
            el.set("link", label_of(self.ctl_by_ddo[ddo.get("uid")].get("_name")))
        el.set("type", "{%s}" % cls)
        return el

    def eventreg(self, node, parent_uid):
        """Register For Events (eventRegNode): built like a property node - four fixed hGrowCItem
        terminals (registration refnum in and out, error in and out) - plus one eventRegItem per
        event source. Its inputs are written in NI's order, `event registration refnum:`, `error
        in (no error):`, then one `event source:` per item IN ITEM ORDER, because the composer
        pairs the i-th source with the i-th item (aixml2vi.add_event_reg). It was not read at all,
        which dropped every user event's wiring and failed the round trip of t118-t125."""
        el = self._node_el(node, parent_uid, "Register For Events")
        roles = {0x10000: ("event registration refnum", True), 0x40001: ("event registration refnum", False),
                 0x20000: ("error in (no error)", True), 0x80001: ("error out", False)}
        head, sources = [], []
        for t in node.find("termList"):
            d = t.find("dco")
            if d is None:
                continue
            if d.get("class") == "hGrowCItem":
                role = roles.get(int(d.findtext("objFlags") or 0))
                if role:
                    self.term_owner[t.get("uid")] = (el, role[0], role[1])
                    if role[1]:
                        head.append((t.get("uid"), role[0]))
            elif d.get("class") == "eventRegItem":
                self.term_owner[t.get("uid")] = (el, "event source", True)
                sources.append((t.get("uid"), "event source"))
        head.sort(key=lambda x: 0 if x[1] == "event registration refnum" else 1)
        self.ordered_inputs[id(el)] = (el, head + sources)
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
                    self.term_owner[t.get("uid")] = (el, self._hgrow_name(t, role[0]), role[1])
            elif d.get("class") == "invokeItem":
                tree = self.tid_tree(t)
                if tree is None or tree.get("Type") == "Void":
                    continue
                out = bool(int(d.findtext("objFlags") or 0) & 1)
                # a typedef'd parameter is labelled on the type it wraps (Rotate's Axis on a
                # SceneObject, the 3D Picture Control's VIs - the XControl sweep, 2026-10-22)
                self.term_owner[t.get("uid")] = (el, self._field_name(tree) or "?", not out)
        return el

    @staticmethod
    def _field_name(e):
        """A cluster field's label - Source and Type are TYPEDEFS labelled on the wrapped type."""
        inner = e.find("TypeDesc") if e.get("Type") == "TypeDef" else None
        return e.get("Label") or (inner.get("Label") if inner is not None else None)

    def eventdata(self, node, parent_uid):
        # an Event Filter Node is an eventDataNode too, without the data node's 0x10000 (NI's
        # Drag and Drop example: 0x150000 against 0x140000) - its fields are INPUTS, handed back
        # to LabVIEW (Accepted?, Discard?), and its aggregate has a second, DCO-less terminal
        flt = not int(node.findtext("objFlags") or 0) & 0x10000
        el = self._node_el(node, parent_uid, "Event Filter Node" if flt else "Event Data Node")
        fields = []
        terms = list(node.find("termList"))
        agg = self.tid_tree(terms[0]) if terms else None
        elems = [c for c in agg if c.tag == "TypeDesc"] if agg is not None else []
        for t in terms[1:]:
            if t.find("dco/typeDesc") is None:
                continue
            tree = self.tid_tree(t)
            nm = self._field_name(tree) if tree is not None else None
            if not nm and tree is not None:
                # LabVIEW's own save may type a field terminal WITHOUT a label (NI's Graph
                # Events.vi: Type). Named by the one cluster field of that type; <i> is NOT the
                # position - LabVIEW numbers drag fields 17 and 18 at positions 8 and 9 - so it
                # is only a tie-breaker, and only where the field there has that type
                sig = repr(aixml2vi.type_sig(tree))
                same = [e for e in elems if repr(aixml2vi.type_sig(e)) == sig]
                idx = t.findtext("dco/i")
                if len(same) == 1:
                    nm = self._field_name(same[0])
                elif idx is not None and idx.strip().isdigit() and int(idx) < len(elems) and elems[int(idx)] in same:
                    nm = self._field_name(elems[int(idx)])
            nm = nm or "?"
            fields.append(nm)
            self.term_owner[t.get("uid")] = (el, nm, flt)
        el.set("fields", ",".join(escape(x) for x in fields))
        return el

    def statvi(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "Static VI Reference")
        recs = self.call_targets.get(node.get("uid"), {})
        el.set("target", recs.get("SVVI") or next(iter(recs.values()), "?"))
        if int(node.findtext("objFlags") or 0) & 0x10000000:
            # strictly typed: the refnum carries the callee's pane (NI exports strict="true")
            el.set("strict", "true")
        for t in node.find("termList"):
            self.term_owner[t.get("uid")] = (el, "value", False)
        return el

    def ctlref(self, node, parent_uid):
        el = self._node_el(node, parent_uid, "VI Server Reference")
        ddo = node.find("ddo")
        if ddo is not None:
            ctl = self.ctl_by_ddo.get(ddo.get("uid"))
            pane = self.panes_by_ddo().get(ddo.get("uid")) if ctl is None else None
            if pane is not None:
                # a reference to a PANE, named by its label as NI names a pane's event source
                el.set("element", escape(pane))
                name = pane
            else:
                el.set("element", ctl.get("_name") if ctl is not None else "?")   # escaped, as NI writes it
                name = label_of(ctl.get("_name")) if ctl is not None else "?"
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
        if name == "?" and node.find("connectorTM") is None and t is not None:
            # a LOCAL whose type carries no label names its control by paramIdx: the index among
            # the panel's controls (fPDCO order) - make_plot_contour.vi's paramIdx 6 is its 7th,
            # `Contour Array`
            # ...counted in the panel's ddoList, OLDEST first (the zPlane order is newest first:
            # it named make_plot_contour.vi's paramIdx 6 `Contour Array` where it is the cluster
            # `Contour Properties`)
            i = int(t.findtext("dco/paramIdx") or 0)
            fps = {e.get("uid"): e for e in self.b.fp.iter("SL__arrayElement") if e.get("class") == "fPDCO"}
            dl = next(self.b.fp.iter("ddoList"), None)
            order = [e.get("uid") for e in dl] if dl is not None else []
            f = fps.get(order[i]) if i < len(order) else None
            if f is not None and f.find("ddo") is not None:
                name = label_text(f.find("ddo")) or "?"
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

    # A primitive saved in an OLDER, COMPACT layout - parmIndex 0..n-1 where the name tables know
    # the current pane's slots: Variant To Data (8003) in MGI's Restore.vi is a 5-slot icon,
    # variant and type on top beside data, error in and out below (read off the wires: the
    # error cluster into 0, the variant into 2, the type constant into 3). The composer writes
    # the current form, which names the same five terminals.
    COMPACT_FORMS = {8003: {0: "error in", 1: "error out", 2: "Variant", 3: "type", 4: "data"}}

    # Call By Reference's own four terminals, by DCO objFlags (Gaussian surface data.vi)
    CBR_OWN = {0x10000: ("reference", True), 0x40001: ("reference out", False),
               0x20000: ("error in (no error)", True), 0x80001: ("error out", False)}

    def callbyref(self, node, parent_uid):
        """CALL BY REFERENCE, as NI's export writes it: the node's own reference and error
        terminals plus one per slot of the CALLEE's pane, named by that slot's label in the
        node's connectorTM (paramIdx = the slot) - and that pane as type="function{..}", NI's
        own literal, from which the composer builds the node and the strict VI refnum feeding it."""
        m = lvheap.TYPEID_RE.fullmatch((node.findtext("connectorTM") or "").strip())
        pane = self.b.heap_type_tree(int(m.group(1))) if m else None
        el = self._node_el(node, parent_uid, "Call By Reference", type=aixml_type(pane) or "")
        slots = [c for c in pane if c.tag == "TypeDesc"] if pane is not None else []
        for t in node.find("termList"):
            d = t.find("dco")
            f = int(d.findtext("objFlags") or 0)
            if d.get("class") == "hGrowCItem":
                nm, sink = self.CBR_OWN.get(f, ("?", not self._is_out(t)))
            else:
                i = int(d.findtext("paramIdx") or 0)
                nm = (slots[i].get("Label") if i < len(slots) else None) or "?"
                sink = not self._is_out(t)
            self.term_owner[t.get("uid")] = (el, nm, sink)
        return el

    def scriptnode(self, node, parent_uid):
        """A SCRIPT NODE (MATLAB script): a Formula Node's shape - variables on the border named by
        vblName, the code in `formula` - plus an error in and out (vblName "??") and a script
        server (scriptName). Its outputs' types are the user's choice, declared nowhere in the
        code, so the reader hands them on after </VI> (`scripttypes`)."""
        code = unquote(node.findtext("formula/text") or "")
        kind = unquote(node.findtext("scriptName/text") or "") or "MATLAB script"
        el = self._node_el(node, parent_uid, kind, text=escape_value(code))
        types = []
        for t in node.find("termList"):
            nm = unquote(t.findtext("dco/vblName/text") or "")
            out = self._is_out(t)
            if nm == "??":
                nm = "error out" if out else "error in"
            elif out:
                lt = aixml_type(self.tid_tree(t))
                if lt:
                    types.append("%s:%s" % (escape(nm), lt))
            self.term_owner[t.get("uid")] = (el, nm, not out)
        if types:
            self.script_types = getattr(self, "script_types", [])
            self.script_types.append((el, types))
        return el

    def decimate(self, node, parent_uid):
        """DECIMATE 1D ARRAY: its array input and one output per element of the cycle, named as
        LabVIEW's help names them - `elements at index 0`, `elements at index 1`, ... - in
        terminal order (make_plot_contour.vi, three outputs)."""
        el = self._node_el(node, parent_uid, "Decimate 1D Array")
        k = 0
        for t in node.find("termList"):
            if self._is_out(t):
                self.term_owner[t.get("uid")] = (el, "elements at index %d" % k, False)
                k += 1
            else:
                self.term_owner[t.get("uid")] = (el, "array", True)
        return el

    def fbox(self, node, parent_uid):
        """A FORMULA NODE (fBox), as NI's export writes it - a Node whose terminals are named by
        their variables (a variable both read and written appears in inputs AND outputs) - plus
        the CODE as text=, which NI's export leaves out: without it a composed Formula Node is
        empty and its outputs never written. text= is the attribute an Expression Node carries,
        so the document stays inside NI's schema."""
        code = unquote(node.findtext("formula/text") or "")
        el = self._node_el(node, parent_uid, "Formula Node", text=escape_value(code))
        for t in node.find("termList"):
            nm = unquote(t.findtext("dco/vblName/text") or "")
            self.term_owner[t.get("uid")] = (el, nm, not self._is_out(t))
        return el

    # ---- wiring --------------------------------------------------------------------------------
    def wire(self, diag):
        sl = diag.find("signalList")
        for sig in (sl if sl is not None else []):
            # an Event Structure's DYNAMIC event terminal is no AIXML terminal: NI's export simply
            # leaves that end out of the registration's net, and so does the reader (IGNORED)
            ends = [e for e in (self.term_owner.get(t.get("uid"), MISSING) for t in sig.find("termList"))
                    if e is not IGNORED]
            ends = [None if e is MISSING else e for e in ends]
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
                st = getattr(self, "dyn_terms", {}).get(tu.get("uid"))
                if st is not None:
                    self.dyn_nets = getattr(self, "dyn_nets", {})
                    self.dyn_nets[st.get("uid")] = net
            for el, term, sink in ends:
                if sink:
                    if term == "#maxin":
                        el.set("maxin", net)
                    elif term == "#selectin":
                        el.set("selectin", net)
                    elif term == "#cond":
                        el.set("cond", net)
                    else:
                        self._add(el, "inputs", term, net)
        return None

    @staticmethod
    def _add(el, attr, term, net):
        cur = el.get(attr)
        item = "%s:%s" % (escape(term), net)
        el.set(attr, item if not cur else cur + "," + item)

    def description_text(self):
        """The VI's description from STRG. pylabview leaves that block BINARY for some VIs - 9 of
        300 cached NI examples, MGI's among them - as a 4-byte big-endian length and the text
        (LVblock's STRG parseRSRCSectionData)."""
        sec = self.b.main.find("STRG/Section")
        if sec is None:
            return ""
        if sec.get("Format") == "bin" and sec.get("File"):
            data = open(os.path.join(self.b.dir, sec.get("File")), "rb").read()
            n = int.from_bytes(data[:4], "big") if len(data) >= 4 else 0
            return data[4:4 + n].decode("mac_roman")
        return string_block_text(sec.find("String"))

    def pane_flags(self):
        """conIdx (as text) -> connection, read off the pane's Function type.

        0x8000 is a DYNAMIC DISPATCH terminal (Infra/PaneDispatch.cs, checked against NI's own
        IsStaticMethod) and NI's exporter writes it `dynamic` whatever wire rule sits beside it.
        This read it as `required` / `recommended` until 2026-10-12, so lvai_vi_terminals on a
        class accessor answered `Sensor in ... required` where NI's export says `dynamic`, and
        a method test could not tell the dispatch input among several class-typed ones.

        A .ctl LabVIEW has saved points CONP at its TypeDef, not at a Function, and has no pane:
        reading that TypeDef's children as terminals failed on 119 of 121 controls (2026-10-14).
        NI's export then gives the control conIdx 0 `required` when its conNum is the heap's
        default 0 (3 of 46 controls compared) and nothing when it is -1 (the other 43).
        """
        out = {}
        cons = int(self.b.main.find("CONP/Section/TypeDesc").get("TypeID"))
        func = self.b.flat[self.b.flat_of_consolidated(cons)]
        if func.get("Type") != "Function":
            return {"0": "required"}
        for i, c in enumerate([c for c in func if c.tag == "TypeDesc"]):
            f = int(c.get("Flags"), 16)
            if c.get("TypeID") is not None and f:
                out[str(i)] = ("dynamic" if f & 0x8000 else "required" if f & 0x1000
                               else "recommended" if f & 0x0800 else "optional")
        return out


def main(argv):
    src = argv[0]
    tmp = None
    if src.lower().endswith(".xml"):
        main_xml = src
    else:
        # a VI INSIDE an .llb container file, its path running through the .llb as LabVIEW writes
        # it: read from the member resolve.py extracted. vi_path below stays the virtual path, so
        # the VI's relative links still resolve from where LabVIEW sees it
        real = src
        if not os.path.exists(src):
            import resolve
            real = resolve.materialize(src)
            if real is None:
                split = resolve.llb_split(src)
                sys.exit("No VI at '%s'%s" % (src, "" if split is None else
                         ": the .llb container '%s' holds no member '%s'" % split))
        tmp = tempfile.mkdtemp(prefix="vi2aixml_")
        main_xml = lvheap.extract(real, tmp)
    try:
        reader = Reader(main_xml, vi_path=None if src.lower().endswith(".xml") else os.path.abspath(src))
        root = reader.read()
    finally:
        if tmp:
            shutil.rmtree(tmp, ignore_errors=True)
    escape_illegal_xml_chars(root)
    ET.indent(root, "  ")
    text = ET.tostring(root, encoding="unicode")
    pane = getattr(reader, "pane_id", None)
    if pane is not None:
        # the connector pane PATTERN, which AIXML has no attribute for: it is part of a subVI's
        # link binding, so a VI recomposed on the station default broke every caller of NI's
        # original (`Bad linkage to subVI`, NI's Dual Mode Thermometer facade over C to F.vi,
        # 4815 recomposed as 4833 - the XControl sweep, 2026-10-22). AFTER </VI>, the one place
        # NI's service ignores (docs/aixml-frontpanel-extension.md); the composer reads it.
        text += '\n<?lvmcp pane="%d"?>' % pane
    dyn = [e.get("uid") for e in getattr(reader, "dynamic_fp", []) if e.get("uid")]
    if dyn:
        text += '\n<?lvmcp dynamic uids="%s"?>' % ",".join(dyn)
    flags = getattr(reader, "pane_slot_flags", None)
    if flags:
        text += '\n<?lvmcp paneflags="%s"?>' % ",".join("%d:%04X" % kv for kv in sorted(flags.items()))
    for el, names in getattr(reader, "class_refs", []):
        # the CLASS of each ref{UDClassInst} in that element's type, in the order the type writes
        # them - AIXML names none, and without it no class-typed terminal could be composed
        if el.get("uid"):
            text += '\n<?lvmcp classes uid="%s" names="%s"?>' % (el.get("uid"), html.escape("|".join(names), quote=True))
    for el in root.iter("Call"):
        # the FILE a call links to, as its link record holds it (TpVal 0: from <vilib>, <userlib>
        # ...; 1: relative to the caller) - a bare `target` is a name only, and MGI's Set Combobox
        # Value.vi calls ITS Compare Two Paths.vi, which the composer otherwise resolved to NI's
        lp = reader.call_paths.get(el.get("uid") or "")
        if lp and lp[1]:
            text += '\n<?lvmcp calllink uid="%s" tp="%d" path="%s"?>' % (
                el.get("uid"), lp[0], html.escape("|".join(lp[1]), quote=True))
    for uid, net in sorted(getattr(reader, "dyn_nets", {}).items()):
        # the net wired into an Event Structure's dynamic event terminal, which NI's export leaves
        # out: with no tunnel carrying the registration the composer has nothing else to go on
        text += '\n<?lvmcp dynreg uid="%s" net="%s"?>' % (uid, html.escape(net, quote=True))
    for el, types in getattr(reader, "script_types", []):
        # a script node's OUTPUT types, which its code does not declare
        if el.get("uid"):
            text += '\n<?lvmcp scripttypes uid="%s" types="%s"?>' % (el.get("uid"), html.escape("|".join(types), quote=True))
    for el, items in getattr(reader, "xctl_refs", []):
        # the XCONTROL each ref{LV.<Class>} of that element's type is typed to, '' for a plain one
        if el.get("uid"):
            text += '\n<?lvmcp xctlrefs uid="%s" items="%s"?>' % (el.get("uid"), html.escape("|".join(items), quote=True))
    if len(argv) > 1:
        open(argv[1], "w", encoding="utf-8").write(text + "\n")
    else:
        print(text)


if __name__ == "__main__":
    main(sys.argv[1:])
