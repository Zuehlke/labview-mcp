# Cold build: the ATM with agents, third run, 2026-09-25

The CLD ATM exam (100928D-01) built cold a third time by agents, into a fresh directory, with the
time measured per phase. Deliverable, timeline and timing: `C:\temp\ATM_Agents_3\` (`DESIGN.md`,
`TIMELINE.txt`, `TIMING.md`). The two runs before it are `docs/cold-build-atm-no-stubs.md` and
`docs/cold-build-atm-agents-pc.md`.

## 1. Result

13 VIs and a class with 3 members, all executable, all with icons, panes per the style guide.
Caraya: 11 suites, **108 tests, 0 failures**; the negative controls in waves 2 and 4 each failed
exactly the broken case. `user.lib\LV_MCP` held 434 stubs before and after: no placeholder, no
node swap, no `pylv_*` call.

| phase | wall |
|---|---|
| P0 template, `ATM.lvproj`, `DESIGN.md` | 0:43 |
| wave 1, four agents in parallel (file layer, presentation, panel helpers, class) | 5:35 |
| integration, 10 VIs listed with `lvai_add_vis_to_project` | 0:18 |
| wave 2, the method `Apply Transaction` and its suite | 9:38 |
| wave 3a, `Handle ATM Action` (and a failed `ATM Main`) | 16:51 |
| wave 3b, `ATM Main` by the orchestrator | ~2:30 |
| wave 4, Caraya suites and the top-level runner | 10:32 |

About **48 minutes of working time**, not counting a pause while waiting for the user. The
application was complete and executable after about 38 minutes.

## 2. What cost time, and what each fix is

| # | finding | fix |
|---|---|---|
| 1 | `lvai_generate_test` left its expected constants UNLABELLED, so `lvai_set_constant` could not break one for a negative control; and it could not call anything before the subject, so every write-then-read case was hand-written AIXML | every expectation is labelled `expected <n>` and listed in `expectedConstants`; a case takes `setup` - VIs called first, chained into the subject by the error wire (§3) |
| 2 | `lvai_run_vi_and_read_values` could not set an enum, array or cluster input; agents built harness VIs | the typed helper has Enum, Ring, Array and Cluster frames (§4) |
| 3 | `lvai_open_file` opened one VI per call - five round trips to load the callees in wave 2 | `viPaths`, one path per line, all through the same project pair (§5) |
| 4 | `Error 53` from `lvai_generate_vi_with_events` named no target; the retry answered `1051` and the agent concluded the name was burned until a LabVIEW restart | the convert runs under a throwaway name, and a 53 is followed by a validate that lists every `Unsupported SubVI` (§6) |
| 5 | `labview-vi-generator` had no `lvai_add_vis_to_project`, so the orchestrator moved its entries | on the roster of the generator, the editor and the Caraya agent, with the phase that uses it |

**Finding 4 cost the most by far - about 47 minutes of waiting** - and the diagnosis was wrong in a
way worth remembering: the name was NOT burned. After a client restart, with LabVIEW's uptime
unchanged, the same document converted at the real path. The request to restart LabVIEW went to the
user without that being checked first.

## 3. `setup` and the labelled expectations

A case may now carry `setup: [{"vi": "<abs path>", "inputs": {...}}]`. Each setup VI is called
before the subject, in order, its `error out` into the next one's `error in` and the last into the
subject's - the error wire is the only thing that orders calls on a generated diagram, so a setup VI
without an error pair, or a subject without `error in`, is refused before anything is written. Each
setup input is a constant named after its terminal. **Direct route only**: a setup VI is called by
name, so it is opened through the project with the subject; the placeholder route refuses a case
carrying `setup`.

Accepted on `Read Accounts File.vi` with `Write Accounts File.vi` as the setup, two cases writing
and reading back a 2 x 4 and a 1 x 4 table: `ok`, route `direct`, 15.4 s; the runner ran **2 tests,
0 failures**.

**The negative control then found a gap one layer further in.** The expectation is a
`array.2{string}`, and `lvai_set_constant` refused it - numeric, boolean, string and enum only - so
labelling the constants would have bought nothing for exactly the subject it was accepted on. It now
takes an array or a cluster as the AIXML literal, the form `expectedConstants` reports:
`LvXmlLiteral` builds LabVIEW's own XML from the literal and the constant's EXPORTED type, and the
helper sets it through `Unflatten From XML` into a variant (§4 has why that works). Accepted:

| arm | answer |
|---|---|
| `expected 1` set to `[[9,X,Y,1]]` | `verified: true`, before and after read from exports, 5.6 s |
| the suite | **2 tests, 1 failure** |
| `expected 1` restored | `verified: true` |
| the suite | **2 tests, 0 failures** |
| a ragged literal `[[1,A],[2]]` | refused by name, nothing written |

An enum, path, refnum, variant or timestamp INSIDE a compound is refused: each has an XML shape
nobody has measured here.

## 4. Typed compound inputs - and the trap in each half

**Arrays and clusters.** `Unflatten From XML` wired with a **Variant constant as its `type`**
yields a variant carrying the value's own type, measured first on a probe VI with a 2D string array
(round trip exact, error 0). So the value goes in as LabVIEW's own XML - the `xml` the tool already
returns under a compound control - and the server wraps a bare `<Array>` or `<Cluster>` in
`<LvVariant>` and folds the indentation between its tags, because the wire format is one line per
value. Accepted: the `accounts` table read out of `Read Accounts File.vi` passed straight into
`Find Account.vi` found account 23456 (`found 1`, `row 1`, `Jennifer`, `1500`); an unknown number
gave `found 0`, `row -1`; a hand-written 1 x 4 table was found; text that is not XML was refused by
`Unflatten From XML` (`1103`) before the run.

**The trap: `Unflatten From XML` decodes NO character reference.** The first version wrote a line
break inside a string member as `&#10;`, and an error cluster's source came back as the literal
text `acceptance&#10;second line`, with error 0 everywhere. LabVIEW's own XML carries a raw line
break there, which a line-paired wire cannot. Such a value is now refused (`inputContainsNewline`,
naming the control) instead of arriving altered.

