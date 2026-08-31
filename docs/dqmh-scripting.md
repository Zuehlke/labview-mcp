# Scripting DQMH: creating modules and events from outside the IDE

DQMH (Delacor Queued Message Handler) ships a set of scripting VIs that build modules and events.
This note records **whether and how they can be driven through this server's interfaces**, measured
on 2026-08-31 against DQMH "Delacor QMH Event Scripter 5.0.0.112" under LabVIEW 2026 32-bit.

Read together with [dqmh-patterns.md](dqmh-patterns.md), which describes what a finished module
*looks like*; this note is about how to *make* one.

The short answer: **yes for modules, proven end to end. Yes for module discovery. Not yet for
events** — the last gap is named in §6 and it is a piece of work, not a wall.

## 1. The layout: menu VIs versus scripting VIs

Under `<LabVIEW>\project\Delacor\DQMH\` there are two kinds of directory, and only one of them is
callable:

| Directory | Contents | Callable? |
|---|---|---|
| `Event\`, `Module\`, `Testing Tools\`, `Real-Time Tools\` | the **Tools-menu entry points** — `Add New DQMH Module.vi`, `Create New DQMH Event.vi`, … | **No.** These are dialog launchers |
| `_DQMH New Module\`, `_DQMH New Event\`, … | the **implementation**, including `Script New Module.vi`, `Script New Event.vi`, `Parse Project for DQMH Modules.vi` | **Yes** |
| `_support\` | shared helpers | — |

The `.txt` file in each menu directory (`Module\Module.txt`) is the menu ordering, not an API.

**The tell is the connector pane, and it is unambiguous.** A menu VI exports as ~135–141 bytes of
AIXML — the `<VI>` element and a description, nothing else — because it has no terminals at all:

```
Module\Add New DQMH Module.vi        135 bytes, no controls
Module\Validate DQMH Module.vi       141 bytes, no controls
```

`lvai_vi_terminals` reports these as `errorKind: noTerminalsFound`. A scripting VI, by contrast,
returns a full terminal list. **So the first move against any DQMH VI is `lvai_vi_terminals`**: it
separates the two kinds in one call, and it is the only contract available (§2).

## 2. The source is locked, so connector panes are the whole contract

Every DQMH VI exports its **controls and indicators but no `<Diagram>`**. Measured on
`Script New Module.vi`: 2 452 bytes of AIXML, nine `<Control>`/`<Indicator>` elements, no diagram
node of any kind. All carry `Created using Delacor QMH Event Scripter 5.0.0.112`.

The practical consequence: **the usual move of "export a VI that already calls the target and copy
its exact shape" does not work here.** Delacor's own callers are locked too. Everything below was
derived from terminal names, types and `required` flags — which turned out to be enough, but it
means a wrong guess is not correctable by reading their code.

## 3. An AIXML `Call` cannot reach them — Error 53

This was the first thing tried and it fails:

```
Unsupported SubVI: DQMH New Module.lvlib:Get Module Type Info.vi
Unsupported SubVI: DQMH New Module.lvlib:Script New Module.vi
```

Both spellings refused, qualified and bare alike. The reason is the rule in `CLAUDE.md`:
generation resolves a target **by name against what the installation can find**, and "what the
installation can find" is `vi.lib`, `user.lib` and `LVAddons`. The DQMH scripting VIs live under
`project\Delacor\`, which is none of those, so **no spelling of the qualifier reaches them.** This
is not the library-membership trap — a correct qualifier is not the missing piece, findability is.

## 4. VI Server by path is the route, and it works

`Open VI Reference` takes a **path** and carries no such restriction. The route is:

```
{LV.Application} → Project:Active Project → {LV.Project} → Application     ← the IDE's app ref
   ↓
Open VI Reference (application reference (local) = that app ref, vi path = the DQMH VI)
   ↓
Ctrl Val.Set × n   (values as variants)
   ↓
Run VI (Wait until done = true)
   ↓
