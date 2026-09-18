# The Actor Framework

What was measured on 2026-09-17 while building `C:\temp\ActorFW_first`, on LabVIEW 2026 32-bit.
Two halves: the part of an Actor Framework application this toolchain **can** generate end to end,
and one construct it **cannot** — with the evidence for the second, because it looks like several
other things first and cost most of a session to pin down.

## 1. Where it lives, and what a `Call` target is spelled

The framework is `vi.lib\ActorFramework\`, and every class in it belongs to the library
`Actor Framework.lvlib` (`NI.Lib.ContainingLibPath` = `/<vilib>/ActorFramework/Actor Framework.lvlib`).
So a `Call` target carries the library qualifier, which is the trap §9 of `lvai_aixml_reference`
already records for OpenG — the palette prints the bare name and the bare name is refused:

| what you want | `target=` |
|---|---|
| launch the top-level actor | `Actor Framework.lvlib\3AActor.lvclass\3ALaunch Root Actor.vi` |
| launch a nested actor | `Actor Framework.lvlib\3AActor.lvclass\3ALaunch Nested Actor.vi` |
| stop an actor | `Actor Framework.lvlib\3AStop Msg.lvclass\3ASend Normal Stop.vi` |
| send any message | `Actor Framework.lvlib\3AMessage Enqueuer.lvclass\3AEnqueue.vi` |

`lvai_palette_index` answers `Actor` with 45 hits, but the **examples** index does not list NI's own
AF examples at all — 6 hits for "Actor Framework" and every one of them MGI's. NI's are on disk at
`examples\Design Patterns\Actor Framework\Actor Framework Fundamentals.lvproj` (the Coffee Shop) and
`examples\Object-Oriented Programming\Actors and Interfaces`. Read them off the filesystem; do not
conclude from the index that they are absent.

**An apostrophe in a terminal name needs no escape.** `Launch Root Actor.vi`'s output really is
`Actor's Enqueuer`, and inside a double-quoted `outputs=` attribute the `'` is literal. Writing
`Actor\5C's Enqueuer` puts a backslash in the name and the wire is refused.

## 2. The shape of an actor and a message, from NI's own export

`Message.lvclass:Do.vi` is the contract every message class overrides. Measured pane — and the
detail that matters is **`Actor out` sits at conIdx 2, not 3**, so an override does NOT use the
house accessor layout:

| terminal | conIdx | connection | type |
|---|---|---|---|
| `Message` | 11 | **dynamic** | the message class |
| `Actor in` | 10 | recommended | `Actor.lvclass` |
| `error in (no error)` | 8 | recommended | error cluster |
| `Actor out` | **2** | recommended | `Actor.lvclass` |
| `error out` | 0 | recommended | error cluster |

Pattern **4815** on the parent, on NI's child and on ours — measured on all three with
`lvai_connector_pane viPath`, identical slot for slot. **An override copies the PARENT's pane, so
ask for the parent's numbers, not the station default and not 4815's style guide.**

The dynamic dispatch terminal may be RENAMED: NI's child calls it `Increase Count Msg` where the
parent says `Message`. The other four names match the parent exactly in NI's child.

NI's `Do.vi` body is worth copying: `To More Specific Class` with its object output unwired feeding
a Case structure on its error, and a SECOND `To More Specific Class` inside the No Error frame with
its error unwired — a trick to avoid copying the Actor object while still passing the original
through if the cast fails.

A message's payload travels in the message class's private data: the Send VI `Bundle`s it into a
class constant and hands that to `Enqueue.vi`; `Do.vi` `Unbundle`s it.

## 3. What generates cleanly — measured, runs

All of this worked first time and is in `C:\temp\ActorFW_first`:

- **A child of `Actor.lvclass` or `Message.lvclass`** via `lvai_create_class` with `parentClassPath`
  pointing into `vi.lib`. `inheritsFrom` came back `Actor Framework.lvlib:Actor.lvclass` and
  `Actor Framework.lvlib:Message.lvclass`, `parentKindsAreComplete: true`.
- **Accessors** via `lvai_create_accessors` — dynamic dispatch, `execState 1`.
- **An ordinary class method** authored in AIXML with `path` stand-ins, made a member with
  `lvai_add_class_method` (`dispatchTerminals` for dynamic dispatch), then pointed at its own
  accessors with `lvai_placeholder_subvi` + `lvai_swap_subvis`. `Counter.lvclass:Increment.vi`
  reads its own `Count`, adds, writes back and appends to a log file: `execState 1`.
- **A plain VI that drives the framework**: `Launcher.vi` builds a Counter, increments it 1/5/10,
  launches it with `Launch Root Actor.vi` and stops it with `Send Normal Stop.vi`. Run through
  `lvai_run_vi_and_read_values`: `Log` = `Count = 1 / Count = 6 / Count = 16`, `error out` clean.

Two mechanics that this build exercised and that are worth restating because they are easy to get
backwards. A **class constant** cannot be authored in AIXML — write a `path` `<Constant>`, give it a
`_name` (which becomes the diagram label), and swap it with `lvai_swap_subvis`' `constantsJson`.
And **one socket on three nodes costs three swap calls**: `socketsLeft` counts `swapsJson` ENTRIES
still in the export, so it read 1, 1, 0 across the three while three, two and one nodes remained.

## 4. THE GAP: a generated OVERRIDE of `Message.lvclass:Do.vi` is `eBad`

**This is the finding.** `lvai_add_class_method` produces a member VI that LabVIEW does not accept
as an override of its parent's method, and the resulting VI is not executable **whatever is on its
diagram**. Because every AF message class must override `Do.vi`, the message half of an Actor
Framework application cannot currently be generated by this toolchain.

The failure is silent everywhere except `lvai_exec_state`:

```
execState 0, eBad
VILoadErr: "VI has an error of type 42000000. The full development version of LabVIEW
             is required to fix the errors."
```

`lvai_add_class_method` answered `ok: true`, `terminalsRetyped: 3`, `dynamicDispatchTerminals: [11]`,
`verifiedOnDisk: true`, `pathStandInsLeft: 0`. `lvai_swap_subvis` answered `socketsLeft: 0` with
correct `callTargets`. `lvai_check_aixml` was clean. The AIXML export of the finished VI is correct.
**Nothing but `lvai_exec_state` disagrees** — the same shape `docs/cold-build-filterbench.md` records
for a missing interface override.

### What it is NOT — four hypotheses, each measured and refuted

Each of these looked right and cost a full regeneration cycle. Recording them so nobody pays twice:

| hypothesis | test | result |
|---|---|---|
| the diagram is wrong (bad wire, the cast, the Case structure) | rebuilt with the Case structure removed, then with **no diagram at all** — a pure pass-through with 0 nodes | **still eBad.** The diagram was never involved |
| the terminal name — we write `error in`, the parent says `error in (no error)` | renamed to match the parent exactly | still eBad |
| `Actor in`/`Actor out` got retyped to the wrong class | read the type space out of the saved file with pylabview: `Label="Actor out"` → `Item Text="Actor.lvclass"`, twice | types are correct |
| `NI.ClassItem.Flags` — NI's working override is `16777344`, ours is `33554432` | edited the `.lvclass` to NI's value, **killed and restarted LabVIEW** for a clean load | **still eBad** |

That last row is the fifth time this repository has been drawn to `NI.ClassItem.Flags` and the fifth
time it was not the answer. CLAUDE.md's rule holds: it is not a dispatch field, and it is not an
override marker either.

**The control is what makes this a finding rather than a guess.** NI's own
`Increase Count Msg.lvclass:Do.vi` was read with the same tool, in the same LabVIEW session, from the
same kind of path: `execState 1`. So `eBad` is a property of our VI, not of how it is being read.

Also measured, and worth knowing before chasing the pane again: `MethodScope` is **1 on ours and 1
on NI's** — access scope is not the difference. `lvai_describe_class` reporting `scope: public` for
that member is reading a different thing and should not be used to settle it.

### What the difference actually is

Extracting both VIs with `pylv_extract` and comparing the block lists:

| | NI's working override | ours |
|---|---|---|
| blocks | …, `LIbd`, `LIfp`, **`LIvi`**, … | …, `LIbd`, `LIfp` — **no `LIvi`** |

`LIvi` is the VI-level link-info block, and pylabview's own parse warning names what is inside NI's:
`List of LinkObjects incorrectly ended with 0 after b'VIPI'`. That is the link to the parent method —
the record that makes the VI an override rather than a second, colliding definition of `Do.vi`.
`ConvertAIXMLToVI` does not write it, and `AddItemFromMemory` + `SetWireRule` do not add it.

This is an inference from a block listing, not a decoded structure — but it is the only remaining
file-level difference after the four refutations above, and it fits the symptom exactly.

### And you cannot dodge it by leaving the override out

`Message.lvclass:Do.vi` carries `NI.ClassItem.MustOverride = true`. Measured: deleting the broken
`Do.vi` and its class entry — leaving a `Message` child with only its two accessors — took
`Read Amount.vi` from `execState 1` to **`execState 0`, "VI has an error of type 8"**. A class that
does not override a must-override method is broken, and **the breakage lands on the other members**,
not on the method that is missing. Same shape as `docs/cold-build-filterbench.md`'s missing interface
override: the VI that reports the fault is never the VI that caused it.

So a `Message` child has exactly two states available to this toolchain today, and both are `eBad`:
with a generated `Do.vi`, and without one. That is why `C:\temp\ActorFW_first` ships no message class
at all rather than a half-built one.

### The route to closing it

`resource\Framework\Providers\LVClassLibrary\NewAccessors\**CLSUIP_CreateOverride.vi**` — the IDE's
own "New → VI for Override…" provider, sitting in the same folder as `CLSUIP_CreateNewAccessor.vi`
that `lvai_create_accessors` already drives. **It is fully scriptable** — measured pane, no dialog:

```
INPUTS   error in (no error)  conIdx 8    Child class  ref{LV.LVClassLibrary}  conIdx 10
         Parent Method        ref{LV.VI}  conIdx 11
OUTPUTS  error out  conIdx 0   NewItemID  string  conIdx 1
         Child class out  conIdx 2        Parent Method out  conIdx 3
```

So this is CLAUDE.md's **fourth interface** — ask whether LabVIEW *compiles* the thing before trying
to write it, and drive NI's own provider when it does.

