#!/usr/bin/env python3
"""Server side of the Step 14.1 rendezvous measurement (Linux; stdlib only).

    rendezvous-server.py --dir DIR --result FILE [--file NAME] [--timeout S] [--accept-wrong-token]

Plays the MCP server's part for ONE connection:
  1. creates DIR (mode 0700) and, in it, the rendezvous file NAME (default rendezvous.txt, mode 0600,
     written by atomic rename) holding `<port>-<token>`: the port of a listener on 127.0.0.1 (OS-chosen)
     and a random 128-bit token (hex);
  2. waits for a stub VI to connect and send `hello-<token>|ver=..|stat=..|path=..|args=..|tick=..|end`;
  3. writes one JSON line to --result: whether a connection arrived, whether its token matched, the
     rendezvous file and directory modes, the evidence fields, and every connection seen (a connection
     with the WRONG token is recorded and does not end the wait; that is how a crossed pair shows up).
Exit 0 when a connection with the right token arrived, 1 otherwise.
The port is printed as `PORT <n>` and the rendezvous path as `RENDEZVOUS <path>` once ready.
"""
import argparse
import json
import os
import secrets
import socket
import stat
import time


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--result", required=True)
    ap.add_argument("--file", default="rendezvous.txt")
    ap.add_argument("--timeout", type=float, default=60)
    a = ap.parse_args()

    os.makedirs(a.dir, mode=0o700, exist_ok=True)
    os.chmod(a.dir, 0o700)
    srv = socket.socket()
    srv.bind(("127.0.0.1", 0))
    srv.listen(8)
    port = srv.getsockname()[1]
    token = secrets.token_hex(16)
    path = os.path.join(a.dir, a.file)
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)   # owner-only from creation
    with os.fdopen(fd, "w") as f:
        f.write("%d-%s" % (port, token))
    os.replace(tmp, path)
    res = {"dir": a.dir, "rendezvous": path, "port": port,
           "dir_mode": oct(stat.S_IMODE(os.stat(a.dir).st_mode)),
           "file_mode": oct(stat.S_IMODE(os.stat(path).st_mode)),
           "connections": [], "matched": False}
    print("PORT %d" % port, flush=True)
    print("RENDEZVOUS %s" % path, flush=True)

    t0 = time.monotonic()
    deadline = t0 + a.timeout
    while time.monotonic() < deadline and not res["matched"]:
        srv.settimeout(max(0.1, deadline - time.monotonic()))
        try:
            conn, peer = srv.accept()
        except socket.timeout:
            break
        conn.settimeout(10)
        buf = b""
        try:
            while not buf.endswith(b"|end"):
                chunk = conn.recv(4096)
                if not chunk:
                    break
                buf += chunk
        except socket.timeout:
            pass
        conn.close()
        msg = buf.decode("utf-8", "replace")
        fields = dict(p.split("=", 1) for p in msg.split("|")[1:] if "=" in p)
        got = msg.split("|", 1)[0]
        c = {"after_s": round(time.monotonic() - t0, 2), "peer": "%s:%d" % peer,
             "token_ok": got == "hello-" + token, "token_seen": got.replace("hello-", "", 1),
             "pid": fields.get("stat", "").split(" ", 1)[0], "ver": fields.get("ver"),
             "stat": fields.get("stat"), "vi_path": fields.get("path"), "args": fields.get("args"),
             "tick": fields.get("tick"), "raw_len": len(buf)}
        res["connections"].append(c)
        res["matched"] = c["token_ok"]
    res["token"] = token
    res["waited_s"] = round(time.monotonic() - t0, 2)
    with open(a.result, "w") as f:
        f.write(json.dumps(res) + "\n")
    print("RESULT matched=%s connections=%d waited_s=%s" % (res["matched"], len(res["connections"]), res["waited_s"]), flush=True)
    return 0 if res["matched"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
