# -*- coding: utf-8 -*-
"""Wire repairs LabVIEW logs on load, the composer's generic tables against its routed ones.

    measure_routes.py <work dir> <test> [<test> ...]

Each suite test is composed twice - AIXML2VI_GENERIC_WIRES=1 and as the composer now does it -
under two VI names, and both are loaded on one fresh LabVIEW; `Wiring insanity found and fixed`
is LabVIEW repairing a table, anything else is printed, and so is each ExecState.
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import selftest as stt  # noqa: E402

PY = sys.executable


def main(argv):
    work, tests = os.path.normpath(os.path.abspath(argv[0])), argv[1:]
    os.makedirs(work, exist_ok=True)
    tot = {"generic": 0, "routed": 0}
    for name in tests:
        text = open(os.path.join(HERE, "tests", name + ".xml"), encoding="utf-8").read()
        vis = {}
        for tag, env in (("generic", {"AIXML2VI_GENERIC_WIRES": "1"}), ("routed", {})):
            src = os.path.join(work, "%s_%s.xml" % (name, tag))
            # its own VI name, or LabVIEW serves the first from memory for the second
            open(src, "w", encoding="utf-8").write(re.sub(r'(<VI _name=")([^"]*?)(\.vi")', r"\g<1>\g<2> %s\g<3>" % tag, text, count=1))
            vi = os.path.join(work, "%s %s.vi" % (name, tag))
            r = subprocess.run([PY, os.path.join(COMPOSER, "aixml2vi.py"), src, vi], capture_output=True, text=True,
                               env=dict(os.environ, **env))
            lines = (r.stdout + r.stderr).splitlines()
            vis[tag] = vi if any(l.startswith("wrote") for l in lines) else None
        if None in vis.values():
            print("%-34s NOT COMPOSED" % name)
            continue
        stt.restart_labview()
        out = []
        for tag in ("generic", "routed"):
            b = len(stt.dwarn_lines())
            state, _, _ = stt.lvcom(vis[tag], "", [], run=False)
            new = stt.dwarn_lines()[b:]
            rep = sum("Wiring insanity" in l for l in new)
            other = [l for l in new if "Wiring insanity" not in l]
            tot[tag] += rep
            out.append("%s ExecState %s repairs %3d%s" % (tag, state, rep, (" OTHER %d: %s" % (len(other), other[0][:90])) if other else ""))
        print("%-34s %s | %s | insane %d %d" % (name, out[0], out[1], stt.insanities(vis["generic"]), stt.insanities(vis["routed"])), flush=True)
    print("TOTAL repairs generic %d, routed %d" % (tot["generic"], tot["routed"]))


if __name__ == "__main__":
    main(sys.argv[1:])
