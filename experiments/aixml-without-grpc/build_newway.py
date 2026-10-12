"""Build the NewWay demo project - an interface, a class implementing it, Caraya unit tests - on
ONE backend, timing every step through the built server.

    build_newway.py <root> <backend> [--dead-grpc] [--negative-control]

--dead-grpc points LABVIEW_GRPC_PORT at a port nothing listens on, so any call that still
reaches NI's service fails instead of quietly succeeding."""
import json
import os
import shutil
import subprocess
import sys
import time

sys.path.insert(0, r"C:\Projects\LabVIEWMCP\experiments\aixml-without-grpc")
# the helper parts this build needs ship in scripts/aixml-pylabview/parts since 2026-10-12;
# until then they came from a harvested copy in %TEMP% (LabVIEWMCP\parts-phase3c)
import helper_parity as hp  # noqa: E402

root, backend = sys.argv[1], sys.argv[2]
dead = "--dead-grpc" in sys.argv
if dead:
    os.environ["LABVIEW_GRPC_PORT"] = "1"   # nothing listens on port 1
name = os.path.basename(root)
proj = os.path.join(root, name + ".lvproj")
src = os.path.join(root, "src")
shutil.rmtree(root, ignore_errors=True)
os.makedirs(src)

ERR = "cluster{bool.status,int32.code,string.source}"


def method_aixml(vi, desc, cls_in, cls_out, cls_type, cls_value, output, body, out_net, comment):
    return f"""<VI _name="{vi}" description="{desc}">
  <Control _name="{cls_in}" type="{cls_type}" uid="4200" uid_parent="root" value="{cls_value}" conIdx="11" connection="required" outputs="value:4200.value"/>
  <Control _name="error in" type="{ERR}" uid="4210" uid_parent="root" value="[false,0,]" conIdx="8" connection="recommended" outputs="value:4210.value"/>
{body}  <Indicator _name="{cls_out}" type="{cls_type}" uid="4220" uid_parent="root" value="{cls_value}" conIdx="3" connection="recommended" inputs="value:4200.value"/>
  <Indicator _name="{output}" type="double" uid="4230" uid_parent="root" value="0" conIdx="2" connection="recommended" inputs="value:{out_net}"/>
  <Indicator _name="error out" type="{ERR}" uid="4240" uid_parent="root" value="[false,0,]" conIdx="0" connection="recommended" inputs="value:4210.value"/>
  <FreeLabel comment="{comment}" uid="4290" uid_parent="root"/>
</VI>
"""


RECT = "cluster{double.Width,double.Height}"
UNBUNDLE = ('  <Node _name="Unbundle By Name" fields="Width,Height" inputs="input cluster:4200.value" '
            'outputs="Width:4250.Width,Height:4250.Height" uid="4250" uid_parent="root"/>\n')
files = {
    "shape_area.xml": method_aixml(
        "Area.vi", "The area of the shape. Every implementing class overrides it.",
        "Shape in", "Shape out", "path", "", "Area", "", "", "Abstract: implementers override it"),
    "shape_perimeter.xml": method_aixml(
        "Perimeter.vi", "The perimeter of the shape. Every implementing class overrides it.",
        "Shape in", "Shape out", "path", "", "Perimeter", "", "", "Abstract: implementers override it"),
    "rect_area.xml": method_aixml(
        "Area.vi", "Area of the rectangle: Width times Height.",
        "Rectangle in", "Rectangle out", RECT, "[0,0]", "Area",
        UNBUNDLE + '  <Node _name="Multiply" inputs="x:4250.Width,y:4250.Height" outputs="x*y:4260.r" uid="4260" uid_parent="root"/>\n',
        "4260.r", "Area is Width times Height"),
    "rect_perimeter.xml": method_aixml(
        "Perimeter.vi", "Perimeter of the rectangle: two times (Width plus Height).",
        "Rectangle in", "Rectangle out", RECT, "[0,0]", "Perimeter",
        UNBUNDLE
        + '  <Node _name="Add" inputs="x:4250.Width,y:4250.Height" outputs="x+y:4260.s" uid="4260" uid_parent="root"/>\n'
        + '  <Constant _name="two" type="double" value="2" uid="4270" uid_parent="root" outputs="value:4270.value"/>\n'
        + '  <Node _name="Multiply" inputs="x:4260.s,y:4270.value" outputs="x*y:4280.r" uid="4280" uid_parent="root"/>\n',
        "4280.r", "Perimeter is 2 x (Width + Height)"),
}
for f, text in files.items():
    open(os.path.join(src, f), "w", encoding="utf-8").write(text)

iface_dir, rect_dir = os.path.join(root, "Shape"), os.path.join(root, "Rectangle")
iface, rect = os.path.join(iface_dir, "Shape.lvclass"), os.path.join(rect_dir, "Rectangle.lvclass")
tests = os.path.join(root, "Tests", "Rectangle")
acc_test = os.path.join(tests, "Test Rectangle Accessors.vi")
meth_test = os.path.join(tests, "Test Rectangle Methods.vi")
runner = os.path.join(root, "Tests", "Run All Tests.vi")


def method(cls_dir, aixml, vi, cls_in, cls_out):
    return {"aixml": os.path.join(src, aixml), "vi": os.path.join(cls_dir, vi),
            "classTerminals": [cls_in, cls_out], "dispatchTerminals": [cls_in, cls_out]}


