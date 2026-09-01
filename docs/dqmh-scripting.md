# Scripting DQMH: creating modules and events from outside the IDE

DQMH (Delacor Queued Message Handler) ships a set of scripting VIs that build modules and events.
This note records **whether and how they can be driven through this server's interfaces**, measured
on 2026-08-31 against DQMH "Delacor QMH Event Scripter 5.0.0.112" under LabVIEW 2026 32-bit.

Read together with [dqmh-patterns.md](dqmh-patterns.md), which describes what a finished module
*looks like*; this note is about how to *make* one.

The short answer: **yes for modules, and yes for events** — both proven end to end. Modules go
through `Script New Module.vi` directly (§5). Events cannot: §6 shows why driving
`Script New Event.vi` from a helper is structurally impossible, and how the supported route is to
drive Delacor's own dialog — which works, at the cost of one synthesised keystroke, because its
OK button is a latched boolean that VI Server may not write.

## 1. The layout: menu VIs versus scripting VIs

Under `<LabVIEW>\project\Delacor\DQMH\` there are two kinds of directory, and only one of them is
callable:

| Directory | Contents | Callable? |
|---|---|---|
| `Event\`, `Module\`, `Testing Tools\`, `Real-Time Tools\` | the **Tools-menu entry points** — `Add New DQMH Module.vi`, `Create New DQMH Event.vi`, … | **No.** These are dialog launchers |
| `_DQMH New Module\`, `_DQMH New Event\`, … | the **implementation**, including `Script New Module.vi`, `Script New Event.vi`, `Parse Project for DQMH Modules.vi` | **Yes** |
| `_support\` | shared helpers | — |

The `.txt` file in each menu directory (`Module\Module.txt`) is the menu ordering, not an API.

**CORRECTED 2026-08-31. This section previously claimed the menu VIs "have no terminals at all",
citing 135–141 byte exports, and used that to conclude they were unscriptable. That was wrong, and
the way it was wrong is worth more than the claim.**

A first export of `Module\Add New DQMH Module.vi` returned 135 bytes — the `<VI>` element and a
description, no controls, no diagram — and `lvai_vi_terminals` duly reported
`errorKind: noTerminalsFound` with the message that such an export means the VI is
"password-protected or otherwise withheld". Re-exported later with `refresh=true`, the same file
gives a full polymorphic terminal listing, and `Event\Create New DQMH Event.vi` goes from 136 bytes
to **80 361 bytes** with its whole diagram and fourteen front-panel controls.

**An empty AIXML export is not proof that a VI is locked.** The likely cause here: those first
exports ran at the very start of a session, with no project open and none of the DQMH libraries in
memory, so LabVIEW could not resolve the hierarchy and returned the bare shell — and
`lvai_convert_vi_to_aixml` **cached that result**, freezing the wrong answer for every later read.

Two rules follow, and they generalise well past DQMH:

- **A suspiciously small export deserves one `refresh=true` before any conclusion is drawn from
  it**, especially for a VI belonging to a library that may not be loaded.
- **Open the project first.** Anything that reads a VI belonging to a framework reads it better
  once that framework is in memory.

The genuinely locked VIs are a different set — `Script New Module.vi` and its siblings under
`_DQMH *\` still export their controls with no `<Diagram>` even on a refresh (§2).

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

### 6.2a The real obstacle, settled: EVERY refnum in `Module Info` dies with the parse

Instrumenting the helper to write its `error out` to a **file** removed the blindness — the run
outlives the MCP request timeout, but the file does not. Two runs then bracket the problem exactly:

| `Library Owning App` set to | how far it got | error |
|---|---|---|
| the **IDE's** application reference (ours, still open) | past the template, argument cluster **written** | `1055` in `Save VI and Add to Library.vi` |
| **`My Computer`**, read from the parse VI's own output | not even the template | `1025` at `Open VI Reference` in `Script Arguments Cluster.vi` |

Read together these settle it. `My Computer` is *also* an output of the finished parse VI, so
substituting it made things **worse**, not better — and the 1055 in the first row is the `Library`
field failing for the same reason one step later. **`Parse Project for DQMH Modules.vi` is run as a
TOP-LEVEL VI and LabVIEW releases the refnums a VI opened when it stops.** All thirteen refnums in
`Module Info` are dead by the time `Script New Event.vi` touches them; the earlier note that the
`ProjectItem` and `Library` references survive was wrong.

Only **one** of the thirteen can be replaced — the application reference, because the helper holds
its own. The other twelve cannot:

- **`LVLibrary.Open` does not exist.** Probed 2026-08-31 on `{LV.Application}`, the way
  `LVClass.Open` was found: `Invoke Node: Invalid method`. (Safe to probe — a wrong *method* is
  rejected cleanly; it is a wrong *class* that provokes the `OMAutoClasses` crash.)
- The VI Server catalogue carries no library or project-item opener either.

**The only route that would work is running the parse as a SUBVI of the helper**, so its refnums
belong to a hierarchy that is still executing. That needs `Call By Reference Node`, which AIXML
does not document and whose strictly-typed VI refnum AIXML cannot express anyway.

So this is a structural dead end with the tools available, not a missing wire. That is a different
and much firmer statement than "it stops", which is all the earlier attempts could say.

**An earlier attempt, before the file logging was in place, killed LabVIEW.** Same partial result — the argument cluster written, nothing else — and no log
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

`Create New DQMH Event.vi` **is readable after all** (§1 — the 136-byte export was a cached
artefact), and reading it shows exactly why a helper cannot substitute for it. Its diagram calls,
in order:

```
Parse Project for DQMH Modules.vi      <- as a SUBVI
Preflight Main VI.vi
Show Arguments Window.vi
Determine Existing Argument Typedef Path.vi
Verify Event Names.vi
Script New Event.vi                    <- as a SUBVI, same hierarchy
Close Scripting References.vi
```

**Parse and Script are subVIs of one running VI.** That is precisely what keeps the thirteen
refnums alive across them, and precisely what a helper driving each as a separate top-level `Run VI`
cannot reproduce (§6.2a). It also runs `Preflight Main VI.vi` and `Verify Event Names.vi` first —
preparation steps a direct call skips entirely.

The dialog carries fourteen front-panel controls, including `Module`, `Event Type`, `Event Name`,
`Event Description`, `Add Tester Button`, `Custom Enqueue VI`, `Broadcast Argument Source`, and
`OK` / `Cancel`, behind an Event Structure.

The project provider under `resource\Framework\Providers\ZE_DQMH\` does no more than launch it:
`CreateEvent_Item_OnCommand.vi` calls `mxLvGetItemRef.vi`, **`FP.Open` with `Activate? = true`** and
**`Run VI` with `Wait Until Done = false`**, pre-selecting the `Module` ring.

**So the entry point Delacor supports is this dialog VI, not `Script New Event.vi`.** Whether it can
be driven headless — set the controls over VI Server, then fire `OK` through a `Value (Signaling)`
property so the Event Structure sees it — is **untested**, and `Show Arguments Window.vi` is a second
dialog that would have to be handled too. Modules are different: `Script New Module.vi` takes plain
values and works end to end (§5).

The route that works today: **Tools ▸ DQMH ▸ Create New DQMH Event**, or the project's right-click
menu, which the provider pre-fills with the module.

## 6.3 Driving the dialog headless: three of four steps work

Measured 2026-08-31 against a running `Create New DQMH Event.vi`. Each step succeeded on its own,
with `error out` 0 and a read-back proving it; the sequence as a whole did not produce an event.

| Step | How | Result |
|---|---|---|
| Set the text and numeric fields | `Ctrl Val.Set` by control **label** | **works** — `Event Name` and `Event Description` read back verbatim |
| Put controls in the Arguments Window | `Select All` + `Copy Selection` on a carrier panel, `Paste Selection` on the dialog's | **works** — the window's `Controls[]` then reported `Name`, `Gewicht` |
| Fire OK | `Value (Signaling)` on the button's reference | **write succeeds** — label read back as `OK` |
| The event itself | Delacor's scripting inside the dialog | **did not happen**; the module was byte-for-byte unchanged |

### The transferable finding: moving controls between panels

`{LV.Panel}` has **no** add-a-control method and `{LV.Application}` offers only
`New LabVIEW Document`, so creating a control on someone else's front panel looks impossible. It is
not: **`Select All` → `Copy Selection` on the source panel, then `Paste Selection` on the target**
moves controls with their labels and types intact, which is all that scripting reading a panel
needs. Combined with an AIXML-generated **carrier VI** — the pattern `lvai_create_class` already
uses for private data fields — that means an argument list can be *authored* as AIXML and then
placed into a dialog that otherwise only accepts hand-dragged controls. `Paste Selection`'s `Pos`
and `Pane` may be left unwired.

This is worth remembering well beyond DQMH.

### Reading a Ring's entries: the cast AIXML *can* express

`Strings []` on `{LV.Ring}` is refused for a reference taken out of `Controls[]` — those are
**generic** control references and the property does not exist on the generic class. The fix is
**`To More Specific Class`**, which is an ordinary AIXML node: its `target class` input takes a
refnum constant of the wanted class (`type="ref{LV.Ring}"`), and the downcast reference then carries
`Strings []` normally.

An earlier revision of this note concluded the entries "could not be read" and fell back on
deriving the index — which is how a run came one button press from scripting into the wrong module.
**Read the list.**

### THE RING ORDER IS NOT WHAT ANYONE WOULD GUESS

Measured on the same project, dialog launched programmatically:

| index | entry |
|---|---|
| 0 | `DQMHdemo.lvlib` |
| 1 | `FirstClone.lvlib` |
| 2 | `Korrekt.lvlib` |
| 3 | `<Select a Module>` |

**The placeholder is LAST, not first.** Two separate assumptions that index 0 was the placeholder —
and therefore that index 1 was the first module — were both wrong, and `Step 6` proved it: with the
ring set to 1 the dialog read *"The new event will be created in FirstClone.lvlib."* The order also
matches neither the project order nor `Parse Project for DQMH Modules.vi`'s output.

**`Step 6` is the verification, and it is free.** That indicator names the target module in words,
and the dialog rewrites it on a module change — so setting the ring through `Value (Signaling)` and
then reading `Step 6` turns an unverifiable index into a checkable fact. With index 0 it read
*"…created in DQMHdemo.lvlib."*

**The dialog needs a round trip to react.** Reading `Step 6` in the same helper run that wrote the
ring returns the OLD text — the write and the Event Structure's response are not synchronous. Read
it in a *separate* tool call.

### The Arguments Window on screen is a TEMPORARY COPY

`Show Arguments Window.vi` does not display `DQMH Arguments Window.vi`. It displays a copy, whose
window title reads:

```
DQMH Arguments Window [lvtemporary_95526.vi] Front Panel on firstDQMH.lvproj
```

The number changes per invocation and the copy has **no file on disk**. Pasting into the template on
disk therefore succeeds, reports the controls back, and changes nothing the dialog will ever read —
the window on screen stays empty. This wasted several rounds and produced a confident wrong
diagnosis (that `Paste Selection` had knocked the window out of its run, since the template read
back as `Idle` — of course it did, it was never the running window).

**Address it by NAME.** `Open VI Reference`'s `vi path` input is polymorphic and accepts a string VI
name for anything in memory. Do **not** run the name through `String To Path`: that makes it a
relative path and the call answers `Error 1445` naming a file beside the helper. Wiring the string
straight in works — pasted into `lvtemporary_95526.vi`, the window's `Controls[]` returned
`Name`, `Gewicht`, and the controls appeared on screen.

Finding the name at run time is the open piece; the title bar shows it, and
`{LV.Application}` `All VIs In Memory` is the obvious place to look.

### Where it stops, and this one is a hard wall: the OK button is LATCHED

With the module verified through `Step 6`, the name and description read back, and the argument
controls in the real window, `Value (Signaling)` on OK still answers **`Error 1193`**.

The cause is not sequencing, timing or state. Measured on the button itself:

```
Label.Text        = "OK"
Mechanical Action = 4   -> Latch When Released
```

**LabVIEW refuses `Value` and `Value (Signaling)` on a latched boolean.** A latch's value belongs to
the run-time between reads, so a property write has nowhere to put it. `Error 1193` is exactly that
refusal. The same call succeeds on the `Module` **ring** in the same dialog and the same execution
state, which is what isolates the cause to the control's mechanical action rather than to anything
about the dialog.

So **VI Server cannot press this button**, and no amount of ordering will change it.

### The synthesised click — and with it the route works end to end

An OS-level click does press it, and the coordinates need not be guessed: **VI Server supplies
them.** Three properties are enough, and the arithmetic is the only subtle part —

```
{LV.VI}      Front Panel Window:Panel Bounds   -> panel area in SCREEN coordinates
{LV.VI}      Front Panel Window:Origin         -> how far the panel is scrolled
{LV.Control} Position:Left / :Top              -> control position within the panel
{LV.Control} Bounds:Area Width / :Area Height  -> to aim at the middle

