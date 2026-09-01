# Writing LUnit unit tests from this server

LUnit is Astemes' xUnit framework for LabVIEW. It was installed on this station on 2026-09-01 and
everything below was measured that day against **LabVIEW 2026 (32-bit)**, LUnit's own examples and a
class built end to end from nothing. Where a claim is not measured it says so.

Every earlier document and the `labview-lunit-unit-test` agent said LUnit was **not installed** and
that nothing about its vocabulary had ever been measured here. Both statements were true on
2026-08-29 and are now false. This file replaces them.

## 1. Where it lives, and why the first search found nothing

LUnit installs into the **32-bit** LabVIEW tree:

```
C:\Program Files (x86)\National Instruments\LabVIEW 2026\
```

A first sweep of `C:\Program Files\National Instruments\LabVIEW 20*` — the 64-bit path — returned
nothing for `*lunit*`, `*astemes*` and every addons folder, which reads exactly like "not
installed". The running LabVIEW is the 32-bit build, because that is the one hosting the AI gRPC
service (`lvai_ensure_labview` prefers it deliberately). **Resolve the install root from the running
process, never from a guess:** `Get-Process LabVIEW | Select-Object Path`.

**Why that sweep found nothing: there is no 64-bit LabVIEW on this machine.** All four of
`C:\Program Files\National Instruments\LabVIEW {2023,2024,2025,2026}` exist and each contains
**exactly one entry, `resource`** — no `LabVIEW.exe`, no `vi.lib`, no `user.lib`. They are leftover
stubs. So the empty answer was correct about those folders and said nothing about the machine.

> **RETRACTION, same day.** This paragraph first blamed the empty listing on **the Bash tool's
> sandbox filtering `C:\Program Files`**, and that claim was false. Re-measured with both tools:
> PowerShell returns the identical one-entry listing for all four folders, and Bash reads the whole
> **32-bit** tree — 22 entries at its root — and
> `…\LabVIEW 2026\user.lib\LV_MCP\` with correct sizes and timestamps. Bash does not filter
> `Program Files`. The evidence that would have prevented the wrong conclusion was already in the
> same session: a PowerShell `Test-Path` on `…\LabVIEW 2025\vi.lib\addons` had *also* come back
> false. **Two tools agreeing is what settles a negative; one tool plus a hypothesis is not.**

| What | Where |
|---|---|
| base classes | `vi.lib\Astemes\LUnit\Test Case.lvclass`, `Test Suite.lvclass`, `LUnit Runnable.lvclass` |
| assertions | `vi.lib\Astemes\LUnit\Palette\` and `Palette\Advanced Assertions\` |
| execution API | `vi.lib\Astemes\LUnit\Palette\API\LUnit Execution API.lvclass` |
| templates | `resource\Astemes\LUnit\Templates\{Test Case, Parameterized Test Case, Inheriting Test Case, Global Fixture Test Case}` |
| project provider | `resource\Framework\Providers\LUnit\`, `LUnit Project Provider.lvlib` |
| Tools-menu VIs | `project\LUnit\` — `LUnit UI.vi`, `New Test Case.vi`, `Run All Tests in Project.vi` |
| examples | `examples\Astemes\LUnit*` — 5 example trees, registered through `examples\exbins\Astemes-LUnit.bin3` |
| palette `.mnu` | `menus\Categories\functions_Astemes_lib_LUnit.mnu` plus two `_functions_astemes_lib_lunit_*.mnu` |

**Both caches must be refreshed after installing it**, and both were:

- `lvai_example_index` with `refresh=true`: **609 → 635 examples, 26 of them LUnit.**
- `lvai_palette_index` with `refresh=true`: **16 LUnit palette entries** out of 2 835.

Unlike Caraya — whose `.mnu` files live under `vi.lib\addons\_JKI Toolkits\dynamic_palette\` and are
therefore invisible to the palette index — LUnit's `.mnu` files are in `menus\Categories\`, which
**is** a scan root. So LUnit really is in the palette index, and a query for it answers.

## 2. What a test case actually is

Measured from `lvai_describe_class` on the base class and on NI's own examples.

- A **test case is a `.lvclass` whose parent is `Test Case.lvclass`**. Nothing else marks it.
  `Test Case.lvclass` itself derives from `LUnit Runnable.lvclass` and carries 17 294 bytes of
  private data.
- A **test method is a public, static-dispatch member VI** of that class. Since LUnit 2.0 the name
  no longer has to begin with `test`; any public member is a test. Helper VIs must be `private` or
  the framework will run them.
- The **connector pane is pattern 4815** (4-2-2-4, 12 terminals), measured on
  `Test Passing.vi`:

  | terminal | conIdx |
  |---|---|
  | `<ClassName> In` | 11 (top left), `required` |
  | `error in (no error)` | 8 (bottom left), `recommended` |
  | `<ClassName> Out` | 3 (top right), `recommended` |
  | `error out` | 0 (bottom right), `recommended` |

- `Setup.vi`, `Teardown.vi`, `Run.vi`, `Name.vi` and `Suite.vi` are **dynamic dispatch** members of
  the base class and are overridden, not created. `Setup.vi` in NI's `Dummy` example stores state
  with a `Bundle By Name` into the class private data, which is how a fixture reaches a test method.
- The **smallest working test case is two files**: `Passing Test Case.lvclass` and
  `Test Passing.vi`. No Setup, no Teardown, no suite file.

**The assertions are members of `Test Case.lvclass`, not free-standing library VIs.** That is the
single most load-bearing fact on this page, because it means every assertion takes the test case
object on a wire:

| Call target | required inputs |
|---|---|
| `Test Case.lvclass:Pass If.vi` | `LUnit Test Case In` |
| `Test Case.lvclass:Fail.vi` / `Skip.vi` | `LUnit Test Case In` |
| `Test Case.lvclass:Pass If Equal.vim` | `LUnit Test Case In`, `Expected`, `Actual` |
| `Test Case.lvclass:Pass If Error.vi` / `Fail If Error.vi` | `LUnit Test Case In` |
| `LUnit Advanced Assertions.lvlib:Pass If Matching String.vi` | `LUnit Test Case In`, `String`, `Regular Expression` |
| `LUnit Execution API.lvclass:LUnit Run Tests.vi` | `Project, Library, or Test Class Path`, `Report Output path`, `Run Tests In Parallel? (T)` |
| `LUnit Execution API.lvclass:LUnit Run Test Case.vi` | `Execution API In`, `Test Case` |

Note the terminal is called `LUnit Test Case In` on every assertion, while the *control* on the test
method's own pane is named `<ClassName> In`. The two are not the same string and both matter.

Full assertion set on the palette: `Pass`/`Fail`/`Skip`, `Pass If`/`Fail If`,
`Pass If Equal`/`Fail If Equal` (`.vim`), `Pass If Error`/`Fail If Error`,
`Pass If Specific Error`/`Fail If Specific Error`, `Pass If Matching String`/`Fail If Matching
String`, `Pass If In Collection`/`Fail If In Collection` (`.vim`).

**Every one of those targets resolves from a bare AIXML `Call`** — measured with a throwaway
`ValidateAIXML` carrying five of them, whose only complaints were unwired required inputs, which is
the signature of a *resolved* target. Two things in that list are worth calling out because they
contradict rules that hold elsewhere:

- **A `.vim` malleable VI resolves as an AIXML `Call` target.** `Test Case.lvclass:Pass If Equal.vim`
  validated. Nothing in this repository had established that before.
- **A provider VI under `resource\Framework\Providers\` resolves too** —
  `LUnit Project Provider.lvlib:LUnit_Create New Test Case.vi` validated, with required inputs `Path`
  and `Application`. So "findable by name" reaches `resource\` as well as `vi.lib`, `user.lib` and
  `LVAddons`. This is the same route `lvai_create_accessors` already uses for
  `MemberVICreation.lvlib:CLSUIP_CreateNewAccessor.vi`.

By contrast `project\LUnit\New Test Case.vi` is a **dialog launcher with no usable pane** — one
`Debug?` boolean and nothing else, the same shape as DQMH's menu VIs. It cannot be driven.

## 3. The wall, and the crack in it

**AIXML cannot express a class-typed terminal.** Measured directly:

```
Error 53 … Control with type=UDClassInst is not supported
             Indicator with type=UDClassInst is not supported
