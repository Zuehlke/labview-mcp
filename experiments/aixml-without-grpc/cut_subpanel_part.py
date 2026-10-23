# -*- coding: utf-8 -*-
"""Cut a front-panel SUB PANEL as the part fpobj/stdSubPanel: the `grouper` LabVIEW wraps it in,
with the stdSubPanel object inside.

    cut_subpanel_part.py <parts-dir> <vi> [--replace]

Cut off NI's examples/Controls and Indicators/Containers/Multiple VIs in a Subpanel.vi
(2026-10-23). A sub panel is no fPDCO and has no data: it sits in its pane's zPlaneList inside a
grouper, typed by a plain LVObjCtl refnum (CtlFlags 0x0002) labelled like it, and its annex
counts the implicit invoke nodes bound to it. AIXML has no control for it - NI's export writes
only the nodes, `link="Subpanel"`.
"""
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
sys.path.insert(0, COMPOSER)
import harvest_parts  # noqa: E402
import lvheap  # noqa: E402


def main(argv):
    parts_dir, vi = argv[0], argv[1]
    b = lvheap.Bundle(lvheap.extract(vi, tempfile.mkdtemp(prefix="subpanel_")))
    h = harvest_parts.Harvester(parts_dir, replace="--replace" in argv)
    h.bundle = b
    source = lvheap.portable_source(vi)
    for e in b.fp.iter("SL__arrayElement"):
        pl = e.find("partsList")
        if e.get("class") == "grouper" and pl is not None and len(pl) == 1 \
                and pl[0].get("class") == "stdSubPanel":
            h.save("fpobj", "fpobj/stdSubPanel", source, e, harvest_parts.resolve_types(b, e))
            break
    for k in h.found:
        print(k)


if __name__ == "__main__":
    main(sys.argv[1:])
