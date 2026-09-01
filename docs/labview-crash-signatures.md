# When LabVIEW disappears: read NI's own log, not the Windows event log

Measured 2026-08-26, after three disappearances in one session that were nearly attributed to the
wrong cause. The user's instinct — "the crashes have something to do with the new code" — was right,
and the first investigation said the opposite because it looked in the wrong place.

## The mistake worth not repeating

`Get-WinEvent` over the Application and System logs found **nothing** naming LabVIEW, and there were
no WER reports and no `.dmp` files under the usual roots. The query was sound: it returned 150 other
events in the same window. From that the session concluded "LabVIEW exited without faulting, so there
is no evidence of an access violation" — and that conclusion was wrong.

**LabVIEW handles its own faults.** It catches them, writes its own log and its own minidump, and
exits. Windows Error Reporting never sees it, so an empty event log says nothing at all about whether
LabVIEW crashed.

## Where the evidence actually is

```
%TEMP%\LabVIEW_32_<version>_interactive_<user>_cur.txt    the CURRENT session
%TEMP%\LabVIEW_32_<version>_interactive_<user>_log.txt    the PREVIOUS session
%TEMP%\LVStatus.txt
```

`_cur.txt` is overwritten when LabVIEW next starts, so **copy it before restarting**. Both files were
written 4 seconds before the process vanished, which is what made the timestamps line up with a
process watcher and turn a suspicion into a measurement.

Search them for `DWarn`, `DAbort`, `minidump id` and `Executing:`.

## The signature that was found

Twice, identically, each with a minidump id:

```
source\ole\OMAutoClasses.cpp(74) : DWarn 0x762E6013:
    Out of bounds TypedObjList access (index: -1, nObj: 0)
[ExecSys:0; Executing:"[VI "LV AI Core.lvlibp:VI generator.vi"]"]

VI call stack:
- LV AI Core.lvlibp:VI generator.vi
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:ValidateAIXML.vi
- LV AI gRPC Service.lvlibp:LVAI.lvclass:Start Sync.vi
- LV AI gRPC Service.lvlibp:gRPC-servicer-release.lvlib:ServiceBase.lvclass:Start.vi
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:RunService.vi
```

**Read it carefully, because it says something specific.** The faulting code is NI's, inside the AI
addon's own `VI generator.vi`, reached from `ValidateAIXML` — not from a generated helper running.
`OMAutoClasses` is the OLE/VI-Server **automation class registry**, and `index: -1, nObj: 0` is a
name looked up in an EMPTY list, "not found" returned as -1, and then used as an index anyway. An
unchecked lookup.

So the trigger is on the way IN, while LabVIEW is parsing AIXML — and the AIXML being validated in
every one of these cases named VI Server classes the catalogue does not list:
`{LV.LVClassLibrary}`, `{LV.Project}`, `{LV.Panel}`, `{LV.Cluster}`. That is a correlation, not a
proven cause; what is established is the crash site.

Two further crash points in the same logs, worth recognising:

| where | what it says |
|---|---|
| `Open project application ref.vi` (2x) | the `Project:Active Project` route itself — used by `lvai_close_vi`, `lvai_close_active_project` and the accessor helper |
| `HeapObjMapImpl.cpp(226)` | `trying to override with non-reserved UID, request: 20 res: 0 max: 42` |
| `BadLinkerObjs.cpp(276)` | NI's own assertion: `LinkIdentity "Auto.lvclass:Auto.ctl" is NOT a bad subObj! Why are we propagating the myth that it is bad?` |
| `ThEvent.cpp(213)` | `DestroyPlatformEvent failed with MgErr 42`, 14x — **not teardown noise**, which is what this row said first. See the next section: on its own, with minidumps, it is the whole story of a second failure mode |

## A second, different mechanism: the run itself

The signature above fires while AIXML is PARSED. A third disappearance, after a run that used the
CACHED helper and therefore never called `ValidateAIXML` at all, looks nothing like it:

```
source\ThEvent.cpp(213) : DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42.   (x15)
```

