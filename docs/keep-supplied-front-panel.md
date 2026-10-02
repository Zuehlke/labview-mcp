# Keeping a supplied front panel: the scripted diagram graft

**Question (2026-09-30):** why does every route in this repository rebuild a VI whole, so that a
supplied VI with a designed front panel - a CLD exam template, a customer panel - cannot be used
as it is? And is there a way to use it anyway?

**Short answer:** the rebuild is a property of the AIXML route, not of LabVIEW. LabVIEW's own VI
Server scripting can put a generated diagram INTO the supplied VI and wire it to the supplied
controls, and that was measured end to end on the Car Wash exam template (CLD 100929C-01): the
result is executable, runs its first state correctly, keeps its typedef bindings, and its front
panel renders **byte-identical** to the untouched template. Three scripted steps, about 0.6 s of
LabVIEW time. Two defects remain, both cosmetic or bookkeeping, and both are named below.

Evidence: `experiments/panel-graft/` (the three probe helpers, the net-comparison script and the
renders). Measured on LabVIEW 2026 32-bit, one fresh instance, `dwarnCount` 0.

---

## 1. Why the VI is always rebuilt today

Four facts, each measured before this investigation, add up to "edit = regenerate":

| # | fact | consequence |
|---|---|---|
| 1 | `ConvertAIXMLToVI` writes a WHOLE VI; there is no "merge into existing" mode | every generation replaces panel, diagram, icon, VI properties |
| 2 | AIXML carries no geometry, no decorations, no control styling and no typedef identity. The template's own export is **1 250 bytes**: seven `<Control>`/`<Indicator>` lines, no positions, the two typedef clusters written as bare `cluster{...}` | a regeneration from the export cannot even describe what is lost |
| 3 | `ApplyAIXMLToVI`, the one surgical RPC, is gated on an attachment a third-party client cannot obtain, and since 2026-09-16 refuses in silence (`aixml-reference.md` §14) | the surgical door is shut, by standing rule |
| 4 | pylabview edits a heap but cannot compose nodes or wires, and a file-level panel swap is impossible because panel and diagram share one `VCTP` type pool (measured by the earlier Car Wash session) | no file-level route either |

So `CLAUDE.md` concluded *"a VI whose EXISTING FRONT PANEL must survive cannot be edited at all"*
and listed *"VI Server diagram scripting (unmeasured here)"* as the only other door. **This document
is that measurement.**

Two beliefs kept that door from being tried, and both were wrong:

- **"The merge is a manual IDE step."** The earlier Car Wash solution grafted its diagram by hand:
  copy the scaffold's diagram, paste into the template, rewire seven terminals - "the clipboard is
  the only thing that preserves every wire in one paste". `cold-build-atm-cld.md` §9 called the
  same merge "unavoidable today". But the clipboard is scriptable: `{LV.TopLevelDiagram}` has
  `Select All`, `Copy Selection` and `Paste`, and all three validate and run from a generated
  helper.
- **"There is no `{LV.Wire}` in the VI Server catalogue."** `CLAUDE.md` and
  `typedef-disconnect.md` §13a both say so, and it is false: `lvai_vi_server_reference cls=LV.Wire`
  answers 23 methods and 27 properties, including `Terminals[]`, and the shipped helper
  `scripts/lvai_wire_dyn_events.xml` has read `{LV.Wire} Terminals[]` since 2026-09-11. Rewiring
  needs exactly that property.

---

## 2. The route, as measured

Inputs: the supplied VI (a COPY - never the original), and a **scaffold**: a generated VI whose
controls have the SAME labels and types as the supplied ones and whose diagram is the finished
program. Here the scaffold was `Car Wash Graft.vi` from the earlier solution; any VI produced by
the normal generation route qualifies. Both are opened in the IDE's application instance
(`Project:Active Project` -> `Application`), so a project must be open and active.

### Step 1 - paste (`probe_graft_paste.xml`)

```
scaffold  {LV.VI} Block Diagram -> {LV.TopLevelDiagram} Select All -> Copy Selection
template  {LV.VI} Block Diagram -> {LV.TopLevelDiagram} Paste        (Pos unwired)
template  {LV.VI} Save.Instrument (path unwired = in place)
```

`error 0`, **204 ms**. The template panel then lists **14** controls: its 7 originals and 7
duplicates labelled `<label> 2` (`Start 2`, `Wash Options 2`, ...). That is exactly what the manual
paste produces - LabVIEW makes a new control for every pasted terminal and the duplicates carry
all the wires. The pasted objects keep their scaffold coordinates.

### Step 2 - swap the duplicates for the originals (`probe_graft_rewire.xml`)

For every control `X` for which a `X 2` exists:

