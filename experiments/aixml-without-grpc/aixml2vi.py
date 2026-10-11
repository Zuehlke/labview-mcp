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
import re
import shutil
import struct
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # the bundle's ._pth leaves it off
import lvheap  # noqa: E402
import resolve  # noqa: E402

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
    "extended": ("NumFloatExt", "stdNum"), "complexsingle": ("NumComplex64", "stdNum"),
    "complexdouble": ("NumComplex128", "stdNum"), "complexextended": ("NumComplexExt", "stdNum"),
    "variant": ("LVVariant", "stdLvVariant"),
    # a timestamp and a waveform are MeasureData with a Flavor (measured on an AIXML-generated VI)
    "timestamp": ("MeasureData.TimeStamp", "absTime"),
    "doublewaveform": ("MeasureData.Float64Waveform", "stdMeasureData"),
}
# type key (lvheap.type_key) -> the front-panel class of its object
DDO_OF_KEY = {v[0]: v[1] for v in SCALARS.values()}
DDO_OF_KEY.update({"Refnum.LVObjCtl": "stdRefNum", "Refnum.VisaRef": "stdRefNum", "Refnum.UsrDefndTag": "stdRefNum",
                   "Picture": "stdPict", "MeasureData.Dynamicdata": "stdMeasureData"})
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


_CALLEES = {}
_CALLEE_DIRS = []


def _cleanup_callees():
    import shutil
    for d in _CALLEE_DIRS:
        shutil.rmtree(d, ignore_errors=True)


import atexit  # noqa: E402
atexit.register(_cleanup_callees)


def _extract_callee(path):
    """A callee's bundle, extracted once per process - a coverage run meets the same vi.lib VIs
    hundreds of times. A callee pylabview cannot read in 30 s (several Wovalab VIs hang it) is
    refused, and remembered as refused."""
    key = os.path.normcase(os.path.abspath(path))
    got = _CALLEES.get(key)
    if got is None:
        try:
            d = tempfile.mkdtemp(prefix="callee_")
            _CALLEE_DIRS.append(d)
            got = lvheap.extract(path, d, timeout=30)
        except RuntimeError as ex:
            got = ex
        _CALLEES[key] = got
    if isinstance(got, Exception):
        raise Unsupported("callee %s cannot be read: %s" % (os.path.basename(path), str(got).splitlines()[0][:120]))
    return got


def split_top(s, sep=","):
    """Split at `sep` outside braces and brackets."""
    out, depth, cur = [], 0, []
    for ch in s:
        if ch in "{[":
            depth += 1
        elif ch in "}]":
            depth -= 1
        if ch == sep and depth == 0:
            out.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    out.append("".join(cur))
    return out


def split_field(s):
    """'array.2{double}.Readings' -> ('array.2{double}', 'Readings'); 'double' -> ('double', None).
    The type is read greedily - a word, an optional .N after `array`, an optional {...} - and
    whatever follows a '.' is the label, which may itself contain anything."""
    m = re.match(r"[A-Za-z0-9_]+", s)
    if not m:
        raise Unsupported("unreadable type %r" % s)
    i = m.end()
    if m.group(0) == "array":
        m2 = re.match(r"\.(\d+)(?=\{)", s[i:])
        if m2:
            i += m2.end()
    if i < len(s) and s[i] == "{":
        depth = 0
        for j in range(i, len(s)):
            depth += {"{": 1, "}": -1}.get(s[j], 0)
            if depth == 0:
                i = j + 1
                break
    rest = s[i:]
    if rest and not rest.startswith("."):
        raise Unsupported("unreadable type %r" % s)
    return s[:i], (unescape(rest[1:]) if rest else None)


ENUM_BASE = {"uint8": "UnitUInt8", "uint16": "UnitUInt16", "uint32": "UnitUInt32"}


def type_tree(aixml_type, label=None, constant=False):
    """AIXML's type grammar -> an inlined TypeDesc tree as VCTP writes it:
    scalars, cluster{T.name,...}, array{T} / array.N{T}, enums uintN{a,b,c}."""
    aixml_type = aixml_type.strip()
    m = re.match(r"([A-Za-z0-9_]+)(?:\.(\d+))?\{(.*)\}$", aixml_type, re.S)
    if m and m.group(1) == "cluster":
        el = ET.Element("TypeDesc", {"Type": "Cluster", "Format": "inline"})
        for item in split_top(m.group(3)):
            t, lab = split_field(item)
            el.append(type_tree(t, lab))
    elif m and m.group(1) == "array":
        el = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
        for _ in range(int(m.group(2) or 1)):
            ET.SubElement(el, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
        t, lab = split_field(m.group(3))
        el.append(type_tree(t, lab))
    elif m and m.group(1) in ENUM_BASE and m.group(2) is None:
        # an enum is the unit-typed integer with one EnumLabel per item (measured: UnitUInt16
        # with Off/On/Auto for uint16{Off,On,Auto})
        el = ET.Element("TypeDesc", {"Type": ENUM_BASE[m.group(1)], "Prop1": "0", "Format": "inline"})
        for item in split_top(m.group(3)):
            ET.SubElement(el, "EnumLabel").text = unescape(item)
    elif m and m.group(1) == "ref" and m.group(2) is None:
        # A CONSTANT of a control class (ref{LV.Cluster} as To More Specific Class' target) is a
        # PLAIN refnum: measured on NI's converter's own build of lvai_typedef_bind.xml, the type is
        # Refnum LVObjCtl CtlFlags 0x001E with nothing nested and the heap object an ordinary
        # stdRefNum - the nested object refnum-classes.tsv records belongs to front-panel CONTROLS.
        el = refnum_type(m.group(3), plain_class=constant)
    elif m and m.group(1) == "tag" and m.group(2) is None:
        # tag{N}: an I/O name control - N is pylabview's TAG_TYPE (13 a DAQmx terminal, 14 a
        # DAQmx physical channel, 2 the old DAQ channel), read off an AIXML-generated probe
        name = TAG_TYPES.get(m.group(3).strip())
        if name is None:
            raise Unsupported("type %r is not composable yet" % aixml_type)
        el = ET.Element("TypeDesc", {"Type": "Tag", "Prop1": "0xFFFFFFFF", "TagType": name, "Format": "inline"})
        el.append(_void_variant())
    elif aixml_type == "picture":
        el = ET.Element("TypeDesc", {"Type": "Picture", "Prop1": "0xFFFFFFFF", "Format": "inline"})
    elif aixml_type == "dynamicdata":
        el = ET.Element("TypeDesc", {"Type": "MeasureData", "Flavor": "Dynamicdata", "Format": "inline"})
    elif aixml_type in SCALARS:
        t, _ = SCALARS[aixml_type]
        if t.startswith("MeasureData."):
            el = ET.Element("TypeDesc", {"Type": "MeasureData", "Flavor": t.split(".", 1)[1], "Format": "inline"})
        else:
            el = ET.Element("TypeDesc", {"Type": t})
            if t not in ("Boolean", "LVVariant"):
                el.set("Prop1", "0xFFFFFFFF" if t in ("String", "Path") else "0")
            el.set("Format", "inline")
    else:
        raise Unsupported("type %r is not composable yet" % aixml_type)
    if label:
        el.set("Label", label)
    return el


_REF_CLASSES = None
EMPTY_STR_SECTION = ('<Section Index="0" Format="inline"><LVVariant HasVarItem2="1" VarType2="0" '
                     'Version="auto"><DataType Index="0" Type="Void" Format="inline" />'
                     '<DataFill><Void /></DataFill></LVVariant></Section>')
TAG_TYPES = {"1": "SharedVarCtl", "2": "DAQChannelOld", "3": "IVILogicalName", "4": "VISArsrcName",
             "5": "UserDefined", "7": "FldPointIOPoint", "8": "MotionResource", "9": "DAQmxTaskName",
             "10": "DAQmxChannel", "11": "DAQmxScaleName", "12": "DAQmxDeviceName", "13": "DAQmxTerminal",
             "14": "DAQmxPhysChannel", "16": "DAQmxSwitch"}


def _void_variant():
    v = ET.Element("LVVariant", {"Index": "0", "HasVarItem2": "1", "VarType2": "0", "Version": "auto"})
    ET.SubElement(v, "DataType", {"Index": "0", "Type": "Void", "Format": "inline"})
    return v


_CONTROL_EVENTS = None


def control_events():
    """'<VI Server class>|<event>' -> what LabVIEW wrote for that event registered DYNAMICALLY on
    a reference of that class, in its own saves: the registration item's code (its CField2 and
    CField4), the item dco's objFlags, the frame's EventSpec fields and the event's data cluster.
    parts/control-events.json, built by build_event_kinds.py over scan_event_regs.py's survey of
    the installation (2026-10-10). The code is not derivable - Picture's Mouse Down is 0C663C09,
    its Mouse Up 0C663C02 - so a pair the installation never saved is refused by name."""
    global _CONTROL_EVENTS
    if _CONTROL_EVENTS is None:
        _CONTROL_EVENTS = {}
        p = os.path.join(HERE, "parts", "control-events.json")
        if os.path.exists(p):
            with open(p, encoding="utf-8") as f:
                _CONTROL_EVENTS = json.load(f)
    return _CONTROL_EVENTS.get("kinds", {})


_STATIC_EVENTS = None


def static_event(event, ctl=None):
    """parts/static-events.json's row for a STATIC event beyond EVENT_SPECS - its EventSpec and the
    data fields after the reference, read off NI's own VIs by static_event_survey.py
    (build_static_events.py). None when the survey never saw it. An event of the VI that a CONTROL
    has too under the same name (Key Down?) carries `controlVariant`, used when the selector names
    a source `ctl` (add_key_events.py, measured on LabVIEW's own save)."""
    global _STATIC_EVENTS
    if _STATIC_EVENTS is None:
        p = os.path.join(HERE, "parts", "static-events.json")
        _STATIC_EVENTS = json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}
    r = _STATIC_EVENTS.get(event)
    if r is not None and ctl is not None and r.get("sourceKind") != "control" and r.get("controlVariant"):
        r = dict(r, **r["controlVariant"])
        r.pop("controlVariant", None)
    elif r is not None and ctl is None and r.get("sourceKind") == "control" and r.get("viVariant"):
        # the other way round: a control's event the VI has too (the notify Key Down, Key Repeat,
        # Key Up - add_key_notify.py), used when the selector names no source
        r = dict(r, **r["viVariant"])
        r.pop("viVariant", None)
    return r


def control_event_row(cls, event):
    """The measured row for `event` registered on a reference of class `cls`: its own, or one read
    on ANOTHER class when the code's family (its CField2) is declared by `cls` or an ancestor of
    it. 0C66 is LV.Control's - Value Change read on an LV.Control reference itself, and the same
    0C663C00 on Array and Digital - so a Boolean's Mouse Down is the Picture's 0C663C09. The
    hierarchy is LabVIEW's own: a refnum's CtlFlags is the class id, docs/vi-server-classes.tsv the
    parent by id (build_event_kinds.py). None when nothing applies."""
    kinds = control_events()
    row = kinds.get("%s|%s" % (cls, event))
    if row is not None:
        return row
    parents, declared = _CONTROL_EVENTS.get("parents", {}), _CONTROL_EVENTS.get("declaredBy", {})
    line, c = [cls], cls
    while c in parents and parents[c] not in line:
        c = parents[c]
        line.append(c)
    # a row read on the nearest ANCESTOR first (LV.Control's own Value Change for a Path), then
    # one read on another descendant of the declaring class (a Picture's Mouse Down for a Path)
    hits = [(line.index(key.split("|")[0]) if key.split("|")[0] in line else len(line), key, r)
            for key, r in kinds.items()
            if key.split("|", 1)[1] == event and len(r["code"]) == 8 and declared.get(r["code"][:4]) in line]
    return min(hits, key=lambda h: (h[0], h[1]))[2] if hits else None


def refnum_type(inner, plain_class=False):
    """ref{LV.<Class>} -> Refnum RefType=LVObjCtl with the class's ID in CtlFlags, read off one
    AIXML-generated control per class (parts/refnum-classes.tsv); ref{Visa.Instr} -> the VISA
    refnum with its Void variant, as the same probe wrote it. A control refnum (ref{LV.Boolean},
    ...) carries an object of the control's class that fixes its data type - not composed yet."""
    global _REF_CLASSES
    if _REF_CLASSES is None:
        _REF_CLASSES = {}
        with open(os.path.join(HERE, "parts", "refnum-classes.tsv"), encoding="utf-8") as f:
            for line in f:
                row = line.rstrip("\r\n").split("\t")
                if line.startswith("#") or row[0] == "class" or not line.strip():
                    continue
                cls, flags, nested = (row + [""])[:3]
                _REF_CLASSES[cls] = (flags, nested)
    inner = inner.strip()
    m = re.fullmatch(r"GenClassTag\.([^.]+)\.(.+)", inner)
    if m:
        # a user-defined tag refnum (NI System Configuration, DataFinder, DAQmx task ...): a
        # UsrDefndTag refnum holding a UserDefined Tag that names the class, as the probe wrote it
        el = ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "UsrDefndTag", "Ident": m.group(1),
                                     "FirstClient": "1", "TypeName": m.group(2), "Format": "inline"})
        tag = ET.SubElement(el, "TypeDesc", {"Type": "Tag", "Prop1": "0xFFFFFFFF", "TagType": "UserDefined", "Format": "inline"})
        ET.SubElement(tag, "Ident").text = m.group(2)
        tag.append(_void_variant())
        el.append(_void_variant())
        return el
    if inner == "Visa.Instr":
        el = ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "VisaRef", "Ident": "Instr",
                                     "FirstClient": "0", "Format": "inline"})
        v = ET.SubElement(el, "LVVariant", {"Index": "0", "HasVarItem2": "1", "VarType2": "0", "Version": "auto"})
        ET.SubElement(v, "DataType", {"Index": "0", "Type": "Void", "Format": "inline"})
        return el
    row = _REF_CLASSES.get(inner)
    if row is None:
        raise Unsupported("type 'ref{%s}' is not composable yet" % inner)
    if row[1] and not plain_class:
        # (a property node's own reference terminals are the PLAIN class refnum even for a
        # control class - measured on an implicit node on a string control: CtlFlags 0x001B,
        # nothing nested - so they pass plain_class)
        raise Unsupported("type 'ref{%s}' is a control refnum (it carries a %s), not composable yet" % (inner, row[1]))
    return ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "LVObjCtl", "CtlFlags": row[0],
                                   "HasItem": "0", "Format": "inline"})


# The VI Server class of a reference TO a front-panel object, by the object's data type, and
# whether the strictly typed refnum carries that data type inside - read off an AIXML-generated
# probe with one control reference per kind (probes/pvsr2.xml, 2026-10-10).
CONTROL_REF_CLASS = {"Boolean": ("0x0008", True), "String": ("0x001B", False), "Path": ("0x000B", False),
                     "Cluster": ("0x001E", True), "Array": ("0x000E", True)}


# A GRAPH's control refnum is of the graph's class, not of its data type's: LabVIEW's save of
# tests/t129_graph_events.xml wrote these where the composer had written Array 0x000E, Digital
# 0x0012 and Cluster 0x001E (2026-10-11). The first three match parts/refnum-classes.tsv; XY Graph
# is not in it and was read off that save.
GRAPH_STYLE_CLASS = {"graph21703": "0x0018",    # Waveform Graph
                     "graph21717": "0x0017",    # Waveform Chart
                     "graph21716": "0x0053",    # XY Graph
                     "graph21720": "0x001A"}    # Intensity Graph


def control_ref_type(ctl_tree, label=None, style=None):
    """A strictly typed control refnum for a control of type `ctl_tree`: Refnum LVObjCtl with the
    control's class (Digital 0x0012 for every numeric, Enum 0x0023, ...) and, for those that are
    typed by their data, the data type inside. `style` is a graph style the object was COMPOSED
    as - the class is then the graph's (GRAPH_STYLE_CLASS), with the data type inside."""
    t = ctl_tree.get("Type") or ""
    if style in GRAPH_STYLE_CLASS:
        flags, inner = GRAPH_STYLE_CLASS[style], True
    elif is_enum(ctl_tree):
        flags, inner = "0x0023", True
    elif t.startswith("Num"):
        flags, inner = "0x0012", True
    elif t in CONTROL_REF_CLASS:
        flags, inner = CONTROL_REF_CLASS[t]
    else:
        raise Unsupported("a reference to a %s control is not composable yet" % t)
    el = ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "LVObjCtl", "CtlFlags": flags,
                                 "HasItem": "0", "Format": "inline"})
    if inner:
        el.append(lvheap.strip_label(ctl_tree))
    if label:
        el.set("Label", label)
    return el


def is_compound(tree):
    return tree.get("Type") in ("Cluster", "Array")


def is_enum(tree):
    return tree.get("Type", "").startswith("Unit") and tree.find("EnumLabel") is not None


def fields(tree):
    return [c for c in tree if c.tag == "TypeDesc"]


def _split_outside(text, sep):
    """Split on `sep` where it is not inside "..." or <...>."""
    parts, cur, quoted, angled = [], [], False, 0
    for ch in text:
        if ch == '"':
            quoted = not quoted
        elif ch == "<" and not quoted:
            angled += 1
        elif ch == ">" and not quoted and angled:
            angled -= 1
        if ch == sep and not quoted and not angled:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return parts


def event_items(sel):
    """An Event Structure frame's selector (unescaped) -> one '<source>: <event>' item per
    registered pair, '"Stop": Value Change' or 'Timeout' for a static one. Events are separated by
    ';' and one event may name several sources: ' <Preview>, <Terminals>: Mouse Down ' is two
    pairs, in the order LabVIEW numbers their EventSpecs - read off NI's exports of the icon
    editor's Buffer for lossless tracking.vi and NewProjectDialog.vi (`"Next": Value Change;
    <Next! Ref>: Value Change`). A comma used to be read as the separator, which split a
    multi-source event into a bare source and a remainder."""
    out = []
    for part in _split_outside(sel, ";"):
        part = part.strip()
        if not part:
            continue
        pieces = _split_outside(part, ":")
        if len(pieces) < 2:
            out.append(part)
            continue
        srcs, ev = ":".join(pieces[:-1]), pieces[-1].strip()
        for s in _split_outside(srcs, ","):
            if s.strip():
                out.append("%s: %s" % (s.strip(), ev))
    return out


WIRE_SAFE = {"Boolean", "String", "Path", "LVVariant", "Void", "NumInt8", "NumInt16", "NumInt32", "NumInt64",
             "NumUInt8", "NumUInt16", "NumUInt32", "NumUInt64", "NumFloat32", "NumFloat64", "NumFloatExt",
             "NumComplex64", "NumComplex128", "NumComplexExt", "UnitUInt8", "UnitUInt16", "UnitUInt32",
             "UnitFloat64", "UnitFloat32", "UnitInt32", "UnitInt16",
             # t99, 2026-10-10: DAQmx terminal / physical channel / old DAQ channel tags and a
             # picture wired control -> indicator, no insanity, values through
             "Tag", "Picture",
             # t104: Transpose 2D Array's sub-array between two Reshape Arrays, values through
             "SubArray"}


def unverified_wire_type(tree):
    """None when every part of a wire's type is one a composed wire has carried in a test that
    loads clean; otherwise a short name of the first part that is not (a DataLog refnum, a
    picture ...). Two of the three VIs that KILLED LabVIEW in the 2026-10-10 load check had a
    wire of such a type."""
    if tree is None:
        return None
    t = tree.get("Type") or ""
    if t == "TypeDef":
        # judged by the type it wraps. Measured 2026-10-10: an LVPositionTypeDef.ctl cluster
        # (lvbd_graft_clear.xml), an array of LVRectTypeDef.ctl (lvpane_probe.xml) and an
        # ItemRef.ctl uint64 between three subVI calls (NI's Get Item Info.vi) all load
        # executable. That last VI's FATAL "Insane Wire" had been blamed on the typedef and was
        # out_key's shared terminal; no .ctl link is written, and NI's converter writes none.
        return unverified_wire_type(typedef_inner(tree))
    if t in ("Cluster", "Array"):
        for f in fields(tree):
            bad = unverified_wire_type(f)
            if bad:
                return bad
        return None
    if t == "MeasureData":
        return None if tree.get("Flavor") in ("Float64Waveform", "TimeStamp", "Dynamicdata") else "MeasureData." + (tree.get("Flavor") or "")
    if t == "Refnum":
        # TDMSFile: t109, TDMS Open -> TDMS Close, file written and path back (2026-10-10)
        # UDClassInst: a class object out of a subVI (DQMH's Default Module Icon.vi), through a
        # case tunnel into the next call - lvdqmh_new_module.xml loaded executable with no
        # insanity in LabVIEW's log (2026-10-10); loaded, not run - it creates a module
        # UserEvent, EventReg: the user-event skeleton composed and RAN - Create, Register, the
        # dynamic event terminal, Generate, Unregister, Destroy - with Ticks Received = 5 and no
        # insanity in the log, and stopped by the payload alone in a variant (2026-10-10)
        if tree.get("RefType") not in ("LVObjCtl", "VisaRef", "UsrDefndTag", "TDMSFile", "UDClassInst",
                                       "UserEvent", "EventReg"):
            return "Refnum." + (tree.get("RefType") or "?")
        if tree.get("RefType") == "LVObjCtl" and [f.get("Type") for f in fields(tree)] == ["Function"]:
            # a strictly typed VI reference: its pane is the callee's own (t103)
            return None if os.environ.get("AIXML2VI_STRICT_VI_REF_OK", "1") == "1" else "Refnum.StrictVI"
        for f in fields(tree):
            bad = unverified_wire_type(f)
            if bad:
                return bad
        return None
    return None if t in WIRE_SAFE else t


def typedef_inner(tree):
    """The type a TypeDef wraps (its nested TypeDesc); any other type itself."""
    while tree is not None and tree.get("Type") == "TypeDef" and fields(tree):
        tree = fields(tree)[0]
    return tree


def elem_of(tree):
    return fields(tree)[0]


def unlabel(tree):
    """The type a WIRE carries: the outer label goes, field labels stay (they are the type)."""
    return lvheap.strip_label(tree)


def signal_kind(tree):
    """lastSignalKind as LabVIEW writes it for the wire's type, low 16 bits - measured on
    AIXML-generated VIs: 0x100|code for a scalar, 0x200|element code for a 1D array, 0x230 for a
    string, 0x350 for a cluster. LabVIEW recomputes it; the value is advisory."""
    if tree is None:
        return 266
    t = tree.get("Type")
    if t == "Array":
        return 0x200 | TYPE_CODE.get(elem_of(tree).get("Type"), 0)
    if t == "Cluster":
        return 0x350
    if t == "String":
        return 0x230
    if t == "Refnum":
        # measured on an AIXML-generated probe: 0x170 on VI Server refnum wires, 0x237 on VISA
        return 0x237 if tree.get("RefType") == "VisaRef" else 0x170
    if t in ENUM_BASE.values():
        t = "Num" + t[4:]
    code = TYPE_CODE.get(t)
    return 0x100 | code if code else 266


NUM_RANK = ["NumUInt8", "NumInt8", "NumUInt16", "NumInt16", "NumUInt32", "NumInt32",
            "NumUInt64", "NumInt64", "NumFloat32", "NumFloat64", "NumFloatExt",
            "NumComplex64", "NumComplex128", "NumComplexExt"]


def grow_base(key):
    """The kind of a growable node's terminal: `element#2` is an element, and Format Into
    String's numbered `input 5` is an input - its arguments are printfArg DCOs in the node's
    dcoList with no index of their own, so a fifth one is a clone of the fourth."""
    return re.sub(r" \d+$", "", key.split("#")[0])


def type_sig(tree):
    """A type's shape with every label and reference annotation dropped - for deciding whether
    a part was built around exactly this type."""
    t = plain(tree)
    for e in t.iter():
        e.text = e.tail = None      # a part's type carries pylabview's indentation
        for k in [k for k in e.attrib if k == "Label" or k.startswith("ref.")]:
            del e.attrib[k]
    return ET.tostring(t)


def plain(tree):
    """A donor type without units or enum labels: UnitFloat64+PhysUnit -> NumFloat64."""
    t = copy.deepcopy(tree)
    if (t.get("Type") or "").startswith("Unit"):
        t.set("Type", "Num" + t.get("Type")[4:])
        for c in list(t):
            if c.tag in ("PhysUnit", "EnumLabel"):
                t.remove(c)
    if t.get("Type") == "SubString":
        # LabVIEW's internal view of part of a string, saved on Match Pattern's outputs in an
        # IDE-saved donor (MGI's Update Label.vi); NI's converter types the same outputs String
        # (its build of lvai_create_accessors.xml, 2026-10-10), and no wire may carry it
        t.set("Type", "String")
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
    if t not in INT_RANGE and t not in FLOAT_RANGE:
        return      # complex and extended come from parts of their own representation
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


def _shift_bounds(e, dy, dx):
    if e is not None and e.text:
        t, l, b, r = lvheap.parse_bounds(e.text)
        e.text = lvheap.fmt_bounds((t + dy, l + dx, b + dy, r + dx))


def internal_shift(o, dy, dx):
    """Shift what a cluster or array holds in ITS OWN frame by (dy, dx). A cluster's pane frame
    is tied to where the cluster sits: docBounds is the negated top-left of the cluster
    (measured on every top-level cluster of three AIXML-generated VIs, front panel and diagram
    alike), and the fields' bounds live in that frame - so is an array's cluster element."""
    cls = o.get("class")
    if cls == "stdClust":
        pane = o.find("paneHierarchy")
        _shift_bounds(pane.find("docBounds"), dy, dx)
        for f in pane.find("zPlaneList"):
            move_obj(f, dy, dx)
    elif cls == "indArr" and o.find("ddo") is not None:
        internal_shift(o.find("ddo"), dy, dx)   # the element itself sits relative to the array


def move_obj(o, dy, dx):
    """Move an object by (dy, dx) and keep a cluster's or array's insides consistent with it:
    moving a cluster constant without them was `Insane Array Constant {graphics}` on an array
    field (and a field outside the pane)."""
    _shift_bounds(o.find("bounds"), dy, dx)
    internal_shift(o, -dy, -dx)


def place_obj(o, top, left):
    b = o.find("bounds")
    if b is None:
        return
    t, l, _, _ = lvheap.parse_bounds(b.text)
    move_obj(o, top - t, left - l)


def vi_label(ddo):
    for part in ddo.find("partsList") if ddo.find("partsList") is not None else []:
        if part.get("class") == "label" and part.findtext("partID") == "16":
            t = part.findtext("textRec/text") or ""
            t = t[1:-1] if t.startswith('"') and t.endswith('"') else t
            # pylabview writes control characters other than LF and TAB as literal &#xNN; text
            return re.sub(r"&#x([0-9A-Fa-f]{2});", lambda m: chr(int(m.group(1), 16)), t)
    return ""


def unescape(s):
    """AIXML escapes a few characters in names as \\XX hex - and a BACKSLASH in a terminal name
    as a doubled backslash: NI writes the label `delimiter (\\t)` as `delimiter (\\\\t)`,
    counted in its own exports, where a value would carry \\5C."""
    out, i = [], 0
    while i < len(s):
        if s.startswith("\\\\", i):
            out.append("\\")
            i += 2
        elif s[i] == "\\" and i + 2 < len(s) + 1 and all(c in "0123456789ABCDEFabcdef" for c in s[i + 1:i + 3]):
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


def rep_key(name, n):
    """The n-th terminal of one name: Build Array's and Unbundle's `element` repeats."""
    return name if n == 0 else "%s#%d" % (name, n)


def keyed(ps):
    """pairs() with repeated terminal names numbered in order: element, element#1, ..."""
    seen, out = {}, []
    for t, n in ps:
        out.append((rep_key(t, seen.get(t, 0)), n))
        seen[t] = seen.get(t, 0) + 1
    return out


def out_key(el, tname):
    """The endpoint key of an OUTPUT terminal: its name, or name + NUL + "out" when an input of
    the same element has that name too. A subVI's pane may carry an input and an output with one
    label - mxLvGetProjectPath.vi's `Item` in and `Item` out - and keyed by name alone both ends
    landed on ONE terminal: a wire into it and a wire out of it, three "Insane Wire {sub}" and a
    FATAL DAbort (NI's Get Item Info.vi, 2026-10-10). Only subVI calls and invoke nodes, which
    name terminals after the callee's labels; a primitive keeps its own naming rules."""
    if el.tag == "Call" or (el.tag == "Node" and el.get("_name") in ("Invoke Node", "Register For Events")):
        if tname in {t for t, _ in pairs(el.get("inputs"))}:
            return tname + "\x00out"
    return tname


def const_value(tree, value):
    """A constant's ConstValue as LabVIEW writes it in the heap: big-endian flattened data,
    and ONE EXTRA ZERO BYTE when the data is all zero. Measured over 120 NI VIs, every
    numeric, boolean and string constant: a default value carries exactly one more byte than
    its type, a non-default one never does - 0 as int32 is 0000000000, false is 0000, the
    empty string 0000000000. Without that byte a zero constant breaks the VI on its own."""
    h = _const_value(tree, value)
    return h + "00" if set(h) <= {"0"} else h


def parse_literal(s):
    """An AIXML compound value - '[7,true]', '[1,2,3]', '[[1,2],[3,4]]', '[false,0,]' - into
    nested lists of still-escaped scalar strings. Commas inside a string arrive as \\2C."""
    s = (s or "").strip()
    if not s.startswith("["):
        return s
    inner = s[1:-1] if s.endswith("]") else s[1:]
    if inner == "":
        return []
    return [parse_literal(x) for x in split_top(inner)]


def _flat(tree, value):
    """Flattened data, big-endian, as LabVIEW's DefaultData/ConstValue carry it: a cluster is
    its fields in order, an array is one int32 per dimension then the elements (measured:
    array{int32} [1,2,3] = 00000003 00000001 00000002 00000003; cluster{int32,bool} [7,true] =
    00000007 01)."""
    t = tree.get("Type")
    if t == "Cluster":
        vals = parse_literal(value) if isinstance(value, str) else value
        if not isinstance(vals, list):
            vals = []
        fs = fields(tree)
        return "".join(_flat(f, vals[i] if i < len(vals) else "") for i, f in enumerate(fs))
    if t == "Array":
        vals = parse_literal(value) if isinstance(value, str) else value
        if not isinstance(vals, list):
            vals = []
        ndims = len(tree.findall("Dimension"))
        dims, cur = [], vals
        for _ in range(ndims):
            dims.append(len(cur) if isinstance(cur, list) else 0)
            cur = cur[0] if isinstance(cur, list) and cur else []

        def leaves(v, depth):
            if depth == 0:
                return [v]
            return [x for item in (v if isinstance(v, list) else []) for x in leaves(item, depth - 1)]
        elem = elem_of(tree)
        return "".join(struct.pack(">i", d).hex().upper() for d in dims) + \
            "".join(_flat(elem, x) for x in leaves(vals, ndims))
    if t in ENUM_BASE.values():
        labels = [e.text for e in tree.findall("EnumLabel")]
        v = unescape(value or "0")
        idx = labels.index(v) if v in labels else int(float(v or 0))
        return struct.pack(INT_FMT["Num" + t[4:]], idx).hex().upper()
    return _const_value(tree, value)


def _quad(x):
    """A float as LabVIEW flattens an extended: IEEE 754 binary128, big-endian - measured,
    100 is 4005 9000 0000 0000 0000 0000 0000 0000."""
    import math
    if x == 0:
        return "00" * 16
    sign = 1 if x < 0 else 0
    if math.isinf(x):
        return ("%04X" % ((sign << 15) | 0x7FFF)) + "00" * 14
    m, e = math.frexp(abs(x))                   # x = m * 2^e, 0.5 <= m < 1
    frac = int((m * 2 - 1) * (1 << 52)) << 60   # 52 bits of a double into 112
    return ("%04X" % ((sign << 15) | (e - 1 + 16383))) + ("%028X" % frac)


def _complex(value):
    """'3', '1.5+2i', '-1-0.5i' -> (re, im)."""
    v = unescape(value or "0").replace(" ", "")
    m = re.fullmatch(r"([+-]?[^+-]+?)(?:([+-][^+-]*)i)?", v)
    if not m:
        raise Unsupported("complex literal %r is not readable" % value)
    return float(m.group(1) or 0), float(m.group(2) or 0) if m.group(2) else 0.0


def _const_value(tree, value):
    t = tree.get("Type")
    if t in ("Cluster", "Array") or t in ENUM_BASE.values():
        return _flat(tree, value)
    if t == "NumFloat64":
        return struct.pack(">d", float(value or 0)).hex().upper()
    if t == "NumFloat32":
        return struct.pack(">f", float(value or 0)).hex().upper()
    if t in INT_FMT:
        return struct.pack(INT_FMT[t], int(float(value or 0))).hex().upper()
    if t == "Boolean":
        return "01" if str(value).lower() in ("true", "1") else "00"
    if t == "LVVariant" and not value:
        # an EMPTY variant, the only one NI's exports write (value=""): flattened with the
        # version stamp of the LabVIEW that wrote it, read off probes/pclean.xml's constant
        return "2600800000000001000400000001000000000000"
    if t in ("String", "Tag"):
        # a Tag constant (a DAQmx terminal name ...) is flattened like a string: measured
        # 0000000C 4F6E626F617264436C6F636B for "OnboardClock"
        b = unescape(value or "").encode("mac_roman")
        return (struct.pack(">I", len(b)) + b).hex().upper()
    if t == "NumFloatExt":
        return _quad(float(value or 0))
    if t in ("NumComplex128", "NumComplex64"):
        re_, im = _complex(value)
        f = ">d" if t == "NumComplex128" else ">f"
        return (struct.pack(f, re_) + struct.pack(f, im)).hex().upper()
    if t == "MeasureData" and tree.get("Flavor") == "TimeStamp":
        # AIXML cannot carry a timestamp literal (LabVIEW's own converter discards it): zero
        return "00" * 16
    if t == "Path":
        v = unescape(value or "")
        if not v:
            # LabVIEW's empty path, as NI's own constants carry it: type 0, no components
            body = struct.pack(">HH", 0, 0)
        else:
            absolute = len(v) > 1 and v[1] == ":"
            comps = [c for c in v.replace("/", "\\").split("\\") if c]
            if absolute:
                # the drive is stored WITHOUT its colon - LabVIEW adds it back: `C:` as written
                # read back `C::\probe\in.txt` (t122, 2026-10-10)
                comps[0] = comps[0].rstrip(":")
            body = struct.pack(">HH", 0 if absolute else 1, len(comps))
            for c in comps:
                cb = c.encode("mac_roman")
                body += struct.pack(">B", len(cb)) + cb
        return (b"PTH0" + struct.pack(">I", len(body)) + body).hex().upper()
    if t == "Refnum":
        # a refnum constant is the null refnum - 4 zero bytes (an AIXML-generated probe wrote
        # 0000000000: those plus the default marker); there is no literal for a live one
        if (value or "").strip():
            raise Unsupported("a refnum constant has no literal value (%r)" % value)
        return "00000000"
    raise Unsupported("no constant encoding for %s" % t)