x = PanelBounds.Left + (Position.Left - Origin.Horizontal) + Width  / 2
y = PanelBounds.Top  + (Position.Top  - Origin.Vertical  ) + Height / 2
```

**The Origin term is not optional.** This dialog is taller than its window and scrolls; measured
here, `Origin` was `(40, -10)` — dropping it would have missed by 40 px horizontally.

Measured 2026-08-31: `PanelBounds = (172, 148, 782, 687)`, `Origin = (40, -10)`, OK at
`(373, 496)` sized `75 x 23` → click at **(542, 665)**, which falls inside the panel bounds as a
sanity check. `SetForegroundWindow` on the dialog, `SetCursorPos`, then `mouse_event` down/up.

> **SUPERSEDED — see §6.7.** This is how the button was first pressed, and it works, but the
> coordinates are unnecessary: `Key Focus` plus one SPACE does the same thing with three fewer
> properties and no arithmetic to get wrong. The helper that computed these coordinates has been
> deleted. The paragraph is kept because the geometry, and the `Origin` term in particular, is the
> reason the shortcut was not obvious.

**And that completed it.** `SimpleEvent`, a Request on `DQMHdemo` with arguments `Name` (string) and
`Gewicht` (double), was created with no human interaction beyond the initial instruction:

| check | result |
|---|---|
| new files | `SimpleEvent.vi`, `SimpleEvent Argument--cluster.ctl` |
| library members | 63 → **65**, both registered |
| `Main.vi` | changed, +3 496 bytes — the MHL frame |
| `Test DQMHdemo API.vi` | changed — the tester button |
| `Request Events--cluster.ctl` | changed — the new event refnum |
| terminals | `Name` [string], `Gewicht` [double], both `required` |
| argument cluster | `cluster{string.Name,double.Gewicht}` |
| VI description | `"Liebe Welt das ist ein Text"` |
| `lvai_describe_project` | `errorCode 0`, `missingItems []`, `missingFiles []` |

**So events ARE scriptable — through the dialog, not through `Script New Event.vi`.** The honest
caveat: one step of the chain is synthesised input, which depends on the dialog being frontmost.
Everything before it is ordinary VI Server and verifies itself. (That step became a keystroke
rather than a click — §6.7.)

### Why it still did not finish

`error out` 0 from the `Value (Signaling)` write means the property was written, **not** that the
Event Structure ran its case. The module never changed, and NI's log for that window carries three
minidumps whose VI call stacks all sit in **our** `VI generator.vi` under `ConvertAIXMLToVI` /
`ValidateAIXML` — the `HeapObjMapImpl.cpp(226)` signature of §  in
`docs/labview-crash-signatures.md`, not anything of Delacor's. Whether the dialog was killed
mid-scripting or never received the event is **not established**.

What is established: the three mechanical steps are available and measured, so the remaining
unknown is narrow. What is not: that this route works end to end. Do not present it as one.

### 6.6 Reproduced, and what the second run added

`SecondEvent` was created the same way on the same module immediately afterwards, following the
written procedure rather than the session that produced it. All seven steps ran clean, and three
documented traps reproduced exactly: the ring order with the placeholder LAST (`0` DQMHdemo,
`1` FirstClone, `2` Korrekt, `3` `<Select a Module>`), `Step 6` still stale inside the writing call
and correct on the next one, and `Origin` again `(40, -10)`.

Two things the second run established that the first could not:

- **The `lvtemporary_*` name can be found without a human.** It was read out of the window title by
  enumerating LabVIEW's visible windows — `DQMH Arguments Window [lvtemporary_961995.vi]` — where
  the first run had it from a screenshot. So no step of the chain needs a person to read the screen.
- **Before the project is closed, the `.lvproj` file is not evidence of what was adopted.**
  `lvai_describe_project` reported ten helper VIs in the project while the file on disk listed none;
  `lvai_close_active_project` then wrote six of them into it. The order must be **close, then read
  the file, then strip** — reading first and finding nothing is a false clean bill.

Result: `SecondEvent.vi` with `Name` [string] and `Gewicht` [double], the `.lvlib` from 65 to 67
members, `Main.vi`'s export from 72 512 to 75 654 bytes carrying both the EHL case and an MHL frame
labelled with the description, and `missingItems`/`missingFiles` empty.

### 6.7 The click was unnecessary: Key Focus plus SPACE

The OK button being Latch When Released was read as "VI Server cannot press it, so compute its
screen position and click". The first half is right and the second was a detour. Measured
2026-09-01 while creating `ThirdEvent`:

| attempt | result |
|---|---|
| write `Mechanical Action` = 0 to disarm the latch, then signal | **`Error 1073`** — not allowed while the VI is running. `MechAction` is U32 and reads 4 |
| write `Key Focus` = true with the dialog behind other windows | `error 0`, and the read-back says **false**. A silent no-op |
| `SetForegroundWindow`, then `Key Focus` = true | read-back **true** |
| focus in one call, SPACE from a *separate* PowerShell invocation | nothing happens — starting the process moved the foreground away |
| foreground **and** SPACE in one invocation | **the event is scripted** |

So the press is: `lvdqmh_dlg_keyfocus.xml` sets `Key Focus` on control index 10 and reads it back,
then one PowerShell script foregrounds the window and sends `keybd_event` VK_SPACE down/up.

**Two rules, both of which cost a failed attempt:**

- **Always read `Key Focus` back.** It returns `error 0` when it did nothing, and a keystroke then
  goes wherever the focus actually is.
- **Foreground and keystroke belong in the same OS-level step.** Splitting them across two tool
  calls loses the foreground to the new process.

The coordinate route — `PanelBounds.Left + (Position.Left - Origin.Horizontal) + Width/2` and the
same for y — is gone from `scripts/`. It worked, and it read three more properties, broke silently
on a scrolled panel, and moved the user's mouse cursor. `lvdqmh_btnpos.xml` was deleted rather than
kept as a fallback, because a fallback nobody exercises is a fallback nobody can trust.

The keystroke is still synthesised input: the dialog must be frontmost, so this is no more
unattended-safe than the click was. It is simply smaller and cannot be thrown off by scrolling.

### 6.8 `Value (Signaling)` re-tested properly — and why the first test was inconclusive

The claim "VI Server cannot press the button" rested on a run that wrote `Value (Signaling)` on
**`{LV.Control}`**, whose value terminal is a variant. That returned `error 0` and the dialog did
nothing — a silent no-op, which is weak evidence: it looks the same as a value that was accepted and
ignored. Re-tested 2026-09-01 on `{LV.Boolean}` via `To More Specific Class`, with a real boolean:

| class written | value | result |
|---|---|---|
| `{LV.Control}` | variant | `error 0`, no effect — **silent no-op** |
| `{LV.Boolean}` | boolean | **`Error 1193`** on the write node, `Property Name: Value (Signaling)` |

The cast itself succeeds (`cast error` 0), so this is LabVIEW refusing the property on a latched
control, not a class mismatch. **The refusal is now measured rather than inferred**, and the generic
route is exposed as the misleading one: a variant handed to `{LV.Control}` is dropped without
complaint. When probing whether a property works, use the SPECIFIC class — a generic write that
reports success may have done nothing.

**Enter does not work either.** `VK_RETURN` to the focused dialog changed nothing; OK is not the
panel's default button. Only SPACE on the focused control presses it.

**And `Key Focus` is flakier than §6.7 suggested.** Across this run it took three attempts:
foreground → focus wrote `false`; foreground again → `false`; foreground again → `true`. Nothing
about the sequence differed. So the rule is not "foreground once, then focus" but:

> Set `Key Focus`, **read it back, and if it is false, foreground the window and try again.** Only
> send the keystroke once the read-back says true.

Without that loop the keystroke goes to whatever else holds focus, silently.

### 6.9 Productised as `lvai_dqmh_new_event` — and what only in-process code hits

The sequence above is fixed, so it became one tool: `lvai_dqmh_new_event` takes a module name, an
event name, a JSON list of typed arguments, and runs the whole chain. Measured 2026-09-01 creating
`FifthEvent` with three arguments: **3.0 s against roughly two minutes by hand**, and the saving is
almost all model latency rather than LabVIEW time.

**Three things broke that had never broken in the hand-driven route**, and each is worth knowing
before automating anything else that drives a GUI:

- **The waits that were free are gone.** The dialog parses the whole project before its module ring
  has entries. Driven by hand that took no code, because a tool call's round trip is seconds — and
  `lvdqmh_dlg_start.xml` says in its own description that it therefore needs no Wait node. In-process
  the first read came back `[""]` and the tool reported the module missing. It now POLLS the ring,
  because the parse takes as long as the project is big.
- **An empty `value` is not a universal default.** The carrier generator emitted
  `<Control type="double" … value=""/>` and got `Error 53, Unrecognized or unsupported attribute set
  in Control with UID 11` — which names the control, not the attribute, and reads like a bad type.
  Numerics need `0`, booleans `false`, only strings and paths take empty. The hand-written carriers
  had `value="0"` all along; the rule was in the examples and did not survive the move into C#.
- **`SetForegroundWindow` does nothing from a background process.** Windows grants the foreground
  only to a process that already has it, and an MCP server never does. From PowerShell it had always
  worked, because the terminal itself was frontmost. The tool's first run logged
  `windowWasFrontmost: false`, sent SPACE anyway, **returned `ok: true` and created nothing**. Two
  fixes: `AttachThreadInput` to the current foreground thread for the duration of the call, which is
  the documented way around the restriction; and no keystroke at all unless the window is confirmed
  frontmost — otherwise it types into whatever the user has in front of them.

That third one is the important one, and it is a process failure rather than a Windows quirk: the
tool had the evidence in hand and reported success anyway. `docs`-wide the rule already existed —
never report success from an empty answer — and it has to hold for a tool's own self-report too.

### 6.10 What the tool's own test runs settled: a BROADCAST gets no case frame at all

Eight events now exist on `DQMHdemo`, five of them built by the tool. Three of the runs were made to
probe edges rather than to be useful, and two of them corrected written rules.

**The verification rule "`Main.vi` changed - that is the MHL frame" is a REQUEST rule.** It was
written from five Requests and does not hold for a Broadcast. Counted 2026-09-01 in `Main.vi`'s AIXML
export, one row per event, by the elements that name it:

| event type | elements in `Main.vi` | what they are |
|---|---|---|
| **Request** (7 of them) | **6** | an EHL `CaseFrame` labelled `Another Module called the "<Event>" API Method.`; an MHL `CaseFrame` labelled with the **event description**; a `FreeLabel`; three argument-cluster `Constant`s |
| **Broadcast** (`MessungFertig`) | **1** | one `Call` to `DQMHdemo.lvlib:MessungFertig.vi`, `uid_parent="root"`, **every input unwired** |

The Broadcast's single node carries its own explanation:

```
comment="#CodeNeeded\0A1. Drop this VI wherever you need to broadcast the
         &quot;MessungFertig&quot; message.\0A2. Delete or edit this label when done."
