#!/usr/bin/env python
"""Read and write the private data control a `.lvclass` carries, without LabVIEW.

    encode  <ctl file>  <out .txt>   .ctl  -> NI.LVClass.FlattenedPrivateDataCTL text
    decode  <.lvclass>  <out .ctl>   the same, backwards - for reading one

WHAT USED TO BE HERE AND WHY IT IS GONE.  This script also built a private data control, by
converting an AIXML-generated cluster VI into one: flip `Instrument Type` to Control, set the
typedef bits, wrap the cluster's TypeDesc.  Every class made that way was reported by LabVIEW
normally and refused by its compiler - "Front panel control contains a data type with a type
definition" - and every accessor built against it broke with it.

The cause is not the flags, which were fixed and still did not help.  A private data control's
TYPE SPACE is compiler output: VCTP, the TopLevel map, TM80's data-space sections and the
front-panel DCO record with its data-space offsets, none of which the source VI has.  The layout
is fully derived in docs/lvclass-creation.md section 2a for anyone who wants to synthesise it -
but LabVIEW's own project provider does it correctly in about 350 ms, so lvai_create_class calls
that instead (`scripts/lvai_create_class.xml`).

Reading one is still useful and is what remains: to inspect a class's private data, and to
transplant a LabVIEW-authored control into a class that needs one.  Section 2a has that recipe.

The blob's 29-byte header is copied verbatim from a LabVIEW-authored class.  Its fields are not
understood; only the u32 length that follows it is.  `encode` self-checks by decoding its own
output, and running `encode` over a `.ctl` taken out of an NI class reproduces that class's
property text byte for byte - which is the check that catches a mis-sized header, because
LabVIEW's own answer to one is a blank class and an error message about paths.
"""

import argparse
import pathlib
import re
import struct
import sys

# Read off examples/Channels/Event Messenger/.../Circle Message.lvclass, LabVIEW 2026 Q3.
# Byte 0-3 are the LVVersion; the rest is opaque.  The u32 length of the .ctl follows it.
HEADER = bytes.fromhex("26008000000000020005000500000c00400001ffffffff000000010001")
TRAILER = b"\x00\x00\x00\x00"
BLOB_PROPERTY = "NI.LVClass.FlattenedPrivateDataCTL"


def lv_encode(data: bytes) -> str:
    """LabVIEW's 6-bit flattened-string encoding: one character per 6 bits, offset 0x21."""
    out, bits, n = [], 0, 0
    for byte in data:
        bits = (bits << 8) | byte
        n += 8
        while n >= 6:
            n -= 6
            out.append(chr(((bits >> n) & 0x3F) + 0x21))
    if n:                                        # pad the final group with zero bits
        out.append(chr(((bits << (6 - n)) & 0x3F) + 0x21))
    return "".join(out)


def lv_decode(text: str) -> bytes:
    out, bits, n = bytearray(), 0, 0
    for ch in text:
        if ch in "\r\n\t ":
            continue
        bits = (bits << 6) | ((ord(ch) - 0x21) & 0x3F)
        n += 6
        if n >= 8:
            n -= 8
            out.append((bits >> n) & 0xFF)
    return bytes(out)


def encode(ctl: pathlib.Path, out: pathlib.Path) -> None:
    data = ctl.read_bytes()
    blob = HEADER + struct.pack(">I", len(data)) + data + TRAILER
    text = lv_encode(blob)
    if lv_decode(text) != blob:
        sys.exit("encoder round trip failed - refusing to write a blob LabVIEW would reject")
    escaped = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    out.write_text(escaped, encoding="utf-8", newline="")
    print(f"{ctl.name}: {len(data)} B -> {len(blob)} B blob -> {len(text)} chars, round trip ok")
    print(f"  paste into <Property Name=\"{BLOB_PROPERTY}\" Type=\"Bin\">…</Property>")


def decode(lvclass: pathlib.Path, out: pathlib.Path) -> None:
    source = lvclass.read_text(encoding="utf-8")
    match = re.search(re.escape(BLOB_PROPERTY) + r'" Type="Bin">(.*?)</Property>', source, re.S)
    if not match:
        sys.exit(f"{lvclass.name} carries no {BLOB_PROPERTY} - the class has no private data")
    text = match.group(1)
    for entity, char in (("&lt;", "<"), ("&gt;", ">"), ("&quot;", '"'), ("&apos;", "'"), ("&amp;", "&")):
        text = text.replace(entity, char)
    blob = lv_decode(text)
    length = struct.unpack(">I", blob[29:33])[0]
    out.write_bytes(blob[33:33 + length])
    print(f"{lvclass.name}: {len(blob)} B blob -> {length} B .ctl -> {out}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("encode", help=".ctl -> the text a .lvclass stores it as")
    p.add_argument("ctl", type=pathlib.Path)
    p.add_argument("out", type=pathlib.Path)

    p = sub.add_parser("decode", help="read a .lvclass's private data back out as a .ctl")
    p.add_argument("lvclass", type=pathlib.Path)
    p.add_argument("out", type=pathlib.Path)

    args = parser.parse_args()
    if args.command == "encode":
        encode(args.ctl, args.out)
    else:
        decode(args.lvclass, args.out)


if __name__ == "__main__":
    main()
