#!/usr/bin/env python3
"""aixml_lint.py - offline linter for AIXML documents.

Finds the faults that are decidable WITHOUT LabVIEW, so that a document can be
released for generation without spending a round trip to learn it was wrong.
No LabVIEW, no MCP, no network, Python 3 standard library only.

    python aixml_lint.py <file.xml> [...]     human readable
    python aixml_lint.py --json <file.xml>    machine readable

Exit code 1 when at least one finding is an error, 0 otherwise.

WHY THESE CHECKS. Measured on one session: ~24 of ~80 tool calls were corrective,
and three fault classes were offline-decidable while neither lvai_validate_aixml
nor lvai_check_aixml reported them:

  A  uid_parent naming an element that EXISTS but is the wrong one. Nothing
     reports it - LabVIEW silently puts the element on the diagram it names, so a
     CaseFrame ends up in a different Case Structure and validate answers
     errorCode 0. lvai_check_aixml only asks whether uid_parent names *some*
     element, which it does.
  B  a net that is consumed but never produced. LabVIEW says
     "<Element>: Contains unwired or bad terminal" and names the CONSUMER, which
     sends you to the wrong end of the wire.
  C  a net that is produced but never consumed. Legitimate on its own, so a
     warning - but a mistyped net name shows up as a B and a C together, and that
     pair is reported as one finding with the likely intended name.

THE PARSING TRAP, and it must be solved before anything else works. In
inputs/outputs/fields, ":" separates a terminal name from a net name and "," the
entries, so those characters are escaped inside names: \\3A colon, \\2C comma,
\\0A newline, \\5C backslash. Real examples: the Select primitive's output is
"s? t\\3Af"; a Property Node carries fields="write+Front Panel Window\\3AState";
terminal names read "error in (no error)" and "max queue size (-1\\2C unlimited)".

Note what the trap actually is: the escape REPLACES the character, so the raw
attribute value holds no literal colon for those names and a plain split is
already correct. What breaks is the ORDER - unescape first and "s? t\\3Af" becomes
"s? t:f", which then splits into two phantom entries. So: split, THEN unescape.
The splitter below is escape-aware anyway, which costs three lines and makes the
intent legible instead of load-bearing on that argument.

A net name is an OPAQUE TOKEN. The uid.terminal convention is only a convention -
"dec.Do_Write" and "banana.value" are both valid - so net names are never
decomposed here, only compared.
"""

from __future__ import annotations

import argparse
import collections
import difflib
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

# --------------------------------------------------------------------------
# Vocabulary
# --------------------------------------------------------------------------

#: Elements that can lexically contain other elements. A uid_parent may only name
#: one of these (or "root"). "Node" is in the list because a disable structure is
#: a Node whose children are Diagram elements.
CONTAINER_TAGS = frozenset({"VI", "Structure", "CaseFrame", "Diagram", "ShiftReg", "Node"})

#: Attributes holding a "terminal:net,terminal:net" list. The direction is taken
#: from the ATTRIBUTE and not from the element kind, which is what makes one rule
#: cover every shape: Control/Constant carry outputs only, Indicator inputs only,
#: Node/Call both, and a Tunnel carries whichever of the two its position implies
#: - inputs at structure level for an In tunnel, outputs inside the frame, the
#: mirror image for an Out tunnel, and BOTH in the single element a loop tunnel
#: is written as. Same for a shift register: Left feeds the loop (outputs) and is
#: fed by the initialiser (inputs); Right is fed from inside (inputs) and its
#: output net leaves the loop on its own (outputs).
LIST_ATTR_DIRECTION = {"outputs": "produce", "inputs": "consume"}

#: Attributes holding ONE bare net name rather than a terminal:net list.
#: count names the loop's own "i" output net - it does NOT wire N, maxin does.
#: maxout is the mirror of maxin and is empty in every document measured here;
#: it is treated as a producer so that an unknown direction cannot manufacture a
#: false "consumed but never produced".
BARE_NET_ATTRS = {
    ("Structure", "count"): "produce",
    ("Structure", "maxin"): "consume",
    ("Structure", "maxout"): "produce",
    ("Structure", "selectin"): "consume",
    ("CaseFrame", "selectout"): "produce",
}

#: uid values below this are inside the range LabVIEW reserves for its own panel heap, and
#: generating one logs `HeapObjMapImpl.cpp(226) : DWarn 0xBB613420: trying to override with
#: non-reserved UID` - one event per element, at VALIDATE time, before anything is written.
#:
#: THE CEILING IS NOT 42, AND IT MOVES. This was 42 until 2026-09-09, taken from the first
#: DWarn anyone looked at. The warning names the live ceiling as `max: N sat: N`, and N GROWS
#: with the panel heap as a session runs. Counted over the 132 blocks of one full build:
#:
#:     max: 42  32x     max: 55  30x     max: 71  22x     max: 81  20x
#:     max: 69   8x     max: 95   6x     max: 87/88/91/79/77  2x each
#:     max: 129  2x     max: 163  2x
#:
#: Range 42 to 163, and **100 of 132 blocks - 76 % - had a ceiling above 42**. A document with
#: uids between 50 and 160 therefore passed this check and warned anyway, the more likely the
#: longer LabVIEW had been up. That also explains why the shipped helpers "log nothing": they
#: are generated early, when the ceiling is still low.
#:
#: 5000 is used instead because it is MEASURED clear: a ten-VI build numbered from 5000 - 12
#: pylabview rebuilds, 20+ runs, nine copies of a 43 kB VI - produced ZERO of these events,
#: while the same documents at uids 10-30 produced one per element.
LOW_UID_CEILING = 5000

#: uid="0" is a SENTINEL, not a number: it may be reused within one document and
#: LabVIEW assigns the element an id of its own. Excluded from the duplicate and
#: low-uid checks.
SENTINEL_UID = "0"

_ESCAPE_RE = re.compile(r"\\([0-9A-Fa-f]{2})")
_INT_RE = re.compile(r"^-?\d+$")
_HEX2_RE = re.compile(r"[0-9A-Fa-f]{2}")
BACKSLASH = chr(92)

