#!/usr/bin/env python3
"""mcp_drive.py - drive one LabVIEWMCP.exe over raw stdio, with no MCP client in between.

The acceptance route for a branch build: the client fetches its tool list once per session,
so a tool or parameter added by the branch cannot be tried from it, and a client strips keys
it does not know (docs/tool-argument-errors.md). This talks to the exe directly - initialize,
then one tools/call per entry of a calls file, all in ONE server process - and prints each
answer's text. Python 3 standard library only.

    python -I mcp_drive.py <LabVIEWMCP.exe> <calls.json> [<private TEMP dir>]

calls.json is a list. Each entry is one call, or {"parallel": [...]} to send several before
reading any answer (how the exec-state/run race of docs/error-list.md 6e was reproduced):

    [
      {"label": "status", "name": "lvai_status", "arguments": {}},
      {"parallel": [
        {"name": "lvai_exec_state", "arguments": {"viPath": "C:\\\\x\\\\A.vi"}},
        {"name": "lvai_run_vi_and_read_values", "arguments": {"viPath": "C:\\\\x\\\\A.vi"}}]}
    ]

THE PRIVATE TEMP. With a third argument the exe runs with TMP/TEMP set to that folder. Every
cache path goes through Path.GetTempPath(), so a branch's helper VIs then never overwrite the
installed plugin's in %TEMP%\\LabVIEWMCP. Two consequences, both measured: a LabVIEW STARTED
by that exe inherits the private TEMP (restart it from a normal shell afterwards), and
lvai_status reads LabVIEW's crash logs from TEMP, so it finds none there - run status calls
without the third argument.

Write calls.json with an editor or a file-writing tool, never through a shell heredoc: the
backslashes in Windows paths are eaten on the way.

Exit code 0 when every call got an answer (an answer may still report ok: false); 1 when the
server closed its output early.
"""
import json
import os
import subprocess
import sys


def main(argv):
    if len(argv) < 3 or argv[1] in ("-h", "--help"):
        print(__doc__)
        return 0 if len(argv) > 1 else 2
    exe, calls_path = argv[1], argv[2]
    private_temp = argv[3] if len(argv) > 3 else None

    env = dict(os.environ)
    if private_temp:
        os.makedirs(private_temp, exist_ok=True)
        env["TMP"] = env["TEMP"] = private_temp

    proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, env=env, text=True,
                            encoding="utf-8", bufsize=1)
    next_id = [0]

    def send(msg):
        proc.stdin.write(json.dumps(msg) + "\n")
        proc.stdin.flush()

    def request(method, params):
        next_id[0] += 1
        send({"jsonrpc": "2.0", "id": next_id[0], "method": method, "params": params})
        return next_id[0]

    def read_until(ids):
        got = {}
        while len(got) < len(ids):
            line = proc.stdout.readline()
            if not line:
                return None
            msg = json.loads(line)
            if msg.get("id") in ids:
                got[msg["id"]] = msg
        return got

    def text_of(msg):
        if "error" in msg:
            return "RPC ERROR: " + json.dumps(msg["error"])
        return "\n".join(c.get("text", "") for c in msg["result"].get("content", []))

    if read_until([request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                          "clientInfo": {"name": "mcp_drive", "version": "1"}})]) is None:
        print("the server closed its output before answering initialize", file=sys.stderr)
        return 1
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    with open(calls_path, encoding="utf-8") as f:
        calls = json.load(f)

    for entry in calls:
        batch = entry["parallel"] if "parallel" in entry else [entry]
        ids = [request("tools/call", {"name": c["name"], "arguments": c.get("arguments", {})})
               for c in batch]
        answers = read_until(ids)
        if answers is None:
            print("the server closed its output mid-run", file=sys.stderr)
            return 1
        for c, i in zip(batch, ids):
            print("=" * 20, c.get("label", c["name"]), "=" * 20)
            print(text_of(answers[i]))
            sys.stdout.flush()

    proc.stdin.close()
    proc.wait(timeout=30)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
