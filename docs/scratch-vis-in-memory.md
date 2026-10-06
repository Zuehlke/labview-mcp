# Scratch VIs left in LabVIEW's memory - where they are, and what can be done

The user's report of 2026-09-26: exiting LabVIEW after a day of builds raised **"Save changes?
(Exit)" with 54 affected items**, every one an unsaved `LVMCP Validate <hash>.vi` except a single
`LVMCP Convert <hash>.vi`. Those are the scratch `_name`s our tools hand to NI's `ValidateAIXML` and
`ConvertAIXMLToVI` so that a refusal cannot burn a document's real name (`Infra/ValidationScratch.cs`).
NI's generator keeps a VI of that name in memory, and nothing released them.

## 1. They cannot be closed afterwards - measured

Probed by name with `Open VI Reference` (a STRING wired to `vi path` - a path made of a bare name is
resolved beside the calling VI instead and answers `Error 7`), in every application instance a
generated helper can reach:

| instance | how it is reached | positive control | scratch VI by name |
|---|---|---|---|
| the helpers' own (the addon's) | `Open VI Reference` unwired, or `Open Application Reference` with no machine name | the running helper `lvai_run_and_read_typed.vi`: found | `1004`, not in memory |
| the IDE's main instance | `Open Application Reference` `localhost`, VI Server over TCP (`server.tcp.enabled=True` in this station's LabVIEW.ini) | a VI opened loose with `lvai_open_file`: found, panel open | `1004` |
| the active project's | `Project:Active Project` -> `Application` | - | `1004`, for a type refusal made while that project was active |

Probed names: failed validates of both kinds (a type refusal and an `Error 53` `Unsupported
SubVI`), with and without a project active, and a successful one. **None was found anywhere.**
`Application:All VIs In Memory` is no help: in the helpers' instance it lists 419 library VIs and
not even the running helper, and over TCP it answers `Error 1032`.

**The converter does not see them either.** Its `1051` ("a file of that name already exists in
memory") is the only other oracle, and a convert under a scratch name straight after a failed
validate of that name - type refusal and `Error 53` alike - answered `errorCode 0`. So the
leftovers sit in a context of NI's own that neither VI Server nor the converter's name check
reaches. The 2026-09-09 measurement of a validate burning a name (`LVMCP Poison Probe.vi`, recorded
in `Infra/ValidationScratch.cs`) did not reproduce on this LabVIEW; that is recorded, not explained.

## 2. What was changed: ONE validation name instead of one per call

`ValidationScratch` now hands every VALIDATION the fixed name `LVMCP Validate.vi`; a CONVERSION keeps
a unique `LVMCP Convert <hash>.vi`, because a failed convert is measured to burn its name for the
next convert. Measured before the change, on one name:

| sequence under `LVMCP Validate Fixed.vi` | answer |
|---|---|
| type refusal, the same again | the same refusal both times |
| a sound document | `errorCode 0` |
| `Unsupported SubVI` (`Error 53`) | correct message |
| a sound document | `errorCode 0` |
| convert after a type refusal, and after an `Error 53` | `errorCode 0` both, no `1051` |

So a reused validation name costs nothing measurable. **What it buys is NOT confirmed yet**: the
exit dialog is the only place the leftovers are visible, so whether it now lists one validation VI
instead of one per call needs one LabVIEW exit after a working session. Until then, the dialog's
**"Don't Save - All"** is safe: nothing of value is in any `LVMCP Validate` / `LVMCP Convert` VI.

## 2a. The fixed name does NOT keep it to one VI - measured 2026-10-06

The open question of section 2 has an answer, and it is no. After one working day on a LabVIEW
started at 16:36 - several cold DQMH builds, an ATM build and the frame-tool development, with
many validations refused as `Unsupported SubVI` on the loaded-subVI route - the user's IDE showed
an **Error list with 17 errors**, every item starting `L…`, and could not reach it.

The LabVIEW process's own window list (Win32 `EnumWindows` filtered on LabVIEW's process id,
class `LVDChild`) named them: **`LVMCP Validate.vi` and `LVMCP Validate2` … `LVMCP Validate12`**,
each with a Front Panel and a Block Diagram window, every title ending ` *` (unsaved) - 13 VIs.
So **LabVIEW gives a new VI whose name is already in memory a NUMBER**, and the fixed name only
moved the pile from `<hash>` suffixes to `2`…`12`. Not every validation left one - the day had far
more than 13 - and which ones stay is not established; the refused ones are the likely candidates.
No VI of the user's projects was among them: the ATM's Main.vi, tester and GUI read `execState 1`.

**Every one of those windows, and the Error list and an `Externally Changed Files List` dialog
beside them, reported `IsWindowVisible` FALSE** while the user saw the two dialogs on screen and
could not click them. So an enumeration that filters on visibility - `DqmhTools.Win32.FindWindow`,
the save-dialog watch, `scripts`' window probes - would not have found them. Closing the two
dialogs was a `WM_CLOSE` posted to their handles (the Close button's message), confirmed gone with
`IsWindow`. **Do not do that to the scratch VIs' windows**: closing an unsaved VI's window raises
LabVIEW's save prompt, a modal that stops the whole gRPC service.

What follows for practice is unchanged: the leftovers are harmless, cannot be closed through VI
Server (section 1), and **"Don't Save - All" at LabVIEW's exit** removes them. Expect the Error
list to show them after a long session; read the names before suspecting the user's code.

## 3. What CAN be closed: a VI opened loose in the IDE

Measured the same day: `Open Application Reference` `localhost` -> `Open VI Reference` by name ->
`{LV.VI}` `FP.Close` -> close both references removed a loose VI from the main instance (the probe
answered `1004` afterwards). `lvai_close_vi` reaches only a project member through the project's
instance, so this is the route for a VI opened with `lvai_open_file` and no project. It needs VI
Server over TCP enabled for localhost, which is a station setting; it is not a tool yet.
