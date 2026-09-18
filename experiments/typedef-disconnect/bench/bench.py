"""Cold build of a project carrying EVERY typedef variant, with the clock on every step.

One server process and one LabVIEW for the whole run, so what is measured is machine time -
the server, LabVIEW and pylabview - with no model-turn latency mixed in. The number of round
trips is reported separately, because `docs/workflow-economics.md` measures a turn at a
median of 7.1 s and that is the dominant cost of a real session, not the milliseconds here.

The variants, all in one project:
  strict typedef enum                     Mode.ctl
  strict typedef holding another typedef  Limits.ctl  -> Mode.ctl      (two levels)
  PLAIN (non-strict) typedef              Reading.ctl
  an ARRAY whose element is a typedef     Acquire.vi  `Samples`
  a LabVIEW CLASS terminal                Sensor.lvclass accessors
  a typedef field inside class private data
"""
import json
import queue
import re
import subprocess
import sys
import threading
import time

EXE = r"C:\Projects\LabVIEWMCP\src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe"
ROOT = r"C:\temp\MixedRig"
AIXML = ROOT + r"\aixml"
BIND = r"C:\temp\TypedefStub\lvai_typedef_bind.vi"
TREE = r"C:\temp\TypedefStub\lvai_typedef_tree.vi"

proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
inbox = queue.Queue()
threading.Thread(target=lambda: [inbox.put(l.strip()) for l in proc.stdout if l.strip()],
                 daemon=True).start()

_id = [10]
timings = []


def rpc(method, params, timeout=900):
    _id[0] += 1
    mid = _id[0]
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": mid, "method": method,
                                 "params": params}) + "\n")
    proc.stdin.flush()
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            msg = json.loads(inbox.get(timeout=1))
        except (queue.Empty, json.JSONDecodeError):
            continue
        if msg.get("id") == mid:
            return msg
    raise TimeoutError(f"no answer to {method} within {timeout}s")


def call(label, tool, args, timeout=900):
    """One MCP tool call, timed. Returns the parsed answer."""
    t0 = time.perf_counter()
    msg = rpc("tools/call", {"name": tool, "arguments": args}, timeout)
    wall = (time.perf_counter() - t0) * 1000
    text = "".join(p.get("text", "") for p in msg.get("result", {}).get("content", []))
    try:
        answer = json.loads(text)
    except json.JSONDecodeError:
        answer = {"_text": text}
    inner = answer.get("elapsedMs") or answer.get("totalElapsedMs")
    timings.append((label, tool, wall, inner))
    flag = ""
    if answer.get("ok") is False or answer.get("errorKind"):
        flag = "  <<< " + str(answer.get("errorKind") or answer.get("error"))[:120]
    print(f"  {wall:8.0f} ms  {label}{flag}")
    return answer


def local(label, fn):
    """One step that never reaches LabVIEW - a pylabview flag patch."""
    t0 = time.perf_counter()
    fn()
    wall = (time.perf_counter() - t0) * 1000
    timings.append((label, "(local)", wall, None))
    print(f"  {wall:8.0f} ms  {label}")


def patch_flags(main_xml, strict=True):
    """AIXML writes a Standard VI; these three attributes make it a typedef. No LabVIEW."""
    text = open(main_xml, encoding="utf-8").read()
    text, n1 = re.subn(r'(<Instrument[^>]*?Type=")Standard(")', r"\1Control\2", text)
    # `TypeDefVI="0"` is a SUBSTRING of `StrictTypeDefVI="0"` - anchor or both get patched.
    text, n2 = re.subn(r'(?<!Strict)(TypeDefVI=")0(")', r"\g<1>1\2", text)
    n3 = 0
    if strict:
        text, n3 = re.subn(r'(StrictTypeDefVI=")0(")', r"\g<1>1\2", text)
    open(main_xml, "w", encoding="utf-8", newline="").write(text)
    if not (n1 and n2) or (strict and not n3):
        raise SystemExit(f"flag patch matched nothing in {main_xml}")


rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                   "clientInfo": {"name": "bench", "version": "1"}}, 60)
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()

exec(open(sys.argv[1], encoding="utf-8").read())

print()
print(f"{'ms':>9}  {'tool ms':>8}  step")
total = inner_total = 0.0
for label, tool, wall, inner in timings:
    total += wall
    inner_total += inner or 0
    print(f"{wall:9.0f}  {(f'{inner:.0f}' if inner else '-'):>8}  {label}")
print(f"{total:9.0f}  {inner_total:8.0f}  TOTAL over {len(timings)} step(s)")
print(f"round trips to the server: {sum(1 for t in timings if t[1] != '(local)')}")

proc.stdin.close()
proc.terminate()
