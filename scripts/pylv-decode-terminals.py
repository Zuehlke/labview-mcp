"""Decode the Timed Loop config-node terminals out of a pylabview BDHb heap.

TWO ENCODING TRAPS, both of which produced nonsense on the first attempt:

1. pylabview renders these byte strings as **MacRoman**, not latin-1 or UTF-8.
   Byte 0xFF comes back as U+02C7 (caron). Reading it any other way turns a
   Timeout of -1 into gibberish.
2. Bytes with no printable MacRoman form are written as the LITERAL text
   `&#x00;` - six characters - not as an XML character reference. ElementTree
   therefore hands them over unresolved and they must be substituted by hand.
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

ENTITY = re.compile(r"&#x([0-9A-Fa-f]{2});")
LEN_PREFIX = 4  # DefaultData opens with a 4-byte big-endian length/pad word


def to_bytes(text):
    """The DefaultData payload as the raw bytes LabVIEW stored."""
    if text is None:
        return None
    text = text.strip()
    if len(text) >= 2 and text[0] == '"' and text[-1] == '"':
        text = text[1:-1]
    out = bytearray()
    for piece, entity in _split(text):
        if entity is not None:
            out.append(entity)
        else:
            out.extend(piece.encode("mac_roman", errors="replace"))
    return bytes(out)


def _split(text):
    """Yield (literal, None) and (None, byte) in document order."""
    at = 0
    for m in ENTITY.finditer(text):
        if m.start() > at:
            yield text[at:m.start()], None
        yield None, int(m.group(1), 16)
        at = m.end()
    if at < len(text):
        yield text[at:], None


def interpret(raw):
    """Best reading of a payload: counted string, else signed integer."""
    if not raw:
        return "-"
    if len(raw) > LEN_PREFIX:
        n = int.from_bytes(raw[:LEN_PREFIX], "big")
        if n and len(raw) == LEN_PREFIX + n:
            body = raw[LEN_PREFIX:]
            if all(32 <= b < 127 for b in body):
                return f'"{body.decode("ascii")}" (counted string)'
    if len(raw) <= 8:
        signed = int.from_bytes(raw, "big", signed=True)
        note = "  <- unbegrenzt" if raw == b"\xff" * len(raw) else ""
        return f"{signed}{note}"
    return f"{len(raw)} Bytes" + (" (alles 0)" if not any(raw) else "")


def rows(path):
    for dco in ET.parse(path).getroot().iter():
        el = dco.find("englishName")
        if el is None or not (el.text or "").strip():
            continue
        try:
            name = bytes.fromhex(el.text.strip()).decode("mac_roman")
        except ValueError:
            continue
        td = dco.find("typeDesc")
        dd = dco.find("DefaultData")
        raw = to_bytes(dd.text) if dd is not None else None
        yield name, (td.text if td is not None else "-"), raw


def render(label, path):
    print(f"\n=== {label} ===")
    print(f"  {'Terminal':<15}{'typeDesc':<14}{'bytes':<26}{'Wert'}")
    for name, td, raw in rows(path):
        shown = "(kein DefaultData)" if raw is None else (
            raw.hex(" ") if len(raw) <= 8 else raw.hex(" ")[:23] + "…")
        print(f"  {name:<15}{td:<14}{shown:<26}{interpret(raw)}")


if len(sys.argv) < 2:
    sys.exit(
        "usage: pylv-decode-terminals.py <VI_BDHb.xml> [more_BDHb.xml ...]\n"
        "\n"
        "Prints every named terminal in a pylabview block-diagram heap together with\n"
        "the value in its DefaultData. Pass two heaps - one from a VI whose structure\n"
        "has its attributes EXPOSED, one collapsed - to see which fields only exist in\n"
        "the exposed form. See FINDINGS.md 3.16."
    )

for arg in sys.argv[1:]:
    render(os.path.basename(arg), arg)
