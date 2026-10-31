import json, glob, os, collections, re
root = os.path.expanduser("~/.claude/projects/C--Projects-LabVIEWMCP")
files = glob.glob(os.path.join(root, "*.jsonl")) + glob.glob(os.path.join(root, "*", "subagents", "*.jsonl"))
cat = collections.Counter(); n = collections.Counter()
rv_sample = None
def text_of(c):
    if isinstance(c, str): return c
    if isinstance(c, list): return "".join(x.get("text","") for x in c if isinstance(x, dict))
    return ""
def classify(cmd):
    c = cmd.lower()
    for key, pat in [("pylv/heap xml", r"pylv|heap|\.vi\.xml|lvheap|bdhp"), ("dotnet build/test", r"dotnet|build\.ps1|run-tests"),
                     ("git", r"^\s*(cd [^&]*&&\s*)?git "), ("python other", r"python"), ("grep/rg", r"\bgrep\b|\brg\b"),
                     ("cat/head/sed", r"\b(cat|head|tail|sed)\b"), ("curl/gh", r"\bgh\b|curl")]:
        if re.search(pat, c): return key
    return "other"
for f in files:
    pend = {}
    for line in open(f, encoding="utf-8"):
        try: o = json.loads(line)
        except: continue
        m = o.get("message")
        if not isinstance(m, dict) or not isinstance(m.get("content"), list): continue
        for c in m["content"]:
            if not isinstance(c, dict): continue
            if c.get("type") == "tool_use":
                if c.get("name") == "Bash": pend[c["id"]] = ("bash", classify(c["input"].get("command","")))
                elif c.get("name") == "mcp__labview__lvai_run_vi_and_read_values": pend[c["id"]] = ("rv", "")
            elif c.get("type") == "tool_result" and c.get("tool_use_id") in pend:
                k, cl = pend[c["tool_use_id"]]; t = text_of(c.get("content"))
                if k == "bash": cat[cl] += len(t); n[cl] += 1
                elif rv_sample is None and 3000 < len(t) < 8000: rv_sample = t
tot = sum(cat.values())
for k, v in cat.most_common(): print(f"{k:22s} {v/1e6:6.2f} M  {100*v/tot:5.1f} %  calls {n[k]}  avg {v//max(n[k],1)}")
open("rv_sample.txt","w",encoding="utf-8").write(rv_sample or "")
