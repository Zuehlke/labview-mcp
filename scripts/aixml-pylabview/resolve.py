# -*- coding: utf-8 -*-
"""Resolve an AIXML <Call target=...> to the callee's file and to the link a caller writes.

Spellings, each read off a link record LabVIEW wrote itself:

  "X.vi"                     beside the caller, or below/above it -> TpVal 1, relative to the
                                                                     CALLER FILE: ["", "X.vi"]
  "X.vi"                     a VI in vi.lib/user.lib/...          -> TpVal 0, ["<vilib>", ..., "X.vi"]
  "Lib.lvlib:X.vi"           a library member                     -> qualified name [Lib.lvlib, X.vi];
                                                                     the diagram record's VILSPathRef
                                                                     names the .lvlib
  "Lib.lvlib:C.lvclass:M.vi" a class member (optionally in a library) -> the same, with the class

A path inside the installation is written relative to a SYMBOLIC ANCHOR. An LVAddons tree
(C:\\Program Files\\NI\\LVAddons\\<addon>\\<version>\\resource\\...) maps onto the same anchor
as the installation's own folder: NI's Is discuss enabled.vi links
<resource>\\plugins\\lv_are_nigel_features_enabled.vi, which lives under LVAddons\\lvai.
Anything else - the examples tree, project\\, the user's own code - is linked RELATIVE to the
caller file, one empty component per level up (["", "", "X.vi"] from a subfolder).

In LabVIEW 2026 almost every .llb is a DIRECTORY (323 of 344 under vi.lib), so its VIs are
ordinary files and the .llb is just one more path component.

The rest are CONTAINER FILES - every DAQmx .llb, VI Tester, JKI EasyXML, OpenG buttons.llb - and
LabVIEW links a member of one exactly like a member of an .llb folder, a path running THROUGH the
.llb: <vilib>\\DAQmx\\configure\\task.llb\\DAQmx Clear Task.vi (NI's Analog Input - Filtering.vi,
read 2026-10-15). So a member keeps that VIRTUAL path everywhere a path is written, and only
materialize() turns it into a real file: pylabview extracts the container once (--keep-names
writes each member under its own name, 0.3 s for task.llb), cached on disk by the .llb's path,
size and time. A container is opened only on a miss, and only when its bytes carry the name.

A library's member URL is either SYMBOLIC (/<vilib>/Utility/AngleManip/x.vi, how installed
libraries write it) or RELATIVE TO THE .lvlib FILE TREATED AS A DIRECTORY (../x.vi is the
library's own folder) - the same convention as a .lvproj, measured on DQMH New Event.lvlib.
A library stored INSIDE an .llb file (VI Tester API.lvlib) follows the same rule from its
virtual path.
"""
import glob
import hashlib
import os
import time
import xml.etree.ElementTree as ET

ANCHORS = (("<vilib>", "vi.lib"), ("<userlib>", "user.lib"), ("<instrlib>", "instr.lib"),
           ("<resource>", "resource"),
           # the help folder: the 3D Graph's properties dialog calls <helpdir>\_LaunchHelpUrl.vi
           ("<helpdir>", "help"))
# an XControl is a library too: its members call each other by `X.xctl:Member.vi`, as NI's
# exporter and the reader write them (the XControl sweep, 2026-10-22)
OWNER_EXT = (".lvlib", ".lvclass", ".xctl")
# the item types an .xctl lists its VIs under, beside a library's plain "VI"
XCTL_VI_TYPES = ("VI", "Method VI", "Property VI", "Ability VI")


def labview_root():
    env = os.environ.get("LABVIEW_ROOT")
    if env and os.path.isdir(env):
        return env
    for base in (r"C:\Program Files (x86)\National Instruments", r"C:\Program Files\National Instruments"):
        for d in sorted(glob.glob(os.path.join(base, "LabVIEW 20*")), reverse=True):
            if os.path.exists(os.path.join(d, "LabVIEW.exe")):
                return d
    return None


def llb_cache_root():
    """Where extracted .llb containers are kept: beside the server's other caches."""
    base = os.environ.get("LABVIEWMCP_CACHE_DIR") or os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache")
    return os.path.join(base, "llb")


