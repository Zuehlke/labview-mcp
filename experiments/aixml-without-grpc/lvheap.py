# -*- coding: utf-8 -*-
"""Shared helpers for reading and writing pylabview bundles without LabVIEW.

A pylabview bundle is a MAIN xml (the RSRC catalogue, VCTP type table among it), a front-panel
heap FPHb.xml, a block-diagram heap BDHb.xml and a handful of binary sidecars. Heap objects
point at types with <typeDesc>TypeID(n)</typeDesc>, where n is a HEAP type id:

    heap TypeID n  --DTHP IndexShift-->  consolidated index  --VCTP TopLevel-->  flat TypeDesc

and a flat TypeDesc points at its children by FLAT id. `TypeTree` resolves all of that into one
self-contained element so a part can travel between VIs; `TypeTable` does the reverse when a VI
is composed.
"""
import copy
import os
import re
import subprocess
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
PYLABVIEW_CMD = os.path.join(REPO, "tools", "pylabview", "runtime", "pylabview.cmd")

TYPEID_RE = re.compile(r"TypeID\((\d+)\)")


def run_pylabview(args, timeout=120):
    r = subprocess.run(["cmd", "/c", PYLABVIEW_CMD] + args, stdin=subprocess.DEVNULL,
                       capture_output=True, text=True, timeout=timeout)
    return r.returncode, r.stdout + r.stderr


def extract(vi_path, out_dir):
    """VI -> bundle. Returns the main xml path."""
    os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(vi_path))[0]
    main = os.path.join(out_dir, base + ".xml")
    code, out = run_pylabview(["-x", "-i", vi_path, "-m", main])
    if code != 0 or not os.path.exists(main):
        raise RuntimeError("extract failed for %s: %s" % (vi_path, out[-2000:]))
    return main


def rebuild(main_xml, vi_path):
    code, out = run_pylabview(["-c", "-m", main_xml, "-i", vi_path])
    if code != 0 or not os.path.exists(vi_path):
        raise RuntimeError("rebuild failed: %s" % out[-3000:])
    return out


class Bundle:
    """Read-only view of an extracted bundle: main tree, both heaps, the type chain."""

    def __init__(self, main_xml):
        self.main_xml = main_xml
        self.dir = os.path.dirname(main_xml)
        self.main = ET.parse(main_xml).getroot()
        self.fp = self._heap("FPHb")
        self.bd = self._heap("BDHb")
        vctp = self.main.find("VCTP/Section")
        self.flat = [td for td in vctp if td.tag == "TypeDesc"]
        self.toplevel = {int(t.get("Index")): int(t.get("FlatTypeID"))
                         for t in vctp.find("TopLevel")}
        dthp = self.main.find("DTHP/Section/TypeDescSlice")
        self.index_shift = int(dthp.get("IndexShift")) if dthp is not None else 0

    def _heap(self, block):
        sec = self.main.find(block + "/Section")
        if sec is None or sec.get("File") is None:
            return None
        return ET.parse(os.path.join(self.dir, sec.get("File"))).getroot()

    def flat_of_heap(self, heap_tid):
        return self.toplevel[self.index_shift + heap_tid - 1]

    def flat_of_consolidated(self, cons):
        return self.toplevel[cons]

    def tree(self, flat_id):
        """A flat TypeDesc with every child reference inlined, recursively."""
        src = self.flat[flat_id]
        out = ET.Element("TypeDesc", {k: v for k, v in src.attrib.items()})
        for child in src:
            if child.tag != "TypeDesc":
                out.append(copy.deepcopy(child))
                continue
            if child.get("TypeID") is not None:
                sub = self.tree(int(child.get("TypeID")))
                for k, v in child.attrib.items():
                    if k != "TypeID":
                        sub.set("ref." + k, v)
                out.append(sub)
            else:
                out.append(copy.deepcopy(child))
        return out

    def heap_type_tree(self, heap_tid):
        return self.tree(self.flat_of_heap(heap_tid))


