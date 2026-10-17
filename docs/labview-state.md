# Looking at a running LabVIEW: `lvai_labview_state` and `lvai_vis_in_memory`

Two read-only tools, ported 2026-10-17 from a colleague's fork, where they are the eyes of a
restart route (`lvai_restart_labview`, `lvai_answer_dialog`). The restart and the dialog answering
are **not** ported: the machine interface here (`Infra/LabViewMachine.cs`) has no call that closes
a process or posts a key or a click, so nothing behind these two tools can change a running
LabVIEW. What was ported, and what was added here, is below.

## 1. `lvai_labview_state` - from outside, no LabVIEW call

Reads Windows, not LabVIEW, so it answers while NI's service is blocked and it is the same on
both AIXML backends. Processes (LabVIEW, `NI.NigelLocalService`, `LVNigelChat`), the LVAutoSave
folder, one CPU sample, and every top-level window of LabVIEW sorted into kinds:

| kind | what it is |
|---|---|
| `unsaved` | a title with `*`; our own scratch VIs are marked `labviewMcpScratch` |
| `possible-dialog` | a VISIBLE titled window that is no VI, project or frame window - or an untitled one the frame owns, or one beside a disabled frame or Project Explorer |
| `hidden-unrecognised` | a HIDDEN titled window nothing measured explains - LabVIEW dialogs have reported themselves invisible while on screen (`scratch-vis-in-memory.md` §2a) |
| `vi`, `project`, `labview-frame`, `getting-started`, `service`, `loading`, `hidden-system`, `untitled` | the rest |

A possible dialog comes with a `screenshot` (PrintWindow, nothing fronted) because LabVIEW draws
its dialogs itself and Windows exposes neither their text nor their buttons. The classifier is
the fork's, with its tests; its window classes and titles come from the fork's measured listings.

**`startedByThisServer`** is true only for a LabVIEW `lvai_ensure_labview` started in this server
process, matched by pid AND start time (a pid is reused). The record is in memory, so a server
restart forgets it.

**Measured here, 2026-10-17** (LabVIEW 2026 Q3, up 6.5 h): one LabVIEW, 33 windows - 21 hidden
system, 8 `unsaved` (four `LVMCP Validate…` VIs, panel and diagram each, all hidden and all marked
`labviewMcpScratch`), one hidden `VI Hierarchy` window as `hidden-unrecognised`, the service's
`RunService.vi`, the frame. No possible dialog. LVAutoSave empty.

## 2. `lvai_vis_in_memory` - the unsaved bits, per application instance

LabVIEW keeps one APPLICATION INSTANCE per context, and a VI lives in exactly one: a VI opened
loose in the main instance, a project's VIs in that project's instance, and NI's add-on in its
own. A helper lists `Application:All VIs In Memory` of ONE instance and reads each VI's
`Modifications:Front Panel Mods Bitset` / `Block Diagram Mods Bitset` and `VI Path`. So the first
question is which instance - the fork's tool read only the one its helper ran in, which on grpc is
NI's add-on: 419 service VIs and nothing of the user's. **`context` chooses it** (the user's
requirement of 2026-10-17):

| context | instance | how it is reached, grpc | how it is reached, pylabview |
|---|---|---|---|
| `main` | LabVIEW's main instance - VIs opened loose | the NI-built helper, run over **ActiveX** | the composed helper, over ActiveX |
| `project` | the ACTIVE project's - its VIs | `lvai_vis_modified_project.vi` in the add-on: `Project:Active Project` -> `Application` | the same helper, composed, over ActiveX |
| `addon` | NI's AI add-on's own | the helper run through NI's service | refused (`contextNotReachable`): ActiveX reaches the main instance only |
| `all` (default) | main + the active project, if any + addon on grpc | | |

`projectPath` pins the project: another active project answers `otherProjectActive`, none
`noActiveProject`, and nothing is opened or activated - the tool is read-only.

Each VI is sorted by origin: `service` (`LV AI …lvlibp`), `labview` (`GSW.lvlibp`), `helper` (our
helpers and scratch VIs, by name or by a path in our caches or `user.lib\LV_MCP`),
`installation` (a path under the running LabVIEW's folder) and `other` - the user's code. The
path is what makes `other` usable: in a project instance the user's VIs are a handful among
hundreds of Caraya, OpenG and vi.lib VIs.

**Measured here, 2026-10-17, the same calls on both backends:**

| state | main | project | addon (grpc) |
|---|---|---|---|
| no project open | 5: four `LVMCP Validate…` scratch VIs, all MODIFIED, plus the helper itself | `noActiveProject` | 419 (404 service, 15 start window) |
| `IconDemo.lvproj` open, one VI opened through it | the same 5 | **876: 747 installation, 7 user VIs** (the six `TemperatureSensor` accessors and the opened `Celsius To Fahrenheit.vi`, with paths), **122 unreadable** | 404 |

pylabview and grpc answered identical lists in every instance.

**The 122 unreadable lines** all answered `1035` to `Open VI Reference` by name: class private
data controls, typedef `.ctl`s and polymorphic VIs of Caraya, OpenG and vi.lib. They carry no path,
so they cannot be sorted by it and land in `other`; the answer lists the readable user code FIRST
for that reason. NI's own packed-library lines answer 1035 too and do not count against
`onlyServiceAndHelpers`; everything else unreadable does.

## 3. A defect the port found in its own helper: the close race

The first project-instance run answered `1025` for EVERY VI. The helper closed the project's
application reference with an error chain that did not pass through the loop, so LabVIEW ran the
`Close Reference` in parallel with it. The reference now runs THROUGH the loop on a shift
register and its close takes it from the right terminal - 0 errors on the readable lines
afterwards. The same shape as `lvai_save_modified_vis.xml`'s fix of 2026-10-17
(`lvclass-creation.md` §10e); a unit test pins it.

## 4. Not measured yet

- A HAND EDIT in each instance, which is the whole point - the bits of `LVMCP Validate…` show the
  read works in `main`; a user edit in the project instance has not been read back yet.
- `possible-dialog` and its screenshot on this station: no dialog was up.
- Which instance a VI opened with `lvai_open_file` WITHOUT a project lands in.
