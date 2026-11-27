# Save version: files for an older LabVIEW

A project kept for an older LabVIEW pins its save version in the `.lvproj`:
`<Property Name="NI.LV.All.SaveVersion" Type="Str">21.0</Property>` (or `Editor version`, which
means "the LabVIEW that saves it"). Two field projects worked that way - one at 21.0 for stations
running LabVIEW 2021, a driver project at 18.0 for LabVIEW 2018 - and both ranked the same gap
first: the tools saved files in the IDE's version, and the target LabVIEW found out only when it
loaded them, on the target PC (`VI version (26.0) is newer`, load error 9, the 21.0 project, 2026-10-30; nine
saving tools wrote 25.0 into 18.0 projects, the driver project).

## 1. What the server does (since 2026-11-13)

**Every tool answer is checked** (`Infra/SaveVersions.cs`, called from `DiagnosingTool`, which every
served tool passes). Before a call, the version and write time of each LabVIEW file its ARGUMENTS
name are read; after it, every LabVIEW file named in the arguments or the ANSWER that was written
during the call is read again - a file named before the call counts as written only when its write
time changed (since 2026-11-13: with the 2 s margin alone, a call made within 2 s of an icon save
was blamed for it), any other one when it was written after the call started, less 2 s. A FOLDER
the arguments name counts as naming the LabVIEW files under it, for their write times (since
2026-11-23: a dryRun over a folder, a second after another call saved its files, was blamed for them).
A COLLECTED answer (`jobStartedUtc`, a job that answered `stillRunning` first) is judged from the
job's start and the pin alone: the collecting call read its "before" after the job's writes. A file now saved newer than it was before, or newer than the version its project
pins, is reported:

```json
"saveVersionCheck": {
  "files": [ { "path": "...\\AddTwo.vi", "savedAs": "26.0", "was": "20.0",
               "projectSaveVersion": "20.0", "project": "...\\P20.lvproj" } ],
  "warning": "1 file(s) were saved by this call in a NEWER LabVIEW version than they had, ..."
}
```

Nothing is refused or undone there - the file is already written; the answer says so while it can
still be restored. An answer with nothing to report is not touched. The versions come from the
FILES, no LabVIEW: a resource file's `LVSR` block starts with the version word (`26008000` = 26.0,
`20008000` = 20.0, measured on files LabVIEW 2026 and 2020 wrote); an XML file carries it as the root
element's `LVVersion`. The pin that applies is the one of a project that lists the file, or lists the
library or class that owns it (`pinFrom: "listed"`); for a file no project lists yet, the composer's
folder rule below (`pinFrom: "folder"` - measured 2026-11-13: a new VI given its icon before it was
listed was reported with no pin at all). Files are recognised by extension (`.vi .ctl .vim .vit .ctt .lvclass
.lvlib .lvproj`); a call naming more than 300 is not checked.

