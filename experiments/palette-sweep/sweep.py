# -*- coding: utf-8 -*-
"""Compose AIXML with the pylabview composer and record every refusal, attributed to an element.

Runs under the server's bundled Python (it imports pylabview through lvheap):

    <bundle>\\python.exe sweep.py examples <outdir> [--shard i/n] [--limit N]
    <bundle>\\python.exe sweep.py calls    <outdir> [--shard i/n]
    <bundle>\\python.exe sweep.py nodes    <outdir> [--shard i/n]

examples: every cached AIXML export of an NI example is composed. On a refusal the element the
          message names is CUT (with its subtree and every wire end that names it) and the rest is
          composed again, up to MAX_CUTS times - so one gap does not hide the next.
calls:    one VI per palette VI (palette index): a single unwired Call of its CallTarget.
nodes:    one VI per known primitive name: a single unwired Node.

Writes <outdir>\\<mode>-<shard>.jsonl, one object per input.
"""
import contextlib
import glob
import io
import json
import os
import re
import sys
import time
import traceback
import xml.etree.ElementTree as ET

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
COMPOSER = os.path.join(REPO, "scripts", "aixml-pylabview")
sys.path.insert(0, COMPOSER)
import aixml2vi  # noqa: E402

CACHE = os.path.join(os.environ["USERPROFILE"], ".labviewmcp", "cache", "aixml")
MAX_CUTS = 25
PANE = ('  <Control _name="error in" type="cluster{bool.status,int32.code,string.source}" '
        'uid="4100" uid_parent="root" value="[false,0,]" outputs="value:" conIdx="8" '
        'connection="required" />\n')


def compose(src_xml, out_vi, search=()):
    """(ok, message): the composer's own main(), stderr captured."""
    argv = [src_xml, out_vi]
    for s in search:
        argv += ["--search", s]
    err, out = io.StringIO(), io.StringIO()
    try:
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
            rc = aixml2vi.main(argv)
    except SystemExit as ex:
        return False, "exit: %s %s" % (ex.code, err.getvalue().strip()[-600:])
    except Exception as ex:  # a composer crash is a finding of its own
        tb = traceback.extract_tb(ex.__traceback__)
        at = "%s:%d" % (os.path.basename(tb[-1].filename), tb[-1].lineno) if tb else "?"
        return False, "CRASH %s: %s @ %s" % (type(ex).__name__, str(ex)[:400], at)
    if rc == 0:
        return True, ""
    msg = err.getvalue().strip()
    return False, msg.replace("UNSUPPORTED: ", "", 1)[:800]


def key_of(el):
    t = el.tag
    if t == "Node":
        return "node:" + (el.get("_name") or "?")
    if t == "Call":
        return "call:" + (el.get("target") or "?")
    if t == "Structure":
        return "struct:" + (el.get("_name") or "?")
    if t == "FixedConst":
        return "fixed:" + (el.get("_name") or "?")
    if t in ("Control", "Indicator"):
        st = el.get("style")
        return "panel:" + (re.sub(r"\d+$", "", st) if st else (el.get("type") or "?"))
    if t == "Constant":
        return "const:" + (el.get("type") or "?")
    return "sub:" + t


def find_culprit(root, msg):
    """The element a refusal names: a uid first, then a quoted name/target/type/style."""
    parent = {c: p for p in root.iter() for c in p}
    by_uid = {el.get("uid"): el for el in root.iter() if el.get("uid")}
    # uids: 'Node X 123', 'uid 123', '%s %s' formats - any number that is an element uid
    for num in re.findall(r"(?<![\w.])(\d{1,7})(?![\w.])", msg):
        if num in by_uid and by_uid[num] is not root:
            return by_uid[num], parent
    quoted = re.findall(r"'([^']+)'|\"([^\"]+)\"", msg)
    quoted = [a or b for a, b in quoted]
    for q in quoted:
        for el in root.iter():
            if el is root:
                continue
            for attr in ("_name", "target", "style", "instance"):
                if el.get(attr) == q:
                    return el, parent
    for q in quoted:
        for el in root.iter():
            if el is root or el.tag in ("VI",):
                continue
            ty = el.get("type") or ""
            if q and (ty == q or (len(q) > 6 and q in ty)):
                return el, parent
    return None, parent