Ctrl Val.Get × n   (results as variants)
```

Four things about this that each cost a measurement:

- **The application reference must be the IDE's, not the addon's.** A generated helper runs inside
  the AI addon's application instance, where the IDE's project and its DQMH VIs do not exist.
- **A project must be OPEN AND ACTIVE**, or the first property answers `Error 1055`. Measured: with
  no project open, the probe returned 1055 from the Property Node and nothing else. It is the first
  thing to check when a DQMH helper reports nothing useful.
- **`Ctrl Val.Get` is a real method** even though the VI Server catalogue served by
  `lvai_vi_server_reference` lists only `Ctrl Val.Get All`. Validation accepts a real method name
  and rejects an invented one, which is what settled it.
- **Values move as variants and never need naming.** `External Modules` is an array of six-field
  clusters; carrying it from one `Ctrl Val.Get` straight into one `Ctrl Val.Set` means the helper
  never spells that type out. This is the trick that makes the whole approach tractable.

Two helpers ship for this, and the split is not cosmetic: **`scripts/lvdqmh_module_types.xml`**
reads the catalogue (read-only) and **`scripts/lvdqmh_new_module.xml`** builds the module. The
index must be chosen *before* the build, so it cannot come from the builder's own output — the
builder returns `type strings` as well, but only as a record of the catalogue it used.

## 5. Creating a module

### 5.1 The two calls are a pair

| Step | VI | Why |
|---|---|---|
| 1 | `DQMH New Module.lvlib:Get Module Type Info.vi` | discover the module-type catalogue |
| 2 | `DQMH New Module.lvlib:Script New Module.vi` | build the module |

`Script New Module.vi`'s terminals:

| Terminal | Type | Flag |
|---|---|---|
| `Module Name` | string | required |
| `Module Save Path` | path | recommended |
| `Module Type` | **uint16** | required |
| `Include Do Something` | bool | required |
| `External Modules` | array of `External Module Info` | required |
| `Library Icon` | `ref{UDClassInst}` | recommended |
| `Project` | `ref{LV.Project}` | required |
| `error in` / `error out` | cluster | recommended |

### 5.2 `Module Type` is an INDEX, not an enum — and that is the trap

The terminal is a **bare uint16 with no enum strings in its export**, so nothing in the pane says
what `0` or `1` means. The meaning is discovered at run time: `Get Module Type Info.vi` returns
`Type Strings` and `Descriptions` as parallel arrays, and `Module Type` indexes them.

Measured on this station:

| index | `Type Strings` | `Descriptions` |
|---|---|---|
| 0 | Singleton | A Singleton module only ever exists as a single instance. |
| 1 | Cloneable | A Cloneable module can have multiple reentrant instances running simultaneously. |
| 2 | Cloneable Panel | Creates a new Cloneable Module that uses the MGI Panel Manager Framework. |
| 3 | Singleton Panel | Creates a new Singleton Module that uses the MGI Panel Manager Framework. |

**Indices 2 and 3 are MGI Panel Manager types — an add-on.** That is the whole point: DQMH 7.x
makes module types *pluggable*, so a station with a add-on installed has more entries than one
without. The list therefore cannot be hardcoded, and **an index is only meaningful next to the
catalogue it came from.** Always read `Type Strings` on the same station and match by name.

### 5.3 `External Modules` is the catalogue, not an option

It is marked `required` and it is where those pluggable types come from — each element carries a
`Location Path`, `Library`, `Tester` and `Custom Scripting VI`. **Pass it through from
`Get Module Type Info.vi`; never pass an empty array.** An empty array would make every index past
the built-in types unreachable and would silently change what a given `Module Type` means.

### 5.4 What a run produces

Measured twice:

| | `Heater`, type 0, Do Something = 1 | `Pump`, type 1, Do Something = 0 |
|---|---|---|
| wall clock | 29.7 s | 42.6 s |
| Delacor `error out` | 0 | 0 |
| files | 58 in the folder, `Heater.lvlib` with 63 members | 83 `.vi`/`.ctl`/`.lvlib` |
| `Do Something*.vi` | present | **absent** |

Both flags demonstrably take effect. The output is the structure documented in
[dqmh-patterns.md](dqmh-patterns.md): `Main.vi`, `Start Module.vi`, `Stop Module.vi`,
`Obtain`/`Destroy Request`/`Broadcast Events.vi`, `Test <Module> API.vi`, and the
`--cluster.ctl` typedefs.

**A Cloneable module is structurally bigger, not just flagged differently.** Measured on
`FirstClone` (type 1, Do Something = 1, 28.2 s): 71 files against 56 for the same request as a
Singleton, and the extra 15 are all clone machinery — `Acquire`/`Obtain`/`Release`/
`Destroy Module Semaphore*.vi`, `Addressed to This Module.vi`, `Get Module Running State.vi` with
`Module Running State--enum.ctl`, `Module Running as Cloneable--error.vi` / `…as Singleton--error.vi`,
and `Init`/`Update Select Module Ring.vi` for the tester's clone selector. It also brings **two
nested libraries**, which `lvai_describe_project` reports in their own right:
`FirstClone.lvlib:Clone Registration.lvlib` and `FirstClone.lvlib:VI Reference Management.lvlib`.
So the module type is checkable after the fact from the file list alone — useful, because the
`.lvlib` mentions both words and grepping it proves nothing.

### 5.5 The directory layout: `Libraries\<ModuleName>\`

A module belongs in its own folder under a `Libraries` folder beside the `.lvproj`, never loose in
the project folder:

```
<project folder>    <project>.lvproj
    Libraries        <ModuleName>\      <- the .lvlib, ~50 VIs and .ctls, and the tester
