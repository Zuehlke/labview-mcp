# Offline audit of saved LabVIEW files (`lvai_audit_files`)

`lvai_audit_files` reads SAVED LabVIEW files with NO LabVIEW (about 20 ms a file). It checks the
house rules a file carries. The method was ported from wirelint and measured on 2026-11-21.

## Upstream

Ported from: felipefoz/wirelint v0.2.0 280d621d0846f2307b2ab4325fdf60ab0b1f287c (2026-11-20)

The same line is `LvFileAudit.UpstreamBaseline`, and `LvFileAuditTests` keeps the two equal. When
wirelint releases a new version:

1. diff its readers and rules against the baseline, and port what changed:

   ```
   git diff v0.2.0..<new> -- src/formats src/rules.rs
   ```

2. re-run the cross-check below;
3. move both lines.

| wirelint file | ours |
|---|---|
| `src/formats/rsrc.rs`: LVSR, STRG, BDPW, LIvi/PTH0 | `LvFileAudit.ReadBinary`, `OwnerLink` |
| `src/formats/heap.rs`: BDHb, `fPTerm` `objFlags` | `LvFileAudit.Terminals` |
| `src/formats/connector.rs`: CONP to VCTP to pattern | `LvFileAudit.ConnectorPane` |
| `src/formats/xml.rs`: owner properties, library items | `LvFileAudit.ReadXml` |
| `src/rules.rs` | `LvFileAudit.Rules`, `Check` |

**Where we deviate on purpose (each one measured):**

- **A polymorphic VI** (its CONP names `PolyVI`, 0xF2; 39 of 1532 `vi.lib` files) and **a global
  VI** (a Cluster, 0x50; 4 of them) have no connector pane, and that is no error. wirelint aborts
  each such file with an analysis error.
- **A PTH0 relative path is relative to the FILE, and an empty component means "up one".**
  - `Close Module.vi` beside its library stores `['', 'Wash Station.lvlib']`.
  - `Lib\LibVi.vi` stores `['', '', 'HkLibG.lvlib']`.

  wirelint pushes the empty component as nothing.
- **The info header is the one the FIRST RSRC header names.** It is not reached by following the
  header chain, because our icon fixtures have a second header that does not point at itself
  (`RsrcMap.Blocks`).
- **Classes count as owners too.** A class member's VILB names its `.lvclass`.
- **Rules not ported**, because they do not concern our output: password, breakpoints, saved
  version, debugging and class mutation history. The breakpoint bit (`InStBit29`) is also
  unconfirmed: it was clear in all 2150 files.

## Rules

| id | severity | what |
|---|---|---|
| `compiled-code` | error | VI or `.ctl` with source-only off, or with a VICD block in its own map |
| `automatic-error-handling` | error | VI with Enable automatic error handling on. Not checked on a polymorphic or global VI, which runs no diagram of its own (29 of the 43 in `vi.lib` carry the bit, `Write To Spreadsheet File.vi` among them) |
| `orphaned-vi` | error | the `.lvlib`/`.lvclass` a VI or `.ctl` names as owner (scanned too) does not list it. A typedef carries the same VILB link (`Broadcast Events--cluster.ctl`: `['', 'Wash Station.lvlib']`) |
| `description` | warning | VI, `.ctl`, `.lvlib` or `.lvclass` without a description |
| `terminal-style` | warning | VI with a panel terminal shown as an icon |
| `source-only-default` | warning | `.lvproj`/`.lvlib`/`.lvclass` without `NI.LV.All.SourceOnly` true |

`ok` is false on any error finding and on any file the reader could not parse. An unread file is
never a pass.

## Inline: every writing tool, 2026-11-22

A closing audit finds a defect only at the end of a build. So the server's tool wrapper
(`DiagnosingTool`) runs the same rules on what ONE call just wrote, in `InlineAudit`. The user's
condition was that it must not cause more trouble than it saves, so it is deliberately narrow:

- **Report-only.** It adds an `audit` field (`errors`, `findings`, `note`) to the answer. `ok` and
  every other field stay as the tool wrote them.
- **Errors only:** compiled code, automatic error handling, orphaned file. A missing description
  on a DQMH scaffold VI or a placeholder is no news on every call; `lvai_audit_files` reports it
  when someone asks.