14 minidumps in that session, no `OMAutoClasses`, no heap-UID warning. So there are at least two
independent ways down, and the second one is not about validation: it is an accumulating failure to
destroy platform event objects during a run that opens and closes many VI Server references. LabVIEW
left about three minutes after the run finished, with nothing in the log after it.

A fourth disappearance carried only `DestroyPlatformEvent` warnings and **no minidump** - that one
was a clean shutdown, a person closing LabVIEW. It is tempting to read that as "minidumps separate a
fault from an ordinary exit"; the next section measures that idea and kills it. The reason session D
has none is simply that nothing was GENERATED in it.

| session | minidumps | distinguishing signature | reading |
|---|---|---|---|
| A | 5 | `BadLinkerObjs`, `HeapObjMapImpl` | fault |
| B | 17 | **`OMAutoClasses` OOB** under `ValidateAIXML` | fault, parse-time |
| C | 14 | `DestroyPlatformEvent` only | reference leak, see below |
| D | 0 | `DestroyPlatformEvent` only | clean exit |

## COUNTING MINIDUMPS IS NOT A FAULT MEASURE - measured, and it corrects the table above

The obvious reading of that table is that minidumps separate a fault from an ordinary exit. **It is
wrong, and the control experiment is three lines of AIXML.** Generating a VI that is a control, an
`Increment` and an indicator - no VI Server, no references, no project - added **3 minidumps** and
**0** `DestroyPlatformEvent` failures.

So NI's generator writes minidumps as a matter of course, roughly one per node, on any generation at
all. A session with 21 of them has generated some VIs; that is the whole of what it says. What the
control experiment DOES establish is the split:

| signature | raised by a plain generation? | belongs to |
|---|---|---|
| `minidump id` | **yes, ~3 for a 3-node VI** | NI's generator, normal operation |
| `DestroyPlatformEvent failed with MgErr 42` | **no, zero** | work that opens VI Server references |

That makes `DestroyPlatformEvent` the signal worth watching and the minidump count noise. Session C's
"14 minidumps" was never evidence of anything.

## A third mechanism, and it was ours: saving the project right after a run

Measured 2026-08-26. Three accessor runs on a cold LabVIEW survived ten minutes with the process
stable. A fourth run, followed immediately by a project save-and-close, killed it in two seconds:

```
source\editor\BadLinkerObjs.cpp(276) : DWarn 0x23276257:
  [LinkIdentity "Bus.lvclass:Bus.ctl" [ My Computer] is NOT a bad subObj!    (x8)
[ExecSys:0; Executing:"[VI "lvai_close_active_project.vi"]"]                 14:53:13.44
```

The process was gone at 14:53:15, and all eight faults name the CLOSE helper, not the accessor
helper. `BadLinkerObjs` is the editor's link bookkeeping and `Bus.ctl` is a class private data
control - so what LabVIEW could not survive was being asked to save a project whose classes had just
been rewritten by the wizard.

**Each operation is fine alone.** `lvai_close_active_project` has run many times in this repository
without incident, and the accessor runs on their own had just proved they survive. It is the
immediate succession that is fatal, which is why `lvai_create_accessors` now strips the helper item
from the `.lvproj` **on disk** by default and leaves the project alone; `closeProject` exists, is off,
and carries this measurement in its own description.

**And the earlier optimism in this document was premature.** The two closed references took survival
from "about three minutes after one run" to "ten minutes across three", which is a real improvement
and not a fix: `DestroyPlatformEvent failed with MgErr 42` is still logged at roughly 13 per run.
LabVIEW still leaves eventually. Verify on disk, immediately, every time.

## A FOURTH mechanism: LabVIEW hangs rather than dying, and it is reproducible

Measured 2026-08-26 across three attempts at the same cold rebuild. LabVIEW does not always leave -
sometimes it stays in the process table and stops answering, which looks completely different from
every signature above and needs a different fix.

**How to tell.** The connection error's status codes separate the two cases, and this was worth
wiring into the message rather than leaving to be re-derived:

| every LabVIEW.exe listener answers | meaning | fix |
|---|---|---|
| `Unavailable` | IDE up, service never started | open Nigel |
| `DeadlineExceeded` | service there, **LabVIEW hung** | kill the process |

