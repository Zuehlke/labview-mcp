# -*- coding: utf-8 -*-
"""Compare vi2aixml.py's export with LabVIEW's OWN AIXML export of the same VI.

    compare_with_nigel.py [--max N]

The AIXML cache (%USERPROFILE%\\.labviewmcp\\cache\\aixml) holds exports LabVIEW wrote through
the NI gRPC service in earlier sessions. Those are the reference here; nothing is asked of
LabVIEW now. Every cached VI is compared on its ROOT-LEVEL subset - controls, indicators,
scalar constants and named primitives on the top-level diagram, and the wires between them -
because that is what vi2aixml reads; structures and what is inside them are left out of both
sides. A cache entry is used only while its recorded file size and timestamp still match the
file, so a VI regenerated since it was cached is skipped rather than reported as a mismatch.
For each VI the script compares:

  elements  (tag, uid, name, type)              - what is on the VI
  values    (uid, value) for constants          - what the constants hold
  edges     (sink uid, sink terminal) -> (source uid, source terminal)  - the wiring

and prints one line per VI plus totals.
"""
import glob
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))  # the composer's modules, since 2026-10-12
sys.path.insert(1, COMPOSER)
import aixml2vi  # noqa: E402
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402

CACHE = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")
OK_TAGS = {"Control", "Indicator", "Constant", "Node"}


def supported(el, prims):
    if not isinstance(el.tag, str) or el.tag not in OK_TAGS or el.get("uid_parent", "root") != "root":
        return False
    if el.tag == "Node":
        return el.get("_name") in prims
    return el.get("type") in aixml2vi.SCALARS or el.get("type") == "timestamp"


def facts(root, prims):
    els, vals, edges = set(), set(), set()
    src_of = {}
    root = [e for e in root if supported(e, prims)]
    for el in root:
        for term, net in aixml2vi.pairs(el.get("outputs")):
            if net:
                src_of[net] = (el.get("uid"), term)
    for el in root:
        els.add((el.tag, el.get("uid"), aixml2vi.unescape(el.get("_name") or ""), el.get("type")))
        if el.tag == "Constant":
            vals.add((el.get("uid"), el.get("value")))
        for term, net in aixml2vi.pairs(el.get("inputs")):
            if net and net in src_of:
                edges.add((el.get("uid"), term, src_of[net]))
    return els, vals, edges


def learn_terms(ref, ours, main_xml, learned):
    """Where our export says ?parmN and NI's names the same wire end, record the name."""
    ref_in = {}
    for el in ref:
        if isinstance(el.tag, str) and el.tag == "Node":
            for term, net in aixml2vi.pairs(el.get("inputs")) + aixml2vi.pairs(el.get("outputs")):
                if net:
                    ref_in.setdefault((el.get("uid"), net.split(".")[0]), set()).add(term)
    b = lvheap.Bundle(main_xml)
    rid_of = {e.get("uid"): e.findtext("primResID") for e in b.bd.iter("SL__arrayElement")
              if e.get("class") == "prim"}
    for el in ours:
        if not isinstance(el.tag, str) or el.tag != "Node":
            continue
        for term, net in aixml2vi.pairs(el.get("inputs")) + aixml2vi.pairs(el.get("outputs")):
            if not term.startswith("?parm") or not net:
                continue
            cands = ref_in.get((el.get("uid"), net.split(".")[0]), set())
            known = {t for t, _ in aixml2vi.pairs(el.get("inputs")) + aixml2vi.pairs(el.get("outputs"))}
            cands = cands - known
            if len(cands) == 1:
                rid = rid_of.get(el.get("uid"))
                learned.setdefault((rid, term[5:]), set()).add(cands.pop())


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 10 ** 9
    learn = {} if "--learn" in argv else None
    prims = set(aixml2vi.load_names()[0])
    totals = {"vis": 0, "els": [0, 0], "vals": [0, 0], "edges": [0, 0], "exact": 0}
    tmp = tempfile.mkdtemp(prefix="cmp_")
    for xml_path in sorted(glob.glob(os.path.join(CACHE, "*.xml"))):
        if totals["vis"] >= limit:
            break
        try:
            ref = ET.parse(xml_path).getroot()
        except ET.ParseError:
            continue
        if not any(supported(e, prims) for e in ref):
            continue
        meta = json.load(open(xml_path[:-4] + ".json", encoding="utf-8"))
        vi = meta.get("ViPath") or ""
        if ".llb" + os.sep in vi.lower() or not os.path.exists(vi):
            continue
        _, ticks, size = meta["Key"].rsplit("|", 2)
        now_ticks = int(os.path.getmtime(vi) * 10 ** 7) + 621355968000000000
        if int(size) != os.path.getsize(vi) or abs(now_ticks - int(ticks)) > 2 * 10 ** 7:
            totals["stale"] = totals.get("stale", 0) + 1
            continue
        try:
            main_xml = lvheap.extract(vi, os.path.join(tmp, str(totals["vis"])))
            ours = vi2aixml.Reader(main_xml).read()
        except Exception as ex:
            print("ERROR %s: %s" % (os.path.basename(vi), ex))
            continue
        totals["vis"] += 1
        a, b = facts(ref, prims), facts(ours, prims)
        if learn is not None:
            learn_terms(ref, ours, main_xml, learn)
        line = []
        exact = True
        for i, k in enumerate(("els", "vals", "edges")):
            hit = len(a[i] & b[i])
            totals[k][0] += hit
            totals[k][1] += len(a[i])
            line.append("%s %d/%d" % (k, hit, len(a[i])))
            if a[i] != b[i]:
                exact = False
                miss = sorted(a[i] - b[i])[:2]
                extra = sorted(b[i] - a[i])[:2]
                line.append("  missing %s extra %s" % (miss, extra))
        totals["exact"] += exact
        print("%-5s %-45s %s" % ("SAME" if exact else "DIFF", os.path.basename(vi)[:45], "; ".join(line)))
    print("\n%d VIs compared, %d identical in every fact, %d stale cache entries skipped"
          % (totals["vis"], totals["exact"], totals.get("stale", 0)))
    for k in ("els", "vals", "edges"):
        print("  %-6s %d of %d" % (k, totals[k][0], totals[k][1]))
    if learn is not None:
        path = os.path.join(COMPOSER, "terminal-names-learned.tsv")
        names, _ = aixml2vi.load_names()
        # MERGE with what an earlier run learned: names already learned load into the
        # composer's table, so a second run finds no ?parmN for them and would otherwise
        # write a file holding only the new ones - measured, 108 names dropped to 1.
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                for line in f:
                    c = line.rstrip("\n").split("\t")
                    if len(c) == 4 and not line.startswith("#") and c[0] != "primResID":
                        learn.setdefault((c[0], c[2]), set()).add(c[3])
        rows = [(rid, p, sorted(n)[0]) for (rid, p), n in sorted(learn.items()) if len(n) == 1 and rid]
        with open(path, "w", encoding="utf-8") as f:
            f.write("# (primResID, parmIndex) -> terminal name, learned by compare_with_nigel.py --learn:\n"
                    "# a wire end vi2aixml could only call ?parmN, named by LabVIEW's own export of the\n"
                    "# same VI. Ambiguous ones (two names for one slot) are left out.\n")
            f.write("primResID\tnode\tparmIndex\tterminal\n")
            inv = {v: k for k, v in names.items()}
            for rid, pidx, name in rows:
                f.write("%s\t%s\t%s\t%s\n" % (rid, inv.get(rid, ""), pidx, name))
        print("learned %d terminal name(s) into %s" % (len(rows), path))


if __name__ == "__main__":
    main(sys.argv[1:])
