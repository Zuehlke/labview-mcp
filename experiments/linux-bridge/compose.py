# -*- coding: utf-8 -*-
"""Run the repo composer (scripts/aixml-pylabview/aixml2vi.py) with the host's python3.

    python3 compose.py <in.xml> <out.vi> [aixml2vi options]
    python3 compose.py --read <in.vi> [vi2aixml options]      # read a VI back to AIXML on stdout

The composer starts pylabview through lvheap._pylabview_argv(), which expects a Windows bundle.
This launcher patches that one function so pylabview runs as [sys.executable, readRSRC.py] from the
vendored source. scripts/ is not changed. Experiment only; Slice 6 replaces it with the real
Linux runtime.
"""
import os
import sys

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(REPO, "scripts", "aixml-pylabview"))
import lvheap  # noqa: E402

READ_RSRC = os.path.join(REPO, "tools", "pylabview", "vendor", "pylabview", "readRSRC.py")
# readRSRC.py does `import pylabview.LVxml`: the package is vendor/pylabview, so vendor/ goes on the path
os.environ["PYTHONPATH"] = os.path.dirname(os.path.dirname(READ_RSRC))
lvheap._pylabview_argv = lambda: [sys.executable, READ_RSRC]

# LVMCP_EXEC_FLAGS="RunOnOpen=1" sets VI execution flags on the composed heap XML just before the
# rebuild. The composer's own `<?lvmcp exec?>` accepts only IsReentrant, PooledReentrancy,
# IsSubroutine, Priority, PrefExecSyst and SaveParallel (aixml2vi.py), so "Run when opened" cannot
# be asked of it. Experiment only: Step 1.2 measures the `labview <vi>` route with it.
_EXEC_FLAGS = dict(kv.split("=", 1) for kv in os.environ.get("LVMCP_EXEC_FLAGS", "").split(",") if kv)
if _EXEC_FLAGS:
    import xml.etree.ElementTree as ET
    _rebuild = lvheap.rebuild

    def _rebuild_with_flags(main_xml, vi_path):
        tree = ET.parse(main_xml)
        exe = tree.getroot().find("LVSR/Section/Execution")
        for k, v in _EXEC_FLAGS.items():
            if exe.get(k) is None:
                raise SystemExit("no such Execution flag: %s" % k)
            exe.set(k, v)
        tree.write(main_xml, encoding="utf-8", xml_declaration=True)
        return _rebuild(main_xml, vi_path)

    lvheap.rebuild = _rebuild_with_flags

import aixml2vi  # noqa: E402

if __name__ == "__main__":
    if sys.argv[1:2] == ["--read"]:
        import vi2aixml  # noqa: E402
        vi2aixml.main(sys.argv[2:])
        sys.exit(0)
    sys.exit(aixml2vi.main(sys.argv[1:]))
