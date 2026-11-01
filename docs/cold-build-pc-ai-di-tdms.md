# Cold build: producer/consumer, simulated AI/DI to TDMS, events to CSV (2026-11-01)

Request: "erstelle noch ein Projekt mit producer consumer wo du AI und DI signale liest (simuliert)
und diese in ein TDMS und die Events in ein CSV speicherst. Alles mit unit tests." Built in
`C:\Temp\PC_AI_DI_TDMS` with a seven-VI build contract, pylabview backend throughout except where
noted.

## Result

| VI | builder |
|---|---|
| `SubVIs\Signals\Simulate AI.vi`, `Simulate DI.vi`, `Detect DI Edges.vi`, `Detect AI Limit Crossings.vi` | signals agent |
| `SubVIs\Logging\Append Events To CSV.vi`, `Write Samples To TDMS.vi` | logging agent |
| `AI DI Logger.vi` (queue of `{Stop, AI Values, DI States, Events}`) | main agent |

- Contract `plan` clean on the first try; `verify` 6/6 after wave 0 and 7/7 at the end.
- Main VI driven with Max Iterations 60, Sample Period 20 ms: Samples Written 60 = 60 values per
  TDMS channel; Events Logged 20 = CSV lines after the header.
- Caraya: 42 assertions, 0 failures (Signals 12, Logging 21, Application 9). The application
  test's 6 events (AI 1 below, AI 0 above, DI 0 rising, AI 0 below, Started, Stopped) were derived
  from the contract defaults before the run, then matched.

## Measured

| agent | wall clock | tool calls | tokens |
|---|---|---|---|
| signals (4 VIs, one `lvai_generate_vis`) | 2.6 min | 27 | 137 k |
| logging (2 VIs) | 5.2 min | 50 | 181 k |
| main (scaffold, then generate on release) | 7.4 min | 39 | 177 k |
| Caraya, both batches | 16.6 min | 80 | 297 k |

Wave 0 and the main scaffold ran in parallel; the Caraya agent started on the Signals VIs while the
logging agent was still building. The tests were the critical path.

## TDMS, measured by the logging agent

- `TDMS Write` refuses a SCALAR on `data` ("Polymorphic terminal cannot accept this data type", both
  backends): one value goes through `Build Array` first.
- A 1D array with an ARRAY of channel names writes every value into the FIRST channel; reading
  `AI 1` then fails with -2532. One sample per channel is one `TDMS Write` per channel in a loop.
- No AIXML type literal for a TDMS refnum was found in the documents, so the contract kept the
  refnum off every pane: `Write Samples To TDMS.vi` takes a path and opens/writes/closes per call.

## Defects found, all on `main` (none from the unmerged `feat/build-contract` commits)

1. **`lvai_generate_test` answered `ok: true` for a test VI LabVIEW could not run.** It read only
   `failedAtStep: execState` (2026-09-25). Since 2026-10-14 a composed VI that is written but broken
   stops at `convert` with `executable: false`, and that fell through to the success outcome.
   FIXED: `BulkTools.WrittenNotExecutable` covers both shapes; `IconWanted` uses it too.
