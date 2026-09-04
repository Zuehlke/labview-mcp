# Unit testing LabVIEW code that this server generated

Measured 2026-08-27 on LabVIEW 2026 (32-bit), Caraya 1.4.4.148, in `C:\temp\UnitTest\`. Everything
below was run end to end; where a route failed, the failure is recorded rather than the workaround
alone.

## 0. THE RULE: the test calls its subject as a STATIC SUBVI

**Always. Including class code. The VI Server variant is the fallback and needs a reason.**

| | route | section |
|---|---|---|
| **default** | the subject dropped on the test diagram as an ordinary subVI | §3a for plain VIs, **§3d for class accessors** |
| fallback | `Open VI Reference` → `Ctrl Val.Set` → `Run VI` → `Ctrl Val.Get` | §3b, §3c |

This is not a style preference. The VI Server version was built first for a class hierarchy on
2026-08-29 and the correction was explicit — *"Du musst die statischen VIs einsetzen bei den
tests!"*. What it costs:

- the diagram stops being LabVIEW code a developer can read — a path string and three Invoke Nodes
  per case instead of one icon;
- the assertion compares a **formatted string** rather than the field's real type, so the expected
  value has to be spelled the way OpenG happens to render it (`30` → `30.000000`);
- a renamed field or a moved folder breaks the test at **run time**, not at edit time — the static
  version simply refuses to compile, which is the whole point of a static link.

**Class code does not exempt you.** It looks like it does: AIXML refuses a class-typed terminal, so
no generated `Call` can name an accessor and `lvai_placeholder_subvi` answers `stubRefused`. The way
through is LabVIEW's own `{LV.SubVI}` `Replace`, which **re-types the wires** where a pylabview link
retarget cannot — §3d, measured over twelve properties of a three-class hierarchy with
`failures="0"` and a negative control.

Use the fallback only where the subject genuinely cannot be linked statically, and say in the report
which route you took and why the static one was not available.

## 0a. What a cold end-to-end run cost, measured 2026-08-29

The whole route — three classes, 24 accessors, five test VIs, 18 assertions — run against a freshly
started LabVIEW with `lvai_generate_class_test`. It works, and **two LabVIEW restarts were needed**,
both for reasons worth recognising rather than working around blindly.

| what | symptom | what it actually was |
|---|---|---|
| **`Error 1562`** at `AddVIToClass.vi`, first accessor slice | `membersAfter: 0`, nothing written, `classIndex` correct | *"The specified project or library is locked"* — the class LabVIEW holds after `lvai_create_class` is locked for editing. **A project close and re-open does NOT clear it**; only a restart did. The `.lvclass` on disk is not read-only and carries no lock property, so the file tells you nothing |

**`Error 1562` REPRODUCED IN A SECOND CONTEXT, 2026-09-01**, which promotes the row above from one
observation to two and narrows what it blames. Building an LUnit test case, `{LV.LVClassLibrary}`
`AddItemFromMemory` — a different method, on a class made minutes earlier by `lvai_create_class` —
answered 1562 with every earlier stage of the same helper reporting `0`. `Stop-Process LabVIEW` plus
`lvai_ensure_labview` plus reopening the project made the **identical call return all zeros**, and
adding a *second* member later in that same session also returned all zeros. So the lock is created
by **class creation**, not by adding members, and it survives everything short of a restart. The
working rule is therefore: **do not create a class and add member VIs to it in one LabVIEW session.**
Per `CLAUDE.md`, "only a restart fixes it" is a symptom rather than a diagnosis — the analogous fault
in `lvai_create_class` was a leaked refnum — so the leak is worth finding. It is not found yet.
Details in `docs/labview-lunit-testing.md` §5.
| **`Error 1051`** at the socket's `Save:Instrument` | validation passes, generation does not | the socket name is **poisoned by its own earlier failed validation** and stays so until LabVIEW restarts. A retry after fixing the authoring bug therefore still fails, on the same names, for a different reason |

Three authoring facts came out of the same run, each of which validated for one type and not another —
which is what makes them expensive to find:

- **`outputs` is required on a `Control` and a `Constant`**, even when nothing consumes the net.
  Omitting it answers `Error -2628 ... missing required attribute 'outputs'` with a line and column,
  which reads like malformed XML and is a missing attribute.
- **`type="double" value=""` is refused**; `type="string" value=""` is fine. The message is
  `Error 53 - Unrecognized or unsupported attribute set in Constant with UID 11`, naming the object
  and not the attribute. In one batch the three string-typed sockets generated and the two
  double-typed ones did not.
- **`type="bool" value="TRUE"` is accepted and silently becomes `false`** — the format wants lower
  case. Nothing reports it: validation passes, the run is green, and only LabVIEW's own export shows
  it. The round trip it produced wrote FALSE onto a default-FALSE object and **passed while testing
  nothing**. Found by the negative control, not by the run.

And one about the accessor budget: **one slice of two fields took 25 s on this station**, so a
`budgetSeconds` of 45 lets a second slice START at 25 s and finish past 50 — beyond the client's
patience, losing the answer to a call that had done the work. The budget is checked BETWEEN slices,
so it bounds how many are started, not how long one takes; set it below one slice's cost when the
class is large.

## 0b. Listing the suite in the project — two ways it went missing in silence

Measured 2026-08-29 on a five-suite run over a three-class hierarchy: every VI generated, every
assertion green, and the user's Project Explorer showed **one** test. The other five had to be
listed by hand. `lvai_generate_class_test` had answered `ok: true` each time.

Two independent defects, both now fixed in `ListInProjectAsync`, and both worth recognising because
the same shape can occur wherever a `.lvproj` is edited around a LabVIEW close:

| defect | what happened | fix |
|---|---|---|
| **written, then swept out again** | `alsoListInProject` named the suite runner before the runner had been generated. The entry was written, counted in `added`, and then removed by `StripHelperItems`' dangling pass — which deletes any self-closing item whose URL resolves to no file. Both halves are individually correct; together they lose a VI without a word | the tidy pass now runs **after** the add rather than before it, a path with no file on disk is **refused by name** and reported in `notOnDisk`, and the answer is **verified by re-reading the `.lvproj`** instead of trusting `added` |
| **clobbered by the next call's close** | each call closes the project first, and LabVIEW's close SAVES its own copy over the file, dropping VI items it never had in memory — so call *n+1* deleted call *n*'s entry. `AddClassToProject` has re-asserted class entries for exactly this reason since 2026-08-28; the test route never did | the VI items are read **before** the close and re-asserted after it, reported as `restored` |

The general rule this leaves: **`added` is what was written, not what survived.** Any step that edits
a `.lvproj` and then runs a tidy pass has to read the file back before it may claim success — the
count is computed too early to be evidence. `ok` on the `projectEntry` step now means *verified from
the file*, and `notOnDisk` / `notListed` name what did not make it.

### A third defect, found by the fix on its first live run

The cold rebuild that exercised the two fixes above turned up a third, and it is the reason
`notOnDisk` earns its place: the path it named was
`C:\Windows\system32\["C:\temp\NetzteilACDC\Run NetzteilACDC Tests.vi"]`.

`alsoListInProject` is a **newline-separated list of absolute paths**. The caller had sent it as a
**JSON array**, which arrives as one ordinary string — and nothing checked. `Path.GetFullPath`
treated the array literal as a *relative* path and resolved it against the **server's** working
directory, which on this station is `C:\Windows\system32`. The nonsense path was then written into
the user's `.lvproj` and swept out again by the tidy pass.

**A relative path is the same trap without the JSON**, and so is a quoted one — a leading `"` is an
ordinary path character, so `"C:\temp\x.vi"` is relative. There is no directory the tool could
sensibly resolve any of them against, so all three are now **refused by name before anything is
generated**, the way `pylv_apply` refuses a malformed operation before the extract: nothing has been
built yet, so a bad argument costs a message instead of a half-finished suite. `testViPath` gets the
same check, having had the identical flaw.

