#!/usr/bin/env python3
"""Generate the Slice 1.1 fixture AIXML: seven target VIs (Target, Callee, Callee2, Caller, Spin, Spin2, BrokenWire) and twelve probe VIs (P0 to P11).

    python3 make-fixtures.py            # writes fixtures/aixml/*.xml
    compose-all.sh composes them with compose.py into fixtures/vi/*.vi

Each probe VI takes `Inputs` (array of string: vi path, arg), calls ONE VI Server or VI Scripting
method, and answers one string `code=<n>;source=<text>;detail=<evidence>` (the error cluster after the method, and
evidence the method did its job). The method is the only thing that differs between probes, so a
failing probe names a failing method.
"""
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "aixml")
ERR = "cluster{bool.status,int32.code,string.source}"


def write(name, body):
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, name + ".xml"), "w", newline="\n") as f:
        f.write(body)


# ---- targets -------------------------------------------------------------------------------------
def add_vi(name, k, comment):
    write(name, f'''<VI _name="{name}.vi" description="Probe fixture\\3A y is x plus {k}.">
  <FreeLabel comment="{comment}" uid="4200" uid_parent="root"/>
  <Control _name="x" conIdx="0" connection="required" outputs="value:4210.value" type="double" uid="4210" uid_parent="root" value="0"/>
  <Constant _name="k" outputs="value:4211.value" type="double" uid="4211" uid_parent="root" value="{k}"/>
  <Node _name="Add" inputs="x:4210.value,y:4211.value" outputs="x+y:4212.x+y" uid="4212" uid_parent="root"/>
  <Indicator _name="y" conIdx="1" connection="recommended" inputs="value:4212.x+y" type="double" uid="4213" uid_parent="root" value="0"/>
</VI>
''')


add_vi("Target", 1, "y is x plus one")
add_vi("Callee", 1, "Callee adds one")
add_vi("Callee2", 2, "Callee2 adds two")

write("Caller", '''<VI _name="Caller.vi" description="Probe fixture\\3A calls Callee.vi once.">
  <FreeLabel comment="One call to Callee" uid="4200" uid_parent="root"/>
  <Control _name="x" conIdx="0" connection="required" outputs="value:4210.value" type="double" uid="4210" uid_parent="root" value="0"/>
  <Call target="Callee.vi" inputs="x:4210.value" outputs="y:4220.y" uid="4220" uid_parent="root"/>
  <Indicator _name="y" conIdx="1" connection="recommended" inputs="value:4220.y" type="double" uid="4221" uid_parent="root" value="0"/>
</VI>
''')

write("Spin", '''<VI _name="Spin.vi" description="Probe fixture\\3A loops until aborted. The stop condition is a constant FALSE.">
  <FreeLabel comment="Never stops by itself" uid="4200" uid_parent="root"/>
  <Constant _name="never" outputs="value:4210.value" type="bool" uid="4210" uid_parent="root" value="false"/>
  <Constant _name="period ms" outputs="value:4211.value" type="int32" uid="4211" uid_parent="root" value="100"/>
  <Structure _name="While Loop" count="" label="Wait 100 ms, never stop" uid="4220" uid_parent="root">
    <Tunnel _id="In1" inputs="value:4211.value" outputs="value:4221.value" uid="4221" uid_parent="4220"/>
    <Tunnel _id="In2" inputs="value:4210.value" outputs="value:4222.value" uid="4222" uid_parent="4220"/>
    <Node _name="Wait (ms)" inputs="milliseconds to wait:4221.value" outputs="millisecond timer value:" uid="4223" uid_parent="4220"/>
    <Condition inputs="value:4222.value" uid="4224" uid_parent="4220" value="stop"/>
  </Structure>
</VI>
''')

# A second VI that never ends, for the probe that leaves it running (a VI of this name cannot be started twice).
write("Spin2", open(os.path.join(OUT, "Spin.xml")).read().replace("Spin.vi", "Spin2.vi"))

