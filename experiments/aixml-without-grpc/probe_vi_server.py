# -*- coding: utf-8 -*-
"""Load-probe VI Server parts: one minimal VI per Invoke Node method or read Property Node item.

    probe_vi_server.py <parts-dir> <cases.tsv> <out-dir> <tag>

cases.tsv rows: `invoke<TAB>LV.Class<TAB>Method` or `prop<TAB>LV.Class<TAB>Property`. Each VI wires a
class refnum control (where refnum-classes.tsv has the class) into the node and error in/out
through it; it is composed with <parts-dir>, loaded over COM (lv-com.ps1) and judged by ExecState,
LabVIEW's `Insane` lines and every DWarn that is not the expected wire repair. One JSON line per
case, and result-<tag>.json at the end.

ExecState 0 is NOT a verdict on the part: a method whose required inputs the probe leaves unwired
is broken by design. Run the same cases against two parts dirs (A/B) and compare - that is what
§7b of FINDINGS did, and how it found the implicit invoke parts that killed LabVIEW on load.
A LabVIEW that died mid-run is restarted only when its log fills; the cases after a crash then
answer `GetVIReference FAILED` or a NullReference, so re-run those alone before believing them.
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
import selftest  # noqa: E402  (its log readers, COM driver and restart)

ERR = "cluster{bool.status,int32.code,string.source}"


def esc(s):
    return s.replace("\\", "\\5C").replace(":", "\\3A").replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;")


def aixml(kind, cls, name, vi, refcls):
    wired = cls in refcls
    rows = ['<VI _name="%s" description="probe">' % vi,
            '<Control _name="error in" outputs="value:4200.e" type="%s" uid="4200" uid_parent="root" value="[false,0,]"/>' % ERR]
    ins = ["error in (no error):4200.e"]
    if wired:
        rows.append('<Control _name="ref" outputs="value:4210.r" type="ref{%s}" uid="4210" uid_parent="root" value=""/>' % cls)
        ins.insert(0, "reference:4210.r")
    if kind == "invoke":
        rows.append('<Node _name="Invoke Node" inputs="%s" outputs="error out:4300.e" target="%s" type="{%s}" uid="4300" uid_parent="root"/>'
                    % (",".join(ins), esc(name), cls))
    else:
        rows.append('<Node _name="Property Node" fields="read+%s" inputs="%s" outputs="error out:4300.e" type="{%s}" uid="4300" uid_parent="root"/>'
                    % (esc(name), ",".join(ins), cls))
    rows.append('<Indicator _name="error out" inputs="value:4300.e" type="%s" uid="4400" uid_parent="root" value="[false,0,]"/>' % ERR)
    rows.append("</VI>")
    return "\n".join(rows), wired


def main(argv):
    parts, cases, out, tag = os.path.normpath(argv[0]), argv[1], os.path.normpath(argv[2]), argv[3]
    os.makedirs(out, exist_ok=True)   # normpath: COM opens nothing given C:/ paths
    refcls = {l.split("\t")[0] for l in open(os.path.join(COMPOSER, "parts", "refnum-classes.tsv"), encoding="utf-8")
              if l.strip() and not l.startswith(("#", "class\t"))}
    res = []
    for i, line in enumerate(l for l in open(cases, encoding="utf-8") if l.strip()):
        kind, cls, name = line.rstrip("\n").split("\t")
        vi = "P%s_%04d.vi" % (tag, i)
        src = os.path.join(out, vi[:-3] + ".xml")
        doc, wired = aixml(kind, cls, name, vi, refcls)
        open(src, "w", encoding="utf-8").write(doc)
        vip = os.path.join(out, vi)
        r = subprocess.run([sys.executable, "-I", os.path.join(COMPOSER, "aixml2vi.py"), src, vip, "--parts", parts],
                           capture_output=True, text=True, stdin=subprocess.DEVNULL)
        row = {"kind": kind, "class": cls, "name": name, "refWired": wired, "vi": vi}
        if r.returncode != 0 or not os.path.exists(vip):
            tail = (r.stdout + r.stderr).strip().splitlines()
            row["compose"] = "REFUSED: " + (tail[-1][:200] if tail else "")
        else:
            if selftest.dwarn_events() >= 85:
                selftest.restart_labview()
            before = selftest.dwarn_lines()
            state, _, txt = selftest.lvcom(vip, "", [], run=False, timeout=90)
            after = selftest.dwarn_lines()
            new = [l for l in after[len(before):] if not any(x in l for x in selftest.EXPECTED_DWARNS)] \
                if len(after) >= len(before) else ["<log reset>"]
            row.update({"compose": "ok", "execState": state, "insane": selftest.insanities(vip),
                        "dwarns": sorted(set(l[:160] for l in new))})
            if state is None:
                row["lvcom"] = txt.strip()[-300:]
        res.append(row)
        print(json.dumps(row), flush=True)
    json.dump(res, open(os.path.join(out, "result-%s.json" % tag), "w", encoding="utf-8"), indent=1)


if __name__ == "__main__":
    main(sys.argv[1:])
