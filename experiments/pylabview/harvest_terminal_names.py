#!/usr/bin/env python3
"""Harvest a (primResID, parmIndex) -> terminal-name table by following the wires.

Companion to harvest_prim_names.py, which names the nodes. This names their terminals, the gap
left open in FINDINGS.md 3.8: pylabview's heap identifies which input of a primitive a wire lands
on by `parmIndex`, an integer, and the numbering is geometric (bottom-to-top) rather than the
order AIXML declares - measured on `Select`, parmIndex 1 is `f`, 2 is `s?`, 3 is `t`, which is
neither AIXML's order nor its reverse. So position cannot be used to join; the wire must be.

The join, per node matched by uid across the two formats:

  AIXML says terminal `s?` of node 71 sits on net "43.value".
  The heap says a signal connects node 71's terminal (parmIndex 2) to fPTerm -> dco -> stdNum#43.
  Both formats agree on the uid 43, so the two ends meet and parmIndex 2 is `s?`.

Generalised, a heap signal joining a terminal of U to a terminal of O is matched to the one net
string that U and O both name in AIXML. That handles inputs and outputs alike and needs no
assumption about ordering. Where more than one net is shared - fan-out from one element into two
terminals of the same node - the observation is dropped rather than guessed.

Usage:
    python harvest_terminal_names.py --pylabview <repo> [--limit N] [--out terminal-names.tsv]
"""

import argparse
import collections
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

DEFAULT_CACHE = pathlib.Path(os.path.expanduser("~")) / ".labviewmcp" / "cache" / "aixml"


def aixml_entries(path):
    """uid -> [(terminalName, netString)] from every inputs=/outputs= attribute."""
    out = collections.defaultdict(list)
    text = pathlib.Path(path).read_text(encoding="utf-8", errors="replace")
    for m in re.finditer(r"<\w+\b([^>]*?)/?>", text):
        attrs = m.group(1)
        u = re.search(r'\buid="(\d+)"', attrs)
        if not u:
            continue
        for kind in ("inputs", "outputs"):
            mm = re.search(kind + r'="([^"]*)"', attrs)
            if not mm:
                continue
            for entry in mm.group(1).split(","):
                if not entry:
                    continue
                # ':' and ',' inside a terminal name are escaped as \3A and \2C, so the raw
                # separators are unambiguous
                name, _, net = entry.partition(":")
                if net:
                    out[u.group(1)].append((name, net))
    return out


def heap_view(bdhb, fphb):
    """(prims, termOwner, signals) for one VI's heaps.

    prims      : node uid -> (primResID, {terminal uid -> parmIndex or None})
    termOwner  : terminal uid -> the uid that AIXML would call its owner
    signals    : list of terminal-uid pairs
    """
    bd = ET.parse(bdhb).getroot()
    fp = ET.parse(fphb).getroot() if os.path.isfile(fphb) else None

    # front panel: fPDCO uid -> the control object inside it, whose uid is AIXML's uid
    dco_to_ctrl = {}
    if fp is not None:
        for el in fp.iter():
            if el.get("class") == "fPDCO" and el.get("uid"):
                for ch in el.iter():
                    if ch is not el and ch.get("uid") and ch.get("class") != "label":
                        dco_to_ctrl[el.get("uid")] = ch.get("uid")
                        break

    prims, term_owner, signals = {}, {}, []
    for el in bd.iter():
        tl = el.find("termList")
        if tl is not None and el.get("uid"):
            for t in tl:
                if t.get("uid"):
                    term_owner.setdefault(t.get("uid"), el.get("uid"))
        if el.get("class") == "fPTerm" and el.get("uid"):
            d = el.find("dco")
            if d is not None and d.get("uid") in dco_to_ctrl:
                term_owner[el.get("uid")] = dco_to_ctrl[d.get("uid")]
        if el.get("class") == "prim" and el.get("uid"):
            pr = el.find("primResID")
            if pr is None or not pr.text:
                continue
            terms = {}
            tl2 = el.find("termList")
            if tl2 is not None:
                for t in tl2:
                    d = t.find("dco")
                    pi = d.find("parmIndex") if d is not None else None
                    terms[t.get("uid")] = pi.text if pi is not None and pi.text else None
            prims[el.get("uid")] = (pr.text, terms)
        if el.get("class") == "signal":
            tl3 = el.find("termList")
            if tl3 is not None:
                uids = [t.get("uid") for t in tl3 if t.get("uid")]
                if len(uids) >= 2:
                    signals.append(uids)
    return prims, term_owner, signals


