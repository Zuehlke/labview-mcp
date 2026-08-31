#!/usr/bin/env python3
"""Measure whether pylabview can round-trip LabVIEW binaries without losing content.

For every input file (.vi, .ctl, a .lvclass member VI, ...):

  1. extract  readRSRC.py -x   binary -> XML set
  2. create   readRSRC.py -c   XML set -> rebuilt binary
  3. dump     readRSRC.py -d   both binaries -> one .bin per block section,
              decompressed, then compared byte for byte

Step 3 is the acceptance criterion, NOT step 2's byte equality: pylabview
recompresses zlib sections at a different level (0x78DA -> 0x789C), which shifts
every following offset, so a faithful rebuild is normally not byte-identical to
the original. Equal decompressed blocks mean LabVIEW is handed the same content.

Usage:
    python roundtrip.py --pylabview <clone> --out <workdir> [--jobs N] FILE...
    python roundtrip.py --pylabview <clone> --out <workdir> --list files.txt
"""
import argparse
import concurrent.futures
import filecmp
import pathlib
import shutil
import subprocess
import sys
import time

READ_RSRC = pathlib.Path("pylabview") / "readRSRC.py"

COLUMNS = ["ok", "kind", "orig_bytes", "xml_bytes", "xml_files", "byte_identical",
           "content_identical", "seconds", "stage", "note", "warnings", "file"]


def run(pylabview_root, python_exe, args, timeout):
    """Invoke readRSRC.py from the clone root - it imports its own package by CWD."""
    proc = subprocess.run(
        [python_exe, str(READ_RSRC)] + list(args),
        cwd=str(pylabview_root),
        capture_output=True,
        text=True,
        timeout=timeout,
    )
    return proc.returncode, (proc.stdout + proc.stderr)


def compare_dumps(dir_a, dir_b):
    """Compare two -d dumps block by block. Returns (identical, note)."""
    names_a = sorted(p.name for p in dir_a.iterdir())
    names_b = sorted(p.name for p in dir_b.iterdir())
    if names_a != names_b:
        only_a = sorted(set(names_a) - set(names_b))
        only_b = sorted(set(names_b) - set(names_a))
        return False, "block set differs: missing %s, extra %s" % (only_a, only_b)
    match, mismatch, errors = filecmp.cmpfiles(dir_a, dir_b, names_a, shallow=False)
    if mismatch or errors:
        note = "differing blocks: %s" % sorted(mismatch)
        if errors:
            note += " unreadable: %s" % sorted(errors)
        return False, note
    return True, "%d blocks identical" % len(match)


