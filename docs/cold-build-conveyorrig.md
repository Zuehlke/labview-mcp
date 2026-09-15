# A twelfth cold build — the schema is CLOSED, and the eleventh build's queued fix cost a second one

Built from nothing on 2026-09-15, a few hours after ShakerRig, through `labview-class-generator`
handing off to `labview-caraya-unit-test`. Artefacts under `C:\temp\ConveyorRig`. Final state:
**3 `.lvclass`, 16 members all `execState 1`, 31 Caraya tests, 2 failures — both of them the
negative controls**, zero LabVIEW restarts.

The build itself is unremarkable and that is worth stating plainly: an interface with one dynamic
and one static member, two classes implementing it, six accessors each, one override each, two
suites. Everything below is what re-measuring the build agent's report actually settled — **four of
its eight findings held, two were overstated, one was already documented, and the cheapest one it
did not notice at all.**

## 1. What was built

| artefact | what it exercises |
|---|---|
| `ISpeedSensor.lvclass` — `Read Speed.vi` (**dynamic**) and `Describe.vi` (**static**, concrete) | both member kinds on one interface |
| `EncoderSensor.lvclass` — Tag, Pulses Per Revolution, Last Count | implements it; override divides |
| `TachoSensor.lvclass` — Channel, Volts Per RPM, Last Voltage | implements it; override divides |
| 12 accessors, 2 overrides, 2 runners, 12 test VIs | the rest |

Both classes: `interfacesLinked: 1`, `parentLinks[0].kind: "interface"`, `parentKindsAreComplete:
true`, `inheritsFrom` staying `LabVIEW Object`. Both guards measured with a **non-zero numerator**
(`12.5 / 0` returning `0`, not `+Inf`), which is what separates the guard from the arithmetic.

## 2. THE `-2628` FAMILY IS NOT THREE ATTRIBUTES — THE SCHEMA IS CLOSED

`docs/cold-build-shakerrig.md` §2, committed hours before this build, measured `outputs` on a
`<Control>` and `inputs` on an `<Indicator>` as required even when the terminal is unwired. That
holds and is not re-litigated here. What this build adds is the **scope**, measured as eight
one-element documents differing in nothing but one attribute:

| document | `ConvertAIXMLToVI` | `ValidateAIXML` says |
|---|---|---|
| `<Control>` no `outputs` | **-2628, 0 bytes** | `Line 3, Column 112, missing required attribute 'outputs'` |
| the same with `outputs` | `0`, 3 792 bytes | — |
| `<Indicator>` no `inputs` | **-2628, 0 bytes** | `Line 3, Column 117, missing required attribute 'inputs'` |
| **`<Constant>` no `outputs`** | **-2628, 0 bytes** | — |
| the same with `outputs` | `0`, 3 584 bytes | — |
| **legal `<Control>` plus `foo="bar"`** | **-2628, 0 bytes** | `Line 3, Column 149, attribute 'foo' is not declared for element 'Control'` |
| `<FreeLabel comment="…">` | `0`, 4 133 bytes | — |
| **`<FreeLabel text="…">`** | **-2628, 0 bytes** | `attribute 'text' is not declared for element 'FreeLabel'` **and** `missing required attribute 'comment'` |

So `<Constant>` joins the other two, and — the general statement — **an attribute the schema does
not declare costs the whole document wherever it appears.** These are not three special rules. They
are one closed schema with a required set and a declared set, and `ValidateAIXML` reports both
halves with a line and a column. A misspelt attribute name is therefore the same fault as a missing
one, which is why `<FreeLabel text=…>` produces two messages rather than one.

**The practical rule, which no `docs/` page states in one line: every `-2628` names its own cause on
the VALIDATE path and none of them do on the CONVERT path.** Measured on the same files: validate's
`errorMessage` carries an `Errors:` block with attribute, line and column in 5–8 ms;
`lvai_convert_aixml_to_vi` returns the identical error code with that block absent. So a `-2628` is
never a mystery — it is a mystery only to whoever converted without validating. That is not a
hypothetical route: `lvai_add_class_method` converts without validating **on purpose**, because the
validator is genuinely stricter for a class wire, and that is exactly where
`docs/cold-build-datalogger.md` §2 paid to diagnose the missing-`value` case by hand.

### THE QUEUED CHECKER FIX COST A SECOND BUILD WITHIN HOURS

Neither cheap checker sees any row of that table. Re-confirmed here:

| document | `lvai_check_aixml` | `scripts/aixml_lint.py` |
|---|---|---|
| `<Control>` no `outputs`, nothing reads it | `ok: true`, 0 errors, 0.09 ms | `[clean]` |
| `<Indicator>` no `inputs` | — | `[clean]` |
| `<Constant>` no `outputs` | — | `[clean]` |
| `<FreeLabel text=…>` | `ok: true`, 0 errors, 0.06 ms | `[clean]` |
| legal `<Control>` + `foo="bar"` | — | `[clean]` (one unrelated net warning) |