```

So **a Broadcast is not wired in by scripting, and cannot be**: nothing handles it, because a
broadcast is fired *by* the module and only its author knows when. DQMH drops the call loose on the
root diagram with a `#CodeNeeded` marker and leaves the placement to a person. `Main.vi` does change
- so the check still passes - but what it gained is a stub, not a frame. Verify a Broadcast by
finding that `Call`, and **say in the report that the module does not fire it yet**; a reader who
takes "`Main.vi` changed" as "the event works" will look for a broadcast that never happens.

`#CodeNeeded` is worth knowing as a convention in its own right: it is how DQMH marks code it
scripted but could not finish. Grep an export for it before declaring a scripted module complete.

**Two argument-count edges, both fine.** Neither had been exercised before, and the empty one is the
kind of path that is usually special-cased wrongly:

| event | arguments | result |
|---|---|---|
| `Abschlusstest` | `[]` | 1 input, 1 output - just `error in (no error)` and `error out`. **The `Argument--cluster.ctl` is still created**, empty, and the `.lvlib` still gains two members |
| `Testchen` | one `string` | `hallo` [string] `required`; `.lvlib` 77 -> 79 |

So the `.ctl` is unconditional: two files and two members per event, whatever the argument list. A
missing `.ctl` is a failure even for an event with no data.

