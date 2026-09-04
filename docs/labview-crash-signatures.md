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

## A SIXTH mechanism: a SPIN, not a crash — and the accessor run's leftovers are implicated

Measured 2026-09-02. `lvai_create_class` was called against a LabVIEW that had started four minutes
earlier and had already completed a `lvai_create_accessors` run for another class. **The call never
returned.** LabVIEW went `Responding = False` and held a steady **~21 % of one core for seven
minutes** while the target directory stayed empty. Killing it and repeating the identical call after
`lvai_ensure_labview` answered `ok: true` in **15.4 s**.

**This is NOT the documented false alarm, and the difference is what to check.** A call made while
the UI thread is merely busy with `Save All This Library` still *answers*, with `ok: false`,
`classIndex: -1` and a port sweep full of `DeadlineExceeded`. This produced **no answer at all and
no file**. `lvai_status` reported the port `DeadlineExceeded`, which its own message reads as "hung,
kill it", and that reading was right.

**The diagnostic that settles it, and the reason a single CPU sample misleads:** sample CPU **twice**
*and* watch the target directory. Rising CPU reads exactly like progress — it is what a long
`Save All This Library` looks like. **Zero bytes on disk after seven minutes is what makes it a
spin.** Neither signal alone is enough.

**What the log implicates, short of a mechanism.** That one instance's `_cur.txt` had grown to 10 820
lines and carried, besides two `minidump id` lines and six of the `HeapObjMapImpl.cpp(226)` DWarns
recorded above, **page after page of `RTSetCleanupProc: leaf and root VIs in different contexts`**
with call chains ending in `MemberVICreation.lvlib:CLSUIP_CreateNewAccessor.vi` and
`lvai_create_accessors.vi`. So the instance was carrying leftover state from the accessor wizard when
the next generation spun.

**The practical rule this yields, and its limits.** `docs/labview-lunit-testing.md` records that a
*subject* class's `Error 1562` lock costs nothing, and that stands — a lock is not this. But **the
accessor RUN's leftovers are not free**, and on this evidence a restart after `lvai_create_accessors`
would have saved **450 s of a 722 s run**. One observation, and the mechanism is not established; the
`RTSetCleanupProc` correlation is suggestive and no more. Treat it as: **after a long accessor run,
a restart before the next generation is cheap insurance at ~30 s against a seven-minute spin.**

That is also the first time in this series that the restart budget went to two rather than one, and
the second one was recovery rather than the mandated `1562` restart.

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

## A pylabview edit produces NO DWarn entries — and that is not the same as being safe

Measured 2026-09-03, prompted by the question "do we get these DWarns when we change files with
pylabview too?".

**The measurement.** LabVIEW restarted so the counter began at zero (it saturates at 200, so a delta
cannot be read from a session that already flooded). Baseline `dwarnCount: 0`. Then a full
`pylv_apply` cycle on one VI — close active project, extract, `conpane` 4833 -> 4815, rebuild, and
the verify step in which **LabVIEW itself loads and exports the rebuilt file**. Afterwards, read from
the log file rather than from the tool: **0 DWarn lines, 0 minidump ids**, new log started 19:06:00.

| route | what it touches | DWarn in one run |
|---|---|---|
| `lvai_swap_subvis` -> `{LV.SubVI}` `Replace` | LabVIEW's LIVE object heap, in memory | **>= 200**, all `HeapObjMapImpl.cpp(226)` |
| `pylv_apply` -> rebuild -> LabVIEW opens the file | a FILE on disk, LabVIEW not watching | **0** |

**Why that shape is expected.** `HeapObjMapImpl` is the map of objects in a loaded VI's heap, and the
signature is `trying to override with non-reserved UID` — a UID handed out that the map had not
reserved. Scripting deletes and recreates heap objects in a running process, which is exactly where
that bookkeeping can drift. pylabview never enters that path: it writes bytes while LabVIEW is not
looking, and LabVIEW then performs an ordinary file open. This explains the measurement; it does not
prove it, and the mechanism is in NI's source.

**THE COUNTER IS NOT A WARNING SYSTEM FOR THE PYLABVIEW ROUTE, and reading it as one would be worse
than not reading it.** `docs/connector-pane-repair.md` records `--reindex` and `--follow` **killing
LabVIEW on load, twice** — `LabVIEW.exe` gone from the process table, with dozens of `--pattern`
changes, retargets and comment placements passing untouched in between. So the two routes appear to
fail in different shapes:

- **LabVIEW scripting degrades gradually** — DWarns accumulate, the process keeps answering, and a
  crash may follow. The count is a real signal, and `dwarnCountSaturated` says when it is a floor.
- **A pylabview file fails all-or-nothing** — it loads correctly, or the process is gone. There is
  nothing to count and no warning to read.

**Scope of this measurement, stated so it is not over-quoted:** ONE operation (`conpane`) on ONE
small VI. The two operations known to kill are now refused by the script, so they cannot be
re-measured, and re-deriving them would mean more crashes on a working station to learn nothing new.

## NARROWED, 2026-09-03: the DWarn is emitted PER AIXML OBJECT, and a high `uid` avoids it entirely

Prompted by "can you narrow down when these DWarns occur?". Measured in one fresh LabVIEW, reading
the log file after each step rather than the tool's count.

| step | DWarn lines | delta |
|---|---|---|
| fresh LabVIEW, baseline | 0 | — |
| full `pylv_apply` cycle **incl. LabVIEW loading the rebuilt file** | 0 | **+0** |
| `lvai_generate_vi`, AIXML with `uid` 10 / 20 / 30 | 12 | **+12** |
| `lvai_validate_aixml` alone, same file | 18 | **+6** |
| `lvai_validate_aixml` alone, `uid` 9010 / 9020 / 9030 | 18 | **+0** |
| `lvai_generate_vi`, `uid` 9010 / 9020 / 9030 | 18 | **+0** |

**The law: two DWarns per uid-bearing AIXML object, per pass.** Three objects gave 6 in validate and
6 in convert. `lvai_generate_vi` runs both, so it costs **four per object**.

**`request:` IS THE `uid` FROM THE FILE, literally.** The three entries read `request: 10`,
`request: 20`, `request: 30` — the exact `uid` values in that AIXML — against `max: 42`, `56`, `67`.
LabVIEW reserves a UID range in the panel heap and our numbers sit inside it. Raising them above the
ceiling produced **zero** entries in both validate and convert.

This confirms the "our own low uids" hypothesis from the fifth-signature section with a direct
dose-response, and it settles the alternative: no unusual input is needed, only a low `uid`.

### Two things this corrects

**The count is not a health signal — it is a measure of how much AIXML was generated.** Saturating at
200 needs about **50 objects**, i.e. two or three generated test VIs. So `looksDegraded` derived from
`dwarnCount` will fire on any real run and means nothing about health. `dwarnCountSaturated` at least
stops the number being quoted as a magnitude.

**"The flood came at the failing swap" was WRONG**, and it was written here from an agent's report
plus a coincidence in timing. Run 9's 36 -> 200 happened across a `lvai_generate_class_test` call
that generates eight socket VIs plus a test VI from AIXML — 36+ objects, hundreds of DWarns by the
law above. The failing `Replace` was concurrent, not causal. `lvai_swap_subvis` drives `Replace`,
which is not an AIXML path at all.

### Why the pylabview route reads zero

It never converts AIXML. `pylv_apply`'s verify step goes the other way — `ConvertVIToAIXML` — and
LabVIEW opening a rebuilt file is an ordinary file load. Nothing asks the panel heap for a UID.

### The fix that is implied, and why it is not applied here

Renumbering generated AIXML to a high base (thousands) would remove the entries entirely. That is a
real change and touches more than one place: every helper in `scripts/`, the uids the tools emit, and
anything that READS a uid — `pylv-place-labels.py` anchors comments by uid, and
`lvai_placeholder_subvi` hands back `uid="NN"` for the caller to fill in. Whether removing the
collision also removes the CRASHES is unknown: the fifth-signature sections record correlation only,
and the same low uids have produced hundreds of working VIs.

Scope: one small VI, one station, ceiling observed at 42-67 and known to grow with object count
(55 / 69 / 83 in an earlier log). Values in the thousands cleared it here; that is not a proof that
any particular base is always safe.

### The control that settles it: LabVIEW's OWN AIXML is silent

Asked directly — "do these only happen with OUR XML? run VI -> XML -> VI unchanged and see." Measured
2026-09-03, same session, continuing the table above from 18.

| step | DWarn lines | delta |
|---|---|---|
| `lvai_convert_vi_to_aixml` on `Probe Subject.vi` (VI -> XML) | 18 | **+0** |
| `lvai_convert_aixml_to_vi` on that export, unmodified (XML -> VI) | 18 | **+0** |

**Zero. The round trip is completely silent**, and the VI is the same three objects that produced 12
entries when built from hand-authored AIXML. So the trigger is NOT "AIXML is being processed" and not
the generator as such — it is the `uid` VALUES in the file.

