"""Phase 3: how many of the server's own helper VIs (scripts/*.xml) the pylabview composer builds,
and whether they are executable - against NI's converter on the same documents as the control.

Drives the BUILT server over raw stdio, so it measures the product route, not a copy of it:

  NI arm        lvai_convert_aixml_to_vi backend grpc       -> "P3 ni <helper>.vi"
                execState over gRPC (the convert's own check) and over ActiveX (lvai_exec_state)
  composer arm  lvai_convert_aixml_to_vi backend pylabview  -> "P3 py <helper>.vi"
                execState over ActiveX (the convert's own check)

The two arms get different FILE names on purpose: two VIs of one name from two folders collide in
LabVIEW's memory. A LabVIEW that disappears during a step is recorded as CRASH for that step and
restarted (LabVIEWMCP --ensure-labview) before the next helper.

Needs LabVIEW running with its AI service up. Writes helper-coverage.tsv beside this file; the VIs
go to %TEMP%\\LabVIEWMCP\\phase3 and are kept for the parity checks that follow.

    python helper_coverage.py [substring ...]     # only helpers whose name contains one of them
    python helper_coverage.py --skip-ni --tsv other.tsv   # composer arm only, into another file;
                                                          # LABVIEWMCP_AIXML_PARTS picks the parts
"""
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
EXE = os.path.join(REPO, "src", "LabVIEWMCP", "bin", "Debug", "net8.0", "LabVIEWMCP.exe")
OUT = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "phase3")
TSV = os.path.join(HERE, "helper-coverage.tsv")


def helpers():
    found = []
    for sub in ("", "aixml-skeletons", os.path.join("templates", "lunit")):
        folder = os.path.join(REPO, "scripts", sub)
        found += [os.path.join(folder, f) for f in sorted(os.listdir(folder)) if f.endswith(".xml")]
    return found


def users_of(name):
    """The .cs files under src/ that mention the helper's bare name - the tools that run it."""
    base = os.path.splitext(name)[0]
    hits = set()
    for root, dirs, files in os.walk(os.path.join(REPO, "src", "LabVIEWMCP")):
        dirs[:] = [d for d in dirs if d not in ("obj", "bin")]
        for f in files:
            if f.endswith(".cs"):
                with open(os.path.join(root, f), encoding="utf-8-sig") as fh:
                    if base in fh.read():
                        hits.add(f[:-3])
    return sorted(hits)


def labview_alive():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq LabVIEW.exe", "/NH"],
                         capture_output=True, text=True).stdout
    return "LabVIEW.exe" in out


def restart_labview():
    subprocess.run([EXE, "--ensure-labview", "--timeout", "240"], capture_output=True, text=True)


