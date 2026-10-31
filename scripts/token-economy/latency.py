import json, glob, os, statistics, datetime
root=os.path.expanduser("~/.claude/projects/C--Projects-LabVIEWMCP")
fs=glob.glob(os.path.join(root,"*.jsonl"))
def ts(s): return datetime.datetime.fromisoformat(s.replace("Z","+00:00"))
rows=[]
for f in fs:
    prev=None; seen=set()
    for line in open(f,encoding="utf-8"):
        try:o=json.loads(line)
        except:continue
        t=o.get("timestamp"); m=o.get("message")
        if not t or not isinstance(m,dict): continue
        if o.get("type")=="user": prev=ts(t); continue
        if o.get("type")=="assistant" and m.get("usage") and m.get("id") not in seen and prev:
            seen.add(m.get("id")); u=m["usage"]
            ctx=(u.get("cache_read_input_tokens") or 0)+(u.get("cache_creation_input_tokens") or 0)+(u.get("input_tokens") or 0)
            out=u.get("output_tokens") or 0
            dt=(ts(t)-prev).total_seconds()
            if 0<dt<600: rows.append((ctx,out,dt))
print("requests",len(rows))
# bucket by context, only small outputs to isolate prefill
for lo,hi in [(0,100e3),(100e3,200e3),(200e3,300e3),(300e3,400e3),(400e3,500e3),(500e3,700e3),(700e3,1.1e6)]:
    for olo,ohi in [(0,200),(200,1000)]:
        d=[r[2] for r in rows if lo<=r[0]<hi and olo<=r[1]<ohi]
        if len(d)>50: print(f"ctx {int(lo/1e3):4d}-{int(hi/1e3):4d}k out {olo}-{ohi}: n={len(d):6d} median {statistics.median(d):5.1f}s")
