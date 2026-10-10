# -*- coding: utf-8 -*-
"""Load every VI coverage.py --build wrote (DIR\\composed.txt) in LabVIEW over COM and count
how many are executable. A broken one is listed with the NI export it was composed from.

    cov_load.py DIR

A composed VI can KILL LabVIEW on load (a fatal insanity - measured on cov7, three of 152). The
VI whose load answered "The remote procedure call failed" is recorded as CRASH, LabVIEW is
restarted through lv-restart.ps1 and the sweep goes on after it - where it used to stop counting
and list every later VI as "RPC server is unavailable".
"""
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEAD = ("RPC server is unavailable", "Object variable or With block variable not set")


def restart():
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-restart.ps1")], capture_output=True, text=True,
                       timeout=300, stdin=subprocess.DEVNULL)
    print("[%s]" % (r.stdout + r.stderr).strip(), file=sys.stderr, flush=True)


def source_name(src):
    meta = src[:-4] + ".json"
    return json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else src


def main(argv):
    d = argv[0]
    rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(d, "composed.txt"), encoding="utf-8") if l.strip()]
    ok, bad, crashed = 0, [], []
    i, retried = 0, False
    while i < len(rows):
        chunk = rows[i:i + 20]           # twenty VIs per COM session
        try:
            r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                os.path.join(HERE, "lv-com.ps1"), "-ViPath", "|".join(v for v, _ in chunk)],
                               capture_output=True, text=True, timeout=600, stdin=subprocess.DEVNULL)
            out = r.stdout
        except subprocess.TimeoutExpired as ex:
            out = ex.stdout.decode(errors="replace") if isinstance(ex.stdout, bytes) else (ex.stdout or "")
        states = re.findall(r"=== (.*)\r?\n\s+(?:ExecState = (\S+)|GetVIReference FAILED: (.*))", out)
        done = 0
        restart_needed = False
        for (vi, state, fail), (_, src) in zip(states, chunk):
            if fail and any(m in fail for m in DEAD):
                restart_needed = True       # LabVIEW was already gone: this VI was not judged
                break
            done += 1
            if state == "1":
                ok += 1
            elif fail and "remote procedure call failed" in fail:
                crashed.append((os.path.basename(vi), source_name(src)))
                restart_needed = True
                break
            else:
                bad.append((os.path.basename(vi), state or "load failed: " + fail[:60], source_name(src)))
        if done == 0 and restart_needed and retried:
            # LabVIEW would not come back for this VI either: judge it a crash and move on
            crashed.append((os.path.basename(chunk[0][0]), source_name(chunk[0][1])))
            done = 1
        if done == 0 and not restart_needed and not states:
            done = len(chunk)               # no answer at all: count the chunk as unjudged
            for v, src in chunk:
                bad.append((os.path.basename(v), "no answer", source_name(src)))
        retried = restart_needed and done == 0
        if restart_needed:
            restart()
        i += done
    print("%d of %d composed NI VIs are executable (ExecState 1), %d crashed LabVIEW" % (ok, len(rows), len(crashed)))
    for vi, name in crashed:
        print("  %s  CRASH  %s" % (vi, name))
    for vi, st, name in bad:
        print("  %s  %s  %s" % (vi, st, name))


if __name__ == "__main__":
    main(sys.argv[1:])
