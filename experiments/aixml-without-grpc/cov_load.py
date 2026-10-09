# -*- coding: utf-8 -*-
"""Load every VI coverage.py --build wrote (DIR\\composed.txt) in LabVIEW over COM and count
how many are executable. A broken one is listed with the NI export it was composed from.

    cov_load.py DIR
"""
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def main(argv):
    d = argv[0]
    rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(d, "composed.txt"), encoding="utf-8") if l.strip()]
    ok, bad = 0, []
    for i in range(0, len(rows), 20):          # twenty VIs per COM session
        chunk = rows[i:i + 20]
        r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                            os.path.join(HERE, "lv-com.ps1"), "-ViPath", "|".join(v for v, _ in chunk)],
                           capture_output=True, text=True, timeout=600, stdin=subprocess.DEVNULL)
        states = re.findall(r"=== (.*)\r?\n\s+(?:ExecState = (\S+)|GetVIReference FAILED: (.*))", r.stdout)
        for (vi, state, fail), (_, src) in zip(states, chunk):
            if state == "1":
                ok += 1
            else:
                meta = src[:-4] + ".json"
                name = json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else src
                bad.append((os.path.basename(vi), state or "load failed: " + fail[:60], name))
    print("%d of %d composed NI VIs are executable (ExecState 1)" % (ok, len(rows)))
    for vi, st, name in bad:
        print("  %s  %s  %s" % (vi, st, name))


if __name__ == "__main__":
    main(sys.argv[1:])