def join_one(bdhb, fphb, aixml, table, stats):
    prims, term_owner, signals = heap_view(bdhb, fphb)
    entries = aixml_entries(aixml)
    if not prims:
        return

    # terminal uid -> [(other terminal uid, other owner uid)]
    partners = collections.defaultdict(list)
    for uids in signals:
        for i, a in enumerate(uids):
            for b in uids[:i] + uids[i + 1:]:
                partners[a].append((b, term_owner.get(b)))

    for node_uid, (res_id, terms) in prims.items():
        if node_uid not in entries:
            stats["node not in AIXML"] += 1
            continue
        nets_u = {net for _, net in entries[node_uid]}
        for term_uid, parm in terms.items():
            for _, other_owner in partners.get(term_uid, []):
                if other_owner is None or other_owner not in entries:
                    continue
                shared = nets_u & {net for _, net in entries[other_owner]}
                if len(shared) != 1:
                    stats["ambiguous or unshared net"] += 1
                    continue
                net = next(iter(shared))
                names = [n for n, s in entries[node_uid] if s == net]
                if len(names) != 1:
                    stats["net names two terminals"] += 1
                    continue
                key = (res_id, parm if parm else "out")
                table[key][names[0]] += 1
                stats["terminals named"] += 1
                break


def unescape(name):
    """AIXML escapes ':' and ',' as \\3A and \\2C because they separate its entries."""
    for esc, ch in (("\\3A", ":"), ("\\2C", ","), ("\\0A", " "), ("\\0D", " ")):
        name = name.replace(esc, ch)
    return name


def extract(pylabview_root, vi_path, work):
    main = pathlib.Path(work) / "h.xml"
    try:
        subprocess.run(
            [sys.executable, os.path.join("pylabview", "readRSRC.py"),
             "-x", "-i", str(vi_path), "-m", str(main)],
            cwd=pylabview_root, capture_output=True, timeout=120, check=False)
    except subprocess.TimeoutExpired:
        return None, None
    bdhb = pathlib.Path(work) / "h_BDHb.xml"
    return (bdhb if bdhb.is_file() else None), str(pathlib.Path(work) / "h_FPHb.xml")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pylabview", required=True)
    ap.add_argument("--cache", default=str(DEFAULT_CACHE))
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--names", default=str(pathlib.Path(__file__).resolve().parent
                                          / "primitive-names.tsv"))
    ap.add_argument("--out", default="terminal-names.tsv")
    ap.add_argument("--vi", action="append", default=[],
                    help="harvest these VIs only, instead of the cache (needs a cached AIXML)")
    args = ap.parse_args()

    node_name = {}
    if os.path.isfile(args.names):
        for line in pathlib.Path(args.names).read_text(encoding="utf-8").splitlines():
            p = line.split("\t")
            if len(p) >= 2 and not line.startswith("#") and p[0] != "primResID":
                node_name[p[0]] = p[1]

    entries = []
    for j in sorted(pathlib.Path(args.cache).glob("*.json")):
        x = j.with_suffix(".xml")
        if not x.is_file():
            continue
        try:
            vi = json.loads(j.read_text(encoding="utf-8", errors="replace")).get("ViPath")
        except (ValueError, OSError):
            continue
        if vi and pathlib.Path(vi).is_file():
            entries.append((pathlib.Path(vi), x))
    if args.vi:
        want = {os.path.normcase(os.path.abspath(v)) for v in args.vi}
        entries = [e for e in entries if os.path.normcase(str(e[0])) in want]
    if args.limit:
        entries = entries[: args.limit]
    print("VIs to harvest: %d" % len(entries), flush=True)

    table = collections.defaultdict(collections.Counter)
    stats = collections.Counter()
    work = tempfile.mkdtemp(prefix="termharvest_")
    try:
        for i, (vi, aix) in enumerate(entries, 1):
            for f in pathlib.Path(work).iterdir():
                f.unlink()
            bdhb, fphb = extract(args.pylabview, vi, work)
            if bdhb is None:
                stats["extract failed"] += 1
                continue
            try:
                join_one(bdhb, fphb, aix, table, stats)
            except ET.ParseError:
                stats["heap unparsable"] += 1
            if i % 20 == 0 or i == len(entries):
                print("  %4d/%d  %d (primResID, parmIndex) pairs so far"
                      % (i, len(entries), len(table)), flush=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# (primResID, parmIndex) -> terminal name, harvested by following wires.\n")
        fh.write("# parmIndex 'out' means the terminal carries no parmIndex, which is how the\n")
        fh.write("# heap marks an output. Numbering is geometric, bottom-to-top; see FINDINGS 3.8.\n")
        fh.write("primResID\tnode\tparmIndex\tterminal\tseen\tcompeting\n")
        for (res, parm) in sorted(table, key=lambda k: (int(k[0]), 99 if k[1] == "out" else int(k[1]))):
            c = table[(res, parm)]
            best, seen = c.most_common(1)[0]
            fh.write("%s\t%s\t%s\t%s\t%d\t%s\n" % (
                res, node_name.get(res, "?"), parm, unescape(best), seen,
                ";".join(unescape(n) for n, _ in c.most_common()[1:])))

    print()
    for k, v in sorted(stats.items()):
        print("  %-24s %d" % (k, v))
    good = sum(1 for k in table if len(table[k]) == 1)
    print("\n  (primResID, parmIndex) pairs : %d, %d unambiguous" % (len(table), good))
    print("  distinct primResID covered   : %d" % len({r for r, _ in table}))
    print("  written to                   : %s" % args.out)


if __name__ == "__main__":
    main()