def llb_split(path):
    """(container .llb FILE, member name) when `path` runs through one, else None. Only the
    first container counts: an .llb inside an .llb does not occur."""
    full = os.path.abspath(path)
    parts = full.split(os.sep)
    for i in range(1, len(parts) - 1):
        if parts[i].lower().endswith(".llb"):
            container = os.sep.join(parts[:i + 1])
            if os.path.isfile(container):
                return container, os.sep.join(parts[i + 1:])
    return None


_LLB_MEMBERS = {}


def llb_members(container):
    """{member name lower: extracted file} of an .llb container, extracting it on first use.
    {} when pylabview cannot read it."""
    key = os.path.normcase(os.path.abspath(container))
    if key in _LLB_MEMBERS:
        return _LLB_MEMBERS[key]
    st = os.stat(container)
    tag = hashlib.sha1(key.encode("utf-8")).hexdigest()[:12]
    out_dir = os.path.join(llb_cache_root(), "%s_%d_%d" % (tag, st.st_size, int(st.st_mtime)))
    main = os.path.join(out_dir, "llb.xml")
    members = {}
    if not os.path.exists(main):
        import lvheap
        os.makedirs(out_dir, exist_ok=True)
        try:
            lvheap.run_pylabview(["-x", "--keep-names", "-i", container, "-m", main], timeout=120)
        except RuntimeError:
            pass
    if os.path.exists(main):
        try:
            root = ET.parse(main).getroot()
        except ET.ParseError:
            root = None
        # the members are the resource-file blocks: UCRF uncompressed, CPRF / ZCRF compressed
        for block in (root if root is not None else []):
            if block.tag not in ("UCRF", "CPRF", "ZCRF"):
                continue
            for sec in block.findall("Section"):
                name, f = sec.get("Name"), sec.get("File")
                if name and f and os.path.exists(os.path.join(out_dir, f)):
                    members[name.lower()] = os.path.join(out_dir, f)
    _LLB_MEMBERS[key] = members
    return members


def materialize(path):
    """A path -> a real file holding it: itself, or the extracted member of an .llb container.
    None when neither exists."""
    if os.path.exists(path):
        return path
    split = llb_split(path)
    if split is None:
        return None
    return llb_members(split[0]).get(split[1].lower())


def exists(path):
    return materialize(path) is not None


def _carries(container, name):
    """Does the container's raw data name `name`? A cheap filter before extracting it."""
    try:
        with open(container, "rb") as fh:
            data = fh.read()
    except OSError:
        return False
    try:
        return name.encode("mac_roman") in data
    except UnicodeEncodeError:
        return name.encode("utf-8", "ignore") in data


def relative_ref(from_file, to_path):
    """A LabVIEW relative path: relative to the caller FILE, "" for every level up."""
    rel = os.path.relpath(os.path.abspath(to_path), os.path.abspath(from_file))
    return ["" if c == ".." else c for c in rel.split(os.sep)]


_INSTALL_INDEX = {}            # roots -> (built at, files, owners, llbs)
INSTALL_INDEX_SECONDS = 600