class Server:
    def __init__(self):
        env = dict(os.environ)
        env.pop("LABVIEWMCP_AIXML_BACKEND", None)
        self.p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8", env=env)
        self.id = 0
        self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                 "clientInfo": {"name": "phase3", "version": "0"}})
        self.call("notifications/initialized", notify=True)

    def call(self, method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            self.id += 1
            msg["id"] = self.id
        self.p.stdin.write(json.dumps(msg) + "\n")
        self.p.stdin.flush()
        if notify:
            return None
        while True:
            reply = json.loads(self.p.stdout.readline())
            if reply.get("id") == self.id:
                return reply

    def tool(self, name, args):
        reply = self.call("tools/call", {"name": name, "arguments": args})
        text = reply["result"]["content"][0]["text"]
        try:
            return json.loads(text)
        except ValueError:
            return {"raw": text}

    def close(self):
        try:
            self.p.stdin.close()
            self.p.terminate()
        except OSError:
            pass


def one_line(text, limit=160):
    text = re.sub(r"\s+", " ", str(text or "")).strip()
    return text if len(text) <= limit else text[:limit - 3] + "..."


def step(server, label, name, args):
    """One tool call; a LabVIEW that is gone afterwards is a CRASH, restarted before the next."""
    t0 = time.time()
    body = server.tool(name, args)
    secs = round(time.time() - t0, 2)
    if not labview_alive():
        print(f"    {label}: LabVIEW DISAPPEARED - restarting", flush=True)
        restart_labview()
        return {"crash": True}, secs
    return body, secs


def main(argv):
    skip_ni = "--skip-ni" in argv
    tsv = os.path.join(HERE, argv[argv.index("--tsv") + 1]) if "--tsv" in argv else TSV
    filters = [a for i, a in enumerate(argv)
               if not a.startswith("--") and (i == 0 or argv[i - 1] != "--tsv")]
    os.makedirs(OUT, exist_ok=True)
    rows = []
    server = Server()
    try:
        for path in helpers():
            name = os.path.basename(path)
            if filters and not any(f in name for f in filters):
                continue
            base = os.path.splitext(name)[0]
            ni_vi = os.path.join(OUT, f"P3 ni {base}.vi")
            py_vi = os.path.join(OUT, f"P3 py {base}.vi")
            row = {"helper": os.path.relpath(path, os.path.join(REPO, "scripts")).replace("\\", "/"),
                   "tools": " ".join(users_of(name))}

            ni, row["ni_s"] = ({}, "") if skip_ni else step(
                server, "ni convert", "lvai_convert_aixml_to_vi",
                {"aiXmlFilePath": path, "viPath": ni_vi, "backend": "grpc"})
            row["ni_convert"] = "CRASH" if ni.get("crash") else str(ni.get("errorCode", ni.get("errorKind", "")))
            row["ni_exec_grpc"] = "" if ni.get("crash") else str(ni.get("execState", ""))
            row["ni_reason"] = "" if ni.get("errorCode") in (0, None) else one_line(ni.get("errorMessage"))
            if os.path.exists(ni_vi) and not skip_ni:
                com, _ = step(server, "ni exec com", "lvai_exec_state", {"viPath": ni_vi, "backend": "pylabview"})
                row["ni_exec_com"] = "CRASH" if com.get("crash") else str(com.get("execState", com.get("errorKind")))
            else:
                row["ni_exec_com"] = ""

            py, row["py_s"] = step(server, "py compose", "lvai_convert_aixml_to_vi",
                                   {"aiXmlFilePath": path, "viPath": py_vi, "backend": "pylabview"})
            if py.get("crash"):
                row.update(py_compose="CRASH", py_reason="", py_exec_com="")
            else:
                row["py_compose"] = "composed" if py.get("errorCode") == 0 else py.get("errorKind", "?")
                row["py_reason"] = "" if py.get("errorCode") == 0 else one_line(py.get("errorMessage"))
                row["py_exec_com"] = str(py.get("execState", "")) if py.get("errorCode") == 0 else ""
                if py.get("executableNote"):
                    row["py_reason"] = one_line(py["executableNote"])

            rows.append(row)
            print(f"{row['helper']:<48} ni {row['ni_convert']}/{row['ni_exec_grpc']}/{row['ni_exec_com']:<3}"
                  f" py {row['py_compose']:<16} {row['py_exec_com']:<2} {row['py_reason'][:70]}", flush=True)
    finally:
        server.close()

    cols = ["helper", "tools", "ni_convert", "ni_exec_grpc", "ni_exec_com", "ni_reason",
            "py_compose", "py_exec_com", "py_reason", "ni_s", "py_s"]
    if not filters:
        with open(tsv, "w", encoding="utf-8", newline="\n") as f:
            f.write("\t".join(cols) + "\n")
            for r in rows:
                f.write("\t".join(str(r.get(c, "")) for c in cols) + "\n")
    composed = [r for r in rows if r["py_compose"] == "composed"]
    print(f"\n{len(rows)} helpers: NI converts {sum(r['ni_convert'] == '0' for r in rows)}, "
          f"composer builds {len(composed)}, of those executable over ActiveX "
          f"{sum(r['py_exec_com'] == '1' for r in composed)}; "
          f"crashes {sum('CRASH' in (r['ni_convert'], r['ni_exec_com'], r['py_compose']) for r in rows)}")


if __name__ == "__main__":
    main(sys.argv[1:])
