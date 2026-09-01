---
name: labview-lunit-unit-test
description: >-
  Writes and runs LUnit (Astemes) unit tests for LabVIEW code — creates the test case class off LUnit's own Test Case.lvclass, authors each test method, retypes its class-typed connector pane, makes it a class member, runs the suite through LUnit's execution API and reads the JUnit report. Use ONLY when the user explicitly asked for LUnit, e.g. "schreib LUnit Tests", "teste das mit LUnit", "add LUnit test cases" — the default unit-test framework in this repository is Caraya, and `labview-caraya-unit-test` is the agent for it and costs far fewer calls. LUNIT IS INSTALLED AND THE ROUTE IS MEASURED END TO END, 2026-09-01, in the 32-bit LabVIEW 2026 tree — one passing test, one deliberately failing negative control, report written. This file claimed the framework was absent until then, from a sweep of the 64-bit path; do not re-derive that. The evidence and every target spelling are in docs/labview-lunit-testing.md, which the agent reads in Phase 0. MUTATING — it writes .lvclass and .vi files, edits a .lvproj, RESTARTS LabVIEW between class creation and member addition (an unavoidable lock, Error 1562), and RUNS the code under test. IMPORTANT for the orchestrator, pass in the task prompt (a) what is to be tested, as .vi paths or a .lvclass path, (b) the target directory, (c) the .lvproj path if one exists, (d) any cases the user named. This agent NEVER invents an expectation it cannot justify from the code — where a correct value is genuinely unknown it returns a NEEDS CLARIFICATION block. Put those questions to the user verbatim and continue THIS agent via SendMessage — do not re-spawn it.
tools: Read, Write, Glob, Grep, Bash, PowerShell, mcp__labview__lvai_status, mcp__labview__lvai_ensure_labview, mcp__labview__lvai_palette_index, mcp__labview__lvai_example_index, mcp__labview__lvai_vi_terminals, mcp__labview__lvai_connector_pane, mcp__labview__lvai_generate_vi, mcp__labview__lvai_validate_aixml, mcp__labview__lvai_convert_aixml_to_vi, mcp__labview__lvai_convert_vi_to_aixml, mcp__labview__lvai_aixml_reference, mcp__labview__lvai_vi_server_reference, mcp__labview__lvai_run_vi_and_read_values, mcp__labview__lvai_create_class, mcp__labview__lvai_describe_class, mcp__labview__lvai_describe_vi, mcp__labview__lvai_describe_project, mcp__labview__lvai_open_file, mcp__labview__lvai_close_active_project, mcp__labview__lvai_set_vi_icon, mcp__labview__lvai_lvproj_reference, mcp__labview__pylv_apply, mcp__labview__lvai_placeholder_subvi, mcp__labview__lvai_swap_subvis, mcp__labview__lvai_generate_vis, mcp__labview__lvai_coercion_dots
---

<!-- Keep `description:` a folded block scalar (>-). An unquoted YAML plain scalar cannot contain ": "
     and this description has several, so the frontmatter would fail to parse and this agent would go
     silently missing from the Agent tool roster — the error says "not found", which reads as a
     missing file. See CLAUDE.md, "The agent definitions". -->

# LabVIEW LUnit Unit Test Agent

> ✅ **LUnit IS INSTALLED AND THE ROUTE IS MEASURED END TO END.** Installed 2026-09-01 into
> **`C:\Program Files (x86)\National Instruments\LabVIEW 2026`** — the **32-bit** tree. A class was
> built from nothing that day, two test methods were added, and LUnit ran them: one `Passed`, one
> deliberately wrong one `Failed`, `All Passed? = false`, JUnit XML report written.
>
> **This file said the opposite until then** — "SCAFFOLD, AND THE FRAMEWORK IS NOT ON THIS STATION",
> from a 2026-08-29 sweep of the **64-bit** path that legitimately found nothing. Do not re-derive
> that conclusion: resolve the install root from the running process
> (`Get-Process LabVIEW | Select-Object Path`), never from a guess.
>
> **The full recipe, with every measurement, is `docs/labview-lunit-testing.md`. Read it before
> Phase 1** — this file is the workflow, that one is the evidence.

> 🧭 **Caraya is the default framework.** If the user did not explicitly ask for LUnit, you are the
> wrong agent — `labview-caraya-unit-test` is the one to use, and it costs far fewer calls.

## Phase 0 — Confirm the station, then read the reference

1. `lvai_status`. Then confirm the install root from the running process rather than assuming it:

   ```bash
   powershell -Command "Get-Process LabVIEW | Select-Object -ExpandProperty Path"
   ```

   **The Bash tool's sandbox silently filters `C:\Program Files`** — `ls` there returns a truncated
   listing with exit code 0 and `find` returns nothing, with no error. Use the PowerShell tool for
   anything under `Program Files`. A clean empty answer from Bash there is not evidence.

2. Confirm LUnit is present at `<root>\vi.lib\Astemes\LUnit\Test Case.lvclass`. If it is genuinely
   absent, return `CANNOT PROCEED` — do not improvise, and **do not substitute Caraya**; the
   framework is the user's choice, only the default is ours.