```
X 2 : {LV.Control} Terminal -> {LV.Terminal} Is Source?, Diagram, Position, Connected Wire
      Connected Wire -> {LV.Wire} Terminals[] -> each end's Is Source?
X   : {LV.Control} Terminal -> {LV.Terminal} Move (owner = X 2's Diagram, position = X 2's Position)
X 2 : {LV.Control} Delete
if X 2 was a SOURCE (a control):   each former sink  -> Connect Wire (Wire Source = X's terminal)
if X 2 was a SINK (an indicator):  X's terminal      -> Connect Wire (Wire Source = former source)
      Auto Route? (F) wired TRUE
```

`error 0`, **303 ms**, 7 pairs, one reconnection each (4 controls to one sink, 3 indicators from
one source). Pairing is by the `" 2"` suffix; see §4.

### Step 3 - clean up (`probe_remove_bad_wires.xml`)

After step 2 the saved VI was **`execState 0`, eBad**. The render showed why: deleting a duplicate
removes its terminal but leaves the wire's **loose end** at each former sink, dashed and broken
(`experiments/panel-graft/renders/grafted-before-cleanup.png`). One call fixes it:

```
{LV.VI} BD.Remove Bad Wires -> Save.Instrument
```

`error 0`, **108 ms**, and the VI is then **`execState 1`**.

---

## 3. Verification - from the files, not the session

| check | result |
|---|---|
| `lvai_exec_state` on the saved VI | `1`, eIdle, no linker errors |
| AIXML export: node / structure / case frame / tunnel counts | 16 / 2 / 6 / 79 - identical to the scaffold |
| each panel terminal's parent and its consumer (`netcheck.py`) | all 7 inside the While Loop, each feeding exactly the sink it fed in the scaffold (`Start` -> case tunnel In4, `stop` -> `Select.s`, `Wash Entry` <- `Unbundle By Name.Wash In Progress`, ...) |
| uids of the panel objects | the TEMPLATE's own: 176, 208, 317, 345, 68, 302, 329 - the real controls are wired, not copies |
| front panel, rendered by `Print.VI To HTML` | **byte-identical PNG** to the untouched template (MD5 `5dc88d63…`) |
| typedef references in the saved file | `Wash Options.ctl` 2, `Car Wash Indicators.ctl` 2 - the same as the template |
| run, `runForMs` 1500 | Initialize state reached: 9 LEDs off, `Wash Entry` FALSE (Wash Vacant), `Elapsed Time` 0.00, all controls enabled, no error |

Not checked: coercion dots. `lvai_coercion_dots` answered `subViCalls: 0` - its enumeration does
not descend into structures, and every subVI call here sits in a Case frame (the same blind spot
`lvai_swap_subvis` had until 2026-09-16). A driven wash cycle was not run either: `Start` and `stop`
are latched, and a latched boolean cannot be signalled (`Error 1193`).

---

## 4. What is still wrong, and what the route needs before it is a tool