**LabVIEW numbers one above its own ceiling, every time.** Its export of that VI reads:

```xml
<Control _name="value"  ... uid="43"/>
<Node    _name="Increment" ... uid="57"/>
<Indicator _name="result"  ... uid="68"/>
```

against the ceilings the DWarn reported for the same three objects — `max: 42`, `56`, `67`. That is
43 = 42+1, 57 = 56+1, 68 = 67+1. LabVIEW allocates immediately above the reserved range; our
`10 / 20 / 30` sit inside it, which is exactly what `trying to override with non-reserved UID` says.

**The practical rule this gives us, and it needs no guessing at a safe base:** number generated AIXML
the way LabVIEW's own export does. Every helper in `scripts/` currently starts at `uid="10"`, so every
helper run emits these entries — which is why an ordinary session accumulates them steadily and
saturates at 200 after about fifty objects.

**What it still does not establish** is whether the collision has anything to do with the crashes.
That remains correlation: the fatal stacks are in the same `VI generator.vi`, and these same low uids
have produced hundreds of working VIs.

### `uid="0"` IS the sentinel: reusable, silent, and LabVIEW numbers the object itself

Asked next — "what happens with uid 0 or -1? does LabVIEW assign a new one?" Measured 2026-09-03,
same session, continuing from 18.

| AIXML `uid` values | result | DWarn delta |
|---|---|---|
| `0 / 1 / 2` | generated | **+8** — four each from 1 and 2, **NONE from 0** |
| `-1 / -2 / -3` | **refused at validate** | — |
| `0 / 1 / 0` (0 reused) | generated, correct | **+4** — only from 1 |

**Negative is refused by the schema, and the message names the floor:**

```
value '-1' must be greater than or equal to minInclusive facet value '0'
```

So `0` is the schema's documented minimum, not an accident.

**`uid="0"` produces no DWarn, may be REUSED, and the object still gets a proper id.** The VI built
from `0 / 1 / 0` exports as:

```xml
<Control   _name="value"     ... outputs="value:43.value" uid="43"/>
<Node      _name="Increment" ... outputs="x+1:57.x+1"     uid="57"/>
<Indicator _name="result"    ... inputs="value:57.x+1"    uid="68"/>
```

— the same `43 / 57 / 68` LabVIEW assigns in its own export of the equivalent VI, with the wiring
intact and the two zero-uid objects given distinct numbers. So an AIXML `uid` is only a
**file-internal wiring reference**; LabVIEW allocates its own regardless, and `0` means "I am not
referenced, number me yourself".

**The rule this gives:**

- an object that **no net references** -> `uid="0"`, always silent whatever the heap ceiling is
- an object a net **must reference** -> a value above LabVIEW's reserved ceiling; `9010` was silent
  where `10` was not

`uid="0"` is the more robust half, because it skips the reservation check rather than trying to clear
a ceiling that GROWS with object count (42-67 here, 55-83 in an earlier log). A number chosen to be
"high enough" is a guess; zero is not.

**Not established:** whether removing the collision removes the CRASHES. Still correlation.

**An unrelated observation from the same probes, recorded so it is not lost:**
`lvai_run_vi_and_read_values` answered `Error 91` at `Control Value:Set` for a `double` input passed
as the string `"41"` — and did so **identically on a known-good VI**, so it is not about uids. Not
investigated further here.

### Does the fix hold on a REAL-SIZED VI? Yes — 42 objects, zero UID DWarns

The three-object probes above leave one doubt: the reserved ceiling GROWS with the object count
(42, 56, 67 for objects 1, 2, 3 — about +13 each), so a uid that clears it at three objects might not
at forty. Measured directly rather than extrapolated.

A generated 42-object VI — one control, forty chained `Increment` nodes, one indicator — with every
referenced object numbered from 9010 in steps of 10 (highest 9410) and the unreferenced sink at
`uid="0"`:

| | |
|---|---|
| `0xBB613420` (non-reserved UID) before | 30 |
| after generating the 42-object VI | **30** — **zero added** |
| extrapolated ceiling at object 42 | ~575, against a lowest uid of 9010 |

**So the answer to "will we still get these when we generate VIs ourselves?" is no, provided every
REFERENCED object is numbered high.** The sentinel alone is not enough: counted across all 39 helpers
in `scripts/`, **853 of 1 089 objects (78 %) are referenced by a net** and need a real uid; only 22 %
can take `uid="0"`.