# One wire only, and it cannot be valid: a string constant into the x input of Add. LabVIEW loads it as a
# broken wire (the probe reads Is Broken? before and the wire count after Remove Bad Wires).
write("BrokenWire", '''<VI _name="BrokenWire.vi" description="Probe fixture\\3A one broken wire, a string constant wired to the x input of Add.">
  <FreeLabel comment="One broken wire" uid="4200" uid_parent="root"/>
  <Constant _name="text" outputs="value:4210.value" type="string" uid="4210" uid_parent="root" value="abc"/>
  <Node _name="Add" inputs="x:4210.value" outputs="x+y:" uid="4212" uid_parent="root"/>
</VI>
''')

# ---- probes --------------------------------------------------------------------------------------
# LabVIEWCLI RunVI opens the VI with a strict VI refnum: the connector pane must be NI's
# (Inputs = array of string on terminal 11; Output/Failure = string on 2; CLI Process Return Code =
# int32 on 3). Measured: any other pane is error 1031 "VI Reference type does not match VI
# connector pane". So a probe carries no `error in`/`error out` (the repo rule is for shipped VIs).
HEAD = '''<VI _name="{name}.vi" description="Slice 1.1 probe\\3A {what}">
  <FreeLabel comment="{comment}" uid="4200" uid_parent="root"/>
  <Control _name="Inputs" conIdx="11" connection="recommended" outputs="value:4209.value" type="array{{string}}" uid="4209" uid_parent="root" value="[]"/>
  <Constant _name="index 0" outputs="value:4205.value" type="int32" uid="4205" uid_parent="root" value="0"/>
  <Constant _name="index 1" outputs="value:4206.value" type="int32" uid="4206" uid_parent="root" value="1"/>
  <Node _name="Index Array" inputs="array:4209.value,index:4205.value" outputs="element:4210.value" uid="4210" uid_parent="root"/>
  <Node _name="Index Array" inputs="array:4209.value,index:4206.value" outputs="element:4211.value" uid="4211" uid_parent="root"/>
  <Node _name="String To Path" inputs="string:4210.value" outputs="path:4212.path" uid="4212" uid_parent="root"/>
  <Node _name="String To Path" inputs="string:4211.value" outputs="path:4213.path" uid="4213" uid_parent="root"/>
'''
TAIL = '''  <Node _name="Unbundle By Name" fields="code,source" inputs="input cluster:{err}" outputs="code:4290.code,source:4290.source" uid="4290" uid_parent="root"/>
  <Node _name="Number To Decimal String" inputs="number:4290.code" outputs="decimal integer string:4294.text" uid="4294" uid_parent="root"/>
  <Constant _name="code label" outputs="value:4295.value" type="string" uid="4295" uid_parent="root" value="code="/>
  <Constant _name="source label" outputs="value:4296.value" type="string" uid="4296" uid_parent="root" value=";source="/>
  <Constant _name="detail label" outputs="value:4297.value" type="string" uid="4297" uid_parent="root" value=";detail="/>
  <Node _name="Concatenate Strings" inputs="string:4295.value,string:4294.text,string:4296.value,string:4290.source,string:4297.value,string:{detail}" outputs="concatenated string:4298.line" uid="4298" uid_parent="root"/>
  <Indicator _name="Output/Failure" conIdx="2" connection="recommended" inputs="value:4298.line" type="string" uid="4291" uid_parent="root" value=""/>
  <Indicator _name="CLI Process Return Code" conIdx="3" connection="recommended" inputs="value:4290.code" type="int32" uid="4292" uid_parent="root" value="0"/>
</VI>
<?lvmcp pane="4815"?>
'''
OPEN = '''  <Node _name="Open VI Reference" inputs="vi path:4212.path" outputs="vi reference:4220.ref,error out:4220.err" uid="4220" uid_parent="root"/>
'''
NOSTR = '''  <Constant _name="empty" outputs="value:4214.value" type="string" uid="4214" uid_parent="root" value=""/>
'''


