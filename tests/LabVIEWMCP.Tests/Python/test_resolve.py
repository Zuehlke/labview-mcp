# -*- coding: utf-8 -*-
"""resolve.py's LVAddons handling (issue #99), on a synthetic installation and LVAddons tree.

    python test_resolve.py <repo root>

Run by ResolverAddonTests through the bundled interpreter. The tree is built in a temp folder:
no LabVIEW, no Program Files. The add-on layout copies the real one - <addon>\\<version>\\
lvaddoninfo.json beside vi.lib, and lvai's Targets\\win32 - and the version and bitness rules
were measured on LabVIEW 26.3.1 (resolve.select_addons).
"""
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(sys.argv.pop(1) if len(sys.argv) > 1 else os.getcwd(),
                                "scripts", "aixml-pylabview"))
import resolve  # noqa: E402

LVCLASS = """<?xml version='1.0' encoding='UTF-8'?>
<Library LVVersion="26008000">
\t<Property Name="NI.LVClass.IsInterface" Type="Bool">{iface}</Property>
\t<Item Name="Friends List" Type="Friends List"/>
\t<Item Name="Parent Libraries" Type="Parent Libraries">{parent}</Item>
\t<Item Name="{name}.vi" Type="VI" URL="../{name}.vi"/>
</Library>
"""


def write(path, text=""):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text)


def addon(root, name, version, info=None, files=(), raw=None):
    """An add-on version folder. `info` gets its AddonName from the folder unless it names one -
    LabVIEW ignores an add-on without it (measured); `raw` writes the file's bytes as given."""
    vdir = os.path.join(root, name, version)
    os.makedirs(vdir, exist_ok=True)
    if raw is not None:
        with open(os.path.join(vdir, "lvaddoninfo.json"), "wb") as fh:
            fh.write(raw)
    elif info is not None:
        write(os.path.join(vdir, "lvaddoninfo.json"), json.dumps(dict({"AddonName": name}, **info)))
    for rel in files:
        write(os.path.join(vdir, rel))
    return vdir


