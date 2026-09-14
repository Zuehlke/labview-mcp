# A cold build, end to end — and the three things it caught

One project built from nothing on 2026-09-14, deliberately using every capability this toolset
has: an interface with a scriptable method, two implementing classes, a class under test with
accessors and a real method, an LMock mock generated headlessly, and an LUnit suite of four tests
including an interaction test. Artefacts under `C:\temp\Thermostat`.

The point of writing it up is not the project. It is that a cold run of the *whole* chain found
three things no single-tool test had: one operational trap, one remedy that was previously manual,
and one genuine defect.

## 1. What was built

| artefact | how |
|---|---|
| `Temperature Source.lvclass` — an INTERFACE, one method `Read Celsius.vi` (dynamic dispatch) | `lvai_create_interface` + `lvai_add_class_method` |
| `Fixed Temperature Source.lvclass` — implements it, field + accessors + the override | `lvai_create_class` with `parentInterfaces`, `lvai_create_accessors`, `lvai_add_class_method` |
| `Thermostat.lvclass` — two fields, four accessors, `Evaluate.vi` (hysteresis decision) | same three tools; `Evaluate` reads its own fields through the placeholder + swap route |
| `Mock Temperature Source.lvclass` | `lvai_generate_mock_class`, one call, 2.8 s |
| `Thermostat Test.lvclass` — four LUnit tests | `lvai_create_class` off LUnit's `Test Case.lvclass`, `lvai_lunit_add_test_method`, `lvai_swap_subvis` |

Final state: **4 tests, 0 failures**, all five classes listed in the `.lvproj`.

Two authoring notes worth keeping. **An `<Indicator>` REQUIRES an `inputs` attribute** — an unwired
output is refused (`missing required attribute 'inputs'`), which is what an interface contract VI
naturally wants; wire a constant. And `lvai_add_class_method`'s `validateFirst` earned its keep
here: it stopped on that fault instead of converting a broken diagram.

**`lvai_create_interface`'s own description says interface methods must be added in the IDE. That
is stale** — `CLAUDE.md` corrected it on 2026-09-07 and `lvai_add_class_method` does it. Believing
the tool description would have cost the interface its contract.

## 2. THE `.lvproj` CLOBBER, and the rule that prevents it

**Measured: two class entries silently deleted from the project file.**

`lvai_add_class_method` finishes with `projectLeftOpen: true`. `lvai_create_class` then writes its
new class's entry into that same `.lvproj` **as a file**, with LabVIEW not involved — which is
correct in isolation and says so. But LabVIEW is still holding its own copy of that project from
before the edit, and the next close **saves that stale copy over the file**. Both entries were
gone; `lvai_create_accessors` then answered `Error 1055` with `classPathsSeen` listing only the one
class LabVIEW knew about.

This is `CLAUDE.md`'s "LabVIEW's save-on-close can write a stale in-memory project over the file"
seen from a new direction: the edit was not made by a human between calls, it was made by another
*tool* in the same chain.

**THE RULE: close the project before any `lvai_create_class` or `lvai_create_interface`.** Those
two edit the `.lvproj` directly, so nothing may be holding it. `lvai_add_class_method` and
`lvai_create_accessors` leave it open, so the close is not optional between them — it is the
handover. And `CLAUDE.md`'s standing advice applies without exception: **read the `.lvproj` after
every close.**

## 3. `projectDidNotBecomeActive` HAS A PROGRAMMATIC REMEDY

`lvai_open_file` answered `No Error` with `projectBecameActive: false`. The documented cause is
that LabVIEW did not have the foreground, and the documented remedy has always been a human
action — "bring its window to the front".

It is automatable, measured here: `SetForegroundWindow` on LabVIEW's `MainWindowHandle` via
PowerShell, then open again, and the second open took. Two lines:

```powershell
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;
  public class Fg{[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int n);}'
$p = Get-Process LabVIEW | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
[void][Fg]::ShowWindow($p.MainWindowHandle, 9); [void][Fg]::SetForegroundWindow($p.MainWindowHandle)
```

Worth knowing because the alternative was to stop and ask a human, and this failure cost 270 s of
wall clock the first time anyone met it.

## 4. THE DEFECT: re-running `lvai_add_class_method` over an existing member destroyed the class

This is the serious one, and it contradicts what the tool documents about itself.

The sequence, all reported green:

1. Suite passing, 4/4. `Thermostat.lvclass`: `privateDataBytes` **5691**, five members, every VI
   `execState 1`.
2. `Evaluate.vi`'s AIXML edited (one comparison inverted, as a negative control) and
   `lvai_add_class_method` re-run over it. The answer was **`ok: true`**, `terminalsRetyped: 4`,
   `verifiedOnDisk: true`, **`memberAlreadyExisted: true`**.
3. The suite then reported all four tests **`Broken`**, not `Failed`.

`Broken` rather than `Failed` is the whole tell: the assertions never ran, so this was not the
negative control working.

What was actually damaged:

| probe | result |
|---|---|
| `Evaluate.vi` AIXML export | **correct** — every call swapped, every wire present |
| `lvai_exec_state` on `Evaluate.vi` | `eBad`, error type 2000008 |
| `lvai_exec_state` on `Read Setpoint C.vi` — an accessor **never touched** | `eBad`, error type 8 |
| `lvai_describe_class` | structurally fine: 5 members, 2 fields — but `privateDataBytes` **5731**, up from 5691 |
| full LabVIEW restart, then re-probe | **still `eBad`** |

