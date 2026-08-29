# Brief: teaching this server to write unit tests

**Status:** design brief, written 2026-08-29. Nothing here is measured *by this document*. Every
factual claim is either (a) cited to `docs/labview-unit-testing.md`, which was measured end to end
on 2026-08-27/28, (b) read out of the current source, or (c) explicitly marked **UNVERIFIED** as a
hypothesis with a cheap measurement attached. Keep that distinction when you edit this file — the
repository's whole value is that its docs say which is which.

**Audience:** the Claude Code session that will build the capability. Read §1 before doing anything,
because the first instinct — "write a test-generator agent" — rebuilds work that already shipped.

---

## 1. What already exists. Do not rebuild it.

| Surface | Where | What it already does |
|---|---|---|
| **Tool** | `src/LabVIEWMCP/Tools/TestTools.cs`, `lvai_generate_test` | Creates a Caraya test `.vi` from `(viPath, casesJson, testViPath)`. Composes `lvai_placeholder_subvi` → `lvai_generate_vi` → `pylv_apply` retarget. One diagram node per case. Already `Destructive = true`. |
| **Tool** | `lvai_placeholder_subvi` | Generates a pane-clone stub into `user.lib\LV_MCP\`, cached by pane signature, and hands back the `Call` element and the `retarget` op. |
| **Doc** | `docs/labview-unit-testing.md`, 374 lines | The measured spike. Caraya target spellings, the `Error 53` wall, the JUnit report finding, the error-chaining defect, the `Error 1051` phantom, the `UDClassInst` refusal. |
| **Agents** | `.claude/agents/labview-{vi-generator,vi-editor,doc-generator,class-generator}.md` | The house style you must match: folded `description: >-`, explicit `tools:` allow-list, `## Hard rules`, numbered phases, `NEEDS CLARIFICATION` / `CANNOT PROCEED` blocks. |

So the mechanics of *authoring* a Caraya test are solved and deterministic. **What is missing is
everything around them**: running the test, reading the verdict, choosing the cases, and refusing to
run when it is not safe.

### The single biggest gap: nothing runs a test

There is no `lvai_run_test`. `docs/labview-unit-testing.md` §4 describes running a suite by hand —
author a harness VI that calls `Caraya.lvlib\3ARun Tests.vi`, instance `Run Test (Scalar Path)`,
`Interactive` wired **false**, `Report Path` ending in **`.xml`** — but no tool does it.

If you leave this to the agent, every session re-authors that harness from prose, and every session
gets a fresh chance to wire `Interactive` to true. That one mistake opens a **modal dialog, which
stops LabVIEW's entire gRPC service until a human clicks it** (§4). An agent that hangs the
transport it depends on cannot recover from its own mistake.

**Build `lvai_run_test` first, before the agent.** Suggested contract:

```
lvai_run_test(testViPath | testFolder, reportPath?, timeoutSeconds?)
  -> { ok, verdict: "pass" | "fail" | "notExecutable" | "error",
       tests, failures, errors, skipped,
       cases: [ { name, classname, status, message } ],
       errorCode, reportPath, raw }
```

It should hard-code `Interactive = false` and force a `.xml` extension on `reportPath` (a `.txt`
extension writes **no file at all**, silently, while `error out` still reports the run completed —
§4). It must parse the JUnit XML, not the error cluster; see §5 below.

---

## 2. Is an agent the right choice?

**Yes for the workflow, no for the knowledge.** An agent is a container for *judgement under a
fresh context window*. It is the wrong place to store facts that must never be re-derived, and it is
a very weak place to put a safety rule.

Use four surfaces. This split is the main thing to take from this brief:

