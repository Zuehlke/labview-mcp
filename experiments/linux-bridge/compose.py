# -*- coding: utf-8 -*-
"""Run the repo composer (scripts/aixml-pylabview/aixml2vi.py) with the host's python3.

    python3 compose.py <in.xml> <out.vi> [aixml2vi options]

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

import aixml2vi  # noqa: E402

if __name__ == "__main__":
    sys.exit(aixml2vi.main(sys.argv[1:]))
