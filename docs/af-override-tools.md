# Actor Framework overrides as tools

`lvai_create_override`, `lvai_af_add_hook`, `lvai_af_remove_hook`, and the loaded-callers guard in
`lvai_add_class_method`. This page says what each one does, what it refuses, and what is measured.
The Actor Framework itself (messages, libraries, the scope rules) is in `labview-actor-framework.md`.

**Status, 2026-10-10:** the routes these tools automate were measured by hand with helper VIs on a
pilot project and on throwaway actors (dates are given per row). The tools and their new helpers are
**not yet run in LabVIEW**. They are linted offline (`scripts/aixml_lint.py`, `AixmlCheck`) and unit
tested without LabVIEW. Treat every answer as unverified until the measurement list in §7 has been
run.

"The kit" below means the hand-run helper VIs of a companion LabVIEW skill whose measured routes
these tools automate; its shape letters (A, B, B-after, C, D) are kept so the two can be compared.

## 1. Why overrides need their own tools

- **AIXML cannot author `Call Parent Class Method`.** A generated override is `eBad` whatever its
  diagram (`labview-actor-framework.md` §4). An Actor Framework override built with
  `lvai_add_class_method` is either broken or has no Call Parent. Without Call Parent, Actor Core
  has no message loop, Stop Core does not stop its auto-stop children, and Handle Error has to
  handle 43 itself.
- **So an override comes from NI's provider** `CLSUIP_CreateOverride` (the IDE's "New > VI for
  Override"), and its body is **scripted** onto the diagram: a member VI, the *hook*, placed and
  wired by VI Server.
- **The order in a build:** class → typedef field binding → accessors → methods → pane-typedef
  binding → library → messages → overrides → hooks. Each hook VI is a class member before it is
  hooked.

## 2. `lvai_create_override`

Inputs: `classPath`, `parentMethod` (an AF lifecycle name, or the path of a plain parent class's
`.vi`), optional `projectPath`, optional `newViPath`.

Checked from the files before LabVIEW, each with a named error:

