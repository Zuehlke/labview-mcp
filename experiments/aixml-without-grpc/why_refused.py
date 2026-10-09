# -*- coding: utf-8 -*-
"""List the cached NI exports whose composition is refused with a message matching a pattern,
with the full message - the step after coverage.py names a bucket.

    why_refused.py <regex> [--max N]
"""
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def main(argv):
    pat = re.compile(argv[0])
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    n = 0
    for f in sorted(glob.glob(os.path.join(CACHE, "*.xml"))):
        c = aixml2vi.Composer(os.path.join(HERE, "parts"))
        meta = f[:-4] + ".json"
        vp = json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else ""
        if vp:
            c.search_dirs, c.caller_file = [os.path.dirname(vp)], vp
        try:
            c.compose(f)
            continue
        except Exception as ex:
            msg = "%s: %s" % (type(ex).__name__, ex)
        if pat.search(msg):
            print("%s\n    %s\n    %s" % (os.path.basename(f), vp, msg[:300]))
            n += 1
            if n >= limit:
                break


if __name__ == "__main__":
    main(sys.argv[1:])