1. **Terminal POSITIONS are offset (cosmetic, measured).** Every moved terminal landed about
   **(+425, +22) px** from its duplicate's place - `stop` scaffold (578, 221) -> grafted (1010, 240),
   `Car Position Slider` (590, 578) -> (1012, 605) - which is the While Loop's diagram origin.
   The three indicators ended up outside the loop's visible border while still belonging to its
   diagram. So `{LV.Terminal} Position` reads in PANE coordinates and `Move`'s `position` is taken
   relative to the OWNER diagram. The correction (subtract the owner's origin) is inferred from
   these numbers, not measured. It matters for a CLD: style is graded.
2. **Loose ends need `BD.Remove Bad Wires`.** It also removes any broken wire that was in the
   supplied VI before - harmless for an empty exam template, worth reporting for anything else.
   Deleting the loose wire per sink before `Connect Wire` would be the targeted alternative;
   untested.
3. **Pairing by `" 2"` is fragile.** A supplied label that already ends in ` 2`, or a third copy,
   breaks it. The robust form: record `Controls[]` refs BEFORE the paste, take the new ones after
   it as the duplicates, and pair by label with LabVIEW's numeric suffix stripped, checking the
   type descriptor matches.
4. **A scaffold control the template does not have** stays on the supplied panel as a new, unplaced
   control. A tool must refuse that (or name it) before pasting - it is the one way this route can
   damage the panel it exists to protect.
5. **Control descriptions are the TEMPLATE's.** The scaffold's descriptions die with its
   duplicates. Copying `Description` from `X 2` to `X` before the `Delete` is one property write.
6. **The system clipboard is used.** It overwrites whatever the user had copied, and two agents
   grafting at once would paste each other's diagrams. Graft only from the orchestrator, one at a
   time - the same rule as swaps and project open/close.
7. **The IDE application instance is required**, so the project must be open and active, with the
   usual consequences of the close-save (`CLAUDE.md`, "ONE AGENT, ONE OUTPUT DIRECTORY").

Every class used is in the catalogue (`TopLevelDiagram`, `Terminal`, `Wire`, `Control`, `VI`,
`Panel`), which matters because authoring against uncatalogued classes preceded three LabVIEW
deaths. None died here.

---

## 5. What this means for a build

The shape that falls out is the hybrid the earlier Car Wash and ATM sessions already chose, with
the manual half scripted:

```
1. copy the supplied VI (never touch the original)
2. generate the application normally into a SCAFFOLD VI whose controls copy the
   supplied labels and types (the supplied VI's own AIXML export gives both)
3. verify the scaffold (execState, unit tests on the subVIs) - it is the program
4. graft: paste -> swap duplicates -> Remove Bad Wires -> save
5. verify the GRAFTED file: execState, a net comparison against the scaffold,
   a panel render compared with the supplied one, a runForMs snapshot
```

Moving the code into the panel VI, rather than the panel into a generated VI, is the right
direction for the reason the earlier session gave: the diagram is the reproducible half. The VI's
own properties - window settings, documentation, icon - should stay the template's too, because
the saved file IS the template; that follows from the route and was not checked property by
property. A panel-into-generated-VI route would have to copy each of them.

**Recommendation:** productise it as one tool (working name `lvai_graft_diagram`), with items 1,
3, 4 and 5 of §4 built in, the §3 checks as its verdict, and item 6 in its description. Items 1
and 3 need one more measurement each before they are written as fact.

---

## 6. The tool: `lvai_graft_diagram` (built the same day)

The recommendation above was carried out. Helpers `scripts/lvbd_graft_paste.xml` and
`scripts/lvbd_graft_rewire.xml`; C# in `src/LabVIEWMCP/Tools/GraftTools.cs`; tests in
`GraftToolsTests.cs` against the three exports of the §2 run (`tests/.../Fixtures/graft/`), each
check with a control arm that must fail.

### What §4 became

| §4 item | now |
|---|---|
| 1. terminal positions offset | **FIXED, measured.** The rewire helper moves each terminal TWICE: the first `Move` lands it and `Position` is read back, the second applies `target + target - landed`. On a fresh copy all 7 terminals came out at exactly the duplicate's coordinates (`Final Left/Top` = `Target Left/Top`), and the render shows them where the scaffold had them. So `Position` reads pane coordinates and `Move` takes owner-relative ones - the correction is right whichever convention holds, which is why it was built that way rather than on the inferred offset |
| 2. loose ends | `BD.Remove Bad Wires` runs inside the rewire helper; a supplied diagram with code is refused, so it cannot remove anything of the user's |
| 3. pairing by `" 2"` | the paste helper returns the labels BEFORE and AFTER the paste; a duplicate is a new label equal to its original plus `" "` plus digits, exactly one per scaffold control. A prefix (`Start Delay 2` is not `Start`'s) and two candidates for one label are both tested: the second is REFUSED (`pairingFailed`), never guessed |
| 4. extra scaffold control | refused before the paste (`scaffoldControlsNotOnPanel`), with a kind or type mismatch (`controlTypeMismatch`) and a non-empty supplied diagram (`panelDiagramNotEmpty`) beside it |
| 5. descriptions | `copyDescriptions` (default on) copies a non-empty scaffold description onto the supplied control before the duplicate is deleted |
| 6. clipboard | in the description; the agents graft only when no other agent runs |
| 7. IDE instance | `Error 1055` is answered as `noActiveProject` |

### Acceptance, 2026-09-30 - the built exe over raw stdio

A client fetches its tool list at session start, so the new tool was called through
`experiments/panel-graft/call_tool.py`. Supplied VI: the ORIGINAL template in the exam folder
(the tool only copies it - its MD5 was unchanged afterwards). Output: a solution folder with its
own `Controls\` and `SubVIs\`, **not listed in the active project**, which had not been measured
before and works.

| | result |
|---|---|
| wall clock | **6.4 s** for export ×3, two renders, both helpers, `execState` |
| verdict | `ok: true`, `execState 1`, 7 of 7 grafted and `placed`, no leftover, `wiringMatchesScaffold`, `nodeCountsMatch`, `panelIdentical`, `diagramChanged` |
| typedef references | 2 -> 4 for each `.ctl` - more, never fewer; the pasted diagram names them too |
| run, `runForMs` 1500 | Initialize state reached, all LEDs off, Wash Vacant, 0.00 s |
| control arm | the finished SOLUTION VI offered as the "supplied" panel: `panelDiagramNotEmpty`, 46 elements, 0.9 s, and no output file left behind |
| project afterwards | the close sweep removed `lvbd_graft_paste.vi` and `lvbd_graft_rewire.vi`, which LabVIEW had adopted - the reason the helpers live under `%TEMP%\LabVIEWMCP\helpers`; a probe helper kept elsewhere stayed listed |

### 6a. In place - the shape the agents use

An exam wants the supplied VI at its own path and under its own name, so the agents copy it aside
and graft FROM the copy INTO the original path. Measured the same day: the template folder copied
as a solution, `Car Wash.vi` and the scaffold both LISTED in the active project, the copy in a
scratch folder, `overwrite: true`.

| | result |
|---|---|
| verdict | `ok: true`, **2.2 s** with the helpers already generated, 7 of 7 `placed` |
| the copy grafted from | MD5 unchanged - the tool never writes its `panelViPath` |
| afterwards | `execState 1` from a fresh open; the close sweep found nothing to remove, and both VIs still listed |

It also ran while a DIFFERENT `Car Wash.vi` - the previous acceptance's output - had been loaded
and run minutes earlier, the same-name shape `CLAUDE.md` warns about; no `1051`, and the verdict
would have caught a stale copy (`wiringMatchesScaffold`, `diagramChanged`).

### 6b. New controls - `allowNewControls`

The user's decision of 2026-09-30: new controls may simply be ADDED, and the layout tidied by hand
afterwards. So a scaffold control whose label the supplied panel lacks is refused by default
(`scaffoldControlsNotOnPanel`) and accepted with `allowNewControls: true`: the paste creates it as a
new control under its own label - no clash, so no numeric suffix - already wired, and it is listed
under `newControls`, excluded from the pairing and from the leftover check. `panelIdentical` is
then reported but no longer decides `ok`, since the panel is meant to change.

Measured the same day on the Car Wash template with a scaffold that echoes the slider into a new
`Position Echo` indicator: without the option the call refused in 1.0 s; with it, `ok: true` in
1.8 s, the two supplied controls swapped and placed, wiring equal to the scaffold, typedefs kept,
`panelIdentical: false`. A run with the slider signalled to 3 read `Position Echo = 3.0`. LabVIEW
put the new indicator ABOVE the existing layout, growing the panel upwards, in the default style;
every supplied object stayed where it was.

### 6c. Event Structures - the graft re-registers them

A static front-panel event is bound to the control it was registered on, and the swap deletes
the pasted duplicates - so a producer loop came back with every frame's selector EMPTY and the VI
eBad (measured 2026-09-30). The graft now writes the scaffold's front-panel specs again after the
swap, by control LABEL, onto the supplied controls, and answers `events`; `pylv-set-event-spec.py`
learned to open the emptied `<EventNodeEvents elements="0" />`. It CLOSES the project for that
pylabview edit and leaves it closed. User-event frames are not touched. `docs/cold-build-carwash-pc.md`.

### 6d. Hardening after the second build (2026-09-30)

Three findings of `docs/cold-build-carwash-pc.md` became code the same day:

- **An unwired scaffold terminal is refused in the plan** (`scaffoldTerminalUnwired`, naming the
  terminals). It has no wire ends to move, and the rewire helper used to fail on it with `1055`,
  which the tool then reported as `noActiveProject` while a project was active. A `1055` from a
  helper that has already proved the project active is now `rewireReferenceInvalid`.
- **No half-graft.** Any failure after the paste copies the plain supplied VI back to the output,
  so a refused call leaves the file as it was.
- **`switchActionControls`** - boolean labels, one per line - are set to Switch When Pressed
  (`Mechanical Action` = 0) in the rewire helper's own IDE session, before its save. A label that
  is not a boolean control on both panels is refused before anything is written
  (`switchActionNotABooleanControl`); the answer carries `switchActions`, read from the export
  (`style` no longer `latched`), and gates `ok`. Accepted on `C:\Temp\GraftSwitchAccept`:
  `ok: true` in 3.0 s, `switchActions {stop: true}`, and a `signalsJson` run then signalled `stop`
  - which `Error 1193` had refused on the latched original.

**Scope, the user's rule of the same day: the graft is for the MAIN GUI only.** The call is
seconds; the route around it is minutes, and a subVI's panel is not worth them.

### 6e. Every graft WITHOUT `switchActionControls` failed - fixed 2026-10-01

**Symptom.** Four calls in a row on 2026-10-01 answered `rewireFailed` with *"The rewire helper
answered error (unreadable)"*, `errorCode: null`, `errorOut: null` - grafting
`C:\Temp\WebBrowser\Web Browser Demo Program.vi` into a copy of `WebBrowser.vi`, whose panel holds
only a LabVIEW 2026 native Web Browser control, with `allowNewControls` (all seven scaffold
controls are new, so nothing is paired). Both helpers ran clean when called by hand. Reproduced
here first time on a fresh copy.

**Cause: an EMPTY array was written with no element.** `ArrayXml` sent an empty list as
`<Array><Name>Switch Labels</Name><Dimsize>0</Dimsize></Array>`. The run tool's helper turns an
array input into a value with `Unflatten From XML` and a VARIANT as the type, which takes the type
from an ELEMENT - and there was none, so it answered **`Error 1103`** at `Unflatten From XML in
lvai_run_and_read_typed.vi` and the rewire helper never started. LabVIEW's own XML for an empty
array carries Dimsize 0 **plus one empty element as a type template** - the READ side of the tool
already knew that (`StringArray` honours Dimsize for exactly that reason, and the test fixture
says so in a comment), the WRITE side did not. Measured as an A/B by calling the rewire helper with
the tool's own inputs: without the template `1103`; with it, error 0 and every output read back.

**Why "even when the list was empty" was the whole story, and the line-break guess was not.**
`Switch Labels` is sent on EVERY call and is empty unless `switchActionControls` is given, so every
graft since §6d without that argument failed - whether or not any controls were paired. §6d was
accepted only WITH a switch label (`{stop}`), and the Car Wash acceptances of §6 to §6c predate the
input. No value contained a line break.

**Why the error was unreadable.** The graft read only the GRAFT helper's `error out` from the run
tool's `values`. When the run tool's OWN helper refuses an input, the target never runs, `values`
is empty, and the reason sits one field over in the same answer (`helperErrorCode`,
`helperErrorXml`). Nothing read it.

**The fix.** `ArrayXml` writes the template element for an empty list. `ReadHelperError` reads the
graft helper's `error out` first and falls back to the run helper's error, the RPC's, and a run-tool
refusal; the failure answer now names `errorCode`, `errorSource` (the cluster's `source` as text,
not raw XML), `failedIn` (`runHelper` or `<step>Helper`), and a refused input has its own kind,
`<step>InputRefused` - so a `1055` from the run helper, which means a control NAME matched nothing,
is no longer read as `noActiveProject`. `swaps` travels as an array rather than as a JSON string.
And `lvai_run_vi_and_read_values` itself now refuses a template-less empty array by name
(`emptyArrayWithoutTemplate`) instead of letting the helper answer `1103`.

**Accepted** with the built exe over raw stdio, same fresh copy, same arguments: `ok: true` in
15.7 s, `execState 1`, all seven new controls wired as in the scaffold (`wiringMatchesScaffold`,
`nodeCountsMatch`), both front-panel event frames (`Go`, `Stop`) registered again. Regression tests
in `GraftToolsTests.cs`: the empty-array template, the run-helper refusal in the measured answer
shape, a run-helper `1055`, and an answer with no error anywhere.

**A second acceptance, from scratch, the same morning** (`C:\Temp\WebBrowserScratch`): a new
project, seven subVIs and a scaffold built by `labview-vi-generator` in 9 min 40 s, then grafted
with `allowNewControls` AND `switchActionControls` `Go`/`Stop` - an empty pair list and a non-empty
switch list in one call. `ok: true` in **3.6 s**, `execState 1`, eight new controls,
`switchActions {Go: true, Stop: true}`, both event frames registered again. Run with `runForMs` and
`Go` signalled, the program found the native Web Browser control on the supplied panel BY LABEL -
which the scaffold alone cannot, since AIXML exports that control as a plain `string` indicator and
cannot create it - and navigated it. Its own `Wait For Page Load.vi` then answered `Error 53` from
`Execute JavaScript`: a defect of the generated program, not of the graft. Diagnosed and fixed the
same morning in the subVIs alone (the grafted VI's file stayed byte-identical), two causes:

- **`Execute JavaScript` needs the control's front panel OPEN IN THE SAME APPLICATION INSTANCE** -
  without it there is no live browser behind the control and every call is `Error 53`, while a
  `Value` write still succeeds. `lvai_run_vi_and_read_values` runs the target in the addon's
  instance and never opens its panel; opening the panel in the IDE does NOT help, because that is
  another instance's copy. A/B on two probes differing only in one `FP.Open`: 4 of 4 calls
  `Error 53` closed, 4 of 4 clean open. The navigate subVI now opens its owning VI's panel when it
  is closed - so a helper run shows the window, and a panel left open by an ABORTED run keeps the
  VI in memory, which is `Error 1357` on the next regeneration until it is closed (`FP.Close` in
  the same instance).
- **The script is a function body and needs `return`**, as NI's examples write it; without one the
  answer is `undefined`, so the load poll could never succeed.

Verified: `https://www.ni.com` and a local `file:///` page both come back with URL, title and
`Ready`, and pressing `Go` twice on one URL reloads instead of timing out.

### 6f. The connector pane is the SUPPLIED VI's - expected, and not said until now

The output is a copy of the supplied VI, and the paste moves diagram objects only. So the pane -
pattern and assignments - is whatever the supplied VI had, and **nothing of the scaffold's pane
travels**. Measured 2026-10-01: supplied `WebBrowser.vi` 4833 with 0 of 16 assigned, scaffold 4833
with `error in` 11 and `error out` 15, graft 4833 with **0 of 16** - its `error in` and `error out`
arrived as NEW controls and sit on the panel only.

That is the intended behaviour for the route's purpose - a supplied VI keeps its icon, properties
and pane alike, and a main GUI is not called as a subVI. Two consequences worth knowing:

- **A new control never gets a pane slot.** Where the result must be callable, assign it afterwards
  with `{LV.ConnectorPane}` in the IDE's application instance - not built into the tool.
- **A supplied pane with assignments is expected to survive**, because the supplied controls are
  never deleted - only their duplicates are. That half is NOT measured: every graft so far had an
  empty supplied pane or did not measure it.

### 6g. A BOUND reference to a control AIXML cannot make - the Web Browser case, 2026-10-02

**The report.** A user on server 1.8.8 could not author a VI with a Web Browser control, and so
not the usual bound reference to it (right-click the terminal > Create > Reference) that
`ExecuteJavaScript` is called through. `link="Web Browser Control"` on a `{LV.WebBrowser}` Invoke
Node validated as `Invalid method`; `lvai_convert_aixml_to_vi` wrote the VI anyway, eBad, with the
node retyped to `{LV.String}`; NI's own export of a Web Browser example did the same; and
`lvai_graft_diagram` refused a copy of that example because its diagram held code.

**What was measured, in order:**

| probe | result |
|---|---|
| `<Node _name="VI Server Reference" element="Msg"/>` into a `{LV.String}` Property Node | validates, converts, reads `Msg` back - **AIXML CAN author a bound reference** |
| the same into a `{LV.WebBrowser}` Invoke Node, `Msg` replaced by a `string` indicator named like the browser | `Invalid method` - the control is a string, so the class is wrong |
| `ref{LV.WebBrowser}` stand-in control into the same node | validates, converts, `execState 1` |
| `{LV.Control}` `Create Control Ref` on the native Web Browser control, panel CLOSED | **`Error 53`** at `Create:Control Reference` |
| on a `string` control, panel closed | `ControlReferenceConstant`, clean |
| on the Web Browser control after `FP.Open` in the IDE instance | `ControlReferenceConstant`; stand-in sinks reconnected; `execState 1`; the export shows `VI Server Reference element="Web Browser Control"` |
| the same on a FRESHLY STARTED LabVIEW | `Error 53` on the first call, clean a minute later - the browser needs a moment after `FP.Open` |

So the defect is not the reference - it is that **the grammar has no Web Browser control**, and a
reference bound to a string is the wrong class. The control has to come from a supplied panel, and
the reference has to be made where the control already exists.

**`lvai_bind_control_references`** does that: for each stand-in (`{"WB Ref":"Web Browser
Control"}`) it opens the panel in the IDE instance, runs `Create Control Ref` - retried every 250 ms
while it answers `53` - moves the new node onto the stand-in terminal's diagram and position,
deletes the stand-in, reconnects every sink, runs `BD.Remove Bad Wires`, saves, and puts the panel
back in the state it found it, also after a failed step. `ok` is decided from an export: no stand-in
left, a `VI Server Reference` to the control feeding exactly the sinks the stand-in fed, `execState
1`. Helper: `scripts/lvbd_bind_control_refs.xml`.

**Accepted 2026-10-02** with the built exe over raw stdio on a cold LabVIEW: a copy of NI's
`Display a URL.vi` grafted with `replaceDiagram` (§6h) and a scaffold that writes `URL String` into
the browser and runs `return 1+1;` through a `WB Ref` stand-in; the bind answered `ok: true` on its
first call (2.9 s), a second call was refused by name because no stand-in was left, and the program
returned `2` with the browser showing the NI page - on its SECOND run. The first run of a fresh
instance answered `Error 53` from `Execute JavaScript` right after its own `FP.Open`: a program that
opens its panel must wait or poll before its first script, as `Wait For Page Load.vi` (§6e) does.

**Two side fixes from the same report.** `lvai_convert_aixml_to_vi` reads the written VI's execution
state now (`checkExecutable`, on by default) and answers `ok: false`, `executable: false` and a
`warning` for a broken result instead of a clean `errorCode 0` - accepted on the field report's
probe. It still WRITES what validation refuses, on purpose, because that tolerance is the route for
class methods and loaded subVIs. And `lvai_check_aixml` / `scripts/aixml_lint.py` warn
`webBrowserReferenceNotAuthorable` / `web-browser-reference-not-authorable` for both bound spellings.

**Accepted end to end in a FRESH session, 2026-10-02, through `labview-vi-generator`.** A new
project, a copy of NI's `Display a URL.vi` as the supplied panel WITH code, and the user-level task
"show the page's title in a new `Page Title` once it has loaded". The agent grafted with
`replaceDiagram` and `allowNewControls`, bound `WB Ref`, and driven through `signalsJson` the VI
showed `Acceptance Page`, `Second Page` and the live NI page title; `execState 1`, the supplied file
unchanged by MD5. About 9.5 minutes of agent time. Its report named four gaps, all closed the same
day:

| gap | what happened | fix |
|---|---|---|
| the poll had no shape | "poll" was written down, and the obvious Event Structure Timeout frame has no terminal in AIXML | the agent's measured shape - a second loop paced by `Wait on Notification` - is `scripts/aixml-skeletons/web-browser-title-poll.xml`, and both agents point at it |
| `Error 1357` after a test run | the program's own `FP.Open` leaves its panel open in the instance the run used, so the VI stays in memory; regenerating the scaffold failed until the agent closed the panel with a helper of its own | `lvai_run_vi_and_read_values` closes such a panel afterwards (`closePanelAfterRun`, default true, answered as `panelClosedAfterRun`). A/B on one VI: `1357`, then the helper answering `panel was open: true`, then the same convert clean; a second call `false`, error 0 |
| a comment clipped after the graft | 43 characters, whole in the scaffold, cut off in the grafted VI around the supplied terminals and the new reference node | the agents' graft step now makes the render and reading the comment mandatory |
| the error rule was unclear | "the error-cluster rule does not apply" left open whether a new `error out` belongs on a supplied panel | no `error in`; an `error out` indicator when new controls are allowed; otherwise `NEEDS CLARIFICATION`, because an unwired error output raises the automatic error dialog, a modal that stops the gRPC service, and `Simple Error Handler.vi` is not callable by bare name |

**`closePanelAfterRun` was accepted over raw stdio as an A/B on two copies of the accepted app**:
with it the run answered `panelClosedAfterRun: {closed: true, panelWasOpen: true}` and a convert
onto that path was clean; with `closePanelAfterRun: false` the same convert answered `1357`. **The
FIRST acceptance failed, and the cause was the tool's own unit test**: the close helper sat at a
fixed path in the real helper cache, the test's fake converter wrote a 14-byte stand-in there, and
the live run took it for a fresh helper and got no outputs back (`panelWasOpen: null`). The helper
now lives beside the run helper, so a test with its own helper path stays in its own directory -
and our own helpers (everything under `%TEMP%\LabVIEWMCP`) get no close step at all, since none of
them opens its panel and about twenty tools run one. A missing answer is now reported with the RPC
error. Same lesson as everywhere in this repository: the test written beside a fix did not verify
it - the live run did.

**A SECOND acceptance, in a new project and a fresh session, on a harder panel** - NI's
`Navigation History Methods.vi`, whose code calls four `{LV.WebBrowser}` methods through `link=`.
The task: a small browser with Back, Forward, Reload and Stop Load, plus a new `Page Title` that
follows every navigation. `labview-vi-generator`, about 10 minutes: `replaceDiagram` emptied the
copy (all 7 controls kept, 6 event frames registered again), ONE `WB Ref` stand-in fed all five
method nodes in two loops and one bind reconnected both (`sinksReconnected: 2`), and driven with
`signalsJson` the titles came out `Page One`, `Page Two`, Back `Page One`, Forward `Page Two`.
`panelClosedAfterRun` answered `closed: true` after every run, and the scaffold regenerated
afterwards without `1357`. The supplied file was unchanged by hash. No tool defect; five gaps in
the agents' guidance, closed the same day:

| gap | fix in `labview-vi-generator` / `labview-vi-editor` Phase 6g |
|---|---|
| the supplied VI, its copy and the output share one file name - exporting the original under it risks `Error 1051` | export a RENAMED copy; an output the task places elsewhere is not "the supplied VI's own path" |
| every scaffold terminal must be wired, also an event button whose value is never needed, with no pattern given | the button's terminal inside its own Value Change frame, wired to a Case selector whose TRUE case acts - a LATCHED button reads TRUE for the press and resets on that read (LabVIEW's documented latch semantics), so one click is one action |
| which buttons to switch for a test - switching the DELIVERED Back/Forward means a human clicks twice per action behind that guard (Switch When Pressed semantics, as the agent pointed out; not clicked by hand) | the deliverable keeps its mechanical actions; tests drive a SECOND, test-only graft with `switchActionControls` naming only the driven buttons |
| that the navigation methods ride the same stand-in | one stand-in serves every `{LV.WebBrowser}` node; method names from NI's example export |
| after a graft (project closed) or a run (hierarchy unloaded) a scaffold calling a project-local subVI regenerates as `Error 53` | re-open that subVI with `lvai_open_file` `viPaths` first; the convert's unsupported-subVI list names it |

**A THIRD acceptance, same panel, the new guidance NOT spelled out in the task**: the agent kept
every delivered button latched (verified from the export: `style="latched"` on all five), drove a
separate test copy with only Back, Forward and Stop switched, exported a renamed copy, and the four
title checks passed; the supplied file unchanged by hash. It found one tool defect and three smaller
gaps, all closed the same day:

| finding | fix |
|---|---|
| a timed run whose target ends on its own - a signalled `Stop` - made the helper's `Abort VI` answer `Error 1000`, reported as `helperFailed: true` with a note saying nothing was set, beside the real final values | reproduced, then fixed: `targetEndedBeforeAbort: true`, `helperFailed: false`, and the note says the values are FINAL |
| an aborted snapshot's `error out` is only the indicator's default, and the answer did not say so | the snapshot note says so and points at signalling Stop |
| `Page Title` read empty twice right after a navigation | the skeleton's script treats an empty title as pending (empty `document.title` counts as pending); an untitled page keeps the previous title |
| a start-up action on a supplied control (NI's original loads the URL at start) had no route | read it through a bound reference - a `ref{LV.String}` stand-in into a `read+Value` Property Node, bound in the same call. Measured on a plain VI: `execState 1`, the read returned the control's value |
| the scaffold's pane, the test copy's `Stop`, re-binding after a re-graft, the boolean case selector spelling, one `url` for two listed VIs | written into both agents; `True`/`False` into `lvai_aixml_reference` §7; `lvai_add_vis_to_project` answers `urls` |

**Still open**: in one of eight runs the program did not end on a signalled `Stop` and an identical
later run did. An `Execute JavaScript` call blocking during a navigation is the hypothesis; it is
not reproduced, so nothing was changed for it.

The same session also showed the client serving a STALE tool catalogue - `replaceDiagram` and
`checkExecutable` absent from the schemas it displayed - while both reached the server and worked.
A parameter missing from a displayed schema is therefore not proof that it is missing from the server;
the DLL is (`grep -a`).

### 6h. A supplied diagram that already holds code: `replaceDiagram`

The graft refused such a panel (`panelDiagramNotEmpty`), and §"Still open" said deleting the old
diagram first "would take the panel terminals with it". **That is true only of a careless delete.**
`scripts/lvbd_graft_clear.xml`, run on the output COPY when `replaceDiagram` is true:

1. moves every control's terminal onto the TOP-LEVEL diagram - a terminal left inside a structure
   is deleted with it, and deleting a terminal deletes its control;
2. deletes every node of the top-level diagram's `Nodes[]` - a structure takes its contents with it.
   **Terminals are not in `Nodes[]`**: NI's Display a URL listed only its While Loop while two
   terminals sat beside it;
3. only then reads and deletes the remaining `Wires[]` and `Decorations[]` (free labels are
   `Text`, boxes `Decoration`), so no reference points at something a structure already took;
4. `BD.Remove Bad Wires`, save.

Measured on that example: loop, Event Structure, Local Variable, two comments and a terminal-to-
terminal wire gone; `URL String`, `Stop` (moved out of its event frame) and the Web Browser control
kept; `execState 1`. The graft then plans against the emptied copy, and the typedef comparison
uses the emptied copy as its baseline - a typedef constant in the discarded code is gone on purpose.
**The old code is discarded in the OUTPUT; the supplied VI itself is never changed.** The answer
reports `diagramReplaced` with the counts.

### Still open

- Placing and styling a new control to match the panel (`Position`, and `Move`/`duplicate` or
  `Replace` with a styled `.ctl` - unmeasured).
- A driven wash cycle through the typedef cluster `Wash Options` (latched buttons are solved by
  `switchActionControls`).
- Coercion dots inside Case frames: `lvai_coercion_dots` does not descend into structures.
- ADDING a bound reference to a VI whose existing code must be KEPT: `replaceDiagram` discards it,
  and `lvai_bind_control_references` needs a stand-in the diagram is wired against. A hand-kept VI
  still reaches the browser through a subVI that takes a `ref{LV.WebBrowser}` terminal.
- CREATING a Web Browser control on a panel that has none (`{LV.GObject}` `Move` with `duplicate`
  from a donor VI, or `Replace` - unmeasured).
