import json, glob, os, sys, collections, re

root = os.path.expanduser(r"~/.claude/projects/C--Projects-LabVIEWMCP")
files = glob.glob(os.path.join(root, "*.jsonl")) + glob.glob(os.path.join(root, "*", "subagents", "*.jsonl"))

out_chars = collections.Counter()   # tool result chars per tool
out_calls = collections.Counter()
in_chars = collections.Counter()    # tool_use input chars per tool
read_kind = collections.Counter()   # Read/grep results by target extension
write_kind = collections.Counter()  # Write content by extension
usage = collections.Counter()

def text_of(c):
    if isinstance(c, str):
        return c
    if isinstance(c, list):
        return "".join(x.get("text", "") for x in c if isinstance(x, dict))
    return ""

for f in files:
    pending = {}
    try:
        fh = open(f, encoding="utf-8")
    except OSError:
        continue
    for line in fh:
        try:
            o = json.loads(line)
        except Exception:
            continue
        m = o.get("message")
        if not isinstance(m, dict):
            continue
        u = m.get("usage")
        if isinstance(u, dict) and o.get("type") == "assistant":
            for k in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens"):
                usage[k] += u.get(k) or 0
        content = m.get("content")
        if not isinstance(content, list):
            continue
        for c in content:
            if not isinstance(c, dict):
                continue
            if c.get("type") == "tool_use":
                name = c.get("name", "?")
                inp = c.get("input", {})
                s = json.dumps(inp, ensure_ascii=False)
                in_chars[name] += len(s)
                tgt = ""
                if name in ("Read", "Write", "Edit"):
                    tgt = os.path.splitext(inp.get("file_path", ""))[1].lower()
                if name == "Write":
                    write_kind[tgt] += len(inp.get("content", ""))
                pending[c.get("id")] = (name, tgt)
            elif c.get("type") == "tool_result":
                name, tgt = pending.get(c.get("tool_use_id"), ("?", ""))
                n = len(text_of(c.get("content")))
                out_chars[name] += n
                out_calls[name] += 1
                if name == "Read":
                    read_kind[tgt] += n

def top(counter, k=25):
    tot = sum(counter.values())
    for name, v in counter.most_common(k):
        print(f"  {name:55s} {v/1e6:8.2f} M chars  {100*v/tot:5.1f} %")
    print(f"  {'TOTAL':55s} {tot/1e6:8.2f} M chars")

print("files", len(files))
print("usage", dict(usage))
print("\nTOOL RESULTS (enter context) by tool:")
top(out_chars)
print("\nTOOL INPUTS (model output) by tool:")
top(in_chars, 15)
print("\nREAD results by extension:")
top(read_kind, 10)
print("\nWRITE content by extension:")
top(write_kind, 10)
pyl = {k: v for k, v in out_chars.items() if "pylv" in k}
print("\npylv results:", {k: (v, out_calls[k]) for k, v in pyl.items()})