The lesson that generalises past this tool: **a path parameter that reaches `Path.GetFullPath`
without a rooted-ness check will silently accept anything at all**, because `GetFullPath` never
fails on a syntactically legal relative path — it invents an answer from the process's working
directory, and that directory belongs to the server rather than to the caller.

## 1. The blocker: a generated test cannot call its subject statically *by AIXML alone*

A unit test calls the VI under test. AIXML cannot express that call. Measured with one throwaway
`ValidateAIXML` carrying three spellings of the same target:

```
Unsupported SubVI: Celsius To Fahrenheit.vi
Unsupported SubVI: C:\temp\UnitTest\Celsius To Fahrenheit.vi
Unsupported SubVI: ..\Celsius To Fahrenheit.vi
```

`Error 53 … Manager call not supported`. Bare name, absolute path and relative path are refused
alike — consistent with `docs/aixml-gap-census.md` §57, and worth re-measuring here because it is
the single fact that decides how a generated test has to be built.

**So the generator route cannot produce a conventional Caraya test**, in which the test VI drops
the subject on the diagram as a subVI. Two things follow, and they are independent:

- the **test** needs another way to invoke its subject (§3);
- the **suite** does not, because Caraya's runner takes a path (§4).

## 2. Caraya is installed but invisible to `lvai_palette_index`

`vi.lib\addons\_JKI Toolkits\Caraya` and `…\VI Tester` are both present. Querying the index for
`Caraya` returns **no match**, and querying for `Assert` returns only NI's 16 `typeassert.mnu`
malleables.

