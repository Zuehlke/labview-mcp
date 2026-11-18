#!/usr/bin/env python3
"""accept_batch_calls.py - the acceptance test of the batch forms, against a running LabVIEW.

Builds a fresh test project and drives one LabVIEWMCP.exe over raw stdio (no MCP client, so a
branch build's new parameters are reachable without a client restart), then checks every answer:

  1. lvai_check_build_contract forBuilder   - a builder's slice, an unknown builder refused,
                                              and mode=verify against the built VI
  2. lvai_run_vi_and_read_values inputSetsJson - several runs, a failed set keeps its answer
  3. lvai_dqmh_new_event eventsJson         - a bad entry refused before anything is scripted,
                                              then four events in one resumable job
  4. lvai_dqmh_place_handler handlersJson   - dry run, three frames, a stop at an occupied
                                              first frame (nextHandler 0, Main.vi unchanged),
                                              and a free frame before an occupied one
                                              (nextHandler 1, one layout, Main.vi executable)
  5. lvai_dqmh_new_unit_test argumentsJson/expectedJson/runTest - a test filled and run green, a
                                              wrong expectation red, a misspelt field refused
                                              before Delacor scripts (no file written)
  6. lvai_create_library over a project folder - a slim answer, the emptied folder removed by
                                              the close sweep, a user's own empty folder kept

    python -I accept_batch_calls.py [--exe <LabVIEWMCP.exe>] [--root <new folder>]

Defaults: the Debug build of this checkout, and C:\\Temp\\BatchCallsAccept_<yyyyMMdd-HHmmss>.
The root must not exist yet. PRECONDITIONS: LabVIEW 2026 running (lvai_ensure_labview), DQMH 7
installed, and NO project open in the IDE - the test opens its own and closes it at the end.
It takes about ten minutes, most of it Delacor's scripting. Python 3 standard library only.

Exit code 0 when every check passed, 1 when one failed, 2 when the server went away.
"""
import argparse
import datetime
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DEFAULT_EXE = os.path.join(REPO, "src", "LabVIEWMCP", "bin", "Debug", "net8.0", "LabVIEWMCP.exe")
ERR = "cluster{bool.status,int32.code,string.source}"


# ------------------------------------------------------------------ the server

class Server:
    def __init__(self, exe):
        self.proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        self.next_id = 0
        self._call_raw("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                      "clientInfo": {"name": "accept_batch_calls", "version": "1"}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _send(self, msg):
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()

    def _call_raw(self, method, params):
        self.next_id += 1
        self._send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params})
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise SystemExit(2)
            msg = json.loads(line)
            if msg.get("id") == self.next_id:
                return msg

    def call(self, name, **arguments):
        """One tools/call; the answer's text parsed as JSON (a dict with `_text` when it is not)."""
        started = time.monotonic()
        msg = self._call_raw("tools/call", {"name": name, "arguments": arguments})
        seconds = time.monotonic() - started
        if "error" in msg:
            return {"ok": False, "_rpcError": msg["error"], "_seconds": seconds}
        text = "\n".join(c.get("text", "") for c in msg["result"].get("content", []))
        try:
            answer = json.loads(text)
        except ValueError:
            answer = {"_text": text}
        answer["_seconds"] = seconds
        return answer

    def call_until_done(self, name, **arguments):
        """A resumable tool: the SAME call again while it answers stillRunning."""
        calls = 0
        while True:
            calls += 1
            answer = self.call(name, **arguments)
            if answer.get("errorKind") != "stillRunning":
                answer["_calls"] = calls
                return answer
            print("   ... stillRunning %.0f s, progress %s" % (
                answer.get("runningForSeconds", 0), json.dumps(answer.get("progress"))))

    def close(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=30)


# ------------------------------------------------------------------ the checks

class Checks:
    def __init__(self):
        self.rows = []

    def check(self, step, what, condition, detail=""):
        self.rows.append((step, what, bool(condition), detail))
        print("   %s  %s%s" % ("PASS" if condition else "FAIL", what, (" - " + detail) if detail and not condition else ""))
        return bool(condition)

    @property
    def failed(self):
        return [r for r in self.rows if not r[2]]


