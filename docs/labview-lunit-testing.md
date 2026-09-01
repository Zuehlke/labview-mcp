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

A second trap sits on top of that one. The **Bash tool's sandbox silently filters `C:\Program
Files`** — `ls` of the LabVIEW 2025 folder returned the single entry `resource` with exit code 0,
and `find` returned nothing at all, with no error. Use the PowerShell tool for anything under
`Program Files`; a clean empty answer from Bash there is not evidence.

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

**`lvai_create_class` writes the LUnit base class into your `.lvproj`.** The demo project ended up
listing `Test Case.lvclass` with a `URL` reaching into `Program Files (x86)`, alongside the real
class. Harmless — LabVIEW would list it as a dependency anyway — but it is a real edit to the user's
project file and it is not what a reader expects to find.

**The first test method's bundle carried `VICD` compiled-code blocks; the second's did not.** The
difference is that the first VI had been loaded by LabVIEW (the failed helper run) before
`pylv_apply` extracted it. `CLAUDE.md` records a wedge caused by editing a bundle with 3 `VICD`
blocks. Nothing went wrong here, but the safe order is the documented one: convert → `pylv_apply` →
only then let LabVIEW load it.

## 6. What is NOT established

- **No `lvai_*` tool wraps any of this yet.** The two helper scripts exist
  (`scripts/lvlu_add_test_method.xml`, `scripts/lvlu_run_tests.xml`) and are driven by hand through
  `lvai_run_vi_and_read_values`. A test method still costs five calls; a Caraya test costs one.
- **The subject has not been called.** Both demo tests assert over primitives on their own diagram.
  A real test calls the code under test, and for project-local code that means
  `lvai_placeholder_subvi` plus `lvai_swap_subvis` — the route `docs/labview-unit-testing.md` §3a
  already documents. It is expected to compose with this one and that has **not been measured**.
- **`Setup.vi`/`Teardown.vi` overrides have not been scripted.** They are dynamic dispatch, which
  needs `SetWireRule(TermIdx, 4)` on top of everything in §4.4 — the §3 route, unmeasured for LUnit.
- **The LUnit project provider has not been driven.** `LUnit_Create New Test Case.vi` resolves as a
  Call target and takes `Path` + `Application`; it would create the class from LUnit's own template,
  bringing a correctly-typed `Test Method Template.vit` with it. That may be a better §4.1 than
  `lvai_create_class` — and it may also avoid the 1562 lock. Untried.
- **Parameterized, inheriting and global-fixture test cases** have templates and examples on this
  station and were not investigated at all.