The reason is where the palette files live: Caraya's `.mnu` files are in
`vi.lib\addons\_JKI Toolkits\dynamic_palette\`, while the index scans `LabVIEW 2026\menus` plus
`%ProgramFiles%\NI\LVAddons`. Neither covers the dynamic palette.

**A miss in the index is therefore not proof that a Call is illegal.** Every Caraya target below
validated and ran. The reliable spelling came from exporting NI's own example
(`Caraya\examples\tests\Test Addition.vi`) and copying its `target=` verbatim — the technique the
palette index's own answer recommends, and here the only one that works:

| what | target |
|---|---|
| define the test | `Caraya.lvlib\3ATest.lvclass\3ADefine Test.vi` |
| assert | `Caraya.lvlib\3AAssert.lvclass\3AAssert Equal Value_Variant.vi` |
| run tests by path | `Caraya.lvlib\3ARun Tests.vi`, `instance="Caraya.lvlib\3ARun Test (Scalar Path).vi"`, `adapt="true"` |

Note the nested `lvlib` + `lvclass` qualifier. `lvai_vi_terminals` prints the polymorphic wrapper's
target with a **literal colon** (`Caraya.lvlib:Run Tests.vi`) while escaping the instance; the
escaped form `Caraya.lvlib\3ARun Tests.vi` is what was validated and used.

## 3. Two ways for the test to reach its subject, and the static one is better

Both were built and both run. §3a is what a LabVIEW developer expects to see; §3b is what a
generator can produce unaided.

|  | static subVI call (§3a) | VI Server (§3b) |
|---|---|---|
| diagram per test case | **1 node** | 3 nodes plus two wires |
| readable as LabVIEW code | yes | no — a sprawl of `Ctrl Val.Set` / `Run VI` / `Ctrl Val.Get` |
| AIXML alone | **no** — needs a pylabview retarget | yes |
| subject's connector pane | **must match the placeholder's, forever** | irrelevant |
| regenerating the subject | breaks the test unless the pane is reproduced | free |

**Prefer the static call.** The VI Server version was written first and the diagram is the argument
against it: three Invoke Nodes and a threaded error wire per case, spread across the screen, where
the static version is one icon with a constant on the left and an assertion on the right.

## 3. One call: `lvai_generate_test`

The whole of §3a in one tool. Hand it the subject and a list of cases:

```json
[{"label":"boiling point","inputs":{"celsius":"100"},"expect":{"fahrenheit":"212"}}]
```

and it runs `lvai_placeholder_subvi`, authors the AIXML, generates, and retargets the call onto the
subject. Each sub-answer comes back whole under `steps`, so a failure reads the same as calling the
three by hand. `inputs` and `expect` are keyed by the subject's **own terminal names**, and each
value is written verbatim into a constant of that terminal's type.

Two limits worth knowing before writing cases:

- **every assertion is `Assert Equal Value_Variant`, and float equality is exact.** Caraya's
  `Assert Almost Equal_Float.vi` is not wired up. The three cases used throughout this document
  (100, 0, -40) are exact in IEEE754 after `C * 1.8 + 32`, checked before they were chosen.
- **read the JUnit report, not `error out`.** The VI's error cluster carries the first failed
  assertion only — §4.

**A retry under the same `testViPath` can fail with `Error 1051` even though the first attempt wrote
nothing.** Measured twice in a row 2026-08-27: a failed *validation* leaves a phantom under the
document's `_name`, and this tool derives that `_name` from the test file name, so the next attempt
at the same path is refused with "a LabVIEW file **of that name** already exists in memory". The
first attempt's error is the one to fix; then generate under a **fresh** name, because the old one
stays poisoned until LabVIEW restarts. Neither closing the active project nor evicting the VI
through a throwaway project cleared it — both were tried, and `Application:All VIs In Memory`
listed nothing of that name in either application instance.

The sections below are what it does and why. Worth reading when it reports something unexpected,
and when the subject needs something the tool does not cover.

## 3a. Static subVI call, via the slot pattern

AIXML cannot author the call (§1), so the test is generated against a **placeholder** — a node the
generator IS allowed to create — and the link is then repointed at the subject with pylabview. The
placeholder is never executed; it exists only so that there is something to repoint.

### Generate the placeholder, do not hunt for one

**`lvai_placeholder_subvi` does all of this in one call.** Hand it the subject; it exports the
subject's pane, hashes the signature, generates the stub into `user.lib\LV_MCP\` only if that
signature has none yet, and answers with the bare name, a ready-to-paste `Call` element carrying
the subject's own terminal names, and the `retarget` operation to hand `pylv_apply`. Verified end
to end 2026-08-27: first call `installed: true`, second `reused: true` with nothing written, and
the resulting test ran `tests="3" failures="0"`. The rest of this section is what it does and why,
which is worth knowing when it reports something unexpected.

**Put a generated stub in `user.lib` and call it by its bare name.** Measured 2026-08-27: a loose VI
in a plain folder under a LabVIEW symbolic root resolves as a `Call` target with **no `.lvlib`, no
`.mnu`, no palette entry and no LabVIEW restart** — `errorCode 0`, checked with a file name that
existed nowhere else on the machine. See §9 of `lvai_aixml_reference` for the whole resolution rule;
it is not the one this repository believed.

The stub is a **pane clone of the subject**: same terminal count, same names, same types, same
`conIdx`. All of that is readable from the subject with `lvai_vi_terminals` and
`lvai_connector_pane`, so it can be generated on demand and cached — create it only if
`user.lib\LV_MCP\` has no stub for that signature yet.

Cloning the pane is not optional, and a *generic* stub does not work. Measured on a controlled pair,
identical in every respect but the terminal type:

| stub terminal type | after retargeting onto the `double` subject |
|---|---|
| `Variant` | **`Error 7 — Bad Linkage`** |
| `double` | `tests="3" failures="0"` |

The connector pane's **type descriptor** is part of the link binding, not just the terminal
positions. So one placeholder cannot serve every signature — which is why generating one per
signature beats installing a catalogue of shapes.

Two things fall out of the stub being ours rather than NI's, and both were real costs before:

- **The pane pattern matches for free.** Stub and subject are both generated on the same station, so
  both get its `DefaultConPane` (4833 here). Nothing has to be repaired.
- **Regenerating the subject no longer breaks the test.** With a borrowed placeholder it did, every
  time — see "The maintenance cost" below, kept because the symptom is worth recognising.

Two blind alleys, so they are not walked again:

- **A hand-written `.lvlib` does not make a generated VI a library member.** `Unsupported SubVI`,
  and still so with the library loaded in an open project. A real member carries `LIvi`/`LIbd`
  ownership blocks that only LabVIEW writes when it saves the library.
- **The dynamic palette needs `.mnu` files**, which are binary LabVIEW resources and cannot be
  authored from nothing. Neither route is needed: loose in a plain folder already resolves.

### The placeholder cannot bridge a CLASS, and that is the one gap worth knowing up front

**The stub is itself generated through AIXML, so it inherits every AIXML refusal.** Measured
2026-08-28 against `Read Name.vi`, an accessor of `Haus.lvclass`:

```
Error 53 occurred at LV AI Core.lvlibp:VI generator.vi
  Control with type=UDClassInst is not supported
  Indicator with type=UDClassInst is not supported