So the damage is **on disk**, not in memory, and it reached members the call never named. The
private data control was rewritten — 40 bytes larger — and every accessor binds to that control,
which is exactly the shape of "all members break at once".

**The restart hypothesis was tested and refuted**, and is recorded because it was the obvious guess:
LabVIEW was killed and restarted, and both VIs read `eBad` afterwards unchanged.

**Repair.** None of the in-place routes worked. `lvai_create_accessors` with an explicit
`fromField` does not repair, it DUPLICATES — NI's wizard appends a number rather than refusing an
existing name, and the tool detected that itself and answered `ok: false` with the cleanup
instructions, which is good behaviour worth noting. What worked was rebuilding: delete the member
`.vi` files, `lvai_create_class` with `overwrite`, accessors, re-add the method, re-swap. After
that `privateDataBytes` was **5691** again and the suite was 4/4 green.

### SETTLED 2026-09-14, and the obvious suspect was wrong

Reproduced on a MINIMAL fixture — one `double` field, its two wizard accessors, and one method
whose diagram is nothing but the class wire and the error chain, so `lvai_add_class_method` is the
only variable:

| arm | result |
|---|---|
| create + accessors + add | `privateDataBytes` **5454**, every member `eIdle` |
| **one re-run**, default pane | `privateDataBytes` **5466**, and `Read Value.vi` — an accessor the call never named — **`eBad`, error type 8** |
| fresh class, **one re-run with `panePattern: 0`** | **`eBad` just the same** |

So the class does **not** need to have been run: the Thermostat's suite having executed it was a
coincidence, not a precondition. And the **pylabview `conpane` rebuild is NOT the cause** — the
arm that skips it entirely breaks the class identically. That was the best-supported hypothesis
and it is refuted.

What remains is the `member` step itself, where `AddItemFromMemory` answers `1004` for an item the
class already lists. The tolerance filter makes that survivable for the *method*, and evidently
still leaves the following class save writing something the accessors no longer match.
`memberAlreadyExisted` is the only input separating the clean run from the destructive one.

**One flawed arm is worth recording.** The first `panePattern: 0` run was made against the class
the previous arm had already broken, and it showed only that `privateDataBytes` did not grow
*further*. That is not the same question: the growth metric is not the corruption metric, and
reading it as one briefly produced the opposite conclusion. The arm had to be redone from a fresh
class.

### The fix

`lvai_add_class_method` now **drops the class's entry for the member first**, in the
project-closed window, so the run takes the first-run path that is measured clean — the same
`dropExistingMembers` step `lvai_lunit_add_test_method` has had since 2026-09-08. Two guards go
with it, because a repair that silently does nothing is the failure this whole page is about:

- **without `projectPath`** the entry cannot be dropped safely (LabVIEW's own save would undo the
  edit), so a re-run is REFUSED — `memberAlreadyListedWithoutProject` — rather than allowed
  through with a green answer;
- **when the remover declines** the file because it is not the one-item-per-block shape it was
  measured against, the call stops with `memberEntryCouldNotBeDropped` instead of proceeding
  hopefully.

`privateDataBytes` remains the cheapest signal that something moved under the accessors: it needs
no LabVIEW, and nothing else in the chain reported anything at all.

### Accepted 2026-09-14, against the arm that caused the damage

Re-measured on a fresh minimal fixture — `C:	emp\RerunProbe`, one `double` field, its two wizard
accessors, and `Touch.vi`, whose diagram is nothing but the class wire and the error chain, so
`lvai_add_class_method` is the only variable. The call is the one that produced the table above:

| what the destructive run did | what the fixed run does |
|---|---|
| `memberAlreadyExisted: true` | **`false`** — the prologue's `dropExistingMembers` removed `Touch.vi` first, so it takes the first-run path |
| `privateDataBytes` 5454 → **5466** | **5458 → 5458** |
| `Read Value.vi` — an accessor the call never named — **`eBad`, error type 8** | **`execState 1`**, and so are the other two members |

**A second check that does not go through LabVIEW at all**: the accessor `.vi` files still carried
their creation timestamps afterwards. The run did not rewrite them — it never opened them. That is
worth preferring to `execState`, because it cannot be satisfied by a healthy in-memory copy.

The refusal was measured the same way: without `projectPath`, `memberAlreadyListedWithoutProject`
naming `Touch.vi`, with no file on disk touched.

**What was NOT verified: the foreground retry of §3.** Its precondition could not be reproduced —
with another application fronted, and again with LabVIEW's window MINIMIZED, the project became
active anyway and `foregroundRetry` correctly reported `null`. So the field is wired and reports
"not needed" honestly; the retry branch itself is still unexercised outside the cold build that
prompted it.

**One cosmetic defect found while reading that output, now fixed.** `dropExistingMembers` and
`openProject` were both hardcoded `order: 2` — in the one report where the fix's correctness rests
on the drop happening INSIDE the project-closed window, the two steps read as simultaneous. It is a
running counter now, which it had to be anyway: the drop is conditional, so on a first run
`openProject` really is step 2.

## 5. A typing rule for LMock's `Verify`

`Verify.vi`'s `Mock` input must be fed from a wire typed as the **mock class**. Wiring it from the
system under test's own interface-typed output is `eBad` — an interface is not a `Mock`, and the
mock class only happens to be both. NI's own example takes it from the output of the recording
call, which is dynamic-dispatch typed on the mock; that is not a stylistic choice.

Ordering still has to come from somewhere: feed `Verify`'s `error in` from the SUT's `error out`,
so the assertion cannot run before the code it is checking.
