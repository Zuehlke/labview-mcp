# Slice 14 measurements: the hardware (handoff, context, modal)

Plan: `plans/cross-platform-server.md`, Slice 14. This file holds Step 14.1 (handoff and rendezvous). Steps 14.2 and 14.3 are not
done. Record date: **2026-11-10**. Only the Linux desktop row is measured here; the Windows row is a handoff section at the end.

## Result first: spec assumption 2 FAILS on Linux as written, and a VI Server relay reaches the resident IDE

**Update of 2026-11-10 (section "Step 14.1 with VI Server TCP enabled" below): with VI Server TCP enabled, a second `labview <vi>` STILL starts its own process (setting on or off). A relay does reach the resident IDE: a stub in the second process opens an application reference to `localhost:3363`, opens and runs a target VI in the IDE (the target's `/proc/self/stat` shows the resident pid) and quits its own process by itself (3.7 s median). Both rendezvous mechanisms and the two-server race pass in that relay form, 20 of 20 trials each. The handoff the spec states ("the launch itself hands the VI to the running process") is not what happens; what works is a second process as a short-lived VI Server client.** The text below is the first measurement (VI Server TCP off), unchanged.

**Update of 2026-11-10, third round (section "Step 14.1, third round: relay quit and LabVIEWCLI"): two routes get a long-lived target into the resident IDE with no LabVIEW process left behind.**
(1) `LabVIEWCLI -OperationName RunVI` of a small resident *starter* VI: the CLI is a native program (not a LabVIEW process), returns in about 3 s, and the starter
starts the target locally with `Run VI` (Wait until done FALSE, Auto Dispose Ref TRUE, nothing closed). The target kept answering 60 s later in the resident pid (Execution:State 2,
100 of 100 echoes). (2) A relay that does the same remotely and quits after 2 s (a second LabVIEW process for 2 s only). Two-server race 20 of 20 trials on each. A target run by
`RunVI` directly dies when the CLI is killed, and two overlapping CLI calls fail with -350052.

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
| Linux desktop, VI Server TCP **on** (`server.tcp.enabled=True`) | VI Server listener: bind address and access list | A | **FINDING**: `0.0.0.0:3363`, list `+192.168.1.231:+127.0.0.1:+localhost`, **not enforced** against 127.0.0.2 and docker0 | `transcripts/slice-14.1-linux-viserver/00-env.txt`, `20-access.txt` |
| Linux desktop, VI Server on | handoff, plain VI | A | **FAIL (NO-HANDOFF): 1 -> 2** | `.../10-handoff-plain.txt` |
| Linux desktop, VI Server on | handoff, run-when-opened VI | A | **FAIL (NO-HANDOFF)**: ran in the new process | `.../11-handoff-run.txt` |
| Linux desktop, VI Server on | relay: target opened and run in the IDE, nonce mechanism (wait until done) | A | **PASS**: ran in resident pid 25795, token matched, relay quit itself after 3.4 s | `.../3-relay-nonce-wait.txt` |
| Linux desktop, VI Server on | relay: argument mechanism, wait until done TRUE | A | **PASS** (3.6 s) | `.../3-relay-arg-wait.txt` |
| Linux desktop, VI Server on | relay: `Run VI` with Wait until done FALSE, reference held 4 s | A | **PASS** (relay 5.9 s) | `.../3-relay-arg-hold.txt` |
| Linux desktop, VI Server on | relay: `Run VI` with Wait until done FALSE, reference closed at once | A | **RECORDED: the target never reports** (closing the reference ends it) | `.../3-relay-arg-nowait.txt` |
| Linux desktop, VI Server on | relay: `Auto Dispose Ref` TRUE, reference closed at once | A | **RECORDED: the target never reports** either | `.../3-relay-arg-dispose.txt` |
| Linux desktop, VI Server on | relay with a run-when-opened target | A | PASS | `.../3-relay-arg-wait-runopen.txt` |
| Linux desktop, VI Server on | two servers, two relays, same moment: nonce | A | **PASS: 20 of 20**, 0 wrong-token connections, every target in the resident pid | `.../5n-race-relay-nonce.txt` |
| Linux desktop, VI Server on | the same, argument | A | **PASS: 20 of 20** | `.../5a-race-relay-arg.txt` |
| Linux desktop, VI Server on | negative control (crossed rendezvous files) | A | PASS: both flagged | `.../59-control-crossed.txt` |
| Linux desktop, VI Server on | run-when-opened check for a VI opened through VI Server | A | RECORDED: no check logged, no dialog; default setting not measured | `.../40-prompt.txt` |
| Linux desktop, VI Server on | modal dialog "Investigate Previous Internal Error or Warning" in a second process | A | **FINDING**: appears after a process that logged a DWarn; needs a human; 16 resets needed in 1 run | `.../80-resets.txt` |
| Linux desktop | `ni-labview-command-line-interface` in the NI apt feed | n/a | PASS (available, 1.4 MB, NOT installed) | `.../60-labviewcli.txt` || Linux desktop, VI Server on, `ni-labview-command-line-interface` 26.3.0.49372-0+f220 installed (dpkg) | relay quits early (`Run VI` wait FALSE, Auto Dispose TRUE, nothing closed): relay gone in 2.0 s, target still in resident pid 60 s later, State 2, 100 echoes 0 failures | A | **PASS** | `transcripts/slice-14.1-linux-round3/2-A2-relay-keep.txt` |
| Linux desktop | `Asynchronous Call By Reference`, option 0x80 | A | **NOT MEASURED**: the repo composer refuses `Start Asynchronous Call` | `.../01-compose.txt` |
| Linux desktop | resident starter VI started by LabVIEWCLI (CLI 3.1 s), target survives 60 s, no LabVIEW process left | A | **PASS** | `.../2-A3-starter-cli.txt` |
| Linux desktop | the same starter started by a relay (2.2 s) | A | PASS | `.../2-A3-starter-relay.txt` |
| Linux desktop | LabVIEWCLI RunVI of a VI: processes before/during/after, duration | A | PASS: native `LabVIEWCLI`, labview count 1/1/1, 3.0 s | `.../10-cli-basics.txt` |
| Linux desktop | LabVIEWCLI RunVI of the long-lived target itself | A | PASS and **blocks for the target's whole life** (fixed NI pane accepted, no error 1031) | `.../3-B-cli-direct-blocks.txt` |
| Linux desktop | the same, LabVIEWCLI killed after 5 s | A | **RECORDED: the target ends with the CLI** (State 1 three s later, echoes fail) | `.../3-B-cli-direct-kill.txt` |
| Linux desktop | two overlapping LabVIEWCLI calls | A | **FINDING**: the second fails with -350052 "You cannot initialize the logger multiple times" | `.../4-race-cli.txt` |
| Linux desktop | two servers, two LabVIEWCLI starters at the same moment (long-lived targets, retry on -350052) | A | **PASS: 20 of 20**, no LabVIEW process left but the resident | `.../4-race-cli.txt` |
| Linux desktop | two servers, two early-quit relays at the same moment | A | **PASS: 20 of 20** | `.../4-race-relay-keep.txt` |
| Linux desktop | negative control (crossed starters) | A | PASS: both flagged | `.../49-control-crossed.txt` |
| Linux desktop, **default** run-when-opened (key absent) | control: `labview <run-when-opened VI>` from a fresh path | P | **PASS: prompt "Security Warning: Run When Opened", VI did not run, not clicked** | `transcripts/slice-14.1-linux-round4/40-control.txt` |
| Linux desktop, default setting | LabVIEWCLI -> starter -> target, 3 fresh directories (and again 3) | P | **PASS 3/3 (and 3/3): no prompt**, target alive 60 s in resident pid, 100 echoes 0 failures | `.../11-13-starter-cli.txt`, `.../rerun-sessions/` |
| Linux desktop, default setting | LabVIEWCLI P0_AppVersion alone from a fresh path | P | PASS: no prompt | `.../20-p0-alone.txt` |
| Linux desktop, default setting | `RunWhenOpenedPermissions.json` after the CLI runs | P | PASS: unchanged | `.../30-permissions-after.txt` |
| Windows 2026 64-bit | every row above, plus the relay, LabVIEWCLI and default-setting rows | | **PENDING** (handoff section below) | |

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

(Items 1, 3 and 5 below are the VI-Server-off measurement; the sections "Step 14.1 with VI Server TCP enabled" and "Step 14.1, third round" correct and extend them (the latter: a LabVIEWCLI-started starter VI or an early-quit relay keeps a long-lived target in the resident IDE with no LabVIEW process left behind): with VI Server
on, handoff still fails, a relay through the VI Server port does reach the resident IDE, and the listener is `0.0.0.0:3363`.)

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
   `Open Application Reference` to `localhost:3363` on a desktop IDE unless the user enables VI Server. With it enabled, the listener is on `0.0.0.0` and the
   access list does not refuse local non-listed addresses (section "with VI Server TCP enabled").
6. **Process hygiene for tests.** `SIGTERM` ends a launched LabVIEW at once and does not rewrite the conf (the conf is written at start). The starts that followed ran
   normally and showed no extra window in `xwininfo` (nobody looked at the screen). Every run here ended its processes that way, the resident IDE included.

## Not measured, and why

- Handoff into a resident LabVIEW by launching the binary: measured with VI Server off and on, fails both times. Through VI Server: see the relay section.
- With VI Server on: the default run-when-opened setting; `Asynchronous Call By Reference` (the composer refuses `Start Asynchronous Call`); a connection from another machine; whether the
  relay's own VI Server listener competes for 3363; the dialog's text. (Measured in the third round: `Auto Dispose Ref` with nothing closed does keep the target alive for 60 s; sessions beyond 60 s are not measured.)
- Run-when-opened under the default setting except run 1 (needs a click; the setting is the user's). Per-path memory for a different path.
- Two stubs in one instance; same-named stubs in one instance; the stub on a user project; modal behavior (Steps 14.2, 14.3).
- The prompt's text and button (no screenshot tool).
- `labview <vi>` without `--` and other arguments.
- Windows (below).

## Step 14.1 with VI Server TCP enabled

Measured 2026-11-10, after the user enabled VI Server TCP in the Linux IDE (the user takes "enabled" as given, because Windows needs it
too). Reproduce with `experiments/linux-bridge-hw/probe-relay.sh` (`START_IDE=1` starts a normal, never headless IDE); transcripts in
`experiments/linux-bridge-hw/transcripts/slice-14.1-linux-viserver/`. Same LabVIEW 26.3.1f1 64-bit. The IDE for this part was started by the
measurement, so it was ended by it (SIGTERM, saving nothing). Every run here has the user's `RunWhenOpened.Behavior=2` in force: label **A**; the
default install was not measured again. The relay VIs are `relay.aixml` (the second process) and `target.aixml` (runs in the IDE).

### Answer first

| Question | Answer |
|---|---|
| Does VI Server on make a second `labview <vi>` hand off to the running IDE? | **No.** Plain VI: count 1 -> 2. Run-when-opened VI: ran in the new process. Same as with VI Server off (`10-handoff-plain.txt`, `11-handoff-run.txt`). |
| Does a relay reach the resident IDE? | **Yes.** A stub in the second process opens `Open Application Reference` to `localhost:3363`, opens the target VI in the IDE and runs it; the target's `/proc/self/stat` shows the resident pid in every trial; the relay then quits its own process by itself. |
| Does the relay carry both rendezvous mechanisms and the two-server race? | **Yes.** Nonce in the target's path and path passed through `Ctrl Val.Set`: 20 of 20 trials each, 0 wrong-token connections, every target in the resident pid, every relay gone by itself. |
| What does the relay cost? | A second LabVIEW process of about 3.5 s (median 3.7 s over 80 relays, longest 15.6 s) that shows windows while it lives, and a modal dialog hazard (below). It is not a handoff in the spec's sense. |

### Setting

The user's change, read only (`91-conf-user-change.txt`): `labview.conf` gained `server.tcp.enabled=True`, `DoNotShowCRAWarningEnableTCP=True`
and `server.tcp.access="+192.168.1.231:+127.0.0.1:+localhost"` (the second is LabVIEW's own flag for the warning shown when TCP is enabled); no
`server.tcp.port` key, so the default port **3363**. LabVIEW also changed its own state keys in the same session (`NI_GSWContentIndex`,
`NI_GSWPosition`, `menuLoc.*`, `RecentFiles.pathList`). Baselines: end of the first measurement sha256 `d8eb5772...cc51`; baseline 3 (this
measurement's start) `d5a475f1...fc96`, 1344 B, mtime 13:20:41; end `98b0cef5...8820`, 1306 B, mtime 14:07:59. Settings lines identical to baseline 3 (checked);
LabVIEW's own state keys changed (`NI_GSWContentIndex`, `menuLoc.*`, `RecentFiles.pathList`) and it added **`autoerr=3`** (`91-conf-user-change.txt`). `labview64.conf` untouched.
Bind and list (`00-env.txt`, `ss -ltnp`): **the listener is `0.0.0.0:3363`** (all interfaces; 30 LabVIEW listeners in all, every one on `0.0.0.0`),
with the access list above. **Security finding for Slice 7 (not changed):** the VI Server port is reachable on every interface. The access list
names the machine's LAN address, loopback and `localhost`, but it did **not refuse** the addresses not in it: a relay opened an application
reference to `127.0.0.2` and to the docker bridge address `172.17.0.1` (neither in the list) and each answered `app=0` and then
`vi=7` (file not found: the remote server answered), the same as `localhost` and `127.0.0.1` (`20-access.txt`). A connection from another machine
was not tried (one machine). So for a client on this host the list is not an access control; assume the listener is open to the LAN until a remote
client shows otherwise.

### Handoff again, with VI Server on

Same procedure as the first measurement (`probe-relay.sh`): one IDE process, then `labview <vi>`.
Plain VI: `count 1 -> 2` (`10-handoff-plain.txt`). Run-when-opened VI: wrote `stat=<new pid>` (`11-handoff-run.txt`). So **spec assumption 2 as
written still fails**; the setting does not give a handoff through the VI Server port on this build. The script runs the relay rows only after
this result (a relay quits the process it runs in) and every relay refuses to quit when its own pid equals the resident pid.

### The relay

`labview relay.vi -- <target> <rendezvous or -> <mode> <report> <resident pid> [host]` (relay composed run-when-opened). In the second process:
`Application:Command Line Arguments` gives the words; `Open Application Reference` (machine `localhost`, port 3363) opens the IDE; `Open VI Reference` there
opens the target; for the argument mechanism `Ctrl Val.Set` writes the rendezvous path into the target's `rendezvous` control (the value goes in as XML through
`Unflatten From XML`, as in the Step 1.2 skeleton); `Run VI`; `Close Reference` x2; the error code of every step and the relay's own pid go to a report
file; `Quit LabVIEW` ends the process. The target is the rendezvous stub with a 1500 ms wait first (so a target ended by a closed reference cannot
have connected yet).

| Mode | Run VI | Result (`3-relay-*.txt`) |
|---|---|---|
| `wait`, nonce | Wait until done TRUE | target ran in the **resident pid**, token matched, relay quit itself after 3.4 s; report `app=0;vi=0;set=0;run=0;close=0` |
| `wait`, argument | Wait until done TRUE | same, 3.6 s |
| `wait`, argument, target composed run-when-opened | Wait until done TRUE | same: it runs and reports, with no check logged in the resident's log |
| `hold`, argument | Wait until done FALSE, relay keeps the reference 4 s | target ran in the resident pid, relay quit after 5.9 s |
| `nowait`, argument | Wait until done FALSE, reference closed at once | **no connection: the target never reported** (the relay was gone after 2.0 s) |
| `dispose`, argument | FALSE with Auto Dispose Ref TRUE, then the reference is closed at once | **no connection** either (relay gone after 2.0 s): `Auto Dispose Ref` did not keep the target alive past the close |

So a target started by `Run VI` ends when the relay closes its reference or quits, in the two forms measured with an early close. **For a bridge that runs
for hours, the relay process must stay alive as long as the bridge**, or a form that survives the reference must be found: `Auto Dispose Ref` with the reference closed at once did not keep it alive (the `dispose` row); `Auto Dispose Ref` with a
reference that is never closed explicitly, and `Asynchronous Call By Reference`, were **not measured**. Whether the target is gone from the IDE's
`All VIs In Memory` afterwards was not read (not measured).

What the relay process showed and did (`3-relay-*.txt`):
- **On screen:** for about 3 s a second set of LabVIEW windows (palette windows Controls, Functions, Tools, Context Help, Window monitor and the Getting
  Started window) opened and closed; no activation, licence or error window; the relay's LabVIEW log has no licence or activation line (only the DWarn
  below). No dialog needed a click in any relay row.
- **Lifetime:** 3.4 to 3.6 s for `wait`, 5.9 s for `hold` (4 s of it the hold), 2.0 s for `nowait` and `dispose`; ended by itself every time ("alive after 45 s" never
  happened in the final run). Race relays: 80 of them, 3.5 to 15.6 s, median 3.7 s.
- **Settings:** the `server.*`, `RunWhenOpened.*` and `DoNotShow*` lines of `labview.conf` were identical after every row; LabVIEW rewrote its own state keys
  (`NI_GSWContentIndex`, `menuLoc.*`, `RecentFiles.pathList`) and added **`autoerr=3`** (a counter LabVIEW keeps for internal errors and warnings; it is the DWarn
  path below). The second process did not touch the user's settings. No licence or activation check was hit.
- **Does the second process also serve 3363?** Not measured (port already in use; the conf is the same). The relay only used it as a client.

### The modal dialog hazard (a finding, with the cause not proven)

When a second process loads a VI while the resident IDE runs, LabVIEW often logs `DWarn ... VIObjCache::DoObjCacheInsert: FAILURE; err=0; sql=5` (SQLite
"busy": the shared `~/natinst/LabVIEW Data/VIObjCache/26.0.0r0/objFileDB.vidb`). **The next LabVIEW process started after one that logged a DWarn showed the
modal dialog "Investigate Previous Internal Error or Warning" and then did not run its VI until a human answered it**; a relay behind such a dialog hangs for
ever (seen: relay rows with no report, `alive` after 45 s, in two earlier runs whose transcripts were replaced by the final run). This session never
clicked it. `probe-relay.sh` therefore checks the previous process's log for a DWarn before each launch and, if there is one, starts a bare LabVIEW (it loads
no VI, so it logs no DWarn) and ends it, which hands the next real launch a clean log: 16 such resets in the final complete run (`80-resets.txt`). The dialog's text and buttons were not captured (no screenshot tool). The correlation (DWarn in the previous log, dialog in the next process,
`autoerr` in the conf) is measured; that the DWarn causes the dialog is not proven. **The user may see this dialog the next time they start LabVIEW**:
this session ended its last process after a bare start (clean log, `0` DWarn lines in both log files) to avoid that.

