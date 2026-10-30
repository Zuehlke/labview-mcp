import json, glob, os, collections, datetime, statistics
root=os.path.expanduser("~/.claude/projects/C--Projects-LabVIEWMCP")
fs=glob.glob(os.path.join(root,"*.jsonl"))+glob.glob(os.path.join(root,"*","subagents","*.jsonl"))
def ts(s): return datetime.datetime.fromisoformat(s.replace("Z","+00:00"))
calls=collections.Counter(); tooltime=collections.Counter(); turnlat=collections.Counter(); outtok=collections.Counter()
pairs=collections.Counter(); total_lat=0; total_tool=0
for f in fs:
    started={}; prev_user=None; last_tool=None; seen=set()
    for line in open(f,encoding="utf-8"):
        try:o=json.loads(line)
        except:continue
        t=o.get("timestamp"); m=o.get("message")
        if not t or not isinstance(m,dict) or not isinstance(m.get("content"),list): continue
        T=ts(t)
        if o.get("type")=="assistant":
            u=m.get("usage") or {}
            for c in m["content"]:
                if isinstance(c,dict) and c.get("type")=="tool_use":
                    n=c["name"].replace("mcp__labview__","")
                    if n=="Bash":
                        cmd=c["input"].get("command","")
                        n="Bash:"+("python" if "python" in cmd else "grep" if "grep" in cmd else "cat/sed" if any(k in cmd for k in("cat ","sed ","head ")) else "git" if "git " in cmd else "dotnet" if "dotnet" in cmd or ".ps1" in cmd else "other")
                    calls[n]+=1; started[c["id"]]=(n,T)
                    if prev_user and m.get("id") not in seen:
                        dt=(T-prev_user).total_seconds()
                        if 0<dt<600: turnlat[n]+=dt; total_lat+=dt
                        seen.add(m.get("id")); outtok[n]+=u.get("output_tokens") or 0
                    if last_tool==n: pairs[n]+=1
                    last_tool=n
        elif o.get("type")=="user":
            for c in m["content"]:
                if isinstance(c,dict) and c.get("type")=="tool_result" and c.get("tool_use_id") in started:
                    n,T0=started.pop(c["tool_use_id"]); dt=(T-T0).total_seconds()
                    if 0<=dt<3600: tooltime[n]+=dt; total_tool+=dt
            prev_user=T
print(f"model latency {total_lat/3600:.1f} h, inside tools {total_tool/3600:.1f} h")
print(f"{'tool':38s}{'calls':>7s}{'repeat%':>8s}{'lat h':>7s}{'tool h':>7s}")
for n,_ in sorted(calls.items(), key=lambda kv: -(turnlat[kv[0]]+tooltime[kv[0]]))[:25]:
    print(f"{n:38s}{calls[n]:7d}{100*pairs[n]/calls[n]:7.0f}%{turnlat[n]/3600:7.1f}{tooltime[n]/3600:7.1f}")
