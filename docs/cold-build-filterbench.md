# An eighth cold build — the first one to test a class METHOD rather than an accessor

Built from nothing on 2026-09-15, after Thermostat, DataLogger, AlarmGate, SampleBench, ValveRig,
PumpStand and KilnRig. All seven of those tested **accessors** — a round trip per field through the
LUnit scaffold — and not one of them tested what a class METHOD actually does. Two tools had
therefore never run in a cold build at all: `lvai_generate_method_test` and, with it, the whole
**Caraya** route. This build aimed at exactly that.

Artefacts under `C:\temp\FilterBench`. Final state: **4 `.lvclass`, 17 VIs, 8 tests (4 Caraya
method + 4 LUnit accessor), 0 failures**, a negative control in between.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `ISampleSource.lvclass` — `Next Sample.vi` (dynamic → `double`) | the contract |
| `Sample Buffer.lvclass` — implements it; `double.Sum`, `int32.Count` | the subject |
| `Add Sample.vi` (METHOD) — reads Sum, adds, writes Sum; reads Count, increments, writes Count | **four accessor calls and two primitives on one generated diagram** |
| `Average.vi` (METHOD) — Sum / Count, guarded to zero when Count is 0 | `Divide`, `Equal?` and `Select` in a generated method |
| `Next Sample.vi` (OVERRIDE) — delegates to `Average` | a method of a class calling another method of the same class |
| `Mock ISampleSource.lvclass` | `lvai_generate_mock_class` with `projectPath` |
| `Test Sample Buffer Methods.vi` + runner (Caraya) | **`lvai_generate_method_test`, first use in a build** |
| `Sample Buffer Test` (LUnit, 4 tests) | the accessor route, for comparison |

Every terminal name came from ONE batched `lvai_aixml_reference` call — `Add`, `Increment`,
`Divide`, `Select`, `Equal?`, `To Double Precision Float` — including `Select`'s output `s? t\3Af`,
whose colon is escaped. Five hand-authored documents, all `[clean]` before the first LabVIEW call.

## 2. A CLASS WAS `eBad` WHILE EVERY OTHER CHECK SAID GREEN, and one tool saw it

After `Add Sample.vi` was generated, made a class member and swapped onto its four accessors, the
answers read:

```
lvai_add_class_method : ok true, terminalsRetyped 2, verifiedOnDisk true
lvai_swap_subvis      : ok true, nodesSwapped 4, socketsLeft 0,
                        callTargets = the four real accessors
lvai_exec_state       : execState 0 — eBad, "VI has an error of type 8"
```

**The cause was not in that VI at all.** `Sample Buffer` implements `ISampleSource`, whose
`Next Sample.vi` had not yet been overridden — and a missing override breaks **every member of the
class**, not the method you happen to be looking at. `CLAUDE.md` states that rule (measured
2026-08-31, `Error 1003` with the require-override flag both set and cleared); what this build adds
is the concrete instance and, more usefully, **which check sees it**:

| check | verdict on the broken class |
|---|---|
| `lvai_add_class_method`'s own verify (pylabview, the saved file) | green — the terminals really are class-typed |
| `lvai_swap_subvis`' verify (LabVIEW's own export) | green — the call targets really are right |
| the AIXML lint | green — the document was always fine |
| **`lvai_exec_state`** | **`eBad`** |

The A/B is clean: the same VI answered `execState 0` before the override existed and `execState 1`
immediately after it landed, with nothing else changed. So **the rule to take from it is an ORDERING
one** — an interface's overrides belong in the same stretch of work as the class's other methods,
not after them — and a practical one: after adding methods to a class that implements an interface,
`lvai_exec_state` is the only cheap thing that will tell you the class is broken.

## 3. `lvai_generate_method_test` WORKS, and all three case shapes were used

Four cases over three shapes, generated in one call, every method called as an ordinary static
subVI:

| case | shape | what it pins |
|---|---|---|
| an empty buffer averages **0** | `expectOutput` + `expectValue` | the zero guard — a DESIGN decision, not observed behaviour |
| `Next Sample` on an empty buffer reports **0** | `expectOutput` + `expectValue` | that the override really delegates to `Average` |
| `Average` leaves `Sum` alone | `writeField` + `value` | that a getter does not mutate |
| `Add Sample` completes | `expectErrorCode` | no error on the normal path |

`callTargets` confirms the shape — `Caraya.lvlib\3ATest.lvclass\3ADefine Test.vi`,
`Assert Equal Value_Variant.vi`, and `Sample Buffer.lvclass\3A{Average,Next Sample,Add Sample,Read
Sum,Write Sum}.vi`. **The first two expectations are the design I chose**, so they pin the design;
the third is a property any getter must have. Nothing here was read off a run and then asserted.

## 4. A CARAYA FAILURE NAMES THE CASE AND NOTHING ELSE — unlike LUnit

The negative control flipped the empty-buffer expectation to `1`. The report:

```xml
<testsuite … tests="4" failures="1">
    <testcase name="NEGATIVE CONTROL - an empty buffer does not average one (Average)">
        <failure message="test failure">"FAIL"</failure>
    </testcase>
```

and the runner's `error out` answered **7002**, which is the documented "a suite failed" signal
rather than a fault — `0` again once the expectation was restored.

**The failure body is the literal string `"FAIL"`.** LUnit's `Pass If Equal.vim` writes
`Expected:bisque firing 12(String) / Actual:  (String)` into the same place, which is why
`lvai_run_lunit_tests`' description can promise "there is nothing to look up". For Caraya there
IS something to look up: the report names WHICH case failed and nothing about why. That is not a
defect in the tool — it is a property of the framework's own report — but it is a real difference
between the two routes and it belongs beside the choice of framework: **a Caraya failure costs a
diagnosis that the same failure under LUnit does not.**

## 5. Verification

- **8 tests, 0 failures** — 4 Caraya method cases, 4 LUnit accessor cases (`foundNoTests: false`).
- **Negative control** on the Caraya side: exactly one named failure, `failures="1"`, `error out`
  7002. Restored, `failures="0"` and `error out` 0.
- **Every method no suite runs** answered `execState 1, eIdle`: the interface's base contract, all
  three `Sample Buffer` methods, and the mock's own override.
- After the final close the `.lvproj` lists all four classes plus LUnit's `Test Case.lvclass` and
  both Caraya VIs, with **no stray helper items**.

## 6. What this build did NOT close

**The mock is still only generated, never used.** Eight builds have now produced an LMock mock and
not one has injected it into a test — `When Next Sample.vi` has never been called. That is the
obvious next build, and it is a real piece of ground rather than a formality: the mock's own VIs are
project-local class code, so a generated test reaches them through the placeholder-and-swap route
like anything else, and `lvai_generate_method_test` knows nothing about mocks. Saying so plainly
rather than counting the mock as covered.
