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
import struct
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

# scripts\aixml-pylabview since 2026-10-12 (experiments\aixml-without-grpc before), so REPO is the
# source checkout - or, shipped, the folder holding the exe, scripts\, docs\ and pylabview\
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
PARTS_VARIABLE = "LABVIEWMCP_AIXML_PARTS"


def _pylabview_cmd():
    """The launcher of the bundle this interpreter belongs to - the server runs the composer with
    the bundled python.exe - else the shipped layout's pylabview\\ or a checkout's runtime."""
    for d in (os.path.dirname(sys.executable), os.path.join(REPO, "pylabview"),
              os.path.join(REPO, "tools", "pylabview", "runtime")):
        if os.path.exists(os.path.join(d, "pylabview.cmd")):
            return os.path.join(d, "pylabview.cmd")
    return os.path.join(REPO, "tools", "pylabview", "runtime", "pylabview.cmd")


PYLABVIEW_CMD = _pylabview_cmd()


_LABVIEW_ROOT = re.compile(r"[A-Za-z]:[\\/](?:[^\\/]*[\\/])*?National Instruments[\\/]LabVIEW [^\\/]+[\\/]")
_ABSOLUTE_FILE = re.compile(r"[A-Za-z]:[\\/](?:[^\\/\"<>]*[\\/])*?([^\\/\"<>]+?\.(?:vim?|vit|ctl|xml|llb|lvclass|lvlib))\b",
                            re.IGNORECASE)


def portable_source(text):
    """A part's PROVENANCE as it ships: the LabVIEW installation as `<labview>\\`, any other
    absolute path cut to its file name - a donor in %TEMP% or a scratch folder is nobody else's
    path, and the parts are published with the code since 2026-10-12. Only provenance goes
    through this; a path that is DATA in a part is never touched."""
    text = _LABVIEW_ROOT.sub(lambda m: "<labview>\\", text)
    return _ABSOLUTE_FILE.sub(lambda m: m.group(1), text)


def own_parts():
    """The folder of the composer's OWN tables - hotpoints, event codes, property and refnum
    names, learned from what LabVIEW does - as opposed to the donor parts `--parts` names, which
    may be harvested elsewhere and carry none of them. They ship beside the code, in parts\\, since
    2026-10-12 (the user's decision: the release carries everything the backend needs)."""
    for d in (os.path.join(HERE, "parts"), os.environ.get(PARTS_VARIABLE, "")):
        if d and os.path.isdir(d):
            return d
    return os.path.join(HERE, "parts")

TYPEID_RE = re.compile(r"TypeID\((\d+)\)")

# The five bytes Windows-1252 leaves undefined; Windows maps each to the C1 control of the same
# number, and so does NI's AIXML export.
_CP1252_HOLES = {0x81, 0x8D, 0x8F, 0x90, 0x9D}


def value_text(b):
    """A string VALUE's bytes - a constant, a default, a case item - as NI's export writes them:
    Windows-1252, the five undefined bytes as U+0081 ... U+009D. Measured 2026-10-15 over the cached
    NI exports: 0xD1 is `Ñ` and 0xFF `ÿ` there (MacRoman, which this code used until then, gives
    `—` and `ˇ`), cp1252's own characters (`€`, `—`, ...) occur, and so do U+0081/8D/8F/90/9D.
    It is the station's code page as LabVIEW stores strings; this station's is 1252."""
    try:
        return b.decode("cp1252")
    except UnicodeDecodeError:
        return "".join(chr(x) if x in _CP1252_HOLES else bytes([x]).decode("cp1252") for x in b)


def value_bytes(s):
    """The inverse of value_text, for the composer."""
    try:
        return s.encode("cp1252")
    except UnicodeEncodeError:
        return b"".join(bytes([ord(c)]) if ord(c) in _CP1252_HOLES else c.encode("cp1252") for c in s)


