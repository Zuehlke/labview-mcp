# -*- coding: utf-8 -*-
"""Dump the class link records (DyOM, FPPI, DDPI, ...) of LabVIEW-saved VIs, field by field,
following pylabview's generic UDClassHeapAPISaveInfo layout - and show whatever sits between the
API link cache and the offset list, which pylabview does not expect for LabVIEW 2026's DyOM.

    class_link_records.py <vi or folder> [...]

Each record: tag, qualified name, path, flag, lib version, the three cache flags, the cache
content, the EXTRA bytes, and the offset list. Offsets are within the section, as pylabview
aligns them."""
import glob
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
COMPOSER = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

CLASS_TAGS = (b"DyOM", b"FPPI", b"DDPI", b"VRPI", b"HOPI", b"PNOM", b"DRPI")


def u32(d, i):
    return int.from_bytes(d[i:i + 4], "big")


def pad(i, n):
    return i + (n - i % n) % n


def record_at(data, i, nxt):
    tag = data[i:i + 4]
    j = pad(i + 4, 4)
    n = u32(data, j)
    j += 4
    names = []
    for _ in range(n):
        ln = data[j]
        names.append(b"".join(x for x in vi2aixml._expand_nested(data[j + 1:j + 1 + ln])).decode("mac_roman", "replace")
                     if False else ":".join(x.decode("mac_roman", "replace") for x in vi2aixml._expand_nested(data[j + 1:j + 1 + ln])))
        j += 1 + ln
    j = pad(j, 2)
    assert data[j:j + 4] == b"PTH0", (tag, hex(j))
    ln = u32(data, j + 4)
    path_bytes = data[j + 8:j + 8 + ln]
    j += 8 + ln
    flag = u32(data, j)
    j += 4
    j = pad(j, 4)
    libver = data[j:j + 8].hex()
    j += 8
    internal, bool2, callparent = data[j], data[j + 1], data[j + 2]
    j += 3
    cl = u32(data, j)
    content = data[j + 4:j + 4 + cl]
    j += 4 + cl
    j = pad(j, 4)
    # the offset list is the last thing before the next record: find k with count==k there
    end = nxt - 2 if data[nxt - 2:nxt] == b"\x00\x02" else nxt
    offs, at = None, None
    for k in range(1, 64):
        a = end - 4 * k - 4
        if a < j:
            break
        if u32(data, a) == k:
            offs, at = [u32(data, a + 4 + 4 * m) for m in range(k)], a
            break
    extra = data[j:at].hex() if at is not None else data[j:end].hex()
    return {"tag": tag.decode(), "names": names, "path": path_bytes.hex(), "flag": flag,
            "libver": libver, "flags": (internal, bool2, callparent), "content": content.hex(),
            "extra": extra, "offsets": offs}


def records(vi):
    tmp = tempfile.mkdtemp(prefix="clr_")
    main = lvheap.extract(vi, tmp)
    b = lvheap.Bundle(main)
    sec = b.main.find("LIbd/Section")
    if sec is None or sec.get("Format") != "bin":
        return "LIbd parsed by pylabview (no class records it could not read)", []
    data = open(os.path.join(b.dir, sec.get("File")), "rb").read()
    starts = sorted(k for t in vi2aixml.RECORD_TAGS for k in _all(data, t))
    out = []
    for n, i in enumerate(starts):
        if data[i:i + 4] in CLASS_TAGS:
            nxt = starts[n + 1] if n + 1 < len(starts) else len(data)
            try:
                out.append(record_at(data, i, nxt))
            except Exception as ex:   # a layout this walker does not know
                out.append({"tag": data[i:i + 4].decode(), "error": str(ex)})
    return None, out


def _all(data, tag):
    i = data.find(tag)
    while i >= 0:
        yield i
        i = data.find(tag, i + 1)


def main(argv):
    vis = []
    for a in argv:
        vis += sorted(glob.glob(os.path.join(a, "**", "*.vi"), recursive=True)) if os.path.isdir(a) else [a]
    for vi in vis:
        note, recs = records(vi)
        print("==", vi)
        if note:
            print("  ", note)
        for r in recs:
            print("  ", r)


if __name__ == "__main__":
    main(sys.argv[1:])
