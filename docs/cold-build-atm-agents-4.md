# Cold build: the ATM with agents, fourth run, 2026-09-25

The CLD ATM exam (100928D-01) built cold a fourth time by agents, into a fresh directory, with the
same waves and the same agent split as the third run (`docs/cold-build-atm-agents-3.md`), so the
two are comparable - and this one ran on the fixes that run 3 produced. Deliverable, timeline and
timing: `C:\temp\ATM_Agents_4\` (`DESIGN.md`, `TIMELINE.txt`, `TIMING.md`).

## 1. Result

**42:20 of wall clock from request to green tests, with no pause** - run 3 needed about 48 minutes
of working time plus a ~47-minute wait for a client restart. The application was complete and
executable after about 30 minutes. 13 VIs and a class with 3 members, all executable; Caraya
**11 suites, 165 tests, 0 failures** (run 3: 108), re-run by the orchestrator: 165/0. No stub
file, no placeholder, no node swap; the real `ATM accounts.txt` untouched.

| phase | wall | run 3 |
|---|---|---|
| set-up | 1:04 | 0:43 |
| wave 1, four agents in parallel | 6:31 | 5:35 |
| integration, 10 VIs listed | 0:26 | 0:18 |
| wave 2, method + its suite | 10:32 | 9:38 |
| wave 3, state machine + main VI | 11:35 | 16:51 + ~2:30 + ~47 min wait |
| wave 4, suites + runner | 12:12 | 10:32 |

What run 3's fixes bought, from the transcripts: no restart wait; `viPaths` loaded five and eight
callees in one call each; `lvai_run_vi_and_read_values` set arrays, enums and clusters directly;
wave 4 wrote NO AIXML by hand, and its negative control was two `lvai_set_constant` calls.

**LabVIEW vanished once**, at 21:02:14 during `lvai_create_class` with four agents on the instance -
no DWarn, no minidump, no `Executing:` tag. The class agent restarted it with `lvai_ensure_labview`
and no other agent lost a call. Cause not established.

## 2. NESTING BEATS `uid_parent` - the main VI came out eBad

Three shift-register seed constants were written inside the consumer loop's `<Structure>` with
`uid_parent="root"`. LabVIEW put them INSIDE the loop, the seeds became feedback, and
`ATM Main.vi` was eBad; `lvai_generate_vi_with_events` never validates, so nothing said why until a
render and an export. **Measured afterwards as a clean A/B**, one While Loop and one seed constant
feeding its shift register, differing only in where the constant is written:

| document | answer |
|---|---|
| constant nested in the `<Structure>`, `uid_parent="root"` | ValidateAIXML: `While Loop: Is a member of a cycle` |
| the same constant at document top level | validate 0, convert 0, 6 954 bytes |

So the nesting decides - the converse of the FreeLabel case in CLAUDE.md, where a top-level
`uid_parent` naming a loop does not reach it. **Fixed**: `lvai_check_aixml` answers
`uidParentContradictsNesting` as an ERROR (so `lvai_generate_vi` blocks on it), and
`lvai_generate_vi_with_events` now runs the check before it converts and refuses with
`failedAtStep: check`. `scripts/aixml_lint.py` had caught this all along as `parent-mismatch` -
with a message claiming LabVIEW follows `uid_parent`, now corrected. Scanned before the check went
in: 128 AIXML files in the repository, none contradicts itself.

## 3. THE CASES OF ONE TEST VI RUN IN PARALLEL

Wave 4's first full run was 165/3: three Handle ATM Action cases shared one fixture file, one case's
setup rewrote it while another read it, and a fixture came out withdrawn twice. Only a case's own
error wire orders its setup before its subject; the cases hang off Define Test side by side.
**Fixed**: `lvai_generate_test` and `lvai_generate_method_test` refuse, before anything is written,
a path given to a SETUP in one case and used by any other case (setup or subject input), naming the
path and the cases. A read-only file several cases share is fine.

## 4. `setup` for a class method's test

`lvai_generate_method_test` had no `setup`, so wave 2 hand-authored its method's mutating suite.
**Fixed**: the same `setup` key and the same parser as `lvai_generate_test`; the setup chain feeds
the METHOD's error in (in place of the `no error` constant), the setup VIs open with the class in
one `lvai_open_file`, and the setup code - export, error-pair check, input names, the fixture race -
is one implementation shared by both tools.

## 5. A line break through `lvai_set_constant`

It refused any value with a line break - the helper's wire pairs names and values by line - so no
multi-line message expectation could be broken for a negative control. **Fixed in the transport**:
`lvai_run_vi_and_read_values` encodes LF as 0x1E and CR as 0x1D, and the typed helper decodes both
before it converts anything, so a multi-line STRING input works as well and so does a compound
value with a multi-line member. A helper that does not decode (the legacy and the `runForMs` one)
still refuses; which one decodes is read from its AIXML (`encoded line feed`). A string constant's
verdict now unescapes the export (`\0A`, `\5C`, `\2C`) before comparing - compared raw, a backslash
or a line break could never have verified.

## 6. Two test VIs over one subject reported one suite name

`lvai_generate_test` titled every suite `Test <subject>`, so the navigation and the transaction
suites of Handle ATM Action were both `Test Handle ATM Action` and `lvai_run_caraya_tests` mapped
neither back (`testVi: null`). **Fixed**: the title is the test VI's own file name, as the class and
method generators already had it.

## 7. Symbolic uids were numbered from 1

`SymbolicUids` numbered from the highest numeric uid plus one, so a document written only in
symbols landed in LabVIEW's reserved range (36 symbols, 1..36, and a saturated DWarn count).
**Fixed**: never below `AixmlCheck.SafeUidBase` (4200).

## 8. `resolvedAtConversion` on a failed conversion

On an Error 53 the answer listed the not-loaded VIs under `loadedSubVIs.resolvedAtConversion` while
the note beside it said "NOT LOADED". **Fixed**: `notResolvedAtConversion` for the not-loaded 53,
`unresolvedAtValidate` when the failure says nothing either way, `resolvedAtConversion` only when
the conversion got past them.

## 9. Acceptance against LabVIEW over raw stdio

On a copy of this build, `C:\temp\ATM_Accept_6`:

| point | arm | answer |
|---|---|---|
| 2 | `lvai_check_aixml` on the nested probe | 1 error, `uidParentContradictsNesting` |
| 2 | `lvai_generate_vi_with_events` on the real main VI's AIXML with one stray nested constant | `failedAtStep: check`, nothing written |
| 7 | convert of a symbols-only document | `symbolicUids` 4200..4204 |
| 8 | `lvai_generate_vi` calling `Get ATM Menu.vi`, not loaded | `notResolvedAtConversion: ["Get ATM Menu.vi"]` |
| 3 | `lvai_generate_test`, two cases writing one fixture | refused at `setup`, test VI not written |
| 3 | `lvai_generate_method_test`, the same | refused at `setup` |
| 4 | method suite: Deposit 100 and Withdraw 300, each with its own fixture written by setup | `ok`, route direct; the runner 14/0, then 14/0 again with the fixtures rewritten (2100.00, 500.00) |
| 4 | negative control on `expected balance 1` | 14/1, exactly `deposit 100 on 12345 (balance)`; restored, 14/0 |
| 6 | two test VIs over `Get ATM Menu.vi` in one runner | suites `Test Get ATM Menu Left` and `... Right`, each mapped to its VI |
| 5 | `lvai_set_constant` appending `\nBROKEN` to the multi-line Welcome expectation | `verified: true`; 14/1 on `Welcome text`; restored, 14/0 |
| 5 | `lvai_run_vi_and_read_values`, `first name` = `Ada\nLine2` | `lineBreaksEncoded`, message `Welcome to Acme Bank\nAda\nLine2 Lovelace...` |

## 10. Not fixed here

- The disappearance at 21:02:14 (§1).
- `Set Control Focus.vi`'s Key Focus write read back FALSE on a closed panel; not verified.
- `lvai_run_vi_and_read_values` still reads indicators only, not controls (recorded in
  `docs/cold-build-atm-agents-3.md` §4).