def lv_text(s):
    """Authored TEXT as pylabview has to be handed it so that LabVIEW on Windows shows `s`.
    pylabview encodes heap text as MacRoman (the skeleton's Encoding), LabVIEW reads the bytes in
    the station's code page, 1252 - so `s` goes to CP1252 bytes first and those bytes are handed
    over as the MacRoman characters that encode to them. Until 2026-11-19 the text went in as it
    was: a ring item `0503 0000h – Toggle bit` showed as `0503 0000h Ð Toggle bit` (the en dash is
    MacRoman 0xD0, CP1252 `Ð`), `Größe 5 °C` as `Grš§e 5 ¡C` - measured by NI's own export of a
    composed ring, a user's report. The reader undoes the same mapping (vi2aixml.heap_text). A
    character CP1252 lacks keeps its compatibility form (NFKD), else becomes `?`. Only for text
    from the document: a donor part's quoted string is already in this form (and may be binary)."""
    if not s:
        return s
    try:
        return value_bytes(s).decode("mac_roman")
    except UnicodeEncodeError:
        pass
    import unicodedata
    out = []
    for ch in s:
        alt = ch
        for cand in (ch, {"−": "-"}.get(ch) or unicodedata.normalize("NFKD", ch), "?"):
            try:
                value_bytes(cand)
                alt = cand
                break
            except UnicodeEncodeError:
                continue
        out.append(alt)
    return value_bytes("".join(out)).decode("mac_roman")


def pylv_to_text(s):
    """The inverse of lv_text: text pylabview rendered as MacRoman, read as the CP1252 LabVIEW on
    Windows wrote. A string MacRoman cannot hold is returned as it is."""
    if not s:
        return s
    try:
        return value_text(s.encode("mac_roman"))
    except UnicodeEncodeError:
        return s


def _pylabview_argv():
    """How to start pylabview. The bundle's own python.exe on readRSRC.py, exactly what
    pylabview.cmd runs - NOT through `cmd /c`: cmd.exe re-parses the command line, so a path
    holding `&` (`Controls\\Path&Icon.ctl` in the LabVIEW Icon Editor, 2026-10-15) was split
    there into two commands. Only a layout without that pair falls back to the launcher."""
    here = os.path.dirname(PYLABVIEW_CMD)
    python = os.path.join(here, "python.exe")
    script = os.path.join(here, "app", "pylabview", "readRSRC.py")
    if os.path.exists(python) and os.path.exists(script):
        return [python, script]
    return ["cmd", "/c", PYLABVIEW_CMD]


def run_pylabview(args, timeout=120):
    # subprocess.run's own timeout kills only cmd.exe: the python grandchild keeps the pipes open
    # and the second communicate() then waits for ever - a coverage run hung 7 minutes on one
    # vi.lib VI that way (2026-10-10). Kill the whole tree instead.
    p = subprocess.Popen(_pylabview_argv() + args, stdin=subprocess.DEVNULL,
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        out, err = p.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
        out, err = p.communicate()
        raise RuntimeError("pylabview timed out after %d s: %s" % (timeout, " ".join(args)))
    return p.returncode, out + err


def _failure_text(out, limit):
    """pylabview's output for an error message: its own SyntaxWarnings dropped (LVheap.py:1757
    filled the whole tail of 6 of 11 failures in the vi.lib sweep, 2026-11-08, hiding the cause)
    and the LAST `Error:` line put first, so a truncated message still names it."""
    lines = out.splitlines()
    keep, skip = [], False
    for ln in lines:
        if "SyntaxWarning" in ln:
            skip = True          # the warning and the source line it quotes after it
            continue
        if skip and ln.startswith((" ", "\t")):
            continue
        skip = False
        keep.append(ln)
    errs = [ln for ln in keep if ln.startswith("Error:") or re.match(r"\w+Error: ", ln)]
    text = "\n".join(keep)[-limit:]
    return (errs[-1] + " | " + text) if errs else text


def extract(vi_path, out_dir, timeout=120):
    """VI -> bundle. Returns the main xml path."""
    os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(vi_path))[0]
    main = os.path.join(out_dir, base + ".xml")
    code, out = run_pylabview(["-x", "-i", vi_path, "-m", main], timeout=timeout)
    if code != 0 or not os.path.exists(main):
        raise RuntimeError("extract failed for %s: %s" % (vi_path, _failure_text(out, 2000)))
    return main


