# Typedefs on a generated call: the coercion dot nobody reports

Measured 2026-08-29. Everything here is about one gap, and the gap is structural rather than a
bug: the only route by which a **generated** VI can call **project-local** code loses typedef
identity on the way, and no step in that route says so.

## 1. The route, and where it leaks

AIXML refuses a `Call` to project-local code — `Error 53, Unsupported SubVI`, for a bare name, a
relative path and an absolute path alike. So a generated caller is built like this:

1. `lvai_placeholder_subvi` clones the subject's connector pane into a stub under `user.lib\LV_MCP\`
2. the AIXML calls the **stub**, which AIXML is allowed to create
3. `pylv_apply {"op":"retarget"}` points that call node at the real VI

That works. It also silently drops every typedef on the subject's pane, because **AIXML cannot
express that a control is an instance of a `.ctl`** — the export renders a typedef as the bare type
it wraps and names neither the `.ctl` nor its owning library, at any depth. The stub is therefore
cloned with `bool` where the subject has a typedef'd boolean.

The consequence is a **coercion dot** on every input you then wire a constant to. What passes
anyway:

| step | verdict |
|---|---|
| `lvai_validate_aixml` | `errorCode 0` |
| `pylv_apply` retarget + verify | `ok`, `callTargets` names the right VI |
| `lvai_run_vi_and_read_values` | runs, `error out` clean |
| `{LV.SubVI} Bad SubVI Linkage` | `false` |

Only `{LV.Terminal} Coercion Dot?` — and a human looking at the diagram — sees it. That is the same
defect class as a misplaced connector pane, which is why `lvai_generate_vi` refuses to call itself
`ok` for one.

**`pylv_route` does not catch it either**, and cannot: its Check A validates the untouched export,
which validates *precisely because* the typedef is already gone, and its Check B scans for
unsupported node families, which a typedef is not.

## 2. Strict typedefs are what make it visible

A **non-strict** typedef of a `bool` is a `bool`; wiring a plain boolean constant to it produces no
dot. A **strict** typedef requires the exact type, and that is when the dot appears.

Read a `.ctl`'s kind with `{LV.VI}` `Control VI Type` (`scripts/lvctl_kind.xml`):

| value | meaning |
|---|---|
| 0 | ordinary control |
| 1 | type definition |
| 2 | **strict** type definition |

Measured on a pair of strict typedefs (a boolean and a colour box): both `2`, both produced a dot.

## 3. Reading the pane's typedefs — `scripts/lvbd_pane_typedefs.xml`

`{LV.Control}` carries `Label`, `Is Typedef?`, `Typedef:Path` and `Typedef:VI`. Loop over
`{LV.VI}` → `Front Panel` → `{LV.Panel}` `Controls[]` and read them.

`Is Typedef?` is `uint32{not a typedef, typedef, strict typedef, class private data}`.

Measured on a five-terminal subject: **three** of five carried typedefs — and one of the three was
an **output**. That matters for what you repair: an output terminal gets no coercion dot, because
nothing is wired *into* it; the bare type travels into whatever consumes the wire instead.

`lvai_placeholder_subvi` reports this as `typedefTerminals` plus a per-terminal `typedef` /
`typedefPath`, so the caller learns it before authoring rather than after.

## 4. Finding the dots — `scripts/lvbd_coercion_dots.xml`, `lvai_coercion_dots`

`{LV.Terminal}` carries `Name`, `Coercion Dot?`, `Connected Wire` and `Type Descriptor`.

Three things about addressing, all learned the expensive way:

**A CALL NODE IS ADDRESSED BY ITS INDEX IN `All Objects[]`, NOT BY VI NAME.** A diagram may call one
subVI several times, and each call is wired separately. Matching on `VI Name` collapses them to a
single node: measured on a caller with **four** coerced terminals across two nodes, the check
reported **two** — and the same shape can report `clean` while a second node still wears dots, which
is a check tool handing out a green light it has not earned.

**A HARD-CODED index is still wrong**, and that is not a contradiction. One hard-coded to 1 read the
wanted subVI in one VI and `error 1099` in another built from the same AIXML. What is safe is an
index **enumerated and used in the same call** — the helper fills `subvis seen` with one entry per
`All Objects[]` element (empty for anything that is not a call), so the position is known in the same
pass that uses it and is never persisted. Pass node index `-1` to enumerate without addressing
anything.