Confirmed independently by `(Get-Process LabVIEW).Responding = False`, and by the title bar reading
`LabVIEW (Not Responding)`. The NI session log carries almost nothing in this state - 2
`DestroyPlatformEvent` lines and 1 minidump - because nothing faulted; it simply stopped.

**The trigger, and this is the actionable part.** Three `lvai_create_class` calls issued back to back
in one shell invocation: the first succeeded, and LabVIEW was hung by the time the second connected.
Reproduced twice, on two freshly started instances. The *same three calls* issued one at a time, with
a `Responding` check between them, **all succeeded and left LabVIEW responsive** - and so did the
three accessor runs after them, for six operations in a row on one instance.

So it is pacing, not any single operation. A `lvai_create_class` ends by load-checking through
`lvai_describe_project`, which opens the project in the IDE; connecting again while that is still
settling is what wedges it. **Do not chain class creations.** One call, confirm, next.

A window of class `LVDChild` is present while hung, which is LabVIEW's dialog class - but it has no
Win32 child controls, because LabVIEW draws its own dialog contents. So enumerating window text tells
you nothing, and `Responding` is the check that works.

## The crash needs NO input from us - measured on a freshly rebooted machine

The single most useful measurement in this whole investigation, and it took a reboot to get it.
After a full Windows restart, with the accessor work finished and nothing of ours running but a
`Get-Process` poll every five seconds:

```
16:36:12  UP pid=29484        the user's own LabVIEW
16:36:28  GONE                died on its own; not one lvai_* call had been made
16:37:08  UP pid=4796         started by --ensure-labview
16:38:27  NOT-RESPONDING
16:38:53  NOT-RESPONDING
16:39:03  GONE                died during a purely passive stability watch
```

Both sessions logged three minidumps and the same `OMAutoClasses` out-of-bounds inside
`LV AI Core.lvlibp:VI generator.vi`. **So the generator runs, and faults, with no request from this
server at all** - which rules out every hypothesis that blames the AIXML being validated, the helper
being generated, or anything else on our side. It also means bisecting our own history would measure
nothing: an older commit crashes the same way, because the crash does not need us.

Two things ruled out while chasing it, both worth not re-checking:

- **The project file is clean.** The dangling helper item with its `../../../../Users/.../Temp/...`
  URL was the obvious suspect for something LabVIEW chokes on at load. It is gone, and
  `Fahrzeuge.lvproj` lists three classes and nothing else.
- **The AI assistant log records the consequence, not the cause.** `%PROGRAMDATA%\National Instruments\AIAssistants\Logs\AIAssistant.txt` shows Nigel starting
  five monitor streams - Palette Search, Code Completion, Discuss VI, Example Search, Front Panel
  Cleanup - and then losing all five to `An existing connection was forcibly closed by the remote
  host`. That is Nigel noticing LabVIEW died.

**What did change is not in this repository.** The sessions of 2026-08-22 ran for hours with
minidumps present but no early death; the two after the reboot lasted about ninety seconds each. The
addon is the common factor, and two Nigel products are installed side by side -
`NI Nigel AI Advisor for LabVIEW 2025 [25.82.49163]` and `NI Nigel Advanced [26.52.49163]` - on a
LabVIEW 2026 station. Worth a repair install, and worth reporting to NI with the `DWarn 0x762E6013`
signature and the minidump ids.

**The work survives it.** Everything the accessor tool produced is verifiable with LabVIEW dead,
because `lvai_describe_class` and `pylv_extract` read files: Auto 14 members and 14 VIs, Bus 10 and
10, Rennauto 10 and 10, descriptions intact. Read the result from disk, not from the IDE.

## The regression that WAS ours: rewriting the .lvproj under a live LabVIEW

The user said "it still worked after lunch" and asked for an older commit to be tried. That was the
right call, and the reasoning that had dismissed it - "the crash needs no input from us, so our code
cannot matter" - was wrong. It ignored that our code leaves STATE behind.

An A/B/A settles it. Three accessor runs each time, from the same reset state, on a fresh LabVIEW:

| attempt | build | result | survival |
|---|---|---|---|
| A | `4464e56`, 14:30 | 3x ok, 14/10/10 members | 4 min, stable |
| B | HEAD | 3x **failed**, 0 members | **gone in 20 s** |
| A' | `4464e56` again | 3x ok, 14/10/10 | 2 min, stable |
| B' | HEAD with `tidyProject: false` | ok, 14 members | alive |

So it is the build, and inside the build it is one option. Exactly one commit after 14:30 changed
what runs inside LabVIEW on every call - `05eb4c9`, wiring `Run VI`'s `Auto Dispose Ref` - and
**reverting it changed nothing**, which is worth recording so nobody re-suspects it.

**The cause is `tidyProject`, introduced at `5e5fbc1` (14:56) and on by default.** It rewrites the
`.lvproj` FILE while LabVIEW still holds that project OPEN. LabVIEW does not survive having the
project file changed under it.

**And it is intermittent for a reason worth knowing.** The tidy only writes when it FINDS a helper
item to strip. A project with none is read and left alone, which is why some runs after 14:56
survived and looked like evidence that the option was harmless. The danger appears exactly once
LabVIEW has saved a helper item in - that is, precisely when the option is worth using.

It now defaults to **off**, in the tool and in the CLI (`--tidy` opts in, replacing `--no-tidy`).
Use it only when LabVIEW does not hold the project - after `--finish-project` has stopped it.

### Verified stable afterwards: three cold rounds, nine runs, no faults logged

Asked for after the fix, and it is the acceptance test this whole investigation was missing. Three
full cold cycles - stop LabVIEW, reset all three classes to zero members, start LabVIEW, open the
project, generate accessors for Auto, Bus and Rennauto:

| round | runs ok | members | LabVIEW after 90 s |
|---|---|---|---|
| 1 | 3/3 | 14 / 10 / 10 | up, 570 MB flat |
| 2 | 3/3 | 14 / 10 / 10 | up, 571 MB flat |
| 3 | 3/3 | 14 / 10 / 10 | up, 559 MB flat |

Then a longer watch on round 3, because the old failure was a death three to four minutes AFTER a
run rather than during one: **still up at 6.5 minutes, memory flat at 559 MB the whole way.**

And the counters that used to be the symptom are gone. The last two session logs are 177 kB of
start-up header each and carry **zero** `minidump id`, **zero** `DestroyPlatformEvent` and **zero**
`OMAutoClasses` lines - after 34 accessors were generated in them. Compare the sessions earlier the
same day: 14 to 21 minidumps and 25 to 38 `DestroyPlatformEvent` failures apiece.

So `DestroyPlatformEvent` was never the leak this document spent so long treating it as. It was a
symptom of the same regression, and it disappeared with it.

**That "zero faults" claim is true of the measurement window and NOT of the day - correcting it
here rather than leaving it to flatter the fix.** The instance those three rounds ran in went on to
live about **seventy minutes** and then died anyway, idle, with `OMAutoClasses` twice and three
minidumps. A restart after that died **thirty seconds** after the service reported ready, again with
nothing asked of it.

So there are two problems and only one of them was ours. `tidyProject` was real, reproducible and is
fixed - the A/B/A settles that. Underneath it sits a second, independent fault in NI's addon that
needs no input at all, kills LabVIEW anywhere between thirty seconds and an hour after start-up, and
always logs `OMAutoClasses` inside `LV AI Core.lvlibp:VI generator.vi`. Nothing in this repository
can fix that one, and no amount of restarting works around it: the service window has been shorter
than a client round trip.

## SETTLED: an unknown VI Server class in ValidateAIXML terminates LabVIEW

Eight cold rounds on 2026-08-26 closed this, and the answer is the hypothesis this document opened
with - which had since been doubted twice and talked out of. `docs/ni-bug-validateaixml-crash.md` is
the write-up to send NI; the short version:

**Validating an AIXML document whose `Invoke Node` names a VI Server class the addon's registry does
not contain kills the process.** A two-node file is enough. The same shape with a catalogued class
(`{LV.VI}` / `Run VI`) validated three times in a row, `errorCode 0` each time, and left LabVIEW
untouched; one validate naming `{LV.ProjectItem}` took it down mid-request.

The rounds that rule out everything else:

