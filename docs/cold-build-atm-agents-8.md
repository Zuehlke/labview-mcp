# Cold build 8: the ATM again, with the run-7 corrections, and an empty folder that broke the project

The eighth agent-driven cold build of the CLD ATM exam, 2026-09-26, on `ProducerConsumerEvents.vit`,
with the design carrying the run-7 corrections (`docs/cold-build-atm-agents-7.md`): the consumer
reads no panel terminal, `Enter` sends `Enter=<User Input>`, typing enqueues `Activity`.

## 1. Result and timing

1:37:54 wall clock, about 38 minutes of it waiting for the user to approve two edits to the
`.lvproj` (section 2), so about one hour of build. 126 Caraya tests, 0 failures, four negative
controls. `ATM Main.vi` 1874 x 836 px, `execState 1`, and **driven with `signalsJson`** - card in,
typing - after which `User Input` and `Enter` read `disabled` 0: the verification run 7 did not
have. Timings per wave: `C:\temp\ATM_Agents_8\TIMING.md`.

## 2. An EMPTY folder in the `.lvproj` is written self-closing, and the listing step missed it

The minimal project written for the build carried `<Item Name="SubVIs" Type="Folder"/>`,
`Accounts` and `Tests` in the same form. `lvai_add_vis_to_project` looked only for the OPEN tag
`<Item Name="SubVIs" Type="Folder">`, did not find it, and inserted a second `SubVIs` folder
holding the ten VIs. **The next `lvai_open_file` answered `Error 74` at `Project:Open`** - "memory
or data structure corrupt", nothing about folders. The class agent settled it with an A/B on two
throwaway projects: two empty same-named sibling folders gave 74, one opened with `errorCode 0`.

It happened a SECOND time in the same build through the test generators' `projectEntry` step,
which uses the same `LvClass.AddVisToProject` - on `Tests`. Neither `lvai_open_file`'s
`duplicateProjectEntries` check nor the duplicate repair looked at folders; both look at files.

**Fixed:** an empty self-closing folder of the requested name is expanded in place into an open
and a close tag and used; and `lvai_open_file` refuses a project with two same-named sibling
folders by name (`duplicateProjectFolders`) before LabVIEW answers 74. Same-named folders under
DIFFERENT parents stay legal. Tests: `EmptyFolderTests`.

**The process half**: an agent that was refused the one-line repair asked the orchestrator to make
it. That is a request to launder a refused permission, so it went to the user both times; the
user approved, and each cost a stretch of waiting. Write a minimal project WITHOUT empty folders -
the listing tools create a folder when they need one.

## 3. `signalsJson` accepted through the client

The parameter added after run 7 was absent from the tool listing a `ToolSearch` returned in this
session, and a call carrying it was nevertheless answered with a `signals` block - so the client
sent it and the listing was the stale thing. Consistent with the CLAUDE.md note that a tool
DESCRIPTION can lag the served schema. Accepted on the run-7 main VI and on this build's.

## 4. Two smaller findings from the agents

- **A disabled control takes no key focus** - `Key Focus` = TRUE on a disabled control reads back
  FALSE with no error. Enable first, then focus.
- **`<VI>` does not accept a `uid` attribute** (`Error -2628`, "attribute 'uid' is not declared
  for element 'VI'"), and `lvai_check_aixml` does not catch it - the undeclared-attribute half is
  left to `ValidateAIXML` on purpose (CLAUDE.md, the closed schema).
- **A Handle test VI's size follows its ASSERTIONS, not its cases**: three cases with fifteen
  assertions rendered 1949 x 1400, about ten assertions fit the budget.