class Resolver:
    def __init__(self, lv_root=None):
        self.lv_root = lv_root or labview_root()
        self.roots = []      # (anchor, directory), installation first
        if self.lv_root:
            for anchor, sub in ANCHORS:
                d = os.path.join(self.lv_root, sub)
                if os.path.isdir(d):
                    self.roots.append((anchor, d))
        addons = r"C:\Program Files\NI\LVAddons"
        for d in sorted(glob.glob(os.path.join(addons, "*", "*"))):
            for anchor, sub in ANCHORS:
                if os.path.isdir(os.path.join(d, sub)):
                    self.roots.append((anchor, os.path.join(d, sub)))
        self._files = None
        self._owners = None
        self._llbs = None    # .llb CONTAINER files, installation first
        self._near = {}

    # ---- indexes, built on first use ------------------------------------------------------
    def _index(self):
        if self._files is not None:
            return
        # the INSTALLATION's index is shared by every Resolver of the process: walking it is
        # 4 000 directories and 0.6 s, which every compose paid for a library-qualified Call.
        # In the server's resident worker (worker.py) that is once per ten minutes instead - an
        # installation changes when a toolkit is installed, not between two composes
        key = tuple(self.roots)
        got = _INSTALL_INDEX.get(key)
        if got is None or time.monotonic() - got[0] > INSTALL_INDEX_SECONDS:
            files, owners, llbs = {}, {}, []
            for anchor, d in self.roots:
                self._walk(d, files, owners, llbs=llbs)
            got = (time.monotonic(), files, owners, llbs)
            _INSTALL_INDEX[key] = got
        self._files, self._owners, self._llbs = got[1], got[2], got[3]

    @staticmethod
    def _walk(d, files, owners, depth=None, llbs=None):
        base = d.rstrip(os.sep).count(os.sep)
        for dirpath, dirnames, filenames in os.walk(d):
            if depth is not None and dirpath.count(os.sep) - base >= depth:
                dirnames[:] = []
            for f in filenames:
                low = f.lower()
                if low.endswith((".vi", ".vim", ".vit")):
                    files.setdefault(low, []).append(os.path.join(dirpath, f))
                elif low.endswith(OWNER_EXT):
                    owners.setdefault(low, []).append(os.path.join(dirpath, f))
                elif low.endswith(".llb") and llbs is not None:
                    llbs.append(os.path.join(dirpath, f))

    def _in_llbs(self, name, llbs):
        """Virtual paths (<container>\\<member>) of every .llb container holding `name`."""
        hits = []
        for container in llbs:
            if not _carries(container, name):
                continue
            if name.lower() in llb_members(container):
                hits.append(os.path.join(container, name))
        return hits

    def _near_index(self, caller_dir):
        """VIs and owners in the caller's folder, its subfolders and two levels above - where an
        example or a project keeps its own subVIs, linked relatively."""
        caller_dir = os.path.abspath(caller_dir)
        if caller_dir not in self._near:
            files, owners, llbs = {}, {}, []
            # the caller's own folder three levels deep, its parent two, the grandparent one -
            # bounded, because a caller in a big tree (C:	emp) would otherwise index all of it
            for up, depth in ((0, 3), (1, 2), (2, 1)):
                d = caller_dir
                for _ in range(up):
                    d = os.path.dirname(d)
                self._walk(d, files, owners, depth=depth, llbs=llbs)
            self._near[caller_dir] = (files, owners, llbs)
        return self._near[caller_dir]

    def symbolic(self, path):
        """(anchor, [components]) for a path inside one of the roots, or None."""
        full = os.path.normcase(os.path.abspath(path))
        best = None
        for anchor, d in self.roots:
            root = os.path.normcase(os.path.abspath(d)) + os.sep
            if full.startswith(root) and (best is None or len(root) > len(best[2])):
                best = (anchor, d, root)
        if best is None:
            return None
        rel = os.path.relpath(os.path.abspath(path), best[1])
        return best[0], rel.split(os.sep)

    def class_parents(self, cls_file, depth=8):
        """The ancestor CLASS files of a .lvclass, nearest first - its `Parent` items' URLs, symbolic
        (/<vilib>/...) or relative to the class file treated as a directory; interfaces skipped."""
        out, cur = [], cls_file
        for _ in range(depth):
            try:
                root = ET.parse(materialize(cur) or cur).getroot()
            except (OSError, ET.ParseError):
                break
            nxt = None
            for item in root.iter("Item"):
                if item.get("Type") != "Parent" or not item.get("URL"):
                    continue
                url = item.get("URL")
                if url.startswith("/<"):
                    anchor, _, rest = url[1:].partition("/")
                    cand = next((os.path.normpath(os.path.join(d, *rest.split("/"))) for a, d in self.roots
                                 if a == anchor), None)
                else:
                    cand = os.path.normpath(os.path.join(cur, *url.split("/")))
                if not cand or not exists(cand):
                    continue
                try:
                    iface = any(p.get("Name") == "NI.LVClass.IsInterface" and (p.text or "").strip().lower() == "true"
                                for p in ET.parse(materialize(cand) or cand).getroot().iter("Property"))
                except (OSError, ET.ParseError):
                    iface = False
                if not iface:
                    nxt = cand
                    break
            if nxt is None:
                break
            out.append(nxt)
            cur = nxt
        return out

    def linked_file(self, tp, comps, caller_files):
        """The file a link record's path ref names: TpVal 0 from one of the roots (`<vilib>`,
        `<userlib>` ...), TpVal 1 relative to the caller FILE treated as a directory - each empty
        component one level up. The first candidate caller that yields an existing file wins."""
        if not comps:
            return None
        if str(tp) == "0":
            for anchor, d in self.roots:
                if comps[0] == anchor:
                    p = os.path.join(d, *comps[1:])
                    return p if exists(p) else None
            return None
        for cf in caller_files:
            if not cf:
                continue
            p = os.path.abspath(cf)
            for c in comps:
                p = os.path.dirname(p) if c == "" else os.path.join(p, c)
            if exists(p):
                return p
        return None

    def ref(self, path, caller_file):
        s = self.symbolic(path)
        if s:
            return ("0", [s[0]] + s[1])
        return ("1", relative_ref(caller_file, path))

    def member_item(self, owner, name, kind=None):
        """The <Item> an owner lists under `name`, at any folder depth, or None."""
        real = materialize(owner)
        if real is None:
            return None
        for item in ET.parse(real).getroot().iter("Item"):
            if item.get("Name") == name and item.get("URL") and (not kind or item.get("Type") in kind):
                return item
        return None

    def member(self, owner, name, kind=("VI",)):
        """The file an owner (.lvlib, .lvclass or .xctl) lists under `name`, at any folder depth."""
        real = materialize(owner)
        if real is None:
            return None
        root = ET.parse(real).getroot()
        for item in root.iter("Item"):
            if item.get("Name") != name or not item.get("URL"):
                continue
            if kind and item.get("Type") not in kind:
                continue
            url = item.get("URL")
            if url.startswith("/<"):
                anchor, _, rest = url[1:].partition("/")
                for a, d in self.roots:
                    p = os.path.normpath(os.path.join(d, *rest.split("/")))
                    if a == anchor and exists(p):
                        return p
                return None
            # relative to the OWNER FILE treated as a directory: ../x.vi is its own folder
            return os.path.normpath(os.path.join(owner, *url.split("/")))
        return None

    def _owner_files(self, name, caller_dirs):
        """Library/class files of that name near ANY search folder first, then the installation's."""
        self._index()
        near = []
        for d in (caller_dirs or []):
            near += self._near_index(d)[1].get(name.lower(), [])
        found = near + self._owners.get(name.lower(), [])
        if found:
            return found
        # a library stored INSIDE an .llb container (VI Tester API.lvlib in VI Tester API.llb)
        llbs = [c for d in (caller_dirs or []) for c in self._near_index(d)[2]] + self._llbs
        return self._in_llbs(name, llbs)

    # ---- the one call -----------------------------------------------------------------------
    def resolve(self, target, caller_dirs, caller_file=None):
        """-> dict(path, qual=[...], path_ref=(tpval, [..]), lib_ref=(tpval, [..]) or None, kind)
        caller_file decides relative links; it defaults to a file in the first caller dir."""
        caller_dir = caller_dirs[0] if caller_dirs else None
        caller_file = caller_file or (os.path.join(caller_dir, "caller.vi") if caller_dir else None)
        if ":" in target:
            parts = target.split(":")
            owners, vi_name = parts[:-1], parts[-1]
            if not all(p.lower().endswith(OWNER_EXT) for p in owners):
                return {"error": "unrecognised qualified name %r" % target}
            # walk the chain: Lib.lvlib -> Class.lvclass -> member, or just the last owner
            candidates = self._owner_files(owners[-1], caller_dirs)
            if not candidates:
                return {"error": "no %s in the installation or near the caller" % owners[-1]}
            for owner in candidates:
                path = self.member(owner, vi_name,
                                   XCTL_VI_TYPES if owner.lower().endswith(".xctl") else ("VI",))
                if path and exists(path):
                    kind = "class member" if owners[-1].lower().endswith(".lvclass") else "library member"
                    return {"path": path, "qual": owners + [vi_name], "kind": kind,
                            "path_ref": self.ref(path, caller_file),
                            "lib_ref": self.ref(owner, caller_file), "owner": owner}
            # an INHERITED method: a dynamic-dispatch call through a child class names the child
            # (`XY Plot Matrix.lvclass:Write Active Plot.vi`) where only its parent, 2D Plot.lvclass,
            # lists the member - XY Plot Matrix's Active Plot Write.vi (the XControl sweep, 2026-10-23)
            if owners[-1].lower().endswith(".lvclass"):
                for owner in candidates:
                    for parent in self.class_parents(owner):
                        path = self.member(parent, vi_name)
                        if path and exists(path):
                            return {"path": path, "qual": [os.path.basename(parent), vi_name], "kind": "class member",
                                    "path_ref": self.ref(path, caller_file),
                                    "lib_ref": self.ref(parent, caller_file), "owner": parent, "inherited": True}
            return {"error": "%s lists no member %r" % (owners[-1], vi_name)}
        for d in caller_dirs:
            p = os.path.join(d, target)
            if os.path.exists(p):
                # a sibling of the caller FILE is linked as ("", name); one found in another
                # search folder needs a real relative path, or LabVIEW looks beside the caller
                # and the VI is broken (a static reference to NI's "Test - Selection List
                # Target.vi", composed into another folder)
                same = caller_file and os.path.normcase(os.path.dirname(os.path.abspath(caller_file))) == os.path.normcase(os.path.abspath(d))
                ref = ("1", ["", target]) if same or not caller_file else ("1", relative_ref(caller_file, p))
                return {"path": p, "qual": [target], "path_ref": ref,
                        "lib_ref": None, "kind": "sibling"}
        # NEAR THE CALLER BEFORE THE INSTALLATION: a project's own `Set Control Value.vi` in
        # SubVIs\ was shadowed by NI's namesake under resource\importtools, whose pane has no
        # `controls` - found by the ATM build (2026-10-11). A bare name in a project means the
        # project's VI, as LabVIEW itself looks beside the caller first when it relinks.
        for d in (caller_dirs or []):
            # every search folder, not only the first: a VI composed elsewhere from an NI
            # export finds its callees beside the ORIGINAL (bisect_vi.py --near)
            near = self._near_index(d)[0].get(target.lower(), [])
            if near:
                return {"path": near[0], "qual": [target], "path_ref": ("1", relative_ref(caller_file, near[0])),
                        "lib_ref": None, "kind": "near the caller", "ambiguous": len(near) > 1}
        self._index()
        hits = self._files.get(target.lower(), [])
        for p in hits:   # the installation's own folders first, then the add-ons
            s = self.symbolic(p)
            if s:
                return {"path": p, "qual": [target], "path_ref": ("0", [s[0]] + s[1]), "lib_ref": None,
                        "kind": "bare name", "ambiguous": len(hits) > 1}
        # last, the .llb CONTAINERS - near the caller first, then the installation's; opened only
        # now, and only those whose bytes carry the name, because extracting one costs a pylabview run
        for d in (caller_dirs or []):
            near = self._in_llbs(target, self._near_index(d)[2])
            if near:
                return {"path": near[0], "qual": [target], "path_ref": ("1", relative_ref(caller_file, near[0])),
                        "lib_ref": None, "kind": "near the caller", "ambiguous": len(near) > 1}
        hits = self._in_llbs(target, self._llbs)
        for p in hits:
            s = self.symbolic(p)
            if s:
                return {"path": p, "qual": [target], "path_ref": ("0", [s[0]] + s[1]), "lib_ref": None,
                        "kind": "bare name", "llb": True, "ambiguous": len(hits) > 1}
        return {"error": "%r is neither near the caller nor in the installation" % target}
