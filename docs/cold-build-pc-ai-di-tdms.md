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
5. Open: the pylabview run route cannot set an array-of-clusters input over COM
   (`ComValues.cs`, #82); the logging agent ran those cases on grpc.

Tests: `RequiredErrorInTestTests` - fixture `Fixtures\pane\Simulate DI.xml` is the reader's export
of the delivered `Simulate DI.vi`; `Sensor Event Demo.xml` (error in `recommended`) is the control.
