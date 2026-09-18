import json
import os
import re

PROJ = ROOT + r"\MixedRig.lvproj"
CLASS = ROOT + r"\Sensor.lvclass"


def ctl(name, xml, strict=True):
    """AIXML writes a Standard VI; the flag patch is what makes it a typedef."""
    path = os.path.join(ROOT, name)
    call(f"generate {name}", "lvai_generate_vi",
         {"aiXmlFilePath": os.path.join(AIXML, xml), "viPath": path, "measurePane": False})
    bundle = os.path.join(ROOT, "_b_" + name.replace(".ctl", ""))
    ex = call(f"extract {name}", "pylv_extract",
              {"viPath": path, "outDirectory": bundle, "annotate": False})
    local(f"flag patch {name}", lambda: patch_flags(ex["mainXml"], strict))
    call(f"rebuild {name}", "pylv_rebuild", {"mainXmlPath": ex["mainXml"], "viPath": path})
    return path


def bind(label, target, index_path, child, source):
    args = {"vi path": target, "child index": str(child), "source ctl path": source}
    if index_path:
        args["index path"] = index_path
    a = call(label, "lvai_run_vi_and_read_values",
             {"viPath": BIND, "inputsJson": json.dumps(args)})
    code = a.get("values", {}).get("code", {}).get("value")
    if code not in (None, "0"):
        raise SystemExit(f"{label}: bind answered code {code}")
    return a


def labels(vi, index_path=""):
    args = {"vi path": vi}
    if index_path:
        args["index path"] = index_path
    a = call(f"list [{index_path or 'root'}] {os.path.basename(vi)}",
             "lvai_run_vi_and_read_values", {"viPath": TREE, "inputsJson": json.dumps(args)})
    return re.findall(r"<Val>(.*?)</Val>", a["values"]["labels"]["xml"], re.S)


def write(path, text):
    open(path, "w", encoding="utf-8", newline="").write(text)


print("PHASE 1 - the typedefs")
mode = ctl("Mode.ctl", "mode.xml", strict=True)
limits = ctl("Limits.ctl", "limits.xml", strict=True)
reading = ctl("Reading.ctl", "reading.xml", strict=False)
bind("bind Mode.ctl into Limits.ctl", limits, "0", 0, mode)

print("PHASE 2 - the plain VI whose pane mixes three variants")
acquire = os.path.join(ROOT, "Acquire.vi")
call("generate Acquire.vi", "lvai_generate_vi",
     {"aiXmlFilePath": os.path.join(AIXML, "acquire.xml"), "viPath": acquire})
names = labels(acquire)
idx = {n: i for i, n in enumerate(names)}
bind("bind Limits.ctl onto Limits", acquire, "", idx["Limits"], limits)
bind("bind Mode.ctl into the Samples ARRAY", acquire, str(idx["Samples"]), 0, mode)
bind("bind Reading.ctl onto Reading", acquire, "", idx["Reading"], reading)

print("PHASE 3 - the class, its typedef field and its accessors")
call("create Sensor.lvclass", "lvai_create_class",
     {"className": "Sensor", "directory": ROOT, "projectPath": PROJ,
      "fields": "string.Name,uint16.Mode"})
call("bind Mode.ctl onto the class field", "lvai_bind_class_fields",
     {"lvclassPath": CLASS, "projectPath": PROJ,
      "bindingsJson": json.dumps([{"field": "Mode", "ctlPath": mode}])})
acc = call("create accessors", "lvai_create_accessors",
           {"lvclassPath": CLASS, "projectPath": PROJ, "accessUi": "Read"})
created = [v.get("path") for v in acc.get("created", []) if isinstance(v, dict)]
print("     accessors:", [os.path.basename(p) for p in created if p])
mode_acc = next((p for p in created if p and "Mode" in os.path.basename(p)), None)

print("PHASE 4 - the measurement: the same pane, three ways")
a = call("A  placeholder, flatten OFF", "lvai_placeholder_subvi",
         {"viPath": acquire, "refresh": True, "flattenTypedefs": False})
b = call("B  placeholder, flatten FLAG", "lvai_placeholder_subvi",
         {"viPath": acquire, "refresh": True, "typedefRoute": "flag"})
call("   open the project for the disconnect route", "lvai_open_file",
     {"projectPath": PROJ, "projectName": os.path.basename(PROJ)})
c = call("C  placeholder, flatten DISCONNECT", "lvai_placeholder_subvi",
         {"viPath": acquire, "refresh": True, "typedefRoute": "disconnect"})
