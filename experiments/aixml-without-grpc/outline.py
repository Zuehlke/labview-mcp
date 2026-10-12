# -*- coding: utf-8 -*-
"""Print a heap object as a compact tree, cosmetics left out.

    outline.py <vi | bundle main.xml> <uid | class> [depth]

Labels, cosmetic parts and colours are skipped, everything structural is shown with its uid,
so a structure's frames, terminals and the lists that own them can be read in one screen.
"""
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # the bundle's ._pth leaves it off
COMPOSER = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402

SKIP_CLASS = {"label", "cosm", "multiCosm", "bigMultiCosm", "numLabel", "annex", "SubCosm",
              "Image", "textHair", "fontRun", "multiLabel"}
SKIP_TAG = {"fgColor", "bgColor", "partID", "masterPart", "image", "textRec", "howGrow",
            "fontofst", "termHotPoint"}


def outline(e, d, maxd, out):
    if d > maxd or e.get("class") in SKIP_CLASS or e.tag in SKIP_TAG:
        return
    t = (e.text or "").strip()
    c, u = e.get("class"), e.get("uid")
    out.append("  " * d + e.tag + (" class=%s" % c if c else "") + (" uid=%s" % u if u else "")
               + (" = %s" % t if t and len(e) == 0 else "")
               + (" elements=%s" % e.get("elements") if e.get("elements") else ""))
    for ch in e:
        outline(ch, d + 1, maxd, out)


def main(argv):
    src, what = argv[0], argv[1]
    depth = int(argv[2]) if len(argv) > 2 else 12
    main_xml = src if src.lower().endswith(".xml") else lvheap.extract(src, tempfile.mkdtemp())
    b = lvheap.Bundle(main_xml)
    out = []
    for heap in (b.bd, b.fp):
        for e in heap.iter():
            if e.get("uid") == what or (not what.isdigit() and e.get("class") == what):
                outline(e, 0, depth, out)
                break
        if out:
            break
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print("\n".join(out))


if __name__ == "__main__":
    main(sys.argv[1:])