| check | refusal |
|---|---|
| the class file exists and reads | `classNotFound`, `classUnreadable` |
| an AF name: the class's parent links reach `Actor.lvclass`; the parent `.vi` is taken from **that** installation's `vi.lib\ActorFramework\Actor\` | `notAnActor`, `parentMethodMissing` |
| a `.vi` path: a `.lvclass` beside it (or up to two folders up) lists it; it is not static; the class descends from that class | `parentMethodNotAMember`, `parentMethodIsStatic`, `notADescendant` |
| the class does not list the method yet, no file sits at the new path, its folder exists, the file name is the parent's | `overrideAlreadyExists`, `fileExists`, `folderMissing`, `badArguments` |

- **`vi name in memory` is derived, library-qualified.** It is `<Lib>.lvlib:<Class>.lvclass:<file>`
  from the class's `NI.Lib.ContainingLib`. A shorter name answers `open new vi error` and nothing is
  saved (measured on a pilot project).
- **Project.** With `projectPath` the project is opened and must become active. Otherwise an
  already active project is required (`noActiveProject`).
- **Stages.** The helper `scripts/lvai_create_override.xml` reports one error cluster per stage.
  The first non-zero one becomes `failedAtStep`:
  - `openParentMethod`
  - `openClass`
  - `createOverride`
  - `openNewVi`
  - `saveNewVi`
  - `saveClass`
  - `closeReferences`

  Each stage carries a hint.
- **Verify, no LabVIEW.** The `.lvclass` is read again: the member is listed, the file is on disk,
  and the scope matches. AF lifecycle methods are protected; a plain override keeps the parent's
  scope. Dynamic dispatch comes from the class file, or from the VI's pane through pylabview
  (`PaneDispatch`). `ok` needs listed + on disk + dispatch not false.
- **Pane.** For an AF method the answer carries the measured pane (§4). For a plain override it
  says to read `lvai_vi_terminals`.

Measured: the helper route on `Message.lvclass:Do.vi` (2026-09-17, `labview-actor-framework.md`
§4) and on four AF overrides of a throwaway actor (2026-10-09). The tool: **unmeasured**.

## 3. `lvai_af_add_hook`

Inputs:
- `overridePath`, `classPath`, `hookViPath`, `shape`
- `wireError`, `sourceHookName`
- name overrides: `classTerminalPrefix`, `hookClassIn`/`Out`, `hookErrorIn`/`Out`
- `allowHandleErrorBeforeCallParent`, `allowSecondInstance`, `dryRun`, `projectPath`

### 3a. Shapes

`<C>` is the class's terminal prefix (the class name).

| shape | kit name | wires | measured by hand |
|---|---|---|---|
| `branch` | A | panel `<C> in` → hook `<C> in`; nothing cut | Actor Core helper loop, pilot project |
| `series` | B | the wire at panel `<C> in` (and `error in`) is cut: panel → hook in, hook out → the old far end (Call Parent) | Actor Core, both blocks, 2026-10-09 |
| `seriesAfter` | B-after | the wire at panel `<C> out` (and `error out`) is cut: Call Parent's output → hook in, hook out → panel | Pre Launch Init, class + error (error 5000 reached Launch), 2026-10-09; a plain override, pilot |
| `branchFromNode` | C | the named series hook's `<C> out` → hook `<C> in`, by `lvbd_connect_by_names` | pilot project |

- Default `wireError`: series shapes wire the error where the pane has the terminal; branch shapes
  do not.
- Not built here: the flat-sequence stop hook after Actor Core's Call Parent (kit shape D) and its
  `close` join (D-close).

### 3b. Panes built in

| override | inputs (conIdx) | outputs (conIdx) | evidence |
|---|---|---|---|
| Pre Launch Init | `<C> in` 11 | `<C> out` 3, `error out` 0 | measured; **no `error in`** |
| Actor Core | `error in` 8, `<C> in` 11 | `Actor out` 3, `error out` 0 | measured; `Actor out` is typed Actor |
| Stop Core | `<C> in` 11, `final error code` 10 | `<C> out` 3 | measured; no error terminals |
| Handle Error | `error in (no error)` 8, `<C> in` 11 | `<C> out` 3, `error out` 0, `stop actor?` 2 | measured |
| Handle Last Ack Core | `error in (no error)` 4, `Last Ack` 6, `<C> in` 7 | `<C> out` 3, `error out` 0 | **unmeasured**, read offline from production code |

A plain override is assumed to have `<C> in`, `error in`, `<C> out` and `error out`. The panel
read-back confirms this or refuses it.

### 3c. Refusals and warnings

Everything below is decided before the diagram is touched. The refusals:

| refusal | why |
|---|---|
| `hookInputUnfed` | A **required** hook input that no planned wire feeds breaks the override, and only a cold exec state shows it (measured, pilot). |
| `handleErrorHookBeforeCallParent` | A `series` hook on Handle Error sees 43 at every normal stop (1608 at an emergency stop), so a hook chained on that error does nothing then. The parent also returns `stop actor?` TRUE whatever the hook did. Use `seriesAfter`; `allowHandleErrorBeforeCallParent` keeps an existing shape, with a warning. |
| `actorOutIsActorTyped` | `seriesAfter` on Actor Core would feed an Actor-typed wire to a child-typed hook. |
| `wireIsBranched` / `panelTerminalNotWired` | A series insert deletes the whole net at the panel terminal and reconnects one far end. So the net must be exactly one plain wire: place series hooks first, then branches. |
| `hookTerminalMissing`, `hookTerminalWrongDirection`, `overrideTerminalMissing`, `noErrorTerminal` | a name or a direction that does not fit |
| `sourceHookNotFound` / `sourceHookAmbiguous`, `hookAlreadyPlaced` | a re-run would place the hook twice |
| `overrideNotAMember`, `noActiveProject`, `projectDidNotBecomeActive` | the override or the project is not ready |

The warnings:
- A recommended hook input left unwired.
- A Pre Launch Init hook whose error cannot fail the launch.
- An Actor Core series hook with an error wire: the error skips Handle Error and the actor ends
  silently; Launch reports 0 and the next `Send` gets 1556 (measured).
- The unmeasured combinations: `seriesAfter` on Stop Core, Handle Error and Handle Last Ack Core.

### 3d. Route

1. Read the hook's terminals and their `connection` from its AIXML export.
2. Run `scripts/lvai_af_hook_read.xml`, which is read-only. It lists:
   - the panel terminal names, and per terminal the number of ends on its wire;
   - the top-level subVI names;
   - the node classes (does a `CallParentNode` show?).
3. Plan by name in C# (`Infra/AfOverrides.cs`) and check the plan. `dryRun` stops here and returns
   the plan.
4. Read the exec state before.
5. Place and wire with `scripts/lvai_af_add_hook.xml`, generated with `lvbd_connect_by_names`
   loaded. It does not save. Series and branch wires use the kit's measured panel-terminal
   calls (`{LV.Control} Terminal`, `{LV.Wire} Delete`, `{LV.Terminal} Connect Wire`). The node
   pairs of `branchFromNode` use `lvbd_connect_by_names`, because a panel terminal is not a
   `ref{LV.Node}`.
6. **Save in a separate run** (`scripts/lvai_af_save_members.xml`), and only when the placement was
   clean.
7. Read the exec state after.

`ok` needs all of these:
- no stage error;
- nothing under `Missing`;
- `Connected` equals the planned wire count;
- a clean save.

On a failure the edit is in memory only, so close the project without saving to discard it.

Every VI is reached through the **active project's application instance**. An edit made in another
instance reports success and changes nothing (`dqmh-scripting.md`). The exec states in the answer
are read in that instance: read the override and its sibling overrides **cold**, project closed,
before trusting them.

## 4. `lvai_af_remove_hook`

Inputs: `overridePath`, `classPath`, `hookName` (path, file name or VI name), `occurrence`,
`resaveSiblings` (default true), `dryRun`, `projectPath`.

1. Read the diagram (`lvai_af_hook_read`) and resolve the hook among the top-level subVIs:
   - none matches: `hookNotOnDiagram`;
   - several and no `occurrence`: `hookNodeAmbiguous`;
   - an `occurrence` out of range: `badArguments`.
2. `scripts/lvai_af_remove_hook.xml` deletes exactly that node (the kit's delete removed every node
   with a terminal name) and runs `BD.Remove Bad Wires`. It reports the exec state before and after.
3. Save the override. Then save the class's other lifecycle overrides **only where they are
   modified** (FP or BD modification bitset non-zero), and then the class.

**Why the siblings.** A node deleted from a branched wire leaves a loose end. The override and a
sibling override of the same class then read state 0, "error of type 1000088". Remove Bad Wires on
the edited VI cleared both (measured, pilot project). A sibling that carries someone's own unsaved
edits is saved too: pass `resaveSiblings: false` when that matters.

A **series** hook cannot be removed this way: the wire through it goes with it. Restore the bare
override from version control (project closed) and re-hook every shape in order (B, C, A).

## 5. `lvai_add_class_method` on a re-run

**The loaded-callers guard.** A re-run over a member that a **loaded** VI calls fails the re-add
with Error 56003, and by then the member entry is already gone (measured with 1.7.2–1.8.5). The
caller can be an override hook or any VI held in memory. So, before anything changes:

1. If the given project is not active, it is opened. Opening it loads the class, and with it every
   member that calls the method.
2. `scripts/lvai_member_callers.xml` takes each member's library-qualified name and, in the
   project's instance, reads:
   - `Application:All VIs In Memory`, to see whether the member is loaded at all (a member that is
     not loaded is never opened, because opening would load it);
   - `Callers' Names` of each member that is loaded.
