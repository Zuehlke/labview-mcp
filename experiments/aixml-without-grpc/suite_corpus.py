# -*- coding: utf-8 -*-
"""LabVIEW's own routes for the composer's own diagrams: every suite test composed with the GENERIC
wire tables (AIXML2VI_GENERIC_WIRES=1), loaded by LabVIEW - which repairs every wire, routing it
between the terminals' real hotpoints - and saved as a copy. wire_corpus.py then reads the copies
like NI's examples, so the hotpoint learners see the terminal kinds the composer makes and NI's
examples do not wire that way (an Event Structure's dynamic terminal in a fan-out, a loop's `i`).

    suite_corpus.py <work dir> [<test> ...]

Writes <work dir>\\saved\\<test>.vi; needs a running LabVIEW over COM (lv-save.ps1), which it
restarts first so nothing is served from memory. A test that does not compose is skipped.
"""
import glob
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import selftest as stt  # noqa: E402

PY = sys.executable


def main(argv):
    # backslashes: LabVIEW's COM opens nothing at a C:/... path (Git Bash hands over forward ones)
    work = os.path.normpath(os.path.abspath(argv[0]))
    names = argv[1:] or [os.path.basename(p)[:-4] for p in sorted(glob.glob(os.path.join(HERE, "tests", "t*.xml")))]
    saved_dir = os.path.join(work, "saved")
    os.makedirs(saved_dir, exist_ok=True)
    stt.restart_labview()
    done = 0
    for name in names:
        vi = os.path.join(work, name + ".vi")
        r = subprocess.run([PY, os.path.join(HERE, "aixml2vi.py"), os.path.join(HERE, "tests", name + ".xml"), vi],
                           capture_output=True, text=True, env=dict(os.environ, AIXML2VI_GENERIC_WIRES="1"))
        if not any(l.startswith("wrote") for l in (r.stdout + r.stderr).splitlines()):
            print("%-34s not composed" % name, flush=True)
            continue
        out = os.path.join(saved_dir, name + ".vi")
        r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                            os.path.join(HERE, "lv-save.ps1"), "-ViPath", vi, "-CopyPath", out],
                           capture_output=True, text=True)
        ok = "saved" in r.stdout
        done += ok
        print("%-34s %s" % (name, "saved" if ok else "SAVE FAILED " + (r.stdout + r.stderr).strip()[:120]), flush=True)
    print("%d of %d saved into %s" % (done, len(names), saved_dir))


if __name__ == "__main__":
    main(sys.argv[1:])