class Selection(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="resolve-")
        self.addons = os.path.join(self.tmp, "LVAddons")
        addon(self.addons, "lvai", "1", {"MinimumSupportedLVVersion": "25.0"})
        addon(self.addons, "lvai", "26.1", {"MinimumSupportedLVVersion": "26.0"})
        addon(self.addons, "lvai", "26.3", {"MinimumSupportedLVVersion": "26.3"})
        addon(self.addons, "daq64", "1", {"MinimumSupportedLVVersion": "22.0", "SupportedBitness": "64"})
        addon(self.addons, "daq32", "1", {"MinimumSupportedLVVersion": "22.0", "SupportedBitness": "32"})
        addon(self.addons, "noinfo", "1", None)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def chosen(self, version, bits):
        return {a: os.path.basename(v) for a, v in resolve.select_addons(self.addons, version, bits)}

    def test_highest_compatible_version_only(self):
        self.assertEqual(self.chosen((26, 3), 32)["lvai"], "26.3")
        self.assertEqual(self.chosen((26, 1), 32)["lvai"], "26.1")
        self.assertEqual(self.chosen((25, 3), 32)["lvai"], "1")
        self.assertNotIn("lvai", self.chosen((24, 3), 32))

    def test_bitness_excludes_the_other_build(self):
        got = self.chosen((26, 3), 32)
        self.assertIn("daq32", got)
        self.assertNotIn("daq64", got)
        self.assertIn("daq64", self.chosen((26, 3), 64))

    def test_unknown_identity_admits_every_version_field(self):
        self.assertEqual(self.chosen(None, None)["lvai"], "26.3")

    def test_an_addon_LabVIEW_ignores_is_not_chosen(self):
        # measured 2026-10-26: no file, not JSON, a UTF-8 BOM, no AddonName - each ignored
        addon(self.addons, "bom", "1", raw=b"\xef\xbb\xbf" + json.dumps({"AddonName": "bom"}).encode())
        addon(self.addons, "badjson", "1", raw=b"not json")
        addon(self.addons, "noname", "1", raw=json.dumps({"ApiVersion": "v1"}).encode())
        addon(self.addons, "renamed", "1", {"AddonName": "something else"})
        got = self.chosen((26, 3), 32)
        for name in ("noinfo", "bom", "badjson", "noname"):
            self.assertNotIn(name, got)
        self.assertIn("renamed", got)

    def test_addons_of_all_locations_ordered_by_name(self):
        # measured: for one file in two add-ons the alphabetically first add-on wins, whichever
        # location holds it and whatever order the INI lists the locations in
        other = os.path.join(self.tmp, "other")
        addon(other, "aprobe", "1", {})
        addon(other, "zprobe", "1", {})
        names = [a for a, _ in resolve.ordered_addons([self.addons, other], (26, 3), 32)]
        self.assertEqual(names, sorted(names, key=str.lower))
        self.assertEqual(names[0], "aprobe")
        self.assertEqual(names, [a for a, _ in resolve.ordered_addons([other, self.addons], (26, 3), 32)])

    def test_same_folder_in_two_locations_the_later_first_and_folder_beats_AddonName(self):
        # measured (lvaddons-probe4): `A;B` took B's dup, `B;A` A's; folder aaa (AddonName zzz)
        # beat folder mmm (AddonName bbb)
        a, b = os.path.join(self.tmp, "A"), os.path.join(self.tmp, "B")
        addon(a, "dup", "1", {})
        addon(b, "dup", "1", {})
        addon(a, "aaa", "1", {"AddonName": "zzz"})
        addon(a, "mmm", "1", {"AddonName": "bbb"})
        first_dup = lambda locs: next(v for n, v in resolve.ordered_addons(locs, (26, 3), 32) if n == "dup")
        self.assertTrue(first_dup([a, b]).startswith(b))
        self.assertTrue(first_dup([b, a]).startswith(a))
        # and the earlier one is GONE, not searched behind it (lvaddons-probe5)
        self.assertEqual([n for n, _ in resolve.ordered_addons([a, b], (26, 3), 32)].count("dup"), 1)
        names = [n for n, _ in resolve.ordered_addons([a], (26, 3), 32)]
        self.assertLess(names.index("aaa"), names.index("mmm"))

    def test_targets_follow_bitness(self):
        vdir = addon(self.addons, "tgt", "1", {}, files=(r"Targets\win32\resource\a.vi", r"Targets\win64\resource\a.vi"))
        names = lambda bits: [os.path.relpath(t, vdir) for t in resolve.addon_trees(vdir, bits)]
        self.assertEqual(names(32), [".", r"Targets\win32"])
        self.assertEqual(names(64), [".", r"Targets\win64"])
        self.assertEqual(names(None), [".", r"Targets\win32", r"Targets\win64"])

    def test_missing_root_is_empty(self):
        self.assertEqual(resolve.select_addons(os.path.join(self.tmp, "nope"), (26, 3), 32), [])


