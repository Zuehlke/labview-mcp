# Creating a library, and listing a VI at target level

Two gaps the IconDemo build of 2026-10-17 had to close by hand: no tool created a `.lvlib` (the
library was written as XML and listed in the `.lvproj` by hand), and `lvai_add_vis_to_project`
could only put a VI into a folder, so a VI meant to sit directly under My Computer was listed by
hand too. Both are tools now. The colleague's fork had neither.

## 1. `lvai_create_library`

NI's route is two VI Server methods, the pair NI's own AddActor provider uses and
`scripts/lvai_create_actor_library.xml` already drove: `{LV.Application}` `Library.Create` in the
ACTIVE project's application instance, then `{LV.Library}` `Save` with the path. The helper is
`scripts/lvai_create_library.xml`; it reads the saved `Path` back and closes the library
reference, because a leaked provider refnum kept a new class in memory past the project close
(`lvclass-creation.md`).

The tool's order follows the open/closed rule in CLAUDE.md - NI's API writes with the project
open, our own file edits need it closed:

1. checks with no LabVIEW: an absolute `.lvlib` path that does NOT exist (an existing one is
   refused, never overwritten), the project file, the description's Windows-1252 text, the
   members' JSON;
2. opens the project and makes it active;
3. `Library.Create` + `Save`;
4. the description through `lvai_set_class_description` (`checkUnsaved=false` - a library made in
   this call has nobody's work in it);
5. closes the project and writes `<Item Type="Library">` at target level, once;
6. the members through `lvai_add_to_library` (NI's `AddItem`, which relinks the callers that are
   LOADED - and the tool loads and saves the others first, §6), then closes again and takes out
   any member entry left loose at target level;
7. verifies from the files: the `.lvlib` is a `<Library>`, its description and members read back,
   the project lists it.

The icon stays a separate call, `lvai_set_owner_icon` with the project closed (`class-icons.md`).

## 2. `lvai_add_vis_to_project targetLevel=true`

Without it every VI goes into a folder, deliberately: an entry at target level is also what
LabVIEW's own save writes for a VI it adopts, so the tool moves a requested VI OUT of target level
into the folder. `targetLevel: true` is the caller saying target level is meant: the VIs are
inserted before `Dependencies`, nothing is moved, a VI already listed anywhere is not listed again,
and the answer says `targetLevel: true` and `Listed at target level.` An empty `folderName` is
still refused rather than read as target level.

## 3. Measured 2026-10-17, both backends

The same call list on pylabview and grpc, each in a fresh project with two generated VIs
(`Double It.vi`, `Triple It.vi`):

| step | pylabview | grpc |
|---|---|---|
| `lvai_add_vis_to_project targetLevel` | `added: 2`, both directly under My Computer | same |
| the same again | `added: 0`, nothing moved | same |
| `lvai_create_library` with description and both VIs | `ok`, library created over ActiveX, description and 2 members read back, project lists it | `ok`, the same through NI's service |
| the same library again | `libraryExists` | same |
| the members afterwards | `execState 1`, `Double It(21)` = 42, no `VICD` | same |

The loose VI entries were already gone after the members were added: LabVIEW's own save dropped
them once they belonged through the library, so `looseEntriesRemoved` was empty both times.

**A fixture difference found on the way, not a defect of these tools**: a `<FreeLabel _name="c">`
composed without complaint on pylabview and was refused by NI's schema on grpc (`attribute '_name'
is not declared for element 'FreeLabel'`). The composer is more lenient than `ValidateAIXML` about
undeclared attributes; an AIXML document meant for both backends is only proven by grpc.

## 4. Access scope and virtual folders (2026-11-11)

The user's rule of 2026-11-11: two or more related NEW VIs go into one new library, its VIs in
virtual folders named for their function, and a subVI that only the library's own VIs call is
PRIVATE. Existing code is not moved into or out of a library unasked.

- **Scope through NI's API, not the file.** `{LV.Library}` `Set Source Scope` takes the item as
  `SourceRef` and NI's `LibraryItemScopeEnum` as `Scope` (1 Public, 2 Private) - the spelling NI's
  own export writes; ValidateAIXML accepted three guessed method names and four guessed parameter
  names alike, by position, so only the export settles it. `lvai_add_to_library` `scope: "private"`
  sets it on each new item and verifies `NI.LibItem.Scope` 2 in the saved `.lvlib`
  (`verify.scopeWrong`). Measured on `experiments\library-2026-11-11`: `Internal Helper.vi` came out
  with the property, `Public Api.vi` (no scope asked) without.
- **Folders** come from `lvai_move_to_folder`, which handles a plain `.lvlib` and creates the folder
  at the root - `lvai_add_to_library`'s `folder` refuses one that does not exist yet.
- After both, `lvai_exec_state` over the members: membership changes every member's qualified name.

## 5. Regenerating a library member keeps it a member (2026-11-21)

**The symptom.** An agent regenerated `Sample Logger.vi`, the public member of
`Sample Logger.lvlib` that calls the library-private `Process Command.vi`, from AIXML whose
`<VI _name>` was the bare `Sample Logger.vi`. The composer wrote the file with NO `LIBN` block: it
was no longer a member, LabVIEW refused its call ("This VI cannot access the referenced item in
private scope") and it read execState 0. The agent put `Sample Logger.lvlib\3ASample Logger.vi` in
`_name` by hand. Reproduced on copies of the application folder with the composer alone: bare name,
no `LIBN`, execState 0; qualified name, `LIBN` written, execState 1.

**The rule, on the pylabview backend** (`LibraryMembership`, in the convert step every
`lvai_generate_vi` goes through): before composing onto an EXISTING file, the owners that file names
for itself (its `LIBN` block) decide the name -

- a bare `_name` is qualified with them, in a copy of the document - the caller's AIXML is not
  edited - and the answer says so under `libraryMembership` (`nameRewritten`, `composedAs`);
- a `_name` already qualified with the same owners is left alone;
- a `_name` qualified with OTHER owners is refused as `libraryOwnerMismatch`: moving a VI between
  libraries is `lvai_add_to_library`'s job;
- a loose file, a new path and a CLASS member (an owner chain with a `.lvclass` in it) change
  nothing - the class tools give their members their class themselves.

`LibraryMembershipTests`. The grpc route is unchanged and was not measured for this.

## 6. Callers outside the items relink only while loaded (2026-11-23)

**Symptom.** A cold build put `Sensor.lvclass` into `ChangeTests.lvlib` after its Caraya tests were
written. `lvai_create_library` answered `ok`, `Main.vi` (an item too) and every class member read
`execState 1` - and all four test VIs were eBad: `Missing subVI Sensor.lvclass:Add Reading.vi`, and
each class constant `Refers to a missing LabVIEW class`. The test VIs sat in the project's `Tests`
folder, not in the library, and were not loaded when the class was added.

**Measured on a two-file reproduction** (now the fixture `Fixtures/library-relink`):

| item added | caller outside it | caller loaded during the add | caller afterwards, read cold |
|---|---|---|---|
| `Cls.lvclass` (static member `Touch.vi`) | `C.vi`: class constant into `Cls.lvclass:Touch.vi` | no | eBad, the two errors above |
| `Cls.lvclass` | `C.vi` | yes, then saved | execState 1 |
| `A.vi` (plain VI) | `B.vi` calls `A.vi` | no | execState 1 |

So `Save All This Library.vi` relinks what LabVIEW holds in memory. A plain VI's caller found the
renamed `Lib.lvlib:A.vi` at its path anyway; a class's qualified name did not resolve. A relink
also sets no Mods bit, so the caller is saved because it is LISTED, not because it looks modified
(`lvai_save_modified_vis` saves every listed path).

**What the tool does now** (`relinkCallers`, default true): before the first `AddItem` it scans
every `.vi`/`.vim` under the project folder for link records naming an item's file name - for a
class item also each member VI's - leaving out the items and the class's own members
(`LibraryTools.OutsideCallers`, the `ViFile.Links` heuristic `lvai_add_to_class` uses). It opens
each through the active project, adds, and saves them; the answer's `callers` lists `found`,
`saved` and `notSaved`. A caller that does not open stops the call before anything is added
(`callerOpenFailed`); one that is not saved makes `ok` false. The scan is a heuristic: a same-named
file elsewhere costs one load and one save, never a missed caller. A caller outside the project
folder is not found.
