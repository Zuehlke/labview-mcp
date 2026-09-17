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
`lvai_add_class_method` → `lvai_swap_subvis` → **a forced LabVIEW resave** → one
`lvai_create_message_class` per method. Result: `Aquarium.lvlib` holding the actor and three message
classes, every VI `execState 1` after a project close.

### 8a. A SWAPPED VI IS NOT FINISHED UNTIL LabVIEW HAS SAVED IT — the `LIvi` block

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
