# Actor Framework overrides as tools

`lvai_create_override`, `lvai_af_add_hook`, `lvai_af_remove_hook`, and the loaded-callers guard in
`lvai_add_class_method`. This page says what each one does, what it refuses, and what is measured.
The Actor Framework itself (messages, libraries, the scope rules) is in `labview-actor-framework.md`.

**Status, 2026-10-10:** the routes these tools automate were measured by hand with helper VIs on a
pilot project and on throwaway actors (dates are given per row). **The tools were then run in
LabVIEW 2026 Q3 (32-bit) on 2026-10-10**, on two throwaway actors (one in no library, one in a
`.lvlib`) and a plain child class; §7 says per item what passed. Two things did not: the unsaved-edit
check reads 0 for edits made by scripting (§4), and a `vi`-only re-run of `lvai_add_class_method`
dropped a member (now refused, §5).

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
§4) and on four AF overrides of a throwaway actor (2026-10-09). The tool, 2026-10-10: all five
lifecycle overrides on an actor in no library and on one in a `.lvlib` (library-qualified name),
and one plain override of a protected parent method (scope kept). Every answer `ok`, every override
state 1 cold. `overrideAlreadyExists` answered in 0.1 s with no LabVIEW call.

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
- **The tool, measured 2026-10-10:** `branch` on Actor Core (the hook ran while the actor ran);
  `series` on Actor Core with error (export: panel -> hook -> Call Parent on both wires);
  `branchFromNode` off that series hook (`sourceHookMatched` names the qualified node; the branch
  logged 1 ms after the series body); `seriesAfter` on Pre Launch Init with error (the hook's error
  5000 came out of Launch, Actor Core never ran), on Stop Core (class only) and on Handle Error (class
  and `error out`): at a Normal Stop the Handle Error hook logged code 0 (the parent had cleared 43)
  and the Stop Core hook ran 7 ms later. Every override state 1 cold.

### 3b. Panes built in

| override | inputs (conIdx) | outputs (conIdx) | evidence |
|---|---|---|---|
| Pre Launch Init | `<C> in` 11 | `<C> out` 3, `error out` 0 | measured; **no `error in`** |
| Actor Core | `error in` 8, `<C> in` 11 | `Actor out` 3, `error out` 0 | measured; `Actor out` is typed Actor |
| Stop Core | `<C> in` 11, `final error code` 10 | `<C> out` 3 | measured; no error terminals |
| Handle Error | `error in (no error)` 8, `<C> in` 11 | `<C> out` 3, `error out` 0, `stop actor?` 2 | measured |
| Handle Last Ack Core | `error in (no error)` 4, `Last Ack` 6, `<C> in` 7 | `<C> out` 3, `error out` 0 | measured 2026-10-10 (`lvai_connector_pane`, pattern 4812); no hook shape on it is measured |

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
| `overrideHasUnsavedEdits` | the override already carries edits this call did not make; the save would write them. **Blind to scripted edits, measured** (§4) |
| `overrideNotAMember`, `noActiveProject`, `projectDidNotBecomeActive` | the override or the project is not ready |

The warnings:
- A recommended hook input left unwired.
- A Pre Launch Init hook whose error cannot fail the launch.
- An Actor Core series hook with an error wire: the error skips Handle Error and the actor ends
  silently; Launch reports 0 and the next `Send` gets 1556 (measured).
- The unmeasured combination: any shape on Handle Last Ack Core.

### 3d. Route

1. Read the hook's terminals and their `connection` from its AIXML export.
2. Run `scripts/lvai_af_hook_read.xml`, which is read-only. It lists:
   - the panel terminal names, and per terminal the number of ends on its wire (`panelWireEnds`);
   - the top-level subVI names;
   - the node classes. Measured 2026-10-10: Call Parent shows as `CallParentNode` (`callParentSeen`
     true), **and** it is also in `SubVIs[]` under the PARENT's VI Name
     (`Actor Framework.lvlib:Actor.lvclass:Actor Core.vi`). Hook, source and remove matching leave
     that node out, so a hook name such as "Actor Core" cannot select Call Parent.
3. Plan by name in C# (`Infra/AfOverrides.cs`) and check the plan. `dryRun` stops here and returns
   the plan.
4. Refuse an override that already has unsaved edits (`overrideHasUnsavedEdits`,
   `lvai_af_modified_members.xml`): the save in step 6 writes the whole VI. Read the exec state
   before.
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
2. **Before anything changes**, `scripts/lvai_af_modified_members.xml` reads the FP and BD
   modification bitsets of the override and of every sibling lifecycle override. One that already
   has unsaved edits is refused, by name: `overrideHasUnsavedEdits` or `siblingHasUnsavedEdits`.
   Those edits are someone else's work, and the rule is that unsaved files this call did not make
   are asked about, never saved along with the cleanup. A dry run reports this refusal too.
3. `scripts/lvai_af_remove_hook.xml` deletes exactly that node (the kit's delete removed every node
   with a terminal name) and runs `BD.Remove Bad Wires`. It reports the exec state before and after.
4. Save the override. Then save the sibling overrides that **became** modified (every sibling was
   clean in step 2, so a modified one now was made so by this call), and then the class.