- **Silent when clean.** With no error finding the answer is byte for byte what the tool wrote.
- **Not about running.** A deliberately unfinished VI - DQMH's scripting leaves several - is no
  finding as long as it is source-only, has automatic error handling off and is listed by its
  owner. Runnability stays with `lvai_exec_state`.
- **A deferred strip is expected.** A VI whose source-only strip waits for the next project close
  (`DeferredSourceOnly`) is not reported for compiled code.
- **A project's own setting is one finding, not one per file** (2026-11-23). In a project whose
  "Separate compiled code from new project items" (`NI.LV.All.SourceOnly`) is off or absent, every
  new item keeps compiled code, whoever writes it. Measured: one `lvai_dqmh_new_event` there was
  eight findings on Delacor-written files, and the same again on every call. Now they fold into ONE
  `compiled-code` finding on the nearest `.lvproj` above the files, with the count and an example.
  The setting is the user's; what is given up is the per-file list, which `lvai_audit_files` still
  gives. A file with no project above it (8 levels), or in a source-only project, keeps its own finding.
- **Which files:** every existing LabVIEW file named in the answer and written since the call began
  (2 s slack), plus the members of a written `.lvlib`/`.lvclass` written since then. An owner is
  read even when not written, so the orphan rule can judge. No `.lvproj` (its only rule is a
  warning), at most 400 files, an unparsable file is skipped.
- **Never on** a read-only tool, `lvai_audit_files` itself or a failed call. Anything the audit
  throws leaves the answer as it was.

## No closing audit any more, 2026-11-23

From 2026-11-21 a build ENDED with `lvai_audit_files` over its folder, and the brief told the
orchestrator to fix every error it found. The user's decision of 2026-11-23 made it on demand:

