"""Retune a Timed Loop's timing in a pylabview-extracted heap.

WHY THIS IS THE ONLY ROUTE THAT WORKS. A Timed Loop's timing attributes are
WIRED inputs. The value lives in the <ConstValue> of the diagram constant feeding
the terminal - NOT in the terminal's <DefaultData>, which LabVIEW overwrites on
its next save, and NOT in the collapsed node's flattened Timing cluster, which
produces a VI LabVIEW cannot load at all. FINDINGS.md 3.17-3.19 has all three
measurements.

PRECONDITION: the inputs must already be wired, in the IDE, by a person. Adding a
constant and a wire is composition, which pylabview cannot do. That is what the
template beside this script is for.

ConstValue is plain hex text - no MacRoman, no CDATA, no entity escaping, and the
file size does not change. None of the DefaultData encoding traps apply here.

usage: pylv-set-timedloop.py <BDHb.xml> Field=value [Field=value ...]
       pylv-set-timedloop.py <BDHb.xml> --show
"""
import re
import sys
import xml.etree.ElementTree as ET

# uid -> field, for scripts/templates/TimedLoop-all-inputs-wired.vi. The uids come
# from LabVIEW's own AIXML export of that VI, not from field order.
TEMPLATE_UIDS = {
    "Processor": "973",
    "SourceName": "2504",
    "Period": "3498",
    "Deadline": "4248",
    "Offset": "4833",
    "Priority": "5362",
    "Timeout": "5889",
    "Mode": "6586",
}

# Fields whose ConstValue is exactly the integer width AIXML reports.
EXACT = {"Deadline": 8, "Priority": 4, "Timeout": 4, "Mode": 4}

# Period, Offset and Processor carry ONE EXTRA TRAILING byte of unestablished
# meaning. The integer occupies the LEADING bytes and the extra one is preserved.
#
# Measured the hard way: writing the integer into the trailing bytes instead made
# LabVIEW read Period=3 where 1000 was intended - because it takes the first eight
# bytes, and 0x00000000000003E8 shifted right by one byte begins 00..03. LabVIEW's
# own AIXML export is what caught it, which is the whole reason that check exists.
PADDED = {"Period": 8, "Offset": 8, "Processor": 4}


def const_node(root, uid):
    """The bDConstDCO whose ConstValue belongs to the constant AIXML calls `uid`.

    The uid LabVIEW reports in an AIXML export is the DISPLAY object's - the
    <ddo class="stdNum" uid="..."> - while ConstValue sits on its parent
    bDConstDCO, which carries a different uid. Matching on the parent's own uid
    finds nothing; that was the first version's bug.
    """
    for node in root.iter():
        if node.find("ConstValue") is None:
            continue
        if node.get("uid") == uid:
            return node
        if any(child.get("uid") == uid for child in node):
            return node
        if any(d.get("uid") == uid for d in node.iter()):
            return node
    return None


def main():
    path, args = sys.argv[1], sys.argv[2:]
    src = open(path, encoding="utf-8", newline="").read()
    root = ET.fromstring(src)

    if args == ["--show"]:
        # An absent constant is not an error and not a missing feature - it means
        # that input is UNWIRED, which for most of them is the better state. Only a
        # wired input is scriptable, so this doubles as a hygiene report: anything
        # marked "not wired" is also anything LabVIEW is left to default.
        for field, uid in TEMPLATE_UIDS.items():
            node = const_node(root, uid)
            if node is None:
                print(f"  {field:<12} uid={uid:<6} not wired")
            else:
                print(f"  {field:<12} uid={uid:<6} {node.find('ConstValue').text.strip()}")
        return

    for arg in args:
        field, _, value = arg.partition("=")
        if field not in TEMPLATE_UIDS:
            sys.exit(f"unknown field {field!r}; known: {', '.join(TEMPLATE_UIDS)}")
        uid = TEMPLATE_UIDS[field]
        node = const_node(root, uid)
        if node is None:
            sys.exit(f"{field}: not wired in this VI, so there is no ConstValue to "
                     "write. A value only reaches a Timed Loop's input over a wire, and "
                     "adding a constant plus a wire is composition - neither AIXML nor "
                     "pylabview can do it, so this one needs the IDE. Run --show to see "
                     "which inputs are wired.")
        old = node.find("ConstValue").text.strip()
        raw = bytes.fromhex(old)

        if field in EXACT:
            width = EXACT[field]
            if len(raw) != width:
                sys.exit(f"{field}: expected {width} bytes, found {len(raw)} - "
                         "the template changed, refusing to write")
            new = int(value).to_bytes(width, "big", signed=True).hex().upper()
        elif field in PADDED:
            width = PADDED[field]
            # These fields turn up at their natural width OR with one extra trailing
            # byte, and WHY is not established - do not infer it. Measured: within a
            # single bundle (TimedLoop-with-subvi-slot) Period is 8 bytes while Offset
            # is 9, so it is not a per-file property such as "LabVIEW re-saved it";
            # that explanation was written here first and the next measurement killed
            # it. Accept either width and preserve whatever trailing bytes exist.
            if len(raw) not in (width, width + 1):
                sys.exit(f"{field}: expected {width} or {width + 1} bytes, found "
                         f"{len(raw)} - the template changed, refusing to write")
            # integer in the LEADING bytes; keep any trailing byte as found
            new = (int(value).to_bytes(width, "big", signed=True) + raw[width:]).hex().upper()
        else:  # SourceName: 4-byte length then the text
            body = value.encode("mac_roman")
            new = (len(body).to_bytes(4, "big") + body).hex().upper()

        # Unique substitution: anchor on the uid so an identical value elsewhere
        # in the heap cannot be hit by accident.
        pattern = re.compile(
            r'(uid="' + re.escape(uid) + r'".*?<ConstValue>)' + re.escape(old) + r'(</ConstValue>)',
            re.S)
        src, n = pattern.subn(lambda m: m.group(1) + new + m.group(2), src, count=1)
        if n != 1:
            sys.exit(f"{field}: could not place the edit (matched {n} times)")
        print(f"  {field:<12} {old} -> {new}")

    open(path, "w", encoding="utf-8", newline="").write(src)


main()