**Pick the step with the ceiling's growth in mind.** Ours grows ~13 per object while a step of 10
grows slower, so the two converge — at about 3 000 objects on this arithmetic. No helper here comes
close (the largest, `lvai_create_accessors.xml`, has 99), but a step of 20 or a base of 100 000
removes the question rather than bounding it.

**A DIFFERENT signature appeared in the same window and is NOT this one:** two entries of
`ThEvent.cpp(213) : DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42`, marked
`[ExecSys:0; NOT InExec]`. That is the second failure mode already described in this document, it is
not a UID collision, and nothing here addresses it. It is named so the "zero" above is not read as
"the log went quiet".

### CORRECTION and the full model: a HIGH uid is KEPT, a low one is replaced

The section above states that "an AIXML `uid` is only a file-internal wiring reference; LabVIEW
allocates its own regardless". **That is true only BELOW the reserved ceiling.** Measured
2026-09-03 after Nigel's description of the schema prompted a duplicate-uid probe.

A VI authored with `9010 / 9020 / 9010` — the last a deliberate duplicate — generated with
`errorCode 0` and exported as:

```xml
<Control   _name="value"     ... outputs="value:9045.value" uid="9045"/>
<Node      _name="Increment" ... outputs="x+1:9020.x+1"     uid="9020"/>
<Indicator _name="result"    ... inputs="value:9020.x+1"    uid="9010"/>
```

**`9020` and `9010` came back verbatim.** Only the colliding one was renumbered, to `9045`. Compare
the low-uid and zero-uid probes, which came back as LabVIEW's own `43 / 57 / 68` every time.

**The model that fits all six probes:**

| what you write | what LabVIEW does | DWarn |
|---|---|---|
| `uid` **above** the reserved ceiling | **keeps it verbatim** | none |
| `uid` **inside** the ceiling, non-zero | warns, then reassigns to its own number | **2 per object per pass** |
| `uid="0"` | treats it as "unspecified", assigns silently; **may be reused** | none |
| a **duplicate** of any value | renumbers one of them, no error | per the rows above |
| **negative** | refused at validate, `minInclusive facet value '0'` | — |

So `trying to override with non-reserved UID` is literally what it says: we asked for a number inside
LabVIEW's reserved range, it declined, and it substituted its own. Nothing is lost — which is why
hundreds of VIs have been generated correctly while emitting these.

**A bonus of numbering high that was not the goal:** the export then carries the SAME uids as the
source AIXML, so a generated VI round-trips stably. Today every generated VI comes back renumbered,
which makes an AIXML diff across a regeneration noisier than it needs to be.

**On uniqueness.** The schema does not enforce it and the generator repairs a collision silently, so
a duplicate is not an error — but it IS a bad idea in authored AIXML: which of the two gets renumbered
is the generator's choice, so the file you wrote and the file you get back disagree in a way nothing
reports. Treat uniqueness as a rule we keep, not one that is checked. `uid="0"` is the deliberate
exception, because it asks for no number at all.

### There are TWO kinds of these DWarns, and only one of them is ours

Twelve VIs built and measured in one LabVIEW session, 2026-09-03, to answer "can we get them to
zero?". The running total, each step read from the log file:

| step | total | delta |
|---|---|---|
| fresh LabVIEW | 0 | — |
| full pylabview cycle incl. LabVIEW loading the file | 0 | +0 |
| generate, `uid` 10 / 20 / 30 | 12 | **+12** |
| validate only, same low uids | 18 | **+6** |
| validate and generate, `uid` 9010+ | 18 | +0 |
| generate, `uid` 0 / 1 / 2 | 26 | **+8** (none from `0`) |
| generate, `uid` 0 reused twice | 30 | **+4** (only from `1`) |
| 42-object VI, high uids, sink at `0` | 30 | +0 |
| duplicate-uid probe, arbitrary wire names | 30 | +0 |
| **first structured VI** — For Loop + indexing tunnel + shift register | 54 | **+24** |
| bare For Loop / tunnels only / shift register only | 54 | +0 |
| LabVIEW's own export of that VI, fed back | 54 | +0 |
| the same VI with every wire renamed | 54 | +0 |
| **the very file that produced the 24, re-run** | 54 | **+0** |

**Class 1 — ours, repeatable, avoidable.** A `uid` inside LabVIEW's reserved range costs two entries
per object per pass, on every generation. `request:` is our number literally. Numbering above the
ceiling, or `uid="0"`, removes it. This is the class the rest of this document is about.

