"""Call one tool of the BUILT server over raw stdio and print its answer.

A client fetches the tool list once, at session start, so a tool added in a session cannot be
called from that session's client. Driving the exe directly is how a new tool is accepted before
the restart - the same route docs/tool-argument-errors.md uses.

    python call_tool.py lvai_graft_diagram @args.json
"""
import json
import queue
import subprocess
import sys
import threading
import time

EXE = r"C:\Projects\LabVIEWMCP\src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe"

tool = sys.argv[1]
args = json.load(open(sys.argv[2][1:], encoding="utf-8")) if sys.argv[2].startswith("@") else json.loads(sys.argv[2])
proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
inbox = queue.Queue()
threading.Thread(target=lambda: [inbox.put(l.strip()) for l in proc.stdout if l.strip()],
                 daemon=True).start()


def rpc(mid, method, params, timeout=900):
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": mid, "method": method, "params": params}) + "\n")
    proc.stdin.flush()
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            msg = json.loads(inbox.get(timeout=1))
        except (queue.Empty, json.JSONDecodeError):
            continue
        if msg.get("id") == mid:
            return msg
    raise TimeoutError(method)


rpc(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                      "clientInfo": {"name": "call_tool", "version": "1"}})
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()
t0 = time.perf_counter()
answer = rpc(2, "tools/call", {"name": tool, "arguments": args})
text = "".join(p.get("text", "") for p in answer.get("result", {}).get("content", []))
print(text or json.dumps(answer, indent=2))
print(f"-- {(time.perf_counter() - t0):.1f} s wall", file=sys.stderr)
proc.stdin.close()
proc.terminate()
