# -*- coding: utf-8 -*-
"""LabVIEW's Error List lines of every NOT-executable VI a cov_load.py run named, read by running
the server's own lvai_exec_state helper over COM (lv-com.ps1 -Run) - one short call per VI instead
of an MCP answer each. Groups the lines by kind.

    census_errors.py <cov_load dir>     (reads <dir>\\load.txt, writes <dir>\\errors.tsv)
"""
import collections
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
HELPER = os.path.join(os.environ.get("TEMP", ""), "LabVIEWMCP", "helpers-pylabview", "lvai_exec_state.vi")


def errors_of(vi):
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(HERE, "lv-com.ps1"), "-ViPath", HELPER, "-Run",
                        "-Inputs", "vi path=" + vi, "-Outputs", "errors xml"],
                       capture_output=True, text=True, timeout=180, stdin=subprocess.DEVNULL)
    return [v for v in re.findall(r"<Val>(.*?)</Val>", r.stdout, re.S) if v.strip()]


def main(argv):
    d = argv[0]
    rows = []
    for l in open(os.path.join(d, "load.txt"), encoding="utf-8").read().splitlines()[1:]:
        p = l.split(None, 2)
        if len(p) == 3 and p[1] != "CRASH" and p[0].endswith(".vi"):
            vi = os.path.normpath(os.path.join(d, "vis", p[0]))
            src = p[2].replace("/", "\\")
            src = src[src.lower().find("examples\\") + 9:] if "examples\\" in src.lower() else src
            errs = errors_of(vi)
            rows.append((p[0], src, errs))
            print("%s %s | %s" % (p[0], src[:60], " || ".join(e[:90] for e in errs[:3])), flush=True)
    with open(os.path.join(d, "errors.tsv"), "w", encoding="utf-8") as f:
        for vi, src, errs in rows:
            f.write("%s\t%s\t%s\n" % (vi, src, " || ".join(errs)))
    kinds = collections.Counter()
    for _, _, errs in rows:
        for e in set(re.sub(r"'[^']*'", "X", re.sub(r"^[^:]*: ", "", e)) for e in errs):
            kinds[e[:80]] += 1
    print("\nby kind:")
    for k, n in kinds.most_common():
        print("%4d %s" % (n, k))


if __name__ == "__main__":
    main(sys.argv[1:])