def cut(root, el, parent):
    gone = {e.get("uid") for e in el.iter() if e.get("uid")}
    p = parent.get(el)
    if p is not None:
        p.remove(el)
    # a uid_parent on the top level (Structure frames are nested, so nesting decides)
    for e in list(root.iter()):
        if e.get("uid_parent") in gone and e is not root:
            pp = {c: q for q in root.iter() for c in q}.get(e)
            if pp is not None:
                pp.remove(e)
                gone |= {x.get("uid") for x in e.iter() if x.get("uid")}
    for e in root.iter():
        for attr in ("inputs", "outputs"):
            v = e.get(attr)
            if not v:
                continue
            parts = []
            for item in v.split(","):
                term, _, net = item.partition(":")
                if net and net.split(".")[0] in gone:
                    net = ""
                parts.append("%s:%s" % (term, net))
            e.set(attr, ",".join(parts))
    return gone


def example_paths():
    """Every NI example VI, from the server's example index (the export cache is emptied each time
    the reader's code changes, so it is no list to iterate)."""
    idx = max(glob.glob(os.path.join(CACHE, "..", "example-index-*.json")), key=os.path.getmtime)
    rows = json.load(open(idx, encoding="utf-8"))["Index"]["Examples"]
    return sorted({r["Path"] for r in rows if r.get("Path", "").lower().endswith(".vi") and os.path.exists(r["Path"])})


def run_examples(outdir, shard, limit):
    """(cached export or None, VI path) per example; --fresh reads every one anew."""
    cached = {}
    for m in glob.glob(os.path.join(CACHE, "*.json")):
        try:
            cached[json.load(open(m, encoding="utf-8")).get("ViPath", "").lower()] = m[:-5] + ".xml"
        except ValueError:
            pass
    import hashlib
    jobs = []
    for p in example_paths():
        x = cached.get(p.lower())
        if x is None:
            # no export cached: a stem of its own, and the reader reads it (FRESH or not)
            x = os.path.join(CACHE, hashlib.sha1(p.lower().encode("utf-8")).hexdigest()[:16].upper() + ".nocache.xml")
        jobs.append((x, p))
    return jobs


FRESH = "--fresh" in sys.argv


def fresh_read(vipath, work):
    """The example read NOW by the current reader (vi2aixml.py), not the cached export - after a
    reader change the cache holds the old reader's output."""
    import vi2aixml
    out = os.path.join(work, "fresh.xml")
    err, so = io.StringIO(), io.StringIO()
    try:
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(so):
            rc = vi2aixml.main([vipath, out])
    except SystemExit as ex:
        rc = ex.code
    except Exception as ex:
        return None, "reader %s: %s" % (type(ex).__name__, str(ex)[:300])
    if rc not in (0, None) or not os.path.exists(out):
        return None, "reader exit %s: %s" % (rc, err.getvalue().strip()[-300:])
    return out, None


