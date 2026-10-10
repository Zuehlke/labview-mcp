# -*- coding: utf-8 -*-
"""How much of NI's own code can aixml2vi compose? Run the composer over every AIXML export in
the cache, write nothing to LabVIEW, and tally why the rest is refused.

    coverage.py [--build DIR [--max N]]

With --build, the VIs that compose are also written into DIR (unique names) for a later load
check through lv-com.ps1, and their paths listed in DIR\\composed.txt.
"""
import collections
import glob
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def bucket(msg):
    """Fold a refusal into a category: the element or construct that stopped it."""
    m = re.match(r"Node '([^']*)' is not a primitive", msg)
    if m:
        return "Node not a primitive: " + m.group(1)
    m = re.match(r"(Control|Indicator|Constant) .* has type '([^'{]*)", msg)
    if m:
        return "%s type %s" % (m.group(1), m.group(2).split("{")[0] or "?")
    m = re.match(r"element <(\w+)>", msg)
    if m:
        return "element <%s>" % m.group(1)
    m = re.match(r"structure '([^']*)'", msg)
    if m:
        return "structure " + m.group(1)
    return re.sub(r"\d+", "N", msg)[:70]


def main(argv):
    build = argv[argv.index("--build") + 1] if "--build" in argv else None
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10 ** 9
    files = sorted(glob.glob(os.path.join(CACHE, "*.xml")))
    ok, why, first = [], collections.Counter(), collections.Counter()
    for f in files:
        c = aixml2vi.Composer(os.path.join(HERE, "parts"))
        # a call is resolved as from the ORIGINAL VI's place: siblings and relative links
        meta = f[:-4] + ".json"
        if os.path.exists(meta):
            import json
            vp = json.load(open(meta, encoding="utf-8")).get("ViPath") or ""
            if vp:
                c.search_dirs = [os.path.dirname(vp)]
                c.caller_file = vp
        try:
            c.compose(f)
            ok.append((f, c))
        except aixml2vi.Unsupported as ex:
            why[bucket(str(ex))] += 1
        except Exception as ex:   # a composer bug, not a refusal - count it apart
            why["COMPOSER ERROR " + type(ex).__name__ + ": " + str(ex)[:50]] += 1
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print("%d of %d cached NI exports compose (%.1f %%)" % (len(ok), len(files), 100.0 * len(ok) / len(files)))
    print("\nwhy the rest is refused (first blocker per VI):")
    for k, n in why.most_common(30):
        print("  %5d  %s" % (n, k))
    if build:
        os.makedirs(build, exist_ok=True)
        paths = []
        for i, (f, c) in enumerate(ok[:limit]):
            vi = os.path.join(build, "cov_%04d.vi" % i)
            tmp = tempfile.mkdtemp(prefix="cov_")
            try:
                lvheap.rebuild(c.write(tmp, "cov_%04d" % i), vi)
                paths.append(vi + "\t" + f)
            except Exception as ex:
                print("REBUILD FAILED %s: %s" % (f, str(ex)[:100]))
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
        open(os.path.join(build, "composed.txt"), "w", encoding="utf-8").write("\n".join(paths) + "\n")
        print("\nwrote %d VIs into %s" % (len(paths), build))


if __name__ == "__main__":
    main(sys.argv[1:])