- **Every error it ever found is fixed where it arose.** The graft clears automatic error handling
  (below). The DQMH tools strip compiled code from `Main.vi` (`sourceOnly`), and `ATM_DQMH_Cold_10`
  was clean. (This said "the DQMH tools strip compiled code" until 2026-11-23: the files Delacor's
  scripting writes keep it when the project's own setting says so - see the fold above.)
- **The same error rules already run on every writing tool** (above), at the call that caused the
  defect. At the end of a build the same finding costs more to fix and says less about its cause.
- **What it adds is warnings.** Description, terminal style and the `.lvproj` default changed no
  build's result.
- **Its cost is turns, not the 20 ms a file.** That is one or two model turns, plus an agent
  tempted to tidy up warnings. This is estimated, not measured.

The one error the inline audit leaves out on purpose is compiled code whose strip was DEFERRED
(`DeferredSourceOnly`). The list lives in the server's memory, and `lvai_close_active_project`
strips its entries. So the brief now says: a build whose tools answered `deferredSourceOnly`
ends with that close, given `projectPath`.

**Still open:** an `audit` field has not been seen in a live tool answer yet. The next cold build
is the check. If the field is missing or wrong there, bring the closing audit back.

`lvai_audit_files` itself is not counted by the parallel-call guard (`ParallelCalls.NotCounted`):
it reads saved files and never reaches LabVIEW, so it may run beside anything.

Tested in `InlineAuditTests` on the real fixtures. Not yet seen in a live tool answer: the tool
roster of a running client changes only after a restart.

## Reliability, measured 2026-11-21

### Corpus

- **Ours, 618 files:** 520 VIs, 68 controls and 30 XML files from
  `C:\Projects\{Gold_Hunter, Niko_Wall_of_confusion, Paul_Pensionierung}` and
  `C:\Temp\{ATM_20261118_kalt2, CarWash_DQMH_Cold, ATM_DQMH_Cold_10, Bausteine}`.
- **NI, 1532 files:** every 15th file of `vi.lib`, LabVIEW 2026 (32-bit).

### Results

1. **A Python port of wirelint against pylabview's full parse:** 480 of 480 fields agreed, in 60
   random VIs. The fields were source-only, debugging, automatic error handling, `InStBit29`,
   description, pattern, and icon and data-type terminals. The port is
   `experiments/offline-audit-2026-11-21/wl_port.py`.
2. **The C# reader against that port, on all 2150 files:** every field agreed. The only
   differences are the 43 polymorphic and global VIs, which the port, like wirelint, could not
   read. The C# reader had 0 read errors.
3. **Orphan check:**
   - over 421 VIs with an owner link in our builds, no false finding;
   - positive control: the real `GH_Render.lvlib` with one `<Item>` removed, which gave exactly
     one finding for that VI.
4. **Semantics:**
   - **Confirmed by LabVIEW earlier:** source-only, Allow debugging and automatic error handling
     (`docs/vi-execution-settings.md`, 2026-11-11). The automatic error handling bit is pylabview's
     `Execution2.DefaultErrorHandling`.
   - **Pattern:** the shift by 3 is from our own `docs/connector-pane-patterns.tsv`.
   - **Not confirmed by toggling it in LabVIEW:** the terminal-style bit (`fPTerm` `objFlags` bit
     26). The distribution fits: all 520 of our VIs read data-type, as our layout draws them, and
     82 NI VIs have icon terminals.

## Case matrix, 2026-11-21

One folder per case, each holding real files (copies). `AuditTools.AuditFiles` was run over each.

| Case | Real file | Found |
|---|---|---|
| compiled code, VI | `ActorFW_first\Aquarium\Append To Log.vi` | yes |
| compiled code, `.ctl` | `CarWash_DQMH_Cold\...\Broadcast Events--cluster.ctl` | yes |
| automatic error handling | grafted `Automatic Teller Machine (ATM).vi` | yes |
| orphaned VI | `Image_Load.vi`, with its `<Item>` cut from `GH_Render.lvlib` | yes |
| orphaned typedef | `Ofenprofil.ctl`, with its `<Item>` cut from `Ofen.lvlib` | yes |
| description, VI / `.ctl` / `.lvlib` | `Read Attributes.vi`, `Car Wash Indicators.ctl`, `Aquarium.lvlib` | yes, yes, yes |
| terminal style | `ActorFW_first\Giraffe\Giraffe\Actor Core.vi` | yes |
| source-only default | `ActorFW_first.lvproj` | yes |
| unreadable file | the ATM VI cut to 3000 bytes | yes, as an analysis error, `ok: false` |

The negative controls gave no error finding:

- a clean library member;
- a clean typedef in its library;
- a polymorphic VI and a global VI from `vi.lib`;
- the whole Gold_Hunter application (65 files).

The run found one false alarm and it is fixed. NI's polymorphic `Write To Spreadsheet File.vi`
really does carry automatic error handling, but such a VI runs no diagram of its own, so the rule
now skips polymorphic and global VIs.

The additional findings in the cases are real: the September Actor Framework build carries
compiled code, and those libraries have no description.

## What it found in our builds

| Finding | Count | Cause |
|---|---|---|
| automatic error handling ON | 3 main VIs (12 with test and scratch copies) | Every main VI grafted into a supplied exam panel: ATM 2026-11-18, ATM DQMH 2026-11-07 and Car Wash 2026-11-05. The template from 2026-03-25 has the setting on, and the graft kept it. |
| compiled code | 54 | Only `CarWash_DQMH_Cold` (2026-11-05): the members of `Wash Station.lvlib` that Delacor's scripter wrote. The later `ATM_DQMH_Cold_10` is clean. |
| description missing | 6 (a further 8 `.lvproj`) | Typedefs, test and scratch VIs, `.lvlib` |

None of these files went through `lvai_generate_vi`, whose own checks cover only the VI it
writes. `lvai_check_aixml` sees AIXML before generation, not the saved file. The audit therefore
complements both and replaces neither.

## The graft now clears automatic error handling

`lvai_graft_diagram` reads the flag from the copy before its pylabview pass. When the flag is on
(as in every exam template), the same pass that writes the event registrations back sets
`Execution2.DefaultErrorHandling` to 0. With no Event Structure, the pass now runs for that alone.

- The answer carries `automaticErrorHandling` with `supplied`, `output` and `kept`.
- `ok` requires `output: false` unless `keepAutomaticErrorHandling` is set.
- **Verified offline:** a copy of the real grafted ATM VI was extracted, patched, stripped and
  rebuilt, and the result read `automaticErrorHandling: false`. Every other fact was unchanged.
- **Not yet run in LabVIEW:** the full graft with this change.