def short(answer):
    return json.dumps({k: v for k, v in answer.items() if not k.startswith("_")})[:600]


# ------------------------------------------------------------------ fixtures

def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


PROJECT = """<?xml version='1.0' encoding='UTF-8'?>
<Project Type="Project" LVVersion="26008000">
\t<Property Name="NI.LV.All.SaveVersion" Type="Str">26.0</Property>
\t<Property Name="NI.LV.All.SourceOnly" Type="Bool">true</Property>
\t<Item Name="My Computer" Type="My Computer">
\t\t<Property Name="server.app.propertiesEnabled" Type="Bool">true</Property>
\t\t<Property Name="specify.custom.address" Type="Bool">false</Property>
\t\t<Item Name="Dependencies" Type="Dependencies"/>
\t\t<Item Name="Build Specifications" Type="Build"/>
\t</Item>
</Project>
"""


def control(name, con, connection, type_, uid, value):
    return ('  <Control _name="%s" conIdx="%d" connection="%s" description="%s." outputs="value:%s.value" '
            'type="%s" uid="%s" uid_parent="root" value="%s"/>\n' % (name, con, connection, name, uid, type_, uid, value))


def indicator(name, con, source, type_, uid, value):
    return ('  <Indicator _name="%s" conIdx="%d" connection="recommended" description="%s." inputs="value:%s" '
            'type="%s" uid="%s" uid_parent="root" value="%s"/>\n' % (name, con, name, source, type_, uid, value))


def vi(name, description, comment, body):
    return ('<?xml version="1.0" encoding="UTF-8"?>\n<VI _name="%s" description="%s">\n%s'
            '  <FreeLabel comment="%s" uid="4290" uid_parent="root"/>\n</VI>\n' % (name, description, body, comment))


ERROR_IN = control("error in", 11, "required", ERR, "4201", "[false,0,]")

FIXTURES = {
    # the run target: sum = x + y, on the pane the contract names
    "Add Numbers": vi("Add Numbers.vi", "Adds x and y.", "Adds the two inputs",
        control("x", 0, "recommended", "double", "4202", "0")
        + control("y", 5, "recommended", "double", "4203", "0")
        + ERROR_IN
        + '  <Node _name="Add" inputs="x:4202.value,y:4203.value" outputs="x+y:4210.x+y" uid="4210" uid_parent="root"/>\n'
        + indicator("sum", 4, "4210.x+y", "double", "4204", "0")
        + indicator("error out", 15, "4201.value", ERR, "4205", "[false,0,]")),
    # DQMH handlers, named after the events' argument and reply fields
    "Set Rate Core": vi("Set Rate Core.vi", "Handler for Set Rate.", "Test handler: shows the rate",
        control("Rate", 0, "recommended", "double", "4202", "0") + ERROR_IN
        + indicator("Rate Seen", 4, "4202.value", "double", "4204", "0")
        + indicator("error out", 15, "4201.value", ERR, "4205", "[false,0,]")),
    "Get Balance Core": vi("Get Balance Core.vi", "Handler for Get Balance.", "Test handler: a fixed balance",
        control("Account", 0, "recommended", "string", "4202", "") + ERROR_IN
        + '  <Constant _name="Fixed" type="double" value="42" outputs="value:4210.value" uid="4210" uid_parent="root"/>\n'
        + indicator("Balance", 4, "4210.value", "double", "4204", "0")
        + indicator("error out", 15, "4201.value", ERR, "4205", "[false,0,]")),
    "Error Only Core": vi("Error Only Core.vi", "Handler for an event without arguments.", "Test handler: error only",
        ERROR_IN + indicator("error out", 15, "4201.value", ERR, "4205", "[false,0,]")),
}


