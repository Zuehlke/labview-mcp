# -*- coding: utf-8 -*-
"""One probe VI per (event, source) for the events the installation scan found, each probed with
probe_events.py: does LabVIEW load it executable, is it what LabVIEW writes, does NI's exporter
name it back? (2026-10-11)

    probe_scanned_events.py <out dir> [name-filter ...] [--write-only]

Every probe has the event's frame - its Event Data Node and, for a filter event, its Event Filter
Node added by the composer - beside a File Value Change frame the run would fire. The sources are
the composable ones: a string, a waveform graph, a ring, a table, the VI.
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
Q = "&quot;"
SOURCES = {
    "Typed": '<Control _name="Typed" outputs="value:" type="string" uid="4203" uid_parent="root" value="" />',
    "Graph": '<Indicator _name="Graph" inputs="value:" style="graph21703" type="array{double}" uid="4203" uid_parent="root" value="[]" />',
    "Choice": '<Control _name="Choice" items="Low,Mid,High" outputs="value:" style="Ring" type="uint16" uid="4203" uid_parent="root" value="1"/>',
    "Grid": '<Control _name="Grid" outputs="value:" style="Table" type="array.2{string}" uid="4203" uid_parent="root" value="[[a,b],[c,d]]"/>',
    None: "",
}
# (event, source) - the source a control event is tried on first; None for the VI's / app's
PROBES = [("Mouse Down?", "Typed"), ("Mouse Down?", "Graph"), ("Drag Starting", "Typed"), ("Drag Ended", "Typed"),
          ("Drag Leave", "Graph"), ("Shortcut Menu Dismissed", "Typed"), ("Shortcut Menu Selection (App)", "Typed"),
          ("Operate Menu Dismissed", "Choice"), ("Operate Menu Dismissed", "Typed"), ("Edit Cell?", "Grid"),
          ("Double Click", "Grid"), ("Panel Close", None), ("Menu Selection? (App)", None), ("Data Change", None),
          ("Direction Change", None), ("Display State Change", None), ("Exec State Change", None),
          ("Drag Over", "Graph"), ("Application Activation", None)]

DOC = '''<?xml version="1.0" encoding="UTF-8"?>
<VI _name="SE %(title)s.vi" description="Probe: %(what)s.">
  %(src)s
  <Control _name="File" outputs="value:" type="path" uid="4204" uid_parent="root" value="" />
  <Structure _name="While Loop" count="4220.value" uid="4220" uid_parent="root">
    <Structure _name="Event Structure" uid="4230" uid_parent="4220">
      <CaseFrame selector="%(sel)s" uid="4300" uid_parent="4230">
        <Constant outputs="value:4302.value" type="bool" uid="4302" uid_parent="4300" value="false"/>
        <Tunnel _id="Out1" inputs="value:4302.value" uid="4303" uid_parent="4300"/>
      </CaseFrame>
      <CaseFrame selector=" %(q)sFile%(q)s\\3A Value Change " uid="4270" uid_parent="4230">
        <Constant outputs="value:4275.value" type="bool" uid="4275" uid_parent="4270" value="true"/>
        <Tunnel _id="Out1" inputs="value:4275.value" uid="4276" uid_parent="4270"/>
      </CaseFrame>
      <Tunnel _id="Out1" outputs="value:4280.value" uid="4280" uid_parent="4230"/>
    </Structure>
    <Condition inputs="value:4280.value" uid="4282" uid_parent="4220" value="stop"/>
  </Structure>
</VI>
'''


def main(argv):
    out = argv[0]
    flt = [a for a in argv[1:] if not a.startswith("--")]
    os.makedirs(out, exist_ok=True)
    for ev, src in PROBES:
        title = re.sub(r"[^A-Za-z0-9]+", " ", "%s on %s" % (ev, src or "VI")).strip()
        if flt and not any(f.lower() in title.lower() for f in flt):
            continue
        sel = (" %s%s%s\\3A %s " % (Q, src, Q, ev)) if src else " %s " % ev
        x = os.path.join(out, title.replace(" ", "_") + ".xml")
        open(x, "w", encoding="utf-8").write(DOC % {"title": title, "what": "%s on %s" % (ev, src or "the VI"),
                                                    "src": SOURCES[src], "sel": sel, "q": Q})
        if "--write-only" in argv:
            continue
        r = subprocess.run([PY, os.path.join(HERE, "probe_events.py"), x], capture_output=True, text=True,
                           stdin=subprocess.DEVNULL, timeout=600)
        txt = r.stdout + r.stderr
        st = re.search(r"ExecState (\d)", txt)
        same = re.search(r"frame 0: data/specs/filter (\w+)", txt)
        names = re.search(r"frame 0: names (\w+)(.*)", txt)
        nc = re.search(r"NOT COMPOSED: (.*)", txt)
        print("%-44s %s" % (title, ("NOT COMPOSED " + nc.group(1)[:110]) if nc else
                            "ExecState %s  data %s  names %s%s" % (st and st.group(1), same and same.group(1),
                                                                   names and names.group(1), names and names.group(2)[:60])),
              flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