#: How AIXML actually spells an enum: an integer type carrying its item list, e.g.
#: uint8{Init,Wait Card,Main Menu}. Measured over 52 proven documents - `enum{...}` does NOT
#: occur and was this module's own bug: the check below tested `type.startswith("enum")` and
#: was therefore DEAD CODE on real files, firing only for the hand-written fixture that had
#: agreed with it. The repository's own rule, learned three times: a tool tested against a
#: plausible fixture is not tested.
_ENUM_TYPE_RE = re.compile(r"^u?int(?:8|16|32|64)\{(.*)\}$", re.DOTALL)


def unescape(text: str) -> str:
    """Turn AIXML's \\XX hex escapes back into the characters they stand for."""
    return _ESCAPE_RE.sub(lambda m: chr(int(m.group(1), 16)), text)


def split_unescaped(text: str, sep: str, maxsplit: int = -1) -> list[str]:
    """Split on separators that are not inside a \\XX escape sequence.

    Walks the string treating a backslash plus two hex digits as one atomic unit,
    so a separator can never be taken from inside an escape. Call this on the RAW
    attribute value and unescape the pieces afterwards - never the other way
    round, which is the trap this module's docstring describes.
    """
    parts: list[str] = []
    buf: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "\\" and _ESCAPE_RE.match(text, i):
            buf.append(text[i : i + 3])
            i += 3
            continue
        if ch == sep and (maxsplit < 0 or len(parts) < maxsplit):
            parts.append("".join(buf))
            buf = []
            i += 1
            continue
        buf.append(ch)
        i += 1
    parts.append("".join(buf))
    return parts


def split_top_level(text: str, sep: str) -> list[str]:
    """Split on separators that are OUTSIDE every {...} group, and outside escapes.

    A type string nests: `cluster{uint16{Dark Roast,Decaf}.Kind,string.Name}` has commas at
    two depths and only the outer one separates fields. Splitting without counting braces
    tore the enum labels apart and reported the fragments as unknown base types - 1166
    findings over 668 of LabVIEW's own exports, nearly all of them this one bug.
    """
    parts: list[str] = []
    buf: list[str] = []
    depth = 0
    i = 0
    while i < len(text):
        ch = text[i]
        if ch == "\\" and _ESCAPE_RE.match(text, i):
            buf.append(text[i : i + 3])
            i += 3
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
        elif ch == sep and depth == 0:
            parts.append("".join(buf))
            buf = []
            i += 1
            continue
        buf.append(ch)
        i += 1
    parts.append("".join(buf))
    return parts


def parse_terminal_list(value: str) -> list[tuple[str, str]]:
    """Parse a "terminal:net,terminal:net" attribute into (terminal, net) pairs.

    The net may be empty, which means the terminal is deliberately unwired.
    Splitting on the FIRST unescaped colon is what keeps a terminal name whose own
    colon is escaped - "s? t\\3Af" - in one piece.
    """
    pairs: list[tuple[str, str]] = []
    for entry in split_unescaped(value, ","):
        if not entry.strip():
            continue
        halves = split_unescaped(entry, ":", maxsplit=1)
        if len(halves) == 1:
            pairs.append((unescape(halves[0]), ""))
        else:
            pairs.append((unescape(halves[0]), unescape(halves[1])))
    return pairs


# --------------------------------------------------------------------------
# Findings
# --------------------------------------------------------------------------

SEVERITY_ORDER = {"error": 0, "warning": 1, "info": 2}


class Finding:
    __slots__ = ("severity", "code", "uid", "element", "path", "message")

    def __init__(self, severity, code, uid, element, path, message):
        self.severity = severity
        self.code = code
        self.uid = uid
        self.element = element
        self.path = path
        self.message = message

    def as_dict(self) -> dict:
        return {
            "severity": self.severity,
            "check": self.code,
            "uid": self.uid,
            "element": self.element,
            "path": self.path,
            "message": self.message,
        }


class _El:
    """One element plus the lexical facts the checks need."""

    __slots__ = ("el", "tag", "uid", "uid_parent", "name", "lexical_parent", "path")

    def __init__(self, el, lexical_parent, path):
        self.el = el
        self.tag = el.tag
        self.uid = el.get("uid")
        self.uid_parent = el.get("uid_parent")
        self.name = el.get("_name") or el.get("target") or el.get("_id") or ""
        self.lexical_parent = lexical_parent
        self.path = path

    def label(self) -> str:
        bits = [self.tag]
        if self.name:
            bits.append(repr(self.name))
        return " ".join(bits)


def _walk(root) -> list[_El]:
    """Flatten the document, recording each element's LEXICAL parent and path."""
    out: list[_El] = []
    counter: dict[int, collections.Counter] = {}

    def rec(el, parent, path):
        out.append(_El(el, parent, path))
        me = out[-1]
        counter[id(el)] = collections.Counter()
        for child in el:
            counter[id(el)][child.tag] += 1
            idx = counter[id(el)][child.tag]
            uid = child.get("uid")
            step = child.tag
            if uid is not None:
                step += f"[uid={uid}]"
            else:
                step += f"[{idx}]"
            rec(child, me, f"{path}/{step}")

    rec(root, None, root.tag)
    return out


# --------------------------------------------------------------------------
# The checks
# --------------------------------------------------------------------------