2. **The subject's required `error in` was left unwired.** An input with no case value was always
   left unwired (2026-08-28), harmless until `error in` became `required` on every new VI
   (#115, 2026-10-31). Every test of a newly generated VI was then eBad ("required input 'error in'
   is not wired"); the agent worked round it with `"error in":"[false,0,]"` in every case.
   FIXED: a REQUIRED `error in` with no value and no setup chain gets a named no-error constant;
   a `recommended` one stays unwired (constants only where required).
3. **The first setup call's `error in` was left unwired and a value given for it dropped**
   (2026-09-27). A setup VI with a required `error in` made every test VI calling it eBad; the agent
   regenerated its helper with `error in` `recommended`, against the house rule. FIXED: a supplied
   value is used, else a required one gets the no-error constant.
4. Open: `lvai_generate_vi` on pylabview does not find `SubVIs\Signals\Simulate AI.vi` from
   `Tests\Signals` - the composer searches the caller's folder, two levels below its parent and one
   below the grandparent (#82). `lvai_generate_test` passes extra folders; `lvai_generate_vi` has no
   parameter for it. The agent fell back to grpc.
5. The pylabview run route could not set an array-of-clusters input given as a LITERAL
   (`ComValues.cs`, #82); the logging agent ran those cases on grpc. FIXED 2026-11-01, see the
   tank-monitor section.

## Second cold build, with the generate_test fix (2026-11-01, `C:\Temp\PC_AI_DI_TDMS_2`)

Same contract, same agent prompts, a fresh folder, the agents forbidden to read the first build.
Client restarted (server built with the fix); LabVIEW not restarted - only `LVMCP Validate`
helpers were in memory (`lvai_vis_in_memory`), so no earlier VI could capture a bare-name Call.
The Caraya agent was NOT told about the `error in` workaround.

| | run 1 | run 2 |
|---|---|---|
| start to last test report | not timed | **16 min 50 s** |
| signals agent | 2.6 min, 27 calls, 137 k | 2.8 min, 30 calls, 144 k |
| logging agent | 5.2 min, 50 calls, 181 k | 5.4 min, 50 calls, 170 k |
| main agent | 7.4 min, 39 calls, 177 k | 6.9 min, 34 calls, 180 k |
| Caraya agent, three batches | 16.6 min, 80 calls, 297 k | **13.1 min, 65 calls, 257 k** |
| assertions | 42/42 | 40/40 |
| `error in` workaround in the cases | in every case | **none** - the generator wired the no-error constant |
| test helper with `error in` against the house rule | `Delete Fixture File.vi` recommended | **none** - required, and the setup chain fed it |

Contract `verify` 7/7. The logger's integration test asserts the same 6 derived events as run 1
(AI 1 below, AI 0 above, DI 0 rising, AI 0 below, Started, Stopped) and the CSV matched line by line.
The logging agent hit the TDMS channel trap again and solved it the other way: one `TDMS Write` of
a 2D array, one row per channel. The main VI kept the 11-stage chain because the rendered diagram
(1792 x 807) was within budget.

Reported by the agents after run 2, and what each turned out to be (all settled 2026-11-01):
- **`route.listedInProject: false` "although the test is listed"** - not wrong, misnamed: it was
  the SUBJECT's membership (a fresh test helper, not listed yet). Renamed `subjectListedInProject`;
  the test VI's own is `testListedInProject`, read from the `.lvproj` after the listing step.
- **A regenerated test VI's `error in` came out `recommended`** - every test template wrote
  `recommended` and relied on the `errorInNotRequired` repair, which a VI that already exists does
  not get (its flags are kept). The templates now write `required`, so the repair has nothing to do
  and no longer shows up on every generate call.
- **`Read from Text File` `convertEol="true"` refused by the composer** - real; the bit is
  0x2000000 of the node's objFlags (640 -> 33555072 on NI's converter's probe pair). Composed and
  read back since (`PylabviewConvertEolTests`).
- **`TDMS List Contents` with `group name` wired "written broken"** - NOT a composer defect: NI's
  converter writes the same broken VI. Wiring `group name` turns the `group names` output into a
  single `string`; the agent wired it to an array indicator. Recorded in `docs/aixml-reference.md`.
- **No tolerance assertion** - `lvai_generate_test` takes a per-case `"tolerance"`: float scalars
  go to Caraya's `Assert Almost Equal_Float.vi`, 1D float arrays to |a-e| <= tol on every element
  AND equal lengths; a tolerance on a case with no float expectation is refused
  (`GenerateTestToleranceTests`, fixture: the delivered `Simulate AI.vi`).

Verified in LabVIEW after a client restart (`C:\Temp\PC_AI_DI_TDMS_2\Tests\Tolerance\`): one runner,
two generated test VIs, both executable with no repair step, `subjectListedInProject` and
`testListedInProject` both true. Caraya: 6 tests, 3 failures - exactly the three NEGATIVE CONTROL
cases (array off by 0.1 in one element; array ONE ELEMENT SHORT, caught by the length check;
scalar 1/3 against 0.34). Passing: `[5,0,-5,0]` within 1e-9 on `Simulate AI.vi`, 1/3 within 1e-9
on a scalar subject, and an exact case beside them. A tolerance on `Simulate DI.vi`'s boolean
array was refused at `cases` and nothing was written. `convertEol` composed on pylabview and run
on a CRLF file: `a\r\nb` became `a\nb`; the same probe without the attribute kept `a\r\nb`.

Tests: `RequiredErrorInTestTests` - fixture `Fixtures\pane\Simulate DI.xml` is the reader's export
of the delivered `Simulate DI.vi`; `Sensor Event Demo.xml` (error in `recommended`) is the control.

## A similar application, cold: the tank monitor (2026-11-01, `C:\Temp\PC_Tank_Monitor`)

Same request shape, different logic, so nothing could be copied: tank levels as a triangle wave
(AI), one rotating open valve (DI), a TWO-limit alarm (`High`/`Low`/`Normal`, an event when the
class changes) and valve `Opened`/`Closed`. New file names throughout - the earlier builds' VIs
were still in memory, and a same-named VI would have captured a bare-name Call. Same agent setup
and prompt shapes as the AI/DI runs, with the tool fixes of this file in place.

| | AI/DI run 2 | tank monitor |
|---|---|---|
| start to last test report | 16 min 50 s | 21 min 16 s |
| simulation agent | 2.8 min, 30 calls, 144 k | 3.0 min, 28 calls, 144 k |
| storage agent | 5.4 min, 50 calls, 170 k | 6.8 min, 54 calls, 187 k |
| main agent | 6.9 min, 34 calls, 180 k | 6.2 min, 33 calls, 167 k |
| Caraya agent, three batches | 13.1 min, 65 calls, 257 k | 13.6 min, 63 calls, 311 k |
| assertions | 40/40 | 35/35 |

Contract `verify` 7/7. The integration test asserts the 14 events DERIVED from the contract before
the run (it needed `Simulate Tank Levels.vi`'s exact values at p = 4, 16, 36 to place the limits),
and the CSV matched line by line. The Caraya agent used `"tolerance"` unprompted for the triangle
values that are not exact in IEEE754, and every simulation test generated `ok` on the first call.
The main VI's lint said 13 stages; the rendered diagram was 1882 x 710, within budget, so no fold.

What it found:
- **`array{array{double}}` written by the composer as a BROKEN VI.** The storage agent's TDMS
  read-back declared its 2D types as nested arrays. NI's converter refuses that document (Error 53,
  naming nothing); the composer wrote it, eBad, with "source string, sink 1D array of string" on
  `TDMS Read` - which the agent reported as a composer defect in `TDMS Read`. With `array.2{...}`
  the same document is executable on both backends and reads the file back correctly (checked
  against the agent's own `tank.tdms`). `scripts/aixml_lint.py` refused the nested form since
  2026-09-09 (`array-nested`); `AixmlCheck` did not. FIXED: `arrayNested` is an ERROR there now, so
  `lvai_generate_vi` stops before writing anything (`AixmlCheckNestedArrayTests`, fixture: the
  reader's export of the broken VI).
- **A comma inside a string-array element** - FIXED 2026-11-01. Measured first: `[Source\2CEvent,
  Tank 0\2CHigh,plain]` composes to three strings, the first two with a comma, on BOTH backends.
  Two tools got it wrong: `lvai_generate_test` escaped the author's `\2C` into `\5C2C` (the text
  `\2C`, so a correct subject failed), and `lvai_run_vi_and_read_values` printed the elements raw
  as `[Source,Event,Tank 0,High,plain]`. Now `\2C` and `\5C` written by the author pass through, a
  case value may be a JSON array or object (`"Rows":["Source,Event"]`, escaped by the tool), and the
  run tool writes a string ELEMENT with `\2C`/`\5C` (a scalar string stays raw).
- **`Index Array` on a sub-array** - FIXED. The refusal was not about the index: the input was
  `Transpose 2D Array`'s output, typed `SubArray`. NI's converter builds the row of a transposed
  `[[1,2,3],[4,5,6]]`; the composer now takes a `SubArray` as the array it is a view of, as its
  indexing tunnel already did. Both backends answer row 1 as `[2,5]`.
- **An array of clusters on the pylabview run route** - FIXED. It was narrower than reported:
  LabVIEW's ActiveX server takes an array of clusters as `object[]` of `object[]`, and the XML
  route already built that - two `{Event Source, Event Text}` clusters given as LabVIEW XML set
  `Write Alarm Log.vi`'s `Events` and reached its CSV intact (measured before the change). Only
  the LITERAL `[[Tank 0,High],[Valve 1,Opened]]` was refused. It now builds the same shape. The
  test found a second fault behind it: a nested list was unescaped one level too early, so
  `[[Valve 1,Opened\2C late]]` read three fields - 2D string arrays had the same fault
  (`ComValuesTests`). Verified after a client restart: that literal on `Write Alarm Log.vi`
  answered `Lines Written 2` and the CSV holds `Tank 0,High` and `Valve 1,Opened, late` - the
  escaped comma arrives as a comma (the CSV writer itself does not quote it, by its contract).
- **No tolerance for 2D float arrays** - FIXED. `tolerance` takes `array.N{double|single|extended}`;
  the size check compares `Array Size` with `Equal?` `aggregate="true"`, because a 2D array's sizes
  are an array. The chain, run on both backends: within 1e-9 true, one value off by 0.1 false, one
  row short false.

Tests: `TankMonitorFindingsTests`.
