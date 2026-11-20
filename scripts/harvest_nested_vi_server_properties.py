#!/usr/bin/env python3
"""List the SUB-OBJECTS a Property Node reaches through, into docs/vi-server-subobjects.tsv.

A property of a sub-object - a graph's plot, cursor or scale, a control's label - is written in
AIXML as `<owner property>.<sub-object property>`: `Plot.Plot Name`, `Y Scale.Range:Minimum`,
`Label.Text`. The catalogue (vi-server-properties.tsv) lists the two halves under two classes
(`{LV.WaveformGraph} Plot`, `{LV.Plot} Plot Name`) and says nowhere how they join, so a model
asked for "the plot name" writes `Plot Name` on the graph and the node does not compose
(measured 2026-11-20, the plot-names probe). lvai_vi_server_reference joins them from this table.

A row is one owner property that returns a sub-object: the property's type is a VI Server refnum
whose CtlFlags names the object's class (parts/refnum-classes.tsv) - the same resolution the
pylabview composer makes (`Composer._nested_property_row`). Only PARTS of the bound object count
(SUB_OBJECTS); a property returning a reference to another object (Owning VI, Terminal) is an
output, never a level of a field.

Columns: class, property, object, fields (how many of the object's catalogue properties the
pylabview composer resolves through it - every one, at the time of writing).

Usage: python scripts/harvest_nested_vi_server_properties.py [--out PATH] [--fields]
--fields prints every nested field instead (class, field, object, composable), for a sweep.
"""
import argparse
import os
import sys
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO, "scripts", "aixml-pylabview"))

import aixml2vi  # noqa: E402

# The classes a Property Node EXPANDS into: parts of the object the node is bound to. NI's exports
# (cache sweep 2026-11-20, 300+ dotted fields) show Cursor, X/Y/Z Scale, Scale, Plot, Label,
# Caption, Text, Ring Text and Unit Label as the first level, and nothing else.
SUB_OBJECTS = {"LV.Text", "LV.Plot", "LV.Cursor", "LV.NumericText", "LV.Scale", "LV.ColorScale",
               "LV.GraphScale", "LV.ColorGraphScale", "LV.SlideScale", "LV.RotaryColorScale"}


class _Parts:
    def __init__(self, d):
        self.d = d


def composer():
    c = aixml2vi.Composer.__new__(aixml2vi.Composer)
    c.parts = _Parts(aixml2vi.OWN_PARTS)
    return c


def _object_class(row, by_flags):
    try:
        t = ET.fromstring(row["type"])
    except (ET.ParseError, KeyError, TypeError):
        return None
    if t.get("Type") != "Refnum" or t.get("RefType") != "LVObjCtl":
        return None
    return by_flags.get((t.get("CtlFlags") or "").upper())


def subobjects(c):
    """[(class, property, object class, [nested fields], [composable fields])]"""
    aixml2vi.refnum_type("LV.VI", plain_class=True)      # loads _REF_CLASSES
    by_flags = {}
    for cls, (flags, _) in aixml2vi._REF_CLASSES.items():
        by_flags.setdefault(flags.upper(), cls)
    by_class = {}
    for cls, prop in c._catalogue():
        by_class.setdefault(cls, set()).add(prop)
    out = []
    for cls in sorted(by_class):
        for prop in sorted(by_class[cls]):
            row = c._property_row(cls, prop)
            sub = _object_class(row, by_flags) if row else None
            if sub is None or sub == cls or sub not in SUB_OBJECTS:
                continue
            fields = ["%s.%s" % (prop, sp) for sp in sorted(by_class.get(sub, ()))]
            ok = [f for f in fields if c._nested_property_row(cls, f) is not None]
            out.append((cls, prop, sub, fields, ok))
    return out


def rows(c):
    """Every nested field: (class, field, object class, 'yes'|'no' composable)."""
    return [(cls, f, sub, "yes" if f in ok else "no")
            for cls, _, sub, fields, ok in subobjects(c) for f in fields]


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--out", default=os.path.join(REPO, "docs", "vi-server-subobjects.tsv"))
    ap.add_argument("--fields", action="store_true")
    a = ap.parse_args()
    c = composer()
    if a.fields:
        for r in rows(c):
            print("{%s}\t%s\t{%s}\t%s" % r)
        return
    got = subobjects(c)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("class\tproperty\tobject\tfields\n")
        for cls, prop, sub, fields, ok in got:
            f.write("{%s}\t%s\t{%s}\t%d\n" % (cls, prop, sub, len(ok)))
    print("%d sub-object rows -> %s" % (len(got), a.out))


if __name__ == "__main__":
    main()