### 6.11 The other two event types need MORE than the dialog was being told

`Request` and `Broadcast` were driven end to end (§6.5–§6.10). The other two were assumed to be the
same chain with a different index, and they are not. **`Script New Event.vi`'s connector pane is the
contract**, and read on 2026-09-01 it has three type-dependent inputs where the tool supplied one:

| terminal | type | who needs it |
|---|---|---|
| `Arguments VI` | `ref{LV.VI}` | all four — the request/payload arguments |
| **`Reply Payload VI`** | `ref{LV.VI}` | a **second** arguments VI, for the reply data |
| **`Round Trip (Broadcast)`** | `string` | Round Trip — the name of the broadcast half |

All three are `required`. So a Round Trip driven without the third gets an **unnamed broadcast half**,
and either reply-carrying type driven without the second gets an **empty reply cluster** — and both
script with no error from Delacor. Nothing reports it. That is why `lvai_dqmh_new_event` now *refuses*
the combinations rather than warning: `replyArgumentsJson` on a Request or Broadcast, a
`roundTripBroadcastName` on anything but a Round Trip, and a Round Trip without one.

`Event Type` here is a **real enum** — `uint16{Request,Broadcast,Request and Wait for Reply,Round
Trip}` — unlike `Module Type`, which is a bare `uint16` whose meaning comes from a runtime catalogue
(§5). So for events the index order IS in the pane and matches the tool's list exactly. `Module Type`
in the same pane shows only `{Singleton,Cloneable}` against the catalogue's four, which is the second
reason never to carry a module-type index.