class TypeTable:
    """Builds VCTP for a composed VI: flat types deduplicated, one TopLevel index per use."""

    # Flat 0 is Void; the two connector-pane Function types are allocated by the composer.
    def __init__(self):
        self.flat = []          # list of ET elements in VCTP form (children as TypeID refs)
        self.canon = {}         # canonical string -> flat id
        self.toplevel = []      # list of flat ids; index i -> consolidated i+1
        self.add_flat(ET.Element("TypeDesc", {"Type": "Void", "Format": "inline"}))

    def add_flat(self, el):
        key = ET.tostring(el, encoding="unicode")
        if key in self.canon:
            return self.canon[key]
        self.flat.append(el)
        self.canon[key] = len(self.flat) - 1
        return len(self.flat) - 1

    def register(self, tree):
        """Inlined type tree -> flat id."""
        el = ET.Element("TypeDesc", {k: v for k, v in tree.attrib.items()
                                     if not k.startswith("ref.")})
        if "Format" not in el.attrib:
            el.set("Format", "inline")
        for child in tree:
            # A typedef's wrapped type is written INLINE (Nested="True"), never as a reference -
            # pylabview's TypeDef reader requires a Type on it and refuses a TypeID.
            if child.tag == "TypeDesc" and child.get("Type") is not None and child.get("Nested") != "True":
                fid = self.register(child)
                ref = ET.Element("TypeDesc", {"TypeID": str(fid)})
                for k, v in child.attrib.items():
                    if k.startswith("ref."):
                        ref.set(k[4:], v)
                el.append(ref)
            else:
                el.append(copy.deepcopy(child))
        return self.add_flat(el)

    def use(self, flat_id):
        """A new consolidated index pointing at flat_id; returns the consolidated index."""
        self.toplevel.append(flat_id)
        return len(self.toplevel)

    def vctp_section(self):
        sec = ET.Element("Section", {"Index": "0", "Format": "inline"})
        for el in self.flat:
            sec.append(copy.deepcopy(el))
        top = ET.SubElement(sec, "TopLevel")
        for i, fid in enumerate(self.toplevel, 1):
            ET.SubElement(top, "TypeDesc", {"Index": str(i), "FlatTypeID": str(fid)})
        return sec


def type_key(tree):
    """A short human key for a type tree, used to index parts: NumFloat64, Boolean, String..."""
    t = tree.get("Type")
    if t == "MeasureData":
        return "MeasureData." + (tree.get("Flavor") or "")
    if t == "Array":
        # an array part is only reusable as-is for its own element kind (an array constant's
        # element is a frameless object of that kind), so the key carries it: Array.String,
        # Array2.NumFloat64
        dims = len(tree.findall("Dimension"))
        elem = [c for c in tree if c.tag == "TypeDesc"]
        return "Array%s.%s" % (dims if dims > 1 else "", type_key(elem[0]) if elem else "?")
    return t


def strip_label(tree):
    t = copy.deepcopy(tree)
    t.attrib.pop("Label", None)
    return t


def all_uids(el):
    return [int(e.get("uid")) for e in el.iter() if e.get("uid") is not None]


def renumber(el, alloc, keep_external=()):
    """Give every uid DEFINED in el a fresh number (an element with children or a class is a
    definition; a bare <X uid=".."/> is a reference). References to uids defined inside el are
    rewritten too; references to anything else are left alone. Returns old->new."""
    defined = {}
    for e in el.iter():
        uid = e.get("uid")
        if uid is None:
            continue
        if e.get("class") is not None or len(e) > 0 or e.text:
            if int(uid) not in defined:
                defined[int(uid)] = alloc()
    for e in el.iter():
        uid = e.get("uid")
        if uid is not None and int(uid) in defined:
            e.set("uid", str(defined[int(uid)]))
    return defined


def set_typeids(el, tids):
    """Replace each <typeDesc>TypeID(n)</typeDesc> in document order with tids[i]."""
    i = 0
    for e in el.iter("typeDesc"):
        if e.text and TYPEID_RE.fullmatch(e.text.strip()):
            e.text = "TypeID(%d)" % tids[i]
            i += 1
    return i


def typeid_elements(el):
    return [e for e in el.iter("typeDesc") if e.text and TYPEID_RE.fullmatch(e.text.strip())]


def set_count(el):
    """Bring every elements="N" attribute in el in line with its children."""
    for e in el.iter():
        if e.get("elements") is not None:
            e.set("elements", str(len([c for c in e if c.tag == "SL__arrayElement"])))


def parse_bounds(s):
    return [int(x) for x in s.strip("() ").split(",")]


def fmt_bounds(b):
    return "(%d, %d, %d, %d)" % tuple(b)


def move_to(el, top, left):
    """Move a top-level heap object so its bounds start at (top, left); keep its size."""
    b = el.find("bounds")
    if b is None:
        return
    t, l, bt, r = parse_bounds(b.text)
    b.text = fmt_bounds((top, left, top + (bt - t), left + (r - l)))


def size_of(el):
    b = el.find("bounds")
    if b is None:
        return (0, 0)
    t, l, bt, r = parse_bounds(b.text)
    return (bt - t, r - l)
