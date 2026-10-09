# -*- coding: utf-8 -*-
"""Resolve an AIXML <Call target=...> to the callee's file and to the link a caller writes.

Three spellings, each read off a link record LabVIEW wrote itself:

  "X.vi"                    a sibling of the caller        -> TpVal 1, ["", "X.vi"]
  "X.vi"                    a VI in vi.lib/user.lib/...    -> TpVal 0, ["<vilib>", ..., "X.vi"]
  "Lib.lvlib:X.vi"          a library member               -> qualified name [Lib.lvlib, X.vi],
                                                              and the diagram record's VILSPathRef
                                                              names the .lvlib

A path inside the installation is written relative to a SYMBOLIC ANCHOR. An LVAddons tree
(C:\\Program Files\\NI\\LVAddons\\<addon>\\<version>\\resource\\...) maps onto the same anchor
as the installation's own folder: NI's Is discuss enabled.vi links
<resource>\\plugins\\lv_are_nigel_features_enabled.vi, which lives under LVAddons\\lvai.

In LabVIEW 2026 almost every .llb is a DIRECTORY (323 of 344 under vi.lib), so its VIs are
ordinary files and the .llb is just one more path component - as in
["<vilib>", "Utility", "error.llb", "Clear Errors.vi"].
"""
import glob
import os
import xml.etree.ElementTree as ET

ANCHORS = (("<vilib>", "vi.lib"), ("<userlib>", "user.lib"), ("<instrlib>", "instr.lib"),
           ("<resource>", "resource"))


def labview_root():
    env = os.environ.get("LABVIEW_ROOT")
    if env and os.path.isdir(env):
        return env
    for base in (r"C:\Program Files (x86)\National Instruments", r"C:\Program Files\National Instruments"):
        for d in sorted(glob.glob(os.path.join(base, "LabVIEW 20*")), reverse=True):
            if os.path.exists(os.path.join(d, "LabVIEW.exe")):
                return d
    return None


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
        self._libs = None

    # ---- indexes, built on first use ------------------------------------------------------
    def _index(self):
        if self._files is not None:
            return
        self._files, self._libs = {}, {}
        for anchor, d in self.roots:
            for dirpath, dirnames, filenames in os.walk(d):
                for f in filenames:
                    low = f.lower()
                    if low.endswith((".vi", ".vim", ".vit")):
                        self._files.setdefault(low, []).append(os.path.join(dirpath, f))
                    elif low.endswith(".lvlib"):
                        self._libs.setdefault(low, []).append(os.path.join(dirpath, f))

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

    def library_member(self, lvlib, vi_name):
        """The file of a VI a .lvlib lists, found by its Item name at any folder depth. An
        installed library writes the URL SYMBOLICALLY - /<vilib>/Utility/AngleManip/x.vi - and
        a user's library relative to itself; both are handled."""
        root = ET.parse(lvlib).getroot()
        for item in root.iter("Item"):
            if item.get("Name") != vi_name or item.get("Type") != "VI" or not item.get("URL"):
                continue
            url = item.get("URL")
            if url.startswith("/<"):
                anchor, _, rest = url[1:].partition("/")
                for a, d in self.roots:
                    p = os.path.normpath(os.path.join(d, *rest.split("/")))
                    if a == anchor and os.path.exists(p):
                        return p
                return None
            return os.path.normpath(os.path.join(os.path.dirname(lvlib), *url.split("/")))
        return None

    # ---- the one call -----------------------------------------------------------------------
    def resolve(self, target, caller_dirs):
        """-> dict(path, qual=[...], path_ref=(tpval, [..]), lib_ref=(tpval, [..]) or None)"""
        if ":" in target:
            parts = target.split(":")
            if len(parts) != 2 or not parts[0].lower().endswith(".lvlib"):
                return {"error": "only Library.lvlib:Member.vi is resolved, not %r" % target}
            lib_name, vi_name = parts
            self._index()
            for lib in self._libs.get(lib_name.lower(), []):
                path = self.library_member(lib, vi_name)
                if path and os.path.exists(path):
                    vs, ls = self.symbolic(path), self.symbolic(lib)
                    if vs and ls:
                        return {"path": path, "qual": [lib_name, vi_name],
                                "path_ref": ("0", [vs[0]] + vs[1]), "lib_ref": ("0", [ls[0]] + ls[1])}
            return {"error": "library %r has no member %r in this installation" % (lib_name, vi_name)}
        for d in caller_dirs:
            p = os.path.join(d, target)
            if os.path.exists(p):
                return {"path": p, "qual": [target], "path_ref": ("1", ["", target]), "lib_ref": None}
        self._index()
        hits = self._files.get(target.lower(), [])
        for p in hits:   # the installation's own folders first, then the add-ons
            s = self.symbolic(p)
            if s:
                return {"path": p, "qual": [target], "path_ref": ("0", [s[0]] + s[1]), "lib_ref": None,
                        "ambiguous": len(hits) > 1}
        return {"error": "%r is neither beside the caller nor in the installation" % target}