def check_parents(elements: list[_El]) -> list[Finding]:
    """A - uid_parent must be the element it is lexically nested in."""
    findings: list[Finding] = []
    by_uid: dict[str, _El] = {}
    for e in elements:
        if e.uid is not None and e.uid != SENTINEL_UID and e.uid not in by_uid:
            by_uid[e.uid] = e

    for e in elements:
        if e.uid_parent is None:
            continue
        parent = e.lexical_parent
        if parent is None:
            continue
        expected = "root" if parent.tag == "VI" else parent.uid
        if expected is None:
            continue
        if e.uid_parent == expected:
            continue

        named = by_uid.get(e.uid_parent)
        if e.uid_parent != "root" and named is None:
            findings.append(
                Finding(
                    "error",
                    "parent-dangling",
                    e.uid,
                    e.label(),
                    e.path,
                    f'uid_parent="{e.uid_parent}" names no element in this document; '
                    f"LabVIEW puts the element on the TOP-LEVEL diagram and reports "
                    f'nothing. It is nested in {parent.label()}, so it must say "{expected}".',
                )
            )
        elif named is not None and named.tag not in CONTAINER_TAGS:
            findings.append(
                Finding(
                    "error",
                    "parent-not-a-container",
                    e.uid,
                    e.label(),
                    e.path,
                    f'uid_parent="{e.uid_parent}" names a {named.tag}, which cannot hold '
                    f"children. It is nested in {parent.label()}, so it must say "
                    f'"{expected}".',
                )
            )
        else:
            target = f"{named.label()}" if named else "the top-level diagram"
            findings.append(
                Finding(
                    "error",
                    "parent-mismatch",
                    e.uid,
                    e.label(),
                    e.path,
                    f'uid_parent="{e.uid_parent}" points at {target}, but the element is '
                    f'lexically nested in {parent.label()} (uid="{expected}"). LabVIEW '
                    f"follows uid_parent and puts it on the WRONG diagram without "
                    f"reporting anything.",
                )
            )
    return findings


def collect_nets(elements: list[_El]):
    """Build the document-wide producer and consumer maps.

    Document-wide and NOT per scope, on purpose: an input tunnel is implicit, so a
    node inside a Case frame may read a root net directly and LabVIEW creates the
    tunnel itself (measured, and executed). A scope-by-scope analysis reports
    those as unproduced and is wrong.
    """
    produced: dict[str, list[tuple[_El, str, str]]] = collections.defaultdict(list)
    consumed: dict[str, list[tuple[_El, str, str]]] = collections.defaultdict(list)

    for e in elements:
        for attr, direction in LIST_ATTR_DIRECTION.items():
            raw = e.el.get(attr)
            if raw is None:
                continue
            for terminal, net in parse_terminal_list(raw):
                if not net:
                    continue  # deliberately unwired
                bucket = produced if direction == "produce" else consumed
                bucket[net].append((e, attr, terminal))

        for (tag, attr), direction in BARE_NET_ATTRS.items():
            if e.tag != tag:
                continue
            raw = e.el.get(attr)
            if raw is None:
                continue
            net = unescape(raw).strip()
            if not net:
                continue
            bucket = produced if direction == "produce" else consumed
            bucket[net].append((e, attr, attr))

    return produced, consumed


def check_nets(produced, consumed) -> list[Finding]:
    """B and C - and the typo pair the two of them make together."""
    findings: list[Finding] = []
    def _self_named(net: str) -> bool:
        """LabVIEW's exporter spells an UNWIRED terminal as a net named after the consuming
        element's own uid - `<Indicator uid="121" inputs="value:121.value"/>` in a document
        that has no diagram content at all. Authoring spells the same thing as an empty net,
        `value:`. Measured over 668 cached exports: this convention alone accounted for 42 of
        the 73 remaining net errors, every one of them a VI LabVIEW itself wrote. A net that
        names its own consumer and has no producer feeds nothing and means nothing, so it is
        dropped rather than reported in either dialect."""
        return any(
            e.uid and net.startswith(e.uid + ".") for e, _attr, _term in consumed[net]
        )

    unproduced = sorted(
        net for net in consumed if net not in produced and not _self_named(net)
    )
    unconsumed = sorted(net for net in produced if net not in consumed)
    paired: set[str] = set()

    for net in unproduced:
        readers = consumed[net]
        where = ", ".join(f"{e.label()} {attr}:{term}" for e, attr, term in readers[:3])
        suggestions = difflib.get_close_matches(net, unconsumed, n=1, cutoff=0.8)
        if suggestions:
            other = suggestions[0]
            paired.add(other)
            writers = produced[other]
            wsrc = ", ".join(f"{e.label()} {attr}" for e, attr, _ in writers[:3])
            e0 = readers[0][0]
            findings.append(
                Finding(
                    "error",
                    "net-typo",
                    e0.uid,
                    e0.label(),
                    e0.path,
                    f'net "{net}" is read by {where} but nothing writes it, while '
                    f'"{other}" is written by {wsrc} and nothing reads it. One name is '
                    f"mistyped - the two differ by very little.",
                )
            )
        else:
            e0 = readers[0][0]
            findings.append(
                Finding(
                    "error",
                    "net-unproduced",
                    e0.uid,
                    e0.label(),
                    e0.path,
                    f'net "{net}" is read by {where} but no element writes it. LabVIEW '
                    f'reports this as "Contains unwired or bad terminal" against the '
                    f"reader, which points away from the cause.",
                )
            )

    def _mandatory_frame_input(net: str) -> bool:
        """An `In` tunnel inside a CaseFrame is REQUIRED to be declared in every frame,
        used or not - so its net going unread carries no information. Measured on a
        verified ten-VI build: 62 of 78 net-unconsumed warnings were exactly this, all on
        working code. The rule elsewhere says an unused one may be written with an empty
        net; naming it uniformly instead is just as valid and must not be punished."""
        for e, attr, _term in produced[net]:
            if not (
                e.tag == "Tunnel"
                and (e.el.get("_id") or "").startswith("In")
                and e.lexical_parent is not None
                and e.lexical_parent.tag == "CaseFrame"
            ):
                return False
        return True

    for net in unconsumed:
        if net in paired or _mandatory_frame_input(net):
            continue
        writers = produced[net]
        e0 = writers[0][0]
        where = ", ".join(f"{e.label()} {attr}:{term}" for e, attr, term in writers[:3])
        # A Control is a different story from a node output: an unread node output is an
        # everyday convenience, whereas an unread Control means the VI takes an input on its
        # front panel and ignores it. Same severity, sharper sentence.
        if all(e.tag == "Control" for e, _, _ in writers):
            reason = (
                "so the VI accepts this input and never uses it. Deliberate for a panel-only "
                "control; otherwise a wire is missing."
            )
        else:
            reason = (
                "Legitimate for an output nobody needs; suspicious if you meant to wire it "
                "somewhere."
            )
        findings.append(
            Finding(
                "warning",
                "net-unconsumed",
                e0.uid,
                e0.label(),
                e0.path,
                f'net "{net}" is written by {where} but nothing reads it. {reason}',
            )
        )
    return findings