| round | after the service reported ready | outcome |
|---|---|---|
| 1-3 | project opened, 34 accessors generated, helper CACHED so no validate ran | alive 7 min, 0 faults |
| 4 | validated `{LV.ProjectItem}` files | dead in 15 s |
| 5 | waited 20 s, then the same | dead in 15 s |
| 6 | project opened, nothing else | alive 4 min, 0 faults |
| 7 | nothing at all | alive 3 min, 0 faults |
| 8 | port pinned so no discovery ran, then the same validates | dead in 15 s |

So it is not the project being open, not elapsed time, not port discovery, and not generating VIs.
Two hypotheses died on the way and are recorded so they are not raised again: rounds 4 and 5 looked
like "LabVIEW dies unless a project is open" until round 7 survived with no project, and looked like
"the CLI's port scan is the trigger" until round 8 died with the port pinned.

**Three consequences for working here.**

- **A generated helper is validated once and then cached.** That is why rounds 1-3 generated 34 VIs
  without a single fault. Do not clear `%TEMP%\LabVIEWMCP\helpers\` unless the AIXML changed.
- **Probing an uncatalogued class is not possible on this build.** The technique that found
  `{LV.Project}`'s `Save` and `Close` costs one LabVIEW per attempt now. The `{LV.ProjectItem}`
  removal method has to come from Context Help or NI, not from a probe.
- **The earlier "it needs no input from us" conclusion was wrong**, and so was blaming NI for
  something unprovoked. It always had a provocation; it just was not the one being looked for.

## MCP ONLY: three more cold rounds, and a `DestroyPlatformEvent` that is NOT a fault

The rounds above were driven partly over the CLI. Asked for afterwards: the same three cold cycles
with **every** LabVIEW call going through the MCP channel and nothing else touching the service, to
settle whether mixing the two transports was itself a factor. Seven MCP calls per round - stop, reset,
`lvai_ensure_labview`, `lvai_open_file`, then `lvai_create_accessors` sliced 3+2+2 for `Auto` and 3+2
for `Bus` and `Rennauto`.

| round | slices ok | Auto | Bus | Rennauto | `" 2"` duplicates | `.lvproj` VI items | LabVIEW after |
|---|---|---|---|---|---|---|---|
| 1 | 7/7 | 14 / 14 | 10 / 10 | 10 / 10 | 0 | 0 | up, 577 MB |
| 2 | 7/7 | 14 / 14 | 10 / 10 | 10 / 10 | 0 | 0 | up, 571 MB |
| 3 | 7/7 | 14 / 14 | 10 / 10 | 10 / 10 | 0 | 0 | up, 679 MB |

VIs on disk / members in the class file, and they agreed every time. **No call timed out in any
round** - the slicing plus the per-field save is what bought that. The resume chain reported itself
correctly throughout: `membersBefore 0 -> membersAfter 6, nextFromField 3`, then `6 -> 10, next 5`,
then `10 -> 14, next 7`, at which point `nextFromField` equals `fieldCount` and the class is done.

**Round 3's log carried 1 minidump and 2 `DestroyPlatformEvent failed with MgErr 42` - and LabVIEW
was fine.** That matters because this document spent a long time treating those two counters as the
symptom, and the section above ends on "zero minidump, zero DestroyPlatformEvent". A non-zero count is
not a contradiction of that and not a fault:

```
RTSetCleanupProc: leaf and root VIs in different contexts. arg=0x8e100003; (Queue)
DPrintfCallChain: RTSetCleanupProc: leaf and root VIs in different contexts.
   [LinkIdentity "MemberVICreation.lvlib:CLSUIP_CreateNewAccessor.vi" [NI.LV.Editor] ...
   [LinkIdentity "lvai_create_accessors.vi"                          [NI.LV.Editor] ...
<DEBUG_OUTPUT>
DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42.
source\ThEvent.cpp(213) : DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42.
[ExecSys:0; NOT InExec]
minidump id: 704dd41b-...
</DEBUG_OUTPUT>
```

Three things in that block say "warning, not crash", and they are worth being able to recognise:

- **`[ExecSys:0; NOT InExec]` and `No VI call stack`.** Nothing was executing. This fires from
  reference teardown, not from a running diagram - unlike the `OMAutoClasses` signature, which always
  arrives with `[Executing: "LV AI Core.lvlibp:VI generator.vi"]`.
- **LabVIEW kept logging afterwards**, and went on to answer every remaining call. A fatal DWarn is
  the last thing in the file.
- **`leaf and root VIs in different contexts`** names the cause exactly, and it is ours by design:
  the helper runs in the addon's context and holds a `Queue` and an `LV Application Reference`
  belonging to the IDE's. Cleaning a refnum across that boundary logs one of these. Two references,
  two warnings.

So the counter to watch is **`OMAutoClasses`**, and the tell for a real fault is the `Executing:` line
beside it. `DestroyPlatformEvent` at single digits with `RTSetCleanupProc` above it is the cross-context
cleanup the accessor route cannot avoid, and 25-38 of them per session was the regression - the
number, not the presence.

### Rounds 4 and 5: the same result, and the FIRST timeout of the series

Asked for after the three above, same procedure. Both rounds landed on the same numbers - `Auto`
14 VIs and 14 members, `Bus` and `Rennauto` 10 and 10, no `" 2"` duplicates, no VI items left in the
`.lvproj`, and round 5 additionally checked for dangling members (listed in a `.lvclass`, absent from
disk): **0**. Both session logs are clean: zero `OMAutoClasses`, zero `minidump id`, zero
`DestroyPlatformEvent`, zero `Executing:`.

**Round 4's first call timed out, and the recovery protocol worked exactly as written.** `Auto` fields
0-2, the 3-field slice, came back `Request timed out` - the first in 35 slices. What followed is the
useful part:

| step | observation |
|---|---|
| immediately after the timeout | `lvai_describe_class` read **4** members - the helper was mid-field |
| polling the class file for 60 s | reached **6 VIs / 6 members** and stopped there |
| `Get-Process LabVIEW` at that moment | **`Responding = False`** |
| `lvai_status` at the same moment | answered normally, all three services listed |
| 30 s later | `Responding = True`, and the next slice ran in full |

Two things worth keeping from that:

- **The timed-out call finished all three fields.** The client gave up; the helper did not. Resuming
  from `memberCount / 2` was correct and produced no duplicate and no gap. Do not retry the slice
  that timed out.
- **`Responding = False` on its own is not the wedge.** It means the UI thread is not pumping
  messages, which a long scripting operation does routinely. The discriminator is `lvai_status`: if
  the service answers, LabVIEW is working, not stuck. Reading `Responding` alone would have called
  this a hang and thrown away a healthy instance - and a `MainWindowTitle` of `LabVIEW` with no extra
  top-level window confirmed there was no modal behind it.

**Poll the class FILE, not LabVIEW, while a timed-out call is still in flight.** It costs nothing,
needs no service, and firing a second `lvai_create_accessors` into a still-running helper is the
concurrent-access case that returns `errorCode -1 unreachable`. Six samples ten seconds apart showed
the count settle, which is the signal that the helper has finished.

## The class-run instability is in NI's GENERATOR, not in our sequencing

**Captured 2026-08-28, and it closed a day of wrong guesses.** A two-class `lvai_create_class` run
took LabVIEW down; the process was gone rather than hung, so NI's handler wrote a log, and copying
`_cur.txt` before the restart caught this:

```
VI call stack:
- LV AI Core.lvlibp:VI generator.vi
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:ConvertAIXMLToVI.vi
- LV AI gRPC Service.lvlibp:LVAI.lvclass:Start Sync.vi
```

**Count the whole log rather than reading one dump.** It holds **12** stack dumps, and their VI call
stacks are `ConvertAIXMLToVI.vi` six times and `ValidateAIXML.vi` six times - nothing else. Every
logged warning in the file comes from AIXML generation; none from the class providers, none from
project code.

**And ignore `mxLvProvider`, which the first version of this section led with.** It is the last frame
of 4 of the 12 native stacks and absent from the other 8, so it cannot be the fault site; it is the
OUTERMOST frame, the entry point of the call chain, and the provider framework is loaded in the IDE
at all times. Reading it as evidence was pattern-matching against the day's topic. The module names
that appear at all - `mxLvProvider`, `nierclient`, `mgcore_SH_`, `sentry`, `lvMax` - are the ambient
LabVIEW ones.

All day, from that same VI:

```
source\panel\HeapObjMapImpl.cpp(226) : DWarn 0xBB613420:
    trying to override with non-reserved UID, request: 10 res: 0 max: 42 sat: 42
[ExecSys:0; Executing:"[VI "LV AI Core.lvlibp:VI generator.vi"]"]
```

So everything this log records happens in **AIXML generation** — validate and convert alike. Class
creation meets it because every class generates a carrier VI; nothing about it is specific to
classes.

Two limits on that, both worth keeping in view. These are DWarn entries with minidumps, i.e.
WARNINGS: whether the process death is the same event is not established, and this document already
shows elsewhere that minidumps count generation, not faults. And the carrier conversion that the
last entry names **reported success** — `errorCode 0`, the .vi written, the provider then running
for another three seconds. The crash came at the close afterwards.

**Why this matters more than the signature itself: it explains four failed diagnoses.** Over one
day the class-run instability was blamed on a stale in-memory project, on wiring `New Class Owner`,
on regenerating the helper, and on cold LabVIEW starts. Each was refuted by the next measurement,
because every one of them was a theory about *our* call sequence. The fault is a level below that,
in code we only call. Symptoms that survive every change to your own ordering are evidence that the
ordering is not the subject.

**What it does NOT excuse.** The same day's work made the failure harmless to the deliverable:
`lvai_create_class` now does its LabVIEW work in a throwaway project, so a crash mid-run leaves the
user's `.lvproj` untouched and the finished `.lvclass` files complete. Measured on the very run that
produced the stack above - LabVIEW died, and both classes plus the project came out correct.

## A FIFTH signature: `HeapObjMapImpl.cpp(226)`, and our own low uids are implicated

Measured 2026-08-31 while trying to script a DQMH event. LabVIEW died and restarted (PID changed);
the saved `_cur.txt` ends with a stack whose VI call stack is unambiguous about the *site*:

```
VI call stack:
- LV AI Core.lvlibp:VI generator.vi
- LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:ConvertAIXMLToVI.vi
- ...:LVAI.lvclass:Start Sync.vi
```

So it fell over **generating a VI from AIXML**, not inside the third-party scripting VIs that the
session was actually driving. Earlier in the same log, repeatedly:

```
source\panel\HeapObjMapImpl.cpp(226) : DWarn 0xBB613420:
    trying to override with non-reserved UID, request: 10 res: 0 max: 42 sat: 42
[ExecSys:0; Executing:"[VI "LV AI Core.lvlibp:VI generator.vi"]"]
```

**`request: 10` and `request: 11` are OUR uid values** — every helper in `scripts/` numbers its
first front-panel controls `uid="10"`, `uid="11"`. LabVIEW is saying those collide with UIDs it
reserves in the panel heap. The warnings are emitted on ordinary, *successful* generations too, so
they are not by themselves a fault; but they establish that the generator is being asked to place
objects at UIDs it did not want to give out, and the fatal stack is in that same code.

**This is correlation, not a demonstrated cause**, and two things argue against jumping to
"renumber everything": those same uids have generated hundreds of working VIs in this repository,
and the AIXML that preceded this crash was also the most unusual ever fed to the generator — a
single `Constant` whose `type` spelled out a nineteen-field cluster containing `ref{LV.Library}`,
`ref{LV.Application}` and eleven `ref{LV.ProjectItem}` fields. That is the same *kind* of input as
the settled `OMAutoClasses` signature above: **AIXML naming VI Server classes, parsed by the
generator.** Either or both may be responsible.

What to take from it, pending a controlled test:

- **A refnum-typed constant is not a free construct.** If AIXML needs `ref{LV.*}` inside a
  `type=`, treat that file as risky input and do not run it in a session holding work you cannot
  lose. Prefer a route that never has to name the type — carrying a value as a **variant** from one
  `Ctrl Val.Get` straight into one `Ctrl Val.Set` does exactly that, and is why the module helper
  (`scripts/lvdqmh_new_module.xml`) never needed such a constant.
- **Validation passing says nothing here.** The file validated in 2.5 s and generated with
  `errorCode 0`; the death came later.
- The restarted LabVIEW comes back **without the gRPC service** — it starts with Nigel, not the
  IDE — so a crash mid-session costs a manual step before any `lvai_*` call works again.

### The fifth signature again, 2026-09-01 — and it REFUTES the refnum half of the hypothesis above

Same site, same DWarn, in a run that had nothing unusual in it. LabVIEW died during the second
`lvai_create_class` of a plain four-field class — `string`, `double`, `int32`, `bool` — whose carrier
VI is the most ordinary AIXML this server generates. **No `ref{LV.*}` anywhere.** So the section
above offers two candidates, "our own low uids" and "AIXML naming VI Server classes", and this run
rules the second one out as *necessary*: the crash happens without it.

What the log adds, all read back from the preserved copy rather than from the agent's report:

- **Twelve occurrences of the DWarn in one session, escalating monotonically**:
  `request: 11 res: 0 max: 55` → `request: 12 … max: 69` → `request: 13 … max: 83`. `request`
  increments by one and `max` by **fourteen** each time. Something accumulates per generation inside
  one LabVIEW session, and the request value climbs with it — which fits "our uid numbering collides
  with a reserved range that grows" better than anything about the input.
- **`OMAutoClasses` appears ZERO times** in that 10 657-line log, so this is cleanly a different
  mechanism from the settled `ValidateAIXML` signature and not a second face of it.
- **The fatal site is `ConvertAIXMLToVI`, not `ValidateAIXML`** — as in the 2026-08-31 measurement.
  Nine seconds earlier the *first* `lvai_create_class` had failed with `Error 1018 (0x3FA)
  Unspecified error … Method Name: Get Errors` inside `ValidateAIXML.vi`, and an identical retry
  succeeded. Plausibly the same heap damage one stage earlier; not established.

**AND THE TOOL REPORTED `ok: true` FOR THE CALL DURING WHICH LabVIEW DIED.** That is the part to
carry away. `lvai_create_class` wrote the class file, verified it from disk, and answered `ok: true`
with a real `classPath`; only its final `closeScratchProject` step noticed the service had gone. The
class was genuinely written — `memberCount: 0`, four fields, sound — so the answer was not false. But
**a successful answer is not evidence that LabVIEW survived the call**, and a caller that reads `ok`
and moves on will make its next call into a dead service and see a port-discovery error with no
connection to the cause. Check the process, not the answer, when anything downstream misbehaves.

The practical rule is unchanged and now better founded: a crash mid-session costs the accessors, not
the class. Restarting and calling `lvai_create_accessors` again resumed from `fromField: 0` and
finished all eight in one call — nothing had to be rebuilt.

## What it means for working here

**Validation is not free of risk, and that is new.** `lvai_validate_aixml` has been treated
throughout this repository as the cheap, safe failure path — "it is cheap and its messages name the
node and terminal", and probing an undocumented VI Server name with it "turned the guess into a
measurement". That is still the right technique, but it is now known to be able to take LabVIEW down
when the AIXML names a class the generator's automation list cannot resolve.

Practical consequences:

- **A generated helper is validated ONCE and then cached.** `lvai_create_accessors`,
  `lvai_close_vi` and the rest keep their helper VI under `%TEMP%\LabVIEWMCP\helpers\`. Do not delete
  it to force a rebuild unless the AIXML actually changed — a development loop that regenerates on
  every iteration pays the validation risk on every iteration, which is exactly how three
  disappearances happened in one session.
- **Copy `_cur.txt` before restarting LabVIEW**, or the evidence is gone.
- **Run a process watcher during a long session.** A 5-second sampler writing `UP pid=… ws=…` to a
  file is what pinned the death to a 35-second window and made the log timestamps meaningful. Sample
  defensively: reading `$p.Threads.Count` on a process that exits mid-loop kills the watcher itself,
  which happened first time round.
- **An empty Windows event log is not an alibi.** Neither for LabVIEW nor for anything else that
  installs its own crash handler.
