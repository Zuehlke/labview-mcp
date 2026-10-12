# -*- coding: utf-8 -*-
"""Round trip through BOTH directions with no LabVIEW and no gRPC service in the loop until the
very end: tests/<name>.xml -> aixml2vi -> VI -> vi2aixml -> AIXML -> aixml2vi -> VI, then the
second VI is loaded and run over COM against tests/expect.json, exactly like selftest.py.

    roundtrip.py [name-filter] [--out DIR] [--no-run]

A test passes when the VI rebuilt from the READER's AIXML computes what the original test
expects - which is what a client of ConvertVIToAIXML + ConvertAIXMLToVI relies on.
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import selftest  # noqa: E402

PY = sys.executable


def run(args):
    r = subprocess.run([PY] + args, capture_output=True, text=True, stdin=subprocess.DEVNULL)
    return r.returncode, (r.stdout + r.stderr).strip()


def main(argv):
    out_dir = argv[argv.index("--out") + 1] if "--out" in argv else r"C:\temp\aixml-no-grpc\rt"
    no_run = "--no-run" in argv
    flt = [a for a in argv if not a.startswith("--") and a != out_dir]
    os.makedirs(out_dir, exist_ok=True)
    expect = json.load(open(os.path.join(HERE, "tests", "expect.json"), encoding="utf-8"))
    stamp = time.strftime("%H%M%S")
    rows = []
    for name in sorted(expect):
        if flt and not any(f in name for f in flt):
            continue
        if expect[name].get("callees"):
            rows.append((name, "SKIPPED", "calls composed callees"))
            continue
        src = os.path.join(HERE, "tests", name + ".xml")
        # the two VIs carry the SAME name as selftest.py gives it (<test>_<stamp>.vi), in two
        # folders: a test that reads its own VI name (t91) must not see a longer one
        os.makedirs(os.path.join(out_dir, "a"), exist_ok=True)
        vi1 = os.path.join(out_dir, "a", "%s_%s.vi" % (name, stamp))
        code, log = run([os.path.join(HERE, "aixml2vi.py"), src, vi1])
        if code:
            rows.append((name, "COMPOSE 1 FAILED", log.splitlines()[-1] if log else ""))
            continue
        xml2 = os.path.join(out_dir, "%s_%s.xml" % (name, stamp))
        code, log = run([os.path.join(HERE, "vi2aixml.py"), vi1, xml2])
        if code:
            rows.append((name, "READ FAILED", log.splitlines()[-1] if log else ""))
            continue
        vi2 = os.path.join(out_dir, "%s_%s.vi" % (name, stamp))
        code, log = run([os.path.join(HERE, "aixml2vi.py"), xml2, vi2])
        if code:
            rows.append((name, "COMPOSE 2 FAILED", log.splitlines()[-1] if log else ""))
            continue
        if no_run:
            rows.append((name, "COMPOSED", ""))
            continue
        state, _, _ = selftest.lvcom(vi2, "", [], run=False)
        if state != "1":
            rows.append((name, "BROKEN (ExecState %s)" % state, vi2))
            continue
        verdicts = []
        for case in expect[name].get("cases", []):
            # a live case's window messages too (Panel Close?, Key Down? fired for real) - without
            # them t133, t138, t139 and t142 failed here while passing in selftest.py
            state, got, raw = selftest.lvcom(vi2, case.get("inputs", ""), list(case["outputs"]),
                                             post=case.get("post"), post_after_ms=case.get("postAfterMs"))
            ok = all(selftest.same(v, got.get(k)) for k, v in case["outputs"].items())
            verdicts.append("%s %s" % ("ok" if ok else "FAIL", ", ".join("%s=%s" % (k, got.get(k)) for k in case["outputs"])))
        rows.append((name, "PASS" if all(v.startswith("ok") for v in verdicts) else "FAIL", "; ".join(verdicts)))
    for name, st, detail in rows:
        print("%-30s %-18s %s" % (name, st, detail[:150]))
    good = sum(1 for _, st, _ in rows if st in ("PASS", "COMPOSED"))
    print("%d of %d round trips %s" % (good, len(rows), "compose" if no_run else "pass"))


if __name__ == "__main__":
    main(sys.argv[1:])
