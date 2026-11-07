# -*- coding: utf-8 -*-
"""Which heap objects own the terminals that make vi2aixml drop a wire ("signal N touches an
unread terminal"): over a census folder's rows.tsv, read each VI that carried the note again and
count the DCO class and the owning node class of every terminal no element claims.

    unread_terms.py <census dir>"""
import collections
import csv
import os
import sys
import tempfile

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts", "aixml-pylabview")))
import lvheap  # noqa: E402
import vi2aixml  # noqa: E402


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    out = argv[0]
    c = collections.Counter()
    ex = {}
    for r in csv.reader(open(os.path.join(out, "rows.tsv"), encoding="utf-8"), delimiter="\t"):
        x = os.path.join(out, "aixml", "c_%04d.xml" % int(r[0]))
        if not os.path.exists(x) or "touches an unread terminal" not in open(x, encoding="utf-8").read():
            continue
        vi = os.path.normpath(r[4])
        try:
            rd = vi2aixml.Reader(lvheap.extract(vi, tempfile.mkdtemp()), vi_path=vi)
            rd.read()
        except Exception:
            continue
        defs = {}
        for node in rd.b.bd.iter("SL__arrayElement"):
            tl = node.find("termList")
            if tl is None or node.get("class") == "signal":
                continue
            for t in tl:
                d = t.find("dco")
                defs[t.get("uid")] = (node.get("class"), d.get("class") if d is not None else None)
        for d in rd.diags:
            sl = d.find("signalList")
            for sig in (sl if sl is not None else []):
                for t in sig.find("termList"):
                    if rd.term_owner.get(t.get("uid"), vi2aixml.MISSING) is vi2aixml.MISSING:
                        k = defs.get(t.get("uid"), ("?", "?"))
                        c[k] += 1
                        ex.setdefault(k, os.path.basename(vi))
    for k, v in c.most_common(30):
        print(v, k, ex[k])


if __name__ == "__main__":
    main(sys.argv[1:])