def do_example(job, work):
    xml, vipath = job
    stem = os.path.splitext(os.path.basename(xml))[0]
    if FRESH or not os.path.exists(xml):
        got, why = fresh_read(vipath, work)
        if got is None:
            return {"vi": vipath, "steps": [], "ok": False, "unreadable": why, "cuts": 0}
        xml = got
    rec = {"vi": vipath, "steps": [], "ok": False}
    try:
        tree = ET.parse(xml)
    except (ET.ParseError, OSError) as ex:
        rec["unreadable"] = str(ex)
        return rec
    root = tree.getroot()
    # the reader's <?lvmcp ...?> lines after </VI> (classes, styles, pane, ...) - ElementTree
    # drops them, and without them the composer refused what it can build (2026-11-19)
    raw = open(xml, encoding="utf-8", errors="replace").read()
    tail = raw.rsplit("</VI>", 1)[1] if "</VI>" in raw else ""
    for i in range(MAX_CUTS + 1):
        src = os.path.join(work, stem + ".xml")
        body = ET.tostring(root, encoding="unicode")
        with open(src, "w", encoding="utf-8") as f:
            f.write(body + tail)
        out = os.path.join(work, stem + ".vi")
        ok, msg = compose(src, out, [os.path.dirname(vipath)])
        if ok:
            rec["ok"] = True
            rec["out"] = out
            break
        el, parent = find_culprit(root, msg)
        step = {"msg": msg}
        if el is None:
            step["key"] = None
            rec["steps"].append(step)
            break
        step["key"] = key_of(el)
        step["uid"] = el.get("uid")
        rec["steps"].append(step)
        cut(root, el, parent)
    rec["cuts"] = len(rec["steps"])
    return rec


def do_single(body, name, work, key):
    doc = ('<VI _name="Sweep %s.vi" description="palette sweep">\n%s%s</VI>\n'
           % (re.sub(r"[^\w]+", "_", name)[:60], PANE, body))
    stem = "s%08x" % (hash(key) & 0xffffffff)
    src = os.path.join(work, stem + ".xml")
    open(src, "w", encoding="utf-8").write(doc)
    ok, msg = compose(src, os.path.join(work, stem + ".vi"))
    return {"key": key, "ok": ok, "msg": msg, "out": os.path.join(work, stem + ".vi") if ok else None}


def esc(s):
    return (s.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;")
            .replace(">", "&gt;").replace("\\", "\\5C").replace(":", "\\3A").replace(",", "\\2C"))


def main():
    mode, outdir = sys.argv[1], sys.argv[2]
    shard, nshard, limit = 0, 1, None
    a = sys.argv[3:]
    if "--shard" in a:
        s = a[a.index("--shard") + 1]
        shard, nshard = (int(x) for x in s.split("/"))
    if "--limit" in a:
        limit = int(a[a.index("--limit") + 1])
    work = os.path.join(outdir, "%s-%d" % (mode, shard))
    os.makedirs(work, exist_ok=True)
    if mode == "examples":
        jobs = run_examples(outdir, shard, limit)
    elif mode == "calls":
        idx = json.load(open(sys.argv[a.index("--index") + 4] if "--index" in a else
                             max(glob.glob(os.path.join(CACHE, "..", "palette-index-*.json")),
                                 key=os.path.getmtime), encoding="utf-8"))["Index"]["Vis"]
        jobs = sorted({v.get("CallTarget") or v["Name"] for v in idx})
    else:
        jobs = [l.strip() for l in open(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                     "node-names.txt"), encoding="utf-8") if l.strip()]
    jobs = jobs[shard::nshard]
    if limit:
        jobs = jobs[:limit]
    path = os.path.join(outdir, "%s-%d.jsonl" % (mode, shard))
    done = set()
    if os.path.exists(path):
        for l in open(path, encoding="utf-8"):
            try:
                r = json.loads(l)
                done.add(r.get("vi") or r.get("key"))
            except ValueError:
                pass
    with open(path, "a", encoding="utf-8") as f:
        for j in jobs:
            ident = j[1] if mode == "examples" else j
            if ident in done:
                continue
            t0 = time.time()
            if mode == "examples":
                rec = do_example(j, work)
            elif mode == "calls":
                body = ('  <Call uid="4200" uid_parent="root" target="%s" inputs="" outputs="" />\n'
                        % j.replace("&", "&amp;").replace('"', "&quot;"))
                rec = do_single(body, j, work, "call:" + j)
            else:
                body = ('  <Node _name="%s" uid="4200" uid_parent="root" inputs="" outputs="" />\n'
                        % j.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;").replace(">", "&gt;"))
                rec = do_single(body, j, work, "node:" + j)
            rec["s"] = round(time.time() - t0, 2)
            f.write(json.dumps(rec) + "\n")
            f.flush()


if __name__ == "__main__":
    main()