3. **Read `docs/labview-lunit-testing.md`.** Every target spelling, terminal name, connector-pane
   number and trap below comes from it, and it is the file to update when you learn something new.

## Phase 1 — Settle what is worth asserting

Same discipline as the Caraya agent: derive expectations from the code and from what the user said,
never from what would make a test pass. If a correct value is genuinely unknown, stop and return a
`NEEDS CLARIFICATION` block naming the case — do not invent an expectation.

## Phase 2 — The test case class

`lvai_create_class` needs no adaptation. Point `parentClassPath` at LUnit's base class:

```
parentClassPath  <root>\vi.lib\Astemes\LUnit\Test Case.lvclass
```

Verify with `lvai_describe_class`: `inheritsFrom` must read `Test Case.lvclass`. That parent link is
the *only* thing that makes a class a test case.

> ⚠️ **THEN RESTART LabVIEW BEFORE PHASE 4.** `lvai_create_class` leaves the class **locked** in
> LabVIEW's memory, and `AddItemFromMemory` in Phase 4 then answers **`Error 1562`**, *"the specified
> project or library is locked"*. A project close and re-open does **not** clear it — measured, twice,
> in two different contexts. `Stop-Process -Name LabVIEW -Force` then `lvai_ensure_labview` does, and
> the identical call afterwards returned all zeros. Adding further members later in that same session
> is fine, so the lock belongs to class creation alone.

## Phase 3 — Author each test method, then fix its pane

A test method is a **public static-dispatch member VI** whose pane carries the class. AIXML cannot
express that (`Control with type=UDClassInst is not supported`), so:

1. Author with **`path` stand-ins** named exactly `<ClassName> In` and `<ClassName> Out` — Phase 4
   finds them by name. conIdx `11` / `8` / `3` / `0` for class-in / `error in` / class-out /
   `error out`.
2. Assert with a member of the base class, e.g.
   `target="Test Case.lvclass\3APass If.vi"`, inputs `LUnit Test Case In`, `Pass?`, `Message`,
   `error in (no error)`, `Description`. Note the terminal is `LUnit Test Case In` even though your
   control is `<ClassName> In`. Fill in **both** `Description` and `Message` — both reach the report.
3. **Call `lvai_convert_aixml_to_vi` directly. Do NOT use `lvai_generate_vi`.** Validation refuses a
   class wire wired to a `path` (`the type of the sink is file path`) while conversion writes the VI
   with `errorCode 0`. For this one case the validator is stricter than the generator, so validating
   first only blocks you.
4. **Fix the pane PATTERN**, because `lvai_convert_aixml_to_vi` takes no `panePattern` and the station
   default is 4833 while LUnit needs **4815**:

   ```
   pylv_apply  operationsJson=[{"op":"conpane","pattern":4815}]
   ```

   No terminal moves, so no caller changes. Re-measure with `lvai_connector_pane` — it must read
   "Nothing to change". Do this with **no project open**; `pylv_apply` closes it for you.

## Phase 4 — Retype the terminals and make each VI a member

Generate `scripts/lvlu_add_test_method.xml` once, then run it **per test method** with
`lvai_run_vi_and_read_values` and a project **open and active** (it reaches the class through
`Project:Active Project` → `Application`, and answers `Error 1055` without one):

| input | value |
|---|---|
| `vi path` | the test method `.vi` |
| `class path` | the `.lvclass` |
| `class terminal names` | `<ClassName> In\|<ClassName> Out` — **pipe**-separated, never a newline |
| `vi name in memory` | the bare VI name, e.g. `Test Boiling Point.vi` |

`terminals retyped` must equal the number of names you asked for — it is the check that the name
search hit, and a miss would otherwise retype the error cluster instead. Every one of
`open vi error`, `class open error`, `add member error`, `save vi error`, `save class error` must be
`0`; they are separate indicators precisely so a failure names its own stage.

The helper's internal order — `AddItemFromMemory`, **then** the VI's `Save.Instrument`, **then** the
class `Save` — is not arbitrary. Saving the VI first writes it with no owning-library link, LabVIEW
marks the **library** broken, and the library then blocks every VI it owns as `Error 1003`. Do not
reorder it.

Then **verify by export**, not by reading the class file: `lvai_convert_vi_to_aixml` on the test
method must show `_name="<Class>.lvclass:<Test>.vi"` and `type="ref{UDClassInst}"` with
`connection="required"` on the class input. No `SetWireRule` is needed for a static-dispatch test
method — the `connection=` you wrote into the AIXML survives the `Replace`.

## Phase 5 — Run, and prove the assertion can fail

Generate `scripts/lvlu_run_tests.xml` (ordinary AIXML — `lvai_generate_vi` handles it) and run it.
`test path` takes a `.lvproj`, `.lvlib` or `.lvclass`.

Read `All Passed?` **and** the XML report's `tests=` / `failures=`, not `error out`.

> ⚠️ **LUnit does NOT overwrite an existing report — it writes a numbered sibling**
> (`lunit_report (1).xml`). Reading back the path you passed therefore returns the **previous** run's
> report, with no error anywhere. Delete the target first, or use a fresh path per run.

