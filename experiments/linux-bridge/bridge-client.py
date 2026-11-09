#!/usr/bin/env python3
"""Echo client for the bridge skeleton (Slice 1.2). Runs inside the container (python3 3.10).

    bridge-client.py [--echoes N] [--connect-timeout S] [--rendezvous FILE]

Plays the server's part: listens on 127.0.0.1 with an OS-chosen port, publishes the port in the
rendezvous file (atomic rename), waits for the skeleton to connect, sends N length-prefixed echo
frames (4-byte big-endian length, then the payload) and checks each answer, then sends `probe`
and prints the reply. Exit 0 only when every echo came back byte for byte.
"""
import argparse
import os
import socket
import statistics
import struct
import time


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
    ap.add_argument("--echoes", type=int, default=100)
    ap.add_argument("--connect-timeout", type=float, default=180)
    ap.add_argument("--rendezvous", default="/tmp/lvmcp-skeleton-port.txt")
    a = ap.parse_args()

    srv = socket.socket()
    srv.bind(("127.0.0.1", 0))
    srv.listen(1)
    port = srv.getsockname()[1]
    tmp = a.rendezvous + ".tmp"
    with open(tmp, "w") as f:
        f.write(str(port))
    os.replace(tmp, a.rendezvous)
    print("listening 127.0.0.1:%d, rendezvous %s" % (port, a.rendezvous), flush=True)

    t_listen = time.monotonic()
    srv.settimeout(a.connect_timeout)
    try:
        conn, peer = srv.accept()
    except socket.timeout:
        print("RESULT echoes=0 failures=%d connect=timeout after %.0f s" % (a.echoes, a.connect_timeout))
        return 1
    connect_s = time.monotonic() - t_listen
    print("skeleton connected from %s:%d after %.1f s" % (peer[0], peer[1], connect_s), flush=True)
    conn.settimeout(10)

    failures, lat = 0, []
    t0 = time.monotonic()
    for i in range(1, a.echoes + 1):
        payload = ("echo-%d-" % i).encode() + os.urandom(8).hex().encode()
        t = time.monotonic()
        try:
            got = roundtrip(conn, payload)
            ok = got == payload
        except Exception as ex:  # a failed echo is a measurement, not a crash
            ok, got = False, repr(ex).encode()
        lat.append((time.monotonic() - t) * 1000)
        if not ok:
            failures += 1
            print("FAIL echo %d: sent %r got %r" % (i, payload, got), flush=True)
    total_ms = (time.monotonic() - t0) * 1000

    conn.settimeout(60)
    try:
        reply = roundtrip(conn, b"probe").decode(errors="replace")
    except Exception as ex:
        reply = "no reply: %r" % (ex,)
    print("PROBE %s" % reply, flush=True)
    conn.close()

    lat.sort()
    print("RESULT echoes=%d failures=%d connect_s=%.1f total_ms=%.0f per_echo_ms min=%.1f median=%.1f p95=%.1f max=%.1f"
          % (a.echoes, failures, connect_s, total_ms, lat[0], statistics.median(lat),
             lat[int(len(lat) * 0.95) - 1], lat[-1]))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
