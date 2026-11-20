#!/usr/bin/env python3
"""One-off, 2026-11-20: give the graph classes the harvest never saw their INHERITED catalogue rows.

vi-server-classes.tsv names the parents by class id: XYGraph and DigitalGraph 24 (WaveformGraph,
CtlFlags 0x0018 in parts/refnum-classes.tsv), MixedSignalGraph 22 (GraphChart, 0x0016). A subclass
has every property of its parent, so the parent's rows are copied with a `source` saying so. The
subclass's OWN properties are not known and are not invented here.
"""
import os

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TSV = os.path.join(REPO, "docs", "vi-server-properties.tsv")
CHILDREN = {"{LV.XYGraph}": "{LV.WaveformGraph}", "{LV.DigitalGraph}": "{LV.WaveformGraph}",
            "{LV.MixedSignalGraph}": "{LV.GraphChart}"}
SOURCE = "inherited from %s (parent class id in vi-server-classes.tsv), 2026-11-20; own properties not harvested"

lines = open(TSV, encoding="utf-8", newline="").read().split("\n")
head, body = lines[0], [l for l in lines[1:] if l]
have = {l.split("\t", 1)[0] for l in body}
for child, parent in CHILDREN.items():
    if child in have:
        continue
    block = ["\t".join([child] + l.split("\t")[1:3] + [SOURCE % parent])
             for l in body if l.startswith(parent + "\t")]
    # inserted before the first class that sorts after it; the existing order is left alone
    at = next((i for i, l in enumerate(body) if l.split("\t", 1)[0] > child), len(body))
    body[at:at] = block
open(TSV, "w", encoding="utf-8", newline="").write("\n".join([head] + body) + "\n")
print(len(body), "rows")