**Driving it works — measured 2026-09-17.** `scripts/lvai_create_override.xml` opens the child class
with `LVClass.Open` and the parent method with `Open VI Reference`, both in the IDE's application
instance, calls the provider, saves the new member with `Save.Instrument` and then saves the class.
Result on `Increment Msg.lvclass`: `Do.vi` on disk, listed in the class, `NI.ClassItem.Flags`
**16777344** — NI's own value — and **`execState 1`**. Its pane reads back as
`Increment Msg.lvclass:Do.vi` with `Increment Msg` conIdx 11 `dynamic` and class-typed
`Actor in`/`Actor out`. So a correct override *can* be produced by tooling.

Two ordering details that are not optional. The provider leaves the new VI **untitled in memory**;
saving the class before giving it a path answers **Error 1019**, so `Save.Instrument` with an
explicit `Path to saved file` has to come first. And the provider's VI never reaches disk if
anything later fails, so a killed LabVIEW loses it silently — the class file is untouched.

### But the override still cannot be given a generated body

That is the half that remains blocked, and two candidate architectures were tested and **refuted**:

| architecture | test | result |
|---|---|---|
| provider creates the override, then `ConvertAIXMLToVI` writes our diagram over it | converted, killed and restarted LabVIEW, re-read | **the VI stops being a member.** It exports as plain `Do.vi`, its class terminals are back to `path`, and `execState 1` only because a standalone VI with path terminals is trivially valid |
| generate ours, then graft the override link | copied the provider VI's `LIvi` block (the one carrying `VIPI`) into our bundle and rebuilt | **still eBad.** `LIvi` is not sufficient |

**The first result is the one to be careful with.** `execState 1` after that convert looks like
success and is not: it is a green reading about a *different artefact* — a VI that stopped claiming
to be an override. `lvai_exec_state` alone cannot tell the two apart; `lvai_vi_terminals` can,
because a member reports its qualified name and its class-typed terminals. **Check what you
measured, not just that it was green.**

Block-diffing the two files past that point does not help either: the working override carries
`VICD`, `TM80`, `DFDS`, `BNID`, `NUID`, `SUID` and more, and those are compiled-code and data-space
blocks that exist *because* LabVIEW compiled it — consequences of being valid, not causes.

So the blocker is narrower and harder than "establish the link": **nothing available can write a
block diagram into an existing VI.** `ConvertAIXMLToVI` always writes a whole new file,
`lvai_apply_aixml_to_vi` is inert from this client, `lvai_swap_subvis` can only repoint nodes that
already exist, and pylabview cannot compose a diagram from nothing. An override mode on
`lvai_add_class_method` therefore cannot be finished by re-ordering the existing steps.

### The right answer is NI's Message Maker — do not author `Do.vi` at all

The premise above ("we must generate the body") is wrong, and the IDE says so: right-clicking an
actor gives **Actor Framework → Create Messages for Actor**, which writes the message class, its
`Do.vi` override *and* the `Send` VI, all derived from a public method of the actor. There is
nothing for us to author.

The provider landscape, measured 2026-09-17:

| provider | entry point | usable? |
|---|---|---|
| `AddActor` — "New → Actor" | `Add Actor.lvlib:Add Actor.vi` | **no — it is a DIALOG.** `OK Button`, `Cancel Button`, `Name of Actor` and `Inherit from:` are front-panel controls that are NOT on the connector pane. `lvai_create_class` with `parentClassPath` = `Actor.lvclass` already does this job |
| `ActorMessageMaker` — "Create Messages for Actor" | `Create Message Classes for Actor.vi` | takes `Object` (uint64), the IDE's own item handle |
| `MessageMakerProvider` | `Create Message Class for Method.vi` | clean pane — `Item Refnum` + `Target` → `New Class Path`, `New Send Method` |
| `Message Maker.lvlib` | `Copy Class.vi`, `Build Concrete Do.vi`, `Build Send.vi`, … | **path-based and public — this is the layer to drive** |

**The item-handle layer is a dead end for us.** `Get Item Info.vi` derives both refnums a provider
needs from one uint64 `Item`, via `mxLvGetProjectPath.vi`, `mxLvGetTarget.vi` and
`mxLvGetItemRef.vi`. The only public way to make that handle is `mxLvGetItem.vi`, which takes an
`NIIM` cluster — and **`NIIM` is a typedef (`mxLvNIIM.ctl`)**, which AIXML cannot express. Measured:
an authored `cluster{string.Item URL,…}` constant arrives at `Bundle By Name` as *"a cluster of 0
elements"*, the same limit this repository already records for error clusters. `CLSUIP_GetProjItemOfMemberVI.vi`
is not a way round it either — it is **private scope**, so `ValidateAIXML` refuses the call.

Reading that private VI is still worth it: it is built entirely from public calls —
`{LV.LVClassLibrary}` `Get All Descendents` (`Type` = `"VI"`) then `{LV.ProjectItem}` `Name` in a
loop — so a ProjectItem for a class member is reachable without it.

### What works, and the one step that does not

`scripts/lvai_create_message_class.xml` drives `Message Maker.lvlib` directly, and the first half is
measured working:

**`Copy Class.vi`** (`Destination Directory`, `Class Name`, `Template Path`, `AppInst`,
`Parent Class Path` → `New Class Path`) clones NI's `Concrete Message Template.lvclass`. On
`Increment Msg` it produced a 41 KB `.lvclass` listing `Increment Msg.ctl` and **`Do.vi`**, and that
`Do.vi` reads back as `Increment Msg.lvclass:Do.vi` with its dynamic dispatch terminal at conIdx 11
and class-typed `Actor in`/`Actor out`. **So the override comes free, from NI's template** — the
whole problem the previous section describes simply does not arise. It is `eBad` only because the
dispatch terminal is still the template's `DNL_Message Template`, which is exactly what the next
step retypes. Note the class lands *directly* in `Destination Directory`, not in a subfolder.

**`Build Concrete Do.vi` fails in our execution context.** Reproduced in isolation, twice, with
LabVIEW fronted and responding:

```
Error 2, Invoke Node in Message Maker.lvlib:Build Concrete Do.vi
Method Name: Front Panel:Open
```

Its exported diagram says why: it opens the Do.vi through `AppInst` and then calls **`FP.Open` with
`Activate = true`, `State = Standard`** — it wants the window actually shown. Our helper runs under
`RunVIAsTopLevel` in the ADDON's application instance, and showing a window for a VI in the IDE's
instance from there is refused. This is the same application-instance boundary this repository
already records for `FP.Close` and `Front Panel Window:Open`, in the one form where it errors
instead of silently doing nothing.

### NI's recipe, so the next step is mechanical

`Build Concrete Do.vi`'s No Error frame, in order, all of them public `Message Maker.lvlib` VIs:

1. `Open VI Reference` (AppInst, Do Method path)
2. **`FP.Open`** (Activate, State = Standard) ← the blocker
3. `Find and Replace GObj.vi` — `GObj Class Name` = `LabVIEWClassConstant`, `Traverse Target` = BD,
   `Class Path` = the actor class: swaps the template's class constant for the actor's
4. `Replace Actor if Using PPL.vi`
5. a While loop of `Find and Replace SubVI.vi` (`Old VI Name` = **`Dummy Actor Method.vi`**,
   `New VI Path` = the actor method) plus `Rewire Do.vi`, until `done?`
6. `Check if File or Folder Exists.vi` on `Read Attributes.vi`, then either replace
   `Dummy Read Attributes.vi` or find it by label and `{LV.GObject}` `Delete` it
7. `Wire FP Controls to Accessor UnBundler.vi`
8. `Set Class Control Label.vi` (`Class Name`)
9. `Apply New VI Tools-Options Settings.vi`
10. `{LV.VI}` `BD.CleanUp`
11. `Save.Instrument` to the Do Method path

### It works — `scripts/lvai_build_message_do.xml`, measured 2026-09-17

Running steps 3-11 ourselves and omitting step 2 produces a **working override with a generated
body**. On `Increment Msg` against `Counter.lvclass:Increment.vi`, every stage reported 0 and the
result is:

```
Increment Msg.lvclass:Do.vi        execState 1
  Increment Msg   conIdx 11  dynamic      (renamed from DNL_Message Template)
  Actor in / Actor out                    class-typed
call targets: Counter.lvclass:Increment.vi, Casting Utility For Actors.vim
"Dummy" occurrences left in the diagram: 0
```

So the whole of the previous section's problem is gone: the override comes from NI's template and
the body is wired by NI's own scripting. Nothing is authored in AIXML.

**Two corrections to what this document said an hour earlier.**

`FP.Open` was **not** the blocker, and blaming the application instance was wrong. The Error 2 was a
**stale in-memory path**: an earlier run had left `Increment Msg.lvclass:Do.vi` registered against a
folder that no longer existed, and LabVIEW resolved the new VI to that ghost — the give-away was the
error naming `C:\Temp\ActorFW_first\Increment Msg\Do.vi` when the file passed in was at the top
level. After `lvai_close_active_project` and copying the class into its own subfolder, `FP.Open`
went through. The lesson is this repository's own: a failure inside somebody else's VI is not
evidence about that VI until the state around it is clean.

