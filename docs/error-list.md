# Reading LabVIEW's Error List

`lvai_error_list` answers what LabVIEW's **Error List** window shows: every broken VI in memory,
each with the Error List's own lines and the explanation printed under them. It closes a gap that
cost real diagnoses: `lvai_exec_state` says THAT a VI is broken (`execState 0`) and names a missing
subVI through `VI Linker Errors`, but a loose wire, an unwired required input or a type conflict
read as eBad and nothing more. The user found those in the IDE while every answer here was silent -
the DQMH handler's unwired `Merge Errors` input of 2026-10-06, and `Wire: Wire has loose ends.` in
`ATM Main.vi` on 2026-10-14, which prompted this tool.

## 1. The two methods

Neither is in LabVIEW's public VI Server catalogue (`docs/vi-server-methods.tsv`); both validate,
convert and run, measured 2026-10-14:

| class | method | terminals | returns |
|---|---|---|---|
| `{LV.Application}` | `Get Broken VI List` | `reference`, `error in (no error)` -> `reference out`, `error out`, `Broken VI List` | the NAMES of the broken VIs in that application instance, not paths |
| `{LV.VI}` | `Get Errors` | `reference`, `error in (no error)`, `Call Dangerously?` -> `reference out`, `error out`, `Errors`, `Details` | two string arrays: `Errors` is the Error List's lines, `Details` the explanation of each |

The spellings come from NI's Application Builder, which uses this pair to explain a failed build
(`vi.lib\AppBuilder\AB_Get_Detailed_BrokenVI_Message.vi`; its diagram is locked, and the method
names were read off an unlocked copy the user supplied). Nothing of that VI is shipped here: the
helper `scripts/lvai_vi_errors.xml` is our own AIXML and only calls the two methods.

Measured on a deliberately broken VI - an `Add` with `y` unwired:

- `Errors`: `Add: Contains unwired or bad terminal`
- `Details`: `One or more required inputs to this function are not wired or are wired incorrectly. Show the Context Help window to see what the connections to this function should be.`

On an executable VI both arrays are empty with no error. On the VI that is itself RUNNING the call
(a probe asking about itself) `Get Errors` answers **Error 1073**.

## 2. Which application instance - the part that decides everything

`Get Broken VI List` lists the broken VIs of ONE application instance, and an unwired reference
means the instance the calling VI runs in. Measured as an A/B with `broken_add.vi` open in the IDE
and the same helper run twice:

| helper run through | instance | list |
|---|---|---|
| ActiveX (`LabViewCom.RunAsync`) | the IDE's main instance | `broken_add.vi`, with its error |
| NI's `RunVIAsTopLevel` (`lvai_run_vi_and_read_values` on grpc) | the AI addon's own instance | **empty, no error** |

So the helper must run over ActiveX. Through NI's service it answers a clean, empty list, which reads
exactly like "nothing is broken".

A VI opened inside a project lives in **that project's** instance. Measured with a one-VI project
`ErrListProbe.lvproj` active and `broken_add.vi` still open loose: the main instance listed
`broken_add.vi` only, the project's instance (reached through `Project:Active Project` ->
`{LV.Project}` `Application`) listed `broken_in_project.vi` only. The helper reads both; the project
half answers `1055` when no project is active, which is not a failure.

## 3. What it does not see

- **Disk.** It reads memory. A broken VI nobody has open is not listed, and the in-memory copy is the
  one read - an unsaved edit included, which is what the IDE shows too. A first attempt on
  `C:\Temp\ATM_pylabview\Automatic Teller Machine\ATM Main.vi`, opened from disk by path, found no
  errors there while the user's Error List showed a loose wire: the IDE's copy was not the file.
  Open a file with `lvai_open_file` first.
- **Warnings.** Both methods return errors only; the Error List's "Show Warnings" has no counterpart
  here.
- **Instances other than main and the active project** - a second open project that is not active,
  for one.

## 4. Generation