def contract(root):
    error_in = {"name": "error in", "type": "@Error", "conIdx": 11, "connection": "required"}
    error_out = {"name": "error out", "type": "@Error", "conIdx": 15, "connection": "recommended"}
    return {
        "application": "Batch calls acceptance",
        "root": root,
        "project": "BatchCallsAccept.lvproj",
        "types": {"Error": ERR, "Unused": "cluster{bool.X}"},
        "vis": [
            {"path": "SubVIs\\Add Numbers.vi", "builder": "leaf", "purpose": "Adds x and y.",
             "inputs": [{"name": "x", "type": "double", "conIdx": 0, "connection": "recommended"},
                        {"name": "y", "type": "double", "conIdx": 5, "connection": "recommended"}, error_in],
             "outputs": [{"name": "sum", "type": "double", "conIdx": 4, "connection": "recommended"}, error_out]},
            {"path": "Main\\Sum Twice.vi", "builder": "main", "purpose": "Calls Add Numbers twice.",
             "calls": ["Add Numbers.vi"],
             "inputs": [error_in], "outputs": [error_out]},
        ],
    }


# ------------------------------------------------------------------ the test

def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--exe", default=DEFAULT_EXE)
    parser.add_argument("--root", default=os.path.join(
        "C:\\Temp", "BatchCallsAccept_" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S")))
    args = parser.parse_args()
    root = os.path.abspath(args.root)
    if os.path.exists(root):
        print("%s exists already - give a new --root" % root, file=sys.stderr)
        return 2
    project = os.path.join(root, "BatchCallsAccept.lvproj")
    handlers = os.path.join(root, "Handlers")
    write(project, PROJECT)
    print("test project: %s\nserver: %s" % (project, args.exe))

    server = Server(args.exe)
    c = Checks()
    started = time.monotonic()
    try:
        # ---- 0. LabVIEW, the project, the fixture VIs
        print("\n== 0. setup")
        ready = server.call("lvai_ensure_labview")
        if not c.check("setup", "LabVIEW answers", ready.get("ok"), short(ready)):
            return 1
        opened = server.call("lvai_open_file", projectPath=project, projectName="BatchCallsAccept.lvproj")
        if not c.check("setup", "test project open and active", opened.get("projectBecameActive"), short(opened)):
            return 1
        pairs = []
        for name, text in FIXTURES.items():
            folder = os.path.join(root, "SubVIs") if name == "Add Numbers" else handlers
            write(os.path.join(root, "Scratch", name + ".xml"), text)
            pairs.append({"aixml": os.path.join(root, "Scratch", name + ".xml"),
                          "vi": os.path.join(folder, name + ".vi")})
        generated = server.call("lvai_generate_vis", pairsJson=json.dumps(pairs), keepAixml=True)
        if not c.check("setup", "four fixture VIs generated", generated.get("generated") == 4, short(generated)):
            return 1

        # ---- 1. the contract slice
        print("\n== 1. lvai_check_build_contract forBuilder")
        contract_path = os.path.join(root, "build-contract.json")
        write(contract_path, json.dumps(contract(root), indent=2))
        whole = os.path.getsize(contract_path)
        leaf = server.call("lvai_check_build_contract", contractPath=contract_path, forBuilder="leaf")
        s = leaf.get("slice") or {}
        c.check("contract", "plan ok", leaf.get("ok"), short(leaf))
        c.check("contract", "slice holds only the leaf's VI", [v.get("path") for v in s.get("vis", [])] == ["SubVIs\\Add Numbers.vi"])
        c.check("contract", "only the types it names (Error, not Unused)", sorted(s.get("types", {})) == ["Error"])
        c.check("contract", "application waves left out", "waves" not in leaf)
        main_slice = server.call("lvai_check_build_contract", contractPath=contract_path, forBuilder="MAIN").get("slice") or {}
        c.check("contract", "builder matched without regard to case; a Call per callee",
                "Add Numbers.vi" in (main_slice.get("callSkeletons") or {}) and "sum:" in (main_slice.get("callSkeletons") or {}).get("Add Numbers.vi", ""))
        c.check("contract", "the caller's slice is in wave 1", [w.get("wave") for w in main_slice.get("waves", [])] == [1])
        nobody = server.call("lvai_check_build_contract", contractPath=contract_path, forBuilder="nobody")
        c.check("contract", "unknown builder refused with the names",
                nobody.get("errorKind") == "noSuchBuilder" and sorted((nobody.get("detail") or {}).get("builders", [])) == ["leaf", "main"])
        verify = server.call("lvai_check_build_contract", contractPath=contract_path, mode="verify", vis="Add Numbers.vi")
        c.check("contract", "the built VI matches the contract (mode=verify)", verify.get("ok"), short(verify))
        print("   contract file %d chars; leaf answer %d chars" % (whole, len(json.dumps(leaf))))

        # ---- 2. several runs in one call
        print("\n== 2. lvai_run_vi_and_read_values inputSetsJson")
        add = os.path.join(root, "SubVIs", "Add Numbers.vi")
        runs = server.call("lvai_run_vi_and_read_values", viPath=add, inputSetsJson=json.dumps(
            [{"x": "1", "y": "2"}, {"x": "-1.5", "y": "0.25"}, {"x": "1", "No Such Control": "1"}]))
        r = runs.get("runs") or []
        c.check("run", "three runs in one call", runs.get("runsDone") == 3, short(runs))
        c.check("run", "1 + 2 = 3", len(r) > 0 and r[0].get("values", {}).get("sum") == 3)
        c.check("run", "-1.5 + 0.25 = -1.25", len(r) > 1 and r[1].get("values", {}).get("sum") == -1.25)
        c.check("run", "an unknown control fails that set and keeps its whole answer",
                len(r) > 2 and r[2].get("ok") is False and (r[2].get("answer") or {}).get("ok") is False)
        c.check("run", "batch ok false, runsFailed 1", runs.get("ok") is False and runs.get("runsFailed") == 1)
        both = server.call("lvai_run_vi_and_read_values", viPath=add, inputsJson='{"x":"1"}', inputSetsJson='[{"x":"1"}]')
        c.check("run", "inputsJson and inputSetsJson together refused", both.get("errorKind") == "badArguments")

        # ---- 3. the DQMH module and four events in one job
        print("\n== 3. lvai_dqmh_new_event eventsJson")
        module = server.call_until_done("lvai_dqmh_new_module", moduleName="Bank", includeDoSomething=False)
        if not c.check("dqmh", "module Bank scripted (%d calls)" % module.get("_calls", 0), module.get("ok"), short(module)):
            return 1
        folder = module.get("folder") or os.path.join(root, "Libraries", "Bank")
        bad = server.call("lvai_dqmh_new_event", moduleName="Bank", eventsJson=json.dumps([
            {"eventName": "Set Rate", "arguments": [{"name": "Rate", "type": "double"}]},
            {"eventName": "Bad One", "replyArguments": [{"name": "X", "type": "double"}]}]))
        c.check("dqmh", "a reply on a plain Request refused before scripting",
                bad.get("errorKind") == "badArguments" and not os.path.exists(os.path.join(folder, "Set Rate.vi")), short(bad))
        events = [
            {"eventName": "Set Rate", "arguments": [{"name": "Rate", "type": "double"}]},
            {"eventName": "Get Balance", "eventType": "Request and Wait for Reply",
             "arguments": [{"name": "Account", "type": "string"}], "replyArguments": [{"name": "Balance", "type": "double"}]},
            {"eventName": "Reset Counter", "arguments": []},
            {"eventName": "Ping", "arguments": []},
        ]
        made = server.call_until_done("lvai_dqmh_new_event", moduleName="Bank", eventsJson=json.dumps(events))
        c.check("dqmh", "four events in one job (%d calls)" % made.get("_calls", 0),
                made.get("ok") and made.get("eventsDone") == 4, short(made))
        reply = next((e for e in made.get("events", []) if e.get("event") == "Get Balance"), {})
        c.check("dqmh", "the reply frame's completionNeeded is kept", bool(reply.get("completionNeeded")))
        c.check("dqmh", "every event VI on disk",
                all(os.path.exists(os.path.join(folder, e["eventName"] + ".vi")) for e in events))

        # ---- 4. handlers into the frames
        print("\n== 4. lvai_dqmh_place_handler handlersJson")
        three = [{"eventName": "Set Rate", "handlerViPath": os.path.join(handlers, "Set Rate Core.vi")},
                 {"eventName": "Get Balance", "handlerViPath": os.path.join(handlers, "Get Balance Core.vi")},
                 {"eventName": "Reset Counter", "handlerViPath": os.path.join(handlers, "Error Only Core.vi")}]
        dry = server.call("lvai_dqmh_place_handler", moduleName="Bank", dryRun=True, handlersJson=json.dumps(three))
        c.check("handlers", "dry run plans all three", dry.get("ok") and dry.get("handlersDone") == 3, short(dry))
        replies = next((h.get("plan", {}).get("replies") for h in dry.get("handlers", []) if h.get("event") == "Get Balance"), [])
        c.check("handlers", "the reply is wired by name (Balance->Balance)", "Balance->Balance" in (replies or []))
        placed = server.call("lvai_dqmh_place_handler", moduleName="Bank", handlersJson=json.dumps(three))
        c.check("handlers", "three handlers placed, %.0f s" % placed["_seconds"],
                placed.get("ok") and placed.get("handlersDone") == 3, short(placed))
        c.check("handlers", "Main.vi executable afterwards", placed.get("mainViExecState") == 1)
        c.check("handlers", "one layout for the batch", (placed.get("cleanUpDiagram") or {}).get("ok") is True)
        c.check("handlers", "every handler inside its frame",
                all(str(h.get("placement", "")).startswith("inside") for h in placed.get("handlers", [])))

        occupied = server.call("lvai_dqmh_place_handler", moduleName="Bank", handlersJson=json.dumps(three[:1]))
        c.check("handlers", "an occupied first frame stops at nextHandler 0",
                occupied.get("ok") is False and occupied.get("nextHandler") == 0
                and occupied["handlers"][0].get("answer", {}).get("errorKind") == "frameAlreadyHasCode", short(occupied))
        c.check("handlers", "...and leaves Main.vi alone", occupied.get("mainViExecState") is None
                and "was not changed" in occupied.get("note", ""))

        mixed = server.call("lvai_dqmh_place_handler", moduleName="Bank", handlersJson=json.dumps(
            [{"eventName": "Ping", "handlerViPath": os.path.join(handlers, "Error Only Core.vi")}] + three[:1]))
        c.check("handlers", "a free frame then an occupied one: 1 placed, nextHandler 1",
                mixed.get("handlersDone") == 1 and mixed.get("nextHandler") == 1, short(mixed))
        c.check("handlers", "...laid out once and Main.vi executable",
                (mixed.get("cleanUpDiagram") or {}).get("ok") is True and mixed.get("mainViExecState") == 1)

        bad_key = server.call("lvai_dqmh_place_handler", moduleName="Bank",
                              handlersJson='[{"eventName":"Ping","handler":"x"}]')
        c.check("handlers", "an unknown key refused by name", bad_key.get("errorKind") == "badArguments"
                and "handler" in bad_key.get("error", ""))

        # ---- 5. a DQMH unit test, filled and run (Get Balance answers a fixed 42)
        print("\n== 5. lvai_dqmh_new_unit_test argumentsJson / expectedJson / runTest")
        tests = os.path.join(root, "Unit Tests", "Bank")
        green = server.call("lvai_dqmh_new_unit_test", moduleName="Bank", eventName="Get Balance",
                            argumentsJson=json.dumps({"Account": "A"}), expectedJson=json.dumps({"Balance": "42"}),
                            runTest=True)
        c.check("unittest", "filled test executable and PASSED, %.0f s" % green["_seconds"],
                green.get("ok") and green.get("pass") is True
                and (green.get("fill") or {}).get("execState") == 1, short(green))
        red = server.call("lvai_dqmh_new_unit_test", moduleName="Bank", eventName="Get Balance",
                          argumentsJson=json.dumps({"Account": "A"}), expectedJson=json.dumps({"Balance": "41"}),
                          runTest=True)
        c.check("unittest", "a wrong expectation runs and answers pass FALSE",
                red.get("ok") and red.get("pass") is False, short(red))
        before = sorted(os.listdir(tests)) if os.path.isdir(tests) else []
        misspelt = server.call("lvai_dqmh_new_unit_test", moduleName="Bank", eventName="Get Balance",
                               expectedJson=json.dumps({"Saldo": "42"}))
        c.check("unittest", "a misspelt reply field refused before scripting, no file written",
                misspelt.get("errorKind") == "badArguments" and "Saldo" in misspelt.get("error", "")
                and before == sorted(os.listdir(tests)), short(misspelt))
        plain = server.call("lvai_dqmh_new_unit_test", moduleName="Bank", eventName="Set Rate",
                            argumentsJson=json.dumps({"Rate": "1"}), expectedJson=json.dumps({"Found": "true"}))
        c.check("unittest", "a plain Request (no reply) refused for filling",
                plain.get("errorKind") == "badArguments" and "plain Request" in plain.get("error", ""), short(plain))
        # ---- 6. a library over a folder: slim answer, the emptied folder swept, a user's kept
        print("\n== 6. lvai_create_library over a project folder")
        closed = server.call("lvai_close_active_project", projectPath=project)
        c.check("library", "test project closed before the library project", closed.get("closed"))
        lib_root = os.path.join(root, "LibraryTest")
        os.makedirs(os.path.join(lib_root, "Parts"))
        parts = [os.path.join(lib_root, "Parts", n) for n in ("Add Numbers.vi", "Error Only Core.vi")]
        shutil.copy(os.path.join(root, "SubVIs", "Add Numbers.vi"), parts[0])
        shutil.copy(os.path.join(handlers, "Error Only Core.vi"), parts[1])
        lib_project = os.path.join(lib_root, "LibraryTest.lvproj")
        write(lib_project, PROJECT.replace(
            '\t\t<Item Name="Dependencies" Type="Dependencies"/>',
            '\t\t<Item Name="Parts" Type="Folder">\n'
            '\t\t\t<Item Name="Add Numbers.vi" Type="VI" URL="../Parts/Add Numbers.vi"/>\n'
            '\t\t\t<Item Name="Error Only Core.vi" Type="VI" URL="../Parts/Error Only Core.vi"/>\n'
            '\t\t</Item>\n'
            '\t\t<Item Name="Keep Me" Type="Folder"/>\n'
            '\t\t<Item Name="Dependencies" Type="Dependencies"/>'))
        lib = server.call("lvai_create_library", libraryPath=os.path.join(lib_root, "Parts Lib", "Parts.lvlib"),
                          projectPath=lib_project, itemsJson=json.dumps([{"path": p} for p in parts]))
        size = len(json.dumps(lib))
        c.check("library", "library created with both members", lib.get("ok")
                and sorted((lib.get("verify") or {}).get("members", [])) == ["Add Numbers.vi", "Error Only Core.vi"],
                short(lib))
        c.check("library", "the answer is slim (%d chars)" % size, size < 6000)
        saved = open(lib_project, encoding="utf-8-sig").read()
        c.check("library", "the emptied folder is gone, the user's empty folder stays",
                '"Parts"' not in saved and '<Item Name="Keep Me" Type="Folder"/>' in saved, saved)
        project = lib_project   # already closed by the library call; the cleanup below closes nothing
        return 1 if c.failed else 0
    finally:
        print("\n== cleanup")
        closed = server.call("lvai_close_active_project", projectPath=project)
        print("   project closed: %s" % closed.get("closed"))
        server.close()
        print("\n%d checks, %d failed, %.0f s" % (len(c.rows), len(c.failed), time.monotonic() - started))
        for step, what, _, detail in c.failed:
            print("   FAILED [%s] %s %s" % (step, what, detail))


if __name__ == "__main__":
    code = main()
    sys.exit(code if code else 0)