The lint answers `net-unproduced` on a document where the missing `outputs` *also* breaks a net —
right verdict, wrong reason, and it says nothing about the attribute. **Both checkers learned the
rule before this session ended; §7 has what shipped and what deliberately did not.**

ShakerRig's commit message says the checker gap "needs a rebuild and is queued separately", which is
an honest and reasonable call. The measured consequence is that **the next build re-derived the same
rule from scratch the same afternoon** — here it cost three converts and a bisect, because the first
probe was written with a missing `outputs=` in *both* arms and therefore failed in both. That is the
cheap version. The expensive version is a class-method chain where the same document reaches
`ConvertAIXMLToVI` with no validate in front of it. **A queued fix is a fix the next session pays
for**, and this is the second time this repository has measured that shape — the first being a
remedy written into `docs/class-method-tooling.md` D1 that nobody moved into code for twelve days.

### The process note, because it changed the outcome

The first probe of `<FreeLabel text=…>` answered `-2628` **in both arms**, control included. A probe
that fails in both arms proves nothing — and rather than reporting the intended finding anyway, the
bisect that followed is what produced this whole section. The control was not a formality; it was
the measurement.

## 3. `lvai_generate_method_test` — TWO CONFIRMED, AND THE HEADLINE CLAIM OVERSTATED

### 3a. Two fields in one case: confirmed, and the refusal is a raw .NET exception

There is no way to seed two fields for one case — one `writeField`, one `value` — so the single most
valuable test in each suite, the actual division (`12.5 / 0.5 = 25.0`), was hand-authored through
placeholder + swap in **both** suites independently. `inputs` cannot stand in: it names the
*method's* terminals, and `Read Speed.vi` has none besides the class wire.

Reaching for the obvious spelling is worse than unsupported. `"writeField":["Last Count","Pulses Per
Revolution"]` answers:

    {"ok": false, "errorKind": "InvalidOperationException",
     "error": "The node must be of type 'JsonValue'."}

A raw exception type, no case index, no key name, no accepted shape. **Yesterday's unknown-key
refusal does not cover a recognised key carrying the wrong value KIND** — the guard exists for
`expectErrorCode` alone, where a quoted number is refused by name, and for nothing else.

### 3b. A `.vi` suffix in `method` is blamed on the filesystem: confirmed verbatim

`"method":"Read Tag.vi"` answers *"'Read Tag.vi' has no .vi beside the class - expected 'Read Tag.vi.vi'.
Add the method with lvai_add_class_method first."*

The method exists; the argument carries a suffix. The remedy offered is the one thing that cannot
help. Four lines: detect the suffix and say so.

### 3c. OVERSTATED — the defaulted required input IS reported, in a named step

The build agent's headline finding was that a required input wired from the tool's default makes an
independence assertion vacuous "silently". Measured as a two-arm call, the same case with and
without `inputs`:

| case | `requiredInputs` step reports |
|---|---|
| no `inputs` | `{terminal: "Tag", type: "string", value: "", source: "this tool's default"}` |
| `"inputs":{"Tag":"PT-101"}` | `{terminal: "Tag", value: "PT-101", source: "the case's inputs"}` |

with a note reading *"Values marked as this tool's default are 0 or empty — if one of them matters
to what the case proves, pass it in the case's `inputs`."* That is the disclosure, by name, with the
remedy. **The tool is not silent and the claim should not have been written as though it were.**