**Enums and rings** take an item name or an index: the name is looked up in `{LV.Enum}` /
`{LV.Ring}` `Strings []`. **The trap: `Fract/Exp String To Number` reads `Balanse` as `0`**, so a
name-else-number fallback silently sets item 0 and runs the target with it. The frame therefore
asks whether the text was a name OR parsed at all (`offset past number > 0`), and otherwise hands
`Ctrl Val.Set` the STRING, which refuses it. Accepted on `Build ATM Message.vi`: `Balance` and `9`
both produced `Your Balance Is: $ 550.00`, and `Balanse` answered **`Error 91` before the run**.

**Not changed, and seen on the way:** `values` holds the target's INDICATORS only - `message kind`
and `error in` never come back, though the tool's description says "every control and indicator".
That is the helper's `Ctrl Val.Get All` and predates this work; it is recorded here, not fixed.

## 5. `lvai_open_file` `viPaths`

One path per line; the first stands in for `viPath` when that is empty, a path that is not there is
refused before anything opens, and `alsoOpened` reports each VI's own error code. Three VIs through
`ATM.lvproj` in one call: 18.4 s, most of it the project loading; the seven callees of §6 in one
call: 8.0 s.

## 6. `Error 53` names its targets

`ConvertAIXMLToVI` answers a call to a VI that is not loaded with `Error 53, Manager call not
supported` and names nothing. On that code the tool now asks `ValidateAIXML`, which names every
target, and reports them under `unsupportedSubVIs`. The convert itself runs under a throwaway
`_name` - which is what makes the retry safe, and why the `1051` of wave 3 cannot recur. Accepted on
the wave 3 source of `ATM Main.vi` with nothing loaded: refused in 600 ms naming **all seven**
project callees; one `lvai_open_file` with `viPaths` for those seven; the same call at the same path
then `ok` in 2.4 s, four events registered, `execState 1`.

## 7. Not fixed here

- `labview-caraya-unit-test` said a backslash in a `lvai_generate_test` value "must be doubled".
  The acceptance above passed single-backslash paths as the value and ran green, so the sentence was
  corrected; the one beside it, that a failed validation poisons the test name, was not re-measured.
- `lvai_run_vi_and_read_values` does not read controls back (§4).