```

So a test method — whose pane *is* a class — cannot be authored by AIXML in one step. The
established remedy from `docs/lvclass-interfaces.md` §3 is to author `path` stand-ins and retype
afterwards. Applied here that runs straight into a second wall:

```
SubVI 'Test Case.lvclass:Pass If Equal.vim': An unsupported data type is wired to this subVI node.
You have connected two terminals of different types.
  The type of the source is Test Case.lvclass [LabVIEW Class].
  The type of the sink is file path.
```

**`ValidateAIXML` type-checks subVI wiring.** A `path` stand-in cannot receive the assertion's
class-typed output, so the file is refused — and it is refused for `Pass If.vi` as much as for the
malleable `Pass If Equal.vim`, because the fault is the class wire, not malleability.

**The crack: `ConvertAIXMLToVI` is more permissive than `ValidateAIXML`.** Measured 2026-09-01 on
the same file, in the same minute:

| RPC | answer |
|---|---|
| `lvai_validate_aixml` | `errorCode 1`, the type errors above |
| `lvai_convert_aixml_to_vi` | **`errorCode 0`**, 9 058 bytes written |

This is not a small detail and it inverts the rule the rest of this repository runs on — "validate
first, it is the cheap failure path". For a class-typed pane the validator is *stricter than the
generator*, so the sequence is: **skip validation, convert, then repair.** The VI LabVIEW writes is
broken in exactly one way — two wires whose sink is a `path` — and retyping the two controls makes
it whole.

The practical consequence for tooling: `lvai_generate_vi` cannot be used for a test method, because
it runs validate first and stops there. Call `lvai_convert_aixml_to_vi` directly.

## 4. The route, measured end to end

Verified twice on `C:\temp\lunit_demo`, once for each of two test methods, ending in a real LUnit run
with a working negative control.

### 4.1 Create the test case class

`lvai_create_class` needs no change at all — point `parentClassPath` at LUnit's base class:

```
className       Temperature Test
directory       C:\temp\lunit_demo\Temperature Test
projectPath     C:\temp\lunit_demo\LUnitDemo.lvproj
parentClassPath C:\Program Files (x86)\...\vi.lib\Astemes\LUnit\Test Case.lvclass
fields          string.Fixture Note
```

Result: `inheritsFrom: "Test Case.lvclass"`, 5 529 bytes of private data, `fields added: 1`,
`parent opened: true`. The private data control is NI's own, so it compiles.

### 4.2 Author the test method with `path` stand-ins

The shape, with the two class terminals as `path`:

```xml
<Control _name="Temperature Test In" conIdx="11" connection="required" type="path" .../>
<Control _name="error in (no error)" conIdx="8" connection="recommended" type="cluster{...}" .../>
  … your computation …
<Call inputs="LUnit Test Case In:184.value,Pass?:313.x = y?,Message:306.value,error in (no error):10.value,Description:305.value"
      outputs="LUnit Test Case Out:169.LUnit Test Case Out,error out:169.error out"
      target="Test Case.lvclass\3APass If.vi" uid="169" uid_parent="root"/>
<Indicator _name="Temperature Test Out" conIdx="3" connection="recommended" type="path" .../>
<Indicator _name="error out" conIdx="0" connection="recommended" type="cluster{...}" .../>
```

Name the two stand-ins exactly `<ClassName> In` and `<ClassName> Out` — step 4.4 finds them by name.

### 4.3 Convert, then fix the pane PATTERN

`lvai_convert_aixml_to_vi` takes **no `panePattern` argument**, so the VI is stamped with the
station default from `LabVIEW.ini` — **4833 here**, where LUnit needs 4815. The conIdx values
written for 4815 then land on the opposite edges, and `lvai_connector_pane` reported exactly that:
`error in` on the output edge, `error out` in the top-left corner.

The fix is the **pattern**, not the assignment — no terminal moves and no caller changes:

```
pylv_apply  viPath=<the test method>  operationsJson=[{"op":"conpane","pattern":4815}]
```

`pattern 4833 -> 4815, 12 slots, no conIdx changed`. A re-measure then reads
`VERDICT: the pane follows NI's style guide - Nothing to change`, with the same four numbers as
NI's `Test Passing.vi`.

**Do this while the project is CLOSED.** `pylv_apply` closes it for you, which is also what the next
step needs to have happened before generation.

### 4.4 Retype the terminals and make the VI a member

`scripts/lvlu_add_test_method.xml` — generate it once, then run it per test method with
`lvai_run_vi_and_read_values`:

| input | value |
|---|---|
| `vi path` | the test method `.vi` |
| `class path` | the `.lvclass` |
| `class terminal names` | `Temperature Test In\|Temperature Test Out` — **pipe**-separated |
| `vi name in memory` | `Test Boiling Point.vi` — the bare name |

**A project must be open and active**, because `LVClass.Open` is wired to the IDE's application
instance so it reaches the class the project holds rather than a second copy beside it. With no
project open that hop answers `Error 1055`.

What the helper does, in this order — and the order is the whole point:

1. `{LV.Application}` `Project:Active Project` → `{LV.Project}` `Application`
2. `Open VI Reference` on the test method, in that instance
3. `{LV.VI}` `Front Panel` → `{LV.Panel}` `Controls[]`, then per control
   `{LV.Control}` `Terminal` → `{LV.Terminal}` `Name`
4. for each wanted name, `{LV.Control}` `Replace` with the `.lvclass` `Path`
5. `{LV.LVClassLibrary}` `AddItemFromMemory`, parameter **`Name`**, the VI's bare name — **first**
6. `{LV.VI}` `Save.Instrument`, path unwired — **second**
7. `{LV.LVClassLibrary}` `Save` — **third**
8. every refnum closed

Steps 5-7 must be in that order. Saving the VI before it is a member writes it with no
owning-library link; LabVIEW then sees library and VI disagree, marks the **library** broken, and the
library blocks every VI it owns — which surfaces as `Error 1003` on unrelated members. That is the
defect diagnosed at length in `docs/lvclass-interfaces.md`; this helper encodes its fix.