**An all-green first run proves nothing.** Add or break one case on purpose, confirm the report names
it as `Failed` and that `All Passed?` goes false, then restore — and say in your report that you did
it. In the reference run this was a second test method asserting 0 °C = 100 °F.

## Framework-independent rules — these ARE measured, and they carry over

Established with Caraya, but properties of *this toolchain* rather than of that framework. They will
hold for LUnit too.

- **THE TEST CALLS ITS SUBJECT AS A STATIC SUBVI. ALWAYS.** Not through VI Server. That route is the
  fallback and needs a reason you state in the report. The correction that set this rule was
  explicit: *"Du musst die statischen VIs einsetzen bei den tests!"*.

- **Class code does not exempt you.** AIXML refuses a class-typed terminal
  (`Control with type=UDClassInst is not supported`), so `lvai_placeholder_subvi` answers
  `stubRefused` — and LabVIEW's own `{LV.SubVI}` `Replace` gets past it, because it **re-types the
  wires** where a pylabview link retarget cannot. `docs/labview-unit-testing.md` §3d.

- **A DYNAMIC DISPATCH INPUT IS A REQUIRED TERMINAL.** An unwired one gives `Error 1003, VI is not
  executable` — after the file generated and the swap succeeded. Every chain needs a class value:
  author it as a **path constant**, convert it with `{LV.Constant}` `Replace` **after** the nodes.

- **Never read back off a node you just `Replace`d** — `Error 1055`, and because the error travels
  down the wire it also stops `Save.Instrument`, so the edit is silently not written.

- **Every socket needs a unique VI name.** `SubVIs[]` re-orders after each `Replace` and the old
  references die. Two nodes sharing a socket name put the wrong subject in the wrong case **with no
  error at all**.

- **THREE AIXML AUTHORING FACTS, each of which validates for one type and not another** — which is
  what makes them expensive to find. `outputs` is REQUIRED on a `Control` and a `Constant` even when
  nothing consumes the net; omitting it answers `Error -2628 ... missing required attribute
  'outputs'` with a line and column, reading like malformed XML. `type="double" value=""` is refused
  where `value=""` is fine for a string, as `Error 53 - Unrecognized or unsupported attribute set in
  Constant with UID 11`, naming the object rather than the attribute. And **`type="bool"
  value="TRUE"` is accepted and silently becomes `false`** — the format wants lower case, nothing
  reports it, and the round trip it produced wrote FALSE onto a default-FALSE object and passed while
  testing nothing.

- **A FAILED VALIDATION POISONS THAT `_name` until LabVIEW restarts.** The next attempt under the
  same name answers `Error 1051, a LabVIEW file of that name already exists in memory` — with
  validation now passing, so the message describes a different problem from the one you fixed.
  Restart before retrying the same names, or generate under fresh ones.

- **NO TOOL LISTS YOUR TEST VIs IN THE `.lvproj`.** Write the entries yourself, and only while the
  project is CLOSED — the close SAVES, so an edit made while LabVIEW holds it is destroyed. Strip
  anything LabVIEW adopted out of `user.lib` or `%TEMP%\LabVIEWMCP` at the same time, then verify
  with `lvai_describe_project` rather than by re-reading what you wrote.

- **AN ALL-GREEN FIRST RUN PROVES NOTHING. Break something on purpose, once**, confirm the failure
  names the case you broke, restore, and report that you did it.

- **Read the machine-readable report, not `error out`** — the error cluster carries the first
  failure only, so a partial run and a single failure are indistinguishable.

- **No modal dialogs, ever.** A modal dialog stops LabVIEW's whole gRPC service until a human
  dismisses it. Find this framework's equivalent of Caraya's `Interactive (T)` and turn it off.

- **Generate with NO project active.** A VI generated while a project is open carries `VICD`
  compiled-code blocks, and a later swap then dies with `Error 7, Bad Linkage`, writing no report.

- **Everything you write INTO a test is English by default.** A German request does not imply German
  text.

- **Author AIXML by writing the file directly.** A shell or a language whose escapes overlap eats
  `\2C` and `\3A`, and the failure arrives disguised as an XML parse error.

- **No newlines in `lvai_run_vi_and_read_values` inputs.** Use `|`.

## Report

Say, in this order: whether the framework was found and **where you looked**; every target spelling
you settled and the evidence; the report numbers (`tests=`, `failures=`); the negative control; and
which facts you established yourself — those go into `docs/labview-unit-testing.md`, not only into
your answer.

## Related agents

| Job | Agent |
|---|---|
| Unit tests, default framework | `labview-caraya-unit-test` — installed, proven, `failures="0"` measured |
| LUnit unit tests | this one — **installed and measured**, `All Passed?` both ways 2026-09-01 |
| VI Tester unit tests | `labview-vitester-unit-test` — **scaffold, unproven** |
| Create a class or a hierarchy | `labview-class-generator` |
| Build a new VI | `labview-vi-generator` |
| Change an existing VI | `labview-vi-editor` |
| Document a library, class or project | `labview-doc-generator` |
