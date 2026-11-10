# Slice 14 measurements: the hardware (handoff, context, modal)

Plan: `plans/cross-platform-server.md`, Slice 14. This file holds Step 14.1 (handoff and rendezvous). Steps 14.2 and 14.3 are not
done. Record date: **2026-11-10**. Only the Linux desktop row is measured here; the Windows row is a handoff section at the end.

## Result first: spec assumption 2 FAILS on Linux

**A second launch of the LabVIEW binary does not hand the VI to the running LabVIEW. It starts a second LabVIEW process.** With one
`labview` process running, `labview <vi>` gave two processes, the VI loaded (and, when run-when-opened, ran) in the new one. Measured twice
(plain VI and run-when-opened VI) by `probe-handoff.sh`, once more by hand before it. The plan says this stops the plan and returns it to
`/specs` (Risks: "An assumption fails (Slices 1 and 14)"). The rendezvous and race rows below pass, but they ran in the fresh process the
launch itself started, not in a handed-off instance, so they say nothing yet about a resident LabVIEW. The machine's LabVIEW is also not the
build the plan names (see Environment).

## Summary table

LabVIEW build and bitness on this machine: **LabVIEW 26.3.1f1 for Linux, 64-bit** (log header `#Version: 26.3.1f1`; ELF 64-bit; install
directory `LabVIEW-2026-64`). The plan and the user said "2026 Q1"; Step 1.2's container build is 26.1f0 (Q1). The Step 1.2 composer comment
calls 26.3.1 "2026 Q3". Record date 2026-11-10. Run with `experiments/linux-bridge-hw/probe-handoff.sh`; transcripts in
`experiments/linux-bridge-hw/transcripts/slice-14.1-linux/`.

"Setting" is the run-when-opened setting in force for the run (see the next section): **P** = LabVIEW's default, prompt shown;
**A** = `RunWhenOpened.Behavior=2` in `labview.conf`, set by the user (always allow). Rows made with **A** are not evidence for a default install.

