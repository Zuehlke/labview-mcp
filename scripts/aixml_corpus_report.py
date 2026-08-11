#!/usr/bin/env python3
"""Mine an AIXML export corpus for the vocabulary a generator has to reproduce.

Input is the directory `LabVIEWMCP --corpus` fills: one .xml per example VI, plus
roundtrip.tsv. Nothing here talks to LabVIEW - the exports are the measurement, this is
only the arithmetic on top of them, so it re-runs in seconds while a sweep takes an hour.

What it answers, in order of how expensive the mistake is:

  signatures.tsv  For every node, the ORDERED terminal lists LabVIEW itself writes. Order is
                  not cosmetic: `Bundle By Name` with `input cluster` first fails validation
                  and with it last succeeds, so the canonical sequence is the thing to copy.
  undocumented.tsv  Nodes used by NI that docs/aixml-reference.md does not name, most
                  frequent first. This is the actual gap list - what a generated VI will get
                  wrong by guessing.
  nodes.tsv       Node frequency, so the gap list can be worked in a useful order.
  attributes.tsv  Attribute vocabulary per element kind, to catch attributes the reference
                  never saw (`elements` on Array To Cluster was one).

Usage:
  python scripts/aixml_corpus_report.py [corpusDir] [--docs docs/aixml-reference.md]
                                        [--out reportDir]
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

DEFAULT_CORPUS = pathlib.Path.home() / "AppData/Local/Temp/lvai-corpus"


def terminals(attr: str) -> list[str]:
    """The terminal names of an `inputs`/`outputs` attribute, in document order.

    Entries are `terminal:net`, comma separated. Both separators are safe: a terminal name
    containing either carries it escaped (`\\3A`, `\\2C`), which is why splitting on the raw
    characters does not corrupt names like `s? t\\3Af`.
    """
    names = []
    for entry in attr.split(","):
        if not entry:
            continue
        names.append(entry.split(":", 1)[0].strip())
    return names


def cell(counter: collections.Counter[str]) -> str:
    """One markdown cell of terminal names, in LabVIEW's own order.

    Two things this has to be honest about. A pipe inside a name would end the cell early, so it
    is escaped. And some nodes have no fixed terminal names at all - a `Local Variable`'s terminal
    is the variable it points at, a `Property Node`'s is the property - so the commonest shape
    there is one VI's spelling and printing it as *the* signature would be a fabrication. Anything
    whose commonest shape covers less than half its sightings is reported as varying instead.
    """
    if not counter:
        return "—"

    shape, count = counter.most_common(1)[0]
    total = sum(counter.values())
    names = [n for n in shape.split(" | ") if n]
    if not names:
        return "—"
    if count / total < 0.5:
        return f"varies per instance ({len(counter)} shapes)"

    escaped = [n.replace("|", "\\|") for n in names]
    text = ", ".join("`" + n + "`" for n in escaped)
    return text if count == total else f"{text} ({count}/{total})"


BEGIN_MARK = "<!-- BEGIN generated: node terminals -->"
END_MARK = "<!-- END generated: node terminals -->"


def curated_text(reference: pathlib.Path) -> str:
    """The reference with its own generated block removed.

    Without this the tool eats its own tail: once the generated table is spliced into the
    document, every node in it counts as "documented", the next run finds nothing to report, and
    the gap list silently empties out. What counts as already-known has to be the text a person
    wrote.
    """
    text = reference.read_text(encoding="utf-8", errors="replace")
    start, end = text.find(BEGIN_MARK), text.find(END_MARK)
    if start == -1 or end == -1 or end < start:
        return text
    return text[:start] + text[end + len(END_MARK):]


def documented_nodes(reference: pathlib.Path) -> set[str]:
    """Node names the reference already spells out, from its markdown tables and prose.

    Deliberately generous - anything in backticks that looks like a node name counts. A false
    positive here only shortens the gap list by an entry that was at least mentioned; a false
    negative would send a reader hunting for something already written down.
    """
    if not reference.exists():
        return set()
    return {m.strip() for m in re.findall(r"`([^`\n]{2,60})`", curated_text(reference))}


def splice(reference: pathlib.Path, table: str) -> bool:
    """Replace the generated block in the reference. False when the markers are absent."""
    text = reference.read_text(encoding="utf-8", errors="replace")
    start, end = text.find(BEGIN_MARK), text.find(END_MARK)
    if start == -1 or end == -1 or end < start:
        return False

    reference.write_text(
        text[:start] + BEGIN_MARK + "\n" + table + text[end:], encoding="utf-8")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus", nargs="?", default=str(DEFAULT_CORPUS))
    parser.add_argument("--docs", default="docs/aixml-reference.md")
    parser.add_argument("--out", default=None)
    parser.add_argument("--update-docs", action="store_true",
                        help="splice the terminal table into --docs between its markers")
    args = parser.parse_args()

    corpus = pathlib.Path(args.corpus)
    xml_dir = corpus / "xml" if (corpus / "xml").is_dir() else corpus
    files = sorted(xml_dir.glob("*.xml"))
    if not files:
        print(f"no .xml under {xml_dir}", file=sys.stderr)
        return 2

    out = pathlib.Path(args.out) if args.out else corpus / "report"
    out.mkdir(parents=True, exist_ok=True)

    node_count: collections.Counter[str] = collections.Counter()
    node_files: dict[str, set[str]] = collections.defaultdict(set)
    signatures: dict[tuple[str, str], collections.Counter[str]] = collections.defaultdict(
        collections.Counter)
    attributes: dict[str, collections.Counter[str]] = collections.defaultdict(
        collections.Counter)
    element_count: collections.Counter[str] = collections.Counter()
    unreadable = 0

    for path in files:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError:
            unreadable += 1
            continue

        for element in root.iter():
            element_count[element.tag] += 1
            for attribute in element.attrib:
                attributes[element.tag][attribute] += 1

            # `_name` identifies a primitive; a Call is identified by being a Call, since its
            # target is a VI path and would blow the histogram up into a list of subVIs.
            key = element.get("_name") if element.tag == "Node" else None
            if element.tag in ("Structure", "CaseFrame"):
                key = f"{element.tag}:{element.get('_name', '')}"
            if not key:
                continue

            node_count[key] += 1
            node_files[key].add(path.name)
            for direction in ("inputs", "outputs"):
                value = element.get(direction)
                if value is None:
                    continue
                signatures[(key, direction)][" | ".join(terminals(value))] += 1

    known = documented_nodes(pathlib.Path(args.docs))

    with (out / "nodes.tsv").open("w", encoding="utf-8") as handle:
        handle.write("node\toccurrences\tviFiles\tdocumented\n")
        for name, count in node_count.most_common():
            handle.write(f"{name}\t{count}\t{len(node_files[name])}\t"
                         f"{'yes' if name in known else 'no'}\n")

    with (out / "undocumented.tsv").open("w", encoding="utf-8") as handle:
        handle.write("node\toccurrences\tviFiles\tcommonestInputs\tcommonestOutputs\n")
        for name, count in node_count.most_common():
            if name in known or name.startswith(("Structure:", "CaseFrame:")):
                continue
            ins = signatures[(name, "inputs")].most_common(1)
            outs = signatures[(name, "outputs")].most_common(1)
            handle.write(f"{name}\t{count}\t{len(node_files[name])}\t"
                         f"{ins[0][0] if ins else ''}\t{outs[0][0] if outs else ''}\n")

    with (out / "signatures.tsv").open("w", encoding="utf-8") as handle:
        handle.write("node\tdirection\toccurrences\torderedTerminals\n")
        for (name, direction), counter in sorted(
                signatures.items(), key=lambda kv: (-node_count[kv[0][0]], kv[0])):
            for shape, count in counter.most_common():
                handle.write(f"{name}\t{direction}\t{count}\t{shape}\n")

    with (out / "attributes.tsv").open("w", encoding="utf-8") as handle:
        handle.write("element\tattribute\toccurrences\n")
        for tag, counter in sorted(attributes.items()):
            for attribute, count in counter.most_common():
                handle.write(f"{tag}\t{attribute}\t{count}\n")

    # The markdown table for docs/aixml-reference.md section 8.
    #
    # Note what this does NOT filter on: whether the prose already mentions the node. An earlier
    # version skipped those, which coupled two unrelated jobs - the curated table exists to carry
    # caveats, this one to be complete - and had the absurd consequence that writing a sentence
    # about a node deleted its terminals from the reference. `known` belongs to the gap list only.
    #
    # It does filter on sightings: a node seen in a single VI is as likely to be that VI's quirk
    # as a general shape, and the table is only worth having if every row can be trusted without
    # re-checking.
    rows = ["| Node | inputs (in order) | outputs (in order) |", "|---|---|---|"]
    for name, _ in node_count.most_common():
        if name.startswith(("Structure:", "CaseFrame:")):
            continue
        if len(node_files[name]) < 2:
            continue
        rows.append(f"| `{name}` | {cell(signatures[(name, 'inputs')])} | "
                    f"{cell(signatures[(name, 'outputs')])} |")

    table = "\n".join(rows) + "\n"
    (out / "terminal-table.md").write_text(table, encoding="utf-8")

    reference = pathlib.Path(args.docs)
    if args.update_docs:
        print("spliced into " + args.docs if splice(reference, table)
              else f"NOT spliced: {BEGIN_MARK} / {END_MARK} not found in {args.docs}")

    undocumented = sum(1 for n in node_count
                       if n not in known and not n.startswith(("Structure:", "CaseFrame:")))
    print(f"{len(files)} exports read ({unreadable} unparsable)")
    print(f"{sum(element_count.values())} elements, {len(node_count)} distinct node kinds")
    print(f"{undocumented} node kinds are NOT named in {args.docs}")
    print(f"report: {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
