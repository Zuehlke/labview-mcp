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
Only ONE version folder per add-on counts - the highest whose lvaddoninfo.json admits this
LabVIEW's version and bitness - and its Targets\\win32 or Targets\\win64 overlays too
(select_addons). An anchor has several roots, so a lookup tries each before it gives up.
WHICH locations is the server's to say (LabViewContext: the running LabVIEW, its -pref INI and
the LVAddons.* keys in it, issue #99): --labview-root and --addon-locations, through parse_args.
Left out, the newest installation and the default root.
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
import json
import os
import re
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


def default_addons_root():
    """%ProgramFiles%\\NI\\LVAddons - the 64-bit Program Files even for a 32-bit LabVIEW, as
    AddonTree.DefaultRoot on the server side. None when absent."""
    for var in ("ProgramW6432", "ProgramFiles", "ProgramFiles(x86)"):
        base = os.environ.get(var)
        if base and os.path.isdir(os.path.join(base, "NI", "LVAddons")):
            return os.path.join(base, "NI", "LVAddons")
    fallback = r"C:\Program Files\NI\LVAddons"
    return fallback if os.path.isdir(fallback) else None


_LV_IDENTITY = {}


def labview_identity(lv_root):
    """((major, minor), bits) of the LabVIEW in `lv_root`, read off LabVIEW.exe: the version from
    its VS_FIXEDFILEINFO (26.3.1f1 -> (26, 3)), the bitness from the PE machine field. A part that
    cannot be read is None, and the folder name stands in for the version as (yy, 99) - so an
    unreadable minor admits every add-on of that year rather than dropping one."""
    if not lv_root:
        return None, None
    key = os.path.normcase(os.path.abspath(lv_root))
    if key in _LV_IDENTITY:
        return _LV_IDENTITY[key]
    version, bits = None, None
    try:
        with open(os.path.join(lv_root, "LabVIEW.exe"), "rb") as fh:
            data = fh.read()
        if data[:2] == b"MZ":
            pe = int.from_bytes(data[0x3C:0x40], "little")
            if data[pe:pe + 4] == b"PE\0\0":
                bits = {0x014C: 32, 0x8664: 64}.get(int.from_bytes(data[pe + 4:pe + 6], "little"))
        # VS_FIXEDFILEINFO follows the "VS_VERSION_INFO" key of the version resource; the bare
        # signature also occurs earlier in LabVIEW.exe by chance (3071.13068 on 26.3.1)
        anchor = data.rfind("VS_VERSION_INFO".encode("utf-16-le"))
        sig = data.find(b"\xbd\x04\xef\xfe", anchor) if anchor >= 0 else -1
        if 0 <= sig - anchor < 64:
            ms = int.from_bytes(data[sig + 8:sig + 12], "little")   # dwFileVersionMS
            version = (ms >> 16, ms & 0xFFFF)
    except OSError:
        pass
    if version is None:
        m = re.search(r"LabVIEW (20)?(\d\d)\b", os.path.basename(os.path.normpath(lv_root)))
        if m:
            version = (int(m.group(2)), 99)
    if bits is None and "(x86)" in lv_root:
        bits = 32
    _LV_IDENTITY[key] = (version, bits)
    return version, bits


def _version_key(text):
    """'26.3' -> (26, 3), '1' -> (1,), 'v26.1' -> (26, 1); None when it is no version."""
    m = re.match(r"v?(\d+(?:\.\d+)*)$", (text or "").strip())
    return tuple(int(p) for p in m.group(1).split(".")) if m else None


def addon_info(version_dir):
    """The add-on's lvaddoninfo.json as a dict, or None when LabVIEW would not load the add-on.
    Measured 2026-10-26 on LabVIEW 26.3.1, a call into each probe add-on run through -pref: the
    add-on is IGNORED without the file, with text that is not JSON, with a UTF-8 BOM before it
    (PowerShell 5's -Encoding utf8 writes one; NI's files carry none) and without an AddonName.
    An AddonName other than the folder name is loaded."""
    try:
        with open(os.path.join(version_dir, "lvaddoninfo.json"), "rb") as fh:
            data = fh.read()
        if data.startswith(b"\xef\xbb\xbf"):
            return None
        info = json.loads(data.decode("utf-8"))
    except (OSError, ValueError):
        return None
    if not isinstance(info, dict) or not str(info.get("AddonName") or "").strip():
        return None
    return info


