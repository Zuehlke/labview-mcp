# -*- coding: utf-8 -*-
"""Compose every tests/*.xml with aixml2vi.py, load and run each result in LabVIEW over COM
(lv-com.ps1 - no Nigel, no gRPC), and compare the outputs with tests/expect.json.

    selftest.py [name-filter] [--out DIR] [--no-restart]

LabVIEW is restarted (lv-restart.ps1) whenever its log nears the 100 DWarn events after which
it stops writing, unless --no-restart is given.

Every build gets a fresh file name: a LabVIEW instance keeps a VI in memory once loaded, and a
second VI under the same name would be the first one again.

A case may carry `post` (and `postAfterMs`): window messages lv-com.ps1 sends to the VI's own
panel during the run - "char:97;sysclose" sends the key a and clicks the close box - and then expect
the pseudo-output "#panel open", which is what Panel Close?'s Discard? decides (t133).
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
PY = sys.executable


def compose(src, vi, search=()):
    """`search`: folders under tests/ a test's Call targets live in (expect.json `search`) - a
    class fixture, which the composer cannot build itself and so is committed."""
    extra = [a for d in search for a in ("--search", os.path.join(HERE, "tests", d))]
    r = subprocess.run([PY, os.path.join(COMPOSER, "aixml2vi.py"), src, vi, "--keep", vi[:-3] + ".bundle"] + extra,
                       capture_output=True, text=True, stdin=subprocess.DEVNULL)
    return r.returncode, (r.stdout + r.stderr).strip()


def lvcom(vi, inputs, outputs, run=True, timeout=60, post=None, post_after_ms=None):
    """`post`: window messages lv-com.ps1 sends to the VI's own panel during the run (a case's
    `post`, e.g. "char:97;sysclose") - Panel Close? and Key Down? as a person fires them. The
    panel's state after the run is then the pseudo-output "#panel open"."""
    cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
           os.path.join(HERE, "lv-com.ps1"), "-ViPath", vi]
    outputs = [o for o in outputs if not o.startswith("#")]
    if run:
        cmd += ["-Run", "-Inputs", inputs, "-Outputs", ";".join(outputs)]
        if post:
            cmd += ["-Post", post] + (["-PostAfterMs", str(post_after_ms)] if post_after_ms else [])
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        out = r.stdout + r.stderr
    except subprocess.TimeoutExpired as ex:
        so = ex.stdout or ""
        out = (so.decode(errors="replace") if isinstance(so, bytes) else so) + "\n<TIMEOUT>"
    state = re.search(r"ExecState = (\S+)", out)
    got = dict(re.findall(r"  get (.+?) = (.*)", out))
    panel = re.search(r"panel open after run = (\S+)", out)
    if panel:
        got["#panel open"] = panel.group(1)
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


# A composed VI's ordinary residue: LabVIEW repairs the composer's wire geometry on load and logs
# it, about two events per wire. Anything ELSE a test adds to the log is a finding - the STR
# block (GetTagVariant: variant has value of null) and the font table (indexing font out of
# range) both passed the Insane-only gate with right values, measured against LabVIEW's own VI
# of the same objects, which logged nothing.
EXPECTED_DWARNS = ("Wiring insanity found and fixed",)
# what an INTERACTION a case posts makes LabVIEW log, whatever VI it runs: closing a popup menu
# with Escape logs "WDestroy called on tracking window" - for LabVIEW's own save of t142 exactly
# as for the composed VI (an A/B, 2026-10-11) - so it is expected only where a case posts esc
POSTED_DWARNS = {"esc": ("WDestroy called on tracking window",)}


def dwarn_lines():
    """The bare DWarn lines of the current log, in order."""
    import glob
    out = []
    for log in glob.glob(os.path.join(os.environ.get("TEMP", ""), "LabVIEW_*_cur.txt")):
        with open(log, encoding="utf-8", errors="replace") as f:
            lines = [l.rstrip() for l in f if l.startswith("DWarn")]
        if len(lines) > len(out):
            out = lines
    return out


# LabVIEW writes 100 DWarn events to its log and then nothing; a composed VI costs about two per
# wire, so a test is started on a fresh LabVIEW once the log is past this many
RESTART_AT = 40


def dwarn_events():
    """DWarn EVENTS in the current log - each is written twice, bare and with its source line."""
    import glob
    n = 0
    for log in glob.glob(os.path.join(os.environ.get("TEMP", ""), "LabVIEW_*_cur.txt")):
        with open(log, encoding="utf-8", errors="replace") as f:
            n = max(n, sum(1 for l in f if l.startswith("DWarn")))
    return n


def lv_exe():
    """The running LabVIEW's own exe - its installation holds what a test copies (an XControl)."""
    r = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process LabVIEW | Select-Object -First 1).Path"],
                       capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL)
    return r.stdout.strip()


