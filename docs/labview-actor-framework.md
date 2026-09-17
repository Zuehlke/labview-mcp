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

So the next experiment is to run steps 3-11 ourselves and **omit step 2**. The open question is
whether LabVIEW's scripting of those replacements needs the panel or diagram window open at all; if
it does, the remaining option is to reach the IDE's own application instance for the whole helper
rather than only for the VI reference.

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