def check_uids(elements: list[_El]) -> list[Finding]:
    """Duplicate uids, and uids inside LabVIEW's reserved range."""
    findings: list[Finding] = []
    seen: dict[str, list[_El]] = collections.defaultdict(list)
    for e in elements:
        if e.uid is None or e.uid == SENTINEL_UID:
            continue
        seen[e.uid].append(e)

    for uid, group in sorted(seen.items()):
        if len(group) > 1:
            where = ", ".join(g.path for g in group)
            findings.append(
                Finding(
                    "error",
                    "uid-duplicate",
                    uid,
                    group[0].label(),
                    group[0].path,
                    f'uid="{uid}" is used by {len(group)} elements ({where}). LabVIEW '
                    f"silently renumbers the duplicates, so a later export stops matching "
                    f"this file and any net or uid_parent naming it becomes ambiguous.",
                )
            )
    # ONE finding per document, not per element. The remedy - renumber - is a property of
    # the document, and reporting it per element buried everything else: 32 772 warnings over
    # 664 of 668 cached exports, 1237 over our own 39 helpers. It is also mostly not the
    # author's business, because in an EXPORT the uids are LabVIEW's own.
    low = sorted(
        (int(e.uid), e) for uid, group in seen.items()
        for e in group[:1] if _INT_RE.match(uid) and int(uid) < LOW_UID_CEILING
    )
    if low:
        nums = [n for n, _ in low]
        first = low[0][1]
        findings.append(
            Finding(
                "warning",
                "uid-low",
                str(nums[0]),
                f"{len(low)} element(s), first {first.label()}",
                first.path,
                f"{len(low)} uid(s) between {nums[0]} and {nums[-1]} are inside the range "
                f"LabVIEW reserves for its own panel heap. If you AUTHORED this, each one logs "
                f'"trying to override with non-reserved UID" at VALIDATE time, before anything '
                f"is written, and LabVIEW assigns its own id anyway - number from "
                f"{LOW_UID_CEILING}, which a whole ten-VI build measured silent. It matters "
                f"because that log saturates at 100 events and lvai_status reports the count as "
                f"a health signal. If this is an EXPORT, the uids are LabVIEW's own and there "
                f"is nothing to do.",
            )
        )

    return findings


def check_terminal_flags(elements: list[_El]) -> list[Finding]:
    """conIdx without connection - LabVIEW reads the omission as required."""
    findings: list[Finding] = []
    for e in elements:
        if e.tag not in ("Control", "Indicator"):
            continue
        # An explicitly required OUTPUT does exactly the same damage as an omitted
        # connection=, and the omission check alone was an arbitrary gap. Measured over 52
        # proven documents: 88 Indicators carry conIdx and every one says "recommended",
        # none says "required" - so flagging it costs no false positive.
        if e.tag == "Indicator" and e.el.get("connection") == "required":
            findings.append(
                Finding(
                    "error",
                    "conn-required-output",
                    e.uid,
                    e.label(),
                    e.path,
                    'connection="required" on an Indicator makes every CALLER that leaves the '
                    "terminal unwired non-executable with Error 1003, while this VI itself "
                    'validates, compiles, runs and exports perfectly. Write "recommended". On a '
                    "VI that already exists the fix is {LV.ConnectorPane} SetWireRule(conIdx, 2), "
                    "not a regeneration - that moves no terminal, so no caller changes.",
                )
            )
            continue

        if e.el.get("conIdx") is None or e.el.get("connection") is not None:
            continue
        if e.tag == "Indicator":
            findings.append(
                Finding(
                    "error",
                    "conn-missing-output",
                    e.uid,
                    e.label(),
                    e.path,
                    "an Indicator with conIdx and no connection= is read as REQUIRED, "
                    "which is never right for an output: every caller that leaves the "
                    "terminal unwired becomes non-executable with Error 1003, while this "
                    "VI itself validates, compiles, runs and exports perfectly. Write "
                    'connection="recommended".',
                )
            )
        else:
            findings.append(
                Finding(
                    "info",
                    "conn-missing-input",
                    e.uid,
                    e.label(),
                    e.path,
                    "a Control with conIdx and no connection= is read as REQUIRED. That "
                    "is a legitimate choice for an input, but say so explicitly rather "
                    "than inheriting it.",
                )
            )
    return findings


def check_value_escapes(elements: list[_El]) -> list[Finding]:
    """A backslash in a `value` is an escape INTRODUCER and must be a \\XX pair.

    A raw one is `Error 42, values in the input are not escaped correctly` - and it is a
    WHOLE-FILE refusal, so one unescaped separator loses every VI in that document. It has
    shipped inside a tool once, with a unit test that asserted the raw separator.

    SCOPE IS `value` ONLY, and that is measured rather than cautious. Across 52 proven
    documents every backslash in a `value` is a valid \\XX pair (26 of them) - but
    `description` carries three RAW ones in `scripts/lvai_class_names.xml`, a shipped helper
    that generates and runs, where the text quotes a Windows path in prose
    (`vi.lib\\Utility\\traverseref.llb\\...`). So `description` tolerates a raw backslash and
    flagging it there would be a false positive on working code. `outputs`, `inputs`, `fields`
    and `target` carry only valid pairs, but none was ever measured REFUSING a raw one, so they
    stay out too.
    """
    findings: list[Finding] = []
    for e in elements:
        raw = e.el.get("value")
        if not raw:
            continue
        i = 0
        while True:
            i = raw.find(BACKSLASH, i)
            if i < 0:
                break
            if raw[i + 1 : i + 2] == BACKSLASH:
                i += 2
                continue
            if not _HEX2_RE.fullmatch(raw[i + 1 : i + 3]):
                findings.append(
                    Finding(
                        "error",
                        "value-raw-backslash",
                        e.uid,
                        e.label(),
                        e.path,
                        f'value="{raw[:60]}" contains a backslash that is not a \\XX escape. '
                        f"A path separator must be written \\5C. LabVIEW refuses the WHOLE "
                        f"FILE with Error 42, so this one character loses every VI in this "
                        f"document.",
                    )
                )
                break  # one finding per element is enough to send you to the attribute
            i += 1
    return findings