```

`lvai_placeholder_subvi` reports this as `errorKind: stubRefused` with the validate answer attached,
so the cause is readable rather than mysterious — but there is no way around it. A dynamic dispatch
accessor carries the class **in and out**, so both halves fail; a static one still has the class
input. **Every class member VI has a class terminal on its pane**, so:

- no generated VI can call an accessor, a constructor or a method, and
- the slot pattern does not lift it either — the plug swapped into the socket would need that same
  pane.

So the **static** route reaches ordinary VIs and **not** class code, until AIXML accepts
`UDClassInst`. This is why `labview-class-generator`'s toolset omits `lvai_placeholder_subvi`: it
would look like an escape and is not one. `docs/lvclass-creation.md` §3 has the same limit from the
other side, including the donor route that is blocked for a different reason.

**This paragraph used to end "generated unit tests reach ordinary VIs and not class code", full
stop, and that is too strong — §3c is a measured counter-example.** The refusal is about a
class-typed *terminal*. Nothing stops a generated VI from driving an accessor through **VI Server**,
where the class object crosses as a Variant and never becomes a wire.

### 3c. A generated test CAN reach class code — the VI Server route (SUPERSEDED by §3d)

**Read §3d first.** This section was written earlier the same day and presented the VI Server helper
as *the* way to test class code. It works and it is measured, but it is the **fallback**: the test
never calls the accessor, it drives it by path, so the diagram is not what a LabVIEW developer
expects and a renamed field breaks it silently at run time rather than at edit time. §3d does the
thing properly — the accessors as ordinary static subVI calls — and is also measured end to end.
Keep this section for the three probe results in the table below, which §3d relies on too.

Measured 2026-08-29 on a three-class hierarchy in `C:\temp\NetzteilACDC` (`Netzteil` with 6 fields,
`ACNetzteil` and `DCNetzteil` with 3 more each, 24 dynamic dispatch accessors). 24 Caraya assertions
over 12 properties, `failures="0"`, green on two consecutive runs.

Three facts make it work, and each was probed before anything was built:

| probe | result |
|---|---|
| Can a **dynamic dispatch** accessor be run top-level by `Run VI`? | **yes** — `errorCode 0` |
| Does `Ctrl Val.Get` return a class-typed indicator? | **yes** — a full `<Object>` with `<Class>Netzteil.lvclass</Class>` and every field |
| Does `Ctrl Val.Set` accept that Variant on the next accessor's class input? | **yes** — the object round-trips |

So the shape is a **non-class helper VI** whose own pane is plain strings and numbers:

```
Open VI Reference (Write <field>.vi)
  Ctrl Val.Get  <field>          -> the control's default, i.e. the field's TYPE
  Scan the value string into that type
  Ctrl Val.Set  <field>
  Run VI
  Ctrl Val.Get  "<class> out"    -> the object, as a Variant
Close Reference
Open VI Reference (Read <field>.vi)
  Ctrl Val.Set  "<class> in"     <- that same Variant
  Run VI
  Ctrl Val.Get  <field>          -> what came back