def probe(name, what, comment, body, err, detail):
    write(name, HEAD.format(name=name, what=what, comment=comment) + NOSTR + body + TAIL.format(err=err, detail=detail))


def state_nodes(ref, err_in, uid, tag):
    u = uid
    xml = f'''  <Node _name="Property Node" fields="read+Execution\\3AState" type="{{LV.VI}}" inputs="reference:{ref},error in (no error):{err_in}" outputs="Execution\\3AState:{u}.state,reference out:,error out:{u}.err" uid="{u}" uid_parent="root"/>
  <Node _name="Number To Decimal String" inputs="number:{u}.state" outputs="decimal integer string:{u+1}.text" uid="{u+1}" uid_parent="root"/>
'''
    return xml, f"{u}.err", f"{u+1}.text"


# 0 Application version: the LabVIEW the probes run in (unwired reference = this application)
write("P0_AppVersion", '''<VI _name="P0_AppVersion.vi" description="Slice 1.1 probe\\3A reads Application Version Number of the running LabVIEW.">
  <FreeLabel comment="Read the version of LabVIEW" uid="4200" uid_parent="root"/>
  <Control _name="Inputs" conIdx="11" connection="recommended" outputs="value:4209.value" type="array{string}" uid="4209" uid_parent="root" value="[]"/>
  <Node _name="Property Node" fields="read+Application\\3AVersion Number" type="{LV.Application}" inputs="reference:,error in (no error):" outputs="Application\\3AVersion Number:4220.ver,reference out:,error out:4220.err" uid="4220" uid_parent="root"/>
  <Node _name="Unbundle By Name" fields="code" inputs="input cluster:4220.err" outputs="code:4223.code" uid="4223" uid_parent="root"/>
  <Indicator _name="Output/Failure" conIdx="2" connection="recommended" inputs="value:4220.ver" type="string" uid="4224" uid_parent="root" value=""/>
  <Indicator _name="CLI Process Return Code" conIdx="3" connection="recommended" inputs="value:4223.code" type="int32" uid="4225" uid_parent="root" value="0"/>
</VI>
<?lvmcp pane="4815"?>
''')

# 1 Open VI Reference: open, close. detail = "opened".
probe("P1_OpenVIReference", "Open VI Reference, then Close Reference.", "Open, then close the reference",
      OPEN + '''  <Node _name="Close Reference" inputs="reference:4220.ref,error in (no error):4220.err" outputs="error out:4230.err" uid="4230" uid_parent="root"/>
''', "4230.err", "4214.value")