**There are TWO argument windows, and the second one is HIDDEN.** The dialog calls
`Show Arguments Window.vi` **once** and it returns both:

```
outputs="Arguments Window:498.Arguments Window,
         Reply Payload Window:498.Reply Payload Window,
         error out:498.error out"
```

Enumerated with a Request-and-Wait event set up and waiting:

```
visible  [Create New DQMH Event]
visible  [DQMH Arguments Window [lvtemporary_419382.vi] Front Panel on firstDQMH.lvproj/My Computer]
HIDDEN   [DQMH Reply Payload Window [lvtemporary_594745.vi] Front Panel on ...]
```

The reply window is hidden **because of how we drive the dialog, not because of the event type**:
`lvdqmh_dlg_fill3` writes `Event Type` with `Ctrl Val.Set`, which fires no event, so the dialog's own
event case — the thing that would reveal the window — never runs. Hidden makes no difference to VI
Server; it only means the window has to be found by an enumeration that does **not** filter on
`IsWindowVisible`, which is what `Win32.AllTitles` is for.

And **do not signal the text fields to fix that**. `Value (Signaling)` on `Event Type` would run the
dialog's event case and rebuild both argument windows, discarding anything already pasted. The ring
is the one control that must be signalled, and it is signalled first, before any paste.

The two titles cannot be confused — `DQMH Reply Payload Window` does not contain `Arguments Window` —
but that was checked rather than assumed, because the request matcher would otherwise have pasted the
request fields into the reply cluster and reported success.