def method_cases(area_wrong=False):
    return json.dumps([
        {"method": "Area", "seed": {"Width": "3", "Height": "4"}, "expectOutput": "Area",
         "expectValue": "13" if area_wrong else "12", "label": "Area of 3 x 4"},
        {"method": "Area", "seed": {"Width": "2.5", "Height": "2"}, "expectOutput": "Area",
         "expectValue": "5", "label": "Area of 2.5 x 2"},
        {"method": "Perimeter", "seed": {"Width": "3", "Height": "4"}, "expectOutput": "Perimeter",
         "expectValue": "14", "label": "Perimeter of 3 x 4"},
    ])


B = {"backend": backend}
STEPS = [
    ("create interface Shape", "lvai_create_interface",
     {"interfaceName": "Shape", "directory": iface_dir, "projectPath": proj, **B}),
    ("interface methods Area, Perimeter", "lvai_add_class_method",
     {"lvclassPath": iface, "projectPath": proj, **B, "methodsJson": json.dumps([
         method(iface_dir, "shape_area.xml", "Area.vi", "Shape in", "Shape out"),
         method(iface_dir, "shape_perimeter.xml", "Perimeter.vi", "Shape in", "Shape out")])}),
    # CLAUDE.md: close the project before any lvai_create_class - it writes the .lvproj as a
    # file, and the next close would save LabVIEW's older copy over that entry
    ("close project", "lvai_close_active_project", {"projectPath": proj, **B}),
    ("create class Rectangle", "lvai_create_class",
     {"className": "Rectangle", "directory": rect_dir, "fields": "double.Width,double.Height",
      "projectPath": proj, "parentInterfaces": iface, **B}),
    ("overrides Area, Perimeter", "lvai_add_class_method",
     {"lvclassPath": rect, "projectPath": proj, **B, "methodsJson": json.dumps([
         method(rect_dir, "rect_area.xml", "Area.vi", "Rectangle in", "Rectangle out"),
         method(rect_dir, "rect_perimeter.xml", "Perimeter.vi", "Rectangle in", "Rectangle out")])}),
    ("accessors", "lvai_create_accessors", {"lvclassPath": rect, "projectPath": proj, **B}),
    ("accessor test", "lvai_generate_class_test",
     {"lvclassPath": rect, "testViPath": acc_test, "projectPath": proj, **B,
      "casesJson": json.dumps([{"field": "Width", "value": "3.5"}, {"field": "Height", "value": "4"}])}),
    ("method test", "lvai_generate_method_test",
     {"lvclassPath": rect, "testViPath": meth_test, "projectPath": proj, **B,
      "casesJson": method_cases()}),
    ("Caraya runner", "lvai_generate_caraya_test_runner",
     {"testViPaths": acc_test + "\n" + meth_test, "runnerViPath": runner, "projectPath": proj, **B}),
    ("run the suite", "lvai_run_caraya_tests", {"runnerViPath": runner, **B}),
]

s = hp.Server()
log = {"root": root, "backend": backend, "deadGrpc": dead, "steps": []}
try:
    if dead:
        r = s.tool("lvai_status", {})
        log["controlStatus"] = r
        print("CONTROL lvai_status with the dead port:",
              json.dumps({k: r.get(k) for k in ("ok", "reachable", "grpcReachable", "error", "errorKind",
                                                  "port", "lvaiPort")})[:400])
    t0 = time.time()
    for label, tool, args in STEPS:
        t = time.time()
        r = s.tool(tool, args)
        dt = time.time() - t
        ok = r.get("ok")
        if tool == "lvai_close_active_project":
            ok = bool(r.get("closed") or r.get("nothingToClose"))
        log["steps"].append({"step": label, "tool": tool, "seconds": round(dt, 1), "ok": ok,
                             "route": r.get("route"), "answer": r})
        print("%-34s %-34s ok=%-5s %6.1f s" % (label, tool, ok, dt))
        if ok is not True:
            print(json.dumps(r, indent=1)[:3000])
            break
    log["totalSeconds"] = round(time.time() - t0, 1)
    print("TOTAL %.1f s" % log["totalSeconds"])
    last = log["steps"][-1]["answer"]
    print("suite:", {k: last.get(k) for k in ("tests", "failures", "errors", "note")})

    # every VI in the project, executable?
    states = {}
    for d, _, fs in os.walk(root):
        for f in fs:
            if f.endswith(".vi"):
                p = os.path.join(d, f)
                r = s.tool("lvai_exec_state", {"viPath": p, "backend": "pylabview"})
                states[os.path.relpath(p, root)] = r.get("execState")
    log["execStates"] = states
    print("exec states:", states)

    if "--negative-control" in sys.argv:
        # break one expectation (Area of 3 x 4 = 13), run, expect exactly one failure, then restore
        for wrong, tag in ((True, "negative control"), (False, "restored")):
            t = time.time()
            r = s.tool("lvai_generate_method_test", {"lvclassPath": rect, "testViPath": meth_test,
                                                    "projectPath": proj, **B,
                                                    "casesJson": method_cases(area_wrong=wrong)})
            g = r.get("ok")
            r = s.tool("lvai_run_caraya_tests", {"runnerViPath": runner, **B})
            print("%-17s generate ok=%s  run: tests=%s failures=%s failing=%s  %.1f s" % (
                tag, g, r.get("tests"), r.get("failures"),
                [f.get("case") or f.get("name") for f in (r.get("failing") or [])], time.time() - t))
            log[tag] = {"generateOk": g, "run": r}
finally:
    s.p.terminate()
    json.dump(log, open(os.path.join(root, "build-log.json"), "w"), indent=1)