**The composer writes the pinned version** (`AixmlComposer.PinnedTarget`), on the pylabview backend:
a VI a project lists takes that project's pin; a NEW VI takes the pin every project in its folder
and up to three folders above agrees on. The composer writes files for LabVIEW 2019 and later only (2020 until upstream #150, 2026-11-24),
so an older pin (18.0) is reported in `versionWarning` and the file is written in the newest version
it would otherwise use.

**`lvai_generate_vi` skips the steps that save through LabVIEW** for a VI older than the running
LabVIEW: the constant tidy, the panel and diagram clean-up and the icon each SAVE, and would
rewrite the file in the running LabVIEW's version. The answer has a `savingStepsSkipped` step and
says so in `note`; the diagram is then the composer's own layout. The size measurement still runs
(a render saves nothing).

**`lvai_describe_project`** answers `saveVersion` (the pin, or `Editor version`) and
`projectFileSavedBy` (the LabVIEW that last saved the `.lvproj`).

## 2. Measured 2026-11-13

A project pinned to `20.0`, LabVIEW 2026 Q3 running, pylabview backend, a branch build over raw
stdio (`mcp_temp`, not committed):

| step | result |
|---|---|
| `lvai_describe_project` | `saveVersion: "20.0"`, `projectFileSavedBy: "20.0"` |
| `lvai_generate_vi` of two VIs the project lists | both written as LabVIEW 20.0 files, `executable: true` in LabVIEW 2026, `savingStepsSkipped` |
| `lvai_run_vi_and_read_values` in LabVIEW 2026 | 2.5 + 40 = 42.5 |
| `lvai_set_vi_icon` on one of them | saved by LabVIEW 2026 as 26.0; `saveVersionCheck`: `was 20.0`, `savedAs 26.0`, `projectSaveVersion 20.0` |
| LabVIEW 2020: the other VI | `execState 1`, ran 42.5 |
| LabVIEW 2020: the one the icon tool re-saved | the reference never opened |

## 3. A project close in a newer LabVIEW rewrites the `.lvproj` (measured 2026-11-13)

A field project pinned to 21.0 and kept for LabVIEW 2021 **64-bit** was opened in LabVIEW 2026 Q3
**32-bit**: the Project Explorer showed it changed (`*`) at once, with nothing edited.
`lvai_restart_labview`'s close saved it (every project close saves - the helper runs Save, then
Close, because an unsaved close would raise a modal prompt). The pin and `LVVersion` stayed 21.0, so
`saveVersionCheck` had nothing to report - and the file was 154 402 -> 100 287 bytes:

- Dependencies emptied: 392 entries (374 VI, 9 Library, 7 Document) -> none. LabVIEW lists them
  again when it loads the VIs.
- Every installer specification: `DistPart[n]` "NI LabVIEW Runtime 2021 SP1 f6 (64-bit)" (with its
  productID) -> "NI LabVIEW Run-Time Engine 2021" with EMPTY productID and flavorID;
  `InstSpecBitness` 64-bit -> 32-bit; `InstSpecVersion` 21018002 -> 21000000. An installer built from
  that project is no longer the one it was.

**Since then every project close reports it** (`Infra/ProjectRewrite.cs`, called by
`lvai_close_active_project` and so by every tool that closes a project, `lvai_restart_labview`
included): the `.lvproj` is copied before the close, compared after LabVIEW's save and BEFORE the
stray sweep, and the answer carries `projectRewrite` - `changed`, `bytes`, `lvVersion` and
`saveVersion` before/after, `dependencies` before/after, `items.removed`/`added` (tree paths outside
Dependencies and Build Specifications), `buildSpecifications.changedProperties` (spec, property,
before, after), `otherPropertiesChanged`, `backupPath` and a `warning` that the close's `note` and
the restart's side-effect line repeat. The copy lives under the cache root
(`project-backups\<name>.<time>.lvproj`, newest 50 kept), never beside the project. Putting it back
is the caller's decision, with the project closed: a save that carries this session's own project
edits must stay. Closing WITHOUT saving was not attempted - it needs a probe of what `Close` does on
an unsaved project (a modal prompt stops every call).

## 3b. Putting the project file back: `restoreProjectFile` (2026-11-14)

Closing WITHOUT saving would avoid the rewrite, but LabVIEW marks an older project modified on open,
and an unsaved close of a modified project raises a modal prompt that stops every call. So the close
still saves, and `lvai_close_active_project restoreProjectFile: true` writes the bytes taken before
that save back - OPT-IN, for a session that changed nothing in the project on purpose, because it
also undoes project edits LabVIEW held in memory. `projectRewrite.restored` says what happened.
Measured on the 21.0 64-bit field copy in LabVIEW 2026 32-bit: the save took the file from 154 333 to
100 137 bytes and Dependencies from 391 to 0; after the restore the file's hash equalled the original.
The project's `.aliases` and `.lvlps` are not covered (LabVIEW re-saves them, measured same sizes).

