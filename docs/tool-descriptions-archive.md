# Tool descriptions before the 2026-11-01 shortening

The full description of every served tool and parameter as it stood on 2026-11-01, before the
descriptions were cut down to what a caller needs at call time (`docs/build-contract.md`'s
sibling `docs/ideen-schneller-und-weniger-tokens.md` §3a: 454 k characters of schema were
re-read on every request of every agent). Kept verbatim so that no measurement, history or
rationale that lived only in a description is lost. Read it when a short description leaves you
asking WHY a rule exists; the rule itself is still in the served description.

## `lvai_add_class_field`

```text
MUTATING: adds private data FIELDS to a `.lvclass` that ALREADY EXISTS, through NI's own
`Message Maker.lvlib:Add Member Data to Private Data Control.vi` - the same provider
`lvai_create_class` drives, which does not care how old the class is.
USE THIS RATHER THAN RE-CREATING THE CLASS. `lvai_create_class`'s `overwrite` would drop
every member; this leaves them alone. Measured 2026-09-17 on a class with 9 members and
again on a fixture with 4 wizard accessors: the field is APPENDED, the members are
untouched, and every VI is still executable after a project close.
IT ADDS ONLY - there is no remove and no rename here. A field name the class already
carries is refused before LabVIEW is touched, because NI's provider would take it and you
would end up with two fields of one name.
NEEDS A PROJECT OPEN AND ACTIVE: the provider reaches LabVIEW through
Project:Active Project and answers Error 1055 without one. Pass projectPath and this call
opens it.
THE ACCESSORS ARE NOT CREATED - a new field has none. Follow with
`lvai_create_accessors` passing `fromField` as the new field's index, which is the old
field count (the answer reports it as `nextFromField`); leaving it at -1 resumes from the
MEMBER count, which is wrong on a class that also has methods.
VERIFIED FROM THE SAVED FILE, and `ok` is gated on the OLD fields surviving as well as the
new ones arriving - "it appends" is a measured property, not a promise NI made.
```

- `lvclassPath`: Absolute path to the .lvclass to extend. It must already exist
- `fields`: Fields to ADD, as `<type>.<name>`, comma separated - the same spelling `lvai_create_class` takes, e.g. `bool.Rechtslauf` or `string.Hersteller,int32.Baujahr`. A name the class already has is refused. Cluster, array and enum fields are not supported here, for the same reason they are not there.
- `projectPath`: The .lvproj to open first. Without an active project the provider answers Error 1055. Omit only when you have opened one yourself. This file is never EDITED here.
- `carrierViPath`: Where to keep the generated CARRIER VI, whose controls become the fields. It is KEPT, not deleted: LabVIEW adopts the carrier into the open project, and deleting it leaves LabVIEW holding a project whose items no longer exist - the same trap `lvai_create_class`'s `keepCarrier` documents. Defaults to a per-user temp directory.
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to the scripts folder's copy
- `regenerateHelper`: Regenerate the helper VI even when it exists (default: false)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_add_class_method`

```text
MUTATING: turns AIXML-generated VIs into real class METHODS - class-typed connector pane,
DYNAMIC DISPATCH, and class membership - for many methods in ONE call.
THIS IS THE STEP AIXML CANNOT DO. A class-typed terminal is `Control with type=UDClassInst
is not supported`, so a method is authored with `path` stand-ins and repaired here. Measured
2026-09-02: doing it by hand cost ~105 s of wall clock for 3.3 s inside LabVIEW.
Each method is either an `aixml` file or a `vi` that already exists (repaired in place).
The AIXML is VALIDATED FIRST and the verdict CLASSIFIED: a refusal naming a class type is
the documented case where ValidateAIXML is stricter than ConvertAIXMLToVI, and is converted
anyway; anything else STOPS, because the converter writes a broken diagram from it and the
fault then surfaces as Error 1003 at RUN time with every file-level check green - measured
2026-09-07 on three overrides that all reported ok. `validateFirst: false` restores the old
convert-blind behaviour.
RE-RUNNING OVER AN EXISTING MEMBER USED TO DESTROY THE CLASS, and this tool said it was
SAFE until 2026-09-14. Measured on a minimal probe - one field, its two wizard accessors,
one method with no subVI calls: a single re-run left EVERY member eBad ON DISK, accessors
the call never named included, with privateDataBytes grown 5454 -> 5466. It survived a
LabVIEW restart, and the call reported ok: true, terminalsRetyped: 2, verifiedOnDisk: true
throughout. The pylabview conpane rebuild was the obvious suspect and is NOT the cause: the
same re-run with panePattern 0, which skips that step, broke it identically.
SO A RE-RUN NOW DROPS THE CLASS'S ENTRY FIRST, in the project-closed window, and takes the
first-run path that is measured clean - reported as the `dropExistingMembers` step. That
needs projectPath: editing the class file while LabVIEW holds the project open is undone by
LabVIEW's own save. Without it, a re-run is REFUSED (`memberAlreadyListedWithoutProject`)
rather than allowed to proceed with a green answer.
A LOADED CALLER STILL BREAKS A RE-RUN: when a loaded VI (an override hook, any caller)
calls the member, the re-add answers Error 56003 AFTER the entry was dropped (measured
1.7.2-1.8.5). So before anything changes, a re-run asks the project's instance for the
member's callers in memory and refuses with `memberHasLoadedCallers`, naming them (measured
2026-10-10: an override's series hook was refused naming the override, class file unchanged;
after the hook was swapped off, the re-run went through; `checkLoadedCallers: false` skips
it). A re-run needs the member's `aixml`: a `vi`-only re-run over a listed member is refused
(`rerunNeedsAixml`) - measured, it dropped a wizard accessor from its class (1004). And a re-add puts the
member at the class root as public; `keepPlacement` (default) writes its old scope and
virtual folder back in a project-closed window afterwards (`restorePlacement` step; the
scope lines are measured on this route 2026-10-10 - protected came back and survived
LabVIEW's next save, state 1 cold; the folder move is UNMEASURED).
AddItemFromMemory's 56002 (already a loose project item) and 1004 (already a member) are
still tolerated and still reported as `memberAlreadyExisted`, gated on the .lvclass itself
listing the VI, because 1004 is also what a wrong `Name` input produces.
A METHOD PASSED AS AN EXISTING `vi` IS NOT DROPPED when the VI names the class as its owner
(LIBN): it keeps that link, so a drop made AddItemFromMemory answer 1004 and left the class
file WITHOUT the entry (measured 2026-10-12). Such a method takes the tolerated path
(`keepExistingMembers`); one the class file no longer lists is refused (`memberOrphaned`) -
pass its aixml. Each terminal's Replace error and its class read back off the panel are in
the `retype` step, and the verify also requires the class file to list the VI.
methodsJson is a JSON ARRAY:
  [{"aixml":"C:\\x\\Initialize.xml","vi":"C:\\cls\\Initialize.vi",
    "classTerminals":["obj in","obj out"],"dispatchTerminals":["obj in","obj out"]}]
`classTerminals` are the pane terminals to retype to the class - found BY NAME, never by
index, because Controls[] order is not portable. `dispatchTerminals` (names) or
`dispatchTerminalIndices` (conIdx numbers) are the ones set to dynamic dispatch; names are
resolved from the VI's own AIXML export. Omit both for a STATIC member.
A TERMINAL MAY CARRY A DIFFERENT CLASS. Write it as an object instead of a name:
  "classTerminals":["obj in","obj out",{"terminal":"Engine in","class":"C:\x\Engine.lvclass"}]
That is NI's own interface shape - `Lever.lvclass:Pry.vi` takes a `Pryable` - and it works
on an INTERFACE too: this tool does not care that the .lvclass carries IsInterface.
THE PROJECT MUST BE OPEN AND ACTIVE for the repair, and CLOSED for the conversion. Pass
projectPath and this call sequences both - closed for every convert and pane repair, then
opened once for every retype. Getting that backwards is Error 56002 on one side and a
silent no-op on the other.
VERIFIED FROM THE SAVED FILE, with pylabview and no LabVIEW: each retyped terminal must show
as `class="udClassDDO"` in the front panel heap and no `stdPath` stand-in may remain beyond
the method's own path terminals and the `path` ELEMENTS inside its cluster or array
controls (`cluster{path.protocol}`) - those are counted from the AIXML, reported as
`pathsInsideClusters`, and left for lvai_bind_pane_typedef where they stand in for a class. A run
that reported four working methods from `Execution:State = 1` was reading an in-memory copy
that never reached disk; this check is what caught it, so `ok` is false without it.
EACH ADDED METHOD THEN HAS ITS BROKEN WIRES CLEARED (`removeBadWires`, default true), with
the project still open and active: a retype that could not re-type a wire leaves it broken
and the method at state 0. The removed wires are listed per method under
`badWireCleanup`; a sink that lost its wire from a source makes `ok` false
(`errorKind: badWireCleanupLostConnection`, the methods named in
`methodsWithLostConnections`). UNMEASURED on a class member: whether Save.Instrument alone
persists a Remove Bad Wires there is not established - read the method COLD afterwards.
```

- `lvclassPath`: Absolute path to the .lvclass the methods belong to
- `methodsJson`: JSON array of methods: aixml and/or vi, classTerminals, dispatchTerminals
- `projectPath`: The .lvproj to sequence. CLOSED for the conversions, then OPENED for the repairs and left open. Omit only when you are driving that sequence yourself - with no project active the helper answers Error 1055 and nothing is retyped.
- `panePattern`: The connector pane pattern to stamp before the repair. 4815 is NI's accessor layout and the default; ConvertAIXMLToVI takes no pattern, so a new VI otherwise carries the station default from LabVIEW.ini. Pass 0 to leave the pane alone. (default: 4815)
- `verify`: Read each saved file back and confirm the terminals really are class-typed (default: true)
- `validateFirst`: Validate each method's AIXML before converting it, and stop on a fault that is NOT the class-wire strictness. On by default. Turn it off only when the classifier wrongly calls a class-wire refusal a real fault - and report that, because the classifier then needs the message adding to it. (default: true)
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to the scripts folder's copy
- `regenerateHelper`: Regenerate the helper VI even when it exists (default: false)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview NO STEP touches NI's service: each method is COMPOSED from its AIXML (the path stand-ins compose like any path), the pane repaired by pylabview as before, and the retype-and-membership helper COMPOSED and run over LabVIEW's ActiveX server, where {LV.Control} Replace, AddItemFromMemory and both saves work as in the add-on's instance. A pylabview "validation" is a scratch compose, not NI's wiring check. helperViPath is ignored there. LabVIEW must already be running.
- `cleanUpDiagram`: Lay out each method's block diagram with our layout engine (lvai_layout_diagram's) once it is a verified member, and save it in place (the cleanUpDiagram step). Reported, never gating: a layout that does not finish leaves the method as converted and still ok. (default: true)
- `removeBadWires`: After each method is added and verified, clear the BROKEN wires left on its diagram (BD.Remove Bad Wires, in the IDE's instance, project still open) and list each under the method's `badWireCleanup`. Default true. A lost connection fails `ok`. Runs before the layout, so the layout sees the cleaned diagram. (default: true)
- `checkLoadedCallers`: On a re-run, ask the project's instance first whether any LOADED VI calls a member about to be dropped, and refuse (`memberHasLoadedCallers`) before anything changes. Default true; turn it off only when the check itself cannot run and you have unhooked every caller by hand. (default: true)
- `keepPlacement`: On a re-run, put each re-added member's access scope and virtual folder back - the add puts it at the class root as public. Default true; a project close/open cycle is added only when a member was non-public or in a folder. (default: true)

## `lvai_add_to_class`

```text
MUTATING: adds EXISTING .vi and .ctl files to ONE .lvclass - at the class root, or into a
virtual folder (created at the root when missing and createFolder is true) - through
{LV.ProjectItem} AddItem in the ACTIVE PROJECT's instance, then Save All This Library.vi on
the class with Save Only Modified TRUE (the class and the files it took in; a .ctl saves only
through a library save).
ADDING A TYPEDEF RENAMES IT (Class.lvclass:X.ctl), and every caller outside the class must
relink - which happens only for callers that are LOADED. A caller that is not loaded keeps
the old link with no error anywhere. So: pass every caller outside the class in callerPaths;
they are opened through the project before the add, and afterwards every one of them is
saved with Save.Instrument - the relink sets no Mods bitset, so waiting for LabVIEW to mark
them saved none (measured 2026-10-17); `verified` requires each one saved (callersNotSaved).
Nothing else is saved; other modified VIs are reported under modifiedNotSaved. A VI in the
project's instance whose bitsets cannot be read refuses the call (unsavedCheckFailed).
CALLERS ARE NOT GUESSED, BUT THEY ARE CHECKED: the project folder is scanned OFFLINE for
files whose link records name an item's file name (a heuristic - same-named files in two
folders are not told apart). A hit that is not in callerPaths and not a member of this class
REFUSES the call (callersNotLoaded) before LabVIEW is touched; acceptUnlistedCallers=true
proceeds anyway and reports them.
PROJECT REQUIRED (projectPath): it is opened and must become active; callers are opened
through it. If anything in the project's instance already carries unsaved changes, the call
is refused (unsavedChangesBefore) - its saves would write them.
Refused before LabVIEW: a file already owned by a class or library (read from the file's
own LIBN resource), a file already listed in this class, an extension other than .vi/.ctl,
a nested folder path, an ambiguous or (createFolder=false) missing folder.
VERIFIED FROM THE FILES: the re-read .lvclass must list every item (in the folder when one
was asked for), each item's own LIBN must name the class, and `filesChanged` lists every file
under the project folder that the call rewrote.
The folder route is a field run's (11 typedefs, 2026-10-11/12); the class-root route, adding
a .vi, the saved-callers restriction and the guards are unmeasured until acceptance.
```

- `lvclassPath`: Absolute path to the .lvclass the files should belong to
- `itemPaths`: JSON array of absolute paths of the files to add, e.g. ["C:\\p\\Mode.ctl","C:\\p\\Helper.vi"]. Each must exist, be a .vi or .ctl, and belong to no class or library yet.
- `projectPath`: The .lvproj the class belongs to. Required: it is opened and made active, and callers are opened through it. It is not edited here.
- `folder`: Virtual folder inside the class to add into; omit for the class root
- `createFolder`: Create the folder at the class root when it is not there (default: true)
- `callerPaths`: JSON array of absolute paths of every VI OUTSIDE the class that uses one of the items. They are loaded through the project before the add so they relink, and every one is saved afterwards. Omit when there are none.
- `acceptUnlistedCallers`: Proceed even though the offline scan found files linking to an item that are not in callerPaths. They will keep the old link unless something loads and saves them. (default: false)
- `checkUnsaved`: Refuse when the target (or, for lvai_add_to_class, any VI in the project's instance) already carries UNSAVED changes, because this call's save would write them too. Default true - keep it for any EXISTING project, where someone may have VIs open with edits of their own. Pass false only in a project this session is building itself: the save then writes whatever is unsaved, which is what a build wants. The user's rule of 2026-10-17. (default: true)
- `timeoutSeconds`: Local budget in seconds, per LabVIEW step (default: 180)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_add_to_library`

```text
MUTATING: adds files that ALREADY EXIST to a `.lvlib`, through NI's own `{LV.Library}`
`AddItem` plus `Save All This Library.vi` - the gesture that RELINKS, which writing
`NI.Lib.ContainingLib` by hand does not.
THE HAND EDIT IS THE TRAP THIS EXISTS FOR. Measured 2026-09-17: hand-writing that property
and a matching `.lvlib` took all three VIs of a class from execState 1 to eBad, with
lvai_describe_class reporting the right qualifiedName and containingLibrary throughout,
both files well-formed, BOM and CRLF intact. Through AddItem the same kind of change left
every caller executable after a project close.
NEEDS A PROJECT OPEN AND ACTIVE: the helper reaches LabVIEW through Project:Active Project
and answers Error 1055 without one. Pass projectPath and this call opens it.
IT DOES NOT EDIT THE .lvproj. A file that was listed in the project on its own should lose
that entry once it belongs through a library - the answer names it under
`projectEntriesToRemove`, and you remove it with the project CLOSED. Doing both in one call
is not possible: AddItem wants the project open and a surviving .lvproj edit wants it shut.
A `folder` PUTS THE ITEM INSIDE A VIRTUAL FOLDER, the way NI's own Message Maker places a
message class: AddItem is invoked on the FOLDER's project item, not on the library.
NI FALLS BACK TO THE ROOT IN SILENCE when the folder does not exist, so a folder that is
not in the library is REFUSED here before LabVIEW is touched, naming the ones that are -
and `verify.placedIn` says where each item really landed.
ONE HELPER RUN PER ITEM, all inside this one call, so a five-item library costs one round
trip rather than five.
VERIFIED FROM THE SAVED .lvlib, which is re-read afterwards - `verify.items` is what is on
disk, not what the runs reported.
AN ACTOR JOINS ITS LIBRARY BEFORE ITS MESSAGES ARE BUILT. Adding an actor class whose
message classes already exist leaves every message's Do.vi eBad (measured 2026-09-18)
while this call answers ok; only lvai_exec_state sees it, and the repair is rebuilding
the messages. Order: class, methods, library, messages.
```