| Machine | Check | Setting | Result | Transcript |
|---|---|---|---|---|
| Linux desktop, 26.3.1f1 64-bit | exactly one LabVIEW process before (pid 13139, normal IDE, started by us from the command line) | A | PASS | `00-env.txt` |
| Linux desktop | composer builds the 4 probe VIs for `--lv-version 26` | A | PASS | `01-compose.txt` |
| Linux desktop | handoff, plain VI: count of `labview` processes before and after | A | **FAIL (NO-HANDOFF): 1 -> 2**, VI loaded in the new process, not run | `10-handoff-plain.txt` |
| Linux desktop | handoff, run-when-opened VI: which process ran it | A | **FAIL (NO-HANDOFF): ran in the new process 17213, not in resident 13139**; count 2 | `11-handoff-run.txt` |
| Linux desktop | handoff, run-when-opened VI, default setting | P | **FAIL (NO-HANDOFF)** and the prompt appeared; a human allowed it; ran in new process 13369, count 2 | `05-manual-first-runs.txt` (by hand) |
| Linux desktop | rendezvous by nonce in the path of the stub VI | A | PASS (1 of 1, 1.46 s to connect, file 0600, dir 0700) | `2n-rendezvous-nonce.txt` |
| Linux desktop | rendezvous by launch argument after `--` | A | PASS (1 of 1, 1.43 s, 0600, 0700) | `2a-rendezvous-arg.txt` |
| Linux desktop | two servers at the same moment, nonce mechanism | A | PASS: 20 of 20 trials, 0 wrong-token connections | `3n-race-nonce.txt` |
| Linux desktop | two servers at the same moment, argument mechanism | A | PASS: 20 of 20 trials, 0 wrong-token connections | `3a-race-arg.txt` |
| Linux desktop | negative control: stubs started with each other's rendezvous file | A | PASS: the race check flagged both crossed connections | `40-control-crossed.txt` |
| Linux desktop | `labview.conf` unchanged by us (identical to the re-baseline except LabVIEW's own `RecentFiles.pathList`) | A | PASS | `90-conf-end.txt`, `91-conf-history.txt` |
| Linux desktop | run-when-opened prompt remembered per path, or for a different path | P | **NOT MEASURED** under the default setting (see below) | `06-RunWhenOpenedPermissions.json` |
| Windows 2026 64-bit | every row above | | **PENDING** (handoff section below) | |

## Environment (Linux desktop)

| Item | Value |
|---|---|
| Machine | Pop!_OS 24.04 LTS, `Linux 7.2.15-76070215-generic x86_64`, GNOME on Wayland, `DISPLAY=:1` (XWayland), 8 processors |
| LabVIEW | 26.3.1f1, 64-bit, normal interactive IDE (never headless), started by the measurement from the command line |
| NI install root | `/usr/local/natinst/LabVIEW-2026-64` |
| Binary | `/usr/local/natinst/LabVIEW-2026-64/labview`, a symlink to `labviewprofull` (`/proc/<pid>/exe` is `labviewprofull`) |
| Process name | `labview` (`/proc/<pid>/comm`; `pgrep -x labview` finds it) |
| Config in use | `~/natinst/.config/LabVIEW-2026/labview.conf`. `labview64.conf` sits beside it and is not touched by any start (mtime 2026-11-08 13:43 throughout). LabVIEW rewrites `labview.conf` at every start (mtime moves) |
| Other state | `~/natinst/LabVIEW Data/` (VIObjCache, `RunWhenOpenedPermissions.json`); logs `/tmp/labview_26.3.1f1_interactive_<user>_cur.txt` (the previous process's log is renamed `..._log.txt` when a second process starts) |
| VI Server TCP | not enabled: no `server.tcp.enabled` key in the conf, nothing on 3363 (`ss`). One loopback listener on `127.0.0.1:41905` (owner: the LabVIEW process; purpose not investigated). So `LabVIEWCLI` is not an option on this station: it is not installed either (`find` finds none) |
| `Application:Version Number` | `26.3.1f1` (read by the probe VIs, `11-handoff-run.txt`) |
| Composer | `experiments/linux-bridge/compose.py`, `--lv-version 26` (file version 26.0.0, loads in 26.3.1f1), `AIXML2VI_TRY_WIRES=Refnum.TCPNetConn`, `LVMCP_EXEC_FLAGS=RunOnOpen=1` for run-when-opened; Pillow in a venv (system Python has none) |

## The run-when-opened prompt and the setting in force

1. **Default install (P): the prompt appears and needs a human.** The first run-when-opened VI launched on this IDE (run 1 in
   `05-manual-first-runs.txt`, `/tmp/lvmcp-hw-test/args-dump.vi`) logged `RunWhenOpenedSecurity::CheckVIForRunPermission: CHECKING
   "args-dump.vi"" at: "/tmp/lvmcp-hw-test/args-dump.vi"` and did not run until the user answered the prompt on screen. This session
   could not capture the prompt text or the button (no screenshot tool works under XWayland: `PIL.ImageGrab` fails with X error 8, and
   `gnome-screenshot`, `import`, `grim` are absent). A human answered it; the run is not unattended evidence.
2. **LabVIEW records the answer per VI path and GUID**, in `~/natinst/LabVIEW Data/RunWhenOpenedPermissions.json` (copy:
   `06-RunWhenOpenedPermissions.json`): `{"viPath": "/tmp/lvmcp-hw-test/args-dump.vi", "viGuid": "dec4a6b6-...", "runAllowed": true}`.
   The file was written at 11:15, the time of the user's click. So an allowed VI at the same path with the same GUID would not prompt again.
   **Not measured:** whether a VI at a different path (the per-nonce stub directories) prompts. By then the user had also set the global
   setting, which hides the difference. Measuring it needs the user to revert the setting; it is not done.
3. **The user then changed the setting to "always allowed" in the Options dialog of that second process** (reported by the coordinator).
   `labview.conf` gained `RunWhenOpened.Behavior=2`, `NewProj.maxNum=10` and `RecentFiles.pathList=...`. Every `A` row ran with that setting;
   none of them prompted. The value 2 is "always allow" on the user's word; the other values were not looked up.
4. `labview.conf` baselines, because the file changed twice for reasons that are not ours (`91-conf-history.txt`):
   baseline 1 at the start of the step (sha256 `7683b964...3de00`, 759 B); LabVIEW's own first-start write changed only
   `NI_GSWContentIndex` 3 -> 4; baseline 2 after the user's change (sha256 `26c4f0e4...cd53`, 857 B, mtime 11:17:14.926). The end-of-step
   check is against baseline 2: **identical except `RecentFiles.pathList`**, which LabVIEW itself extends with every VI path it is launched
   with (sha256 now `d8eb5772...cc51`, 1231 B). We never wrote the file. After the run it lists ~10 of our `/tmp/lvmcp-hw.*` paths
   (that key is LabVIEW's recent-files list).

## Scenario: a VI handed to a running LabVIEW opens in that instance (Linux desktop row): FAILS

Precondition held: exactly one `labview` process (pid 13139). `labview /tmp/.../args-dump.vi` (plain, not run-when-opened):

```
before: count=1 pids=[13139]
after 15 s: count=2 pids=[13139 17051]   launched pid 17051 alive: yes
  pid 17051 cmdline=/usr/local/natinst/LabVIEW-2026-64/labview /tmp/lvmcp-hw.AchX8O/hp/args-dump.vi
VI ran (dump file written by its diagram): no
```

Run-when-opened (`11-handoff-run.txt`): the VI wrote `stat=<pid> (labview) ...` from `/proc/self/stat`; the pid is the launched one, not 13139.
So: **a plain `labview <vi>` loads the VI into a new process and does not run it; run-when-opened (with the prompt answered or the setting on) runs it
there.** The `labview` command stays in the foreground for the life of the new process and prints nothing; the new process is a child of the caller's shell (ppid = the shell), it is not forked off the resident one.
Both processes stay open side by side (two sets of palettes and windows). Not tried: any other flag of the binary (none is documented
here; `labview --help` was not run, as it might open the IDE), and VI Server (not enabled here; enabling it edits the conf, which is the
user's decision, not ours).

## Scenario: the probe finds its own rendezvous file (Linux desktop row): PASSES, in a fresh process

Server side: `rendezvous-server.py` creates the directory 0700, writes `rendezvous.txt` (or `rv.txt`) 0600 from creation (`O_EXCL`, atomic
rename) with `<port>-<token>` (OS-chosen loopback port, 128-bit hex token), listens on 127.0.0.1 and records every connection. Stub side
(`stub.aixml`, `stub-arg.aixml`, composed per connection under a unique file name): read the file, `TCP Open Connection`, send
`hello-<token>|ver=..|stat=..|path=..|args=..|tick=..|end`, close; it also writes the same text to `<rendezvous>.report`.

| Mechanism | What the stub sees | Result |
|---|---|---|
| nonce in the path | `Current VI's Path` = `/tmp/lvmcp-hw.X/r-nonce/stub-r-nonce.vi`; `Strip Path` + `rendezvous.txt` is the file | connected after 1.46 s, token match, one connection |
| launch argument | `labview <vi> -- <rendezvous path>`: `Application:Command Line Arguments` = `[binary path, <rendezvous path>]`; the stub takes the last element | connected after 1.43 s, token match |

Facts that came with it: the array holds the binary path first and the words after `--` next; **neither the `--` nor the VI path is in
it**. Without `--` was not tried. `Application:Command Line Arguments`, `Current VI's Path`, `Strip Path`, `Build Path`, `Search/Split String`,
`TCP Open Connection/Write/Close` and a read of `/proc/self/stat` all compose and run in LabVIEW 26.3.1f1 (stub 9.4 kB, composes in under 1 s).
The stub's `/proc/self/stat` read gives the pid of the process the stub runs in; that is how every row here proves which process ran it.

## Scenario: two servers start at the same moment (Linux desktop row): PASSES, 20 + 20 trials

Per trial: two servers (own directory, own port, own token) and two stubs composed under unique names, then both `labview` launches back to
back with no wait. Passing a trial needs for each server: token match, exactly one connection, no wrong-token connection, the reported
pid equal to the pid of the process launched for that server, the reported VI path equal to that server's stub, rendezvous file 0600 and
directory 0700 (and for the argument mechanism the argument seen is that server's file).

- Nonce mechanism: 20 of 20 trials, 0 mismatches. Argument mechanism: 20 of 20, 0 mismatches. 80 connections, **1.57 to 2.54 s** after the
  servers were ready (median 2.06 s) for a freshly started LabVIEW process (not a resident one).
- **Control** so the check can fail (`40-control-crossed.txt`): stub A started with server B's file and stub B with A's. The check flagged both
  (`pid-seen=23060 launched=23057`). Without this control a pass would prove nothing.
- Weakness of this measurement, plainly: both stubs ran in two different processes, so the case the spec means (two stubs in ONE resident
  LabVIEW, started by one handoff each) was **not** measured: it needs a handoff that works. Same-name stubs in different directories inside
  one process were not measured either (it needs two stubs in one process). The stubs here have unique names (`stub-<tag>.vi`); how LabVIEW
  treats two same-named VIs from different directories in one instance stays open.
- LabVIEW moved the mtime of `labview.conf` at the starts that were checked; across the ~86 starts of the script the file stayed identical but for `RecentFiles.pathList` (guarded per trial by the script).

## Findings the later slices need

1. **Slice 9 (launcher).** On Linux a second `labview <vi>` is a second process, not a handoff. A launcher that starts the bridge by running the
   binary with a VI path gets its own LabVIEW process each time (about 2 s until the stub runs, a full IDE window set of its own). It cannot
   assume the user's running IDE receives the VI. The spec's "attach to the user's activated IDE" needs another route, to be decided in `/specs`.
2. **Slice 9 (run-when-opened).** With LabVIEW's default setting the run-when-opened route needs a **human click** on a prompt, once per VI path and
   GUID (the answer is stored in `~/natinst/LabVIEW Data/RunWhenOpenedPermissions.json`); the only other way is `RunWhenOpened.Behavior=2` in
   `labview.conf`, which is the user's setting and read-only to us. The launcher cannot assume the setting is on. Whether a new path (the per-nonce
   stub directories) prompts under the default setting is **not measured**; assume it does until it is.
3. **Slice 6 (rendezvous).** Both mechanisms work in a freshly started process. The simplest working one is the launch argument:
   `labview <stub.vi> -- <rendezvous file>`, the stub takes the last element of `Application:Command Line Arguments`. The nonce-in-path mechanism works
   too and needs no argument, but then every connection needs its own composed VI and, if two stubs can ever share one process, a unique VI name.
   Wire format used and verified: file `<port>-<token>`, owner-only (file 0600 from creation, directory 0700); first message
   `hello-<token>...`. `Refnum.TCPNetConn` must be allowed in the composer (Step 1.2 finding 1).
4. **Slice 4 (installs and config on Linux).** Install root `/usr/local/natinst/LabVIEW-2026-64` (directory name `LabVIEW-<year>-<bits>`); binary `labview`
   (a symlink to `labviewprofull`); process name `labview`; **the conf in use is `~/natinst/.config/LabVIEW-2026/labview.conf`, not `labview64.conf`**
   (the 64-bit install does not use the `64` file); version `26.3.1f1` from `Application:Version Number` and from the log header; LabVIEW logs to
   `/tmp/labview_<ver>_interactive_<user>_cur.txt`. The conf carries no `server.tcp.enabled` here, so VI Server TCP is off on a fresh Linux desktop IDE.
   LabVIEW rewrites the conf at start and keeps a `RecentFiles.pathList` there, so a file hash of it is not a stable identity.
5. **Slice 7 (bridge in the editor).** No Linux IDE listener is on 3363 by default; the `127.0.0.1:41905` listener is unexplained. The bridge cannot count on
   `Open Application Reference` to `localhost:3363` on a desktop IDE unless the user enables VI Server.
6. **Process hygiene for tests.** `SIGTERM` ends a launched LabVIEW at once and does not rewrite the conf (the conf is written at start). The starts that followed ran
   normally and showed no extra window in `xwininfo` (nobody looked at the screen). Every run here ended its processes that way, the resident IDE included.

## Not measured, and why

- Handoff into a resident LabVIEW by any route (no route exists on this IDE without VI Server).
- Run-when-opened under the default setting except run 1 (needs a click; the setting is the user's). Per-path memory for a different path.
- Two stubs in one instance; same-named stubs in one instance; the stub on a user project; modal behavior (Steps 14.2, 14.3).
- The prompt's text and button (no screenshot tool).
- `labview <vi>` without `--` and other arguments.
- Windows (below).

## Step 14.1 Windows row (run on the Windows laptop session)

For a Claude Code session on the Windows laptop with LabVIEW 2026 64-bit, from a clone on the branch `feat/xplat-14`. Record failures as
results. Do not change `scripts/`, `src/`, `skeleton.aixml`, or `experiments/linux-bridge/`. New files only under
`experiments/linux-bridge-hw/transcripts/slice-14.1-windows/` and this file. Needs Python 3 with Pillow (`py -3 -m pip install pillow`),
and LabVIEW 2026 64-bit **running as exactly one process**. Do not start it headless. If LabVIEW shows the run-when-opened prompt, write down what
it shows and which setting is in force (`RunWhenOpened.Behavior` in `LabVIEW.ini`, read only), answer it with the least change, and label the run.
Do not edit `LabVIEW.ini`. The Linux scripts are bash and are not used here; the commands below do the same steps by hand.

```powershell
cd <repo root>
$T = "experiments\linux-bridge-hw\transcripts\slice-14.1-windows"; $W = "C:\lvmcp-hw"
New-Item -ItemType Directory -Force $T, $W | Out-Null
$LV = "<path of LabVIEW.exe, 2026 64-bit>"
$env:AIXML2VI_TRY_WIRES = 'Refnum.TCPNetConn'
# the major version of THIS LabVIEW (the probes are composed for it, never a newer one); here 26 if it is 2026
$major = 26
# the dump VI writes to /tmp/...; on Windows give it a Windows path (edit a COPY, not the committed file)
(Get-Content experiments\linux-bridge-hw\args-dump.aixml) -replace '/tmp/lvmcp-hw-args.txt','C:\5Clvmcp-hw\5Cargs.txt' | Set-Content $W\args-dump.aixml
# 0. environment: install root, process names, ini in use
"$LV"; Get-Process LabVIEW | Format-Table Id, ProcessName, Path | Out-String | Set-Content "$T\00-env.txt"
# 1. compose (expect exit 0 each; /proc/self/stat does not exist on Windows: the `stat=` field will be empty, that is expected)
$env:LVMCP_EXEC_FLAGS = ''
py -3 experiments\linux-bridge\compose.py $W\args-dump.aixml $W\args-dump-plain.vi --lv-version $major *> "$T\01-compose-plain.txt"
$env:LVMCP_EXEC_FLAGS = 'RunOnOpen=1'
py -3 experiments\linux-bridge\compose.py $W\args-dump.aixml $W\args-dump-run.vi --lv-version $major *> "$T\01-compose-run.txt"
# 2. handoff, plain VI: count before and after (expect 1 and 1 if LabVIEW hands off; the Linux row measured 1 and 2)
$before = (Get-Process LabVIEW).Count
& $LV $W\args-dump-plain.vi; Start-Sleep 15
$after = (Get-Process LabVIEW).Count
"before=$before after=$after" | Set-Content "$T\10-handoff-plain.txt"
# 3. handoff, run-when-opened VI (the prompt may appear: record it). The VI ran in the existing process iff the dump exists and the count stayed 1
Remove-Item $W\args.txt -ErrorAction SilentlyContinue
& $LV $W\args-dump-run.vi -- C:\lvmcp-hw\arg1 second; Start-Sleep 15
"count=$((Get-Process LabVIEW).Count)"; Get-Content $W\args.txt | Tee-Object "$T\11-handoff-run.txt"
```

The probe VIs close nothing: close the opened VIs in LabVIEW by hand afterwards (File > Close, Don't Save), do not save them.

Rendezvous and race (stubs `stub.aixml` for the nonce mechanism, `stub-arg.aixml` for the argument mechanism; the stubs report `stat=` empty on Windows,
so the matching is by token and by the `path=` the stub reports, not by pid). One server per stub, each in its own directory:

```powershell
$env:LVMCP_EXEC_FLAGS = 'RunOnOpen=1'
function New-Pair($mech, $tag) {
  $src = if ($mech -eq 'nonce') { 'stub.aixml' } else { 'stub-arg.aixml' }; $file = if ($mech -eq 'nonce') { 'rendezvous.txt' } else { 'rv.txt' }
  $d = "$W\$tag"; New-Item -ItemType Directory -Force $d | Out-Null
  $vi = "$d\stub-$tag.vi"
  py -3 experiments\linux-bridge\compose.py experiments\linux-bridge-hw\$src $vi --lv-version $major | Out-Null
  $srv = Start-Process py -ArgumentList '-3','experiments\linux-bridge-hw\rendezvous-server.py','--dir',$d,'--file',$file,'--result',"$d\result.json",'--timeout','60' -PassThru -WindowStyle Hidden
  [pscustomobject]@{ Vi = $vi; Rv = "$d\$file"; Dir = $d; Mech = $mech }
}
function Start-Stub($p) { if ($p.Mech -eq 'nonce') { & $LV $p.Vi } else { & $LV $p.Vi -- $p.Rv } }
# single: one pair per mechanism; the pass condition is matched=True in result.json, vi_path = $p.Vi, and the count of LabVIEW processes unchanged (1)
# race: 20 trials of two pairs, started back to back; record matched and vi_path per server for every trial
foreach ($mech in 'nonce','arg') {
  for ($i = 1; $i -le 20; $i++) {
    $a = New-Pair $mech "k$i-A"; $b = New-Pair $mech "k$i-B"; Start-Sleep 1
    Start-Stub $a; Start-Stub $b
    Start-Sleep 20
    foreach ($p in $a, $b) { $r = Get-Content "$($p.Dir)\result.json" | ConvertFrom-Json
      "$mech trial $i $($p.Dir): matched=$($r.matched) connections=$($r.connections.Count) vi_path_ok=$($r.connections[0].vi_path -eq $p.Vi) count=$((Get-Process LabVIEW).Count)" | Add-Content "$T\3-race.txt" }
  }
}
```

(The file-mode fields of `result.json` are meaningless on Windows; check owner-only access with `icacls <dir>\rendezvous.txt` instead and paste it.)
Record, in a table below with the transcript names: the LabVIEW process count before and after for the plain and the run-when-opened launch (**the
Windows pass for the handoff row is count 1 -> 1 with the VI having run**), `Application:Version Number` from the dump file, the launch-argument array
as the dump shows it (`args=`), per mechanism the connect time and whether the token matched, the race counts (matched and `vi_path_ok`), the
run-when-opened setting in force and whether a prompt appeared, anything LabVIEW showed on screen, and whether LabVIEW wrote `LabVIEW.ini`. If the
count goes to 2 on Windows too, say so at the top: that is a spec failure, as on Linux.

**Windows 2026 64-bit result: PENDING.** (Replace this line with the table.)