## 3a. Links the target LabVIEW lacks: `lvai_check_links` (2026-11-14)

A file written for the old version can still link a `vi.lib` VI only the newer LabVIEW has - a
save in the newer one relinks some calls - and the target says so only when it LOADS the VI. Two
field projects found it on the target PC. `lvai_check_links` reads every file's link records (the
`PTH0` path references in the top-level `LIvi`/`LIfp`/`LIbd` blocks, `Infra/ViLinks.cs`; no LabVIEW,
no pylabview) and looks each path under `<vilib>`, `<userlib>`, `<instrlib>`, `<resource>` or
`<menus>` up in an INSTALLED target: the release named, or the one the nearest `.lvproj` pins. An
`.llb` member is looked up by its name in the `.llb` (a heuristic); a class's private data, linked
through its `.lvclass`, is present when the class is. Relative links (the user's own code) and
`<extravilib>` (channel instances LabVIEW generates per user) are counted, not checked.

Measured 2026-11-14 over LabVIEW 2026's 1 809 example files (3 975 installation links, 0.5 s):

| target | missing | what |
|---|---|---|
| LabVIEW 2026 (control) | 4 | DSC and Real-Time module VIs, modules not installed here |
| LabVIEW 2020 | 32 | DQMH (not installed for 2020), the newer Python session VIs, `error.llb`'s newer string VIs, `Write Waveforms to File (1D).vi` |

The first run also named every `Actor.lvclass\Actor.ctl`-style link missing: a class's private
data control is linked THROUGH its class file. Shipping file lists of every LabVIEW version was
left out on purpose: the check needs the target installed on the PC.

## 4. Not done, and why

- **Saving through LabVIEW in an older version.** The tools that save through LabVIEW (layout,
  icons, swaps, typedef binds, class tools) still write the running LabVIEW's version; the check
  reports it. A save through the project that pins the version is what LabVIEW itself honours (the
  driver project saw `lvai_bind_typedef_constants`, which saves with the project active, write 18.0),
  but opening the project for it clashes with the rule that no tool opens or closes a project while
  agents work in parallel, and a newer LabVIEW closing an older project rewrites the `.lvproj`
  (measured in the 21.0 project). Not attempted.
- **NI's converter** (`backend: grpc`) writes the running LabVIEW's version; only the composer takes
  a target version.
- **`.lvversion` files** and a library's or class's own save version are not read: not measured here.

## 5. A project's pin is not honoured by a save through VI Server, also with the project active (measured 2026-11-19)

Two copies of a supplied application saved in LabVIEW 17.0, each in a minimal project pinned to
`20.0` and to `17.0` (`lvai_describe_project` reads both pins; LabVIEW 2026 opens both projects
with no dialog and keeps the pin and `LVVersion` on close). `lvai_set_vi_descriptions` after the
close, and `lvai_layout_diagram` WITH the pinned project active, both left the VI as 26.0
(`LVSR` 17008000 -> 26008000; `saveVersionCheck`: `savedAs 26.0, was 17.0, projectSaveVersion
20.0` / `17.0`). So setting the project's save version - the practice for LabVIEW 2024 and later
when a developer saves from the IDE - does not carry to our tools' saves: they call the VI's Save
method. The Save for Previous Version method after such a save brings the file back: see §6. Not tried: a
save from the IDE's own menu. The composer does write the pinned version (20.0 at the oldest), so the files it
writes keep it; what LabVIEW re-saves (clean-up, icon, descriptions, graft) does not.

## 6. Save for Previous Version brings a re-saved VI back to the pin (measured 2026-11-20)

The `pin20` fixture of §5 after its 26.0 save (`Boiler.vi` LVSR 26008000; its three typedef `.ctl`
files still 17008000). A generated probe ran Open VI Reference -> `{LV.VI}` Invoke `Save.For
Previous` (`Path to saved file`, `Oldest compatible target version` = the STRING `"20.0"`) -> Close
Reference, with the project closed, in the default instance:

- to a new folder: the copy reads 20008000, no error, no warnings. Only the VI was written, none
  of its typedefs or subVIs;
- IN PLACE (target = the VI's own path): no error, the file reads 20008000 (12318 -> 12838 bytes).

LabVIEW 2026 then opened `Pin20.lvproj` with `projectBecameActive`, `execState 1` and no errors,
nothing in memory modified, no dialog; the close answered `projectRewrite.changed: false`, and
the VI still read 20.0 afterwards. So a tool's 26.0 save can be undone per VI. Since 2026-11-23
`lvai_save_for_previous` does it (below); until then no tool did.

**A dependency newer than the pin: one Save For Previous PER FILE (measured 2026-11-23).** This
section said "one Save For Previous per file is the likely answer, unconfirmed". `Pin20.lvproj`
(pin 20.0) listing `Uses Limits.vi` and its typedef `Limits.ctl`, BOTH saved 26.0; the same probe,
project closed:

- the VI to a new folder: 20.0, and the folder holds ONLY the VI - the newer `.ctl` is not written;
- the VI in place: 20.0, `Limits.ctl` stays 26.0. LabVIEW 2026 opens the project with the mix
  (`projectBecameActive`, execState 1, no dialog, nothing modified) - but a LabVIEW 2020 could not
  load the 26.0 typedef;
- the `.ctl` through the SAME probe: Open VI Reference takes a `.ctl` with no error, and `Save.For
  Previous` brought it to 20.0, to a new folder and in place, still a typedef with its type;
- with both at 20.0 the project opened as before and its close answered `projectRewrite.changed:
  false`.

## 7. `lvai_save_for_previous` (since 2026-11-23)

The route of §6 as a tool. `paths` names files or folders (every `.vi .ctl .vim .vit .ctt` under a
folder); the target is `targetVersion` (`20.0`, `20` or `2020`) or, per file, the pin `saveVersionCheck`
uses: a project listing the file or its owner (`pinFrom: listed`), else the pin every project above it
agrees on (`pinFrom: folder`). A file at or below its target, or with no pin, is not touched. Each file
is saved IN PLACE by a helper VI (`scripts/lvai_save_for_previous.xml`: Open VI Reference ->
`{LV.VI}` `Save.For Previous` -> Close Reference, the probe of §6), `.ctl` files first, and
read back from disk: a save LabVIEW answered without error but that left the file newer is
`targetNotReached`, and `ok` is false. `dryRun` lists what would be saved. `saveVersionCheck`'s warning
names the tool since the same day; it used to advise "re-save them through the project that pins
the older version", which §5 had measured NOT to work.

What it gives up, and what is not measured:

- one file per save, as §6 measured: a subVI or typedef still newer than the target needs its own
  entry - name the folder;
- measured with the project CLOSED. With it open, the default instance's open of a file the project
  holds is not measured; close the project first;
- NOT the ActiveX method. `VI.SaveForPrevious(path, "20.0")` over ActiveX answered no error and
  wrote **25.0** on LabVIEW 2026.3 (live check 2026-11-23: a VI and a typedef, twice; the read-back
  answered `targetNotReached`). FINDINGS.md had recorded the same for "24.0" and "20.0" earlier. The
  tool's first version used it; the Invoke Node with the same string writes 20.0 (§6);
- `.lvclass`, `.lvlib` and `.lvproj` are XML and are not saved by it (`notAViOrCtl`). Live
  2026-11-23: a class member VI went 26.0 -> 20.0 and stayed executable, but its `.lvclass` kept
  `LVVersion="26008000"`; whether LabVIEW 2020 opens that class file is not measured;
- a class member: FINDINGS.md's 25.0 for a class-linked VI was the ActiveX route; through the
  helper a dynamic-dispatch member reached 20.0 (live 2026-11-23). A miss would read back as
  `targetNotReached`.
