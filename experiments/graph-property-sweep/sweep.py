#!/usr/bin/env python3
"""Compose one VI per graph class and sub-object, each with ONE Property Node holding every
nested field of that sub-object (`Plot.Plot Name`, `Y Scale.Range:Minimum`, ...), for LabVIEW to
load - the load check lv-com.ps1 answers ExecState for. A field the composer refuses is reported
here; a field LabVIEW refuses makes the VI eBad, and --split then composes one VI per field.

Usage: python sweep.py OUTDIR [--split CLASS:SUBOBJECT ...]
Writes OUTDIR/<class>_<sub>.vi (or <class>_<sub>_<n>.vi with --split) and OUTDIR/manifest.tsv.
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "scripts"))
import harvest_nested_vi_server_properties as h  # noqa: E402

COMPOSER = os.path.join(REPO, "scripts", "aixml-pylabview", "aixml2vi.py")

GRAPHS = {   # class -> (style token, data type, value)
    "LV.WaveformGraph": ("graph21703", "array{double}", "[]"),
    "LV.WaveformChart": ("graph21717", "double", "0"),
    "LV.XYGraph": ("graph21716", "cluster{array{double}.X,array{double}.Y}", "[[],[]]"),
    "LV.IntensityGraph": ("graph21720", "array.2{double}", "[]"),
}


def esc(s):
    return s.replace("\\", "\\5C").replace(":", "\\3A").replace(",", "\\2C").replace('"', "&quot;")


def field_access(c, cls, field):
    row = c._nested_property_row(cls, field)
    acc = (row or {}).get("access") or "r"
    return "read" if "r" in acc else "write"


def aixml(name, cls, fields):
    style, typ, val = GRAPHS[cls]
    items, ins, outs = [], ["reference:4201.ref"], ["reference out:", "error out:"]
    for acc, f in fields:
        items.append("%s+%s" % (acc, esc(f)))
        if acc == "read":
            outs.append("%s:" % esc(f))
        else:
            # a write gets a wire from the matching read is not possible here: leave it unwired
            pass
    return ('<VI _name="%s" description="Graph property sweep.">\n'
            '  <Indicator _name="G" inputs="value:" style="%s" type="%s" uid="4200" uid_parent="root" value="%s"/>\n'
            '  <Node _name="VI Server Reference" element="G" outputs="G:4201.ref" uid="4201" uid_parent="root"/>\n'
            '  <Node _name="Property Node" fields="%s" inputs="%s" outputs="%s" type="{%s}" uid="4210" uid_parent="root"/>\n'
            '</VI>\n') % (name, style, typ, val, ",".join(items), ",".join(ins), ",".join(outs), cls)


def compose(src, vi):
    p = subprocess.run([sys.executable, COMPOSER, src, vi], capture_output=True, text=True)
    return p.returncode, (p.stderr or p.stdout).strip().splitlines()[-1:] or [""]


def main():
    out = sys.argv[1]
    split = set(a for a in sys.argv[2:] if a != "--split")
    os.makedirs(out, exist_ok=True)
    c = h.composer()
    rows = [r for r in h.rows(c) if r[0] in GRAPHS]
    groups = {}
    for cls, field, sub, ok in rows:
        groups.setdefault((cls, field.split(".", 1)[0]), []).append(field)
    man = open(os.path.join(out, "manifest.tsv"), "a", encoding="utf-8")
    for (cls, so), fields in sorted(groups.items()):
        key = "%s:%s" % (cls, so)
        if split and key not in split:
            continue
        sets = [[f] for f in fields] if key in split else [fields]
        for n, fs in enumerate(sets):
            base = "%s_%s%s" % (cls.split(".")[1], so.replace(" ", ""), "_%d" % n if key in split else "")
            src = os.path.join(out, base + ".xml")
            vi = os.path.join(out, base + ".vi")
            with open(src, "w", encoding="utf-8") as f:
                f.write(aixml(base + ".vi", cls, [(field_access(c, cls, x), x) for x in fs]))
            rc, msg = compose(src, vi)
            man.write("%s\t%s\t%s\t%s\t%s\n" % (base, cls, so, ";".join(fs), "composed" if rc == 0 else "REFUSED " + msg[0]))
            print(base, len(fs), "ok" if rc == 0 else "REFUSED " + msg[0])


if __name__ == "__main__":
    main()