def rebuild(main_xml, vi_path):
    code, out = run_pylabview(["-c", "-m", main_xml, "-i", vi_path])
    if code != 0 or not os.path.exists(vi_path):
        raise RuntimeError("rebuild failed: %s" % _failure_text(out, 3000))
    return out


class Bundle:
    """Read-only view of an extracted bundle: main tree, both heaps, the type chain."""

    def __init__(self, main_xml, collections=True):
        self.main_xml = main_xml
        self.dir = os.path.dirname(main_xml)
        self.main = ET.parse(main_xml).getroot()
        self.fp = self._heap("FPHb")
        self.bd = self._heap("BDHb")
        vctp = self.main.find("VCTP/Section")
        self.flat = [td for td in vctp if td.tag == "TypeDesc"]
        for td in self.flat:
            # labels and enum items as LabVIEW wrote them (CP1252), not as pylabview rendered the
            # bytes (MacRoman) - the inverse of lv_text, which vctp_section applies when a type
            # goes back out. Before the collections, which decode their raw label themselves.
            for e in td.iter():
                if e.get("Label"):
                    e.set("Label", pylv_to_text(e.get("Label")))
                if e.tag == "EnumLabel" and e.text:
                    e.text = pylv_to_text(e.text)
        if collections:
            # the reader's view AND the composer's since issue #140: TypeTable writes a Map or Set
            # back as its raw descriptor with the ids of the VI being composed. Until then the
            # composer copied a callee's map as it stood - a sidecar File of the CALLEE's bundle -
            # and the rebuild died with FileNotFoundError on <callee>_VCTP_td*.bin
            self._collections()
        for td in self.flat:
            # a type still RAW names the file it came from, so a composer that cannot carry it
            # into another VI says WHICH callee (issue #140)
            if td.get("Format") == "bin" and td.get("File"):
                td.set("_origin", os.path.basename(main_xml)[:-4] + ".vi")
        self.toplevel = {int(t.get("Index")): int(t.get("FlatTypeID"))
                         for t in vctp.find("TopLevel")}
        dthp = self.main.find("DTHP/Section/TypeDescSlice")
        self.index_shift = int(dthp.get("IndexShift")) if dthp is not None else 0

    def _collections(self):
        """SET and MAP types (0x73, 0x74), which pylabview keeps raw (patch ref-family-non-refnum-raw):
        their bytes are the element's index into this flat list - a map a count and then the key's
        and the value's - and a Pascal label. Rewritten here as Type="Set"/"Map" with TypeID
        children, so tree() inlines them like any other container; measured on NI's Set and Map
        Collection examples, whose export writes set{string} and map{string.word,uint64.count}
        where the reader said ?115 and ?116 (2026-11-06)."""
        for td in self.flat:
            kind = {"115": "Set", "116": "Map"}.get(td.get("Type"))
            if kind is None or not td.get("File"):
                continue
            try:
                b = open(os.path.join(self.dir, td.get("File")), "rb").read()
            except OSError:
                continue
            if kind == "Set" and len(b) >= 2:
                idx, pos = [int.from_bytes(b[0:2], "big")], 2
            elif kind == "Map" and len(b) >= 6:
                idx, pos = [int.from_bytes(b[2:4], "big"), int.from_bytes(b[4:6], "big")], 6
            else:
                continue
            if any(i >= len(self.flat) for i in idx):
                continue
            td.set("Type", kind)
            td.set("Format", "inline")
            if td.get("HasLabel") == "1" and pos < len(b):
                n = b[pos]
                td.set("Label", b[pos + 1:pos + 1 + n].decode("cp1252", "replace"))
            for i in idx:
                ET.SubElement(td, "TypeDesc", {"TypeID": str(i)})

    def _heap(self, block):
        sec = self.main.find(block + "/Section")
        if sec is None or sec.get("File") is None:
            return None
        return ET.parse(os.path.join(self.dir, sec.get("File"))).getroot()

    def flat_of_heap(self, heap_tid):
        return self.toplevel[self.index_shift + heap_tid - 1]

    def flat_of_consolidated(self, cons):
        return self.toplevel[cons]

    # The READER's hook for linked typedefs: a callable taking a flat TypeDef element and returning
    # the TypeDef as its .ctl on disk holds it now (fully inlined), or None to keep the copy this
    # file embeds. None here - the composer copies a callee's types as they stand.
    typedef_resolver = None

    def tree(self, flat_id, snapshot=False):
        """A flat TypeDesc with every child reference inlined, recursively. A reference past
        the end of the list (NI's Set Collection - Word Counting.vi: a set type) comes back as
        an Unknown type, which the reader reports, instead of ending the whole read.

        A linked TYPEDEF comes back as its .ctl defines it when a typedef_resolver is set, unless
        `snapshot`: then as this file embeds it - the type its saved DATA is flattened against."""
        if not 0 <= flat_id < len(self.flat):
            return ET.Element("TypeDesc", {"Type": "Unknown", "Format": "inline", "ref.TypeID": str(flat_id)})
        return self._inline(self.flat[flat_id], snapshot)

    def _inline(self, src, snapshot=False):
        if not snapshot and self.typedef_resolver is not None and src.get("Type") == "TypeDef":
            disk = self.typedef_resolver(src, self._inline(src, True))
            if disk is not None:
                return disk
        out = ET.Element("TypeDesc", {k: v for k, v in src.attrib.items()})
        for child in src:
            if child.tag != "TypeDesc":
                out.append(copy.deepcopy(child))
                continue
            if child.get("TypeID") is not None:
                sub = self.tree(int(child.get("TypeID")), snapshot)
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
                out.append(self._inline(child, snapshot))
        return out

    def heap_type_tree(self, heap_tid, snapshot=False):
        return self.tree(self.flat_of_heap(heap_tid), snapshot)


