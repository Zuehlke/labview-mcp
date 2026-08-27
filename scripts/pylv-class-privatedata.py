#!/usr/bin/env python
"""Build a LabVIEW class private data control from an AIXML-generated cluster VI.

AIXML cannot author a `.ctl` and pylabview cannot author a control from nothing, but the two
compose: AIXML generates a VI whose front panel carries the cluster, and this script converts
that VI's extracted bundle into a class private data control and encodes it into the form a
`.lvclass` stores it in.  See docs/lvclass-creation.md for the measurements behind every
constant here.

Three steps, one per subcommand:

    patch   <bundle main .xml> <ClassName>     turn an extracted cluster VI into private data
    encode  <ctl file>         <out .txt>      .ctl  -> NI.LVClass.FlattenedPrivateDataCTL text
    decode  <.lvclass>         <out .ctl>      the same, backwards - for reading one

The full cycle around `patch` is pylv_extract before it and pylv_rebuild after it; neither needs
LabVIEW.  A worked end-to-end run is in docs/lvclass-creation.md section 1.

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

# Attribute flips that separate a plain VI from a class private data control.  The strictness
# trio (InStBit13, InStBit23, StrictTypeDefVI) is the recipe verified in pylabview-controls.md
# section 2; IsPrivateDataForUDClass is what makes it private data rather than a strict typedef.
FLAG_EDITS = [
    (r'(<Instrument Type=")Standard(")', r"\1Control\2", "Instrument Type"),
    (r'(<Instrument [^>]*?InStBit13=")0(")', r"\g<1>1\2", "InStBit13"),
    (r'(<Instrument [^>]*?InStBit23=")1(")', r"\g<1>0\2", "InStBit23"),
    (r'(<Execution [^>]*?\bTypeDefVI=")0(")', r"\g<1>1\2", "TypeDefVI"),
    (r'(<Execution [^>]*?\bStrictTypeDefVI=")0(")', r"\g<1>1\2", "StrictTypeDefVI"),
    (r'(<Execution2 [^>]*?\bInlinableDiagram=")1(")', r"\g<1>0\2", "InlinableDiagram"),
    (r'(<Execution2 [^>]*?\bIsPrivateDataForUDClass=")0(")', r"\g<1>1\2", "IsPrivateDataForUDClass"),
    (r'(<Execution2 [^>]*?\bDefaultErrorHandling=")0(")', r"\g<1>1\2", "DefaultErrorHandling"),
]

CLUSTER_LABEL = "Cluster of class private data"


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


def patch(main_xml: pathlib.Path, class_name: str) -> None:
    # Text mode with newline='' - the bundle is LF throughout and Python's default would turn
    # every line into CRLF, hiding the handful that actually changed.
    text = main_xml.read_text(encoding="utf-8", newline="")

    for pattern, replacement, what in FLAG_EDITS:
        text, hits = re.subn(pattern, replacement, text, count=1)
        if hits != 1:
            sys.exit(f"{main_xml.name}: expected exactly one {what} to change, found {hits}. "
                     f"Is this the bundle of an AIXML-generated cluster VI?")

    # Wrap the cluster's TypeDesc IN PLACE: the TypeDef takes over the cluster's own FlatTypeID
    # and the nested cluster gets none, so no TypeID moves and TopLevel stays valid.
    match = re.search(
        r'^([ ]*)<TypeDesc Type="Cluster" Format="inline" Label="' + re.escape(CLUSTER_LABEL) + r'">\n'
        r"(.*?)^\1  </TypeDesc>\n", text, re.S | re.M)
    if not match:
        sys.exit(f'{main_xml.name}: no cluster labelled "{CLUSTER_LABEL}". '
                 f"Name the AIXML Control that, so LabVIEW builds the type under the right label.")

    indent, children = match.group(1), match.group(2)
    nested = re.sub(r"^  ", "", children, flags=re.M)       # children keep their own depth
    nested = re.sub(r"^", "  ", nested, flags=re.M)         # then one level in for the nest
    wrapped = (
        f'{indent}<TypeDesc Type="TypeDef" Flag1="0x0" Format="inline">\n'
        f'{indent}  <TypeDesc Type="Cluster" Nested="True" Format="inline" Label="{CLUSTER_LABEL}">\n'
        f"{nested}"
        f"{indent}    </TypeDesc>\n"
        f'{indent}  <Label Text="{class_name}.lvclass" />\n'
        f'{indent}  <Label Text="{class_name}.ctl" />\n'
        f"{indent}  </TypeDesc>\n")
    text = text[:match.start()] + wrapped + text[match.end():]

    main_xml.write_text(text, encoding="utf-8", newline="")
    print(f"{main_xml.name}: patched into the private data control of {class_name}.lvclass. "
          f"pylv_rebuild it to {class_name}.ctl next.")


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

    p = sub.add_parser("patch", help="turn an extracted cluster VI into a private data control")
    p.add_argument("main_xml", type=pathlib.Path, help="the bundle's main .xml from pylv_extract")
    p.add_argument("class_name", help="the class name without .lvclass, e.g. Bus")

    p = sub.add_parser("encode", help=".ctl -> the text a .lvclass stores it as")
    p.add_argument("ctl", type=pathlib.Path)
    p.add_argument("out", type=pathlib.Path)

    p = sub.add_parser("decode", help="read a .lvclass's private data back out as a .ctl")
    p.add_argument("lvclass", type=pathlib.Path)
    p.add_argument("out", type=pathlib.Path)

    args = parser.parse_args()
    if args.command == "patch":
        patch(args.main_xml, args.class_name)
    elif args.command == "encode":
        encode(args.ctl, args.out)
    else:
        decode(args.lvclass, args.out)


if __name__ == "__main__":
    main()