def check_case_tunnels(elements: list[_El]) -> list[Finding]:
    """Every OUT tunnel of a case structure must appear in every frame.

    An output tunnel a frame does not assign leaves that case's output unwired, which breaks
    the VI. An orphan `_id` inside a frame - one the structure does not declare - dangles.

    THE IN DIRECTION IS DELIBERATELY NOT CHECKED, and that is a correction. The rule as
    written elsewhere is "every frame must declare every tunnel", and measured over 52 proven
    documents that is too strong: `scripts/lvai_add_class_method.xml` and
    `scripts/lvlu_add_test_method.xml` both have a frame that declares only `Out1` and omits
    `In1`-`In3`, and both are shipped helpers that run. Not one document anywhere omits an
    `Out` tunnel. So an unused IN tunnel may be left out of a frame; an OUT one may not.

    THE DEFAULT FRAME IS NOT CHECKED EITHER, for the same reason. "Always add a Default frame"
    would fire on all 8 case structures in those documents - none has one, including two whose
    selectors are plain strings - because a fully covered enum needs no Default. A rule that is
    wrong on every proven instance is not a rule.
    """
    findings: list[Finding] = []
    for e in elements:
        if e.tag != "Structure":
            continue
        frames = [c for c in e.el if c.tag == "CaseFrame"]
        if not frames:
            continue

        declared = {c.get("_id") for c in e.el if c.tag == "Tunnel" and c.get("_id")}
        required_out = {i for i in declared if i.startswith("Out")}

        for frame in frames:
            inner = {c.get("_id") for c in frame if c.tag == "Tunnel" and c.get("_id")}
            uid = frame.get("uid")
            path = f"{e.path}/CaseFrame[uid={uid}]"
            label = f"CaseFrame {frame.get('selector')!r}"

            missing = sorted(required_out - inner)
            if missing:
                findings.append(
                    Finding(
                        "error",
                        "case-frame-missing-out-tunnel",
                        uid,
                        label,
                        path,
                        f"this frame does not assign {', '.join(missing)}, which the structure "
                        f"declares. An output tunnel left unassigned in one case leaves that "
                        f"case's output unwired and breaks the VI - the other frames assigning "
                        f"it is not enough.",
                    )
                )

            orphan = sorted(i for i in inner if i not in declared)
            if orphan:
                findings.append(
                    Finding(
                        "error",
                        "case-frame-orphan-tunnel",
                        uid,
                        label,
                        path,
                        f"this frame declares {', '.join(orphan)}, which the structure itself "
                        f"does not. A case tunnel exists at both levels or at neither; the "
                        f"inner half alone connects to nothing outside.",
                    )
                )
    return findings


def _load_catalogue() -> tuple[set[tuple[str, str]], set[str]]:
    """The VI Server property catalogue, for the ONE lookup that is safe against its gaps.

    THE CATALOGUE CANNOT VALIDATE AIXML VOCABULARY, measured 2026-09-09 over the 39 shipped
    helpers - all of which generate and run. Of the 240 Property/Invoke references in them,
    **94 are absent from the catalogue**: 55 of 162 properties and 39 of 78 methods, exactly
    half. The cause is that it is keyed by DISPLAY NAME while AIXML wants the scripting
    identifier - `{LV.VI} Set Control Value [Variant]` is listed, `Ctrl Val.Set` is what a
    Call must say and is listed nowhere. Even the class column is short: `{LV.Project}` and
    `{LV.LVClassLibrary}` are used by shipped helpers and missing. So a membership check
    would accuse working code, which is worse than no check at all.
    What IS sound is a POSITIVE lookup, used by check_property_nodes below.
    """
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, os.pardir, "docs", "vi-server-properties.tsv")
    props: set[tuple[str, str]] = set()
    classes: set[str] = set()
    try:
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                parts = line.rstrip("\n").split("\t")
                if len(parts) >= 2 and parts[0].startswith("{"):
                    classes.add(parts[0])
                    props.add((parts[0], parts[1]))
    except OSError:
        return set(), set()  # shipped beside the exe; absent is not an error
    return props, classes


_CATALOGUE_PROPS, _CATALOGUE_CLASSES = _load_catalogue()


