# NI bug report: ValidateAIXML terminates LabVIEW on an unknown VI Server class

Everything below was measured on 2026-08-26 and is reproducible in under a minute.

## Product

| | |
|---|---|
| LabVIEW | 2026 Q3, 32-bit, version string `26.3.1f1` |
| Addon | NI Nigel / the AI assistant, providing the `lvai.LVAI` gRPC service |
| Also installed | `NI Nigel AI Advisor for LabVIEW 2025 [25.82.49163]` and `NI Nigel Advanced [26.52.49163]` |
| OS | Windows 11 Enterprise, build 26100 |

## Symptom

**LabVIEW terminates immediately** when the `ValidateAIXML` gRPC method is given an AIXML document
containing an `Invoke Node` whose `type` names a VI Server class that the addon's automation-class
registry does not contain. The gRPC call does not return; the client sees the connection close
mid-request.

Expected behaviour is an error return - "unknown class", the same way an unknown *method* on a known
class is reported - not process termination.

## Minimal reproducer

Two nodes, no diagram, no project needed. Save as `crash.xml`:

```xml
<VI _name="crash.vi" description="Minimal reproducer.">
  <Node _name="Invoke Node" target="RemoveItem" type="{LV.ProjectItem}"
        outputs="reference out:20.reference out,error out:20.error out" uid="20" uid_parent="root"/>
</VI>
```

Call `ValidateAIXML` with that path. LabVIEW is gone before the call returns.

`{LV.ProjectItem}` is not an invented class - NI's own
`resource\Framework\Providers\LVClassLibrary\NewAccessors\VIRetooler\CLSUIP_GetProjItemOfMemberVI.vi`
exports as `type="{LV.ProjectItem}"` with `fields="read+Name"`. It is simply absent from whatever
list the validator consults.

## The control, which is what makes this precise

The same document shape with a class the registry DOES contain is harmless. Validated three times in
a row, `errorCode 0` each time, process untouched:

```xml
<VI _name="known.vi" description="Control.">
  <Node _name="Invoke Node" target="Run VI" type="{LV.VI}"
        outputs="reference out:20.reference out,error out:20.error out" uid="20" uid_parent="root"/>
</VI>
```

So it is neither validation itself nor the two-node shape. It is the unknown class name.

## What LabVIEW logs

`%TEMP%\LabVIEW_32_26.3.1f1_interactive_<user>_cur.txt`, written about four seconds before the
process disappears. Windows Error Reporting sees nothing, because LabVIEW handles the fault itself:

```
source\ole\OMAutoClasses.cpp(74) : DWarn 0x762E6013:
    Out of bounds TypedObjList access (index: -1, nObj: 0)
[ExecSys:0; Executing:"[VI "LV AI Core.lvlibp:VI generator.vi"]"]

VI call stack:
- LV AI Core.lvlibp:VI generator.vi
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:ValidateAIXML.vi
- LV AI gRPC Service.lvlibp:LVAI.lvclass:Start Sync.vi
- LV AI gRPC Service.lvlibp:gRPC-servicer-release.lvlib:ServiceBase.lvclass:Start.vi
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:RunService.vi
```

**The message names the defect.** `index: -1, nObj: 0` is a name looked up in an EMPTY
`TypedObjList`, the "not found" result of -1 returned, and then used as an index without a bounds
check. Two `DWarn`s and three to six `minidump id` lines per occurrence.

## Evidence that nothing else is required

Eight cold rounds, each one stopping LabVIEW, starting it fresh and waiting for the service:

| round | what was done after the service reported ready | outcome |
|---|---|---|
| 1-3 | opened a project, generated 34 accessor VIs (no `ValidateAIXML`, helper cached) | alive 7 min, **0 faults** |
| 4 | validated `{LV.ProjectItem}` files | **dead inside 15 s**, 3 dumps |
| 5 | waited 20 s first, then the same | **dead inside 15 s**, 3 dumps |
| 6 | opened a project, then nothing | alive 4 min, 0 faults |
| 7 | nothing at all | alive 3 min, 0 faults |
| 8 | pinned the port so no discovery ran, then the same validates | **dead inside 15 s**, 3 dumps |

Ruled out by those rows: the project being open or closed, elapsed time since start-up, gRPC port
discovery, and generating VIs. The only thing present in every death and absent from every survival
is a `ValidateAIXML` naming a class the registry lacks.

## Ruled out: the client that issues the call

Asked directly, and it was a real gap - every confirmed crash up to that point had been issued by
the same short-lived CLI process, so "the CLI does something odd" and "the document content does it"
could not be told apart. Round 8 had ruled out port discovery but not the CLI itself.

