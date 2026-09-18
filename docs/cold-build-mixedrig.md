# Cold build: one project carrying every typedef variant — 2026-09-18

The question this run exists to answer: **does `lvai_flatten_typedefs` cost time, and if not, where
does the time actually go?** So it is a build of one small project that carries every variant the
flatten route has been taught, with the clock on every step.

Driven through **one server process and one LabVIEW**, so what is measured is machine time — the
server, LabVIEW and pylabview — with no model-turn latency mixed in. Round trips are counted
separately, because `docs/workflow-economics.md` measures a turn at a median of **7.1 s** and that,
not the milliseconds here, is the dominant cost of a real session.

**LabVIEW was restarted first.** §12e-a of `docs/typedef-disconnect.md` measured a long-lived
instance producing `.ctl` files that are eBad ON DISK, which would have made the fixture build fail
for a reason that has nothing to do with what is being measured.

## The rig

| file | variant it contributes |
|---|---|
| `Mode.ctl` | a strict typedef enum |
| `Limits.ctl` → `Mode.ctl` | a typedef holding another typedef — **two levels** |
| `Reading.ctl` | a **plain (non-strict)** typedef |
| `Acquire.vi` | pane with `Limits` (nested), `Samples` (an **array** whose element is a typedef), `Reading` (plain) |
| `Sensor.lvclass` | a class, with `Mode.ctl` bound onto a private data field |
| `Read Name.vi` | an accessor, so a pane with **class terminals** |

## Where the 30.5 s went

27 steps, **24 round trips**, 30 519 ms of wall clock against 20 729 ms inside the tools.

| phase | ms | share |
|---|---|---|
| 1 — three typedef `.ctl`s and the nested bind | 3 096 | 10 % |
| 2 — `Acquire.vi` and its three binds | 788 | 3 % |
| **3 — the CLASS: create, bind the field, accessors** | **15 099** | **49 %** |
| 4 — the three placeholder modes | 9 804 | 32 % |
| 5 — caller plus retarget | 1 733 | 6 % |

**Half the build is the class phase, and it is the half that FAILED.** The typedef half — every
variant this work was about — is 10 % of the time and worked first try.

## What the flatten actually costs

The same pane, three ways, measured with the flat-copy cache warm:

| | wall | the flatten's own `elapsedMs` |
|---|---|---|
| A  `flattenTypedefs: false` | 2 307 ms | — |
| B  flag route, cache **warm** | 2 436 ms | **873 ms** |
| B  flag route, cache **cold** (first run) | 5 918 ms | ~5 600 ms |
| C  disconnect route | 3 306 ms | **1 766 ms** |

All three produced `typedefSites: 3`, `subjectTypedefObjects: 4`, `typedefObjectsInStub: 0`.

The 3-against-4 is the unit difference §12f documents, seen doing its job on a real pane: three
outermost sites (`Limits`, the `Samples` element, `Reading`) against four typedef instances in the
file (the fourth is `Mode` inside `Limits`).

**So the flatten costs about 0.9 s warm — an eighth of one model turn.** The cold 5.6 s is paid once
per typedef chain, ever: the copies are cached on disk under a hash of the source's content. Against
the ~7.1 s a single extra round trip costs, asking the question "should I flatten this?" is more
expensive than flattening.

**And the flag route should stay the default.** It is twice as fast as the disconnect route
(873 ms against 1 766 ms) and needs nothing of the caller, where disconnect needs a project open and
active and opens the stub in the editor. The earlier guess that disconnect would be the faster one —
it copies no files — is refuted: what it saves in copying it spends on LabVIEW round trips.

## End to end, which is the point

`Acq Caller.vi` was authored against the stub, generated, and retargeted onto `Acquire.vi`:

```
callTargets   ["Acquire.vi"]
execState     1, eIdle
coercionDots  0
```

A two-level typedef, an array of a typedef and a plain typedef all survive into a caller that
LabVIEW can run, with no coercion dots. And on the accessor, `classTerminals: 2` with the
**conditional closing note** firing correctly — *"repoint the call with lvai_swap_subvis, NOT with
the `retarget` below"* — which is the fix made hours earlier, working in a real build.

## What to optimise, and it is not the typedef tooling

### The class phase is the cost and the fragility — the correctness half is FIXED

