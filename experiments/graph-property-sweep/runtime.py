#!/usr/bin/env python3
"""Compose one VI per graph class that WRITES a set of nested properties and reads them back, for
lv-com.ps1 to run - the behaviour check after sweep.py's load check.

Usage: python runtime.py OUTDIR      (prints the lv-com.ps1 -Outputs list)
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from sweep import COMPOSER, GRAPHS, esc  # noqa: E402

# field -> (constant type, value written); Active Plot / Active Cursor first, as on NI's nodes.
# Measured 2026-11-20: Active Plot must name a plot the graph HAS (an empty graph has plot 0 only;
# 1 answers Error 1077), and Label.Text is writable in EDIT mode only (Error 1073 at run time) -
# Caption.Text answers Error 1320 on an object that has no caption.
CASES = {
    "LV.WaveformGraph": [("Active Plot", "int32", "0"), ("Plot.Plot Name", "string", "Second"),
                         ("Plot.Line Width", "int32", "3"), ("Plot.Plot Color", "uint32", "255"),
                         ("Plot.Visible", "bool", "false"),
                         ("X Scale.Range:Maximum", "double", "50"), ("Y Scale.Name Label.Text", "string", "Volts")],
    "LV.WaveformChart": [("Active Plot", "int32", "0"), ("Plot.Plot Name", "string", "Temp"),
                         ("Plot.Line Width", "int32", "2"),
                         ("Y Scale.Range:Minimum", "double", "-5"), ("X Scale.Name Label.Text", "string", "Samples")],
    "LV.XYGraph": [("Active Plot", "int32", "0"), ("Plot.Plot Name", "string", "Orbit"),
                   ("Plot.Point Style", "int32", "2"), ("Y Scale.Range:Maximum", "double", "7")],
    "LV.IntensityGraph": [("Z Scale.Range:Maximum", "double", "9"), ("Z Scale.Name Label.Text", "string", "Heat"),
                          ("Y Scale.Range:Maximum", "double", "4")],
}
SELECTORS = ("Active Plot", "Active Cursor")


def aixml(name, cls):
    style, typ, val = GRAPHS[cls]
    cases = CASES[cls]
    out = ['<VI _name="%s" description="Writes nested graph properties and reads them back.">' % name,
           '  <Indicator _name="G" inputs="value:" style="%s" type="%s" uid="4200" uid_parent="root" value="%s"/>' % (style, typ, val),
           '  <Node _name="VI Server Reference" element="G" outputs="G:4201.ref" uid="4201" uid_parent="root"/>']
    uid = 4300
    wfields, wins = [], ["reference:4201.ref"]
    for f, t, v in cases:
        out.append('  <Constant outputs="value:%d.value" type="%s" uid="%d" uid_parent="root" value="%s"/>' % (uid, t, uid, v))
        wfields.append("write+" + esc(f))
        wins.append("%s:%d.value" % (esc(f), uid))
        uid += 1
    out.append('  <Node _name="Property Node" fields="%s" inputs="%s" outputs="reference out:4210.ref,error out:4210.err" type="{%s}" uid="4210" uid_parent="root"/>'
               % (",".join(wfields), ",".join(wins), cls))
    # read back: the selector written again (from the same constant), every other field read
    rfields, rins, routs = [], ["reference:4210.ref", "error in (no error):4210.err"], []
    for i, (f, t, v) in enumerate(cases):
        if f in SELECTORS:
            rfields.append("write+" + esc(f))
            rins.append("%s:%d.value" % (esc(f), 4300 + i))
        else:
            rfields.append("read+" + esc(f))
            routs.append("%s:4220.%s" % (esc(f), esc(f)))
    out.append('  <Node _name="Property Node" fields="%s" inputs="%s" outputs="%s,reference out:,error out:4220.err" type="{%s}" uid="4220" uid_parent="root"/>'
               % (",".join(rfields), ",".join(rins), ",".join(routs), cls))
    names = []
    for i, (f, t, v) in enumerate(cases):
        if f in SELECTORS:
            continue
        n = "R" + str(i)
        names.append((n, f, v))
        out.append('  <Indicator _name="%s" inputs="value:4220.%s" type="%s" uid="%d" uid_parent="root" value="%s"/>'
                   % (n, esc(f), t, 4400 + i, "" if t == "string" else ("false" if t == "bool" else "0")))
    out.append('  <Indicator _name="error out" inputs="value:4220.err" type="cluster{bool.status,int32.code,string.source}" uid="4500" uid_parent="root" value="[false,0,]"/>')
    out.append('</VI>')
    return "\n".join(out) + "\n", names


def main():
    d = sys.argv[1]
    os.makedirs(d, exist_ok=True)
    for cls in CASES:
        base = "RT_" + cls.split(".")[1]
        src, names = aixml(base + ".vi", cls)
        open(os.path.join(d, base + ".xml"), "w", encoding="utf-8").write(src)
        p = subprocess.run([sys.executable, COMPOSER, os.path.join(d, base + ".xml"), os.path.join(d, base + ".vi")],
                           capture_output=True, text=True)
        print(base, "composed" if p.returncode == 0 else "REFUSED " + (p.stderr.strip().splitlines() or [""])[-1])
        print("  outputs:", ";".join([n for n, _, _ in names] + ["error out"]))
        for n, f, v in names:
            print("  %s = %s  expect %s" % (n, f, v))


if __name__ == "__main__":
    main()