**Class 2 — LabVIEW's own, one-off per session, NOT avoidable.** The 24 from the first structured VI
carried `request: 1, 1, 3, 10, 11, 20` — numbers that appear **nowhere in the AIXML**, whose uids were
all 9010-9100. They did not reappear when the identical file was generated again, nor for the
isolated parts. It is a first-use cost paid when a construct is first instantiated in a session, in
LabVIEW's own low-numbered internal objects.

**The wrong turn, recorded because it was two measurements away from being written down as fact.**
The first structured VI differed from the silent probes in its wire names, so "non-canonical wire
names cost DWarns" fitted every observation available at that moment. Two controls killed it:
renaming every wire in a silent VI added nothing, and re-running the guilty file added nothing. The
difference was WHEN it ran, not WHAT was in it. A single measurement of a stateful system cannot
distinguish a property of the input from a property of the session — re-run the same input before
believing either.

### What LabVIEW changes when it re-exports a VI you authored

Asked directly: does LabVIEW correct our uids? Measured on the structured VI, every uid high:

**It corrected none of them.** All nine came back verbatim - 9010, 9020, 9030, 9040, 9060, 9070,
9080, 9090, 9100, `uid_parent` relationships intact. What it rewrote was the **wire names**:

| authored | came back as |
|---|---|
| `9040.elem` | `9040.value` |
| `9060.sum` | `9060.x+y` |
| `9050.acc` | `9060.x` |
| `9090.total` | `9090.value` |

So LabVIEW's own convention is `<uid>.<terminal name>` using an endpoint's REAL terminal name - and
not always the producer's: the shift register's `Left` output became `9060.x`, named after the
CONSUMING node's uid and input terminal. Nothing depends on this (a wire name is an arbitrary token),
but it is what an export will look like, so a diff of authored-versus-exported AIXML shows wire-name
churn that means nothing.

### CORRECTION: there is no "first-use cost" class, and generation CAN be silent

The section above concluded from the 24 entries that a second, unavoidable class exists — a one-off
cost when a construct is first instantiated. **That is wrong, and the reasoning behind it was
unsound: the +24 spanned a stretch in which SEVEN operations ran, and it was attributed to the one
at the end because that one was interesting.** Nothing was measured in between.

Re-run properly on 2026-09-04, LabVIEW restarted so the counter began at zero, every input replayed
with a measurement after each:

| operation, in order, in one fresh session | `0xBB613420` |
|---|---|
| baseline, nothing generated | 0 |
| **structured VI** — For Loop + indexing tunnel + shift register, all uids 9010+ | **0** |
| tunnel VI, uids 9010+ | 0 |
| 42-object chain, uids 9010+ with the sink at `uid="0"` | 0 |
| a Case-structure file that FAILS validation | 0 |
| `uid_parent="7777"`, resolving to nothing | 0 |
| a Ring whose `value` is outside its `values` | 0 |
| a `double` control with `value="hello"`, `Error 53` | 0 |

**Zero throughout, including the exact file that "produced" the 24.** So the answer to "can we
generate our own VIs without adding DWarn entries?" is **yes**, and it is now demonstrated rather
than inferred: eight operations, four of them generations, one of them structured, none silent by
luck.

**The condition is the one this document already establishes:** every `uid` above LabVIEW's reserved
ceiling, or `uid="0"` where nothing references the element. A low `uid` remains reliably costly —
twelve entries for a three-object VI, reproducible on demand.

**What produced the 24 is NOT established.** It required session state that the fresh replay does not
have, and the only structural difference is that the earlier session had generated several LOW-uid
VIs first. That is a hypothesis, not a measurement, and it is recorded as such. The `request:` values
there — 1, 1, 3, 10, 11, 20 against ceilings climbing 42 to 130 — belong to no element of the AIXML
being generated.

**The process lesson, which is the same one three times now.** A delta between two measurements is
evidence about the WHOLE interval, not about the operation you happened to be interested in. Both
wrong turns in this investigation had that shape: "non-canonical wire names cost DWarns", killed by
two controls, and "there is an unavoidable first-use class", killed by a restart and a replay. In a
stateful system, measure after every step or claim nothing about which step did it.

### OPEN: the uid rule does NOT hold for our own shipped helpers, and that is unexplained

Measured 2026-09-04 in one fresh session, counting after every step, while deciding whether
`scripts/*.xml` needs renumbering:

| what was generated or validated | uids | `0xBB613420` added |
|---|---|---|
| `subject.xml`, three objects | 10 / 20 / 30 | **12** |
| the same file with uids raised | 9010 / 9020 / 9030 | 0 |
| `lvai_run_and_read.xml` **regenerated by force** (27 objects) | from 10, controls at 10 and 11 | **0** |
| `lvai_run_and_read.xml` validated directly | same | **0** |
| `lvai_swap_subvis.xml` validated (65 objects) | from 10, `conIdx="0"` | **0** |

