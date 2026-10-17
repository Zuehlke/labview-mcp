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
6. the members through `lvai_add_to_library` (NI's `AddItem`, which relinks callers), then closes
   again and takes out any member entry left loose at target level;
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
