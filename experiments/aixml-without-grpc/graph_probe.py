# -*- coding: utf-8 -*-
"""Load a composed GRAPH VI and report what LabVIEW logs and whether COM still answers - the check
graphs fail today (2026-10-10: `bad mutex verifier in ThMutexDestroy`, then every COM call timed
out until LabVIEW was restarted).

    graph_probe.py <in.xml | in.vi> [--no-graphs]

An .xml is composed first with AIXML2VI_GRAPHS=1 (or without, with --no-graphs, as the control). Then:
load over COM, a SECOND COM call with a deadline (the hang shows there), and every line LabVIEW's
log gained meanwhile.
"""
import glob
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # since 2026-10-12
PY = sys.executable


def log_path():
    logs = glob.glob(os.path.join(os.environ.get("TEMP", ""), "LabVIEW_*_cur.txt"))
    return max(logs, key=os.path.getmtime) if logs else None


def com_state(vi, timeout):
    try:
        r = subprocess.run(["powershell", "-ExecutionPolicy", "Bypass", "-File", os.path.join(HERE, "lv-com.ps1"),
                            "-ViPath", vi], capture_output=True, text=True, timeout=timeout)
        return [l.strip() for l in r.stdout.splitlines() if "ExecState" in l or "FAILED" in l] or [r.stdout.strip()[-200:]]
    except subprocess.TimeoutExpired:
        return ["COM TIMEOUT after %d s" % timeout]


def main(argv):
    src = argv[0]
    vi = src
    if src.lower().endswith(".xml"):
        out = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "graphs")
        os.makedirs(out, exist_ok=True)
        vi = os.path.join(out, "%s %s.vi" % (os.path.splitext(os.path.basename(src))[0], time.strftime("%H%M%S")))
        env = dict(os.environ)
        if "--no-graphs" not in argv:
            env["AIXML2VI_GRAPHS"] = "1"
        r = subprocess.run([PY, os.path.join(COMPOSER, "aixml2vi.py"), src, vi], capture_output=True, text=True, env=env)
        print("\n".join(l for l in (r.stdout + r.stderr).splitlines() if "style" in l or "UNSUPPORTED" in l or "wrote" in l))
        if not os.path.exists(vi):
            return 1
    lp = log_path()
    before = os.path.getsize(lp) if lp else 0
    print("load  :", com_state(vi, 60))
    time.sleep(2)
    print("again :", com_state(vi, 30))
    lp2 = log_path()
    if lp2:
        with open(lp2, encoding="utf-8", errors="replace") as f:
            if lp2 == lp:
                f.seek(before)
            new = f.read()
        for l in new.splitlines():
            if l.strip() and not l.startswith("#"):
                print("LOG", l[:240])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