def check_property_nodes(elements: list[_El]) -> list[Finding]:
    """Property and Invoke Node vocabulary, limited to what is measurably decidable.

    Measured 2026-09-09 against the real ValidateAIXML:
      link="User Input" on a Property Node  ->  Error 53, "Unrecognized or unsupported
          attribute set in Node "Property Node" with UID 4300". `link` is in the generated
          attribute list but an implicit/linked property node is not authorable.
      fields="read+Label\\3AText" on {LV.Control}  ->  "Property Node: Invalid property",
          PLUS a cascade onto innocent nodes ("the type of the source is void").
      fields="read+Label.Text" on {LV.Control}  ->  errorCode 0.
    So a nested property IS authorable and the separator is a DOT. The report this was built
    from concluded "two nodes, no nested property"; the two-node chain also validates, but it
    is not required.
    """
    findings: list[Finding] = []
    for e in elements:
        if e.tag != "Node" or e.name not in ("Property Node", "Invoke Node"):
            continue

        # NO CHECK ON link=. It was a warning until 2026-09-09 and it was simply wrong:
        # link= is the WORKING way to bind an implicit Property Node to a front-panel
        # control, and a verified ten-VI build uses it 28 times -
        # `link="Card Simulator" type="{LV.Boolean}" fields="write+Strings[4]"` with the
        # error wire threaded through. Two probes of mine failed with it, but both had
        # invented a control name and class that did not match anything on the pane, so
        # they measured my fixture and not the attribute. LabVIEW's own exporter writes it
        # on 275 nodes besides. A check that flags the documented, working form is worse
        # than no check.

        cls = e.el.get("type")
        for entry in split_unescaped(e.el.get("fields") or "", ","):
            entry = unescape(entry).strip()
            if not entry:
                continue
            if "+" not in entry:
                findings.append(
                    Finding(
                        "error",
                        "propnode-field-format",
                        e.uid,
                        e.label(),
                        e.path,
                        f'fields entry "{entry}" has no direction. LabVIEW requires '
                        f'"direction+Name" - write read+{entry} or write+{entry}.',
                    )
                )
                continue

            name = entry.split("+", 1)[1]
            if not cls or ":" not in name or not _CATALOGUE_PROPS:
                continue
            head = name.split(":", 1)[0]
            # POSITIVE lookups only: the head IS a property, the whole name is NOT. Every
            # legitimate colon name in the shipped helpers has a category prefix for a head -
            # Project, Connector Pane, Front Panel Window, Application, Typedef - and none of
            # those is a property in its own right, so this fired 0 times on 33 real names.
            if (cls, head) in _CATALOGUE_PROPS and (cls, name) not in _CATALOGUE_PROPS:
                findings.append(
                    Finding(
                        "warning",
                        "propnode-nested-colon",
                        e.uid,
                        e.label(),
                        e.path,
                        f'"{name}" on {cls} looks like a nested property written with a '
                        f'COLON. "{head}" is a property of that class in its own right, so '
                        f'the nesting separator is a DOT: write "{head}.{name.split(":", 1)[1]}". '
                        f'The colon form answers "Property Node: Invalid property" and drags '
                        f"innocent nodes into the message with it.",
                    )
                )
    return findings


def check_terminal_lists(elements: list[_El]) -> list[Finding]:
    """An inputs=/outputs= attribute must be absent rather than empty or ragged.

    Measured 2026-09-09: <Node _name="Current VI's Path" inputs="" .../> answers
    `Object terminal not found for input: : on Current VI's Path`, while the same node with
    the attribute simply left off answers errorCode 0.

    THE OTHER THREE SHAPES THE SPECIFICATION LISTED ARE ACCEPTED, and two of them were about
    to be shipped as false positives - `scripts/lvai_add_class_method.xml` and
    `scripts/lvlu_add_test_method.xml` both carry `outputs="reference out:,error out:,"` and
    both run. Probed one shape per file the same day:

        outputs="path:4200.path,"    trailing comma   errorCode 0
        outputs=",path:4200.path"    leading comma    errorCode 0
        outputs="path:4200.path,,"   doubled comma    Error 1, "Only 1 output object
                                                      terminals named ''; cannot connect :"
        outputs="path"               no colon at all  errorCode 0

    So a SINGLE empty entry is tolerated and only a second one is refused - which is why the
    rule below counts them instead of looking for a comma in the wrong place.
    """
    findings: list[Finding] = []
    for e in elements:
        for attr in ("inputs", "outputs"):
            raw = e.el.get(attr)
            if raw is None:
                continue
            if not raw.strip():
                findings.append(
                    Finding(
                        "error",
                        "terminal-list-empty",
                        e.uid,
                        e.label(),
                        e.path,
                        f'{attr}="" is not the same as leaving the attribute off. LabVIEW '
                        f'reads it as one nameless terminal and answers "Object terminal not '
                        f'found for input: :". Delete the attribute.',
                    )
                )
                continue
            blanks = [i for i, x in enumerate(split_unescaped(raw, ",")) if not x.strip()]
            if len(blanks) > 1:
                findings.append(
                    Finding(
                        "error",
                        "terminal-list-malformed",
                        e.uid,
                        e.label(),
                        e.path,
                        f'{attr}="{raw}" has {len(blanks)} empty entries (positions '
                        f"{', '.join(str(i + 1) for i in blanks)}) - a doubled comma. LabVIEW "
                        f"answers Error 1, \"Only 1 output object terminals named ''; cannot "
                        f'connect :". A single leading or trailing comma is tolerated; a '
                        f"second empty entry is not.",
                    )
                )
    return findings


#: Base type tokens, MEASURED 2026-09-09: one Constant of each in a single probe answered
#: errorCode 0 from the real ValidateAIXML. The set is an allowlist, so a base type LabVIEW
#: accepts but this list omits would be a false positive - add it rather than weakening the
#: check. Nothing outside this set appears as a base token anywhere in the shipped helpers.
BASE_TYPES = frozenset({
    "bool", "string", "path", "double", "single",
    "int8", "int16", "int32", "int64",
    "uint8", "uint16", "uint32", "uint64",
    "variant", "timestamp",
    # Seen only in LabVIEW's OWN exports, never authored: an unwired pane slot is
    # `void`, and a VI refnum carries its pane as `function{...}`.
    "void", "extended", "single", "complexsingle", "complexdouble", "complexextended",
    "picture", "dynamicdata", "doublewaveform", "digitalwaveform", "digitaltable",
    "set", "map", "fixed",
})

_IDENT_RE = re.compile(r"[A-Za-z][A-Za-z0-9_]*")