### Target VI and the run-when-opened prompt

A plain target and a run-when-opened target, opened and run through VI Server in the IDE, logged **no** `RunWhenOpenedSecurity` line in the resident's log
and raised no dialog (`40-prompt.txt`). With `RunWhenOpened.Behavior=2` a prompt could not appear in any case, so this does not show what the default
setting does for a VI Server open. Expected: no prompt (the VI is not opened by a file open); not measured under the default.

### An alternative without a second process: LabVIEWCLI (search only, nothing installed)

`ni-labview-command-line-interface` is **available** for this distribution from NI's apt feed (`/etc/apt/sources.list.d/ni-labview-2026-noble-full.list`,
`https://download.ni.com/ni-linux-desktop/LabVIEW/2026/Q3/f1/full/deb/ni-labview-2026/noble`): candidate `26.3.0.49372-0+f220`, 1.4 MB, depends on
`labview-2026-rte (>= 26.3.0)` and `ni-python-interface (>= 26.3.0)`, both installed here; `apt-get -s install` plans exactly one new package
(`60-labviewcli.txt`). It would need root to install and the VI Server TCP setting on (the port and an access list). Not measured: whether its binary is a
client that needs no LabVIEW process of its own on this platform, and whether it hits the same VI Server listener or LabVIEW's own start (the Step 1.2 container
runs `LabVIEWCLI` inside a LabVIEW start). The server could also speak the VI Server wire protocol itself; that protocol was not looked at.