**So a low `uid` is not sufficient on its own.** Two of our shipped helpers carry front-panel controls
at `uid="10"` and `uid="11"` - exactly the values the earlier sections identify - and cost nothing,
while a three-element probe with `uid="10"` costs twelve, reproducibly, in the same session minutes
apart. `conIdx` is not the difference (both use `0`), nor object count (65 vs 3, the larger is the
silent one).

**What this changes practically:** nothing about the helpers under `scripts/`. They were measured
directly and they are silent, forced regeneration included, so renumbering 39 files buys nothing that
has been demonstrated. What still costs is the AIXML the TOOLS emit for VIs they generate on every
run - `PlaceholderTools`, `TestTools`, `MethodTestTools`, `DqmhTools` all write `uid="10".."13"` -
and those are small generated VIs of the same shape as the probe. Run 9's saturation appeared during
exactly such a call.

**What it changes about the RULE:** "a uid inside the reserved range costs two entries per object per
pass" is true of every probe measured here and false of two shipped helpers. The mechanism is not
established, so the rule should be applied as "measure the file you care about", not as a law. Do not
renumber anything on the strength of the rule alone; renumber, then measure the same file before and
after.

### Run 10, 2026-09-04: the uid DWarn is GONE from a real build, and what is left is a different fault

The first cold class build after `lvai_check_aixml` and its repair went in, instrumented with a
`dwarnCount` reading at EVERY phase boundary rather than at the ends - the fix for the reasoning
error that produced the retracted "first-use" claim.

**The headline, verified from the log file rather than from a tool:**

```
DWarn 0xBB613420  (trying to override with non-reserved UID):   0
DWarn 0xECE53844  (DestroyPlatformEvent failed with MgErr 42): 34
```

**Zero.** Two `lvai_create_class` calls, each converting a carrier VI from AIXML, 18 accessors, four
generated Caraya suites - and the signature this document spent two days characterising did not
appear once. The repair fired on every generation and reported exactly what it raised: 6 uids
(10-15 -> 4200-4250) for the base carrier, 3 for the child, 3 for each Caraya runner (10, 11, 40).

**So the answer to "when do they occur, and can we fix them" is: the uid ones occurred at AIXML->VI
conversion, and they are fixed.** Not mitigated - absent.

**What is left is NOT the same fault.** All 34 lines are `source\ThEvent.cpp(213)`, and
`dwarnCount` counts LINES while each event writes two, so 34 lines are **17 events**. Attribution
from the per-phase readings:

| phase | Δ events |
|---|---|
| project creation, both `lvai_create_class`, `lvai_bind_class_fields` | **0** |
| all five `lvai_create_accessors` slices | **0** |
| verify + `lvai_close_active_project` | **0** |
| a whole Caraya run of 7 tests | **0** |
| the two test agents' `lvai_generate_class_test` calls | 17 |

Fourteen of the seventeen carry **no VI attribution at all** (`[ExecSys:0; NOT InExec]`, no call
stack) and name `nierclient`/`sentry` frames - NI's own error-reporting client. They cluster around
`lvai_generate_class_test` at about +8 per call and do NOT scale with the number of test cases.

**Best current reading: `0xECE53844` is event-handle teardown in the helper machinery, roughly
proportional to how many distinct helper VIs a tool spins up and tears down.** It is not AIXML
conversion, not execution, and not the project close - the close in this run produced zero, and a
`lvai_describe_project` with no close anywhere near it produced +4 on its own.

**A mid-run hypothesis was formed and discarded**, which is worth recording because it is the same
trap: two events landed next to `lvai_close_active_project.vi` and were briefly read as caused by the
close. The later `describe_project` reading falsified it.

### The remaining source-side finding

**Our own shipped template AIXML carries reserved-range uids** - the class carrier and the Caraya
runner both - so the repair renumbers them on every single generation. It works, but the template and
the export disagree by construction. Renumbering the templates at source would make the repair a
no-op there. Note this is cosmetic rather than a DWarn fix: measured separately, our shipped HELPER
AIXML uses low uids and logs nothing, and why the two differ is still unexplained.

### Timing, and why it must not be read as a regression