`lvai_create_accessors` answered **`errorCode 1026`**, created the VIs on disk and wrote **no member
list** (`membersAfter: 0`). **The A/B cleared the typedef work first**: a second class with the same
fields and NO typedef bound onto any field failed identically, and so did a class in a pristine
project — so neither the typedef field nor the project's contents.

**The cause was in our own helper, and it only fires for `accessUi` other than the default.**
`lvai_create_accessors.xml` reads BOTH of the wizard's outputs — `Read VI` and `Write VI` — and
writes a VI Description on each, on ONE error chain:

```
1010 CLSUIP_CreateNewAccessor  ->  1011 (Read VI)  -> … -> 1016
                               ->  1020 (Write VI) -> … -> 1040 -> Save All This Library
```

Ask for `"Read"` and the `Write VI` reference is invalid, `1020` answers **1026**, and the error
rides the wire into `Save All This Library.vi`, which is what writes the member list. So the
accessors land on disk and the class never learns about them. Every earlier cold build used the
default `"R/W"` and never met it — measured as the control, `R/W` answered `errorCode 0` with
`membersAfter: 6` while `"Read"` on the same rig answered `1026` with `0`.

**The fix takes the un-asked-for branch out of the chain**, and needs no Case structure: the helper
already receives `Access UI` as the wizard's own enum (`0 Read, 1 Write, 2 R/W`), so two `Not Equal?`
comparisons give `wantRead`/`wantWrite`, and four `Select` nodes pick between the real chain and the
one that bypasses the dead branch — for the error, and for each reported path.

| `accessUi` | before | after |
|---|---|---|
| `"Read"` | `1026`, `membersAfter: 0` | **`errorCode 0`, members written**, `writePath` empty |
| `"Write"` | untested, same shape | **`ok: true`**, member written |
| `"R/W"` | worked | unchanged |

`ok: false` still appears on a re-run over a class that already has accessors — that is the
`mangledAccessors` guard doing its job, because NI's wizard appends a number rather than refusing a
name that exists. Not this defect.

### Which project is active is now REPORTED

Two arms of that A/B were lost to `Error 1055` with nothing saying what LabVIEW was looking at.
`scripts/lvai_active_project.xml` now reads `{LV.Project} Path` as well, so `lvai_open_file` answers
`activeProjectPath` and `activeProjectCheck` names the project instead of saying "a project is
active".

**A mismatch is annotated, not enforced, and that is deliberate.** The obvious next step was to call
"a different project is active" a failure — but probed the same day, opening project A while B was
active SWITCHED correctly, so the case has never been observed. A guard that refuses a working call
on an unproven rule is worse than the silence it replaces, and two paths can differ by spelling
alone. `activeProjectPathDiffers` reports it; `projectBecameActive` is untouched.

### Three documented project-state hazards fired in one build

All three are already written down, and all three still cost this run time:

- **A class created by `lvai_create_class` was missing from the `.lvproj`.** `Sensor2.lvclass` was on
  disk and not in the file, so the accessor wizard answered `Error 1055` with `classIndex -1`. This
  is the "a later close saved LabVIEW's older in-memory copy over the file" hazard. Writing the entry
  by hand **with the project closed** fixed it.
- **A project opened and closed earlier in the session does not become active again on a bare open.**
  `lvai_open_file` answered in **36 ms** with `Active Project` still pointing elsewhere; a
  `lvai_close_active_project` first, then the open, took 1 641 ms and worked. Two arms of the A/B
  were wasted on this before it was spotted.
- **A `<userlib>/LV_MCP/LVMCP Stub …` entry was adopted into the `.lvproj`** by a save, exactly the
  case `lvai_close_active_project`'s `projectSweep` exists for — and it was not swept, because this
  run never passed `projectPath`.

### The round-trip arithmetic

24 round trips for this build. At the measured 7.1 s median turn that is about **170 s of latency
against 30 s of machine time**, a ratio of roughly 5.6 : 1 — worse than the repository's 3.6 : 1
average, because almost every step here is a cheap call. **The optimisation lever is the number of
calls, not their duration**, and the three biggest candidates are: the `.ctl` fixture build (three
MCP calls per typedef — generate, extract, rebuild — around a flag patch that needs no LabVIEW at
all), and the class phase's open/close dance.