Close Reference
```

Because that helper has **no class terminal**, `lvai_placeholder_subvi` clones its pane happily
(`typedefTerminals: 0`) and `lvai_generate_test` produces an ordinary static-call Caraya test
against it. The class stays entirely inside the helper. Working source:
`C:\temp\NetzteilACDC\aixml\Netzteil Roundtrip.xml`.

Two things the helper gets for free, both worth having:

- **the field's type never has to be declared.** `Ctrl Val.Get` on the Write accessor's own data
  control *before* running returns its default, which carries the type; the value then crosses the
  test's pane as a string and is scanned into it. One helper serves `string`, `double`, `int32` and
  `bool` alike.
- **which class actually dispatched is observable.** `Flatten To XML` on the object Variant and pull
  `<Class>` out with two `Match Pattern`s. An `ACNetzteil` accessor answers `ACNetzteil.lvclass`, so
  a test can assert that a child accessor really produced a child object — which no file-level check
  can show.

**`Scan Variant from String__ogtk.vi` TRUNCATES A STRING AT THE FIRST WHITESPACE**, and it does so
with `errorCode 0`. Measured: `PS 3010 DF` came back `PS`. It scans `%s`. The fix is not a Case
structure — flatten the type Variant, `Match Pattern` for `<String>`, and `Select` between the scan
and a plain `To Variant`, because neither branch has a side effect. With that, `PS 3010 DF` survives
and the numeric path is unchanged.

**Read-back formatting, measured**, so an expected value can be written by hand rather than copied
out of a run: `string` verbatim, `double` `%f` with six decimals (`30` → `30.000000`), `int32` plain
(`3`), `bool` `TRUE`/`FALSE`.

**An all-green first run proves nothing here either.** A throwaway case expecting
`THIS SHOULD NOT MATCH` was generated and run: `ASSERTATION FAILED`, from
`Caraya.lvlib:Assert.lvclass:Assert_Core.vi`. Do that once before believing a green suite.

**The `VICD` rule from `CLAUDE.md` bites this route hard, and the symptom names the wrong thing.**
The first of the three test VIs was generated while the user's project was still open from the
accessor phase, so LabVIEW had compiled it and its pylabview bundle carried `VICD0`/`VICD1` where the
other two carried none. It retargeted, `verify` reported the right `callTargets` — and the suite then
died with `Error 7, Bad Linkage`, naming that VI, writing no report at all. Regenerating the same VI
with **no project active** produced an 11-file bundle with no `VICD` and a green run. So: generate
tests *after* the project is closed, and read the extract step's file list — `VICD` in it is the
warning.

**Caraya's own test manager can fail once after the test VIs are re-saved.** After
`lvai_set_vi_icon` re-saved all five VIs, the next suite run returned `Error 1` at
`Generate User Event` in `Caraya.lvlib:Basic Test Manager.lvclass:Send Test Event.vi` and wrote no
report. The run after that was green, and so was the one after it. It is a stale refnum in Caraya,
not a failing test — but a CI job that runs the suite exactly once after a rebuild would report it
as a failure.

### 3d. THE STATIC ROUTE: class accessors as ordinary subVI calls, via `{LV.SubVI}` `Replace`

Measured 2026-08-29 in `C:\temp\NetzteilACDC`. Four Caraya test VIs, twelve properties over three
classes, every accessor a **static subVI call on the test diagram**, `failures="0"` on two
consecutive runs, and a negative control that fails on demand.

**The blocker is narrower than the documents said.** Three spellings of a direct `Call` to a class
accessor are refused even with the owning project open —

```
Unsupported SubVI: Write Hersteller.vi
Unsupported SubVI: Netzteil.lvclass:Write Hersteller.vi
Unsupported SubVI: C:\temp\NetzteilACDC\Write Hersteller.vi
```

— and `lvai_placeholder_subvi` still answers `stubRefused` / `UDClassInst`. What is NOT blocked is
LabVIEW's own **"Replace with a VI from disk"**, exposed as `{LV.SubVI}` → `Replace (Style; Path;
PaletteString)`. Unlike a pylabview link retarget, which only rewrites the link record and therefore
demands two type-identical panes, `Replace` **re-types the wires**. So a socket whose class terminals
are stand-ins can be swapped for the real accessor and the wires survive.

The recipe, all four parts measured:

1. **Author a socket VI per node**, on the accessor's own pane pattern (`4815` here — measure it,
   do not assume). Class terminals become **`path`**, the data terminal becomes **`variant`**.
   `path` because no private data field is a path, so the class-source constant is findable by class
   name; `variant` because any constant coerces into it, so one socket serves `string`, `double`,
   `int32` and `bool` alike.
2. **Author the test against those sockets** with ordinary AIXML — constants, Caraya's `Define Test`
   and `Assert Equal Value_Variant`, and the socket chain write → read.
3. **Swap the nodes**: `{LV.SubVI}` `Replace` with the accessor's path.
4. **Swap the class source LAST**: `{LV.Constant}` `Replace` on the path constant with the
   `.lvclass` path.

LabVIEW's own export of the result:

```xml
<Call target="Netzteil.lvclass\3AWrite Hersteller.vi"
      inputs="Netzteil in:,Hersteller:20.value,error in (no error):"
      outputs="Netzteil out:88.Netzteil out,error out:"/>
<Call target="Netzteil.lvclass\3ARead Hersteller.vi"
      inputs="Netzteil in:88.Netzteil out,error in (no error):" .../>
