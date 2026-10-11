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
    # subprocess.run's own timeout kills only cmd.exe: the python grandchild keeps the pipes open
    # and the second communicate() then waits for ever - a coverage run hung 7 minutes on one
    # vi.lib VI that way (2026-10-10). Kill the whole tree instead.
    p = subprocess.Popen(["cmd", "/c", PYLABVIEW_CMD] + args, stdin=subprocess.DEVNULL,
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        out, err = p.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
        out, err = p.communicate()
        raise RuntimeError("pylabview timed out after %d s: %s" % (timeout, " ".join(args)))
    return p.returncode, out + err


def extract(vi_path, out_dir, timeout=120):
    """VI -> bundle. Returns the main xml path."""
    os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(vi_path))[0]
    main = os.path.join(out_dir, base + ".xml")
    code, out = run_pylabview(["-x", "-i", vi_path, "-m", main], timeout=timeout)
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
        """A flat TypeDesc with every child reference inlined, recursively. A reference past
        the end of the list (NI's Set Collection - Word Counting.vi: a set type) comes back as
        an Unknown type, which the reader reports, instead of ending the whole read."""
        if not 0 <= flat_id < len(self.flat):
            return ET.Element("TypeDesc", {"Type": "Unknown", "Format": "inline", "ref.TypeID": str(flat_id)})
        return self._inline(self.flat[flat_id])

    def _inline(self, src):
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
                # An INLINE child - a typedef's wrapped type, Nested="True" - has TypeID children
                # of its own, and they index THIS file's flat list too. Copied as they stood, a
                # harvested LVPositionTypeDef.ctl carried its donor's ids for Left and Top into
                # the composed VI, where 2 and 3 were two strings, and the VI loaded broken
                # (2026-10-10, lvbd_graft_paste.xml; 72 of 1167 parts were affected).
                out.append(self._inline(child))
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
        return self.add_flat(self._flat_form(tree))

    def _flat_form(self, tree):
        el = ET.Element("TypeDesc", {k: v for k, v in tree.attrib.items()
                                     if not k.startswith("ref.")})
        if "Format" not in el.attrib:
            el.set("Format", "inline")
        for child in tree:
            if child.tag == "TypeDesc" and child.get("Type") is not None and child.get("Nested") != "True":
                fid = self.register(child)
                ref = ET.Element("TypeDesc", {"TypeID": str(fid)})
                for k, v in child.attrib.items():
                    if k.startswith("ref."):
                        ref.set(k[4:], v)
                el.append(ref)
            elif child.tag == "TypeDesc" and child.get("Nested") == "True":
                # A typedef's wrapped type is written INLINE, never as a reference - pylabview's
                # TypeDef reader requires a Type on it and refuses a TypeID - but ITS children are
                # references into this VI's list like any other, so they are registered here.
                el.append(self._flat_form(child))
            else:
                el.append(copy.deepcopy(child))
        return el

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
    if t == "Tag":
        return "Tag." + (tree.get("TagType") or "?")
    if t == "Refnum":
        # a VI Server, VISA, user-event and class refnum are different objects behind one Type:
        # the parts once filed as plain "Refnum" were four kinds under one key (2026-10-10)
        return "Refnum." + (tree.get("RefType") or "?")
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
    """Replace each type reference typeid_elements() finds - a <typeDesc>, a chart's <histTD> -
    in document order with tids[i]. It walked <typeDesc> alone until 2026-10-11, so a histTD kept
    the donor's number whatever the harvest carried."""
    els = typeid_elements(el)
    if len(tids) < len(els):
        # as strict as the loop it replaces, which indexed tids[i]: a part whose type list is
        # shorter than its references was harvested before the reference kind was known
        raise IndexError("%d type references, %d types" % (len(els), len(tids)))
    for e, tid in zip(els, tids):
        e.text = "TypeID(%d)" % tid
    return len(els)


# Heap tags that name a type the object's own data does not decide, and that harvest and compose
# must carry like a typeDesc. A CHART's histTD (its history buffer: a cluster around a 1024-element
# array of the data type) was missing until 2026-10-11: harvested parts lacked its type, the
# composer left the donor's number in place, it named whatever that number was in the new VI (a
# Boolean in the probe), and loading the VI logged "bad mutex verifier in ThMutexDestroy" and hung
# every later COM call. connectorTM, omidTypeDesc and dataTypeDesc are set explicitly where used.
TYPEID_TAGS = ("typeDesc", "histTD")


def typeid_elements(el):
    return [e for e in el.iter() if e.tag in TYPEID_TAGS and e.text and TYPEID_RE.fullmatch(e.text.strip())]


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
