#!/usr/bin/env python3
"""Server side of the Step 14.1 round-3 measurement: one rendezvous, then a long-lived echo session (Linux; stdlib only).

    echo-server.py --dir DIR --result FILE [--file NAME] [--trigger FILE] [--echoes N] [--timeout S] [--status FILE]

Like rendezvous-server.py (directory 0700, rendezvous file 0600 holding `<port>-<token>`, listener on 127.0.0.1), but the
connection is KEPT after the hello (`hello-<token>|ver=..|stat=..|path=..|args=..|tick=..|end`). Events are appended to
--status as `<seconds since start> <event>` lines (LISTENING, HELLO pid=<pid> token_ok=<bool>, ECHO-START, ECHO-DONE ...).
After the hello it sends one echo at once (the session works), then waits until the --trigger file exists (the script
touches it N seconds after the relay is gone), then sends N echo frames (4-byte big-endian length, then the payload; the
target must send the same frame back) and counts failures. The JSON result has every number. Exit 0 only when the token
matched, the first echo came back and all N echoes came back with 0 failures.
"""
import argparse, json, os, secrets, socket, stat, struct, time


def recv_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("peer closed after %d of %d bytes" % (len(buf), n))
        buf += chunk
    return buf


def roundtrip(sock, payload):
    sock.sendall(struct.pack(">I", len(payload)) + payload)
    (n,) = struct.unpack(">I", recv_exact(sock, 4))
    return recv_exact(sock, n)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--result", required=True)
    ap.add_argument("--file", default="rendezvous.txt")
    ap.add_argument("--trigger", default="")
    ap.add_argument("--status", default="")
    ap.add_argument("--echoes", type=int, default=100)
    ap.add_argument("--timeout", type=float, default=240)
    a = ap.parse_args()
    t0 = time.monotonic()

    def ev(msg):
        if a.status:
            with open(a.status, "a") as f:
                f.write("%.2f %s\n" % (time.monotonic() - t0, msg))

    os.makedirs(a.dir, mode=0o700, exist_ok=True)
    os.chmod(a.dir, 0o700)
    srv = socket.socket(); srv.bind(("127.0.0.1", 0)); srv.listen(8)
    port = srv.getsockname()[1]; token = secrets.token_hex(16)
    path = os.path.join(a.dir, a.file); tmp = path + ".tmp"
    with os.fdopen(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as f:
        f.write("%d-%s" % (port, token))
    os.replace(tmp, path)
    res = {"rendezvous": path, "port": port, "dir_mode": oct(stat.S_IMODE(os.stat(a.dir).st_mode)),
           "file_mode": oct(stat.S_IMODE(os.stat(path).st_mode)), "token_ok": False, "pid": None, "vi_path": None,
           "first_echo_ok": False, "echoes_requested": a.echoes, "echoes_ok": 0, "failures": 0, "error": None}
    ev("LISTENING %d" % port)
    print("PORT %d\nRENDEZVOUS %s" % (port, path), flush=True)
    conn = None
    deadline = t0 + a.timeout
    try:
        while time.monotonic() < deadline and not res["token_ok"]:
            srv.settimeout(max(0.1, deadline - time.monotonic()))
            try:
                c, peer = srv.accept()
            except socket.timeout:
                break
            c.settimeout(10); buf = b""
            while not buf.endswith(b"|end"):
                chunk = c.recv(4096)
                if not chunk:
                    break
                buf += chunk
            msg = buf.decode("utf-8", "replace"); got = msg.split("|", 1)[0]
            fields = dict(p.split("=", 1) for p in msg.split("|")[1:] if "=" in p)
            ok = got == "hello-" + token
            ev("HELLO pid=%s token_ok=%s" % (fields.get("stat", "").split(" ", 1)[0], ok))
            if ok:
                res.update(token_ok=True, pid=fields.get("stat", "").split(" ", 1)[0], vi_path=fields.get("path"),
                           ver=fields.get("ver"), connect_s=round(time.monotonic() - t0, 2)); conn = c
            else:
                c.close()
        if conn is None:
            res["error"] = "no connection with the right token"
        else:
            conn.settimeout(30)
            res["first_echo_ok"] = roundtrip(conn, b"first-echo") == b"first-echo"
            ev("FIRST-ECHO %s" % res["first_echo_ok"])
            while a.trigger and not os.path.exists(a.trigger) and time.monotonic() < deadline:
                time.sleep(0.2)
            ev("ECHO-START")
            conn.settimeout(10)
            for i in range(a.echoes):
                p = ("echo-%d-" % i).encode() + os.urandom(6).hex().encode()
                try:
                    if roundtrip(conn, p) == p:
                        res["echoes_ok"] += 1
                    else:
                        res["failures"] += 1
                except Exception as ex:
                    res["failures"] += 1; res["error"] = repr(ex)
                    break
            ev("ECHO-DONE ok=%d failures=%d" % (res["echoes_ok"], res["failures"]))
            conn.close(); ev("CLOSED")
    finally:
        res["total_s"] = round(time.monotonic() - t0, 1)
        with open(a.result, "w") as f:
            f.write(json.dumps(res) + "\n")
    good = res["token_ok"] and res["first_echo_ok"] and res["echoes_ok"] == a.echoes and not res["failures"]
    print("RESULT ok=%s echoes=%d failures=%d" % (good, res["echoes_ok"], res["failures"]), flush=True)
    return 0 if good else 1


if __name__ == "__main__":
    raise SystemExit(main())
