#!/usr/bin/env python3
"""lvbd_wire_metrics.py - how a block diagram's wires are DRAWN: bends, length, backward runs.

    python -I lvbd_wire_metrics.py <file.vi> ...

Reads each VI's block-diagram heap through pylabview (no LabVIEW) and decodes every wire's
compressedWireTable with lvbd_backward_wires.segments - the two-ended format and the fan-out
tree (experiments/aixml-without-grpc/FINDINGS.md section 4). Wires inside an XNode are left out.
The table is what LabVIEW last SAVED, so measure after a save.

A BEND is a joint where a wire turns: every inner joint of a two-ended table, and every 00/01
code of a fan-out tree; a branch point is a junction, not a bend. LENGTH is the sum of the drawn
segments. BACKWARD counts horizontal segments heading west, 8 px or longer.

Prints one JSON object per VI:
{"vi": ..., "wires": n, "bends": n, "length": px, "backward": n, "undecoded": n}
or {"vi": ..., "error": "..."}. Python 3 standard library plus the bundled pylabview's readRSRC.py;
run it with the bundle's own interpreter, a system Python answers "no diagram heap".
"""
import glob
import json
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lvbd_backward_wires as bw  # noqa: E402

MIN_BACK = 8


def bends_of(hexs):
    """Bends of one table, or None when it is not understood."""
    b = bytes.fromhex(hexs)
    if len(b) < 2:
        return None
    n = b[0]
    if b[1] in bw.START:                                   # two-ended: every inner joint turns
        return max(0, n - 2)
    if b[1] == 0x00 and len(b) > 2 and b[2] in bw.START:  # fan-out tree: 00/01 codes turn
        return sum(1 for c in b[3:3 + n - 2] if c in (0x00, 0x01))
    return None


def measure(vi, rd):
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([sys.executable, "-I", rd, "-x", "-i", vi, "-m", os.path.join(tmp, "x.xml")],
                       capture_output=True, check=False)
        heaps = glob.glob(os.path.join(tmp, "*_BDHb.xml"))
        if not heaps:
            return {"vi": vi, "error": "no diagram heap"}
        root = ET.parse(heaps[0]).getroot()
    parent = {c: p for p in root.iter() for c in p}
    out = {"vi": vi, "wires": 0, "bends": 0, "length": 0, "backward": 0, "undecoded": 0}
    for e in root.iter("compressedWireTable"):
        p, hidden = parent.get(e), False
        while p is not None:
            if p.get("class") in bw.HIDDEN:
                hidden = True
                break
            p = parent.get(p)
        if hidden or not e.text:
            continue
        table = e.text.strip()
        out["wires"] += 1
        segs = bw.segments(table)
        bends = bends_of(table)
        if segs is None or bends is None:
            out["undecoded"] += 1
            continue
        out["bends"] += bends
        out["length"] += sum(L for _, L in segs if L is not None)
        out["backward"] += sum(1 for d, L in segs if d == bw.W and (L is None or L >= MIN_BACK))
    return out


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    rd = bw.readrsrc()
    for vi in argv[1:]:
        print(json.dumps(measure(vi, rd)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