For completeness, the dialog's front panel as measured, which is the list to reach for when another
type needs something new:

```
Round Trip (Request)   Round Trip (Broadcast)   Event Type   Event Name   Event Description
Module   Existing Request   Broadcast Argument Source {New Argument,Existing Argument}
Enqueue Message VI {Standard,Custom}   Custom Enqueue VI   Add Tester Button   OK   Cancel   Help
```

**Both reply-carrying types were then created end to end.** Two claims written into this file
earlier the same day were wrong and are retracted below, because the way they were wrong is the
useful part.

**`Event Type` sits at `Controls[]` index 1, and signalling it reveals the reply window.** Harvested
with `lvdqmh_dlg_probe`, which also confirmed the two indices the tool already carried:

```
 0 Module (Ring)          1 Event Type (Ring)      2 Add Tester Button      3 Enqueue Message VI
 4 Existing Request       5 Custom Enqueue VI      6 Broadcast Arg Source   7 Event Name
 8 Round Trip (Broadcast) 9 Event Description     10 OK                    11 Cancel
12 Help                  13 Round Trip (Request)  14 Step 6                15 Context Help
```

`Value (Signaling)` on index 1 turns the Reply Payload Window from `HIDDEN` to `visible` in one
call. `Ctrl Val.Set` on the same control does not, and neither does re-signalling the module ring.
The tool now does this for the two reply-carrying types only, before any paste, and checks the
control's own `Label.Text` so a shifted `Controls[]` order is caught at the write rather than three
steps later.