### Findings (Step 14.1 with VI Server on)

1. **Assumption 2 is resolved differently, not by the setting:** a second launch is always a second process; the working route is the relay (a second
   process as a short-lived VI Server client) or LabVIEWCLI, and both need VI Server TCP on. The user's premise that Windows has it on today fits.
2. **Slice 6:** the rendezvous mechanisms carry over unchanged to a target running in the resident IDE: file `<port>-<token>`, owner-only, argument through
   `Ctrl Val.Set` or nonce beside the target. 40 of 40 trials correct, plus a control that proves the check can fail.
3. **Slice 7 (security):** `0.0.0.0:3363`, access list not enforced against local non-listed addresses (above). Decide loopback restriction or accept.
4. **Slice 9:** a relay launcher needs a second process alive as long as the bridge (unless Auto Dispose/ACBR keeps the target alive, unmeasured), the
   run-when-opened setting or a click for the relay VI itself (the relay is run-when-opened here), and a defence against the "Investigate Previous Internal
   Error or Warning" dialog, which a headless-free IDE cannot avoid on its own.
5. **Slice 4:** `server.tcp.enabled`, `server.tcp.access`, `DoNotShowCRAWarningEnableTCP` are the conf keys; the port key is absent at the default 3363.

## Step 14.1, third round: relay quit and LabVIEWCLI