```

**Five traps, each of which cost a run:**

- **A DYNAMIC DISPATCH INPUT IS A REQUIRED TERMINAL.** Leaving the first accessor's class input
  unwired gives `Error 1003, VI is not executable` at `Run VI` — and nothing before that says so:
  the file generates, the Replace succeeds, the export looks right. That is the whole reason for the
  path constant in step 4.
- **Order matters.** Nodes first, constant last. The other way round the wire has a class source and
  a path sink and breaks.
- **The node reference does NOT survive `Replace`.** Reading `VI Name` off the Invoke Node's
  `reference out` — or off its `Replace` output — answers `Error 1055, object reference invalid`,
  and because that error travels down the wire it also **stops `Save.Instrument`**, so the edit is
  silently not written. Read nothing back; verify with an AIXML export.
- **`{LV.Diagram}` `SubVIs[]` re-orders after every `Replace`**, and the old references die. A helper
  that swaps several nodes must re-read the array on every iteration, and each socket must have a
  **unique VI name** — two nodes calling the same socket cannot be told apart, and the wrong accessor
  lands in the wrong test case with no error at all.
- **A half-applied `Replace` leaves the in-memory VI unusable**: after the `1055` run, `VI Name`
  came back empty for *both* nodes, including the one never touched. The file on disk was untouched;
  generate under a fresh name and start over.

What this buys over §3c: the test reads as LabVIEW code, the assertion compares the field's **real
type** rather than a formatted string, and a renamed field breaks the test at edit time.

Working sources under `C:\temp\NetzteilACDC\aixml\`: `stub-W-template.xml` / `stub-R-template.xml`
for the sockets, `lvmcp_replace_subvis_by_name.xml` and `lvmcp_replace_path_constants.xml` for the
two swap helpers, `lvmcp_list_subvis.xml` / `lvmcp_list_objects.xml` for reading the orders back.

**Write those AIXML files with a file tool.** Generating them from a Python heredoc turned every
`\2C` into an octal escape — `chr(2)` followed by `C` — and `ValidateAIXML` reported it as
`Error -2628, an error occurred while parsing the document`, which reads like malformed XML. That is
exactly the trap `CLAUDE.md` §6 documents, met in the wild.

**And the list arguments cannot be newline-separated.** `lvai_run_vi_and_read_values` rejects a
newline inside a value, because its own helper separates name/value pairs that way. Use another
delimiter — `|` is safe, being illegal in a Windows path.

### The original route, for the record

The first working version borrowed `NI_Gmath.lvlib:Error Function.vi` from the palette — `x`
[double] at conIdx 3, `erf(x)` [double] at conIdx 1, pattern 4805 — because it is the smallest
palette VI with one double in and one double out, and then **shaped the subject to fit it**
(`conIdx` 3 and 1 in the AIXML, then `pylv_apply {"op":"conpane","pattern":4805}`). It works, and it
is what the measurements below were taken on. It is superseded because it needs a lucky palette hit
per signature and it makes every regeneration of the subject a two-step operation.

### The retarget, in one call

```json
[{"op":"retarget","from":"LVMCP Stub 527c392b89.vi","to":"Celsius To Fahrenheit.vi",
  "path":"C:\\temp\\UnitTest\\Celsius To Fahrenheit.vi"}]
```

`lvai_placeholder_subvi` returns exactly this under `retarget`, so it does not have to be written
by hand. The stub name is a hash of the signature — here `i:celsius:double:0|o:fahrenheit:double:4`
— which is what makes two subjects with the same pane share one stub.

`pylv_apply` closes the project, extracts, retargets, rebuilds and AIXML-exports the result in
**4.7 s**, and the `verify` step's `callTargets` name `Celsius To Fahrenheit.vi`.

**This took ten calls of hand surgery the first time**, and it is worth recording why, because the
symptom pointed at the wrong thing. `pylv-retarget-subvi.py --list` reported

```
  NI_Gmath.lvlib   (2 references)
  Caraya.lvlib   (4 references)
```

— no VI names at all. `LinkSaveQualName` is **itself a segment list**; for a library-owned VI it
reads `NI_Gmath.lvlib` / `Error Function.vi`, and the script captured only the first `<String>`.
Passing `Error Function.vi` as `from` was then rejected as "not a subVI link in this bundle", which
reads as though the VI were not called at all.

A library-owned link record also carries a third element the plain case has not:

```xml
<VILSPathRef Ident="PTH0" TpVal="0">
  <String>&lt;vilib&gt;</String><String>gmath</String><String>NI_Gmath.lvlib</String>
  </VILSPathRef>
