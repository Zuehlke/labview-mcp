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

A library's member URL is either SYMBOLIC (/<vilib>/Utility/AngleManip/x.vi, how installed
libraries write it) or RELATIVE TO THE .lvlib FILE TREATED AS A DIRECTORY (../x.vi is the
library's own folder) - the same convention as a .lvproj, measured on DQMH New Event.lvlib.
"""
import glob
import os
import xml.etree.ElementTree as ET

ANCHORS = (("<vilib>", "vi.lib"), ("<userlib>", "user.lib"), ("<instrlib>", "instr.lib"),
           ("<resource>", "resource"))
OWNER_EXT = (".lvlib", ".lvclass")


def labview_root():
    env = os.environ.get("LABVIEW_ROOT")
    if env and os.path.isdir(env):
        return env
    for base in (r"C:\Program Files (x86)\National Instruments", r"C:\Program Files\National Instruments"):
        for d in sorted(glob.glob(os.path.join(base, "LabVIEW 20*")), reverse=True):
            if os.path.exists(os.path.join(d, "LabVIEW.exe")):
                return d
    return None


def relative_ref(from_file, to_path):
    """A LabVIEW relative path: relative to the caller FILE, "" for every level up."""
    rel = os.path.relpath(os.path.abspath(to_path), os.path.abspath(from_file))
    return ["" if c == ".." else c for c in rel.split(os.sep)]


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
        self._near = {}

    # ---- indexes, built on first use ------------------------------------------------------
    def _index(self):
        if self._files is not None:
            return
        self._files, self._owners = {}, {}
        for anchor, d in self.roots:
            self._walk(d, self._files, self._owners)

    @staticmethod
    def _walk(d, files, owners, depth=None):
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

    def _near_index(self, caller_dir):
        """VIs and owners in the caller's folder, its subfolders and two levels above - where an
        example or a project keeps its own subVIs, linked relatively."""
        caller_dir = os.path.abspath(caller_dir)
        if caller_dir not in self._near:
            files, owners = {}, {}
            # the caller's own folder three levels deep, its parent two, the grandparent one -
            # bounded, because a caller in a big tree (C:	emp) would otherwise index all of it
            for up, depth in ((0, 3), (1, 2), (2, 1)):
                d = caller_dir
                for _ in range(up):
                    d = os.path.dirname(d)
                self._walk(d, files, owners, depth=depth)
            self._near[caller_dir] = (files, owners)
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

    def ref(self, path, caller_file):
        s = self.symbolic(path)
        if s:
            return ("0", [s[0]] + s[1])
        return ("1", relative_ref(caller_file, path))

    def member(self, owner, name, kind=("VI",)):
        """The file an owner (.lvlib or .lvclass) lists under `name`, at any folder depth."""
        root = ET.parse(owner).getroot()
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
                    if a == anchor and os.path.exists(p):
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
        return near + self._owners.get(name.lower(), [])

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
                path = self.member(owner, vi_name)
                if path and os.path.exists(path):
                    kind = "class member" if owners[-1].lower().endswith(".lvclass") else "library member"
                    return {"path": path, "qual": owners + [vi_name], "kind": kind,
                            "path_ref": self.ref(path, caller_file),
                            "lib_ref": self.ref(owner, caller_file), "owner": owner}
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
        self._index()
        hits = self._files.get(target.lower(), [])
        for p in hits:   # the installation's own folders first, then the add-ons
            s = self.symbolic(p)
            if s:
                return {"path": p, "qual": [target], "path_ref": ("0", [s[0]] + s[1]), "lib_ref": None,
                        "kind": "bare name", "ambiguous": len(hits) > 1}
        for d in (caller_dirs or []):
            # every search folder, not only the first: a VI composed elsewhere from an NI
            # export finds its callees beside the ORIGINAL (bisect_vi.py --near)
            near = self._near_index(d)[0].get(target.lower(), [])
            if near:
                return {"path": near[0], "qual": [target], "path_ref": ("1", relative_ref(caller_file, near[0])),
                        "lib_ref": None, "kind": "near the caller", "ambiguous": len(near) > 1}
        return {"error": "%r is neither near the caller nor in the installation" % target}