What NI's `Build Concrete Do.vi` then fails on is **`Replace Actor if Using PPL.vi` → `Find Item in
PPL.vi`, Error 1055** — its `Target` input is a `ref{LV.TargetItem}` we cannot construct, because
the only public route to one is `mxLvGetItem.vi` and its `NIIM` is a typedef. Our reimplementation
omits that step, which is sound here: it only matters when the actor lives in a packed library.

**The recipe that works**, all of it public `Message Maker.lvlib`:

1. `Copy Class.vi` — clone `Concrete Message Template.lvclass`, parent `Message.lvclass`.
   **Give it its own subfolder**: the class lands directly in `Destination Directory`.
2. `Open VI Reference` on the new `Do.vi` through the IDE's `AppInst`
3. `Find and Replace GObj.vi` — `GObj Class Name` = `LabVIEWClassConstant`, `Traverse Target` = BD
4. a While loop of `Find and Replace SubVI.vi` (`Old VI Name` = `Dummy Actor Method.vi`) plus
   `Rewire Do.vi`, stopping on `done?` or an error. **The loop is essential**: the template carries
   **six** `Dummy Actor Method.vi` nodes in a six-frame case ("a".."f"), one per wiring variant, and
   `Rewire Do.vi` keeps the frame that fits the method's pane and deletes the rest
5. `TRef Find Object By Label.vi` + `{LV.GObject}` `Delete` for `Dummy Read Attributes.vi`
6. `Wire FP Controls to Accessor UnBundler.vi`, `Set Class Control Label.vi`,
   `Apply New VI Tools-Options Settings.vi`, `BD.CleanUp`, `Save.Instrument`

### The complete pipeline, payload and Send VI included

**This is `lvai_create_message_class`.** It takes the actor's `.lvclass` and a method NAME — the
extension is optional — and defaults everything else: the class is `<method> Msg`, the folder sits
beside the actor's own, and the template and `Message.lvclass` come from the LabVIEW installation
the actor's **own parent links** land in, so they can never be from a different install than the
actor. That walk doubles as the check that the class really is an actor.

Four things are refused before LabVIEW is touched at all, because `Copy Class.vi` is the first step
and a half-built class cannot simply be re-run over: a method that is not a member (the answer names
the members), a member whose file is missing, a class that does not descend from `Actor.lvclass`,
and a destination that already holds a class of that name. The `verify` block is read back off the
saved `.lvclass` with pylabview — `fields` and `members`, not the run's own account of itself.

`scripts/lvai_create_message_class.xml` is the helper it drives. Measured on
`Increment Msg` against `Counter.lvclass:Increment.vi`, every stage reports 0:

| step | VI |
|---|---|
| clone the template | `Copy Class.vi` — **`Message Template.lvclass`**, parent `Message.lvclass` |
| read the method's parameters | `Copy Member Data from Target.vi` → `Wiring Rules`, `Controls[]` |
| give the message its payload | `Add Member Data to Private Data Control.vi` |
| rewire `Do.vi` | `Find and Replace GObj` → loop of `Special Replace of SubVI Node` + `Rewire Do` → `Wire FP Controls to UnBundler` → `Set Class Control Label` → `BD.CleanUp` → `Save.Instrument` |
| create the Send VI | `Create Send Method.vi` |
| build the Send VI | `Add Controls to Method` → `Find and Replace GObj` (label `Message Type`) → then ONE of two frames → `BD.CleanUp` → `Clean Up Panel` → `Save.Instrument` |
| **save the class** | `{LV.Application}` `LVClass.Open` → `{LV.LVClassLibrary}` `Save` |

Result: `Do.vi` at `execState 1` calling `Counter.lvclass:Increment.vi` with `Amount` wired from the
message's own private data, and `Send Increment.vi` at `execState 1` whose pane is
`Message Enqueuer` (11, required), `Amount` (10, int32), `Message Priority (Normal)` (7),
`error in (no error)` (8) → `error out` (0), `Message Enqueuer out` (3) — byte for byte the shape of
NI's own `Send Increase Count.vi`.

**Which template you pick decides which builder you must follow.** `Concrete Message Template` ships
`Do.vi` and `Dummy Read Attributes.vi` but **no `Send Template.vi`**, so `Create Send Method.vi`
answers `Error 7` naming a file that was never copied; it belongs to the coupled abstract/concrete
pair and its builder is `Build Concrete Do.vi`, which uses `Wire FP Controls to **Accessor**
UnBundler`. `Message Template` ships `Do.vi`, `Dummy Actor Method.vi` and `Send Template.vi`, and its
builder is `Build Do.vi`, which uses `Special Replace of SubVI Node` and
`Wire FP Controls to UnBundler`. Mixing the two answers 1055 from a `To More Specific Class` inside
the wrong wiring VI.

**`Build Send.vi`'s two halves are ALTERNATIVES, not a sequence.** Its case structure selects on
`Array Size(Controls out)`: with **no** payload it runs `Wire Class to Enqueue` + `BD.Remove Bad
Wires`, with **one or more** it runs `Controls to Connector Pane` + `Wire FP Controls to Bundler`.
Running both in a row invalidates the control references and answers 1055 — measured, because that
is what a first reading of the call list produces.

### The class must be SAVED, and nothing says so until the project closes

The first complete run reported 0 at every stage and produced two VIs at `execState 1`. After a
`lvai_close_active_project` / reopen cycle **both were `eBad`** — errors of type 8, 300008, 100008 —
and the class on disk still listed `Send Template.vi` with no `Amount` field. The Message Maker's
changes to the CLASS (its private data control, and the renamed member) live in memory; only
`Save.Instrument` on the two VIs had been called. This is CLAUDE.md's rule for
`lvai_add_class_method` — *the owning class must be saved in the same run* — arriving from a new
direction, and the window in which it looks fine is the whole session up to the close.

Adding `{LV.Application}` `LVClass.Open` + `{LV.LVClassLibrary}` `Save` at the end fixed it:
`lvai_describe_class` now reads `Amount` (NumInt32) and members `Send Increment.vi`, `Do.vi` off the
saved file, and all three VIs survive the cycle at `execState 1`.

**Build this AIXML with a file tool, not a Python heredoc.** Doing the latter turned `\3A` and `\2C`
into the bytes `0x03` and `0x02` — seven and one occurrence — and the file was refused as not
well-formed XML. That is exactly the octal-escape trap CLAUDE.md records, met in practice.

## 5. LabVIEW died creating the second AF class, and the log named the site

Two `lvai_create_class` calls fired back to back, both deriving from `Actor Framework.lvlib`
classes. The second answered `Could not find a port serving lvai.LVAI` and `LabVIEW.exe` was gone
from the process table. **No minidump was written**, so LabVIEW's own crash handler did not fire.

`%TEMP%\LabVIEW_32_*_cur.txt`, copied before restarting:

```
VI call stack:
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:Open project application ref.vi
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:OpenFile.vi
*** Dumping Bread Crumb Stack ***
#** VILinkObjRemoveCore: "…\vi.lib\ActorFramework\Message Enqueuer\Enqueue Critical.vi"
```

`Open project application ref.vi` is already on CLAUDE.md's list of crash sites. What is new is the
breadcrumb: the fault was **unlinking an Actor Framework VI**, which is consistent with the class
creation pulling the whole AF hierarchy into memory and the scratch project's teardown tripping over
it. The class files themselves survived intact and were verified from disk afterwards.

The Nigel service log (`%ProgramData%\National Instruments\AIAssistants\Logs`) recorded the moment as
five `Stopped monitoring due to exception … StatusCode="Unavailable"` lines — unlike the silent
disappearance CLAUDE.md records elsewhere, this one left a trace in both logs.

**Space AF class creations apart**, as CLAUDE.md already advises for `lvai_create_class` generally.

## 6. One toolchain trap this build hit

**`sed -i` in Git Bash rewrites a CRLF file as LF.** Editing a `.lvclass` — which LabVIEW writes with
CRLF — took it from 12 208 to 12 157 bytes, exactly one byte per line, while `diff` reported all 51
lines changed and `grep -c $'\r'` still answered 51 for both files and hid it. `tr -cd '\r' | wc -c`
is the honest measure: 51 against 0. Use PowerShell's `[System.IO.File]::ReadAllText` /
`WriteAllText` with `UTF8Encoding($true)` to keep the BOM and the line endings, and verify the byte
count. This is CLAUDE.md's heap-payload line-ending rule one layer out, on the class file.

## 7. Accepted through the MCP client, 2026-09-17 — and one gap the acceptance found

`lvai_create_message_class` was built in the session that could not call it: a client fetches the
tool list once at session start, so the tool that was added after that start was unreachable, exactly
as CLAUDE.md records for `lvai_close_active_project`'s `projectPath`. With LabVIEW and the client
restarted it was called for the first time, **on a method it had never seen** — `Write Count.vi`, the
accessor, rather than the `Increment.vi` the pipeline was developed against.

| arm | result |
|---|---|
| `actorMethodName: "Decrement"` | `notAClassMember`, listing `Increment.vi`, `Read Count.vi`, `Write Count.vi` |
| `actorMethodName: "Increment"` | `messageClassExists`, naming `…\Increment Msg\Increment Msg.lvclass` |
| `actorMethodName: "Write Count"` | **`ok: true`**, all nine stages 0, **3 461 ms** inside LabVIEW |

The second arm is worth keeping as an arm rather than a nuisance: the refusal reports the path it was
about to write, so it pins **both** defaults — `<method> Msg` and a folder beside the actor's own —
on the real call path rather than in a unit test.

What the created class is, read back off disk: private data `Count`, `NumInt32`, lifted from the
accessor's own input; members `Send Write Count.vi` and `Do.vi`, with no `Send Template.vi` left; and
`Do.vi`'s export carries `Call target="Counter.lvclass\3AWrite Count.vi"` with
`Count:151.value` wired out of the message's own `Unbundle` — so the payload really does travel.
`Send Write Count.vi`'s pane is NI's shape with the payload at conIdx 10:
`Message Enqueuer` (11, required), `Count` (10, required), `Message Priority (Normal)` (7),
`error in (no error)` (8) → `error out` (0), `Message Enqueuer out` (3).

**THE SAVE FIX HOLDS.** Both VIs read `execState 1` before the project close AND after it — the exact
cycle that used to leave both `eBad` with the class on disk still listing `Send Template.vi`. The
existing `Increment` example ran unchanged afterwards (`Count = 1 / 6 / 16`, `error out` 0), so a
second message class in the same folder disturbs neither the first nor the actor.

**THE GAP: the new class is NOT written into the `.lvproj`, and `projectPath` does not mean what it
means on the sibling tools.** Measured on the same run — the project's item list was identical before
and after, and `lvai_close_active_project`'s sweep answered `strayVisRemoved: 0`, so nothing added the
class and nothing removed it either. On `lvai_create_class` the `projectPath` argument is the file the
tool EDITS; here it is only the project the tool OPENS, because every Message Maker step reaches
LabVIEW through `Project:Active Project`. One name, two meanings, across tools a caller uses in the
same breath — which is the shape CLAUDE.md records as costing eighteen days when a description
explains a mechanism that is not the one in play. Until it writes an entry, add the message class to
the project by hand, with the project CLOSED.

## 8. The AddActor provider: how to create an actor INSIDE a library, 2026-09-17

§4 records `AddActor` as unusable because `Add Actor.lvlib:Add Actor.vi` is a DIALOG whose
`OK Button`, `Name of Actor` and `Inherit from:` are not on the connector pane. That is true of the
dialog and **false as a conclusion about the provider**, exactly as it was for the Message Maker: the
work is done by two `Support\` VIs with clean panes, and the dialog only collects arguments for them.

| worker | pane |
|---|---|
| `Add Actor Library to Project.vi` | `Library Path`, `Target Item` (`ref{LV.TargetItem}`), `Application` → `Library out` |
| `Create Child Actor.vi` | `Library in` (`ref{LV.Library}`), `Actor Name`, `Parent Class` (`ref{LV.LVClassLibrary}`), `App Inst` → `Library out` |

**`ref{LV.TargetItem}` IS NOT THE BLOCKER IT LOOKS LIKE.** §4 rules the item-handle layer out because
`NIIM` is a typedef AIXML cannot build, and that still holds — but reading
`Add Actor Library to Project.vi` shows the TargetItem is used by exactly ONE node, the last one:

```
{LV.Application} Library.Create            -> Create Library      (no TargetItem)
{LV.Library}     AddItem  Name="Messages for this Actor", Path=<empty>, Type="Folder"
{LV.Library}     Save     Path=<library path>                     (library now on disk)
{LV.TargetItem}  AddItem  Name, Path, Type="Library"              <- the only TargetItem use
```

That last node lists the library in the project, which we already do as an ordinary `.lvproj` file
edit with the project closed. So **the whole chain is reachable without an item handle.**

**`{LV.Library}` `AddItem` WITH `Type="LVClass"` IS THE GESTURE THAT PUTS A CLASS IN A LIBRARY**, and
that is the finding that matters. `Create Child Actor.vi` ends with it, followed by
`Edit LVLibs.lvlib:Save All This Library.vi`. Driving those two against an EXISTING class writes BOTH
halves of the membership: the `.lvlib` gains its `<Item … Type="LVClass">` and the `.lvclass` gains
`NI.Lib.ContainingLib` plus a `ContainingLibPath` at the right depth — `../../` for a class one folder
down, `../../../` for one two down.

**HAND-WRITING THOSE TWO PROPERTIES IS NOT THE SAME THING, and it is a silent wrecker.** Measured the
same day on `Hund`: writing the `.lvlib` and inserting `NI.Lib.ContainingLib` into the class by hand
gave a file that every cheap check passed — `lvai_describe_class` read `qualifiedName`
`Hund.lvlib:Hund.lvclass` and `containingLibrary: Hund.lvlib`, both XML files parsed, BOM and CRLF
intact — and **all three VIs went from `execState 1` to `eBad`**, because the property changes the
class's QUALIFIED NAME while nothing relinks the members that call each other by it. Reverting the
files restored the classes; the eBad survived a project close AND a full LabVIEW restart, which is
how it was finally traced to the file rather than to memory. The A/B is clean: only the library
membership changed.

**`Library.Open` EXISTS, though the VI Server catalogue lists neither it nor `LVClass.Open`.** It is
an `{LV.Application}` Invoke Node with `Path`, output `Library.Open`, shaped exactly like
`LVClass.Open` in NI's own dialog. Validated 2026-09-17; that is what lets a second run add more
classes to a library that already exists, instead of rebuilding it.

**The route, end to end, as built for `Aquarium`:** `lvai_create_class` (fields + parent
`Actor.lvclass`, no `projectPath`, so it gets no class entry of its own) → a helper running
`Library.Create` + `AddItem` folder + `AddItem` class + `Save` + `Save All This Library` → the
`.lvlib` written into the `.lvproj` by hand with the project CLOSED → `lvai_create_accessors` →
`lvai_add_class_method` → `lvai_swap_subvis` → the icon (a resave, wanted anyway — NOT required, see 8b) → one
`lvai_create_message_class` per method. Result: `Aquarium.lvlib` holding the actor and three message
classes, every VI `execState 1` after a project close.

### 8a. RETRACTED — see 8b. The claim below did not survive a clean probe

Measured on `Hund.lvclass:Bellen.vi`, and it is the reason the section above insists on a resave.
The file `lvai_swap_subvis` leaves behind was missing its `LIvi` block, the one carrying subVI
linkage:

| stage | blocks |
|---|---|
| after `lvai_add_class_method` | 11 files, no `LIvi`, no `LIbd`/`LIfp` |
| after `lvai_swap_subvis` | 13 files, `LIbd`+`LIfp` added, **still no `LIvi`** |
| after `lvai_set_vi_icon` (forces a real save, `viResaved: true`) | 15 files, **`LIvi` present** |

**In the no-`LIvi` state every check in this repository was green**: `lvai_exec_state` answered
`execState 1` while LabVIEW held the VI, the AIXML export carried all three Calls with correct
class-qualified targets and dynamic dispatch at 11/3, `socketsLeft` was 0 with correct `callTargets`,
and `pylv_apply`'s pane check said the pane follows NI's style guide. After a LabVIEW **restart** the
same VI was `eBad`, and the message `Do.vi` that calls it was eBad transitively — while
`Send Bellen.vi`, which touches no actor class, stayed fine. The control is
`Counter.lvclass:Increment.vi`: same shape, built the same way, HAS `LIvi`, survives a cold LabVIEW —
and it had been given an icon in an earlier session, which is the resave this one never got.

So a warm `execState 1` taken straight after a swap is worth nothing, which is this file's
`Execution:State = 1 IS NOT EVIDENCE` rule reaching one layer further: it is not enough to ask the
FILE either, unless you ask it after LabVIEW has written it. **`grep -a -c LIvi <file>.vi` is the
cheap check**, and `lvai_set_vi_icon` is the cheap fix — it is wanted anyway, and the icon is the
last step of every generation route here.

n = 1, and that VI had also been through an unrelated library experiment, so the attribution is
strong rather than clean; a minimal probe is queued.

## 9. The launcher, and two things it re-measured — 2026-09-17

`Launcher Aquarium.vi` drives the whole chain from outside: clear the log, `Launch Root Actor`, four
`Send` VIs, `Send Normal Stop`, wait 800 ms for the queue to drain, read the log back. Measured
output, twice, `error out` 0 both times and the same four lines in `aquarium-log.txt`:

```
Temperatur = 24.5 C
Licht = an
Futter gesamt = 3
Futter gesamt = 5
```

**`3` then `5` is the assertion that matters** — two separate `Fuettern` messages, and the actor kept
its running total between them. A single send would have proved delivery but not state.

**`lvai_generate_vi` CANNOT BUILD THIS VI, and the refusal names the right thing for the wrong
reason.** Validation answered three times *"You have connected two terminals of different types …
the type of the source is file path, the type of the sink is Actor Framework.lvlib:Message
Enqueuer.lvclass"* — all three are the placeholder stand-in pattern, not faults. This is CLAUDE.md's
class-wire strictness rule reaching the launcher: `lvai_convert_aixml_to_vi` on the same file wrote
11 889 bytes without complaint, and the swap then repaired the types. **Check the byte count**, since
an empty generated VI is about 3 170 bytes.

**ONE SOCKET ON TWO NODES COSTS TWO SWAP CALLS, confirmed again.** Both `Fuettern` sends use one
stub, and the first call answered `nodesSwapped: 3`, `constantsSwapped: 1`, **`socketsLeft: 1`** with
`diagramSubVis` listing the stub twice; the second call took it to 0. `socketsLeft` counts
`swapsJson` ENTRIES still in the export, not nodes — exactly as §*swap* of CLAUDE.md records.

**`{LV.Library}` `AddItem` TAKES `Type="VI"` TOO, and it relinks a plain VI's CALLERS.** `Append To
Log.vi` — the one generic VI the three actor methods log through, factored out under the user's
repeated-operation rule — was moved into `Aquarium.lvlib` after the fact. That changes its qualified
name to `Aquarium.lvlib:Append To Log.vi`, which is the same kind of change that left every `Hund`
VI `eBad` in §8. Through NI's `AddItem` plus `Save All This Library` it did not: after a project
close the three methods, the launcher and the VI itself all read `execState 1`, and the launcher
produced the identical log. **So the difference between the two routes is now measured on the same
kind of change twice — hand-edited property: broken; NI's AddItem: sound.**

Its standalone `.lvproj` entry was removed first, with the project closed: a library member belongs
to the project through its library, not beside it.

### Helpers

`scripts/lvai_create_actor_library.xml` creates the library and puts one existing class in it
(`Library.Create` → `AddItem` folder → `Save` → `AddItem` class → `Save All This Library`).
`scripts/lvai_add_one_to_library.xml` adds ONE further item to a library that already exists
(`Library.Open` → `AddItem` → `Save All This Library`), with `Type` as an input so it serves
`LVClass` and `VI` alike. Both need a project open and ACTIVE, because they reach LabVIEW through
`Project:Active Project`. Neither is wrapped in an `lvai_*` tool yet.

### 8b. The `LIvi` claim is REFUTED — probed 2026-09-17, same day

§8a said `lvai_swap_subvis` leaves a VI without its `LIvi` block, warm-green and cold-broken, and
that a forced LabVIEW save is what repairs it. **A clean probe does not reproduce any of it**, and
the section is wrong. It is left standing above, struck through, because the observations in it are
real — it is the CONCLUSION that does not follow.

The probe, on `Aquarium.lvclass:Licht Schalten.vi` — a VI built from scratch the same day, whose
whole history is known, and which had never been near the library experiment:

| step | `LIvi` | bytes |
|---|---|---|
| after `lvai_add_class_method` | **present** | 9 502 |
| after `lvai_swap_subvis` | **present** | 8 758 |
| project closed, read back cold | — | `execState 1`, and its message `Do.vi` too |

So the swap neither strips the block nor leaves a VI that fails a cold load, and **no forced resave
was needed**. `lvai_swap_subvis` needs no fix, and none was made.

**What the two VIs differ in is what they CALL**, which is the likeliest reason `Bellen.vi` had no
`LIvi` and `Licht Schalten.vi` does: Bellen calls only members of its OWN class, whose links live in
the `LIbd`/`LIfp` UDClass-API blocks, while Licht Schalten also calls `Append To Log.vi`, an ordinary
subVI. On that reading the missing block was NORMAL for that VI and never a defect at all. Not
established — `Counter.lvclass:Increment.vi` also calls only class members and does carry `LIvi`,
though its own parse warning names `VIPI`, which may be a different kind of link. Nobody has decoded
these blocks and this file should not pretend otherwise.

**What survives from §8a is only this**: `Hund.lvclass:Bellen.vi` was `eBad` after a LabVIEW restart
with every file-level check green, and `lvai_set_vi_icon`'s forced resave made it executable again.
That is a real measurement. Its CAUSE is unknown, and the library round trip that VI had been through
remains the only candidate that the `Licht Schalten` probe does not rule out.

**The process lesson is the one this file keeps paying for.** A correlation found while chasing a
failure — the block appeared in the same step that fixed the VI — was written up as a mechanism, with
a remedy, a `grep` recipe and a queued tool change, on n = 1, on a VI whose history was already known
to be contaminated. §8a said so itself in its last line and recommended a probe; the probe took four
tool calls and refuted it. **Run the probe before writing the mechanism down, not after.**

## 10. A message class belongs IN the folder, and `{LV.Library}` `AddItem` cannot put it there

Measured 2026-09-17, after the Heizung build put four classes in `Heizung.lvlib` and every one of
them landed at the library ROOT — the `Messages for this Actor` folder sat there empty. §8 records
`{LV.Library}` `AddItem` as the gesture that relinks, and that is still true; what it does NOT do is
choose a folder, because a library's root is the only thing it can add to.

**NI's own placement is one layer down, and the node is on a different class.**
`Message Maker.lvlib:Add to Project.vi` — which `Coupled Message Scripter:Add to Project.vi` calls
with `Folder` = `"MESSAGES FOR THIS ACTOR"` — resolves the folder and then invokes

```
{LV.ProjectItem} AddItem   Name, Path, Type
```

on the FOLDER's project item. The resolution is worth copying verbatim:

```
{LV.Library} Get All Descendents   Type="Folder"        -> array of {LV.ProjectItem}
  For each: {LV.ProjectItem} Name -> To Upper Case
  Search 1D Array against the wanted name, upper-cased
  found    -> Delete From Array at that index; `deleted portion` IS the folder's item
  not found-> use the LIBRARY reference itself
{LV.ProjectItem} AddItem on whichever came out
Edit LVLibs.lvlib:Save All This Library.vi
```

**One node serves both arms because a `{LV.Library}` IS a `{LV.ProjectItem}`** - NI wires the library
reference straight into the same `AddItem` node as the fallback. That is what makes this cheap to
implement: no second code path.

**THE FALLBACK IS SILENT, AND THAT IS THE DANGEROUS HALF.** A folder name that matches nothing puts
the item at the root with `error out` 0, which is exactly the outcome that looks like success and is
not. `lvai_add_to_library` therefore REFUSES a folder that is not in the `.lvlib` before LabVIEW is
touched, naming the folders that are, and its `verify.placedIn` reports the folder each item really
ended up in rather than only that it is somewhere in the library.

**AND `AddItem` REFUSES AN ITEM THE LIBRARY ALREADY HOLDS - `error out` 56002.** Measured on
`Bellen Msg.lvclass`, already at the root: `folder index: 0` (so the search had found the folder)
and then 56002 from `AddItem`, which stopped the chain before `Save All This Library`, so nothing
was written. So moving an item into a folder is NOT one call: drop its `<Item>` line from the
`.lvlib` with the project CLOSED, then add it again with the folder. The class keeps its
`NI.Lib.ContainingLib` throughout - it is still a member, just briefly unlisted - so this is not the
dangling-file case that opens a modal search dialog, and NI's code tolerates 56002 for exactly this
reason (`ignore error for autopopulating folders`).

Repaired that way on both libraries and re-checked cold: `Hund.lvlib` and `Heizung.lvlib` nest every
message class under `Messages for this Actor`, with the actor class at the root, and every VI reads
`execState 1` after a project close.

## 11. Lampe - the acceptance run for `folder`, and the regression it found

Built 2026-09-17 in the session after the one that shipped the `folder` parameter, because a client
fetches the tool list **once at session start** and a parameter declared later is stripped before
sending - so the feature could not be tested where it was written. `Lampe.lvclass`
(`Name` String, `Helligkeit` I32, `An` Bool) off `Actor.lvclass`, three methods, three messages.

**THE READ-ONLY ARM CAME FIRST, because either outcome is a refusal and nothing is written.** A
bogus folder against an item the library already holds:

```
lvai_add_to_library(libraryPath=…\Heizung\Heizung.lvlib,
                    itemsJson=[{"path":…\Heizung\Heizung\Heizung.lvclass"}],
                    folder="Nachrichten")
-> errorKind "folderNotInLibrary", foldersInLibrary: ["Messages for this Actor"]
```

Had the client dropped the argument this would have answered `itemAlreadyInLibrary` instead, so one
call settles that the parameter arrives AND that the guard fires. **The ToolSearch schema text was
stale while the server was not** - it listed no `folder` property, which read exactly like an
unrebuilt build and cost a DLL check. That check was itself wrong first: an ASCII `grep` of a .NET
assembly found none of the new markers, and **the control - a string certainly in the old code -
also read 0**, which is what exposed the method rather than the build. .NET strings are UTF-16.

**The mutating arm was the three message classes in ONE call**, and it is the measurement that
matters: `placedIn` three for three, `notInRequestedFolder: []`, and the saved `.lvlib` nests all
three under `Messages for this Actor` with the actor class at the root.

### 11a. A message carries TWO payload fields, and the order is the pane's

`payloadControlCount` had been `1` on every message ever built here, so a multi-field payload was
untested. `Dimmen.vi` takes `Helligkeit` (I32) and `Rampe ms` (I32) besides the class wire and the
error cluster:

| message | payloadControlCount | fields read back from the saved `.lvclass` |
|---|---|---|
| `Umbenennen Msg` | 1 | `Neuer Name` **String** |
| `Dimmen Msg` | **2** | `Helligkeit` NumInt32, `Rampe ms` NumInt32 |
| `Schalten Msg` | 1 | `An` Boolean |

So the Message Maker takes every non-class, non-error input **in connector-pane order** and makes
one private data field each; a String payload works like any other. Nothing special had to be done
for either case - which is worth writing down precisely because both were being planned around.

### 11b. THE FOLDER PARAMETER BROKE THE LIBRARY ROOT, which is the DEFAULT path

Found in the same session, adding `Append To Log.vi` to `Lampe.lvlib` with no folder:

```
ok: false, verify.missing: ["Append To Log.vi"]
step addItem -> badArguments: "Input 'folder' has an empty value. Names and values are paired
                by POSITION, and an empty value does not survive the helper's split"
```

`lvai_add_to_library` sent `["folder"] = folder ?? ""`, and `lvai_run_vi_and_read_values` refuses an
empty value outright rather than passing it on - so **every call WITHOUT a folder failed before
LabVIEW ran**, from the commit that added the feature. The root path is the default and the one the
tool shipped with; three folder calls had just succeeded in the same session, which is exactly why
nothing looked wrong.

**Nothing was written, and the only thing that said so was `verify`.** All three per-item steps in a
successful call and this one have the same shape, and `itemsAdded` lists what was ASKED for - it is
`verify.missing`, re-read from the saved `.lvlib`, that disagreed. That is the verify block earning
its keep: a tool that trusted its own run answers would have reported success.

The fix is to OMIT the input rather than send it empty - the helper's own control defaults to `""`,
whose `Search 1D Array` answers `-1`, which is the root fallback the helper was already written
around. It lives in `LibraryTools.HelperInputs`, its own function **so that it can be tested without
LabVIEW**: every other guard in that tool runs before the connection and this one did not, so no
existing test could reach it. Two tests, because a builder that dropped the folder ALWAYS would pass
the first one: `NoFolderMEANSNOFOLDERINPUT_NotAnEmptyOne` and `AFolderIsPassedThroughVerbatim`.

**The general shape is one this repository already records**, from `runForMs` picking its helper:
*a parameter that one mode ignores must not be able to defeat that mode.* Here the mode was the
default one.

### 11c. The log helper needs its file to exist

`Append To Log.vi` is `Read from Text File` -> `Concatenate Strings` -> `Write to Text File`, and the
read's error propagates into the write, so **on a file that does not exist yet the first line is
lost and the file is never created**. Aquarium's copy has the same shape and works only because its
log file was created by hand first. Create the log file before the first run, or the first message
handled leaves no trace and the diagram looks broken.

## 12. Ventilator - a message with NO payload, and a Double one

Built 2026-09-17 to close the two gaps §11a left. `Ventilator.lvclass`
(`Name` String, `Stufe` I32, `Laeuft` Bool) off `Actor.lvclass`, three methods, three messages,
every VI `execState 1` cold after a project close.

**`payloadControlCount` REACHES 0, and nothing special is needed to get there.** `Anhalten.vi`
takes only the class wire and the error cluster - four terminals, no payload - and the Message
Maker produced a class with `fields: []` and `privateDataBytes` 11 805, against 12 265 for the
one-field message beside it. That is the commonest real message in an actor system (`Stop`,
`Reset`, `Toggle`) and it had never been built here.

| message | payloadControlCount | field |
|---|---|---|
| `Anhalten Msg` | **0** | - |
| `Stufe Setzen Msg` | 1 | `Stufe` NumInt32 |
| `Kalibrieren Msg` | 1 | `Faktor` **NumFloat64** |

With §11a that makes the measured range 0, 1 and 2 payload fields, over String, I32, Double and
Boolean. The rule is the same at every point: every non-class, non-error input of the method
becomes one private data field.

**THE ORDER CLAUSE THAT USED TO END THAT SENTENCE - "in connector-pane order" - IS WRONG, and §13
has the measurement.** It held for every message built up to that point because they all sat on
4815, where the extra inputs run 10 then 9 and descending conIdx happens to read top to bottom.

### 12a. The control arm for the `Save All This Library` crash

`docs/labview-crash-signatures.md` records LabVIEW disappearing while `Save All This Library.vi`
added `Append To Log.vi` to `Lampe.lvlib`, with one clean difference available: that VI had a loose
`<Item>` of its own in the `.lvproj`, where the three message classes added just before it had
none. This build was sequenced to test that without provoking it - **the log VI was generated and
added to its library BEFORE the project was ever saved with it open**, so it never acquired a loose
entry:

| add | loose `.lvproj` entry | elapsed | outcome |
|---|---|---|---|
| `Append To Log.vi` -> `Lampe.lvlib` | **yes** | **6 145 ms** | LabVIEW gone |
| `Append To Log.vi` -> `Ventilator.lvlib` | no | **184 ms** | fine |

Same tool, same file type, same root fallback (`folder index: -1`). **The 33x difference in elapsed
time is the part worth keeping**: it says the crashing call was doing substantially more work, which
fits LabVIEW reconciling an item that the project held two ways - and the `.lvproj` after that call
proves it did exactly that, having moved the loose line out and adopted six strays with no close in
the session.

Still n=1 against n=1, and still not a cause. What it does justify is the ORDER: **add a helper VI
to its library before anything can list it loose**, which costs nothing and avoids the state
entirely. The deliberate A/B - a throwaway VI listed loosely, then added - has not been run.

### 12b. `projectDidNotBecomeActive` needed a HAND fronting, three times

`lvai_open_file` answered `No Error` with no active project three calls running, each time with
`foregroundRetry: null` - so the built-in retry that `Infra/LabViewWindow.cs` exists for did not
fire at all. A `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` from PowerShell fixed it on the next
call. The diagnosis is the documented one; what is new is that the automatic remedy reported
nothing, which is worth a look before an unattended run depends on it.

## 13. Drucker - three payload fields, a path payload, and a method off 4815

Built 2026-09-17, in the session that could first CALL `lvai_add_class_field`. `Drucker.lvclass`
(`Name` String, `Seiten Gesamt` I32, plus `Bereit` Bool added afterwards with that tool), two
methods, two messages, every VI `execState 1` cold after a project close.

**`payloadControlCount` REACHES 3, AND ONE OF THEM IS A `path`.** `Drucken.vi` takes `Datei`
(path), `Kopien` (I32) and `Duplex` (Bool) beside the class wire and the error chain:

| message | payloadControlCount | fields, in the order the Message Maker wrote them |
|---|---|---|
| `Drucken Msg` | **3** | `Duplex` Boolean, `Kopien` NumInt32, **`Datei` Path** |
| `Bereit Melden Msg` | 1 | `Bereit` Boolean |

With §11a and §12 the measured range is now **0 to 3 payload fields** over String, I32, Double,
Boolean and Path.

### 13a. THE FIELD ORDER IS DESCENDING conIdx, NOT READING ORDER

§12 said "in connector-pane order" and that was an accident of 4815. `Drucken`'s inputs sit at
conIdx 5, 7, 9 - running DOWN the left edge of 4833 - and the fields came back `Duplex`, `Kopien`,
`Datei`, which is 9, 7, 5. Re-read against the earlier case: `Dimmen`'s inputs were 10 and 9 and
its fields were `Helligkeit`, `Rampe ms` - also descending. So both measurements agree on
**descending conIdx**, and on 4815 alone that coincides with top-to-bottom.

It matters for `Send <Method>.vi`'s pane and for anyone reading the private data control: on a
pattern whose inputs ascend down the edge, the message's fields are in the opposite order to the
method's terminals.

### 13b. A CLASS METHOD DOES NOT HAVE TO BE ON 4815

Three payload inputs do not fit NI's accessor layout: 4815 offers 11, 10, 9 and 8 on the left, and
8 is `error in`, so a class method has room for exactly TWO extra inputs. `lvai_add_class_method`
re-panes onto 4815 by default and `panePattern: 0` leaves the pane alone - so the method was
authored against the station default 4833 (`Drucker in` 0, `Datei` 5, `Kopien` 7, `Duplex` 9,
`error in` 11; `Drucker out` 4, `error out` 15) and passed through with `panePattern: 0`. Dynamic
dispatch resolved to 0 and 4, read off the VI's own export, and the result is `execState 1`.

### 13c. The verify could not tell a REAL path parameter from a stand-in

`Drucken` answered `ok: false`, `pathStandInsLeft: 1`, with `terminals retyped: 2`, the member
added, the class saved and the VI executable. The on-disk check counts every `class="stdPath"`
object in the front-panel heap and demanded zero, because the only reason a generated method
normally carries a `path` terminal is that AIXML refuses a class-typed one. `Datei` is a real path
parameter. `Bereit Melden`, with no path payload, answered 0 in the same run - the control arm.

**It had been seen once before and left as a comment.** `VerifyFailureDetail` records the same
shape from 2026-09-07 ("one of three `path` stand-ins, so two were legitimately left") and the
remedy then was only to stop that branch throwing. Fixed now: the expectation is counted from the
authoring AIXML - `path` terminals that are not named in `classTerminals` - reported as
`expectedPathStandInsLeft`, and NOT gated at all when there is no document to count (a method
handed over as an existing `vi`), because demanding zero there would re-create the false negative
for exactly the callers who cannot see why.

**ACCEPTED 2026-09-17 against a live LabVIEW, in the session after the fix** - a tool change cannot
be exercised in the session that makes it, because the client fetches the schema once at start.
`Waage.lvclass:Kalibrieren.vi`, the same shape as `Drucken` (two class terminals plus a `Datei`
input that really is a path), answered **`pathStandInsLeft: 1`, `expectedPathStandInsLeft: 1`,
`ok: true`**. **The control arm is what makes that mean anything**: `Wiegen.vi` in the same call,
same class, no path payload, answered `expectedPathStandInsLeft: 0` - so the expectation is
computed per method from its own document, where a function hardcoded to 1 would have passed the
first arm alone.

### 13d. Two ordering facts this build paid for

**`lvai_create_accessors` needs the class in the ACTIVE project; `lvai_add_class_field` does not.**
The library was created and the class went into it, but `Drucker.lvlib` was not yet listed in the
`.lvproj` - and the accessor wizard answered `Error 56002` from `AddVIToClass.vi` with
`membersAfter: 0`, nothing written. Adding the library entry with the project closed and reopening
fixed it; `classIndex` went from 25 to 5. The add-field call before it had worked fine in that same
state.

**And the new tool's carrier directory was invisible to the project sweep.**
`lvai_add_class_field` keeps its carrier under `%TEMP%\LabVIEWMCP\carriers\` deliberately, and
`StripHelperItems` knew only `helpers/` and `classes/` - so the first real run left
`Drucker-add-20260917150348.vi` in the user's `.lvproj` while the same sweep reported removing the
helper beside it. The file still exists, so the dangling pass cannot catch it either. **A new tool
that writes into a new directory has to teach that pattern about it**; nothing else in the chain
notices.

**ACCEPTED 2026-09-17**: the Waage build ran `lvai_add_class_field` the same way, and the close
answered `strayVisRemovedNames: [..., "Waage-add-20260917152105.vi (helper tree)"]` beside the
three sockets. The `.lvproj` afterwards holds no `carriers/` entry at all, where the Drucker build
needed that line removed by hand.

## 14. Waage - the acceptance build, and what a clean run looks like

Built 2026-09-17 in the session after §13's two fixes shipped, because neither could be exercised
where it was written. `Waage.lvclass` (`Name` String, `Letztes Gewicht` Double, plus
`Kalibrierdatei` Path added afterwards), two methods, two messages, every VI `execState 1` cold.
Both acceptances are recorded in §13c and §13d rather than here, with their control arms.

**`lvai_add_class_field` TAKES A `path` FIELD** - it had only ever been given `bool`.
`fieldsBefore` 2 -> `fieldsAfter` 3, `fieldsLost: []`, member count unmoved. Nothing special was
needed, which is the useful part: the field type travels through `LvClass.CarrierAixml` like any
other, so the tool's type coverage is the same allowlist `lvai_create_class` has.

**AND THE RUN WAS UNEVENTFUL, WHICH IS THE POINT WORTH RECORDING.** No `Error 56002`, no transient
validator failure, no hand edit beyond the one library entry - against the Drucker build, which
paid for all three. The difference is ONE ordering decision taken from §13d: **the library's entry
goes into the `.lvproj`, with the project closed, BEFORE `lvai_create_accessors` runs.** The
sequence that works, end to end:

1. `lvai_create_class` - project closed, scratch project, no `projectPath`
2. generate `Append To Log.vi` - before anything can adopt it loose
3. open the project -> `scripts/lvai_create_actor_library.xml` -> `lvai_add_to_library` at the ROOT
4. **close, write the `<Item ... Type="Library">` line, reopen**
5. `lvai_add_class_field` for anything the class still needs
6. `lvai_create_accessors` with an explicit `fromField 0` and the full field count
7. placeholders -> author -> `lvai_add_class_method` -> `lvai_swap_subvis` -> icons
8. `lvai_create_message_class` per method -> `lvai_add_to_library` with the folder
9. close with `projectPath` (the sweep runs here) -> `lvai_exec_state` cold

Steps 4 and 5 are the two this file learned the hard way, in that order.

## 15. Ofen - an actor whose private data carries a TYPEDEF, 2026-09-18

Built to answer one question: does the typedef tooling hold when the typedef is inside an ACTOR's
private data, all the way out to the message class? `Ofen.lvclass` (`Name` String, `Profil`
**Ofenprofil.ctl**, `Heizt` Boolean), two methods, two messages, every VI `execState 1` after a
LabVIEW restart. It does hold - and getting there found two real defects, one of them a hard stop.

The rig, beyond §14's sequence: `Ofenprofil.ctl` is a plain (non-strict) typedef wrapping
`cluster{double.Solltemperatur,double.Toleranz}`, made the way `docs/typedef-disconnect.md` §1
prescribes - `lvai_generate_vi` to the `.ctl` path, then a pylabview flag patch of
`<Instrument Type>` and `TypeDefVI`. `lvai_bind_class_fields` put it on the `Profil` field
**before** any accessor existed, which is the ordering `lvai_placeholder_subvi` insists on.

### 15a. THE HARD STOP: the accessor wizard refuses a `.ctl` LabVIEW has never SAVED

`lvai_create_accessors` answered **`Error 1061`** on the typedef field and only on that field:

```
New VI Object in MemberVICreation.lvlib:BaseAccessorScripter.lvclass:CreateControlFromReference.vi
  -> CreateControl.vi -> AddControlToWriteVI.vi -> ScriptAccessorVIs.vi -> CLSUIP_CreateNewAccessor.vi
```

`Name` (index 0) produced both accessors; `Profil` (index 1) produced none and stopped the slice, so
`Heizt` never ran either. It is **not** a Write-side quirk: asking for `accessUi: "Read"` gave the
identical code through `CreateIndicator.vi` -> `AddIndicatorToReadVI.vi`.

**The cause is the shape of the `.ctl`, and `lvai_describe_ctl` shows it without LabVIEW.** A
flag-patched control has never been written by LabVIEW itself, so it still carries the CONNECTOR
PANE of the VI it was generated from:

| file | `controlVIType` | `wrappedType` | `fields` | accessor wizard |
|---|---|---|---|---|
| `Ofenprofil.ctl` as patched | typedef | **`Function`** | 16, one real + 15 `Void` | **`Error 1061`** |
| `Ofenprofil.ctl` after one LabVIEW save | typedef | **`TypeDef`** | **1** | works |
| `Outer Config.ctl` (the §1 fixture) | strict typedef | `TypeDef` | 1 | works - it had been re-saved by a `Replace` |
| `Inner Mode.ctl` (the §1 fixture) | strict typedef | **`Function`** | 16 | never asked - it is only ever used NESTED |

So the two fixtures that this repository has been reasoning from differ in exactly this, and the
difference was invisible because only one of them was ever the direct source of an accessor.
Strictness is not the variable: `Ofenprofil.ctl` is plain and works once saved.

**The repair is one LabVIEW save with the path UNWIRED** - `{LV.VI}` `Save.Instrument` in the IDE's
own application instance, which writes a control in place. 4 155 -> 4 367 bytes, `wrappedType`
`Function` -> `TypeDef`, `fields` 16 -> 1, and the same `lvai_create_accessors` call then produced
all six accessors with `errorCode 0`. **`lvai_resave_ctl` is that call now**, and it reads the file
back so `wrappedTypeAfter` is the verdict rather than an `errorCode 0`.

**`lvai_describe_ctl` would have answered this in 200 ms and did not**, because nothing in it
compared `isTypedef` against `wrappedType`. **It does now** - `needsLabviewSave`, a file-only check
costing no LabVIEW, naming `lvai_resave_ctl` and the 1061 it prevents.

**And the FIRST run after the repair still failed, with `Error 43` from `Save All This Library.vi`.**
That is the documented "an earlier failed run left accessor VIs in memory with no path" symptom, and
the ` 4` / ` 3` suffixes on `Read Profil 4.vi` / `Write Profil 3.vi` are its tell. `lvai_close_active_project`
could not clear it either - **`Error 1019`**, the close refused while those VIs were unsaved - so the
sequence that works is: kill LabVIEW, delete the suffixed VIs, restart, reopen the project, run once.

### 15b. A GENERATED CLASS METHOD LOSES THE TYPEDEF ON ITS OWN PANE, and the repair hint is wrong

After `lvai_swap_subvis`, `Profil Setzen.vi` read **`coerced: 1`** - on the `Ofenprofil` terminal of
the `Write Profil.vi` call, and nowhere else. That is not a placeholder failure: the stub was
flattened correctly (`typedefSites: 1`, `flattened: [Ofenprofil]`, `typedefObjectsInStub: 0`). It is
the method's OWN pane. AIXML has no typedef in its grammar, so the `Profil` control it authored is
a bare cluster, and `lvai_add_class_method` retypes only the CLASS terminals.

**`lvai_coercion_dots` points the repair at `lvai_bind_typedef_constants`, which cannot reach this
case** - that tool finds each coerced source by its CONSTANT label, and here the source is a
front-panel CONTROL. The note is right for the case it was written for and silently wrong here.

The repair is the same `{LV.Control}` `Replace` gesture `lvai_bind_class_fields` uses, aimed at the
method's own pane, with the owning class saved in the same run because `Save.Instrument` alone does
not persist a `Replace` - **on a plain VI either, measured afterwards**, which is why the tool
requires the `.lvclass`. **`lvai_bind_pane_typedef` does it**: terminals found by NAME (`Controls[]`
order is not portable), `Replace`, `Save.Instrument`, `{LV.LVClassLibrary}` `Save`, and then the
SAVED FILE re-read to confirm the `.ctl` is really in it. One call, `terminals bound: 1`, and
`lvai_coercion_dots` then answered **`clean: true`, `coerced: 0`**. `lvai_coercion_dots` names both
repairs now instead of asserting the constant one. `docs/typedef-disconnect.md` §13a-§13c.

### 15c. The typedef DOES travel all the way, which is what the build set out to show

Read from the saved files, no LabVIEW involved:

| file | what it says |
|---|---|
| `Ofen.lvclass` | `Profil` -> `type: TypeDef`, `isTypedef: true`, `typedef: Ofenprofil.ctl` |
| `Write Profil.vi` | pane terminal named **`Ofenprofil`** - NI's wizard names an accessor control after the TYPEDEF, not after the field |
| the stub for it | `typedefTerminals: 1`, `typedefPath` naming the `.ctl`, `typedefObjectsInStub: 0` after the flatten |
| `Profil Setzen.vi` | `coercionDots 0` after the pane bind |
| `Profil Setzen Msg.lvclass` | `Profil` -> `isTypedef: true`, `typedef: Ofenprofil.ctl` |

So the Message Maker carries the typedef into the message payload by itself - the message class's
private data field is a TypeDef naming the same `.ctl`, with nothing asked of it.

### 15d. ONE MESSAGE CLASS PER FOLDER - two in one folder is `Error 1055`

`lvai_create_message_class`'s `directory` puts the class **directly** in the folder it is given, and
a message class always brings a `Do.vi` and a `Send Template.vi`. Giving both messages
`...\Ofen Messages` made the second answer **`Error 1055`** at `replaceActorMethod`, from
`To More Specific Class in Message Maker.lvlib:Special Replace of SubVI Node.vi` - a reference that
is invalid because the file it wanted was the FIRST message's. Every earlier build in this project
(Waage, Drucker) used `<Actor> Messages\<Method> Msg\`, which is what NI's own wizard does; the
collision only appeared because this build passed the shared folder.

The half-built class must go before retrying - `Copy Class.vi` does not overwrite - and it goes with
the project CLOSED.

### 15e. THE `.ctl` BELONGS IN THE LIBRARY, and NI's own `AddItem` relinks it

The user's correction, and it is right: `Ofenprofil.ctl` is part of this class and this library, so
it goes into `Ofen.lvlib` like the actor and its messages rather than sitting beside them as a loose
dependency. `lvai_add_to_library` with `type: "VI"` does it - the `.ctl` is an RSRC file LabVIEW
lists that way - and the whole point of going through NI's `{LV.Library}` `AddItem` plus
`Save All This Library.vi` is that library membership changes the item's QUALIFIED NAME and this
route relinks what refers to it. Writing `NI.Lib.ContainingLib` by hand does not, and §8 records that
as the silent wrecker it is.

Verified after the change, cold, past a LabVIEW restart:

| check | answer |
|---|---|
| `Ofen.lvclass` field `Profil` | still `isTypedef: true`, `typedef: Ofenprofil.ctl` |
| `privateDataBytes` | 7 277 -> 7 405, the qualified name growing by `Ofen.lvlib:` |
| `Profil Setzen.vi`, `Do.vi` | `execState 1` |
| `lvai_coercion_dots` on the `Write Profil.vi` call | `clean: true`, `coerced: 0` |
| the VI's own `VCTP` | `<Label Text="Ofen.lvlib" />` then `<Label Text="Ofenprofil.ctl" />` |

That last row is the membership showing up inside the caller's type descriptor, which is the thing a
hand edit would have left inconsistent.

**And the `.lvproj` gets no entry of its own** - the `.ctl` belongs to the project THROUGH the
library now, exactly like `Append To Log.vi`.

### 15f. What the run cost

Around 50 MCP calls, of which the two defects above account for roughly 15. No LabVIEW crash. Two
deliberate restarts: one to clear the `Error 43` state, one for the cold `lvai_exec_state` check.

## 16. ComputerMaus - the ORDER of library and messages, 2026-09-18

Two typedefs in the private data, two messages, everything through a `.lvlib`. The build worked
first time; the ONE defect is an ordering rule nothing in this repository stated, and it is the
kind that every file-level check passes.

### 16a. THE LIBRARY MUST EXIST BEFORE THE MESSAGES - measured, with a clean A/B

**Adding the actor class to a `.lvlib` AFTER its message classes were built leaves EVERY `Do.vi`
`eBad`.** Library membership rewrites the class's qualified name to
`ComputerMaus.lvlib:ComputerMaus.lvclass`, and `Do.vi` is the VI that CALLS the actor method, so its
link breaks.

**The tell that identifies it in one call is which half survives:**

| VI | after the class joined the library |
|---|---|
| `Do.vi` (calls the actor method) | **`execState 0`, eBad** |
| `Send <Method>.vi` (only enqueues) | `execState 1` |

Both message classes failed the same way, and nothing else reported it: `lvai_add_to_library`
answered `ok` with `verify.items` complete and `placedIn` correct, the `.lvlib` re-read clean, and
`lvai_describe_vi` showed the message's OWN qualified name already updated to
`ComputerMaus.lvlib:Bewegen Msg.lvclass:Do.vi` - so the relink DID reach the message class and still
left the call broken. `lvai_exec_state` was the only thing that saw it, exactly as
`lvai_add_to_library`'s own closing note warns.

**The repair is to rebuild the messages, and the A/B is clean**: same tool, same arguments, the only
difference being that the actor was a library member this time - `execState 0` before, **`1`** after,
nothing else touched. Verified again cold after a project close, and a second time on `Foerderband`,
whose `Starten Msg` was retrofitted the same way.

**So the order is: class -> methods -> LIBRARY -> messages.** If a library is being retrofitted onto
an actor that already has messages, delete the message classes and rebuild them; there is no cheaper
repair, and loading the hierarchy does not fix it (measured - a `lvai_describe_vi` that pulled the
whole hierarchy in left `execState` at 0).

**AND REMOVE THE LIBRARY'S ITEM ENTRY BEFORE DELETING THE FILES.** A `.lvlib` listing a file that is
not there opens LabVIEW's modal search dialog on load, and a modal stops the whole gRPC service. The
same rule as for a `.lvproj`, and the order is: entry out (project CLOSED), then the folder.

### 16b. What LabVIEW does for you, and what it does not

**It swaps the `.lvproj` entry itself.** After `lvai_add_to_library` plus a close, the project's
loose `<Item ... ComputerMaus.lvclass>` line was gone and `<Item Name="ComputerMaus.lvlib" ...>` was
in its place - written by LabVIEW's own save, with no edit of ours. `projectEntriesToRemove` names
them anyway, which is right: that is a prediction, not a report, and a caller who skips the close
still has to act on it.

**It does NOT list a message class in the project at all.** NI's Message Maker writes the files and
leaves the `.lvproj` alone, the same way the class provider does. Through a library that is correct
and nothing needs doing; for a loose message class the entry has to be written by hand, with the
project closed.

### 16c. The typedef route held, on a second independent build

Every step of `docs/typedef-disconnect.md` section 13 reproduced without a surprise: `.ctl` generated
with the project CLOSED (11 extract files, 4069 bytes), flags patched, `lvai_resave_ctl` taking
`wrappedType` from `Function` to `TypeDef`, `lvai_bind_class_fields` binding both fields before the
accessors, and **no `Error 1061`** from the wizard. The methods' own panes wore one coercion dot each,
`lvai_coercion_dots` named `lvai_bind_pane_typedef` as the repair, and both came back `clean: true`.

**The typedef travels all the way into the message payload**: both message classes report their field
as `type: TypeDef`, read off the saved `.lvclass`.

### 16d. `<VI>` REQUIRES a `description` attribute, and the lint does not know it

`scripts/aixml_lint.py` answered `[clean]` for a document that `ValidateAIXML` then refused with
`Error -2628 ... Line 2, Column 41, Message: missing required attribute 'description'`. The lint
checks the missing-required case for `Control`, `Indicator` and `Constant` and not for `VI` itself.
One line of friction here, and the validate path named it exactly - which is the half that already
works.

## 17. Medikament — the first build that FOLLOWED the ordering rule, and an agent that could not finish it, 2026-09-18

Two typedefs in the private data, two messages, **built by the `labview-class-generator` agent** at
the user's request — where sections 15 and 16 were driven by hand. Section 16's ordering rule was
applied forwards for the first time instead of being discovered by retrofit, and it held: **class →
methods → pane typedef binding → LIBRARY → messages**, with `Do.vi` and `Send <Method>.vi` at
`execState 1` on both messages and no rebuild of anything.

### 17a. THE AGENT CANNOT FINISH A TYPEDEF CLASS, because four tools were missing from its roster

This is the finding worth the section. `labview-class-generator`'s own description says it *"binds
`.ctl` typedef fields so they point at the file"*, and its roster held `pylv_extract`,
`lvai_describe_ctl` and `lvai_bind_class_fields` — enough to READ a `.ctl` and bind one, and not
enough to produce or repair one:

| missing tool | what it is needed for | what happens without it |
|---|---|---|
| `pylv_rebuild` | writing the patched `.ctl` back — it had `pylv_extract` and so could read but not write | a typedef `.ctl` cannot be created at all |
| `lvai_resave_ctl` | converting a fixture-route `.ctl` from `wrappedType: Function` to `TypeDef` | **`Error 1061`** from the accessor wizard, mid-Phase-3, naming nothing |
| `lvai_coercion_dots` | seeing the dot a typedef method parameter leaves | silently shipped |
| `lvai_bind_pane_typedef` | repairing it | unrepairable by the agent |

So the build was split three ways — the orchestrator made both `.ctl` files, the agent made the
class, accessors and methods, the orchestrator repaired the two panes and then did the library and
the messages. That split is not the interesting part; **the roster is.** This is the third occurrence
of the shape `CLAUDE.md` already records for `labview-vi-generator` and `labview-vi-editor`: *a
capability the definition describes and the roster withholds reads as a capability that does not
exist.* All four are in the roster now, and Phase 2b describes when to reach for them — because a
tool added to a roster with no phase explaining it is only half the fix.

`lvai_add_to_library` and `lvai_create_message_class` were deliberately NOT added. The ordering rule
means the orchestrator has to sequence those steps anyway, and both need the project open while the
`.lvproj` edits around them need it closed — which is the orchestrator's business in a build that
may have several agents in it.

### 17b. The pane binding has to come before the MESSAGES, not merely before the library

Section 16c recorded that the typedef travels into the message payload. This build shows the
direction of the dependency, because the order was chosen deliberately rather than observed: both
message classes came back with `fields: [{label: "…", type: "TypeDef"}]`, read off the saved
`.lvclass`, and the Message Maker builds that payload by cloning **the actor method's own terminal**.
Bind the pane after the message exists and the message keeps the bare cluster, with nothing
reporting a difference — the same silence the binding itself has.

So Phase 2b's `lvai_bind_class_fields` is not the whole typedef story for an actor: the FIELD binding
serves the accessors, the PANE binding serves the messages, and they are different calls against
different objects.

### 17c. `uid_parent` is required on a `<Control>`, and the root is spelled `root`

Three refusals before the first `.ctl` generated, all schema-level, all answered `[clean]` by
`scripts/aixml_lint.py`:

| written | refusal |
|---|---|
| `<VI name="Dosierung" …>` | `attribute 'name' is not declared for element 'VI'` — it is `_name` |
| `<Control … />` with no `uid_parent` | `missing required attribute 'uid_parent'` |
| `uid_parent="0"` | `lvai_check_aixml` `danglingParent` — a root-level element is `uid_parent="root"` |

The middle row widens section 16d: the cheap checkers cover the missing-required case for `value` and
the net attributes and not for `uid_parent`, exactly as they do not cover `<VI description>`. The
third row is the one worth keeping for its own sake — `0` is the sentinel for a *uid*, and reading it
as also being the sentinel for a *parent* is a plausible-but-wrong generalisation that
`lvai_check_aixml` catches and explicitly refuses to "repair", because silently reparenting to root
is the fault it exists to prevent.

Every refusal named its line and column in 5–10 ms, so the cost was three round trips rather than a
diagnosis. That is the argument against widening the lint in a hurry and for authoring against a
known-good skeleton: section 2 of `lvai_aixml_reference` has one, and reading it first would have
cost one call instead of three.

## 18. Strassenkarte — the acceptance run for §17a's roster fix, 2026-09-18

Same shape as §17 (two typedefs in the private data, two messages, everything through a `.lvlib`),
built the same afternoon at the user's request, in `C:\temp\TypedefTests`. It exists to answer one
question: §17a added `pylv_rebuild`, `lvai_resave_ctl`, `lvai_coercion_dots` and
`lvai_bind_pane_typedef` to `labview-class-generator`'s roster and wrote Phase 2b around them, and
**that fix had never been run** — the build it came out of was split three ways precisely because the
tools were missing. This file's own rule is that a fix verified only by the change alongside it is
not verified.

**It holds. The agent did the whole typedef half alone**, in one task: both `.ctl` files generated
with the project closed (11 extract files each, no `VICD`), flags patched through `pylv_rebuild`,
`lvai_resave_ctl` taking both from `wrappedType: Function` to `TypeDef` (4241→4405 and 4290→4446
bytes), `lvai_bind_class_fields` before any accessor, **no `Error 1061`** from the wizard, and both
methods' own panes repaired with `lvai_bind_pane_typedef` after `lvai_coercion_dots` found one dot
each. No orchestrator intervention inside that half and no LabVIEW restart in the whole build.

**The split that remains is the one §17a chose deliberately**, and it was the orchestrator's whole
share: the `.lvlib` (`scripts/lvai_create_actor_library.xml`), `lvai_add_to_library`, and the two
`lvai_create_message_class` calls. That is the ordering rule of §16a, not a roster gap.

### 18a. The sequence, and what each step needed

| step | project | what it did |
|---|---|---|
| agent: `.ctl` ×2, class, accessors ×8, methods ×2, pane binds | closed for the `.ctl`s, open for the rest, **closed at handover** | §17's Phase 2b, unassisted |
| `lvai_create_actor_library.xml` | **open** | `Library.Create` + folder + `AddItem` class + save |
| `lvai_add_to_library` (both `.ctl`, root) | **open** | §15e — the `.ctl` belongs to the library |
| `lvai_close_active_project` | → closed | sweep removed the helper VI |
| `lvai_create_message_class` ×2 | **open** | one folder each, per §15d |
| `lvai_add_to_library` (both Msg, `Messages for this Actor`) | **open** | `placedIn` correct for both |
| `lvai_close_active_project` | → closed | `strayVisRemoved: 0` |
| `lvai_exec_state` ×6, cold | closed | all `1` |

**LabVIEW swapped the `.lvproj` entry itself again**, as §16b records: after the first close the loose
`Strassenkarte.lvclass` line was gone and `<Item Name="Strassenkarte.lvlib" Type="Library">` was in
its place, with no edit of ours. The hand edit §14 step 4 prescribes was not needed — that step
predates the library being created while the project is open, and the prediction under
`projectEntriesToRemove` is still worth acting on for a caller who skips the close.

### 18b. The typedef reaches the message payload, on a third independent build

Read from the saved files: `Strassenkarte.lvclass` fields `Ausschnitt` → `Kartenausschnitt.ctl` and
`Letzter Abschnitt` → `Strassenabschnitt.ctl`, both `isTypedef: true`, `privateDataBytes` 8546 → 8966
as library membership grew the qualified name; `Ausschnitt Setzen Msg.lvclass` and
`Abschnitt Hinzufuegen Msg.lvclass` each carry one field of `type: TypeDef`. §17b's ordering — pane
binding **before** the messages — was applied forwards and the payloads came out typed, so the
dependency direction it states reproduces rather than being re-derived.

### 18c. What it cost

About 15 orchestrator calls beside the one agent task (65 tool uses, ~7.5 min). No crash, no restart,
no hand edit of any LabVIEW file. **An uneventful run is the point**: against §17, which paid for a
three-way split, and §15, whose two defects cost roughly 15 calls of its ~50.

### 18d. A finished class tree CAN be relocated, as one block, with one `.lvproj` edit

The user's correction after the build: the class's files should have gone into a subfolder named
after the class, not beside the `.lvproj`. Nothing in this repository had a recipe for moving a
finished class, and the obvious assumption — that it means rebuilding — is wrong.

**Every relative URL in the chain is relative to the file that holds it**, so moving the whole set
together changes none of them:

| holder | URL it writes | after the move |
|---|---|---|
| `.lvclass` → its members | `../Write Ausschnitt.vi` | unchanged — both moved |
| `.lvlib` → class, `.ctl`, messages | `../Strassenkarte.lvclass` | unchanged — both moved |
| message `Do.vi` → the actor method | across two folder levels | unchanged — both moved |
| `.lvclass` → `Actor.lvclass` | `/<vilib>/…` symbolic | unaffected by any move |
| **`.lvproj` → the `.lvlib`** | `../Strassenkarte.lvlib` | **the one edit**: `../Strassenkarte/Strassenkarte.lvlib` |

So the move is: close the project, `mv` the class, its `.lvlib`, its `.ctl` files, its member VIs
**and its messages folder** into the new directory, and rewrite that single `URL`. Measured
2026-09-18 on the §18 build: all six `execState` checks still `1`, `lvai_describe_class` unchanged
including both typedef bindings, and after reopening and closing the project LabVIEW **kept the
edited URL** rather than rewriting it — which is the real test, since its save is what undoes a
`.lvproj` edit made at the wrong moment.

**The hazard to respect is the modal.** A `.lvproj` or `.lvlib` naming a file that is not there
opens LabVIEW's search dialog on load, and a modal stops the whole gRPC service. So probe with
`lvai_exec_state` BEFORE opening the project: it opens a VI reference with no application instance,
needs no project, and answers whether the links resolve — a cheap check that cannot wedge the
service. Open the project only once it has passed.

**Leave nothing behind.** Moving the actor but not its `Strassenkarte Messages\` folder is the one
way to break this: the two would then shift by different amounts and every `Do.vi` link would need
repointing. The block is the whole class, messages included.