```

the path of the **owning library**. Retargeting onto a project-local VI that belongs to no library
has to dispose of it, and the script now replaces it with the empty `ZeroFill` form — **scoped to
the one record**, because two subVIs of the same library each carry their own and a global edit
strips the library from the record that was not retargeted. Caraya's `Define Test.vi` and
`Assert Equal Value_Variant.vi` in this very bundle are that pair.

What the tool rewrites, per matched record:

| element | from | to |
|---|---|---|
| `LinkSaveQualName` | `NI_Gmath.lvlib` / `Error Function.vi` | `Celsius To Fahrenheit.vi` (one segment) |
| `LinkSavePathRef` | `<vilib>` / `gmath` / `SpecialFunctions.llb` / `Error Function.vi` | `C:` / `temp` / `UnitTest` / `Celsius To Fahrenheit.vi` |
| `VILSPathRef` | the `NI_Gmath.lvlib` path above | `<VILSPathRef Ident="PTH0" TpVal="0" ZeroFill="True" />` |
| `_BDHb.xml` `<text>` | `"Error Function.vi"` ×3 | `"Celsius To Fahrenheit.vi"` ×3 |

`TpVal="0"` is kept for the absolute path — that is what `--path` has always produced. The
`ZeroFill` form for the now-absent library was taken from the empty `LinkSavePathRef` shape the
script's own comments document; it is a **reasoned guess that LabVIEW accepted**, not something
measured from a reference file.

Verified the only way that counts, by LabVIEW's own export of the rebuilt VI:

```xml
<Call inputs="celsius:110.value" outputs="fahrenheit:210.fahrenheit" target="Celsius To Fahrenheit.vi" …/>
```

The wires rebound to the subject's own terminal names. `StdViGUID` in the `VIVI` record was left
holding the placeholder's GUID and LabVIEW did not object.

### The maintenance cost — of the BORROWED placeholder only

This is what the generated stub removes. It is kept because the **symptom is worth recognising**:
any pane mismatch between caller and subject, however it arises, surfaces exactly like this.

**Regenerating the subject breaks the test, and the error does not say why.** After
`lvai_generate_vi` rewrote `Celsius To Fahrenheit.vi`, Caraya answered

```
7101  At least one test is not in a executable state.
ASSERTATION FAILED: VI not in an executable state (C:\temp\UnitTest\Test Celsius To Fahrenheit.vi)
```

The cause is not stale memory — closing the project changed nothing. It is the pane: generation had
stamped 4833 again, so the terminals moved and the caller's wires dangled. Re-applying
`{"op":"conpane","pattern":4805}` fixed it with no other change. With a *borrowed* placeholder,
every regeneration of the subject needs that repair repeated — and until it is, the failure reads
as a broken test rather than a mismatched pane. With a generated stub the question never arises,
because both sides carry the station default.

The same message is what a wrong-typed placeholder produces at a different moment: `Error 7 — Bad
Linkage` if LabVIEW rejects the link outright, `7101 not in an executable state` if it accepts the
link and the diagram will not compile. Neither mentions the pane.

## 3b. VI Server invocation

Since a static Call is impossible, the test opens a reference to the subject and drives it. All of
this is ordinary AIXML — the shapes were copied from the shipped helper
`scripts/lvai_run_and_read.xml`:

```
String To Path -> Open VI Reference
  Invoke Node {LV.VI} "Ctrl Val.Set"  (Control Name, Value)
  Invoke Node {LV.VI} "Run VI"        (Wait until done = true)
  Invoke Node {LV.VI} "Ctrl Val.Get"  (Control Name) -> Value
  Call Assert Equal Value_Variant     (Expected, Actual = that Value, Label)
Close Reference
```

`Ctrl Val.Get` is **not in `docs/vi-server-methods.tsv`** — the catalogue lists only the display
name `Get Control Value [Variant]` and its scripting sibling `Ctrl Val.Get All`. The scripting name
`Ctrl Val.Get` with output terminal `Value` was settled by a throwaway `ValidateAIXML`
(`errorCode 0`), the same way `lvai_close_active_project`'s methods were found.

Two side effects of this route that are worth having:

- the test is **not statically linked** to its subject, so regenerating the subject does not hit
  `Error 1357`. Measured: the subject was overwritten twice between test runs with no close and no
  project handling at all.
- the subject's connector pane does not constrain the test, so the subject can be regenerated at
  will — the one thing the static route of §3a cannot survive.

### The trap: do not chain the subject's invocation on the assertion's error wire

First version wired each case's `Ctrl Val.Set` from the previous case's **assert** `error out`. With
a deliberately broken subject the run reported one failure and looked correct. It was not: an
`Invoke Node` does not execute with an incoming error, so **cases 2 and 3 never ran** and their
absence was invisible — the error cluster reports the first error only, so a partial run and a
single failure are indistinguishable.

The fix is to give the VI Server chain its own clean error path (Set → Run → Get → next Set), hang
each assertion off its own `Ctrl Val.Get`, and `Merge Errors` the assertions at the end. Data
dependency on each `Value` still orders every assertion after its own case. Re-measured with the
same broken subject: **3 tests, 2 failures**, which is the arithmetically correct answer.

**An all-green first run proves very little.** This defect was only visible because the subject was
deliberately broken and the expected failure count was known in advance.

## 4. Running the tests and getting a report

`Caraya.lvlib:Run Tests.vi` / instance `Run Test (Scalar Path)` takes the test as a **path** and
loads it dynamically, so the harness VI needs no static link either — `Error 53` never arises at
this level. Wire `Interactive (T)` to **false**: true opens a modal report dialog, and a modal
dialog stops LabVIEW's whole gRPC service until a human dismisses it.

**The report format is chosen by the file extension of `Report Path`, and `.txt` writes nothing.**
`AutoSelect Test Report.vi` switches on the lowercased extension with cases `""`, `"xml"` and
`Default`. `.txt` falls into `Default` → `Create DefaultReport.vi`, and **no file appeared** —
measured twice, with `error out` reporting `7002 Caraya Test Manager: Test Suite failed`, i.e. the
run itself had completed. `.xml` selects the JUnit writer and produces the file immediately:

```xml
<testsuite name="Test Celsius To Fahrenheit" errors="0" skipped="0" tests="3" failures="2" …>
    <testcase classname="…" name="boiling point - 100 C should be 212 F">
        <failure message="{Expected value: 212.000000, Asserted value: 192.000000}">"FAIL"</failure>
    </testcase>