# 2 Execution State: read the state of Target (idle, 1) - detail is the number
x, e, t = state_nodes("4220.ref", "4220.err", 4230, "s")
probe("P2_ExecutionState", "Open VI Reference, read Execution State, Close Reference.", "Read Execution State",
      OPEN + x + f'''  <Node _name="Close Reference" inputs="reference:4220.ref,error in (no error):{e}" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", t)

# 3 Run VI async: Run VI with Wait until done FALSE on Spin, then read the state (running = 3 or 2)
x, e, t = state_nodes("4221.ref", "4221.err", 4231, "s")
probe("P3_RunVIAsync", "Open VI Reference, Run VI with Wait until done FALSE, read Execution State, close the reference.", "Run async, then read state",
      OPEN + '''  <Constant _name="do not wait" outputs="value:4215.value" type="bool" uid="4215" uid_parent="root" value="false"/>
  <Node _name="Invoke Node" target="Run VI" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err,Wait until done:4215.value" outputs="reference out:4221.ref,error out:4221.err" uid="4221" uid_parent="root"/>
''' + x + f'''  <Node _name="Close Reference" inputs="reference:4221.ref,error in (no error):{e}" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", t)

# 4 Abort VI: a VI that never ends is started, read (running), aborted, read again (idle). One probe,
# one reference: measured, a reference closed after Run VI async leaves the VI idle (state 1), and
# Abort VI on an idle VI is error 1000, so the abort has to follow the start inside one probe.
x1, e1, t1 = state_nodes("4221.ref", "4221.err", 4231, "before")
x2, e2, t2 = state_nodes("4222.ref", "4222.err", 4251, "after")
probe("P4_AbortVI", "Open VI Reference, Run VI async, read state, Abort VI, read state, close the reference.", "Start, abort, read state twice",
      OPEN + '''  <Constant _name="do not wait" outputs="value:4215.value" type="bool" uid="4215" uid_parent="root" value="false"/>
  <Node _name="Invoke Node" target="Run VI" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err,Wait until done:4215.value" outputs="reference out:4221.ref,error out:4221.err" uid="4221" uid_parent="root"/>
''' + x1 + f'''  <Node _name="Invoke Node" target="Abort VI" type="{{LV.VI}}" inputs="reference:4221.ref,error in (no error):{e1}" outputs="reference out:4222.ref,error out:4222.err" uid="4222" uid_parent="root"/>
''' + x2.replace("4222.ref", "4222.ref") + f'''  <Constant _name="comma" outputs="value:4217.value" type="string" uid="4217" uid_parent="root" value=","/>
  <Node _name="Concatenate Strings" inputs="string:{t1},string:4217.value,string:{t2}" outputs="concatenated string:4218.both" uid="4218" uid_parent="root"/>
  <Node _name="Close Reference" inputs="reference:4222.ref,error in (no error):{e2}" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", "4218.both")

# 9 Abort VI on a VI that is not running (control for row 4): Abort alone, then read the state.
x, e, t = state_nodes("4221.ref", "4221.err", 4231, "s")
probe("P9_AbortIdle", "Control: Open VI Reference, Abort VI on a VI that is not running, read state, close the reference.", "Abort without a running VI",
      OPEN + '''  <Node _name="Invoke Node" target="Abort VI" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err" outputs="reference out:4221.ref,error out:4221.err" uid="4221" uid_parent="root"/>
''' + x + f'''  <Node _name="Close Reference" inputs="reference:4221.ref,error in (no error):{e}" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", t)

# 5 Print VI To HTML: write `arg` as the html file path; Format default
probe("P5_PrintVIToHTML", "Open VI Reference, Print VI To HTML to the path in arg, close the reference.", "Print VI to an HTML file",
      OPEN + '''  <Node _name="Invoke Node" target="Print VI To HTML" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err,HTML File Path:4213.path" outputs="reference out:4221.ref,error out:4221.err" uid="4221" uid_parent="root"/>
  <Node _name="Close Reference" inputs="reference:4221.ref,error in (no error):4221.err" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", "4211.value")

# 6 SubVI Replace: Caller.vi's first SubVI node -> the VI at `arg`. Replace invalidates the node
# reference, so the evidence is read afresh: SubVIs[] again, element 0, VI Name (Callee2.vi expected).
probe("P6_SubVIReplace", "Open VI Reference, Block Diagram, SubVIs[], SubVI Replace with the VI in arg, then read VI Name of SubVIs[0] again.", "Replace the SubVI with arg",
      OPEN + '''  <Node _name="Property Node" fields="read+Block Diagram" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err" outputs="Block Diagram:4221.bd,reference out:,error out:4221.err" uid="4221" uid_parent="root"/>
  <Node _name="Property Node" fields="read+SubVIs[]" type="{LV.Diagram}" inputs="reference:4221.bd,error in (no error):4221.err" outputs="SubVIs[]:4222.subvis,reference out:4222.bd,error out:4222.err" uid="4222" uid_parent="root"/>
  <Constant _name="first" outputs="value:4216.value" type="int32" uid="4216" uid_parent="root" value="0"/>
  <Node _name="Index Array" inputs="array:4222.subvis,index:4216.value" outputs="element:4223.sub" uid="4223" uid_parent="root"/>
  <Node _name="Invoke Node" target="Replace" type="{LV.SubVI}" inputs="reference:4223.sub,error in (no error):4222.err,Style:,Path:4213.path,PaletteString:" outputs="reference out:,error out:4224.err,Replace:" uid="4224" uid_parent="root"/>
  <Node _name="Property Node" fields="read+SubVIs[]" type="{LV.Diagram}" inputs="reference:4222.bd,error in (no error):4224.err" outputs="SubVIs[]:4225.subvis,reference out:,error out:4225.err" uid="4225" uid_parent="root"/>
  <Node _name="Index Array" inputs="array:4225.subvis,index:4216.value" outputs="element:4226.sub" uid="4226" uid_parent="root"/>
  <Node _name="Property Node" fields="read+VI Name" type="{LV.SubVI}" inputs="reference:4226.sub,error in (no error):4225.err" outputs="VI Name:4227.name,reference out:,error out:4227.err" uid="4227" uid_parent="root"/>
  <Node _name="Close Reference" inputs="reference:4220.ref,error in (no error):4227.err" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", "4227.name")

# 7 Remove Bad Wires on BrokenWire.vi (arg 1): the wire count and the wire's Is Broken? before, the wire count
# after. detail = "<wires before>,<broken>,<wires after>"; pass is "1,TRUE,0".
SZ = lambda arr, uid: f'''  <Node _name="Array Size" inputs="array:{arr}" outputs="size(s):{uid}.n" uid="{uid}" uid_parent="root"/>
  <Node _name="Number To Decimal String" inputs="number:{uid}.n" outputs="decimal integer string:{uid+1}.text" uid="{uid+1}" uid_parent="root"/>
'''
probe("P7_RemoveBadWires", "Open VI Reference on a VI with one broken wire, read Wires[] and Is Broken?, BD.Remove Bad Wires, read Wires[] again, close the reference.", "Count wires before and after",
      OPEN + '''  <Node _name="Property Node" fields="read+Block Diagram" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err" outputs="Block Diagram:4221.bd,reference out:,error out:4221.err" uid="4221" uid_parent="root"/>
  <Node _name="Property Node" fields="read+Wires[]" type="{LV.TopLevelDiagram}" inputs="reference:4221.bd,error in (no error):4221.err" outputs="Wires[]:4222.wires,reference out:,error out:4222.err" uid="4222" uid_parent="root"/>
  <Node _name="Index Array" inputs="array:4222.wires,index:4205.value" outputs="element:4223.wire" uid="4223" uid_parent="root"/>
  <Node _name="Property Node" fields="read+Is Broken?" type="{LV.Wire}" inputs="reference:4223.wire,error in (no error):4222.err" outputs="Is Broken?:4224.broken,reference out:,error out:4224.err" uid="4224" uid_parent="root"/>
  <Constant _name="TRUE text" outputs="value:4225.value" type="string" uid="4225" uid_parent="root" value="TRUE"/>
  <Constant _name="FALSE text" outputs="value:4226.value" type="string" uid="4226" uid_parent="root" value="FALSE"/>
  <Node _name="Select" inputs="t:4225.value,s:4224.broken,f:4226.value" outputs="s? t\\3Af:4227.sel" uid="4227" uid_parent="root"/>
  <Node _name="Invoke Node" target="BD.Remove Bad Wires" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4224.err" outputs="reference out:4228.ref,error out:4228.err" uid="4228" uid_parent="root"/>
  <Node _name="Property Node" fields="read+Wires[]" type="{LV.TopLevelDiagram}" inputs="reference:4221.bd,error in (no error):4228.err" outputs="Wires[]:4229.wires,reference out:,error out:4229.err" uid="4229" uid_parent="root"/>
  <Constant _name="comma" outputs="value:4217.value" type="string" uid="4217" uid_parent="root" value=","/>
''' + SZ("4222.wires", 4230) + SZ("4229.wires", 4232) + '''  <Node _name="Concatenate Strings" inputs="string:4231.text,string:4217.value,string:4227.sel,string:4217.value,string:4233.text" outputs="concatenated string:4234.all" uid="4234" uid_parent="root"/>
  <Node _name="Close Reference" inputs="reference:4228.ref,error in (no error):4229.err" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", "4234.all")

# 8 Clean Up Diagram on Target.vi: Nodes[] element 0 is moved to Left = 2000 (read back), BD.CleanUp runs, the
# same object's Left is read again. detail = "<left after the move>,<left after the clean up>"; pass is "2000,<other>".
LEFT_W = lambda ref, err, uid: f'''  <Node _name="Property Node" fields="write+Position\\3ALeft" type="{{LV.GObject}}" inputs="reference:{ref},error in (no error):{err},Position\\3ALeft:4225.value" outputs="reference out:,error out:{uid}.err" uid="{uid}" uid_parent="root"/>
'''
LEFT_R = lambda ref, err, uid: f'''  <Node _name="Property Node" fields="read+Position\\3ALeft" type="{{LV.GObject}}" inputs="reference:{ref},error in (no error):{err}" outputs="Position\\3ALeft:{uid}.left,reference out:,error out:{uid}.err" uid="{uid}" uid_parent="root"/>
'''
NS = lambda src, uid: f'''  <Node _name="Number To Decimal String" inputs="number:{src}" outputs="decimal integer string:{uid}.text" uid="{uid}" uid_parent="root"/>
'''
NODE0 = lambda bd, err, uid: f'''  <Node _name="Property Node" fields="read+Nodes[]" type="{{LV.Diagram}}" inputs="reference:{bd},error in (no error):{err}" outputs="Nodes[]:{uid}.nodes,reference out:,error out:{uid}.err" uid="{uid}" uid_parent="root"/>
  <Node _name="Index Array" inputs="array:{uid}.nodes,index:4205.value" outputs="element:{uid+1}.node" uid="{uid+1}" uid_parent="root"/>
'''
probe("P8_CleanUpDiagram", "Open VI Reference, move the first node of the diagram to Left 2000, BD.CleanUp, read the node's Left again, close the reference.", "Move a node, clean up, read it",
      OPEN + '''  <Node _name="Property Node" fields="read+Block Diagram" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err" outputs="Block Diagram:4221.bd,reference out:,error out:4221.err" uid="4221" uid_parent="root"/>
  <Constant _name="far left" outputs="value:4225.value" type="int16" uid="4225" uid_parent="root" value="2000"/>
  <Constant _name="comma" outputs="value:4217.value" type="string" uid="4217" uid_parent="root" value=","/>
''' + NODE0("4221.bd", "4221.err", 4230)
      + LEFT_W("4231.node", "4230.err", 4233)
      + LEFT_R("4231.node", "4233.err", 4234)
      + NS("4234.left", 4236)
      + '''  <Node _name="Invoke Node" target="BD.CleanUp" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4234.err" outputs="reference out:4250.ref,error out:4250.err" uid="4250" uid_parent="root"/>
''' + NODE0("4221.bd", "4250.err", 4260)
      + LEFT_R("4261.node", "4260.err", 4263)
      + NS("4263.left", 4265)
      + '''  <Node _name="Concatenate Strings" inputs="string:4236.text,string:4217.value,string:4265.text" outputs="concatenated string:4270.all" uid="4270" uid_parent="root"/>
  <Node _name="Close Reference" inputs="reference:4250.ref,error in (no error):4263.err" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", "4270.all")
# 10 Is a VI in memory, by NAME (Inputs[0]), without opening anything by path: Application:All VIs In Memory,
# Search 1D Array. detail = "<index>,<count of VIs in memory>"; index -1 means not in memory.
probe("P10_VIsInMemory", "Reads Application All VIs In Memory and searches it for the name in Inputs[0]. Opens no VI by path.", "Is this VI name in memory",
      '''  <Node _name="Property Node" fields="read+Application\\3AAll VIs In Memory" type="{LV.Application}" inputs="reference:,error in (no error):" outputs="Application\\3AAll VIs In Memory:4220.vis,reference out:,error out:4220.err" uid="4220" uid_parent="root"/>
  <Node _name="Search 1D Array" inputs="1D array:4220.vis,element:4210.value,start index (0):" outputs="index of element:4221.idx" uid="4221" uid_parent="root"/>
  <Node _name="Array Size" inputs="array:4220.vis" outputs="size(s):4223.n" uid="4223" uid_parent="root"/>
  <Constant _name="comma" outputs="value:4217.value" type="string" uid="4217" uid_parent="root" value=","/>
''' + NS("4221.idx", 4222) + NS("4223.n", 4224) + '''  <Node _name="Concatenate Strings" inputs="string:4222.text,string:4217.value,string:4224.text" outputs="concatenated string:4226.all" uid="4226" uid_parent="root"/>
''', "4220.err", "4226.all")
# 11 Run VI async, Close Reference while the PROBE keeps running, then a second Open VI Reference: does the VI
# outlive the reference it was started through? Separates Close Reference from "the probe VI ended".
# The second open waits 500 ms after Close Reference (a Wait whose input and whose result gate the path), because a
# first draft read the state at once and got 2 in one run and 1 in the next (unrecorded observation).
# detail = "<state before Close Reference>,<state read through the second reference 500 ms later>".
xa, ea, ta = state_nodes("4221.ref", "4221.err", 4231, "before")
xb, eb, tb = state_nodes("4234.ref", "4234.err", 4251, "after")
probe("P11_RunAsyncCloseRef", "Open VI Reference, Run VI async, read state, Close Reference, Open VI Reference again, read state, close.", "Run, close the ref, reopen, read",
      OPEN + '''  <Constant _name="do not wait" outputs="value:4215.value" type="bool" uid="4215" uid_parent="root" value="false"/>
  <Constant _name="comma" outputs="value:4217.value" type="string" uid="4217" uid_parent="root" value=","/>
  <Node _name="Invoke Node" target="Run VI" type="{LV.VI}" inputs="reference:4220.ref,error in (no error):4220.err,Wait until done:4215.value" outputs="reference out:4221.ref,error out:4221.err" uid="4221" uid_parent="root"/>
''' + xa + f'''  <Node _name="Close Reference" inputs="reference:4221.ref,error in (no error):{ea}" outputs="error out:4233.err" uid="4233" uid_parent="root"/>
  <Node _name="Unbundle By Name" fields="status" inputs="input cluster:4233.err" outputs="status:4270.status" uid="4270" uid_parent="root"/>
  <Constant _name="settle ms" outputs="value:4272.value" type="uint32" uid="4272" uid_parent="root" value="500"/>
  <Node _name="Select" inputs="t:4272.value,s:4270.status,f:4272.value" outputs="s? t\\3Af:4271.sel" uid="4271" uid_parent="root"/>
  <Node _name="Wait (ms)" inputs="milliseconds to wait:4271.sel" outputs="millisecond timer value:4273.tick" uid="4273" uid_parent="root"/>
  <Node _name="Greater Or Equal To 0?" inputs="x:4273.tick" outputs="x &gt;= 0?:4274.ok" uid="4274" uid_parent="root"/>
  <Node _name="Select" inputs="t:4212.path,s:4274.ok,f:4212.path" outputs="s? t\\3Af:4275.sel" uid="4275" uid_parent="root"/>
  <Node _name="Open VI Reference" inputs="vi path:4275.sel,error in (no error):4233.err" outputs="vi reference:4234.ref,error out:4234.err" uid="4234" uid_parent="root"/>
''' + xb + f'''  <Node _name="Concatenate Strings" inputs="string:{ta},string:4217.value,string:{tb}" outputs="concatenated string:4260.both" uid="4260" uid_parent="root"/>
  <Node _name="Close Reference" inputs="reference:4234.ref,error in (no error):{eb}" outputs="error out:4240.err" uid="4240" uid_parent="root"/>
''', "4240.err", "4260.both")
print("wrote", len(os.listdir(OUT)), "files to", OUT)