Measured 2026-11-10 after the user installed `ni-labview-command-line-interface` themselves. Reproduce with `experiments/linux-bridge-hw/probe-round3.sh`
(`START_IDE=1` starts a normal, never headless IDE); transcripts in `experiments/linux-bridge-hw/transcripts/slice-14.1-linux-round3/`. The IDE for this
round was started by the measurement, so it was ended by it (SIGTERM, saving nothing), then one bare start/stop left clean logs (0 DWarn lines), so no "Investigate
Previous Internal Error or Warning" dialog should wait for the user. Same setting in force as round 2 (`RunWhenOpened.Behavior=2`, `server.tcp.enabled=True`): label **A**.
The CLI route needs **no** run-when-opened VI at all (starter and target are plain VIs), so it does not depend on that setting; a prompt-free run under the default
setting is expected, not measured.

### Answer first

| Question | Answer |
|---|---|
| A long-lived bridge in the resident IDE with no LabVIEW process left behind? | **Yes, by two routes.** (1) LabVIEWCLI runs a resident *starter* VI; the starter fire-and-forgets the target locally. (2) A relay does it remotely and quits. Both: the target answered 100 echoes with 0 failures **60 s after the launcher was gone**, in the resident pid, `Execution:State` = 2 (a control read of the same VI after the server hung up gave 1). |
| What makes the target outlive its starter? | `Run VI` with **Wait until done FALSE and Auto Dispose Ref TRUE**, and **no `Close Reference`** (not of the VI, not of the application reference) before the starter ends or the relay quits. The same call with the reference closed at once ended the target (round 2, `dispose` row, and `nowait`). |
| LabVIEWCLI installed as a package? | `dpkg -l`: `ni-labview-command-line-interface 26.3.0.49372-0+f220 amd64`, with `ni-python-interface 26.3.0.49370-0+f218`; binary `/usr/local/natinst/share/nilvcli/LabVIEWCLI` (symlink `/usr/local/bin/LabVIEWCLI`). |
| Is LabVIEWCLI a LabVIEW process? | **Not an IDE process, but a LabVIEW run-time process (corrected in round 4).** A native x86-64 ELF (8 shared libraries by `ldd`, none of mono or dotnet; it loads `nilvcli.so`); `ps` shows `LabVIEWCLI`. The `labview` count stayed **1 before, during and after** every call. Round 4 showed that each call opens the hidden windows of a LabVIEW run-time (`lvrt`, `Window monitor`, `Closed`, `Context Help`, off screen) for about 3 s; the claim here that there was "no LabVIEW runtime" rested on `ldd` alone and was wrong. The package depends on `labview-2026-rte`. |

