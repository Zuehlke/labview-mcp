# -*- coding: utf-8 -*-
"""Fold the sweep's refusals into TOPICS - one gap that one change would close - and rank them.

    python topics.py <outdir> <census.json> <topics.json>

blocks     examples whose FIRST refusal is this topic (free of cut artefacts)
anywhere   examples refusing this topic at any step
usage      census: example VIs that use the element(s) at all (max over the topic's keys)
isolated   palette items refused when composed alone (calls/nodes sweep)
"""
import collections
import glob
import json
import os
import re
import sys

# (topic, layer, regex over "key | message"); first match wins. layer: composer or reader
TOPICS = [
    ("Property Node bound to a control: the reader writes class {?}", "reader", r"class '\{\?\}'"),
    ("Control style lost in the export (Tree, Listbox, Tab, Slide, ...): node bound to a plain control", "reader", r"the export names no style or class"),
    ("Property Node linked to a control the export does not carry", "reader", r"no (control|front-panel object) '.*' to link to"),
    ("Class control/indicator on the panel (ref{UDClassInst})", "composer", r"^panel:.*type 'ref\{UDClassInst\}'"),
    ("Class constant inside an array or cluster", "composer", r"^const:.*type 'ref\{UDClassInst\}'"),
    ("Property Node: DAQmx property not in the harvested table", "composer", r"of GenClassTag\.DAQmx.*not in the harvested table|DAQmx .*harvested yet"),
    ("Property/Invoke Node on a VISA class", "composer", r"class '\{Visa\."),
    ("Property/Invoke Node on an ActiveX/.NET class or ActiveX refnum", "composer", r"ActiveX|DotNet|\.NET"),
    ("Property Node on NI System Configuration (nisysapi)", "composer", r"nisysapi"),
    ("Property Node: other property or class not harvested", "composer", r"^node:(Property|Invoke) Node.*(harvested|not composable)"),
    ("Express VI instance: terminals depend on its configuration", "composer", r"call:(Ex_|.*Source\.vi|.*Block\.vi).*has no (input|output)"),
    ("XNode call", "composer", r"is an XNode"),
    ("Refnum wires never composed yet (UsrDefined, AutoRef, Occurrence, TCP, DataSocket, Python session)", "composer", r"carries a Refnum\.|stored raw by pylabview"),
    ("Refnum types without a part (DataLog, DataFinder, TDMS, EventReg)", "composer", r"no (const )?part for a .* refnum|no const part for a|ref\{(TDMSFile|EventReg)\}"),
    ("Digital waveform / digital table type", "composer", r"digital(waveform|table)"),
    ("Picture type (control or constant)", "composer", r"Picture|type 'picture'"),
    ("Waveform/MeasureData constant", "composer", r"constant encoding for MeasureData"),
    ("Timed Loop", "composer", r"Timed Loop"),
    ("Stacked Sequence in NI's export form (one frame)", "composer", r"Stacked Sequence"),
    ("Feedback Node inside a case/sequence (not directly in a loop)", "composer", r"Feedback Node.*only at the top level"),
    ("Expression Node", "composer", r"Expression Node"),
    ("Python Node", "composer", r"Python Node"),
    ("Constructor Node (.NET)", "composer", r"Constructor Node"),
    ("Asynchronous call (Start/Wait On Asynchronous Call)", "composer", r"Asynchronous Call"),
    ("Register For Events on a non-user-event source", "composer", r"Register For Events"),
    ("VI Server Reference to a SubPanel / Web Browser control", "composer", r"SubPanel|Web Browser"),
    ("Graph style or graph cursor property", "composer", r"style 'graph|Cursor\."),
    ("Fixed-point type", "composer", r"type 'fixed'"),
    ("Numeric with physical unit (double{m})", "composer", r"double\{[^}]*\}"),
    ("Channel wire (Tag, Stream, ...)", "composer", r"CHANNEL template"),
    ("In Place Element: Array Split/Replace Subarrays, Array Index/Replace", "composer", r"Array Split / Replace|Array Index / Replace"),
    ("Waveform Unbundle/Bundle Elements, Build Cluster Array", "composer", r"Waveform Unbundle|Build Cluster Array"),
    ("Map and Set primitives", "composer", r"Build Map|Map Get|Remove From Set|Read Map|Empty Collection"),
    ("File I/O: Format Into File / Scan From File", "composer", r"Format Into File|Scan From File"),
    ("Merge Signals", "composer", r"Merge Signals"),
    ("MATLAB script / Call MATLAB Function", "composer", r"MATLAB"),
    ("Call Library Function Node from NI's export", "composer", r"Call Library Function"),
    ("Call By Reference without a typed refnum", "composer", r"Call By Reference"),
    ("Conditional Disable Structure with a target condition", "composer", r"Conditional Disable"),
    ("Event structure: an event the control type does not have", "composer", r"is an event of"),
    ("Network primitives without a part (UDP, Bluetooth, IrDA, DataSocket, Shared Variable)", "composer", r"(UDP|Bluetooth|IrDA|DataSocket|Variable) .*has no harvested part"),
    ("Other primitives without a part (Sinc, Inverse Tangent (2 Input))", "composer", r"has no harvested part"),
    ("Primitive whose terminal names the table lacks", "composer", r"has no terminal|no template with terminals"),
    ("Composer crash", "composer", r"CRASH"),
    ("Dynamic-dispatch method called by bare name (VI Tester, ...)", "composer", r"dynamic-dispatch method"),
    ("Library member the .lvlib does not list (licensed toolkit)", "composer", r"lists no member"),
    ("Callee unreadable (pylabview timeout)", "composer", r"timed out"),
    ("Not installed on this station", "station", r"neither near the caller nor in the installation|no NI_Vision_Development_Module"),
    ("Primitive unknown by name (unresolved ?primResID)", "reader", r"\?primResID|\?fixed"),
]
# isolated-only refusals that say nothing about support: an UNWIRED node whose type comes from its wire
ARTEFACT = re.compile(r"\(None\) is not known|must be wired|needs a wired|needs a numeric wire|no control or indicator named|"
                      r"not in an event frame|sits outside an In Place|cannot be read: extract failed for .*out\\\\nodes|"
                      r"&amp;|no registration carries|names no parent class|LABELLED source|unwired event source")


