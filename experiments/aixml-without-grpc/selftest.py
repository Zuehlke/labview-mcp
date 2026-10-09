# -*- coding: utf-8 -*-
"""Compose every tests/*.xml with aixml2vi.py, load and run each result in LabVIEW over COM
(lv-com.ps1 - no Nigel, no gRPC), and compare the outputs with tests/expect.json.

    selftest.py [name-filter] [--out DIR]

Every build gets a fresh file name: a LabVIEW instance keeps a VI in memory once loaded, and a
second VI under the same name would be the first one again.
"""
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
PY = sys.executable


def compose(src, vi):
    r = subprocess.run([PY, os.path.join(HERE, "aixml2vi.py"), src, vi, "--keep", vi[:-3] + ".bundle"],
                       capture_output=True, text=True, stdin=subprocess.DEVNULL)
    return r.returncode, (r.stdout + r.stderr).strip()


def lvcom(vi, inputs, outputs, run=True, timeout=60):
    cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
           os.path.join(HERE, "lv-com.ps1"), "-ViPath", vi]
    if run:
        cmd += ["-Run", "-Inputs", inputs, "-Outputs", ";".join(outputs)]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        out = r.stdout + r.stderr
    except subprocess.TimeoutExpired as ex:
        out = (ex.stdout or b"").decode(errors="replace") + "\n<TIMEOUT>"
    state = re.search(r"ExecState = (\S+)", out)
    got = dict(re.findall(r"  get (.+?) = (.*)", out))
    return (state.group(1) if state else None), got, out


def same(want, got):
    if got is None:
        return False
    try:
        return abs(float(want) - float(got)) < 1e-9 * max(1.0, abs(float(want)))
    except ValueError:
        return str(want).lower() == str(got).lower()


def main(argv):
    flt = [a for a in argv if not a.startswith("--")]
    out_dir = r"C:\temp\aixml-no-grpc\out"
    if "--out" in argv:
        out_dir = argv[argv.index("--out") + 1]
    os.makedirs(out_dir, exist_ok=True)
    expect = json.load(open(os.path.join(HERE, "tests", "expect.json"), encoding="utf-8"))
    stamp = time.strftime("%H%M%S")
    results = []
    for name in sorted(expect):
        if flt and not any(f in name for f in flt):
            continue
        src = os.path.join(HERE, "tests", name + ".xml")
        vi = os.path.join(out_dir, "%s_%s.vi" % (name, stamp))
        code, log = compose(src, vi)
        if code != 0:
            results.append((name, "COMPOSE FAILED", log.splitlines()[-1] if log else ""))
            continue
        cases = expect[name].get("cases", [])
        state, got, raw = lvcom(vi, "", [], run=False)
        if state != "1":
            results.append((name, "BROKEN (ExecState %s)" % state, vi))
            continue
        verdicts = []
        for case in cases:
            state, got, raw = lvcom(vi, case.get("inputs", ""), list(case["outputs"]))
            ok = all(same(v, got.get(k)) for k, v in case["outputs"].items())
            verdicts.append("%s %s -> %s" % ("ok" if ok else "FAIL", case.get("inputs", ""),
                                             ", ".join("%s=%s" % (k, got.get(k)) for k in case["outputs"])))
            if "<TIMEOUT>" in raw:
                verdicts[-1] += " (TIMEOUT)"
        status = "PASS" if all(v.startswith("ok") for v in verdicts) else "FAIL"
        results.append((name, status if cases else "LOADS", "; ".join(verdicts)))
    for name, status, detail in results:
        print("%-28s %-22s %s" % (name, status, detail))
    return 0 if all(s in ("PASS", "LOADS") for _, s, _ in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