**Why the siblings.** A node deleted from a branched wire leaves a loose end. The override and a
sibling override of the same class then read state 0, "error of type 1000088". Remove Bad Wires on
the edited VI cleared both (measured, pilot project). `resaveSiblings: false` leaves even those
siblings unsaved.

**Measured 2026-10-10.** The remove itself works: a branch hook on Actor Core removed with `matches:
1`, `execStateAfter: 1`, the override and all four siblings state 1 cold (the 1000088 loose end did
not occur on this actor); two nodes of one name gave `hookNodeAmbiguous`, `occurrence: 1` removed
one. **The unsaved-edit check does not work for scripted edits:** the FP and BD Mods Bitsets read 0
after a moved panel control, a moved diagram node and a new node dropped by `New VI Object`, inside an
undo transaction too, on a class member reached through the project's instance (`{LV.Diagram}
Modified` read TRUE on a clean VI as well, so it is no signal either). A sibling carrying such an
edit was NOT refused, and because the sibling re-save reads the same bits, every sibling was
`skippedUnmodified`. Whether an edit made by hand in the IDE sets the bits is unmeasured (it needs
a person). Until it is, treat `siblingHasUnsavedEdits` / `overrideHasUnsavedEdits` as unproven and
check for unsaved work yourself before a remove.

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
  route is measured by hand, and on this tool 2026-10-10: a protected member came back protected,
  kept both lines through LabVIEW's next save, state 1 cold.
- moves the block into its folder. **Unmeasured** (no clean way to put an AIXML member into a class
  folder for the test).

**Measured 2026-10-10:** `memberHasLoadedCallers` refused a re-run over an override's series hook
before anything changed, naming the override (class file and VIs byte-identical); after the hook was
swapped onto a same-pane member the re-run went through, and the hook was swapped back.

**A re-run needs the member's AIXML.** A `vi`-only re-run over a listed wizard accessor dropped the
entry, the re-add of the unchanged file answered `AddItemFromMemory` 1004, and the class was saved
WITHOUT the member (measured 2026-10-10). Such a call is now refused before anything changes:
`rerunNeedsAixml`.

## 6. Helpers

| script | does | saves | state |
|---|---|---|---|
| `lvai_create_override.xml` | NI's `CLSUIP_CreateOverride`, then saves VI and class | yes | measured 2026-09-17 / 2026-10-09 |
| `lvai_af_hook_read.xml` | lists panel terminals with wire ends, subVI names, node classes | no | measured 2026-10-10 |
| `lvai_af_add_hook.xml` | places one hook; series cut, branch tap, node pairs | no | measured 2026-10-10, all four shapes |
| `lvai_af_remove_hook.xml` | deletes one hook node, Remove Bad Wires | no | measured 2026-10-10 |
| `lvai_af_modified_members.xml` | which listed VIs carry unsaved edits (FP/BD modification bitsets) | no | runs; **reads 0 for scripted edits** (2026-10-10), IDE edits unmeasured |
| `lvai_af_save_members.xml` | saves listed VIs (optionally only modified ones), then the class | yes | measured as a list 2026-10-10; `Only If Modified` skips everything (same bits) |
| `lvai_member_callers.xml` | loaded members and their callers | no | measured 2026-10-10 |

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
3. **`lvai_af_remove_hook`**:
   - on a branch hook whose wire branches: the override and its sibling overrides must read state
     1 cold afterwards, and `siblingSave.saved` lists only siblings the remove made modified;
   - with a sibling override that has a pre-existing unsaved edit (change it in the IDE, do not
     save): expect `siblingHasUnsavedEdits` naming it, and nothing changed - the hook still on the
     diagram, no file written (compare timestamps), the sibling still unsaved;
   - the same with the override itself edited: expect `overrideHasUnsavedEdits`.
4. **`lvai_add_class_method`** re-run:
   - over a hook an override calls: expect `memberHasLoadedCallers` and an unchanged class file;
   - over a protected member in a folder: expect scope and folder back, `lvai_describe_class`,
     state 1 cold.

**Results, 2026-10-10** (LabVIEW 2026 Q3 32-bit, throwaway actors and project):

| item | result |
|---|---|
| 1 `lvai_create_override` | pass: 5 + 5 lifecycle overrides (no library / `.lvlib`), one plain override (protected kept), `overrideAlreadyExists`; all state 1 cold |
| 2 `branch`, `series`, `branchFromNode`, `seriesAfter` (PLI, Stop Core, Handle Error), `dryRun` | pass; runs logged the hooks in AF order, PLI's 5000 reached Launch |
| 2 refusals `hookInputUnfed`, `wireIsBranched`, `handleErrorHookBeforeCallParent` | pass, nothing changed (file times identical) |
| 2 `CallParentNode` in Node Classes | yes; Call Parent is also in `SubVIs[]` (§3d) |
| 3 remove a branch hook; `hookNodeAmbiguous` | pass; override and siblings state 1 cold |
| 3 `siblingHasUnsavedEdits` / `overrideHasUnsavedEdits` | **fail** for scripted edits (bits read 0, §4); IDE edits unmeasured |
| 4 re-run over a hooked member | pass: `memberHasLoadedCallers`, class unchanged; re-run after unhooking ok |
| 4 re-run over a protected member in a folder | scope: pass; folder: unmeasured; `vi`-only re-run: failed, now refused (`rerunNeedsAixml`) |
