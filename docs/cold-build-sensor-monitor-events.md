# Cold build: user events + event structure + a class behind an interface, 2026-09-25

The first agent-driven build that combines everything an ordinary LabVIEW application carries: an
interface with a dynamic method, a class implementing it whose override reads and writes its OWN
fields, a user event with a payload, and an Event Structure handling it beside front-panel events.
Deliverable, timing and sequence diagram: `C:\temp\SensorMonitor_Events\` (`TIMING.md`,
`SensorMonitor-sequence.mmd`).

## 1. Result

Request 11:59:49; **application executable after 15:35, Caraya suite green (11/0, negative control
caught) after 22:24.** Four agents in three waves: class + leaf VI in parallel, then the main VI,
then the tests. **Zero stub files** (`user.lib\LV_MCP` 434 before and after, 0 stub tool calls in
the transcripts). Every test generator answered `route: direct`.

## 2. A CLASS METHOD CALLING ITS OWN ACCESSORS NEEDS NO STUB - measured, first time

The open question of `docs/aixml-call-loaded-vi.md` §8. The route that worked, first try:

1. open ONE accessor of the class through the project (`lvai_open_file`);
2. author the override with `path` stand-ins for its class terminals and direct Calls
   `Simulated Sensor.lvclass\3ARead Current.vi` etc.;
3. `lvai_generate_vi` to the member's final path - validate refuses (`Unsupported SubVI`), convert
   resolves all three accessors, `failedAtStep: execState` is EXPECTED (path stand-ins feed class
   inputs) and the file is written;
4. `lvai_add_class_method` with that EXISTING `vi` - retype, dynamic dispatch, membership:
   `ok: true`, `terminalsRetyped: 2`, and afterwards `execState 1`.

**One manual step it needed**, and it is a tool gap: step 3 runs with the project open, so
LabVIEW's save lists the new VI as a LOOSE project item; the close's sweep keeps it (it is not in
one of our temp trees), and `lvai_add_class_method` would then answer 56002. The agent removed the
`<Item>` line by hand with the project closed. `lvai_add_class_method` should drop a loose entry for
the VI it is about to make a member.

## 3. Where pyLabVIEW still runs

Not a single `pylv_*` call, but four TOOLS use it inside: `lvai_add_class_method` (its pane step
moves a member onto 4815 with `conpane`) and the three event tools -
`lvai_generate_vi_with_events` (registration), `lvai_wire_dynamic_events`,
`lvai_set_event_data_fields` (the user-event payload). "No pyLabVIEW" is therefore reachable only
for an application without an Event Structure and without class methods authored through AIXML.

## 4. Findings

- **`lvai_create_class` cannot set a field default.** `Gain` was asked for with default 1 and is 0,
  so the default object's reading is 0. The main VI writes Gain 2 before its loop.
- **A method-test case seeds ONE field.** `reading = Current x Gain` with Gain != 0 is not
  expressible, so the multiplication is asserted only as `0 x 0`; the running application shows it.
- **`lvai_generate_method_test` accepts `writeField` + `expectOutput` without `expectFieldValue`
  and silently adds a "field SURVIVED the call" assertion** - wrong for a method that changes the
  field (here Current + 1), and nothing in the answer says it was added. The test agent found it
  only by exporting the suite.
- **`runForMs` did not reach the helper** from `lvai_run_vi_and_read_values`: 2500 and 5000 both
  returned after ~1.07 s, the helper's default. Driving `lvai_run_for_ms.vi` directly with 4000 ran
  4.06 s. Not diagnosed; the session's client schema may predate the parameter's handling.
- **`lvai_wire_dynamic_events` closes the project without a path**, so its save adopted its own
  helper `lvai_wire_dyn_events.vi` into the `.lvproj`; the orchestrator's close with `projectPath`
  swept it. The test generators' internal closes likewise report `noProjectPathGiven`.
- **`lvai_generate_caraya_test_runner` leaves the project open** (`projectLeftOpen: true`).
- **Authoring dominates the event VI**: 212 of 349 s in wave 2 was the model writing AIXML; the four
  event and swap tools together took under 10 s inside LabVIEW.

## 5. All six findings FIXED the same day - accepted against LabVIEW over raw stdio

Probe project `C:	emp\FindingsProbe\`, one run, every arm measured:

| finding | fix | acceptance |
|---|---|---|
| loose project entry before `lvai_add_class_method` | its project-closed window now drops a LOOSE `.lvproj` entry for each VI it is making a member (`dropLooseProjectEntries`), matched by resolved URL, never under a class or library item | a method generated with the class loaded, project closed (entry adopted), then the call: `removed: ["Scaled.vi"]`, `execState 1`, entry gone |
| no field defaults | `lvai_create_class` / `lvai_add_class_field` take `<type>.<name>=<value>`; the value becomes the carrier control's literal, which NI's provider copies into the field. Numbers, booleans and the unsigned range are checked; a timestamp default is refused (the converter discards it) | `double.Gain=1` -> `Read Gain.vi` on the default object reads `1.0` |
| a method case seeds one field | `seed`: `{"Current":"3","Gain":"2"}` writes fields before the call without asserting them, on both routes | `reading == 6` green; `Current` 3 -> 4 green, with `Gain` seeded |
| silent survival assertion | the answer's `assertions` step lists every assertion per case and `seededNotAsserted`, and WARNS when `writeField` beside an output or error assertion implies "unchanged" | step present, cases spelled out |
| `runForMs` ignored | it chose the timed helper and was never WRITTEN into its `run for ms` control, so every timed run lasted the helper's default 1000 ms | `runForMs: 4000` -> 4272 ms, 16 readings, alarm TRUE |
| closes without a path, runner leaves the project open | `lvai_close_active_project` reads the ACTIVE project's path before closing when none is given, so every internal close sweeps (`projectPathFrom`); the runner no longer reopens - measured beforehand that a runner over class suites runs green with its project closed | close with no arguments: `swept: true`, `projectPathFrom: active project`; runner `projectLeftOpen: false`, suite 2/0 with the project closed |