The terminal is still found by `Name`.

**`{LV.SubVI} Terminals[]` is indexed by connector pane SLOT, not by terminal.** On pattern 4833 it
is sixteen entries; a measured five-terminal call occupied slots **0, 4, 9, 11, 15** and left eleven
nameless. Counting the nameless ones reports a five-terminal call as sixteen clean terminals.

`error 1099` on the `{LV.SubVI}` read is not "no such subVI": it is the subVI's **file** failing to
resolve from the caller's folder, which is exactly what a caller copied to a scratch directory looks
like. Put the test subject beside its subVI.

## 5. Repairing them — `lvai_bind_typedef_constants`

Two VI Server operations, and the obvious one is not the fix.

| operation | what it does |
|---|---|
| `{LV.Terminal}` `Create Constant` | creates a constant carrying the terminal's **exact** type, typedef and all, deriving the `.ctl` with nothing passed in — but it does **not rewire**. Measured: the diagram gained an object (6 → 7), the old bare constant stayed wired, the dot stayed `true` |
| `{LV.Constant}` `Replace`, `Path` = that `.ctl` | re-points the **wired** constant, which is what preserves the wire. Measured dot before `true`, after `false` |

So the sequence is: `Create Constant` to learn the type → read `Typedef:Path` off it → `Delete` the
throwaway → `Replace` the real one. **The caller never supplies a `.ctl` path.**

`Replace` exists on `{LV.GObject}`, so no narrowing cast is needed to call it; `{LV.Constant}` is
the right class for everything else, carrying `Is Typedef?`, `Typedef:Path`, `Label`, `Terminal`,
`Value`, `Discon Typedef` and `Update Typedef` generically instead of per constant class.

### Two traps around `Replace`

**It invalidates the reference it was called on.** The next Property Node on that reference answers
`error 1055`, and a label written afterwards is silently lost — the write simply never runs.

**Its return value cannot be consumed.** Capturing the Invoke Node's `Replace` output made
generation fail with *"the type of the source is void"* at three unrelated nodes, including one
twenty lines earlier; the identical shape on `Create Constant` works. So read nothing from the
constant after the replace. The proof that the bind landed is the **terminal's** `Coercion Dot?`,
whose reference does survive.

### The verdict is a sweep, not the repair helper's own reading

The repair helper matches its subVI by `VI Name`, and that is sound — it uses the node only to
derive the terminal's **type**, which is a property of the callee's pane rather than of the call
node, so every node of that name yields the same typedef. What one node cannot speak for is
**coercion**, which is per node and per wire.

So `lvai_bind_typedef_constants` runs the coercion sweep before and after the whole batch and
reports `coercedBefore` / `coercedAfter` / `stillCoerced`. Its per-terminal rows say what was found
and which `.ctl` was derived; they are not the verdict. Measured: with the verdict taken from a
single node, binding a second call's constants reported them as *already clean* although the
`Replace` had just landed — the reading described the wrong node. `coercedAfter: null` means the
sweep could not run, and then nothing at all should be concluded.

### The constant must carry a label

The repair finds each constant by its own `Label` → `{LV.Text}` `Text`, because two boolean
constants on one diagram are otherwise indistinguishable and index order is not stable.

**AIXML's `_name` on a `Constant` becomes that constant's block diagram label** — measured. So the
authoring rule is: *name every constant you wire into a subVI call after the terminal it feeds.*

```xml
<Constant _name="Borkenkaefer" type="bool" value="false" outputs="value:60.value" uid="60" uid_parent="root"/>
```

A constant created by hand in the IDE usually has no label at all, and cannot be found this way.

## 5a. A class INSIDE a cluster - the caller's cluster after the swap (2026-10-11, not yet measured)

`lvai_placeholder_subvi` writes a class that is a cluster ELEMENT as a `path` element of the same
cluster - `cluster{ref{UDClassInst}.hardware,uint32.intervalMs}` becomes
`cluster{path.hardware,uint32.intervalMs}`, through nested clusters and arrays of clusters - and
lists each under `classElementsInClusters` as `{terminal, element}` (`inner.hw`, `devices[].hw`).
Until that day the whole terminal became a scalar `path`, because the class test looked for
`UDClassInst` anywhere in the type string, so a caller authored against the socket had no cluster to
wire. A class inside a refnum payload, or an array of classes outside any cluster, still collapses
the whole terminal as before.

