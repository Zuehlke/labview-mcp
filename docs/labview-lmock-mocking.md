# LMock — generating and using mock classes

Astemes LMock generates mock classes for LabVIEW unit tests. It is an **add-on to a test
framework, not a framework** — the same mock class is verified from an LUnit test case or from a
Caraya test VI, through two different bridge VIs. Everything below was measured on
2026-09-14 against the LMock installed in
`C:\Program Files (x86)\National Instruments\LabVIEW 2026`, with the evaluation artefacts under
`C:\temp\Lmock_Eval`.

The headline: **the whole route is scriptable and nothing about it needs an IDE gesture**, which
is not what LMock's own documentation suggests — it describes mock creation as a right-click in
the Project Explorer.

## 1. Where LMock lives

| what | where |
|---|---|
| the API and the scripting code | `vi.lib\Astemes\LMock\` |
| the project provider (the right-click entry) | `resource\Framework\Providers\LMock\` + `GProviders\LMock_signed.ini` |
| palette | `menus\Categories\functions_Astemes_lib_LMock.mnu` (6 VIs) and `…\_functions_Astemes_lib_LMock\functions_Astemes_lib_LMock_for_Caraya.mnu` (1 VI) |
| shipping example | `examples\Astemes\LMock\Serial Driver\Serial Driver Development.lvproj` |

`lvai_palette_index` answers 7 hits for `LMock` after a `refresh`: `At Least.vi`, `Exactly.vi`,
`Never.vi`, `One or More.vi`, `One.vi`, `Verify.vi` and `Verify LMock for Caraya.vi`.

**`lvai_example_index` DOES NOT FIND THE LMock EXAMPLE, and a refresh does not fix it.** Rebuilt
here on 2026-09-14 (630 plain examples, 972 including specialised): `LMock` answers *no match*, and
so does `Serial Driver`, with `includeSpecialised` on. `Astemes` answers 21 hits and **every one of
them is LUnit** — those VIs carry NI's description and keyword properties, and the LMock tree
carries none at all, on its VIs or on its `.lvproj`. So the index is behaving correctly and the
example is simply not registered.

The consequence for a session is the one this repository keeps relearning in another form: *a miss
in the index is not proof the thing is absent.* Reach for the example by path —
`examples\Astemes\LMock\Serial Driver\` — not by query.

**Read the shipping example before designing anything.** `Serial Driver Development.lvproj` is a
complete worked case — an interface (`Serial.lvclass`, `NI.LVClass.IsInterface = true`), a class
under test (`Driver.lvclass`) that depends on it, a generated mock, and an LUnit test case with
three tests. Its exports are small (about 3.5 kB each) and faithful, so they are the cheapest
possible source for every target spelling on this page.

## 2. Generating a mock class — ONE call, no IDE gesture

`LMock Mock Class Generator.lvclass:LMock Generate Mock Class.vi` is an ordinary public member in
`vi.lib` and takes **paths only**:

| terminal | type | conIdx | flag |
|---|---|---|---|
| `Source Class Path` | `path` | 10 | required |
| `Destination Class Path` | `path` | 9 | required |
| `Add to lvproj?` | `bool` | 6 | optional |
| `error in (no error)` | cluster | 8 | recommended |
| `Created Files` | `array{path.appended path}` | 2 | recommended |
| `error out` | cluster | 0 | recommended |

An AIXML `Call` to `LMock Mock Class Generator.lvclass\3ALMock Generate Mock Class.vi` **validates
with `errorCode 0`**, so a generated helper VI drives it directly. `scripts/lmock_generate_mock.xml`
is that helper: two `string` controls through `String To Path` (a VI Server control value will not
coerce a string into a path), the Call, and the file list back out.

One thing measured and **not** explained, recorded rather than reasoned about. Three generations
of that document landed on two different pane patterns: 4833 with the authored `conIdx` values,
then **4815 with the terminals silently re-assigned** (`conIdx="15"` does not exist on a 12-slot
pattern), then 4833 again. The one that differed was the one generated **while a project was
open**, and the other two ran with it closed — one observation each, so that is a correlation to
test, not a cause. All three came back style-compliant. `CLAUDE.md` has had four wrong revisions
of the pane rule already, so this page adds no fifth theory: measure the pane after generating,
every time, and do not assume the numbers you wrote are the numbers you got.

Measured on the shipped `Serial.lvclass` interface: **31 s of wall clock cold**, six files written
— the `.lvclass`, `Create.vi`, one override per interface method (`Read.vi`, `Write.vi`) and one
`When <Method>.vi` per interface method. The class comes out with **two** `Parent Libraries`
entries, `Mock.lvclass` and the mocked interface, and `lvai_exec_state` reads `1` (eIdle) on its
members. A second run in the same session cost **2.9 s** — the 31 s is LabVIEW loading LMock's
scripting code, paid once.

### THE SOURCE MUST BE AN INTERFACE, and an ordinary class fails LOUDLY and LEAVES A MESS

Measured on three classes from the same example:

| source | `NI.LVClass.IsInterface` | result |
|---|---|---|
| `Serial.lvclass` | true | 6 files, `error out = 0` |
| `Log.lvclass` | true | 4 files, `error out = 0` |
| `Simulated Serial.lvclass` | **absent** | **`Error 1704`**, *"Reference refers to a library that is not an interface"* |

This page asserted the opposite for one revision — that LMock mocks any class with dynamic
dispatch members — on the strength of `Log.lvclass` succeeding. That was an **inference, not a
measurement**: `Log.lvclass` was assumed to be an ordinary class and never checked, and it is an
interface. LMock's own documentation was right all along. The routing question is exactly **"is
the dependency an interface?"** — and if it is not, the dependency has to be *given* one before it
can be mocked.

Two operational consequences, both worse than the error code suggests:

- **`Created Files` IS NOT A RECORD OF WHAT EXISTS.** The 1704 run returned two paths in it —
  `Mock SimSerial LMCP.lvclass` and `Create.vi` — and the destination directory was **empty**.
  So the array is what LMock set out to write, not what survived, and on a failed run the two
  disagree. Check `error out` first and the filesystem second; never infer success from a
  non-empty `Created Files`.
- **EVERY LMock failure so far has raised a MODAL DIALOG** — 1055 for the missing project, 1704
  for the wrong source type, both stopping the gRPC service until a human clicked Continue. The
  error cluster is returned *as well*, but far too late to matter.

So a productised `lvai_generate_mock_class` **must validate before it calls LMock, not after**:
read `NI.LVClass.IsInterface` straight out of the `.lvclass` — it is plain XML, needs no LabVIEW,
and costs nothing — and refuse a non-interface locally. Anything that reaches LMock in a state it
dislikes costs a human interruption, which is the one failure mode an unattended run cannot
absorb.

**The generated mock is equivalent to the one NI ships.** Diffing the AIXML export of the
generated `When Read.vi` against the example's own, with the class name normalised, leaves *only
uid numbering* — every element, type, net and target identical. `Read.vi` differs by one extra
`Merge Errors`, which the installed LMock adds and the older example does not.

### The trap: `Add to lvproj?` defaults to TRUE, and the failure is a MODAL DIALOG

Leaving that optional input unwired does **not** mean "do not add to a project". It means the
callee's default, and the default is TRUE — so LMock calls
`LMock Add New Mock to Project.vi`, which reaches LabVIEW through `Project:Active Project` and
answers **`Error 1055`** when no project is active. That error surfaced as an on-screen error
dialog that a human had to dismiss, and **a modal dialog stops the whole gRPC service** until they
do.

The files are all written first, so the run is not lost — but the session is blocked. So:

- **wire `Add to lvproj?` = FALSE explicitly**, or
- open the project and make it active first (`lvai_open_file`, and check `projectBecameActive`).

`scripts/lmock_generate_mock.xml` wires the FALSE constant, and that is **verified rather than
assumed**: re-run with the project deliberately closed, it answered `error out = 0` and wrote its
four files with no dialog at all. The mock then has to be added to the `.lvproj` separately, which
is the same split `lvai_create_class` already makes.

This is a genuine counter-example to `CLAUDE.md`'s standing rule that a `recommended` or
`optional` input stays unwired unless you have a real value. The rule's reasoning is about
surplus constants on typedef panes; it does not cover an optional input whose default triggers a
side effect. **Where an optional input selects a SIDE EFFECT rather than a value, wire it.**

Not measured: whether LMock creates the destination directory. It was created beforehand here.

## 3. Writing the test — every LMock target resolves in AIXML

Measured by validating a probe with nothing wired, so the only complaints were unwired required
inputs — which is the signature of a target LabVIEW resolved and read the connector pane of:

| what you want | `Call target=` | note |
|---|---|---|
| expectation count | `LMock.lvlib\3AOne.vi` | **malleable** — also needs `adapt="true"` and `instance="LMock One.lvlib\3AOne Identical Inputs.vi"` |
| …the others | `LMock.lvlib\3AExactly.vi`, `…\3AAt Least.vi`, `…\3ANever.vi`, `…\3AOne or More.vi` | each has its own `<Name>.lvlib` of instances |
| verify, **LUnit** | `LMock.lvlib\3ALMock Verifier.lvclass\3AVerify.vi` | `LUnit Test Case In`, `Mock`, `Description` → `LUnit Test Case Out` |
| verify, **Caraya** | `Verify LMock for Caraya.vi` | **bare name**, validated `errorCode 0` on its own |

**The LUnit verifier is a THREE-part qualifier — library, then class, then VI.** `CLAUDE.md` §9
records that a library's own *folders* are not part of a qualifier; a **class inside a library**
is. `LMock.lvlib\3AVerify.vi` is not the spelling and `Verify.vi` is not either.

**The Caraya bridge is a loose VI in `vi.lib`**, not a library member, so it resolves by bare name
— and it takes only `Mock In` and the error cluster, with no test-case object. A Caraya LMock test
is therefore *simpler* to author than an LUnit one: the test VI is an ordinary VI with no
class-typed connector pane, so `lvai_generate_vi` can build it and only the mock and subject calls
need the placeholder route.

### The shape of a test

From the example's `Test 1`, which is the canonical expectation test:

1. `<Mock>.lvclass:Create.vi` → the mock
2. `LMock.lvlib:One.vi` (or `Exactly`/`At Least`/`Never`/`One or More`) → puts the mock in
   recording mode
3. call the **mocked method** on that recording mock, with the inputs you expect — this *records*
   the expectation, it does not run anything
4. build and exercise the system under test with the mock wired in
5. `Verify` with the mock from step 3

Stubbing return values is the other half and needs no expectation: call `When <Method>.vi`, whose
inputs are the interface method's **outputs** (data flows reversed), then use the mock.

## 4. The full generated-test route, and what it costs

Nothing below needs a tool that does not already exist, except step 1.

| step | call | note |
|---|---|---|
| 1 | **`lvai_generate_mock_class`** | ~31 s cold, 2.9 s warm. Wraps §2 with the pre-flight below |
| 2 | `lvai_placeholder_subvi` with `viPaths` | one call for every mock and subject VI the test calls; class terminals become `path` stand-ins |
| 3 | author the test AIXML | `Call`s to the stubs by bare name, `Call`s to LMock by the spellings in §3 |
| 4 | `lvai_lunit_add_test_method` | converts WITHOUT validating, forces pane 4815, retypes the class terminals, adds class membership |
| 5 | `lvai_swap_subvis` | repoints every stub onto the real class VI; `{LV.SubVI}` `Replace` re-types the wires |
| 6 | `lvai_run_lunit_tests` | or the Caraya runner |

For Caraya, step 4 becomes `lvai_generate_vi` and step 6 `lvai_generate_caraya_test_runner`.

**A regeneration LOSES the swap, every time.** Re-running step 4 over an existing method rewrites
the VI from the AIXML, which names the stubs — so the suite then reports the case as **`Broken`**,
not `Failed`. Measured here by regenerating and running without re-swapping. Step 5 must follow
step 4 on every pass, including a re-run that only changed a constant.

### Measured end to end

One test method (`Test 4 Read Voltage Sends Measure Command.vi`) added to the example's existing
`Driver Test.lvclass`:

- suite before: 3 tests, 0 failures
- suite after: **4 tests, 0 failures**, and `lvai_swap_subvis` reported `socketsLeft: 0` with
  `callTargets` reading exactly like NI's own hand-built Test 1.
- **negative control**: the expected command changed from `MEASURE:VOLTAGE?` to `WRONG:COMMAND?`,
  regenerated and re-swapped → **1 failure, naming the case**, with LMock's own message:

  ```
  Write.vi Expected Once but Never Called with Expected Inputs
  Call 1: error in (no error): No Error(Cluster),
          write buffer: {-WRONG:CO-}M{+E+}{-M-}A{-ND-}{+SURE:VOLTAGE+}?(String)
  ```

  That is a character-level diff, `{-expected-}` against `{+actual+}`, and it arrives already
  parsed in `cases[].failures[].text` from `lvai_run_lunit_tests`. Nothing has to be looked up to
  report why a mock expectation failed.

## 5. What this means for the toolset

**Only one primitive was missing**, and it is the one that fits the shape `CLAUDE.md` names as a
tool waiting to be written — cheap for LabVIEW, expensive in turns, and never varying.
**`lvai_generate_mock_class` now exists**: it wraps §2 with `Add to lvproj?` forced FALSE, the
destination directory created, and — the part that matters most — the interface check done from
the file *before* LabVIEW is touched.

**Its pre-flight is the design, not a convenience.** Both refusals measured here reached a human
as a modal dialog, and a modal dialog stops the gRPC service until someone clicks Continue, so
"call LMock and read `error out`" is not a usable contract: by the time the cluster comes back the
session is already blocked. Everything decidable from files is therefore decided from files —
`NI.LVClass.IsInterface`, the destination's extension, whether it already exists — and `ok` is
judged from the filesystem rather than from `Created Files`, which a refused run was measured
populating with two paths it never wrote.

### Acceptance, 2026-09-14, against a freshly started LabVIEW

Run after the build, with the client restarted so the tool was really served:

| case | result |
|---|---|
| interface → fresh destination | `ok: true`, 6 files created, **6 on disk**, `errorCode 0`, no dialog, destination directory created by the tool. 11.8 s including one helper generation |
| same again, no `overwrite` | `destinationExists` |
| same again with `overwrite: true` | `ok: true`, `helperGenerated: false` (cache reused). **35 s** |
| ordinary class (`Simulated Serial.lvclass`) | `sourceIsNotAnInterface`, `ancestors: ["Serial.lvclass"]`, **no directory created** |
| destination = the interface itself, `overwrite: true` | `destinationIsTheSource` — the guard correctly outranks `overwrite` |
| `lvai_exec_state` on the generated `Read.vi` | `1` (eIdle), no linker errors |
| the four-test LUnit suite over the earlier generated mock | 4 tests, 0 failures |

**THE NON-INTERFACE REFUSAL CAME BACK WHILE THE gRPC SERVICE WAS UNREACHABLE.** That was an
accident of ordering — LabVIEW had not been restarted yet, and `lvai_status` in the same breath
answered "could not find a port serving lvai.LVAI" across 28 candidates. It is the strongest
evidence the design could get, and stronger than anything planned: the pre-flight demonstrably
touches no LabVIEW at all, so the refusal costs nothing and cannot itself provoke the dialog it
exists to prevent. Worth keeping as the regression others should reproduce — **stop LabVIEW and
check the guard still answers.**

One number is unexplained and is recorded rather than theorised about: the **overwrite** run took
35 s against the first run's 11.8 s, though the helper was already cached. An `lvai_exec_state`
read had loaded the mock into LabVIEW's memory in between, which is a plausible cause and one
observation. Do not budget from the faster figure.

The next candidate, not built: `lvai_generate_mock_test` for the stereotyped skeleton of §3 —
create the mock, record an expectation, exercise the subject, verify. Same reasoning as
`lvai_generate_caraya_test_runner`, whose hand-authoring was 186 s of wall clock against 6.1 s
inside LabVIEW.

**LMock does not want an agent of its own.** The unit-test agents are cut along the *framework*
axis — `labview-caraya-unit-test`, `labview-lunit-unit-test`, `labview-vitester-unit-test` — and
LMock is orthogonal to that axis: the same mock class serves Caraya and LUnit, and only the
verifier VI differs. A third agent would have to carry both frameworks' rules and be kept in step
with both, which is the drift that `plugin/agents/` already demonstrated. The LMock phase belongs
**inside** the two existing framework agents, and the routing question they need to answer is a
single one, settled from a file with no LabVIEW running: *is the dependency an interface? then
mock it instead of hand-writing a simulator.* If it is not an interface, mocking is not available
until someone extracts one — which is a design change to the code under test, and therefore the
user's call, not the agent's.