3. Any caller: refused with `memberHasLoadedCallers`, listing the callers per member.
   - The way out is to unhook first: `lvai_af_remove_hook`, or `lvai_swap_subvis` onto a
     placeholder.
   - Then re-run and hook or swap back.
4. The check cannot run: refused with `loadedCallersUnchecked`. `checkLoadedCallers: false` skips
   the check.

A caller opened outside the project, in the default instance, is not seen.

**Scope and folder.** A re-add puts the member at the class root as **public**. With
`keepPlacement` (default):
1. Before the drop, the member's `NI.ClassItem.MethodScope` and `NI.LibItem.Scope` lines and its
   folder chain are read from the class file.
2. After the add, the project is closed, the class file is edited, and the project is opened again.
   This happens only when a member was non-public or in a folder. The step is `restorePlacement`.

The edit (`Infra/ClassMemberPlacement.cs`):
- is text, byte-preserving;
- abandons the file on any shape it does not know;
- puts back **both** scope lines. LabVIEW's next save resets a MethodScope changed alone; that
  route is measured by hand.
- moves the block into its folder. **Unmeasured.**

## 6. Helpers

| script | does | saves | state |
|---|---|---|---|
| `lvai_create_override.xml` | NI's `CLSUIP_CreateOverride`, then saves VI and class | yes | measured 2026-09-17 / 2026-10-09 |
| `lvai_af_hook_read.xml` | lists panel terminals with wire ends, subVI names, node classes | no | unmeasured |
| `lvai_af_add_hook.xml` | places one hook; series cut, branch tap, node pairs | no | unmeasured; its calls are the kit's measured ones |
| `lvai_af_remove_hook.xml` | deletes one hook node, Remove Bad Wires | no | unmeasured |
| `lvai_af_save_members.xml` | saves listed VIs (optionally only modified ones), then the class | yes | unmeasured as a list; single VI measured |
| `lvai_member_callers.xml` | loaded members and their callers | no | unmeasured |

All of them are generated under a throwaway name and run through the typed run helper, as
`lvai_dqmh_place_handler`'s helpers are.

## 7. What must be measured before this is trusted

Run each item on a throwaway actor in a throwaway project. Read every exec state cold, with the
project closed.

1. **`lvai_create_override`** for each lifecycle method:
   - an actor in no library;
   - an actor in a `.lvlib`;
   - a plain parent `.vi`;
   - a refusal: `overrideAlreadyExists`.
2. **`lvai_af_add_hook`**, one run per shape:
   - `branch` on Actor Core;
   - `series` on Actor Core with error;
   - `seriesAfter` on Pre Launch Init with error, with a forced error that must reach Launch;
   - `branchFromNode` after a series hook;
   - `seriesAfter` on Stop Core and on Handle Error;
   - `dryRun`;
   - the refusals `hookInputUnfed`, `wireIsBranched` and `handleErrorHookBeforeCallParent`.

   Also check whether `lvai_af_hook_read` lists a `CallParentNode`.
3. **`lvai_af_remove_hook`** on a branch hook whose wire branches. The override and its sibling
   overrides must read state 1 cold afterwards.
4. **`lvai_add_class_method`** re-run:
   - over a hook an override calls: expect `memberHasLoadedCallers` and an unchanged class file;
   - over a protected member in a folder: expect scope and folder back, `lvai_describe_class`,
     state 1 cold.
