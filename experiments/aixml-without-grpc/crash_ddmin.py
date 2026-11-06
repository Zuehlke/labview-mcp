# -*- coding: utf-8 -*-
"""Shrink an AIXML document that composes into a VI which KILLS LabVIEW on load to a minimal one
that still does - delta debugging over the element tree, one compose + load per trial.

    crash_ddmin.py <export.xml> <out dir> [--near ORIGINAL.vi] [--max-trials N]

A trial removes a set of elements (a top-level unit, or children inside a structure); every input
that named a net the removed elements produced is unwired, so the rest still composes. A trial that
the composer refuses, or that loads (crashing or not), is judged by its load: CRASH keeps the
removal, anything else undoes it. LabVIEW is restarted (lv-restart.ps1) after each crash and every
25 loads. The result: <out dir>\\min.xml and the trial log in <out dir>\\trials.tsv.
"""
import copy
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
PY = os.path.normpath(os.path.join(HERE, "..", "..", "tools", "pylabview", "runtime", "python.exe"))
DEAD = ("remote procedure call failed", "RPC server is unavailable")


def ps(script, *args, timeout=600):
    return subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                           os.path.join(HERE, script), *args], capture_output=True, text=True,
                          timeout=timeout, stdin=subprocess.DEVNULL)


class Runner:
    def __init__(self, out, near, tail):
        self.out, self.near, self.tail = out, near, tail
        self.n, self.loads = 0, 0
        self.log = open(os.path.join(out, "trials.tsv"), "a", encoding="utf-8")
        ps("lv-restart.ps1")

    def verdict(self, root, what):
        """'CRASH', 'ok', 'broken' or 'refused' for the document `root`."""
        self.n += 1
        name = "t%03d" % self.n
        root.set("_name", name + ".vi")
        x = os.path.join(self.out, name + ".xml")
        ET.ElementTree(root).write(x, encoding="utf-8")
        with open(x, "a", encoding="utf-8") as f:
            f.write("\n" + self.tail)
        vi = os.path.normpath(x[:-4] + ".vi")
        args = [PY, "-W", "ignore::SyntaxWarning", os.path.join(COMPOSER, "aixml2vi.py"), x, vi]
        if self.near:
            args += ["--search", os.path.dirname(self.near)]
        subprocess.run(args, capture_output=True, text=True, stdin=subprocess.DEVNULL)
        if not os.path.exists(vi):
            v = "refused"
        else:
            r = ps("lv-com.ps1", "-ViPath", vi)
            self.loads += 1
            out = r.stdout + r.stderr
            if any(d in out for d in DEAD):
                v = "CRASH"
                ps("lv-restart.ps1")
                self.loads = 0
            else:
                m = re.search(r"ExecState = (\S+)", out)
                v = "ok" if m and m.group(1) == "1" else "broken"
                if self.loads >= 25:
                    ps("lv-restart.ps1")
                    self.loads = 0
        self.log.write("%s\t%s\t%s\n" % (name, v, what))
        self.log.flush()
        print("%s %-7s %s" % (name, v, what), flush=True)
        return v


def produced(el):
    """The nets (uid.terminal) an element and its descendants produce."""
    nets = set()
    for e in el.iter():
        for _, n in pairs(e.get("outputs")):
            if n:
                nets.add(n)
    return nets


def pairs(s):
    """aixml2vi.pairs without the unescape: 'x:5.value,y:7.out' -> [('x', '5.value'), ...], the
    terminal kept escaped so the attribute can be written back unchanged."""
    out = []
    for item in (s or "").split(","):
        if item:
            term, _, net = item.rpartition(":")
            out.append((term, net))
    return out


def without(root, targets):
    """A copy of `root` with the elements whose ids are in `targets` removed and their nets unwired."""
    r = copy.deepcopy(root)
    gone = set()
    for parent in list(r.iter()):
        for ch in list(parent):
            if id_of(ch) in targets:
                gone |= produced(ch)
                parent.remove(ch)
    if gone:
        for e in r.iter():
            for attr in ("inputs", "selectin", "count", "maxin", "maxout"):
                v = e.get(attr)
                if not v:
                    continue
                if attr == "inputs":
                    e.set(attr, ",".join("%s:%s" % (k, "" if n in gone else n) for k, n in pairs(v)))
                elif v in gone:
                    e.set(attr, "")
    return r


def id_of(el):
    return el.get("uid") or "%s:%s" % (el.tag, el.get("_id") or el.get("_name"))


def ddmin(run, root, parent_path, what):
    """Remove as many children of the element at `parent_path` as possible, keeping the crash."""
    def node(r):
        n = r
        for i in parent_path:
            n = [c for c in n if isinstance(c.tag, str)][i]
        return n
    kids = [c for c in node(root) if isinstance(c.tag, str) and c.tag not in ("Tunnel", "CaseFrame")]
    ids = [id_of(c) for c in kids]
    n = 2
    while len(ids) >= 1:
        chunk = max(1, len(ids) // n)
        removed_any = False
        for i in range(0, len(ids), chunk):
            cut = set(ids[i:i + chunk])
            trial = without(root, cut)
            if run.verdict(trial, "%s: drop %d of %d" % (what, len(cut), len(ids))) == "CRASH":
                root, ids = trial, [x for x in ids if x not in cut]
                n = max(n - 1, 2)
                removed_any = True
                break
        if not removed_any:
            if chunk == 1:
                break
            n = min(len(ids), n * 2)
    return root


def main(argv):
    src, out = argv[0], argv[1]
    near = argv[argv.index("--near") + 1] if "--near" in argv else None
    os.makedirs(out, exist_ok=True)
    text = open(src, encoding="utf-8").read()
    tail = "\n".join(re.findall(r"<\?lvmcp[^\n]*\?>", text.rsplit("</VI>", 1)[1]))
    root = ET.parse(src).getroot()
    for fl in [c for c in root if c.tag == "FreeLabel"]:
        root.remove(fl)
    run = Runner(out, near, tail)
    if run.verdict(copy.deepcopy(root), "baseline") != "CRASH":
        print("the baseline does not crash - nothing to minimise")
        return
    root = ddmin(run, root, [], "top")
    # then inside each remaining structure, deepest last
    changed = True
    while changed:
        changed = False
        paths = []

        def walk(n, p):
            for i, c in enumerate([c for c in n if isinstance(c.tag, str)]):
                if c.tag in ("Structure", "CaseFrame"):
                    paths.append(p + [i])
                    walk(c, p + [i])
        walk(root, [])
        for p in paths:
            before = len(list(root.iter()))
            try:
                root = ddmin(run, root, p, "in %s" % p)
            except IndexError:
                continue
            if len(list(root.iter())) < before:
                changed = True
                break
    ET.ElementTree(root).write(os.path.join(out, "min.xml"), encoding="utf-8")
    with open(os.path.join(out, "min.xml"), "a", encoding="utf-8") as f:
        f.write("\n" + tail)
    print("minimal document: %s (%d elements)" % (os.path.join(out, "min.xml"), len(list(root.iter()))))


if __name__ == "__main__":
    main(sys.argv[1:])