- `libraryPath`: Absolute path to the .lvlib the items should belong to. It must already exist
- `itemsJson`: JSON array of items to add, e.g. [{"path":"C:\\p\\Pump\\Pump.lvclass"},{"path":"C:\\p\\Log.vi","type":"VI"}] `path` is required and the file must exist. `name` defaults to the file's own name, which is what NI writes. `type` defaults to `LVClass` for a .lvclass and `VI` for a .vi; any other extension must say its own type, because guessing one is how a library gets an item LabVIEW then cannot bind.
- `folder`: Virtual folder inside the library to add into, e.g. "Messages for this Actor". Omit for the library root. It must already exist in the .lvlib - a name that is not there is refused rather than quietly landing at the root, which is what NI's own AddItem does. The whole call uses one folder; add to two folders with two calls.
- `projectPath`: The .lvproj to open first. Without an active project every AddItem answers Error 1055. Omit only when you have opened one yourself. This file is never EDITED here.
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to the scripts folder's copy
- `regenerateHelper`: Regenerate the helper VI even when it exists (default: false)
- `timeoutSeconds`: Local budget in seconds, per item (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_add_vis_to_project`

```text
MUTATING: lists VIs in a `.lvproj` under one virtual folder, as a FILE edit made with the
project CLOSED - it closes the active project first, because LabVIEW's close saves its own
copy over the file and would destroy the edit - and leaves it closed unless `reopen`.
USE THIS INSTEAD OF EDITING A .lvproj BY HAND. Measured 2026-09-25 on the ATM cold build: a
hand edit listed three VIs under `SubVIs` that LabVIEW's own save had already listed at
target level, and the next open answered `Error 74` and loaded nothing.
A VI ALREADY LISTED IS NEVER LISTED TWICE. One at TARGET level - where LabVIEW's save drops
every VI it adopts - is MOVED into the folder (`movedIntoFolder`); one inside another folder
stays and is named under `listedElsewhere`. Any file the project already lists twice is
repaired, keeping the entry inside a folder (`duplicateEntriesRemoved`), and the strays our
own tools leave behind - helper VIs, user.lib\LV_MCP sockets, entries whose file is gone -
are swept as on every close. A path with no file behind it is NOT listed (`notOnDisk`).
`lvai_open_file` refuses a project that lists a file twice (`duplicateProjectEntries`);
this call with that projectPath and the VIs you meant to list is the repair.
TARGET LEVEL ONLY WHEN ASKED: `targetLevel: true` lists the VIs directly under My Computer,
beside the classes and libraries - a top-level application VI, say - and then a VI already
at target level stays there. Without it every VI goes into a folder, because an entry at
target level is also what LabVIEW's own save writes for a VI it adopts.
```

- `projectPath`: The .lvproj to list the VIs in
- `viPaths`: The VIs to list, one absolute path per line
- `folderName`: Virtual folder to list them under; created at target level if missing. NAME IT for what it holds: the default suits subVIs. Ignored with targetLevel (default: "SubVIs")
- `reopen`: Open the project again afterwards. Leave false when the next step generates (default: false)
- `targetLevel`: List the VIs directly under My Computer instead of in a folder - for a top-level VI the user starts from the Project Explorer (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_af_add_hook`

```text
MUTATING: places ONE hook VI (normally a member of the same class) on an override's
diagram and wires it, then saves the override and the class in a SEPARATE step. Shapes:
`branch` - the override's class input tapped beside Call Parent (the hook runs in
parallel; the helper loop of Actor Core); `series` - the hook between the class/error
inputs and Call Parent (a body that hands a changed object to the parent);
`seriesAfter` - the hook between Call Parent and the override's outputs (Pre Launch Init's
creating body, whose error then fails the launch; a Handle Error handler; a Stop Core
release); `branchFromNode` - a branch off the output of a series hook already placed, so it
sees that hook's result (pass sourceHookName). Place series hooks before branches.
THE PANES ARE BUILT IN, per AF override: Pre Launch Init has no error in; Stop Core no
error terminals; Handle Error takes `error in (no error)` and returns `stop actor?`; Actor
Core's class output is `Actor out`, typed Actor, so seriesAfter is refused there. Class
terminals take the class's name (`<Class> in` / `<Class> out`).
REFUSED BEFORE ANYTHING CHANGES: a hook whose REQUIRED input the planned wires leave unfed
(one unwired required input breaks the override and only a cold exec state shows it); a
Handle Error hook before Call Parent (error 43 arrives there at every normal stop, and the
parent returns stop actor? TRUE whatever the hook did) unless
allowHandleErrorBeforeCallParent; a series insert into a wire that already branches; a name
that matches no terminal; the same hook already on the diagram; an override that already
carries unsaved edits (`overrideHasUnsavedEdits` - the save would write them). dryRun reads the diagram
and returns the planned wires without changing anything.
Wiring is by NAME: series and branch wires use the kit's measured panel-terminal route
(Control.Terminal + Connect Wire; series cuts the wire and reconnects its far end), a
branchFromNode wire uses lvbd_connect_by_names. NEEDS THE PROJECT OPEN AND ACTIVE. The answer
lists every wire made and the override's execution state before and after - read in the
project's instance, so read it again COLD (lvai_exec_state, project closed) before trusting
it. The kit shapes are measured by hand (branch, series on Actor Core, seriesAfter on Pre
Launch Init and a plain override, branchFromNode). THIS TOOL measured 2026-10-10 on throwaway
actors: branch, series and branchFromNode on Actor Core, seriesAfter on Pre Launch Init (its
error 5000 came out of Launch), Stop Core and Handle Error (both hooks ran after their Call
Parent at the stop), dryRun and the refusals; every override state 1 cold. THE UNSAVED-EDIT
CHECK reads the FP/BD Mods Bitsets: a hand edit in the IDE sets them, but edits made by VI
Server scripting leave them at 0 (a moved object, a new node, inside an undo transaction too -
both measured 2026-10-10), so scripted unsaved edits are NOT seen.
```

- `overridePath`: Absolute path to the override .vi (a member of classPath)
- `classPath`: Absolute path to the class's .lvclass
- `hookViPath`: Absolute path to the hook .vi to place
- `shape`: branch, branchFromNode, series or seriesAfter
- `wireError`: Also wire the error chain. Default: series and seriesAfter wire it where the override's pane has the terminal; branch and branchFromNode do not.
- `sourceHookName`: For branchFromNode: the series hook to tap, by file name or VI name
- `classTerminalPrefix`: The override's class terminal prefix; defaults to the class name
- `hookClassIn`: The hook's class input; defaults to '<class> in'
- `hookClassOut`: The hook's class output; defaults to '<class> out'
- `hookErrorIn`: The hook's error input; defaults to 'error in'
- `hookErrorOut`: The hook's error output; defaults to 'error out'
- `allowHandleErrorBeforeCallParent`: Keep an existing shape: allow a Handle Error hook before Call Parent (default: false)
- `allowSecondInstance`: Place the hook even when the same VI is already on the diagram (default: false)
- `dryRun`: Read the diagram and return the planned wires; change nothing (default: false)
- `projectPath`: The .lvproj to open first; omit when it is already open and active
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_af_remove_hook`

```text
MUTATING: deletes ONE hook subVI node from an override's diagram, runs Remove Bad Wires on
the override, and saves - the override always, its SIBLING overrides only where the edit
left them modified, then the class. BEFORE ANYTHING CHANGES it reads the unsaved state of the
override and every sibling lifecycle override: one that already carries unsaved edits -
someone else's work - is refused (`overrideHasUnsavedEdits` / `siblingHasUnsavedEdits`,
naming it), so the only siblings ever saved are the ones this call made modified.
WHY THE SIBLINGS: deleting a node that sat on a branched
wire leaves a loose end, and both the override and a sibling override of the same class then
read state 0 ("error of type 1000088"); Remove Bad Wires on the edited VI cleared both
(measured). The node is chosen by the hook's VI name; with two of the same name pass
`occurrence`. A dry run lists the subVIs on the diagram. A SERIES HOOK IS BYPASSED, the
inverse of the series insert: `hookShape` (read off the override's own export) names each
output something reads, the call pairs it with the hook's input of the same stem
(`<C> out` -> `<C> in`, `error out` -> `error in`), and the helper connects the wire's
source at that input straight to every sink of that output before the node goes
(`bypassed`, `sinksReconnected`). An output with no input of its stem is refused before
anything changes (`seriesHookNotRemovable`). Before 2026-10-17 a series remove cut the
class wire and SAVED the eBad override; a remove that leaves the override broken anyway is
still NOT SAVED (`removeBrokeOverride`): the file on disk stays the working one. NEEDS THE
PROJECT OPEN AND ACTIVE. The kit's delete + clean route is measured;
Measured 2026-10-10: one hook removed (matches 1, state 1 cold for the override and every
sibling), `hookNodeAmbiguous` and `occurrence`. NOT RELIABLE: the unsaved-edit check and the
sibling re-save both read the FP/BD Mods Bitsets. A hand edit in the IDE sets them - a sibling
with one was refused (`siblingHasUnsavedEdits`), nothing changed - but edits made by VI Server
scripting leave them at 0 (both measured), so a scripted unsaved edit is NOT refused and a
sibling this call's own scripting touched is not re-saved on their word.
```

- `overridePath`: Absolute path to the override .vi
- `classPath`: Absolute path to the class's .lvclass
- `hookName`: The hook to remove: its path, file name or VI name
- `occurrence`: Which of several nodes with that name, from 0 in diagram order
- `resaveSiblings`: Save the sibling overrides the remove made modified (default true) (default: true)
- `dryRun`: List what would be removed; change nothing (default: false)
- `projectPath`: The .lvproj to open first; omit when it is already open and active
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_aixml_reference`

```text
The rules for reading and WRITING LabVIEW AIXML. Call this before authoring or editing
any AIXML - the format has no published schema and its wiring model is
counter-intuitive, so generating without it produces plausible but wrong XML.
Without arguments: the essential rules plus a section list (cheap, start here).
With node: ONLY the passages mentioning that node or term - the right call when you
want one node's terminal names, e.g. node='Build Waveform', node='Index Array',
node='graph21703'. Section 8 alone is 54 kB, far past what is worth reading whole.
PASS EVERY NODE YOUR DIAGRAM NEEDS IN ONE CALL, comma-separated:
node='Select,Index Array,Build Waveform,Greater?'. Terms are matched by substring, so
single lookups return the same text over and over - the 2D-indexing block answers
`Index Array`, `disabled index` and `Array Subset` alike. A batch prints each passage
ONCE and each table header once instead of once per term. Measured on one VI: 18 single
lookups where 3 batched calls carried the same content.
With section: that section's full text ('types', 'structures', 'escaping', 'wiring',
a heading number, or part of a title). With section='all': the whole document.
```

- `section`: Section number, keyword or title fragment; 'all' for everything; omit for the essentials
- `node`: Node or term to look up, e.g. 'Build Waveform'. Returns only the table rows, code blocks and paragraphs that mention it, each under its heading. SEVERAL TERMS AT ONCE, comma-separated - 'Select,Index Array,Not' - which is one round trip and prints each passage once rather than once per term. Takes precedence over section
- `limit`: Max passages to return (default 40, max 400) (default: 40)
- `page`: Which chunk of an over-long section to return, 1-based. Omit for the subsection index, which is what you want first (default: 0)

## `lvai_apply_aixml_to_vi`

```text
RPC ApplyAIXMLToVI. MUTATING in principle: applies an AIXML description onto an EXISTING
VI. This is the RPC behind LabVIEW's AI code completion.
DO NOT CALL IT. It is gated on a per-VI attachment bound to the CALLER, which no
third-party client can obtain, and from here it has never patched anything: `Error 42`
through 2026-09-11 with sixteen variables ruled out, and a SILENT `errorCode 0` since
2026-09-16 - measured on one VI twice, closed and then open in the IDE, with the AIXML
export identical both times.
THE SILENCE IS THE WHOLE PROBLEM: `errorCode 0` is what success looks like everywhere else
in this interface, so "try it, it costs one round trip" now means reporting a VI as
surgically patched when the file was never touched.
So this tool REFUSES unless userAskedForThisByName is true - the user's standing rule of
2026-09-16. An edit is a full REGENERATION (lvai_generate_vi over the same path), and a VI
whose existing front panel must survive cannot be edited at all: say so and let the user
choose the route. docs/aixml-reference.md section 14 has the evidence.
```

- `viPath`: Absolute path to the .vi to modify
- `aiXmlFilePath`: Absolute path to the AIXML .xml describing the change
- `timeoutSeconds`: Local budget in seconds (default: 240)
- `userAskedForThisByName`: The user asked for THIS RPC, by name, in THIS session. Without it the call is refused and no RPC is made. It is not a force flag for an agent that thinks it knows better: the path is closed because it does not work, and because re-establishing that costs turns and teaches nobody anything. (default: false)

## `lvai_bind_class_fields`

```text
MUTATING: binds one or more `.ctl` TYPEDEFS onto fields of a `.lvclass`'s private data
control - export the cluster, Replace each field, move it back - in ONE call.
REPLACES FIVE ROUND TRIPS. Measured 2026-09-02: the same sequence by hand cost 116 s of
wall clock for 0.8 s inside LabVIEW, the worst ratio of that run.
EVERY SOURCE IS VETTED FIRST and a non-typedef is REFUSED BY NAME before anything is
touched. This is the failure the tool exists for: a `.ctl` that is not a typedef binds with
`error out = 0`, installs the right type, and produces NO typedef link - measured on two of
NI's own controls. Pass `force: true` to install the type anyway, knowing it will not bind.
bindingsJson is a JSON ARRAY, one object per field. Name the field EITHER way:
  [{"field":"Task Reference","ctlPath":"C:\\ctl\\Task.ctl"},{"fieldIndex":1,"ctlPath":"..."}]
A name is resolved against the private data control's own field labels, read off the class
file with pylabview - so a misspelling is answered with the list of real names.
THE PROJECT MUST BE OPEN AND ACTIVE, and stays open. Pass projectPath and this call opens
it; with no project open the helpers answer Error 1055, and with the class held by a
project the unwired route answers Error 1073 on Move. Do NOT cycle the project around this
call - a close/reopen around a class rewritten through an unwired open killed LabVIEW.
BIND BEFORE GENERATING ACCESSORS. An accessor generated before the binding keeps the bare
type for ever; nothing refreshes it. `accessorsAlreadyPresent` in the answer is the warning.
Every field reports `typedefBefore` and `typedefAfter` from the helper AND `boundInFile`
read back off the saved class - `ok` is false unless the file agrees.
```

- `lvclassPath`: Absolute path to the .lvclass whose private data is to be bound
- `bindingsJson`: JSON array of bindings: field or fieldIndex, plus ctlPath
- `projectPath`: The .lvproj to open and leave open. Required in practice: the helpers reach the class through Project:Active Project and answer Error 1055 with nothing open. Omit only when a project is ALREADY open and active.
- `force`: Bind even when the source .ctl is not a typedef. It will not bind. (default: false)
- `verify`: Read the saved class back and confirm each field really is a TypeDef now (default: true)
- `keepExportedCtl`: Keep the exported scratch .ctl instead of deleting it (default: false)
- `helperDirectory`: Where to keep the generated helper VIs
- `regenerateHelpers`: Regenerate the helper VIs even when they exist (default: false)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_bind_control_references`

```text
MUTATING: edits a VI IN PLACE, through VI Server scripting in the IDE's application
instance. For each STAND-IN - a refnum control such as `WB Ref` of type
ref{LV.WebBrowser} - it creates a BOUND control reference of a real front-panel control
(what right-click > Create > Reference makes), wires it to every sink the stand-in fed,
deletes the stand-in and saves.
USE IT WHEN AIXML CANNOT MAKE THE CONTROL ITSELF - the Web Browser control is the measured
case: AIXML writes it as a string, so `link=` on a {LV.WebBrowser} Invoke Node and a
`VI Server Reference` to it are both broken. Author the program against a stand-in
control of the reference type instead (ref{LV.WebBrowser} is a valid type literal and an
unlinked Invoke Node on it validates), graft it into the supplied panel that holds the
real control (lvai_graft_diagram allowNewControls), then call this. For a control AIXML
CAN create, author the reference directly - <Node _name="VI Server Reference"
element="<label>" outputs="<label>:<net>"/> - and this tool is not needed.
bindingsJson maps stand-in label to control label: {"WB Ref":"Web Browser Control"}.
THE FRONT PANEL IS OPENED in the IDE for the edit and put back afterwards: Create Control
Ref answers Error 53 on a Web Browser control whose panel is closed (measured).
NEEDS AN ACTIVE PROJECT (Error 1055 otherwise); the VI need not be listed in it.
OK IS DECIDED FROM THE FILE: execState 1, no stand-in left, and each control's reference
node feeding the same sinks its stand-in fed. A copy of the VI from before the edit is
kept and named under backupPath.
```

- `viPath`: Absolute path of the VI to edit. Saved in place
- `bindingsJson`: JSON object, stand-in label -> control label, e.g. {"WB Ref":"Web Browser Control"}
- `timeoutSeconds`: Local budget in seconds, per step (default: 180)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_bind_pane_typedef`

```text
MUTATING: installs a .ctl typedef onto named FRONT PANEL controls of an existing CLASS
MEMBER through {LV.Control} Replace, and saves the VI and its owning class in one run.
THIS IS THE OTHER HALF OF lvai_bind_typedef_constants, and the half nothing else reaches. A
GENERATED VI CANNOT CARRY A TYPEDEF ON ITS OWN PANE: AIXML has no typedef in its grammar,
so a control it authored is the bare underlying type, and a call into a subVI whose terminal
IS the typedef then wears a coercion dot that survives the placeholder flatten and the swap.
lvai_bind_typedef_constants finds its target by the CONSTANT's label and cannot reach a
front-panel control; this does.
THE CLASS PATH IS REQUIRED, AND THAT IS A MEASUREMENT. Measured 2026-09-18 on a plain VI in
no project and no class: with the class save left out, Replace plus Save.Instrument answered
error 0 at every stage and the saved file carried ZERO typedef objects - LabVIEW rewrote it
and dropped the binding. The class Save is what commits it. What commits it for a VI that is
NOT a class member is not established, so this tool does not offer that.
PRECONDITION: a project must be OPEN and ACTIVE - {LV.Control} Replace is a SILENT no-op
outside the IDE's own application instance, reporting terminals retyped while changing
nothing. Pass projectPath to have it opened here.
THE VERDICT IS `verified`, AND IT IS THREE CONDITIONS AT ONCE. Each requested .ctl must
appear as a <Label> in the SAVED VI's own type descriptors - read with pylabview, no
LabVIEW - AND every requested terminal must be on the panel AND the helper must have matched
as many as were asked for. `terminalsBound` alone is not the verdict: a run reported 1 for a
file that ended up with none. The FILE alone is not either, measured on acceptance - asked
to bind a terminal that does not exist on a VI whose .ctl was ALREADY bound, the file check
passed on its own and the call answered ok for something it never did. `terminalsNotOnPanel`
names the terminals that disqualified a run; `terminalNamesSeen` is what the panel has.
AND A FIRST OPEN READS EVERY CONTROL NAME AS EMPTY. Measured twice on one fixture: the
first run after LabVIEW had never loaded the VI saw four nameless controls and bound
nothing at error 0; the second saw all four names and bound. This tool retries once for
exactly that and reports it as `retriedAfterEmptyNames`.
```

- `viPath`: Absolute path to the class member .vi whose pane controls should be bound
- `lvclassPath`: Absolute path to the .lvclass that owns the VI. REQUIRED: Save.Instrument alone does not commit a Replace - measured, on a class member AND on a plain VI.
- `bindingsJson`: JSON array of bindings, e.g. [{"terminal":"Profil","ctlPath":"C:\\p\\Ofenprofil.ctl"}] `terminal` is the control's label exactly as lvai_vi_terminals prints it. An unknown key is refused by name rather than dropped.
- `projectPath`: The .lvproj to open first. Without an active project the Replace is a silent no-op. Omit only when you have opened one yourself.
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to the scripts folder's copy
- `regenerateHelper`: Regenerate the helper VI even when it exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_bind_typedef_constants`

```text
MUTATING: re-points block diagram CONSTANTS so they carry the typedef their subVI terminal
expects, which is what removes a coercion dot. Saves the VI in place.
WHEN YOU NEED IT: after lvai_placeholder_subvi + pylv_apply retarget, whenever the real
subVI's connector pane carries typedefs. AIXML cannot express a typedef, so the stub is
cloned with the bare underlying type and every such terminal comes out coerced. The call
still links, still runs and still validates - nothing reports it, which is why this exists.
NO .ctl PATH IS PASSED IN. The helper calls {LV.Terminal} Create Constant to obtain a
throwaway constant carrying the terminal's exact type, reads Typedef:Path off it, deletes
it, and only then Replaces the wired constant. Create Constant alone is NOT the fix -
measured, it creates a floating constant and does not rewire, so the coercion dot survives.
THE CONSTANT MUST CARRY A LABEL, and AIXML's `_name` on a Constant is what becomes one.
Author every constant you wire into a subVI call as `_name="<terminal name>"`; without a
label this tool cannot tell two boolean constants apart. Pass constantLabels only when the
labels differ from the terminal names.
PRECONDITION: a project must be OPEN and ACTIVE in the IDE - the helper reaches the VI
through Application:Project:Active Project so it edits the copy the project holds. Error
1055 is blamed on the project only once the subVI is known to be on the diagram - a name
matching no node fails with the same code.
THE NAME IS RESOLVED FIRST, against the names the sweep before the edit sees: an exact
match wins, a bare class-member name ('Write Config.vi') maps onto the one qualified name
it matches ('Channel.lvclass:Write Config.vi'), and a name matching nothing
(`subViNotOnDiagram`) or several qualified names (`subViNameAmbiguous`) is refused before
anything is edited, with the names on the diagram listed. `subViNameResolved` says which
name the helper used.
THE VERDICT IS `coercedAfter` AND `stillCoerced`, from a sweep over EVERY subVI call node
run before and after the edit - not the per-terminal rows. Those come from a helper that
matches one node by VI Name, which cannot speak for a diagram calling the same subVI twice:
measured, it reported terminals as already clean when it had in fact just replaced their
constants. `coercedAfter: 0` is the proof; `coercedAfter: null` means the sweep could not
run and nothing should be concluded.
Nothing is read back from the constant after the Replace: that method invalidates the
reference it was called on (error 1055) and its return value cannot be consumed either.
THE LAST STEP CLEARS BROKEN WIRES (`removeBadWires`, default true): a Replace that cannot
re-type a wire leaves it broken, and the VI then reads state 0. Every removed wire is listed
under `badWireCleanup`; a sink that lost its wire from a source makes `ok` false.
```

- `viPath`: Absolute path to the .vi whose constants should be bound
- `subViName`: The subVI call to work on, by its VI Name as LabVIEW reports it - e.g. 'CalculateSomething.vi'. A class or library member may be named bare ('Write Config.vi') or qualified ('Channel.lvclass:Write Config.vi'); a bare name is resolved to the one qualified name it matches on the diagram before the helper runs.
- `terminals`: Comma-separated terminal names on that call whose constants should be bound - e.g. 'Borkenkaefer,PlantColor'. Spelled exactly as lvai_vi_terminals prints them, including any double spaces. A terminal whose constant already carries the typedef is replaced again harmlessly; the sweep afterwards is what says whether anything was wrong.
- `constantLabels`: Comma-separated constant labels, positionally matched to `terminals`. Omit unless the labels differ from the terminal names, which is the convention this tool expects.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory, because the scripts folder next to the exe may be read-only.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvbd_bind_constant.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.
- `removeBadWires`: After the binds, clear the BROKEN wires left on the diagram (BD.Remove Bad Wires, in the IDE's instance) and list each under `badWireCleanup`. Default true. A sink that had a wire from a source and has none afterwards fails `ok` (`errorKind: badWireCleanupLostConnection`). A VI with no broken wire is not touched. (default: true)

## `lvai_build_from_build_specification`

```text
RPC BuildFromBuildSpecification. MUTATING: runs a build specification of a .lvproj and
returns the generated files. Writes build output to disk and can take minutes - raise
timeoutSeconds accordingly. This is the CI-shaped capability of the interface.
```

- `projectPath`: Absolute path to the .lvproj
- `buildSpecificationName`: Exact name of the build specification as it appears in the project
- `timeoutSeconds`: Local budget in seconds - builds are slow (default: 900)

## `lvai_check_aixml`

```text
Checks an AIXML file for the faults ValidateAIXML was MEASURED not to catch. Needs no
LabVIEW, no gRPC and no licence - it is pure text analysis, so it costs milliseconds and
works in CI. Run it before lvai_validate_aixml, not instead of it.
WHAT IT CATCHES, all measured 2026-09-03 by putting one small VI per case through the real
validator: a `uid_parent` naming no element - an ERROR, because LabVIEW silently reparents
that element to the TOP-LEVEL diagram, which moves a node out of the loop it belonged in and
reports nothing at validate, convert or run; a duplicate `uid`, which LabVIEW silently
renumbers so the export stops matching your file; and a Ring whose default `value` is not
among its `values`. It also reports, as INFO only, uids inside LabVIEW's reserved range.
THREE MORE, measured 2026-09-04 the same way: an ENUM `value` that is one of its LABELS
rather than an index - LabVIEW discards it and writes 0 - and an enum index past the last
item, which LabVIEW CLAMPS (9 became 4 on a five-item enum); plus a connector-pane terminal
with NO `connection` attribute, which LabVIEW reads as REQUIRED rather than as unspecified.
A required OUTPUT is the damaging one: it makes every CALLER non-executable with Error 1003
while the VI itself validates, converts, runs and exports perfectly.
TWO MORE, measured 2026-09-14 and 2026-09-15. A `Control`, `Indicator` or `Constant` with NO
`value` attribute is an ERROR that costs the WHOLE document: ConvertAIXMLToVI answers
`Error -2628, An error occurred while parsing the document` and writes NOTHING, while the
file is perfectly well-formed XML - so that message is about the SCHEMA, not your quoting.
And a `timestamp` carrying a NON-EMPTY `value` is a WARNING, because LabVIEW accepts the
document and silently DISCARDS the literal; measured against five types that keep theirs.
That one matters most in a generated round-trip test, which authors the written value and
the expected value in one document: both vanish, and the assertion then compares empty with
empty and PASSES while pinning nothing.
AND ONE MORE, measured 2026-09-15: the NET attribute is required too, and on an UNWIRED
terminal as much as a wired one - `outputs` on a Control or a Constant, `inputs` on an
Indicator. Same -2628, same whole-document loss, one attribute over. An interface
declaration passes its class wire through and leaves the payload alone, so its own payload
terminals are deliberately unwired and this is their normal shape; the spelling is the
attribute present with an EMPTY net, `outputs="value:"`.
AND PROPERTY/INVOKE NAMES, measured 2026-10-10: `fields="read+Version Number"` on
{LV.Application} KILLED LabVIEW inside ConvertAIXMLToVI, 4 of 4 runs; the name is
`Application:Version Number`. An ERROR (`propertyNameUnknown`) when a property name is
unknown and a known name of the class is `Category:` plus it - lvai_convert_aixml_to_vi
and lvai_validate_aixml REFUSE such a document before any RPC, and `fix` writes the full
name when only ONE fits (`Version Number` fits two - `Operating System:Version Number` is
the other - so it stays an error naming both). A WARNING (`propertyNameNotKnown`, `methodNameNotKnown`, `viServerClassUnknown`) for
any other unknown name, with the nearest known one. Known means in LabVIEW's catalogue OR
in AIXML known to work (NI's exports, our helpers): the catalogue alone misses half of
our own working helpers' names, so a warning is a prompt to verify, not a verdict.
WHAT THIS DOES NOT CHECK, and will not: an attribute the schema does not DECLARE at all -
`text` on a FreeLabel where the name is `comment`. Same -2628, but catching it cheaply
needs a copy of NI's attribute list per element, and a list one entry short would refuse a
WORKING document. ValidateAIXML names that one with a line and a column in about 6 ms,
which is the right place for it.
WHAT `fix: true` WILL NOT DO is as deliberate as what it will. It writes the type's literal
for a Control or an Indicator, whose value is a default state the type decides - and leaves
a CONSTANT alone, naming it, because there the literal is the DATA: an author who omitted it
may have meant 42, and writing 0 turns a document that refuses to convert into one that
converts and computes the wrong answer. The discarded timestamp is likewise reported and
never repaired, since no non-empty timestamp literal exists to correct it to. A missing NET
attribute is repaired only where the document decides it: the empty net when nothing reads
the terminal, the real net when exactly ONE thing does, and nothing at all when several do.
WHAT IT DOES NOT DO, on purpose: terminal names, types, wiring, cycles and case completeness
are things only LabVIEW knows, and it checks them well. This is a pre-filter that moves cheap
failures off the round trip; lvai_validate_aixml is still required.
A NEW VI NEEDS A REQUIRED INPUT - the user's rule of 2026-10-31, which replaced the old
"error in is recommended": with newVi (the default) `error in` must be connection=
"required" (`errorInNotRequired`, repaired by fix), and a VI with inputs but no `error in`
must have its most important input required (`noRequiredInput`, an ERROR never repaired -
only the author knows which input that is). Pass newVi: false for the AIXML of a VI that
ALREADY EXISTS - an extension or revision keeps every connector-pane flag as it was, and
then fix touches none of them.
```

- `aiXmlFilePath`: Absolute path to the .xml file to check
- `fix`: Also REPAIR what can be repaired unambiguously - a duplicate uid nothing is nested inside, a uid in LabVIEW's reserved range, an enum value given as a LABEL (set to that label's index), and an output terminal with no `connection` (set to recommended, because a required output is never right), and a property name with its category dropped when exactly one full name fits (`Version Number` -> `Application:Version Number`, in fields and terminals). A dangling uid_parent, a bad Ring default, an enum index out of range and a required INPUT are never repaired: the author's intent is unknown, and for the parent, putting it on root IS the damage LabVIEW already does (default: false)
- `fixedPath`: Where to write the repaired XML. Omit to overwrite aiXmlFilePath in place
- `newVi`: TRUE (the default) for a VI generated COMPLETELY NEW: it must have a required input - `error in`, or without one its most important input. FALSE for the AIXML of a VI that already exists, being extended or revised: no connector-pane flag is checked or repaired, so callers of that VI keep working. (default: true)

## `lvai_check_build_contract`

```text
Build: check the BUILD CONTRACT of a multi-VI application - one JSON file naming every VI
with its path, owner, builder agent, connector pane (terminal names, types, conIdx,
connection) and what it calls. The orchestrator writes it BEFORE spawning any agent and
hands each agent the file; agents implement against it instead of settling interfaces
themselves. Format and workflow: docs/build-contract.md.
mode=plan (no LabVIEW): refuses unknown keys, then checks the house rules for every VI the
build creates (error in required + error out, error cluster type, no required output,
every terminal on a slot of its pattern, error corners), duplicate and clashing file
names, calls that resolve to no or several VIs (a bare `Deposit.vi` beside
`Account Manager.lvlib:Deposit.vi`), installation namesakes, two builders in one folder,
and cycles. Answers the build WAVES (what can be built in parallel, in order) and, for the
names in `callSkeletonsFor`, a ready `<Call>` per VI so a caller needs no lvai_vi_terminals.
mode=verify (after a wave): exports each built VI named in `vis` (default: all that exist)
and compares its pane with the contract - missing or extra terminals, type, conIdx,
connection, direction. `ok` is false on any difference or on a named VI not yet built.
```

- `contractPath`: Absolute path to the contract JSON (build-contract.json)
- `mode`: plan (default): check the contract, no LabVIEW. verify: compare built VIs with it (default: "plan")
- `vis`: verify only: the VIs to compare, one per line, as contract paths or names. Empty: every contract VI that exists on disk
- `callSkeletonsFor`: plan only: VI names or paths, one per line, whose Call skeleton to return
- `timeoutSeconds`: Local budget in seconds per exported VI (verify) (default: 180)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.

## `lvai_close_active_project`

```text
MUTATING: SAVES the project that is active in the LabVIEW IDE and then CLOSES it, releasing
it and its members from memory. There is no RPC for this either; it composes the VI Server
route, reached through Application:Project:Active Project.
IT SAVES FIRST, and that is not a convenience. The Close method carries NO save parameter -
verified by generating the node and exporting it back, its only inputs are `reference` and
`error in`. An unsaved project would therefore risk LabVIEW's modal save prompt, and a modal
dialog stops the entire gRPC service until a human dismisses it. If you do not want the
project saved, close it by hand in the IDE instead.
Error 1055 means no project was active, so there was nothing to close. That is also what a
second call returns, which is how the close can be verified.
Both Save and Close were found by PROBING: the VI Server catalogue does not list the
{LV.Project} class at all, and an earlier revision of docs/vi-server-reference.md concluded
from that there was no way to close a project. ValidateAIXML accepts a real method name and
rejects an invented one, which is what turned the guess into a measurement.
```

- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory. Generated once and reused; pass regenerateHelper to force a rebuild.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvai_close_active_project.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `projectPath`: The .lvproj being closed. OPTIONAL: without it the ACTIVE project's path is read from LabVIEW before the close, so the sweep runs either way (`projectPathFrom` says which). The saved file is swept for items LabVIEW adopted into it - helper VIs out of our temp trees, sockets under <userlib>/LV_MCP, and entries whose file is not there - and the answer NAMES every one it removed. The save this tool performs is what writes those entries, so this is the step where they appear.
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview the close helper is COMPOSED and run over LabVIEW's ActiveX server - the same Save and Close, the same sweep afterwards. helperViPath and regenerateHelper apply to the grpc helper only.

## `lvai_close_vi`

```text
MUTATING (IDE state): closes a VI's front panel INSIDE the IDE's application instance,
which releases the VI from memory. Call this when lvai_convert_aixml_to_vi answers
Error 1357, "a LabVIEW file from that path already exists in memory" - opening a VI with
lvai_open_file is enough to cause that, so it is the normal state after looking at
generated code.
There is NO RPC for this: nothing in the lvai surface closes anything, and the VI Server
catalogue has no unload method, so this composes the VI Server route - generate the helper
from scripts\lvai_close_vi.xml (once, then reused) and run it.
TWO PRECONDITIONS, both measured. A project must be ACTIVE in the IDE, or the chain fails
with Error 1055. And the VI must be a MEMBER of that project, opened through it - a VI
opened loose is loaded where the project's application cannot see its panel, and the write
fails naming Front Panel Window:State. Generate VIs into a project and this holds by
construction.
Do NOT reach for FP.Close or FP.Set Close If Lonely instead: measured, they report no error
and do nothing, because a generated helper runs in the ADDON's instance where the VI's
windows do not exist.
`closed` says the chain raised no error. The decisive proof is the regeneration itself,
which this tool deliberately does not perform.
```

- `viPath`: Absolute path to the .vi to release from memory
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory, because the scripts folder next to the exe may be read-only. Generated once and reused; pass regenerateHelper to force a rebuild.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvai_close_vi.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_coercion_dots`

```text
READ-ONLY: reports every terminal of every subVI call on a VI's block diagram together with
its {LV.Terminal} Coercion Dot?. A dot means the wired type is not identical to the terminal
type, so LabVIEW is converting on the wire.
WHY IT EXISTS: the placeholder-plus-retarget route - the only way a generated VI can call
project-local code - leaves a dot on every terminal whose real subVI carries a typedef,
because AIXML cannot express a typedef and the stub is cloned with the bare underlying type.
Validation, the retarget and a run ALL pass in that state. This is the only thing that sees
it, the same way lvai_connector_pane is the only thing that sees a misplaced pane.
WHICH REPAIR depends on what feeds the terminal, and the answer comes back in
`repairs`: a diagram CONSTANT is lvai_bind_typedef_constants, a front-panel CONTROL of
the calling VI is lvai_bind_pane_typedef. This description named only the first until
2026-09-18 and was measured sending a build at the tool that could not reach its case.
Needs no active project. With one active it opens the VI in that project's application
instance, where the IDE holds it; with none, in its own, so it also works while pylv_apply
has the project closed. It used to open the VI outside the project always, and measured
2026-10-13 that copy read NO subVI calls while the project held the VI.
ONE ENTRY PER CALL NODE, not per subVI name. A diagram may call the same subVI several
times and each call is wired separately, so each is reported on its own with the
`nodeIndex` that identifies it. Omit subViName to sweep the whole diagram; pass it to
restrict the sweep to one subVI's calls - it still reports every node that calls it.
Unassigned connector pane slots are dropped rather than reported as nameless terminals.
`clean` is only ever true when nodes were actually examined: a sweep that saw nothing says
so instead of passing for a clean bill of health.
A DOT ON A VARIANT INPUT IS NOT A FINDING: it is LabVIEW's ordinary conversion into a
Variant (every Caraya assert input is one), there is nothing to bind, and it is reported
as `intoVariant` and counted under `coercedIntoVariant` instead of `coerced`, so it does
not make `clean` false.
```

- `viPath`: Absolute path to the .vi to inspect
- `subViName`: Check only this subVI call, by its VI Name - e.g. 'CalculateSomething.vi'. A class or library member may be named bare ('Write Config.vi') or qualified ('Channel.lvclass:Write Config.vi'); a name that matches nothing is refused with the names that ARE on the diagram. Omit to sweep every subVI call on the diagram, which costs one helper run per call.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvbd_coercion_dots.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_connector_pane`

```text
Which conIdx sits WHERE on a connector pane - measured, not remembered. Call this before
writing conIdx into AIXML, and again after generating, because the pane pattern is chosen by
the generator and cannot be predicted from the indices you use.
With viPath: measures that VI's pane through VI Server, joins it with the VI's AIXML export
and answers with the slot map, every terminal's current position, every breach of NI's style
guide, and the conIdx each terminal SHOULD have. Read-only - it neither changes nor keeps the
VI in memory.
With pattern: the measured slot map for one pattern id, no LabVIEW needed.
With neither: all 36 patterns (4800-4835), which of them have measured geometry, AND the
pattern a newly generated VI gets on this station - LabVIEW hands every new VI the default
pane from LabVIEW.ini, key DefaultConPane, so that one IS knowable before you generate.
Call it with no argument first to learn which conIdx to write, then with viPath to check what
you actually got.
WHY IT EXISTS: the numbering is NOT the same across patterns. On 4815 the bottom-left slot -
where error in belongs - is conIdx 8; on 4833 it is 11, and conIdx 0 is bottom-RIGHT on 4815
but TOP-LEFT on 4833. A generated VI has come out as either. Guessing from one pattern's map
put two inputs on the output edge in a VI that validated and ran.
A pattern with no measured geometry says so instead of guessing: `Pattern` is read-only in VI
Server, so a pattern can only be observed on a VI that already uses it.
ON THE pylabview BACKEND, with viPath, nothing reaches LabVIEW or NI's service: the pattern
is read from the FILE (scripts/pylv-conpane.py), the terminals from our reader's export
(vi2aixml.py), and the slot positions from the measured pattern table - the same answer
and the same verdict, with the table's majority orientation standing in for the probe's
measurement. A viPath answer names `backend` and `backendSource` on its last line.
```

- `viPath`: Absolute path to the .vi to measure. Omit to ask about a pattern instead
- `pattern`: A pattern id, 4800-4835, to look up without touching LabVIEW. Ignored when viPath is given, because a measurement beats a table.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory. Generated once and reused; pass regenerateHelper to force a rebuild.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvpane_probe.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. Only viPath reaches either: a pattern or the full list needs no LabVIEW and no backend. On pylabview the helper arguments are not used - there is no helper.

## `lvai_convert_aixml_to_vi`

```text
RPC ConvertAIXMLToVI. MUTATING: creates a real .vi file at viPath from an AIXML file,
overwriting whatever is there. This is LabVIEW code generation.
With openVI the new VI is also opened in the IDE.
Validate the XML first (lvai_validate_aixml) and write to a scratch path until the
output is what you expect.
BEFORE authoring AIXML call lvai_aixml_reference - the format has no published schema
and two rules fail silently: a `uid.terminal` string names a NET (wire), not a
pointer to an element, and fan-out is expressed by repeating that net string; and
terminal names are literal LabVIEW labels that must be looked up, not guessed
(`Increment` -> `x+1`, but `Greater?` -> `x > y?` with spaces).
A Call to a project-local subVI is rejected (Error 53) UNLESS that subVI is OPEN in
LabVIEW - through its project or loose - when this runs; then it resolves by bare name
and the link is written into the file. Validation refuses it either way; lvai_generate_vi
converts past that refusal for you, under a throwaway name, and checks executability. A
failed convert here burns the caller's name (1051 next time). Measured 2026-09-25 on plain VIs and on class members (open one member and
the whole class resolves); a .ctl is NOT accepted. docs/aixml-call-loaded-vi.md.
IT WRITES WHAT VALIDATION REFUSES, ON PURPOSE - that tolerance is the route for class
methods and loaded subVIs - so a written file is not a working one. checkExecutable (on
by default) reads the result's execution state afterwards and answers `executable`; a
broken VI comes back with ok false and `warning` saying so, instead of a clean errorCode 0.
Measured 2026-10-01: a Web Browser document the validator refused was written here, eBad,
with nothing in the answer to say so.
```

- `aiXmlFilePath`: Absolute path to the source AIXML .xml file
- `viPath`: Absolute path of the .vi to create - WILL BE OVERWRITTEN
- `openVI`: Open the created VI in the LabVIEW editor (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 240)
- `checkExecutable`: Read the written VI's execution state and answer `executable`; a broken VI then answers ok false (default: true)
- `allowUnknownPropertyNames`: Convert a document whose Property Node uses a SHORT property name the check knows the full form of (`Version Number` for `Application:Version Number`). Off by default: that was measured KILLING LabVIEW in this RPC, 4 of 4 runs, so the document is refused before anything reaches LabVIEW. lvai_check_aixml with fix repairs it. (default: false)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.

## `lvai_convert_vi_to_aixml`

```text
RPC ConvertVIToAIXML. Serializes an existing VI into LabVIEW's AIXML text format and
writes it to aiXmlFilePath. Does NOT modify the VI. With returnContent the XML is also
returned inline, which is normally what you want when reading code.
This is the reference path for learning the AIXML dialect before generating any.
CACHED ON DISK, but only for VIs that belong to the LabVIEW INSTALLATION - the examples
tree, vi.lib, user.lib and every LVAddon, which is to say exactly the example and palette
VIs you read to learn from. MEASURED over 1677 of them: median 331 ms per export, p99 24 s,
worst case 93 s, and the cost is loading the VI rather than writing the XML (size and
duration correlate at r = 0.002). A second read of the same VI is a file copy, and needs no
running LabVIEW at all.
YOUR OWN CODE IS NEVER CACHED and needs no flag for that: an export depends on a VI's
subVIs too, and user code changes one subVI at a time behind a caller whose own timestamp
never moves. Every answer carries fromCache and a one-line cacheNote, so which of the two
happened is visible rather than guessed. Pass refresh to re-export a cached VI anyway.
A VI INSIDE AN `.llb` CONTAINER FILE - every DAQmx VI - is read by its path THROUGH the
`.llb`, as LabVIEW writes it (`...\configure\task.llb\DAQmx Clear Task.vi`): NI's converter
takes it as it is, and its export is cached keyed on the `.llb`'s stat since 2026-10-15;
on pylabview the reader extracts the container into %USERPROFILE%\.labviewmcp\cache\llb
once and reads the member there.
ON pylabview THE CACHE IS USED TOO, since 2026-10-15, kept apart from NI's entries by its
key: a cached NI export of the VI is served first (higher fidelity, no NI service needed to
read it), then the reader's own earlier export, else a fresh read is cached under the
reader's key. A reader entry is never served to a grpc call, and the reader's code hash is
part of its key, so a fixed reader never serves an old reading. `exportBy` names whose
export came back.
```

- `viPath`: Absolute path to the source .vi
- `aiXmlFilePath`: Absolute path of the .xml file to write
- `returnContent`: Also return the written XML inline (default: true)
- `maxContentChars`: Truncate inline content to this many characters (0 = unlimited) (default: 60000)
- `timeoutSeconds`: Local budget in seconds (default: 180)
- `refresh`: Ignore any cached export and ask LabVIEW again (default: false)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.

## `lvai_convert_vis_to_aixml`

```text
Several VIs through ConvertVIToAIXML in one call, written into outputDirectory. Use this
when TRIAGING CANDIDATES - deciding which example or template is the right starting point -
where the alternative is one tool call per candidate.
WHAT IT DOES AND DOES NOT PARALLELISE, both measured. Cached exports are served
CONCURRENTLY: a hit is a file copy in this process with no LabVIEW involved, and concurrent
file reads are worth about 21x on a cold tree. Anything not cached is exported
SEQUENTIALLY, because LabVIEW serialises the RPC anyway - six ConvertAIXMLToVI calls issued
at once took 559 ms against 543 ms one after another, so firing them concurrently buys
nothing and a slow one would make every other call queue behind it.
The practical consequence: the first pass over a set of candidates costs what it costs, and
every later pass is nearly free. Caching rules are those of lvai_convert_vi_to_aixml -
installation VIs only, never your own code.
Content is NOT returned inline by default: a batch of full exports is large, and the point
here is to get the files onto disk cheaply so you can read the ones worth reading. Each
answer gives that VI's xmlPath.
```

- `viPaths`: Absolute .vi paths, ONE PER LINE
- `outputDirectory`: Absolute path of the directory to write the .xml files into
- `returnContent`: Also return each export inline (default: false)
- `maxContentChars`: Truncate each inline export to this many characters (0 = unlimited) (default: 20000)
- `timeoutSeconds`: Local budget in seconds, per VI (default: 180)
- `refresh`: Ignore cached exports and ask LabVIEW again (default: false)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview each VI is read by vi2aixml.py through the export cache, as lvai_convert_vi_to_aixml reads one - NI's cached export first, then the reader's own - and no LabVIEW is asked.

## `lvai_create_accessors`

```text
MUTATING: creates Read and/or Write accessor VIs for EVERY private data field of a
.lvclass, saves each one beside the class, and writes the member list into the class file.
This is LabVIEW's own wizard, not a reimplementation: it calls
MemberVICreation.lvlib:CLSUIP_CreateNewAccessor.vi, the body behind the IDE's
"New >> VI for Data Member Access", so the VIs are correct by construction. Verified by a
pylabview diff against wizard-made accessors for another class - same front-panel heap
including both udClassDDO objects, same class refnums, same connector pane pattern, same
.lvclass item block.
NEITHER AIXML NOR PYLABVIEW CAN DO THIS. An accessor needs a front-panel control whose type
is the class; AIXML refuses to author one, and the heap object it needs (udClassDDO) is of a
class pylabview never writes from scratch. docs/lvclass-creation.md has both measurements.
PRECONDITION: the class must belong to a project that is ACTIVE in the IDE, because the
class reference is found among Project:Active Project's classes. `classIndex` -1 in the
answer means the path was not among them - check the project is open and that lvclassPath
spells the same path LabVIEW reports.
accessUi = "R/W" creates BOTH accessors per field in one wizard call, which is the default
and halves the work. dynamicDispatch true gives dynamic dispatch terminals.
The answer lists every VI created with the path it was saved to, so which field an accessor
belongs to is reported rather than assumed; fieldIndex is the field's REAL position in the
cluster, not its position within this call's slice.
A CLASS WITH MANY FIELDS NEEDS SEVERAL CALLS, and fieldCount DEFAULTS TO 2 for that reason.
The per-field cost is not constant: Save All This Library checks the whole library every time,
so it went 11.5 s with an empty class, about 20 s at 6 members and past 30 s at 12 - which
means three fields fit the FIRST call and two fit any of them, against a client that gives up
near 60 s.
BUT YOU NO LONGER TRACK THE OFFSET, AND THAT IS THE POINT: `fromField` defaults to -1, which
RESUMES from the class file's own member count, and one call now takes slice after slice
until either the class is finished or `budgetSeconds` (45, deliberately under the client's
limit) is spent. So the whole class is "call it, read moreToDo, call it again with the same
arguments" - no offset arithmetic, and a retry after a timeout is correct by default.
`slicesRun` and `slices` say what one call actually did. The library is saved after EVERY
field, so an interrupted call leaves a consistent partial class rather than orphan VIs the
class file does not mention.
A TIMED-OUT CALL HAS USUALLY DONE ITS WORK, and "usually" is now the honest word. The client
gives up near 60 s while the helper keeps going, and the library is saved after every field.
This paragraph claimed the class is therefore "left consistent - never a mismatch"; that is
true only when the timeout lands BETWEEN fields. Measured 2026-09-02: one landed inside a
pair, leaving SEVEN accessor members, and the resume then rebuilt that field and NI's wizard
appended a number rather than refusing - `Read Bio 2.vi` - while every step reported
errorCode 0. So on "Request timed out", call again: the default -1 reads the class file and
continues. If it finds a half-built field it now REFUSES with `halfBuiltAccessors` and names
it, instead of resuming onto it; delete that orphaned accessor and its <Item> block with the
project closed, then call again. And any run that ends with duplicate accessors on disk
answers `ok: false` with `mangledAccessors`, because LabVIEW does not consider the
duplication an error and nothing else would report it. Every successful answer also carries membersBefore,
membersAfter and nextFromField, all read off the class file rather than from the run.
VERIFY IMMEDIATELY - lvai_describe_class reads the .lvclass file and needs no LabVIEW, so
the check survives whatever the IDE does next. This used to read "expect LabVIEW to go down
a few minutes after the run", and that is no longer what happens: the helper left the
project reference and the IDE application reference open, and closing both changed the
outcome. Before: LabVIEW left about three minutes after ONE run. After: three runs back to
back, twice, with the process still up half an hour later.
The leak is reduced rather than gone, so do not read this as a clean bill of health. The
session log still carries `DestroyPlatformEvent failed with MgErr 42`, roughly 13 per run
against 15 for a single run before. Every faulting site is NI's own code, and the minidump
count means nothing - a three-node VI with no VI Server in it also produces three.
RUN IT ONCE PER CLASS ON A CLEAN MEMORY. An earlier failed run leaves accessors in memory
unsaved, and then the next run names them "Read X 2.vi" and the class file never gets its
member list. Restart LabVIEW or close the project first.
EVERY ACCESSOR COMES OUT SOURCE-ONLY - no VICD, TM80, DFDS or CCST - and `sourceOnly` says
so per VI, read from each saved file. The wizard decides that from the PROJECT's
NI.LV.All.SourceOnly ("Separate compiled code from new project items"): measured
2026-10-12, false gave all six accessors all four blocks, true gave none. So with
projectPath this call sets it first when the project lacks it - closing the project if
LabVIEW holds it, because it is a file edit - and reports that as `projectSourceOnly`;
compiled code in an accessor after that is `ok: false`. Without projectPath nothing can be
set, and `sourceOnlyNote` says why the accessors carry compiled code.
```

- `lvclassPath`: Absolute path to the .lvclass whose fields need accessors
- `dynamicDispatch`: Dynamic dispatch terminals (the wizard's 'Dynamic'). False gives static (default: true)
- `accessUi`: Which accessors to create: "Read", "Write" or "R/W" (default, both) (default: "R/W")
- `includeErrorTerminals`: Give the accessors error in / error out terminals (default: true)
- `makeAvailableThroughPropertyNodes`: Expose the accessors through Property Nodes (default: false)
- `virtualFolderName`: Virtual folder inside the class to put them in; empty for the class root (default: empty)
- `fromField`: First private data field to build accessors for, counting from 0. Together with fieldCount this bounds one call to a SLICE of the cluster, which is how a class with many fields gets built without any single call outliving the MCP client's patience. MEASURED: a 7-field class needs about 70 s and the client gives up near 60 s, twice leaving 12 of 14 VIs on disk. Two calls of 4 and 3 fields fit with room to spare. LEAVE IT AT -1, WHICH RESUMES. The class file's own member count says how many fields already have accessors, so -1 reads it and starts there - which is what nextFromField has always reported and what a caller used to spend a turn passing back in. It also makes the retry after a client timeout correct by default: measured 2026-08-29, a slice that answered `Request timed out` had COMPLETED on disk, and resuming from the file picked up exactly where it stopped. Pass an explicit index only to rebuild a slice. (default: -1)
- `fieldCount`: How many fields to build from fromField. TWO by default, and the number is measured rather than chosen: the per-field library save gets slower as the class grows, because Save All This Library checks the whole library every time. On one 7-field class the cost per field went 11.5 s with an empty class, about 20 s at 6 members, past 30 s at 12 - so three fields fit only the FIRST call and two is the honest default. A larger number is harmless in itself (Array Subset clamps past the end); it may just not come back inside the client's patience. (default: 2)
- `tidyProject`: Strip helper items out of the .lvproj on disk when the run is done. OFF BY DEFAULT, and the reason is the worst regression this tool has had: it rewrites the project FILE while LabVIEW still holds that project OPEN, and LabVIEW does not survive it. MEASURED as an A/B/A on 2026-08-26, three accessor runs each time from the same reset state. The build from before this option existed: three runs ok, 14/10/10 members, LabVIEW stable four minutes. The build with it on: three runs FAILED, 0 members, LabVIEW gone in twenty seconds. The old build again, immediately after: ok again, stable. Then the same new build with tidyProject FALSE: 14 members, LabVIEW alive. So it is this option, not the machine, and not the Auto Dispose Ref edit that was reverted first while chasing it. Turn it on ONLY when LabVIEW does not have the project open - after --finish-project has stopped LabVIEW, for instance. It is otherwise safe and idempotent: it edits only items whose URL points into the helpers directory. (default: false)
- `closeProject`: Also SAVE and CLOSE the active project first, which flushes the in-memory helper item so it can be stripped. MEASURED TO HAVE KILLED LABVIEW when done immediately after an accessor run - eight BadLinkerObjs assertions naming the class private data control, and the process gone two seconds later. Off by default for that reason; if you want it, restart LabVIEW between the run and the close. (default: false)
- `projectPath`: The class's .lvproj. It is opened first, its "Separate compiled code from new project items" is set so NI's wizard writes SOURCE-ONLY accessors, and it is the one tidied. Omit it and the ACTIVE project is used - the one the wizard works on - else the nearest .lvproj above the class that lists it; `projectPathFrom` says which. Until 2026-10-13 an omitted projectPath skipped the setting, and the accessors carried compiled code.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory. Generated once and reused; pass regenerateHelper to force a rebuild.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvai_create_accessors.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `budgetSeconds`: How long this call may keep taking SLICES before it returns, in seconds. It runs slice after slice - resuming from the class file each time - until the class is finished or this budget is spent, then answers with moreToDo. 45 by default, and the number is the MCP client's limit rather than a preference: the client gives up near 60 s, so a loop that simply built every field would reliably outlive its own answer, which is the exact failure slicing exists to prevent. The budget is checked BETWEEN slices, so one slow slice can still overrun it - the check bounds how many are started, not how long one takes. (default: 45)
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview NO STEP touches NI's service: the wizard helper is COMPOSED and run over LabVIEW's ActiveX server, and the project opened and closed the same way. NI's accessor wizard is the same VI either way. helperViPath is ignored there - composed helpers live in their own folder. LabVIEW must already be running.
- `setIcons`: Draw an icon on every accessor this call creates - the class's first word as the banner, READ or WRITE and the field under it - as the last step of each slice. Default true; a class that has its OWN icon keeps the template the wizard laid on and is not touched. Reported per VI under `icons`, never gating. (default: true)

## `lvai_create_class`

```text
MUTATING: creates a real `.lvclass` on disk, with a private data control carrying the fields
you name, and lists it in a project. LabVIEW's OWN project provider VIs do the work -
`Add Class.lvlib:Add Class to Project (path).vi` and `Message Maker.lvlib:Add Member Data to
Private Data Control.vi` - which is the whole design, because a private data control is
COMPILER OUTPUT. Building one from a converted VI gave classes LabVIEW reported normally and
its compiler refused, for weeks. docs/lvclass-creation.md section 2a has that diagnosis.
The sequence, all of it returned under `steps`: prepare the .lvproj, open it, generate a
CARRIER VI whose front-panel controls are the fields, run NI's two providers against it,
close the project, write the class entry into the .lvproj, then verify FROM THE CLASS FILE.
A RUNNING, ACTIVE PROJECT IS THE PRECONDITION. The providers reach LabVIEW through
`Project:Active Project` and answer Error 1055 without one.
BACK-TO-BACK CALLS ON ONE PROJECT WORK, and no LabVIEW restart is needed between them. That
was not true until 2026-08-28: the helper leaked the Class refnum NI's provider returns,
which kept the new class in LabVIEW's memory after the project closed, so the next run could
not bind the project item to it and created the child as a ROOT class with no error. Closing
that one reference fixed it. THE PARENT NO LONGER COMES FROM THE PROJECT AT ALL: the helper
opens it from its path with {LV.Application} LVClass.Open, which needs no project membership
- probed against a project listing no classes. That removes the whole chain that made this
fragile (parent must be a project member -> the .lvproj must list it -> the file must be
written -> the project must be closed to allow writing it). `parent opened` replaces the old
`parent index`: a boolean, false when a parent was asked for and did not open.
FIELDS are `<type>.<name>`, comma separated, the same spelling AIXML's cluster grammar uses:
`string.Manufacturer,int32.Year Of Manufacture,double.Top Speed kmh`. Scalars only - string,
path, bool, double, single, timestamp and the int/uint widths. A cluster, array or enum field is
not supported and is refused by name. Omit `fields` for a class with empty private data.
INHERITANCE: pass parentClassPath. The parent must already be LISTED IN THE PROJECT - NI's
provider finds it by searching the active project's classes and, finding nothing, silently
makes a ROOT class with no error at all. That is checked here rather than trusted, and the
answer says which of the two causes it was.
ACCESSORS ARE A SEPARATE CALL: `lvai_create_accessors`, which drives the IDE's own
"VI for Data Member Access" wizard. AIXML cannot author them - it refuses a class-typed
terminal (`Control with type=UDClassInst is not supported`).
Needs a running LabVIEW throughout. No pylabview bundle is involved any more.
```

- `className`: Class name without the extension, e.g. Auto
- `directory`: Folder to create <className>.lvclass in; created if absent
- `fields`: Private data fields as `<type>.<name>`, comma separated, e.g. `string.Manufacturer,int32.Year Of Manufacture`. A DEFAULT is `<type>.<name>=<value>`, e.g. `double.Gain=1,bool.Enabled=true` - without one a field defaults to its type's empty value (0, false, ""). Not for timestamp fields. Omit for empty private data. A TYPEDEF field from typedefFieldsJson may be PLACED here as `typedef.<name>`, e.g. `typedef.Config,double.Gain=1` - it then keeps that position. Left out, it goes after every field listed here. Naming it here with a real type (`string.Config`) is refused.
- `typedefFieldsJson`: Fields that are TYPEDEF instances, as a JSON object: field name -> absolute path of an existing typedef .ctl, e.g. {"Config":"C:\\T\\Typedefs\\Channel Config.ctl"}. Each is created as a placeholder and then bound to its .ctl with lvai_bind_class_fields in the same call, so the field keeps its name and carries the typedef. Needs projectPath. Do NOT also list it in `fields` with a type; write `typedef.<name>` there to fix its position, or leave it out and it comes after the fields listed there. Create the accessors AFTER this call - an accessor made before the bind keeps the bare type. Until 2026-09-25 this took two calls and a placeholder type chosen by hand.
- `parentClassPath`: Absolute path to the parent .lvclass this one derives from
- `parentInterfaces`: Interfaces this class implements, ONE ABSOLUTE PATH PER LINE. Each must be a .lvclass whose NI.LVClass.IsInterface is true - checked here from the file, because passing an ordinary class would land in NI's `Parent Interfaces` terminal where nothing says it is wrong. This is MULTIPLE inheritance and it is legal: a class may implement any number of interfaces alongside its single parent class. NI's Basic Interfaces example has Flathead implementing both Rotating Tool and Lever. THE LINK IS ONLY SETTABLE AT CREATION TIME - NI's provider takes it as an input, and there is no scriptable way to add one afterwards (CLSUIP_ChangeInterfaceInheritance.vi is a modal dialog, which would stop the gRPC service). So a class that should implement an interface has to be created with it, or deleted and created again. That is why this is a parameter here rather than a separate tool.
- `projectPath`: A .lvproj to list the class in - created if absent, extended if it exists. When omitted a throwaway project is used for the load check and deleted afterwards.
- `verify`: Read the finished .lvclass back and gate `ok` on it: field count, private data size and the parent link. FROM THE FILE, not through lvai_describe_project - that check was here and reported `ok: false` for a perfectly sound class, because LabVIEW still had the project loaded from step 1 and served its cached copy rather than the .lvproj just written. Measured 2026-08-28 on a cold run. (default: true)
- `overwrite`: Replace an existing .lvclass. Refused by default - it would drop its members (default: false)
- `settleMs`: Milliseconds to wait after the .lvproj has been written and before LabVIEW opens it. A MITIGATION FOR A SUSPECTED RACE, not a diagnosis: the file is written and LabVIEW reads it milliseconds later, and this run has repeatedly seen LabVIEW open a project without a class the file plainly listed. Whether timing causes that is unproven - the pause is a parameter so it can be set to 0 and measured against. (default: 400)
- `keepCarrier`: Keep the generated carrier VI on disk instead of deleting it with the rest of the work directory. TRUE by default, and it is not tidiness that is being traded away: LabVIEW adopts the carrier into the project, and deleting it leaves LabVIEW holding a project whose items no longer exist. That broken copy survives the close, overwrites the .lvproj with itself, and stops `Project:Active Project` answering - which the accessor phase reports as Error 1055. The carrier's project ENTRY is stripped from the file either way; only a 6 kB VI in %TEMP% is left behind. (default: true)
- `timeoutSeconds`: Local budget in seconds, per step (default: 180)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview NO STEP touches NI's service: the throwaway project is opened and closed, the carrier composed, and NI's own class provider run, all through COMPOSED helpers over LabVIEW's ActiveX server. LabVIEW must already be running. typedefFieldsJson binds on the same route - lvai_bind_class_fields inherits the backend.

## `lvai_create_interface`

```text
MUTATING: creates a real LabVIEW INTERFACE on disk and lists it in a project. An interface
is a `.lvclass` file - there is no `.lvinterface` - and NI's manual defines it as "a class
without a private data control". That one difference is what enables a form of MULTIPLE
inheritance: a class has one parent class but any number of interfaces, so its objects can
be passed into several modules each requiring a different role.
LabVIEW's OWN project provider does the work: `Add Interface.lvlib:Add Interface to Project
(path).vi`, an exact mirror of the class provider - measured 2026-08-31 with
lvai_vi_terminals against both - with two differences that matter. There is NO `Parent
Class` terminal at all, because an interface may only inherit from other interfaces, and
the refnum it returns is called `Interface`. A RUNNING, ACTIVE PROJECT IS THE PRECONDITION,
as for classes: the provider reaches LabVIEW through `Project:Active Project` and answers
Error 1055 without one. LabVIEW works in a THROWAWAY project it opens itself, so the
project you keep is never opened, adopted into, or saved over.
NO FIELDS PARAMETER, and that is not an omission: an interface cannot hold private data, so
there is no carrier VI and no call to add-member-data - the two steps that make up most of
`lvai_create_class`. If you want data, you want a class.
INTERFACE METHODS ARE SCRIPTABLE - use `lvai_add_class_method`. This description said they
were an IDE gesture until 2026-09-14, which was already wrong when written: CLAUDE.md
corrected it on 2026-09-07, measured over five VIs on IVehicle.lvclass. An interface IS a
`.lvclass`, so LVClass.Open, AddItemFromMemory, {LV.Control} Replace and SetWireRule all
behave the same on one, and the tool does not inspect NI.LVClass.IsInterface at all.
Following this paragraph cost one agent a hand-built duplicate of a tool that worked.
FINISH THE INTERFACE - the .lvclass AND every method - BEFORE the first class that
implements it. `lvai_create_class` takes the interface list at CREATION time only, and a
declared method breaks every implementing class until that class's override exists.
docs/lvclass-interfaces.md has the measurements.
NAMING, from NI's manual: avoid a leading capital `I`. LabVIEW distinguishes interfaces and
classes by GLYPH, most of the IDE treats them identically, and callers do not care which
they have - so avoiding the `I` lets a class become an interface, or the reverse, without
refactoring caller code. Prefer a capability (`Can Measure Voltage`) or a category (`Lever`).
Advice, not a rule: the name you pass is used as given.
Verification is FROM THE FILE - `NI.LVClass.IsInterface` and the absence of a private data
item - because `lvai_describe_project` reports an interface as `Type="LVClass"`, exactly
like a class, and cannot tell you which you got.
```

- `interfaceName`: Interface name without the extension, e.g. Lever or Can Measure Voltage
- `directory`: Folder to create <interfaceName>.lvclass in; created if absent
- `parentInterfaces`: Interfaces this one inherits from, ONE ABSOLUTE PATH PER LINE. An interface may inherit from other interfaces and from nothing else - there is no Parent Class terminal on NI's provider. Each path is checked to BE an interface before anything is written.
- `projectPath`: A .lvproj to list the interface in - created if absent, extended if it exists. When omitted a throwaway project is used for the run and deleted afterwards.
- `verify`: Read the finished file back and gate `ok` on it: the IsInterface flag and the absence of a private data item, plus every parent link asked for. From the FILE, never through lvai_describe_project - that reported ok:false for a sound class because LabVIEW served its cached copy of the project. (default: true)
- `overwrite`: Replace an existing .lvclass. Refused by default - it would drop its members (default: false)
- `settleMs`: Milliseconds to wait after the .lvproj has been written and before LabVIEW opens it. Carried over from lvai_create_class, where it mitigates a SUSPECTED race that has never been diagnosed. Set to 0 to measure against it. (default: 400)
- `timeoutSeconds`: Local budget in seconds, per step (default: 180)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview NO STEP touches NI's service: the throwaway project is opened and closed and NI's own interface provider run through COMPOSED helpers over LabVIEW's ActiveX server, as lvai_create_class does there. LabVIEW must already be running.

## `lvai_create_library`

```text
MUTATING: creates a NEW, empty .lvlib through LabVIEW's own VI Server -
{LV.Application} Library.Create in the ACTIVE project's instance, then {LV.Library} Save
to libraryPath - lists it in the project at target level, and optionally sets its
description and adds existing VIs, classes or controls to it in the same call.
A library that already exists is refused, never overwritten. The project is opened
(projectPath, required) and made active first; it is CLOSED before the .lvproj entry is
written, because LabVIEW's close saves its own copy over the file.
`description` goes through lvai_set_class_description (CP1252 only - checked before
anything is created). `itemsJson` goes through lvai_add_to_library, which relinks callers
through NI's AddItem; a member that was listed in the project on its own is then taken
out of the target level (`looseEntriesRemoved`), since it belongs through the library now.
VERIFIED FROM THE FILES: the .lvlib exists and is a <Library>, the project lists it, and
the description and members read back from the saved .lvlib.
The icon is a separate call: lvai_set_owner_icon, with the project closed.
```

- `libraryPath`: Absolute path of the new .lvlib, e.g. C:\p\Utils\Utils.lvlib. Its folder is created if missing
- `projectPath`: The .lvproj the library belongs to - opened and made active, and given the entry
- `description`: Optional description of the library, Windows-1252 characters only
- `itemsJson`: Optional JSON array of EXISTING files to put into the library, exactly as lvai_add_to_library takes them: [{"path":"C:\\p\\Utils\\A.vi"}, ...]
- `timeoutSeconds`: Local budget in seconds, per LabVIEW step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_create_message_class`

```text
MUTATING: creates a complete Actor Framework MESSAGE class for one public method of an
actor - the class, its private data (one field per parameter of the method), the `Do.vi`
override that calls the method, and the `Send <Method>.vi` that enqueues it.
IT AUTHORS NO DIAGRAM. A generated override of Message.lvclass:Do.vi is eBad whatever is on
it; this drives NI's own Message Maker instead, which clones a template whose Do.vi is
already a real override. Measured 2026-09-17 on Counter.lvclass:Increment.vi: Do.vi at
execState 1 calling Counter.lvclass:Increment.vi with Amount wired from the message's own
private data, and Send Increment.vi at execState 1 with the pane
`Message Enqueuer` (11, required), `Amount` (10), `Message Priority (Normal)` (7),
`error in (no error)` (8) -> `error out` (0), `Message Enqueuer out` (3) - byte for byte
NI's own Send VI shape.
NEEDS A PROJECT OPEN AND ACTIVE: every step reaches LabVIEW through Project:Active Project
and answers Error 1055 without one. Pass projectPath and this call opens it.
THE CLASS IS SAVED AT THE END, and that is not a detail. The Message Maker's changes to the
CLASS - its private data control and the renamed Send member - live in memory, so a run that
saves only the two VIs reports 0 at every stage, gives two VIs at execState 1, and leaves
BOTH eBad after the next project close, with the class on disk still listing
`Send Template.vi` and no payload field. Measured, then fixed with LVClass.Open + Save.
VERIFIED FROM THE SAVED FILE: the answer's `verify` block is read back with pylabview, not
from the run, so `fields` and `members` are what is on disk.
THE ACTOR MUST DESCEND FROM Actor.lvclass - checked by walking the class file's own parent
links, which is also how the LabVIEW installation is located, so the template and the
message parent always come from the same install as the actor.
PUT THE ACTOR IN ITS LIBRARY FIRST. An actor added to a .lvlib after its messages exist
leaves every Do.vi eBad (measured 2026-09-18) with every file-level check green; the
repair is rebuilding the messages. Order: class, methods, library, messages.
```

- `actorClassPath`: Absolute path to the actor's .lvclass - it must descend from Actor.lvclass
- `actorMethodName`: The actor method the message should call, e.g. "Increment" or "Increment.vi". It must be a PUBLIC member of the actor class (refused as `methodNotPublic` otherwise, before anything is written) and live beside it on disk. Its non-class, non-error inputs become the message's payload, one private data field each.
- `className`: Name for the new message class. Defaults to "<method> Msg", which is NI's own convention - `Increment` gives `Increment Msg`.
- `directory`: Folder to create the message class in. Defaults to `<method> Msg` in the folder that holds the actor's own folder, i.e. a sibling of it. The class lands directly in this folder, not in a subfolder of it, so give EVERY message its own folder: two message classes in one folder answer Error 1055 at replaceActorMethod. A layout that keeps an actor's messages together is `<actor folder>\..\<Actor> Messages\<Method> Msg`, which earlier builds passed explicitly and the AF guide describes as NI's wizard's own.
- `projectPath`: The .lvproj to open first. The Message Maker reaches LabVIEW through Project:Active Project, so without an active project every stage answers Error 1055. Omit only when you have opened one yourself.
- `templatePath`: The message class template to clone. Defaults to NI's `Message Template.lvclass` in the actor's own LabVIEW installation. DO NOT pass `Concrete Message Template.lvclass`: it ships no `Send Template.vi`, so the Send step answers Error 7 naming a file that was never copied, and it needs a different builder chain entirely.
- `parentClassPath`: Parent of the new class. Defaults to Actor Framework's Message.lvclass
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to the scripts folder's copy
- `regenerateHelper`: Regenerate the helper VI even when it exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_create_override`

```text
MUTATING: creates the override of ONE parent method in a class through NI's own provider
CLSUIP_CreateOverride - the IDE's "New > VI for Override" - so the new VI carries Call Parent
and its parent's pane. NEVER build an Actor Framework override with lvai_add_class_method or
AIXML: AIXML cannot author Call Parent, and a generated override is eBad whatever its
diagram, so Actor Core would lose its message loop and Stop Core the stop of its children.
parentMethod is an AF lifecycle name - Pre Launch Init, Actor Core, Stop Core, Handle Error,
Handle Last Ack Core - resolved under the LabVIEW installation the actor's own parent links
land in, or the path of a parent class's .vi for a plain class.
CHECKED FROM THE FILES BEFORE LABVIEW: the class exists; its ancestry reaches the parent;
the method is not a member yet and no file is at the new path; the parent method is not
static. The `vi name in memory` the provider's new VI is opened by is derived from the
class file and LIBRARY-QUALIFIED (Lib.lvlib:Cls.lvclass:Actor Core.vi) - a shorter name
answers `open new vi error` and nothing is saved, measured.
NEEDS A PROJECT OPEN AND ACTIVE (Project:Active Project); pass projectPath and this call
opens it. One stage per helper step, so a failure names it (`failedAtStep`).
VERIFIED FROM THE SAVED .lvclass: the member is listed, its scope (protected for an AF
override - NI's own lifecycle methods are protected; a plain override keeps its parent's
scope) and dynamic dispatch (from the class file, or from the VI's pane with pylabview -
no LabVIEW). The answer carries the override's pane for an AF method.
The helper route is measured (2026-09-17, a message class's Do.vi; four AF overrides on a
throwaway actor, 2026-10-09); this tool measured 2026-10-10 - five lifecycle overrides on an
actor in no library, five on one in a .lvlib, one plain override (parent's protected scope
kept), every one state 1 cold. Read the new
override cold with lvai_exec_state after closing the project; then hook bodies into it
with lvai_af_add_hook.
```

- `classPath`: Absolute path to the child class's .lvclass
- `parentMethod`: An AF lifecycle method name ("Actor Core", "Stop Core.vi", ...) for an actor, or the absolute path of the parent class method's .vi for any other class.
- `projectPath`: The .lvproj to open first. The provider reaches LabVIEW through Project:Active Project, so without an active project every stage answers Error 1055. Omit only when the class's project is already open and active.
- `newViPath`: Where to save the new override. Defaults to the class's own folder with the parent's file name, which is NI's convention - an override keeps its parent's file name.
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_create_typedef`

```text
MUTATING: creates a typedef `.ctl` from an AIXML type literal - `uint16{Off,Voltage,Current}`,
`cluster{double.Min,double.Max}`, anything a `<Control type=...>` accepts - through VI Server
alone. NO pylabview writes anything and no flag is patched: a carrier VI holds the control,
LabVIEW moves it onto a new Control VI, sets `Control VI Type` and saves it. Strict typedefs
via `strict`.
NESTED TYPEDEFS: `elementTypedefsJson` makes top-level cluster ELEMENTS instances of typedef
files that already exist - {"Channel Mode":"C:\\...\\Channel Mode.ctl"} - with one
{LV.Control} Replace each, in the IDE's application instance, so a PROJECT IS NEEDED for that
step: pass projectPath and the tool opens it. ALL elements are bound in ONE open and ONE
save - two separate runs were measured leaving a stray type descriptor in the file. Replace
keeps each element's LABEL, measured; `labelAfter` shows it and gates `ok`.
Create the inner typedefs FIRST, with this tool, then the cluster that contains them.
WITH projectPath the .ctl is listed in the project under folderName - together with every
element typedef that lies in the project's own folder tree, so the inner typedefs do not end
up under Dependencies only - and the project is left CLOSED: the listing is a file edit, and
LabVIEW's close would save over it otherwise. A typedef from vi.lib or elsewhere is used,
never listed.
THE VERDICT IS READ FROM THE SAVED FILE: `verifiedFromFile` holds lvai_describe_ctl's reading
(isTypedef, isStrictTypedef, needsLabviewSave) and each element typedef's name must appear
in the saved .ctl. The in-memory `Control VI Type` is reported beside it and is NOT the
verdict: VI Server's enum is one higher than the file flag - 1 plain, 2 typedef, 3 strict -
and writing 1 answers `error 0` for a control that is not a typedef, measured 2026-09-25.
A generated CONSTANT still cannot be authored as a typedef: AIXML has no spelling for one
(13 spellings measured with the .ctl loaded, all refused). A constant wired into a typedef
terminal is bound afterwards - lvai_generate_class_test and lvai_generate_method_test do it
themselves, lvai_bind_typedef_constants for anything else.
```

- `ctlPath`: Absolute path of the .ctl to create
- `type`: The AIXML type literal the typedef wraps, exactly as a <Control type=...> takes it: `uint16{Off,Voltage,Current}`, `cluster{string.Name,int32.Samples}`, `double`.
- `label`: The control's label. Defaults to the file name without .ctl
- `strict`: Save a STRICT typedef instead of a plain one (default: false)
- `elementTypedefsJson`: JSON object: top-level cluster element label -> absolute path of an EXISTING typedef .ctl that element should be an instance of, e.g. {"Channel Mode":"C:\\T\\Channel Mode.ctl","Range":"C:\\T\\Range.ctl"}. Needs a cluster `type` and projectPath. Element labels are matched exactly against `type`.
- `projectPath`: The .lvproj the typedef belongs to. Required with elementTypedefsJson, whose Replace only works in the IDE's application instance. With it the .ctl is also listed under folderName, and the project is left CLOSED.
- `folderName`: Project folder to list the .ctl under (default: "Typedefs")
- `overwrite`: Replace an existing .ctl at ctlPath. Off by default (default: false)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_describe_class`

```text
Read-only, and needs NO running LabVIEW: a `.lvclass` is plain XML on disk, so this parses
it directly.
THE REASON IT EXISTS IS INHERITANCE. `lvai_describe_project` reports a class's `parent` as
its OWNING LIBRARY, not its base class - measured on NI's own hierarchy, where
`Circle Message.lvclass` derives from `Draw Message.lvclass` and reports
`"parent": "Draw Messages.lvlib"`. Nothing in the 23-RPC gRPC interface reports inheritance
or access scope, so the file is the only source and this is the only reader.
It checks BOTH representations, in the order the census established: plain-text
`Parent Libraries` items (LabVIEW 2026 writes these) then the encoded
`NI.LVClass.ParentClassLinkInfo` for older files. No file carried both; a class with neither
derives from `LabVIEW Object`, which is reported as such rather than as an empty answer.
`ancestorSource` says which representation the answer came from, because the encoded one is
recovered by regex over decoded bytes and its ORDER is not guaranteed.
Members come back with their effective scope from `NI.ClassItem.MethodScope` - per member,
no propagation, unlike a `.lvlib` where the scope sits on the folder. Folder names are
ignored on purpose: the census found folders called `private` whose members were all public.
`fields` REPORTS EACH PRIVATE DATA FIELD with its label, its type descriptor, whether it
is a bound typedef, and its `default` - decoded from the flattened private data, a
cluster as an object of its members, an enum as value and item; a type whose layout is
not decoded (path, refnum, array, timestamp) stops the walk and `fieldsDefaultsNote`
says where. Nothing else reported this: measured 2026-09-03, establishing what types
a class's fields carry by hand cost 154 s of wall clock for 4.2 s inside LabVIEW - reading
the 6-bit codec out of the source, unwrapping the flattened control, and extracting it. It
is also the check that distinguishes a successful typedef bind from one that installed the
type and no link. Costs one pylabview read, no LabVIEW; `includeFields: false` skips it.
`privateDataBytes` is the size of the control inside the class file. `-1` means the
flattened property is there but does not decode, which is what a corrupt or wrongly wrapped
blob looks like - and the state in which LabVIEW reports the class with every field blank.
```

- `lvclassPath`: Absolute path to the .lvclass to read
- `includeFields`: Also report each private data FIELD with its label and type descriptor. On by default. It costs one pylabview read of the class's own private data control - no LabVIEW, about 300 ms - and it is the only way to see what a field actually carries: `lvai_describe_ctl` on the unwrapped control stops one level up at `Cluster of class private data`, and nothing else reports it at all. Pass false to skip it on a class you only want the member list for. (default: true)
- `readMemberFiles`: Read each member VI's own saved file with pylabview (no LabVIEW, about 250 ms per VI, four at a time) for two things. `dynamicDispatch` where the .lvclass records no IsStaticMethod: a connector pane terminal flagged 0x8000 means dynamic dispatch, measured against NI's own IsStaticMethod on 149 of 149 class members, and `dynamicDispatchFrom` says which source answered. And `paneTypedefs`: every pane terminal that carries a typedef, with the .ctl it names - so an accessor still bound to its field's typedef reads [{"terminal":"Channel Config","typedef":"Channel Config.ctl"}]. Pass false to skip the file reads. On by default. (default: true)
- `timeoutSeconds`: Local budget in seconds (default: 45)

## `lvai_describe_ctl`

```text
READ-ONLY and NEEDS NO RUNNING LABVIEW: what kind of control a `.ctl` is, what type it
wraps, and whether it can be bound to as a typedef.
ASK THIS BEFORE BINDING ANYTHING. A `.ctl` that is not a typedef binds with `error out = 0`
and produces NO typedef link - measured 2026-09-02 on two of NI's own controls, where both
Replace calls succeeded and neither bound, because `DAQmx Task Name NI_Silver.ctl` and
`errclust.llb\Error Cluster.ctl` are ordinary controls (`TypeDefVI="0"`). The success and
the failure look identical from the calling side; only the source tells them apart.
`controlVIType` is LabVIEW's own enum, reconstructed from the saved file rather than from a
running IDE: 0 not a typedef, 1 typedef, 2 strict typedef, 3 class private data.
`{LV.VI} Control VI Type` in VI Server is ONE HIGHER - 1 plain control, 2 typedef, 3 strict -
measured 2026-09-25: `scripts/lvctl_kind.xml` read 1 for a file this tool reads 0, and a
control saved with 1 written was not a typedef. This text claimed the two agreed until then.
`bindable` is the verdict to act on, and `whyNotBindable` names the reason in one sentence.
`wrappedType` is what the control actually carries - the type a binding would install, and
the type a generated constant must be authored as. Each entry of `fields` says its `kind`:
`enum` with its `items` (pylabview's `UnitUInt*` types), or `numeric` - which is what a RING is
too, because a ring keeps its labels on the panel rather than in the type. A field that is a
typedef instance names it in `typedef` and describes the type inside it.
AND READ `needsLabviewSave`. A .ctl produced by the fixture route - generate a VI to a .ctl
path, then patch the flags in the pylabview bundle - has never been written by LabVIEW and
still carries that VI's CONNECTOR PANE, which shows up here as `wrappedType: "Function"`
with 16 fields instead of `"TypeDef"` with one. It binds, it flattens, it validates; NI's
accessor wizard then refuses the field it is bound to with Error 1061 in the middle of a
class build. lvai_resave_ctl is the one-call repair.
A `.vi` is accepted too and reports `isControl: false`, because pointing this at the wrong
file is a likelier mistake than wanting it.
```

- `ctlPath`: Absolute path to the .ctl (a .vi is accepted and reported as not a control)
- `keepBundle`: Keep the pylabview bundle instead of deleting it. The bundle is an implementation detail of this call; pass true only when the raw XML is wanted for something else. (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 45)

## `lvai_describe_project`

```text
RPC GetDescribeProjectPromptInfo (server streaming). Returns a JSON description of a
.lvproj - its items, hierarchy and targets. Use this before describing individual VIs
to find out what a project actually contains.
```

- `projectPath`: Absolute path to the .lvproj file
- `projectName`: Optional project name; usually leave empty
- `maxMessages`: Max stream messages to collect (default: 10)
- `timeoutSeconds`: Local budget in seconds (default: 120)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview the answer is read from the .lvproj and the .lvlib files it lists, in NI's shape: targets with their libraries (every item with its effective scope), classes, VIs, other files, missing files and build specifications. A target's address is not in the file and stays empty (`warnings` says so). No running LabVIEW is needed.

## `lvai_describe_vi`

```text
RPC GetDescribeVIPromptInfo (server streaming). Returns a JSON description of a VI,
including its AIXML representation under 'viXml' and, with getNodesInfo, the block
diagram nodes.
TO READ CODE, PREFER lvai_convert_vi_to_aixml: it carries the same diagram for a fraction
of the context, MEASURED on one VI in issue #19 at 17 532 bytes against 62 091 characters
here, and it is cached on disk for installation VIs where this call never is. Use this tool
for what it adds instead - the description, the controls and indicators, the subVI list and
the owning project - or cap the payload with maxContentChars.
The VI does not need to be open. FIRST TOUCH CAN BE SLOW, and slow here means minutes: a
class-member VI drags its whole hierarchy in, and it exceeded the 120 s default in issue #19.
Raise timeoutSeconds rather than concluding the VI or the server is broken.
```

- `viPath`: Absolute path to the .vi file, e.g. C:\path\To\My.vi
- `viName`: Optional VI name; usually leave empty and let the path speak
- `getNodesInfo`: Include block-diagram node information (bigger payload) (default: true)
- `maxMessages`: Max stream messages to collect (default: 10)
- `timeoutSeconds`: Local budget in seconds (default: 120)
- `maxContentChars`: Truncate each message's infoJson to this many characters (0 = unlimited) (default: 0)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview the answer is built from the FILE by our reader, in the same shape and with NI's keys: viXml is the reader's AIXML, controlsIndicators and subvisInfo are read from it, owningProjectPath is the nearest .lvproj listing the VI itself; viImage stays empty (`warnings` says so). No running LabVIEW is needed.

## `lvai_discuss_file`

```text
MUTATING: launches NI's Nigel chat application if it is not already running and attaches
the given VI or project to it - the same two stages as the IDE's "Discuss with Nigel"
command, driven over VI Server. The chat then shows the file with its diagram, description
and terminal table.
IT REPORTS A REAL VERDICT, which is why it drives NI's two packed-library VIs rather than
the menu callback that wraps them: the callback returns nothing at all, discarding
InvokeDiscussVI's own error code. `discussErrorCode` and `discussErrorMessage` are that
discarded verdict, and `ok` is gated on them.
This is the EMITTING side of the Discuss feature. Intercepting it is a different question
and remains impossible: lvai_monitor_discuss_vi cannot receive the event, because the
monitors are single-subscriber streams and NI's own service always wins.
IT DOES NOT UNLOCK lvai_apply_aixml_to_vi. Measured: Discuss fired on a VI, Apply on that
same VI 30 s later, still Error 42. The gate is on the caller, not on the VI.
The chat also validates the file ITSELF, in its window - handed a path that is not a VI it
shows "The file you selected is not a VI." - so the extension is checked here, before the
call.
```

- `targetPath`: Absolute path of the .vi or .lvproj to discuss
- `targetName`: The name Nigel shows for the file. Defaults to the file name, which is what NI's own menu callback passes.
- `fileType`: 'VI' or 'PROJECT', case-insensitive. Defaults to VI, which is what the IDE's own Discuss command hardcodes. It must agree with the file's extension. (default: "VI")
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory. Generated once and reused - but regenerated automatically when the shipped AIXML is newer, so an upgrade cannot leave a stale helper behind.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvai_discuss.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists and is current (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)

## `lvai_dqmh_add_helper_loop`

```text
MUTATING, DQMH 7.x ONLY: adds a helper loop to a DQMH module's Main.vi in the ACTIVE
project, with NO dialog - Delacor's own Script New Helper Loop.vi through a generated
wrapper, the result of Tools > DQMH > Module > Add Helper Loop to DQMH Module. The loop gets
its own event structure, so a PRIVATE request can name it as its consuming loop
(lvai_dqmh_new_event requestScoping + consumingLoop). The name is checked first against the
loops Main.vi already has, which Delacor's dialog refuses with a MODAL. Refused while any
module has unsaved changes or is locked. On DQMH 5/6 it answers `needsDqmh7`.
```

- `moduleName`: DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `helperLoopName`: Name of the new helper loop, e.g. 'Acquisition Loop'
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_convert_event`

```text
MUTATING: converts a plain DQMH Request into a Request and Wait for Reply - or, on DQMH
7.x, into a Round Trip (`toEventType`, with `roundTripBroadcastName` for the broadcast half
it creates) - in the ACTIVE project, with NO dialog - Delacor's own Convert Event.vi
through a generated wrapper. DQMH 5/6 offer only the first conversion, and a Round Trip is
refused there by name. `eventNotFound` lists the plain requests the module has. Delacor's converter raises a MODAL when it meets a non-standard wiring in
Main.vi's event loop; a module Delacor scripted does not, and a run that does not return is
answered as `scriptingTimedOut` naming the windows on screen. Refused while any module has
unsaved changes or is locked. Delacor KEEPS the old message frame, labelled
#Code_Review_Todo, beside the new one of the same name - measured - and that frame is for a
person to delete; `completionNeeded` says so.
```

- `moduleName`: DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `eventName`: Plain Request to convert, e.g. 'Set Speed'
- `toEventType`: 'Request and Wait for Reply' (default, every DQMH version) or 'Round Trip' (DQMH 7.x only) (default: "Request and Wait for Reply")
- `roundTripBroadcastName`: Round Trip only: the name of the broadcast half the conversion creates (default: empty)
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_create_module_template`

```text
MUTATING, and it writes into the user's LabVIEW Data folder: turns an existing DQMH module
into a module TEMPLATE, which Add New DQMH Module then offers as a module type - the result
of Tools > DQMH > Module > Create DQMH Module Template with NO dialog (Delacor's own VI ends
in a modal even on success, so this repeats its two steps itself).
With copyToLabviewData (default) the module's folder is COPIED to
`<LabVIEW Data>\DQMH Module Templates\Source\<relativeLocation>\<Name>` and the metadata
refers to that copy; without it nothing is copied and the metadata refers to the module in
place with absolute paths, as Delacor's own option does. Either way a metadata file
`<Name>.xml` is written into `...\MetaData`. An existing template of that name is refused
unless overwrite is true - Delacor's own tool silently wipes it. The answer confirms the new
type appears in Delacor's module-type catalogue and gives its index.
```

- `libraryPath`: Absolute path of the module's .lvlib
- `description`: Description shown for the new module type in Add New DQMH Module
- `relativeLocation`: Sub-folder under the template Source folder, e.g. 'MyCompany'; may be empty (default: empty)
- `copyToLabviewData`: Copy the module into LabVIEW Data (Delacor's default); false refers to it in place (default: true)
- `overwrite`: Replace an existing template of the same name (default: false)
- `testerVirtualFolder`: DQMH 7.x only: the project virtual folder a module made from this template puts its tester in; empty is Delacor's default, 'Testers' (default: empty)
- `libraryVirtualFolder`: DQMH 7.x only: the project virtual folder for the module library; empty is Delacor's default, 'Modules' (default: empty)
- `relativeSavePath`: DQMH 7.x only: where a module made from this template is saved, relative to its project; empty or absolute is Delacor's default, 'Libraries' (default: empty)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_create_rt_tester`

```text
MUTATING: creates the Real-Time API tester (`Test <Module> API-RT.vi`) of a DQMH module in
the ACTIVE project with NO dialog - Delacor's own Scripter - Create RT Tester.vi through a
generated wrapper. Delacor offers it for Singleton and Cloneable modules alike (measured:
its menu VI asks Parse for Both). Its scripter answers two cases with a MODAL - an RT tester
that already exists, and a standard tester it cannot find - so the dry run asks Delacor's
own finders first and refuses both by name. THE NEW RT TESTER IS NOT EXECUTABLE, by
Delacor's design: each request frame carries #CodeNeeded for its required inputs. Refused
while any module has unsaved changes or is locked.
```

- `moduleName`: Cloneable DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_duplicate_event`

```text
MUTATING, DQMH 7.x ONLY: copies ONE event of a DQMH module in the ACTIVE project under a new
name - into the same module or into another one (destinationModuleName) - with NO dialog:
Delacor's own Duplicate Event.vi through a generated wrapper, the result of Tools > DQMH >
Event > Duplicate DQMH Event. Arguments, reply payload and type are copied; the description
too, unless `description` replaces it. A Round Trip is copied as a pair and needs
roundTripBroadcastName for the new broadcast half. The names are checked first the way
Delacor's dialog checks them with a MODAL. `events` in a refusal lists what the source module
offers, as Delacor's ring shows it. What Delacor leaves for a person - a Broadcast's loose
#CodeNeeded call, a reply frame's Merge Errors, a tester case - is the same as for a new event
and named in `completionNeeded`. Refused while anything in the project is unsaved or a module
is locked. On DQMH 5/6 it answers `needsDqmh7`.
```

- `moduleName`: Source DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `eventName`: Event to copy, e.g. 'Set Speed'
- `newEventName`: Name of the copy
- `destinationModuleName`: Module to copy into; empty means the source module (default: empty)
- `roundTripBroadcastName`: Round Trip only, and required for it: the name of the new broadcast half (default: empty)
- `description`: Description of the copy; empty copies the source event's own (default: empty)
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_duplicate_module`

```text
MUTATING, DQMH 7.x ONLY: copies a DQMH module of the ACTIVE project into a NEW module under a
new name, with NO dialog - Delacor's own Scripter - Duplicate Module.vi through a generated
wrapper, the result of Tools > DQMH > Module > Duplicate DQMH Module. Every event, the API
tester and Main.vi are copied and renamed; the RT tester is not (Delacor's own scope,
measured). The new module goes into `<saveFolder>\<newModuleName>` - saveFolder is the
PARENT folder, measured - and the project lists it. The new name is checked first the way
Delacor's dialog checks it with a MODAL: not blank, a valid file name, no library of that
name in the project, no non-empty folder of that name. A source module whose Main.vi still
calls a VI that no longer exists makes LabVIEW open a file dialog while it copies - remove
such leftovers first. Refused while any module has unsaved changes or is locked. On DQMH
5/6 it answers `needsDqmh7`.
```

- `moduleName`: DQMH module to copy, e.g. 'Heater' or 'Heater.lvlib'
- `newModuleName`: Name of the new module, without .lvlib
- `saveFolder`: PARENT folder of the new module's folder, relative to the project folder or absolute; empty means the folder that holds the source module's folder (default: empty)
- `description`: Description of the new module's library (default: empty)
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_new_event`

```text
MUTATING: creates a DQMH event - Request, Broadcast, Request and Wait for Reply or Round
Trip - with typed arguments on a module in the ACTIVE project.

WITH NO DIALOG AND NO KEYSTROKE, so it is safe unattended: a generated wrapper
calls Delacor's own Parse Project for DQMH Modules.vi and Script New Event.vi as loaded
subVIs of ONE caller, which keeps Module Info's refnums alive (docs/dqmh-scripting.md
section 9). A dry run first finds the module and its folder, and the checks Delacor's
dialog would answer with a MODAL - blank or invalid names, reserved or duplicate argument
labels, an event name already used as a file or as a Main.vi case - are made here and
refused by name. The answer lists the files written with each VI's exec state and
confirms Main.vi is still executable. Names may be bare or as Delacor spells them.

BOTH DQMH GENERATIONS: DQMH 5/6 (scripting under project\Delacor\DQMH) and DQMH 7.x
(shared scripting VIs moved to vi.lib, Script New Event.vi taking one Event Info cluster)
are read off the installation and driven by their own wrapper; `dqmhGeneration` in the
answer says which ran. On 7.x a Broadcast also leaves the API tester NOT executable until
its #DQMH_CodeNeeded status string is wired - `completionNeeded` says so.

A project must be OPEN AND ACTIVE.
```

- `moduleName`: Module to add the event to, e.g. 'Heater' or 'Heater.lvlib'. Matched against the project's DQMH modules, case-insensitively.
- `eventName`: Name of the new event, e.g. 'Do Something Else'
- `argumentsJson`: Arguments as JSON: [{"name":"Channel","type":"string"},{"name":"Gain","type":"double"}]. The names and types become the event's Argument--cluster.ctl fields, so they are the event's public contract. Pass [] for an event with no arguments. Types are AIXML type names - string, double, int32, uint32, bool, path and so on.
- `eventType`: Event type: Request, Broadcast, Request and Wait for Reply, or Round Trip (default: "Request")
- `replyArgumentsJson`: Reply data, same JSON shape as argumentsJson, for the two types that carry a reply - Request and Wait for Reply, and Round Trip. These become the REPLY cluster, a second Argument--cluster.ctl, and they are a different set from argumentsJson: those are what the caller sends, these are what the module sends back. Leave empty for a reply that carries only the error cluster. Refused on Request and Broadcast, which have no reply. (default: empty)
- `roundTripBroadcastName`: Round Trip only, and required for it: the name of the BROADCAST half of the pair. A Round Trip is a request plus the broadcast that answers it, so it needs two names - eventName is the request. Refused on the other three types. (default: empty)
- `description`: Event description. Delacor writes it into the message-handling frame's label, where it documents the event on the module's block diagram. (default: empty)
- `addTesterButton`: Add a button to the module's API tester that fires this event. FALSE STILL ADDS AN EVENT CASE to the tester - Delacor's own behaviour - with no button and nothing wired, which leaves the tester NOT executable until that case is assigned or deleted (issue #112); keep true unless that is what you want (default: true)
- `requestScoping`: DQMH 7.x only, Request and Request and Wait for Reply only: 'Public Request' (default), 'Private Request' or 'Local Instance'. A private or local-instance request is consumed by the loop named in consumingLoop (default: "Public Request")
- `consumingLoop`: DQMH 7.x only: the loop of Main.vi that consumes a PRIVATE or LOCAL INSTANCE request - 'Event Handling Loop' (default) or a helper loop made with lvai_dqmh_add_helper_loop (default: "Event Handling Loop")
- `timeoutSeconds`: Local budget in seconds; the scripting itself takes tens of seconds (default: 600)
- `answerWithinSeconds`: Answer within this many seconds; past it the event is still scripted in the server and the SAME call again collects the answer (the client gives up at 60 s) (default: 45)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_new_module`

```text
MUTATING: creates a DQMH module in the ACTIVE project with NO dialog - Delacor's own
Script New Module.vi through a generated wrapper, the result of Tools > DQMH > Module > Add
New DQMH Module. About fifty files are written into `<project folder>\Libraries\<Name>`
(Delacor's layout; `saveFolder` overrides it). DQMH 5/6 list the library under a
`<Name> Module` folder with the API tester at target level; DQMH 7.x under `Modules` and
the tester under `Testers`, or the folders given in `libraryVirtualFolder` and
`testerVirtualFolder`. BOTH DQMH GENERATIONS ARE SUPPORTED: the scripting layout is read
off the installation and `dqmhGeneration` says which ran.
THE MODULE TYPE IS A NAME from the station's catalogue - Singleton, Cloneable, and whatever
add-ons or templates (lvai_dqmh_create_module_template) contribute; `catalogue` in every
answer lists them, and a name that is not there is refused. Refused too while anything in
the project is unsaved, or - Delacor's own checks - when the target folder already holds a
LabVIEW file at its top level or the project a library of that name. The answer verifies the module from the files: library, Main.vi,
tester, Do Something present or absent as asked, and both VIs' exec state.
```

- `moduleName`: Module name, e.g. 'Heater' - becomes Heater.lvlib
- `moduleType`: Module type by NAME from the station's catalogue, e.g. 'Singleton' or 'Cloneable' (default: "Singleton")
- `includeDoSomething`: Keep Delacor's Do Something example events (default: true)
- `saveFolder`: Folder for the module's files, relative to the project folder or absolute; empty means <project folder>\Libraries\<Name> (default: empty)
- `moduleDescription`: DQMH 7.x only: the module library's description (default: empty)
- `testerVirtualFolder`: DQMH 7.x only: project virtual folder for the API tester; empty is Delacor's default, 'Testers' (measured) (default: empty)
- `libraryVirtualFolder`: DQMH 7.x only: project virtual folder for the module library; empty is Delacor's default, 'Modules' (measured) (default: empty)
- `timeoutSeconds`: Local budget in seconds; Delacor takes 20-45 s to script a module (default: 600)
- `answerWithinSeconds`: Answer within this many seconds; past it the module is still scripted in the server and the SAME call again collects the answer (the client gives up at 60 s) (default: 45)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_new_unit_test`

```text
MUTATING: creates Delacor's unit test for ONE request event of a DQMH module in the ACTIVE
project - the result of Tools > DQMH > Testing Tools > New DQMH Unit Test, with NO dialog
and NO keystroke, so it is safe unattended. Writes three VIs under
`<project folder>\Unit Tests\<Module>\` - `Test - <Module> - <Event> <n>.vi`,
`<Module> setup.vi`, `<Module> teardown.vi` - and Delacor saves the .lvproj itself with a
`Unit Tests` folder listing them.

THE TEST VI IS NOT EXECUTABLE WHEN IT IS CREATED, and that is Delacor's design, not a
failure: its event frames carry `#CodeNeeded` notes asking for the module's broadcasts to be
configured, and Delacor's own template is broken the same way (measured). The answer
reports each created VI's exec state so this is visible rather than discovered.

ONLY REQUEST EVENTS get a unit test - Delacor's own filter. A name not found comes back as
`eventNotFound` with the request events the module has.

Names may be given bare (`Heater`, `Do Something`) or as Delacor spells them
(`Heater.lvlib`, `Do Something.vi`); case does not matter. Requires a project OPEN AND
ACTIVE and no unsaved changes in any module (Delacor refuses those, and so does this).

How: opens four of Delacor's scripting VIs (unmodified, left open) so the generated wrapper
`scripts/lvdqmh_new_unit_test.xml` can call them as static subVIs, then runs it.
```

- `moduleName`: DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `eventName`: Request event to test, e.g. 'Do Something' or 'Do Something.vi'
- `timeoutSeconds`: Local budget in seconds; the scripting itself takes about a second (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_place_handler`

```text
MUTATING: fills ONE message frame of a DQMH module's Main.vi - the frame Delacor scripts
with a #CodeNeeded label - by dropping a HANDLER subVI into it and wiring it BY NAME:
each request argument (the frame's Unbundle By Name) into the handler input of the same
name, Variant To Data's error out into the handler's `error in`, and for a Request and
Wait for Reply each handler output into the reply field of the same name plus its
`error out` into `<Event>_error` AND into the frame's Merge Errors, whose second input
Delacor leaves unwired - that unwired input is what keeps Main.vi from running after a
Request and Wait for Reply event. In a plain Request frame, which has no reply, the
handler goes IN SERIES into the frame's error wire instead (error source -> handler ->
whatever that wire fed), so its error out reaches the frame's error chain.
THE HANDLER IS PLACED VISIBLY: right of the argument source and inside the frame's case
structure, and the answer reports `placement` read back from LabVIEW. `ok` is false when
the handler sits OUTSIDE the frame - a DQMH frame does not grow, so a node outside it is
cut off out of sight while Main.vi still runs (the user's finding of 2026-10-31: until
then the handler was dropped at the argument source's pane coordinates, which a frame
reads relative to its own origin) - or when its error out reached nothing.
The #CodeNeeded text (#DQMH_CodeNeeded in DQMH 7) is deleted and Main.vi saved.
So name the handler's terminals after the event's argument and reply fields - that is
the whole contract. A dry run first reads the frame; a handler input or reply field that
finds no partner is reported, never guessed, and a frame that already holds a subVI is
refused. Needs the module's project OPEN AND ACTIVE. The answer gives Main.vi's exec
state before and after.
```

- `moduleName`: Module, e.g. 'Bank' or 'Bank.lvlib'
- `eventName`: Event whose message frame gets the handler, e.g. 'Verify Account'
- `handlerViPath`: Absolute path of the handler subVI
- `dryRun`: Report what would be wired and change nothing (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_reference`

```text
How a DQMH (Delacor Queued Message Handler) module is built, as it appears in AIXML:
the fixed set of module VIs, the two-loop Main.vi, the typed request/broadcast event
clusters, and the naming conventions. Read this before analysing or discussing a
project that uses DQMH - the two loops sit inside a case frame rather than at the
diagram root, so a naive tree walk finds nothing.
Note it also records what CANNOT be generated: every DQMH framework call is a
project-local subVI, which AIXML generation rejects.
Without arguments: a section list. With section: that section, or 'all'. A section value
that is not a heading is looked up as a TERM instead - and it may be a comma-separated
list, which costs one round trip and prints each passage once.
```

- `section`: Section number or title fragment; 'all' for everything; omit for the section list. A value that matches no heading is looked up as a term, and may be a comma-separated list of them

## `lvai_dqmh_remove_do_something`

```text
MUTATING: removes the "Do Something" example events from a DQMH module in the ACTIVE
project with NO dialog - Delacor's own Remove Do Something.vi, the step Add New DQMH
Module runs when its Do Something box is cleared, through a generated wrapper. It rescripts
Main.vi and the API tester, which stay executable (measured). A module that has no Do
Something event left is refused from the dry run - Delacor's VI answers Error 1055 for it.
Refused while any module has unsaved changes or is locked.
```

- `moduleName`: DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_remove_event`

```text
MUTATING: removes one event from a DQMH module in the ACTIVE project with NO dialog -
Delacor's own Remove Event.vi, driven through a generated wrapper. Removing the REQUEST of
a Round Trip removes its broadcast half too, exactly as Delacor's dialog does; the answer
names it as `dependentBroadcast`. DELACOR LEAVES CLEAN-UP FOR A PERSON, measured
2026-10-06: Main.vi keeps a loose call to a removed Broadcast and a Round Trip's message
frame with a #Code_Review_Todo label, and the API tester keeps the removed event's frame -
so both are typically NOT executable afterwards (Delacor's dialog opens the Error List).
`completionNeeded` says what to clean up. Names may be bare or as Delacor spells them (`Heater.lvlib`,
`Do Something.vi`). Refused while any module has unsaved changes or is locked - Delacor's
dialog refuses the same. Unit tests of the removed event are NOT removed; the answer lists
files that were deleted.
```

- `moduleName`: DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `eventName`: Event to remove, e.g. 'Do Something' or 'Do Something.vi'
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_rename_event`

```text
MUTATING: renames one event of a DQMH module in the ACTIVE project with NO dialog -
Delacor's own Rename Event.vi through a generated wrapper. The new name is checked first
the way Delacor's dialog would check it with a MODAL: not blank, a valid file name, no
`<new>.vi` already in the module folder, no event and no Main.vi case of that name.
Refused while any module has unsaved changes or is locked. Delacor does not
save the module's .lvlib after a rename - measured: the file on disk still listed the old
VIs - so this call saves it, and says so in `librarySaved`.
```

- `moduleName`: DQMH module, e.g. 'Heater' or 'Heater.lvlib'
- `eventName`: Event to rename, e.g. 'Do Something' or 'Do Something.vi'
- `newEventName`: New event name, without .vi
- `description`: DQMH 7.x only: the event's new description. Empty keeps the event VI's own description, as Delacor's dialog pre-fills it (default: empty)
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_rename_module`

```text
MUTATING: renames a DQMH module in the ACTIVE project with NO dialog - Delacor's own
Scripter - Rename Module.vi through a generated wrapper. The new name is checked first the
way Delacor's dialog checks it with a MODAL: not blank, a valid file name, and no library of
that name already in the project. Refused while any module has unsaved changes or is
locked. The answer reads the module back under its new name. DELACOR SAVES THE WHOLE
PROJECT at the end (Save All This Project) - measured: 52 files in other modules re-saved -
so `modifiedFiles` is long; the project's virtual folder keeps the OLD `<Name> Module`
label.
```

- `moduleName`: DQMH module to rename, e.g. 'Heater' or 'Heater.lvlib'
- `newModuleName`: New module name, without .lvlib
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_dqmh_validate_module`

```text
READ-ONLY: runs Delacor's own `Validate DQMH Module (Headless).vi` over a project - the
checks of Tools > DQMH > Module > Validate DQMH Module, with no dialog (its error handlers
are wired to "no dialog"). Returns Delacor's `Validation Results` text as it reports it.
Nothing is fixed: Delacor's fixers belong to its interactive validator.
```

- `projectPath`: The .lvproj to validate; empty means the ACTIVE project (default: empty)
- `timeoutSeconds`: Local budget in seconds (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in its main application instance, and Main.vi is read by our own reader. On grpc the route is the one these tools always had. Either way LabVIEW must be running with the project open and active.

## `lvai_drop_palette_item`

```text
RPC DropPaletteItem. MUTATING: places the palette item with the given GUID onto the
block diagram of the target VI. This edits real code. Prefer the AIXML path
(lvai_apply_aixml_to_vi) when you need control over placement and wiring - a drop
gives you neither.
```

- `guid`: Palette item GUID
- `viPath`: Absolute path to the target .vi
- `viName`: VI name, or empty
- `projectPath`: Absolute path to the owning .lvproj, or empty
- `projectName`: Project name, or empty
- `timeoutSeconds`: Local budget in seconds (default: 120)

## `lvai_dump_schema`

```text
Ask the running LabVIEW server for its own schema via gRPC server reflection and
render it. Use this to detect schema drift: the interface is private and NI can
change it between LabVIEW versions, so this is the authoritative list of what the
installed version actually supports - not what this MCP server was built against.
format: 'summary' (services, rpcs, message fields) or 'json' (raw FileDescriptorProto).
```

- `format`: 'summary' (default) or 'json' (default: "summary")
- `outputPath`: Optional: also write the rendered output to this file path

## `lvai_ensure_labview`

```text
Start a LabVIEW instance. If one is already running it is used as-is and nothing is
started. Otherwise the newest installed LabVIEW is launched, preferring the 32-bit build
- that is the one hosting the AI gRPC service. No version number is hardcoded; LabVIEW
NXG is ignored.
MUTATING: may start an IDE, which is visible to whoever is at the machine.
The launch is VERIFIED, not assumed: a LabVIEW created as a child of this server is
killed by the host's job object within about half a second, so the tool tries several
launch routes and keeps only one whose process is still alive two seconds later. The
winner is reported as launchMethod; if none survives you get errorKind
'launch-did-not-survive' listing every attempt, never a hopeful 'starting'.
THE SERVICE COMES UP WITH LabVIEW ITSELF, about 45 s after a cold start, with no Nigel
chat opened - measured 2026-10-17 with NI.NigelLocalService running. This text said for
weeks that the 'LV AI gRPC Service' starts with NIGEL and not with the IDE; that is
refuted. What still stands is the earlier observation that a LabVIEW up for twenty
minutes once held 30 listener ports and served lvai.LVAI on none of them - so a
'starting' that persists for more than about two minutes, with LabVIEW visibly
running, is not this tool being slow: then ask whoever is at the machine to open
Nigel, and call lvai_status once.
Otherwise, when LabVIEW is still coming up, calling again is right - the second call
finds the instance and waits. For an unattended long wait use the CLI:
LabVIEWMCP --ensure-labview.
```

- `waitSeconds`: How long to wait for the gRPC service to answer, in seconds (capped at 45) (default: 40)
- `keepAutoSave`: Do NOT clear Documents\\LabVIEW Data\\LVAutoSave before starting LabVIEW. It is cleared by default because leftover recovery data makes LabVIEW raise a modal recovery dialog on start, and a modal dialog stops this gRPC service until a human dismisses it. Only ever cleared when this tool starts LabVIEW itself. (default: false)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview READY means LabVIEW answers over its ActiveX server - what that backend uses - not that NI's gRPC service answers: a LabVIEW older than 2026 has no such service, and on pylabview nothing waits for Nigel.

## `lvai_error_list`

```text
READ-ONLY: what LabVIEW's Error List window shows - every BROKEN VI in memory, each with the
Error List's own lines (`Add: Contains unwired or bad terminal`, `Wire: Wire has loose ends.`)
and the explanation the dialog prints under them. lvai_exec_state says THAT a VI is broken;
this says WHY, which nothing else here does for a loose wire, an unwired required input or a
type conflict.
TWO APPLICATION INSTANCES ARE READ: the IDE's main one, and the ACTIVE PROJECT's, because a
VI opened inside a project lives there. `instances` reports each; the project one answers
`available: false` when no project is active, which is not a failure.
IT SEES WHAT IS IN MEMORY, NOT WHAT IS ON DISK. A broken VI nobody has open is not listed, and
the in-memory copy is what the IDE shows - an unsaved edit included. To ask about a file on
disk, open it first with lvai_open_file, then call this.
`viName` narrows the answer to one VI (a name or a path; the file name is matched).
NEEDS A RUNNING LabVIEW, not NI's service: the helper is composed by our composer (NI's
converter is the fallback) and run over LabVIEW's ActiveX server, which runs it in the
IDE's main instance - through NI's service it would run in the addon's own instance and see
nothing, measured. Warnings are not reported: the methods return errors only.
lvai_exec_state names the same lines for ONE VI read from disk, which every tool that
checks executability now forwards as `errors`.
```

- `viName`: Only this VI: a VI name or a path - its file name is matched, case-insensitively
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 120)

## `lvai_example_index`

```text
The shipping examples of this LabVIEW installation, with NI's own description and search
keywords, read from the example VIs themselves. Query this BEFORE designing a diagram from
scratch: where lvai_palette_index answers "which VI may I Call", this answers "has NI
already wired this up" - a whole working diagram rather than a single node. Feed a .vi hit's
path to lvai_convert_vi_to_aixml to read how it is built; a hit may also be a .lvproj, a
whole example application, and for that the follow-up is lvai_describe_project instead.
Matches on name, category, keywords and description; EVERY word of the query must appear,
though each may come from a different field. Without a query: the totals plus the
scan location.
Needs NO running LabVIEW - the metadata is plain text inside each .vi.
CACHED ON DISK and reused by every later session. Scanning is a read of 2510 files and
costs about a minute the first time on a machine; after that it is read back in under a
second, and the server warms it at start-up so the first call does not pay even that. The
cache never expires on its own - pass refresh after installing or upgrading LabVIEW or an
add-on, and only then.
Both roots are read: <LabVIEW>\examples AND every LVAddon under %ProgramFiles%\NI\LVAddons,
which is where driver and toolkit examples now install.
Examples that carry no in-VI metadata are covered too: some drivers - NI-DAQmx above all -
register through an external binary index (exbins\*.bin3 and *.bin4), and those are read as
well. An index file that does not fit the known format is skipped whole and named in the
output, so any remaining gap stays visible rather than silent.
A VI absent here may still have a description: lvai_filter_example_search_candidates reads
any VI's own description property, including vi.lib.
BY DEFAULT ONLY PLAIN-LabVIEW EXAMPLES ARE LISTED. An example needing LabVIEW FPGA,
LabVIEW Real-Time or a licensed toolkit is worse than no hit - it reads as the answer, and
the VI built from it will not open where that module is missing. NI-DAQmx is treated as
always installed and stays in. The count held back is always reported; pass
includeSpecialised to see them, each labelled with what it needs.
```

- `query`: Words to look for, e.g. 'TDMS', 'state machine', 'read binary file'. EVERY word must appear in the entry, but each may come from a different field - name, category, keyword or description. Fewer words match more
- `limit`: Max rows to return (default 10, max 60) (default: 10)
- `refresh`: Rebuild the index from disk. Costs about a minute - only after installing or upgrading LabVIEW or an add-on (default: false)
- `includeSpecialised`: Also list examples needing FPGA, Real-Time or a licensed toolkit (default: false)
- `installRoot`: Override the LabVIEW installation folder; omit to use the newest installed
- `addonsRoot`: Override the LVAddons folder; omit to discover it under Program Files

## `lvai_exec_state`

```text
READ-ONLY: reads a VI's {LV.VI} Execution:State and VI Linker Errors through VI Server and
says whether it is executable. This is the only thing in the toolset that answers that
question without RUNNING the VI - which matters because a top-level UI loop never ends on
its own, so "just run it" is not available for the VI that most needs checking.
WHAT THE NUMBER MEANS: 0 eBad - BROKEN, 1 eIdle - fine and not running, 2 eRunTopLevel,
3 eRunning. And -1 means the REFERENCE NEVER OPENED, which is not a verdict about the VI at
all: read `code` and `source` for why. Those two are kept apart on purpose, because a failed
open leaves the state property unread at 0 and would otherwise read as eBad.
LINKER ERRORS NAME THE CAUSE of a missing link - `Missing subVI X.vi in VI Y.vi.` - as
LabVIEW's flattened XML of the string; an empty `<Val>` means none. Both backends read them
with the same helper VI; on pylabview `linkerErrorsAvailable` says whether they were read.
WHEN TO REACH FOR IT: after any pylv_apply retarget, after generating a VI that calls
project-local code, and whenever a chain of green answers still leaves you unsure. Measured:
pylv_apply reported ok with every callTarget correct over a caller that was Error 1003.
Opening and closing a VI REFERENCE does not burn the path for a later regeneration - only
lvai_open_file does that - and the helper opens WITHOUT an application instance, so this
also answers while pylv_apply has the project closed.
A BROKEN CLASS MEMBER ALSO GETS `missingInterfaceOverrides`, read from the files with no
LabVIEW: every dynamic-dispatch method of every interface the owning .lvclass implements
that the class does not override by name, with the interface's own `mustOverride` flag. A
missing override broke EVERY member of its class in this repository's measurements, and
the broken VI does not name it. Members whose dispatch cannot be read are listed under
`dispatchUnknown`, never as missing; a VI in no class, or a class with no interface, gets
null with `missingInterfaceOverridesReason`. An executable VI skips the check.
SEVERAL VIs IN ONE CALL: pass viPaths, one path per line (with or without viPath). The
answer is then one object - `brokenVis`, `executableVis`, `couldNotOpenVis`, `failed`, and
every VI's whole single answer under `results`. Measured over this repository's transcripts
the tool was called straight after itself 54 % of the time; a batch costs one model turn
where N did, and the VIs are read one after another, so nothing is faster inside LabVIEW.
```

- `viPath`: Absolute path to the .vi to interrogate. Optional when viPaths is given.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvai_exec_state.xml in scriptsDirectory.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview the SAME helper is COMPOSED into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server instead of NI's service, so linker errors are named there too. Only when that helper cannot be composed or run is the state read directly over ActiveX, which has no linker-error property: the answer then says `linkerErrorsAvailable: false` and why. LabVIEW must already be running - this never starts it.
- `viPaths`: Absolute .vi paths, ONE PER LINE, for several VIs in one answer. May be combined with viPath.

## `lvai_filter_example_search_candidates`

```text
RPC FilterExampleSearchCandidates (unary). Given example VI paths, returns each
example's description as LabVIEW knows it.
```

- `examplePaths`: Example VI paths, comma or newline separated
- `timeoutSeconds`: Local budget in seconds (default: 60)

## `lvai_filter_palette_search_candidates`

```text
RPC FilterPaletteSearchCandidates (unary). Given palette item GUIDs, returns the
displayable details LabVIEW keeps for them: title, description, icon, palette
hierarchy and help path. This is the resolver behind LabVIEW's AI palette search.
```

- `itemGuids`: Palette item GUIDs, comma or newline separated
- `timeoutSeconds`: Local budget in seconds (default: 60)

## `lvai_find_palette_item`

```text
RPC FindPaletteItem. MUTATING (IDE state): makes LabVIEW reveal/highlight the palette
item with the given GUID. Purely a UI action - useful to confirm a GUID resolves.
```

- `guid`: Palette item GUID
- `timeoutSeconds`: Local budget in seconds (default: 60)

## `lvai_flatten_typedefs`

```text
MUTATING: installs the SUBJECT's real terminal types onto a placeholder stub, with every
typedef link removed at every depth, and verifies it from the saved file.
WHEN YOU NEED IT: after lvai_placeholder_subvi, when the subject's pane carries typedefs and
the stub's AIXML-approximated type is not good enough. lvai_placeholder_subvi calls this for
you unless you pass flattenTypedefs false, so a direct call is for repairing a stub that
already exists.
HOW IT WORKS, and it is NOT `Discon Typedef`. A typedef is a `.ctl` with TypeDefVI set in
its save record. Each `.ctl` of the chain is copied into a cache directory, that bit is
cleared with pylabview - no LabVIEW - and the copy is then installed with
{LV.Control} Replace, which against a NON-typedef installs the type and creates no link.
THE COPIES DO NOT GO BESIDE THE STUB. Measured 2026-09-18 as an A/B seconds apart in one
LabVIEW: a Replace whose SOURCE .ctl sits under user.lib answers error 1154, and the same
content outside the installation answers 0. Nothing is lost by moving them out - the copy
is not a typedef, so the Replace leaves no link and the stub does not reference it.
MEASURED 2026-09-18: {LV.Control} Discon Typedef from a generated helper KILLS LabVIEW, five
times, with nothing in NI's crash log and the target never written. Do not reach for it.
THE RECURSION IS THE POINT. Clearing the flag on the outer `.ctl` alone leaves a typedef
embedded in its definition, which then arrives in the stub as a live typeDef heap object -
measured as an A/B. So the chain is flattened and rebound from the inside out.
THE VERDICT IS `typedefObjectsInStub`, read back off the SAVED stub: 0 is the pass. It
counts `class="typeDef"` objects in the front panel heap, NOT `Type="TypeDef"` entries in
the type pool - a flattened `.ctl` keeps stale pool entries that reach nothing.
AN ARRAY IS WALKED INTO as well, since 2026-09-18: `Controls[]` is not declared on
{LV.Array}, so the helper reads `Array Element` beside it and picks on `Class Name`, and an
array is then a container with exactly one child at index 0. Measured on an array of a
typedef enum, which had been named in `notDescended` and is now flattened like any other.
The flattened copies are CACHED under a hash of the source's CONTENT, so a second stub over
the same typedef costs one Replace and editing the typedef produces a different copy rather
than a stale hit.
A CHILD WHOSE PROPERTY READ FAILED IS REPORTED, NOT SKIPPED. The tree helper returns a read
code per control, because only the last iteration's error could reach its error out: a
control that failed came back as a plausible row with an empty path while the VI answered 0,
and keying on the empty path skipped it in silence. Measured 2026-09-18.
`subjectTypedefObjects` IS THE SUBJECT ASKED AGAIN FROM ITS FILE, with no LabVIEW involved,
and it exists to kill a GREEN NO-OP. Everything else here comes through VI Server, and a
LabVIEW that has been through many open/Replace/Discon cycles serves a STALE in-memory copy
whose typedef links are already gone - the walk then finds nothing and the call reports
success for having nothing to do. A file that holds typedef objects while the walk found NONE
is now `errorKind: subjectTypedefsNotSeen`, not an ok.
THE TWO COUNTS ARE IN DIFFERENT UNITS and only that one comparison is a verdict:
`typedefSites` counts the OUTERMOST typedef per branch, `subjectTypedefObjects` every
instance at every depth, so 1 against 2 is the ordinary shape of a typedef inside a typedef.
```

- `viPath`: Absolute path to the stub whose terminals should carry the real types
- `subjectPath`: Absolute path to the SUBJECT - the VI whose pane the stub was cloned from, and the only place the typedef identities still exist. The stub itself carries none, which is exactly why it cannot be its own source.
- `route`: Which measured route to use, "flag" (the default) or "disconnect". They reach the same end - the subject's real type with no link at any depth - and differ in what they need. "flag" copies each `.ctl` of the chain and clears its TypeDefVI bit with pylabview. It needs NO project, NO editor and NO IDE application instance, and it cannot end the LabVIEW session. It writes copies into a cache. "disconnect" installs the ORIGINAL `.ctl` and then calls {LV.Control} Discon Typedef. It copies nothing, is faster, and on a CLASS terminal LabVIEW refuses it itself with Error 1088 instead of needing a guard of ours. It REQUIRES a project open and active AND the stub open in the editor - this call opens it for you - and missing either kills LabVIEW with nothing in NI's crash log, measured five times. THE COST OF "disconnect" IS THAT IT OPENS THE STUB IN THE EDITOR, which puts its path in LabVIEW's memory - so a later regeneration of that stub can answer Error 1357 until the VI is closed or LabVIEW restarts. The answer says so in `stubOpenedInEditor`. (default: "flag")
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_generate_caraya_test_runner`

```text
MUTATING: writes the ONE VI that runs a whole Caraya suite - every test VI's path built
relative to the runner's own location, collected into an array, handed to
`Caraya.lvlib\3ARun Tests.vi` (instance `Run Test (Array Path)`) with `Interactive (T)`
FALSE and a `Report Path` ending in .xml - and lists it in the project.
THIS REPLACES THE MOST EXPENSIVE HAND-AUTHORED STEP OF A TEST RUN. Measured 2026-08-30 over
a three-class, five-suite build: authoring, generating and debugging the runner took 186 s
of wall clock against 6.1 s inside LabVIEW. Almost all of it was the model writing AIXML it
had written twice before, because the runner's shape never varies - only the file names do.
The whole build was 920 s, so this one step was a fifth of it.
RELATIVE PATHS ARE THE DESIGN, not a detail. `Current VI's Path` -> `Strip Path` ->
`Build Path` per test means the folder can be copied or renamed and the suite still runs. A
test VI that does not live under the runner's own folder is therefore REFUSED by name rather
than written as an absolute constant that breaks at run time with `Error 7`.
`Interactive (T)` IS FALSE AND STAYS FALSE - TRUE opens Caraya's modal report dialog, and a
modal dialog stops LabVIEW's whole gRPC service until a human dismisses it.
READ THE JUNIT REPORT, NOT `error out`. Caraya answers 7002 when a suite FAILED, which is a
pass/fail signal rather than a fault, and the cluster carries the first failed assertion
only. The runner returns the report's absolute path in `Report Path used`.
CARAYA ONLY. The array-of-paths shape and both target spellings were measured off a working
runner's export; nothing equivalent has been measured here for LUnit or VI Tester, so this
does not pretend to be framework-neutral.
```

- `testViPaths`: The test VIs to run, ONE ABSOLUTE PATH PER LINE, plain text and NOT JSON. Every one of them must live under the runner's own folder, directly or in a subfolder.
- `runnerViPath`: Absolute path of the runner .vi - WILL BE OVERWRITTEN.
- `reportFileName`: File name of the JUnit report, written beside the runner. MUST end in .xml - a .txt extension makes Caraya write no file at all. Defaults to '<runner>-TestReport.xml'.
- `projectPath`: The `.lvproj` to list the runner in. Pass it whenever the tests belong to a project: the project is CLOSED before the file is edited and LEFT closed, because LabVIEW's close saves its own copy over the file and would destroy the edit. A runner runs with its project closed - measured, class suites included.
- `testFolderName`: Virtual folder inside the project to list the runner in (default: "Tests")
- `keepAixml`: Keep the generated AIXML instead of deleting what succeeded (default: false)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_generate_class_test`

```text
MUTATING: writes a Caraya unit test that exercises a .lvclass's accessors as ORDINARY STATIC
SUBVIS - one write-then-read round trip per field - and leaves it ready to run.
THIS IS THE CALL THAT REPLACES ABOUT FORTY. Measured 2026-08-29 building the same thing by
hand for a three-class hierarchy: sockets generated one at a time, the test authored, then
nineteen further calls alternating a node listing and a node swap. Everything below happens
inside this one call.
TWO ROUTES, and `route` in the answer says which ran and why. DIRECT, the default since
2026-09-25: one accessor is opened through the project that lists the class (found by
itself, or projectPath), the test names the REAL accessors - a class with one member open
is a legal Call target, docs/aixml-call-loaded-vi.md - each chain's seed `path` constant is
turned into the class with {LV.Constant} Replace, and the project is closed. When no single
project lists the class, or the accessors do not resolve, the SOCKET route runs:
WHY SOCKETS AT ALL. lvai_generate_test's placeholder is a pane clone generated through
AIXML, and AIXML refuses a class-typed terminal - `Control with type=UDClassInst is not
supported` - so it answers `stubRefused` for any class member. This route instead authors
sockets whose class terminals are `path` stand-ins and then uses LabVIEW's own {LV.SubVI}
Replace, which RE-TYPES THE WIRES where a pylabview link retarget cannot.
casesJson is a JSON ARRAY, one object per field:
  [{"field":"Hersteller","value":"Fluke"},{"field":"Max Spannung V","value":"30"}]
A CLUSTER FIELD'S `value` is AIXML's cluster literal: the elements in the cluster's own
order, comma separated, in square brackets, a nested cluster bracketed again, a string
UNQUOTED and an enum as its INDEX, not its item name. Channel Config {Name, Channel Mode
(Off,Voltage,Current), Range {Min,Max}, Samples} as Voltage, -10..10, 1000 samples:
  {"field":"Config","value":"[CH1,1,[-10,10],1000]"}
Measured 2026-09-25 - LabVIEW's export of the saved test read that literal back
unchanged. A comma inside a string element is not measured.
A FRESH OBJECT'S DEFAULT is a case too - `expectDefault` in place of `value` reads the field
off the seed object with no Write and asserts that: {"field":"Gain","expectDefault":"1"}.
One round trip and one default case per field.
The only case keys are `field`, `value`, `expectDefault`, `label` and `type`; ANY OTHER IS
REFUSED BY NAME,
with the accepted set listed and a pointer to the tool that can do what the stray key was
reaching for. In particular a value the field should hold AFTER some method ran is
lvai_generate_method_test's `expectFieldValue`, not a case key here.
`label` is optional and names the case in the JUnit report. `type` is optional too - when
omitted the field's type is read off the Write accessor's own export, which is authoritative
and costs one export per field.
THE EXPECTED VALUE IS WHAT WAS WRITTEN. A round trip needs no invented expectation, and the
same constant feeds both the write and the assertion, so the two cannot drift apart. Float
equality is EXACT - choose values that are exact in IEEE754.
seedClassPath tests INHERITANCE: leave it out and each chain starts from lvclassPath's own
constant, or point it at a CHILD class to run the parent's accessors on a child object.
A CLIENT TIMEOUT IS NOT EVIDENCE THAT THE WORK FAILED. Measured 2026-09-03, twice in one
run: the MCP client answered `Request timed out` / `Connection closed` while the server
kept going and completed correctly - the .vi on disk, its mtime and a fresh AIXML export
all confirmed the full generate, the swaps and the project entry had landed. VERIFY
BEFORE RETRYING: check the file, or you generate the same suite twice and the second
attempt fights the first for the sockets.
READ THE JUNIT REPORT, NOT `error out` - the cluster carries the first failed assertion only.
AND PROVE IT CAN FAIL before believing a green run: lvai_set_constant on a DEFAULT case's
`expected <n>` constant, confirm exactly one failure, set it back. A round trip cannot be
broken that way - its `written <n>` constant feeds both the Write and the assertion.
```

- `lvclassPath`: Absolute path to the .lvclass whose accessors are the subject
- `casesJson`: JSON array of cases: field, value, and optionally label and type
- `testViPath`: Absolute path of the test .vi - WILL BE OVERWRITTEN. Defaults to 'Test <Class>.vi' beside the class.
- `seedClassPath`: The .lvclass each chain is seeded with. Defaults to lvclassPath. Point it at a CHILD class to test that an inherited accessor works on a child object - the accessors stay the parent's, only the object changes.
- `projectPath`: The `.lvproj` to LIST the test VIs in. Pass it whenever the class belongs to a project, which is nearly always: nothing else writes a VI into a `.lvproj`, and a suite that is not listed is one the user cannot find. Measured 2026-08-29 - a complete, green, verified suite was handed over and the Project Explorer showed the classes and no tests at all. The project is CLOSED before the file is edited and left closed, because LabVIEW's close saves its own copy over the file and would destroy the edit. On the DIRECT route an omitted projectPath is not "list it nowhere": the route uses the one project that lists the class, and lists the test there under testFolderName.
- `testFolderName`: Virtual folder inside the project to list the tests in. A test LabVIEW's own save has just adopted at the project's top level - which is what happens to every test generated with the project open - is MOVED into it; one that was listed elsewhere before this call stays where it was, and `listedElsewhere` names it. (default: "Tests")
- `alsoListInProject`: Further VIs to list in that same folder, ONE ABSOLUTE PATH PER LINE, plain text and NOT JSON - normally the suite runner. A bracket, a quote or a relative path is refused by name before anything is generated; it used to be resolved against the SERVER's working directory and written into the .lvproj as a path that cannot exist. They go in through this call because the project has to be CLOSED while the file is edited, and doing it here costs one close/re-open instead of two. A runner is not this call's artefact - it spans several classes, and one exists per suite rather than per class - so it cannot be derived. Measured 2026-08-29: the runner reached the project only because LabVIEW happened to adopt it while saving, which is luck rather than a mechanism. THE FILE MUST ALREADY EXIST. A path named here before its VI has been generated used to be written into the .lvproj, counted in `added`, and then swept back out by the tidy pass's dangling check - `ok: true`, nothing in the tree. It is now refused by name and reported in `notOnDisk`, so generate the runner FIRST and name it on the last call.
- `keepAixml`: Keep the generated AIXML instead of deleting what succeeded (default: false)
- `verbose`: Keep every sub-answer whole. OFF by default, and that default is a measurement: this call answered 72 818 characters for a five-field class on 2026-09-03, over the MCP output limit, so it spilled to a file and cost the caller two extra reads to get at three numbers. It nests a complete lvai_generate_vi answer PER SOCKET - ten of them for five fields, each with its own validate, convert and pane sub-answers. A step that FAILED is reported whole regardless of this flag, so nothing diagnostic is lost. (default: false)
- `directCall`: Call the accessors DIRECTLY instead of through sockets: open one of them through the class's project, name the real accessors in the test, turn the seed constants into the class, close the project. On by default. False forces the socket route. (default: true)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.
- `measureDiagram`: Render the test VI to report its `diagramSize`, after our layout of its diagram (the cleanUpDiagram step, lvai_layout_diagram's engine) - on by default, because a test VI has the same 1920 x 1080 budget as any other. False skips the render AND the clean-up. (default: true)

## `lvai_generate_method_test`

```text
MUTATING: writes a Caraya unit test that calls a class's METHODS as ORDINARY STATIC SUBVIS
and asserts either the error code they return or that the class wire survives them.
THE COMPANION TO lvai_generate_class_test, which does accessors. Measured 2026-09-02:
authoring a method suite by hand was the largest single item of that run - ~80 s of wall
clock for 0 s inside LabVIEW, because the shape never varies.
TWO ROUTES, and `route` in the answer says which ran and why. DIRECT, the default since
2026-09-25: one method is opened through the project that lists the class (found by
itself, or projectPath), the suite names the REAL methods and accessors - a class with one
member open is a legal Call target, docs/aixml-call-loaded-vi.md - each chain's seed `path`
constant becomes the class through {LV.Constant} Replace, and the project is closed. No
socket is generated and no node is swapped. When no single project lists the class or the
members do not resolve, the SOCKET route runs: a socket per method call and per accessor,
swapped for the real member afterwards. A case the method cannot serve - a wire-survival
case on a method that returns no object - is REFUSED on the direct route before anything is
written, where the socket route would have generated a suite that cannot run.
casesJson is a JSON ARRAY, one object per case, in one of FOUR shapes:
  [{"method":"Describe","expectOutput":"description",
    "expectValue":"Bicycle - a human-powered two-wheeled vehicle.",
    "label":"Describe names the class"},
   {"method":"Initialize","expectErrorCode":-200099,
    "label":"Initialize with no device reports invalid physical channel"},
   {"method":"Start","writeField":"Timeout","value":"10.0",
    "label":"Timeout survives Start"},
   {"method":"Zero","writeField":"Reading","value":"12.5","expectFieldValue":"0",
    "label":"Zero sets Reading to 0"}]
AN UNKNOWN CASE KEY IS REFUSED BY NAME, with the accepted ones listed. It used to be
dropped in silence: measured 2026-09-15, two test agents independently reached for
`expectFieldValue` before it existed, had it discarded, and got `ok: true` for a suite
whose assertion asserted the OPPOSITE of the one asked for. `expectErrorCode` must be an
unquoted NUMBER, and a quoted one is refused rather than discarded.
`expectOutput` + `expectValue` assert a value the method RETURNS on a named terminal -
which is what a `Describe.vi`, a formatter or any non-accessor getter needs, and what this
tool could NOT express until 2026-09-07. The gap cost a cold build's test phase 634 s ->
890 s, because four such tests were hand-authored instead. The terminal's TYPE is read off
the method's own export (override with `outputType`), and a name the method does not
declare is REFUSED BY NAME with the available ones listed: the socket swap re-attaches
wires by terminal name, so a misspelling would leave the real terminal unwired and the
suite would pass having asserted a default. `expectValue":""` is legal and means the empty
value - an interface declaration body returning "" is the normal case.
`expectErrorCode` asserts the `code` of the method's own error cluster. `writeField` +
`value` writes a field, calls the method, and reads the field back OFF THE RETURNED OBJECT -
pass `readField` when it differs. That shape asserts the field SURVIVED the call unchanged,
which is the one assertion a dynamic dispatch mistake fails. For a method whose JOB is to
change the field - a Zero, a Reset, an Increment - add `expectFieldValue`: the seed still
comes from `value`, and the read-back is asserted against `expectFieldValue` instead. Its
absence meant such a method could not be tested at all, because seeding 12.5 into a `Zero`
asserted `12.5 == 0`. A case may carry any combination.
`seed` WRITES FIELDS WITHOUT ASSERTING THEM, as many as the method needs:
  {"method":"Read Value","seed":{"Current":"3","Gain":"2"},
   "expectOutput":"reading","expectValue":"6"}
That is the shape for a method reading two fields, which one `writeField` could not
express. The answer's `assertions` step lists every assertion each case generated, and
warns where `writeField` beside `expectOutput`/`expectErrorCode` implies "the field is
UNCHANGED after the call" - move the field to `seed` if it only has to be set.
`expectFields` ASSERTS SEVERAL CHANGED FIELDS from one request, each starting from `seed`:
  {"method":"Add Value","inputs":{"Value":"2.5"},"seed":{"Sum":"1.5","Count":"1"},
   "expectFields":{"Sum":"4","Count":"2"}}
It becomes one case per field, labelled `<label> (<field>)`.
`setup` CALLS OTHER VIs BEFORE THE METHOD, exactly as in lvai_generate_test - a fixture
written before a Deposit, a file reset before a Withdraw:
  "setup":[{"vi":"C:\\...\\Write Accounts File.vi","inputs":{"accounts path":"...","accounts":"[[1,A,B,10]]"}}]
Their chain feeds the METHOD's error in, which is what orders them; a setup error therefore
reaches the method, which is the honest outcome. Direct route only. The cases of one test
VI RUN IN PARALLEL, so a path given to a setup in one case and used by another is refused -
one fixture per writing case.
WHAT AN `expectValue` PINS IS OBSERVED BEHAVIOUR, not a specification, unless the user gave
you the value. Say which in your report - a measured string asserted as if it were the spec
freezes whatever the method happens to do today.
THE METHOD'S ERROR IS NEVER CHAINED INTO THE ASSERTIONS. A method under test is expected to
fail with no hardware; chaining it would poison every later assertion and report failures
the test itself caused. It is fed `no error` - or its case's setup chain - and its
`error out` is only unbundled.
EVERY REQUIRED INPUT OF THE METHOD IS WIRED, and that is not optional. A `required` input
left empty makes the whole suite NOT EXECUTABLE - Caraya answers `7101, At least one test is
not in a executable state` - and nothing upstream sees it: measured 2026-09-03, this call
answered `ok: true` for exactly such a suite. The method's own export is read for them, and
a type with no honest default (a refnum, an IO-name tag, a variant) is REFUSED BY NAME
rather than guessed. Supply those per case:
  [{"method":"Initialize","expectErrorCode":-200099,
    "inputs":{"Physical Channel":"Dev1/ai0"}}]
`inputs` sets ANY input terminal of the method, required or not: a required one is always
wired (with the default where the case gives none), any other only when the case names it,
so an unnamed one keeps the method's own default. A name the method does not declare is
refused (`inputTerminalNotFound`), as is the class or error input (`inputNotSettable`), and
a front-panel object with no conIdx in `inputs` or `expectOutput` (`terminalNotOnPane`) -
a Call cannot wire it, and LabVIEW says only `An input parameter is invalid`.
Until 2026-09-25 a value for a non-required terminal was dropped in silence.
EVERY METHOD MUST ALREADY BE A CLASS MEMBER with a class-typed pane - use
lvai_add_class_method first. A method whose .vi is missing is named rather than generated.
A CLIENT TIMEOUT IS NOT EVIDENCE THAT THE WORK FAILED. Measured 2026-09-03, twice in one
run: the MCP client answered `Request timed out` / `Connection closed` while the server
kept going and completed correctly - the .vi on disk, its mtime and a fresh AIXML export
all confirmed the full generate, the swaps and the project entry had landed. VERIFY
BEFORE RETRYING: check the file, or you generate the same suite twice and the second
attempt fights the first for the sockets.
READ THE JUNIT REPORT, NOT `error out`, and PROVE IT CAN FAIL once: change one
expectErrorCode by a digit, confirm exactly one failure, put it back.
A COLD EXPORT OF A DRIVER-DEPENDENT METHOD HAS KILLED LabVIEW. Measured 2026-09-03: three
deaths in one session, every one while this call pulled a DAQmx class member's hierarchy in
for the first time; the log tail was hundreds of `DSToExtFuncLinkRef::UnFlatten` lines for
DAQmx channel variants. Exporting one method FIRST, on its own, made the call succeed. This
tool now does that warm-up itself and reports it as its own step, so a death there is
attributed to the warm-up rather than to a case - but it is a mitigation, not a fix, and
the crash is in NI's code. If it happens anyway, restart LabVIEW and call again: the second
attempt has always succeeded.
```

- `lvclassPath`: Absolute path to the .lvclass whose methods are the subject
- `casesJson`: JSON array of cases: method, plus expectOutput+expectValue, expectErrorCode, or writeField+value
- `testViPath`: Absolute path of the test .vi - WILL BE OVERWRITTEN. Defaults to 'Test <Class> Methods.vi' beside the class.
- `seedClassPath`: The .lvclass each chain is seeded with. Defaults to lvclassPath; point it at a CHILD to run a parent's methods on a child object.
- `projectPath`: The .lvproj that lists the class. The direct route opens the class through it, and the test VI is listed in it under testFolderName. Omitted, the direct route uses the one project that lists the class and lists the test there too; the socket route lists it nowhere.
- `testFolderName`: Virtual folder inside the project to list the test in. A test LabVIEW's own save has just adopted at the project's top level is MOVED into it; one that was listed elsewhere before this call stays where it was, and `listedElsewhere` names it. (default: "Tests")
- `keepAixml`: Keep the generated AIXML instead of deleting what succeeded (default: false)
- `directCall`: Call the methods and accessors DIRECTLY with the class open through its project, instead of through sockets. The socket route still runs when no single project lists the class or the calls do not resolve; `route` in the answer says which ran. (default: true)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.
- `measureDiagram`: Render the test VI to report its `diagramSize`, after our layout of its diagram (the cleanUpDiagram step, lvai_layout_diagram's engine) - on by default, because a test VI has the same 1920 x 1080 budget as any other. False skips the render AND the clean-up. (default: true)

## `lvai_generate_mock_class`

```text
MUTATING: creates an Astemes LMock mock class on disk for an INTERFACE - the .lvclass, a
Create.vi constructor, one override per dynamic dispatch member, and one `When <Method>.vi`
per member. LMock's own generator does the work, so the result is what its right-click
entry produces; measured against the mock NI ships with the LMock example, the two differ
only in uid numbering.
THE SOURCE MUST BE AN INTERFACE. An ordinary .lvclass is refused HERE, before LabVIEW is
touched, because LMock answers Error 1704 with a MODAL DIALOG that stops this whole gRPC
service until someone clicks Continue. NI.LVClass.IsInterface is read straight out of the
file - plain XML, no LabVIEW, no cost. If the dependency is not an interface, mocking is
not available until one is extracted, which is a design change to the code under test.
TO LIST THE MOCK IN A PROJECT, PASS `projectPath` - NOT `addToProject`. The entry is then
written into the .lvproj as a FILE edit with LabVIEW uninvolved, which is the same route
lvai_create_class uses and the only one measured to survive. The ACTIVE PROJECT IS CLOSED
FIRST and has to be: the two requirements are mutually exclusive, because LMock's terminal
needs the project OPEN and an edit that survives needs it CLOSED.
`addToProject` IS FALSE BY DEFAULT, and since 2026-09-15 that is not only about the modal
dialog - IT DOES NOT SURVIVE. Measured: a mock generated with addToProject:true against an
open, active project wrote all four files and appeared in the .lvproj NOT AT ALL, because
the next lvai_close_active_project saves LabVIEW's in-memory copy over the file and the
mock is not in that copy. The same save also deleted an entry for an earlier mock that had
been added to the file BY HAND, which is what makes the mechanism unambiguous. The answer
reported ok:true and four filesOnDisk throughout, so nothing anywhere said the project
entry had been lost.
The old reason still holds too: LMock's own terminal defaults to TRUE when left unwired,
and with no active project that is Error 1055 - as a modal dialog that stops this service.
READ `ok` AND `filesOnDisk`, NOT `filesCreated`. LMock reports the files it set out to
write; a failed run has been measured returning two paths and writing neither. `ok` is true
only when the error cluster is clean AND the .lvclass is really there.
Mock generation is only the first step of a mock-based test. The mock's own VIs are
project-local class code, so a generated test reaches them through lvai_placeholder_subvi
plus lvai_swap_subvis, exactly as any other class code. docs/labview-lmock-mocking.md has
the whole route and every target spelling.
```

- `interfacePath`: Absolute path to the .lvclass to mock. MUST be an interface - one whose NI.LVClass.IsInterface property is true. An ordinary class is refused here rather than by LMock, whose refusal is a modal dialog.
- `destinationPath`: Absolute path of the mock .lvclass to create. Its directory is created if absent - LMock does not create one, and its failure to do so is not reported usefully.
- `addToProject`: Ask LMock to list the new class in the ACTIVE project. FALSE by default. LMock's own terminal defaults to TRUE when unwired, which is Error 1055 plus a modal dialog when no project is active - that is the trap this default exists to close. Pass true only when a project is open and active. (default: false)
- `projectPath`: The .lvproj to LIST the finished mock in, written as a FILE edit with LabVIEW uninvolved - the same route lvai_create_class uses, and the only one measured to survive. Use this INSTEAD of addToProject. THE ACTIVE PROJECT IS CLOSED FIRST, because it has to be: LabVIEW's save-on-close writes its in-memory copy over the file, so an entry written while it holds the project is deleted moments later.
- `overwrite`: Replace an existing destination .lvclass. Refused by default. (default: false)
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory, because the scripts folder next to the exe may be read-only. Generated once and reused.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lmock_generate_mock.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds - LMock costs about 31 s on a cold session (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_generate_test`

```text
MUTATING: writes a Caraya unit-test VI that calls viPath as an ordinary static subVI, one
node per case, and leaves it ready to run.
TWO ROUTES, and the answer's `route` says which ran and why. DIRECT, the default since
2026-09-25: the subject is opened through the .lvproj that lists it (found by itself, or
projectPath), the test's Call names the subject itself - a loaded project VI is a legal
Call target, docs/aixml-call-loaded-vi.md - and the project is closed again, which releases
the subject; the test is then listed under testFolderName (`projectEntry` step). No
placeholder, no retarget, no pylabview. When no
single project lists the subject, when it will not become active, or when the subject
still does not resolve, the PLACEHOLDER route runs as before: lvai_placeholder_subvi for a
call node AIXML is allowed to create, lvai_generate_vi for the test, then pylv_apply's
retarget to point that node at your VI. Either way the active project is closed at the
end. Each sub-answer comes back whole under `steps`, so a failure reads the same as calling
them by hand. Measured: 18 calls by hand before this existed, 10 of them editing an object
heap.
casesJson is a JSON ARRAY, one object per test case:
  [{"label":"boiling point","inputs":{"celsius":"100"},"expect":{"fahrenheit":"212"}}]
The only case keys are `label`, `inputs`, `expect` and `setup`; ANY OTHER IS REFUSED BY
NAME, with the accepted set listed. It used to be dropped in silence - measured on the
sibling tool lvai_generate_method_test, where two agents invented a key, had it discarded
and got `ok: true` for a suite that asserted the opposite of what they asked.
`setup` CALLS OTHER VIs BEFORE THE SUBJECT, in order, each one's `error out` into the next
one's `error in` and the last into the subject's - a round trip (write, then read back) or a
reset before a transaction is one case:
  "setup":[{"vi":"C:\\...\\Write Accounts File.vi","inputs":{"accounts path":"...","accounts":"[[1,A]]"}}]
Direct route only; each setup VI needs an error in/out pair and the subject an `error in`.
The subject is still called ONCE per case; its outputs are what `expect` asserts.
THE CASES OF ONE TEST VI RUN IN PARALLEL - only a case's own error wire orders its setup
before its subject - so a path given to a SETUP in one case and used by any other case is
REFUSED before anything is written: give every case that writes its own fixture path.
Measured on the fourth ATM cold build, where three cases sharing one fixture failed and
passed by turns.
EVERY EXPECTED CONSTANT IS LABELLED `expected <n>` and listed in `expectedConstants` with
its case - lvai_set_constant breaks one for a negative control.
`inputs` and `expect` are keyed by the SUBJECT's own terminal names - lvai_vi_terminals
prints them, and so does this tool's placeholder step. CONNECTOR-PANE TERMINALS ONLY: a
front-panel object with no conIdx (a UI VI's Stop button), an `inputs` key that is an
output or an `expect` key that is an input is refused by name, with the pane listed under
`refusal` - a Call naming an off-pane control is `Error 1 ... An input parameter is
invalid` from LabVIEW, measured 2026-10-12. Values are written verbatim into an
AIXML constant of that terminal's type, so "100" is a double if the terminal is a double;
a value the format cannot express as a constant is refused at validation, by name.
EVERY assertion is `Assert Equal Value_Variant`, and float equality is EXACT - Caraya's
Assert Almost Equal is not wired up here. Choose case values that are exact in IEEE754, or
assert on something else.
READ THE JUNIT REPORT, NOT `error out`. The test VI's error cluster carries the FIRST failed
assertion only. Run the test with Caraya's runner (`Caraya.lvlib\3ARun Tests.vi`, instance
`Run Test (Scalar Path)`, Interactive FALSE and a Report Path ending in .xml) and read that -
a `.txt` extension writes no file at all.
`ok` false with failedAtStep `retarget` means THE TEST VI WAS WRITTEN and still calls the
placeholder. It is the link that needs another pass, not the diagram.
```

- `viPath`: Absolute path to the VI under test
- `casesJson`: JSON array of cases: label, inputs and expect, keyed by terminal name
- `testViPath`: Absolute path of the test .vi - WILL BE OVERWRITTEN. Defaults to 'Test <subject>.vi' beside the subject.
- `projectPath`: The .lvproj the subject belongs to, for the DIRECT route. Omit it and the one project at or up to three folders above the subject that LISTS it is used; with none, or with more than one, the placeholder route runs instead and the answer says why.
- `directCall`: Call the subject DIRECTLY instead of through a placeholder: open it through its project, author the test's Call against the subject's own name, generate, close the project. On by default. False forces the placeholder route. (default: true)
- `testFolderName`: Virtual folder of the subject's project the test is listed in, on the DIRECT route. LabVIEW's own save adopts the freshly generated test at the project's top level; that entry is moved here. A test listed somewhere else BEFORE the call stays where it is. (default: "Tests")
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.
- `measureDiagram`: Render the test VI to report its `diagramSize`, after our layout of its diagram (the cleanUpDiagram step, lvai_layout_diagram's engine) - on by default, because a test VI has the same 1920 x 1080 budget as any other. False skips the render AND the clean-up. (default: true)

## `lvai_generate_vi`

```text
MUTATING: the whole create-a-VI sequence in one round trip - lvai_validate_aixml, then
lvai_convert_aixml_to_vi, then lvai_connector_pane on the result. Stops at the first step
that fails and reports which one, so a failure reads the same as calling the three by hand.
USE THIS INSTEAD OF THE THREE SEPARATE CALLS when you are generating from AIXML you have
just authored. It is not a shortcut past them: each step's own answer is returned under
`steps`, including elapsedMs, so nothing is hidden.
THE PANE CHECK IS THE POINT, not the saved turns. lvai_convert_aixml_to_vi cannot see a
badly placed connector pane and neither can a run: a VI whose inputs sit on the output edge
validates, generates and executes. That defect has shipped twice. `ok` here is false when
the pane breaches NI's style guide, and the corrected conIdx values come back ready to
paste - author them into the AIXML and call this again.
`ok` false with `paneViolations` > 0 still means the .vi FILE WAS WRITTEN. The generation
succeeded; it is the pane that needs another pass.
Pass measurePane=false to skip the measurement - only worth it for a scratch probe with no
connector pane at all.
Pass panePattern to put the result on a SPECIFIC pane pattern instead of the station
default - needed when the VI must match a pane that already exists, because a caller's
wires bind to terminal positions and the same conIdx means different edges on a different
pattern. It runs before the measurement, so what comes back describes the final pane.
IT CALLS YOUR OWN CODE DIRECTLY WHEN THAT CODE IS OPEN IN LABVIEW. ValidateAIXML refuses a
Call to a project-local VI or class member in every state, while ConvertAIXMLToVI resolves
it by name once the target is loaded - opened through its project, opened loose, or for a
class any one member opened. So when validation names ONLY `Unsupported SubVI` targets,
this converts anyway, under a throwaway _name so a refusal burns nothing, and then gates
on the VI being EXECUTABLE in place of the validation that could not check those calls.
The answer says so under `loadedSubVIs`. If the targets were not loaded the convert
answers Error 53 and the note names them: open one through its project with
lvai_open_file and call again. That replaces lvai_placeholder_subvi + lvai_swap_subvis
for a caller of project code; a .ctl is NOT a Call target, and a constant feeding a
typedef input still needs lvai_bind_typedef_constants. docs/aixml-call-loaded-vi.md.
```

- `aiXmlFilePath`: Absolute path to the source AIXML .xml file
- `viPath`: Absolute path of the .vi to create - WILL BE OVERWRITTEN
- `openVI`: Open the created VI in the LabVIEW editor (default: false)
- `measurePane`: Measure the connector pane afterwards and gate `ok` on it (default: true)
- `panePattern`: Connector pane PATTERN the generated VI must end up on, e.g. 4815. Omit to keep whatever LabVIEW.ini's DefaultConPane gives it, which is the normal case. Pass it when the VI has to match a pane that already exists - a caller's wires bind to terminal positions, and the same conIdx means different edges on a different pattern. Applied after generation by the same pylabview step as pylv_apply {"op":"conpane"}, so it moves NO terminal and no caller has to change; it also closes the active project first, because a rebuild under a loaded VI writes the file while LabVIEW keeps serving its stale copy. The VI comes back SOURCE-ONLY: its compiled code is stripped so LabVIEW recompiles it, because the stale copy of that code was measured running the old VI (wrong result, no error) and reported crashing 64-bit LabVIEW. docs/pylabview-stale-compiled-code.md.
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `measureDiagram`: Render the generated block diagram and report its size under `diagramSize`, with the longest dependency chain read off the AIXML. On by default: the rule is a diagram of about 1920 x 1080, and three agent builds in a row shipped main VIs 3306-4152 px wide while every other check was green. An over-budget diagram does not fail the call - the VI is written - but the answer says so and names the chain to fold into a subVI. Costs one render, 90-320 ms measured. (default: true)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview the validate step is skipped - the composer refuses by name instead - and the VI's executability is read in its place, because the composer does not type-check wiring. NO STEP touches NI's service: executability and the diagram size go over LabVIEW's ActiveX server (which needs a RUNNING LabVIEW and never starts one), the pane is read from the file by pylabview, and panePattern rebuilds without closing a project.
- `cleanUpDiagram`: Lay out the written VI's block diagram with OUR layout engine - the pipeline of lvai_layout_diagram, since 2026-10-16 in place of LabVIEW's BD.CleanUp - and save it in place, BEFORE the diagram is measured, so diagramSize is the diagram you get. Omit it to clean up whenever the diagram is measured, which is every deliverable; helpers and carriers skip it. Skipped with openVI, where the IDE holds its own copy, and for a DQMH module's Main.vi. A VI that had no compiled code comes back with none. A failed layout does not fail the call - the VI is put back as generated, and the cleanUpDiagram step (engine: lvai_layout_diagram) says why.
- `cleanUpPanel`: Arrange the FRONT PANEL with the algorithm behind LabVIEW's Edit - Clean Up Panel and save in place - the user's rule for every VI that is NOT the application's MAIN VI, whose panel is the user interface and laid out on purpose: pass false for that one. Omit it to follow cleanUpDiagram. A panel holding a tab control or a splitter is left alone (the algorithm answers it with a modal dialog), reported as skipped.
- `icon`: The VI's ICON, drawn as the last step: "L1|L2|L3" - banner text and up to two lines, 5 characters each, drawable A-Z 0-9 space - . / : + #. Omit it to have one DERIVED from the VI's name (and its class or the tested subject); pass "none" to leave LabVIEW's blank icon. It FLATTENS any class template layer the VI had (reported as templateLayerReplaced); set a class icon afterwards with lvai_set_owner_icon applyToMembers. Drawn on every deliverable (whenever the diagram is measured), after every other step; helpers and carriers get none.

## `lvai_generate_vi_with_events`

```text
MUTATING: generates a VI from AIXML and REGISTERS its Event Structure's front-panel events,
which lvai_generate_vi cannot do. Use it for any document whose Event Structure has a frame
other than Timeout.
IT SKIPS VALIDATION ON PURPOSE. ValidateAIXML refuses every static event frame ("one or
more event cases have no events defined") while ConvertAIXMLToVI on the same bytes answers
errorCode 0 and writes the VI - measured on both NI design-pattern templates and on
documents authored from scratch. So run lvai_check_aixml first if you want a cheap
pre-filter; nothing here type-checks your wiring, and `ok` says the events are registered
and LabVIEW can run the result, not that the diagram is right.
NO EVENT MAPPING ARGUMENT, because the document already carries it: a selector spells its
control and its trigger (` "Setpoint"\3A Value Change `), and a second mapping would just be
somewhere for the two to disagree. Frames are registered in document order - position IS
`diagramIdx`. A `Timeout` frame is left alone; it needs no registration.
AN UNKNOWN TRIGGER IS REFUSED BY NAME rather than written as a Value Change, and so is a
document with two Event Structures - `diagramIdx` is a position within ONE structure, so
mapping two of them would be a guess.
THE EVENT DATA NODE'S FIELD SELECTION DOES NOT SURVIVE CONVERSION - every node comes back
`Source,Type,Time` - AND A WIRE AUTHORED FROM A DROPPED TERMINAL IS DROPPED WITH IT,
silently, leaving the consumer's input unwired at errorCode 0. AIXML is not the limitation:
LabVIEW's own export writes that attribute. The importer discards it, because the frame
carries no registration yet at that point. Whatever your document asked for is reported
back per frame as `dataFieldsDropped`, so this is visible instead of being something you
find weeks later. For a front-panel event, read the control's own TERMINAL in its frame
(what NI's templates do); for a USER EVENT there is no such terminal and the gesture stays
in the IDE - and a front-panel Local Variable is NOT a substitute, it holds what the panel
has at that instant rather than what the event carried.
A diagram comment too long for its box is also cut off in silence; only
lvai_render_diagrams shows that.
`ok: false` is never a rollback - the .vi on disk carries whatever got as far as it got, and
`steps` holds each sub-answer whole.
```

- `aiXmlFilePath`: Absolute path to the source AIXML .xml file
- `viPath`: Absolute path of the .vi to create - WILL BE OVERWRITTEN
- `verify`: Read execState back afterwards, which is the only check that sees a frame left unregistered (default: true)
- `bundleDirectory`: Where to put the extracted bundle. Defaults to a fresh temp directory, deleted on success. Pass a path to keep it - useful when a spec write needs debugging.
- `timeoutSeconds`: Local budget in seconds, per step (default: 180)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview this is lvai_generate_vi on that backend, ONE step: the composer writes every event frame's registration, the dynamic event terminal's wire and a user event's payload field itself, so there is no pylabview registration pass, no lvai_wire_dynamic_events and no lvai_set_event_data_fields to follow - and NI's service is not asked. Read a user event's payload by naming its field in the Event Data Node (`fields="Source,Samples,Time"`); a grpc-route placeholder constant stays the constant it is.

## `lvai_generate_vis`

```text
MUTATING: several AIXML files through the whole lvai_generate_vi sequence - validate,
convert, measure the pane - in ONE call, applied in the order given, EXCEPT that entries
with a `panePattern` run after the ones without: a pane repair closes the project, which
unloads the callees of everything after it (measured 2026-09-26, Error 53). `results` is
in the order given, and `ranLast` names what moved.
USE IT FOR BOILERPLATE SETS, which is where it pays: the socket VIs a class unit test needs
are one per test slot and fully determined by the subject's pane. Measured 2026-08-29 over a
three-class hierarchy, generating them one at a time cost 34 calls of lvai_generate_vi for
29 distinct targets - about four minutes, nearly all of it turn latency rather than LabVIEW.
pairsJson is a JSON ARRAY, one object per VI:
  [{"aixml":"C:\\t\\a.xml","vi":"C:\\t\\a.vi","panePattern":4815},
   {"aixml":"C:\\t\\b.xml","vi":"C:\\t\\b.vi"}]
`panePattern` is optional and per VI - a socket that has to match an accessor's pane needs
4815 while the test beside it takes the station default.
SEQUENTIAL ON PURPOSE, exactly like lvai_convert_vis_to_aixml: six generate calls issued
together took 559 ms against 543 ms one after another, because LabVIEW serialises the RPC.
The saving here is round trips, not throughput.
IT DOES NOT STOP AT THE FIRST FAILURE. Each entry gets its own answer under `results`, so
one bad AIXML does not cost you the other nine; `ok` is true only when every entry
generated. A failure reads exactly as lvai_generate_vi's would.
THE AIXML IS DELETED ON SUCCESS and KEPT ON FAILURE, the same bargain pylv_apply makes with
its bundle: the intermediate is noise once the .vi exists, and it is the only evidence when
the .vi does not. Pass keepAixml to keep it either way.
```

- `pairsJson`: JSON array of {aixml, vi, panePattern?} objects, applied in order
- `openVI`: Open each created VI in the LabVIEW editor (default: false)
- `measurePane`: Measure each connector pane afterwards and gate `ok` on it (default: true)
- `keepAixml`: Keep the AIXML sources instead of deleting the ones that succeeded (default: false)
- `timeoutSeconds`: Local budget in seconds, per VI (default: 300)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.

## `lvai_get_application_configuration`

```text
RPC GetApplicationConfiguration. Returns the LabVIEW application configuration
(currently just the UI language). Cheapest possible liveness probe.
```

## `lvai_graft_diagram`

```text
MUTATING: writes a COPY of a supplied VI (panelViPath) to outputViPath and puts the block
diagram of a generated SCAFFOLD VI into it, wired to the SUPPLIED controls - so the result
carries the supplied front panel untouched (layout, decorations, styled controls, typedef
bindings, VI properties, icon) and the generated program. The route for a CLD exam template
or any customer panel that must not be regenerated. The supplied VI itself is never changed.
THE SCAFFOLD is an ordinary generated VI whose controls and indicators carry EXACTLY the
supplied labels and types (the supplied VI's AIXML export gives both) and whose diagram is
the finished, verified program. Refused before anything is pasted when a label's kind or
type differs, when the supplied diagram already holds code (unless replaceDiagram), and - unless allowNewControls -
when a scaffold control is not on the supplied panel.
NEW CONTROLS (allowNewControls true): a scaffold control whose label the panel lacks is
pasted as a NEW control, wired as in the scaffold, and listed under newControls. LabVIEW
places it and gives it the default style - tidy the layout by hand afterwards. The panel is
then no longer identical, so panelIdentical is reported but does not decide ok.
THE CONNECTOR PANE IS THE SUPPLIED VI'S: nothing of the scaffold's pane travels, so a new
control - error in and error out included - gets no slot. Assign one afterwards if needed.
HOW: LabVIEW's scripted clipboard pastes the scaffold diagram, which makes a duplicate of
every supplied control ('Start 2'); each duplicate's terminal is replaced by the supplied
one at the same place and reconnected to the same wire ends, the loose ends are removed,
and the VI is saved. About 0.6 s of LabVIEW for a CLD panel.
OK IS DECIDED FROM THE FILE: execState 1, no duplicate left, every terminal feeding the
same sinks as in the scaffold, node counts equal, typedef references kept, and the panel
render byte-identical to the supplied one (comparePanel). The helpers' own error 0 is not
evidence - one probe saved an eBad VI with every error cluster clean.
NEEDS AN ACTIVE PROJECT (the edit happens in the IDE's application instance - Error 1055
otherwise). USES THE SYSTEM CLIPBOARD: whatever the user had copied is replaced, and two
grafts at once would paste each other's diagrams - run it from the orchestrator only, one
at a time. The scaffold's file name must differ from the output's.
SWITCH ACTION: switchActionControls sets the named boolean controls to Switch When Pressed
in the same IDE session, before the save, and switchActions reports each from the saved
file - a latched boolean cannot be signalled by a test (Error 1193). true there means the
saved control is NOT latched; switchActionsWereLatched says whether the supplied one was,
so true/false there means it was a switch already and nothing was changed for it.
A FAILED STEP LEAVES NO HALF-GRAFT: outputViPath is put back to the plain supplied copy.
An unwired scaffold terminal is refused up front (scaffoldTerminalUnwired).
ONLY FOR THE MAIN GUI: use it for the one top-level VI whose front panel is supplied or
user-facing. A subVI's panel carries nothing worth this route - regenerate subVIs.
A SUPPLIED DIAGRAM THAT ALREADY HOLDS CODE is refused unless replaceDiagram is true: the
copy's diagram is then EMPTIED first - every control's terminal moved onto the top-level
diagram so none is deleted with a structure, then every node, wire and decoration deleted
- and the scaffold grafted into it. The old code is DISCARDED in the output; the supplied
VI itself is untouched. Measured 2026-10-02 on NI's Display a URL example: loop, event
structure, local variable and comments gone, all three controls kept, execState 1.
TO GRAFT IN PLACE - the supplied VI must keep its path and name, as an exam requires - copy
it aside first and pass that COPY as panelViPath and the original path as outputViPath with
overwrite true. Do not open the supplied VI itself before: LabVIEW would keep serving the
copy it loaded. Measured with the output NOT listed in the active project, which works.
```

- `panelViPath`: Absolute path of the SUPPLIED VI whose front panel must survive. Never changed
- `scaffoldViPath`: Absolute path of the generated scaffold VI: same control labels and types, finished diagram
- `outputViPath`: Absolute path the grafted VI is written to - a copy of panelViPath. Must differ from both inputs
- `overwrite`: Overwrite outputViPath when it exists (default: false)
- `copyDescriptions`: Copy each scaffold control's non-empty description onto the supplied control (default: true)
- `comparePanel`: Render the panel before and after and compare the images byte for byte (default: true)
- `allowNewControls`: Allow scaffold controls the supplied panel does not have: they are added as new controls, placed by LabVIEW (default: false)
- `replaceDiagram`: Empty the supplied diagram first when it already holds code - the old code is DISCARDED in the output, every control is kept (default: false)
- `switchActionControls`: Boolean controls to set to Switch When Pressed, ONE LABEL PER LINE - e.g. latched buttons a test must be able to signal. Set in the same run, verified from the saved file
- `timeoutSeconds`: Local budget in seconds, per step (default: 180)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_labview_state`

```text
Read-only and makes NO LabVIEW call, so it answers while the service is blocked - and it
answers the same whichever AIXML backend is in force, because it reads Windows, not
LabVIEW. Lists the LabVIEW processes (pid, start time, path) and whether THIS server
started one (recorded by lvai_ensure_labview), the Nigel processes
(NI.NigelLocalService, LVNigelChat), the LVAutoSave folder (resolved through the
Documents known folder, which may be redirected) and its file count, every LabVIEW
window sorted into kinds, and one CPU sample.
`unsaved` = titles with `*`; labview-mcp's own scratch VIs (`LVMCP Validate…`) are
marked `labviewMcpScratch`. Each possible dialog carries `screenshot`, a PNG path to
READ: LabVIEW draws its dialogs, so the picture is the only way to see which one it is.
`possibleDialogs` = VISIBLE titled windows that are not a VI, project or frame window,
and VISIBLE UNTITLED windows that a LabVIEW frame owns or that sit beside a disabled
frame or Project Explorer (measured in the fork 2026-10-10: LabVIEW's "has changed on
disk" prompt is untitled and owned by the frame, which it disables), each with a
`reason`, named with class, buttons and text where Windows exposes them.
`hiddenUnrecognised` = HIDDEN titled windows nothing measured explains, named the same
way: measured 2026-10-06, LabVIEW dialogs on screen reported themselves invisible
(docs/scratch-vis-in-memory.md §2a). Hidden windows of measured system classes (IME,
OLE, DDE, GDI+) and LabVIEW's measured hidden windows are not listed there. A VI panel
with a custom window title is flagged too - look at it.
CPU: quiet below 1 CPU s per 30 s; idle is not safe - a modal dialog leaves LabVIEW idle.
NOTHING is pressed, fronted or closed. A modified VI with no open window does not show
here: lvai_vis_in_memory reads the modification bits. docs/labview-state.md.
```

- `cpuSampleSeconds`: Seconds of CPU sampling, 0 to skip (capped at 30) (default: 5)
- `includeHiddenWindows`: Also list hidden system windows (normally left out) (default: false)
- `screenshots`: A PNG of each VISIBLE possible dialog (PrintWindow: nothing is brought to the front), path under `screenshot`, in %TEMP%\LabVIEWMCP\dialogs. On by default: a few ms, and only when a dialog is up. (default: true)

## `lvai_layout_diagram`

```text
MUTATING: rearranges a VI's whole block diagram and SAVES IT IN PLACE - our replacement for
LabVIEW's Clean Up Diagram (BD.CleanUp), for a large existing VI as much as for a
generated one. A copy of the VI from before is kept and named under backupPath.
The layout: columns follow data flow; the error chain runs as one straight rail along the
bottom of each diagram with everything else stacked above it; parallel structures stack
vertically; on the top level controls go to the first column and indicators to the last; a
constant hangs left of the terminal it feeds; a comment moves with the object it describes;
every While/For Loop, Case and Event Structure is sized to its contents (Frame Size), its
tunnels levelled with what they connect, and every wire cleaned up. After the save, each
constant fan (two or more constants into one node) gets staggered wire geometry of its own.
Measured on a DQMH Main.vi: NI's Clean Up spread its diagram to 3805 x 1692 px; the
original Delacor layout was 1344 x 805.
Structures of a kind not yet measured (sequences, timed loops, in-place, disable) move as a
whole with their insides untouched - the answer names them.
dryRun computes and reports the plan without touching the VI. render puts PNGs of the
result beside the answer. verify (default) dumps the diagram again afterwards and reports
how many objects landed where they were meant to.
Works on the pylabview backend over ActiveX with no project open; the VI is opened by path.
A DQMH Main.vi IS laid out when named here - only the automatic clean-up after generation
skips it.
```

- `viPath`: Absolute path of the VI to lay out. Saved in place
- `dryRun`: Compute and report the plan, change nothing (default: false)
- `verify`: Dump the diagram again afterwards and compare it with the plan (default: true)
- `render`: Render the laid-out diagram to PNG, into renderDirectory (default: false)
- `renderDirectory`: Where the PNGs go when render is true; a temp folder otherwise
- `timeoutSeconds`: Local budget in seconds, per step (default: 240)
- `routeFans`: After the save, give each CONSTANT FAN - two or more constants wired into one node - staggered wire geometry of our own through the heap, where Clean Up Wire may cross them. Skipped on a VI that carries compiled code (default: true)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_layout_diagrams`

```text
MUTATING: lvai_layout_diagram over MANY VIs in one call - each saved in place, each with a
backup - with `parallel` workers in THIS server process. viPaths takes one absolute path per
line; a line naming a FOLDER takes every .vi under it, so a whole project is one call.
PARALLEL ONLY ON pylabview. There each worker has an ActiveX lane of its own and helper VIs
of its own, so the workers' LabVIEW operations run beside each other; measured over
ATM_DQMH_Cold's 76 VIs with four server processes: 46 s against 73 s, every VI where
planned and executable. On grpc the work runs one VI after the other (`parallel` is
reported as 1): NI's service is not measured running two helper VIs at once.
A worker never shares a VI with another, and the default ActiveX operations of every other
tool still run alone. Answers one compact row per VI and the totals; a VI that failed is
named with its errorKind, and the others go on.
```

- `viPaths`: The VIs, one absolute path per line; a folder takes every .vi under it
- `parallel`: Workers, 1-8. Parallel on pylabview only (default: 4)
- `verify`: Dump each diagram again afterwards and compare it with the plan (default: true)
- `timeoutSeconds`: Local budget in seconds, per step and VI (default: 240)
- `routeFans`: Give each constant fan wire geometry of our own, as lvai_layout_diagram does (default: true)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_list_labview_installations`

```text
List the LabVIEW installations found on this machine and which one would be started,
without starting anything. Bitness is read from each executable's PE header rather than
guessed from its folder. Useful to check the choice before calling lvai_ensure_labview.
```

## `lvai_log_usage_data`

```text
RPC LogUsageData. Writes a key/value pair into LabVIEW's usage telemetry. Included for
completeness of the interface; it emits analytics data, so there is rarely a reason to
call it.
```

- `key`: Telemetry key
- `value`: Telemetry value
- `timeoutSeconds`: Local budget in seconds (default: 30)

## `lvai_lookup_info_cache_items`

```text
RPC LookupInfoCacheItems (server streaming). Detail lookup for known cache GUIDs.
getPrototype returns the CONNECTOR PANE of a palette VI - that is what you need to
wire a node correctly when generating AIXML.
```

- `guids`: Cache item GUIDs, comma or newline separated
- `getStandardInfo`: Include standard item info (default: true)
- `getNodesInfo`: Include block-diagram node info (default: false)
- `getPrototype`: Include the connector pane / prototype (default: true)
- `maxMessages`: Max stream messages to collect (default: 20)
- `timeoutSeconds`: Local budget in seconds (default: 60)

## `lvai_lunit_add_test_method`

```text
MUTATING: takes AIXML you authored for one or more LUnit test methods and finishes each one -
converts it, puts its connector pane on LUnit's 4815 pattern, retypes the class-typed
terminals, makes the VI a MEMBER of the test case class, and verifies from LabVIEW's own
export. Many methods in ONE call.
USE THIS INSTEAD OF lvai_generate_vi, which cannot do it: lvai_validate_aixml type-checks
subVI wiring and REFUSES a class wire fed from a `path` stand-in, while
lvai_convert_aixml_to_vi writes the identical file with errorCode 0. For this one case the
validator is stricter than the generator, so validating first only blocks you.
WHAT YOU AUTHOR is the test method with `path` STAND-INS for its two class terminals, named
`<TestClassName> In` and `<TestClassName> Out`, at conIdx 11 and 3, with `error in (no
error)` at 8 and `error out` at 0. Assert with a member of the base class, e.g.
target="Test Case.lvclass\3APass If Equal.vim". The terminal on the assertion is called
`LUnit Test Case In` even though your control is `<TestClassName> In`.
THE TERMINAL NAMES ARE DERIVED from the .lvclass file name, so you do not pass them and
cannot misspell them; pass classTerminalNames only for a pane that deviates.
methodsJson is a JSON ARRAY, one entry per test method:
  [{"aixml":"C:\\t\\tm_marke.xml","vi":"C:\\t\\Tests\\Test Marke Round Trip.vi"}]
PASS projectPath AND IT MANAGES THE PROJECT FOR YOU, which it has to, because the two halves
of this job want OPPOSITE states. Converting and repairing a pane need the project CLOSED - a
VI generated while it is open is adopted as a loose project item and the membership step then
answers `Error 56002` - while the membership step needs it OPEN AND ACTIVE, because the
helper reaches the class through Project:Active Project -> Application so it edits the copy
the project holds rather than a second one beside it, and answers `Error 1055` otherwise.
So the run is two phases: close, then convert+pane for EVERY method, then open, then
retype+member+verify for every method. Without projectPath both phases run in whatever state
you left the IDE in, and 56002 or 1055 is then yours to interpret.
AND THE CLASS MUST NOT HAVE BEEN CREATED IN THIS LabVIEW SESSION. lvai_create_class leaves
it LOCKED and this answers `Error 1562`, "the specified project or library is locked". A
project close and reopen does NOT clear it - only a LabVIEW restart does. Create the class,
restart, then call this.
ORDER IS ENFORCED, membership LAST: saving the VI before it is a member writes it with no
owning-library link, and LabVIEW then marks the whole LIBRARY broken, blocking every VI it
owns with Error 1003. A method that fails before membership can simply be regenerated; one
that fails after cannot.
RE-RUNNING OVER AN EXISTING TEST METHOD IS SAFE SINCE 2026-09-08, and before that date it
SILENTLY EMPTIED THE SUITE. AddItemFromMemory answers 56002 or 1004 for a method the class
already lists; that error used to stop the helper's chain after the convert had already
overwritten the .vi, so the file was left carrying a fresh diagram with NO owning-library
link while the .lvclass still listed it. Nothing reported it - lvai_describe_class still
showed every method as a public member - and lvai_run_lunit_tests answered `tests: 0` with
`allPassed: true`.
THE REPAIR IS IN TWO PARTS, AND TOLERATING THE ERROR WAS ONLY THE SMALLER ONE. Measured
2026-09-08: with both codes filtered the run reported `memberAlreadyExisted: true`, both
saves executing, and `isClassMember` STILL FALSE - because AddItemFromMemory does not
re-adopt an item the project already holds, so there was no link to save. What fixes it is
the `dropExistingMembers` phase: with the project closed, the .lvclass entry for every
method this call is regenerating is removed, and the ordinary add path then writes the link
into the .vi. `projectPath` IS REQUIRED for that - without it the caller owns the project
state, and editing the class file under an open project would be undone by LabVIEW's own
save. The filter is kept as the guard that makes any remaining refusal visible.
SO ADDING COMMENTS OR CHANGING VALUES IN A FINISHED SUITE IS: edit the AIXML, call this
with projectPath, redo every lvai_swap_subvis, re-apply icons and any placed comments -
the convert destroys all three - then run the tests.
`verify` re-exports each finished VI and is the only real proof: it must report the VI's
name as `<Class>.lvclass:<Method>.vi` and both class terminals back as `ref{UDClassInst}`.
```

- `classPath`: Absolute path to the test case .lvclass the methods become members of
- `methodsJson`: JSON array of {aixml, vi} - the authored AIXML and where the test method .vi goes. One entry per test method; they are processed in order.
- `classTerminalNames`: Pipe-separated names of the two class-typed terminals. Omit to derive them from the .lvclass file name as `<Name> In|<Name> Out`, which is the convention this tool's own documentation prescribes.
- `projectPath`: Absolute path to the .lvproj, FILE NAME INCLUDED. Pass it and the tool closes the project for the convert-and-pane phase and reopens it for the membership phase, which is the only order that works. Omit only if you are managing that yourself.
- `panePattern`: Connector pane pattern to force; LUnit needs 4815, the 4-2-2-4 pattern (default: 4815)
- `verify`: Re-export each finished VI and check the member link and terminal types (default: true)
- `verbose`: Keep every sub-answer whole. OFF by default because the full ones overflow a client's token limit: measured 2026-09-01, six methods produced 86 000 characters, almost all of it LabVIEW's AIXML export per method plus the pylabview extract listing, and digging `methodsAdded` back out of it cost three extra turns. A step that FAILED is always reported whole regardless of this flag. (default: false)
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to lvlu_add_test_method.xml in scriptsDirectory
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.
- `cleanUpDiagram`: Lay out each test method's block diagram with our layout engine (lvai_layout_diagram's) once it is a verified member, and save it in place (the cleanUpDiagram step). Reported, never gating. (default: true)

## `lvai_lunit_scaffold_class_tests`

```text
MUTATING (it writes .xml files and may install socket VIs into the LabVIEW installation):
emits the AIXML for a whole LUnit suite over a class - one round-trip test per private data
field, one all-defaults test and one write-all/read-all independence test - and hands back
the `methodsJson` for lvai_lunit_add_test_method and the `swapsJson`/`constantsJson` for
lvai_swap_subvis, ready to paste.
THIS IS THE TRANSCRIPTION HALF OF THE ROUTE, and it was the largest item left in it: measured
over seven builds, authoring these six files cost 60-90 s of wall clock against 1.7 s inside
tools, in one turn, writing about 19 kB of text whose shape never varies.
IT DERIVES EVERYTHING IT CAN. Fields and their types come from the class's own Read/Write
accessors; the sockets come from lvai_placeholder_subvi, which it calls per accessor and
which caches, so a rebuild of the same class costs nothing. You do not pass field names,
types, terminal names or stub hashes, and so cannot misspell them.
YOU SUPPLY THE VALUES, and that is deliberate. valuesJson gives one value per field:
  [{"field":"Sorte","value":"Boskoop"},{"field":"Gewicht g","value":"182.5"}]
A generator that invented them would produce six green tests that pin nothing, which is the
one failure this route must not make easy. Every value must be NON-DEFAULT and distinct, or a
Write that stores nothing - or into the wrong field - passes.
NO ROUND TRIP IS WRITTEN FOR A `timestamp` FIELD, and that is not a gap. ConvertAIXMLToVI
DISCARDS a non-empty literal of that type - measured 2026-09-15 against five types that keep
theirs - so a round trip would author the written value and Expected in one document, lose
both, compare empty with empty and PASS. The field is named under `roundTripsSkipped`; the
defaults and independence tests still cover it, and a real test of it has to set the value at
RUN TIME.
WHAT IT DOES NOT DO: create the test case class, add the methods, swap the sockets or run
anything. The order is lvai_create_class -> RESTART LabVIEW -> this -> the .lvproj entry ->
lvai_lunit_add_test_method -> lvai_swap_subvis (all methods in ONE message) ->
lvai_run_lunit_tests. scripts/templates/lunit/README.md is that recipe in full, and the
templates there are this tool's specification - LUnitScaffoldTests compares the two.
```

- `classPath`: Absolute path to the SUBJECT .lvclass - the code under test
- `testClassPath`: Absolute path to the test case .lvclass the methods will belong to
- `outputDirectory`: Directory to write the .xml files into; created if absent
- `valuesJson`: JSON array of {field, value} - one NON-DEFAULT, distinct value per field, as the string AIXML will carry. Every field of the class must appear.
- `testMethodDirectory`: Where the test method .vi files will go, which is what the returned methodsJson names. Defaults to the folder holding testClassPath - a member VI belongs beside its class - so the answer is paste-ready. Pass it only for a layout that separates them.
- `timeoutSeconds`: Local budget in seconds, per placeholder (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_lvlib_reference`

```text
The grammar of a `.lvlib` and a `.lvclass`: the item types, where ACCESS SCOPE is
recorded and how it is inherited, and how a class names its parent. Read this before
deciding what is public - no RPC reports access scope, so the library file is the only
source, and the two scope properties behave differently: on a `.lvlib` the scope sits on
the FOLDER and its members inherit it, while a `.lvclass` records it per member.
Also covers the encoded parent-class record, which is where inheritance comes from on
LabVIEW versions that do not write plain-text Parent items.
Without arguments: a section list. With section: that section, or 'all'. A section value
that is not a heading is looked up as a TERM instead - and it may be a comma-separated
list, which costs one round trip and prints each passage once.
```

- `section`: Section number or title fragment; 'all' for everything; omit for the section list. A value that matches no heading is looked up as a term, and may be a comma-separated list of them

## `lvai_lvproj_reference`

```text
The grammar of a LabVIEW `.lvproj` file: item types, virtual versus auto-populating
folders, the `URL` forms, property scopes, build specifications, formatting rules and
recipes for generating one. Derived by census over a corpus of real project files, with
counts throughout so a rule can be told apart from a single observation.
Read this before parsing or writing a `.lvproj`. Two traps it documents: a project's
own library members are NOT listed in the file (the `.lvlib` owns them), and no RPC adds
a file to a project - `.lvproj` generation is on NI's unsupported list, so any project
edit is plain XML work outside the AIXML path.
Without arguments: a section list. With section: that section, or 'all'. A section value
that is not a heading is looked up as a TERM instead - and it may be a comma-separated
list, which costs one round trip and prints each passage once.
```

- `section`: Section number or title fragment; 'all' for everything; omit for the section list. A value that matches no heading is looked up as a term, and may be a comma-separated list of them

## `lvai_make_malleable`

```text
MUTATING: the whole create-a-MALLEABLE-VI route in one round trip - generate an ordinary
.vi from your AIXML, pylv_extract it, set the four LVSR flags a .vim needs, pylv_rebuild to
the .vim path, and read execState back.
USE THIS INSTEAD OF DOING THE FOUR STEPS BY HAND. They never vary, and lvai_generate_vi
REFUSES a .vim path outright because generating straight to one produces a VI that is eBad
while every cheap check stays green.
THE VERIFY IS THE POINT, not the saved turns. execState is the only cheap thing that sees a
broken malleable VI: validate, convert, lvai_connector_pane and both linters all pass one.
`ok` is false when the result is not eIdle.
IT ALSO GATES ON THE INTERMEDIATE .vi, and that gate earns its keep: a diagram that is
broken as an ordinary VI is not rescued by anything below, and failing at that step names
the real fault instead of reporting a malleable VI that will not load.
THE INTERMEDIATE .vi IS KEPT ON PURPOSE - next to the .vim, same base name, reported as
`intermediateViPath`. It is what you regenerate from, and it is the pane donor
lvai_placeholder_subvi needs when a caller has to reach this .vim from inside a project.
IT DOES NOT CLOSE THE PROJECT, and that is deliberate rather than an omission. A rebuild
onto a path LabVIEW still holds writes the file while LabVIEW keeps serving its in-memory
copy, so the verification confirms the VI you REPLACED - but closing a project SAVES
LabVIEW's copy over the .lvproj and is forbidden to an agent sharing one instance. So when
the target already existed, the answer says so in `staleReadRisk` and names the remedy
rather than taking it. On a fresh path there is no risk and nothing is reported.
docs/malleable-vis.md has the flags, the eight-arm bisect and the caller-side swap route.
```

- `aiXmlFilePath`: Absolute path to the source AIXML .xml file
- `viPath`: Absolute path of the .vim to create - WILL BE OVERWRITTEN
- `intermediateViPath`: Where to put the intermediate ordinary .vi. Defaults to the .vim path with a .vi extension, which keeps the pair together and is what you want almost always.
- `keepBundle`: Keep the pylabview bundle instead of deleting it, and report where. Off by default; worth turning on only when a patch step needs debugging. (default: false)
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_monitor_code_completion`

```text
RPC MonitorCodeCompletion (bidirectional). The most interesting hook: fires when the
user asks LabVIEW for AI code completion. The inbound item carries the user's prompt in
'request' plus a guid, and 'incomplete' when more is coming.
Answer with AIXML change descriptions - this is the same channel Nigel uses to write
code into the block diagram.
Reply shape (MonitorCodeCompletionRequest):
{"guid":"<echo>","suggestions":[{"changes":"<aixml>","rationale":"why"}],"errorCode":0}
Blocks until LabVIEW pushes an item or the timeout expires. A timeout is a normal
result, not an error: it means nobody triggered the feature in the IDE meanwhile.
Trigger it by hand in LabVIEW while this call is pending.
```

- `sendReady`: Send an empty request first to register the monitor (default: false)
- `replyJson`: Optional protobuf-JSON MonitorCodeCompletionRequest to answer with
- `maxMessages`: Max items to collect (default: 1)
- `timeoutSeconds`: How long to wait, in seconds (capped at 45 - see MaxToolWaitSeconds; use the CLI --watch mode for longer watches) (default: 45)

## `lvai_monitor_discuss_vi`

```text
RPC MonitorDiscussVI (bidirectional). Fires when the user invokes "Discuss VI" in
LabVIEW; the inbound item names the VI or project the user wants to talk about.
Reply shape (replyJson, MonitorDiscussVIRequest): {"errorCode":0,"errorMessage":""}
EXPECT THIS ONE TO TIME OUT EVEN WHEN THE FEATURE FIRES. The monitors are
single-subscriber streams and NI's own service always wins - measured, including with
the service stopped first, where the click starts it and the event still goes there.
lvai_discuss_file TRIGGERS the feature, but it does not make the event arrive here:
emitting it and receiving it are separate questions, and only the first one is open.
Blocks until LabVIEW pushes an item or the timeout expires. A timeout is a normal
result, not an error: it means nobody triggered the feature in the IDE meanwhile.
Trigger it by hand in LabVIEW while this call is pending.
```

- `sendReady`: Send an empty request first to register the monitor (default: false)
- `replyJson`: Optional protobuf-JSON MonitorDiscussVIRequest to answer with
- `maxMessages`: Max items to collect (default: 1)
- `timeoutSeconds`: How long to wait, in seconds (capped at 45 - see MaxToolWaitSeconds; use the CLI --watch mode for longer watches) (default: 45)

## `lvai_monitor_example_searches`

```text
RPC MonitorExampleSearches (bidirectional). Fires when the user runs LabVIEW's AI
example search; the inbound item carries searchString + guid.
Reply shape (MonitorExampleSearchesRequest):
{"examples":[{"examplePath":"...","rationale":"why"}],"guid":"<echo>","errorCode":0}
Blocks until LabVIEW pushes an item or the timeout expires. A timeout is a normal
result, not an error: it means nobody triggered the feature in the IDE meanwhile.
Trigger it by hand in LabVIEW while this call is pending.
```

- `sendReady`: Send an empty request first to register the monitor (default: false)
- `replyJson`: Optional protobuf-JSON MonitorExampleSearchesRequest to answer with
- `maxMessages`: Max items to collect (default: 1)
- `timeoutSeconds`: How long to wait, in seconds (capped at 45 - see MaxToolWaitSeconds; use the CLI --watch mode for longer watches) (default: 45)

## `lvai_monitor_front_panel_cleanup`

```text
RPC MonitorFrontPanelCleanup (bidirectional). Fires when the user asks LabVIEW to tidy
a front panel. The inbound item carries panel state in 'request' plus a guid.
Reply shape (MonitorFrontPanelCleanupRequest):
{"guid":"<echo>","panelInfo":"<layout>","errorCode":0}
Blocks until LabVIEW pushes an item or the timeout expires. A timeout is a normal
result, not an error: it means nobody triggered the feature in the IDE meanwhile.
Trigger it by hand in LabVIEW while this call is pending.
```

- `sendReady`: Send an empty request first to register the monitor (default: false)
- `replyJson`: Optional protobuf-JSON MonitorFrontPanelCleanupRequest to answer with
- `maxMessages`: Max items to collect (default: 1)
- `timeoutSeconds`: How long to wait, in seconds (capped at 45 - see MaxToolWaitSeconds; use the CLI --watch mode for longer watches) (default: 45)

## `lvai_monitor_palette_searches`

```text
RPC MonitorPaletteSearches (bidirectional). Fires when the user runs LabVIEW's AI
palette search; the inbound item carries searchString + guid. Answer with the palette
items you consider relevant, echoing the SAME guid.
Reply shape (MonitorPaletteSearchesRequest):
{"items":[{"itemGuid":"...","rationale":"why"}],"guid":"<echo>","errorCode":0}
Blocks until LabVIEW pushes an item or the timeout expires. A timeout is a normal
result, not an error: it means nobody triggered the feature in the IDE meanwhile.
Trigger it by hand in LabVIEW while this call is pending.
```

- `sendReady`: Send an empty request first to register the monitor (default: false)
- `replyJson`: Optional protobuf-JSON MonitorPaletteSearchesRequest to answer with
- `maxMessages`: Max items to collect (default: 1)
- `timeoutSeconds`: How long to wait, in seconds (capped at 45 - see MaxToolWaitSeconds; use the CLI --watch mode for longer watches) (default: 45)

## `lvai_monitor_project_changes`

```text
RPC MonitorProjectChanges (server streaming). Emits an event whenever a project item is
added, modified or removed. The only monitor that is a plain one-way stream.
Blocks until LabVIEW pushes an item or the timeout expires. A timeout is a normal
result, not an error: it means nobody triggered the feature in the IDE meanwhile.
Trigger it by hand in LabVIEW while this call is pending.
```

- `maxMessages`: Max events to collect before returning (default: 5)
- `timeoutSeconds`: How long to wait, in seconds (capped at 45 - see MaxToolWaitSeconds; use the CLI --watch mode for longer watches) (default: 45)

## `lvai_move_to_folder`

```text
MUTATING: moves members of ONE .lvclass or plain .lvlib into a virtual folder of that
owner, creating the folder at the owner's root when it is missing and createFolder is true.
A virtual folder exists only in the owner's XML, so NO FILE MOVES, no qualified name changes
and nothing relinks - only the owner file is saved.
Route: LVClass.Open / Library.Open in the ACTIVE PROJECT's instance, {LV.Library} AddItem
(Type Folder) when needed, Get All Descendents, {LV.ProjectItem} Move per item, then Save of
the owner only. Measured on every member of 15 classes (a field run, 2026-10-11/12); the
.lvlib arm is the unmeasured twin.
NEEDS A PROJECT OPEN AND ACTIVE (Error 1055 otherwise, errorKind noActiveProject). Pass
projectPath and this call opens it.
REFUSED BEFORE LABVIEW, from the owner file: a name that is not a member (`members` lists
what is there), the class private data control, a Property Definition, an accessor INSIDE a
Property Definition, a folder, items of different types in one call (one per call: VI, or
LVClass in a library), a folder name that occurs twice, a missing folder with
createFolder=false, and a folder PATH ("A/B") - the helper finds a folder by name and
creates one at the root, so nested paths are not supported. An item already in the folder
is skipped and named under `alreadyThere`.
ALL OR NOTHING: if LabVIEW does not list one of the items, or the owner already carries
unsaved changes, nothing is moved (itemsNotFoundByLabview / ownerHasUnsavedChanges).
VERIFIED FROM THE SAVED FILE: each item's parent in the re-read XML must be the folder,
every member listed before must still be listed, and `otherFilesChanged` must be empty.
```

- `ownerPath`: Absolute path to the .lvclass or .lvlib
- `folder`: Virtual folder name, e.g. "Accessors". A single name; nested paths are refused
- `itemNames`: JSON array of member names as the owner lists them, e.g. ["Read Name.vi","Set Name.vi"]. Names are matched exactly first, then ignoring case.
- `createFolder`: Create the folder at the owner's root when it is not there (default: true)
- `projectPath`: The .lvproj to open (and make active) first; it is not edited
- `checkUnsaved`: Refuse when the target (or, for lvai_add_to_class, any VI in the project's instance) already carries UNSAVED changes, because this call's save would write them too. Default true - keep it for any EXISTING project, where someone may have VIs open with edits of their own. Pass false only in a project this session is building itself: the save then writes whatever is unsaved, which is what a build wants. The user's rule of 2026-10-17. (default: true)
- `timeoutSeconds`: Local budget in seconds (default: 120)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_open_file`

```text
RPC OpenFile. MUTATING (IDE state): opens a VI and/or a project in the running LabVIEW
editor. Pass the VI pair, the project pair, or both. Harmless but visible to whoever is
sitting in front of LabVIEW.
THERE IS NO `path` OR `filePath` PARAMETER, and getting that wrong is expensive because the
failure lies: the argument is DROPPED, all four fields reach LabVIEW empty, and LabVIEW
answers `Error 7, File not found` about a file nobody named. Measured 2026-08-27 and again
2026-09-14 - five identical Error 7 answers across three paths that plainly exist, a
refuted foreground hypothesis and a pointless LabVIEW restart, while lvai_describe_project
read the very same path with errorCode 0.
THIS TEXT USED TO SAY `filePath` IS "folded onto the closest declared one" AND THAT IS FALSE.
The fold normalises `_`, `-` and case only, so `vi_path` reaches `viPath` and `filePath`
reaches nothing. Believing the old sentence sends you hunting for a path that was passed as
a VI, which is the one thing that did not happen. Both holes are closed now - an unrecognised
argument name is REFUSED by name, and a call with neither path is refused here - so this is
history rather than a live trap.
A .lvproj goes in `projectPath` WITH `projectName`; that pair returns No Error immediately.
`No Error` DOES NOT MEAN A PROJECT BECAME ACTIVE, and that is not a quibble - almost
everything that edits a class needs the project ACTIVE, not merely open. Measured
2026-09-03: three opens in a row answered `No Error` and left no active project, so every
following call answered `Error 1055`; what fixed it was giving the LabVIEW WINDOW the
foreground, because Chrome had focus. Diagnosing that cost 270 s of wall clock for 2.9 s
inside LabVIEW. So a project open now reads `Project:Active Project` back and reports
`projectBecameActive`, with `errorKind: projectDidNotBecomeActive` and the cause named when
it did not. Pass `checkActive: false` to skip the check, which costs one short helper run.
THE FOREGROUND IS THE CAUSE OF ONE FAILURE, NOT OF EVERY ONE - read `errorCode` first.
`Error 1025, Application Reference is invalid` means the IDE's application reference has
gone invalid while the ADDON's instance keeps working, so a `1154` from a flatten, a bind
or a retype in the same session is the same fault and re-running it cannot help; measured
2026-09-18, with `lvai_status` green throughout. The `hint` used to open "The open itself
reported no error" whatever the code said, because it was keyed on `projectBecameActive`
alone.
```

- `viPath`: Absolute path to the .vi, or empty
- `viName`: VI name, or empty
- `projectPath`: Absolute path to the .lvproj, or empty
- `projectName`: Project name, or empty
- `checkActive`: After opening a PROJECT, read Project:Active Project back and report whether one actually became active. On by default: `No Error` alone has been measured leaving no active project, and every class-editing call then fails with Error 1055 pointing nowhere useful. Ignored when no projectPath is given. (default: true)
- `timeoutSeconds`: Local budget in seconds (default: 120)
- `viPaths`: MORE VIs to open in the same call, ONE ABSOLUTE PATH PER LINE - each through the same project pair when one is given, which is what makes project-local code resolvable as an AIXML Call target. The first one stands in for viPath when that is empty. Added 2026-09-25: loading five callees for a direct Call cost five round trips in the third ATM cold build. `alsoOpened` reports each one's own error code.
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview everything goes over LabVIEW's ActiveX server: a project with a composed helper (Project.Open plus its window), checked for being ACTIVE the same way; a VI with no project has its front panel opened in LabVIEW's main instance, and with a project it is opened THROUGH it (lvai_open_vi_in_project). LabVIEW must already be running.

## `lvai_palette_index`

```text
The VIs reachable from this LabVIEW installation's palettes, read from its .mnu files.
Use this to FIND a VI to Call - it is the searchable catalogue of what this station has.
THIS LIST IS NOT THE SET OF LEGAL TARGETS, and this description claimed it was until
2026-08-27. Generation resolves a target BY NAME against what the installation can find, so
things absent from here resolve too: a library member with its qualifier
(`Caraya.lvlib:VI Name.vi`, in no palette), and a loose VI in a plain folder under vi.lib or
user.lib by its bare name - which is what lets a GENERATED placeholder dropped into
user.lib be callable with no .lvlib, no .mnu and no restart. What does NOT resolve: a VI
inside an .llb by bare name, a path in any spelling, and project-local code, loose or in a
project library. Full table in lvai_aixml_reference, section 9.
A MISS HERE IS NOT PROOF THAT A CALL IS ILLEGAL - settle a target with a throwaway
ValidateAIXML instead.
THE INDEX USED TO MISS WHOLE TOOLKITS AND EVERY INSTRUMENT DRIVER, and that is fixed as of
2026-09-07. It scanned only `menus` folders, so a .mnu anywhere else was invisible: a query
for "Caraya" answered "no match" for VIs that validate and run, because its 18 palette
files live under vi.lib\addons\_JKI Toolkits\dynamic_palette\. Swept the whole
installation: 157 .mnu files sit outside every `menus` folder, and reading them took the
index from 582 palette files to 743 and from 2835 VIs to 3335 - 500 more, 15%.

  vi.lib     404 new   Caraya, VI Tester, JSONtext, Wovalab, plus NI's own 3D Picture
                       Control, SFTP and SSH
  instr.lib   71 new   INSTRUMENT DRIVERS - and this station has one real driver and nine
                       _Template skeletons, so a real test rig yields far more
  user.lib    25 new   OpenG, and whatever the user installed
  Targets      0 new   its FPGA palettes carry .ctl controls, which this index filters out

Entries from those trees are labelled `vi.lib: <path>`, `instr.lib: <path>` and so on.
A HIT IS THE VI, NOT NECESSARILY THE TARGET STRING. A palette VI owned by a library needs
its `lvlib:` qualifier and is refused by bare name - MEASURED: `Draw Image from
File__ogtk.vi` gives "Unsupported SubVI" where `openg_picture.lvlib:Draw Image from
File__ogtk.vi` validates and runs. A .mnu stores only the bare name, so this tool cannot
print the qualifier and it is not derivable from the palette path either. Get it by
exporting a VI that already calls the target, or settle it in one throwaway ValidateAIXML
with both spellings as two Calls - an unresolvable target is named in the message, a
resolved one only complains about unwired terminals.
Scanned from disk at call time because the palette is station-specific - installed
toolkits and add-ons hook into it - and cached for the process lifetime. THREE kinds of
location are read: LabVIEW's own menus folder; every LVAddon under
%ProgramFiles%\NI\LVAddons, which is where drivers such as NI-DAQmx now install; and the
trees vi.lib, user.lib, instr.lib and Targets, swept whole for the .mnu files that sit
outside any menus folder. An entry from anything but the IDE's own menus is labelled with
the tree it came from. The one .mnu deliberately left out is
resource\plugins\PopupMenus\...\Class Methods Shortcut Palette.mnu - a right-click menu,
not a palette of callable VIs.
Without a query: the totals plus the scan location. With query: matching VI names and the
palette each was found in; EVERY word of the query must appear, in the name or in the
palette path. Note BUILT-IN FUNCTIONS ARE NOT LISTED: a palette entry for a
primitive carries only its display label, which is not the AIXML node name (the palette
says "To XML" where AIXML wants "Flatten To XML"), and a Call is the wrong construct for
one anyway - primitives are `Node` elements.
```

- `query`: Words to look for, e.g. 'PNG', 'Error Handler', 'read spreadsheet'. EVERY word must appear, but each may come from either the VI name or its palette path - so 'file read' reaches file.mnu. Fewer words match more
- `limit`: Max rows to return (default 40, max 400) (default: 40)
- `refresh`: Rescan instead of using the cached index (default: false)
- `installRoot`: Override the LabVIEW installation folder; omit to use the newest installed
- `addonsRoot`: Override the LVAddons folder; omit to discover it under Program Files

## `lvai_placeholder_subvi`

```text
MUTATING: makes sure a placeholder subVI exists whose connector pane is an exact clone of
viPath's, generating it into <LabVIEW>\user.lib\LV_MCP\ if it is not already there, and
answers with the bare name to use as a `Call` target plus the ready-to-paste Call element.
THIS IS HOW A GENERATED VI COMES TO CALL YOUR OWN CODE. AIXML refuses a Call to
project-local code (Error 53) and pylabview cannot create a node, so: author the new VI
with a Call to this placeholder, generate it, then repoint that call with
pylv_apply {"op":"retarget","from":"<placeholder>","to":"<your VI>","path":"..."}.
The placeholder is never executed - it is a socket, not a function.
IT WRITES INTO THE LABVIEW INSTALLATION, one folder, and says so in `installed`. Nothing
else on the station is touched and no .lvlib, .mnu, palette entry or restart is involved -
a loose VI in a plain folder under user.lib resolves by bare name, measured. Uninstall by
deleting the folder. If the folder cannot be written the answer says so rather than
failing obscurely; the station may need the directory created by hand once.
CACHED PER TARGET: the stub name is a hash of the SUBJECT's identity - its qualified name, or
its full path when it has none - plus the terminal names, types, conIdx and connection. So a
second call for the same VI with an unchanged pane writes nothing, and two VIs NEVER share a
stub. They did until 2026-10-11, keyed on the pane alone: two Actor Framework methods with
identical panes, or methods of different classes (an export writes every class as bare
`ref{UDClassInst}`), got ONE socket, and lvai_swap_subvis could not tell their nodes apart.
Stubs cached under the old key are left where they are. Pass refresh to regenerate anyway.
A CLASS INSIDE A CLUSTER keeps its cluster: `cluster{ref{UDClassInst}.hardware,uint32.n}`
becomes `cluster{path.hardware,uint32.n}` - only the element is the stand-in - where it
used to collapse the whole terminal to `path`. Named under `classElementsInClusters`.
THE CLONE IS EXACT ON PURPOSE. A generic Variant placeholder does NOT work: the pane's type
descriptor is part of the link binding, and retargeting a Variant stub onto a double VI
gives `Error 7, Bad Linkage`. Measured on a controlled pair.
A POLYMORPHIC viPath is refused - it has no pane of its own to clone. Point this at the
instance you mean to call.
```

- `viPath`: Absolute path to the VI whose connector pane the placeholder must match. OPTIONAL only in the sense that `viPaths` replaces it - one of the two must be given, and that is checked here rather than by the schema. It was declared REQUIRED until 2026-09-03, which made the documented batch call impossible: passing `viPaths` alone was refused by the CLIENT with `MCP error -32602 ... expected string, received undefined`, before the server saw it, so this tool's own "viPath is ignored" was unreachable text.
- `refresh`: Regenerate the stub even when one for this signature already exists (default: false)
- `viPaths`: SEVERAL VIs AT ONCE, one absolute path per line, plain text and NOT JSON. Given this, `viPath` is ignored and the answer carries one entry per VI under `placeholders`. THE SAVING IS ROUND TRIPS, NOT LABVIEW TIME - LabVIEW serialises the export either way, so this is the same trade `lvai_generate_vis` makes. Measured 2026-09-03 on the socket route over one class: nine placeholders cost 25 s of wall clock against 2.5 s inside LabVIEW, because they went out as four separate calls and a round trip is a model turn.
- `flattenTypedefs`: After the stub exists, give its typedef terminals the SUBJECT'S REAL TYPES with every typedef link removed at every depth - lvai_flatten_typedefs, run for you. A no-op when the pane carries no typedef, so leaving it on costs nothing on most panes. WHY IT IS NOT FREE WHERE IT APPLIES: it copies each `.ctl` of the chain into user.lib\LV_MCP, clears its TypeDefVI flag with pylabview, and installs it with {LV.Control} Replace - one LabVIEW round trip per level. Turn it off to get the old behaviour, where the stub keeps AIXML's approximation of the type. IT CANNOT FAIL THE PLACEHOLDER. A stub whose types could not be improved is still a usable stub, so the outcome is reported under `typedefFlatten` and `ok` above stays about the placeholder itself - but a failure is ALSO named at the top level, in `typedefFlattenWarning`, because a green `ok` over a stub that kept an approximation is exactly the answer nobody reads far enough into. (default: true)
- `typedefRoute`: Which route the flatten uses, "flag" (the default) or "disconnect" - see lvai_flatten_typedefs. "flag" needs nothing of you. "disconnect" is faster and copies no `.ctl`, but REQUIRES a project open and active, opens this stub in the editor, and kills LabVIEW if either precondition is missing. (default: "flag")
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_read_icons`

```text
Reads the CURRENT icons of VIs, classes and plain libraries out to 32x32 PNG files in one
call, without changing them - and with expectedDir compares each one pixel by pixel with an
expected PNG. This is the verification step after any icon work: a regeneration, an
lvai_set_vi_icon, an lvai_set_owner_icon, or a "before" copy to compare against later.
WHY A TOOL: LabVIEW's Save VI Icon to File writes NOTHING into a missing folder or past
MAX_PATH and still answers code 0 - measured in a pilot project, 146 codes 0 for 145 files.
So every output folder is created first, every PNG path over 259 characters is refused before
LabVIEW, and each PNG is checked to exist and to have been written by this call. `written`
is the verdict, not the code.
PIXELS, NOT BYTES: LabVIEW writes a palette PNG, design tools write truecolour, so equal
images differ in every byte. The compare decodes both (status OK / DIFF / SIZE / NO
REFERENCE / AMBIGUOUS / ERROR, with the first differing pixels).
Layout: a VI goes to <outDir>\<its path relative to baseDir, .vi -> .png>; a class or
library to <outDir>\<its relative path>.png (Foo.lvclass.png). The expected PNG is looked up
at the same relative path under expectedDir, else as the one PNG of that file name anywhere
under it. The project must be CLOSED (checked first). VIs go through LabVIEW 100 per call.
Measured in a pilot project on LabVIEW 2026 Q3 (the batch helper at 146 VIs, the class and
library helpers 2026-10-08); the compare is a port of the measured Python script.
```

- `paths`: What to read, ONE ABSOLUTE PATH PER LINE: .vi files, .lvclass and .lvlib files, or folders - a folder adds every .vi under it, recursively. A path containing ';' is refused: the helper's list format is ';'-separated.
- `outDir`: Output folder for the PNGs. Created if missing. KEEP IT SHORT - a PNG path over 259 characters is refused, because LabVIEW would silently write nothing there.
- `expectedDir`: Optional folder of expected PNGs laid out like outDir (e.g. a generator's output, or an earlier read-back). Each icon read is compared with its expected PNG pixel by pixel.
- `baseDir`: The folder the relative output paths are taken from. Defaults to the deepest folder holding every path given. Pass the same base the expected tree was made from.
- `tolerance`: Per-channel difference still counted as equal in the compare (default 0) (default: 0)
- `ignoreAlpha`: Compare RGB only, ignoring alpha (default: false)
- `refreshOwnerIcons`: Read every class and library icon through LabVIEW again instead of from the owner-icon cache. The cache is keyed on the owner file's own NI.Lib.Icon line, so a changed icon is never served stale; this is for doubting the cache itself. (default: false)
- `helperDirectory`: Where to keep the generated helper VIs. Defaults to %TEMP%\LabVIEWMCP\helpers
- `regenerateHelpers`: Regenerate the helper VIs even when they are current (default: false)
- `timeoutSeconds`: Local budget in seconds, per LabVIEW call (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_render_diagrams`

```text
Renders the BLOCK DIAGRAM of one or more VIs to PNG through LabVIEW's own
Print.VI To HTML, and reports the image files per VI so you know which ones to read.
SEVERAL VIs IN ONE CALL - one absolute path per line in viPaths. LabVIEW serialises the
work either way, so what this saves is round trips, which is the only thing that costs
real time: measured 2026-09-08, one agent run spent 1000 s of wall clock against 120 s
inside its tools, so a call is worth about 9.8 s and doing three VIs by hand cost ten.
USE IT AFTER PLACING DIAGRAM COMMENTS, and LOOK at what comes back. Nothing else in this
interface can see a comment that is clipped, sitting on a Case selector, or off the
visible diagram - all three were measured passing validation, rebuild, export and a run
on the same day, and only the picture disagreed.
THE IMAGE DIRECTORY IS CREATED FOR YOU. LabVIEW does not create it and answers Error 118
instead, which reads like a rendering failure and is a missing folder.
DO NOT judge this by errorCode: RunVIAsTopLevel cannot read the helper's indicators back,
so a successful render can still report 91. The verdict is `rendered` per VI, taken from
the PNG files that actually appeared during the call.
Each VI gets `diagrams`: the top-level diagram first, then one entry per Case frame
(LabVIEW names them <base>d.png and <base>d1..dN.png). A VI whose diagram has no Case
structure yields exactly one.
IT LOADS EACH VI INTO LABVIEW, which is the one side effect worth knowing: do any
pylabview edit (pylv_apply) BEFORE rendering, not after, or the rebuild writes the file
while LabVIEW keeps serving the copy it just loaded.
```

- `viPaths`: The VIs to render, ONE ABSOLUTE PATH PER LINE, plain text and not JSON. A single path is fine.
- `outputDirectory`: Directory for the HTML and PNG output. CREATED IF MISSING, which is the point - LabVIEW's own writer does not create it and fails with Error 118. Defaults to a per-user temp directory.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user cache directory, because the scripts folder next to the exe may be read-only. Generated once and reused; pass regenerateHelper to force a rebuild.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvdoc_print.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds, per VI (default: 300)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview each VI is printed by LabVIEW's own Print.VI To HTML over ActiveX - no helper VI and no NI service - so the helper arguments are not used, and LabVIEW must already be running.

## `lvai_resave_ctl`

```text
MUTATING: opens a .ctl in the IDE's own application instance and saves it in place with
{LV.VI} Save.Instrument, the path left UNWIRED, which is how LabVIEW writes a control back
over itself.
WHY IT EXISTS: a .ctl built the fixture way - generate a VI to a .ctl path, then patch
<Instrument Type> and TypeDefVI in the pylabview bundle - has never been written by LabVIEW
and still carries that VI's CONNECTOR PANE. lvai_describe_ctl shows it as
`wrappedType: "Function"` with 16 fields and flags it as `needsLabviewSave`. Everything that
only needs the TYPE reads straight through it, and NI's ACCESSOR WIZARD DOES NOT:
lvai_create_accessors answers Error 1061 at CreateControlFromReference.vi for the field it
is bound to, on the Read side and the Write side alike, which is a hard stop in the middle
of a class build with nothing naming the cause.
PRECONDITION: a project must be OPEN and ACTIVE - the helper reaches the file through
Project:Active Project. Pass projectPath to have it opened here.
THE VERDICT IS `wrappedTypeAfter`, re-read from the saved file with pylabview: it must be
`TypeDef`. `errorCode 0` on its own says only that LabVIEW answered.
This is a no-op on a .ctl LabVIEW has already written, which is why it is safe to run
whenever lvai_describe_ctl asks for it.
```

- `ctlPath`: Absolute path to the .ctl to rewrite in place
- `projectPath`: The .lvproj to open first. Without an active project the helper answers Error 1055. Omit only when you have opened one yourself.
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to the scripts folder's copy
- `regenerateHelper`: Regenerate the helper VI even when it exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_run_caraya_tests`

```text
MUTATING: EXECUTES a Caraya suite runner (the VI lvai_generate_caraya_test_runner writes) and
answers from the JUnit report it writes: per suite the test and failure counts, and every
failing case NAMED with its suite and the test VI it lives in. `ok` is true only when the
report was written BY THIS RUN, carries at least one test, and has no failure or error.
DO NOT READ THE RUNNER'S `error out` FOR THE VERDICT - it is Caraya's single error for the
whole run, 7002 when something failed, and its SOURCE is not the failing VI: measured
2026-09-25, it named `Test Channel.vi` while the failing case was in
`Test Channel Defaults.vi`. It is reported under `runnerErrorOut` for completeness, with
`sourceNamesAFailingSuite` saying whether it points at the right place.
A Caraya failure body is the literal string "FAIL", so the case LABEL is what identifies it -
which is why the generators label every case.
The report is found by itself: `reportPath` if given, else the `*.xml` beside the runner that
this run wrote. A report older than the run is never used; `reportFresh: false` says so.
```

- `runnerViPath`: Absolute path to the Caraya runner .vi
- `reportPath`: The JUnit report the runner writes. Omit to take the .xml beside the runner that this run wrote - the runner generator's default is '<runner>-TestReport.xml'.
- `timeoutSeconds`: Local budget in seconds - a suite runs every test VI in it (default: 600)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_run_lunit_tests`

```text
MUTATING (it EXECUTES the code under test): runs an LUnit suite through LUnit's own
execution API and returns the report already parsed - per test case, with the failure
message for each one that failed.
testPath takes a .lvclass, a .lvlib or a .lvproj. A class runs just that test case and is
MUCH faster: measured 3.2 s against 12.2 s for the .lvproj that contained it, finding the
same tests. Point it at the narrowest thing you want run.
THE TRAP THIS CLOSES: LUnit does NOT overwrite an existing report - it writes a numbered
sibling, `report (1).xml`. So reading back the path you passed returns the PREVIOUS run's
numbers, with no error anywhere. This deletes the target and its numbered siblings first
(freshReport), and if a sibling still appears afterwards it says so instead of reporting
stale figures.
A SUITE THAT RAN NOTHING IS NOT A GREEN SUITE, AND `allPassed` ALONE CANNOT TELL YOU.
LUnit answers `All Passed?` true when it found no test methods, and `failures == 0` is
vacuously true for an empty report, so the two figures a caller naturally reads both say
pass. CHECK `tests`, or branch on `foundNoTests`; `ok` is false in that case since
2026-09-08. It is a real state and not a hypothetical: a test method that is listed in the
.lvclass but no longer carries the owning-library link inside the .vi is invisible to LUnit,
while lvai_describe_class still reports it as a public member.
READ `allPassed` AND `failures`, never the error cluster: an error cluster carries the first
failure only, so a partial run and a single failing assertion are indistinguishable in it.
`cases` carries one entry per test method with its status, assertion count and, for a
failure, LUnit's own message - which for Pass If Equal.vim already contains Expected and
Actual, so there is nothing to look up.
AN ALL-GREEN RUN PROVES NOTHING ON ITS OWN. Break one thing on purpose - repointing a read
accessor with lvai_swap_subvis is the cheapest way and needs no regeneration - confirm the
report names that case, then restore.
A .xml report path produces JUnit XML, which is what this parses; a .txt path produces plain
text and comes back unparsed under `output`.
```

- `testPath`: Absolute path to the .lvclass, .lvlib or .lvproj holding the tests
- `reportPath`: Where to write the report. Defaults to a fresh timestamp-free path beside the test target. A .xml extension gives JUnit XML and is parsed; .txt gives plain text.
- `parallel`: Run test cases in parallel threads - faster, but reorders the report (default: false)
- `freshReport`: Delete the report path and its numbered siblings first (default: true)
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to lvlu_run_tests.xml in scriptsDirectory
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_run_vi_and_read_values`

```text
MUTATING: EXECUTES the VI, with whatever side effects it has.
Use this instead of lvai_run_vi_as_top_level whenever the VI has ANY output that is not
a string - boolean, numeric, cluster, array, waveform. That tool returns those empty
with errorCode 91 after the VI has run; this one returns their real values, because the
helper reads them through VI Server and flattens them to XML before they cross back.
Values are returned per control name with a type and a native JSON value.
INPUTS ARE STILL WRITTEN AS TEXT, BUT THEY ARE NO LONGER LIMITED TO STRING CONTROLS.
The helper reads the target's panel, asks each named control what it is, and converts
before setting - so a path, a double, an integer and a boolean can all be set by passing
their text ("C:\\data\\in.csv", "12.5", "42", "true"). Measured 2026-09-16 on one control
of each type.
AN ENUM OR A TEXT RING takes an item NAME ("withdraw", exact and case-sensitive) or an
index ("2"). Text that is neither is refused by Ctrl Val.Set BEFORE the run - it used to
be the case that nothing but a string could be set at all.
VALUES COME BACK AS NATIVE JSON under `value` since 2026-10-30 - a cluster as an object, an
array as a list, an enum as its item name - and the flattened XML is left out unless
includeXml is true; a timestamp, waveform, variant or refnum keeps its `xml`.
AN ARRAY OR A CLUSTER takes LabVIEW's own XML for the value - what this tool returns
under `xml` with includeXml: true, so a value read back can be pasted in as the
next call's input. A bare <Array> or <Cluster> is wrapped in <LvVariant> for you, and
the indentation between its tags is folded away; the helper then turns it into a Variant
that carries its own type, so you never name the type. Since 2026-09-25. Do not write a
line break as &#10; there - Unflatten From XML decodes no character reference, measured.
AN EMPTY ARRAY NEEDS ONE ELEMENT AS A TYPE TEMPLATE, as LabVIEW writes it: Dimsize 0 plus
e.g. <String><Name></Name><Val></Val></String>. Without it the helper cannot tell the type
(Error 1103), so it is refused here (emptyArrayWithoutTemplate). Measured 2026-10-01.
A LINE BREAK IN A VALUE IS FINE - a multi-line string, or an XML value with a multi-line
member: it travels encoded (LF as 0x1E, CR as 0x1D) and the helper decodes it before it
converts anything; `lineBreaksEncoded` names the inputs it applied to. Only the default
helper decodes, so with runForMs or the legacy helper a line break is still refused. A
line break in a control NAME is always refused - names and values are paired by line.
A control name that matches nothing on the target's panel is Error 1055 and the target
does NOT run - watch helperFailed, because errorCode is RunVIAsTopLevel's and reads 0.
Reading is done through a VI REFERENCE, which is released afterwards, so this does not
burn the target's path for a later lvai_convert_aixml_to_vi.
A VI INSIDE AN `.llb` CONTAINER FILE runs by its path THROUGH the `.llb`, as LabVIEW links
it (`...\JKI_EasyXML.llb\Escape-Unescape String for XML__JKI EasyXML.vi`), on both
backends since 2026-10-15; until then the existence check refused it.
```

- `viPath`: Absolute path to the .vi to run
- `inputsJson`: Control values as a JSON object, e.g. {"file name":"C:\\data\\in.csv","count":"42"}. Keys are control labels, values are always TEXT - the helper converts each one to whatever type the named control actually is. String, path, numeric, boolean, enum and ring controls take plain text; an array or cluster control takes its value as LabVIEW XML (<Array>…</Array>, <Cluster>…</Cluster>, or wrapped in <LvVariant>). Omit for a VI that needs no inputs.
- `includeRawXml`: Also return the helper's raw flattened XML under valuesXml. Off by default because it repeats everything in values; it is returned regardless when parsing yields nothing, so a parse failure never costs you the data. (default: false)
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user cache directory, because the scripts folder next to the exe may be read-only. Generated once and reused; pass regenerateHelper to force a rebuild.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvai_run_and_read.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `timeoutSeconds`: Local budget in seconds - raise it for long-running VIs (default: 300)
- `runForMs`: Run the VI for this many milliseconds, read its front panel, then ABORT it - instead of waiting for it to finish. Zero, the default, keeps the original behaviour. THIS IS THE ONLY WAY TO LOOK AT A VI THAT NEVER ENDS, which is every event or polling loop and therefore every real application. Without it such a VI times out the call and keeps running, and the caller has to generate a scriptable copy of their own program to test it at all. TWO THINGS TO KNOW. The values are a SNAPSHOT at the moment the time elapsed, not a result: a loop that has not reached its second state yet shows the first. And Abort is not a stop button - it kills the VI where it stands and runs NO cleanup the diagram may contain, so a VI that closes files or releases hardware on its normal path does not do that here. IT ALSO ANSWERS `disabled`: every control's label with its Disabled state at the snapshot (0 enabled, 1 disabled, 2 disabled and greyed out) - the one thing a snapshot of values cannot show, and nothing can look afterwards because the VI leaves memory. Since 2026-09-26; null when that read failed. IT PICKS ITS OWN HELPER, and a helperAixmlPath naming the untimed one is corrected rather than obeyed - the answer says so in `helperOverridden`. Only lvai_run_for_ms.vi has a `run for ms` control; lvai_run_and_read.vi waits for the target to finish, so aiming this at it while asking for runForMs waits for a VI that never ends and BLOCKS THE WHOLE SERVICE until LabVIEW is killed. Measured 2026-09-16; it cost two restarts. (default: 0)
- `signalsJson`: EVENTS TO FIRE while the VI runs, with runForMs only: a JSON ARRAY, in order, e.g. [{"control":"Card Simulator","value":"true"},{"control":"User Input","value":"23456"}]. After runForMs has elapsed each control is written with Value (Signaling) - which fires its Value Change event exactly as a click or a keystroke would - and signalGapMs is waited after each one; the snapshot comes after the last. Values are text: a boolean TRUE/1, a numeric its number, a string its text, a cluster or array LabVIEW XML. THIS IS WHAT A START-UP SNAPSHOT CANNOT SEE. The seventh ATM build passed validation, execState, 58 unit tests and a runForMs snapshot while its consumer read User Input before the user had typed; only driving an event would have shown it. A LATCHED BOOLEAN CANNOT BE SIGNALLED - LabVIEW refuses Value (Signaling) on one with Error 1193, reported under `signals`. The snapshot is taken either way.
- `signalGapMs`: Wait after each signal, so the target handles it before the next (default: 500)
- `closePanelAfterRun`: Close the target's front panel afterwards when the RUN left it open - a program that opens its own panel (FP.Open, as a Web Browser control needs) keeps the VI in memory otherwise, and the next generation onto its path answers Error 1357. Measured 2026-10-02. Only a panel open in the instance the run used is touched. (default: true)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview the VI is run over LabVIEW's ActiveX server instead of NI's service: no helper VI, the panel's names and types read from the file by pylabview, inputs and values in AIXML literals ("3.5", "[1,2,3]", "[false,0,]"), runForMs honoured. signalsJson runs the timed helper COMPOSED by our composer, over ActiveX, against the target whose typed inputs were set first - Value (Signaling) needs VI Server, which ActiveX lacks. LabVIEW must already be running - this never starts it.
- `includeXml`: Also return each value's flattened LabVIEW XML under `xml`. Off by default: every value whose type is decoded comes back as native JSON under `value` instead - a cluster as an object, an array as a list, a boolean as true/false, an enum as its item name - which is a fraction of the XML. Turn it on to paste a read value back in as an array or cluster INPUT, which takes LabVIEW's XML. A value that is not decoded (a timestamp, waveform, variant, refnum or complex number) keeps its `xml` either way. (default: false)

## `lvai_run_vi_as_top_level`

```text
RPC RunVIAsTopLevel. MUTATING: actually EXECUTES the VI in LabVIEW with the given
control values and returns its indicator values. Side effects are whatever the VI does
- it can drive hardware, write files or move a stage.
inputs/outputs are string maps, and the values really do cross as STRINGS: measured,
LabVIEW does NOT coerce them to the control's type. String controls work; a numeric or
path control fails with errorCode 91 at Control Value:Set BEFORE the VI runs - as "42"
and as 42 alike. So take numbers and paths in as strings and convert them on the
diagram. On the way out an array or cluster indicator also fails to marshal, and there
errorCode 91 arrives AFTER the VI has run correctly - it is not proof of failure.
Details and the measurements in lvai_aixml_reference section 10.
Never run a VI you have not inspected with lvai_describe_vi first.
```

- `viPath`: Absolute path to the .vi to run
- `inputsJson`: Control values as JSON object, e.g. {"X":"3","Y":"4"}. Keys are control labels. Omit for a VI that needs no inputs.
- `timeoutSeconds`: Local budget in seconds - raise it for long-running VIs (default: 300)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers. On pylabview the VI runs over LabVIEW's ActiveX server - the route of lvai_run_vi_and_read_values, whose answer comes back: inputs converted to each control's own type, every indicator read back with its type, NI's service not asked.

## `lvai_search_info_cache`

```text
RPC SearchInfoCache (server streaming). Full-text search over LabVIEW's info cache
(palette items, examples, help). Returns matches as JSON in 'infoJson'.
NOTE: observed returning an empty list on a station whose cache is not populated -
an empty result is not necessarily an error. Feed resulting GUIDs into
lvai_lookup_info_cache_items for detail.
```

- `searchTerms`: Search terms, comma or newline separated
- `tagFilters`: Optional tag filters, comma separated
- `caseSensitive`: Case sensitive matching (default: false)
- `limit`: Max hits requested from the server (0 = server default) (default: 20)
- `offset`: Result offset for paging (default: 0)
- `maxMessages`: Max stream messages to collect (default: 10)
- `timeoutSeconds`: Local budget in seconds (default: 60)

## `lvai_set_class_description`

```text
MUTATING: sets the description (NI.Lib.Description) of ONE .lvclass or plain .lvlib
through LabVIEW's own VI Server - LVClass.Open / Library.Open in the ACTIVE PROJECT's
instance, write Description, read it back, then Save of that one file
({LV.LVClassLibrary} Save / {LV.Library} Save). Nothing else is saved.
NEEDS A PROJECT OPEN AND ACTIVE: the helper reaches LabVIEW through Project:Active Project
and answers Error 1055 without one, reported as errorKind noActiveProject. Pass projectPath
and this call opens it.
THE TEXT IS STORED ANSI (Windows-1252). Measured 2026-10-11/12 on 15 classes: an arrow came
back as "?" with no error from the write, the read-back or the save. So any character
outside CP1252 - and any control character other than TAB, CR and LF - is REFUSED before
LabVIEW is touched (errorKind textNotCp1252), each one named with its position and an
ASCII replacement where one is obvious ("->" for an arrow); suggestedText carries the whole
text rewritten when every offender has one. Line breaks are SENT as LF: LabVIEW's Save
writes each LF as CRLF, and a CRLF sent in reached the file as CR CR LF (measured
2026-10-13), a blank line per break. The file ends up CRLF either way.
AN OWNER WITH UNSAVED CHANGES IS LEFT ALONE (errorKind ownerHasUnsavedChanges): its Save
would write those changes too. Save or revert it first.
VERIFIED FROM THE SAVED FILE: the .lvclass/.lvlib is re-read as XML and its
NI.Lib.Description compared with the text (line breaks compared as LF) - `verified` never
comes from LabVIEW's own read-back, which reads memory. `filesChanged` lists every file
under the owner's folder whose write time or length changed, so "only that file was
saved" is checked rather than assumed.
The .lvclass route is measured (a field run, 15 classes); the .lvlib route, its twin
through {LV.Library}, on one library (2026-10-13). An empty text is refused: an empty value
cannot travel to the helper, so clearing a description is not supported here.
```

- `ownerPath`: Absolute path to the .lvclass or .lvlib
- `text`: The new description, whole - it replaces the old one. Windows-1252 characters only; line breaks allowed. LabVIEW's description fields understand <b>bold</b> tags.
- `projectPath`: The .lvproj to open (and make active) first. Omit when the owner's project is already open and active. This file is not edited.
- `checkUnsaved`: Refuse when the target (or, for lvai_add_to_class, any VI in the project's instance) already carries UNSAVED changes, because this call's save would write them too. Default true - keep it for any EXISTING project, where someone may have VIs open with edits of their own. Pass false only in a project this session is building itself: the save then writes whatever is unsaved, which is what a build wants. The user's rule of 2026-10-17. (default: true)
- `timeoutSeconds`: Local budget in seconds (default: 120)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_set_constant`

```text
MUTATING: sets the value of ONE block diagram constant, found by its LABEL, and saves the VI
in place - no regeneration, so the rest of the VI is untouched. The generated tests label
every constant (`written 1`, `expected 2`), so this is the cheap way to run a NEGATIVE
CONTROL: set an expectation wrong, run the suite, confirm exactly one failure, set it back.
Before 2026-09-25 that cost two full regenerations of about 56 s each.
The value is TEXT and is converted to the constant's own type, read off the VI's export:
a number for any integer, float or enum constant (an enum also takes an item name), TRUE /
FALSE or 1 / 0 for a boolean, the text itself for a string. AN ARRAY OR A CLUSTER takes
the AIXML literal - `[[9,X,Y,1]]` for an array.2{string}, `[true,5000,src]` for an error
cluster - which is the form lvai_generate_test lists under `expectedConstants`; its
members may be string, bool or numeric. A path, or an enum inside a compound, is refused
rather than guessed at.
THE VERDICT IS THE FILE: the VI is exported again afterwards and `verified` says the
constant now holds the value asked for. `before` and `after` are both from exports.
A label that is not on the diagram, or is on it twice, is refused with the labels that
are there. Close any project holding the VI first - the helper edits it in the addon's
application instance, and a copy open in the IDE would not see the change.
```

- `viPath`: Absolute path to the .vi - it is saved in place
- `constantLabel`: The constant's block diagram label, exactly - e.g. 'expected 2'
- `value`: The new value as text - '2', '0.5', 'TRUE', 'CH9', or an enum item name. A string may contain line breaks
- `timeoutSeconds`: Local budget in seconds, per step (default: 180)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_set_event_data_fields`

```text
MUTATING: points an Event Structure frame's EVENT DATA NODE at the payload of the user event
it handles, so the frame READS what the event carried. That is the default shape of a user
event, and it is the one thing lvai_generate_vi_with_events cannot produce: LabVIEW's
importer discards an Event Data Node's field selection, because at conversion time the frame
carries no registration and only Source, Type and Time exist.
CALL IT THIRD - after lvai_generate_vi_with_events and lvai_wire_dynamic_events, and after
lvai_set_vi_icon if you want an icon. Earlier is wasted: a regeneration replaces the diagram.
AUTHOR A PLACEHOLDER FIRST. This takes over an EXISTING wire and never makes one, so the
AIXML must wire a labelled constant (`_name="Payload Placeholder"`) into the node that
should consume the field. THE SINK MUST BE A PRIMITIVE OR SUBVI INPUT: a front-panel
indicator terminal and a tunnel carry no type in the block diagram, and the row takes its
type from the sink.
FIELD INDEX: 4 is the FIRST PAYLOAD ITEM - 0 Source, 1 Type, 2 Time, 3 UsrEvtRef, then 4 and
5 for a two-element cluster payload, and 4 for a scalar one. Measured; an earlier note
calling 3 "the payload cluster" was wrong and produced an event-refnum terminal.
TWO FIELDS PER FRAME IS THE CEILING. A converted frame has three rows and the Source row
carries no field index to rewrite; the other two are repointed and stop showing Type and
Time. Read anything further off the payload cluster downstream.
A CONSTANT PLACEHOLDER IS KEPT on the diagram, unwired, which is legal - deleting one was
measured as `LabVIEW load error code 6: Could not load block diagram`. A NODE placeholder is
deleted, because an unwired Local Variable is a broken VI. The helper decides from what it
finds; you do not pass a flag.
`ok: false` is never a rollback - the .vi on disk carries whatever got as far as it got.
```

- `viPath`: Absolute path to the .vi to edit - it is REBUILT in place
- `fields`: One line per Event Data Node: `<dataNodeUid> <placeholderUid>:<fieldIndex> [...]`, for example `4244 4280:4`. The uids are the ones YOUR AIXML wrote - for a constant LabVIEW keeps it on an inner object and renumbers the outer one, and the helper resolves that, so you never read a heap.
- `verify`: Read execState back afterwards, from a copy LabVIEW has never loaded (default: true)
- `bundleDirectory`: Where to put the extracted bundle. Defaults to a fresh temp directory, deleted on success. Pass a path to keep it - useful when an edit needs debugging.
- `timeoutSeconds`: Local budget in seconds, per step (default: 180)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. On pylabview the verify reads execState over ActiveX and the row names back with our own reader. A VI the composer wrote reads its payload already - name the field in the Event Data Node - and needs no call of this tool.

## `lvai_set_owner_icon`

```text
MUTATING: sets the icon of an EXISTING .lvclass or plain .lvlib from a 32x32 PNG and saves the
owner; with applyToMembers it also lays that icon onto member VIs as their NI_Library
TEMPLATE LAYER (LabVIEW's own ApplyIconToVIs - white is transparent, so each VI keeps its own
glyph where the template is white) and SAVES each member. Codes per member in `members`.
lvai_set_vi_icon cannot do this: it sets pixels on one VI and flattens its layers.
THE PROJECT MUST BE CLOSED - with it open LabVIEW answers Error 1562 and writes nothing. The
active project is checked first and the call refuses with `projectOpen`; close it with
lvai_close_active_project. Every other check runs on the files before LabVIEW is touched:
the owner exists and is what its extension says, the PNG is a 32x32 PNG, every member named
is a .vi the owner lists and its file is there.
BUILT AROUND THREE MEASURED, SILENT FAILURES of the bare VI Server route: ApplyIconToVIs
saves no member; on a plain .lvlib it changes nothing unless the members are opened first;
and a project left open makes it a no-op with 1562. This opens every member, applies, and
saves each one.
VERIFIED BY READ-BACK, not by the codes: the owner's icon is read back out to a PNG and
compared pixel by pixel with yours (`ownerIcon.pixelsDifferent`, 0 for web-safe colours;
LabVIEW shifts other colours and that is reported, not failed); each applied member is read
back too and must show every non-white pixel of the owner's icon (`templateMissing` 0).
SIDE EFFECTS TO REPORT: the owner and every applied member are re-saved, by the LabVIEW
running - a project saved in an older version is upgraded (`ownerLvVersionBefore/After`).
Measured through this tool 2026-10-10 on LabVIEW 2026 Q3: a class with nine members and a
plain library with two, every code 0, owner read-back 0 px, templateMissing 0 on every member,
members executable; with memberPaths only the named member changed on disk. Lists over 100
members (the list-file helper) are unmeasured.
```

- `ownerPath`: Absolute path to the .lvclass or plain .lvlib whose icon is set - it is saved in place
- `pngPath`: Absolute path to the 32x32 PNG to use: the template - banner and glyph, WHITE wherever a member's own icon should show through. Pick colours from the web-safe cube (each channel 00, 33, 66, 99, CC, FF) and the read-back is an exact equality check.
- `applyToMembers`: Also apply the icon to member VIs as their template layer and save each member. Off by default: setting the owner's icon changes no member, and saving members is the part with side effects. (default: false)
- `memberPaths`: With applyToMembers: the member VIs to apply to, ONE ABSOLUTE PATH PER LINE. Omit for every .vi member the owner lists. Each must be a .vi the owner lists. Naming only the VIs a task added keeps existing members untouched on disk.
- `readBackDir`: Folder for the read-back PNGs (the owner's icon, and members/ under it). Defaults to a short folder under %TEMP%\LabVIEWMCP\icons. Created if missing. Keep it short: a PNG path over 259 characters is written by nobody, with no error - it is refused here instead.
- `helperDirectory`: Where to keep the generated helper VIs. Defaults to %TEMP%\LabVIEWMCP\helpers
- `regenerateHelpers`: Regenerate the helper VIs even when they are current (default: false)
- `timeoutSeconds`: Local budget in seconds, per LabVIEW call (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_set_vi_descriptions`

```text
MUTATING: sets the VI Description of each listed VI and saves it with Save.Instrument -
NO REGENERATION, so the diagram, the front panel and the ICON stay exactly as they are.
One helper run per VI inside this one call.
THE PROJECT MUST BE CLOSED. The route (open by path in the default application instance,
write, read back, Save.Instrument) was measured that way on a production tree, 62
descriptions; with a project active this call refuses (errorKind projectActive) - close it
with lvai_close_active_project first.
THE TEXT IS STORED ANSI (Windows-1252): any character outside CP1252, and any control
character other than TAB, CR and LF, is refused for the whole batch before LabVIEW is
touched (textNotCp1252, naming the entry, each character, its position and an ASCII
replacement). Line breaks inside a description are fine and are written CRLF.
A VI THAT ALREADY HAS UNSAVED CHANGES IS LEFT ALONE (that entry answers
viHasUnsavedChanges): its Save.Instrument would write those changes too.
VERIFIED PER VI FROM THE SAVED FILE: the VI's STRG resource is read back offline and
compared with the text (line breaks as LF), and the icon pixels (icl8/icl4/ICON resources)
are compared before and after - `iconUnchanged`. `otherFilesChanged` lists any file under
the VIs' folders that changed and is not one of the listed VIs. Entries are independent:
one failing does not stop the rest, and `results` has one entry per VI.
Only .vi files; an empty description is refused (an empty value cannot travel to the
helper), so clearing a description is not supported here. For a class or library
description use lvai_set_class_description.
```

- `entriesJson`: JSON array of entries, e.g. [{"viPath":"C:\\p\\Read.vi","description":"Reads the value."}]. `viPath` (absolute .vi) and `description` (the whole new text, replacing the old) are both required; no other key is accepted. Each VI at most once.
- `checkUnsaved`: Refuse when the target (or, for lvai_add_to_class, any VI in the project's instance) already carries UNSAVED changes, because this call's save would write them too. Default true - keep it for any EXISTING project, where someone may have VIs open with edits of their own. Pass false only in a project this session is building itself: the save then writes whatever is unsaved, which is what a build wants. The user's rule of 2026-10-17. (default: true)
- `timeoutSeconds`: Local budget in seconds, per VI (default: 120)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_set_vi_icon`

```text
MUTATING: replaces the icon of an EXISTING .vi and saves the VI in place.
lvai_convert_aixml_to_vi cannot do this - "VI icon graphics" is on NI's not-supported
list - so this composes the VI Server route instead: generate the helper VI from
scripts\lvdoc_set_icon.xml (once, then reused), run it, read the icon back out.
Measured on LabVIEW 2026: a 32x32 PNG is applied as-is and the icon read back out of
the VI is pixel-identical. Other formats and sizes are untested.
TWO WAYS TO SUPPLY THE IMAGE. Either pass line1/line2/line3 and let this DRAW the
32x32 icon for you - banner across the top plus up to two lines under it, 5 characters
per line, from a built-in bitmap font - or pass iconImagePath if you have your own art.
PREFER THE LINES: drawing the PNG yourself through PowerShell and System.Drawing was
measured at 12.5 s of a generation session, against 0 ms here, and it costs an extra
tool call worth about 11 s of wall clock on top.
CALL THIS LAST. lvai_convert_aixml_to_vi over an existing path DESTROYS the icon -
measured on two VIs - so re-apply after every regeneration. It is safe in the other
direction: setting an icon does not leave the VI in memory, so the path can still be
regenerated afterwards.
`ok` IS `verified`, and on a verified run `errorCode` is 0: RunVIAsTopLevel's own 91 - the
known read-back artefact, which appears on success - is dropped, and only an unexpected
runner code is kept under runnerErrorCode. Look at the read-back PNG to see what actually
landed in the VI.
IT FLATTENS THE ICON: the icon editor's layers - a class or library template layer and the
VI's own editable layer - become plain pixels. On hand-made icons that changed 27-36 % of
every icon and lost the editable text. To refresh only the class or library TEMPLATE of a VI
whose own layer must survive, pass keepOwnLayer instead of an image: no pixels are set, the
owner's icon is re-applied (ApplyIconToVIs) and the VI saved, and the answer reports the
layer data before and after. keepOwnLayer was measured 2026-10-10 - see its parameter.
SEVERAL VIs IN ONE CALL: pass iconsJson, a JSON array of {"viPath", "line1", "line2",
"line3", "iconImagePath"} - viPath required, the rest optional and falling back to the
same-named parameters of this call; colours, helper settings and timeout are shared. The
icons are set one after another and the answer carries `failed` plus every VI's whole single
answer under `results`. Measured over this repository's transcripts this tool was called
straight after itself 70 % of the time, so a batch saves a model turn per VI.
```

- `viPath`: Absolute path to the .vi whose icon is replaced - it is saved in place. Optional when iconsJson is given.
- `iconImagePath`: Absolute path to a ready-made icon image; a 32x32 PNG is what was measured. Leave this out and pass line1/line2/line3 instead to have the icon drawn here. If both are given the file wins and the lines are reported as ignored.
- `readBackPath`: Where to write the icon read back OUT of the VI afterwards, for verification. Defaults to a temp file. Its directory is created if missing - LabVIEW's own file write does not create directories and fails with Error 7 instead.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user cache directory, because the scripts folder next to the exe may be read-only. Generated once and reused; pass regenerateHelper to force a rebuild.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvdoc_set_icon.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `line1`: Text for the coloured banner across the top of a drawn icon, e.g. "DAQ". Up to 5 characters; A-Z, 0-9, space and - . / : + # are drawn, anything else is reported. Lowercase is upper-cased - a 5x7 cell has no room for descenders.
- `line2`: Second line of a drawn icon, under the banner. Up to 5 characters
- `line3`: Third line of a drawn icon. Up to 5 characters
- `bannerColor`: Banner colour of a drawn icon as RRGGBB or #RRGGBB. Defaults to 006699, the web-safe neighbour of NI blue - LabVIEW quantises a stored icon to the web-safe cube, so a colour outside it reads back changed and is reported in warnings.
- `backgroundColor`: Body background of a drawn icon as RRGGBB. Defaults to white FFFFFF
- `borderColor`: 1 px frame of a drawn icon as RRGGBB. Defaults to black 000000
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `keepOwnLayer`: Keep the VI's own icon layer and re-apply only its class or library TEMPLATE, instead of setting pixels and flattening. Pass no image and no lines with it. The owner's current icon is laid over the VI with ApplyIconToVIs (white is transparent) and the VI saved; the project must be closed. The answer reports the VI's icon layer data before and after (read from the file), whether every template pixel landed, and how many pixels outside the template changed. Measured 2026-10-10 on LabVIEW 2026 Q3 on a VI that already had a template layer and an own pixel layer, after the class icon changed: the template layer was replaced (not stacked), every template pixel landed and no pixel outside it moved. An own layer of editable text (made in the icon editor) has not been run - judge each call by iconLayersAfter, templateMissing and pixelsChangedOutsideTemplate. (default: false)
- `ownerPath`: With keepOwnLayer: the .lvclass or .lvlib whose icon is the template. Defaults to the one class or library in the VI's folder or up to three above that lists the VI.
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.
- `iconsJson`: Several VIs in one call: a JSON array of {"viPath": "...", "line1": "...", "line2": "...", "line3": "...", "iconImagePath": "..."}. Only viPath is required per entry; a missing line or image falls back to this call's own parameter. An unknown key is refused by name.

## `lvai_status`

```text
Show whether LabVIEW's embedded AI gRPC server is reachable: the discovered port,
how it was found, and the service list reported by server reflection. Start here -
every other lvai_* tool depends on this connection. The port is ephemeral and
changes on each LabVIEW restart, so it is re-discovered automatically.
Also reports scriptsDirectory: the absolute path of the helper scripts shipped next to
this server's exe. Use it instead of a relative path - it works from any working
directory, and from a binary-only install with no repository checkout.
`labviewHealth` counts the `DWarn` EVENTS in NI's OWN crash log, which is where
LabVIEW records faults - its crash handler means Windows Error Reporting never sees them,
so an empty event log is not an alibi. Events, not lines: NI writes each one twice and
this counted the substring until 2026-09-08, so `dwarnCount` was double throughout and
every DWarn figure in this repository predating that halves. `dwarnCountedBy` names the
rule that counted. USE IT AFTER A FAILURE, NEVER AS A GATE BEFORE
WORK: measured 2026-09-03 over two runs, it was wrong in BOTH directions - an instance at
200 completed a whole cold class build without incident, and an instance reading 0
crashed inside ConvertAIXMLToVI minutes later. What it is good for is the retrospective
question: a class-editing call has failed for no reason the project state explains, so did
the count move, and does a restart cure it? The log is reset at start, so the count covers
this instance only.
```

## `lvai_swap_subvis`

```text
MUTATING: repoints subVI nodes and diagram constants on ONE block diagram through LabVIEW's
own {LV.SubVI} / {LV.Constant} Replace, all in a single run, then saves the VI in place.
THIS IS HOW A GENERATED VI COMES TO CALL CLASS CODE. AIXML refuses a class-typed terminal, so
no generated Call can name an accessor and lvai_placeholder_subvi answers `stubRefused`.
Replace is the IDE's own "Replace with a VI from disk" and RE-TYPES THE WIRES, which a
pylabview link retarget cannot - so a test authored against sockets whose class terminals are
`path` stand-ins becomes a test that calls the real accessors statically.
swapsJson is a JSON ARRAY of nodes to repoint:
  [{"socket":"LVMCP Acc W1.vi","target":"C:\\cls\\Write Hersteller.vi"}]
constantsJson is a JSON ARRAY of constants to turn into class constants:
  [{"label":"seed 1","class":"C:\\cls\\Netzteil.lvclass"}]
NODES ARE SWAPPED FIRST AND CONSTANTS LAST, always. A dynamic dispatch input is a REQUIRED
terminal, so a class chain needs a class value; with the nodes already swapped that wire has
a class sink and Replace re-types it, where the other order breaks it.
A CONSTANT IS FOUND AT ANY DEPTH since 2026-10-11 - inside a loop or a Case frame too. It
was read off the top-level diagram's All Objects[], which does not descend, so a seed
constant in a structure was never found. A LABEL THAT MORE THAN ONE CONSTANT CARRIES IS
REFUSED: it is not replaced, `constantsAmbiguous` names it with its match count, `ok` is
false (`errorKind: constantLabelAmbiguous`), and every other label is still replaced and
the VI saved - matching is by label, so two constants with one label cannot say which one
is meant. A label no constant carries is `constantsNotOnDiagram`. `constantMatches` gives
every label's count; `constantsSeen` is how many constants the traversal returned, and
zero there means the helper's class name matched nothing. Measured 2026-10-17 on both
backends: `Constant` matched all five constants of a probe (path, bool), a path seed
inside a Case frame inside a While Loop became a class constant, and a label two
constants carried was refused with both left as they were.
A CALL INSIDE A LOOP OR A CASE FRAME IS REACHED, and that is new as of 2026-09-16. The
helper collected from {LV.Diagram} SubVIs[], which lists the diagram it is asked about and
does NOT descend into structures, so a nested call came back under `socketsNotOnDiagram`
while sitting plainly on the diagram - measured on a VI with three calls at the top level
and two inside a Case frame, where `diagramSubVis` listed three of five. It traverses the
whole block diagram now, verified on a main VI whose six calls all sit inside its event
loop: all six listed, five of five swapped in one call. The pylabview fallback was no
answer either: a
{"op":"retarget"} on the same VI rewrote the links and LabVIEW then refused to load it with
`Missing subVI`, while callTargets and the AIXML export were both green.
EVERY SOCKET NAME MUST BE UNIQUE IN `swapsJson` - this refuses a name given twice, because
matching is by VI Name and two ENTRIES for one name cannot say which node gets which target.
A name that is not on the diagram is refused too: the helper's array search answers -1 and
Index Array then clamps to element 0, silently swapping a node you did not name. WHEN THAT
HAPPENS THE ANSWER NAMES EVERY subVI THE DIAGRAM HAS, in `diagramSubVis`, so the correction
costs no round trip - the names were always in the helper's reply and were reachable only
inside the swap step's sub-answer, which `verbose: false` strips.
AND A NODE ALREADY POINTED AT A CLASS MEMBER CARRIES ITS QUALIFIED NAME. A second swap over
the same diagram must say `Centrifugal Pump.lvclass:Read Last Event.vi`, not
`Read Last Event.vi` - the bare name matches nothing. Measured twice, on
`DAQmxAnalogInput.lvclass:Start.vi` and again 2026-09-15 while setting up a negative control.
`nodesSwapped` IS AN OUTCOME, NOT AN ECHO OF THE REQUEST - `nodesAsked` is the request. It
WAS the request, and the answer then contradicted itself: `nodesSwapped: 8` beside a
`socketsNotOnDiagram` listing five of those eight, which cost ~100 s of wall clock to
disbelieve (docs/class-method-tooling.md D1). Nothing landed when the helper errored,
because the error on the wire stops Save.Instrument and the file is untouched.
ERROR 1055 MEANS NO ACTIVE PROJECT, and it is reported as `errorKind: noActiveProject`
rather than as a bare number beside an Invoke Node's name. `{LV.SubVI}` Replace is a silent
no-op outside the IDE's own application instance, so this tool needs the project OPEN.
MEASURED 2026-09-15: `lvai_run_lunit_tests` leaves no active project behind, so a swap
issued straight after a test run lands here every time - call `lvai_open_file` between them.
THE DIAGRAM IS A DIFFERENT QUESTION, and this said "EVERY SOCKET NAME MUST BE UNIQUE on the
diagram" until 2026-09-15, which is FALSE and read as "a generated VI cannot call the same
method twice". Measured on a two-call probe: one call swaps ONE node, answers `ok: false`
with `socketsLeft: 1`, and the SAME call again swaps the other and answers `socketsLeft: 0`.
The result is executable - `execState 1` with a class constant feeding the chain. So a
diagram MAY call one method several times, and it costs ONE CALL PER NODE whatever N is -
measured to three on 2026-09-15.
`socketsLeft` IS NOT A NODE COUNTER, and this said it was. It counts how many of the
`swapsJson` ENTRIES still occur in the export, so with one entry naming a repeated socket it
reads 1 until the last node goes and then 0 - measured over three swaps as 1, 1, 0 while
two, one and zero nodes remained. The two-call probe this was written from could not see
that: at N=2 a count and a flag are the same two numbers. `callTargets` is de-duplicated by
name and cannot answer it either. READ `diagramSubVis`, which lists the diagram's subVI
names WITH REPETITION - it shows the state BEFORE the swap it accompanies, so it says what
this call was working on rather than what is left afterwards.
NOTHING IS READ BACK FROM A REPLACED OBJECT, because the reference does not survive it -
Error 1055 - and that error would travel down the wire and stop the save. `verify` therefore
re-exports the VI through LabVIEW afterwards and reports its call targets; that export is the
only real proof, and `socketsLeft` above zero means a swap did not land.
THE LAST STEP CLEARS BROKEN WIRES (`removeBadWires`, default true, since 2026-10-11): a
Replace that cannot re-type a wire leaves it broken and the VI at state 0. It runs before
the verify export, lists every removed wire under `badWireCleanup`, and a sink that lost its
wire from a source makes `ok` false (`errorKind: badWireCleanupLostConnection`).
```

- `viPath`: Absolute path to the .vi to edit - it is SAVED IN PLACE. Omit it when you pass `editsJson`, which carries a path per VI. IT USED TO BE REQUIRED, WHICH MADE `editsJson` UNREACHABLE. The description promised that `viPath` is ignored when `editsJson` is given, and a required parameter is enforced by the CLIENT against the schema - so the batch call was refused with `-32602 Invalid arguments` before the server could honour its own contract. Measured 2026-09-07; the caller worked around it with a dummy path. Identical to the defect fixed on lvai_placeholder_subvi on 2026-09-03, which is why the lesson is written on both: a parameter that ONE mode ignores must not be mandatory in the schema.
- `swapsJson`: JSON array of {socket, target} node swaps. Omit to swap no nodes.
- `constantsJson`: JSON array of {label, class} constant swaps. Omit to swap no constants.
- `verify`: Export the VI BEFORE and AFTER, and report what it now calls plus a wiring comparison. On by default; the before-export is the whole added cost - one RPC, not a model turn. `callTargets` and `socketsLeft` are the verdict. `wiringLost` is REPORTING ONLY. IT IS NOT A VERDICT, AND THAT IS MEASURED. It gated `ok` for one day and produced false negatives on three separate runs, for two reasons that together make the comparison undecidable: a uid is NOT stable across `Replace` when several nodes move - five Write nodes were measured jumping to 1070/273/269/230/124, so a before/after pairing by uid compares unrelated nodes - and a correct swap onto a real accessor legitimately ends with FEWER wired terminals than the socket had, because the accessor's error terminals are fed a fresh `no error` constant instead of being chained. Read `wiringLost` against the export when a diagram looks wrong; never branch on it. (default: true)
- `verbose`: Keep every sub-answer whole. OFF by default because the full ones are large for no benefit: measured 2026-09-01, six swaps in one message returned about 34 kB, most of it LabVIEW's entire AIXML export inline plus a flattened per-node value dump. What a caller reads - callTargets, nodesSwapped, socketsLeft - is lifted out already. A step that FAILED is reported whole regardless of this flag. (default: false)
- `helperViPath`: Where to keep the generated helper VI
- `helperAixmlPath`: The helper's AIXML source; defaults to lvai_swap_subvis.xml in scriptsDirectory
- `regenerateHelper`: Regenerate the helper VI even when it already exists (default: false)
- `editsJson`: SEVERAL VIs IN ONE CALL, as a JSON array - given this, `viPath`, `swapsJson` and `constantsJson` are ignored and the answer carries one entry per VI under `edits`: [{"vi":"C:\\t\\A.vi","swaps":[{"socket":"LVMCP Mth1.vi","target":"C:\\c\\M.vi"}], "constants":[{"label":"Seed1","class":"C:\\c\\Daq.lvclass"}]}] THE SAVING IS ROUND TRIPS, NOT LABVIEW TIME. LabVIEW serialises the work either way, so this makes the same trade `lvai_generate_vis` does. Measured 2026-09-03 over a four-method class: four swap calls cost 30 s of wall clock against 4.4 s inside LabVIEW, because each was its own model turn. EVERY VI IS STILL SAVED IN PLACE, and a failure STOPS the batch at that VI - the ones before it are already written, and the answer says how many.
- `timeoutSeconds`: Local budget in seconds, per step (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.
- `cleanUpDiagram`: Lay out each VI's block diagram with our layout engine (lvai_layout_diagram's) after a successful swap and save it in place (the cleanUpDiagram field) - a Replace keeps each node's position while its size and terminals change. Reported, never gating; a DQMH module's Main.vi is never touched. (default: true)
- `removeBadWires`: After a saved swap, clear the BROKEN wires left on the diagram (BD.Remove Bad Wires, in the IDE's instance) and report each removed wire under `badWireCleanup`. Default true - the user's rule of 2026-10-11. A sink that had a wire from a source before and none after is a LOST CONNECTION: it is named in `inputsLeftUnwired` and `ok` is false (`errorKind: badWireCleanupLostConnection`). A VI with no broken wire is not touched. Runs before the layout, so the layout sees the cleaned diagram. (default: true)

## `lvai_validate_aixml`

```text
RPC ValidateAIXML. Asks LabVIEW whether an AIXML file is well-formed and semantically
acceptable, WITHOUT creating anything. Always run this before lvai_convert_aixml_to_vi
or lvai_apply_aixml_to_vi - it is the cheap failure path.
Reading the messages: "Unsupported SubVI: X" means the Call target cannot be resolved
by validation - project-local subVIs and Express VIs never can, EVEN WHEN the project
subVI is open in LabVIEW, while lvai_convert_aixml_to_vi then ACCEPTS it (measured
2026-09-25, docs/aixml-call-loaded-vi.md); "Object terminal not found for
input" means a misspelled terminal name, or fallout from such a Call.
lvai_aixml_reference has the authoring rules and a verified terminal-name table.
A REFUSAL NO LONGER BURNS THE VI's NAME. Validation runs against a throwaway copy whose
`_name` is replaced, because a failed validate used to register the real name in LabVIEW's
memory and every later ConvertAIXMLToVI for it answered Error 1051 until a restart -
measured, on a file that had never existed on disk. The answer says what name LabVIEW
actually saw under `validatedAs`. Pass preserveName to validate the real one.
```

- `aiXmlFilePath`: Absolute path to the .xml file to validate
- `timeoutSeconds`: Local budget in seconds (default: 120)
- `preserveName`: Hand LabVIEW the document's REAL `_name` instead of a throwaway. Off by default: a failed validation registers the name in LabVIEW's memory and burns it for every later ConvertAIXMLToVI until a restart. Turn it on only to check the name itself. (default: false)
- `allowUnknownPropertyNames`: Hand LabVIEW a document whose Property Node uses a SHORT property name the check knows the full form of (`Version Number` for `Application:Version Number`). Off by default: the short form was measured killing LabVIEW inside ConvertAIXMLToVI, so the document is refused before any RPC. Turn it on only to measure that. (default: false)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.

## `lvai_vi_server_reference`

```text
Look up the exact Invoke Node / Property Node vocabulary for generating a VI that calls
VI Server: 3094 methods and 6451 properties over 157 classes, with their terminal names.
Use this whenever you author AIXML containing an Invoke Node or Property Node.
A method's `target` string CANNOT be derived any other way - method names are binary IDs
inside a .vi, LabVIEW.exe does not carry them as text, and SearchInfoCache covers palette
items rather than VI Server. Guessing produces XML that validates and then does nothing.
Without arguments: how to use it plus the class list. With query and/or cls: matching
rows only. SEVERAL NAMES IN ONE CALL, comma-separated - query='Callees,Icon,Print' -
which is one round trip and never prints the same row twice.
A row with a `source` field was added without a harvest and says where it came from -
a `lvserver.rsc` row is a candidate name, not a validated one.
Combine with lvai_run_vi_as_top_level to reach capabilities no RPC exposes -
that is how a VI's icon and connector pane are obtained.
```

- `query`: Substring of the method or property name, e.g. 'To HTML', 'Callees'. Several at once, comma-separated: 'Callees,Icon' - matching rows are merged and de-duplicated
- `cls`: Class filter, with or without braces: 'LV.VI', '{LV.Application}'
- `kind`: 'methods', 'properties' or 'both' (default)
- `limit`: Max rows to return (default 40, max 400) (default: 40)
- `section`: Section of the guide document, or 'all'; ignored when query/cls are given

## `lvai_vi_terminals`

```text
The exact terminal names for putting a `Call` to some VI in AIXML - read out of that VI's
own export, so no guessing and no hunting for another VI that already calls it.
USE THIS after lvai_palette_index gives you a VI: that tool says a VI may be Called, this
one says what its terminals are called, and they are not derivable. Measured on
`Read Delimited Spreadsheet.vi`: `max characters/row  (no limit\3A0)` has TWO spaces,
`delimiter (\\t)` has a doubled backslash, and the wrapper is polymorphic so a Call also
needs an `instance`. Returns a ready-to-paste `Call` element.
For a POLYMORPHIC VI it lists every instance with its own Call - note the attribute
shuffle: the wrapper name is `target`, the instance name goes in `instance`.
Read-only, and it does NOT burn the path for a later lvai_convert_aixml_to_vi - measured:
exporting a VI leaves it regenerable, only lvai_open_file does not.
The last line of the answer says whether the export came from the disk cache or from
LabVIEW, the same way lvai_convert_vi_to_aixml reports it; pass refresh to force a
re-export when you suspect a stale entry.
A VI INSIDE AN `.llb` CONTAINER FILE IS READ TOO, since 2026-10-15 - spell the path through
the `.llb` as LabVIEW does: `...\DAQmx\configure\task.llb\DAQmx Clear Task.vi`. Every DAQmx
VI lives in such a file, and until then this tool answered `FileNotFoundException` for all
of them, on both backends, because the server checked the path exists before asking
anyone. NI's converter takes the path as it is (measured, `errorCode 0`), and the export is
cached like any installation VI, keyed on the `.llb`'s own stat; the pylabview reader
extracts the container once into `%USERPROFILE%\.labviewmcp\cache\llb` and reads the
member there. A member name the `.llb` does not hold is the reader's or NI's refusal.
The other route still works and is worth knowing for a whole API: export an NI EXAMPLE
that already calls the VI - `lvai_example_index` finds one - and a single example
(`Voltage - Finite Input.vi`) yields every terminal name and both polymorphic instance
names at once.
ON THE pylabview BACKEND the export is our own reader's (vi2aixml.py) - no LabVIEW and no
NI service at all - and it goes through the export cache too since 2026-10-15, kept apart
from NI's entries by its key: a cached NI export of the VI is served first (higher fidelity,
reading it needs no NI service), then the reader's own earlier export, else a fresh read is
cached. A reader entry is never served to a grpc call. The provenance line says whose export
it was; the answer's last line names `backend` and `backendSource`.
```

- `viPath`: Absolute path to the .vi whose terminals you need
- `refresh`: Re-export even when a cached export exists (default: false)
- `timeoutSeconds`: Local budget in seconds (default: 180)
- `backend`: Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or "pylabview" (no NI service at all: our own composer and reader write and read a VI with no LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over its ActiveX server). Omit it to use the server's default - --aixml-backend or LABVIEWMCP_AIXML_BACKEND, else "aixmlBackend" in %USERPROFILE%\.labviewmcp\settings.json (grpc, pylabview or auto - the file explains each), else pylabview when the LabVIEW in use is older than 2026, else grpc. Every answer names the backend that ran under `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.

## `lvai_vis_in_memory`

```text
Every VI in memory of the APPLICATION INSTANCES you name, with each VI's front-panel and
block-diagram modification bitsets (nonzero = unsaved), each sorted as service (NI's
`LV AI …lvlibp`), labview (`GSW.lvlibp`, the start window), helper (labview-mcp's own
helpers and LVMCP scratch VIs) or other. Only the modified, unreadable and `other` lines
come back, never all 400. Classes and libraries are never listed, only VIs and controls.
`context` CHOOSES THE INSTANCE - LabVIEW keeps one per context, and a VI is in exactly one:
  main    - LabVIEW's main application instance, where a VI opened loose lives. Reached
            over LabVIEW's ActiveX server on BOTH backends (that IS the main instance);
            on grpc the NI-built helper is run there, on pylabview the composed one.
  project - the ACTIVE project's instance (Project:Active Project, then its Application),
            where a project's VIs live. Pass projectPath to make sure it is that project:
            another active project is refused by name, and none answers noActiveProject.
            Nothing is opened or activated here - this tool is read-only.
  addon   - NI's AI add-on's own instance, where a grpc helper runs (the service's VIs;
            the fork measured 419). grpc only: ActiveX cannot reach it, so pylabview
            refuses it by name.
  all     - main, the active project if there is one, and addon on grpc. The default.
Each instance answers under `instances` with its own `onlyServiceAndHelpers`; `modified`
at the top joins them, each line tagged with its context.
A probe run while a project is open may be adopted into that project on its next save.
Runs scripts\lvai_vis_modified.xml (main, addon) and lvai_vis_modified_project.xml
(project). On grpc a helper is generated once into %TEMP%\LabVIEWMCP\helpers, and
generating validates it, which leaves an `LVMCP Validate` VI in memory - generate at the
start of a session. On pylabview the composer needs no validation. NI's packed-library
.ctl lines answer 1035 and do not count against `onlyServiceAndHelpers`.
docs/labview-state.md.
```

- `context`: main, project, addon or all (default all). See the description.
- `projectPath`: With context project or all: the .lvproj that must be the ACTIVE one. Another active project is refused, nothing is opened.
- `regenerateHelper`: Regenerate the helper VIs even when they already exist (default: false)
- `timeoutSeconds`: Local budget in seconds, per instance (default: 60)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `lvai_wire_dynamic_events`

```text
MUTATING: branches a VI's event registration refnum onto its Event Structure's DYNAMIC
EVENT terminal and saves the VI. That connection is the one thing AIXML cannot express, so
it is what a regeneration loses - and without it a VI with user-event frames does not run
at all (measured: Execution:State 0 before, 1 after).
TWO SOURCES, TRIED IN THAT ORDER. Preferred: the structure's own Event Registration Refnum
TUNNEL, whose net is branched - author that tunnel in AIXML, because a <Tunnel> on a
<Structure> survives every regeneration and is what makes the whole route repeatable.
Fallback: the Register For Events node on the block diagram, wired straight across the loop
border, which LabVIEW accepts and answers by creating the tunnel itself. `sourceFrom` says
which was used; the fallback assumes ONE registration node and reports the count it saw.
BOTH ENDS ARE FOUND BY NAME, never by index: the terminal and the tunnel both answer to
"Event Registration Refnum". The net's SOURCE is then walked to, because offering the
structure's own tunnel as the source is Error 1062, "Specified objects cannot be wired
together" - two sinks have no direction between them.
VERIFY BY RENDERING, and the tool says this in its own answer: with Auto Route? at its
FALSE default the connection is real and LabVIEW draws no wire, and NO property tells the
two apart - Position, Bounds and CleanUpWire are all identical. This helper wires
Auto Route? TRUE, and lvai_render_diagrams is still the only check that sees the result.
Render from a path LabVIEW has never loaded: a VI can be loaded twice, and a render of the
just-edited path has been measured showing the PRE-EDIT diagram from the addon instance's
own copy.
THE SEARCH IS AT ANY DEPTH: NI's Traverse for GObjects.vi walks the whole block diagram
for the EventStructure and, for the fallback, the RegisterForEvents node - so an Event
Structure in a Case frame inside a While Loop (the Actor Framework helper-loop shape) is
found, where the earlier two-level search missed it. eventStructuresFound is the count;
with more than one the FIRST is wired and the note says so.
It needs a project OPEN AND ACTIVE in the IDE: the VI is opened through
Application:Project:Active Project so the edit lands in the copy the user is looking at.
IT ALSO FINISHES A USER-EVENT FRAME, which nothing else can, and that is why a VI with one
ends this call EXECUTABLE instead of eBad. LabVIEW normalises a user-event EventSpec when
it LOADS the VI, against the wire present in the FILE at that moment - so the spec
lvai_generate_vi_with_events writes is necessarily discarded, by the very save this tool
performs. Afterwards it sticks, so the spec is written AGAIN here and the VI rebuilt:
measured 2026-09-11 on two VIs, both eBad -> eIdle on exactly that step. Read
userEventStep; `changed` says whether there was anything to do.
THE BUNDLE STEP CLOSES THE ACTIVE PROJECT, saving it, because pylabview writes the file
while LabVIEW keeps serving its own copy - so without the release the verification reads
the VI it replaced. It is ONE round trip for both jobs (user-event specs, use-default
flags), and it is skipped entirely, project included, when neither has anything to do.
projectClosed says whether it happened and projectClosedFor why. finishUserEvents: false
turns off the user-event half.
SEVERAL USER-EVENT FRAMES ARE FINISHED TOO (since 2026-10-10): dynIndex is the event's
position among the registration's items, lvai_generate_vi_with_events writes it per frame,
and this tool reads it off the VI BEFORE its wiring save destroys it, then writes each
frame back. A VI generated before that date wrote dynIndex 1 on every frame and is refused
rather than guessed - a wrong dynIndex gives a VI that loads, compiles and fires the WRONG
event; generate it again.
IT ALSO WIRES THE RIGHT SIDE (wireRightSide, default true): the structure's SOURCE-side
Event Registration Refnum terminal to the event registration refnum input of Unregister
For Events, found at any depth by Traverse (class Function, exact, with a sink of that
lower-case name), Auto Wire and Auto Route TRUE, LabVIEW making the tunnels. Read
rightSide: no Unregister node is {skipped: "noUnregisterForEvents"}, more than one is
refused and named with the count, an input already wired is a no-op, and a helper that
stopped before searching (no active project) is {skipped: "notReached"}. It runs on its own
error branch, so a right-side failure does not lose the left wire.
A TUNNEL THAT WIRE CREATES ON AN ENCLOSING CASE STRUCTURE IS UNWIRED IN THE OTHER FRAMES
AND BREAKS THE VI, so "Use Default if Unwired" is set on it - in the pylabview bundle,
because AIXML refuses that property ("Invalid property"). ONLY tunnels this call created:
the VI is extracted BEFORE the helper runs and only selTun uids absent from that baseline
are touched; if any baseline tunnel is missing afterwards the uids are not comparable and
nothing is patched. useDefaultSet names the tunnels.
```

- `viPath`: Absolute path of the .vi to wire. It is SAVED in place on success.
- `helperViPath`: Where to keep the generated helper VI. Defaults to a per-user temp directory. Generated once and reused, and regenerated when the shipped AIXML is newer.
- `helperAixmlPath`: The helper's AIXML source. Defaults to lvai_wire_dyn_events.xml inside the folder lvai_status reports as scriptsDirectory.
- `regenerateHelper`: Regenerate the helper VI even when it already exists and is current (default: false)
- `finishUserEvents`: After wiring, write the VI's USER-EVENT EventSpecs again and rebuild - the step that can only happen once the wire exists, because LabVIEW discards a user-event spec it cannot resolve when it LOADS the VI. On by default: without it a VI with a user-event frame is left eBad, which is not a deliverable. It CLOSES THE ACTIVE PROJECT (saving it) to release the VI from LabVIEW's memory, because pylabview writes the file happily while LabVIEW keeps serving its own copy. Skipped entirely, project included, when the VI has no user-event frame to finish. (default: true)
- `wireRightSide`: Also wire the RIGHT side: the Event Structure's source-side Event Registration Refnum terminal to Unregister For Events, then set Use Default if Unwired on any Case Structure output tunnel that wire created. On by default. Costs one extra pylabview extract up front (the baseline the new tunnels are told apart by). (default: true)
- `timeoutSeconds`: Local budget in seconds (default: 300)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md. On pylabview the helper is COMPOSED and run over ActiveX, and every later step inherits the backend. A VI the composer wrote needs none of this - it is wired and its user events registered in the compose - and is answered as already wired.

## `pylv_apply`

```text
MUTATING: the whole pylabview edit cycle in one round trip - close the active project,
pylv_extract, run the helper scripts you name, pylv_rebuild, and AIXML-export the result to
check the edit landed. The bundle is an implementation detail; it is deleted on success and
KEPT (and named) on failure.
WITH NO OPERATIONS IT IS READ-ONLY AND INSPECTS: extract, then every helper script's listing
mode at once - the pane (`--show`), the subVI link table (`--list`), and the diagram
comments with their placeable anchors (`--list`). That is the call to make FIRST, because
the mapping you pass back in as operations is only knowable from those listings. Nothing is
rebuilt, the .vi is not touched, and the active project is NOT closed - inspect mode has no
side effects at all.
operationsJson is a JSON ARRAY, applied IN ORDER:
  {"op":"conpane","pattern":4815}
      change the connector pane PATTERN, moving no terminal - so no caller has to change.
      This is the fix for a pane whose conIdx values were cloned from a VI on another
      pattern. A genuinely wrong ASSIGNMENT is not repairable here: regenerate from AIXML.
  {"op":"retarget","from":"Old.vi","to":"New.vi","path":"C:\\dir\\New.vi"}
      point a subVI Call at a different subVI. Give `path` whenever the new VI is not in
      the same folder as the old one. A POLYMORPHIC target needs TWO entries, one for the
      instance and one for the wrapper, or the diagram caption keeps naming NI's wrapper.
      `from` takes the qualified name the inspect listing prints - a library-owned subVI
      reads `NI_Gmath.lvlib:Error Function.vi` - or just the file name when only one link
      ends in it. `to` is always a plain file name; a library-owned NEW target is refused,
      because its owning-library path cannot be derived.
      The new subVI must keep the connector pane contract - check both with
      lvai_connector_pane first; this tool cannot. When it is breached the symptom does
      not mention linking at all: LabVIEW reports the CALLER as not executable.
  {"op":"placeLabels","place":"9001:130,9002:140","side":"auto","gap":20}
      move diagram comments onto the nodes they describe. `side` is auto|above|below and
      auto is right nearly always: a comment about a subVI CALL goes below it, because the
      subVI's own label already occupies the space above.
WHY THIS IS ONE TOOL AND NOT FOUR CALLS. pylv_rebuild answers with a `gatesNotChecked` list
because it cannot check them from where it stands. The first of those gates is the
expensive one: LabVIEW does not lock a .vi, so a rebuild under a loaded VI succeeds and
LabVIEW then keeps serving its stale in-memory copy - a verification run afterwards
confirms the VI you REPLACED. Worse, the usual way to release it (lvai_open_file) makes
LabVIEW COMPILE the VI, and the VICD blocks that leaves are copied through unparsed, so the
rebuilt file carries compiled code describing the state before your edit. Measured
2026-08-24 on one VI pair: 0 VICD blocks generated, ran and wrote its files; 3 VICD blocks
returned 1039 "VI was aborted" and then wedged LabVIEW into a restart. Hence closeProject
defaults to true and runs FIRST.
AND EVERY EDIT STRIPS THE COMPILED CODE before the rebuild (`stripCompiled` step), marking
the VI source-only so LabVIEW recompiles it from the diagram on load. Closing the project
is not enough: a VI CONVERTED while its callees were loaded already carries VICD when it
is written. Measured 2026-09-29: a conpane-only edit on such a VI ran and returned 0 where
the unedited VI returns 8 - error out clean, execState 1 - and a 64-bit user saw the same
route crash LabVIEW. The rebuilt file is then checked for a VICD block, and one left
behind fails the call.
Needs no running LabVIEW for the pylabview half. closeProject and verify do; when LabVIEW
is unreachable both are reported as skipped, with why - an unreachable LabVIEW cannot be
holding the VI in memory either, so skipping the close is safe rather than silent.
```

- `viPath`: Absolute path to the .vi to edit - WILL BE OVERWRITTEN unless operations is empty
- `operationsJson`: JSON array of operations, applied in order. Omit or pass [] for the read-only inspect mode, which extracts and returns every listing instead of rebuilding.
- `closeProject`: Save and close the project active in the IDE before extracting (default: true)
- `verify`: AIXML-export the rebuilt VI, report its Call targets, and read whether LabVIEW can still run it. (default: true)
- `bundleDirectory`: Where to put the extracted bundle. Defaults to a fresh temp directory, deleted on success. Pass a path to keep it - useful when an operation needs debugging.
- `timeoutSeconds`: Local budget in seconds, per step (default: 120)
- `gateOnExecState`: Let a NOT EXECUTABLE result make `ok` false. On by default, because `ok` is the field a script reads before moving on, and a retarget that breaks the connector pane contract is invisible everywhere else - measured, a caller was Error 1003 while callTargets, the export and validate were all green. `ok: false` is a measurement and NOT a rollback: the .vi on disk carries the edit. Turn it OFF for a deliberately unfinished intermediate state. The placeholder-plus-retarget route is built out of those, and a gate that forbade them would break the one workflow this tool exists to serve. (default: true)
- `backend`: Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI service at all). On pylabview every step inside this call follows - each VI it writes is COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit it to use the server's default. docs/aixml-backends.md.

## `pylv_extract`

```text
Extract a .vi, .ctl or .llb into a directory of XML plus binary sidecars, and annotate the
numbers with names. Does NOT modify the source file, and needs NO running LabVIEW.
This reads what AIXML cannot: icons, panel images, layout, decorations, and the diagram of
a VI whose constructs the AIXML generator refuses. Measured, 38 of 38 .vi and .ctl files
round-tripped content-identical on LabVIEW 2026 Q3.
A .lvclass, .lvlib or .lvproj is NOT an RSRC container in LabVIEW 2026 - those are already
plain XML on disk, so read them with an XML parser instead. This tool reports that rather
than failing obscurely.
annotate (default true) writes primitive and terminal names in as XML comments -
`<primResID>1050</primResID><!-- Add -->`. A trailing `?` marks a name resting on fewer
than three sightings in the corpus; 218 of the 574 terminal roles rest on one. The comments
are inert: a rebuild from an annotated bundle is byte-identical to one from an
un-annotated bundle, measured.
Expect warnings on stderr about blocks that could not be parsed - VITS alone falls back on
nearly every file. Those blocks are copied through verbatim, which is why the round trip is
lossless, so they are reported separately from an actual failure.
```

- `viPath`: Absolute path to the .vi/.ctl/.llb to read
- `outDirectory`: Directory to write the XML bundle into; created if absent
- `annotate`: Write primitive and terminal names in as XML comments (default: true)
- `timeoutSeconds`: Local budget in seconds; clamped to what an MCP client will await (default: 45)

## `pylv_rebuild`

```text
MUTATING: rebuild a .vi from an XML bundle produced by pylv_extract, writing to viPath and
overwriting whatever is there. Needs NO running LabVIEW.
THE PATH MUST NOT BE LOADED IN LABVIEW, and the failure is silent rather than loud.
Measured: LabVIEW does not lock a .vi file, so this write always succeeds - but LabVIEW
keeps serving its in-memory copy, so a verification run afterwards confirms the VI you
REPLACED, not the one you built. Call lvai_close_vi on the target first. That tool needs
the VI to be a member of a project that is active in the IDE, which is why generating into
a project matters; for a loose VI, rebuild to a fresh path instead.
Annotation comments left by pylv_extract are ignored on the way back in, so an annotated
bundle rebuilds byte-identically to an un-annotated one.
Byte equality with the ORIGINAL is the wrong acceptance test: LabVIEW compresses block
sections at zlib level 9 and pylabview at level 6, which shifts every offset after the
first section. Compare decompressed block content, or just load the result.
Verify afterwards - this tool cannot. Read ExecState through VI Server (1 is eIdle, 0 is
eBad), then AIXML-export the result and confirm the change you intended is present.
```

- `mainXmlPath`: Absolute path to the bundle's MAIN .xml (the one pylv_extract reports as mainXml)
- `viPath`: Absolute path of the .vi to write - WILL BE OVERWRITTEN
- `timeoutSeconds`: Local budget in seconds; clamped to what an MCP client will await (default: 45)

## `pylv_route`

```text
Decide, by measurement rather than guess, whether an existing VI can be edited through
AIXML or has to go through pylabview - and say why. Call this BEFORE planning any edit.
Two checks, because one is not sound:
A. export the VI and hand the export straight back to ValidateAIXML without changing a
   byte. errorCode 1 means AIXML cannot rebuild this VI at all.
B. scan the export for node families NI publishes as unsupported - Event Structure,
   Timed Loop. These pass check A with errorCode 0 and then come back GUTTED: measured on
   NI's own VI, every CaseFrame gone and one frame left labelled '[0] Timeout'. Check A
   alone would route them to AIXML and destroy them.
Context for the answer: over 1687 of NI's VIs, 1679 export but only 627 regenerate. For
editing existing code AIXML is the minority route, and 737 of the 1052 failures are a Call
to the VI's own subVIs - which is a capability AIXML lacks entirely, not a bug.
This tool decides; it does not switch. A pylabview edit is a surgical change to an object
heap, so it cannot be synthesised from a high-level request - you still have to author it.
Needs a running LabVIEW for check A.
```

- `viPath`: Absolute path to the .vi to assess
- `timeoutSeconds`: Local budget in seconds (default: 180)

## `pylv_status`

```text
Whether the bundled pylabview is present and usable, where it is, and what it is made of -
Python version and architecture, the pinned upstream commit, when it was provisioned, and
whether the two name tables travelled with it.
Start here: every other pylv_* tool is unusable without the bundle. It is about 38 MB, so
it is not committed: a CHECKOUT has none until tools\pylabview\provision.ps1 has run,
while a BINARY install carries it inside the release archive - every release up to and
including v0.9.0 shipped without one, and there the fix is updating the plugin, not
running a script that is not installed.
Needs NO running LabVIEW and no Python installation. The bundle is isolated by a
pythonNNN._pth file beside its python.exe, python.org's own embeddable mechanism, so it
ignores PYTHONPATH, PYTHONHOME, the registry and every site-packages directory.
```
