# -*- coding: utf-8 -*-
"""Cut the IMPLICIT Property and Invoke Nodes an XControl host binds to an XControl instance -
an XControl's own property and method - as parts node/xctl/prop and node/xctl/invoke.

    cut_xctl_node_parts.py <parts-dir> <host vi>

Cut off NI's Run Dual Mode Thermometer XControl.vi (2026-10-23): a propNode and an invokeNode
whose <ddo> names an xControl object, typed by an XControl refnum (LVObjCtl 0x0006, LVXC items).
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
    b = lvheap.Bundle(lvheap.extract(vi, tempfile.mkdtemp(prefix="xctlnode_")))
    xobjs = {e.get("uid") for e in b.fp.iter() if e.get("class") == "xControl"}
    h = harvest_parts.Harvester(parts_dir, replace="--replace" in argv)
    h.bundle = b
    source = lvheap.portable_source(vi)
    for e in b.bd.iter("SL__arrayElement"):
        d = e.find("ddo")
        if e.get("class") in ("propNode", "invokeNode") and d is not None and d.get("uid") in xobjs:
            key = "node/xctl/prop" if e.get("class") == "propNode" else "node/xctl/invoke"
            h.save("node", key, source, e, harvest_parts.resolve_types(b, e))
    for k in h.found:
        print(k)


if __name__ == "__main__":
    main(sys.argv[1:])