print("     A typedefObjectsInStub:", a.get("typedefFlatten", {}).get("ran", "n/a"),
      "| B:", b.get("typedefFlatten", {}).get("typedefObjectsInStub"),
      "sites", b.get("typedefFlatten", {}).get("typedefSites"),
      "| C:", c.get("typedefFlatten", {}).get("typedefObjectsInStub"),
      "sites", c.get("typedefFlatten", {}).get("typedefSites"))

d = None
if mode_acc:
    d = call("D  placeholder on the CLASS accessor", "lvai_placeholder_subvi",
             {"viPath": mode_acc, "refresh": True, "typedefRoute": "flag"})
    print("     D classTerminals:", d.get("classTerminals"),
          "typedefSites:", d.get("typedefFlatten", {}).get("typedefSites"),
          "objectsInStub:", d.get("typedefFlatten", {}).get("typedefObjectsInStub"))

print("PHASE 5 - end to end: a caller, swapped onto the real code")
if d:
    stub = d["placeholder"]
    write(os.path.join(AIXML, "acc-caller.xml"), f"""<?xml version="1.0" encoding="UTF-8"?>
<VI _name="Acc Caller.vi" description="Calls the CLASS accessor's stub. lvai_swap_subvis then repoints it at the real member - the only route that resolves a class member and re-types the path stand-ins.">
  <Control _name="Sensor" conIdx="0" connection="recommended" description="The path stand-in the stub declares in place of the class wire." outputs="value:4200.value" type="path" uid="4200" uid_parent="root" value=""/>
  <Control _name="error in" conIdx="11" connection="recommended" description="Standard error chain in." outputs="value:4202.value" type="cluster{{bool.status,int32.code,string.source}}" uid="4202" uid_parent="root" value="[false,0,]"/>
  <Call target="{stub}" inputs="Sensor in:4200.value,error in (no error):4202.value" outputs="error out:4210.err" uid="4210" uid_parent="root"/>
  <Indicator _name="error out" conIdx="15" connection="recommended" description="Standard error chain out." inputs="value:4210.err" type="cluster{{bool.status,int32.code,string.source}}" uid="4300" uid_parent="root" value="[false,0,]"/>
</VI>
""")
    acc_caller = os.path.join(ROOT, "Acc Caller.vi")
    gen = call("generate Acc Caller.vi", "lvai_generate_vi",
               {"aiXmlFilePath": os.path.join(AIXML, "acc-caller.xml"), "viPath": acc_caller})
    if gen.get("ok"):
        sw = call("swap the stub for the class member", "lvai_swap_subvis",
                  {"viPath": acc_caller,
                   "swapsJson": json.dumps([{"socket": stub,
                                             "target": os.path.basename(mode_acc),
                                             "path": mode_acc}])})
        print("     socketsLeft:", sw.get("socketsLeft"), "targets:", sw.get("callTargets"))
        call("exec state of Acc Caller.vi", "lvai_exec_state", {"viPath": acc_caller})

stub_b = c["placeholder"]
write(os.path.join(AIXML, "acq-caller.xml"), f"""<?xml version="1.0" encoding="UTF-8"?>
<VI _name="Acq Caller.vi" description="Calls Acquire.vi's stub. Acquire.vi is a LOOSE VI\\2C so a pylabview link retarget reaches it - measured. A class member needs lvai_swap_subvis instead.">
  <Control _name="error in" conIdx="11" connection="recommended" description="Standard error chain in." outputs="value:4202.value" type="cluster{{bool.status,int32.code,string.source}}" uid="4202" uid_parent="root" value="[false,0,]"/>
  <Call target="{stub_b}" inputs="error in:4202.value" outputs="error out:4210.err" uid="4210" uid_parent="root"/>
  <Indicator _name="error out" conIdx="15" connection="recommended" description="Standard error chain out." inputs="value:4210.err" type="cluster{{bool.status,int32.code,string.source}}" uid="4300" uid_parent="root" value="[false,0,]"/>
</VI>
""")
acq_caller = os.path.join(ROOT, "Acq Caller.vi")
gen = call("generate Acq Caller.vi", "lvai_generate_vi",
           {"aiXmlFilePath": os.path.join(AIXML, "acq-caller.xml"), "viPath": acq_caller})
if gen.get("ok"):
    ap = call("retarget the stub onto Acquire.vi", "pylv_apply",
              {"viPath": acq_caller, "gateOnExecState": False,
               "operationsJson": json.dumps([{"op": "retarget", "from": stub_b,
                                              "to": "Acquire.vi", "path": acquire}])})
    v = next((s for s in ap.get("steps", []) if s.get("step") == "verify"), {})
    print("     callTargets:", v.get("callTargets"), "execState:", v.get("execState"),
          "coercionDots:", v.get("coercionDots"))
