# -*- coding: utf-8 -*-
"""The NI examples that show each gap closed on 2026-11-19, one group per gap, written as
<group>\t<path> to the demo folder's sources.tsv.

    <bundle>\\python.exe demo_sources.py <demo dir>
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sweep  # noqa: E402

WANT = {
    "XNode": ["Convert Database Variant to Data.vi", "Continuous Overflow Statistics.vi",
              "Generate Correlated Signal.vi"],
    "ControlClass": ["Vertical Fill Slide.vi", "Function Generator with FM.vi", "Populate Tree Items.vi",
                     "Nonlinear Spring Constant fit.vi"],
    "ImplicitNode": ["Wavelet Packet - Join Node.vi"],
    "Refnum": ["Simple Occurrence.vi", "System Identification.vi"],
    "DAQmx": ["Counter - Continuous Output.vi", "Voltage - Finite Output.vi"],
    "VISA": ["Simple Serial.vi", "Set Serial Break Event.vi"],
    "ClassControl": ["Write Number of children.vi"],
}
ERROR_RING = r"C:\Program Files (x86)\National Instruments\LabVIEW 2026\vi.lib\picture\jpeg.llb\Check Color Table Size.vi"


def main(demo):
    paths = sweep.example_paths()
    lines = []
    for group, names in WANT.items():
        for n in names:
            hit = [p for p in paths if os.path.basename(p) == n]
            print(group, n, hit[0] if hit else "NOT FOUND")
            if hit:
                lines.append("%s\t%s" % (group, hit[0]))
    lines.append("XNode\t%s" % ERROR_RING)
    open(os.path.join(demo, "sources.tsv"), "w", encoding="utf-8").write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main(sys.argv[1])
