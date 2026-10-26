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


def addon(root, name, version, info=None, files=()):
    vdir = os.path.join(root, name, version)
    os.makedirs(vdir, exist_ok=True)
    if info is not None:
        write(os.path.join(vdir, "lvaddoninfo.json"), json.dumps(info))
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

    def test_unknown_identity_or_missing_info_admits(self):
        self.assertEqual(self.chosen(None, None)["lvai"], "26.3")
        self.assertIn("noinfo", self.chosen((26, 3), 32))

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


if __name__ == "__main__":
    # the classes by name, not unittest.main(): the server's resident worker (worker.py) runs a
    # script through exec, so sys.modules["__main__"] is the WORKER and main() found no tests
    loader = unittest.TestLoader()
    suite = unittest.TestSuite(loader.loadTestsFromTestCase(c) for c in (Selection, Lookup))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    sys.exit(0 if result.wasSuccessful() and result.testsRun else 1)
