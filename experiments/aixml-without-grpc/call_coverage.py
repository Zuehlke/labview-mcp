# -*- coding: utf-8 -*-
"""How many of the subVI calls in NI's own code can the composer write? Every <Call> in the
cached AIXML exports is resolved to its callee file, the callee's pane is read through
pylabview, and the call's terminal names are checked against the callee's pane labels -
everything add_call needs, without composing the caller.

    call_coverage.py [--max N]
"""
import collections
import glob
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import resolve  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")


def pane_labels(path, tmp):
    b = lvheap.Bundle(lvheap.extract(path, tempfile.mkdtemp(dir=tmp)))
    names = set()
    for fpdco in b.fp.iter("SL__arrayElement"):
        if fpdco.get("class") == "fPDCO":
            con = fpdco.findtext("conNum")
            if con is None or int(con) >= 0:
                names.add(aixml2vi.vi_label(fpdco.find("ddo")))
    con_id = int(b.fp.findtext("root/conPane/conId"))
    dd = b.main.find("LVSR/Section/Execution").get("DynamicDispatch") == "1"
    return names, con_id, dd


def main(argv):
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10 ** 9
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    r = resolve.Resolver()
    tmp = tempfile.mkdtemp(prefix="callcov_")
    panes = {}
    why = collections.Counter()
    kinds = collections.Counter()
    ok = total = 0
    for x in sorted(glob.glob(os.path.join(CACHE, "*.xml")))[:limit]:
        try:
            root = ET.parse(x).getroot()
        except ET.ParseError:
            continue
        caller = json.load(open(x[:-4] + ".json", encoding="utf-8")).get("ViPath") or ""
        for c in root.iter("Call"):
            total += 1
            target = aixml2vi.unescape(c.get("instance") or c.get("target") or "")
            res = r.resolve(target, [os.path.dirname(caller)], caller)
            if "error" in res:
                why["unresolved: " + res["error"][:70]] += 1
                continue
            kind = res.get("kind", "?")
            path = res["path"]
            if path not in panes:
                try:
                    panes[path] = pane_labels(path, tmp)
                except Exception as ex:
                    panes[path] = ex
            if isinstance(panes[path], Exception):
                why["callee unreadable: " + type(panes[path]).__name__] += 1
                continue
            names, con_id, dd = panes[path]
            if dd:
                why["dynamic-dispatch method (resolved; its node is not composable yet)"] += 1
                continue
            if con_id not in aixml2vi.load_pane_geometry():
                why["pane pattern %d has no measured geometry" % con_id] += 1
                continue
            wanted = [t for t, _ in aixml2vi.pairs(c.get("inputs")) + aixml2vi.pairs(c.get("outputs"))]
            missing = [t for t in wanted if t not in names]
            if missing:
                why["terminal name not on the callee's pane%s" % (" (Express VI)" if "Ex_" in os.path.basename(path)
                    or "Express" in path else "")] += 1
                continue
            ok += 1
            kinds[kind] += 1
    print("%d of %d calls resolvable with every terminal on the callee's pane (%.1f %%)"
          % (ok, total, 100.0 * ok / max(1, total)))
    print("  by kind:", dict(kinds))
    print("  %d distinct callees read" % len(panes))
    for k, n in why.most_common(20):
        print("  %5d  %s" % (n, k))


if __name__ == "__main__":
    main(sys.argv[1:])