class RawTypeError(Exception):
    """A type the composer cannot write into a VI's type table; aixml2vi reports it as UNSUPPORTED."""


# Map and Set, as lvheap.Bundle(collections=True) names them, -> the raw type code VCTP holds
COLLECTION_CODES = {"Map": "116", "Set": "115"}


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
        if tree.get("Type") in COLLECTION_CODES:
            return self._collection_form(tree)
        if tree.get("Format") == "bin" and tree.get("File"):
            # a type pylabview keeps RAW, copied from a callee's bundle: its bytes name flat ids of
            # THAT file and its sidecar is not in this one - written as it stood, the rebuild died
            # with FileNotFoundError on <callee>_VCTP_td*.bin (issue #140)
            raise RawTypeError("type %s%s%s is stored raw by pylabview and cannot be carried into another VI"
                               % (tree.get("Type"), " %r" % tree.get("Label") if tree.get("Label") else "",
                                  " (from %s)" % tree.get("_origin") if tree.get("_origin") else ""))
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

    def _collection_form(self, tree):
        """A MAP or SET as VCTP holds it: pylabview cannot parse 0x74/0x73, so the descriptor is a
        raw sidecar - a map u16 2, then the key's and the value's FLAT ids, a set the element's -
        and, when labelled, a Pascal label padded to an even length. Measured on NI's Map and Set
        Collection examples (Word Counting: 0002 0004 0005 + "Map of word to occurrence count:" + 00).
        The bytes stay on the element as `_raw` until vctp_section writes them out."""
        kids = [c for c in tree if c.tag == "TypeDesc"]
        want = 2 if tree.get("Type") == "Map" else 1
        if len(kids) != want:
            raise RawTypeError("a %s type needs %d element type(s), found %d" % (tree.get("Type"), want, len(kids)))
        ids = [self.register(k) for k in kids]
        raw = (struct.pack(">H", 2) if want == 2 else b"") + b"".join(struct.pack(">H", i) for i in ids)
        el = ET.Element("TypeDesc", {"Type": COLLECTION_CODES[tree.get("Type")], "Format": "bin"})
        label = tree.get("Label")
        if label:
            b = label.encode("cp1252", "replace")[:255]
            raw += bytes([len(b)]) + b
            if len(raw) % 2:
                raw += b"\0"
            el.set("HasLabel", "1")
        el.set("_raw", raw.hex())
        return el

    def use(self, flat_id):
        """A new consolidated index pointing at flat_id; returns the consolidated index."""
        self.toplevel.append(flat_id)
        return len(self.toplevel)

    def vctp_section(self, out_dir=None, base=None):
        """VCTP's section. A raw type (a map or set) is written as its sidecar <base>_VCTP_tdNNNN.bin
        into `out_dir`, which a VI with one requires."""
        sec = ET.Element("Section", {"Index": "0", "Format": "inline"})
        for i, el in enumerate(self.flat):
            el = copy.deepcopy(el)
            raw = el.attrib.pop("_raw", None)
            if raw is not None:
                if out_dir is None:
                    raise RawTypeError("a map or set type needs the output folder to write its descriptor")
                fn = "%s_VCTP_td%04d_%04d.bin" % (base, i, i)
                with open(os.path.join(out_dir, fn), "wb") as f:
                    f.write(bytes.fromhex(raw))
                el.set("File", fn)
                # pylabview reads File before HasLabel - keep its own attribute order
                if el.attrib.pop("HasLabel", None):
                    el.set("HasLabel", "1")
            else:
                # a label or an enum item is authored text: pylabview writes it as MacRoman,
                # LabVIEW reads it in CP1252 (lv_text) - `Code Größe` was `Code Grš§e`
                # until 2026-11-19. A raw type carries its label as CP1252 bytes already.
                for e in el.iter():
                    if e.get("Label"):
                        e.set("Label", lv_text(e.get("Label")))
                    if e.tag == "EnumLabel" and e.text:
                        e.text = lv_text(e.text)
            sec.append(el)
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