def addon_compatible(info, version, bits):
    """May LabVIEW `version` (major, minor) of `bits` load this add-on version? None - no usable
    lvaddoninfo.json - never; an absent version or bitness field does not exclude it."""
    if info is None:
        return False
    want = str(info.get("SupportedBitness") or "").strip()
    if want and bits and want != str(bits):
        return False
    minimum = _version_key(str(info.get("MinimumSupportedLVVersion") or ""))
    if minimum and version and minimum[:2] > tuple(version)[:2]:
        return False
    return True


def select_addons(addons_root, version=None, bits=None):
    """[(addon, version dir)]: per add-on folder, the HIGHEST version folder this LabVIEW may load.
    Measured 2026-10-26 on LabVIEW 26.3.1 (32-bit): with lvai\\1, lvai\\26.1 and lvai\\26.3
    installed, its log names files from lvai\\26.3 only (213 paths) and none from the other two,
    and nothing from a Targets\\win64. Until then every version of every add-on was searched,
    nidaqmx64 beside a 32-bit LabVIEW included."""
    out = []
    if not addons_root or not os.path.isdir(addons_root):
        return out
    for addon in sorted(os.listdir(addons_root)):
        adir = os.path.join(addons_root, addon)
        if not os.path.isdir(adir):
            continue
        best = None
        for ver in os.listdir(adir):
            vdir = os.path.join(adir, ver)
            key = _version_key(ver)
            if key is None or not os.path.isdir(vdir):
                continue
            if not addon_compatible(addon_info(vdir), version, bits):
                continue
            if best is None or key > best[0]:
                best = (key, vdir)
        if best:
            out.append((addon, best[1]))
    return out


def ordered_addons(locations, version=None, bits=None):
    """select_addons over every location, as ONE list ordered by add-on FOLDER name (case
    ignored) - the order LabVIEW picks a file present in two add-ons in. Measured 2026-10-26
    (experiments\\lvaddons): folder aaa with AddonName zzz beat folder mmm with AddonName bbb; and
    the SAME folder name in two locations is taken from the location the INI names LAST - `A;B`
    gave B's, `B;A` A's, a CustomLocation lost to an additional one. Whether the earlier one is
    then searched at all is not measured; it stays in the list, after."""
    chosen = [(i, a, v) for i, loc in enumerate(locations) for a, v in select_addons(loc, version, bits)]
    return [(a, v) for _i, a, v in sorted(chosen, key=lambda t: (t[1].lower(), -t[0]))]


def addon_trees(version_dir, bits=None):
    """The folders of one add-on version that overlay the installation: the version folder itself
    and Targets\\win32 or Targets\\win64 by bitness (lvai keeps its resource\\AI there) - both
    when the bitness is unknown."""
    trees = [version_dir]
    targets = os.path.join(version_dir, "Targets")
    for t in ({32: ("win32",), 64: ("win64",)}.get(bits) or ("win32", "win64")):
        if os.path.isdir(os.path.join(targets, t)):
            trees.append(os.path.join(targets, t))
    return trees


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


_INSTALL_INDEX = {}            # (roots, stamp) -> (built at, files, owners, llbs)
INSTALL_INDEX_SECONDS = 600

# The LabVIEW the server works for (issue #99) - set by configure() from the server's
# --labview-root / --addon-locations, per request, so the resident worker follows a switch.
# None: not configured, and the old discovery applies.
_CONFIGURED = {"lv_root": None, "locations": None}


def configure(lv_root=None, locations=None):
    """The installation and the LVAddons locations to resolve against. `locations` is a list, the
    base location first and the additional ones after it; [] means LabVIEW loads no add-ons;
    None falls back to the default root."""
    _CONFIGURED["lv_root"] = lv_root or None
    _CONFIGURED["locations"] = list(locations) if locations is not None else None