Re-run through a completely different client - a long-lived MCP server holding an open channel,
which had never successfully validated a file of this shape before:

| call | client | result |
|---|---|---|
| `known.xml` (catalogued class) | MCP | `errorCode 0`, process alive |
| `c1.xml` (`{LV.ProjectItem}`) | MCP | connection lost mid-request, **process gone**, `OMAutoClasses` x2, 5 minidumps |

Byte-identical to the CLI outcome, down to `Executing:"[VI "LV AI Core.lvlibp:VI generator.vi"]"`.
**So it reproduces through two independent gRPC clients**, which is stronger evidence than the
original single-path measurement: it is the document, not the caller.

## Ruled out: the auto-save recovery directory

Worth recording because it is the obvious suspect and it is wrong. `LVAutoSave` under
`Documents\LabVIEW Data` held twelve archives, eight of them from the day of these measurements,
with timestamps matching the crashes almost one for one - which reads exactly like a cause.

Tested directly: every file moved out, `LVAutoSave` verified empty, LabVIEW started fresh. The A/B
reproduced unchanged - three validates on a catalogued class at `errorCode 0` with the process alive,
then one validate naming `{LV.ProjectItem}` and the process gone in eight seconds with the same two
`OMAutoClasses` entries. Zero new archives had been written at that point.

**The archives are a consequence, not a cause.** LabVIEW writes one when it STARTS and finds
auto-save data left over from an abnormal end, so eight archives in a day means eight restarts after
eight abnormal ends. Counting them is a way to count past crashes, not a way to prevent one.

A separate hazard does live in that directory and is worth keeping apart from this bug: leftover
auto-save data can make LabVIEW open a recovery DIALOG on start, and a modal dialog stops the whole
gRPC service until a human dismisses it. Clearing the directory is therefore reasonable hygiene
before an unattended start - it just has nothing to do with the crash described here.

## Other classes that trigger it

**Confirmed:** `{LV.ProjectItem}`.

**`{LV.Panel}` and `{LV.Cluster}` were listed here and that was wrong** - they are perfectly
ordinary, and `lvai_create_accessors.xml` names both on property nodes and validates in about two
seconds, repeatedly. They were swept in from memory rather than measured. Corrected rather than
quietly dropped, because a bug report that names harmless classes wastes the reader's time.

`{LV.Project}` and `{LV.LVClassLibrary}` are the honest open question. Both are absent from the
catalogue harvested off this installation, and both are named - on PROPERTY nodes, with valid
properties - by a document that validates cleanly every time. So an unknown class alone is not
sufficient; what has been measured to crash is an unknown class carrying an INVOKE node whose method
cannot be resolved. Whether an unresolvable method on a *known* class does the same is untested, and
that control is what would pin the defect exactly.

## A SEPARATE defect found while ruling the client out: concurrent load drops the service

Asked whether LabVIEW's gRPC might struggle with CLI and MCP hitting it at once. It does, in a way
worth reporting on its own - and it is NOT the crash above.

| test | payload | result |
|---|---|---|
| 6 sequential CLI validates | harmless, catalogued class | 6 x `errorCode 0`, process fine |
| 6 concurrent CLI validates | same | 6 x `errorCode 0`, process fine |
| 3 CLI streams for 60 s + one concurrent MCP call | same | MCP returned `errorCode 0` but took **3018 ms against a normal 831 ms** |
| 4 CLI streams for 45 s | same | **9 of 54 calls failed** |

Every one of those nine failed as *unreachable* - the channel could not be established - not as a
validation error. The document was never seen. And they are **synchronised**: all four streams failed
at call 4, all four again at call 8, one more at call 14. So the service stops answering for a
window and every request in flight sees it, then it recovers. LabVIEW stayed alive and
`Responding = True` throughout.

**Why this matters beyond throughput.** A transient drop is indistinguishable, from the client side,
from LabVIEW having died - both read as "could not find a port serving lvai.LVAI". Any tool that
concludes "LabVIEW crashed" from a failed connection will be wrong roughly one call in six under
load. Checking whether the process is still there is the only way to tell them apart.

It also confirms an older measurement in this repository from the other direction: firing several
`lvai_create_class` calls back to back caused a hang, and spacing them apart avoided it entirely.
Spacing is not superstition.

## Impact

Any tool that discovers VI Server vocabulary by validating candidate AIXML - the documented way to
tell a real method from an invented one - kills the IDE on its first miss. It also makes the affected
classes unusable in generated code, because there is no way to check a name before using it.