def _tsv(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        return [r for r in csv.reader((l for l in f if not l.startswith("#")), delimiter="\t")
                if r and r[0] != "primResID"]


def load_names():
    """primitive-names.tsv / terminal-names.tsv (experiments/pylabview), plus local supplements:
    names primitive-names.tsv lacks (Greater?, Greater Or Equal? ... primitive-names-extra.tsv),
    terminal names learned from NI's exports and re-harvested on 2026-10-10."""
    prim_by_name = {}
    for row in _tsv(os.path.join(TABLES, "primitive-names.tsv")) + _tsv(os.path.join(HERE, "primitive-names-extra.tsv")):
        # the tables keep AIXML's escaping, XML entities AND \XX ("Boolean To (0\2C1)"),
        # while a node's _name is compared unescaped
        rid, name = row[0], unescape(html.unescape(row[1]))
        seen = int(row[2]) if len(row) > 2 and row[2].isdigit() else 0
        if name not in prim_by_name or seen > prim_by_name[name][1]:
            prim_by_name[name] = (rid, seen)
    terms = {}
    for row in _tsv(os.path.join(TABLES, "terminal-names.tsv")):
        terms[(row[0], row[2])] = unescape(html.unescape(row[3]))
    for extra in ("terminal-names-learned.tsv", "terminal-names-extra.tsv"):
        for row in _tsv(os.path.join(HERE, extra)):
            terms.setdefault((row[0], row[2]), unescape(html.unescape(row[3])))
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
        # A part harvested before 2026-10-10 may carry its DONOR's type ids inside a typedef's
        # inline type (lvheap.Bundle._inline); composed from, it yields a VI whose typedef points
        # at unrelated types and loads broken. Refused by name - repair_nested_types.py fixes it.
        for t in types:
            for n in t.iter("TypeDesc"):
                if n.get("Nested") == "True" and any(c.tag == "TypeDesc" and c.get("TypeID") is not None for c in n):
                    raise Unsupported("part %s carries its donor's type ids inside a typedef - run "
                                      "repair_nested_types.py on %s" % (key, self.d))
        heap = copy.deepcopy(list(root.find("heap"))[0])
        images = {int(i.get("index")): i.text for i in root.find("images")} if root.find("images") is not None else {}
        if images:
            heap.set("_images", json.dumps(images))   # carried to instantiate(), removed there
        if root.find("piccs") is not None:
            heap.set("_piccs", json.dumps({int(i.get("index")): i.text for i in root.find("piccs")}))
        if root.find("strs") is not None:
            heap.set("_strs", json.dumps({s.get("Index"): ET.tostring(s, encoding="unicode")
                                          for s in root.find("strs")}))
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
        self.resolver = None         # built on the first <Call> that needs the installation
        self.links = {}              # callee path -> {"name", "flat", "cons", "offsets": [uid]}
        self.dyn_links = {}          # dynamic-dispatch callee path -> resolver result + dynLink uids
        self.class_consts = {}       # .lvclass path -> {"qual", "lib_ref", "uids": [ddo uid]}

    # ---- uids and types ------------------------------------------------------------------
    def alloc(self):
        while self.next_uid in self.used_uids:
            self.next_uid += 1
        self.used_uids.add(self.next_uid)
        return self.next_uid

    def claim(self, uid):
        try:
            uid = int(uid)
        except (TypeError, ValueError):
            # a uid may be a NAME - `wb`, `lbl` in aixml-skeletons/web-browser-read-page-title.xml,
            # which NI's converter builds executable - and the heap then gets a number of its own
            return self.alloc()
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
        if heap.get("_images"):
            # Images EMBEDDED in the donor (DSIM sections, positive ImageResIDs) come along under
            # numbers free in this VI: a waveform control without them was "Could not load the
            # image" and a broken VI. Identical images share one section.
            mapping = {}
            for old, hx in json.loads(heap.get("_images")).items():
                self.images = getattr(self, "images", {})
                same = next((i for i, h in self.images.items() if h == hx), None)
                if same is None:
                    same = max([1] + list(self.images)) + 1
                    self.images[same] = hx
                mapping[int(old)] = same
            for e in heap.iter("ImageResID"):
                try:
                    v = int(e.text)
                except (TypeError, ValueError):
                    continue
                if v in mapping:
                    e.text = str(mapping[v])
            del heap.attrib["_images"]
        if heap.get("_piccs"):
            # a graph's plot pictures (PICC sections, named by ImageInternalsResID) come along
            # under numbers free in this VI, identical pictures sharing one section
            mapping = {}
            self.piccs = getattr(self, "piccs", {})
            for old, hx in json.loads(heap.get("_piccs")).items():
                same = next((i for i, h in self.piccs.items() if h == hx), None)
                if same is None:
                    same = max([1] + list(self.piccs)) + 1
                    self.piccs[same] = hx
                mapping[int(old)] = same
            for e in heap.iter("ImageInternalsResID"):
                try:
                    v = int(e.text)
                except (TypeError, ValueError):
                    continue
                if v in mapping:
                    e.text = str(mapping[v])
            del heap.attrib["_piccs"]
        if heap.get("_strs") or any(True for _ in heap.iter("variantIndex")):
            # a tag control or refnum names its STR section (displayFilter, typeClass): each
            # object gets a section of its own under a number free in this VI, as LabVIEW's
            # own files do. A part harvested before the STR sections were kept has none - it
            # then gets an empty attribute list rather than an index into nothing.
            strs = json.loads(heap.get("_strs") or "{}")
            self.strs = getattr(self, "strs", {})
            for e in heap.iter("variantIndex"):
                old = (e.text or "").strip()
                if not old.isdigit():
                    continue
                new = max([1] + list(self.strs)) + 1
                sec = ET.fromstring(strs[old]) if old in strs else None
                if sec is None:
                    sec = ET.fromstring(EMPTY_STR_SECTION)
                sec.set("Index", str(new))
                self.strs[new] = sec
                e.text = str(new)
            heap.attrib.pop("_strs", None)
        # An annex's nRC/oRC is a coordinate pair tied to where the DONOR sat: the same array
        # constant was clean at the donor's (417, 231) and "Insane Array Constant {graphics}
        # (0x80)" at (102, 40); without the pair it was clean at both (measured 2026-10-09).
        # Only a NEGATIVE pair is a position; a waveform control's (0, 4) / (0, 1) is not, and
        # stripping it was one of two differences from a working reference.
        for p in heap.iter():
            pair = [c for c in p if c.tag in ("nRC", "oRC")]
            if pair and any(v < 0 for c in pair for v in lvheap.parse_bounds("(%s, 0, 0)" % (c.text or "0, 0").strip("() "))[:2]):
                for c in pair:
                    p.remove(c)
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

    # ---- data objects: scalars, enums, clusters, arrays -------------------------------------
    DDO_CLASS = {"Cluster": "stdClust", "Array": "indArr"}

    @staticmethod
    def _direction(e, is_ind):
        f = e.find("objFlags")
        v = int(f.text) if f is not None else 0
        v = v | 1 if is_ind else v & ~1
        if f is None:
            if not v:
                return      # no field means 0; an added <objFlags>0</objFlags> differs from LabVIEW's form
            f = ET.Element("objFlags")
            e.insert(0, f)
        f.text = str(v)

    def _typed_donor(self, holder, donor_types, keep):
        """Type every TypeID in `holder` that is NOT under one of the `keep` subtrees with the
        donor's own type, in order - the index display of an array, say."""
        skip = set()
        for k in keep:
            skip.update(id(x) for x in k.iter("typeDesc"))
        for i, e in enumerate(lvheap.typeid_elements(holder)):
            if id(e) not in skip and e.get("_t") is not None:
                tree = plain(lvheap.strip_label(donor_types[int(e.get("_t"))]))
                e.text = "TypeID(%d)" % self.heap_tid(tree)
        for e in holder.iter("typeDesc"):
            e.attrib.pop("_t", None)

    # A refnum CONSTANT of a VI Server class is a CLASS SPECIFIER CONSTANT (control resource 2094,
    # showing its class) in NI's converter's builds - 18 of 18 classes in probes/prefcls.xml, and
    # Cluster, Array and ClassSpecifierConstant in the helpers - except VI and Application, which
    # are plain refnum constants (3412, probes/pref). Composed from the generic part, lvai_class_
    # names.xml could not find its own `csc` through Open VI Object Reference: Error 1054,
    # measured 2026-10-10; LabVIEW loaded it executable all the same.
    CSC_KEY = "const/stdRefNum/Refnum.LVObjCtl.ClassSpecifier"

    @staticmethod
    def _is_class_specifier(tree, prefix):
        if prefix != "const" or tree.get("Type") != "Refnum" or tree.get("RefType") != "LVObjCtl" or fields(tree):
            return False
        try:
            return int(tree.get("CtlFlags") or "0", 16) not in (0x0001, 0x0002)
        except ValueError:
            return False

    @staticmethod
    def _class_of_refnum(tree):
        refnum_type("LV.VI", plain_class=True)          # loads _REF_CLASSES
        flags = (tree.get("CtlFlags") or "").upper()
        return next((c for c, (f, _) in _REF_CLASSES.items() if f.upper() == flags), "LV.Generic")

    @staticmethod
    def _ddo_of(holder):
        """The data object inside a part: an fPDCO's <ddo>, a constant term's dco/ddo, or - for
        an array-element part - the part itself."""
        if holder.find("ddo") is not None:
            return holder.find("ddo")
        if holder.find("dco/ddo") is not None:
            return holder.find("dco/ddo")
        return holder

    def build_ddo(self, tree, is_ind, prefix="fpdco", tag="ddo", in_array=False, force_class=None):
        """One data object for `tree` (labelled: the label is the object's name), composed from
        parts. A cluster is a stdClust whose paneHierarchy holds one object per field (ddoList
        oldest first, zPlaneList newest first); an array is an indArr whose inner <ddo> is the
        element - measured on AIXML-generated VIs. Inside an array (in_array) objects come from
        arrelem/ parts: an array constant's element and its fields are frameless objects of
        their own kind. Returns (ddo, source, height, width)."""
        t = tree.get("Type")
        name = tree.get("Label") or ""
        want, other = ("ind", "ctl") if is_ind else ("ctl", "ind")
        if prefix == "const":
            want = other = None
        if t in self.DDO_CLASS and not force_class:
            cls = self.DDO_CLASS[t]
            key = None
            if in_array and self.parts.get("arrelem/%s/%s" % (prefix, cls)) is not None:
                key = "arrelem/%s/%s" % (prefix, cls)
            if key is None:
                # best part: same element kind and direction, then same element kind, then any
                # array/cluster part of the direction (its element is then built from parts)
                exact = lvheap.type_key(unlabel(tree)).replace(".", "_")
                dims_key = exact.split("_")[0]          # Array, Array2, Cluster
                best = None
                for k in self._keys("%s/%s/" % (prefix, cls)):
                    last = k.split("/")[-1]
                    d = k.split("/")[2] if prefix == "fpdco" else None
                    if not (last == exact or last == t or last.startswith(t)):
                        continue
                    if t == "Array" and last.split("_")[0] != dims_key:
                        continue    # the number of dimensions is the container's own shape
                    rank = (0 if last == exact else 2) + (0 if want is None or d == want else 1)
                    if best is None or rank < best[0]:
                        best = (rank, k)
                key = best[1] if best else None
            if key is None:
                raise Unsupported("no %s part for a %s" % (prefix, t))
            holder, donor_types, src = self.instantiate(key)
            for i, e in enumerate(lvheap.typeid_elements(holder)):
                e.set("_t", str(i))
            ddo = self._ddo_of(holder)
            if t == "Cluster":
                dl = ddo.find("ddoList")
                z = ddo.find("paneHierarchy/zPlaneList")
                # fields are stacked from where the donor's first field sat: a front-panel
                # cluster's pane and a diagram constant's use different origins (measured
                # (-31,-16) under origin (-4,-4) against (-222,-227) under (-241,-243))
                tops = [lvheap.parse_bounds(c.find("bounds").text) for c in z if c.find("bounds") is not None]
                y0 = min(b[0] for b in tops) if tops else -31
                x0 = min(b[1] for b in tops) if tops else -16
                for lst in (dl, z):
                    for c in list(lst):
                        lst.remove(c)
                y, w = y0, 0
                kids = []
                for f in fields(tree):
                    child, _, h, cw = self.build_ddo(f, is_ind, prefix, "SL__arrayElement", in_array)
                    place_obj(child, y, x0)
                    y += max(h, 14) + (0 if in_array else 4)
                    w = max(w, cw)
                    z.insert(0, child)
                    ET.SubElement(dl, "SL__arrayElement", {"uid": child.get("uid")})
                    kids.append(child)
                dl.set("elements", str(len(dl)))
                z.set("elements", str(len(z)))
                pane = ddo.find("paneHierarchy")
                # the pane shows the fields from its ORIGIN on: a diagram constant's origin sits
                # 19 px above its first field, so sizing the pane to the fields alone cut the
                # last one off - "Insane Array Constant {graphics}" for an array field
                oy, ox = (lvheap.parse_bounds("(%s, 0, 0)" % pane.findtext("origin").strip("() "))[:2]
                          if pane.find("origin") is not None else (y0, x0))
                ph = max(y - y0, y - oy + 3)
                pw = max(w, x0 + w - ox + 3)
                pb = lvheap.parse_bounds(pane.find("bounds").text)
                pane.find("bounds").text = lvheap.fmt_bounds((pb[0], pb[1], pb[0] + ph, pb[1] + pw))
                b = lvheap.parse_bounds(ddo.find("bounds").text)
                ddo.find("bounds").text = lvheap.fmt_bounds((b[0], b[1], b[0] + pb[0] + ph + 4, b[1] + pb[1] + pw + 4))
            else:
                old = ddo.find("ddo")
                kids = []
                elem = elem_of(tree)
                dims = len(tree.findall("Dimension"))
                ai = ddo.find("arrayIndices")
                donor_dims = len(ai) if ai is not None else 1
                if dims != donor_dims:
                    raise Unsupported("no %d-dimensional array part (the donor has %d)" % (dims, donor_dims))
                want_cls = "stdRing" if is_enum(elem) else self.DDO_CLASS.get(elem.get("Type")) or \
                    SCALARS.get(next((k for k, v in SCALARS.items() if v[0] == elem.get("Type")), ""), (None, None))[1]
                if old is not None and old.get("class") == want_cls and want_cls in ("stdNum", "stdBool", "stdString", "stdPath"):
                    # An element OF THE DONOR'S KIND is the donor's own element, re-typed: in an
                    # array CONSTANT it is a frameless object with no label (objFlags 0x20000,
                    # parts placed from x=1), and a standalone constant part in its place was
                    # "Insane Array Constant {graphics} (0x80)" whatever its box.
                    adapt_numeric(old, elem.get("Type"))
                    if prefix == "fpdco":
                        self._direction(old, is_ind)
                    for e in old.iter("typeDesc"):
                        if lvheap.TYPEID_RE.fullmatch((e.text or "").strip()):
                            e.text = "TypeID(%d)" % self.heap_tid(elem)
                            e.attrib.pop("_t", None)
                    kids.append(old)
                elif old is not None:
                    child, _, h, cw = self.build_ddo(elem, is_ind, prefix, "ddo", True)
                    place_obj(child, *lvheap.parse_bounds(old.find("bounds").text)[:2])
                    if child.get("class") == old.get("class") and child.get("class") != "stdClust":
                        # an element of the same kind takes the donor element's exact box: a
                        # numeric part's own 50 px in an array constant whose cell is 13 px was
                        # "Insane Array Constant {graphics} (0x80)" even inside a wider array
                        child.find("bounds").text = old.find("bounds").text
                    pos = list(ddo).index(old)
                    ddo.remove(old)
                    ddo.insert(pos, child)
                    kids.append(child)
                    # the element is placed in the array's own coordinates and must lie inside
                    # it: an int32 element 50 px wide in a 73 px array constant was measured
                    # "Insane Array Constant {graphics} (0x80)"
                    ct, cl, cb, cr = lvheap.parse_bounds(child.find("bounds").text)
                    b = lvheap.parse_bounds(ddo.find("bounds").text)
                    ddo.find("bounds").text = lvheap.fmt_bounds(
                        (b[0], b[1], max(b[2], b[0] + cb + 3), max(b[3], b[1] + cr + 3)))
            if is_ind is not None and prefix == "fpdco":
                self._direction(ddo, is_ind)
            self._typed_donor(holder, donor_types, kids)
            own = [e for e in (holder.find("typeDesc"), ddo.find("typeDesc"),
                               holder.find("dco/typeDesc")) if e is not None]
            for e in own:
                e.text = "TypeID(%d)" % self.heap_tid(tree)
            self._set_label(ddo, name)
            ddo = self._retag(ddo, tag)
            h, w = lvheap.size_of(ddo)
            return (holder if tag is None else ddo), src, h, w
        # scalars and enums: a front-panel or constant part's ddo, or an array element's
        tkey = lvheap.type_key(tree)
        ddo_class = DDO_OF_KEY.get(tkey) or ("stdTag" if tkey.startswith("Tag.") else None)
        if is_enum(tree):
            ddo_class = "stdRing"
        if force_class:
            ddo_class = force_class     # a text ring, a table: the style picks the object
        if ddo_class is None:
            raise Unsupported("no data object for type %s" % t)
        key = None
        if in_array and self.parts.get("arrelem/%s/%s" % (prefix, ddo_class)) is not None:
            key = "arrelem/%s/%s" % (prefix, ddo_class)
        if key is None and self._is_class_specifier(tree, prefix) and self.parts.get(self.CSC_KEY) is not None:
            key = self.CSC_KEY
        if key is None:
            key, _ = self.pick("%s/%s/" % (prefix, ddo_class), want, other, tkey)
        if key is None and is_enum(tree):
            key, _ = self.pick("%s/%s/" % (prefix, ddo_class), want, other, "UnitUInt16")
        if key is None:
            raise Unsupported("no %s part for %s (%s)" % (prefix, ddo_class, t))
        holder, donor_types, src = self.instantiate(key)
        if key == self.CSC_KEY:
            # the constant SHOWS its class - the label part 84 reads `Cluster`, `Generic`...
            for part in self._ddo_of(holder).iter("SL__arrayElement"):
                if part.get("class") == "label" and part.findtext("partID") == "84" \
                        and part.find("textRec/text") is not None:
                    part.find("textRec/text").text = '"%s"' % self._class_of_refnum(tree)[3:]
        if tkey == "Refnum.UsrDefndTag":
            # a user-defined tag refnum is CLASS-specific inside: its tag ddo names the class's
            # DLL (tagDLLName "nisyscfg.*") and its STR section the class (typeClass) - a part
            # of another class would be a refnum to the wrong library
            have = next((x.get("TypeName") for d in donor_types for x in d.iter("TypeDesc")
                         if x.get("RefType") == "UsrDefndTag"), None)
            if have != tree.get("TypeName"):
                raise Unsupported("no part for a %s refnum (the harvested one is %s)" % (tree.get("TypeName"), have))
            for vi_ in holder.iter("variantIndex"):
                sec = getattr(self, "strs", {}).get(int(vi_.text))
                tc = sec.find(".//LVVariant[@Name='typeClass']/DataFill/String") if sec is not None else None
                if tc is not None:
                    tc.text = tree.get("Ident")
        ddo = self._ddo_of(holder)
        adapt_numeric(ddo, t)
        if is_enum(tree):
            self._set_enum(ddo, tree)
        if prefix == "fpdco":
            # an LED indicator flipped to a control was measured working; a field of a cluster
            # follows the cluster's direction
            self._direction(ddo, is_ind)
        self._set_label(ddo, name)
        # the object's own type is retyped; what it holds inside (a waveform control's t0, dt and
        # Y, a timestamp's boolean part) keeps the donor's - all of them retyped, pylabview
        # could not even parse the waveform's numeric parts
        own = ET.tostring(plain(lvheap.strip_label(donor_types[0]))) if donor_types else None
        tids = []
        for dt in donor_types:
            same = ET.tostring(plain(lvheap.strip_label(dt))) == own
            # an inner part keeps its donor type WITH its label (the waveform's t0, dt, Y)
            tids.append(self.heap_tid(tree) if same else self.heap_tid(plain(dt)))
        lvheap.set_typeids(holder, tids or [self.heap_tid(tree) for _ in lvheap.typeid_elements(holder)])
        if tag is None:
            return holder, src, 0, 0
        h, w = lvheap.size_of(ddo)
        return self._retag(ddo, tag), src, h, w

    @staticmethod
    def _retag(ddo, tag):
        """A field of a cluster is <SL__arrayElement class=...>, a top-level or array element
        <ddo class=...> - same content."""
        if tag is None or ddo.tag == tag:
            return ddo
        new = ET.Element(tag, dict(ddo.attrib))
        new.text, new.tail = ddo.text, ddo.tail
        for c in ddo:
            new.append(c)
        return new

    @staticmethod
    def _set_enum(ddo, tree):
        """An enum control is a stdRing whose multiLabel `buf` lists the items -
        (3)"Off""On""Auto" - with StdNumMax the base type's maximum."""
        labels = [e.text or "" for e in tree.findall("EnumLabel")]
        if any('"' in x for x in labels):
            raise Unsupported("enum item with a quote is not composable yet")
        for part in ddo.iter("SL__arrayElement"):
            if part.get("class") == "multiLabel" and part.find("buf") is not None:
                part.find("buf").text = "(%d)%s" % (len(labels), "".join('"%s"' % x for x in labels))
        mx = {"UnitUInt8": "255", "UnitUInt16": "65535", "UnitUInt32": "4294967295"}.get(tree.get("Type"))
        if mx and ddo.find("StdNumMax") is not None:
            ddo.find("StdNumMax").text = mx
        if ddo.find("StdNumMin") is not None:
            ddo.find("StdNumMin").text = "0"
        if ddo.find("StdNumInc") is not None:
            ddo.find("StdNumInc").text = "0"

    @staticmethod
    def _set_ring(ddo, tree, items):
        """A TEXT RING (style="Ring", items="a,b,c"): a stdRing like an enum's, its multiLabel
        `buf` listing the items, but the type stays a plain number - the items live in the
        control, not in the type (read off an AIXML-generated probe, probes/pstyle.xml)."""
        labels = [unescape(x) for x in split_top(items)] if items else []
        if any('"' in x for x in labels):
            raise Unsupported("ring item with a quote is not composable yet")
        for part in ddo.iter("SL__arrayElement"):
            if part.get("class") == "multiLabel" and part.find("buf") is not None:
                part.find("buf").text = "(%d)%s" % (len(labels), "".join('"%s"' % x for x in labels))

    # ---- elements ------------------------------------------------------------------------
    def _set_default(self, fpdco, tree, value, name):
        """A control's DEFAULT VALUE: the fPDCO's last child <DefaultData>, the flattened data
        as pylabview writes a heap string - MacRoman text in quotes, control characters other
        than \n and \t as the literal text &#xNN;. LabVIEW writes it only for a value that is
        not the type's default (3 of the many controls of NI's ODE-PredatorPrey.vi: an array
        [300,50] is 00000002 4072C00000000000 4049000000000000). Until 2026-10-10 the composer
        wrote none, so every control came out at its type's default; a part cut from an NI VI
        could also bring its DONOR's default along, which is removed here."""
        for old in fpdco.findall("DefaultData"):
            fpdco.remove(old)
        if value in (None, ""):
            return
        try:
            hx = _flat(unlabel(tree), value)
        except (Unsupported, ValueError, TypeError, KeyError, IndexError, struct.error) as ex:
            self.report.append("default value of %r not composed (%s)" % (name, ex))
            return
        if not hx or set(hx) <= {"0"}:
            return
        txt = bytes.fromhex(hx).decode("mac_roman")
        txt = "".join("&#x%02X;" % ord(c) if ord(c) < 32 and c not in "\n\t" else c for c in txt)
        ET.SubElement(fpdco, "DefaultData").text = '"%s"' % txt

    def add_fp(self, el, row):
        name = unescape(el.get("_name"))
        is_ind = el.tag == "Indicator"
        aixml_type = el.get("type")
        diag = self.diag_of(el)
        tree = type_tree(aixml_type, label=name)
        want = "ind" if is_ind else "ctl"
        # style: NI's exports write latched (414), Ring (376, with items=), Table (22) and the
        # graph and chart styles graphNNNNN (~420) - until 2026-10-10 all of them were dropped
        # without a word, so a latched Stop came out a switch
        style, items = el.get("style"), el.get("items")
        force = None
        if style == "Ring" or (items is not None and not is_enum(tree)):
            if not tree.get("Type", "").startswith("Num") or tree.get("Type") in ("NumComplex64", "NumComplex128", "NumComplexExt"):
                raise Unsupported("%s %r: a ring of %s is not composable yet" % (el.tag, name, tree.get("Type")))
            force = "stdRing"
        elif style == "Table":
            if tree.get("Type") != "Array":
                raise Unsupported("%s %r: style Table on %s" % (el.tag, name, tree.get("Type")))
            force = "tableControl"
        elif style and re.fullmatch(r"graph\d+", style):
            # a graph or chart: stdGraph whose annex rsrcID is the number (harvest_parts.py
            # keys the parts stdGraphNNNNN). Its plots are built around its data type, so a part
            # is used only for EXACTLY that type; otherwise the data is shown in the plain
            # control and the report says so (refusing them cost 237 of NI's VIs)
            force = "stdGraph" + style[5:]
            got = None
            for d in (("ind", "ctl") if is_ind else ("ctl", "ind")):
                got = self.parts.get("fpdco/%s/%s/%s" % (force, d, lvheap.type_key(tree)))
                if got is not None:
                    break
            if os.environ.get("AIXML2VI_GRAPHS") == "0":
                # ON by default since 2026-10-11. It was off because loading a composed graph VI
                # logged "bad mutex verifier in ThMutexDestroy" and hung every later COM call - a
                # CHART's history type (histTD) was never carried by a part, so it named whatever
                # its number meant in the new VI. A bisect over the ten objects of probes/pgraph.xml
                # showed only the charts hanging; with histTD carried all ten load clean
                # (lvheap.TYPEID_TAGS). AIXML2VI_GRAPHS=0 composes the plain controls instead.
                got = None
            if got is None or type_sig(got[1][0]) != type_sig(tree):
                self.report.append("style %r of %r not composed (no %s part for this type) - shown as a plain %s"
                                   % (style, name, force, tree.get("Type")))
                force = None
        elif style == "latched" and (is_ind or tree.get("Type") != "Boolean"):
            raise Unsupported("%s %r: style latched on a %s %s" % (el.tag, name, "indicator" if is_ind else "control", tree.get("Type")))
        elif style not in (None, "", "latched"):
            self.report.append("style %r of %r not composed - shown as a plain %s" % (style, name, tree.get("Type")))
        fpdco, src, h, _ = self.build_ddo(tree, is_ind, "fpdco", None, force_class=force)
        if force and force.startswith("stdGraph"):
            # composed AS a graph: its control references are of the graph's class
            self.graph_styles = getattr(self, "graph_styles", {})
            self.graph_styles[name] = style
        ddo = fpdco.find("ddo")
        if force == "stdRing":
            self._set_ring(ddo, tree, items or "")
        if style == "latched":
            # mechanical action: bit 0x20000 of the boolean's ddo objFlags, as NI's converter
            # writes "latched" (NI's own Stop buttons carry 0x60000, which the reader also
            # reads as latched)
            f = ddo.find("objFlags")
            if f is None:
                f = ET.Element("objFlags")
                ddo.insert(0, f)
            f.text = str(int(f.text or 0) | 0x20000)
        old = int(ddo.get("uid"))
        ddo.set("uid", str(self.claim(el.get("uid"))))
        self.used_uids.discard(old)
        self._direction(fpdco, is_ind)
        self._direction(ddo, is_ind)
        self._set_default(fpdco, tree, el.get("value"), name)
        con = el.get("conIdx")
        # A conIdx a terminal EARLIER in the document already took leaves this one OFF the pane:
        # lvpane_probe.xml gives `VI Path` and `terminals` both conIdx 1, NI's converter's build
        # assigns VI Path and not terminals (measured 2026-10-10), and two terminals on one slot
        # was a FATAL insanity - "FrontPanelDataController ... {id } (0x4)", DAbort in fpsane.cpp,
        # LabVIEW gone. Not separated: whether NI keeps the earlier one or the CONTROL - here the
        # control came first.
        taken = getattr(self, "con_taken", None)
        if taken is None:
            taken = self.con_taken = {}
        if con is not None and con in taken:
            self.report.append("WARNING: %r claims conIdx %s, which %r already has - left off the pane, "
                               "as NI's converter does" % (name, con, taken[con]))
            con = None
        elif con is not None:
            taken[con] = name
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
        # stacked per column by real height: a cluster or array is taller than a numeric
        self.fp_y = getattr(self, "fp_y", {0: 16, 1: 16})
        place_obj(ddo, self.fp_y[col], 16 + 300 * col)
        self.fp_y[col] += max(lvheap.size_of(ddo)[0], 24) + 32
        # z-order lists run NEWEST FIRST in LabVIEW's own files (ddoList and termList run
        # oldest first); appending made the first control LabVIEW's insane one ({flags} 0x1000)
        self.pane_z.insert(0, fpdco)
        ET.SubElement(self.ddo_list, "SL__arrayElement", {"uid": fpdco.get("uid")})
        # a Local Variable names its control by this position in ddoList
        self.fp_info = getattr(self, "fp_info", {})
        self.fp_info.setdefault(name, (len(self.ddo_list) - 1, tree, fpdco))
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
        self.report.append("%s %r <- %s" % (el.tag, name, src))

    def _callee_terminal(self, call_el, tname):
        """(slot type, resolver result) of the terminal `tname` on a Call's callee pane."""
        target = unescape(call_el.get("instance") or call_el.get("target") or "")
        if self.resolver is None:
            self.resolver = resolve.Resolver()
        res = self.resolver.resolve(target, self.search_dirs, getattr(self, "caller_file", None))
        if "error" in res:
            return None, res
        callee = lvheap.Bundle(_extract_callee(res["path"]))
        conp = int(callee.main.find("CONP/Section/TypeDesc").get("TypeID"))
        slots = [c for c in callee.tree(callee.flat_of_consolidated(conp)) if c.tag == "TypeDesc"]
        for fpdco in callee.fp.iter("SL__arrayElement"):
            if fpdco.get("class") != "fPDCO":
                continue
            con = fpdco.findtext("conNum")
            con = 0 if con is None else int(con)
            if 0 <= con < len(slots) and vi_label(fpdco.find("ddo")) == tname:
                t = copy.deepcopy(slots[con])
                for k in [k for k in t.attrib if k.startswith("ref.")]:
                    del t.attrib[k]
                return t, res
        return None, res

    def add_class_const(self, el):
        """A CLASS constant. NI's export writes one as type ref{UDClassInst} and never names the
        class - so, as NI's converter does, the class is the one its wire FEEDS: the class
        terminal of the Call the constant is wired into. The object is a bDConstDCO around a
        udClassDDO (a part cut from LabVIEW's own save of such a constant) and its value is the
        class's flattened DEFAULT: uint32 1, the qualified name as one length-prefixed run of
        Pascal strings ending in 00, PADDED TO 4, then 8 zero bytes - `00000001 13 11
        Rectangle.lvclass 00` + 8 x 00 in LabVIEW's file. The padding is measured, not read: that
        name happens to end on a multiple of 4, and padding to 2 left `Box.lvclass` (18 bytes)
        eBad, "error of type 200", where padding to 4 loads executable (A/B, 2026-10-11). Its
        links are written by write_class_links (FPPI, DDPI and the class's VIPI)."""
        name = unescape(el.get("_name") or "")
        nets = {n for _, n in pairs(el.get("outputs")) if n}
        cls_tree, res = None, None
        for e in self._elements:
            if e.tag != "Call" or cls_tree is not None:
                continue
            for tname, net in pairs(e.get("inputs")):
                if net in nets:
                    t, r = self._callee_terminal(e, tname)
                    if t is not None and t.get("RefType") == "UDClassInst":
                        cls_tree, res = t, r
                        break
        if cls_tree is None or not res or not res.get("owner"):
            raise Unsupported("Constant %r %s: a class constant (ref{UDClassInst}) must feed a "
                              "class terminal of a Call, which names its class" % (name, el.get("uid")))
        qual = [i.get("Text") for i in cls_tree.findall("Item")]
        if len(qual) != 1:
            raise Unsupported("Constant %r: class %s inside a library - not composable yet"
                              % (name, ":".join(qual)))
        got = self.instantiate("const/udClassDDO/Refnum.UDClassInst")
        if got is None:
            raise Unsupported("no class-constant part harvested (const/udClassDDO/Refnum.UDClassInst)")
        term, donor_types, src = got
        dco = term.find("dco")
        ddo = dco.find("ddo")
        old = int(ddo.get("uid"))
        ddo.set("uid", str(self.claim(el.get("uid"))))
        self.used_uids.discard(old)
        self._set_label(ddo, name)
        typed = []
        for donor in donor_types:
            t = copy.deepcopy(cls_tree)
            if donor.get("Label") is not None and name:
                t.set("Label", name)
            else:
                t.attrib.pop("Label", None)
            typed.append(self.heap_tid(t))
        lvheap.set_typeids(term, typed)
        names = b"".join(bytes([len(q.encode("mac_roman"))]) + q.encode("mac_roman") for q in qual) + b"\x00"
        value = (1).to_bytes(4, "big") + bytes([len(names)]) + names
        value += b"\x00" * (-len(value) % 4) + b"\x00" * 8
        dco.find("ConstValue").text = value.hex().upper()
        diag = self.diag_of(el)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
        diag.srn_terms.append(term)
        self.ends[(el.get("uid"), "value")] = (term.get("uid"), diag)
        diag.placed.append((ddo, el))
        for net in nets:
            self.net_type[net] = lvheap.strip_label(cls_tree)
        c = self.class_consts.setdefault(res["owner"], {"qual": qual, "lib_ref": res["lib_ref"], "uids": []})
        c["uids"].append(int(ddo.get("uid")))
        self.report.append("Constant %r <- %s (class %s)" % (name, src, qual[0]))

    def add_const(self, el):
        aixml_type = el.get("type")
        if (aixml_type or "").replace(" ", "") == "ref{UDClassInst}":
            return self.add_class_const(el)
        diag = self.diag_of(el)
        name = unescape(el.get("_name") or "")
        tree = type_tree(aixml_type, label=name or None, constant=True)
        term, src, _, _ = self.build_ddo(tree, None, "const", None)
        dco = term.find("dco")
        ddo = dco.find("ddo")
        old = int(ddo.get("uid"))
        ddo.set("uid", str(self.claim(el.get("uid"))))
        self.used_uids.discard(old)
        dco.find("ConstValue").text = const_value(tree, el.get("value"))
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
        diag.srn_terms.append(term)
        self.ends[(el.get("uid"), "value")] = (term.get("uid"), diag)
        diag.placed.append((ddo, el))
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = type_tree(aixml_type, constant=True)
        self.report.append("Constant %r <- %s" % (name, src))

    # palette FIXED constants: AIXML's name -> the bDFixed ddo's `cnst` code (absent for 0),
    # read off an AIXML-generated VI holding all ten that NI's exports use
    FIXED = {"0STR": "03", "0PTH": "17", "2PI": "05", "PI": "04", "EOL": "19", "CRTN": "00",
             "TAB": "02", "+INF": "0F", "MEPS": "1C", "NREF": "1A"}

    def add_fixed(self, el):
        """<FixedConst _name="PI"/>: a diagram constant whose ddo is a `bDFixed` carrying the
        constant's code; its type and value come with the part."""
        name = el.get("_name")
        code = self.FIXED.get(name)
        if code is None:
            raise Unsupported("FixedConst %r is not composable yet" % name)
        diag = self.diag_of(el)
        got = self.instantiate("fixed/%s" % code)
        if got is None:
            raise Unsupported("no part harvested for FixedConst %r" % name)
        term, donor_types, src = got
        ddo = term.find("dco/ddo")
        old = int(ddo.get("uid"))
        ddo.set("uid", str(self.claim(el.get("uid"))))
        self.used_uids.discard(old)
        self._set_label(ddo, "")
        tree = plain(lvheap.strip_label(donor_types[0]))
        lvheap.set_typeids(term, [self.heap_tid(tree) for _ in lvheap.typeid_elements(term)])
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": term.get("uid")}))
        diag.srn_terms.append(term)
        self.ends[(el.get("uid"), "value")] = (term.get("uid"), diag)
        diag.placed.append((ddo, el))
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = tree
        self.report.append("FixedConst %r <- %s" % (name, src))

    # attributes a Node may carry that the composer writes faithfully; anything else is a MODE
    # (operation=OR, aggregate=true, includeLow, dimensions ...) that changes what the node
    # computes, and ignoring it composed a VI that loads and computes something else - measured
    # on NI's In Range and Coerce.vi (aggregate=true)
    NODE_ATTRS = {"_name", "inputs", "outputs", "uid", "uid_parent", "_id"}
    PRIM_MODES = {"In Range and Coerce": ("includeHigh", "includeLow"),   # name -> mode attributes add_node writes
                  "Read from Text File": ("readLines",)}
    # a mode that is a bit of the prim's objFlags, read off probes/pmodes.xml through NI's own
    # converter: includeLow 0x200000, includeHigh 0x100000, neither 0 (NI's exports write the
    # attribute only when it is true, 21 of 27 In Range nodes with both)
    PRIM_MODE_BITS = {"In Range and Coerce": {"includeLow": 0x200000, "includeHigh": 0x100000},
                      # Read Lines: 640 -> 67109504 on NI's converter's build of a probe pair
                      # differing only in readLines="true", 2026-10-10 (lvpane_sweep.xml uses it)
                      "Read from Text File": {"readLines": 0x4000000}}

    def check_attrs(self, el, extra=()):
        if el.get("comment"):
            # a comment NI attaches to a node is documentation, like a FreeLabel: reported, not
            # composed - refused, it stopped eight of NI's VIs at their first such node
            self.report.append("skipped comment on %s %s (comments are not composed yet)"
                               % (unescape(el.get("_name") or el.tag), el.get("uid")))
        unknown = [a for a in el.attrib if a not in self.NODE_ATTRS and a not in extra and a != "comment"]
        if unknown:
            raise Unsupported("Node %r: mode %s is not composable yet" % (
                unescape(el.get("_name") or ""), ", ".join("%s=%s" % (a, el.get(a)) for a in unknown)))

    def add_node(self, el):
        name = unescape(el.get("_name"))
        rid = self.prim_by_name.get(name)
        if rid is None:
            raise Unsupported("Node %r is not a primitive this prototype knows" % name)
        self.check_attrs(el, self.PRIM_MODES.get(name, ()))
        diag = self.diag_of(el)
        got = self.instantiate("prim/%s" % rid, own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("Node %r (primResID %s) has no harvested part" % (name, rid))
        prim, donor_types, src = got
        bits = self.PRIM_MODE_BITS.get(name)
        if bits:
            f = prim.find("objFlags")
            if f is None:
                f = ET.Element("objFlags")
                prim.insert(0, f)
            v = int(f.text or 0)
            for attr, bit in bits.items():
                v = (v | bit) if (el.get(attr) or "").lower() == "true" else (v & ~bit)
            f.text = str(v)
            if not v:
                prim.remove(f)
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
                # a terminal the name tables do not know is only a problem when the AIXML WIRES
                # it - an unwired one keeps a placeholder, and a wired name that matches nothing
                # is refused below with the list of known names
                tname = "?parm%s" % pidx
            by_name[tname] = t
        ins, outs = pairs(el.get("inputs")), pairs(el.get("outputs"))
        for tname, net in ins + outs:
            if tname not in by_name and net is None:
                continue        # listed but unwired (NI's exports list every terminal): no need to place it
            if tname not in by_name:
                # NI's converter takes a terminal name in any case (`variant` for `Variant`)
                hit = [k for k in by_name if k.lower() == tname.lower()]
                if len(hit) == 1:
                    by_name[tname] = by_name[hit[0]]
                    continue
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
            kinds = {w.get("Type") for w in wired}
            if len(kinds) > 1 and not kinds <= set(NUM_RANK):
                # Not one polymorphic group but terminals that merely SHARED a donor type: Build
                # Path cut from Create New Palette.vi has a path on all three, and with a STRING
                # wired into `name or relative path` NI's converter types that terminal String,
                # leaves `base path` and the output Path, and draws no coercion dot (measured
                # 2026-10-10 on its build of lvpdc_export.xml). Joining made all three String and
                # the VI loaded broken.
                continue
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
        # An OUTPUT of another donor type follows the input's ARRAY SHAPE: Not A Number/Path/
        # Refnum? cut from a matrix example answers an array of booleans, and with a scalar wired
        # in LabVIEW answers one boolean. The output keeps its element type and loses (or gains)
        # as many array levels as the wired input differs from the donor's.
        def depth(x):
            n = 0
            while x is not None and x.get("Type") in ("Array", "TypeDef"):
                if x.get("Type") == "Array":
                    n += len(x.findall("Dimension"))
                kids = fields(x)
                x = kids[0] if kids else None
            return n

        def reshape(x, k):
            if k < 0:
                left = -k
                while left > 0:
                    if x.get("Type") != "Array" or len(x.findall("Dimension")) > left:
                        return None
                    left -= len(x.findall("Dimension"))
                    x = copy.deepcopy(elem_of(x))
                x.attrib.pop("Label", None)
                return x
            a = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
            for _ in range(k):
                ET.SubElement(a, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
            a.append(copy.deepcopy(x))
            return a
        deltas = {depth(new_types[i]) - depth(donor[i]) for i in wired_in}
        if len(deltas) == 1 and 0 not in deltas and name not in self.WHOLE_VALUE:
            k = deltas.pop()
            donor_in_depth = {depth(donor[i]) for i in wired_in}
            for j, t in enumerate(terms):
                out = int(t.find("dco").findtext("objFlags") or 0) & 1
                same_group = any(canon[j] == canon[i] for i in wired_in)
                if out and not same_group and j not in wired_in and depth(donor[j]) in donor_in_depth:
                    r = reshape(new_types[j], k)
                    if r is not None:
                        new_types[j] = r
        if name == "Create User Event" and "user event" in by_name:
            # its output is a UserEvent refnum around the datatype, NAMED by the datatype's label:
            # `Tick` in LabVIEW's own save of the user-event skeleton, and the event frame's
            # selector `<Tick>: User Event` must agree with it
            net = dict(ins).get("user event datatype")
            lab = self._source_label(net) if net else None
            dt = self.net_type.get(net) if net else None
            if dt is None or not lab:
                raise Unsupported("Create User Event %s: the datatype needs a LABELLED source - the label "
                                  "names the event" % el.get("uid"))
            u = ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "UserEvent", "Format": "inline", "Label": lab})
            inner = copy.deepcopy(dt)
            inner.set("Label", lab)
            u.append(inner)
            new_types[idx_of[id(by_name["user event"])]] = u
        if name == "Read from Text File" and (el.get("readLines") or "").lower() == "true" \
                and dict(ins).get("count") and "text" in by_name:
            # reading LINES with `count` wired answers an ARRAY of strings - NI's build of the
            # same probe types `text` Array{String}. With count unwired it stays one string
            # (CLAUDE.md, "A mode attribute can change a node's output type")
            a = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
            ET.SubElement(a, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
            ET.SubElement(a, "TypeDesc", {"Type": "String", "Prop1": "0xFFFFFFFF", "Format": "inline"})
            new_types[idx_of[id(by_name["text"])]] = a
        for tname, net in outs:
            if net:
                self.net_type[net] = new_types[idx_of[id(by_name[tname])]]
        term_types = list(new_types)
        if name in self.WHOLE_VALUE:
            # the wired input carries its SOURCE's label, which these write into their result:
            # NI's converter's build of lvai_run_and_read.xml flattens `error out` as
            # <Cluster><Name>error out</Name>..., the composed one as <Name></Name> (2026-10-10).
            # LabVIEW reads the name off the wire's SOURCE end - a fresh copy with only this
            # input labelled still wrote <Name></Name> - so a primitive output feeding one is
            # labelled too, as NI's build labels every primitive terminal.
            for i, net in wired_in.items():
                lab = self._source_label(net)
                if lab:
                    term_types[i] = copy.deepcopy(new_types[i])
                    term_types[i].set("Label", lab)
                    self._label_primitive_source(net, lab)
        lvheap.set_typeids(prim, [self.heap_tid(term_types[i]) for i in range(len(terms))])
        for i, t in enumerate(terms):
            if int(t.find("dco").findtext("objFlags") or 0) & 1 and donor_types[i].get("Label"):
                # an output's label is the terminal's own (`error out`), read by _source_label
                self.term_label[t.get("uid")] = donor_types[i].get("Label")
        for tname, t in by_name.items():
            self.ends[(el.get("uid"), tname)] = (t.get("uid"), diag)
        diag.nodes.append(prim)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": prim.get("uid")}))
        diag.placed.append((prim, el))
        self.report.append("Node %r = primResID %s <- %s" % (name, rid, src))

    # A primitive that takes its input WHOLE: its output does not follow the input's array shape
    # (Flatten To XML's `xml string` stays one string for an array in - the shape rule above made
    # it an array, measured 2026-10-10 on lvai_run_and_read.xml), and the input's LABEL is data
    # it writes out. Flatten To XML is the measured one; the other two flatten the same way.
    WHOLE_VALUE = {"Flatten To XML", "Flatten To String", "To Variant"}

    def _label_primitive_source(self, net, label):
        """Give the PRIMITIVE output driving `net` its own label on its type (see WHOLE_VALUE)."""
        for e in self._by_uid.values():
            for n, key in self.endpoints(e)[0]:
                if n != net or e.tag == "Control":
                    continue
                end = self.ends.get(key)
                if end is None or end[0] not in self.term_label or net not in self.net_type:
                    return
                term = next((x for x in end[1].el.iter() if x.get("uid") == end[0]), None)
                td = term.find("dco/typeDesc") if term is not None else None
                if td is not None:
                    labelled = copy.deepcopy(self.net_type[net])
                    labelled.set("Label", label)
                    td.text = "TypeID(%d)" % self.heap_tid(labelled)
                return

    def _source_label(self, net):
        """The label of the terminal that drives `net`: a control's name, a primitive output's own
        label, or the label on the source terminal's type. None when there is none."""
        for e in self._by_uid.values():
            srcs, _ = self.endpoints(e)
            for n, key in srcs:
                if n != net:
                    continue
                if e.tag in ("Control", "Constant"):
                    # (a constant's label names a user event: `<Constant _name="Tick">`)
                    return unescape(e.get("_name") or "") or None
                end = self.ends.get(key)
                if end is None:
                    return None
                tuid, diag = end
                if tuid in self.term_label:
                    return self.term_label[tuid]
                term = next((x for x in diag.el.iter() if x.get("uid") == tuid), None)
                td = term.find("dco/typeDesc") if term is not None else None
                m = lvheap.TYPEID_RE.fullmatch((td.text or "").strip()) if td is not None else None
                if m is None:
                    return None
                cons = int(m.group(1)) + INDEX_SHIFT - 1
                if not 0 < cons <= len(self.types.toplevel) or self.types.toplevel[cons - 1] is None:
                    return None
                return self.types.flat[self.types.toplevel[cons - 1]].get("Label")
        return None

    # ---- VI Server: property and invoke nodes ------------------------------------------------
    def _vi_server_table(self):
        t = getattr(self, "_vist", None)
        if t is None:
            path = os.path.join(self.parts.d, "vi-server-props.json")
            t = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {"classes": {}, "props": {}, "methods": {}}
            self._vist = t
        return t

    @staticmethod
    def _vi_server_class(el):
        m = re.fullmatch(r"\{(LV\.[A-Za-z0-9]+)\}", (el.get("type") or "").strip())
        if not m:
            raise Unsupported("%s %s: class %r is not composable yet" % (el.get("_name"), el.get("uid"), el.get("type")))
        return m.group(1)

    def _nested_property_row(self, cls, name):
        """`Label.Text`: the row for each level, the next level's class read off the previous
        level's data type - Label returns a refnum with CtlFlags 0x0005, which refnum-classes.tsv
        names LV.Text. None when any level is unknown or does not return an object."""
        refnum_type("LV.VI", plain_class=True)          # loads _REF_CLASSES
        by_flags = {}
        for c, (flags, _) in _REF_CLASSES.items():
            by_flags.setdefault(flags.upper(), c)
        owner, levels = cls, []
        parts_ = name.split(".")
        for i, part in enumerate(parts_):
            row = self._property_row(owner, part)
            if row is None:
                return None
            levels.append(dict(row, _name=part))
            if i < len(parts_) - 1:
                t = ET.fromstring(row["type"])
                if t.get("Type") != "Refnum" or t.get("RefType") != "LVObjCtl":
                    return None
                owner = by_flags.get((t.get("CtlFlags") or "").upper())
                if owner is None:
                    return None
        # the terminal's type is labelled with the WHOLE path, "Label.Text" - as NI's build of the
        # same node labels it - not with the last level's name alone
        return dict(levels[-1], levels=levels,
                    label=".".join(lvl.get("label") or lvl["_name"] for lvl in levels))

    def _property_row(self, cls, name):
        """The table row for property `name` on class `cls` - harvested off NI's VIs and probes
        (vi-server-props.json). A property seen only on OTHER classes is taken over when every
        class it was seen on agrees on its code (Visible is 104013824 on Array, Boolean, String
        ... - an inherited property keeps its code) AND LabVIEW's catalogue lists it for `cls`
        (docs/vi-server-properties.tsv lists inherited properties per class). Value and Disabled
        carry different codes per class (String 104093696, Path 104032256; Disabled and
        DisabledEnum) and are therefore never borrowed."""
        table = self._vi_server_table()["props"]
        row = table.get(cls, {}).get(name)
        if row is not None:
            return row
        seen = [other[name] for other in table.values() if name in other]
        if not seen or len({r["code"] for r in seen}) != 1 or len({r["type"] for r in seen}) != 1:
            return None
        if (cls, name) not in self._catalogue():
            return None
        return dict(seen[0], declaredBy=cls)

    def _method_by_row_label(self, cls, label):
        """The harvested method of `cls` whose part labels its FIRST row `label`, when exactly one
        does. Reads the part files only; nothing is instantiated."""
        import harvest_parts
        hits = []
        for k in self._vi_server_table().get("methods", {}).get(cls, {}):
            try:
                got = self.parts.get("invoke/%s/%s" % (cls, harvest_parts.meth_key(k)))
            except Unsupported:
                continue
            if got is None:
                continue
            heap, types, _ = got
            tel = lvheap.typeid_elements(heap)
            for t in heap.find("termList"):
                d = t.find("dco")
                if d is not None and d.get("class") == "invokeItem":
                    e = d.find("typeDesc")
                    i = next((n for n, x in enumerate(tel) if x is e), None)
                    if i is not None and types[i].get("Label") == label:
                        hits.append(k)
                    break
        return hits[0] if len(hits) == 1 else None

    def _wired_subclass(self, cls, el):
        """The class of the plain class refnum wired into `reference`, when it is a SUBCLASS of
        `cls` - judged from the catalogue, where a subclass lists every property of its parent
        (inherited ones included). None otherwise."""
        net = dict(pairs(el.get("inputs"))).get("reference")
        wired = self.net_type.get(net) if net else None
        if wired is None or wired.get("Type") != "Refnum" or wired.get("RefType") != "LVObjCtl" or fields(wired):
            return None
        refnum_type("LV.VI", plain_class=True)          # loads _REF_CLASSES
        flags = (wired.get("CtlFlags") or "").upper()
        sub = next((c for c, (f, _) in _REF_CLASSES.items() if f.upper() == flags), None)
        if sub is None or sub == cls:
            return None
        cat = self._catalogue()
        mine = {p for c, p in cat if c == cls}
        theirs = {p for c, p in cat if c == sub}
        return sub if mine and mine <= theirs else None

    def _catalogue(self):
        cat = getattr(self, "_cat", None)
        if cat is None:
            cat = set()
            with open(os.path.join(DOCS, "vi-server-properties.tsv"), encoding="utf-8") as f:
                for line in f:
                    row = line.rstrip("\r\n").split("\t")
                    m = re.fullmatch(r"\{(LV\.[A-Za-z0-9]+)\}", row[0]) if row else None
                    if m and len(row) > 1:
                        cat.add((m.group(1), row[1]))
            self._cat = cat
        return cat

    def _hgrow_terms(self, node):
        """The four fixed terminals of a property/invoke node by role: their DCO objFlags are
        0x10000 reference, 0x40001 reference out, 0x20000 error in, 0x80001 error out."""
        roles = {0x10000: "reference", 0x40001: "reference out", 0x20000: "error in (no error)", 0x80001: "error out"}
        out = {}
        for t in node.find("termList"):
            d = t.find("dco")
            if d is not None and d.get("class") == "hGrowCItem":
                r = roles.get(int(d.findtext("objFlags") or 0))
                if r:
                    out[r] = t
        return out

    def add_property_node(self, el):
        """A Property Node on a VI Server class (type="{LV.VI}"), composed from one harvested
        propNode: the four fixed terminals (reference, reference out, error in, error out) are
        kept and retyped with the class's refnum, then one propItem terminal per field, 16 px
        rows from y=18, each carrying a propItemInfo with the property's NAME, CODE, the data
        type twice (typeDesc labelled with the short name, dataTypeDesc bare) and the declaring
        class as VoidBlock{refnum} (omidTypeDesc) - measured on an AIXML-generated probe."""
        self.check_attrs(el, ("fields", "type", "link"))
        cls = self._vi_server_class(el)
        table = self._vi_server_table()
        crow = table["classes"].get(cls)
        if crow is None:
            raise Unsupported("Property Node %s: no property node on %s harvested yet" % (el.get("uid"), cls))
        link = unescape(el.get("link") or "")
        linked = None
        if link:
            # an IMPLICIT node, bound to a front-panel object by name: it names that object's
            # ddo, carries a label with its name and hides its reference terminals
            linked = getattr(self, "fp_info", {}).get(link)
            if linked is None:
                raise Unsupported("Property Node %s: no control %r to link to" % (el.get("uid"), link))
        fields_ = [unescape(f) for f in split_top(el.get("fields") or "") if f]

        def rows_for(c):
            out = []
            for f in fields_:
                mode, _, name = f.partition("+")
                if mode not in ("read", "write") or not name:
                    raise Unsupported("Property Node %s: field %r is not read+/write+" % (el.get("uid"), f))
                row = self._property_row(c, name)
                if row is None and "." in name:
                    row = self._nested_property_row(c, name)
                if row is None:
                    return None, name
                if 'Nested="True"' in (row.get("type") or "") and any(
                        n.get("Nested") == "True" and any(x.tag == "TypeDesc" and x.get("TypeID") is not None for x in n)
                        for n in ET.fromstring(row["type"]).iter("TypeDesc")):
                    # the table's type still names its DONOR's type ids inside a typedef (three
                    # rows repair_nested_types.py could not verify against their donor)
                    raise Unsupported("Property Node %s: the table's type for %r of %s carries its "
                                      "donor's type ids - not composable" % (el.get("uid"), name, c))
                out.append((mode, name, row))
            return out, None

        items, missing = rows_for(cls)
        if items is None:
            # NI's converter types the node by the MORE SPECIFIC class wired into it: lvbd_graft_
            # rewire.xml asks {LV.Terminal} for `Diagram` on what Control.Terminal returned, and
            # NI's build is a CtrlTerm node (LV.ControlTerminal, 0x400B) whose `Diagram` is
            # 104112130 - measured 2026-10-10. Taken only where the declared class would be
            # refused, so nothing that composed before changes.
            sub = self._wired_subclass(cls, el)
            if sub is not None and table["classes"].get(sub) is not None:
                items_sub, _ = rows_for(sub)
                if items_sub is not None:
                    cls, items, crow = sub, items_sub, table["classes"][sub]
        if items is None:
            raise Unsupported("Property Node %s: property %r of %s is not in the harvested table" % (el.get("uid"), missing, cls))
        diag = self.diag_of(el)
        got = self.instantiate("node/propNode/implicit" if linked else "node/propNode", own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("Property Node %s: no %s property node template harvested" % (el.get("uid"), "implicit" if linked else "explicit"))
        node, donor_types, src = got
        for i, e in enumerate(lvheap.typeid_elements(node)):
            e.set("_t", str(i))
        fixed = self._hgrow_terms(node)
        tl = node.find("termList")
        tpl = next((t for t in tl if t.find("dco") is not None and t.find("dco").get("class") == "propItem"), None)
        if tpl is None or len(fixed) != 4:
            raise Unsupported("Property Node %s: the harvested template is incomplete" % el.get("uid"))
        tpl = copy.deepcopy(tpl)
        for t in list(tl):
            if t not in fixed.values():
                tl.remove(t)
        ref_tree = refnum_type(cls, plain_class=True)
        ins0 = dict(pairs(el.get("inputs")))
        wired_ref = self.net_type.get(ins0.get("reference")) if ins0.get("reference") else None
        strict = wired_ref is not None and wired_ref.get("Type") == "Refnum" and fields(wired_ref)
        if strict:
            # behind a STRICTLY TYPED control reference (VI Server Reference element=...) the
            # node is typed with that refnum, its oMId names it, and its objFlags are 640, not
            # 0x2000280 (measured on an AIXML-generated probe, probes/pvsr.xml)
            ref_tree = lvheap.strip_label(wired_ref)
            node.find("objFlags").text = "640"
        for role in ("reference", "reference out"):
            # each fixed terminal its OWN labelled type, as LabVIEW writes them
            lab = copy.deepcopy(ref_tree)
            lab.set("Label", role)
            fixed[role].find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(lab)
            fixed[role].find("dco/typeDesc").attrib.pop("_t", None)
        for role in ("error in (no error)", "error out"):
            e = fixed[role].find("dco/typeDesc")
            lab = copy.deepcopy(donor_types[int(e.get("_t"))])
            lab.set("Label", role)
            e.text = "TypeID(%d)" % self.heap_tid(lab)
            del e.attrib["_t"]
        for e in node.iter("typeDesc"):
            if e.get("_t") is not None:
                e.text = "TypeID(%d)" % self.heap_tid(plain(lvheap.strip_label(donor_types[int(e.get("_t"))])))
                del e.attrib["_t"]
        ins = dict(pairs(el.get("inputs")))
        outs = dict(pairs(el.get("outputs")))
        dl = node.find("dcoList")
        for c in list(dl):
            dl.remove(c)
        width = int(lvheap.parse_bounds(node.find("bounds").text)[3] - lvheap.parse_bounds(node.find("bounds").text)[1])
        for k, (mode, name, row) in enumerate(items):
            t = copy.deepcopy(tpl)
            lvheap.renumber(t, self.alloc)
            d = t.find("dco")
            dt = ET.fromstring(row["type"])
            lab = copy.deepcopy(dt)
            if row.get("label"):
                lab.set("Label", row["label"])
            for e in t.iter():
                e.attrib.pop("_t", None)
            d.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(lab)
            f = d.find("objFlags")
            if mode == "read":
                if f is None:
                    f = ET.Element("objFlags")
                    d.insert(0, f)
                f.text = "1"
            elif f is not None:
                d.remove(f)
            d.find("termBounds").text = lvheap.fmt_bounds((18 + 16 * k, 0, 34 + 16 * k, width))
            plist = d.find("propList")
            template = plist.find("SL__arrayElement")
            # A NESTED property (Label.Text) is ONE terminal carrying one propItemInfo PER LEVEL -
            # Label on the control, then Text on the text object Label returns - measured on NI's
            # converter's own build of lvbd_graft_paste.xml. A plain property has one level.
            levels = row.get("levels") or [dict(row, _name=name)]
            for c in list(plist):
                plist.remove(c)
            for lvl in levels:
                info = copy.deepcopy(template)
                if len(levels) > 1:
                    lvheap.renumber(info, self.alloc)
                decl = refnum_type(lvl.get("declaredBy") or cls, plain_class=True)
                vb = ET.Element("TypeDesc", {"Type": "VoidBlock", "Format": "inline"})
                vb.append(decl)
                info.find("omidTypeDesc").text = "TypeID(%d)" % self.heap_tid(vb)
                info.find("dataTypeDesc").text = "TypeID(%d)" % self.heap_tid(ET.fromstring(lvl["type"]))
                # an ALIAS row (alias_props.py) maps NI's export name onto the one LabVIEW stores:
                # "Block Diagram Window:Open" is "BDWin.Open" in the heap
                info.find("PropItemName").text = '"%s"' % (lvl.get("heapName") or lvl.get("_name") or name)
                pc = info.find("PropItemCode")
                if lvl["code"]:
                    if pc is None:
                        pc = ET.SubElement(info, "PropItemCode")
                    pc.text = str(lvl["code"])
                elif pc is not None:
                    info.remove(pc)
                plist.append(info)
            plist.set("elements", str(len(levels)))
            tf = t.find("objFlags")
            if mode == "write":
                if tf is None:
                    tf = ET.Element("objFlags")
                    t.insert(0, tf)
                tf.text = "32768"
            elif tf is not None:
                t.remove(tf)
            tl.append(t)
            ET.SubElement(dl, "SL__arrayElement", {"uid": d.get("uid")})
            self.ends[(el.get("uid"), name)] = (t.get("uid"), diag)
            if mode == "read" and outs.get(name):
                self.net_type[outs[name]] = dt
        tl.set("elements", str(len(tl)))
        dl.set("elements", str(len(dl)))
        for role, t in fixed.items():
            self.ends[(el.get("uid"), role)] = (t.get("uid"), diag)
            if linked and role in ("reference", "reference out"):
                continue        # hidden (objFlags 0x800000), as the template has them
            if role in ("reference", "error in (no error)"):
                tf = t.find("objFlags")
                wired = bool(ins.get(role))
                if wired and tf is None:
                    tf = ET.Element("objFlags")
                    t.insert(0, tf)
                if tf is not None:
                    tf.text = str((int(tf.text or 0) | 0x8000) if wired else (int(tf.text or 0) & ~0x8000))
                    if tf.text == "0":
                        t.remove(tf)
        if outs.get("reference out"):
            self.net_type[outs["reference out"]] = ref_tree
        if outs.get("error out"):
            self.net_type[outs["error out"]] = type_tree("cluster{bool.status,int32.code,string.source}")
        b = lvheap.parse_bounds(node.find("bounds").text)
        node.find("bounds").text = lvheap.fmt_bounds((b[0], b[1], b[0] + 18 + 16 * len(items) + 1, b[1] + width))
        node.find("nodeName").text = '"%s"' % crow["nodeName"]
        node.find("oMId").text = self._om_id_of(ref_tree) if strict else self._om_id(cls)
        if linked:
            node.find("ddo").set("uid", linked[2].find("ddo").get("uid"))
            lab = node.find("label")
            if lab is not None:
                lab.find("textRec/text").text = '"%s"' % link
                lb = lvheap.parse_bounds(lab.find("bounds").text)
                lab.find("bounds").text = lvheap.fmt_bounds((lb[0], lb[1], lb[2], lb[1] + max(8, 6 * len(link))))
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Property Node %s %s <- %s" % (cls, ", ".join(n for _, n, _ in items), src))

    def add_invoke_node(self, el):
        """An Invoke Node: a whole harvested node per class and method (invoke/<class>/<method>),
        its parameter rows as LabVIEW laid them out. A row is two invokeItem terminals - the left
        one an input, the right one an output (DCO objFlags bit 0), Void where the row has no
        such side; the return value is the row named after the method. Terminals are named by
        the label of their type."""
        self.check_attrs(el, ("type", "target"))
        cls = self._vi_server_class(el)
        meth = unescape(el.get("target") or "")
        import harvest_parts
        got = self.instantiate("invoke/%s/%s" % (cls, harvest_parts.meth_key(meth)), own_uid=el.get("uid"))
        if got is None:
            # The part is filed under the name the HEAP stores; AIXML may spell the method the way
            # NI's export does ("Save VI Icon to File" for "VI Icon.Save to File"). alias_props.py
            # learns those pairs into the methods table as rows carrying `heapName`.
            alias = self._vi_server_table().get("methods", {}).get(cls, {}).get(meth, {}).get("heapName")
            if alias:
                got = self.instantiate("invoke/%s/%s" % (cls, harvest_parts.meth_key(alias)), own_uid=el.get("uid"))
        if got is None:
            # LabVIEW's CATALOGUE names a method one way and its export another - "Save VI Icon to
            # File" against "VI Icon.Save to File" - and NI's converter takes both. The one known
            # method of this class with exactly the same WORDS is the same method; two such and
            # it stays a refusal, because then it would be a guess.
            def words(name):
                return sorted(w for w in re.split(r"[ .:]+", name.lower()) if w)
            same = [k for k in self._vi_server_table().get("methods", {}).get(cls, {})
                    if words(k) == words(meth) and k != meth]
            if len(same) == 1:
                got = self.instantiate("invoke/%s/%s" % (cls, harvest_parts.meth_key(same[0])), own_uid=el.get("uid"))
        if got is None:
            # The catalogue's SHORT name - `Discon Typedef` - is the label LabVIEW gives the
            # method's first row; the heap stores `Disconnect From Typedef`. Measured 2026-10-10
            # on NI's converter's build of lvai_typedef_discon.xml. The one harvested method of
            # the class whose first row carries that label is the method; two stay a refusal.
            hit = self._method_by_row_label(cls, meth)
            if hit is not None:
                got = self.instantiate("invoke/%s/%s" % (cls, harvest_parts.meth_key(hit)), own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("Invoke Node %s: no invoke node %s.%s harvested yet" % (el.get("uid"), cls, meth))
        node, donor_types, src = got
        diag = self.diag_of(el)
        tel = lvheap.typeid_elements(node)
        if len(tel) != len(donor_types):
            raise Unsupported("Invoke Node %s: part has %d typeDescs for %d types" % (el.get("uid"), len(tel), len(donor_types)))
        names = {}
        for e, tree in zip(tel, donor_types):
            e.text = "TypeID(%d)" % self.heap_tid(tree)
        ins = dict(pairs(el.get("inputs")))
        outs = dict(pairs(el.get("outputs")))
        for role, t in self._hgrow_terms(node).items():
            names[role] = t
        for t, tree in zip([x for x in node.find("termList")], [None] * len(node.find("termList"))):
            d = t.find("dco")
            if d is None or d.get("class") != "invokeItem":
                continue
            ty = self.parts_type_of(t, donor_types, tel)
            if ty is None or ty.get("Type") == "Void":
                continue
            out = int(d.findtext("objFlags") or 0) & 1
            # A TYPEDEF parameter carries its name on the type INSIDE it, not on the wrapper:
            # Control.Move's `position` is a TypeDef of LVPositionTypeDef.ctl around a cluster
            # labelled "position" (Phase 3, 2026-10-10 - six helpers refused for it).
            label = ty.get("Label") or ""
            if not label and ty.get("Type") == "TypeDef":
                inner = next((c for c in ty if c.tag == "TypeDesc"), None)
                label = (inner.get("Label") or "") if inner is not None else ""
            names[label + ("\x00out" if out else "\x00in")] = t
        # The RETURN value is the output side of the method's first row, labelled with the
        # method's DISPLAY name (`Open Library`, `Get Control Value Variant`). An AIXML output
        # name that matches no output terminal is that return value: NI's converter's builds
        # wire `Library.Open` (lvai_add_one_to_library.xml), `Controls` on Ctrl Val.Get All -
        # the name of its boolean INPUT - (lvai_run_and_read.xml) and `Value` on Ctrl Val.Get
        # (lvdqmh_module_types.xml) to it, measured 2026-10-10. Two such names on one node stay
        # a refusal: which one is the return would be a guess.
        ret = None
        for t in node.find("termList"):
            d = t.find("dco")
            if d is not None and d.get("class") == "invokeItem" and int(d.findtext("objFlags") or 0) & 1:
                ty = self.parts_type_of(t, donor_types, tel)
                ret = t if ty is not None and ty.get("Type") != "Void" else None
                break

        def find(tname, side):
            # (an Element with no children is FALSE in a boolean context - never `a or b` on them)
            t = names.get(tname)
            if t is None:
                t = names.get(tname + "\x00" + side)
            if t is None:
                # NI's converter takes a parameter in any case: `Path` for Library.Open's `path`
                want = (tname.lower(), (tname + "\x00" + side).lower())
                hit = {id(v): v for k, v in names.items() if k.lower() in want}
                if len(hit) == 1:
                    t = next(iter(hit.values()))
            return t

        unresolved = [n for n, net in outs.items() if net and find(n, "out") is None]

        for tname, net in ins.items():
            t = find(tname, "in")
            if t is None and net is None:
                continue    # listed and unwired, as NI's exports list every parameter
            if t is None:
                raise Unsupported("Invoke Node %s: %s.%s has no input %r" % (el.get("uid"), cls, meth, tname))
            self.ends[(el.get("uid"), tname)] = (t.get("uid"), diag)
        for tname, net in outs.items():
            t = find(tname, "out")
            if t is None and net is None:
                continue
            if t is None and ret is not None and unresolved == [tname]:
                t = ret         # the return value, by whatever name the document gives it
            if t is None:
                raise Unsupported("Invoke Node %s: %s.%s has no output %r" % (el.get("uid"), cls, meth, tname))
            self.ends[(el.get("uid"), out_key(el, tname))] = (t.get("uid"), diag)
            if net:
                ty = self.parts_type_of(t, donor_types, tel)
                self.net_type[net] = lvheap.strip_label(ty) if ty is not None else None
        # Term objFlags 0x8000 marks an INPUT as wired, and a harvested part keeps its DONOR's
        # wiring: Paste From Clipboard.vi wires TopLevelDiagram.Paste's `Pos`, which NI's own
        # build of lvbd_graft_paste.xml leaves unwired with the flag clear. So the flag follows
        # this document's wiring, as add_property_node already does it. Not a repair: measured
        # 2026-10-10 as an A/B, the donor's flag left on the unwired Pos still loads executable
        # (that VI's break was its typedef's type ids - lvheap.Bundle._inline).
        wired = set()
        for tname, net in ins.items():
            t = find(tname, "in")
            if t is not None and net:
                wired.add(t.get("uid"))
        for t in node.find("termList"):
            d = t.find("dco")
            if d is None or int(d.findtext("objFlags") or 0) & 1:
                continue        # an output
            tf = t.find("objFlags")
            on = t.get("uid") in wired
            if on and tf is None:
                tf = ET.Element("objFlags")
                t.insert(0, tf)
            if tf is not None:
                tf.text = str((int(tf.text or 0) | 0x8000) if on else (int(tf.text or 0) & ~0x8000))
                if tf.text == "0":
                    t.remove(tf)
        node.find("oMId").text = self._om_id(cls)
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Invoke Node %s.%s <- %s" % (cls, meth, src))

    def _om_id_of(self, tree):
        """oMId for an arbitrary class refnum type (a strictly typed control refnum included):
        the consolidated index of the type without its label."""
        bare = lvheap.strip_label(tree)
        key = ET.tostring(bare, encoding="unicode")
        self._om_ids = getattr(self, "_om_ids", {})
        if key not in self._om_ids:
            cons = self.types.use(self.types.register(bare))
            if cons > 0x7FFF:
                raise Unsupported("oMId %d is not encodable yet" % cons)
            self._om_ids[key] = "%04X" % cons
        return self._om_ids[key]

    def _om_id(self, cls):
        """A property or invoke node's oMId is NOT a class number: it is the index, in THIS VI's
        flat type list (VCTP), of the class's bare refnum type - measured: NI's probe wrote 0003
        and 0004 where flat types 3 and 4 were the VI and Application refnums, and a composed
        VI carrying those numbers over had LabVIEW resolve neither class (property shown as
        {Generic} read+Property, oMId rewritten 000000 on save) and was broken."""
        # the CONSOLIDATED (TopLevel) index, which NI's file kept equal to the flat one
        return self._om_id_of(refnum_type(cls, plain_class=True))

    @staticmethod
    def parts_type_of(term, donor_types, tel):
        e = term.find("dco/typeDesc")
        for x, tree in zip(tel, donor_types):
            if x is e:
                return tree
        return None

    # ---- cluster and array nodes: classes of their own, not prims -------------------------
    AGG_NODES = {"Bundle": "node/mux", "Unbundle": "node/demux",
                 "Bundle By Name": "node/nMux/bundle", "Unbundle By Name": "node/nMux/unbundle",
                 "Index Array": "node/aIndx", "Build Array": "node/aBuild"}

    def _term_from(self, tpl, tree, flags=None, dco_flags=None, index=None):
        """A fresh copy of a template terminal, typed `tree`; `index` is the field number a
        nMux DCO carries as <i> (absent for field 0, as LabVIEW writes it)."""
        t = copy.deepcopy(tpl)
        lvheap.renumber(t, self.alloc)
        dco = t.find("dco")
        if flags is not None:
            t.find("objFlags").text = str(flags)
        if dco_flags is not None:
            f = dco.find("objFlags")
            if dco_flags == 0 and f is not None:
                dco.remove(f)
            elif dco_flags:
                if f is None:
                    f = ET.Element("objFlags")
                    dco.insert(0, f)
                f.text = str(dco_flags)
        i = dco.find("i")
        if index is not None:
            if index == 0 and i is not None:
                dco.remove(i)
            elif index:
                if i is None:
                    i = ET.SubElement(dco, "i")
                i.text = str(index)
        dco.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(tree)
        return t

    def add_agg_node(self, el):
        """Bundle (mux), Unbundle (demux), Bundle/Unbundle By Name (nMux), Index Array (aIndx)
        and Build Array (aBuild), measured on an AIXML-generated VI:

          Unbundle By Name  [cluster in = dcoAgg, one out per field with <i> = field number]
          Bundle By Name    [out = dcoAgg whose termList is [out, cluster in], cluster in
                             (a bare <dco uid=agg/>, objFlags 0x28040), one in per field, <i>]
          Bundle            the same with one in per FIELD OF THE CLUSTER, wired (0x8040) or
                            not (0x40); its terminals are named after the fields
          Unbundle          [cluster in = dcoAgg, one out per field]
          Index Array       [array in = srcDCO, element out = dcoAgg, index in]
          Build Array       [array out = dcoAgg, one in per element]
        The terminal list is rebuilt from the WIRED types; dcoList names the per-field DCOs."""
        name = unescape(el.get("_name"))
        self.check_attrs(el, {"Unbundle By Name": ("fields",), "Bundle By Name": ("fields",),
                              "Build Array": ("concat",), "Index Array": ("dimensions",)}.get(name, ()))
        diag = self.diag_of(el)
        got = self.instantiate(self.AGG_NODES[name], own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("no part harvested for %r" % name)
        node, _, src = got
        for d in node.iter("dco"):
            for e in d.findall("inplace"):
                d.remove(e)
        tl = node.find("termList")
        terms = list(tl)
        ins, outs = pairs(el.get("inputs")), pairs(el.get("outputs"))
        dl = node.find("dcoList")
        for c in list(dl):
            dl.remove(c)
        ends = {}

        def wired_type(net, what):
            if net not in self.net_type:
                raise Unsupported("%s %s: the type of %s (%r) is not known" % (name, el.get("uid"), what, net))
            return self.net_type[net]

        if name in ("Unbundle By Name", "Unbundle"):
            agg, tpl = terms[0], terms[1]
            cin = [n for t, n in ins if t in ("input cluster", "cluster")]
            ctype = wired_type(cin[0] if cin else None, "the cluster")
            inner = typedef_inner(ctype)
            if inner.get("Type") != "Cluster":
                raise Unsupported("%s %s: input is %s, not a cluster" % (name, el.get("uid"), inner.get("Type")))
            # a TYPEDEF'd cluster (a subVI's pane hands one out) keeps the typedef on the
            # terminal; its fields are the wrapped cluster's
            fs = fields(inner)
            agg.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(ctype)
            for c in terms[1:]:
                tl.remove(c)
            ends["input cluster" if name == "Unbundle By Name" else "cluster"] = (agg, None)
            labels = [f.get("Label") or "element" for f in fs]
            if name == "Unbundle By Name":
                want = [unescape(x) for x in split_top(el.get("fields") or "") if x] or [t for t, _ in outs]
                for w in want:
                    if w not in labels:
                        raise Unsupported("%s %s: no field %r (has %s)" % (name, el.get("uid"), w, ", ".join(labels)))
                picked = [(w, labels.index(w)) for w in want]
            else:
                # every field, keyed like endpoints() keys repeated names: element, element#1...
                picked = [(rep_key(labels[i], labels[:i].count(labels[i])), i) for i in range(len(fs))]
            for key, idx in picked:
                t = self._term_from(tpl, fs[idx], index=idx if name == "Unbundle By Name" else None)
                tl.append(t)
                ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                ends[key] = (t, fs[idx])
            for key, net in keyed(outs):
                if key not in ends:
                    raise Unsupported("%s %s: no output %r" % (name, el.get("uid"), key))
                if net:
                    self.net_type[net] = unlabel(ends[key][1])
            for key, (t, _) in ends.items():
                self.ends[(el.get("uid"), key)] = (t.get("uid"), diag)
        elif name in ("Bundle By Name", "Bundle"):
            agg, cref, tpl = terms[0], terms[1], terms[2]
            cin = [n for t, n in ins if t in ("input cluster", "cluster")]
            if (not cin or cin[0] is None) and name == "Bundle":
                # NO cluster wired: the output is a cluster of the elements, each field named
                # like its terminal (NI's "Frequency Analysis of a Filter Design.vi": Fs, #s);
                # the unwired cluster input keeps its terminal, objFlags 0x20040 not 0x28040
                ctype = ET.Element("TypeDesc", {"Type": "Cluster", "Format": "inline"})
                for tname, net in ins:
                    if tname in ("cluster", "input cluster"):
                        continue
                    f = copy.deepcopy(unlabel(wired_type(net, "an element")))
                    if tname != "element":
                        f.set("Label", tname)
                    ctype.append(f)
                cref.find("objFlags").text = str(0x20040)
            elif not cin or cin[0] is None:
                raise Unsupported("%s %s: a cluster input must be wired" % (name, el.get("uid")))
            else:
                ctype = wired_type(cin[0], "the cluster")
            if typedef_inner(ctype).get("Type") != "Cluster":
                raise Unsupported("%s %s: input is %s, not a cluster" % (name, el.get("uid"), typedef_inner(ctype).get("Type")))
            fs = fields(typedef_inner(ctype))
            labels = [f.get("Label") or "element" for f in fs]
            agg.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(ctype)
            for c in terms[2:]:
                tl.remove(c)
            self.ends[(el.get("uid"), "input cluster" if name == "Bundle By Name" else "cluster")] = (cref.get("uid"), diag)
            self.ends[(el.get("uid"), "output cluster")] = (agg.get("uid"), diag)
            field_ins = [(t, n) for t, n in ins if t not in ("input cluster", "cluster")]
            if name == "Bundle By Name":
                for fname, net in field_ins:
                    if fname not in labels:
                        raise Unsupported("%s %s: no field %r (has %s)" % (name, el.get("uid"), fname, ", ".join(labels)))
                    idx = labels.index(fname)
                    t = self._term_from(tpl, fs[idx], flags=32832, index=idx)
                    tl.append(t)
                    ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                    self.ends[(el.get("uid"), fname)] = (t.get("uid"), diag)
            else:
                # one terminal per field, in field order; AIXML names them after the fields and
                # repeats "element" for unlabelled ones
                given = dict(keyed(field_ins))
                keys = []
                for idx, f in enumerate(fs):
                    k = rep_key(labels[idx], labels[:idx].count(labels[idx]))
                    keys.append(k)
                    t = self._term_from(tpl, f, flags=32832 if given.get(k) else 64, dco_flags=0)
                    tl.append(t)
                    ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                    self.ends[(el.get("uid"), k)] = (t.get("uid"), diag)
                unknown = [k for k in given if k not in keys]
                if unknown:
                    raise Unsupported("%s %s: no field %r (has %s)" % (name, el.get("uid"), unknown[0], ", ".join(labels)))
            for _, net in outs:
                if net:
                    self.net_type[net] = unlabel(ctype)
        elif name == "Index Array":
            # Measured on an AIXML-generated VI with four shapes: the terminals run
            # [array, out, its indices..., out, its indices...]; an index DCO carries 0x200000
            # for the FIRST dimension and 0x400000 for the LAST (both on a 1D array), plus
            # 0x10000 when disabled - a disabled index turns the output into a subarray; every
            # output after the first is DCO 0x40001, and the node 0x80000 once there are two.
            src_t, out_t, idx_t = terms[0], terms[1], terms[2]
            arr = [n for t, n in ins if t == "array"]
            atype = wired_type(arr[0] if arr else None, "the array")
            n = len(atype.findall("Dimension")) if atype.get("Type") == "Array" else 0
            if n not in (1, 2):
                raise Unsupported("Index Array %s: a %s input is not composable yet"
                                  % (el.get("uid"), "%dD array" % n if n else atype.get("Type")))
            idx_ins = [(k, net) for (k, net), (t, _) in zip(keyed(ins), ins) if t != "array"]
            raw = [t for t, _ in ins if t != "array"]
            if not idx_ins:
                idx_ins, raw = [("index", None)], ["index"]
            if len(idx_ins) % n:
                raise Unsupported("Index Array %s: %d indices for a %dD array" % (el.get("uid"), len(idx_ins), n))
            for c in terms[1:]:
                tl.remove(c)
            src_t.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(atype)
            self.ends[(el.get("uid"), "array")] = (src_t.get("uid"), diag)
            out_keys = [k for k, _ in keyed(outs)]
            out_nets = [net for _, net in keyed(outs)]
            int32 = type_tree("int32")
            for g in range(len(idx_ins) // n):
                group = idx_ins[g * n:(g + 1) * n]
                disabled = [raw[g * n + d].startswith("disabled") for d in range(n)]
                keep = sum(disabled)
                if keep:
                    ot = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
                    for _ in range(keep):
                        ET.SubElement(ot, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
                    ot.append(copy.deepcopy(unlabel(elem_of(atype))))
                else:
                    ot = unlabel(elem_of(atype))
                o = out_t if g == 0 else self._term_from(out_t, ot, dco_flags=0x40001)
                o.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(ot)
                tl.append(o)
                if g > 0:
                    ET.SubElement(dl, "SL__arrayElement", {"uid": o.find("dco").get("uid")})
                if g < len(out_keys):
                    self.ends[(el.get("uid"), out_keys[g])] = (o.get("uid"), diag)
                    if out_nets[g]:
                        self.net_type[out_nets[g]] = ot
                for d, (key, net) in enumerate(group):
                    flags = (0x200000 if d == 0 else 0) | (0x400000 if d == n - 1 else 0) | \
                        (0x10000 if disabled[d] else 0)
                    t = self._term_from(idx_t, int32, flags=32832 if net else 64, dco_flags=flags)
                    tl.append(t)
                    ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                    self.ends[(el.get("uid"), key)] = (t.get("uid"), diag)
            if len(idx_ins) // n > 1:
                f = node.find("objFlags")
                if f is None:
                    f = ET.Element("objFlags")
                    node.insert(0, f)
                f.text = str(int(f.text or 0) | 0x80000)
        else:   # Build Array
            agg, tpl = terms[0], terms[1]
            types = [wired_type(n, "an input") for _, n in ins]
            concat = el.get("concat") == "true" or any(t == "array" for t, _ in ins)
            if concat:
                # CONCATENATING: every `array` input is an array of the output's own type, its
                # DCO flagged 0x10000, the first one 0x810000 (measured on an AIXML-generated
                # Build Array of two string arrays); an `element` input stays unflagged
                arrays = [wt for (tn, _), wt in zip(ins, types) if tn == "array"]
                if any(a.get("Type") != "Array" or len(a.findall("Dimension")) != 1 for a in arrays):
                    raise Unsupported("Build Array %s: only 1D arrays are concatenated yet" % el.get("uid"))
                et = numeric_join([unlabel(elem_of(a)) for a in arrays] +
                                  [wt for (tn, _), wt in zip(ins, types) if tn != "array"])
            else:
                et = numeric_join(types) if types else type_tree("double")
            atype = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
            ET.SubElement(atype, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
            atype.append(copy.deepcopy(unlabel(et)))
            agg.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(atype)
            for c in terms[1:]:
                tl.remove(c)
            for k, (((key, net), (tname, _)), wt) in enumerate(zip(zip(keyed(ins), ins), types)):
                flags = None
                if concat and tname == "array":
                    flags = 0x810000 if k == 0 else 0x10000
                t = self._term_from(tpl, wt, dco_flags=flags)
                tl.append(t)
                ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                self.ends[(el.get("uid"), key)] = (t.get("uid"), diag)
            self.ends[(el.get("uid"), "appended array")] = (agg.get("uid"), diag)
            for _, net in outs:
                if net:
                    self.net_type[net] = atype
        tl.set("elements", str(len(tl)))
        dl.set("elements", str(len(dl)))
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Node %r <- %s" % (name, src))

    # ---- local variables and feedback nodes ---------------------------------------------------
    def add_local(self, el):
        """A Local Variable is a gRef node with one terminal typed like its control, pointing at
        the control by its position in the front panel's ddoList (paramIdx, absent for 0) -
        measured on an AIXML-generated VI: `copy`, third on the panel, carries paramIdx 2."""
        self.check_attrs(el, ("element",))
        name = unescape(el.get("element") or "")
        info = getattr(self, "fp_info", {}).get(name)
        if info is None:
            raise Unsupported("Local Variable %s: no control or indicator named %r" % (el.get("uid"), name))
        idx, tree, fpdco = info
        # the control's fPDCO counts every diagram object that refers to it - its terminal AND
        # each local (termListLength 2 with one local, measured); left at 1, the local's DCO was
        # "insane {sub}" and LabVIEW crashed on the next load
        tll = fpdco.find("termListLength")
        if tll is not None:
            tll.text = str(int(tll.text or 1) + 1)
        ins, outs = pairs(el.get("inputs")), pairs(el.get("outputs"))
        write = bool(ins)
        diag = self.diag_of(el)
        node, _, src = self.instantiate("node/gRef/%s" % ("write" if write else "read"))
        term = node.find("termList")[0]
        dco = term.find("dco")
        pi = dco.find("paramIdx")
        if idx == 0 and pi is not None:
            dco.remove(pi)
        elif idx:
            if pi is None:
                pi = ET.SubElement(dco, "paramIdx")
            pi.text = str(idx)
        dco.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(tree)
        # the gRef's label names the VI that OWNS the control, not the control: LabVIEW's own
        # local read "pseq.vi"; the control's name there was an insane GRefDCO {sub} and a crash
        lab = node.find("label/textRec/text")
        if lab is not None:
            lab.text = '"%s"' % self.vi_name.replace("\\", "\\\\").replace('"', '\\"')
        self.ends[(el.get("uid"), name)] = (term.get("uid"), diag)
        for _, net in outs:
            if net:
                self.net_type[net] = unlabel(tree)
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Local Variable %r <- %s" % (name, src))

    def add_global(self, el):
        """A Global Variable: the same gRef node as a local, plus connectorTM - the global VI's
        pane - and the call link pair (VIVI + IUVI naming the node), as a subVI call has; the
        terminal is typed like the global's control and paramIdx is that control's position in
        the global VI's ddoList. Read off NI's Add to Project.vi (Localized Strings.vi)."""
        self.check_attrs(el, ("element", "target"))
        name = unescape(el.get("element") or "")
        target = unescape(el.get("target") or "")
        if self.resolver is None:
            self.resolver = resolve.Resolver()
        res = self.resolver.resolve(target, self.search_dirs, getattr(self, "caller_file", None))
        if "error" in res:
            raise Unsupported("Global Variable %s: %s" % (el.get("uid"), res["error"]))
        g = lvheap.Bundle(_extract_callee(res["path"]))
        if (g.main.find("LVSR/Section/Instrument").get("Type") or "") != "Global":
            raise Unsupported("Global Variable %s: %r is not a global VI" % (el.get("uid"), target))
        order = [x.get("uid") for x in (g.fp.find("root/ddoList") if g.fp.find("root/ddoList") is not None else [])]
        hit = None
        for f in g.fp.iter("SL__arrayElement"):
            if f.get("class") == "fPDCO" and vi_label(f.find("ddo")) == name:
                hit = f
                break
        if hit is None or hit.get("uid") not in order:
            raise Unsupported("Global Variable %s: %r has no control %r" % (el.get("uid"), target, name))
        idx = order.index(hit.get("uid"))
        e = lvheap.typeid_elements(hit)[0]
        tree = g.heap_type_tree(int(lvheap.TYPEID_RE.fullmatch(e.text.strip()).group(1)))
        conp = int(g.main.find("CONP/Section/TypeDesc").get("TypeID"))
        pane = g.tree(g.flat_of_consolidated(conp))
        ins, outs = pairs(el.get("inputs")), pairs(el.get("outputs"))
        write = any(n for _, n in ins)
        diag = self.diag_of(el)
        got = self.instantiate("node/gRef/global/read", own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("no global variable part harvested")
        node, _, src = got
        term = node.find("termList")[0]
        dco = term.find("dco")
        if write:
            # a WRITING gRef: the terminal is a sink (term objFlags 0x8000) and its DCO drops
            # the output bit - the difference between the local read and write parts
            tf = term.find("objFlags")
            if tf is None:
                tf = ET.Element("objFlags")
                term.insert(0, tf)
            tf.text = str(int(tf.text or 0) | 0x8000)
            df = dco.find("objFlags")
            if df is not None:
                dco.remove(df)
        pi = dco.find("paramIdx")
        if idx == 0 and pi is not None:
            dco.remove(pi)
        elif idx:
            if pi is None:
                pi = ET.SubElement(dco, "paramIdx")
            pi.text = str(idx)
        dco.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(tree)
        node.find("connectorTM").text = "TypeID(%d)" % self.heap_tid(pane)
        for x in node.iter("typeDesc"):
            x.attrib.pop("_t", None)
        lab = node.find("label/textRec/text")
        if lab is not None:
            lab.text = '"%s"' % os.path.basename(res["path"])
        self.ends[(el.get("uid"), name)] = (term.get("uid"), diag)
        for _, net in outs:
            if net:
                self.net_type[net] = unlabel(tree)
        link = self.links.setdefault(res["path"], dict(res, pane=pane, offsets=[]))
        link["offsets"].append(int(node.get("uid")))
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Global Variable %r of %r <- %s" % (name, target, src))

    def add_feedback(self, el):
        """A Feedback Node is two heap nodes naming each other - hiddenFBNode (previous value =
        leftFeedback, initializer = initFeedback) and slaveFBInputNode (next value =
        rightFeedback) - built like a shift register. Both sit in the diagram's node and z
        lists and share their bounds."""
        self.check_attrs(el)
        in_loop = None
        if (el.get("uid_parent") or "root") != "root":
            # INSIDE A LOOP the initializer is not the hidden node's: LabVIEW moves initFeedback
            # into the LOOP's termList (a border terminal fed from outside) naming the loop in
            # iFeedbackLoop - read off NI's "Feedback Node - Building an Array.vi". Composed with
            # the initializer on the node it was "good to bad after compiling".
            p = self._by_uid.get(el.get("uid_parent"))
            in_loop = next((x for x in self.loops if x[1].owner is p), None) if p is not None else None
            if in_loop is None:
                raise Unsupported("Feedback Node %s: only at the top level or directly in a loop" % el.get("uid"))
        ins, outs = dict(pairs(el.get("inputs"))), dict(pairs(el.get("outputs")))
        # (an Element with no children is falsy, so no `a or b` here)
        tree = self.net_type.get(ins.get("initializer"))
        if tree is None:
            tree = self.net_type.get(ins.get("next value"))
        if tree is None:
            raise Unsupported("Feedback Node %s: its type is not known (wire its initializer)" % el.get("uid"))
        diag = self.diag_of(el)
        wrap, _, src = self.instantiate("node/feedback")
        hidden, slave = list(wrap)
        lvheap.set_typeids(wrap, [self.heap_tid(tree) for _ in lvheap.typeid_elements(wrap)])
        terms = {t.find("dco").get("class"): t for n in (hidden, slave) for t in n.find("termList")}
        keys = {"initializer": "initFeedback", "previous value": "leftFeedback", "next value": "rightFeedback"}
        for k, cls in keys.items():
            self.ends[(el.get("uid"), k)] = (terms[cls].get("uid"), diag)
        if not ins.get("initializer"):
            t = terms["initFeedback"]
            t.find("objFlags").text = str(int(t.findtext("objFlags") or 0) & ~0x8000)
        if in_loop is not None and ins.get("initializer"):
            # (an UNINITIALIZED node keeps initFeedback on itself, unwired, with no
            # iFeedbackLoop - NI's Moving Histogram PtByPt.vi; moved to the border it was
            # a broken VI, t102)
            loop, inner, outer = in_loop
            it = terms["initFeedback"]
            hidden.find("termList").remove(it)
            hidden.find("termList").set("elements", str(len(hidden.find("termList"))))
            loop.find("termList").append(it)
            loop.find("termList").set("elements", str(len(loop.find("termList"))))
            d = it.find("dco")
            if d.find("iFeedbackLoop") is None:
                ET.SubElement(d, "iFeedbackLoop", {"uid": loop.get("uid")})
            self.ends[(el.get("uid"), "initializer")] = (it.get("uid"), outer)
        for net in outs.values():
            if net:
                self.net_type[net] = tree
        # LabVIEW's own order: hidden then slave in nodeList, so slave above hidden in the z list
        for n in (hidden, slave):
            diag.nodes.append(n)
            diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": n.get("uid")}))
        # a diagram holding a feedback node carries objFlags bit 0x200 (a loop's inner diagram:
        # 0x800 -> 0xA00, measured); without it the VI was "good to bad after compiling"
        f = diag.el.find("objFlags")
        if f is None:
            f = ET.Element("objFlags")
            diag.el.insert(0, f)
        f.text = str(int(f.text or 0) | 0x200)
        self.fb_origin = getattr(self, "fb_origin", {})
        self.fb_origin[id(hidden)] = lvheap.parse_bounds(hidden.find("bounds").text)[:2]
        diag.placed.append((hidden, el))
        self.fb_pairs = getattr(self, "fb_pairs", []) + [(hidden, slave)]
        self.report.append("Feedback Node <- %s" % src)

    # ---- waveforms --------------------------------------------------------------------------
    def waveform_fields(self):
        """The fields a Float64 waveform bundles, as LabVIEW's waveform control lists them."""
        err = type_tree("cluster{bool.status,int32.code,string.source}", "error")
        return [type_tree("timestamp", "t0"), type_tree("double", "dt"),
                type_tree("array{double}", "Y"), err, type_tree("variant", "attributes")]

    def add_waveform_node(self, el):
        """Build Waveform and Get Waveform Components are Bundle/Unbundle By Name (nMux) on the
        waveform's fields, the node flagged 0x80000 on top (0xC0000 / 0xD0000, measured on an
        AIXML-generated VI); each field DCO carries <i>, t0 0, dt 1, Y 2, error 3, attributes 4."""
        name = unescape(el.get("_name"))
        # `fields` restates which fields the node shows; the terminals named in inputs/outputs
        # already say it, so it is accepted and cross-checked rather than refused
        self.check_attrs(el, ("fields",))
        build = name == "Build Waveform"
        diag = self.diag_of(el)
        node, _, src = self.instantiate("node/nMux/%s" % ("bundle" if build else "unbundle"), own_uid=el.get("uid"))
        for d in node.iter("dco"):
            for e in d.findall("inplace"):
                d.remove(e)
        fs = self.waveform_fields()
        labels = [f.get("Label") for f in fs]
        wtype = type_tree("doublewaveform")
        tl = node.find("termList")
        terms = list(tl)
        dl = node.find("dcoList")
        for c in list(dl):
            dl.remove(c)
        ins, outs = keyed(pairs(el.get("inputs"))), keyed(pairs(el.get("outputs")))
        if build:
            agg, cref, tpl = terms[0], terms[1], terms[2]
            for c in terms[2:]:
                tl.remove(c)
            agg.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(wtype)
            win = dict(ins).get("waveform")
            if not win:
                cref.find("objFlags").text = str(0x20040)   # no waveform wired, as Bundle's
            self.ends[(el.get("uid"), "waveform")] = (cref.get("uid"), diag)
            self.ends[(el.get("uid"), "output waveform")] = (agg.get("uid"), diag)
            for k, net in ins:
                if k == "waveform":
                    continue
                if k not in labels:
                    raise Unsupported("Build Waveform %s: no field %r" % (el.get("uid"), k))
                idx = labels.index(k)
                t = self._term_from(tpl, unlabel(fs[idx]) if k != "Y" else fs[idx], flags=32832, index=idx)
                tl.append(t)
                ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                self.ends[(el.get("uid"), k)] = (t.get("uid"), diag)
            for _, net in outs:
                if net:
                    self.net_type[net] = wtype
        else:
            agg, tpl = terms[0], terms[1]
            for c in terms[1:]:
                tl.remove(c)
            agg.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(wtype)
            self.ends[(el.get("uid"), "waveform")] = (agg.get("uid"), diag)
            for k, net in outs:
                if k not in labels:
                    raise Unsupported("Get Waveform Components %s: no field %r" % (el.get("uid"), k))
                idx = labels.index(k)
                t = self._term_from(tpl, fs[idx], index=idx)
                tl.append(t)
                ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                self.ends[(el.get("uid"), k)] = (t.get("uid"), diag)
                if net:
                    self.net_type[net] = unlabel(fs[idx])
        f = node.find("objFlags")
        if f is None:
            f = ET.Element("objFlags")
            node.insert(0, f)
        f.text = str(int(f.text or 0) | 0x80000)
        tl.set("elements", str(len(tl)))
        dl.set("elements", str(len(dl)))
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Node %r <- %s" % (name, src))

    # ---- template nodes: heap nodes harvested with each terminal named (harvest_templates.py)
    CA_OPS = {"Add": 0, "Multiply": 0x10000, "AND": 0x20000, "OR": 0x30000, "XOR": 0x40000}
    GROWABLE = {"Merge Errors", "Concatenate Strings", "Compound Arithmetic", "Format Into String"}

    def _templates(self, name):
        if not hasattr(self, "_tcache"):
            self._tcache = {}
            for k in self._keys("tnode/"):
                p = os.path.join(self.parts.d, k.replace("/", "__").replace(".", "_") + ".xml")
                root = ET.parse(p).getroot()
                self._tcache.setdefault(root.get("node"), []).append(
                    (k, root.get("ins").split("|") if root.get("ins") else [],
                     root.get("outs").split("|") if root.get("outs") else []))
        return self._tcache.get(name, [])

    def add_template_node(self, el):
        """A node rebuilt from a harvested template whose terminals carry their AIXML names.
        The template must have the element's terminals; a growable node (Merge Errors,
        Concatenate Strings, Compound Arithmetic) gets more inputs by cloning one."""
        name = unescape(el.get("_name"))
        dims_node = name in ("Array Subset", "Replace Array Subset")
        self.check_attrs(el, ("operation", "inversions") if name == "Compound Arithmetic" else
                         ("dimensions",) if dims_node else ())
        ins, outs = keyed(pairs(el.get("inputs"))), keyed(pairs(el.get("outputs")))
        want_in = [k for k, _ in ins]
        want_out = [k for k, _ in outs]
        best = None
        for key, t_in, t_out in self._templates(name):
            if dims_node and el.get("dimensions"):
                # a node of N dimensions has N index terminals (index, index#1 / index (row),
                # index (col)); NI's export says how many in `dimensions`
                if sum(1 for k in t_in if "index" in k) != int(el.get("dimensions")):
                    continue
            if not set(want_out) <= set(t_out):
                continue
            if set(want_in) <= set(t_in):
                score = len(t_in) - len(want_in)
                if name in self.GROWABLE and score:
                    # a LARGER growable template leaves its surplus inputs unwired, and that broke
                    # Concatenate Strings: five inputs on the six-input template loaded broken,
                    # all six wired loaded executable (2026-10-10, lvai_inventory.xml). Cloning
                    # from a smaller one is preferred; this stays the fallback.
                    score += 200
            elif name in self.GROWABLE and all(grow_base(k) in {grow_base(x) for x in t_in} for k in want_in):
                score = 100 + len(want_in) - len(t_in)        # clone inputs
            else:
                continue
            if best is None or score < best[0]:
                best = (score, key, t_in, t_out)
        if best is None:
            raise Unsupported("Node %r: no template with terminals %s" % (name, ", ".join(want_in + want_out)))
        _, key, t_in, t_out = best
        diag = self.diag_of(el)
        node, donor_types, src = self.instantiate(key, own_uid=el.get("uid"))
        for i, e in enumerate(lvheap.typeid_elements(node)):
            e.set("_t", str(i))
        for d in node.iter("dco"):
            for e in d.findall("inplace"):
                d.remove(e)
        tl = node.find("termList")
        by_key = {t.get("aixml"): t for t in tl if t.get("aixml")}
        # growable: clone the last input of the repeated kind until every wanted key exists
        for k in want_in:
            if k in by_key:
                continue
            base = grow_base(k)
            proto = [t for kk, t in by_key.items() if grow_base(kk) == base][-1]
            t = copy.deepcopy(proto)
            lvheap.renumber(t, self.alloc)
            t.set("aixml", k)
            tl.insert(list(tl).index(proto) + 1, t)
            dl = node.find("dcoList")
            if dl is not None:
                ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
                dl.set("elements", str(len(dl)))
            by_key[k] = t
        # growable, the other way: a REPEATED input the element does not have (input 2 beside
        # input 1) is removed, never a singular optional one (initial string). Left in, it was an
        # unwired argument, and that broke the VI: NI's Graph Events.vi formats ONE input with no
        # format string and composed eBad on the two-input template, executable with input 2
        # removed (2026-10-11) - the Concatenate Strings case above, from the other side
        if name in self.GROWABLE:
            kept = {grow_base(k) for k in want_in}
            for k in [k for k in list(by_key) if k in t_in and k not in want_in]:
                base = grow_base(k)
                if base == k.split("#")[0] and "#" not in k and not re.search(r" \d+$", k):
                    continue
                if base not in kept:
                    continue
                t = by_key.pop(k)
                tl.remove(t)
                dl = node.find("dcoList")
                du = t.find("dco").get("uid") if t.find("dco") is not None else None
                if dl is not None and du is not None:
                    for e in [e for e in dl if e.get("uid") == du]:
                        dl.remove(e)
                    dl.set("elements", str(len(dl)))
        tl.set("elements", str(len(tl)))

        def donor(t):
            e = t.find("dco/typeDesc")
            return plain(lvheap.strip_label(donor_types[int(e.get("_t"))])) if e is not None and e.get("_t") else None

        types = {}
        for k, net in ins:
            types[k] = self.net_type.get(net) if net else None
        wired = [types[k] for k, _ in ins if types.get(k) is not None]
        for k, t in by_key.items():
            if types.get(k) is None:
                types[k] = donor(t)
        # outputs whose type follows the inputs
        arr = types.get("array")
        if name == "Compound Arithmetic":
            for k in want_out:
                types[k] = numeric_join(wired) if wired else types.get(k)
        elif name == "Initialize Array" and types.get("element") is not None:
            a = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
            for _ in [k for k in by_key if k.startswith("dimension size")]:
                ET.SubElement(a, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
            a.append(copy.deepcopy(unlabel(types["element"])))
            types["initialized array"] = a
        elif name in ("Array Subset", "Replace Array Subset", "Insert Into Array", "Delete From Array") and arr is not None:
            for k in ("subarray", "output array", "array w/ subset deleted"):
                types[k] = arr
            if name == "Delete From Array":
                lw = dict(ins).get("length")
                types["deleted portion"] = arr if lw else unlabel(elem_of(arr)) if arr.get("Type") == "Array" else arr
        elif name == "Scan From String":
            for k in list(by_key):
                if k.startswith("output "):
                    dv = types.get("default value " + k.split(" ", 1)[1])
                    if dv is not None:
                        types[k] = dv
        for k, t in by_key.items():
            # a terminal sharing another's DCO (an in-place pair: <dco uid=../>) has no type
            if types.get(k) is not None and t.find("dco/typeDesc") is not None:
                t.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(types[k])
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        if name == "Compound Arithmetic":
            op = el.get("operation") or "Add"
            if op not in self.CA_OPS:
                raise Unsupported("Compound Arithmetic operation %r is not composable yet" % op)
            f = node.find("objFlags")
            if f is None:
                f = ET.Element("objFlags")
                node.insert(0, f)
            f.text = str((int(f.text or 0) & ~0x70000) | self.CA_OPS[op])
            # inversions: one entry per terminal, the OUTPUT first, then the inputs in order
            inv = (el.get("inversions") or "").split(",")
            order = ["result"] + [k for k in want_in]
            for k, flag in zip(order, inv):
                t = by_key.get(k)
                if t is None:
                    continue
                d = t.find("dco")
                f = d.find("objFlags")
                v = int(f.text) if f is not None else 0
                v = (v | 0x10000) if flag.strip() == "i" else (v & ~0x10000)
                if f is None and v:
                    f = ET.Element("objFlags")
                    d.insert(0, f)
                if f is not None:
                    if v:
                        f.text = str(v)
                    else:
                        d.remove(f)
        for k, t in by_key.items():
            self.ends[(el.get("uid"), k)] = (t.get("uid"), diag)
            t.attrib.pop("aixml", None)
            f = t.find("objFlags")
            if k in dict(ins) and f is not None:
                # bit 0x8000 marks a wired input (the reader's sink test)
                v = int(f.text or 0)
                f.text = str(v | 0x8000 if dict(ins)[k] else v & ~0x8000)
        for k, net in outs:
            if net and types.get(k) is not None:
                self.net_type[net] = unlabel(types[k])
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Node %r <- template %s (%s)" % (name, key, src))

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
        # A polymorphic call names its parent in `target` and the member it adapted to in
        # `instance`; calling the instance directly is the same diagram with the choice fixed.
        target = unescape(el.get("instance") or el.get("target") or "")
        if self.resolver is None:
            self.resolver = resolve.Resolver()
        res = self.resolver.resolve(target, self.search_dirs, getattr(self, "caller_file", None))
        if "error" in res:
            raise Unsupported("Call %s: %s" % (el.get("uid"), res["error"]))
        path = res["path"]
        diag = self.diag_of(el)
        callee = lvheap.Bundle(_extract_callee(path))
        conp = int(callee.main.find("CONP/Section/TypeDesc").get("TypeID"))
        # dynamic dispatch is a PANE property: a terminal whose Flags carry 0x8000
        # (Infra/PaneDispatch.cs, measured against NI's IsStaticMethod) - the LVSR flag stays
        # unset on a wizard-made accessor that dispatches dynamically
        dynamic = (callee.main.find("LVSR/Section/Execution").get("DynamicDispatch") == "1"
                   or any(int(s.get("ref.Flags") or "0", 16) & 0x8000
                          for s in callee.tree(callee.flat_of_consolidated(conp)) if s.tag == "TypeDesc"))
        if dynamic:
            # A DYNAMIC-DISPATCH call (2026-10-11): the same call node as a static one, class
            # dynIUse, with one dynLink child; its link is a DyOM naming that child, beside VIPI
            # records for the class and the method - read off LabVIEW's own saves of test VIs
            # calling a class's accessors and methods (write_links). A class inside a LIBRARY
            # qualifies its names one level deeper, which no measured file shows yet.
            if res.get("kind") != "class member" or len(res.get("qual") or []) != 2:
                raise Unsupported("Call %s: %r is a dynamic-dispatch method of a class this "
                                  "composer cannot link yet (only a class outside any library)"
                                  % (el.get("uid"), target))
        # a STATIC class member (Caraya's Define Test.vi) stays an ordinary call with an IUVI that
        # names the class in VILSPathRef - composed and run that way since Phase 3
        pane = callee.tree(callee.flat_of_consolidated(conp))
        slots = [c for c in pane if c.tag == "TypeDesc"]
        con_id = int(callee.fp.findtext("root/conPane/conId"))
        geo = self.pane_geometry.get(con_id, {})
        name_at = {}
        required_at = set()
        for fpdco in callee.fp.iter("SL__arrayElement"):
            if fpdco.get("class") == "fPDCO":
                con = fpdco.findtext("conNum")
                con = 0 if con is None else int(con)
                if con >= 0:
                    name_at[con] = vi_label(fpdco.find("ddo"))
                    if int(fpdco.findtext("objFlags") or 0) & 0x80:
                        required_at.add(con)
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
        # inputs and outputs apart: a pane may label an input and an output alike (out_key)
        by_dir = {"in": {}, "out": {}}
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
            if kind == "in" and i in required_at and dco.find("objFlags") is not None:
                # an input the CALLEE marks required: DCO objFlags 0x80 where a recommended one
                # has 0x200 - NI's converter's build of Get Item Info.vi's export, 2026-10-10.
                # Matches NI's file; the A/B with 0x200 there loads executable too.
                dco.find("objFlags").text = str((int(dco.findtext("objFlags")) & ~0x280) | 0x80)
            slot_type = copy.deepcopy(slot)
            for k in [k for k in slot_type.attrib if k.startswith("ref.")]:
                del slot_type.attrib[k]
            dco.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(slot_type)
            tl.append(t)
            if kind != "none" and i in name_at:
                by_dir[kind][name_at[i]] = (t, lvheap.strip_label(slot_type))
        # one terminal per slot of the CALLEE'S pattern: 16 on 4833 - the count must follow, or
        # the node keeps the donor's 12 and LabVIEW calls the caller broken (measured)
        tl.set("elements", str(len(tl)))
        node.find("connectorTM").text = "TypeID(%d)" % self.heap_tid(pane)
        # LabVIEW's own call node (AIXML-generated caller of a 4833 VI) carries objFlags 512;
        # the 4815 donor's 640 adds a bit LabVIEW did not write there
        node.find("objFlags").text = "512"
        for part in node.iter("label"):
            if part.find("textRec/text") is not None:
                part.find("textRec/text").text = '"%s"' % target.split(":")[-1]
                break
        listed = {"in": [], "out": []}
        for direction, attr in (("in", "inputs"), ("out", "outputs")):
            other = by_dir["out" if direction == "in" else "in"]
            for tname, net in pairs(el.get(attr)):
                if tname in by_dir[direction]:
                    listed[direction].append((tname, net))
                elif net is None and tname in other:
                    continue        # listed on the wrong side and unwired: nothing to place
                else:
                    raise Unsupported("Call %r has no %s %r on its pane (has: %s)"
                                      % (target, "input" if direction == "in" else "output", tname,
                                         ", ".join(sorted(set(by_dir["in"]) | set(by_dir["out"])))))
        for tname, net in listed["in"]:
            src = self.net_type.get(net) if net else None
            t, want = by_dir["in"][tname]
            if src is not None and want.get("Type") == "TypeDef" and src.get("Type") != "TypeDef":
                # a PLAIN value into a TYPEDEF terminal is a coercion: term objFlags 0x40000 (the
                # dot) and dsw 1024, as NI's converter wrote a uint64 control into
                # mxLvGetProjectPath.vi's `Item` (ItemRef.ctl); a typedef into a plain sink - the
                # same Item into the uint64 indicator - carries no dot there. Matches NI's file,
                # repairs nothing: measured 2026-10-10 as an A/B, the VI loads executable without
                # it (that VI's FATAL "Insane Wire" was out_key's shared terminal).
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
        for tname, net in listed["out"]:
            if net:
                self.net_type[net] = by_dir["out"][tname][1]
        for tname, (t, _) in by_dir["in"].items():
            self.ends[(el.get("uid"), tname)] = (t.get("uid"), diag)
        for tname, (t, _) in by_dir["out"].items():
            self.ends[(el.get("uid"), out_key(el, tname))] = (t.get("uid"), diag)
        if dynamic:
            node.set("class", "dynIUse")
            dyn = ET.SubElement(node, "dynLink", {"class": "dynLink", "uid": str(self.alloc()),
                                                  "ScopeInfo": "0"})
            link = self.dyn_links.setdefault(path, dict(res, offsets=[]))
            link["offsets"].append(int(dyn.get("uid")))
        else:
            link = self.links.setdefault(path, dict(res, pane=pane, offsets=[]))
            link["offsets"].append(int(node.get("uid")))
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Call %r <- %s (pane %d, %d slots)" % (target, src, con_id, len(slots)))

    def add_expr_node(self, el):
        """An Expression Node (exprNode): one input and one output bDDCO typed with the wired
        numeric type, the formula as the text of its textNodeLabel and errOfst = the formula's
        length - read off NI's Bandlimited Signal Generation.vi (`n/4.0+0.5`, errOfst 09)."""
        self.check_attrs(el, ("text",))
        text = unescape(el.get("text") or "")
        if not text or len(text.encode("mac_roman", "replace")) > 0xFF:
            raise Unsupported("Expression Node %s: text %r is not composable" % (el.get("uid"), text))
        ins, outs = dict(pairs(el.get("inputs"))), dict(pairs(el.get("outputs")))
        src_net = ins.get("input")
        tree = self.net_type.get(src_net) if src_net else None
        if tree is None or not (tree.get("Type") or "").startswith("Num"):
            raise Unsupported("Expression Node %s: needs a numeric wire into input" % el.get("uid"))
        diag = self.diag_of(el)
        got = self.instantiate("node/exprNode", own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("no exprNode part harvested")
        node, _, src = got
        tin, tout = list(node.find("termList"))
        for t in (tin, tout):
            t.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(lvheap.strip_label(tree))
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        tw = max(20, 6 * len(text))
        lab = node.find("textNodeLabel")
        lab.find("textRec/text").text = '"%s"' % text
        lab.find("bounds").text = lvheap.fmt_bounds((0, 8, 17, 8 + tw))
        tout.find("dco/termBounds").text = lvheap.fmt_bounds((0, 8 + tw, 17, 16 + tw))
        b = lvheap.parse_bounds(node.find("bounds").text)
        node.find("bounds").text = lvheap.fmt_bounds((b[0], b[1], b[0] + 17, b[1] + 16 + tw))
        node.find("errOfst").text = "%02X" % len(text.encode("mac_roman", "replace"))
        self.ends[(el.get("uid"), "input")] = (tin.get("uid"), diag)
        self.ends[(el.get("uid"), "output")] = (tout.get("uid"), diag)
        if outs.get("output"):
            self.net_type[outs["output"]] = lvheap.strip_label(tree)
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Expression Node %r <- %s" % (text, src))

    def add_vi_server_ref(self, el):
        """VI Server Reference: element="<label>" is a reference to that front-panel object (a
        ctlRefConst naming its ddo, typed with the strictly typed control refnum); type="{LV.VI}"
        with the output This VI is the VI's own reference. Both carry an oMId and a label."""
        self.check_attrs(el, ("element", "type"))
        diag = self.diag_of(el)
        outs = pairs(el.get("outputs"))
        element = unescape(el.get("element") or "")
        if element:
            info = getattr(self, "fp_info", {}).get(element)
            if info is None:
                raise Unsupported("VI Server Reference %s: no control %r" % (el.get("uid"), element))
            tree = control_ref_type(info[1], element, style=getattr(self, "graph_styles", {}).get(element))
            key, text = "node/ctlRefConst/control", element
        else:
            if (el.get("type") or "").strip() != "{LV.VI}":
                raise Unsupported("VI Server Reference %s: %r is not composable yet" % (el.get("uid"), el.get("type")))
            tree = refnum_type("LV.VI", plain_class=True)
            tree.set("Label", "This VI")
            key, text = "node/ctlRefConst/thisvi", "This VI"
        got = self.instantiate(key, own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("VI Server Reference %s: no %s part harvested" % (el.get("uid"), key))
        node, _, src = got
        t = node.find("termList/SL__arrayElement")
        t.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(tree)
        node.find("oMId").text = self._om_id_of(tree)
        lab = node.find("label")
        if lab is not None:
            lab.find("textRec/text").text = '"%s"' % text
            lb = lvheap.parse_bounds(lab.find("bounds").text)
            lab.find("bounds").text = lvheap.fmt_bounds((lb[0], lb[1], lb[2], lb[1] + max(8, 6 * len(text))))
        if element:
            node.find("ddo").set("uid", info[2].find("ddo").get("uid"))
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        for tname, net in outs:
            self.ends[(el.get("uid"), tname)] = (t.get("uid"), diag)
            if net:
                self.net_type[net] = lvheap.strip_label(tree)
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("VI Server Reference %s <- %s" % (text, src))

    def add_static_ref(self, el):
        """Static VI Reference: a statVIRef node (label = the VI's name, connectorTM = its pane,
        one VI-refnum output) and a link pair - BSVR in the VI's block typed with the VI refnum,
        SVVI in the diagram's naming the node by uid - as LabVIEW wrote them for Clear Errors.vi."""
        self.check_attrs(el, ("target", "strict"))
        target = unescape(el.get("target") or "")
        strict = (el.get("strict") or "").lower() == "true"
        if self.resolver is None:
            self.resolver = resolve.Resolver()
        res = self.resolver.resolve(target, self.search_dirs, getattr(self, "caller_file", None))
        if "error" in res:
            raise Unsupported("Static VI Reference %s: %s" % (el.get("uid"), res["error"]))
        callee = lvheap.Bundle(_extract_callee(res["path"]))
        conp = int(callee.main.find("CONP/Section/TypeDesc").get("TypeID"))
        pane = callee.tree(callee.flat_of_consolidated(conp))
        diag = self.diag_of(el)
        got = self.instantiate("node/statVIRef", own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("Static VI Reference %s: no statVIRef part harvested" % el.get("uid"))
        node, _, src = got
        vi_ref = refnum_type("LV.VI", plain_class=True)
        if strict:
            # a STRICTLY TYPED reference: the VI refnum holds the callee's connector pane - the
            # same Function type as connectorTM - and the node carries 0x10000000 (NI's
            # ODE-PredatorPrey.vi: objFlags 268435968 against 512)
            vi_ref.append(copy.deepcopy(pane))
            f = node.find("objFlags")
            if f is None:
                f = ET.Element("objFlags")
                node.insert(0, f)
            f.text = str(int(f.text or 0) | 0x10000000)
        t = node.find("termList/SL__arrayElement")
        t.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(vi_ref)
        node.find("connectorTM").text = "TypeID(%d)" % self.heap_tid(pane)
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        lab = node.find("label")
        if lab is not None:
            name = target.split(":")[-1]
            lab.find("textRec/text").text = '"%s"' % name
            lb = lvheap.parse_bounds(lab.find("bounds").text)
            lab.find("bounds").text = lvheap.fmt_bounds((lb[0], lb[1], lb[2], lb[1] + max(8, 5 * len(name))))
        for tname, net in pairs(el.get("outputs")):
            self.ends[(el.get("uid"), tname)] = (t.get("uid"), diag)
            if net:
                self.net_type[net] = vi_ref
        self.static_links = getattr(self, "static_links", {})
        link = self.static_links.setdefault(res["path"], dict(res, offsets=[]))
        link["offsets"].append(int(node.get("uid")))
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Static VI Reference %r <- %s" % (target, src))

    def write_links(self):
        """VIVI into LIvi/LVIN and IUVI into LIbd/BDHP, one pair per callee."""
        lvin = self.main.find("LIvi/Section/LVIN")
        bdhp = self.main.find("LIbd/Section/BDHP")
        attrs = {"LinkSaveFlag": "0", "VILinkLibVersion": "1", "VILinkFieldA": "0", "VILinkField4": "1",
                 "VILinkFieldB": "", "VILinkFieldC": "", "VILinkFieldD": "0", "TypedLinkFlags": "0"}
        def path_ref(holder, tag, tpval, comps):
            pr = ET.SubElement(holder, tag, {"Ident": "PTH0", "TpVal": tpval})
            for c in comps:
                ET.SubElement(pr, "String").text = c or None
            return pr
        for path, link in self.links.items():
            cons = self.types.use(self.types.register(link["pane"]))
            for tag, holder in (("VIVI", lvin), ("IUVI", bdhp)):
                rec = ET.SubElement(holder, tag, dict(attrs))
                qual = ET.SubElement(rec, "LinkSaveQualName")
                for q in link["qual"]:
                    ET.SubElement(qual, "String").text = q
                path_ref(rec, "LinkSavePathRef", *link["path_ref"])
                ET.SubElement(rec, "TypeDesc", {"TypeID": str(cons) if tag == "VIVI" else "0"})
                if tag == "IUVI":
                    ol = ET.SubElement(rec, "LinkOffsetList")
                    for uid in link["offsets"]:
                        ET.SubElement(ol, "Offset").text = "0x%04X" % uid
                    # a library member's diagram record names the .lvlib; anything else zero-fills
                    if link["lib_ref"]:
                        path_ref(rec, "VILSPathRef", *link["lib_ref"])
                    else:
                        ET.SubElement(rec, "VILSPathRef", {"Ident": "PTH0", "TpVal": "0", "ZeroFill": "True"})
                    ET.SubElement(rec, "IUseStr")
        self.write_class_links(lvin, bdhp, path_ref)
        vi_ref_cons = None
        for path, link in getattr(self, "static_links", {}).items():
            if vi_ref_cons is None:
                vi_ref_cons = self.types.use(self.types.register(refnum_type("LV.VI", plain_class=True)))
            for tag, holder in (("BSVR", lvin), ("SVVI", bdhp)):
                rec = ET.SubElement(holder, tag, dict(attrs, VILinkProp2="0") if tag == "BSVR" else dict(attrs))
                qual = ET.SubElement(rec, "LinkSaveQualName")
                for q in link["qual"]:
                    ET.SubElement(qual, "String").text = q
                path_ref(rec, "LinkSavePathRef", *link["path_ref"])
                ET.SubElement(rec, "TypeDesc", {"TypeID": str(vi_ref_cons) if tag == "BSVR" else "0"})
                if tag == "SVVI":
                    ol = ET.SubElement(rec, "LinkOffsetList")
                    for uid in link["offsets"]:
                        ET.SubElement(ol, "Offset").text = "0x%04X" % uid
                    if link["lib_ref"]:
                        path_ref(rec, "VILSPathRef", *link["lib_ref"])
                    else:
                        ET.SubElement(rec, "VILSPathRef", {"Ident": "PTH0", "TpVal": "0", "ZeroFill": "True"})

    def renumber_piccs(self):
        """Give the graph pictures LabVIEW's numbering: in HEAP ORDER from 2, one PICC section
        per reference. NI's converter writes exactly that (42 references, 2..43, in document
        order, on probes/pgraph.xml); the composer numbered them in INSTANTIATION order and let
        identical pictures share one section, and a composed graph VI then logged "bad mutex
        verifier in ThMutexDestroy" on load (2026-10-10)."""
        piccs = getattr(self, "piccs", None)
        if not piccs:
            return
        refs = [e for heap in (self.fp, self.bd) for e in heap.iter("ImageInternalsResID")
                if (e.text or "").strip().isdigit() and int(e.text) in piccs]
        fresh = {}
        for n, e in enumerate(refs, start=2):
            fresh[n] = piccs[int(e.text)]
            e.text = str(n)
        self.piccs = fresh

    @staticmethod
    def _class_version(lvclass):
        """A class's NI.Lib.Version as LabVIEW stores it in a link: four uint16, big-endian, one
        integer - 1.0.0.0 is 281474976710656 in every record of the measured files."""
        try:
            for p in ET.parse(lvclass).getroot().iter("Property"):
                if p.get("Name") == "NI.Lib.Version" and p.text:
                    parts = [int(x) for x in p.text.strip().split(".")][:4] + [0, 0, 0, 0]
                    return sum(v << (48 - 16 * i) for i, v in enumerate(parts[:4]))
        except (ET.ParseError, OSError, ValueError):
            pass
        return 1 << 48

    @staticmethod
    def _lit(data):
        """Bytes as pylabview writes an unprintable string: `&#xNN;` per byte, literally."""
        return "".join("&#x%02X;" % b if b < 0x20 or b > 0x7E else chr(b) for b in data)

    def write_class_links(self, lvin, bdhp, path_ref):
        """The links of a CLASS a VI uses, as LabVIEW 2026 saves them - read off its own saves of
        four test VIs that call a class's accessors and methods (2026-10-11), and readable as XML
        only with pylabview patched for that version's 8 extra link-cache bytes (patches.json,
        class-link-cache-extra).
          LIvi  VIPI per class, FIRST, second name 07 00, content 0;
                VIPI per method called, its name NESTED (01, length, name), content = its OWN
                1-based position among LIvi's records
          LIbd  DyOM per method called: the same names, content = that method's VIPI position,
                its cache list the call nodes' dynLink uids
                FPPI and DDPI per class with constants: cache list the constants' ddo uids
        The 8 bytes after the content are 0 in a VIPI and 00000000ff000000 in the others, in
        every one of the 22 records measured; what they mean is not established."""
        classes = {}
        for link in self.dyn_links.values():
            classes.setdefault(link["owner"], {"qual": link["qual"][:1], "lib_ref": link["lib_ref"]})
        for owner, c in self.class_consts.items():
            classes.setdefault(owner, {"qual": c["qual"], "lib_ref": c["lib_ref"]})
        if not classes:
            return

        def record(holder, tag, owner, second, content, extra, cache=None, at=None):
            c = classes[owner]
            rec = ET.Element(tag, {"LinkSaveFlag": "0",
                                   "APILinkLibVersion": str(self._class_version(owner)),
                                   "APILinkIsInternal": "0", "APILinkBool2": "1",
                                   "APILinkCallParentNodes": "0", "APILinkExtra": extra})
            qual = ET.SubElement(rec, "LinkSaveQualName")
            ET.SubElement(qual, "String").text = c["qual"][0]
            ET.SubElement(qual, "String").text = second
            path_ref(rec, "LinkSavePathRef", *c["lib_ref"])
            ET.SubElement(rec, "APILinkContent").text = self._lit(content.to_bytes(2, "big"))
            if cache is not None:
                cl = ET.SubElement(rec, "APILinkCacheList")
                for uid in cache:
                    ET.SubElement(cl, "Offset").text = "0x%04X" % uid
            if at is None:
                holder.append(rec)
            else:
                holder.insert(at, rec)
            return rec

        zero, heap_extra = "0000000000000000", "00000000ff000000"
        for i, owner in enumerate(classes):
            record(lvin, "VIPI", owner, self._lit(b"\x07\x00"), 0, zero, at=i)
        for path, link in self.dyn_links.items():
            name = link["qual"][-1].encode("mac_roman")
            nested = self._lit(bytes([1, len(name)])) + link["qual"][-1]
            rec = record(lvin, "VIPI", link["owner"], nested, 0, zero)
            position = list(lvin).index(rec) + 1
            rec.find("APILinkContent").text = self._lit(position.to_bytes(2, "big"))
            record(bdhp, "DyOM", link["owner"], nested, position, heap_extra, cache=link["offsets"])
        for owner, c in self.class_consts.items():
            for tag in ("FPPI", "DDPI"):
                record(bdhp, tag, owner, self._lit(b"\x07\x00"), 0, heap_extra, cache=c["uids"])

    def add_case(self, el):
        frames_el = [f for f in el if isinstance(f.tag, str) and f.tag == "CaseFrame"]
        if sorted(f.get("selector") for f in frames_el) == ["False", "True"]:
            return self.add_case_bool(el)
        return self.add_case_n(el)

    @staticmethod
    def _selector_items(sel):
        """'0, 2..5, Default' -> ['0', '2..5', 'Default']; commas inside a quoted string stay."""
        items, cur, quoted = [], [], False
        for ch in sel:
            if ch == '"':
                quoted = not quoted
            if ch == "," and not quoted:
                items.append("".join(cur).strip())
                cur = []
            else:
                cur.append(ch)
        items.append("".join(cur).strip())
        return [x for x in items if x]

    def add_case_n(self, el):
        """A Case Structure on an integer, an enum, a string or an error cluster, with any
        number of frames. Measured on AIXML-generated VIs: every frame is a diagram whose sRN
        holds the caseSel's inner terminal; SelectRangeArray32 maps values to frames as
        {start, end, startRangeType, endRangeType, diagramIdx} - a single value is (v, v, 0, 0),
        a range a..b is (a, b, 1, 1), an open end is type 3 at INT_MIN/INT_MAX; a string case
        lists its strings (hex) in SelectStringArray and ranges over their indices;
        SelectDefaultCase is the Default frame's index as one hex byte, FF for none;
        SelectNRightType is the number of ranges. The selector's TYPE and the ranges are set in
        finish_cases(), once the wire into the selector is typed."""
        frames_el = [f for f in el if isinstance(f.tag, str) and f.tag == "CaseFrame"]
        sels = [unescape(f.get("selector") or "") for f in frames_el]
        items = [self._selector_items(s) for s in sels]
        flat = [x for its in items for x in its if x != "Default"]
        # NI's own export writes the error frame "Error .." (23 of 23 in the cache)
        items = [[("Error" if x.strip() == "Error .." else x) for x in its] for its in items]
        flat = [x for its in items for x in its if x != "Default"]
        if flat and all(x in ("No Error", "Error") for x in flat):
            kind = "error"
        elif any(x.startswith('"') for x in flat):
            kind = "string"
        elif all(re.fullmatch(r"-?(0x[0-9A-Fa-f]+|\d+)?(\.\.)?(-?(0x[0-9A-Fa-f]+|\d+))?", x) for x in flat):
            kind = "int"
        else:
            kind = "enum"
        parent = self.diag_of(el)
        got = self.instantiate("struct/select/%s" % ("int" if kind == "enum" else kind), own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("no %s case structure part harvested" % kind)
        case, donor_types, src = got
        donor_ranges = [(int(r.findtext("diagramIdx") or 0), copy.deepcopy(r)) for r in case.find("SelectRangeArray32")]
        sel = next(t for t in case.find("termList") if t.find("dco").get("class") == "caseSel")
        sel_dco = sel.find("dco")
        dl = case.find("diagramList")
        proto = copy.deepcopy(dl[0])
        pd = Diagram(proto)
        for c in list(pd.srn_terms):
            d = c.find("dco")
            if d is None or d.get("uid") != sel_dco.get("uid"):
                pd.srn_terms.remove(c)
        for c in list(pd.nodes):
            if c.get("class") != "sRN":
                pd.nodes.remove(c)
        for lst in (pd.z, pd.signals):
            for c in list(lst):
                lst.remove(c)
        for c in list(dl):
            dl.remove(c)
        for c in list(case.find("termList")):
            if c is not sel:
                case.find("termList").remove(c)
        for c in list(case.find("tunnelList")):
            case.find("tunnelList").remove(c)
        sel_tl = sel_dco.find("termList")
        for c in list(sel_tl):
            sel_tl.remove(c)
        frame_diags = []
        for f in frames_el:
            d = copy.deepcopy(proto)
            lvheap.renumber(d, self.alloc)
            dl.append(d)
            fd = Diagram(d, owner=f)
            inner = fd.srn_terms[0]
            ET.SubElement(sel_tl, "SL__arrayElement", {"uid": inner.get("uid")})
            self.diagrams[f.get("uid")] = fd
            self.ends[(f.get("uid"), "selectout")] = (inner.get("uid"), fd)
            frame_diags.append(fd)
        ET.SubElement(sel_tl, "SL__arrayElement", {"uid": sel.get("uid")})
        sel_tl.set("elements", str(len(sel_tl)))
        dl.set("elements", str(len(dl)))
        if case.find("dIdx") is not None:
            case.find("dIdx").text = "0"
        for e in case.iter("typeDesc"):
            e.attrib.pop("_t", None)
        parent.nodes.append(case)
        parent.z.insert(0, ET.Element("SL__arrayElement", {"uid": case.get("uid")}))
        parent.placed.append((case, el))
        self.cases = getattr(self, "cases", [])
        self.cases.append((case, frame_diags, parent))
        self.ends[(el.get("uid"), "sel")] = (sel.get("uid"), parent)
        self.pending_cases = getattr(self, "pending_cases", [])
        self.pending_cases.append((case, el, kind, items, donor_ranges, frames_el))
        self.report.append("Structure 'Case Structure' (%s, %d frames) <- %s" % (kind, len(frames_el), src))

    def finish_cases(self):
        """Selector type and value-to-frame ranges, once every wire is typed."""
        INT_MIN, INT_MAX = -2 ** 31, 2 ** 31 - 1
        for case, el, kind, items, donor_ranges, frames_el in getattr(self, "pending_cases", []):
            net = el.get("selectin")
            st = self.net_type.get(net)
            if st is None:
                raise Unsupported("Case Structure %s: the selector wire %r is not typed" % (el.get("uid"), net))
            sel = next(t for t in case.find("termList") if t.find("dco").get("class") == "caseSel")
            sel.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(st)
            for f in frames_el:
                if f.get("selectout"):
                    self.net_type[f.get("selectout")] = unlabel(st)
            ranges, strings, default = [], [], 0xFF
            if kind == "error":
                if st.get("Type") != "Cluster":
                    raise Unsupported("Case Structure %s: 'No Error'/'Error' frames need an error cluster" % el.get("uid"))
                # the donor's own two ranges, matched to the frames by name: the donor was
                # authored [No Error, Error], and LabVIEW kept that order
                by_name = {"No Error": 0, "Error": 1}
                for i, its in enumerate(items):
                    for x in its:
                        if x == "Default":
                            default = i
                            continue
                        for di, r in donor_ranges:
                            if di == by_name[x]:
                                r = copy.deepcopy(r)
                                r.find("diagramIdx").text = str(i)
                                ranges.append((int(r.findtext("start")), r))
            else:
                if kind == "string" and is_enum(st):
                    # NI's export QUOTES an enum case's items ("Off", "Auto".."Manual") - the
                    # wire, not the quotes, says it is an enum (11 NI VIs refused as a string case)
                    kind = "enum"
                    items = [[x.replace('"', "") for x in its] for its in items]
                if kind == "string":
                    if st.get("Type") != "String":
                        raise Unsupported("Case Structure %s: string selectors on a %s wire" % (el.get("uid"), st.get("Type")))
                    strs = sorted({x[1:-1] for its in items for x in its if x.startswith('"') and ".." not in x},
                                  key=lambda s: s.encode("mac_roman"))
                    strings = strs
                    value = lambda x: strs.index(x[1:-1])
                elif kind == "enum":
                    labels = [e.text for e in st.findall("EnumLabel")]
                    if not labels:
                        raise Unsupported("Case Structure %s: enum selectors on a %s wire" % (el.get("uid"), st.get("Type")))
                    def value(x, labels=labels):
                        if x not in labels:
                            raise Unsupported("Case Structure %s: %r is not an item of the enum" % (el.get("uid"), x))
                        return labels.index(x)
                else:
                    if st.get("Type") not in INT_RANGE and st.get("Type") not in ENUM_BASE.values():
                        raise Unsupported("Case Structure %s: an integer case on a %s wire" % (el.get("uid"), st.get("Type")))
                    value = lambda x: int(x, 0)
                for i, its in enumerate(items):
                    for x in its:
                        if x == "Default":
                            default = i
                            continue
                        if ".." in x and kind != "string":
                            a, b = x.split("..", 1)
                            s, st_t = (value(a), 1) if a else (INT_MIN, 3)
                            e, et_t = (value(b), 1) if b else (INT_MAX, 3)
                        elif ".." in x:
                            raise Unsupported("Case Structure %s: string ranges are not composable yet" % el.get("uid"))
                        else:
                            s = e = value(x)
                            st_t = et_t = 0
                        r = ET.Element("SL__arrayElement", {"class": "SelectorRange"})
                        for tag, v in (("start", s), ("end", e), ("startRangeType", st_t),
                                       ("endRangeType", et_t), ("diagramIdx", i)):
                            ET.SubElement(r, tag).text = str(v)
                        ranges.append((s, r))
            ra = case.find("SelectRangeArray32")
            for c in list(ra):
                ra.remove(c)
            for _, r in sorted(ranges, key=lambda x: x[0]):
                ra.append(r)
            ra.set("elements", str(len(ra)))
            sa = case.find("SelectStringArray")
            for c in list(sa):
                sa.remove(c)
            for s in strings:
                ET.SubElement(sa, "SL__arrayElement").text = s.encode("mac_roman").hex().upper() or None
            sa.set("elements", str(len(sa)))
            case.find("SelectDefaultCase").text = "%02X" % default
            case.find("SelectNRightType").text = str(len(ra))

    def add_case_bool(self, el):
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
        part = getattr(self, "case_tunnel_part", {}).get(case.get("uid"))
        got = (self.instantiate("%s/%s" % (part, d)) if part else None) or             self.instantiate("seltun/bool/%s" % d) or self.instantiate("seltun/n/%s" % d)
        if got is None:
            raise Unsupported("no %s case-tunnel part harvested" % d)
        wrap = got[0]
        parts = list(wrap)
        outer, inner_terms = parts[0], parts[1:]
        if len(inner_terms) != len(frame_diags):
            # one inner terminal per FRAME: the template's first, cloned; the selTun DCO's
            # termList names them in frame order, then the outer terminal
            tpl = inner_terms[0]
            inner_terms = []
            for _ in frame_diags:
                t = copy.deepcopy(tpl)
                t.set("uid", str(self.alloc()))
                inner_terms.append(t)
            tl = outer.find("dco/termList")
            for c in list(tl):
                tl.remove(c)
            for t in inner_terms + [outer]:
                ET.SubElement(tl, "SL__arrayElement", {"uid": t.get("uid")})
            tl.set("elements", str(len(tl)))
        if d == "in":
            nets = [n for _, n in pairs(outer_el.get("inputs")) if n]
        else:
            nets = [n for h in halves.values() for _, n in pairs(h.get("inputs")) if n]
        tree = next((self.net_type[n] for n in nets if n in self.net_type), None)
        if tree is None:
            raise Unsupported("case tunnel %s: no typed wire reaches it" % outer_el.get("_id"))
        lvheap.set_typeids(wrap, [self.heap_tid(tree) for _ in lvheap.typeid_elements(wrap)])
        # cond="true" on an OUTPUT tunnel is the selTun's 0x300000 bits: LabVIEW exports NI's case
        # tunnel (objFlags 0x300801) with it and an AIXML-generated one without cond (0x801)
        # without (measured 2026-10-10); written only when the AIXML asks for it
        cond = outer_el.get("cond") or next((h.get("cond") for h in halves.values() if h.get("cond")), None)
        if cond not in (None, "true"):
            raise Unsupported("case tunnel %s: cond=%r is not composable yet" % (outer_el.get("_id"), cond))
        f = outer.find("dco/objFlags")
        if d == "out":
            f.text = str((int(f.text) & ~0x300000) | (0x300000 if cond == "true" else 0))
        elif cond == "true":
            # NI exports an INPUT tunnel with cond="true" as well: its selTun carries 0x200000,
            # the bit every cond="true" tunnel has (DataFinder_Pass_Fail_Analysis.vi: in
            # 0x200800, out 0x200801 and 0x300801, all exported cond="true")
            f.text = str(int(f.text) | 0x200000)
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

    # EventSpec rows by event kind, read off NI's own VIs (event_specs.py over 40 of them):
    # (source, regFlags, eSource, type, eFlags, bound to a control?)
    # "User Event": read off LabVIEW's own save of the finished user-event skeleton (2026-10-10):
    # source 1, regFlags 1, eSource 25, type 1000 (the 0x03E8 the registration carries), and
    # dynIndex 1 - the first dynamic-event terminal pair - where a static event has 0
    EVENT_SPECS = {"User Event": ("1", "1", "25", "1000", "0", False),
                   "Value Change": ("3", "1", "0", "1073741826", "4", True),
                   "Mouse Enter": ("3", "1", "0", "1073741839", "4", True),
                   "Mouse Leave": ("3", "1", "0", "1073741838", "4", True),
                   "Timeout": ("4", "0", "0", "1073741825", "1", False)}

    def add_event(self, el):
        """An Event Structure (eventStruct), built like a case structure: one diagram per frame,
        selTun tunnels with an inner terminal per frame, and the dynamic-event and timeout
        terminals (eventDynDCO left/right, eventTimeOut) whose termLists name one inner terminal
        per frame then the outer one. Which event a frame handles is an EventSpec in
        EventNodeEvents; the frame's Event Data Node is named in dataNodeList. Both are written
        in finish_events(), once the panel exists. Template: NI's Radio Buttons.vi."""
        frames_el = [f for f in el if isinstance(f.tag, str) and f.tag == "CaseFrame"]
        parent = self.diag_of(el)
        node, donor_types, src = self.instantiate("struct/event", own_uid=el.get("uid"))
        # the dynamic-event and timeout terminals keep the DONOR's TypeIDs until mapped into this
        # VI's table - left as they were they named whatever this VI had at 12-14 (two booleans,
        # measured), and LabVIEW died loading the VI
        for i, e in enumerate(lvheap.typeid_elements(node)):
            e.set("_t", str(i))
        dl = node.find("diagramList")
        keep_cls = ("eventDynDCO", "eventTimeOut")
        fixed = [t for t in node.find("termList") if t.find("dco").get("class") in keep_cls]
        for t in list(node.find("termList")):
            if t not in fixed:
                node.find("termList").remove(t)
        for c in list(node.find("tunnelList")):
            node.find("tunnelList").remove(c)
        fixed_dcos = {t.find("dco").get("uid"): t for t in fixed}
        proto = copy.deepcopy(dl[0])
        pd = Diagram(proto)
        for c in list(pd.srn_terms):
            d = c.find("dco")
            if c.get("class") != "term" or d is None or d.get("uid") not in fixed_dcos:
                pd.srn_terms.remove(c)
        for c in list(pd.nodes):
            if c.get("class") != "sRN":
                pd.nodes.remove(c)
        for lst in (pd.z, pd.signals):
            for c in list(lst):
                lst.remove(c)
        for c in list(dl):
            dl.remove(c)
        inner_of = {u: [] for u in fixed_dcos}
        frame_diags = []
        for f in frames_el:
            d = copy.deepcopy(proto)
            lvheap.renumber(d, self.alloc)
            dl.append(d)
            fd = Diagram(d, owner=f)
            self.diagrams[f.get("uid")] = fd
            frame_diags.append(fd)
            for t in fd.srn_terms:
                inner_of[t.find("dco").get("uid")].append(t.get("uid"))
                if f is not frames_el[0]:
                    # a frame not on display carries its inner event terminals as 0x420000
                    # (NI's Radio Buttons.vi), the displayed one as the template has them
                    t.find("objFlags").text = "4325376"
        for du, t in fixed_dcos.items():
            tl = t.find("dco/termList")
            for c in list(tl):
                tl.remove(c)
            for u in inner_of[du] + [t.get("uid")]:
                ET.SubElement(tl, "SL__arrayElement", {"uid": u})
            tl.set("elements", str(len(tl)))
        dl.set("elements", str(len(dl)))
        if node.find("dIdx") is not None:
            node.find("dIdx").text = "0"
        for e in node.iter("typeDesc"):
            if e.get("_t") is not None:
                tree = plain(lvheap.strip_label(donor_types[int(e.get("_t"))]))
                e.text = "TypeID(%d)" % self.heap_tid(tree)
                del e.attrib["_t"]
        parent.nodes.append(node)
        parent.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        parent.placed.append((node, el))
        self.cases = getattr(self, "cases", [])
        self.cases.append((node, frame_diags, parent))
        self.case_tunnel_part = getattr(self, "case_tunnel_part", {})
        self.case_tunnel_part[node.get("uid")] = "seltun/event"
        self.events = getattr(self, "events", [])
        self.events.append((node, el, frames_el))
        # EVERY event frame has its Event Data Node - LabVIEW does not let one be deleted, and
        # dataNodeList names one per frame; a frame without one in the AIXML gets an empty one
        have = {c.get("uid_parent") for c in el.iter("Node") if c.get("_name") == "Event Data Node"}
        self._synth_event_data = getattr(self, "_synth_event_data", [])
        for f in frames_el:
            if f.get("uid") not in have:
                # with one (unwired) field, as LabVIEW draws a fresh frame's node
                its = event_items(unescape(f.get("selector") or ""))
                _, kind = self._event_of(its[0])
                # Time is in every event's data, NewVal only in a lone Value Change's
                fld = "NewVal" if len(its) == 1 and kind == "Value Change" and not self._dyn_source(its[0]) else "Time"
                self._synth_event_data.append(ET.Element("Node", {"_name": "Event Data Node", "fields": fld,
                                                                  "outputs": fld + ":", "uid": "0", "uid_parent": f.get("uid")}))
        # and a FILTER event's frame its Event Filter Node: left out, LabVIEW makes one on load
        # whose field is no field (int32, <i>-1</i>, the node flagged 0x4) and the VI is broken -
        # measured, executable once the node is there. A frame without one in the AIXML gets one
        # with the event's first settable field, unwired (Drag Enter: Accepted?)
        have_f = {c.get("uid_parent") for c in el.iter("Node") if c.get("_name") == "Event Filter Node"}
        self._synth_event_filter = getattr(self, "_synth_event_filter", [])
        for f in frames_el:
            its = event_items(unescape(f.get("selector") or ""))
            rows = [static_event(self._event_of(it)[1], self._event_of(it)[0]) if self._dyn_source(it) is None else None
                    for it in its]
            filt = [r is not None and int(r["spec"]["type"]) < 0 for r in rows]
            if not any(filt):
                continue
            if not all(filt):
                # LabVIEW breaks it: Drop with a Value Change in one frame loaded ExecState 0, and
                # LabVIEW's own save made its placeholder filter node there (2026-10-11)
                raise Unsupported("Event Structure %s: a frame handling a filter event with a notify, dynamic or "
                                  "timeout event - LabVIEW breaks such a frame (measured)" % el.get("uid"))
            if f.get("uid") in have_f or not all(r.get("filterFields") for r in rows):
                continue
            # several filter events: the settable fields they ALL have, by name, in the first one's
            # order - the merge the frame's filter cluster gets (add_event_filter)
            per = [[self._field_label(x) for x in map(ET.fromstring, r["filterFields"]) if not self._hidden(x)]
                   for r in rows]
            settable = [x for x in per[0] if all(x in p for p in per[1:])]
            self._synth_event_filter.append(ET.Element("Node", {
                "_name": "Event Filter Node", "fields": settable[0] if settable else "",
                "inputs": (settable[0] + ":") if settable else "", "uid": "0", "uid_parent": f.get("uid")}))
        self.report.append("Structure 'Event Structure' (%d frames) <- %s" % (len(frames_el), src))

    @staticmethod
    def _event_of(selector):
        """' "Stop": Value Change ' -> ('Stop', 'Value Change'); ' Timeout ' -> (None, 'Timeout')."""
        s = unescape(selector or "").strip()
        m = re.fullmatch(r'"(.*)"\s*:\s*(.+)', s)
        if m:
            return m.group(1), m.group(2).strip()
        # ' <Tick>: User Event ' - the user event is named by its datatype's label
        u = re.fullmatch(r'<(.*)>\s*:\s*User Event', s)
        return (u.group(1), "User Event") if u else (None, s)

    def _is_user_event_frame(self, frame):
        """A frame handling a user event - among others, too (NewProjectDialog.vi's `<Resize>:
        User Event; "Main Pane": Pane Size`)."""
        return any(self._event_of(it)[1] == "User Event" for it in event_items(unescape(frame.get("selector") or "")))

    def _registration_of(self, struct_el):
        """(net, type) of the EVENT REGISTRATION REFNUM wired into an Event Structure through one
        of its input tunnels - the tunnel the skeleton routes it through on purpose."""
        for t in struct_el:
            if not isinstance(t.tag, str) or t.tag != "Tunnel":
                continue
            for _, n in pairs(t.get("inputs")):
                tree = self.net_type.get(n) if n else None
                if tree is not None and tree.get("Type") == "Refnum" and tree.get("RefType") == "EventReg":
                    return n, tree
        return None, None

    def _user_event_position(self, struct_el, name):
        """1-based position of the user event among ALL the registration's items - a control
        reference before it counts (Dynamic Event Generation.vi numbers VI, Picture, Picture as
        dynIndex 1, 2, 3)."""
        _, reg = self._registration_of(struct_el)
        order = [(c.get("Label") or (fields(c)[0].get("Label") if fields(c) else ""))
                 if c.get("Type") == "Refnum" and c.get("RefType") == "UserEvent" else None
                 for c in fields(reg)]
        return order.index(name) + 1

    @staticmethod
    def _dyn_source(selector):
        """' <Picture>: Mouse Down ' -> ('Picture', 'Mouse Down'): an event registered dynamically
        on a VI Server reference - a control, the VI. ' <#1>: Mouse Move ' names an UNLABELLED
        source by its position in the registration (NI's export of Dynamic Event Generation.vi,
        a Not-a-Refnum VI constant at position 1). None for a static event and a user event."""
        s = unescape(selector or "").strip()
        m = re.fullmatch(r"<(.*)>\s*:\s*(.+)", s)
        if not m or m.group(2).strip() == "User Event":
            return None
        return m.group(1), m.group(2).strip()

    def _static_row(self, uid, ctl, kind):
        """The static-events.json row for a control's NOTIFY event, or a refusal naming why not:
        a filter event (`Mouse Down?`, type with the 0x80000000 bit) needs an Event Filter Node, a
        VI or application event has no control, and a class-dependent one was not generalised."""
        r = static_event(kind, ctl)
        if r is None:
            raise Unsupported("Event Structure %s: event %r is not composable yet" % (uid, kind))
        if int(r["spec"]["type"]) < 0 and not r.get("filterFields"):
            raise Unsupported("Event Structure %s: the filter event %r has no measured Event Filter Node "
                              "cluster (add_filter_fields.py)" % (uid, kind))
        if r["sourceKind"] in ("vi", "application"):
            # an event of the VI itself (Panel Close?, Key Down?) or of the APPLICATION (VI
            # Activation): NI writes the selector with NO source - " Panel Close? ", " VI
            # Activation " - and the spec as source 5, eFlags 2 for the VI, source 4, eFlags 1,
            # regFlags 0 for the application, ddoUID 0 both (NI's NewProjectDialog.vi, the MGI
            # Panel Manager actors, Using the VI Activation Event for VI Scripting.vi; 2026-10-11)
            if ctl is not None:
                raise Unsupported("Event Structure %s: %r is an event of the %s - its selector names no source, "
                                  "not %r" % (uid, kind, "VI" if r["sourceKind"] == "vi" else "application", ctl))
        elif r["sourceKind"] != "control" or ctl is None:
            raise Unsupported("Event Structure %s: the %s event %r is not composable yet" % (uid, r["sourceKind"], kind))
        if r.get("classDependent"):
            raise Unsupported("Event Structure %s: %r carries class-dependent data - not composable yet" % (uid, kind))
        return r

    def _static_spec(self, uid, ctl, kind):
        r = self._static_row(uid, ctl, kind)
        s = r["spec"]
        # bound to a control (its ddoUID) unless it is the VI's own event
        return (s["source"], s["regFlags"], s["eSource"], s["type"], s["eFlags"], r["sourceKind"] == "control")

    def _is_dyn_frame(self, frame):
        return any(self._dyn_source(p) for p in event_items(unescape(frame.get("selector") or "")))

    @staticmethod
    def _reg_code(child):
        """The registration item code a child of an EventReg type stands for: a user event's is
        03E8 (CField4 alone, as LabVIEW writes the item), a control reference's CField2 + CField4."""
        c2 = (child.get("ref.CField2") or "0x0000")[2:].upper()
        c4 = (child.get("ref.CField4") or "0x0000")[2:].upper()
        return c4 if c2 == "0000" else c2 + c4

    def _dyn_row(self, child, event):
        row = control_event_row(self._class_of_refnum(child), event)
        return row if row is not None and row["code"].upper() == self._reg_code(child) else None

    def _dyn_position(self, struct_uid, reg, name, event):
        """(dynIndex, measured row) of the registration item that registers `event` on the source
        `name` - its label, or #<position> when it has none."""
        for i, c in enumerate(fields(reg) if reg is not None else [], 1):
            if c.get("RefType") != "LVObjCtl" or (c.get("Label") or "#%d" % i) != name:
                continue
            row = self._dyn_row(c, event)
            if row is not None:
                return i, row
        raise Unsupported("Event Structure %s: the registration wired into it does not register %r on %r"
                          % (struct_uid, event, name))

    def _structures_fed_by(self, net):
        """The Event Structures a registration on `net` reaches: through loop tunnels and shift
        registers (inputs -> outputs), and into a case's frames by the inner tunnels sharing the
        outer one's _id, to a tunnel ON an Event Structure."""
        seen, todo, found = {net}, [net], []
        while todo:
            n = todo.pop()
            for e in self._elements:
                if e.tag not in ("Tunnel", "Left", "Right") or n not in [x for _, x in pairs(e.get("inputs"))]:
                    continue
                outs = [x for _, x in pairs(e.get("outputs")) if x]
                parent = self._by_uid.get(e.get("uid_parent"))
                if e.tag == "Tunnel" and parent is not None and parent.tag == "Structure":
                    if parent.get("_name") == "Event Structure" and parent not in found:
                        found.append(parent)
                    outs += [x for f in parent if isinstance(f.tag, str) and f.tag == "CaseFrame"
                             for t in f if isinstance(t.tag, str) and t.tag == "Tunnel" and t.get("_id") == e.get("_id")
                             for _, x in pairs(t.get("outputs")) if x]
                for x in outs:
                    if x not in seen:
                        seen.add(x)
                        todo.append(x)
        return found

    @staticmethod
    def _user_events_of(reg):
        """The UserEvent refnums a registration type carries, by name."""
        return {(c.get("Label") or (fields(c)[0].get("Label") if fields(c) else "")): c
                for c in fields(reg) if c.get("Type") == "Refnum" and c.get("RefType") == "UserEvent"}

    def finish_events(self):
        for node, el, frames_el in getattr(self, "events", []):
            if any(self._is_user_event_frame(f) or self._is_dyn_frame(f) for f in frames_el):
                self._wire_registration(node, el, frames_el)
            specs = node.find("EventNodeEvents")
            for c in list(specs):
                specs.remove(c)
            for i, f in enumerate(frames_el):
                for part in event_items(unescape(f.get("selector") or "")):
                    dyn = self._dyn_source(part)
                    if dyn is not None:
                        # a control reference's event: the spec as LabVIEW wrote it for that
                        # (class, event), dynIndex the item's position, no ddo
                        pos, drow = self._dyn_position(el.get("uid"), self._registration_of(el)[1], *dyn)
                        s_ = drow["spec"]
                        row = (s_["source"], s_["regFlags"], s_["eSource"], s_["type"], s_["eFlags"], False)
                        ctl, kind, dyn_index = dyn[0], dyn[1], str(pos)
                    else:
                        ctl, kind = self._event_of(part)
                        row = self.EVENT_SPECS.get(kind) or self._static_spec(el.get("uid"), ctl, kind)
                        # a user event's dynIndex is its 1-based POSITION in the registration - 1
                        # and 2 for the two events of MGI's Special Monitor Actor Core.vi; static 0
                        dyn_index = str(self._user_event_position(el, ctl)) if kind == "User Event" else "0"
                    ddo = "0"
                    if row[5]:
                        info = getattr(self, "fp_info", {}).get(ctl)
                        if info is None:
                            raise Unsupported("Event Structure %s: no control %r" % (el.get("uid"), ctl))
                        ddo = info[2].find("ddo").get("uid")
                    sp = ET.SubElement(specs, "SL__arrayElement", {"class": "EventSpec"})
                    for tag, v in (("diagramIdx", str(i)), ("source", row[0]), ("regFlags", row[1]),
                                   ("eSource", row[2]), ("type", row[3]), ("eFlags", row[4]),
                                   ("ddoUID", ddo)):
                        ET.SubElement(sp, tag).text = v
                    ET.SubElement(sp, "menuTag")
                    ET.SubElement(sp, "dynIndex").text = dyn_index
            specs.set("elements", str(len(specs)))
            dn = getattr(self, "event_data_nodes", {})
            for lst_tag in ("dataNodeList", "filterNodeList"):
                lst = node.find(lst_tag)
                for c in list(lst):
                    lst.remove(c)
                fn = getattr(self, "event_filter_nodes", {})
                for f in frames_el:
                    u = (dn if lst_tag == "dataNodeList" else fn).get(f.get("uid"), "0")
                    ET.SubElement(lst, "SL__arrayElement", {"uid": u})
                lst.set("elements", str(len(lst)))
            # the selector label caches the displayed frame's text (CLAUDE.md: written by hand,
            # LabVIEW does not regenerate it) - frame 0
            sel = node.find("selString/textRec/text")
            if sel is not None and frames_el:
                sel.text = '" [0] %s "' % unescape(frames_el[0].get("selector") or "").strip()

    def _wire_registration(self, node, el, frames_el):
        """A structure with user-event frames: its DYNAMIC EVENT terminals carry the registration
        type, and the left one is one more end of the registration's net - the tunnel the document
        routes it through stays, as in LabVIEW's own save (a four-ended wire there). This is the
        wire lvai_wire_dynamic_events makes after NI's converter; composed, it is there at once."""
        net, reg = self._registration_of(el)
        if net is None:
            raise Unsupported("Event Structure %s: a user-event frame needs the event registration refnum "
                              "wired into the structure through a tunnel" % el.get("uid"))
        have = self._user_events_of(reg)
        for f in frames_el:
            for part in event_items(unescape(f.get("selector") or "")):
                name, kind = self._event_of(part)
                if kind == "User Event" and name not in have:
                    raise Unsupported("Event Structure %s: no user event %r in the registration (it has %s)"
                                      % (el.get("uid"), name, ", ".join(sorted(have)) or "none"))
                if self._dyn_source(part) is not None:
                    self._dyn_position(el.get("uid"), reg, *self._dyn_source(part))
        dyn = [t for t in node.find("termList") if t.find("dco").get("class") == "eventDynDCO"]
        left = [t for t in dyn if not int(t.find("dco").findtext("objFlags") or 0) & 1]
        if len(dyn) != 2 or len(left) != 1:
            raise Unsupported("Event Structure %s: the part's dynamic-event terminals are not one pair" % el.get("uid"))
        labelled = copy.deepcopy(reg)
        labelled.set("Label", "Event Registration Refnum")
        for t in dyn:
            t.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(labelled)
        f_ = left[0].find("objFlags")
        if f_ is None:
            f_ = ET.Element("objFlags")
            left[0].insert(0, f_)
        f_.text = str(int(f_.text or 0) | 0x8000)
        self.dyn_reg = getattr(self, "dyn_reg", {})
        self.dyn_reg[el.get("uid")] = net
        self.ends[(el.get("uid"), "dyn")] = (left[0].get("uid"), self.diag_of(el))

    def add_event_reg(self, el):
        """Register For Events (eventRegNode): built like a property node - four fixed hGrowCItem
        terminals (registration refnum in and out, error in and out) and one eventRegItem per event
        source, code 03E8 for a user event. The registration type is the source's UserEvent refnum
        inside an EventReg refnum, the child carrying CField0 0x0001, CField2 0x0000, CField4
        0x03E8 - all read off LabVIEW's own save of the finished user-event skeleton (2026-10-10).
        SEVERAL user events: one eventRegItem each, all code 03E8, the registration carrying them in
        source order with the same three fields - read off MGI's Special Monitor Actor Core.vi, two
        user events in one node.

        A CONTROL REFERENCE (a control, the VI - any VI Server object whose event a frame names as
        `<Picture>: Mouse Down`): one item per (source, event), its code and the child's CField2 and
        CField4 the pair LabVIEW wrote for that class and event (control_events()). NI's AIXML does
        not say which event an item registers - its inputs are `event source:<net>` and nothing
        more - so each source's events are read off the Event Structures this registration reaches,
        in frame order; a source wired twice takes them in the order the frames first name them.
        Dynamic Event Generation.vi registers VI, Picture, Picture for Mouse Move, Mouse Down,
        Mouse Up, and its frames name Mouse Down, Mouse Move, Mouse Up - the Picture's two in the
        order of its items.

        RE-REGISTRATION (the refnum input wired, as inside that example's frames): the items are the
        incoming registration's, the i-th event source feeding the i-th item; an unwired source
        leaves its item as it is."""
        uid = el.get("uid")
        ins = keyed(pairs(el.get("inputs")))
        src_keys = [(k, n) for k, n in ins if grow_base(k) == "event source"]
        reg_in = dict(ins).get("event registration refnum")
        reg_in_t = self.net_type.get(reg_in) if reg_in else None
        if reg_in and (reg_in_t is None or reg_in_t.get("RefType") != "EventReg"):
            raise Unsupported("Register For Events %s: the registration refnum wired in is not typed" % uid)
        # one entry per item: (source key, net, item type, child CFields or None, dco objFlags, name)
        entries = []
        if reg_in_t is not None:
            reg = lvheap.strip_label(copy.deepcopy(reg_in_t))
            children = fields(reg)
            if len(src_keys) > len(children):
                raise Unsupported("Register For Events %s: %d event sources for a registration of %d items"
                                  % (uid, len(src_keys), len(children)))
            for i, child in enumerate(children):
                k, n = src_keys[i] if i < len(src_keys) else (None, None)
                it = copy.deepcopy(child)
                for a in [a for a in it.attrib if a.startswith("ref.")]:
                    del it.attrib[a]
                if n:
                    u = self.net_type.get(n)
                    if u is None or u.get("RefType") != child.get("RefType") or \
                            (child.get("RefType") == "LVObjCtl" and u.get("CtlFlags") != child.get("CtlFlags")):
                        raise Unsupported("Register For Events %s: event source %r does not match item %d of the "
                                          "registration wired in" % (uid, k, i + 1))
                flags = "0"
                if child.get("RefType") == "LVObjCtl":
                    # a code names one event of one declaring class, so any row carrying it will do
                    row = next((r for r in control_events().values() if r["code"].upper() == self._reg_code(child)), None)
                    flags = (row or {}).get("dcoFlags", "0")
                entries.append((k, n, it, None, flags, it.get("Label") or ""))
        else:
            srcs = [(k, n) for k, n in src_keys if n]
            if not srcs:
                raise Unsupported("Register For Events %s: no event source wired" % uid)
            if len(srcs) != len(src_keys):
                raise Unsupported("Register For Events %s: an unwired event source on a new registration" % uid)
            ctl = []
            for pos, (k, n) in enumerate(srcs, 1):
                u = self.net_type.get(n)
                if u is not None and u.get("Type") == "Refnum" and u.get("RefType") == "UserEvent" and fields(u):
                    nm = fields(u)[0].get("Label") or ""
                    uev = copy.deepcopy(u)
                    uev.set("Label", nm)
                    entries.append((k, n, uev, ("0x0001", "0x0000", "0x03E8"), "0", nm))
                elif u is not None and u.get("Type") == "Refnum" and u.get("RefType") == "LVObjCtl":
                    label = self._source_label(n)
                    it = lvheap.strip_label(copy.deepcopy(u))
                    if label:
                        it.set("Label", label)
                    entries.append([k, n, it, None, "0", label or "#%d" % pos])
                    ctl.append(len(entries) - 1)
                else:
                    raise Unsupported("Register For Events %s: event source %r is neither a user event nor a "
                                      "VI Server reference" % (uid, k))
            if ctl:
                out_net = next((n for k, n in pairs(el.get("outputs")) if k == "event registration refnum" and n), None)
                structs = self._structures_fed_by(out_net) if out_net else []
                if not structs:
                    raise Unsupported("Register For Events %s: a VI Server reference registers the event an Event "
                                      "Structure names for it, and no Event Structure is reached from this "
                                      "registration - route the refnum into one through a tunnel" % uid)
                wanted = {}
                for st in structs:
                    for f in st:
                        if not isinstance(f.tag, str) or f.tag != "CaseFrame":
                            continue
                        for part in event_items(unescape(f.get("selector") or "")):
                            d = self._dyn_source(part)
                            if d is not None and d[1] not in wanted.setdefault(d[0], []):
                                wanted[d[0]].append(d[1])
                by_name = {}
                for i in ctl:
                    by_name.setdefault(entries[i][5], []).append(i)
                for name, idx in by_name.items():
                    evs = wanted.get(name, [])
                    if len(evs) != len(idx):
                        raise Unsupported("Register For Events %s: %d source(s) named %r, and the Event Structure "
                                          "handles %d event(s) of it (%s)" % (uid, len(idx), name, len(evs),
                                                                              ", ".join(evs) or "none"))
                    for i, ev in zip(idx, evs):
                        cls = self._class_of_refnum(entries[i][2])
                        row = control_event_row(cls, ev)
                        if row is None:
                            have = sorted({k_.split("|", 1)[1] for k_ in control_events()
                                           if control_event_row(cls, k_.split("|", 1)[1]) is not None})
                            raise Unsupported("Register For Events %s: no measured registration for %r on %s (measured "
                                              "for %s: %s)" % (uid, ev, cls, cls, ", ".join(have) or "nothing"))
                        code = row["code"].upper()
                        entries[i][3] = ("0x0001", "0x" + code[:4], "0x" + code[4:])
                        entries[i][4] = row.get("dcoFlags", "0")
                entries = [tuple(e) for e in entries]
            reg = ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "EventReg", "Field0": "0x0000", "Format": "inline"})
            for _, _, it, cf, _, _ in entries:
                child = copy.deepcopy(it)
                for k_, v in zip(("ref.CField0", "ref.CField2", "ref.CField4"), cf):
                    child.set(k_, v)
                reg.append(child)
        names = [e[5] for e in entries]
        if len(set(n for n, e in zip(names, entries) if e[2].get("RefType") == "UserEvent")) != \
                len([e for e in entries if e[2].get("RefType") == "UserEvent"]):
            raise Unsupported("Register For Events %s: two user events named alike (%s)" % (uid, ", ".join(names)))
        got = self.instantiate("node/eventReg", own_uid=uid)
        if got is None:
            raise Unsupported("Register For Events %s: no eventRegNode part harvested" % uid)
        node, donor_types, src = got
        diag = self.diag_of(el)
        terms = list(node.find("termList"))
        roles = {0x10000: "event registration refnum", 0x40001: "event registration refnum out",
                 0x20000: "error in (no error)", 0x80001: "error out"}
        by = {}
        item = None
        for t in terms:
            d = t.find("dco")
            if d.get("class") == "hGrowCItem":
                by[roles.get(int(d.findtext("objFlags") or 0))] = t
            elif d.get("class") == "eventRegItem":
                item = t
        if len(by) != 4 or item is None:
            raise Unsupported("Register For Events %s: the harvested part is not the measured shape" % uid)
        # one item per source: the part's own, then clones below it, a 16 px row each
        tl = node.find("termList")
        items = [item]
        for _ in entries[1:]:
            t = copy.deepcopy(items[-1])
            lvheap.renumber(t, self.alloc)
            tb = t.find("dco/termBounds")
            if tb is not None:
                a, l_, b_, r_ = lvheap.parse_bounds(tb.text)
                tb.text = lvheap.fmt_bounds((a + 16, l_, b_ + 16, r_))
            tl.insert(list(tl).index(items[-1]) + 1, t)
            ET.SubElement(node.find("dcoList"), "SL__arrayElement", {"uid": t.find("dco").get("uid")})
            items.append(t)
        tl.set("elements", str(len(tl)))
        node.find("dcoList").set("elements", str(len(node.find("dcoList"))))
        if len(entries) > 1:
            nb = lvheap.parse_bounds(node.find("bounds").text)
            node.find("bounds").text = lvheap.fmt_bounds((nb[0], nb[1], nb[2] + 16 * (len(entries) - 1), nb[3]))
        err = type_tree("cluster{bool.status,int32.code,string.source}")
        for role, label, tree in (("event registration refnum", "event registration refnum", reg),
                                  ("event registration refnum out", "Event Registration Refnum", reg),
                                  ("error in (no error)", "error in (no error)", err),
                                  ("error out", "error out", err)):
            tt = copy.deepcopy(tree)
            tt.set("Label", label)
            by[role].find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(tt)
        for t, (_, _, it, _, dco_flags, _), child in zip(items, entries, fields(reg)):
            d = t.find("dco")
            d.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(it)
            # the item's code: a user event's 03E8, a control reference's CField2 + CField4
            d.find("code").text = self._reg_code(child)
            f = d.find("objFlags")
            if dco_flags not in ("0", "", None):
                if f is None:
                    f = ET.Element("objFlags")
                    d.insert(0, f)
                f.text = dco_flags
            elif f is not None:
                d.remove(f)
        wired = {k for k, n in ins if n}
        for t, on in [(by["event registration refnum"], "event registration refnum" in wired),
                      (by["error in (no error)"], "error in (no error)" in wired)] + \
                     [(it, bool(n)) for it, (_, n, _, _, _, _) in zip(items, entries)]:
            f = t.find("objFlags")
            if on and f is None:
                f = ET.Element("objFlags")
                t.insert(0, f)
            if f is not None:
                f.text = str((int(f.text or 0) | 0x8000) if on else (int(f.text or 0) & ~0x8000))
                if f.text == "0":
                    t.remove(f)
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        node.find("oMId").text = self._om_id_of(reg)
        self.ends[(uid, "event registration refnum")] = (by["event registration refnum"].get("uid"), diag)
        self.ends[(uid, out_key(el, "event registration refnum"))] = (by["event registration refnum out"].get("uid"), diag)
        self.ends[(uid, "error in (no error)")] = (by["error in (no error)"].get("uid"), diag)
        self.ends[(uid, "error out")] = (by["error out"].get("uid"), diag)
        for (k, _, _, _, _, _), it in zip(entries, items):
            if k is not None:
                self.ends[(uid, k)] = (it.get("uid"), diag)
        for k, n in pairs(el.get("outputs")):
            if n and k == "event registration refnum":
                self.net_type[n] = reg
            elif n and k == "error out":
                self.net_type[n] = err
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.report.append("Register For Events (%s%s) <- %s" % ("re-registering " if reg_in_t is not None else "",
                                                               ", ".join(n or "?" for n in names), src))

    @staticmethod
    def _field_label(f):
        """A data cluster field's name - Source and Type are TYPEDEFS whose label sits on the
        wrapped type, not on the TypeDef."""
        return f.get("Label") or (fields(f)[0].get("Label") if f.get("Type") == "TypeDef" and fields(f) else None)

    def _event_fields(self, el, frame, item, fs):
        """The data cluster of ONE (source, event) item of a frame, as a list of labelled fields.
        `fs` is the Event Data Node donor's own Value Change cluster: Source, Type, Time, CtlRef,
        OldVal, NewVal."""
        ctl, kind = self._event_of(item)
        dyn = self._dyn_source(item)
        fs = [copy.deepcopy(f) for f in fs]
        if dyn is not None:
            # a control reference's event: the data cluster LabVIEW wrote for that (class, event),
            # its reference field (CtlRef, VIRef) typed as the registered item - Source, Type and
            # Time stay the donor's typedefs, which carry the enum names
            st_el = self._by_uid.get(frame.get("uid_parent"))
            _, reg = self._registration_of(st_el)
            pos, row = self._dyn_position(st_el.get("uid"), reg, *dyn)
            if not row.get("data"):
                raise Unsupported("Event Data Node %s: no data cluster measured for %s" % (el.get("uid"), dyn[1]))
            dfs = fields(ET.fromstring(row["data"]))
            for f in dfs[3:]:
                for e in f.iter("TypeDesc"):
                    if e.get("Type", "").startswith("Unit") and not any(c.tag == "EnumLabel" for c in e):
                        raise Unsupported("Event Data Node %s: the measured %s data carries an enum without its "
                                          "item names - rebuild control-events.json with --enum-labels" % (el.get("uid"), dyn[1]))
            ref = copy.deepcopy(fields(reg)[pos - 1])
            for k in [k for k in ref.attrib if k.startswith("ref.")]:
                del ref.attrib[k]
            ref.set("Label", dfs[3].get("Label") or "CtlRef")
            fs = [fs[0], fs[1], fs[2], ref] + [copy.deepcopy(f) for f in dfs[4:]]
            # a STRICTLY typed reference's OldVal and NewVal are its data type, as for a static
            # Value Change they are the control's - not the measured donor's. A reference that is
            # not strict still fixes them where its class does: LabVIEW's own save of a composed VI
            # wrote Path for a Path reference and String for a String one (2026-10-10), where the
            # generic LV.Control reference carries a Variant (NewProjectDialog.vi)
            vt = fields(ref)[0] if fields(ref) else \
                {"0x000B": "path", "0x001B": "string"}.get((ref.get("CtlFlags") or "").upper().replace("0X", "0x"))
            if dyn[1] == "Value Change" and vt is not None:
                vt = type_tree(vt) if isinstance(vt, str) else vt
                for i, f in enumerate(fs[4:], 4):
                    if f.get("Label") in ("OldVal", "NewVal"):
                        fs[i] = lvheap.strip_label(copy.deepcopy(vt))
                        fs[i].set("Label", f.get("Label"))
            return fs
        static = None
        if kind not in ("Value Change", "Mouse Enter", "Mouse Leave", "Timeout", "User Event"):
            static = self._static_row(el.get("uid"), ctl, kind)
        if static is not None and static["sourceKind"] in ("vi", "application"):
            # the VI's own event: VIRef, a PLAIN VI refnum (LVObjCtl 0x0002), then the event's
            # fields - as LabVIEW wrote it in NI's NewProjectDialog.vi for Key Down? and Panel Close?;
            # the application's: AppRef, a plain Application refnum (0x0001) - NI's VI Activation
            vi_ev = static["sourceKind"] == "vi"
            ref = ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "LVObjCtl", "CtlFlags": "0x0002" if vi_ev else "0x0001",
                                          "HasItem": "0", "Format": "inline",
                                          "Label": static.get("refLabel") or ("VIRef" if vi_ev else "AppRef")})
            return fs[:3] + [ref] + [ET.fromstring(x) for x in static["fields"]]
        if kind in ("Value Change", "Mouse Enter", "Mouse Leave") or static is not None:
            ctl_el = next((e for e in self._by_uid.values() if e.tag in ("Control", "Indicator")
                           and unescape(e.get("_name") or "") == ctl), None)
            if ctl_el is None:
                raise Unsupported("Event Data Node %s: no control %r" % (el.get("uid"), ctl))
            ct = type_tree(ctl_el.get("type"))
            # CtlRef: the control's STRICTLY typed refnum - LabVIEW's save of a composed probe wrote
            # 0x0008{Boolean} and 0x0012{NumFloat64} for a switch Boolean's and a double's - EXCEPT
            # a LATCHED Boolean's, which is plain 0x0008: measured as an A/B on one probe, and the
            # shape of NI's Set Busy Cursor.vi and Manage DataPlugins.vi, all latched (2026-10-10).
            # A String's and a Path's have nothing inside either way
            ref = control_ref_type(ct, style=getattr(self, "graph_styles", {}).get(ctl))
            if (ctl_el.get("style") or "") == "latched":
                for c in fields(ref):
                    ref.remove(c)
            ref.set("Label", "CtlRef")
            if static is not None:
                # a surveyed static event: its own fields after the reference, as NI's VIs carry
                # them (Mouse Down: Coords, Button, Mods, PlatMods)
                return fs[:3] + [ref] + [ET.fromstring(x) for x in static["fields"]]
            if kind != "Value Change":
                # Mouse Enter / Mouse Leave: Source, Type, Time, CtlRef and nothing else - LabVIEW's
                # save of a single one, and NI's Handling Mouse Events.vi with six in one frame
                return fs[:3] + [ref]
            out = fs[:3] + [ref]
            for lab in ("OldVal", "NewVal"):
                v = copy.deepcopy(ct)
                v.set("Label", lab)
                out.append(v)
            return out
        if kind == "Timeout":
            return fs[:3]
        if kind == "User Event":
            # LabVIEW's own save of the finished skeleton: Source (the typedef), Type and Time as
            # plain u32, UsrEvtRef the user event refnum, then the payload - field 4
            _, reg = self._registration_of(self._by_uid.get(frame.get("uid_parent")))
            u = self._user_events_of(reg).get(ctl) if reg is not None else None
            if u is None:
                raise Unsupported("Event Data Node %s: no registration carries the user event %r" % (el.get("uid"), ctl))
            payload = copy.deepcopy(fields(u)[0])
            # a CLUSTER payload is FLATTENED: its elements are fields 4, 5, ... - LabVIEW's save of
            # a two-element one (Last, N) wrote Source, Type, Time, UsrEvtRef, Last, N, and NI's
            # export names `Last` in `fields=` directly (2026-10-10)
            payload = [copy.deepcopy(f) for f in fields(payload)] if payload.get("Type") == "Cluster" else [payload]
            uref = copy.deepcopy(u)
            for k in [k for k in uref.attrib if k.startswith("ref.")]:
                del uref.attrib[k]
            uref.set("Label", "UsrEvtRef")
            typ = ET.Element("TypeDesc", {"Type": "NumUInt32", "Prop1": "0", "Format": "inline", "Label": "Type"})
            return [fs[0], typ, fs[2], uref] + payload
        raise Unsupported("Event Data Node %s: the data of a %r event is not composable yet" % (el.get("uid"), kind))

    def _common_class_ref(self, uid, refs):
        """The PLAIN refnum of the nearest class every reference in `refs` descends from - what
        LabVIEW gives a frame's CtlRef when its sources differ: Ring, Numeric, Ring and Slide give
        LV.Numeric (2D Interpolation.vi), a Ring and an array LV.Control (Image Compression with
        DCT.vi), a Boolean and a generic reference LV.Control (NewProjectDialog.vi)."""
        control_events()                                  # loads the class hierarchy
        parents = (_CONTROL_EVENTS or {}).get("parents", {})

        def line(c):
            out = [c]
            while c in parents and parents[c] not in out:
                c = parents[c]
                out.append(c)
            return out
        classes = [self._class_of_refnum(r) for r in refs]
        for cand in line(classes[0]):
            if all(cand in line(c) for c in classes[1:]):
                row = _REF_CLASSES.get(cand)
                if row is None:
                    break
                return ET.Element("TypeDesc", {"Type": "Refnum", "RefType": "LVObjCtl", "CtlFlags": row[0],
                                               "HasItem": "0", "Format": "inline", "Label": refs[0].get("Label") or "CtlRef"})
        raise Unsupported("Event Data Node %s: no common class known for %s" % (uid, ", ".join(classes)))

    def _merge_event_fields(self, uid, clusters):
        """A frame handling SEVERAL events gets the fields they ALL have, by name, in the first
        one's order (NI's corpus, 17 frames: Value Change and Panel Close keep Source, Type, Time;
        Key Down and Key Repeat keep every field). A field all have with DIFFERENT types becomes:
        CtlRef the nearest common class's plain refnum, OldVal and NewVal a Variant (2D
        Interpolation.vi, four numerics of three representations), Type a plain u32 (a user event
        with a Pane Size, NewProjectDialog.vi), and UsrEvtRef is dropped. Any other such field is
        refused - not measured. Checked against LabVIEW's own save of two probes, 18 frames of
        single, several, user, dynamic and mixed events: identical in every one."""
        out = []
        for f in clusters[0]:
            lab = self._field_label(f)
            others = [next((g for g in c if self._field_label(g) == lab), None) for c in clusters[1:]]
            if any(o is None for o in others):
                continue
            if all(type_sig(f) == type_sig(o) for o in others):
                out.append(f)
            elif lab in ("CtlRef", "VIRef"):
                out.append(self._common_class_ref(uid, [f] + others))
            elif lab in ("OldVal", "NewVal"):
                out.append(ET.Element("TypeDesc", {"Type": "LVVariant", "Format": "inline", "Label": lab}))
            elif lab == "Type":
                out.append(ET.Element("TypeDesc", {"Type": "NumUInt32", "Prop1": "0", "Format": "inline", "Label": "Type"}))
            elif lab == "UsrEvtRef":
                # two user events of DIFFERENT payload types: LabVIEW's save drops the reference
                # (an int32 and a string, 2026-10-10); of ONE payload type it keeps it, labelled
                # with the first event's payload - which the identical-type branch above writes
                continue
            else:
                raise Unsupported("Event Data Node %s: the frame's events give %r different types - not measured"
                                  % (uid, lab))
        return out

    def add_event_data(self, el):
        """An Event Data Node: Unbundle By Name over the frame's "Event Data" cluster - for a
        Value Change Source, Type, Time, CtlRef, OldVal, NewVal (NewVal is field 5, measured),
        OldVal/NewVal typed like the control; for a Timeout Source, Type, Time."""
        self.check_attrs(el, ("fields",))
        frame = self._by_uid.get(el.get("uid_parent"))
        if frame is None or frame.tag != "CaseFrame":
            raise Unsupported("Event Data Node %s: not in an event frame" % el.get("uid"))
        sel_items = event_items(unescape(frame.get("selector") or ""))
        ctl, kind = self._event_of(sel_items[0])
        diag = self.diag_of(el)
        node, donor_types, src = self.instantiate("node/eventData")
        terms = list(node.find("termList"))
        agg, tpl = terms[0], terms[1]
        donor = plain(lvheap.strip_label(donor_types[0]))
        clusters = [self._event_fields(el, frame, it, fields(donor)) for it in sel_items]
        fs = clusters[0] if len(clusters) == 1 else self._merge_event_fields(el.get("uid"), clusters)
        asked = [unescape(x) for x in split_top(el.get("fields") or "") if x] or [k for k, _ in pairs(el.get("outputs"))]
        if "CtlRef" in asked:
            # the reference LabVIEW writes was measured for these control types only (probes of
            # 2026-10-10) - reading it off another is refused rather than guessed
            for it in sel_items:
                c, k = self._event_of(it)
                c_el = next((e for e in self._by_uid.values() if e.tag in ("Control", "Indicator")
                             and unescape(e.get("_name") or "") == c), None) if self._dyn_source(it) is None else None
                t = type_tree(c_el.get("type")).get("Type", "") if c_el is not None else ""
                if c_el is not None and not (t in ("Boolean", "String", "Path") or (t.startswith("Num") and not is_enum(type_tree(c_el.get("type"))))):
                    raise Unsupported("Event Data Node %s: CtlRef of a %s control is not measured yet" % (el.get("uid"), t))
        cl = ET.Element("TypeDesc", dict(donor.attrib))
        for f in fs:
            cl.append(f)
        cl.set("Label", "Event Data")
        # Source and Type are TYPEDEFS (eventsource.ctl, eventtype.ctl) whose label sits on the
        # wrapped type, not on the TypeDef: read as None they were "no field 'Source'" in 18
        # of NI's VIs
        labels = [self._field_label(f) for f in fs]
        for c in terms[1:]:
            node.find("termList").remove(c)
        dl = node.find("dcoList")
        for c in list(dl):
            dl.remove(c)
        agg.find("dco/typeDesc").text = "TypeID(%d)" % self.heap_tid(cl)
        want = [unescape(x) for x in split_top(el.get("fields") or "") if x] or [k for k, _ in pairs(el.get("outputs"))]
        outs = dict(keyed(pairs(el.get("outputs"))))
        for k in want:
            if k not in labels:
                raise Unsupported("Event Data Node %s: no field %r for %s" % (el.get("uid"), k, kind))
            idx = labels.index(k)
            t = self._term_from(tpl, fs[idx], index=idx)
            node.find("termList").append(t)
            ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
            self.ends[(el.get("uid"), k)] = (t.get("uid"), diag)
            if outs.get(k):
                self.net_type[outs[k]] = unlabel(fs[idx])
        node.find("termList").set("elements", str(len(node.find("termList"))))
        dl.set("elements", str(len(dl)))
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.event_data_nodes = getattr(self, "event_data_nodes", {})
        self.event_data_nodes[frame.get("uid")] = node.get("uid")
        self.report.append("Event Data Node <- %s" % src)

    @staticmethod
    def _hidden(f):
        """Bit7 on a cluster field - set on the wrapped type of a TypeDef, as LabVIEW writes it."""
        inner = fields(f)[0] if f.get("Type") == "TypeDef" and fields(f) else f
        return inner.get("Bit7") == "1"

    def add_event_filter(self, el):
        """An Event Filter Node - the second eventDataNode of a FILTER event's frame, through
        which the frame hands values BACK (Drag Enter's and Drop's Accepted?, a "?" event's
        Discard?). As LabVIEW writes it in NI's Drag and Drop - Passing Custom Drag Data.vi: the
        node's objFlags without the data node's 0x10000; its cluster "Event Filter Data" with
        Source, Type, Time and the reference hidden (Bit7 on each) and then the fields
        add_filter_fields.py read off NI's own filter node for that event; the aggregate DCO
        flagged 1 and carrying a termList of its own term and a second, DCO-less term (objFlags
        0x820000); one INPUT terminal per field, wired 0x8040, unwired 0x40, its DCO unflagged.
        A field Bit7 hides on this node cannot be set and is refused by name. `<i>` is written as
        the field's position; LabVIEW rewrites it on load (8 became 17 for Available Data Names on
        the data node, measured)."""
        self.check_attrs(el, ("fields",))
        frame = self._by_uid.get(el.get("uid_parent"))
        if frame is None or frame.tag != "CaseFrame":
            raise Unsupported("Event Filter Node %s: not in an event frame" % el.get("uid"))
        items = event_items(unescape(frame.get("selector") or ""))
        if any(self._dyn_source(it) is not None for it in items):
            raise Unsupported("Event Filter Node %s: a filter event registered dynamically is not measured"
                              % el.get("uid"))
        kind = self._event_of(items[0])[1]
        diag = self.diag_of(el)
        node, donor_types, src = self.instantiate("node/eventData")
        terms = list(node.find("termList"))
        agg, tpl = terms[0], terms[1]
        donor = plain(lvheap.strip_label(donor_types[0]))
        # one filter cluster per event - Source, Type, Time and the reference, then the event's
        # measured filter fields - merged by name as a frame's data clusters are when the frame
        # handles several (_merge_event_fields); the base fields are hidden (Bit7) on this node
        # whatever the merge made of them (a common class's plain refnum carries no bit)
        clusters = []
        for it in items:
            c_, k_ = self._event_of(it)
            row = static_event(k_, c_)
            if row is None or int(row["spec"]["type"]) >= 0 or not row.get("filterFields"):
                raise Unsupported("Event Filter Node %s: %r is not a filter event with a measured filter cluster"
                                  % (el.get("uid"), k_))
            clusters.append(self._event_fields(el, frame, it, fields(donor))[:4] +
                            [ET.fromstring(x) for x in row["filterFields"]])
        if len({self._event_of(it)[1] for it in items}) > 1:
            # DIFFERENT filter events share a frame only with IDENTICAL data, the reference's class
            # included - measured on LabVIEW's own load: Drag Enter and Drop on one graph, and on two
            # graphs, ExecState 1; on a graph and a string, and Panel Close? with Key Down?, 0. The
            # SAME event on several sources is fine across classes (Drag Enter on a graph and a
            # string): LabVIEW then gives the reference their common class
            sigs = {tuple(repr(type_sig(f)) + (self._field_label(f) or "") for f in c[3:]) for c in clusters}
            if len(sigs) > 1:
                raise Unsupported("Event Filter Node %s: the frame's filter events %s differ in their data or in "
                                  "their reference's class - LabVIEW breaks such a frame (measured)"
                                  % (el.get("uid"), " / ".join(it.strip() for it in items)))
        fs = clusters[0] if len(clusters) == 1 else self._merge_event_fields(el.get("uid"), clusters)
        fs = [copy.deepcopy(f) for f in fs]
        for f in fs:
            if self._field_label(f) in ("Source", "Type", "Time", "CtlRef", "VIRef", "AppRef"):
                (fields(f)[0] if f.get("Type") == "TypeDef" and fields(f) else f).set("Bit7", "1")
        cl = ET.Element("TypeDesc", dict(donor.attrib))
        for f in fs:
            cl.append(f)
        cl.set("Label", "Event Filter Data")
        labels = [self._field_label(f) for f in fs]
        nf = node.find("objFlags")
        nf.text = str(int(nf.text or 0) & ~0x10000)
        tl = node.find("termList")
        for c in terms[1:]:
            tl.remove(c)
        dl = node.find("dcoList")
        for c in list(dl):
            dl.remove(c)
        adco = agg.find("dco")
        adco.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(cl)
        af = adco.find("objFlags")
        if af is None:
            af = ET.Element("objFlags")
            adco.insert(0, af)
        af.text = "1"
        second = ET.Element("SL__arrayElement", {"class": "term", "uid": str(self.alloc())})
        ET.SubElement(second, "objFlags").text = "8519680"
        ET.SubElement(second, "dco", {"uid": adco.get("uid")})
        tl.insert(1, second)
        atl = ET.Element("termList", {"elements": "2"})
        ET.SubElement(atl, "SL__arrayElement", {"uid": agg.get("uid")})
        ET.SubElement(atl, "SL__arrayElement", {"uid": second.get("uid")})
        adco.insert(1, atl)
        want = [unescape(x) for x in split_top(el.get("fields") or "") if x] or [k for k, _ in pairs(el.get("inputs"))]
        ins = dict(keyed(pairs(el.get("inputs"))))
        for k in want:
            if k not in labels:
                raise Unsupported("Event Filter Node %s: no field %r for %s (it has %s)" % (
                    el.get("uid"), k, kind, ", ".join(l for l, f in zip(labels, fs) if not self._hidden(f))))
            idx = labels.index(k)
            if self._hidden(fs[idx]):
                raise Unsupported("Event Filter Node %s: %r cannot be set for %s - LabVIEW hides it on the filter node"
                                  % (el.get("uid"), k, kind))
            t = self._term_from(tpl, fs[idx], flags=0x8040 if ins.get(k) else 0x40, dco_flags=0, index=idx)
            tl.append(t)
            ET.SubElement(dl, "SL__arrayElement", {"uid": t.find("dco").get("uid")})
            self.ends[(el.get("uid"), k)] = (t.get("uid"), diag)
        tl.set("elements", str(len(tl)))
        dl.set("elements", str(len(dl)))
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        diag.nodes.append(node)
        diag.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        diag.placed.append((node, el))
        self.event_filter_nodes = getattr(self, "event_filter_nodes", {})
        self.event_filter_nodes[frame.get("uid")] = node.get("uid")
        self.report.append("Event Filter Node <- %s" % src)

    DISABLES = {"Diagram Disable Structure": "diagram", "Conditional Disable Structure": "conditional"}

    def _cond_expr(self, sel, el):
        """' RUN_TIME_ENGINE==True ' -> an ExpressionInfo as LabVIEW writes it: the strings in
        Tag0000, a prefix tree in activeDiag - an operator TreeElt (Tag0000 FF, two children,
        activePlot FD for ==) then one leaf per string (activePlot = its index). Measured on an
        AIXML-generated Conditional Disable Structure; other operators are refused."""
        info = ET.Element("activeDiag", {"class": "ExpressionInfo"})
        s = sel.strip()
        if s in ("Default", ""):
            ET.SubElement(info, "Tag0000", {"elements": "0"})
            ET.SubElement(info, "activeDiag", {"elements": "0"})
            return info
        # the operator is the root TreeElt's activePlot: FD for ==, FE for != (read off NI's
        # Scale TDMS Data.vi, " TARGET_TYPE!=RT ")
        m = re.fullmatch(r"([A-Za-z_][A-Za-z0-9_]*)\s*(==|!=)\s*(\S+)", s)
        if not m:
            raise Unsupported("Conditional Disable %s: condition %r is not composable yet" % (el.get("uid"), s))
        strs = ET.SubElement(info, "Tag0000", {"elements": "2"})
        for x in (m.group(1), m.group(3)):
            ET.SubElement(strs, "SL__arrayElement").text = x.encode("mac_roman").hex().upper()
        tree = ET.SubElement(info, "activeDiag", {"elements": "3"})
        op_plot = "FE" if m.group(2) == "!=" else "FD"
        for tag0, ad, mk, plot in (("FF", "01", "2", op_plot), ("00", "FF", "-1", "00"), ("00", "FF", "-1", "01")):
            t = ET.SubElement(tree, "SL__arrayElement", {"class": "TreeElt"})
            ET.SubElement(t, "Tag0000").text = tag0
            ET.SubElement(t, "activeDiag").text = ad
            ET.SubElement(t, "activeMarker").text = mk
            ET.SubElement(t, "activePlot").text = plot
        return info

    def add_disable(self, el):
        """Diagram Disable and Conditional Disable structures: a commentNode, built like a case
        structure - one diagram per frame, commentTun tunnels with an inner terminal per frame
        - plus commentSelInfoArray (one ExpressionInfo per frame). A Diagram Disable carries
        commentMode True and activeDiag/defaultDiag, the ENABLED frame's index: NI's own
        converter set it to the last frame whatever the labels said (its probe ran 'Disabled'),
        the composer sets the frame whose selector is ' Enabled '."""
        kind = self.DISABLES[unescape(el.get("_name"))]
        self.check_attrs(el)
        frames_el = [c for c in el if isinstance(c.tag, str) and c.tag == "Diagram"]
        parent = self.diag_of(el)
        node, _, src = self.instantiate("struct/disable/%s" % kind, own_uid=el.get("uid"))
        dl = node.find("diagramList")
        proto = copy.deepcopy(dl[0])
        pd = Diagram(proto)
        for c in list(pd.srn_terms):
            pd.srn_terms.remove(c)
        for c in list(pd.nodes):
            if c.get("class") != "sRN":
                pd.nodes.remove(c)
        for lst in (pd.z, pd.signals):
            for c in list(lst):
                lst.remove(c)
        for c in list(dl):
            dl.remove(c)
        for lst in (node.find("termList"), node.find("tunnelList")):
            for c in list(lst):
                lst.remove(c)
        info = node.find("commentSelInfoArray")
        proto_info = copy.deepcopy(info[0])
        for c in list(info):
            info.remove(c)
        frame_diags = []
        enabled = 0
        for i, f in enumerate(frames_el):
            d = copy.deepcopy(proto)
            lvheap.renumber(d, self.alloc)
            dl.append(d)
            fd = Diagram(d, owner=f)
            self.diagrams[f.get("uid")] = fd
            frame_diags.append(fd)
            e = copy.deepcopy(proto_info)
            sel = unescape(f.get("selector") or "")
            if kind == "conditional":
                e.remove(e.find("activeDiag"))
                e.append(self._cond_expr(sel, el))
            elif sel.strip() == "Enabled":
                enabled = i
            info.append(e)
        info.set("elements", str(len(info)))
        dl.set("elements", str(len(dl)))
        for tag in ("activeDiag", "defaultDiag"):
            if node.find(tag) is not None:
                node.find(tag).text = "%02X" % enabled
        if node.find("dIdx") is not None:
            node.find("dIdx").text = "0"
        for e in node.iter("typeDesc"):
            e.attrib.pop("_t", None)
        parent.nodes.append(node)
        parent.z.insert(0, ET.Element("SL__arrayElement", {"uid": node.get("uid")}))
        parent.placed.append((node, el))
        self.cases = getattr(self, "cases", [])
        self.cases.append((node, frame_diags, parent))
        self.case_tunnel_part = getattr(self, "case_tunnel_part", {})
        self.case_tunnel_part[node.get("uid")] = "commenttun"
        self.report.append("Structure %r (%d frames) <- %s" % (el.get("_name"), len(frames_el), src))

    def add_flatseq(self, el):
        """A Flat Sequence: a flatSequence object (defined in the parent's z list) holding one
        sequenceFrame structure per frame, side by side; the PARENT's nodeList names each
        frame. A frame is a one-diagram structure with seqTun tunnels ([inner, outer], 2048 in,
        2049 out); data between frames runs through a wire in the parent diagram. Measured on an
        AIXML-generated VI."""
        frames_el = [c for c in el if isinstance(c.tag, str) and c.tag == "Diagram"] or [el]
        parent = self.diag_of(el)
        got = self.instantiate("struct/flatSequence", own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("no flat sequence part harvested")
        seq, donor_types, src = got
        sl = seq.find("sequenceList")
        proto = copy.deepcopy(sl[0])
        for c in list(sl):
            sl.remove(c)
        for c in list(proto.find("termList")):
            proto.find("termList").remove(c)
        for c in list(proto.find("tunnelList")):
            proto.find("tunnelList").remove(c)
        pd = Diagram(proto.find("diagramList")[0])
        for c in list(pd.srn_terms):
            pd.srn_terms.remove(c)
        for c in list(pd.nodes):
            if c.get("class") != "sRN":
                pd.nodes.remove(c)
        for lst in (pd.z, pd.signals):
            for c in list(lst):
                lst.remove(c)
        self.seqs = getattr(self, "seqs", [])
        frames = []
        for f in frames_el:
            fr = copy.deepcopy(proto)
            lvheap.renumber(fr, self.alloc)
            sl.append(fr)
            fd = Diagram(fr.find("diagramList")[0], owner=f)
            self.diagrams[f.get("uid")] = fd
            ET.SubElement(parent.nodes, "SL__arrayElement", {"uid": fr.get("uid")})
            frames.append((fr, fd, f))
        sl.set("elements", str(len(sl)))
        parent.z.insert(0, seq)
        parent.placed.append((seq, el))
        self.seqs.append((seq, frames, parent))
        self.report.append("Structure 'Flat Sequence' (%d frames) <- %s" % (len(frames), src))

    INPLACE_PAIRS = ("In Place In / Out Element",)

    def add_inplace(self, el):
        """An In Place Element Structure: a decomposeRecomposeStructure with ONE diagram. Each
        in/out element pair is two decomposeMatchNodes defined in that diagram's nodeList, the
        left one on the left border (outer input -> inner output), the right one on the right
        border (inner input -> outer output), joined by a matchPoserInterface in the
        structure's multiSegPipeFlange2Size; the structure's own termList holds only the two
        OUTER terminals, each named by the matching decomposeDCO's termList [own, outer].
        Read off an AIXML-generated VI (probes/ptypes.xml). Only the In / Out Element pair is
        composed; array, cluster, variant and reference pairs are refused by name."""
        parent = self.diag_of(el)
        got = self.instantiate("struct/inplace", own_uid=el.get("uid"))
        if got is None:
            raise Unsupported("no In Place Element Structure part harvested")
        node, _, src = got
        d = Diagram(node.find("diagramList")[0], owner=el)
        match = sorted([n for n in d.nodes if n.get("class") == "decomposeMatchNode"],
                       key=lambda n: lvheap.parse_bounds(n.find("bounds").text)[1])
        mspf = node.find("multiSegPipeFlange2Size")
        if len(match) != 2 or mspf is None or len(mspf) != 1 or len(node.find("termList")) != 2:
            raise Unsupported("the In Place part does not hold exactly one in/out pair")
        proto = ET.Element("proto")
        for x in match + [mspf[0]] + list(node.find("termList")):
            proto.append(copy.deepcopy(x))
        for c in list(d.nodes):
            if c.get("class") != "sRN":
                d.nodes.remove(c)
        for lst in (d.z, d.signals, d.srn_terms, node.find("termList"), mspf):
            for c in list(lst):
                lst.remove(c)
        self.diagrams[el.get("uid")] = d
        parent.z.insert(0, node)
        ET.SubElement(parent.nodes, "SL__arrayElement", {"uid": node.get("uid")})
        parent.placed.append((node, el))
        self.inplaces = getattr(self, "inplaces", [])
        self.inplaces.append({"node": node, "diag": d, "parent": parent, "proto": proto, "pairs": []})
        self.report.append("Structure 'In Place Element Structure' <- %s" % src)

    def add_inplace_tunnel(self, el, st_el):
        """A plain tunnel on an In Place Element Structure: ONE AIXML <Tunnel> with both sides,
        as on a flat sequence frame. In the heap a decomposeRecomposeTunnel DCO (2048 in, 2049
        out, termList [inner, outer]) in the structure's termList and
        tunnelList, its inner terminal in the diagram's sRN. Flags as LabVIEW wrote them in
        NI's Buffered Stream to Tab-Delimited Text File.vi: outer 0x8040 / inner 0x20000 going
        in, outer 0x40 / inner 0x28000 going out."""
        d = "in" if el.get("_id", "").startswith("In") else "out"
        ip = next((x for x in getattr(self, "inplaces", []) if x["diag"].owner is st_el), None)
        if ip is None:
            raise Unsupported("Tunnel %s: no In Place Element Structure %s" % (el.get("uid"), st_el.get("uid")))
        node, inner, parent = ip["node"], ip["diag"], ip["parent"]
        src_net = pairs(el.get("inputs"))[0][1] if pairs(el.get("inputs")) else None
        tree = self.net_type.get(src_net)
        if tree is None:
            raise Unsupported("Tunnel %s: the type of %r is not known" % (el.get("uid"), src_net))
        dco_uid, in_uid = self.alloc(), self.alloc()
        out_uid = self.claim(el.get("uid"))
        outer = ET.Element("SL__arrayElement", {"class": "term", "uid": str(out_uid)})
        ET.SubElement(outer, "objFlags").text = str(0x8040 if d == "in" else 0x40)
        dco = ET.SubElement(outer, "dco", {"class": "decomposeRecomposeTunnel", "uid": str(dco_uid)})
        ET.SubElement(dco, "objFlags").text = "2048" if d == "in" else "2049"
        tl = ET.SubElement(dco, "termList", {"elements": "2"})
        ET.SubElement(tl, "SL__arrayElement", {"uid": str(in_uid)})
        ET.SubElement(tl, "SL__arrayElement", {"uid": str(out_uid)})
        ET.SubElement(dco, "typeDesc").text = "TypeID(%d)" % self.heap_tid(tree)   # labelled, as LabVIEW types it
        ET.SubElement(dco, "termBounds").text = lvheap.fmt_bounds((20, 0, 29, 9))
        node.find("termList").append(outer)
        ET.SubElement(node.find("tunnelList"), "SL__arrayElement", {"uid": str(dco_uid)})
        it = ET.SubElement(inner.srn_terms, "SL__arrayElement", {"class": "term", "uid": str(in_uid)})
        ET.SubElement(it, "objFlags").text = str(0x20000 if d == "in" else 0x28000)
        ET.SubElement(it, "dco", {"uid": str(dco_uid)})
        ip.setdefault("tunnels", []).append((dco, d))
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = tree
        outer_end, inner_end = (str(out_uid), parent), (str(in_uid), inner)
        self.ends[(el.get("uid"), "in")] = outer_end if d == "in" else inner_end
        self.ends[(el.get("uid"), "out")] = inner_end if d == "in" else outer_end
        self.report.append("In Place tunnel %s (%s)" % (el.get("_id"), d))

    def _claim_into(self, obj, aixml_uid, *refs):
        """Give a heap object the AIXML uid (claim() falls back to a fresh one when it is
        taken or reserved) and point the bare references to it at the new number."""
        old = obj.get("uid")
        new = str(self.claim(aixml_uid))
        obj.set("uid", new)
        for r in refs:
            if r is not None and r.get("uid") == old:
                r.set("uid", new)
        if new != old:
            self.used_uids.discard(int(old))

    def add_inplace_node(self, el):
        """One half of an In Place pair. The LEFT half is the element without _id - it creates
        the pair, typed by its outer input; the RIGHT half names the left one in _id."""
        st_el = self._by_uid.get(el.get("uid_parent"))
        ip = next((x for x in getattr(self, "inplaces", []) if x["diag"].owner is st_el), None)
        if ip is None:
            raise Unsupported("Node %s: %r sits outside an In Place Element Structure"
                              % (el.get("uid"), el.get("_name")))
        node, d, parent = ip["node"], ip["diag"], ip["parent"]
        self.inplace_pair = getattr(self, "inplace_pair", {})
        if not el.get("_id"):
            src_net = pairs(el.get("inputs"))[0][1] if pairs(el.get("inputs")) else None
            tree = self.net_type.get(src_net)
            if tree is None:
                raise Unsupported("Node %s: the type of %r is not known" % (el.get("uid"), src_net))
            pair = copy.deepcopy(ip["proto"])
            lvheap.renumber(pair, self.alloc)
            left, right, poser, outer_in, outer_out = list(pair)
            # the left match node keeps the AIXML uid, as LabVIEW's converter keeps it
            self._claim_into(left, el.get("uid"), poser.find("multiSegPipeFlange1Depth"))
            tid = self.heap_tid(unlabel(tree))
            lvheap.set_typeids(left, [tid] * len(lvheap.typeid_elements(left)))
            lvheap.set_typeids(right, [tid] * len(lvheap.typeid_elements(right)))
            # match nodes sit right behind the sRN, before every ordinary node, as LabVIEW lists them
            k = 1 + 2 * len(ip["pairs"])
            d.nodes.insert(k, left)
            d.nodes.insert(k + 1, right)
            node.find("termList").extend([outer_in, outer_out])
            node.find("multiSegPipeFlange2Size").append(poser)
            ip["pairs"].append((left, right))
            self.inplace_pair[el.get("uid")] = (left, right, outer_out, tree, poser)
            self.ends[(el.get("uid"), "in")] = (outer_in.get("uid"), parent)
            self.ends[(el.get("uid"), "out")] = (left.find("termList")[1].get("uid"), d)
        else:
            got = self.inplace_pair.get(el.get("_id"))
            if got is None:
                raise Unsupported("Node %s: no left In Place element %s" % (el.get("uid"), el.get("_id")))
            left, right, outer_out, tree, poser = got
            self._claim_into(right, el.get("uid"), poser.find("multiSegPipeFlange2Depth"))
            self.ends[(el.get("uid"), "in")] = (right.find("termList")[0].get("uid"), d)
            self.ends[(el.get("uid"), "out")] = (outer_out.get("uid"), parent)
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = tree
        self.report.append("In Place element %s (%s)" % (el.get("uid"), "out" if el.get("_id") else "in"))

    def add_seq_tunnel(self, el, frame_el):
        """A Flat Sequence frame's tunnel: one AIXML <Tunnel> with both sides, outer half on the
        frame (in the parent diagram), inner half in the frame's diagram."""
        d = "in" if el.get("_id", "").startswith("In") else "out"
        seq, frames, parent = next((s for s in self.seqs if any(f is frame_el for _, _, f in s[1])), (None, None, None))
        if seq is None:
            raise Unsupported("Tunnel %s: no flat sequence frame %s" % (el.get("uid"), frame_el.get("uid")))
        fr, fd, _ = next(x for x in frames if x[2] is frame_el)
        got = self.instantiate("seqtun/%s" % d)
        if got is None:
            raise Unsupported("no %s sequence tunnel part harvested" % d)
        wrap = got[0]
        outer, inner = list(wrap)
        src_net = pairs(el.get("inputs"))[0][1] if pairs(el.get("inputs")) else None
        tree = self.net_type.get(src_net)
        if tree is None:
            raise Unsupported("Tunnel %s: the type of %r is not known" % (el.get("uid"), src_net))
        lvheap.set_typeids(wrap, [self.heap_tid(tree) for _ in lvheap.typeid_elements(wrap)])
        fr.find("termList").append(outer)
        ET.SubElement(fr.find("tunnelList"), "SL__arrayElement", {"uid": outer.find("dco").get("uid")})
        fd.srn_terms.append(inner)
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = tree
        outer_end, inner_end = (outer.get("uid"), parent), (inner.get("uid"), fd)
        self.ends[(el.get("uid"), "in")] = outer_end if d == "in" else inner_end
        self.ends[(el.get("uid"), "out")] = inner_end if d == "in" else outer_end
        self.report.append("Sequence tunnel %s (%s)" % (el.get("_id"), d))

    def add_tunnel(self, el, loop_el):
        d = "in" if el.get("_id", "").startswith("In") else "out"
        mode = el.get("mode")
        if mode not in (None, "", "index", "concat") or (mode == "concat" and d != "out"):
            raise Unsupported("Tunnel %s: mode=%r is not composable yet" % (el.get("uid"), mode))
        cond_net = el.get("cond")
        if cond_net and (d != "out" or mode != "index"):
            # only a collecting OUTPUT tunnel filters - never silently dropped otherwise
            raise Unsupported("Tunnel %s: cond=%r on a %s%s tunnel is not composable yet"
                              % (el.get("uid"), cond_net, d, " indexing" if mode else ""))
        if mode == "index" and d == "in":
            src = pairs(el.get("inputs"))[0][1] if pairs(el.get("inputs")) else None
            st = self.net_type.get(src)
            if st is not None and st.get("Type") == "SubArray":
                # a sub-array (Transpose 2D Array, Reverse 1D Array ...) indexes like the array
                # it is a view of - read as a scalar it lost NI's mode="index" in ODE-PredatorPrey
                st = copy.deepcopy(st)
                st.set("Type", "Array")
                self.net_type[src] = st
            if st is not None and typedef_inner(st).get("Type") != "Array":
                # (an array inside a TYPEDEF indexes like any array - All Types[] is
                # ASC_ArrayOfUniqueIDStringParentIDStringName.ctl, lvai_class_names.xml)
                # NI's exports mark some input tunnels `index` that a string or a scalar feeds -
                # which LabVIEW cannot index (Database Delete.vi: a string constant); the
                # tunnel in the VI is a plain one
                mode = None
        loop, inner, parent = next(x for x in self.loops if x[1].owner is loop_el)
        kind = loop.get("class")
        suffix = "-index" if mode == "index" else ""
        got = None
        for k in (kind, "whileLoop", "forLoop"):   # an lpTun is the same on both loops
            got = self.instantiate("tunnel/%s/%s%s" % (k, d, suffix))
            if got is not None:
                break
        if got is None:
            raise Unsupported("no %s%s tunnel part harvested" % (d, suffix))
        wrap = got[0]
        outer, innert = list(wrap)
        dco = outer.find("dco")
        if mode == "concat":
            # A CONCATENATING output: a plain lpTun - no innerLpTunDCO - typed with the array on
            # both sides, TunnelType and DefaultTunnelType 02. Exactly the For Loop part's donor,
            # NI's TDMS Advanced Finite Asynchronous Read.vi (a 1D U32 array, 2026-10-10).
            for tag in ("TunnelType", "DefaultTunnelType"):
                tt = dco.find(tag)
                if tt is None:
                    tt = ET.Element(tag)
                    tb_ = dco.find("termBounds")
                    dco.insert(list(dco).index(tb_) + 1 if tb_ is not None else len(dco), tt)
                tt.text = "02"
            # (the order typeDesc, termBounds, TunnelType, DefaultTunnelType is the donor's)
            tts = [dco.find("TunnelType"), dco.find("DefaultTunnelType")]
            for tt in tts:
                dco.remove(tt)
            tb_ = dco.find("termBounds")
            at = list(dco).index(tb_) + 1 if tb_ is not None else len(dco)
            for i, tt in enumerate(tts):
                dco.insert(at + i, tt)
        elif mode != "index":
            # a PLAIN tunnel carries no TunnelType: the For Loop part (cut from an NI TDMS
            # example) had TunnelType 02, a CONCATENATING tunnel, and every plain For Loop
            # output composed from it was broken (t31, measured 2026-10-10)
            for tag in ("TunnelType", "DefaultTunnelType"):
                for tt in dco.findall(tag):
                    dco.remove(tt)
        src_net = pairs(el.get("inputs"))[0][1] if pairs(el.get("inputs")) else None
        tree = self.net_type.get(src_net)
        if tree is None:
            raise Unsupported("Tunnel %s: the type of %r is not known" % (el.get("uid"), src_net))
        if mode == "concat" and typedef_inner(tree).get("Type") != "Array":
            raise Unsupported("Tunnel %s: a concatenating output needs an array inside" % el.get("uid"))
        if mode == "index":
            # An INDEXING tunnel is the lpTun typed with the ARRAY plus an innerLpTunDCO typed
            # with the ELEMENT (measured on an AIXML-generated For Loop): going in, the wire
            # outside is the array; going out, the wire inside is the element.
            if d == "in":
                # A typedef'd array keeps its typedef OUTSIDE and hands out its element inside -
                # itself a typedef there: NI's converter's build of lvai_class_names.xml types the
                # lpTun TypeDef{Array{TypeDef{Cluster}}} and the inner DCO TypeDef{Cluster}.
                base = typedef_inner(tree)
                if base.get("Type") != "Array":
                    raise Unsupported("Tunnel %s: an indexing input needs an array" % el.get("uid"))
                arr, elem = tree, unlabel(elem_of(base))
                n = len(base.findall("Dimension"))
                if n > 1:
                    # indexing an N-D array yields its (N-1)-D rows
                    elem = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
                    for _ in range(n - 1):
                        ET.SubElement(elem, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
                    elem.append(copy.deepcopy(unlabel(elem_of(base))))
            else:
                elem = tree
                # collecting arrays builds ONE array of one more dimension, never an array of
                # arrays (LabVIEW has none)
                base = elem_of(elem) if elem.get("Type") == "Array" else elem
                n = len(elem.findall("Dimension")) + 1 if elem.get("Type") == "Array" else 1
                arr = ET.Element("TypeDesc", {"Type": "Array", "Format": "inline"})
                for _ in range(n):
                    ET.SubElement(arr, "Dimension", {"Flags": "0x00FF", "FixedSize": "0xFFFFFF"})
                arr.append(copy.deepcopy(unlabel(base)))
            dco.find("typeDesc").text = "TypeID(%d)" % self.heap_tid(arr)
            dco.find("innerLpTunDCO/typeDesc").text = "TypeID(%d)" % self.heap_tid(elem)
            out_tree = elem if d == "in" else arr
        else:
            lvheap.set_typeids(outer, [self.heap_tid(tree) for _ in lvheap.typeid_elements(outer)])
            out_tree = tree
        for e in wrap.iter():
            e.attrib.pop("_t", None)   # the shell's bookkeeping travelled with the template
        for _, net in pairs(el.get("outputs")):
            if net:
                self.net_type[net] = out_tree
        n = len(loop.find("tunnelList"))
        tb = dco.find("termBounds")
        if tb is not None:
            t, l, b, r = lvheap.parse_bounds(tb.text)
            tb.text = lvheap.fmt_bounds((20 + 16 * n, l, 20 + 16 * n + (b - t), r))
        loop.find("termList").append(outer)
        ET.SubElement(loop.find("tunnelList"), "SL__arrayElement", {"uid": dco.get("uid")})
        inner.srn_terms.append(innert)
        if cond_net:
            self._add_tunnel_condition(el, loop, inner, dco)
        outer_end, inner_end = (outer.get("uid"), parent), (innert.get("uid"), inner)
        self.ends[(el.get("uid"), "in")] = outer_end if d == "in" else inner_end
        self.ends[(el.get("uid"), "out")] = inner_end if d == "in" else outer_end
        self.report.append("Tunnel %s (%s)" % (el.get("_id"), d))

    def _add_tunnel_condition(self, el, loop, inner, dco):
        """A CONDITIONAL output tunnel (AIXML cond="<net>"): the lpTun and its innerLpTunDCO
        carry 0x1000000, the lpTun ends in IsConditional True and LpTunConditionDCO, and the
        condition is a terminal of its own - an lpTunConditionDCO on the loop border (termList
        [inner, outer], back-reference lpTunDCO, NOT in the tunnelList) with its boolean input
        inside, in the inner sRN. Read off NI's Separate Array Values.vi."""
        for d_ in (dco, dco.find("innerLpTunDCO")):
            f = d_.find("objFlags")
            if f is None:
                f = ET.Element("objFlags")
                d_.insert(0, f)
            f.text = str(int(f.text or 0) | 0x1000000)
        cd_uid, ot_uid, it_uid = self.alloc(), self.alloc(), self.alloc()
        ET.SubElement(dco, "IsConditional").text = "True"
        ET.SubElement(dco, "LpTunConditionDCO", {"uid": str(cd_uid)})
        ot = ET.SubElement(loop.find("termList"), "SL__arrayElement", {"class": "term", "uid": str(ot_uid)})
        ET.SubElement(ot, "objFlags").text = "256"
        cd = ET.SubElement(ot, "dco", {"class": "lpTunConditionDCO", "uid": str(cd_uid)})
        ET.SubElement(cd, "objFlags").text = "1"
        tl = ET.SubElement(cd, "termList", {"elements": "2"})
        ET.SubElement(tl, "SL__arrayElement", {"uid": str(it_uid)})
        ET.SubElement(tl, "SL__arrayElement", {"uid": str(ot_uid)})
        ET.SubElement(cd, "typeDesc").text = "TypeID(%d)" % self.heap_tid(type_tree("bool"))
        tb = dco.find("termBounds")
        t, l, b, r = lvheap.parse_bounds(tb.text) if tb is not None else (20, 0, 29, 9)
        ET.SubElement(cd, "termBounds").text = lvheap.fmt_bounds((b, l, b + 9, r))
        ET.SubElement(cd, "lpTunDCO", {"uid": dco.get("uid")})
        it = ET.SubElement(inner.srn_terms, "SL__arrayElement", {"class": "term", "uid": str(it_uid)})
        ET.SubElement(it, "objFlags").text = "164161"
        ET.SubElement(it, "dco", {"uid": str(cd_uid)})
        self.ends[(el.get("uid"), "cond")] = (str(it_uid), inner)
        self.cond_tunnels = getattr(self, "cond_tunnels", [])
        self.cond_tunnels.append((dco, cd))

    def add_shiftreg(self, el, loop_el):
        """AIXML <ShiftReg><Left/><Right/></ShiftReg> onto the harvested four-terminal group:
        Left takes the initializer OUTSIDE and supplies the previous value INSIDE; Right
        collects the next value INSIDE and delivers the final one OUTSIDE. Only an
        INITIALIZED register is composed - an uninitialized one takes its type from the inner
        diagram, a cycle this composer does not resolve."""
        left = next((c for c in el if isinstance(c.tag, str) and c.tag == "Left"), None)
        right = next((c for c in el if isinstance(c.tag, str) and c.tag == "Right"), None)
        init = [n for _, n in pairs(left.get("inputs"))] if left is not None else []
        # a STACKED register has one Left with several elements (value, value2, value3 - the
        # k-th input initializes, the k-th output reads, element k): each element is an lSR of
        # its own, all named in the rSR's lsrDCOList top to bottom (NI's Running Average with
        # Shift Registers.vi). Mapped onto ONE left terminal it put that terminal into three
        # signals - "Insane List {dupe}" and a fatal DAbort that killed LabVIEW.
        stack = max(len(init), len(pairs(left.get("outputs") if left is not None else "")), 1)
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
        r_dco = r_out.find("dco")
        lsr_list = r_dco.find("lsrDCOList")
        for k in range(1, stack):
            lo, li = copy.deepcopy(l_out), copy.deepcopy(l_in)
            lo.set("uid", str(self.alloc()))
            li.set("uid", str(self.alloc()))
            lod = lo.find("dco")
            lod.set("uid", str(self.alloc()))
            tl = lod.find("termList")
            for c in list(tl):
                tl.remove(c)
            ET.SubElement(tl, "SL__arrayElement", {"uid": li.get("uid")})
            ET.SubElement(tl, "SL__arrayElement", {"uid": lo.get("uid")})
            li.find("dco").set("uid", lod.get("uid"))
            if k >= len(init) or init[k] is None:
                lo.find("objFlags").text = str(int(lo.findtext("objFlags") or 0) & ~0x8000)
            ET.SubElement(lsr_list, "SL__arrayElement", {"uid": lod.get("uid")})
            loop.find("termList").append(lo)
            inner.srn_terms.append(li)
            self.ends[(left.get("uid"), "in#%d" % k)] = (lo.get("uid"), parent)
            self.ends[(left.get("uid"), "out#%d" % k)] = (li.get("uid"), inner)
        lsr_list.set("elements", str(len(lsr_list)))
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
            # an enum part serves every enum width: its items and maximum are rewritten anyway,
            # and the NI-example UnitUInt32 ring was measured breaking a VI on its own where the
            # clean UnitUInt16 one, re-typed, works
            # numeric parts adapt between integer and float representations only: a complex
            # part re-typed int32 kept its complex limits and pylabview refused it
            plain_num = set(INT_RANGE) | set(FLOAT_RANGE)
            adaptable = exact or (kt in plain_num and t in plain_num) or \
                (kt.startswith("UnitUInt") and t.startswith("UnitUInt"))
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
        if el.tag in ("Control", "Indicator", "Constant", "FixedConst"):
            return ([(n, (uid, "value")) for _, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, "value")) for _, n in pairs(el.get("inputs")) if n])
        if el.tag == "Node" and unescape(el.get("_name") or "") in self.INPLACE_PAIRS:
            # both of its terminals are called `value`; one is outside, one inside
            return ([(n, (uid, "out")) for _, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, "in")) for _, n in pairs(el.get("inputs")) if n])
        if el.tag in ("Node", "Call"):
            # a name used twice (Build Array's `element`) is numbered: element, element#1, ...
            return ([(n, (uid, out_key(el, t))) for t, n in keyed(pairs(el.get("outputs"))) if n],
                    [(n, (uid, t)) for t, n in keyed(pairs(el.get("inputs"))) if n])
        if el.tag == "Left":
            # a stacked register's elements in order: out, out#1, out#2 / in, in#1, in#2
            k = lambda base, i: base if i == 0 else "%s#%d" % (base, i)
            return ([(n, (uid, k("out", i))) for i, (_, n) in enumerate(pairs(el.get("outputs"))) if n],
                    [(n, (uid, k("in", i))) for i, (_, n) in enumerate(pairs(el.get("inputs"))) if n])
        if el.tag in ("Tunnel", "Right"):
            # cond="true" is a CASE tunnel's use-default flag; on a loop tunnel cond names the
            # net that decides which elements are collected
            cond = el.get("cond")
            return ([(n, (uid, "out")) for _, n in pairs(el.get("outputs")) if n],
                    [(n, (uid, "in")) for _, n in pairs(el.get("inputs")) if n]
                    + ([(cond, (uid, "cond"))] if cond and cond not in ("true", "false") else []))
        if el.tag == "Condition":
            return [], [(n, (el.get("_loop"), "stop")) for _, n in pairs(el.get("inputs")) if n]
        if el.tag == "CaseFrame":
            return ([(el.get("selectout"), (uid, "selectout"))] if el.get("selectout") else []), []
        if el.tag == "Structure" and el.get("_name") == "Event Structure":
            net = getattr(self, "dyn_reg", {}).get(uid)
            return [], ([(net, (uid, "dyn"))] if net else [])
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
            kind0 = self.net_type.get(net)
            bad = unverified_wire_type(kind0)
            if bad and bad not in os.environ.get("AIXML2VI_TRY_WIRES", "").split(","):
                # AIXML2VI_TRY_WIRES=Tag.DAQmxTerminal,... lets ONE measurement carry a type
                # before it joins the verified set
                # a FATAL "Insane Wire {sub}" with a DataLog refnum from a subVI's pane
                # (NI's Monitoring Keyboard and Mouse Activity.vi): a wire may only carry a type
                # this composer has carried on a measured wire
                raise Unsupported("net %r carries a %s, which no composed wire has carried yet" % (net, bad))
            missing = [key for _, key in [ends["src"]] + ends["sinks"] if key not in self.ends]
            if missing:
                # an end nothing composed - a For Loop's conditional terminal (a <Condition> in a
                # For Loop; the part has no stop terminal) crashed seven of NI's VIs as a KeyError
                el0 = self._by_uid.get(missing[0][0])
                raise Unsupported("net %r ends at %s %s of %s, which is not composable yet"
                                  % (net, missing[0][1], missing[0][0],
                                     (el0.get("_name") or el0.tag) if el0 is not None else "an element"))
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
            if kind is not None and kind.get("Type") == "TypeDef":
                # the kind of the type a typedef wraps: 0x108 on an ItemRef.ctl (uint64) wire in
                # NI's converter's build of Get Item Info.vi's export, where this wrote 266 -
                # LabVIEW recomputes the kind (signal_kind's note), but the file now says what NI's does
                kind = typedef_inner(kind)
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
        frame_dims = {}       # (an Element takes no Python attributes)
        # Each kind of structure is sized around what it holds, one kind after the other - so a
        # structure inside one of an EARLIER kind was placed at the 120 x 200 default before its
        # own size existed: an Event Structure in a While Loop, whose fourth output tunnel then
        # sat across its bottom border, "Insane Tunnel {graphics} (0x80): owner=Event
        # Structure", in a fresh LabVIEW every time (2026-10-11, t130). Sizing is repeated until
        # nothing changes, one round per level of nesting at most.
        for _ in range(16):
            before = dict(sizes)
            self._size_structures(lv, sizes, frame_dims)
            if sizes == before:
                break
        self._place(self.root, lv, sizes, 40, 40)
        for seq, frames, parent in getattr(self, "seqs", []):
            st, sl_, _, _ = lvheap.parse_bounds(seq.find("bounds").text)
            x = 0
            for (fr, fd, _), (h, w) in zip(frames, frame_dims[id(seq)]):
                H = lvheap.size_of(seq)[0]
                fr.find("bounds").text = lvheap.fmt_bounds((0, x, H, x + w))
                cr = fr.find("contRect")
                if cr is not None:
                    cr.text = lvheap.fmt_bounds((10, x + 6, H - 10, x + w - 6))
                sb = fd.srn.find("bounds")
                if sb is not None:
                    sb.text = lvheap.fmt_bounds((-st, -(sl_ + x), -st, -(sl_ + x)))
                # A frame's tunnels are placed in the FRAME's coordinates; on a border it shares
                # with a neighbour LabVIEW puts them on the bottom edge instead (an AIXML-generated
                # VI: first frame's output at (81, 87) of 90 x 112; on the shared side it was
                # "Insane Sequence Tunnel {graphics}")
                first, last = fr is frames[0][0], fr is frames[-1][0]
                slot = {0: 0, 1: 0, "b0": 0, "b1": 0}
                for t in fr.find("termList"):
                    tb = t.find("dco/termBounds")
                    if tb is None:
                        continue
                    out = int(t.find("dco").findtext("objFlags") or 0) & 1
                    tt, tl_, tbb, tr = lvheap.parse_bounds(tb.text)
                    th, tw = tbb - tt, tr - tl_
                    shared = (out and not last) or (not out and not first)
                    if shared:
                        k = slot["b%d" % out]
                        slot["b%d" % out] += 1
                        y = H - th
                        xx = w - 25 - 12 * k if out else 10 + 12 * k
                    else:
                        y = 20 + 24 * slot[out]
                        slot[out] += 1
                        xx = w - tw if out else 0
                    tb.text = lvheap.fmt_bounds((y, xx, y + th, xx + tw))
                x += w - 5
        for ip in getattr(self, "inplaces", []):
            # contRect 4 px inside, the pairs down the borders from y 23 - as LabVIEW wrote a
            # 101 px structure: left node (23, 0, 35, 16), right node (23, 85, 35, 101)
            node, d = ip["node"], ip["diag"]
            t, l, b, r = lvheap.parse_bounds(node.find("bounds").text)
            cr = node.find("contRect")
            if cr is not None:
                cr.text = lvheap.fmt_bounds((t + 4, l + 4, b - 4, r - 4))
            sb = d.srn.find("bounds")
            if sb is not None:
                sb.text = lvheap.fmt_bounds((-t, -l, -t, -l))
            for k, (left, right) in enumerate(ip["pairs"]):
                y = 23 + 24 * k
                left.find("bounds").text = lvheap.fmt_bounds((y, 0, y + 12, 16))
                right.find("bounds").text = lvheap.fmt_bounds((y, r - l - 16, y + 12, r - l))
            # tunnels below the pairs: inputs down the left border, outputs down the right
            slot = {"in": len(ip["pairs"]), "out": len(ip["pairs"])}
            for tdco, d_ in ip.get("tunnels", []):
                y = 23 + 24 * slot[d_]
                slot[d_] += 1
                x = 0 if d_ == "in" else r - l - 9
                tdco.find("termBounds").text = lvheap.fmt_bounds((y, x, y + 9, x + 9))
            # firstNodeIdx is NOT an index into the node list: LabVIEW wrote 3 for an In Place
            # structure with no pairs and two tunnels, 42 and 69 in NI's examples; written as
            # 1 + 2 x pairs it was 1 there and the VI went "good to bad after compiling". The
            # part's value (3, from an AIXML-generated VI) is kept.
            node.find("termList").set("elements", str(len(node.find("termList"))))
            mspf = node.find("multiSegPipeFlange2Size")
            mspf.set("elements", str(len(mspf)))
        for case, frame_diags, parent in getattr(self, "cases", []):
            t, l, b, r = lvheap.parse_bounds(case.find("bounds").text)
            cr = case.find("contRect")
            if cr is not None:
                cr.text = lvheap.fmt_bounds((t + 6, l + 6, b - 6, r - 6))
            for fd in frame_diags:
                sb = fd.srn.find("bounds")
                if sb is not None:
                    sb.text = lvheap.fmt_bounds((-t, -l, -t, -l))
            event = case.get("class") == "eventStruct"
            slot = {0: 0, 1: 0}
            w_ = r - l
            for c in case.find("termList"):
                dco = c.find("dco")
                tb = dco.find("termBounds")
                if tb is not None and dco.get("class") in ("eventDynDCO", "eventTimeOut"):
                    # the timeout terminal at the top-left corner, the dynamic-event terminals
                    # at the top of the left and right borders (NI's Radio Buttons.vi)
                    tt, tl, tbb, tr = lvheap.parse_bounds(tb.text)
                    if dco.get("class") == "eventTimeOut":
                        tb.text = lvheap.fmt_bounds((0, 0, tbb - tt, tr - tl))
                    else:
                        out = int(dco.findtext("objFlags") or 0) & 1
                        x = w_ - (tr - tl) if out else 0
                        tb.text = lvheap.fmt_bounds((20, x, 20 + tbb - tt, x + tr - tl))
                    continue
                if tb is None or dco.get("class") == "caseSel":
                    continue
                out = int(dco.findtext("objFlags") or 0) & 1
                tt, tl, tbb, tr = lvheap.parse_bounds(tb.text)
                y = (45 if event else 30) + 24 * slot[out]
                slot[out] += 1
                x = (r - l) - (tr - tl) if out else 0
                tb.text = lvheap.fmt_bounds((y, x, y + (tbb - tt), x + (tr - tl)))
            # The selector terminal keeps the donor's coordinates otherwise - y 230 in a donor
            # 273 px tall - and outside a smaller case LabVIEW logs it insane ({graphics} 0x80).
            # It goes on the left edge below the input tunnels.
            sel = next((c for c in case.find("termList") if c.find("dco").get("class") == "caseSel"), None)
            stb = sel.find("dco/termBounds") if sel is not None else None   # a disable has none
            if stb is not None:
                tt, tl, tbb, tr = lvheap.parse_bounds(stb.text)
                y = 30 + 24 * slot[0] + 6
                stb.text = lvheap.fmt_bounds((y, 0, y + (tbb - tt), tr - tl))
        for loop, inner, parent in self.loops:
            for c in loop.find("termList"):
                if c.find("dco") is not None and c.find("dco").get("class") == "lpTunConditionDCO":
                    loop.find("termList").remove(c)
                    loop.find("termList").append(c)    # out of the border slots below
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
                if tb is None or c.find("dco").get("class") in ("lMax", "lpTunConditionDCO"):
                    continue
                out = int(c.find("dco").findtext("objFlags") or 0) & 1
                tt, tl, tbb, tr = lvheap.parse_bounds(tb.text)
                y = 20 + 24 * slot[out]
                slot[out] += 1
                x = w - (tr - tl) if out else 0
                tb.text = lvheap.fmt_bounds((y, x, y + (tbb - tt), x + (tr - tl)))
                for tdco, cdco in getattr(self, "cond_tunnels", []):
                    if tdco is c.find("dco"):
                        # the condition sits right below its tunnel, as LabVIEW draws it
                        cdco.find("termBounds").text = lvheap.fmt_bounds((y + (tbb - tt), x, y + (tbb - tt) + 9, x + (tr - tl)))

    def _size_structures(self, lv, sizes, frame_dims):
        """One round of sizing every structure around its placed contents (layout())."""
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
        for ip in reversed(getattr(self, "inplaces", [])):
            w, h = self._place(ip["diag"], lv, sizes, 30, 30)
            n_t = {d_: sum(1 for _, x in ip.get("tunnels", []) if x == d_) for d_ in ("in", "out")}
            n = max(1, len(ip["pairs"]) + max(n_t.values()))
            sizes[ip["node"].get("uid")] = (max(h + 40, 23 + 24 * n + 20, 90), max(w + 40, 100))
        for seq, frames, parent in reversed(getattr(self, "seqs", [])):
            dims = []
            for fr, fd, _ in frames:
                w, h = self._place(fd, lv, sizes, 30, 30)
                n_in = sum(1 for t in fr.find("termList") if not int(t.find("dco").findtext("objFlags") or 0) & 1)
                n_out = len(fr.find("termList")) - n_in
                dims.append((max(h + 40, 30 + 24 * max(n_in, n_out, 1) + 20, 90), max(w + 40, 112)))
            H = max(d[0] for d in dims)
            W = sum(d[1] for d in dims) - 5 * (len(dims) - 1)   # neighbours share 5 px of border
            sizes[seq.get("uid")] = (H, W)
            frame_dims[id(seq)] = dims

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
                place_obj(obj, y, x)
                h, w = lvheap.size_of(obj)
            rows[L] = rows.get(L, 0) + max(h, 32) + 30
            right, bottom = max(right, x + w), max(bottom, y + h)
        return right, bottom

    def connector_pane(self):
        top = max([con for con, _, _, _ in self.pane_terms] or [-1])
        if top >= self.pane_codes.get(self.con_id, (0x108, 16))[1]:
            # a conIdx past the default pattern's slots: NI's converter then picks a larger
            # pattern by itself (4834, 20 slots, for conIdx up to 19 - CLAUDE.md); the same
            # family up to 4835's 28, refused beyond. Indexed past the list it was an IndexError.
            bigger = [p for p in (4834, 4835) if self.pane_codes.get(p, (0, 0))[1] > top]
            if not bigger:
                raise Unsupported("conIdx %d needs a connector pane of more than 28 terminals" % top)
            self.con_id = bigger[0]
            self.fp.find("root/conPane/conId").text = str(self.con_id)
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
            if el.tag in ("Structure", "CaseFrame", "Diagram") or                     (el.tag == "Node" and any(isinstance(c.tag, str) and c.tag == "Diagram" for c in el)):
                out.append(el)
                for c in self.flatten(el):
                    if c.tag == "Condition" and not c.get("_loop"):
                        # the INNERMOST structure is the loop; every enclosing one used to
                        # overwrite it, and a While Loop inside a Case Structure lost its stop
                        # terminal's wire (seven of NI's VIs: KeyError ('<case uid>', 'stop'))
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
        self.con_taken = {}
        self.term_label = {}
        elements = []
        for el in self.flatten(vi):
            if el.tag in ("Control", "Indicator", "Constant", "Node", "Structure", "Tunnel", "Condition",
                          "ShiftReg", "Left", "Right", "CaseFrame", "Call", "FixedConst", "Diagram"):
                elements.append(el)
            elif el.tag in ("FreeLabel",):
                self.report.append("skipped %s (comments are not composed yet)" % el.tag)
            else:
                raise Unsupported("element <%s> is not composable yet" % el.tag)
        by_uid = {e.get("uid"): e for e in elements}
        self._by_uid = by_uid
        self._elements = elements
        # structures first, outermost first, so every diagram exists before anything lands in it
        for el in [e for e in elements if e.tag == "Structure"]:
            if el.get("_name") == "Case Structure":
                self.add_case(el)
            elif el.get("_name") == "Event Structure":
                self.add_event(el)
            elif el.get("_name") == "Flat Sequence Frame":
                self.add_flatseq(el)
            elif el.get("_name") == "In Place Element Structure":
                self.add_inplace(el)
            else:
                self.add_loop(el)
        for el in [e for e in elements if e.tag == "Node" and unescape(e.get("_name") or "") in self.DISABLES]:
            self.add_disable(el)

        def seq_frame(uid):
            """The flat-sequence frame a tunnel sits on: a <Diagram> of one, or the structure."""
            p = by_uid.get(uid)
            if p is not None and p.tag == "Diagram":
                gp = by_uid.get(p.get("uid_parent"))
                return p if gp is not None and gp.get("_name") == "Flat Sequence Frame" else None
            return p if p is not None and p.get("_name") == "Flat Sequence Frame" else None
        # a case tunnel is written as one outside half on the structure and one inside half
        # per frame, sharing an _id; it is scheduled as one item, by its outside half
        case_halves = {}
        for e in elements:
            if e.tag == "Tunnel":
                p = by_uid.get(e.get("uid_parent"))
                gp = by_uid.get(p.get("uid_parent")) if p is not None else None
                if p is not None and (p.tag == "CaseFrame" or (p.tag == "Diagram" and gp is not None and gp.tag == "Node")):
                    case_halves.setdefault((p.get("uid_parent"), e.get("_id")), {})[p.get("uid")] = e
        row = {"Control": 0, "Indicator": 0}
        for el in [e for e in elements if e.tag in ("Control", "Indicator")]:
            self.add_fp(el, row[el.tag])
            row[el.tag] += 1
        for el in [e for e in elements if e.tag in ("Constant", "FixedConst")]:
            self.add_const(el) if el.tag == "Constant" else self.add_fixed(el)
        # nodes and tunnels in dependency order, so every input type is known when it is placed
        def is_case(uid):
            """A case structure, or a disable structure - tunnels with one inner half per frame."""
            p = by_uid.get(uid)
            return p is not None and ((p.tag == "Structure" and p.get("_name") in ("Case Structure", "Event Structure"))
                                      or (p.tag == "Node" and unescape(p.get("_name") or "") in self.DISABLES))
        pending = [e for e in elements if (e.tag in ("Node", "ShiftReg", "Call")
                                           and unescape(e.get("_name") or "") not in self.DISABLES)
                   or (e.tag == "Tunnel" and by_uid.get(e.get("uid_parent")) is not None
                       and (by_uid[e.get("uid_parent")].tag == "Structure" or is_case(e.get("uid_parent"))
                            or seq_frame(e.get("uid_parent")) is not None))]

        def inputs_of(e):
            if e.tag == "Node" and unescape(e.get("_name") or "") == "Feedback Node":
                # its next value comes from downstream - only the initializer orders it; with
                # NO initializer the node waits for its next value instead, which 16 of the 19
                # such nodes in NI's exports take from something that does not read the node's
                # own output (a control, a constant, a call) - the rest stay refused
                ins = pairs(e.get("inputs"))
                init = [(k, n) for k, n in ins if k == "initializer"]
                return init if any(n for _, n in init) else [(k, n) for k, n in ins if k == "next value"]
            if e.tag == "Node" and unescape(e.get("_name") or "") == "Event Data Node":
                # a USER EVENT's data cluster is read off the registration wired into the
                # structure, so its node waits until every input tunnel of the structure is typed
                frame = by_uid.get(e.get("uid_parent"))
                st = by_uid.get(frame.get("uid_parent")) if frame is not None else None
                if frame is not None and st is not None and (self._is_user_event_frame(frame) or self._is_dyn_frame(frame)):
                    return [("reg", n) for t in st if isinstance(t.tag, str) and t.tag == "Tunnel"
                            for _, n in pairs(t.get("inputs")) if n]
            if e.tag == "ShiftReg":   # a register's type comes from its initializer
                left = next(c for c in e if isinstance(c.tag, str) and c.tag == "Left")
                return pairs(left.get("inputs"))
            if e.tag == "Tunnel" and is_case(e.get("uid_parent")) and e.get("_id", "").startswith("Out"):
                halves = case_halves.get((e.get("uid_parent"), e.get("_id")), {}).values()
                ins = [x for h in halves for x in pairs(h.get("inputs")) if x[1]]
                return ins[:1] if any(n in self.net_type for _, n in ins) else ins
            return pairs(e.get("inputs"))
        while pending:
            # a frame's selector value (selectout) is typed as soon as the selector's wire is
            for case, cel, kind, items, dr, frames_el in getattr(self, "pending_cases", []):
                if cel.get("selectin") in self.net_type:
                    for f in frames_el:
                        if f.get("selectout"):
                            self.net_type[f.get("selectout")] = unlabel(self.net_type[cel.get("selectin")])
            ready = [e for e in pending if all(net in self.net_type or net is None
                                               for _, net in inputs_of(e))]
            if not ready:
                ready = pending[:1]
            for e in ready:
                nm = unescape(e.get("_name") or "")
                if e.tag == "Node" and nm in self.AGG_NODES:
                    self.add_agg_node(e)
                elif e.tag == "Node" and nm in ("Build Waveform", "Get Waveform Components"):
                    self.add_waveform_node(e)
                elif e.tag == "Node" and nm == "Event Data Node":
                    self.add_event_data(e)
                elif e.tag == "Node" and nm == "Event Filter Node":
                    self.add_event_filter(e)
                elif e.tag == "Node" and nm == "Register For Events":
                    self.add_event_reg(e)
                elif e.tag == "Node" and nm == "Local Variable":
                    self.add_local(e)
                elif e.tag == "Node" and nm == "Feedback Node":
                    self.add_feedback(e)
                elif e.tag == "Node" and nm == "Property Node":
                    self.add_property_node(e)
                elif e.tag == "Node" and nm == "VI Server Reference":
                    self.add_vi_server_ref(e)
                elif e.tag == "Node" and nm == "Static VI Reference":
                    self.add_static_ref(e)
                elif e.tag == "Node" and nm == "Expression Node":
                    self.add_expr_node(e)
                elif e.tag == "Node" and nm == "Global Variable":
                    self.add_global(e)
                elif e.tag == "Node" and nm in self.INPLACE_PAIRS:
                    self.add_inplace_node(e)
                elif e.tag == "Node" and nm.startswith("In Place "):
                    raise Unsupported("Node %s: %r is not composable yet" % (e.get("uid"), nm))
                elif e.tag == "Node" and nm == "Invoke Node":
                    self.add_invoke_node(e)
                elif e.tag == "Node" and nm not in self.prim_by_name and self._templates(nm):
                    self.add_template_node(e)
                elif e.tag == "Node":
                    self.add_node(e)
                elif e.tag == "Call":
                    self.add_call(e)
                elif e.tag == "Tunnel" and seq_frame(e.get("uid_parent")) is not None:
                    self.add_seq_tunnel(e, seq_frame(e.get("uid_parent")))
                elif e.tag == "Tunnel" and is_case(e.get("uid_parent")):
                    self.add_case_tunnel(e, by_uid[e.get("uid_parent")],
                                         case_halves.get((e.get("uid_parent"), e.get("_id")), {}))
                elif e.tag == "Tunnel" and by_uid[e.get("uid_parent")].get("_name") == "In Place Element Structure":
                    self.add_inplace_tunnel(e, by_uid[e.get("uid_parent")])
                elif e.tag == "Tunnel":
                    self.add_tunnel(e, by_uid[e.get("uid_parent")])
                else:
                    self.add_shiftreg(e, by_uid[e.get("uid_parent")])
                pending.remove(e)
        for e in getattr(self, "_synth_event_data", []):
            self.add_event_data(e)
        for e in getattr(self, "_synth_event_filter", []):
            self.add_event_filter(e)
        self.finish_cases()
        self.finish_events()
        nets = self.wire(elements)
        self.layout(nets)
        for hidden, slave in getattr(self, "fb_pairs", []):
            slave.find("bounds").text = hidden.find("bounds").text   # one node on the diagram
            # the hidden node's terminals sit in DIAGRAM coordinates (LabVIEW's: termBounds equal
            # to the node's own top-left), so they move with it
            t0, l0 = self.fb_origin[id(hidden)]
            t1, l1 = lvheap.parse_bounds(hidden.find("bounds").text)[:2]
            for t in hidden.find("termList"):
                _shift_bounds(t.find("dco/termBounds"), t1 - t0, l1 - l0)
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
        for seq, frames, _ in getattr(self, "seqs", []):
            grown.append(seq.find("sequenceList"))
            for fr, _, _ in frames:
                grown += [fr.find("termList"), fr.find("tunnelList")]
        # an In Place structure's tunnelList left at the part's 0 dropped both tunnels from the
        # file and broke the VI (t105) - pylabview writes as many entries as the count says
        for ip in getattr(self, "inplaces", []):
            n = ip["node"]
            grown += [n.find("termList"), n.find("tunnelList"), n.find("multiSegPipeFlange2Size")]
        for lst in grown:
            lst.set("elements", str(len([c for c in lst if c.tag == "SL__arrayElement"])))
        self.cons.set("elements", str(self.pane_codes.get(self.con_id, (0x108, 16))[1]))
        if self.pane_terms:
            # the panel root's objFlags 0x10000 means NO TERMINAL ON THE PANE: every
            # AIXML-generated VI without a pane carries it, NI's own t01 (three terminals) does
            # not. Left in by the skeleton, a VI with a pane logged "Nonfatal insanity (0x1000)"
            # on the first control - on a FRESHLY started LabVIEW only, which is why it hid for
            # weeks behind tests that ran after other VIs (A/B 2026-10-10)
            f = self.fp.find("root/objFlags")
            if f is not None:
                v = int(f.text or 0) & ~0x10000
                if v:
                    f.text = str(v)
                else:
                    self.fp.find("root").remove(f)
        # a ConpaneConnection without an index attribute means 'previous + 1' - so the list
        # must be in slot order (out of order: Connector Pane insane {index})
        conns = sorted(list(self.cons), key=lambda c: int(c.get("index", "0")))
        for c in list(self.cons):
            self.cons.remove(c)
        for c in conns:
            self.cons.append(c)
        self.renumber_piccs()
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
        if getattr(self, "piccs", None):
            # the PICC block sits right before the front-panel heap, as in LabVIEW's own files
            blk = ET.Element("PICC")
            m.insert(list(m).index(m.find("FPHb")), blk)
            for i, hx in sorted(self.piccs.items()):
                fn = "%s_PICC%d.bin" % (base, i)
                with open(os.path.join(out_dir, fn), "wb") as f:
                    f.write(bytes.fromhex(hx))
                ET.SubElement(blk, "Section", {"Index": str(i), "Format": "bin", "File": fn})
        if getattr(self, "strs", None):
            # the STR block sits between FPEx and DSIM, as in LabVIEW's own files
            blk = m.find("STR")
            if blk is None:
                blk = ET.Element("STR")
                m.insert(list(m).index(m.find("FPEx")) + 1, blk)
            for i, sec in sorted(self.strs.items()):
                blk.append(sec)
        if getattr(self, "images", None):
            # the DSIM block ("Dynamic Size Image") sits before the front-panel heap, as in
            # LabVIEW's own files; one binary section per image
            blk = m.find("DSIM")
            if blk is None:
                blk = ET.Element("DSIM")
                m.insert(list(m).index(m.find("FPHb")), blk)
            for i, hx in sorted(self.images.items()):
                fn = "%s_DSIM%d.bin" % (base, i)
                with open(os.path.join(out_dir, fn), "wb") as f:
                    f.write(bytes.fromhex(hx))
                ET.SubElement(blk, "Section", {"Index": str(i), "Format": "bin", "File": fn})
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
    search = []
    args = []
    i = 0
    while i < len(argv):
        if argv[i] == "--parts":
            parts = argv[i + 1]; i += 2
        elif argv[i] == "--keep":
            keep = argv[i + 1]; i += 2
        elif argv[i] == "--search":
            # more folders to resolve a Call in, after the output VI's own (an NI export is
            # composed with its callees beside the ORIGINAL VI)
            search.append(argv[i + 1]); i += 2
        else:
            args.append(argv[i]); i += 1
    src, out_vi = args
    c = Composer(parts)
    c.search_dirs = [os.path.dirname(os.path.abspath(out_vi))] + search
    c.caller_file = os.path.abspath(out_vi)   # relative links are computed from the caller FILE
    try:
        c.compose(src)
    except Unsupported as ex:
        print("UNSUPPORTED: %s" % ex, file=sys.stderr)
        return 2
    base = os.path.splitext(os.path.basename(out_vi))[0]
    # the bundle is an implementation detail unless --keep names a place for it: 719 left
    # behind in %TEMP% (50 MB) before this was cleaned up, 2026-10-10
    bdir = keep or tempfile.mkdtemp(prefix="aixml2vi_")
    try:
        main_xml = c.write(bdir, base)
        lvheap.rebuild(main_xml, out_vi)
    finally:
        if not keep:
            shutil.rmtree(bdir, ignore_errors=True)
    for r in c.report:
        print(r)
    print("wrote %s (%d bytes)%s" % (out_vi, os.path.getsize(out_vi), ", bundle %s" % bdir if keep else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