def restart_labview():
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-restart.ps1")], capture_output=True, text=True,
                       timeout=300, stdin=subprocess.DEVNULL)
    print("[%s]" % (r.stdout + r.stderr).strip(), file=sys.stderr, flush=True)


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
    out_dir = out_arg = r"C:\temp\aixml-no-grpc\out"
    if "--out" in argv:
        # backslashes: LabVIEW over COM loads no VI from a forward-slash path, and every case of
        # a run then read "BROKEN (ExecState None)" (2026-10-12)
        out_arg = argv[argv.index("--out") + 1]
        out_dir = os.path.abspath(out_arg)
    flt = [a for a in argv if not a.startswith("--") and a != out_arg]
    os.makedirs(out_dir, exist_ok=True)
    expect = json.load(open(os.path.join(HERE, "tests", "expect.json"), encoding="utf-8"))
    stamp = time.strftime("%H%M%S")
    auto = "--no-restart" not in argv
    results = []
    for name in sorted(expect):
        if flt and not any(f in name for f in flt):
            continue
        # a fresh LabVIEW before a test that might not fit in what is left of the log, and the
        # test once more on a fresh one if it filled the log: a "(log full)" verdict is then the
        # test's OWN size, not the tests before it
        if auto and dwarn_events() >= RESTART_AT:
            restart_labview()
        got = run_test(name, expect, out_dir, stamp)
        if auto and any(s.endswith("(log full)") for _, s, _ in got) and dwarn_events() > 0:
            restart_labview()
            got = run_test(name, expect, out_dir, stamp)
        results.extend(got)
    for name, status, detail in results:
        print("%-28s %-22s %s" % (name, status, detail))
    sat = log_saturated()
    if sat and not auto:
        print("WARNING: %s is SATURATED (>= 100 DWarn events) - LabVIEW no longer writes it, so the "
              "insanity check above saw nothing. Restart LabVIEW and run again." % sat)
        return 1
    full = [n for n, s, _ in results if s.endswith("(log full)")]
    if full:
        print("NOT CHECKED for insanity - each fills a fresh LabVIEW log on its own: %s" % ", ".join(full))
    return 0 if all(s in ("PASS", "LOADS", "PASS (log full)", "LOADS (log full)") for _, s, _ in results) else 1  # insane counts as a failure


def run_test(name, expect, out_dir, stamp):
    results = []
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
    xc = expect[name].get("xcontrol")
    if xc:
        # an XControl's FACADE: composed over the facade of a copy of NI's own XControl, where its
        # .xctl lists it - and on a fresh LabVIEW, which otherwise serves the previous run's copy
        # of the same library from memory, whatever path is asked for (2026-10-11)
        if "--no-restart" not in sys.argv:
            restart_labview()
        root = os.path.dirname(lv_exe())
        dst = os.path.join(out_dir, "%s_%s" % (name, stamp))
        shutil.copytree(os.path.join(root, xc["from"]), dst)
        vi = os.path.join(dst, xc["facade"])
    log_before = len(dwarn_lines())
    code, log = compose(src, vi, expect[name].get("search", ()))
    if code != 0:
        results.append((name, "COMPOSE FAILED", log.splitlines()[-1] if log else ""))
        return results
    cases = expect[name].get("cases", [])
    state, got, raw = lvcom(vi, "", [], run=False)
    stale = re.search(r"SERVED FROM (.+)", raw)
    if stale:
        results.append((name, "STALE", "LabVIEW served %s for %s" % (stale.group(1).strip(), vi)))
        return results
    if state != "1":
        results.append((name, "BROKEN (ExecState %s)" % state, vi))
        return results
    if xc and xc.get("host"):
        # the VI that USES the XControl: broken when its facade is
        hvi = os.path.join(os.path.dirname(vi), xc["host"])
        hstate, _, hraw = lvcom(hvi, "", [], run=False)
        if hstate != "1" or "SERVED FROM" in hraw:
            results.append((name, "HOST BROKEN (ExecState %s)" % hstate, hvi))
            return results
    run_vi = vi
    if xc and xc.get("hostCases"):
        # an XControl lives only on an OPEN panel: the host runs with its panel open, and its
        # XControl method answers only once the facade has returned from its event - 0 against
        # NI's 212 while a composed facade waited for ever (2026-10-11)
        run_vi, cases = hvi, xc["hostCases"]
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-fp.ps1"), "-ViPath", hvi],
                       capture_output=True, text=True, timeout=120, stdin=subprocess.DEVNULL)
    verdicts = []
    for case in cases:
        state, got, raw = lvcom(run_vi, case.get("inputs", ""), list(case["outputs"]),
                                post=case.get("post"), post_after_ms=case.get("postAfterMs"))
        ok = all(same(v, got.get(k)) for k, v in case["outputs"].items()) and "SERVED FROM" not in raw
        verdicts.append("%s %s -> %s" % ("ok" if ok else "FAIL", case.get("inputs", ""),
                                         ", ".join("%s=%s" % (k, got.get(k)) for k in case["outputs"])))
        if "<TIMEOUT>" in raw:
            verdicts[-1] += " (TIMEOUT)"
    status = "PASS" if all(v.startswith("ok") for v in verdicts) else "FAIL"
    bad = insanities(vi)
    steps = {p.split(":")[0] for c in cases for p in (c.get("post") or "").split(";") if p}
    posted = tuple(x for k in steps for x in POSTED_DWARNS.get(k, ()))
    extra = [re.sub(r"0x[0-9A-F]{8}", "", l) for l in dwarn_lines()[log_before:]
             if not any(x in l for x in EXPECTED_DWARNS + posted)]
    if extra:
        status += " dwarn:%d" % len(extra)
        verdicts.append("first unexpected DWarn: %s" % extra[0][:120])
    if bad:
        status += " insane:%d" % bad
    elif log_saturated():
        status += " (log full)"   # LabVIEW stopped logging: nothing could be checked
    # a test with no run cases (an event loop waits for ever) only loads - its insanity
    # verdict is kept, which a plain "LOADS" used to throw away
    results.append((name, status if cases else status.replace("PASS", "LOADS"), "; ".join(verdicts)))
    return results


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
