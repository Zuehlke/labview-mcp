# -*- coding: utf-8 -*-
"""Cut NODE TEMPLATES: heap nodes of their own class (Merge Errors, Concatenate Strings, Format
Into String, Compound Arithmetic, Initialize Array, ...) with every terminal labelled by the
AIXML terminal it is, so the composer can rebuild the same node from an AIXML <Node>.

The label comes from a JOIN, not from position: heap terminal order is the node's own (Delete
From Array runs [deleted portion, array, length, array w/ subset deleted, index]) and has
nothing to do with AIXML's. A heap signal joins a terminal of node U to some other object O;
AIXML names the net U's terminal and O share; the terminal is that one. A terminal left
unwired is labelled by elimination when exactly one name and one terminal are left.

    harvest_templates.py <parts-dir> --probe <aixml> <vi> [...]      reference VIs we generated
    harvest_templates.py <parts-dir> --corpus [--names "A,B"]       NI's cached exports

A part is saved as tnode/<name>/<signature>, the signature being the AIXML terminal keys in
order (inputs | outputs), unwired ones included.
"""
import glob
import hashlib
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
import harvest_parts  # noqa: E402
import lvheap  # noqa: E402

TEMPLATE_NODES = {"Merge Errors", "Concatenate Strings", "Format Into String", "Scan From String",
                  "Compound Arithmetic", "Initialize Array", "Delete From Array", "Array Subset",
                  "Replace Array Subset", "Insert Into Array", "Build Waveform",
                  "Get Waveform Components", "Index & Bundle Cluster Array", "Interleave 1D Arrays",
                  "Decimate 1D Array", "Reshape Array", "Array Max & Min", "Search 1D Array",
                  "Split 1D Array", "Rotate 1D Array", "Sort 1D Array", "Transpose 2D Array",
                  "Array To Cluster", "Cluster To Array", "Max & Min", "Select", "Equal?"}


def signature(node_el):
    ins = [k for k, _ in aixml2vi.keyed(aixml2vi.pairs(node_el.get("inputs")))]
    outs = [k for k, _ in aixml2vi.keyed(aixml2vi.pairs(node_el.get("outputs")))]
    return ins, outs


def sig_key(name, ins, outs, mode=""):
    s = "%s|%s|%s" % (",".join(ins), ",".join(outs), mode)
    return "tnode/%s/%s" % (name.replace("/", "_").replace("?", "Q").replace(" ", "_").replace("&", "and"),
                            hashlib.md5(s.encode("utf-8")).hexdigest()[:10])


def nets_of(el):
    out = set()
    for a in ("inputs", "outputs"):
        for _, n in aixml2vi.pairs(el.get(a)):
            if n:
                out.add(n)
    for a in ("selectin", "selectout", "count", "maxin", "maxout"):
        if el.get(a):
            out.add(el.get(a))
    return out