| | run 1 | run 2 | run 9 | **run 10** |
|---|---|---|---|---|
| wall clock | 3 434 s | 1 787 s | 1 091 s | **1 441 s** |
| inside LabVIEW | 353 s | 362 s | 429 s | **~292 s** |
| ratio | 9.7 : 1 | 4.9 : 1 | 2.5 : 1 | **4.9 : 1** |
| calls | - | - | 67 | ~110 |
| tests delivered | - | - | 11 | **13** |

Run 10 is slower than run 9 and **two deliberate differences account for it**, so it is not evidence
that anything regressed:

- **The test agents ran SEQUENTIALLY** (955 s of the 1 441). In run 9 they ran in parallel and
  finished together. That is orchestration, not tooling.
- **The instrumentation cost about 60 s of pure turn latency for 0 s of LabVIEW** - eight
  `lvai_status` calls purely to read a counter. If this benchmark repeats, the honest fix is for
  mutating tools to return `dwarnBefore`/`dwarnAfter` themselves and delete the phase-boundary dance
  entirely.

### Run 12 settles the attribution: it is NI's OWN code, and generation is innocent

Run 12 measured every phase individually, icons included, in a LabVIEW started at zero. 36 new
lines = 18 events, every one `ThEvent.cpp(213) 0xECE53844`, **zero uid entries for the third run
running**.

| phase | VIs touched | Δ events |
|---|---|---|
| project file creation | — | 0 |
| `lvai_create_class` x2 (AIXML carrier + NI provider) | 2 | 2 |
| `lvai_bind_class_fields` | — | 1 |
| **`lvai_create_accessors`, 5 slices** | **18 created** | **12** |
| method authoring: close + 8 placeholders + 7 validates | 8 | 1 |
| `lvai_add_class_method` x2 | 4 | **0** |
| `lvai_swap_subvis`, 4 VIs | 4 | 1 |
| **icons, `lvai_set_vi_icon`** | **22 RE-SAVED** | **0** |
| `lvai_generate_class_test` x3, `generate_method_test`, runner | ~8 created | 1 |
| project edits, test runs, JUnit | — | 0 |
| **running the class's DAQmx methods top-level, no device** | — | **1** |

**ICONS ARE REFUTED, and they were the strongest remaining hypothesis.** A `lvai_set_vi_icon` call
draws a bitmap and does `Save.Instrument`; 22 of them in the class build and 6 more across the two
test agents re-saved 28 VIs and produced **nothing**. So it is not saving, not writing, not touching
a VI on disk.

**Generation is innocent too.** AIXML -> VI, placeholders, swaps, project edits, running a Caraya
suite: all zero. `lvai_add_class_method`, which converts four VIs and retypes their panes, produced
zero.

**What is left are two things, and both are NI's own code running, not ours:**

- **`lvai_create_accessors`** - 12 events for 18 accessor VIs, about 0.67 per VI, by far the largest
  single row. It drives `CLSUIP_CreateNewAccessor.vi`, NI's provider wizard, which loads and releases
  the whole class library per field.
- **Executing DAQmx with no device** - the only two events in run 12's second test agent came from
  running `Initialize`/`Start`/`Read`/`Close` top-level to observe their error codes. Not from
  generating them.

That fits the signature exactly: `DestroyPlatformEvent failed with MgErr 42` with
`[ExecSys:0; NOT InExec]` is LabVIEW failing to release an OS event handle during housekeeping, and
the two rows above are the phases where NI's own code churns the most handles.

**Can we fix it? No, and it does not need fixing.** Both producers are inside NI's code, reached
through the only route that exists for the job. LabVIEW never died in runs 10, 11 or 12, no restart
was needed, and `dwarnCountSaturated` stayed false in every one - the counter never approached its
200 floor. It is housekeeping noise.

**The one thing that IS ours: `looksDegraded` reacts to the count regardless of signature**, and read
`true` during run 11 purely because the number rose. A harmless `ThEvent` teardown and the
`OMAutoClasses` signature that precedes real deaths are not the same event, and the health answer
should not treat them alike.

### Two naming traps this run turned up

- **A class method called `Read.vi` hit `Error 1051`** - "a LabVIEW file of that name already exists
  in memory". Renaming it `Read Samples.vi` cleared it. Short generic member names - `Read`, `Write`,
  `Open`, `Close` - are risky in an environment that resolves by NAME; note that `Close.vi` in the
  same class did NOT collide, so this is about what happens to be loaded, not about a fixed list.
- **`lvai_status`'s `DeadlineExceeded` text says "LabVIEW is HUNG ... the process has to be killed".
  Under two concurrent agents that is wrong and dangerous**: `Responding` was `True` and the service
  answered normally 40 s later. `DeadlineExceeded` means BUSY when another agent holds the instance,
  and following the advice would kill the other agent's work.