`AddItemFromMemory`'s parameter name is **`Name`** and `Save` exists on `{LV.LVClassLibrary}`. Both
were settled by `lvai_validate_aixml`, which returned `errorCode 0` on the first attempt — worth
recording because `{LV.LVClassLibrary}` is absent from the VI Server catalogue (`docs/vi-server-classes.tsv`
lists the class, `docs/vi-server-methods.tsv` has not one method row for it), so the reference cannot
answer and validation is the only oracle.

Measured result, both runs:

```
terminals retyped   2
terminal names seen ["Temperature Test In", "error in (no error)",
                     "Temperature Test Out", "error out"]
open vi / class open / add member / save vi / save class / error out   all 0
```

And the finished VI exports as NI's own shape — this is the verification that matters:

```xml
<VI _name="Temperature Test.lvclass:Test Boiling Point.vi" …>
  <Control _name="Temperature Test In" conIdx="11" connection="required" type="ref{UDClassInst}" …/>
  …
  <Indicator _name="Temperature Test Out" conIdx="3" connection="recommended" type="ref{UDClassInst}" …/>
```

**No `SetWireRule` was needed.** `docs/lvclass-interfaces.md` §3 records that AIXML terminals come
out as wire rule 1 (optional) and need `SetWireRule` to reach `recommended`. That did not happen
here: the `connection="required"` and `connection="recommended"` written into the AIXML **survived
the `Replace`** and came back unchanged in the export. The difference from §3 is that §3's VIs were
*dynamic dispatch* overrides needing rule 4, which AIXML has no attribute for; a static-dispatch
test method needs only what `connection=` already expresses.

### 4.5 Run it

`scripts/lvlu_run_tests.xml` wraps `LUnit Execution API.lvclass:LUnit Run Tests.vi`. **This half
needs no retyping, no membership step and no VI Server** — the runner takes a path and returns a
string, so it is ordinary AIXML and `lvai_generate_vi` handles it (validate included, `errorCode 0`).

`test path` accepts a `.lvproj`, a `.lvlib` or a `.lvclass`. Pointing it at the class:

```
Output      Temperature Test.lvclass:Test Freezing Point.vi: Failed
            Temperature Test.lvclass:Test Boiling Point.vi: Passed
All Passed? false
error out   0
```

The XML report carries `failures="1" tests="2"` and both the `Description` and the `Message` string
inside `<failure>`, so both are worth filling in.

**That is a `Pass If.vi` fact and does NOT generalise. `Pass If Equal.vim` has NO `Message`
terminal.** Measured 2026-09-01 with `lvai_vi_terminals` on
`vi.lib\Astemes\LUnit\Palette\Pass If Equal.vim`: the terminals are `LUnit Test Case In` (conIdx 11,
required), `Expected` (10), `Actual` (9), `Delta` (6, optional), `Description` (7),
`error in (no error)` (8), out `LUnit Test Case Out` (3) and `error out` (0). Fill in `Description`
only — and the assertion then writes the comparison into the report itself:

```xml
<failure message="Expected:-1.250000(Double Float)&#xA;Actual:  0.000000(Double Float)"
         type="Pass if Equal">
```

Note `type="Pass if Equal"` with a lower-case "if". **That makes `Pass If Equal.vim` the better
assertion for a round trip than `Pass If.vi`**: there is no hand-written failure text to keep in step
with the expected value, which is a class of stale-message bug the boolean form invites.

The CLI works too, for CI:

```
LabVIEWCLI -OperationName LUnit -ProjectPath "<x>" -ReportPath "<y>" -TestRunners 1
```

## 5. Traps, each of them measured

**`Error 1562` at `AddItemFromMemory` means the class is LOCKED, and only a LabVIEW restart clears
it.** This is the one trap that costs a whole run. `docs/labview-unit-testing.md` already recorded
1562 — *"the specified project or library is locked"* — for `AddVIToClass.vi` after
`lvai_create_class`, noting that a project close and re-open does **not** clear it. Confirmed here in
a second context, and both halves held:

- First attempt, same session that had just run `lvai_create_class`: every stage before it zero,
  `terminals retyped: 2`, then `add member error: 1562` and 1562 propagated to both saves.
- After `Stop-Process LabVIEW` + `lvai_ensure_labview` + reopening the project, **the identical call
  returned all zeros.**
- Adding a **second** test method later in that same session also returned all zeros. So the lock is
  created by `lvai_create_class`, not by `AddItemFromMemory` or by adding members as such.

**The practical rule: do not create the class and add its test methods in one LabVIEW session.**
Create the classes, restart LabVIEW, then add the methods. Per `CLAUDE.md`'s own warning, "only a
restart fixes it" is a symptom and not a diagnosis — the analogous case in `lvai_create_class` turned
out to be a leaked refnum — so the leak in the class-creation path is worth finding rather than
living with. It is not found yet.

**A restart may cost you the retyping.** The `Replace` and the saves live in memory until step 7. A
restart between them loses the retyping, and the VI on disk still carries `path` terminals — re-run
the helper, which redoes both.

**LUnit does not overwrite an existing report; it writes a numbered sibling.** The second run of the
demo left `lunit_report.xml` (the first run, `tests="1"`) untouched and wrote
`lunit_report (1).xml` (`tests="2" failures="1"`). So **reading back the path you passed returns the
PREVIOUS run's report**, with no error anywhere. Delete the target first, or use a fresh path per
run. Nothing reports this.

**`lvai_open_file` wants the full `.lvproj` path in `projectPath`, filename included.** Passing the
directory and putting the file name in `projectName` — which is how the parameter pair reads — gives
`Error 1`, *"An input parameter is invalid"*, from `Open project application ref.vi`. The full path
in `projectPath` with the same `projectName` returns `No Error`.

**The LUnit base class ends up listed in your `.lvproj` — and the culprit is the project SAVE, not
`lvai_create_class`.** The demo project ended up listing `Test Case.lvclass` with a `URL` reaching
into `Program Files (x86)`, alongside the real class, and this paragraph blamed the class tool.
Re-measured 2026-09-01 on the `Brille` run: `lvai_create_class` now works in a throwaway project
(`steps[0].action: "scratch"`) and does **not** touch the user's file. The entry appears at
`lvai_close_active_project`, which **saves** before closing, and LabVIEW writes it as

```xml
<Item Name="Test Case.lvclass" Type="LVClass" URL="/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass"/>
```

Deleting that one line **while the project is closed** sticks: a later project-level LUnit run loaded
the file and did not re-add it. So the trap moved from the tool to the IDE and the remedy is
unchanged — edit a `.lvproj` only while it is closed.

**The first test method's bundle carried `VICD` compiled-code blocks; the second's did not.** The
difference is that the first VI had been loaded by LabVIEW (the failed helper run) before
`pylv_apply` extracted it. `CLAUDE.md` records a wedge caused by editing a bundle with 3 `VICD`
blocks. Nothing went wrong here, but the safe order is the documented one: convert → `pylv_apply` →
only then let LabVIEW load it.

## 6. Calling the subject: MEASURED, and the socket must be hand-authored