class Lookup(unittest.TestCase):
    """A file the installation's vi.lib lacks is found in an add-on's vi.lib."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="resolve-")
        self.lv = os.path.join(self.tmp, "LabVIEW 2026")
        write(os.path.join(self.lv, "vi.lib", "Utility", "Core.vi"))
        self.addons = os.path.join(self.tmp, "LVAddons")
        info = {"MinimumSupportedLVVersion": "24.0"}
        vdir = addon(self.addons, "pkg", "1", info)
        lib = os.path.join(vdir, "vi.lib", "Pkg")
        write(os.path.join(lib, "Base", "Base.lvclass"), LVCLASS.format(name="Base", iface="false", parent=""))
        write(os.path.join(lib, "Base", "Base.vi"))
        write(os.path.join(lib, "Test", "Test.lvclass"), LVCLASS.format(
            name="Test", iface="false",
            parent='<Item Name="Base.lvclass" Type="Parent" URL="/&lt;vilib&gt;/Pkg/Base/Base.lvclass"/>'))
        write(os.path.join(lib, "Test", "Test.vi"))
        # an OLDER version of the same add-on carrying a different Test.lvclass, which must lose
        old = addon(self.addons, "pkg", "0", info)
        write(os.path.join(old, "vi.lib", "Pkg", "Old", "Test.lvclass"), LVCLASS.format(name="Old", iface="false", parent=""))
        self.lib = lib
        resolve._INSTALL_INDEX.clear()
        self.r = resolve.Resolver(self.lv, self.addons)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_the_installation_comes_before_every_addon(self):
        # measured (lvaddons-probe4): an add-on copy of vi.lib\Utility\error.llb\Trim Whitespace
        # (string).vi lost to the installation's
        kinds = [d.startswith(self.lv) for _a, d in self.r.roots]
        self.assertEqual(kinds, sorted(kinds, reverse=True))

    def test_symbolic_link_searches_every_vilib(self):
        self.assertEqual(self.r.linked_file("0", ["<vilib>", "Utility", "Core.vi"], []),
                         os.path.join(self.lv, "vi.lib", "Utility", "Core.vi"))
        self.assertEqual(self.r.linked_file("0", ["<vilib>", "Pkg", "Test", "Test.vi"], []),
                         os.path.join(self.lib, "Test", "Test.vi"))
        self.assertIsNone(self.r.linked_file("0", ["<vilib>", "Pkg", "Missing.vi"], []))

    def test_parent_class_in_an_addon(self):
        self.assertEqual(self.r.class_parents(os.path.join(self.lib, "Test", "Test.lvclass")),
                         [os.path.join(self.lib, "Base", "Base.lvclass")])

    def test_class_member_of_the_selected_version(self):
        got = self.r.resolve("Test.lvclass:Test.vi", [os.path.join(self.tmp, "proj")])
        self.assertNotIn("error", got, got)
        self.assertEqual(got["path"], os.path.join(self.lib, "Test", "Test.vi"))
        self.assertEqual(got["path_ref"], ("0", ["<vilib>", "Pkg", "Test", "Test.vi"]))


class Configured(unittest.TestCase):
    """--labview-root / --addon-locations, as the server passes them (LabViewContext)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="resolve-")
        self.lv = os.path.join(self.tmp, "LabVIEW 2024")
        os.makedirs(os.path.join(self.lv, "vi.lib"))
        self.base = os.path.join(self.tmp, "base")
        self.venv = os.path.join(self.tmp, "venv")
        addon(self.base, "drv", "1", {"MinimumSupportedLVVersion": "22.0"}, files=(r"vi.lib\Drv\drv.vi",))
        addon(self.venv, "pkg", "1", {"MinimumSupportedLVVersion": "24.0"}, files=(r"vi.lib\Pkg\pkg.vi",))

    def tearDown(self):
        resolve.configure()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_flags_are_consumed_and_configure_the_resolver(self):
        rest = resolve.parse_args(["--parts", "P", "--labview-root", self.lv,
                                   "--addon-locations", self.base + ";" + self.venv, "in.xml", "out.vi"])
        self.assertEqual(rest, ["--parts", "P", "in.xml", "out.vi"])
        r = resolve.Resolver()
        self.assertEqual(r.lv_root, self.lv)
        self.assertEqual([a for a, _ in r.addons], ["drv", "pkg"])
        self.assertEqual(r.linked_file("0", ["<vilib>", "Pkg", "pkg.vi"], []),
                         os.path.join(self.venv, "pkg", "1", "vi.lib", "Pkg", "pkg.vi"))

    def test_empty_locations_mean_no_addons_and_no_flag_means_the_default(self):
        resolve.parse_args(["--labview-root", self.lv, "--addon-locations", ""])
        self.assertEqual(resolve.Resolver().addons, [])
        resolve.parse_args(["x"])
        self.assertIsNone(resolve._CONFIGURED["locations"])


if __name__ == "__main__":
    # the classes by name, not unittest.main(): the server's resident worker (worker.py) runs a
    # script through exec, so sys.modules["__main__"] is the WORKER and main() found no tests
    loader = unittest.TestLoader()
    suite = unittest.TestSuite(loader.loadTestsFromTestCase(c) for c in (Selection, Lookup, Configured))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    sys.exit(0 if result.wasSuccessful() and result.testsRun else 1)
