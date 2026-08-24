#!/usr/bin/env python3
"""Survey .ctl files structurally, and print ONLY aggregates - never a name or a path.

Why the constraint is in the code and not just in the reviewer's head: the interesting corpora are
customer trees, and a document written from one must not carry the customer's vocabulary. Every
identifier this script touches - file names, control labels, enum item text, library names - stays
inside the process. What comes out is counts and structural shapes.

What it answers:
  * how a .ctl declares itself (Instrument Type, top-level type kind)
  * whether `Flag1` on the TypeDef descriptor discriminates strict from non-strict typedefs
  * where an enum keeps its item names, and whether the counted panel buffer is always present
  * how many copies of the same enum a single .ctl carries

Usage:
    python survey_controls.py --bundle <pylabview bundle dir> --root <tree> [--limit N]
"""

import argparse
import collections
import os
import pathlib
import re
import shutil
import subprocess
import tempfile

FLAG1 = re.compile(r'<TypeDesc Type="TypeDef" Flag1="(0x[0-9A-Fa-f]+)"')
INSTRUMENT = re.compile(r'<Instrument Type="(\w+)"')
ENUM_TD = re.compile(r'<TypeDesc Type="(Unit\w+)"[^>]*>\s*<EnumLabel>')
ENUM_LABELS = re.compile(r'<EnumLabel>')
COUNTED_BUF = re.compile(r'<buf>\((\d+)\)(?:"[^"]*")+</buf>')
NESTED = re.compile(r'<TypeDesc Type="TypeDef"[^>]*>\s*<TypeDesc Type="(\w+)"')
INST_BITS = re.compile(r'(InStBit\d+|DebugCapable|PrintAfterExec)="(\d)"')


def extract(bundle, path, work):
    main = os.path.join(work, "c.xml")
    try:
        subprocess.run([os.path.join(bundle, "python.exe"),
                        os.path.join("app", "pylabview", "readRSRC.py"),
                        "-x", "-i", str(path), "-m", main],
                       cwd=bundle, capture_output=True, timeout=90, check=False)
    except subprocess.TimeoutExpired:
        return None, None
    fp = os.path.join(work, "c_FPHb.xml")
    return (main if os.path.isfile(main) else None,
            fp if os.path.isfile(fp) else None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", required=True)
    ap.add_argument("--root", required=True)
    ap.add_argument("--limit", type=int, default=0)
    args = ap.parse_args()

    files = sorted(pathlib.Path(args.root).rglob("*.ctl"))
    if args.limit:
        files = files[: args.limit]
    print("controls to inspect: %d" % len(files), flush=True)

    stats = collections.Counter()
    instrument = collections.Counter()
    nested_kind = collections.Counter()
    flag_zero_nested = collections.Counter()      # nested kind, when Flag1 == 0
    flag_set_nested = collections.Counter()       # nested kind, when Flag1 != 0
    flag_values = collections.Counter()           # how often each Flag1 VALUE recurs
    enum_copies = collections.Counter()
    enum_items = collections.Counter()
    buf_vs_labels = collections.Counter()
    bit_profiles = collections.Counter()

    work = tempfile.mkdtemp(prefix="ctlsurvey_")
    try:
        for i, path in enumerate(files, 1):
            for f in pathlib.Path(work).iterdir():
                f.unlink()
            main_xml, fp_xml = extract(args.bundle, path, work)
            if main_xml is None:
                stats["extract failed"] += 1
                continue
            text = pathlib.Path(main_xml).read_text(encoding="utf-8", errors="replace")
            stats["inspected"] += 1

            for m in INSTRUMENT.finditer(text):
                instrument[m.group(1)] += 1
                break

            flags = FLAG1.findall(text)
            if not flags:
                stats["no TypeDef descriptor"] += 1
                continue
            stats["has TypeDef descriptor"] += 1

            nested = NESTED.search(text)
            kind = nested.group(1) if nested else "(none)"
            nested_kind[kind] += 1

            top = flags[0]
            if int(top, 16) == 0:
                stats["Flag1 == 0"] += 1
                flag_zero_nested[kind] += 1
            else:
                stats["Flag1 != 0"] += 1
                flag_set_nested[kind] += 1
                flag_values[top] += 1

            # enums: how many copies of the item list, and how long
            copies = len(ENUM_TD.findall(text))
            if copies:
                enum_copies[copies] += 1
                enum_items[len(ENUM_LABELS.findall(text)) // max(copies, 1)] += 1

                if fp_xml is not None:
                    fp = pathlib.Path(fp_xml).read_text(encoding="utf-8", errors="replace")
                    bufs = COUNTED_BUF.findall(fp)
                    buf_vs_labels["panel buffer present" if bufs else "NO panel buffer"] += 1

            # the instrument bit pattern, as a shape rather than as values
            bits = INST_BITS.findall(text)
            on = tuple(sorted(n for n, v in bits if v == "1"))
            bit_profiles[on] += 1

            if i % 200 == 0:
                print("  %4d/%d" % (i, len(files)), flush=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    def show(title, counter, limit=12):
        print("\n%s" % title)
        for k, v in counter.most_common(limit):
            print("  %-46s %d" % (k, v))

    print()
    for k, v in sorted(stats.items()):
        print("  %-28s %d" % (k, v))
    show("Instrument Type", instrument)
    show("nested type kind under TypeDef", nested_kind)
    show("nested kind when Flag1 == 0", flag_zero_nested)
    show("nested kind when Flag1 != 0", flag_set_nested)
    print("\nFlag1 non-zero values: %d distinct over %d controls"
          % (len(flag_values), sum(flag_values.values())))
    repeats = sum(1 for v in flag_values.values() if v > 1)
    print("  values seen more than once: %d" % repeats)
    print("  largest recurrence        : %d"
          % (max(flag_values.values()) if flag_values else 0))
    show("copies of the enum item list per control", enum_copies)
    show("enum item count", enum_items)
    show("counted panel buffer, for enum controls", buf_vs_labels)
    print("\ninstrument bits set (top shapes, names only - no file identity)")
    for bits, n in bit_profiles.most_common(6):
        print("  %-46s %d" % (",".join(bits) or "(none)", n))


if __name__ == "__main__":
    main()
