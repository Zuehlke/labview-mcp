import json, glob, os, collections
root = os.path.expanduser("~/.claude/projects/C--Projects-LabVIEWMCP")
files = glob.glob(os.path.join(root, "*.jsonl")) + glob.glob(os.path.join(root, "*", "subagents", "*.jsonl"))
served = collections.Counter(); relaxed = collections.Counter(); compact = collections.Counter(); unparsed = 0
def text_of(c):
    if isinstance(c, str): return c
    if isinstance(c, list): return "".join(x.get("text","") for x in c if isinstance(x, dict))
    return ""
for f in files:
    pend = {}
    for line in open(f, encoding="utf-8"):
        try: o = json.loads(line)
        except: continue
        m = o.get("message")
        if not isinstance(m, dict) or not isinstance(m.get("content"), list): continue
        for c in m["content"]:
            if not isinstance(c, dict): continue
            if c.get("type") == "tool_use" and c.get("name","").startswith("mcp__labview__"):
                pend[c["id"]] = c["name"].replace("mcp__labview__","")
            elif c.get("type") == "tool_result" and c.get("tool_use_id") in pend:
                t = text_of(c.get("content")); name = pend[c["tool_use_id"]]
                try: j = json.loads(t)
                except Exception: unparsed += len(t); continue
                served[name] += len(t)
                relaxed[name] += len(json.dumps(j, ensure_ascii=False, indent=2))
                compact[name] += len(json.dumps(j, ensure_ascii=False, separators=(",",":")))
S, R, C = sum(served.values()), sum(relaxed.values()), sum(compact.values())
print(f"JSON answers: served {S/1e6:.2f} M, relaxed escaping {R/1e6:.2f} M ({100*(1-R/S):.1f} % less), +compact {C/1e6:.2f} M ({100*(1-C/S):.1f} % less); non-JSON {unparsed/1e6:.2f} M")
rows = sorted(served, key=lambda k: served[k]-relaxed[k], reverse=True)[:12]
for k in rows: print(f"  {k:38s} {served[k]/1e6:6.2f} M -> {relaxed[k]/1e6:6.2f} M  ({100*(1-relaxed[k]/served[k]):4.1f} %)")
