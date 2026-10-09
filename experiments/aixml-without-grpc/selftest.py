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
        so = ex.stdout or ""
        out = (so.decode(errors="replace") if isinstance(so, bytes) else so) + "\n<TIMEOUT>"
    state = re.search(r"ExecState = (\S+)", out)
    got = dict(re.findall(r"  get (.+?) = (.*)", out))
    return (state.group(1) if state else None), got, out


def insanities(vi):
    """Lines LabVIEW's own log (LabVIEW_*_cur.txt in TEMP) wrote about this VI's heap. A VI can
    load, run and compute while LabVIEW still calls one of its objects insane, and the same
    insanity is FATAL in another combination (measured: a DAbort on mask 0x1006) - so every
    composed VI is held to zero."""
    import glob
    name = os.path.basename(vi)
    n = 0
    for log in glob.glob(os.path.join(os.environ.get("TEMP", ""), "LabVIEW_*_cur.txt")):
        with open(log, encoding="utf-8", errors="replace") as f:
            n += sum(1 for l in f if l.startswith("Insane ") and '"%s"' % name in l)
    return n


def log_saturated():
    """LabVIEW STOPS WRITING its log after about 100 DWarn events (200 lines) - measured
    2026-10-09: the log froze at 1.75 MB while VIs kept loading, so every later insanity check
    read nothing and passed. A saturated log makes this gate blind; restart LabVIEW."""
    import glob
    for log in glob.glob(os.path.join(os.environ.get("TEMP", ""), "LabVIEW_*_cur.txt")):
        with open(log, encoding="utf-8", errors="replace") as f:
            if sum(1 for l in f if "DWarn" in l) >= 200:
                return log
    return None


def same(want, got):
    if got is None:
        return False
    w, g = str(want).strip(), str(got).strip()
    if w.startswith("[") and g.startswith("["):
        # an array or cluster, printed by lv-com.ps1 as [a,b,[c,d]]: element by element
        import aixml2vi
        a, b = aixml2vi.parse_literal(w), aixml2vi.parse_literal(g)

        def eq(x, y):
            if isinstance(x, list) or isinstance(y, list):
                return isinstance(x, list) and isinstance(y, list) and len(x) == len(y) \
                    and all(eq(p, q) for p, q in zip(x, y))
            return same(x, y)
        return eq(a, b)
    try:
        a, b = float(want), float(got)
        # inf - inf is nan, so equal values are settled first
        return a == b or abs(a - b) < 1e-9 * max(1.0, abs(a))
    except ValueError:
        return w.lower() == g.lower()


def main(argv):
    out_dir = r"C:\temp\aixml-no-grpc\out"
    if "--out" in argv:
        out_dir = argv[argv.index("--out") + 1]
    flt = [a for a in argv if not a.startswith("--") and a != out_dir]
    os.makedirs(out_dir, exist_ok=True)
    expect = json.load(open(os.path.join(HERE, "tests", "expect.json"), encoding="utf-8"))
    stamp = time.strftime("%H%M%S")
    results = []
    for name in sorted(expect):
        if flt and not any(f in name for f in flt):
            continue
        src = os.path.join(HERE, "tests", name + ".xml")
        # a test may CALL other composed VIs: compose those first, under a per-run file name,
        # because LabVIEW keeps a loaded VI in memory by name and would serve the old one
        for fname, callee in expect[name].get("callees", {}).items():
            cvi = os.path.join(out_dir, fname.replace("{stamp}", stamp))
            os.makedirs(os.path.dirname(cvi), exist_ok=True)   # a callee may live in a subfolder
            ccode, clog = compose(os.path.join(HERE, "tests", callee + ".xml"), cvi)
            if ccode != 0:
                results.append((name, "CALLEE FAILED", clog.splitlines()[-1] if clog else ""))
        text = open(src, encoding="utf-8").read()
        if "{stamp}" in text:
            src = os.path.join(out_dir, "%s_%s.xml" % (name, stamp))
            open(src, "w", encoding="utf-8").write(text.replace("{stamp}", stamp))
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
        bad = insanities(vi)
        if bad:
            status += " insane:%d" % bad
        elif log_saturated():
            status += " (log full)"   # LabVIEW stopped logging: nothing could be checked
        results.append((name, status if cases else "LOADS", "; ".join(verdicts)))
    for name, status, detail in results:
        print("%-28s %-22s %s" % (name, status, detail))
    sat = log_saturated()
    if sat:
        print("WARNING: %s is SATURATED (>= 100 DWarn events) - LabVIEW no longer writes it, so the "
              "insanity check above saw nothing. Restart LabVIEW and run again." % sat)
        return 1
    return 0 if all(s in ("PASS", "LOADS", "PASS (log full)", "LOADS (log full)") for _, s, _ in results) else 1  # insane counts as a failure


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
