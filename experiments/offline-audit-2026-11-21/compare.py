"""Compare wl_port facts against pylabview's full parse for every ref/<tag>_NNNN folder."""
import glob, json, os, re, sys
import xml.etree.ElementTree as ET
S = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, S)
import wl_port

def ref_facts(d):
    root = ET.parse(os.path.join(d, "vi.xml")).getroot()
    lvsr = root.find("LVSR/Section")
    ex2 = lvsr.find("Execution2").attrib; ins = lvsr.find("Instrument").attrib
    f = {"sourceOnly": ex2["SourceOnly"] == "1", "autoErr": ex2["DefaultErrorHandling"] == "1",
         "debug": ins["DebugCapable"] == "1", "breakpoints": ins["InStBit29"] == "1"}
    s = root.find("STRG/Section/String")
    txt = "".join(s.itertext()) if s is not None else ""
    f["desc"] = bool(txt.replace(" ", "").strip())
    conp = root.find("CONP/Section/TypeDesc")
    vctp = root.find("VCTP/Section")
    if conp is not None and vctp is not None:
        tid = int(conp.get("TypeID"))
        top = {int(t.get("Index")): int(t.get("FlatTypeID")) for t in vctp.find("TopLevel")}
        flat = [t for t in vctp if t.tag == "TypeDesc"]
        td = flat[top[tid]]
        if td.get("Type") == "Function":
            f["pattern"] = int(td.get("Pattern"), 0) >> 3
    bd = os.path.join(d, "vi_BDHb.xml")
    if os.path.exists(bd):
        b = ET.parse(bd).getroot()
        icons = [int(e.find("objFlags").text) & 0x04000000 != 0
                 for e in b.iter("SL__arrayElement") if e.get("class") == "fPTerm" and e.find("objFlags") is not None]
        f["termIcon"] = sum(icons); f["termData"] = len(icons) - sum(icons)
    return f

rows = []; diffs = []
for d in sorted(glob.glob(os.path.join(S, "ref", "*_sample_*"))):
    src = open(os.path.join(d, "src.txt"), encoding="utf-8").read().strip()
    a = wl_port.read_vi(src); b = ref_facts(d)
    for k, v in b.items():
        rows.append(k)
        if a.get(k) != v:
            diffs.append((os.path.basename(d), k, a.get(k), v, src))
print("compared fields:", len(rows), "files:", len(glob.glob(os.path.join(S, "ref", "*_sample_*"))))
print("diffs:", len(diffs))
for x in diffs: print(x)