def roundtrip(src, pylabview_root, python_exe, work_root, timeout):
    src = pathlib.Path(src)
    slot = work_root / ("%08x" % (abs(hash(str(src))) & 0xFFFFFFFF))
    if slot.exists():
        shutil.rmtree(slot, ignore_errors=True)
    for sub in ("xml", "dump_orig", "dump_new"):
        (slot / sub).mkdir(parents=True, exist_ok=True)

    result = {
        "file": str(src),
        "kind": src.suffix.lower(),
        "orig_bytes": src.stat().st_size,
        "stage": "",
        "ok": False,
        "byte_identical": False,
        "content_identical": False,
        "xml_bytes": 0,
        "xml_files": 0,
        "note": "",
        "warnings": "",
        "seconds": 0.0,
    }
    started = time.time()
    try:
        main_xml = slot / "xml" / "main.xml"
        rebuilt = slot / ("rebuilt" + src.suffix)

        result["stage"] = "extract"
        rc, log = run(pylabview_root, python_exe,
                      ["-x", "-i", str(src), "-m", str(main_xml)], timeout)
        warn = [ln.split(": ", 1)[-1] for ln in log.splitlines()
                if "Warning:" in ln or "failed" in ln]
        result["warnings"] = " | ".join(sorted(set(warn))[:4])
        if rc != 0 or not main_xml.exists():
            result["note"] = (log.strip().splitlines() or ["no output"])[-1][:300]
            return result
        xmls = [p for p in (slot / "xml").iterdir() if p.is_file()]
        result["xml_files"] = len(xmls)
        result["xml_bytes"] = sum(p.stat().st_size for p in xmls)

        result["stage"] = "create"
        rc, log = run(pylabview_root, python_exe,
                      ["-c", "-m", str(main_xml), "-i", str(rebuilt)], timeout)
        if rc != 0 or not rebuilt.exists():
            result["note"] = (log.strip().splitlines() or ["no output"])[-1][:300]
            return result

        result["byte_identical"] = filecmp.cmp(str(src), str(rebuilt), shallow=False)

        result["stage"] = "dump"
        for binary, out in ((src, "dump_orig"), (rebuilt, "dump_new")):
            rc, log = run(pylabview_root, python_exe,
                          ["-d", "-i", str(binary), "-m", str(slot / out / "d.xml")], timeout)
            if rc != 0:
                result["note"] = "dump failed: " + (log.strip().splitlines() or [""])[-1][:200]
                return result

        same, note = compare_dumps(slot / "dump_orig", slot / "dump_new")
        result["content_identical"] = same
        result["note"] = note
        result["ok"] = same
        result["stage"] = "done"
    except subprocess.TimeoutExpired:
        result["note"] = "timeout after %ds in stage %s" % (timeout, result["stage"])
    except Exception as exc:                    # report it, never abort the sweep
        result["note"] = "%s: %s" % (type(exc).__name__, exc)
    finally:
        result["seconds"] = round(time.time() - started, 2)
        shutil.rmtree(slot, ignore_errors=True)
    return result


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pylabview", required=True, help="root of the pylabview clone")
    ap.add_argument("--out", required=True, help="scratch directory for intermediates")
    ap.add_argument("--python", default=sys.executable, help="interpreter that has Pillow")
    ap.add_argument("--jobs", type=int, default=6)
    ap.add_argument("--timeout", type=int, default=300)
    ap.add_argument("--list", dest="listfile", help="file with one input path per line")
    ap.add_argument("--tsv", help="write the per-file table here")
    ap.add_argument("files", nargs="*")
    args = ap.parse_args()

    targets = list(args.files)
    if args.listfile:
        with open(args.listfile, encoding="utf-8") as fh:
            targets += [ln.strip() for ln in fh if ln.strip()]
    targets = [t for t in targets if pathlib.Path(t).is_file()]
    if not targets:
        ap.error("no existing input files")

    work_root = pathlib.Path(args.out)
    work_root.mkdir(parents=True, exist_ok=True)
    rows = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        futures = [pool.submit(roundtrip, t, pathlib.Path(args.pylabview),
                               args.python, work_root, args.timeout) for t in targets]
        for done in concurrent.futures.as_completed(futures):
            row = done.result()
            rows.append(row)
            print("%-4s %-7s %8d B -> %9d B XML %6.1fs  %s | %s" % (
                "OK" if row["ok"] else "FAIL", row["kind"], row["orig_bytes"],
                row["xml_bytes"], row["seconds"],
                pathlib.Path(row["file"]).name, row["note"][:70]), flush=True)

    rows.sort(key=lambda r: (r["ok"], r["file"]))
    if args.tsv:
        with open(args.tsv, "w", encoding="utf-8", newline="") as fh:
            fh.write("\t".join(COLUMNS) + "\n")
            for r in rows:
                fh.write("\t".join(str(r[c]) for c in COLUMNS) + "\n")

    ok = sum(1 for r in rows if r["ok"])
    byte_id = sum(1 for r in rows if r["byte_identical"])
    src_total = sum(r["orig_bytes"] for r in rows)
    xml_total = sum(r["xml_bytes"] for r in rows)
    print("")
    print("%d/%d content-identical, %d byte-identical" % (ok, len(rows), byte_id))
    if src_total:
        print("XML expansion: %.2f MB source -> %.2f MB XML (x%.1f)" % (
            src_total / 1e6, xml_total / 1e6, xml_total / src_total))
    for r in rows:
        if not r["ok"]:
            print("  FAIL %s [%s] %s" % (
                pathlib.Path(r["file"]).name, r["stage"], r["note"][:160]))


if __name__ == "__main__":
    main()
