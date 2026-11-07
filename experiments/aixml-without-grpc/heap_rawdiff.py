# -*- coding: utf-8 -*-
"""Block-by-block RAW comparison of two RSRC files (a VI and its pylabview round trip): every
block section's raw bytes, and - where the block is coded - its decoded bytes. Shows what
pylabview's extract smooths over, because re-extracting both files gives identical bundles.

    heap_rawdiff.py <original.vi> <rebuilt.vi>
"""
import argparse
import os
import sys

APP = os.path.join(os.path.dirname(sys.executable), "app")
sys.path.insert(0, APP)
from pylabview.LVrsrcontainer import VI  # noqa: E402
from pylabview.LVblock import BLOCK_CODING  # noqa: E402


def load(path):
    po = argparse.Namespace(rsrc=path, xml="", verbose=0, textcp="mac_roman", raw_connectors=False,
                            print_map=None, keep_names=False, typedesc_list_limit=4095,
                            array_data_limit=(2 ** 28) - 1, store_as_data_above=4095,
                            filebase=os.path.splitext(os.path.basename(path))[0], password=None)
    fh = open(path, "rb")
    return VI(po, rsrc_fh=fh), fh


def sections(vi):
    out = {}
    for ident, block in vi.blocks.items():
        block.readRawDataSections()
        for snum, sec in block.sections.items():
            raw = sec.raw_data or b""
            try:
                dec = block.getData(section_num=snum, use_coding=sec.block_coding).read()
            except Exception as ex:   # noqa: BLE001
                dec = ("decode failed: %s" % ex).encode()
            out[(ident, snum)] = (raw, dec, sec.block_coding)
    return out


def main(argv):
    (va, fa), (vb, fb) = load(argv[0]), load(argv[1])
    sa, sb = sections(va), sections(vb)
    print("block order original:", list(va.blocks.keys()))
    print("block order rebuilt :", list(vb.blocks.keys()))
    for k in sorted(set(sa) | set(sb), key=str):
        if k not in sa or k not in sb:
            print("%-14s only in %s" % (k, "original" if k in sa else "rebuilt"))
            continue
        (ra, da, ca), (rb, db, cb) = sa[k], sb[k]
        if ra == rb:
            continue
        first = next((i for i in range(min(len(da), len(db))) if da[i] != db[i]), min(len(da), len(db)))
        print("%-14s raw %6d -> %6d  coding %s/%s  decoded %6d -> %6d  %s" % (
            k, len(ra), len(rb), getattr(ca, "name", ca), getattr(cb, "name", cb), len(da), len(db),
            "DECODED SAME" if da == db else "decoded differs at %d: %s | %s" % (
                first, da[first:first + 24].hex(), db[first:first + 24].hex())))
    fa.close()
    fb.close()


if __name__ == "__main__":
    main(sys.argv[1:])