The swap re-types the wires; it does NOT re-type the CALLER's own cluster source, which still
carries the `path` element where the real subVI wants the class - the same leak §1 describes for a
typedef, one level down. Two follow-ups, by where the cluster comes from:

| caller's source | follow-up |
|---|---|
| a front-panel control | `lvai_bind_pane_typedef` - the constant-label route of §5 cannot reach a control |
| a cluster constant | `lvai_swap_subvis` with `constantsJson` `[{"label":"<constant label>","class":"<path to the typedef .ctl>"}]` |

The second row is `{LV.Constant}` `Replace` pointed at a `.ctl` rather than a `.lvclass`, which turns
a bare cluster constant into an instance of that typedef. **It was observed in a field run and is
not measured in this repository** - in particular it is not established what happens to the
constant's value, or to a cluster the `.ctl` does not match. Check the result with
`lvai_coercion_dots` and `lvai_exec_state` rather than trusting `constantsSwapped`.

## 6. Where the check belongs

In `pylv_apply`'s **verify** step, not in `lvai_generate_vi`. At generation time the call still
targets the stub, so no dot can exist yet; the dot is created by the retarget. `pylv_apply` now
reports `coercionDots` and, when non-zero, `coercedTerminals`.

The probe opens the VI **without** an application instance, deliberately — `pylv_apply` has just
closed the project, so anything reached through `Project:Active Project` would answer `error 1055`.
The repair is the opposite: it edits the copy the project holds and therefore *needs* the project
open and active.

## 7. What pylabview cannot do here

Nothing. A diagram constant that is not already a typedef instance has no typedef heap object to
re-point, and pylabview composes nothing from scratch. Re-pointing one that already exists is not
the cheap label substitution it looks like either — the typedef's file name sits twelve times in
`VCTP` and three more in `VITS`, a block pylabview copies through unparsed. With `Replace` available
there is no reason to try.

## 8. Authoring notes that cost time

**`Error 1051` on generation is about the destination FILENAME, not the AIXML's `_name`.** The
tool's own hint says "generate under a fresh name"; changing `_name` alone was measured to give the
identical error, and only a different *file name* went through. A failed validation occupies the
name for the rest of the LabVIEW session. Workaround without restarting: generate to a scratch file
name in the same folder and copy it over the target — LabVIEW derives a VI's name from its file
name, and no name is stored in the file.

**AIXML rejects XML comments** with `Error 42, Generic error`. Notes belong in the `description`
attribute.

**Do not accumulate across nested For loops with shift registers.** A nested pair, each accumulating
a string, was measured to keep only the **last** outer iteration — which reads as a plausible report
of one subVI while silently dropping the rest. Use two *sequential* loops with indexed output
tunnels instead; that shape is what both helpers here use.

**Do not check this repo's C# with `dotnet build -t:Compile`.** It produces an assembly **without**
the embedded documents, and the next incremental test run trusts it — about ninety embedded-resource
tests then fail for reasons unrelated to the change under test. Delete
`src/LabVIEWMCP/obj/<config>/<tfm>/LabVIEWMCP.dll` to recover. Build to a scratch output path
instead when the exe is locked by a running server:

```
dotnet test tests/LabVIEWMCP.Tests/LabVIEWMCP.Tests.csproj -p:BaseOutputPath=<scratch>\
```

## The sweep does not descend into structures — and a PARTIAL sweep passes as clean

Measured 2026-09-09. `lvai_coercion_dots` enumerates through exactly one property chain:

```
{LV.VI} read+Block Diagram  ->  {LV.Diagram} read+All Objects[]
```

That is the TOP diagram only. A Case Structure appears in `All Objects[]` as one object, and
every call inside its frames is invisible.

**The damaging part is not the blindness, it is the verdict.** On a fixture with three calls to
the same stub — one at top level, two inside a Case frame inside a While loop — the tool answered

```
subViCalls: 1, terminalsChecked: 3, coerced: 0, clean: TRUE
note: "No coercion dot on any subVI call terminal. Nothing to repair."
```

A clean bill of health for a VI where two of three calls were never looked at. The
`examinedNothing` guard only fires when the sweep finds *nothing at all*; a partial sweep is
indistinguishable from a complete one. For the placeholder-plus-retarget route this is the
dangerous direction: "nothing to repair" is exactly the answer that makes a caller skip
`lvai_bind_typedef_constants` while dots sit in the frames.

