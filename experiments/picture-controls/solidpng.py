"""Writes solid-colour 32x32 RGBA PNGs, one per colour, to tell button states apart in a screenshot.

usage: solidpng.py <outDir> <rrggbb> [<rrggbb> ...]   ->  <outDir>/state<i>.png
"""
import os, struct, sys, zlib


def png(path, rgb, w=32, h=32):
    r, g, b = (int(rgb[i:i + 2], 16) for i in (0, 2, 4))
    raw = b"".join(b"\x00" + bytes((r, g, b, 255)) * w for _ in range(h))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


out = sys.argv[1]
os.makedirs(out, exist_ok=True)
for i, c in enumerate(sys.argv[2:]):
    png(os.path.join(out, "state%d.png" % i), c)
    print(os.path.join(out, "state%d.png" % i), c)
