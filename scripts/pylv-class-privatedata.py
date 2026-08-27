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

# Attribute flips that separate a plain VI from a class private data control.
#
# InStBit13 IS DELIBERATELY NOT TOUCHED, and it used to be.  The strictness trio
# (InStBit13, InStBit23, StrictTypeDefVI) is the recipe in pylabview-controls.md section 2 - and
# that section is about a STRICT TYPEDEF.  A class private data control differs: measured
# 2026-08-27 against a class built BY HAND IN THE IDE with the same five fields, whose control
# carries `InStBit13="0"` - which is what an AIXML-generated VI already has, so setting it to 1
# was pure damage.  `PropTypesIssues` and `DefaultErrorHandling` were wrong the same way.
#
# TAKE THE REFERENCE FROM A CLASS YOU BUILD, not from a shipped example.  A first attempt read
# these off NI's `Circle Message.lvclass` and got InStBit23 backwards - that class disagrees with a
# freshly authored one on exactly that bit, and its private data holds different fields so nothing
# else could be held constant either.  That produced a second wrong answer on top of the first.
#
# All of it went unnoticed because the create-class load check asks LabVIEW whether it REPORTS the
# class, and LabVIEW reports a class whose private data does not compile perfectly happily -
# `missingItems: []`, a name, a private data item, `errorCode 0`.  Only the IDE's Error list shows
# it, and `Execution.State`/`BadDDO` in the saved file.
#
# THE FLAGS ARE NOT THE WHOLE DEFECT.  With all of them correct the control still does not load:
# its type space (VCTP/TM80/TopLevel) is a VI's, not a control's.  docs/lvclass-creation.md §2a has
# that layout fully derived - it is buildable, and the piece still missing is that LabVIEW
# RENUMBERS the type space when it saves, so a generator has to write the pre-save numbering rather
# than the post-save one this was first measured in.
FLAG_EDITS = [
    (r'(<Instrument Type=")Standard(")', r"\1Control\2", "Instrument Type"),
    (r'(<Instrument [^>]*?InStBit23=")1(")', r"\g<1>0\2", "InStBit23"),
    (r'(<Execution [^>]*?\bTypeDefVI=")0(")', r"\g<1>1\2", "TypeDefVI"),
    (r'(<Execution [^>]*?\bStrictTypeDefVI=")0(")', r"\g<1>1\2", "StrictTypeDefVI"),
    # An AIXML-generated VI arrives with PropTypesIssues set - literally "this VI has property
    # type problems" - and it survives into the control unless cleared here.  A LabVIEW-authored
    # private data control carries 0.
    (r'(<Execution [^>]*?\bPropTypesIssues=")1(")', r"\g<1>0\2", "PropTypesIssues"),
    (r'(<Execution2 [^>]*?\bInlinableDiagram=")1(")', r"\g<1>0\2", "InlinableDiagram"),
    (r'(<Execution2 [^>]*?\bIsPrivateDataForUDClass=")0(")', r"\g<1>1\2", "IsPrivateDataForUDClass"),
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

    check_flags(text, main_xml)

    main_xml.write_text(text, encoding="utf-8", newline="")
    print(f"{main_xml.name}: patched into the private data control of {class_name}.lvclass. "
          f"pylv_rebuild it to {class_name}.ctl next.")

    missing = check_data_space(text)
    if missing:
        print("  WARNING: this control will NOT load - " + ", ".join(missing) + ".")
        print("  A class made from it is reported by LabVIEW and refused by its compiler: the IDE's")
        print("  Error list says \"Front panel control contains a data type with a type definition\"")
        print("  and every accessor built against it breaks with it. Take the private data control")
        print("  from the IDE instead - docs/lvclass-creation.md section 2a has the transplant")
        print("  recipe, and the layout that would have to be synthesised to lift this.")


# What a loadable private data control has and a converted VI does not. These are the three
# markers of the data space; the layout behind them is in docs/lvclass-creation.md section 2a.
#
# THIS IS A CHECK, NOT A CONSTANT MESSAGE, on purpose: the day the type space is synthesised these
# markers appear and the warning goes quiet by itself, rather than having to be remembered.
DATA_SPACE_MARKERS = [
    (r'<TypeDesc Type="Cluster" Format="inline" Label="udf">',
     "the VCTP carries no data-space cluster"),
    (r'<Section Index="2" IndexShift="8"', "TM80 has no second section"),
    (r'<DataFill TypeID="6">', "DFDS has no front-panel DCO table"),
]


def check_data_space(text: str):
    """Which markers of a loadable control's data space are absent."""
    return [why for pattern, why in DATA_SPACE_MARKERS if not re.search(pattern, text)]


# What a LabVIEW-authored class private data control carries, measured 2026-08-27 against a
# CONTROL CASE: a class built by hand in the IDE whose private data holds the very same five
# fields as the generated one, so every difference is a difference in how the file was made
# rather than in what it contains.
#
# THE REFERENCE MATTERS AS MUCH AS THE VALUES.  A first attempt took these off NI's shipped
# `Circle Message.lvclass` and got InStBit23 wrong - that class disagrees with a freshly authored
# one on exactly that bit, presumably a LabVIEW-version difference, and it has different fields
# so nothing else could be held constant either.  Re-measure against a same-content class made by
# the LabVIEW in front of you, not against whatever example is to hand.
GOLDEN_FLAGS = {
    "Instrument": {"Type": "Control", "InStBit13": "0", "InStBit23": "0"},
    "Execution": {"TypeDefVI": "1", "StrictTypeDefVI": "1", "PropTypesIssues": "0"},
    "Execution2": {"InlinableDiagram": "0", "IsPrivateDataForUDClass": "1",
                   "DefaultErrorHandling": "0", "SourceOnly": "1"},
}


def check_flags(text: str, main_xml: pathlib.Path) -> None:
    """Refuse to write a control whose flags differ from a LabVIEW-authored one."""
    wrong = []
    for element, wanted in GOLDEN_FLAGS.items():
        found = re.search(r"<" + element + r"\b([^>]*)>", text)
        if not found:
            sys.exit(f"{main_xml.name}: no <{element}> element - this is not a VI bundle.")
        for attribute, value in wanted.items():
            got = re.search(re.escape(attribute) + r'="([^"]*)"', found.group(1))
            if got is None or got.group(1) != value:
                wrong.append(f"{element}.{attribute} is "
                             f"{got.group(1) if got else 'absent'!r}, expected {value!r}")

    if wrong:
        sys.exit("  ABORT: the patched control does not match a LabVIEW-authored private data "
                 "control:\n    " + "\n    ".join(wrong) +
                 "\n  Writing it anyway produces a class LabVIEW loads and reports normally while "
                 "its private data does not compile - which then breaks every accessor. Measured; "
                 "see docs/lvclass-creation.md.")


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