```

**Pass that folder as `Module Save Path` and Delacor does the rest.** Measured 2026-08-31 with the
path pointing straight at `…\Libraries\Vent`: all 48 files landed there, the project folder stayed
clean, and the scripter wrote the relative URLs itself —

```xml
<Item Name="Vent Module" Type="Folder">
  <Item Name="Vent.lvlib" Type="Library" URL="../Libraries/Vent/Vent.lvlib"/>
</Item>
<Item Name="Test Vent API.vi" Type="VI" URL="../Libraries/Vent/Test Vent API.vi"/>
```

Note the asymmetry, which is Delacor's own and should not be "tidied": the `.lvlib` goes inside a
virtual folder named `<ModuleName> Module`, while the tester is listed at target top level — even
though the tester *file* sits in the module folder with everything else.

Getting this wrong is cheap to avoid and tedious to repair: a module written into the project
folder has to be moved file by file (~56 of them) and the `.lvproj` URLs hand-edited afterwards.
The `.lvlib` itself survives such a move untouched, because it references its members as
`../<name>` — relative to its own folder — so moving the library together with its members keeps
every path valid.

### 5.6 Two things the caller must clean up afterwards

**The scripter invalidates the `Project` reference it was handed.** The first run reported
`Error 1055` from a `Close Reference` while the module itself was built perfectly and Delacor's own
`error out` was 0. Chained into one indicator, that cleanup failure is indistinguishable from a
scripting failure and reads as "the module was not created". So the helper now reports **`error
out`** (everything up to and including the scripting) and **`cleanup error out`** separately —
verified on the second run: `0`, `0`, `1055`.

**LabVIEW MAY adopt the helper VI into the user's project.** After the first run the saved
`.lvproj` listed `lvdqmh_new_module.vi` alongside `Heater.lvlib` — the behaviour `CLAUDE.md`
records, that LabVIEW adopts every VI it has open when it saves a project.

**But it is not reliable, and that matters more than the adoption itself.** Measured over four
runs on 2026-08-31: adopted on three (`Heater`, `DQMHdemo`, `FirstClone` — the last adopting *both*
helpers of that run), **not** adopted on one (`Vent`), with no difference identified between them —
same helpers, same route, same session. So a tool must **always inspect the `.lvproj` afterwards**
rather than either assuming a cleanup is needed or assuming it is not. The condition is open, and
one clean run is not evidence that the next one will be.

Adoption also leaves the helper in memory: regenerating it to the same path then fails with
`Error 1357`, which is why later measurements had to be generated under fresh names.

## 6. Events: the dialog is the only supported route

**Creating an event headless does not work, and the reason is not a missing piece of wiring — it is
that Delacor never built one.** Measured 2026-08-31, in this order.

### 6.1 What does work

`Module Info` — the 19-field, mostly-refnum cluster `Script New Event.vi` wants — is obtainable.
`DQMH New Event.lvlib:Parse Project for DQMH Modules.vi` produces it over the same
VI-Server-by-path route: 506 ms, `error out` 0, every `ProjectItem` refnum resolved. Its `Project`
control is labelled `Project (unwired: Active Project)`, so leaving it unset selects the active
project.

**The carrier-VI pattern also works, and that was the genuinely uncertain part.** A VI whose front
panel holds one control per argument (`Name` as string, `Gewicht` as double), handed to
`Script New Event.vi` as `Arguments VI`, produced a real `SimpleEvent Argument--cluster.ctl` of
12 678 bytes in the module folder. So Delacor's `Script Arguments Cluster.vi` does read an ordinary
generated VI's panel — the same trick `lvai_create_class` uses for private data.

Two traps on the way there, both worth keeping:

- **`Library Owning App` dies with the parse.** `Parse Project…` is run as a top-level VI and then
  *ends*, and LabVIEW releases the refnums a VI opened when it finishes. By the time
  `Script New Event.vi` uses the cluster, that one field is dead — the failure is
  `Error 1025, Application Reference is invalid`, raised inside `Script Arguments Cluster.vi` while
  opening Delacor's own `Argument--cluster.ctt` template. The eleven `ProjectItem` references and
  the `Library` reference survive, being the live project's own objects. A `Bundle By Name` putting
  the helper's own IDE application reference into that field fixes it.
- **The carrier VI is CONSUMED.** After the first run it was gone from disk entirely — not moved,
  not renamed, absent from a filesystem-wide search. Generate a fresh one for every attempt.

### 6.2 Where it stops

With both traps fixed the run got as far as the argument cluster and then **stopped**: no
`SimpleEvent.vi`, `Main.vi` byte-identical to its backup, the `.lvlib` still at 63 members and not
listing the event. One orphaned `.ctl` on disk, nothing else. The module was rolled back from a
backup and `lvai_describe_project` confirmed `missingItems: []`, `missingFiles: []`.

The cause was not established, because the run outlived the MCP client's request timeout while
LabVIEW kept working — so the `error out` was never read.

**A third attempt, instrumented to write its `error out` to a FILE so the timeout could not take
it, killed LabVIEW.** Same partial result — the argument cluster written, nothing else — and no log
file, because the process died first. NI's `_cur.txt` puts the fault inside
`LV AI Core.lvlibp:VI generator.vi` under `ConvertAIXMLToVI.vi`, i.e. in **AIXML generation, not in
Delacor's code**, with `HeapObjMapImpl.cpp(226)` warnings naming our own low uids. That attempt was
also the one carrying a `Constant` whose `type` spelled out eleven `ref{LV.ProjectItem}` fields —
the same class of input as the settled `OMAutoClasses` crash. `docs/labview-crash-signatures.md`
has the analysis; the practical rule is that **a refnum-typed AIXML constant is risky input**, and
the module route never needs one because it moves such values as variants.

Three attempts, three different failure modes, one dead LabVIEW, and no event. The module was
restored from a backup each time and verified clean.

### 6.3 Why not to keep pushing

**`Create New DQMH Event.vi` has no connector pane at all** — 136 bytes of AIXML, no controls, no
indicators. Like the module menu VIs (§1) it is a dialog launcher.

And the project provider under `resource\Framework\Providers\ZE_DQMH\` — which *is* readable,
unlike Delacor's scripting VIs — settles it. `CreateEvent_Item_OnCommand.vi` calls exactly three
things: `mxLvGetItemRef.vi`, **`FP.Open` with `Activate? = true`**, and **`Run VI` with
`Wait Until Done = false`**. It pre-selects a `Ring` control named `Module` and hands the dialog to
the user. That is the whole of it.

**So the officially supported path for creating an event is the dialog, on both routes Delacor
ships.** `Script New Event.vi` is internal implementation reached only from a running dialog that
has already built state around it; driving it directly is fighting an API that was never a
contract, and a DQMH upgrade may change it without notice. Modules are different — there
`Script New Module.vi` takes plain values and works end to end (§5).

The practical answer for a caller who wants an event: **Tools ▸ DQMH ▸ Create New DQMH Event**, or
the project's right-click menu, which the provider pre-fills with the module.

## 7. What is not reachable this way

`Validate DQMH Module.vi`, `Rename DQMH Module.vi`, `Rename`/`Remove`/`Convert DQMH Event.vi` are
all **menu VIs with no connector pane** (§1), so there is nothing to drive. Whether an underlying
scriptable VI exists for each has not been checked — `_DQMH Rename Module\`, `_DQMH Remove Event\`
and `_DQMH Validate Module\` exist as directories and are the place to look. Do that with
`lvai_vi_terminals` before assuming either way.

## 8. Summary

| Capability | Route | Status |
|---|---|---|
| List module types | `Get Module Type Info.vi` over VI Server | **measured**, 121 ms |
| Create a module | `+ Script New Module.vi` | **measured end to end**, 30–43 s |
| Find modules in a project | `Parse Project for DQMH Modules.vi` | **measured**, 506 ms |
| Create an event | dialog only — see §6 | **not scriptable**; carrier VI proven, the rest stops |
| Validate / rename / remove | menu VIs have no pane; look in `_DQMH *\` | **not investigated** |