### RETRACTED again: `lvai_create_accessors` is NOT the source either

The section above names `create_accessors` as the dominant producer, at about 0.67 events per
accessor VI, from run 12's per-phase reading of `dwarn=8 -> 32`. That reading was correctly isolated
and is not in doubt. **The conclusion drawn from it is wrong**, because the operation was never
tested on its own:

| probe, each in a quiet LabVIEW, counted immediately before and after | Δ |
|---|---|
| `lvai_create_class`, 4 fields, scratch project opened and closed | **0** |
| a second `lvai_create_class` in the same instance | **0** |
| **`lvai_create_accessors`, 8 accessor VIs in 2 slices, 31.8 s inside LabVIEW** | **0** |
| `lvai_generate_vi`, a helper run, a cached and a fresh placeholder | 0 |
| `lvai_describe_project`, `lvai_close_active_project` | 0 |
| icons: 28 VIs re-saved across a whole run | 0 |

**Nine operations, all zero.** So no single call produces these entries, `create_accessors`
included. What is established is only that they appear during long multi-step runs and not during
isolated probes; WHY is not established, and this document should stop naming a culprit until it is.

**The process failure is the same one three times, and it is worth naming precisely.** In each case
a delta was measured across an interval, the most interesting operation in that interval was named
as the cause, and the operation was never run on its own. A per-phase measurement is better than a
per-run one and it is still not an isolation - a phase contains everything else the session was
carrying. **The only thing that isolates is running the operation alone against a counter read
immediately before and after.** It costs two extra calls and it has now overturned three
conclusions.

What survives from all of it: the **uid** signature is genuinely fixed and has not reappeared in
three runs, and the remaining `ThEvent` signature is harmless - LabVIEW never died, no restart was
needed, and the counter never approached its floor in any of them.

### Run 13, and the end of the hunt: TEN operations isolated, all zero

Run 13 was measured per phase, with the largest-delta phase then re-run ALONE against a counter read
immediately before and after - the discipline that has now overturned every attribution this document
made.

| run 13 phase | Δ |
|---|---|
| Phase 0, both `create_class`, typedef bind | 0 |
| **18 accessors** | **0** |
| methods built (4) — *a test agent was spawned into the same instance mid-phase* | **32** |
| error codes observed | 0 |
| **22 icons** | **0** |

Then, with both test agents finished and nothing else touching LabVIEW:

| isolated call | Δ |
|---|---|
| `lvai_create_class`, 1 field | **0** |
| **`lvai_add_class_method`, full success, 2 terminals retyped, verified on disk** | **0** |

**The complete isolation table now stands at ten operations, every one zero:** `generate_vi`, a
helper run, a cached placeholder, a fresh placeholder written into `user.lib`, `describe_project`,
`close_active_project`, `create_class` (twice, with and without fields), `create_accessors` (8 VIs,
31.8 s inside LabVIEW), and `add_class_method`. Icons add a further 28 VI re-saves across two runs at
zero.

**So no operation this server performs produces these entries on its own.** Every phase attribution -
`generate_class_test`, `create_accessors`, `add_class_method` - has failed when the same call was run
alone. Three retractions, one method.

**What is NOT explained.** Run 12's accessor phase produced +24 with no other agent running, and run
13's identically-shaped accessor phase produced 0. Concurrency fits run 13's methods phase and fails
on run 12. No variable has been found that predicts which phases produce entries. That is the honest
state, and this document should carry it as an open question rather than a fourth culprit.

**What IS settled, and it is the part that matters:**

- The **uid** signature - the one that was ours, and the one that scaled with the AIXML we wrote - is
  **fixed**. Zero occurrences across runs 10, 11, 12 and 13.
- The remaining `ThEvent 0xECE53844 DestroyPlatformEvent` signature is **harmless**: `NOT InExec`, no
  VI call stack, NI's own frames. LabVIEW did not die once in four runs, no restart was needed, and
  the counter never approached its 200 floor.
- `looksDegraded` no longer counts it, so a clean run no longer reads as degraded. Confirmed in run
  13: 32 entries, all `ThEvent`, `looksDegraded: false`.

**The method, stated once for the next investigation.** A delta measured across an interval is
evidence about the interval, not about the operation you find interesting inside it. A per-phase
measurement is finer than a per-run one and is still an interval. The only thing that isolates is
running the operation alone, with a counter read immediately before and after. It costs two calls.
It has been decisive four times.