| Surface | Holds | Why there | Failure mode if misplaced |
|---|---|---|---|
| **MCP tool** (C#) | Deterministic mechanics: author the test, run it, parse the report | Runs identically every time; testable in `tests/LabVIEWMCP.Tests` | In a prompt: re-derived per session, wired wrong occasionally, unversioned |
| **Doc / reference tool** | Measured facts: target spellings, traps, error-code meanings | Queryable, citable, dated, survives model changes | In a prompt: bloats the system prompt, goes stale invisibly |
| **Agent** (`.md`) | Phase order, case selection, style choice, what to refuse, how to report | Needs to reason about *this* VI | In a tool: unmaintainable branching |
| **Hook** (`PreToolUse`) | The non-negotiable: no execution without an answered safety gate | **Deterministic. Cannot be argued out of it.** | In a prompt only: a prompt rule is advisory — a long session under pressure will skip it |

That last row is the point most worth internalising if these workflows are new to you: **a rule in a
prompt is a strong suggestion, a rule in a hook is a wall.** The repository already relies on this
distinction — `plugin/hooks/hooks.json` has a `platform-guard.sh` on `SessionStart`, and the
plugin's read-only allow-list is what keeps mutating tools behind a prompt. Your hardware rule
belongs in the same layer, not only in the agent's prose. See §6.

### Agent shape: one agent, not two

Write **one** `labview-test-generator`, parameterised by framework, rather than a Caraya agent and
an LUnit agent. Roughly 80% of the content — case selection, the AAA/Gherkin decision, the hardware
gate, the negative-control rule, the reporting format — is framework-independent. The
framework-specific part is a table: how a test is invoked, how a suite is run, how a verdict is
read. Duplicating a 400-line agent to vary a table is how the two copies drift.

Keep the framework knowledge in the doc, and have the agent read one section of it.

---

## 3. Two walls that bound the scope. Settle these with the user before building.

### Wall 1 — generated tests cannot reach class code, and this contradicts a stated rule

Your rule *"unit tests should test the public API of a library or a class, not its internals"* is the
right rule and it is currently **unimplementable for classes**. Measured 2026-08-28
(`labview-unit-testing.md` §3a):

```
Error 53 occurred at LV AI Core.lvlibp:VI generator.vi
Control with type=UDClassInst is not supported
Indicator with type=UDClassInst is not supported
```

The placeholder stub is itself generated through AIXML, so it inherits every AIXML refusal. **Every
class member VI has a class terminal on its pane**, so no generated VI can call an accessor, a
constructor or a method, and the slot pattern does not lift it either — the plug swapped into the
socket would need that same pane. `lvai_placeholder_subvi` reports this honestly as
`errorKind: stubRefused`.

Consequences you must not paper over:

- The agent's reachable subject set today is **ordinary VIs with non-class terminals**.
- For a class, the agent must return `CANNOT PROCEED` naming `UDClassInst`, not attempt a
  workaround, and not silently test something adjacent that it *can* reach.
- The `{LV.SubVI}` → `Replace` method (`docs/vi-server-methods.tsv`) is the untried route that could
  make the static call a supported operation rather than heap surgery — `labview-unit-testing.md` §6
  lists it as unmeasured. That is the highest-value experiment in this whole area.

**UNVERIFIED, worth one cheap measurement:** whether a `.lvlib`-owned subject (not a class — just a
library member with ordinary terminals) can be retargeted onto. §3a documents that retargeting *away
from* a library-owned link needs the `VILSPathRef` disposed of via `ZeroFill`, but retargeting *onto*
a library member is not covered. Since testing a library's public API is exactly the stated goal,
measure this before promising it. → trial **T4** below.

### Wall 2 — every assertion is exact float equality

`lvai_generate_test` emits `Caraya.lvlib\3AAssert.lvclass\3AAssert Equal Value_Variant.vi` for every
case. `Assert Almost Equal_Float.vi` exists in Caraya and **is not wired up**
(`labview-unit-testing.md` §3, §6).

The spike's own three cases (100, 0, −40 °C) were chosen *because* they are exact in IEEE754 after
`C * 1.8 + 32`, and the doc says so. An agent that does not know this will author `37.5 → 99.5` and
produce a red test against correct code — the worst possible output, because it teaches the user to
distrust the tool.

Until a tolerance assertion is wired: the agent must either prove a float expectation is exactly
representable, or refuse the case and say why. Wiring `Assert Almost Equal_Float` into
`TestTools.cs` (with an explicit tolerance in the case JSON) is a small, high-value change — do it
in the same pass as `lvai_run_test`.

---

## 4. The first trials — a ladder, cheapest first

Run these on the Windows station with LabVIEW 2026 Q3 and Caraya installed. **Do them by hand
through the MCP tools before writing a single line of agent prompt.** The purpose is not to build
anything; it is to find out which of the claims above still hold on *this* machine, and to produce
the measurements the agent will later be taught from.

Work in a scratch folder (`C:\temp\UnitTest\`), on copies, with a commit first.

| # | Trial | Command / route | What it settles |
|---|---|---|---|
| **T0** | Baseline | `lvai_status`, then `pylv_status` | Server up, LabVIEW reachable, pylabview bundle provisioned. `pylv_*` answering `notProvisioned` blocks the whole retarget route. |
| **T1** | Reproduce the known-good | Generate a trivial `Celsius To Fahrenheit.vi`, then `lvai_generate_test` with the doc's three cases (100/0/−40) | That the shipped path still works unchanged on LabVIEW 2026 Q3. If this fails, stop and fix the tool — nothing downstream is meaningful. |
| **T2** | **Run it, and read the verdict** | Author the §4 harness by hand once: `Caraya.lvlib\3ARun Tests.vi`, instance `Run Test (Scalar Path)`, `adapt="true"`, `Interactive`=**false**, `Report Path` = `…\report.xml` | The exact shape `lvai_run_test` must encapsulate. Record the JUnit XML verbatim and the `errorCode`. |
| **T3** | **Prove the harness can fail** | Change one expectation to a wrong value. Re-run. | **The most important trial.** You must see `tests="3" failures="1"` and see it in the *XML*, not the error cluster. §3b records a defect where an all-green run hid two cases that never executed. An all-green first run proves very little. |
| **T4** | Library-owned subject | Put the subject in a real `.lvlib` (saved by LabVIEW, so it carries `LIvi`/`LIbd`), then `lvai_generate_test` against it | Wall 1's open question. Expect either a clean pass or a retarget failure in `VILSPathRef` handling. Either answer is publishable. |
| **T5** | Class subject — confirm the refusal | `lvai_placeholder_subvi` against a `.lvclass` accessor | That `errorKind: stubRefused` still names `UDClassInst`. Confirms the boundary the agent will enforce. |
| **T6** | Non-scalar terminals | A subject with two inputs, then one with a cluster, then an array | `labview-unit-testing.md` §6 lists these as untried. The placeholder generator is claimed to handle them; only 1×1 `double` was measured. |
| **T7** | Error-cluster contract | A subject with `error in`/`error out`. Case: error in ⇒ VI must not execute and must pass the error through | Whether the current case JSON can even express an error-cluster input. Probably not — that is a `TestTools.cs` gap, and it matters because "error in short-circuits" is a real LabVIEW contract that is commonly broken. |
| **T8** | Folder discovery | `Run Test (Scalar Path)` against a **folder** with `Inspect Recursively` | §6 lists this as unmeasured; it is the realistic CI shape and it decides whether `lvai_run_test` takes a file or a directory. |

**Traps to expect while doing this, all previously measured:**

- **Never retry a failed generation under the same `testViPath`.** A failed *validation* leaves a
  phantom under the document's `_name`, which is derived from the test file name. The retry fails
  with `Error 1051` ("a LabVIEW file of that name already exists in memory") even though the first
  attempt wrote nothing. Neither closing the project nor evicting via a throwaway project clears it
  — both were tried. Fix the *first* error, then generate under a **fresh name**; the old one stays
  poisoned until LabVIEW restarts.
- **`7002` is the pass/fail signal, not a fault.** `resource\errors\Caraya-errors.txt` defines it as
  `Caraya Test Manager: Test Suite failed`. Green returns `errorCode 0`.
- **`7101 At least one test is not in a executable state`** is *not* a test failure. It means the
  test VI will not compile — nearly always a connector-pane mismatch after the subject was
  regenerated, not a defect in the code under test. §3a has the full symptom.
- **Caraya is invisible to `lvai_palette_index`** (its `.mnu` files live in
  `vi.lib\addons\_JKI Toolkits\dynamic_palette\`, which the index does not scan). A miss in the
  index is *not* proof a `Call` is illegal. Get target spellings by exporting NI's own example,
  `Caraya\examples\tests\Test Addition.vi`, and copying `target=` verbatim.

Write the results up as a dated section in `docs/labview-unit-testing.md`, in the house style:
record failures, not just the workaround.

---

## 5. How the agent knows whether tests passed

This deserves its own section because it is where a naive implementation silently lies.

### Caraya — measured

**Read the JUnit XML report. Never the error cluster.**

```xml
<testsuite name="Test Celsius To Fahrenheit" errors="0" skipped="0" tests="3" failures="2" …>
  <testcase classname="…" name="boiling point - 100 C should be 212 F">
    <failure message="{Expected value: 212.000000, Asserted value: 192.000000}">"FAIL"</failure>
```

- The report format is chosen by the **file extension** of `Report Path`. `.xml` selects the JUnit
  writer. `.txt` falls through `AutoSelect Test Report.vi`'s `Default` case to
  `Create DefaultReport.vi` and **no file appears** — measured twice, while `error out` reported the
  run had completed. Force `.xml`.
- The error cluster carries the **first** failed assertion only, because `Merge Errors` keeps the
  first. A partial run and a single failure are indistinguishable from it.

The verdict table the agent must apply:

| Evidence | Verdict | Agent must report |
|---|---|---|
| XML present, `failures=0 errors=0`, `tests` == cases authored | **pass** | green, with the case count |
| XML present, `failures>0` | **fail** | each failing case name + `message` |
| XML present, `tests` < cases authored | **INVALID RUN** | cases did not execute — investigate before believing anything |
| `errorCode 7101` | **not executable** | the test does not compile; suspect the connector pane, not the subject's logic |
| No XML file | **INVALID RUN** | never report pass; the writer was not selected |
| `errorCode 7002`, XML present | use the XML | 7002 is "suite failed", a signal not a fault |

That third row is the lesson from §3b, generalised: **compare the executed case count against the
authored case count, every run.** It is the only cheap defence against a test that silently skipped
most of its work.

### LUnit — UNVERIFIED, measure before designing

From the public material (`github.com/Astemes/Astemes-LUnit`, `lunit.astemes.com`), LUnit is
**class-based**: a test case is a `.lvclass`, and the framework discovers and runs test methods
through dynamic dispatch. That shape is why Caraya is "the straightforward one" — a Caraya test is a
plain VI calling library VIs, which is exactly what the generator can author
(`labview-unit-testing.md` §5 makes this argument against JKI VI Tester for the same reason).

**If that is right, LUnit runs straight into Wall 1.** A test-case VI would carry the test-case
class on its pane, `UDClassInst` would be refused, and the placeholder route would not lift it
either. `lvai_create_class` exists, but generating **dynamic-dispatch override VI bodies** is
unmeasured (§5).

So the LUnit phase does not start with an agent. It starts with three measurements:

1. Export an LUnit example test case with `lvai_convert_vi_to_aixml` and look at the pane. Does the
   test method actually carry the class, or does LUnit accept plain VIs by convention?
2. If it carries the class: can `lvai_create_class` + `lvai_create_accessors` produce a
   dynamic-dispatch override at all? Try one throwaway `ValidateAIXML` with a `UDClassInst` control
   before anything else — it is a five-second answer.
3. How does LUnit report? If it emits JUnit XML, the §5 verdict logic is reusable verbatim and the
   framework table in the doc grows one row. If it reports some other way, that is a second parser.

Do not write LUnit content into the agent until (1) and (2) are answered. If Wall 1 blocks it, the
honest outcome is a documented `CANNOT PROCEED` for LUnit plus a note that it unblocks when AIXML
accepts `UDClassInst` — the same shape `docs/lvclass-creation.md` §3 already uses.

---

## 6. The hardware safety gate — three layers, because prose is not enough

Your rule: *"before executing unit tests, ask if hardware is involved and if it is safe to execute
those unit test VIs."* Correct rule. Here is why the obvious implementation fails, and what to do.

**A spawned subagent has no user and cannot ask.** This repository already learned that — every
existing agent uses the `NEEDS CLARIFICATION` / `CANNOT PROCEED` pattern and the orchestrator relays
the question, then continues **the same agent** via `SendMessage` rather than re-spawning it. Do not
put `AskUserQuestion` in the agent. The question must travel out as a block and the answer must come
back into the same context.

Three layers, weakest to strongest:

**Layer 1 — static pre-flight (evidence, not a question).** Before any run, export the subject and
its call graph and scan for hardware and side-effect primitives: `DAQmx`, `VISA`, `NI-Scope`,
`NI-FGEN`, `IVI`, `FPGA`, serial, TCP/UDP, `Motion`, plus destructive file I/O and `System Exec`.
`lvai_describe_vi` and `lvai_convert_vi_to_aixml` give you the diagram; `pylv_extract` works with no
LabVIEW at all. Classify:

- **clean** — pure computation, no I/O found ⇒ run without asking. This is the common case and
  asking every time trains the user to click through.
- **dirty** — a hardware or destructive primitive found ⇒ **stop**, name the primitive and the VI it
  sits in, and return `NEEDS CLARIFICATION`.
- **inconclusive** — dynamic dispatch, a Call By Reference, a `.lvlibp`, or a subVI that could not be
  read ⇒ treat as dirty. Say *why* it is inconclusive.

The scan is cheap and it makes the question specific. "May I run this?" gets a reflexive yes.
"`Read Sensor.vi` calls `DAQmx Start Task` — is the rig connected and safe to actuate?" gets a real
answer. Specificity is what makes a safety gate survive contact with a busy user.

**Layer 2 — agent hard rule.** In `## Hard rules`, first item: never call
`lvai_run_vi_as_top_level`, `lvai_run_vi_and_read_values`, or `lvai_run_test` before the pre-flight
has run and, if dirty or inconclusive, an explicit human approval has come back. Approval covers
*that* subject and *that* session, not the next one.

**Layer 3 — `PreToolUse` hook, the actual wall.** Matcher on the three execution tools. It reads the
tool input, and blocks unless an approval token exists for that path — e.g. a line in
`.labview-mcp/approved-runs.json` written only by an explicit user action. Deny with a message that
tells the agent how to get approval, so it routes to `NEEDS CLARIFICATION` instead of thrashing.

Layer 3 is what makes the guarantee real. Layers 1 and 2 are what make it pleasant. Ship 1 and 2
first if you must, but do not describe the result as a safety guarantee until 3 exists — and say so
in the agent's report.

**Extra rule the user did not list, and should have:** a test VI is a *top-level* VI that Caraya
loads dynamically and runs. If the subject actuates hardware, the test does too — every time, in CI,
unattended, possibly in a loop. So the agent must also flag the *maintenance* consequence: a test
that touches hardware is not a unit test, and the right answer is usually to test the pure logic and
put the hardware behind a boundary. Say it once, as advice, then do what the user asked.

---

## 7. AAA versus Gherkin — where the structure actually lives

The user wants the choice to be theirs. Two implementation notes that make this concrete rather than
decorative:

**Store the preference, do not ask every time.** A project-level setting (a key in the project's
`CLAUDE.md`, or `.labview-mcp/test-style.json`) with values `aaa` | `gherkin`. Ask once, on first
use in a project, via the orchestrator; then honour it silently. A subagent asking a settled style
question on every invocation is the fastest way to get its rules ignored.

**In a Caraya test the structure can only live in two places**, because a test is a diagram:

1. **The case label**, which becomes the JUnit `name=` attribute — and therefore *is* the sentence a
   human reads when it fails. This is the highest-leverage naming decision in the whole feature.
   - AAA: `boiling point - 100 C should be 212 F` (the spike's own style)
   - Gherkin: `Given 100 C When converted Then 212 F`
2. **`FreeLabel` diagram comments** — and here is the measured reason this works: per
   `labview-vi-editor.md`, `FreeLabel` is **the one annotation that survives a regeneration**.
   Decorations, layout, colours and fonts are all lost. So Arrange/Act/Assert or
   Given/When/Then markers placed as `FreeLabel` are the only structural annotation that persists.
   `scripts/pylv-place-labels.py` exists to put a comment where you meant it.

That is a real constraint doing real work: the style preference is implementable *because* of a
measured fact about what survives the round trip. Cite it in the agent so the next session does not
try to express AAA through diagram layout and lose it.

---

## 8. The rule set

Draft these as the agent's `## Hard rules`, matching the house voice — imperative, each with its
reason, and the ones learned from a failure saying so. The **Surface** column says where each rule
actually belongs; several are not prompt rules at all.

### Safety and honesty

| Rule | Surface | Reason |
|---|---|---|
| Never execute before the hardware pre-flight; dirty or inconclusive ⇒ `NEEDS CLARIFICATION` | Hook + agent | §6 |
| **A generated test that has never been seen to fail is not evidence.** After a green run, deliberately break one expectation, confirm the failure count, restore it | Agent | §3b: an all-green run hid two cases that never executed |
| Compare executed case count against authored case count, every run | Tool (`lvai_run_test`) | Same defect. Cheap, mechanical, catches the silent class |
| Report the verdict from the JUnit XML, never from `error out` | Tool | §4 |
| Never claim coverage that was not measured; name what was *not* tested | Agent | House evidence discipline |
| Never modify the subject to make a test pass | Agent | The superseded borrowed-placeholder route did exactly this (shaping the subject's `conIdx` to fit the stub) and it is recorded as the reason that route was abandoned |
| Say in the report which tools mutated what, and whether LabVIEW was started | Agent | Matches the other four agents |

### Design of the tests

| Rule | Surface | Reason |
|---|---|---|
| Test the public API — a `.lvlib`'s exported members, a class's public methods. Never a private member | Agent | User's rule. **Bounded by Wall 1**: classes are unreachable today; library members pending T4 |
| Cases come from **boundaries and equivalence classes**, not three arbitrary numbers: zero, empty array, empty string, min/max, negative, off-by-one, and the documented error path | Agent | The spike's 100/0/−40 were chosen for float exactness, not coverage — do not cargo-cult them as a template |
| A float expectation must be **exactly representable** after the subject's arithmetic, or the case is refused with the reason | Agent + tool | Wall 2. Lift this rule when `Assert Almost Equal_Float` is wired |
| Test the **error-cluster contract**: `error in` set ⇒ the VI must not execute and must pass the error through | Agent | A real, commonly-broken LabVIEW contract. Blocked until T7 says the case JSON can express it |
| One behaviour per case; the label must identify what failed without opening the diagram | Agent | The label *is* the JUnit `name=`; §7 |
| Never test LabVIEW primitives or the framework itself | Agent | Standard |
| **Beware state between cases**: all cases run sequentially in one test VI, so an uninitialised shift register, an FGV or a non-reentrant subject leaks from case to case. Detect it, and either reset between cases or report that the subject is not unit-testable as written | Agent | LabVIEW-specific and easy to miss. Ordinary xUnit isolation does not apply here — there is no per-case fixture |
| Deterministic only: no wall-clock dependence, no dialogs, no user interaction, no network | Agent | A modal dialog stops the whole gRPC service (§4) |
| Clean up what the test creates; a re-run must give the same answer | Agent | Standard |

### Mechanics that must not be re-derived

| Rule | Surface | Reason |
|---|---|---|
| `Interactive` = **false**, `Report Path` = **`.xml`** | Tool, hard-coded | §4; `.txt` writes nothing silently, `true` hangs the transport |
| After a failed generation, **never** reuse the same `testViPath` — fix the first error, then use a fresh name | Agent + tool guard | `Error 1051` phantom, measured twice |
| Prefer the static subVI call (one node per case) over VI Server (three nodes and two wires) | Already in the tool | §3 comparison table |
| Get Caraya target spellings from NI's own example export, not from the palette index | Doc | Caraya is invisible to the index; a miss is not proof |
| `7101` ⇒ suspect the connector pane, not the logic. Re-run after any regeneration of the subject | Agent | §3a: regeneration moves the terminals and the error never mentions the pane |
| If the VI Server route is ever used: each case gets its own clean error path, assertions merged at the end — never chain a case on the previous assertion's `error out` | Doc + tool | §3b, the defect that produced a false green |

---

## 9. Build order

1. **T0–T3.** Confirm the shipped path works and that you can make a test go red on purpose. Write
   the results into `docs/labview-unit-testing.md` with the date.
2. **`lvai_run_test`** in `TestTools.cs`: run + JUnit parse + the §5 verdict table, with
   `Interactive`/`.xml` hard-coded and the case-count check built in. Unit-test it in
   `tests/LabVIEWMCP.Tests` against captured XML — no LabVIEW needed for the parser.
3. **`Assert Almost Equal_Float`** with an explicit per-case tolerance. Lifts Wall 2.
4. **T4–T8.** The scope questions. Each answer either widens the agent's remit or produces a
   documented refusal.
5. **`lvai_unit_test_reference`** — an embedded queryable doc alongside `lvai_aixml_reference` and
   friends, plus an MCP resource `labview://unit-test-reference`. This is how the agent gets the
   facts without carrying them in its system prompt.
6. **The hardware pre-flight**, as a tool or a documented procedure, and the **`PreToolUse` hook**.
7. **`labview-test-generator.md`**, one agent, framework-parameterised. Only now — it is thin if
   steps 2–6 did their job.
8. **LUnit measurements** (§5), then a framework row in the doc, then possibly a second reporting
   parser.

Steps 2 and 3 are independent of 4–8 and can go first regardless of what the trials say.

---

## 10. Anti-goals

- Do not put the measured facts in the agent prompt. They belong in the doc and the reference tool.
- Do not write two framework agents.
- Do not let the agent author the Caraya run harness from prose. That is `lvai_run_test`'s job.
- Do not report a pass without a parsed XML report in hand.
- Do not work around Wall 1. `stubRefused` is the honest answer; a workaround that appears to test a
  class is worse than a refusal.
- Do not claim the hardware gate is a guarantee until the hook exists.

---

## 11. Agent frontmatter — the trap that costs a session

Keep `description:` a **folded block scalar** (`description: >-`, text indented two spaces on the
next line). An unquoted YAML plain scalar **cannot contain `: `** — colon followed by space. On
2026-08-13 all three agents had `IMPORTANT for the orchestrator: pass in …` in their descriptions,
the frontmatter failed to parse, and **all three agents vanished from the roster in silence** — the
error says "not found", which reads as a missing file. Inside a block scalar, colons, quotes and `#`
are literal.

Every existing agent in `.claude/agents/` carries an HTML comment saying this directly under the
frontmatter. Copy it.

Also: a registered agent is measurably cheaper than handing the definition to `general-purpose` as a
task prompt — 4 min 06 s versus 5 min 07 s / 5 min 43 s for the same VI, because a registered
definition *is* the subagent's system prompt rather than a 31 kB file it must read first.