This section said "the subject has not been called … expected to compose with this one and that has
**not been measured**". It is measured now — 2026-09-01, on `C:\temp\brille`: a `Brille` class with
four fields and eight dynamic-dispatch accessors, and a `Brille Test.lvclass` whose **six test
methods call those accessors as ordinary static subVI calls**. `tests="6" failures="0"`, 10
assertions, on three separate runs, with a negative control that failed exactly one case on demand.

**`lvai_placeholder_subvi` does NOT work here.** Pointed at `Write Marke.vi` it answers
`errorKind: stubRefused`, because cloning the subject's pane means authoring a class-typed terminal:
`Error 53 … Control with type=UDClassInst is not supported`. `docs/labview-unit-testing.md` §3a
predicts this. So the sockets are **hand-authored AIXML generated straight into
`<LabVIEW>\user.lib\LV_MCP\`**, where a loose VI resolves by bare name — `lvai_generate_vis` did all
eight in one call with `panePattern: 4815` and `paneViolations: 0` each.

**Give the socket's data terminal the accessor's REAL type — not `variant`.**
`docs/labview-unit-testing.md` §3d uses `variant` so that one socket serves every field, and that is
wrong for LUnit: the assertion is `Pass If Equal.vim`, a **malleable** VI, so a data wire that changes
type across the `Replace` forces the `.vim` instance to re-adapt. With the real type (`string`,
`double`, `bool`) the assertion wire is byte-identical before and after the swap and only the two
class wires change, `path` → the class. Measured: zero coercion dots on every terminal read.

**Name each socket after the FIELD, not after the signature.** `lvai_swap_subvis` refuses duplicate
VI names on one diagram — matching is by VI name — so a test that writes both `Dioptrien` fields and
reads both back needs four distinct sockets even though two pairs share a signature. A
hash-of-signature name would have collided; `LVMCP Brille WDioL.vi` / `WDioR.vi` / `RDioL.vi` /
`RDioR.vi` gives uniqueness and self-documentation for free.

**The finished tests do not reference the sockets at all.** LabVIEW's export of every test method
names only `Brille.lvclass\3A…` and `Test Case.lvclass\3APass If Equal.vim`. So
`user.lib\LV_MCP\LVMCP Brille *.vi` is a **build-time** dependency: the tests run on a station that
does not have them, and uninstalling means deleting the files. They are needed only to *regenerate* a
test method.

**The order is convert → `pylv_apply` conpane 4815 → `lvai_swap_subvis` → `lvlu_add_test_method`.
Membership goes LAST.** If a swap fails, a regeneration then costs only the first three steps —
whereas a finished test method is a class member, and re-converting it from AIXML would write a VI
with no owning-library link, the defect that marks the whole library broken (§4.4). It is also why
the negative control below was injected by *swap* rather than by editing an expected constant: there
is no safe way to re-convert a finished member.

**`lvai_swap_subvis` works on a test method BOTH BEFORE AND AFTER it becomes a class member**, so the
route stays re-editable. One caveat: after Phase 4 the diagram's node names are **class-qualified**,
so the `socket` string for a later swap is `Brille.lvclass:Read Dioptrien Links.vi`, not the bare file
name. Measured twice — injecting and reverting the negative control on a finished member, `errorCode
0` both ways, class-typed pane intact.

**The negative control is worth copying as a technique.** Rather than break an expected value, one
`lvai_swap_subvis` call repointed `Test Dioptrien Links Round Trip.vi`'s read node from
`Read Dioptrien Links.vi` to `Read Dioptrien Rechts.vi` — the exact fault the test exists to catch.
That case went `Failed` with `Expected:-1.250000 … Actual: 0.000000`, the other five stayed `Passed`,
and one more swap restored green. A per-field round trip alone cannot catch a Write accessor that
stores into two fields, because the field it should not touch is never read afterwards; a test that
writes both and reads both closes that hole.

**A `.vim` instance is expensive and cannot be retargeted by name.** It appears in `SubVIs[]` as
`<Caller>.vi:Instance:<GUID>.vi` — e.g.
`Brille Test.lvclass:Test Dioptrien Links Round Trip.vi:Instance:fccbdfcf-…-d76e9acaf652.vi`. It can
therefore never collide with a socket name, and it also cannot be named in a swap. Each stored
instance costs about **66 kB in the caller**: one assertion → 66 kB, two → 138 kB, four → 280 kB.
That is why `Test Field Defaults.vi` with four assertions is 280 kB.

**Pointing `test path` at the `.lvclass` is 4x faster than at the `.lvproj` and finds the same
tests**: 3.2 s against 12.2 s, `tests="6"` both ways.

**`lvai_coercion_dots` reads a class-member test method only PARTIALLY, and its `ok: false` there is
not about dots.** On `Test Dioptrien Independence.vi`: `subViCalls: 6`, `terminalsChecked: 23`,
`coerced: 0` — but **two of the six nodes came back `subViFound: ""`** with the Error-1099 note
(`Brille.lvclass:Write Dioptrien Rechts.vi` and one `.vim` instance), while nodes of the same *kinds*
resolved fine. It opens the VI with no application instance on purpose, and a class member's siblings
do not all resolve that way. The `subVi` names are all correct, so the call-target list is
trustworthy; only the per-terminal read is partial.

**`lvai_describe_project` does not leave the project active** — `lvai_close_active_project`
immediately afterwards answered `1055 / nothingToClose: true`. So it is safe between `.lvproj` edits
without risking a save.

**`Unavailable` right after a LabVIEW restart means "still coming up", not "open Nigel".** The 1562
restart cost one extra call, not a human: `lvai_ensure_labview` returned `state: "starting"` with
`lastError: "The operation was canceled."` after 40 s, and `lvai_status` showed all 30 LabVIEW.exe
listeners answering `Unavailable` — which both `lvai_status`'s own hint and the agent definition read
as "the service has not started, ask someone to open Nigel". **A second `lvai_ensure_labview` with
`waitSeconds: 90` answered `state: "ready"` in 224 ms with nobody involved.** Call again before
asking anyone to do anything.

## 7. What is still NOT established

- **The model still authors each test method's AIXML.** That is the part that genuinely varies, so
  it stays a writing job. Everything downstream of it is now two tools — see §8.
- **`Setup.vi`/`Teardown.vi` overrides have not been scripted.** They are dynamic dispatch, needing
  `SetWireRule(TermIdx, 4)` on top of §4.4. None of the `Brille` tests needed a fixture — each builds
  its own object from a class constant — so this stayed untried.
- **The LUnit project provider has not been driven.** `LUnit_Create New Test Case.vi` resolves as a
  Call target and takes `Path` + `Application`; it would create the class from LUnit's own template,
  bringing a correctly-typed `Test Method Template.vit` with it, and it may also avoid the 1562 lock.
  Untried.
- **Parameterized, inheriting and global-fixture test cases** have templates and examples on this
  station and were not investigated at all.
- **Test method icons.** `lvai_set_vi_icon` re-saves the VI, and the `Brille` run deliberately
  skipped it rather than risk the owning-library link for cosmetics. Whether a re-save is actually
  safe on a class member is unmeasured.
- **`Setup.vi`/`Teardown.vi` overrides have not been scripted.** They are dynamic dispatch, which
  needs `SetWireRule(TermIdx, 4)` on top of everything in §4.4 — the §3 route, unmeasured for LUnit.
- **The LUnit project provider has not been driven.** `LUnit_Create New Test Case.vi` resolves as a
  Call target and takes `Path` + `Application`; it would create the class from LUnit's own template,
  bringing a correctly-typed `Test Method Template.vit` with it. That may be a better §4.1 than
  `lvai_create_class` — and it may also avoid the 1562 lock. Untried.
- **Parameterized, inheriting and global-fixture test cases** have templates and examples on this
  station and were not investigated at all.

## 8. The two tools, and what is left to the model

Added 2026-09-01, after the `Brille` run cost **85 tool calls** for six test methods where
`lvai_generate_test` builds a Caraya test in one. Both wrap the helper scripts rather than
reimplementing them, and §§3-6 remain the evidence for what they do.

| tool | replaces | notes |
|---|---|---|
| `lvai_lunit_add_test_method` | §4.3 + §4.4 per method, so 3N calls become 1 | takes `classPath` plus a `methodsJson` array of `{aixml, vi}`. Converts **without** validating, forces pane pattern 4815, retypes, adds the member, verifies from LabVIEW's own export |
| `lvai_run_lunit_tests` | §4.5 plus reading and parsing the report | returns `tests`, `failures` and one entry per case with its failure message; deletes the report and its numbered siblings first |

**Three things the first tool derives so they cannot be got wrong**, each of which was a real
mistake in the manual runs: the class terminal names come from the `.lvclass` file name as
`<Name> In` / `<Name> Out` (pass `classTerminalNames` only for a pane that deviates); the pane
pattern defaults to 4815 rather than the station's 4833; and membership is always last, so a
failure before it costs a regeneration instead of a repair.

**`ok: false` still means read `methods[].detail.hint`.** The three errors that actually happen are
named there rather than left to be looked up — `1562` the class lock (§5), `1055` no active project,
`56002` the VI adopted as a loose project item. A count of retyped terminals below the number asked
for prints the pane's real terminal names beside the ones wanted, because that failure is a
misspelling and nothing else reports it.

**`lvai_placeholder_subvi` on class code: the fix needed TWO halves and shipped with one.** This
paragraph claimed it "now works on class code" on 2026-09-01 and that was wrong the same day.
Measured on four accessors of `Weinglas`, all still `errorKind: stubRefused` — but with a completely
different validate message:

```
Only VIs owned by a LabVIEW class may use dynamic terminals in the connector pane.
Dynamic dispatch terminals are only allowed on VIs that are members of LabVIEW classes.
```

The **type** half had landed (`ref{UDClassInst}` → `path`) and the **wire rule** half had not: an
accessor's class terminals are `connection="dynamic"`, the clone kept that, and a socket is a *loose*
VI in `user.lib\LV_MCP\` — not a class member, so it may not carry a dynamic terminal. Both halves
have to be downgraded at the same moment, and the tool now sets `required` on the input and
`recommended` on the output alongside the type. Dynamic dispatch is meaningless on a socket anyway:
it is never executed, and the swap re-types the wires.

**The message is misleading twice over** and is worth recognising: it names dynamic dispatch and
class membership rather than the placeholder, so it reads as though the *subject accessor* were at
fault. It is not — the accessor is a perfectly good class member; the clone is not.

The clone is therefore **not** exact for class terminals, which is sound only because
`lvai_swap_subvis` retargets through `{LV.SubVI}` `Replace` and that re-types the wires. A pylabview
link retarget on such a socket still answers `Error 7, Bad Linkage`, and the exact-clone rule still
holds for every other type. `classTerminals` / `classTerminalNames` in the answer say which terminals
were substituted.

**What is NOT wrapped, deliberately:** authoring the test method's AIXML. That is the part that
varies per case — the values, the arithmetic, which accessor to call — and the one place a wrong
guess produces a test that passes while testing nothing. A tool that generated it would have to
invent expectations.

## 9. A second class through the route, 2026-09-01 — `Weinglas`

Four fields of four **different** types (`string`, `double`, `int32`, `bool`) against `Brille`'s
string/double/double/bool. Six test methods, 12 assertions, `tests="6" failures="0"` on two green
runs with a negative control between them. What the second class changed:

**`int32` needs no special handling, and neither does anything else in §6's "real type, never
`variant`" rule.** A socket authored `type="int32"` with `Expected` as `type="int32" value="6"` gave
`coerced: 0` on all five terminals of both the Read and the Write node after the swap, and
`Pass If Equal.vim` adapted without complaint. `type="bool" value="true"` survived as `true` — the
lower-case rule in `CLAUDE.md` holds — and `type="double" value="620.5"` as `620.5`. So string,
double, int32 and bool are all covered with no exceptions.

**But four distinct types make the independence test WEAKER, not stronger, and only one pair still
needs it.** The type system already rules out most cross-field writes; the exception is
`double` ↔ `int32`, which coerce into each other silently. And a single write-all-then-read-all test
only detects a `Write` that disturbs a field written **earlier** — 6 of the 12 ordered pairs — because
a later write repairs the damage. Say that limitation in the VI description rather than letting the
test look stronger than it is.

**§6's negative-control technique does NOT transfer to a class whose fields all differ in type.**
`Brille` had two `double` fields, so `Read Dioptrien Links` → `Read Dioptrien Rechts` was a
like-for-like read swap. Where every field has its own type there is no such swap, and repointing a
read at a differently-typed field changes the type of the wire feeding `Pass If Equal.vim`, forcing
the malleable instance to re-adapt — untested, and the plausible outcome is
`7101, At least one test is not in a executable state` rather than a clean `Failed`.

**Inject on the WRITE side instead.** Repointing `Write Volumen ml.vi` → `Write Anzahl im Set.vi`
puts the DBL constant on an I32 *input*, which is an ordinary coercion; the assertion's wire stays
DBL and byte-identical, and the result is exactly one clean failure with
`Expected:620.500000 … Actual:  0.000000` — the zero being the proof the double field was never
written. Two constraints decide which test you may inject into:

- **the replacement must not already be on that diagram**, or the restoring swap meets
  `lvai_swap_subvis`'s duplicate-name refusal. That rules out a write-all/read-all test entirely;
  pick a single-field round trip.
- after membership the socket string is class-qualified — `Weinglas.lvclass:Write Volumen ml.vi`.

**A swap-and-restore does not shrink the VI back, so file size is not an "unchanged" check.** The
membership save compacts a converted test method sharply — the four round trips went 77.9–78.3 kB
after `pylv_apply` to 65.6–65.9 kB after `Save.Instrument`. The one that took two extra `Replace`
operations for the negative control sits at **79 766 bytes**, ~21 % larger than its identically
shaped siblings while being functionally identical. Judge a restore by the export's `target=` and by
a re-run, never by bytes.

**`lvai_coercion_dots` reads a finished class member fully when the sweep is SCOPED.** §6 records two
of six nodes returning `subViFound: ""` with the Error-1099 note on a whole-diagram sweep. Passing
`subViName` to restrict it to one node on a finished member gave `subViFound` populated,
`terminalsChecked: 5`, `coerced: 0`. Not a contradiction — different scope — but a scoped sweep is
the way to get a trustworthy per-terminal answer on a class member.

**`lvai_create_class` with NO `projectPath` and NO `fields` is the right call for a test case class.**
A test case needs no private data when each test builds its subject from a class constant, and
omitting `projectPath` is what keeps the user's `.lvproj` clean: `fieldsAsked: 0`, `fieldsAdded: 0`,
`privateDataBytes: 4718`, `parent opened: true`. §5's trap — the LUnit base class getting promoted to
a top-level `.lvproj` item by the project save — **did not reproduce** on this run, where the test
class's entry was hand-written into the `.lvproj` while it was closed and before LabVIEW had ever
opened it. After six `AddItemFromMemory` calls, three LUnit runs and a close, the file still held
exactly the two entries put there. One clean run is not proof the promotion never happens, so keep
checking the file — but the cleanup step may not be required.

**A `lvai_generate_vis` entry that fails at `convert` may still have WRITTEN the `.vi` — on the wrong
pane pattern, silently.** One entry of an eight-socket batch answered
`Unavailable: An existing connection was forcibly closed by the remote host` with
`viExistsNow: true` and 5 959 bytes on disk; LabVIEW was alive (same PID, unchanged start time) and
the other seven succeeded, so it was one dropped call rather than a death. But `panePattern` is
applied **after** convert, so that file kept the station default — measured at 4833 with three style
violations. Nothing else sees it: the file exists, is the right size, and the answer only says the
convert step failed. NI's log carried a bare stack with **no exception header**, ending at
`LV AI Core.lvlibp:VI generator.vi` ← `…:ConvertAIXMLToVI.vi`. **The rule: after any entry reporting
`failedAtStep: "convert"` with the file present, treat the pane as unset and regenerate that entry
alone.** `lvai_generate_vi` now says this in its own convert-failure note.

## 10. A third class, `Banane` — the tools measured against the hand route

Same shape as §9 on purpose: four fields of four types, six test methods, 12 assertions. What
changed is that the two tools of §8 were available. Both green runs `tests="6" failures="0"`, with a
write-side negative control between them naming exactly the case that was broken.

### The numbers, which are the point

| | Brille (hand) | Weinglas (hand) | **Banane (tools)** |
|---|---|---|---|
| class phase | 6.0 min · 29 calls | 8.7 min · 34 calls | **1.8 min · 10 calls** |
| test phase | 21.0 min · 85 calls | 22.9 min · 100 calls | **10.1 min · 42 calls** |

Inside the test phase: **528 s wall clock against 102.8 s inside tools — a ratio of 5.1 : 1**, worse
than the repository's 3.6 : 1 baseline, and the reason is that what remains is almost entirely
model authoring rather than tool work. `lvai_lunit_add_test_method` did all six methods in **one
call, 24.9 s**, `methodsAdded: 6`, `failedAtStep: null`; `lvai_run_lunit_tests` returned each report
parsed in 3.7–4.7 s against the `.lvclass`.

**The largest remaining item is authoring the six test methods' AIXML: about 110 s of pure model
latency over three turns, with essentially no tool time.** §8 leaves that to the model deliberately,
and that is still right for the *choice* of values and the descriptions — but five of the six files
were mechanical transpositions of the previous class's, with four field names and four values
substituted. A generator taking `{class, fields, values, cases}` and emitting the four round trips
plus defaults plus independence would remove most of it. That is the next tool.

### `lvai_placeholder_subvi` on class code: VERIFIED, and §6 changes because of it

All eight `Banane` accessors — Read and Write over a `string`, `double`, `int32` and `bool` field —
answered `ok: true` with `classTerminals: 2` and `classTerminalNames: ["Banane in","Banane out"]`.
`stubRefused` is gone, and the two-halves fix (type **and** wire rule) holds. **So §6's "the sockets
have to be hand-authored" no longer applies**: eight `lvai_placeholder_subvi` calls issued in one
message replaced writing eight socket AIXML files plus an `lvai_generate_vis` batch, at the cost of
one turn.

**But hash-named stubs are only safe when the field types are pairwise DISTINCT.** The tool names a
stub by a hash of the signature, and §6 requires distinct names per node on one diagram. With four
different field types the eight signatures differ, so the eight hashes do, and the write-all/read-all
test swapped `nodesSwapped: 8, socketsLeft: 0`. **This is a property of the class, not of the tool**
— two fields of the same type give two identical Read signatures, one hash, and `lvai_swap_subvis`
refuses the duplicate. Such a class still needs sockets named per field, which is what §6 describes.

### The `Error 1562` lock is per-class, and only the TEST class's lock is load-bearing

Confirmed from the other side, which §5 could not do. `Banane.lvclass` was created by
`lvai_create_class` in the same LabVIEW session that then linked all eight of its accessors into six
test methods through `{LV.SubVI}` `Replace`, built a `Banane` class constant per method with
`{LV.Constant}` `Replace`, and saved six times — **every one `errorCode 0`, no 1562 anywhere.**

So a *subject* class being locked does not matter: **linking to a class is not editing it.** Only
the class you call `AddItemFromMemory` on needs the restart, and that is the test case class. **A
class-plus-tests run therefore needs ONE restart, not two** — after creating the test case class,
never between the subject class and the tests. The orchestrator's restart between the two phases was
unnecessary in the two earlier runs.

### Issue per-VI `lvai_*` calls in ONE message, even though LabVIEW serialises them

Six `lvai_swap_subvis` calls in one message took 1.2–8.3 s each, 16.4 s of tool time — LabVIEW
serialised them as `CLAUDE.md` says it will, and the six *turns* became one, which is where the money
is. Same for the eight `lvai_placeholder_subvi` calls. The existing rule that fanning out buys
nothing is about *throughput inside LabVIEW*; it is not an argument against batching round trips, and
reading it that way costs a turn per VI.

### Two defects in `lvai_lunit_add_test_method` this run exposed

**Its answer overflowed the client's token limit** — 85 968 characters over 1 173 lines, because each
method carried LabVIEW's whole AIXML export plus `pylv_apply`'s extract file list and warnings.
Recovering `methodsAdded` and `terminals retyped` from it cost **three extra grep turns**, so the tool
built to save round trips was spending them. Fixed: sub-answers are slimmed to the fields that say
whether the step worked, a failed step is still reported whole, and `verbose: true` restores the old
behaviour.

**Its answer misreported the phase order, and a careful reader drew the wrong conclusion.** The
execution is right — close, convert+pane for every method, open, membership for every method — but
the close and the open were both appended to one `prologue` array, so the answer *read* as though both
happened before any method, and the run concluded "either the two-phase ordering is not what it
implements, or adoption needs more than an open project". Neither: the reporting was misleading.
Fixed — the array is now `projectPhases`, each entry carries `order` and a `thenWhat` saying which
loop runs after it. **A tool whose answer implies the wrong sequence is a defect even when the
sequence is right**, because the next reader reasons from the answer, not the source.

## 11. A fourth class, `Apfel` — the fixes checked, and three smaller ones found

Same shape again: four fields of four distinct types, six methods, 12 assertions. **394 s, 38 calls,
one restart**, two green runs with a write-side negative control between them
(`Write Gewicht g` → `Write Erntejahr`, `Expected:182.250000 … Actual:  0.000000`, exactly one case
named). §10's five prescriptions all held with nothing to correct.

**Both §10 fixes verified.** The slimmed answer needed no grep turns and carried everything needed to
judge each step. `projectPhases` makes the close → convert → open → membership order unambiguous —
and the useful detail is that `order` alone would not have: it is the `thenWhat` string naming which
loop follows each entry that does the work.

**`typedefNote` is a FALSE ALARM on a class pane, and it prescribed the wrong repair.** All eight
`Apfel` stubs answered `typedefTerminals: 2` with
`typedefPath: …\Apfel.lvclass\Apfel.ctl` and the standard advice to fix coercion dots with
`lvai_bind_typedef_constants` — on the very two terminals `classTerminalNote` already explains. The
cause: a `.lvclass` presents its private data control as the terminal's type, so the typedef detector
sees a `.ctl`. The advice does not apply, because `{LV.SubVI}` `Replace` re-types those wires and no
constant is ever bound to them; the run was green with `bind_typedef_constants` never called. Fixed —
terminals already counted in `classTerminals` are excluded from the typedef count, and any that were
excluded are reported as `classTerminalsNotCountedAsTypedefs`. **Reporting a repair that must not
happen reads as a skipped step**, which is the same class of defect as §10's phase-order mix-up: the
answer was misleading while the behaviour was right.

**`Pass If Equal.vim` gains an unwired `Delta:` terminal in the post-swap export.** Every verified
export reads `…,Description:113.value,Delta:` where the authored AIXML had none. The malleable
instance re-adapts when the class wires are re-typed and exposes the numeric-tolerance terminal; it
is unwired and harmless, and it appears on the string and boolean assertions too. Not a diff worth
chasing.

**Two measurement gaps, now closed.** `lvai_placeholder_subvi` reported no duration at all, so a
batch of eight was invisible in the run's tool-time sum — and `lvai_lunit_add_test_method`'s `verify`
step carried none either. That matters more than it sounds: **wall clock minus tool time is how the
next tool gets chosen, so a step with no `elapsedMs` cannot be chosen against.** Both now report one,
and the placeholder's stopwatch starts before its export rather than after it.

**A `bash` heredoc is not a safe way to write several AIXML files**, and the failure names the wrong
place: six `cat > … <<'EOF'` blocks came back `unexpected EOF while looking for matching '''` with a
line number inside the third file and nothing wrong in any of them. `CLAUDE.md` already says to write
AIXML directly, for the `A` escaping; this is a second reason. Six file writes in one message is
one turn anyway, so there is nothing to gain from the heredoc.

**`closeProject` answering `1055 / nothingToClose: true` is correct, not a failure**, when
`lvai_create_class` ran earlier in the session: it closes its own scratch project, so nothing is left
active for the phase-one close to do.


## 11. A fifth run, `Apfel` cold from nothing — 2026-09-01

LabVIEW killed first, the previous run archived, so both phases started genuinely cold. Six methods,
12 assertions, `tests="6" failures="0"` on two green runs with a write-side negative control between
them. §10's five prescriptions held; four things are new.

| phase | Weinglas | Banane | Apfel warm | **Apfel cold** |
|---|---|---|---|---|
| class, calls | 34 | 10 | 8 | **7** |
| tests, calls | 100 | 42 | 38 | **43** |

The class phase reached 7 calls because `lvai_create_accessors` now opens the project itself and
reports `memberNames` plus a `dispatchFlags` histogram — both verified this run
(`projectOpened: {errorCode: 0}`, `dispatchFlags: {"0": 8}`, matching an independent grep). The test
phase did **not** improve, and the reason is in the last item below.

### LabVIEW's warmth dominates the class phase, and `budgetSeconds` is tuned for the wrong case

| instance age at first slice | LabVIEW time for 8 accessors |
|---|---|
| 75 min | **21.4 s** |
| 100 s | 56.3 s |
| ~2 min, cold process | **80.0 s** (slice 1 45.8 s, slice 2 34.2 s) |

Two things follow. **A wall-clock comparison between runs is only valid if LabVIEW's warmth matches**
— call count is the robust metric. And **`budgetSeconds: 45` is exactly wrong on a cold instance**:
the first slice took 45.8 s, overran, and the call therefore stopped after one slice and needed a
second turn. Slice 2 then did the same two fields in 34.2 s *despite the library being twice as
large*, so warm-up outweighs library growth. On a cold instance either raise the budget past ~90 s
to fit both slices in one call, or accept two calls and set it low deliberately.

### `lvai_swap_subvis` inherited the answer-size defect, and it is now fixed in a shared place

Six swaps in one message returned about **34 kB** — three times the whole slimmed
`lvai_lunit_add_test_method` answer for the same six VIs — because its verify step returns LabVIEW's
entire AIXML export inline plus a flattened per-node value dump. `verify: false` avoids the payload
and then nothing proves the swap landed, so that is not the answer.

Fixed the same way as §10's, but the reducer moved to `Json.Slim` rather than being copied: **the
defect is structural to composition, not particular to either tool.** A tool that composes N others
inlines N whole answers, and the caller then spends turns grepping its own result — so the tool built
to save round trips starts spending them. Both tools now slim their sub-answers unless a step
**failed** (then the whole answer is kept, which is when it is needed) or `verbose: true`.

### The socket name has TWO spellings in one run, and membership decides which

The initial swaps take the **bare stub name** (`LVMCP Stub 57df820953.vi`); the negative control
afterwards takes the **class-qualified** name (`Apfel.lvclass:Write Gewicht g.vi`), because by then
the node calls a class member. Both appeared minutes apart on the same VI.

### Three smaller measurements

**The stub cache spans runs and LabVIEW restarts.** All eight placeholders answered `reused: true` in
0.686 s total: the hash covers terminal names, types, `conIdx` and direction, so a re-created class
with the same name and field types produces the same signatures. **A repeat build of an
identically-shaped class pays nothing for sockets.**

**A failed case carries TWO `<failure>` entries**, not one — the `Pass if Equal` one with
`Expected`/`Actual`, and a second of type `Test Case` whose text is `Failed:` plus the Description.
§4.5 showed only the first.

**§5's `Test Case.lvclass` adoption did not recur**, second clean run in a row. So that trap is not
deterministic: read the `.lvproj`, do not assume there is something to strip.

### The remaining cost is orientation, not authoring — and that changes what the next tool is

**Largest item by wall minus tool: 98.0 s of wall clock against 5.2 s inside tools over 9 turns,
locating and reading §10, §4 and three earlier runs' AIXML templates** — about 82 s of it plain file
reading. §10 named the next tool as an AIXML *generator*; this run says that is only half of it. Four
of those nine turns went on **finding a template to copy**, and this run's authoring was fast only
because the previous `Apfel` run's six files existed to transpose from — which also makes its
authoring time incomparable with `Weinglas` or `Banane`.

So the tool should **ship the skeleton, not merely emit it**: templates under `scripts/templates/`
that need no searching, with the generator on top. That ordering was proposed and deferred once
already; the second measurement is the argument for doing the cheap half first.

### One process note on this run, kept because it cost the measurement something

The run brief handed to the agent said the `typedefNote` suppression was **not** in the build. It
was — committed earlier the same afternoon — and the agent correctly reported
`typedefTerminals: 0` with `classTerminalsNotCountedAsTypedefs: 2`. **A stale brief is as expensive
as a stale document**, and the fix is the same: check the commit, not the memory of having written it.

## 12. A sixth run, `Apfel` cold again — the variance run, 2026-09-01

A deliberate repeat of §11 with nothing changed but `Json.Slim` on `lvai_swap_subvis`, to separate
improvement from noise. Six methods, 12 assertions, `tests="6" failures="0"` on runs 1 and 3 with a
write-side negative control between them.

| | Weinglas | Banane | Apfel #5 | **Apfel #6** |
|---|---|---|---|---|
| class phase, calls | 34 | 10 | 7 | **6** |
| test phase, calls | 100 | 42 | 43 | **39** |
| test phase, wall | 1375 s | 609 s | 492 s | **364 s** |

### READ THE AUTHORING NUMBERS WITH CARE — three of them measure different things

This is the most important caveat on this page for anyone using these figures to justify work.

| run | how the six AIXML files were produced |
|---|---|
| `Weinglas`, `Banane` | **authored** from the reference in §§3–6 |
| `Apfel` #5 | **transposed** from `Banane`'s six files — names and values substituted |
| `Apfel` #6 | **copied verbatim** — same class name, field names and types, so even the embedded stub hashes still resolved |

So only the first pair measures authoring at all, and §12's apparent improvement over §11 is partly
that artefact rather than tooling. **The honest reading is that the shipped-skeleton half of the
proposed generator would reproduce run #6's near-zero authoring cost by design** — which is the
strongest argument yet for building the templates before the generator.

### `budgetSeconds: 100` is right and 45 is wrong, on a cold instance

Eight accessors in **one** call, 57.2 s, against 80.0 s in **two** calls at `budgetSeconds: 45`. The
cold penalty sits almost entirely in the FIRST slice — 37.6 s against 19.6 s — so it is a one-off
wizard warm-up, not per-field growth. A budget of 45 stops right after the expensive slice and pays a
whole extra turn for the cheap one.

> **AND `slices[].elapsedMs` IS CUMULATIVE, which was read as per-slice and produced a wrong figure
> in this document's own history.** One `Stopwatch` is started before the loop and every slice records
> `wall.ElapsedMilliseconds`, so the values must not be summed — a run summed two of them to 94.8 s
> inside a call that lasted 104 s, then spent two turns in the source finding out why. An earlier
> report of "slice 1 45.8 s, slice 2 34.2 s" was also mislabelled: those were two separate *calls*.
> Fixed — the answer now carries `sliceMs` per slice alongside the cumulative `elapsedMs`, plus
> `elapsedMsIsCumulative: true`, a `totalElapsedMs` for the whole call including the project open, and
> **`labviewAgeSeconds`**.

### Instance warmth dominates the LUnit RUN too, not just the class phase

Three `lvai_run_lunit_tests` calls over the same six tests, minutes apart on a LabVIEW started 70 s
before the first: **11.8 s → 8.1 s → 4.9 s**, monotone. §10's "3.7–4.7 s against the `.lvclass`" is
the **warm** figure only, and a first run on a fresh instance is about 2.5× that. Same rule as for
`budgetSeconds`: **compare call counts across runs, not wall clock, unless the warmth matches.**
`labviewAgeSeconds` now makes that checkable.

### The slimming works, and the composer must time its own steps

Six swaps came back at about **6.3 kB against §11's ~34 kB**, with `callTargets`, `nodesSwapped`,
`socketsLeft` and `constantsSwapped` all intact and top-level — no grep turns, no truncation. One gap
found and fixed: the `verify` sub-answer carried no `elapsedMs`, because the callee does not report
one on that path, so the export half of each swap was recoverable only by subtraction. **A composing
tool always knows how long its own step took and now reports it** rather than hoping the callee did —
by §11's own argument that an untimed step cannot be chosen against.

### Two smaller measurements

**`lvai_ensure_labview`'s second call reached 55 ms.** Eight cold starts on record; the second-call
spread is now 0.055 / 0.69 / 1.2 / 1.2 / 12.1 / 27.7 s. The first call still spends its whole budget
and answers `starting` with `lastError: "The operation was canceled."` — that remains normal.

**`lvai_create_class`'s verify step reported `inheritsFrom: null` for a root class**, and its note read
`inherits from ''`, where `lvai_describe_class` correctly falls back to `LabVIEW Object`. Nothing was
wrong with the class; a reader checking inheritance simply could not tell a healthy root class from a
parent link that failed to take. Fixed to use the same fallback.

### Orientation is still the largest item, and a better brief halves it

**53.4 s of wall clock against ~1 s inside tools, over 8 turns** — against §11's 98.0 s over 9. The
difference is that the brief named the sections and listed the prescriptions, so the agent went
straight to them. The residue is reading §10 and §11 themselves plus four turns finding the archived
AIXML, and **only the second half is something a tool can remove**. That is what
`scripts/templates/` is for; a brief can shorten the reading but never delete it.

## 13. The templates are shipped — `scripts/templates/lunit/`

The cheap half of §12's "next tool", built 2026-09-01. Three skeletons plus a README:

| file | shape |
|---|---|
| `round-trip.xml` | one field: write, read back, assert equal — fully parameterised |
| `defaults.xml` | four fields read off a FRESH class constant, four assertions |
| `independence.xml` | write all four on one object, read all four back, four assertions |
| `README.md` | the placeholder table, the `swapsJson` / `constantsJson` shapes, and four wiring rules |

**They are the verified files, not a rewrite, and that is checked mechanically.** Each was produced
by taking the corresponding file from the run in §12 — generated, run, `tests="6" failures="0"` with a
negative control — and replacing names and values with `{{PLACEHOLDER}}`. Filling the placeholders
back in reproduces all three originals **line for line**, which was confirmed before shipping.

`LUnitTemplateTests` pins what matters and deliberately not the prose: every placeholder fillable and
the result valid XML; the 4-2-2-4 pane with the class terminals at conIdx 11 and 3 as `path`
stand-ins and the error terminals at 8 and 0; the `Test Case.lvclassAPass If Equal.vim` target with
its escape intact; the seed constant a `path` named `<Class> seed`; the unbroken error chain; and the
one difference that decides whether the two four-field templates test anything —
**`defaults.xml` reads the untouched seed four times, `independence.xml` reads the object all four
writes ran on.** Swapping those two produces tests that pass and prove nothing, and nothing else
would catch it.

`scripts/**/*` already ships with `RecursiveDir`, so the folder needs no `.csproj` change and reaches
a binary-only install with the rest of `scripts\`.

**What this does NOT remove.** The *values* and the *descriptions* still have to be chosen, and that
is the half where a wrong guess produces a green test that checks nothing — so the agent's Phase 3
now says the skeletons are the starting point and the values are the job. §12's other half, a
generator emitting these from `{class, fields, values, cases}`, is still unbuilt and is now the whole
of the remaining authoring cost.

**And the reading cost stays.** §12 measured orientation at 53 s even with a brief that named the
sections; four of those turns were finding files and are what this removes, the rest is reading §§10–12
and cannot be deleted by a tool — only shortened by a brief that says where to look. The agent's
Phase 3 now points at `README.md` instead of §§3–6 for exactly that reason.