def tag_refnum_class(tree):
    """The CLASS of a user-defined tag refnum (`NIDAQ`, `nisyscfg`, `Variable`) as a PART KEY
    carries it, or None. A `Refnum.UsrDefndTag` is class-specific inside - its tag ddo names the
    class's DLL and its STR section the typeClass - so one class's part cannot serve another, and
    the parts are filed by class beside the generic key (2026-10-27). A key's last segment maps
    `_` back to `.`, so a class holding `.` or a separator would not round-trip through the file
    name and keeps the generic key. ONE implementation: harvest_parts.py files the part with it
    and aixml2vi.py asks for it, and two copies of this rule would drift."""
    name = tree.get("TypeName") or ""
    return name if name and not any(c in name for c in "./\\") else None


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


# The block diagram window's view, in diagram coordinates: pBounds is the region the window
# shows when the VI opens. NI's examples put it round their code; a composed VI kept the
# skeleton's (243, 336, ...) and opened on an empty diagram (the user's screenshot, 2026-10-31).
VIEW_MARGIN = 20


def content_top_left(heap):
    """The top-left of what the top-level diagram shows: nodes, panel terminals, decorations."""
    root = heap.find("root")
    if root is None:
        return None
    tops, lefts = [], []

    def add(text):
        t, l, b, r = parse_bounds(text)
        if b > t or r > l:
            tops.append(t)
            lefts.append(l)

    nodes = root.find("nodeList")
    for node in (nodes if nodes is not None else []):
        if node.get("class") == "sRN":
            terms = node.find("termList")
            for term in (terms if terms is not None else []):
                if term.find("bounds") is not None:
                    add(term.findtext("bounds"))
        elif node.find("bounds") is not None:
            add(node.findtext("bounds"))
    z = root.find("zPlaneList")
    for deco in (z if z is not None else []):
        if deco.get("class") and deco.find("bounds") is not None:
            add(deco.findtext("bounds"))
    return (min(tops), min(lefts)) if tops else None


def view_start(content, size):
    """0, the user's wish, while the code starts within the first quarter of the window;
    otherwise where the code starts - so no code is cut off and none lies out of sight."""
    start = content - VIEW_MARGIN
    return 0 if 0 <= start <= size // 4 else start


def fit_window_view(heap):
    """Put the window's view (pBounds) over the diagram's code, its size kept. True if changed."""
    view = heap.find("pBounds")
    corner = content_top_left(heap)
    if view is None or corner is None:
        return False
    t, l, b, r = parse_bounds(view.text)
    top, left = view_start(corner[0], b - t), view_start(corner[1], r - l)
    text = fmt_bounds((top, left, top + b - t, left + r - l))
    if text == view.text:
        return False
    view.text = text
    return True
