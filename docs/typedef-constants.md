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