**ROUND TRIP: four files, and the reply lands on the BROADCAST half.**

| check | result |
|---|---|
| files | **four**: `RoundTripTest.vi`, `RoundTripTestDone.vi`, `… Argument--cluster.ctl`, `… (Reply Payload)--cluster.ctl` |
| `.lvlib` members | 88 to **92**, so **+4** for this type |
| the broadcast half | named exactly as `roundTripBroadcastName` was passed |
| `RoundTripTest.vi` | `Auftrag` in, `wait for reply (T)` in, `error out` and `timed out?` out |
| `RoundTripTestDone.vi` | **`Reply Payload [cluster{double.Ergebnis, cluster{…}.RoundTripTest_error}]`** as an INPUT |

So the reply field is there. A Round Trip answers *through* the broadcast, so its payload is an input
to the broadcast VI, not an output of the request VI — which is why looking only at the request VI
made it seem lost.

**RETRACTED: "a paste into the hidden reply window is accepted and then ignored".** It is not
ignored. `ReplyTest3` was created with the window revealed and its request VI still shows no
`Reply Payload` output, so revealing it changed nothing observable — but the cluster is correct.
`pylv_extract` on `ReplyTest3 (Reply Payload)--cluster.ctl` gives:

```
<TypeDesc Type="NumFloat64" Prop1="0" Format="inline" Label="Istwert" />
```

with `Stabil` beside it. The reply fields reached the cluster in every run.

**RETRACTED: the file-size argument.** "14 208 bytes for a one-field cluster, so my 14 164 must be
empty" was reasoning from a quantity that does not track content:
`RoundTripTest (Reply Payload)--cluster.ctl` is **13 939 bytes** — the smallest of all of them — and
demonstrably carries `Ergebnis`. **A `.ctl`'s size says nothing about its fields**, any more than a
raw byte search does: `Kanal` and `Sollwert` do not appear as ASCII in an argument cluster whose
event plainly has both terminals. Read a `.ctl` with `pylv_extract` and look at `VCTP`, or read the
VI that uses it with `lvai_vi_terminals`. Nothing cheaper is sound.