class _TypeParser:
    """Recursive descent over the type grammar of aixml-reference.md section 5.

    Enum label lists, ref kinds and {LV.Class} bodies are OPAQUE: their contents are names,
    not type tokens, and treating them as tokens is how a naive scan "finds" 30 unknown types
    in a corpus that works.
    """

    def __init__(self, text: str):
        self.s = text
        self.i = 0
        self.errors: list[str] = []
        #: Problems that cannot be errors because a NAME may contain a brace. Measured over
        #: 668 of LabVIEW's own exports: `Generalized Fourier Spectrum.vi` has a cluster field
        #: called `Power Spectrum FFT {X}` and `Continuous Serial Write and Read.vi` an ASCII
        #: enum whose labels include `{` and `}`. Only `}` is escaped, as D - `{` is written
        #: raw - so no brace counter can tell a name from a nesting level.
        self.soft: list[str] = []

    def parse(self) -> list[str]:
        self.term()
        if not self.errors and self.i < len(self.s):
            self.errors.append(f"unexpected trailing text {self.s[self.i:]!r}")
        return self.errors

    # -- helpers
    def _at(self, ch: str) -> bool:
        return self.i < len(self.s) and self.s[self.i] == ch

    def _brace_body(self, what: str) -> str | None:
        """Consume a {...} group and return its body, or record an imbalance."""
        if not self._at("{"):
            self.errors.append(f"expected '{{' after {what}")
            return None
        depth = 0
        start = self.i + 1
        while self.i < len(self.s):
            if self.s[self.i] == "{":
                depth += 1
            elif self.s[self.i] == "}":
                depth -= 1
                if depth == 0:
                    body = self.s[start : self.i]
                    self.i += 1
                    return body
            self.i += 1
        self.soft.append(f"unbalanced braces after {what}")
        self.i = len(self.s)  # the rest is unparseable; do not manufacture follow-on errors
        return None

    def _instance_suffix(self) -> None:
        """A trailing .Name after a term names the INSTANCE, not the type."""
        if self._at("."):
            self.i += 1
            while self.i < len(self.s) and self.s[self.i] not in ",}":
                self.i += 1

    # -- grammar
    def term(self) -> None:
        if self.i >= len(self.s):
            self.errors.append("empty type")
            return

        if self._at("{"):  # a bare class reference, {LV.Control}
            body = self._brace_body("'{'")
            if body is not None and not body.strip():
                self.errors.append("empty class reference {}")
            self._instance_suffix()
            return

        m = _IDENT_RE.match(self.s, self.i)
        if not m:
            self.errors.append(f"expected a type name at {self.s[self.i:][:20]!r}")
            self.i = len(self.s)
            return
        head = m.group(0)
        self.i = m.end()

        if head == "array":
            if self._at(".") and _IDENT_RE.match(self.s, self.i + 1) is None:
                j = self.i + 1  # array.N - the rank is an infix, not an instance name
                while j < len(self.s) and self.s[j].isdigit():
                    j += 1
                if j > self.i + 1:
                    self.i = j
            body = self._brace_body("'array'")
            if body is not None:
                self._sub(body, "array element")
        elif head == "cluster":
            body = self._brace_body("'cluster'")
            if body is not None:
                for field in split_top_level(body, ","):
                    if field.strip():
                        self._sub(field, "cluster field")
        elif head == "ref":
            kind = self._brace_body("'ref'")
            if kind is not None and self._at("{"):
                payload = self._brace_body("'ref' payload")
                if payload is not None:
                    self._sub(payload, "ref payload")
        elif head in ("tag", "function"):
            # tag{14} is an IO name; function{...} is an exported pane signature - both
            # carry numbers and nested type lists that are not ours to validate.
            self._brace_body(f"'{head}'")
        elif head in BASE_TYPES:
            if self._at("{"):
                self._brace_body(f"enum {head}")  # labels are opaque
        else:
            self.errors.append(
                f"unknown base type {head!r}"
                + (f" - did you mean {_nearest(head)!r}?" if _nearest(head) else "")
            )
            # do not try to parse further: everything after is suspect
            self.i = len(self.s)
            return

        self._instance_suffix()

    def _sub(self, text: str, what: str) -> None:
        inner = _TypeParser(text).parse()
        for e in inner:
            self.errors.append(f"in {what} {text!r}: {e}")


def _nearest(token: str) -> str | None:
    hit = difflib.get_close_matches(token, sorted(BASE_TYPES), n=1, cutoff=0.7)
    return hit[0] if hit else None


def check_type_grammar(elements: list[_El]) -> list[Finding]:
    """Parse every type= against the grammar, naming the SUBSTRING that is wrong.

    Measured 2026-09-09 against the real ValidateAIXML:
      cluster{int82.Record Index,string.Name}  ->  Error 53, "Unrecognized or unsupported
          attribute set in Constant with UID 4200" - the element, not the attribute, and
          nothing about where in the type string. Finding it cost a bisection probe.
      cluster{int32.Record Index,string.Name}  ->  errorCode 0. The control isolates the
          one token as the cause.
      array{array{double}}                     ->  Error 53, same shapeless message.
      array{string                             ->  Error 53.
    NOT flagged, because it was measured VALID: cluster{bool,int32}, a field with no .Name,
    answers errorCode 0. The specification this was built from called it an error.
    """
    findings: list[Finding] = []
    for e in elements:
        raw = e.el.get("type")
        if not raw:
            continue

        if "array{array{" in raw.replace(" ", ""):
            findings.append(
                Finding(
                    "error",
                    "array-nested",
                    e.uid,
                    e.label(),
                    e.path,
                    f'type="{raw}" nests one array inside another. A multidimensional array '
                    f"is written array.N{{...}} with the rank as an infix; the nested form is "
                    f"refused with an Error 53 that names only the element.",
                )
            )
            continue

        parser = _TypeParser(raw)
        hard = parser.parse()
        for problem in parser.soft:
            findings.append(
                Finding(
                    "warning", "type-braces", e.uid, e.label(), e.path,
                    f'type="{raw}" does not brace-balance: {problem}. A warning, not an error: '
                    f"a field name or enum label may contain a raw '{{' while '}}' is escaped "
                    f"as \7D, so this is unprovable from the text - but an authored type that "
                    f"really is unbalanced is refused with Error 53.",
                )
            )
        for problem in hard:
            findings.append(
                Finding(
                    "error",
                    "type-grammar",
                    e.uid,
                    e.label(),
                    e.path,
                    f'type="{raw}" does not parse: {problem}. LabVIEW answers Error 53, '
                    f'"Unrecognized or unsupported attribute set in {e.tag} with UID {e.uid}" '
                    f"- naming the element but neither the attribute nor the place in the "
                    f"string.",
                )
            )
    return findings


