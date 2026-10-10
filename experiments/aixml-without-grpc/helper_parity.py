"""Phase 3 parity: does a COMPOSED helper compute what NI's converter built from the same AIXML?

Runs each read-only helper twice over ActiveX - "P3 ni <helper>.vi" and "P3 py <helper>.vi", as
left in %TEMP%\\LabVIEWMCP\\phase3 by helper_coverage.py - with the same inputs, through the built
server's lvai_run_vi_and_read_values (backend pylabview), and compares every value read back.
Both run in the same application instance, so a difference is the composer's, not LabVIEW's.

Needs LabVIEW running, and LABVIEWMCP_AIXML_PARTS set the way the coverage run had it (the VIs are
already built; the parts only matter for nothing here, the reader lists the panel).
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
EXE = os.path.join(REPO, "src", "LabVIEWMCP", "bin", "Debug", "net8.0", "LabVIEWMCP.exe")
BUILT = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "phase3")
WORK = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "phase3-parity")
LV = r"C:\Program Files (x86)\National Instruments\LabVIEW 2026"
BROKEN = os.path.join(WORK, "broken add.vi")

CASES = [
    ("lvai_active_project", {}),
    ("lvai_vis_in_memory", {}),
    ("lvctl_kind", {"VI Path": LV + r"\vi.lib\silver_ctls\IO\DAQmx Task Name NI_Silver.ctl"}),
    ("lvbd_pane_typedefs", {"vi path": LV + r"\vi.lib\Utility\AggHandler\Bounds.vi"}),
    ("lvbd_broken_reason", {"VI Path": BROKEN}),
    ("lvdoc_print", {"VI Path": LV + r"\vi.lib\Utility\AggHandler\Bounds.vi",
                     "HTML File Path": os.path.join(WORK, "{arm}", "bounds.html"),
                     "Image Directory": os.path.join(WORK, "{arm}")}),
    # composed only after the Phase 3 repairs (learned terminal names, method aliases)
    ("lvai_exec_state", {"vi path": BROKEN}),
    ("lvai_exec_state", {"vi path": LV + r"\vi.lib\Utility\AggHandler\Bounds.vi"}),
    ("lvdoc_get_icon", {"VI Path": LV + r"\vi.lib\Utility\AggHandler\Bounds.vi",
                        "Read Back Path": os.path.join(WORK, "{arm}", "icon.png")}),
]


class Server:
    def __init__(self):
        env = dict(os.environ)
        env.pop("LABVIEWMCP_AIXML_BACKEND", None)
        self.p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8", env=env)
        self.id = 0
        self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                 "clientInfo": {"name": "parity", "version": "0"}})
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
        return json.loads(self.call("tools/call", {"name": name, "arguments": args})
                          ["result"]["content"][0]["text"])


def normalise(value, arm):
    """The helper's own name appears in what it reads about memory; the arm prefix is not a result."""
    return (value or "").replace("P3 ni ", "").replace("P3 py ", "")


def main():
    os.makedirs(WORK, exist_ok=True)
    server = Server()
    # a VI that is broken on purpose, composed from the phase-2 probe document
    broken_src = os.path.join(os.environ["TEMP"], "claude", "C--Projects-LabVIEWMCP",
                              "3e50f8b9-0a6c-4e11-a9fd-4411f1a52c05", "scratchpad", "broken_add.xml")
    print("broken VI:", server.tool("lvai_convert_aixml_to_vi", {
        "aiXmlFilePath": broken_src, "viPath": BROKEN, "backend": "pylabview"}).get("executable"))
    same = 0
    for helper, inputs in CASES:
        results = {}
        for arm in ("ni", "py"):
            args = {k: v.replace("{arm}", arm) for k, v in inputs.items()}
            for v in args.values():
                if "{arm}" not in v and v.startswith(WORK):
                    os.makedirs(os.path.dirname(v), exist_ok=True)
            os.makedirs(os.path.join(WORK, arm), exist_ok=True)
            body = server.tool("lvai_run_vi_and_read_values", {
                "viPath": os.path.join(BUILT, f"P3 ni {helper}.vi") if arm == "ni"
                          else os.path.join(BUILT, "py", f"{helper}.vi"),
                "inputsJson": json.dumps(args), "backend": "pylabview", "timeoutSeconds": 60})
            if not body.get("ok"):
                results[arm] = {"FAILED": f"{body.get('errorKind')}: {body.get('error')}"}
                continue
            results[arm] = {k: normalise(v.get("value"), arm) for k, v in body["values"].items()
                            if k not in args}
        if helper == "lvdoc_print":
            for arm in ("ni", "py"):
                folder = os.path.join(WORK, arm)
                results[arm]["files"] = " ".join(sorted(os.listdir(folder)))
        diff = {k: (results["ni"].get(k), results["py"].get(k))
                for k in set(results["ni"]) | set(results["py"]) if results["ni"].get(k) != results["py"].get(k)}
        same += not diff
        print(f"{helper:<22} {'SAME' if not diff else 'DIFFERENT'}  outputs {sorted(results['ni'])}")
        for k, (a, b) in diff.items():
            print(f"    {k}: ni={str(a)[:150]!r}  py={str(b)[:150]!r}")
    print(f"{same} of {len(CASES)} helpers gave identical outputs")
    server.p.terminate()


if __name__ == "__main__":
    main()