**What is still open for Request and Wait for Reply**: its request VI carries no `Reply Payload`
output, while DQMH's own `Do Something Else and Wait for Reply.vi` has one
(`cluster{double.Value, error}`, conIdx 2). The cluster file is right, so this is about the connector
pane, not the data. Whether a *scripted* R&W event is simply shaped that way — Delacor's example
comes from the module template, not from the event scripter — is **not settled**. A run driven by
hand through the same dialog produced the same shape, which points at DQMH rather than at this
route, but the hand-driven run's reply fields were not recorded, so it is not proof.

**The OK press needed a SECOND attempt in both runs**, and this is what makes the retry loop worth
having rather than a nicety:

```
focusOk    attempt 1  label OK  focusSettled true
pressSpace attempt 1  skipped: the dialog did not come to the foreground
focusOk    attempt 2  label OK  focusSettled true
pressSpace attempt 2  windowWasFrontmost true       <- scripted
```

Key focus settles on the first try; the *foreground* does not. Refusing to send the keystroke
without a confirmed foreground, and trying again, is the whole difference between a run that works
and one that types a space into whatever the user has in front of them.

### 6.12 `GetForegroundWindow() == 0` means the desktop is locked

The `ReplyTest` run filled the dialog correctly, confirmed the module by name, pasted both arguments —
and then reported `Key Focus never settled` after five attempts. §6.8 already knew focus was flaky and
prescribed a retry loop, so the message looked like that same flakiness and sent the next step at
LabVIEW. It was not:

| probe | result |
|---|---|
| `SetForegroundWindow` on the dialog, with `AttachThreadInput` | **false** |
| `GetForegroundWindow()` | **0** |

**A null foreground window means no window can hold the foreground** — a locked workstation, a running
screensaver, a disconnected session. The keystroke route cannot work there at all, and no number of
retries changes that. `lvai_dqmh_new_event` now checks this **before starting the dialog** and answers
`errorKind: desktopNotInteractive` in one sentence, rather than driving six steps and stopping one
short with a message about LabVIEW.

This is the sharpest form of the limitation the tool has always reported: not "needs the dialog
frontmost" but "needs a desktop that has a foreground at all".

### 6.13 When `lvai_open_file` answers Error 7 for everything

Getting a project active for the run above failed in a way worth writing down, because the error names
the wrong cause:

```
Error 7 ... OpenFile.vi
LabVIEW: (Hex 0x7) File not found. The file might be in a different location or deleted.
```

The file was there. Three things were established before believing anything:

- It is **not the project**: a freshly written six-line minimal `.lvproj` in a scratch folder got the
  same Error 7.
- It is **not LabVIEW being unresponsive**: `lvai_describe_project` read the *same* project in the
  same second with `errorCode 0` and `missingFiles: []`.
- It is **not the line endings**. `sed -i` on the `.lvproj` while stripping adopted helpers had turned
  every CRLF into LF — real, and worth repairing since every other `.lvproj` on the station is CRLF —
  but restoring them changed nothing. Recorded because the theory was stated before it was tested.

**What worked was the gesture a person would use: open the `.lvproj` through its file association**
(`Start-Process 'C:\temp\...\x.lvproj'`). Windows hands the file to the already-running LabVIEW, which
opens it and makes it active — after which the dialog started, the ring listed all three modules and
every step ran. So a wedged `OpenFile.vi` is not a dead end.

The cause of the Error 7 is **not established**. What is: it is universal while it lasts, it does not
implicate the file, and there is a way around it.

**And strip helpers from a `.lvproj` with a tool that preserves CRLF.** `sed -i` does not. Nothing
broke that has been traced to it, but a project file whose every line differs from its last committed
version hides the one line that was meant to change.

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
| Create a **Request** or **Broadcast** event | `Create New DQMH Event.vi` over VI Server + one SPACE keystroke — §6.9 | **measured end to end**, 2.6–3.0 s via `lvai_dqmh_new_event`; needs a desktop with a foreground |
| Create a **Request and Wait for Reply** event | same, plus the Reply Payload Window — §6.11 | **measured end to end**: three files, +3 members, `wait for reply (T)` in, and the reply cluster carries the pasted fields. Its request VI exposes no `Reply Payload` output — open, see §6.11 |
| Create a **Round Trip** event | same, plus `roundTripBroadcastName` — §6.11 | **measured end to end**: four files, +4 members, the broadcast half named as passed, its `Reply Payload` input carrying the reply field |
| Validate / rename / remove | menu VIs have no pane; look in `_DQMH *\` | **not investigated** |