```

Use `.xml`. It is also what CI wants, and it is the only output that carries **every** assertion —
the error cluster carries one, because `Merge Errors` keeps the first.

`7002` is the pass/fail signal, not a fault: `resource\errors\Caraya-errors.txt` defines it as
`Caraya Test Manager: Test Suite failed`. A green run returns `errorCode 0`.

### 4a. The runner is generated, not hand-authored — `lvai_generate_caraya_test_runner`

A suite of several tests uses the **`Run Test (Array Path)`** instance instead: one call, an array
of paths, one report. That runner's shape never varies — `Current VI's Path` → `Strip Path` → one
`Build Path` per test → `Build Array` → the call, with the report path built the same way — and
only the file names differ between runs.

**Which is why hand-authoring it was the single most expensive step of a test build.** Measured
2026-08-30 on a cold three-class, five-suite run: writing, generating and debugging the runner cost
**186 s of wall clock against 6.1 s inside LabVIEW**, out of 920 s for the whole build. Nearly all
of it was the model re-deriving AIXML it had produced twice before. The tool does it in one call.

Two rules it enforces, both learned the expensive way:

- **Paths are relative to the runner's own location**, never absolute. That is what lets the folder
  be copied or renamed with the suite still working. A test VI that does not live under the runner's
  folder is refused by name rather than written as an absolute constant — that failure would arrive
  at run time as `Error 7`, with nothing in the report to say which path was wrong.
- **`Interactive (T)` is FALSE and stays false**, for the modal-dialog reason above.

The pane is not measured: a runner has no inputs and its two indicators are read from the front
panel, so there is no connector pane to get wrong.

**Why only this one carries the framework in its name.** `lvai_generate_test` and
`lvai_generate_class_test` are just as Caraya-specific — both hard-wire
`Caraya.lvlib\3ATest.lvclass\3ADefine Test.vi` and `Assert Equal Value_Variant.vi` — and they are
*not* called `lvai_generate_caraya_*`. That is a decision, taken 2026-08-30, not an oversight: those
two are released, and an MCP tool name is quoted literally in agent frontmatter `tools:` lists and in
users' permission allow-lists, where a rename fails **silently** — the agent registers and simply
cannot call the tool, which is the same failure the plugin tool-prefix drift produced that morning.
The runner was unreleased, so it cost nothing to name properly. Rename the other two only as a
deliberate, announced change.

### 4b. The FIRST run after a rebuild answers `Error 1` and writes no report

Measured 2026-09-04 on a four-suite run: immediately after the test VIs were rebuilt, the runner
returned `Error 1` at `Generate User Event` inside
`Caraya.lvlib:Basic Test Manager.lvclass:Send Test Event.vi` and produced no report file. Running it
a second time, changing nothing, was clean — four suites, `failures="0"`.

`Error 1` from `Generate User Event` is an invalid refnum: Caraya's test manager is holding a user
event that belonged to the copies of the test VIs LabVIEW had in memory before the rebuild replaced
them on disk.

Two practical consequences:

- **A run-once-after-rebuild CI job reports a failure that is not one.** Either run the suite twice
  and take the second, or close the project between the rebuild and the run.
- **Do not diagnose it as a broken test.** It names Caraya's own VI, not yours, and no report is
  written at all — which looks like the suite never executed, because it did not.

Related, same cause, and the reason a green suite should not be tidied afterwards: `lvai_set_vi_icon`
re-saves every VI it touches, which re-arms this on the next run.

## 5. Why Caraya rather than JKI VI Tester

Both ship in `vi.lib\addons\_JKI Toolkits`. A Caraya test is a plain VI calling library VIs, which
is exactly what the generator can author. A VI Tester test case is a `.lvclass` inheriting from
`TestCase.lvclass` with dynamic-dispatch overrides; `lvai_create_class` exists, but generating
dynamic-dispatch override VIs is unmeasured. VI Tester was not tried.

## 6. What the spike did not settle

- **Float comparison.** `Assert Equal Value_Variant` is exact equality. The three cases used here
  (100, 0, -40) are exact in IEEE754 after `C * 1.8 + 32`, checked before choosing them. Real code
  wants `Assert Almost Equal_Float.vi`; it was not exercised.
- **Discovery by folder.** The harness names one test VI. `Run Test (Scalar Path)` accepts a folder
  with `Inspect Recursively`, which is the realistic CI shape, and was not measured.
- **Inputs other than one scalar.** `Ctrl Val.Set` takes a Variant, so a cluster or array subject
  should work, but only a `double` was measured.
- **Placeholders beyond one double in, one double out.** The route is settled (§3a: generate a pane
  clone into `user.lib`), but only the 1×1 `double` case has been generated and run. Two inputs, a
  cluster, an array and an error cluster are untried, and the stub generator that would produce
  them on demand is not built yet.
- **Whether `VILSPathRef` `ZeroFill` is the right empty form** (§3a). LabVIEW accepted it; no
  reference file was found that stores a library-less call in a parsed block, so it rests on one
  working measurement.
- **`{LV.SubVI}` → `Replace`** (`Style; Path; PaletteString`, `docs/vi-server-methods.tsv`). This is
  LabVIEW's own "Replace with…", so it would handle library ownership and relinking without any
  heap surgery, and would make §3a a supported operation rather than a hand edit. Not tried.