def check_types(elements: list[_El]) -> list[Finding]:
    """Enum defaults given as labels, and the nested multidimensional array form."""
    findings: list[Finding] = []
    for e in elements:
        type_ = e.el.get("type")
        if not type_:
            continue

        enum_match = _ENUM_TYPE_RE.match(type_)
        if enum_match:
            value = (e.el.get("value") or "").strip()
            if not value:
                continue
            items = [unescape(x) for x in split_unescaped(enum_match.group(1), ",")]
            if not _INT_RE.match(value):
                hint = ""
                if value in items:
                    hint = f' Its index is {items.index(value)}.'
                findings.append(
                    Finding(
                        "error",
                        "enum-label",
                        e.uid,
                        e.label(),
                        e.path,
                        f'value="{value}" is an enum LABEL, not an index. LabVIEW '
                        f"DISCARDS it and writes 0, with no message at validate, convert "
                        f"or run - so the VI silently runs the wrong case.{hint}",
                    )
                )
            elif items and int(value) >= len(items):
                findings.append(
                    Finding(
                        "warning",
                        "enum-out-of-range",
                        e.uid,
                        e.label(),
                        e.path,
                        f'value="{value}" is past the last of {len(items)} items. LabVIEW '
                        f"CLAMPS an enum to {len(items) - 1} and reports nothing. A warning "
                        f"rather than an error because a RING may legitimately hold a value "
                        f"outside its item list, and no Ring occurs in the 52 documents this "
                        f"was measured against - so whether the two share this spelling is "
                        f"unknown.",
                    )
                )
    return findings


# --------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------


def lint_file(path: str) -> list[Finding]:
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as exc:
        return [Finding("error", "not-well-formed", None, "", "", f"{path} is not well-formed XML: {exc}")]

    elements = _walk(root)

    # An empty <VI/> is not a clean document, it is an unexamined one - and answering "clean"
    # for it is the same false bill of health this tool exists to prevent elsewhere.
    # Measured 2026-09-09 on one of NI's own shipped controls, so anyone can repeat it:
    # ConvertVIToAIXML on `vi.lib\silver_ctls\IO\DAQmx Task Name NI_Silver.ctl` returns 58
    # bytes, `<VI _name="DAQmx Task Name NI_Silver.ctl" description=""/>`, with
    # errorCode 0 - no cluster, no field, no name. NI lists .ctl as unsupported for authoring
    # and reading one is equally empty. It is not only controls: 4 of 668 cached exports are
    # empty too, all of them Tools-menu VIs (DQMH's Add New DQMH Module.vi and friends).
    if len(elements) <= 1:
        return [
            Finding(
                "warning",
                "empty-document",
                None,
                root.get("_name") or root.tag,
                root.tag,
                "this document has no diagram content, so NOTHING below was checked - a clean "
                "result here means nothing. A .ctl exports to AIXML as an empty <VI/> with "
                "errorCode 0, and so does a Tools-menu VI. Read a .ctl with pylv_extract or "
                "lvai_describe_ctl, which carry the whole heap; AIXML cannot express one.",
            )
        ]

    findings: list[Finding] = []
    findings += check_parents(elements)
    produced, consumed = collect_nets(elements)
    findings += check_nets(produced, consumed)
    findings += check_uids(elements)
    findings += check_terminal_flags(elements)
    findings += check_value_escapes(elements)
    findings += check_case_tunnels(elements)
    findings += check_type_grammar(elements)
    findings += check_property_nodes(elements)
    findings += check_terminal_lists(elements)
    findings += check_types(elements)
    findings.sort(key=lambda f: (SEVERITY_ORDER[f.severity], f.code, f.uid or ""))
    return findings


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(
        description="Lint AIXML documents offline, before any LabVIEW or MCP call."
    )
    ap.add_argument("files", nargs="+", help="AIXML .xml files to check")
    ap.add_argument("--json", action="store_true", dest="as_json", help="machine-readable output")
    ap.add_argument(
        "--min-severity",
        choices=("error", "warning", "info"),
        default="warning",
        help="hide findings below this severity. Default WARNING, so the info-level "
        "uid-low is off unless asked for: it fires 392 times on the shipped helpers, and "
        "acting on it is what one session measured as harmful - renumbering 12 uids to "
        "silence it introduced a type typo that cost a bisection probe to find. Pass "
        "--min-severity info to see it.",
    )
    args = ap.parse_args(argv)

    floor = SEVERITY_ORDER[args.min_severity]
    results = {
        path: [f for f in lint_file(path) if SEVERITY_ORDER[f.severity] <= floor]
        for path in args.files
    }
    errors = sum(1 for fs in results.values() for f in fs if f.severity == "error")

    if args.as_json:
        payload = {
            "ok": errors == 0,
            "errors": errors,
            "files": [
                {"file": path, "findings": [f.as_dict() for f in fs]}
                for path, fs in results.items()
            ],
        }
        print(json.dumps(payload, indent=2))
        return 1 if errors else 0

    total = collections.Counter()
    for path, findings in results.items():
        counts = collections.Counter(f.severity for f in findings)
        total.update(counts)
        summary = ", ".join(f"{n} {sev}" for sev, n in sorted(counts.items())) or "clean"
        print(f"\n{path}  [{summary}]")
        for f in findings:
            uid = f"uid={f.uid}" if f.uid else "uid=-"
            print(f"  {f.severity.upper():7} {f.code:24} {uid:12} {f.element}")
            print(f"          {f.message}")
            print(f"          at {f.path}")

    print(
        f"\n{len(results)} file(s): "
        f"{total['error']} error(s), {total['warning']} warning(s), {total['info']} info"
    )
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