def topic_of(key, msg):
    s = "%s | %s" % (key or "", msg or "")
    for name, layer, rx in TOPICS:
        if re.search(rx, s):
            return name, layer
    return None, None


def main(outdir, census_path, out):
    census = {r["key"]: r for r in json.load(open(census_path, encoding="utf-8"))["rows"]}
    T = collections.defaultdict(lambda: {"blocks": 0, "anywhere": 0, "usage": 0, "isolated": set(),
                                         "keys": collections.Counter(), "sample": None, "examples": []})
    unmatched = collections.Counter()
    for f in glob.glob(os.path.join(outdir, "examples-*.jsonl")):
        for l in open(f, encoding="utf-8"):
            r = json.loads(l)
            seen = set()
            for i, s in enumerate(r["steps"]):
                name, layer = topic_of(s.get("key"), s["msg"])
                if not name:
                    if i == 0:
                        unmatched[re.sub(r"\d+", "#", s["msg"])[:140]] += 1
                    continue
                t = T[name]
                t["layer"] = layer
                if i == 0:
                    t["blocks"] += 1
                    if len(t["examples"]) < 3:
                        t["examples"].append(r["vi"])
                if name not in seen:
                    t["anywhere"] += 1
                    seen.add(name)
                t["keys"][s.get("key")] += 1
                t["sample"] = t["sample"] or s["msg"]
    for f in glob.glob(os.path.join(outdir, "calls-*.jsonl")) + glob.glob(os.path.join(outdir, "nodes-*.jsonl")):
        for l in open(f, encoding="utf-8"):
            r = json.loads(l)
            if r["ok"] or ARTEFACT.search(r["msg"]):
                continue
            name, layer = topic_of(r["key"], r["msg"])
            if not name:
                unmatched["ISO " + r["msg"][:140]] += 1
                continue
            t = T[name]
            t["layer"] = layer
            t["isolated"].add(r["key"])
            t["keys"][r["key"]] += 1
            t["sample"] = t["sample"] or r["msg"]
    rows = []
    for name, t in T.items():
        keys = [k for k in t["keys"] if k]
        t["usage"] = max([census.get(k, {}).get("vis", 0) for k in keys] + [0])
        rows.append({"topic": name, "layer": t["layer"], "blocks": t["blocks"], "anywhere": t["anywhere"],
                     "usage": t["usage"], "isolatedRefused": sorted(t["isolated"]),
                     "elements": [k for k, _ in t["keys"].most_common(8) if k], "sample": t["sample"],
                     "examples": t["examples"]})
    rows.sort(key=lambda r: (r["layer"] == "station", -r["blocks"], -r["anywhere"], -r["usage"],
                             -len(r["isolatedRefused"])))
    json.dump({"topics": rows, "unmatchedFirstRefusals": unmatched.most_common(60)},
              open(out, "w", encoding="utf-8"), indent=1)
    for r in rows:
        print("%3d %3d %4d %4d  %-9s %s" % (r["blocks"], r["anywhere"], r["usage"], len(r["isolatedRefused"]),
                                          r["layer"], r["topic"]))
    print("unmatched:")
    for m, c in unmatched.most_common(40):
        print(c, m)


if __name__ == "__main__":
    main(*sys.argv[1:4])