### A. Can the relay quit early?

Long-lived target = `echo-target.vi`: connects back with the hello, answers one echo at once, then answers echo frames (4-byte big-endian length, then the payload) until
the server hangs up, with no read timeout. `echo-server.py` keeps the connection, waits until the script says go (60 s after the launcher is gone), then sends 100 echoes. The
`Execution:State` of the target is read 12 s before that through `LabVIEWCLI RunVI` of `P2_ExecutionState.vi` (so the check itself runs in the resident). Pass = launcher gone
quickly (a relay within 10 s), the target still in the resident pid 60 s later with 100 of 100 echoes, State 2 or 3, and no LabVIEW process but the resident.

| Variant | Launcher | Result (`2-*.txt`) |
|---|---|---|
| 1. `Asynchronous Call By Reference`, option 0x80 | n/a | **Not measurable here.** The repo composer refuses the node (`UNSUPPORTED: Node 'Start Asynchronous Call' is not a primitive this prototype knows`, `01-compose.txt`; its census had already refused the four NI asynchronous-call examples). So whether a remote application reference allows 0x80, and any error code, are **not measured**. `Call By Reference` composes but is synchronous. |
| 2. `Run VI`, Wait until done FALSE, Auto Dispose Ref TRUE, nothing closed (`relay-keep.aixml`, mode `dispose`) | relay in a second LabVIEW process | relay gone after **2.0 s**; hello after about 4 s; target alive **60.1 s** later, State **2**, **100 echoes, 0 failures**, pid = resident, labview processes left: 1; report `app=0;vi=0;set=0;run=0` |
| 3a. Resident starter (`starter.aixml`) started through **LabVIEWCLI** | `LabVIEWCLI -OperationName RunVI -LabVIEWPath <labview> -PortNumber 3363 -VIPath starter.vi` | CLI returned after **3.1 s**; target alive 60.0 s later, State 2, 100 echoes 0 failures; **no second LabVIEW process at any time** |
| 3b. The same starter started by a relay (`relay.vi`, mode `wait`) | relay | relay gone after 2.2 s; same result |

