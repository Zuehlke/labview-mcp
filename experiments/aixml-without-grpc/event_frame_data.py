# -*- coding: utf-8 -*-
"""What LabVIEW wrote for each Event Structure frame of a VI: its EventSpecs, the type of every
STATIC source control (by ddoUID), and the frame's event data cluster - pylabview only. Built to
measure the data cluster of a frame that handles SEVERAL events (2026-10-10).

    event_frame_data.py <out.jsonl> <vi> [<vi> ...] [--multi-only]

One JSON line per Event Structure: {"vi", "uid", "frames": [{"idx", "specs", "sources", "data"}]}.
"""
import json
import os
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402


def flat_xml(b, td_text):
    m = lvheap.TYPEID_RE.fullmatch((td_text or "").strip())
    if not m:
        return None
    return ET.tostring(b.heap_type_tree(int(m.group(1))), encoding="unicode")


def node_by_uid(heap, uid):
    """The heap OBJECT with this uid - not the first element naming it (a z-order list does too)."""
    return next((x for x in heap.iter("SL__arrayElement") if x.get("uid") == uid and x.get("class")), None)


def ddo_type(b, uid):
    """Type and class of the front-panel data object with this uid."""
    for d in b.fp.iter():
        if d.tag in ("ddo", "SL__arrayElement") and d.get("uid") == uid and d.get("class"):
            return {"class": d.get("class"), "type": flat_xml(b, d.findtext("typeDesc"))}
    return None


def scan(vi, multi_only):
    work = tempfile.mkdtemp(prefix="evframe_")
    try:
        copy = os.path.join(work, "x" + os.path.splitext(vi)[1])
        shutil.copyfile(vi, copy)
        b = lvheap.Bundle(lvheap.extract(copy, work, timeout=120))
        out = []
        for n in b.bd.iter("SL__arrayElement"):
            if n.get("class") != "eventStruct":
                continue
            specs = [{c.tag: (c.text or "").strip() for c in sp} for sp in n.find("EventNodeEvents")]
            dl = [c.get("uid") for c in n.find("dataNodeList")]
            fl = [c.get("uid") for c in n.find("filterNodeList")] if n.find("filterNodeList") is not None else []
            frames = []
            for i, du in enumerate(dl):
                fs = [s for s in specs if s.get("diagramIdx") == str(i)]
                if multi_only and len(fs) < 2:
                    continue
                node = node_by_uid(b.bd, du)
                agg = node.find("termList")[0].find("dco") if node is not None and node.find("termList") is not None else None
                # a FILTER event's frame has a second eventDataNode, the Event Filter Node, whose
                # cluster ("Event Filter Data") is what the frame hands back (2026-10-11)
                fnode = node_by_uid(b.bd, fl[i]) if i < len(fl) and fl[i] not in (None, "0") else None
                fagg = fnode.find("termList")[0].find("dco") if fnode is not None and fnode.find("termList") is not None else None
                frames.append({"idx": i, "specs": fs,
                               "sources": {s["ddoUID"]: ddo_type(b, s["ddoUID"]) for s in fs if s.get("ddoUID") not in (None, "", "0")},
                               "data": flat_xml(b, agg.findtext("typeDesc")) if agg is not None else None,
                               "filter": flat_xml(b, fagg.findtext("typeDesc")) if fagg is not None else None})
            if frames:
                out.append({"vi": vi, "uid": n.get("uid"), "frames": frames})
        return out
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main(argv):
    out_path = argv[0]
    multi = "--multi-only" in argv
    vis = [a for a in argv[1:] if not a.startswith("--")]
    with open(out_path, "w", encoding="utf-8") as f:
        for vi in vis:
            try:
                for rec in scan(vi, multi):
                    f.write(json.dumps(rec) + "\n")
            except Exception as ex:  # one unreadable VI does not stop the rest
                print("UNREADABLE %s: %s" % (vi, str(ex).splitlines()[0][:200]))
    print("wrote", out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