def parse_args(argv):
    """Remove --labview-root R and --addon-locations L1;L2 from argv and configure() with them.
    An EMPTY --addon-locations is "no add-ons"; leaving the flag out keeps the default root."""
    root, locations, rest, i = None, None, [], 0
    while i < len(argv):
        if argv[i] == "--labview-root" and i + 1 < len(argv):
            root, i = argv[i + 1], i + 2
        elif argv[i] == "--addon-locations" and i + 1 < len(argv):
            locations, i = [p for p in argv[i + 1].split(";") if p.strip()], i + 2
        else:
            rest.append(argv[i])
            i += 1
    configure(root, locations)
    return rest


def _stamp(locations, chosen):
    """The write times an add-on install moves: each location and each chosen version folder.
    A location outside %ProgramFiles% is a project's (an lvpm venv) and changes between two
    composes; the default root changes when something is installed."""
    out = []
    for p in list(locations) + [v for _a, v in chosen] + [os.path.join(v, "lvaddoninfo.json") for _a, v in chosen]:
        try:
            out.append(os.stat(p).st_mtime_ns)
        except OSError:
            out.append(0)
    return tuple(out)


class Resolver:
    def __init__(self, lv_root=None, addons_root=None):
        self.lv_root = lv_root or _CONFIGURED["lv_root"] or labview_root()
        self.lv_version, self.lv_bits = labview_identity(self.lv_root)
        self.roots = []      # (anchor, directory), installation first
        if self.lv_root:
            for anchor, sub in ANCHORS:
                d = os.path.join(self.lv_root, sub)
                if os.path.isdir(d):
                    self.roots.append((anchor, d))
        if addons_root is not None:
            self.locations = [addons_root]
        elif _CONFIGURED["locations"] is not None:
            self.locations = _CONFIGURED["locations"]
        else:
            self.locations = [d for d in (default_addons_root(),) if d]
        # the add-ons of EVERY location ordered by add-on name - measured 2026-10-26: for one
        # <vilib> file in two add-ons LabVIEW took the alphabetically first add-on, whichever
        # location held it and whatever order the INI listed them in, the CustomLocation included
        self.addons = ordered_addons(self.locations, self.lv_version, self.lv_bits)
        self.stamp = _stamp(self.locations, self.addons)
        for _addon, vdir in self.addons:
            for tree in addon_trees(vdir, self.lv_bits):
                for anchor, sub in ANCHORS:
                    if os.path.isdir(os.path.join(tree, sub)):
                        self.roots.append((anchor, os.path.join(tree, sub)))
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
        # the stamp in the key: an `lvpm install` into a venv is seen on the next compose, where the
        # ten-minute lifetime alone served the index from before it
        key = (tuple(self.roots), self.stamp)
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
                    # every root of that anchor, not the first: an add-on's class sits in the
                    # add-on's vi.lib, which comes after the installation's
                    anchor, _, rest = url[1:].partition("/")
                    cand = next((p for p in (os.path.normpath(os.path.join(d, *rest.split("/")))
                                             for a, d in self.roots if a == anchor) if exists(p)), None)
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
            # every root of that anchor: <vilib> is the installation's vi.lib AND each add-on's,
            # and the first one lacking the file used to end the search (issue #99)
            for anchor, d in self.roots:
                if comps[0] == anchor:
                    p = os.path.join(d, *comps[1:])
                    if exists(p):
                        return p
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
            # lists the member - XY Plot Matrix's Active Plot Write.vi (the XControl sweep, 2026-10-23).
            # The LINK still names the CHILD and its .lvclass, as LabVIEW writes it: NI's 2D Feather
            # facade links `2D Feather.lvclass:Set Range.vi` at 2D Feather.lvclass though only 2D
            # Plot has the VI - named after the parent, its calls were "Wire: Class conflict".
            # `path` is the parent's VI, which the composer reads the pane from.
            if owners[-1].lower().endswith(".lvclass"):
                for owner in candidates:
                    for parent in self.class_parents(owner):
                        path = self.member(parent, vi_name)
                        if path and exists(path):
                            return {"path": path, "qual": owners + [vi_name], "kind": "class member",
                                    "path_ref": self.ref(path, caller_file),
                                    "lib_ref": self.ref(owner, caller_file), "owner": owner, "inherited": True,
                                    "defined_in": parent}
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