How the starter works (`starter.aixml`, composed on NI's fixed pane 4815 so RunVI accepts it): it reads `starter.args` beside itself (line 1 the target VI, line 2 the rendezvous
file), opens the target with `Open VI Reference` and no application reference (local to the instance it runs in), sets the target's `rendezvous` control through
`Ctrl Val.Set`, calls `Run VI` (Wait until done FALSE, Auto Dispose Ref TRUE), closes nothing and ends. The state values: 2 while running, 1 once idle (control row). Not measured: lifetimes
beyond 60 s (each session ran 60 s and one echo burst), what the target does when the user closes the IDE, and whether the VI stays in `All VIs In Memory` after it ended.

### B. LabVIEWCLI as the client

| Fact | Measured (`10-cli-basics.txt`, `3-B-*.txt`) |
|---|---|
| Command | `LabVIEWCLI -OperationName RunVI -LabVIEWPath /usr/local/natinst/LabVIEW-2026-64/labview -PortNumber 3363 -VIPath <vi> [args]`. **`-LabVIEWPath` is needed on Linux**: without it the error is `-350004 ... the path to LabVIEW is unknown`. It never started a LabVIEW (one was running). |
| Process | native ELF `LabVIEWCLI`, no mono, no dotnet, but a LabVIEW run-time process (round 4: it opens hidden `lvrt` windows); `labview` count 1 / 1 / 1 before, during, after |
| Call time | 3.0 to 3.8 s for a trivial VI (`P0_AppVersion.vi`, answer `26.3.1f1`); the starter 3.1 s |
| Blocks until the VI ends? | **Yes.** RunVI of the long-lived target itself (`echo-target.vi`, composed on NI's pane: Inputs[] on 11, Output/Failure on 2, CLI Process Return Code on 3) ran it in the resident pid and the CLI returned only when the server hung up (4.4 s in the immediate-echo case). No error 1031. |
| Target survives the CLI being killed? | **No.** The CLI of the long-lived target was killed (SIGTERM, 9.6 s in); 3 s later the target's State was 1 (idle) and the echoes failed. This is why the starter exists: it returns at once and the target is already detached. |
| After the starter returned | the target lives on without the CLI (variant 3a above); the CLI having ended on its own is the normal case |
| Overlapping calls | **A second `LabVIEWCLI` call that overlaps a running one fails:** `Error code : -350052 ... You cannot initialize the logger multiple times.` (measured with two concurrent `P0_AppVersion.vi` calls). The launcher must serialize CLI calls or retry; the race rows retried the failed one (in every trial exactly one of the two needed a second call: 13 times B, 7 times A). A long-running RunVI (the direct route) therefore **blocks every other CLI call** for its whole life: another reason for the starter. |
| LabVIEW's own state | every call writes `/tmp/lvtemporary_<n>.log` (removed by the script) |
| Prompt, dialog | none; the CLI shows no window; no dialog during any CLI call; no activation or licence message |

### Race: two servers at the same moment, by the two working routes

20 trials each (`4-race-cli.txt`, `4-race-relay-keep.txt`), all with long-lived echo targets and 100 echoes each: per trial two servers (own directory, 0700; rendezvous file 0600),
two launchers started back to back (two LabVIEWCLI starters, retried on -350052; or two early-quit relays), pass = each server got a connection with its own token from the resident pid,
from the target started for it, 100 echoes back, and the `labview` count back to 1. **CLI: 20 of 20. Early-quit relay: 20 of 20.** The crossed control (`49-control-crossed.txt`) was
flagged on both sides. `labview.conf` settings lines (server.*, RunWhenOpened.*, DoNotShow*, NewProj.*) identical at the end of every trial; LabVIEW changed only its own state keys
(`NI_GSWContentIndex`, `menuLoc.*`, `RecentFiles.pathList`; `91-conf-history.txt`). No "Investigate Previous Internal Error or Warning" dialog (0 resets were needed this round).

### Findings (third round)

1. **Slice 9 (launcher):** the route that leaves no extra LabVIEW process is *LabVIEWCLI RunVI of a resident starter VI*. It needs: the NI package `ni-labview-command-line-interface`
   (apt, 1.4 MB, root, same feed as LabVIEW), VI Server TCP on, `-LabVIEWPath`, and **serialization of CLI calls** (-350052). The starter must detach the target with `Run VI` (Wait until done
   FALSE, Auto Dispose Ref TRUE) and close nothing. The early-quit relay (a second LabVIEW process for 2 s, run-when-opened) is the fallback when the package is missing; it needs the
   run-when-opened setting or a click and carries the DWarn dialog hazard. The stub and starter are plain VIs for the CLI route.
2. **Slice 6:** a bridge started this way keeps its TCP connection for minutes and answers echoes; the server can reach the resident through the same rendezvous (file, token, hello).
3. **Slice 7:** the starter does not need a project; the target runs in the resident's main application instance (it is reachable through `Open VI Reference` there). The
   `NI.LV.Editor` context (Step 14.2) is not touched by this round.
4. **Not measured:** `Asynchronous Call By Reference` 0x80 (composer refuses `Start Asynchronous Call`); beyond 60 s; the default run-when-opened setting for the relay route; Windows.

## Step 14.1, round 4: default run-when-opened setting

Measured 2026-11-10 after the user set "Run when opened" back to the default. Reproduce with `experiments/linux-bridge-hw/probe-round4.sh` (`START_IDE=1`
starts a normal, never headless IDE; it stops if `labview.conf` still says `Behavior=2`); transcripts in `experiments/linux-bridge-hw/transcripts/slice-14.1-linux-round4/`
(main run) and `.../rerun-sessions/` (sessions only, with the final window check, see the note below). The IDE was started by the measurement (the user's was not
running), so it was ended by it (SIGTERM, then one bare start/stop for clean logs, 0 DWarn lines). Nothing was clicked by this session.

### Answer first: click-free: **YES, under the default setting**

| Check | Result |
|---|---|
| Default in force | `labview.conf` has **no `RunWhenOpened.*` line** (the `Behavior=2` line is gone; baseline 5 `78489d0a...`, 1247 B). `RunWhenOpenedPermissions.json` unchanged by the whole round: sha256 `24b02f9a...448b`, one entry (the round-1 `args-dump.vi` path). |
| **Control proves the default is in force** | `labview <run-when-opened VI>` from a fresh path (`c6d2a9fb160e8.vi`): the VI did **not** run in 20 s, the new process had a window titled **"Security Warning: Run When Opened"** (and the VI's front panel) and logged `RunWhenOpenedSecurity::CheckVIForRunPermission: CHECKING "c6d2a9fb160e8.vi"`. Not clicked; the process was ended after 20 s. (`40-control.txt`.) The user also saw the pop-up and reported "the pop up just came up to ask to run this vi"; that was a control process (an earlier, interrupted pass of this same control was ended by me as well). |
| LabVIEWCLI -> starter -> target, fresh directory, new names, 3 runs | **3 of 3 PASS, and 3 of 3 again in the rerun**: CLI back in 3.2 to 4.0 s (a hang would show at the 60 s limit), exit 0; **no Security Warning window**; **0 RunWhenOpened lines** in the resident's log; target alive in the resident pid 60.0 s later, `Execution:State` 2, 100 echoes 0 failures; `labview` count 1 before, during and after (`11/12/13-starter-cli.txt`). |
| NI's own CLI path: P0_AppVersion.vi alone from a fresh path | PASS: answer `26.3.1f1`, 3.2 s, no prompt window, no log line (`20-p0-alone.txt`) |
| New entry in `RunWhenOpenedPermissions.json`? | No: identical before and after (`30-permissions-after.txt`) |
| `labview.conf` | the settings lines (server.*, DoNotShow*, NewProj.*; no RunWhenOpened key) identical at the end; only LabVIEW's own state keys moved (`NI_GSWContentIndex`); end sha256 `15929bc3...afc`, 1294 B, mtime 15:27:00 (`90-conf-end.txt`) |

**Why it is click-free.** The prompt belongs to VIs that carry the **Run when opened** flag, opened by file (`labview <vi>`). The starter and the target are plain VIs opened and run
through VI Server by `LabVIEWCLI RunVI` and `Open VI Reference`/`Run VI`; LabVIEW logged no check for them, whatever the setting. So the CLI route does not depend on the setting at all,
**but the early-quit relay does not have this property**: `relay.vi` is a run-when-opened VI launched with `labview <vi>`, exactly the control, so under the default setting it needs a human
click (or the user's `Behavior=2`).

**Note on the main run's row `starter-cli-2` (FAIL).** The first window check counted any new window name outside four hidden ones, and in run 2 one of LabVIEWCLI's own windows named
`LabVIEW` appeared during the state-probe call. Everything else in that run was as in runs 1 and 3 (no Security Warning window, 0 log lines, 3.2 s). The check was narrowed to the
title measured in the control (Security / Warning / Run When Opened) and unknown other names are listed, not failed; the three sessions were repeated with that check in `rerun-sessions/`
(3 of 3 PASS, the other new windows are listed per run; the window sizes in that column are unreliable, they carry window ids). The control was not repeated (it would show the pop-up again).

**LabVIEWCLI is a LabVIEW run-time process.** Each call opens off-screen windows named `lvrt`, `Window monitor`, `Closed` and `Context Help` (same class `labview`/`LabVIEW64-2026` as the IDE's
windows) for its 3 s. This corrects round 3's "no LabVIEW runtime". It is not an IDE (`labview`) process and it never showed a licence, activation or prompt window. Whether it needs an X display
was not measured.

### Findings (round 4)

1. **Slice 9:** the LabVIEWCLI -> starter -> target route is click-free under the default setting, on this build, **provided the starter and target are not flagged "Run when opened"**; it needs no
   change of the user's LabVIEW options. The relay route is not click-free under the default (its VI is run-when-opened). Prefer the CLI route; keep the relay as a fallback that needs the click or the setting.
2. **First run only?** A permission is stored per VI path and GUID after the user's first answer (round 1). The CLI route needs none. Whether a fresh *project* VI flagged run-when-opened that arrives
   through the CLI (e.g. a user's own VI) would prompt was not measured.
3. **Slice 4/7:** the prompt window title on this build is "Security Warning: Run When Opened" (the hook for a `modal window` check if the launcher ever has to detect it).

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

**LabVIEWCLI and early-quit rows (third round; measure on Windows after the handoff check).** Windows has `LabVIEWCLI` natively. After the count test, run from the repository root
`LabVIEWCLI -OperationName RunVI -LabVIEWPath $LV -PortNumber 3363 -VIPath C:\lvmcp-hw\starter-r1.vi` with a starter composed like the Linux one (`starter.aixml`, `echo-target.aixml`, pane 4815
as RunVI needs; `starter.args` beside the starter holds the target path and the rendezvous path with backslashes), against a running LabVIEW, `rendezvous-server.py` replaced by
`echo-server.py` (`--trigger` file, `--echoes 100`). Record: whether LabVIEWCLI starts any LabVIEW process (it must not), the process count before/during/after, the CLI duration, whether
a second overlapping CLI call fails with -350052 as on Linux, whether the target answers 100 echoes 60 s after the CLI ended, and `Execution:State` of the target through
`P2_ExecutionState.vi` run by the CLI. Repeat with `relay-keep.aixml` only if `LabVIEW.exe <vi>` does not hand off (see the warning below).

**Default run-when-opened setting (round 4; ask the same question on Windows).** With LabVIEW's default (no `RunWhenOpened`-style prompt answer stored, setting not "always allow"): (1) control: launch a
run-when-opened VI by `LabVIEW.exe <vi>` from a fresh path, note whether the prompt appears and its title, do not click, end the process you started after 20 s; (2) `LabVIEW.exe <vi>` hand-off of a
plain VI and of a run-when-opened VI: does the prompt appear in the *running* instance, and does the VI run in it?; (3) the LabVIEWCLI -> starter -> target route in three fresh directories: any prompt, CLI
back in seconds, target alive 60 s later with 100 echoes. Record the prompt's window title and any stored permission file on Windows. The Linux result: the CLI route is click-free, the run-when-opened relay is not.

**Relay row (add to the Windows session, VI Server TCP must be enabled on port 3363; on Windows it normally is).** The relay is the G-CLI-style route measured on Linux
(section "with VI Server TCP enabled"). Windows has `LabVIEWCLI`, so measure both: (a) the same relay (`relay.aixml`: second process opens `localhost:3363`, opens and
runs `target.aixml` in the running LabVIEW, quits its own process), and (b) `LabVIEWCLI -OperationName RunVI` against the running LabVIEW with a target (as the Step 1.2
Windows row does). On Windows the first thing to find out is whether `LabVIEW.exe <vi>` hands off at all (it normally does; if the count stays 1 the relay is not needed there).
Evidence of the process the target ran in: the stubs report `stat=` empty on Windows, so use `Application:Version Number` plus the process count before and after plus
`path=`, and record the LabVIEW process id the relay's quit ended (`(Get-Process LabVIEW).Id` before and after; the resident id must not change). Commands, with `$P` the process
count: compose `relay.aixml` with `RunOnOpen=1` and `target.aixml` plain (`py -3 experiments\linux-bridge\compose.py ... --lv-version $major`, `AIXML2VI_TRY_WIRES=Refnum.TCPNetConn`),
start a `rendezvous-server.py --dir C:\lvmcp-hw\r1 --file rv.txt --result C:\lvmcp-hw\r1\result.json`, then
`& $LV C:\lvmcp-hw\relay-run.vi -- C:\lvmcp-hw\r1\target-r1.vi C:\lvmcp-hw\r1\rv.txt wait C:\lvmcp-hw\r1\relay.report 0 localhost`
(the `0` is a resident pid that no real process has: the relay's Linux-only safety against quitting the resident compares `/proc/self/stat`, which Windows lacks, so on Windows the
relay compares nothing and WILL quit the process it runs in. **Only run it if the count test above showed that `LabVIEW.exe <vi>` starts its own process; if it hands off, the
relay would quit the running LabVIEW. Do not run the relay in that case.**). Record: relay process lifetime, whether the target connected with the right token, the relay report line, any
prompt or dialog, and the same for 20 two-server trials per mechanism if time allows. Record the result as one row each.

**Windows 2026 64-bit result: PENDING.** (Replace this line with the table.)