The composer HAS parts for both methods - `invoke__LV_Application__Get Broken VI List.xml` and
`invoke__LV_VI__Get Errors.xml` in `scriptsixml-pylabview\parts`, harvested from NI's own
`AB_Get_Detailed_BrokenVI_Message.vi`, which pylabview reads although its diagram is locked. So the
helper is composed (no NI service needed) and NI's converter is only the fallback; both builds were
measured listing the same broken VI with the same line. `lvai_check_aixml` answered
`methodNameNotKnown` for both names until `docs/vi-server-aixml-names.tsv` was re-harvested with
this helper in `scripts\`.

## 5. Every executability check names the cause

`scripts/lvai_exec_state.xml` calls `Get Errors` on the VI it already opened by PATH, so every tool
that checks executability through it - `lvai_exec_state`, the `execState` step of `lvai_generate_vi`
and the test generators, `lvai_convert_aixml_to_vi`'s `executable` (on the composer route only when
the VI is broken, through the composed helper) - answers `errors`, each line with its explanation.
For a file on disk the instance question of section 2 does not arise: a VI opened by path is
answered wherever the helper runs, measured on both backends with a broken VI NOT in memory:

| route | execState | linker errors | Get Errors |
|---|---|---|---|
| grpc, NI-converted helper | 0 | `The VI is not executable.  The full development version of LabVIEW is required to fix the errors.` | `Add: Contains unwired or bad terminal` |
| pylabview, composed helper over ActiveX | 0 | the same | the same |
| an executable VI | 1 | empty | empty list, code 0 |

So for a fault in a VI's OWN diagram the linker text never had the cause; `errors` does.

`Get Errors` runs on its OWN error chain: on a VI that is running it answers `1073`, and that must
not turn a good state reading into an error. The answer then says `errorsRead: false` - not read,
which is not the same as none. A helper built before this change is rebuilt automatically, because
the grpc route now checks the AIXML's timestamp like every other helper (it checked existence only).


## 6. Acceptance after a restart, 2026-10-14

Run through the MCP client after restarting LabVIEW and the client, on both backends, with one
broken VI loose in the main instance and one inside an active project:

| call | result |
|---|---|
| `lvai_exec_state`, grpc and pylabview, VI on disk | `errors: [Add: Contains unwired or bad terminal ...]` on both |
| `lvai_error_list`, fresh LabVIEW | empty main list, project half `available: false` (1055) |
| `lvai_error_list` with both VIs open | each VI in its own instance, with its line |
| `lvai_error_list viName` with a path in other case | matched by file name |
| `lvai_convert_aixml_to_vi`, grpc and pylabview | `errors` beside `executable: false` |
| `lvai_generate_vi`, pylabview | the note quotes the first Error List line |

Three defects the acceptance found, all fixed the same day:

- **Two `lvai_error_list` calls issued together collided** on the one helper VI: the second answered
  `0x3E8 The VI is not in a state compatible with this operation`. One run at a time now
  (`HelperGate`), re-measured with two calls in parallel.
- **`lvai_generate_vi` on pylabview reported a written, broken VI as "The pylabview composer failed
  and wrote nothing: No Error"** - because `convert` answers `ok: false` for a broken VI and the
  generic failure test read that as a failure of the composer. The executable check now runs first.
- **A parallel convert once answered `errors: null` with no reason**, and it did not reproduce
  alone or in a second parallel run. The answer now carries `errorsNotReadBecause`, so the next
  occurrence names its cause instead of leaving it to be guessed.

### 6a. Second acceptance after a restart - and the parallel miss, partly explained

The whole table above held again. The parallel miss came back once more - `lvai_generate_vi` and a
`convert` issued together, the generate answering `errors: null` - and this time the new field named
the place: **Get Errors itself answered an error**, so the state was read and the lines were not. It
is intermittent: 2 of about ten composer-route calls issued in parallel, never alone, and four
issued together right after answered correctly. Which code Get Errors returns there is not yet
known; `errorsNotReadBecause` now names it (`Get Errors answered error <code>`), on
`lvai_exec_state` as well as on `convert`, so the next occurrence settles it. Until then, read a
broken VI's `errors` again with `lvai_exec_state` alone when a parallel build answers null.

### 6b. Third acceptance - the parallel miss is `1026`, and it is retried

After the next restart the table held a third time, and the miss reproduced once in seven parallel
calls, now with its code: **`Get Errors answered error 1026`** - *VI Reference is invalid* - on the
very reference the same helper run had just read `Execution:State` through. So the state is always
right and only the lines go missing; what invalidates the reference in between is not established
(it needs another call running in parallel, and has never happened to a call alone). The composed
route now runs the helper ONCE MORE, inside the same gate, when it reads a broken VI whose lines Get
Errors refused (`NeedsErrorsRetry`). Six parallel calls afterwards all answered with their lines -
which does not prove the retry fired, since the miss is rare (3 of about 17 parallel calls so far);
if it ever comes back after the retry, `errorsNotReadBecause` names the code again.

### 6c. Fourth acceptance - the same miss on the grpc route

After the next restart everything above held again, and a nine-call parallel round showed the miss
on the OTHER route: a grpc `convert` answered `errors: null` with no reason at all - its warning
still claiming `errors` held the lines. The retry and the reason had been built into the ActiveX
route only. The grpc route (`ExecStateTools.ReadAsync`) now retries the same way, and `convert`
carries `errorsNotReadBecause` and words its warning by what was actually read. A six-call mixed
round afterwards (two grpc, four composer) answered every one with its lines.

### 6d. Fifth acceptance

After another restart: the baseline as before, and a nine-call parallel round (two `lvai_error_list`,
two grpc `convert`, three composer `convert`, two composer `lvai_generate_vi`) answered every one
with its Error List line, `errorsNotReadBecause: null` throughout. Over the five acceptance rounds
the unread-lines case occurred 4 times in about 40 parallel calls, always with another call in
flight, never on a call alone, and not once since both routes retry.