def harvest(h, aixml_path, bundle, source, names):
    h.bundle = bundle
    root = ET.parse(aixml_path).getroot()
    by_uid = {e.get("uid"): e for e in root.iter() if e.get("uid")}
    bd = bundle.bd
    parent = {c: p for p in bd.iter() for c in p}
    fp_ddo = {}
    if bundle.fp is not None:
        for f in bundle.fp.iter("SL__arrayElement"):
            if f.get("class") == "fPDCO" and f.find("ddo") is not None:
                fp_ddo[f.get("uid")] = f.find("ddo").get("uid")
    term_by_uid = {t.get("uid"): t for t in bd.iter("SL__arrayElement") if t.get("class") in ("term", "fPTerm")}
    sig_of_term = {}
    for s in bd.iter("SL__arrayElement"):
        if s.get("class") == "signal":
            uids = [x.get("uid") for x in s.find("termList")]
            for u in uids:
                sig_of_term[u] = uids

    def aixml_owner(tu):
        """The AIXML uids a heap terminal may stand for."""
        t = term_by_uid.get(tu)
        out = [tu]
        if t is None:
            return out
        d = t.find("dco")
        if t.get("class") == "fPTerm" and d is not None:
            out.append(fp_ddo.get(d.get("uid")))
        if d is not None and d.find("ddo") is not None:
            out.append(d.find("ddo").get("uid"))
        p = parent.get(t)
        if p is not None and p.tag == "termList":
            out.append(parent[p].get("uid"))
        return [u for u in out if u]

    n = 0
    for node_el in root.iter("Node"):
        name = aixml2vi.unescape(node_el.get("_name") or "")
        if name not in names:
            continue
        heap_node = next((e for e in bd.iter("SL__arrayElement") if e.get("uid") == node_el.get("uid")
                          and e.get("class") not in ("term", "signal", "prim") and e.find("termList") is not None), None)
        if heap_node is None:
            continue
        ins, outs = signature(node_el)
        keyed = aixml2vi.keyed(aixml2vi.pairs(node_el.get("inputs"))) + aixml2vi.keyed(aixml2vi.pairs(node_el.get("outputs")))
        net_of = dict(keyed)
        terms = [t for t in heap_node.find("termList") if t.get("class") == "term"]
        label, pending = {}, {}
        for t in terms:
            others = [u for u in sig_of_term.get(t.get("uid"), []) if u != t.get("uid")]
            cands = None
            for o in others:
                # each other end of the wire narrows it down: on a fan-out the source names
                # one key, a second sink on the same node may name several - intersect them
                c = set()
                for au in aixml_owner(o):
                    el = by_uid.get(au)
                    if el is not None:
                        common = nets_of(el)
                        c |= {k for k, net in keyed if net and net in common}
                if c:
                    cands = c if cands is None else (cands & c) or cands
            if cands and len(cands) == 1:
                label[t.get("uid")] = cands.pop()
            elif cands:
                pending[t.get("uid")] = cands
        # interchangeable inputs on one net (Compound Arithmetic's four `value` from one wire):
        # same base name, so they are handed out in terminal order
        for t in terms:
            u = t.get("uid")
            if u in pending:
                free = sorted(k for k in pending[u] if k not in label.values())
                bases = {k.split("#")[0] for k in free}
                if free and len(bases) == 1:
                    label[u] = free[0]
        left_terms = [t for t in terms if t.get("uid") not in label]
        left_keys = [k for k, _ in keyed if k not in label.values()]
        if len(left_terms) == 1 and len(left_keys) == 1:
            label[left_terms[0].get("uid")] = left_keys[0]
        if len(label) != len(terms) or len(set(label.values())) != len(terms):
            continue    # not every terminal could be named: no template
        part = lvheap.renumber  # noqa: F841  (the part is renumbered when it is used)
        el = __import__("copy").deepcopy(heap_node)
        for t in el.find("termList"):
            if t.get("uid") in label:
                t.set("aixml", label[t.get("uid")])
        mode = ";".join("%s=%s" % (a, node_el.get(a)) for a in sorted(node_el.attrib)
                        if a not in ("_name", "inputs", "outputs", "uid", "uid_parent"))
        key = sig_key(name, ins, outs, mode)
        h.save("tnode", key, source, el, harvest_parts.resolve_types(bundle, el),
               {"node": name, "ins": "|".join(ins), "outs": "|".join(outs), "mode": mode,
                "nodeFlags": heap_node.findtext("objFlags") or "0"})
        n += 1
    return n


def main(argv):
    parts_dir = argv[0]
    h = harvest_parts.Harvester(parts_dir, replace="--replace" in argv, clean="--probe" in argv)
    tmp = tempfile.mkdtemp(prefix="harvest_")
    names = TEMPLATE_NODES
    if "--names" in argv:
        names = set(argv[argv.index("--names") + 1].split(","))
    total = 0
    if "--probe" in argv:
        i = argv.index("--probe") + 1
        rest = [a for a in argv[i:] if not a.startswith("--")]
        for k in range(0, len(rest) - 1, 2):
            ax, vi = rest[k], rest[k + 1]
            b = lvheap.Bundle(lvheap.extract(vi, os.path.join(tmp, "p%d" % k)))
            total += harvest(h, ax, b, "AIXML-generated probe: %s" % os.path.basename(vi), names)
    if "--corpus" in argv:
        cache = os.path.join(os.path.expanduser("~"), ".labviewmcp", "cache", "aixml")
        for k, x in enumerate(sorted(glob.glob(os.path.join(cache, "*.xml")))):
            meta = x[:-4] + ".json"
            vp = json.load(open(meta, encoding="utf-8")).get("ViPath") if os.path.exists(meta) else None
            if not vp or not os.path.exists(vp) or ".llb\\" in vp.lower():
                continue
            try:
                text = open(x, encoding="utf-8").read()
            except OSError:
                continue
            if not any('_name="%s"' % nm.replace("&", "&amp;") in text for nm in names):
                continue
            try:
                b = lvheap.Bundle(lvheap.extract(vp, os.path.join(tmp, "c%d" % k)))
                total += harvest(h, x, b, vp, names)
            except Exception as ex:
                print("SKIP %s: %s" % (vp, ex), file=sys.stderr)
    for k in sorted(h.found):
        print(k)
    print("%d template(s) seen, %d new part(s)" % (total, len(h.found)), file=sys.stderr)


if __name__ == "__main__":
    main(sys.argv[1:])