### The invariant any fix must keep: REPORT, never GATE

A coercion dot is a deliberate intermediate state. The route inserts stub VIs and `.ctl`s whose
panes carry the bare underlying type, swaps them with pylabview, and only then repairs the dots.
So `coerced > 0` is the EXPECTED reading between those steps, and anything that refuses to
proceed on it breaks the only route by which a generated VI can call project-local code.

Today nothing gates: `pylv_apply`'s verify records `coercionDots` as a count plus
`coercedTerminals` and leaves `ok` alone. **Keep it that way.** Note the temptation grows once
the sweep descends — a tool answering `subViCalls: 0` invites nobody to gate on it, one
answering "40 calls, 12 coerced" does. `docs/class-method-tooling.md` §4m records what happened
the one time a check on this route gated `ok`: `wiringLost` was wrong every time it fired and
was retracted to reporting-only inside a day.

### The acceptance fixture

Calls to one stub at top level and inside a Case frame inside a While loop — the same stub three
times, because the tool's contract is one entry per call NODE, not per VI name. Any
`user.lib\LV_MCP\LVMCP *.vi` stub works as the target; take its terminals from
`lvai_vi_terminals` first, because the stubs are hashed by signature and two of them do not
share one. Generate with `measurePane: false` — it is a probe with no pane worth checking.

### Why the descent is not a two-node addition

The obvious change — a second `To More Specific Class` to `{LV.Structure}` plus a
`read+Diagrams[]` in the existing enumeration loop — was tried and **broke the enumeration that
already worked**: `subvis seen` came back as N empty strings, so even the top-level call was
lost, while the untouched AIXML regenerated to the same path still found it. Bisected: with only
the new `ref{LV.Structure}` constant added and no new nodes, the helper works
(`subViCalls: 1, terminalsChecked: 3`). So the constant is innocent and the fault is in the added
nodes — the fan-out of the object reference to a second downcast, or the second indexed output
tunnel. Cause not established; the helper was reverted rather than left in that state.

And a true recursion cannot simply be nested loops: this helper's own description records that
"a nested pair accumulating through shift registers was measured to keep only the LAST outer
iteration". The two designs that respect it:

- **One While loop, two worklists** — one of diagrams, one of objects — with a Case inside
  choosing which to pop. No nesting, arbitrary depth, everything stays in the helper.
- **Recursion in C#**, with the helper taking a diagram ADDRESS (`"3.0"` = object 3, diagram 0)
  instead of only a node index. Costs one helper run per diagram.

Either way `nodeIndex` stops being unique — an index into `All Objects[]` is per diagram — so the
address becomes compound or a flat ordinal over a traversal both passes must walk identically.
That contract, not the descent, is the bulk of the work.

## The sweep read NOTHING while a project held the VI (2026-10-13)

**Symptom.** With the caller's project open, `lvai_coercion_dots` answered `subViCalls: 0` on a
caller that has one call, and so did both sweeps inside `lvai_bind_typedef_constants` - which
NEEDS the project. Its name resolution then had nothing to resolve against (a misspelt
`subViName` was not refused but passed on as given), and its verify step said it could not run
while the bind had worked. Measured on one caller: with no project, three sweeps in a row read the
call; with the project open, one did and two read nothing.

**Two causes, one after the other.** The helper opened the VI with no application reference - a
SECOND copy in the helper's own instance - while the IDE held the real one in the project's. It now
opens the VI in `Project:Active Project` -> `Application` when a project is active (the route
`lvbd_bind_constant.xml` always took) and in its own instance only when none is, which keeps the
`pylv_apply` case, project closed, working. That made reads correct but not reliable: eight sweeps
read `1 0 0 1 0 1 0 1` calls, every failure EMPTY and never wrong, and with the caller held open in
the IDE the same eight read 1 each time. The VI is loaded and unloaded on each helper run and a run
that catches it in between reads nothing - the "first open reads empty" trap CLAUDE.md records. So
an empty run is retried, up to three tries (`TypedefTools.SweepAttempts`), and
`lvai_coercion_dots` reports `retriedRuns`. Measured after both: sixteen sweeps with the project
open all correct, at most one retry each; `Gibtsnicht.vi` refused as `subViNotOnDiagram`; the bind
`ok: true` with `coercedBefore: 1`, `coercedAfter: 0`.

`lvai_exec_state` opens its VI without an application reference too, and has not been measured
against this.