What survives is narrower and still real: the *salience*. `ok: true` and "2 wire-survival
assertion(s)" is the summary a reader takes away from a 66 KB answer, and the qualifier sits in step
five. And the assertion genuinely loses its power in one identifiable case — **when the defaulted
literal cannot be told apart from a no-op**, which for a string field on a fresh object is exactly
`""`. That condition is checkable in about ten lines (a defaulted required input whose literal
equals the case's seed `value`), which is a better remedy than a note either way.

### 3d. OVERSTATED — `diagramSubVis` already documents its own tense

The claim was that `diagramSubVis` is the pre-swap listing and therefore contradicts `socketsLeft: 0`
in the same answer. The listing *is* pre-swap — the helper's own indicator is described as "Every
subVI node name on the diagram BEFORE the swap" — but `lvai_swap_subvis`' description already says
so: *"it shows the state BEFORE the swap it accompanies, so it says what this call was working on
rather than what is left afterwards"*, added 2026-09-15. Residue: the field NAME and the answer's
own `note` carry no tense, so the qualifier lives only in the description. Not a defect worth code.

### 3e. Already documented

`socketsLeft` counting `swapsJson` ENTRIES rather than nodes is `docs/cold-build-kilnrig.md` §2.
Nothing new.

## 4. `lvai_generate_caraya_test_runner` LISTS ONLY ITSELF IN THE `.lvproj`

Confirmed from the source rather than from the symptom: the runner parses every suite path into
`tests` and then calls `ListInProjectAsync(projectPath, testFolderName, [runnerViPath], …)`. It holds
the full list and lists one entry.

For a suite built entirely by the generators this is invisible — each of them lists its own test VI
as it goes. It bites the moment a test VI is **hand-authored**, which §3a makes necessary for any
case needing two fields, and both agents here had to edit the closed project file themselves. The
failure `projectEntry` exists to prevent, reachable by stepping one foot off the generated path.

## 5. Not re-measured

The build agent reported the Caraya runner's `error out.source` naming the **first** suite in the
array rather than the failing one — here a VI that passed 3/3, while the failure was in the negative
control. That is Caraya's own error chaining, not our tooling, and it was not re-measured. Recorded
as observed once, by one agent, so the next reader can recognise it rather than trust it.

## 6. Figures

| | |
|---|---|
| wall clock, build agent (both classes + both suites) | 31 min |
| LabVIEW restarts | 0 |
| `dwarnCount` at start | 41, `looksDegraded: false` |
| members checked with `lvai_exec_state` | 16, all `execState 1` |
| Caraya tests / failures | 31 / 2, both negative controls |
| probe documents converted or validated for §2 | 11 |
| probe cost, §2 entire | under 3 minutes, ~1 s inside LabVIEW |

## 7. Fixed in the same session

All four of the changes the evidence supported, plus the hint that makes the fifth unnecessary.
1 911 tests green, 53/53 in the lint's own suite.

| what | where |
|---|---|
| **`terminalWithoutNetAttribute`** — a `<Control>` or `<Constant>` with no `outputs`, an `<Indicator>` with no `inputs`, ERROR | `AixmlCheck`, and `terminal-no-net-attribute` in `scripts/aixml_lint.py` |
| the matching repair under `fix: true` | `AixmlCheck.Fix` |
| **`schemaHint` on every `-2628`** — naming `lvai_validate_aixml` and the three commonest causes | `lvai_convert_aixml_to_vi` |
| **`RejectWrongCaseValueKinds`** — a recognised key whose value is the wrong JSON kind, refused by name | `TestTools`, shared by all three `casesJson` tools |
| **the runner lists its suites**, not only itself | `lvai_generate_caraya_test_runner` |
| **`methodNameCarriesExtension`** — a `.vi` suffix in `method` named as the argument's fault | `lvai_generate_method_test` |

**The repair is narrower than the check, on purpose and for the third time in this repository.**
Nothing referencing the element's uid means the terminal is unwired, so the spelling is the empty
net. Exactly one net naming it means that is the net, and writing anything else would leave a reader
dangling. **Several** nets naming one uid is reported and left alone — picking one would be a guess,
the same line `indicatorWithoutValue` draws around a `<Constant>` and `timestampValueDiscarded`
around a discarded literal.

**The undeclared-attribute half was deliberately NOT put in the checkers**, which is the one place
this build's own headline argues for more than was built. Catching it cheaply needs a copy of NI's
declared-attribute list per element, and **a list one entry short refuses a working document** —
strictly worse than the silence it replaces, and unnecessary when the real schema answers in 6 ms.
That is what `schemaHint` is for: it costs nothing, cannot be wrong, and points at the tool that
already knows.

**Two of the fixes were narrowed by their own test runs**, which is worth recording because both
looked right until something disagreed:

- The shared kind guard initially tolerated a JSON number where a string was wanted — `"value":12.5`
  reads naturally for a numeric field. But the reader below it is `GetValue<string>()`, which throws
  on a number node exactly as it does on an array, so tolerating the kind would have **moved** the
  raw exception rather than removed it. Refused by name instead, with the reason.
- It was also wired into `lvai_generate_test`'s `inputs` and `expect`, and **broke an existing
  test**: `Map()` already refuses a non-object there, and says "not an object of terminal name to
  value" where the generic guard says only "must be an OBJECT". The specific message is the better
  one, so the guard was narrowed to `label` and `Map` left alone. A shared check is worth having
  where there is a hole, not where it would replace a good message with a generic one.

The fifth item — warning when a wire-survival case's seed `value` matches a defaulted required
input's literal — was **not built**. It is the only one of the five whose failure mode is a weaker
assertion rather than a lost document or a raw exception, and §3c shows the tool already names both
the value and where it came from.
