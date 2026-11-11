# Working in this repository

Rules that came out of building this server. Each one exists because ignoring it cost real work.
This file holds the RULES; the measurements, the history and the wrong turns behind them are in
`docs/` (the pointer after each rule) and, verbatim as of 2026-11-01, in
`docs/claude-md-archive.md`. Read those when a rule's reason matters. Rules are stated as
measured: if a measurement contradicts one, fix the rule here and say what it claimed before.

## Agents first

**LabVIEW work is DELEGATED to the matching `labview-*` agent, not done in the main session.**
The user's standing rule of 2026-09-27. Work directly only when the user asks for it.

| Request | Agent |
|---|---|
| a new VI | `labview-vi-generator` |
| implement a SUPPLIED VI whose front panel must be kept (exam template) | `labview-vi-generator` - pass the supplied VI; it grafts (Phase 6g) |
| change an existing VI | `labview-vi-editor` |
| a class, class hierarchy or interface | `labview-class-generator` |
| a library (`.lvlib`), its members, description and icon | `labview-class-generator` |
| unit tests (default framework) | `labview-caraya-unit-test` |
| LUnit / VI Tester tests, only when named | `labview-lunit-unit-test` / `labview-vitester-unit-test` |
| documentation | `labview-doc-generator` |
| a DQMH module or event | `labview-dqmh-module` |

The agent definitions carry rules a session working directly never reads. Relay an agent's
`NEEDS CLARIFICATION` block to the user verbatim and continue THAT agent via `SendMessage`; never
answer it on the user's behalf.

**A plugin user's main session never sees this file**: it reads `docs/orchestrator-brief.md`, the
server's MCP `instructions`. Keep that brief in step with every change to this section.
`docs/server-instructions.md`.

**A MULTI-VI APPLICATION STARTS WITH A BUILD CONTRACT, written by the orchestrator BEFORE the first
agent** (the user's decision of 2026-11-01). `build-contract.json` in the build folder names every
VI with its path, owner, builder, connector pane and callees; `lvai_check_build_contract mode=plan`
checks it without LabVIEW and answers the build WAVES. Every builder gets the contract path and
implements against it, the main-VI agent authors its scaffold in parallel with wave 0, and
`mode=verify` gates each wave before the next is built. `docs/build-contract.md`.

**TWO OR MORE RELATED NEW VIs GO INTO ONE NEW LIBRARY** (the user's rule of 2026-11-11). A single
standalone VI stays loose; existing code is never moved into or out of a library unasked. The
orchestrator does it after the last wave: `lvai_create_library` with the API VIs,
`lvai_add_to_library scope=private` for subVIs only the library calls, `lvai_move_to_folder` per
function folder (never flat), then `lvai_exec_state` over the members. The VI agents search the
PROJECT's own VIs before the palette. `docs/orchestrator-brief.md`.

**ONE AGENT, ONE OUTPUT DIRECTORY.** Two agents in one folder overwrite each other's files. Give
each its own folder (`<project>\Tests\<ClassName>\`) and say so in its prompt.

**PARALLEL AGENTS SHARE ONE LabVIEW, SO NO AGENT MAY OPEN OR CLOSE A PROJECT OR SWAP.** A project
is global to the instance, and a close SAVES LabVIEW's copy over the `.lvproj`. Agents GENERATE; the
orchestrator opens projects, SWAPS and lists VIs (`lvai_add_vis_to_project`, never by hand).
Build by DEPENDENCY: leaf VIs in parallel with no project open, then one agent for the callers.
The server ENFORCES it: `lvai_open_file` with a project and `lvai_close_active_project` are
refused (`projectChangeWhileParallel`) while top-level tool calls overlap or overlapped in the last
60 s (`ParallelCalls`; an agent's project open crashed LabVIEW on 2026-11-11).
Restart LabVIEW between two builds of the same application: a same-named VI of the earlier build,
still in memory, captures a bare-name `Call` in silence.

## Generating LabVIEW code

**First decide what kind of thing you are looking for:**

| What you want | Construct | Where to look |
|---|---|---|
| a **whole working diagram** - state machine, producer/consumer, TDMS streaming | a shipping example to read and adapt | `lvai_example_index` |
| a computation on **data** - read a file, sort, parse, compare | primitive `Node`, or a subVI `Call` | `lvai_palette_index`; terminal names from an export |
| a **property or action of a LabVIEW object** - a VI, control, panel, project, the application | `Property Node` / `Invoke Node` | `lvai_vi_server_reference` |
| a **whole application skeleton** | an NI `.vit` template, copied to a `.vi` | `templates\Frameworks\DesignPatterns\`; `docs/labview-vit-templates.md` |
| a **VI's icon** | neither - AIXML cannot carry one | `lvai_set_vi_icon` |

"Get this VI's icon", "list a project's items", "is this VI broken" are properties and methods,
never palette functions.

**Check whether NI already built it** with `lvai_example_index`, and read the hit with
`lvai_convert_vi_to_aixml`. Do this first for anything pattern-shaped. Toolkit-only examples
(FPGA, RT) are held back unless `includeSpecialised`; the index is cached, and after a LabVIEW or
add-on install it needs `refresh=true` once. A hit may be a `.lvproj` - use
`lvai_describe_project` then. `docs/example-corpus.md`.

**Exports of installation VIs are cached** under `%USERPROFILE%\.labviewmcp\cache\aixml` (not
`%LOCALAPPDATA%`; `CacheDirectory.Root` is the authority). Your own code is never cached, because
an export depends on its subVIs too. So browsing examples is cheap - browse. On the pylabview
backend the server reads the examples into the cache in the background after a start and deletes
reader entries of older reader code (`ReaderPrewarm`; progress in `pylv_status readerPrewarm`,
coverage - warm, partial, cold - in `exportCache`). Triage several
candidates in ONE `lvai_convert_vis_to_aixml`; LabVIEW serialises the work, so fanning out
`lvai_*` calls gains nothing.

**Reuse a palette VI before rebuilding anything.** Query `lvai_palette_index` first; a hit is the
design. Rebuild from primitives only when there is no hit AND the target fails to resolve, and
say which. A third-party package (OpenG, JKI, MGI) is not a reason to rebuild: name it in the
report as information and call it.

**How a `Call` target resolves - by NAME against what the installation can find:**
- A plain VI in `vi.lib`, `user.lib`, `instr.lib` or an LVAddon resolves by its bare name, with
  or without a palette entry.
- A library member needs its qualifier, `openg_picture.lvlib\3ADraw Image from File__ogtk.vi`.
  The qualifier is FLAT (no library folders), and it is not derivable from the index: get it from
  an export of a VI that calls it, or settle both spellings in one throwaway validate.
- **A VI inside an `.llb` is not a reason to give up** - search for it and try the call.
- **A path never resolves**, in any spelling.
- **Project-local code resolves by bare name once it is LOADED** (`lvai_open_file`, through its
  project when one is active). `ValidateAIXML` still refuses it, so use `lvai_generate_vi`, which
  converts past an `Unsupported SubVI` refusal and gates on executability. A `.ctl` is never a
  `Call` target. `docs/aixml-call-loaded-vi.md`.
- **A miss in the index is not proof that a call is illegal.**

Every validation leaves a scratch `LVMCP Validate.vi` (numbered `…2`, `…3`) in memory that cannot
be closed; "Don't Save - All" at exit is always safe. `docs/scratch-vis-in-memory.md`.

**Look terminal names up, never guess them** (`lvai_vi_terminals`, or the contract's
`callSkeletonsFor`). They are literal and surprising (`x > y?`). Batch every lookup into ONE call:
`lvai_aixml_reference node=` and `lvai_vi_server_reference query=` take comma-separated lists.
**Optimise the number of calls, not the cost of one** - a round trip is a model turn of about 7 s,
and model latency is several times the time spent in tools. `docs/workflow-economics.md`,
`docs/token-economy.md`. A step that is cheap for LabVIEW and expensive in turns is a tool waiting
to be written.

**DRAW WHAT A LABVIEW DEVELOPER WOULD** (user-forum feedback, 2026-11-11): a compare against 0 is
`Less Than 0?` and kin, +1/-1 is `Increment`/`Decrement` (`primitiveForConstant`); an error cluster
goes straight into `Not`/`And`/`Or` and a `stop` terminal, never via `Unbundle By Name status`
(`errorStatusUnbundled`). `lvai_generate_vi` hides every constant's LABEL (`tidyConstants`; the
text stays, so binding by label works), shows white-space strings as codes and array constants at
full size; the composer writes an error constant as NI's brown icon. `docs/aixml-reference.md` §9.

**Wire a constant only where the callee marks the input `required`.** Validation enforces
`required` and says nothing about the rest. **Write `connection=` on every terminal that has a
`conIdx`: an omitted one means REQUIRED**, and a required OUTPUT breaks every caller that leaves it
unwired. `lvai_check_aixml` repairs the output case.

**AIXML values:** a backslash in `value=` is `\5C` (a raw one refuses the whole file, `Error 42`);
a line break is `&#10;`; commas inside a `value` are structure. A mode attribute can change a
node's output type only together with its wiring (`Read from Text File` `readLines` needs `count`)
- copy a variant already in the state you want.

**Author AIXML by writing the file directly** - a shell eats `\3A` and `\5C`. Put NO XML comment
and no processing instruction before or inside `<VI>`: before it, the VI comes out EMPTY with
`errorCode 0` (a VI of ~3 170 bytes is empty); inside it, `Error 42`. Only after `</VI>` is ignored.
`docs/aixml-frontpanel-extension.md`.

**What validation does NOT catch - `lvai_check_aixml` does, without LabVIEW, and `lvai_generate_vi`
runs it:** a `uid_parent` naming no element (silently placed on the top-level diagram); a duplicate
`uid` (silently renumbered); a Ring or enum value outside its items, or an enum value written as a
LABEL (discarded to `0`); a non-empty `timestamp` value (discarded - so a round-trip test of a
timestamp through AIXML constants pins nothing); `uid_parent` contradicting the nesting (the NESTING
decides, in both directions); a `FreeLabel` that must be written INSIDE its loop or frame.
`uid="0"` is a sentinel LabVIEW renumbers. **Number uids from 4200**: low uids fall in LabVIEW's
reserved range and each costs a DWarn. `AixmlCheck.SafeUidBase` and `scripts/aixml_lint.py` must
agree. `docs/aixml-reference.md`, `docs/aixml-lint.md`.

**The schema is CLOSED.** `<Control>`, `<Indicator>` and `<Constant>` require `value`; a Control
needs `outputs`, an Indicator `inputs`, even unwired (`outputs="value:"`); `<VI>` requires
`description`. A missing or undeclared attribute costs the whole document as `Error -2628` with
nothing written - and only `lvai_validate_aixml` names the cause (line and column); convert does
not. `docs/cold-build-conveyorrig.md`.

**A property name with its category dropped KILLS LabVIEW in `ConvertAIXMLToVI`**
(`read+Version Number` for `Application\3AVersion Number`). Convert and validate refuse such a
document before the RPC (`propertyNameWouldCrashLabview`). `docs/aixml-reference.md`.

**Always generate a VI into a project.** If none exists, write a minimal `.lvproj` (§2 of
`lvai_lvproj_reference`) and list the VI in it. `ConvertAIXMLToVI` cannot overwrite a VI LabVIEW
has loaded (`Error 1357`), and only `lvai_close_vi` releases it - which needs the project ACTIVE
and the VI opened THROUGH it. A generated helper runs in the AI addon's application instance: it
cannot see the IDE's windows, so a window measurement from it proves nothing.
`docs/vi-server-reference.md`.

**Generate with `lvai_generate_vi` (or `lvai_generate_vis`), not validate-then-convert by hand.**
It validates, converts, measures the pane and the diagram size, lays out the diagram with our own
engine (`cleanUpDiagram`; never on a DQMH Main.vi) and cleans up the panel of every VI that is not
the main GUI (`cleanUpPanel` false for the main VI). `ok: false` with `failedAtStep: connectorPane`
still wrote the VI. `docs/bulk-operations.md`, `docs/diagram-layout.md`.

**Validate, then verify by RUNNING.** Validation says nothing about behaviour, and for a class wire
it is STRICTER than conversion (convert writes, validate refuses). Run with
`lvai_run_vi_and_read_values`: inputs go in as text and land as the control's type (numeric,
boolean, path, string; an enum by name or index; an array or cluster as LabVIEW's XML); a value
must not contain a line break. A VI that does not end runs only with `runForMs` - the untimed
helper blocks the whole gRPC service. An event-driven VI is verified by DRIVING its events
(`signalsJson`), not by a start-up snapshot. Never report success from an empty answer.

**EVERY VI WE CREATE CARRIES `error in` AND `error out`, on the BOTTOM ROW of the pane** (the user's
rule of 2026-09-12):
- the input is labelled `error in`, not NI's `error in (no error)` - but a CALLEE's terminal name
  stays as that callee declares it;
- the type is `cluster{bool.status,int32.code,string.source}` on both;
- `error out` is ONE output carrying every error path - join forks with `Merge Errors`, carry the
  chain through a loop in a SHIFT REGISTER (a For Loop tunnel indexes);
- a VI generated NEW has `error in` `required` (without `error in`: its most important input);
  `error out` and every other output `recommended` (the user's rule of 2026-10-31);
- an EXTENDED or revised VI keeps every flag as it was, and the rule does not retrofit existing
  VIs unasked;
- a top-level VI with no callers is not an exception.

**Ask `lvai_connector_pane` where terminals go; never carry a map in your head.** `conIdx` is a
POSITION and its meaning depends on the pattern. A new VI gets the station's `DefaultConPane` from
`LabVIEW.ini` (4833 here) - **that file is read-only to us**. A VI naming a conIdx the default
lacks is put on a larger pattern by the generator (4834 for up to 19). **A class member is
authored against 4815** (class in 11, more inputs 10/9, `error in` 8, class out 3, more outputs
2/1, `error out` 0), because the class tools re-pane it. A wrong-looking pane is often the PATTERN,
not the assignment - fixing the pattern moves no terminal. A wrong assignment is fixed by
regenerating; moving terminals through the heap kills LabVIEW. `docs/connector-pane-repair.md`.

**Every generated VI carries at least one diagram comment, POSITION-INDEPENDENT and under ~45
characters** ("One iteration per element of Values", never "Scale by 9/5"). A box does not grow,
and a longer comment is clipped in silence. What one loop iteration or one frame does goes into
the structure's `label=` (its subdiagram label). Placing comments on nodes is OPTIONAL and costs
about 30 s each (`scripts/pylv-place-labels.py`; a comment about a subVI call goes BELOW it). On
the default path, do not touch pylabview. `docs/diagram-comments.md`.

**A BLOCK DIAGRAM STAYS AROUND 1920 x 1080, AND A REPEATED OPERATION BECOMES ONE GENERIC SUBVI**
(the user's rule). It is measured: `lvai_check_aixml` answers `diagramChain` (budget 10 stages)
before generating, `lvai_generate_vi` answers `diagramSize`/`withinBudget` after. An over-budget VI
is not done. Width follows the longest dependency chain - fold a SEQUENTIAL stretch into one subVI;
height follows what sits in parallel. A helper that repeats one operation takes an ARRAY and knows
nothing about the application (`Set Controls Disabled.vi`: references plus one boolean). A generated
test VI has the same budget: about three cases per test VI. `docs/diagram-size.md`.

**Control references:** a bound reference is authorable -
`<Node _name="VI Server Reference" element="<label>"/>` - so a GROUP is an array of REFERENCES,
never of names. A VI reads its own panel with `{LV.VI}` `Front Panel` -> `{LV.Panel}` `Controls[]`
and an unwired `reference`. `docs/control-reference-binding.md`.

**A producer/consumer that passes every check can still not work:** a value the consumer needs
travels WITH the command, read in the producer's event frame - never a control terminal read
beside a `Dequeue` (`controlReadBeforeWait`). Typing counts as activity where a spec has an
inactivity timeout (`Value Change` + `Update While Typing?`). `docs/cold-build-atm-agents-7.md`.

**A regeneration costs the whole swap cycle** (sockets back, icon gone), so batch documentation
changes into a regeneration you make anyway.

**Everything written INTO a VI is English by default** - descriptions, terminal descriptions,
comments. A German request does not imply German text. Control NAMES stay as the caller specified.

## Calls into your own code and unit tests

**A UNIT TEST CALLS ITS SUBJECT AS A STATIC SUBVI. ALWAYS. Never through VI Server** (the user's
correction). `lvai_generate_test`, `lvai_generate_class_test` and `lvai_generate_method_test` call
the subject DIRECTLY by default - they open its project, name it in the `Call`, and fall back to
the placeholder route (`route` says which). The VI Server variant (§3c of
`docs/labview-unit-testing.md`) is a named fallback only. Cases of ONE test VI run in PARALLEL, so
a fixture a case writes cannot be shared.

**When a direct call cannot be used, `lvai_placeholder_subvi` generates the socket** - never hunt
the palette for a stand-in and never hand-write one. The clone must be EXACT (a type difference is
`Error 7, Bad Linkage`). A placeholder loses every typedef on the pane, silently: check
`typedefTerminals`/`coercionDots` and repair with `lvai_bind_typedef_constants` - so **name every
constant you wire into a generated call after the terminal it feeds**. A class inside a cluster
becomes a `path` element; bind the caller's cluster afterwards (`lvai_bind_pane_typedef`, or
`lvai_swap_subvis` with the `.ctl` as the constant's class). `docs/typedef-constants.md`.

**A DYNAMIC DISPATCH INPUT IS A REQUIRED TERMINAL**: leave it unwired and the test is `Error 1003`.
Feed it a class constant, authored as a `path` constant and converted with `{LV.Constant}`
`Replace` AFTER the nodes (`lvai_swap_subvis constantsJson`). `docs/labview-unit-testing.md` §3d.

**`lvai_swap_subvis`:**
- It needs an ACTIVE project - `{LV.SubVI}` `Replace` is a silent no-op outside the IDE's instance.
  `lvai_run_lunit_tests` leaves NO active project.
- It reaches calls and constants at ANY depth.
- A socket appearing on N nodes costs N calls. `socketsLeft` counts swap ENTRIES still present;
  `diagramSubVis` lists nodes with repetition.
- A node already swapped carries its QUALIFIED name.
- A constant label that occurs more than once is refused - name seeds `_name`-uniquely.
- `wiringLost` is REPORTING ONLY.
- It ends with `BD.Remove Bad Wires`, and `inputsLeftUnwired` names any connection that was lost.
- `pylv_apply` retarget is not a generation route (it leaves `Missing subVI`).

`docs/class-method-tooling.md`.

**A VI AT STATE 0 WITH A BROKEN-WIRE TEXT GETS REMOVE BAD WIRES BEFORE ANY REBUILD** (the user's rule).

**A tool tested against a plausible fixture is not tested** - build fixtures by unwrapping the
real artefact, and verify a fix by reproducing the original failure, not by the test written with
it.

**A Caraya failure names the case and nothing else** (`"FAIL"`); LUnit reports Expected/Actual.
The runner's `error out` 7002 means a failed suite. Write and run suites with
`lvai_generate_caraya_test_runner` and `lvai_run_caraya_tests`.

## Classes, libraries, typedefs

**Ask whether LabVIEW COMPILES the thing before writing it.** A class, its private data control,
its accessors, an interface, a library, an Actor Framework message, a typedef: each is created
through NI's own providers or VI Server, never by writing the file - `lvai_create_class`,
`lvai_create_interface`, `lvai_create_accessors`, `lvai_add_class_field`, `lvai_add_class_method`,
`lvai_create_library`, `lvai_add_to_library`, `lvai_create_message_class`, `lvai_create_typedef`.
`docs/lvclass-creation.md`.

**Who writes the file decides whether the project may be open.** WE write `.lvproj`, `.lvclass` or
`.lvlib` directly -> the project must be CLOSED (otherwise LabVIEW's save undoes it, or crashes).
NI's API writes it -> open is fine, and the providers REQUIRE it open and active (`Error 1055`
otherwise). Close the project before any `lvai_create_class` or `lvai_create_interface`; read the
`.lvproj` after every close - a close SAVES LabVIEW's in-memory copy over the file, and LabVIEW
adopts every open VI when it saves (the close's `projectPath` sweep removes ours).

**Order:** an interface and EVERY method it declares, before the first class that implements it
(the link is creation-time only; a declared method breaks every implementer until its override
exists - a missing override makes EVERY member of the class eBad, which only `lvai_exec_state`
sees). Class -> methods -> LIBRARY -> messages: adding a class to a library after its messages
leaves every `Do.vi` eBad. Remove a library's `<Item>` BEFORE deleting its files. Never write
`NI.Lib.ContainingLib` by hand. `docs/labview-actor-framework.md`.

**`lvai_add_class_method`** converts with `path` stand-ins, retypes the class terminals, adds the
member, sets wire rules and saves VI and class. It VALIDATES first and converts through only the
documented class-wire strictness. A re-run drops the class entry first and needs `projectPath`.
A method of the class cannot `Call` its own accessors while the class is not loaded - take
parameters on the pane, or go through placeholders. `Call Parent Class Method` is not authorable:
Actor Framework lifecycle overrides come from `lvai_create_override` + `lvai_af_add_hook`.
`docs/class-method-tooling.md`, `docs/af-override-tools.md`.

**A private class member must be STATIC**, and scope is set through VI Server, never by editing
`NI.ClassItem.MethodScope`. Do not read `NI.ClassItem.Flags` to tell dispatch from static - read
`connection=` from `lvai_vi_terminals`. `lvai_describe_class` tells interface from class parents
by opening each link's URL.

**`Execution:State = 1` IS NOT EVIDENCE THAT A REPAIR REACHED DISK - ask the FILE, not the
session** (`pylv_extract`, the saved `.lvclass`). A `Replace` on a class member persists only with
the owning class saved in the same run, and is a silent no-op outside the IDE's instance.

**Close every refnum a provider hands back** - a leaked class refnum keeps the class in memory past
the project close, and the next child is created with NO parent and no error. Read `parent index`.
"Only a restart fixes it" is a symptom, not a diagnosis.

**Typedefs:** AIXML cannot express a typedef binding; an AIXML edit de-links every typedef on the
VI and nothing reports it (`pylv_route` included) - check with `pylv_extract` first. Bind with
`{LV.Control}` `Replace`:
- class fields: `lvai_bind_class_fields`, after `lvai_describe_ctl` says the source IS a typedef -
  a bind against an ordinary control succeeds and binds nothing;
- constants: `lvai_bind_typedef_constants` for one feeding a subVI call; any other (into
  `Bundle By Name`, a queue's element type) `lvai_swap_subvis constantsJson` with the `.ctl`;
- a method's own pane: `lvai_bind_pane_typedef`.

Create a typedef with `lvai_create_typedef` (VI Server; nested typedefs in ONE run). A class field
can carry a default (`double.Gain=1`) and may be a `path`. `docs/typedef-disconnect.md`.

## Which interface to reach for

| what you are doing | route |
|---|---|
| create a NEW VI | AIXML (`lvai_generate_vi`) |
| edit an EXISTING VI | `pylv_route` first; an AIXML edit is a FULL REGENERATION |
| a class, its private data, an accessor | NI's provider VIs through the class tools |
| a DQMH module, or a vendor toolkit under `project\` | the `lvai_dqmh_*` tools (VI Server by path, generated wrappers over LOADED scripters) |
| a property or method no RPC exposes | a generated VI Server helper - the exception; say which route and why |
| `.ctl`, icon, layout, decorations, read with no LabVIEW | pylabview (`pylv_*`) |

**The AIXML tools have two backends**, NI's gRPC service and our pylabview composer, chosen by
`"aixmlBackend"` in `%USERPROFILE%\.labviewmcp\settings.json` (or `--aixml-backend`,
`LABVIEWMCP_AIXML_BACKEND`). **pylabview is the default** (the user's decision of 2026-11-05; it
was grpc, pylabview only below LabVIEW 2026); an existing settings file is switched to it once,
marked by `"setDefaultDone": true`. The composer writes the file version of the LabVIEW in use. What needs a running LabVIEW goes over ActiveX there, which
only ATTACHES, never starts LabVIEW. Pass `backend` only when you mean it.
`docs/aixml-backends.md`.

**`lvai_apply_aixml_to_vi` IS NOT CALLED. EVER - unless the user asks for it by name.** It answers
`errorCode 0` and changes nothing. So an edit is a full regeneration, and **a VI whose existing
front panel must survive cannot be edited**: say so and let the user choose. For a SUPPLIED panel
with an empty diagram, `lvai_graft_diagram` grafts a generated scaffold into it (system clipboard:
one graft at a time; `replaceDiagram` discards existing code). **Graft only the main GUI**; subVIs
are regenerated. A latched button stays latched in the delivered VI. A control AIXML cannot create
(Web Browser) is reached through a `ref{LV.…}` stand-in plus `lvai_bind_control_references`.
`docs/keep-supplied-front-panel.md`.

**`lvai_convert_aixml_to_vi` answers `executable`**; an `eBad` VI is a blocker, never a caveat.

**pylabview:**
- Run the whole edit through `pylv_apply` (call it with no operations first to inspect).
- **CLOSE THE PROJECT, not the VI**, before an edit: `lvai_close_vi` leaves the project loaded and
  LabVIEW compiles it.
- `pylv_apply` strips compiled code, because stale `VICD` returns wrong results or crashes 64-bit
  LabVIEW.
- `pylv_rebuild` succeeding is not verification: read the value back after LabVIEW loaded AND
  saved the VI.
- Heap text keeps LF line endings; `DefaultData` is MacRoman.

`docs/pylabview-stale-compiled-code.md`.

**Event structures:**
- **A user event carries data and the handler reads it** - that is the DEFAULT (the user's rule);
  never a Local Variable as a substitute.
- On grpc: a labelled placeholder constant into a prim input, then `lvai_set_event_data_fields`.
  On pylabview the composer writes it in one go.
- **Route the registration refnum into the structure as a `<Tunnel>`**, then
  `lvai_wire_dynamic_events` wires the dynamic terminal.
- A front-panel event can read the control terminal inside its own frame instead of an Event Data
  Node; a user event cannot.
- `lvai_generate_vi_with_events` registers front-panel events.
- A Timed Loop's timing is not in its export; it is set through a WIRED constant
  (`scripts/pylv-set-timedloop.py`).
- Logic inside a construct AIXML refuses goes through a subVI `Call` used as a slot.

`docs/labview-vit-templates.md`, `scripts/templates/README.md`.

**Not every working measurement becomes a tool.** A repeatable operation on the user's code is
productised (script, tool, tests, docs); a one-shot investigation of NI internals is written down.

## Projects and LabVIEW state

**`lvai_open_file`:**
- `Error 1025` means the `.lvproj` path does not exist (or uses forward slashes).
- **Check that the file exists before concluding anything about the machine.**
- `No Error` does not mean the project became active - read `projectBecameActive`. The cause is
  LabVIEW lacking the foreground; the tool retries with it fronted.
- A project with two same-named sibling folders, or a file listed twice, is `Error 74` on open.
  Write a minimal project with no empty folders.

**Delacor's scripters save the project as they go;** an unsaved VI in the project folder raises a
modal `Save changes before closing?` that stops the gRPC service. The DQMH tools refuse while
anything there is unsaved and answer that dialog themselves. During test runs, dismiss popping
LabVIEW dialogs yourself with the least-change button and report it.

**A modal dialog stops the whole gRPC service.** Anything that can raise one - a missing file in a
`.lvlib`, LMock's refusals, the auto-save recovery - is checked from the files first.

## When a tool call fails

`An error occurred invoking 'lvai_…'` is OUR argument-binding message; the detail is on the
server's stderr. Argument problems come back as `badArguments` data; a near-miss name is folded,
an undeclared one refused - but the Claude client DROPS undeclared keys before sending, so a tool
whose parameters are all optional guards "these arguments ask for nothing" itself. Unknown keys
inside any `casesJson`/`*Json` are refused by name. A schema `default` key makes the client demand
the argument, so defaults are folded into descriptions. **A new parameter cannot be accepted in the
session that added it**: the client fetched the tool list at session start.
`docs/tool-argument-errors.md`.

**Write down what was measured and leave the "cannot" out** - an impossibility stated as fact stops
anyone looking again. When a description explains a mechanism, check the mechanism.

## When LabVIEW disappears

- Starting LabVIEW through our tools empties `LVAutoSave` first, because a recovery dialog would
  stop the service (`keepAutoSave` opts out).
- Read NI's own log, `%TEMP%\LabVIEW_32_<ver>_interactive_<user>_cur.txt`, and copy it before
  restarting; Windows' event log never sees LabVIEW's crashes.
- The gRPC service logs separately, under `%ProgramData%\National Instruments\AIAssistants\Logs`
  (newest file).
- `dwarnCount` counts events. The `[Executing: …]` tag names where a warning was emitted, not what
  caused it.
- Validation is not risk-free: helpers are validated once and cached under
  `%TEMP%\LabVIEWMCP\helpers\` - do not clear that cache without cause.

`docs/labview-crash-signatures.md`.

## Writing things down

**Empirically derived `lvai` behaviour belongs in `docs/`, not only in the conversation.** Record
the measurement AND the symptom that led to it. Correct a document when a measurement contradicts
it, and say what the old text claimed. When a measurement contradicts a number, fix the thing that
PRODUCES the number. A rule that has to survive into a shipped build belongs here or in a served
document; `experiments/` does not ship.

## Where the knowledge lives

| Question | Document | Tool |
|---|---|---|
| Read or write AIXML | `docs/aixml-reference.md` | `lvai_aixml_reference` |
| What validation does NOT catch; lint with no LabVIEW | `docs/aixml-reference.md`, `docs/aixml-lint.md` | `lvai_check_aixml`, `scripts/aixml_lint.py` |
| AIXML without NI's service; which LabVIEW and INI the server uses | `docs/aixml-backends.md` | `backend`, `lvai_list_labview_installations` |
| A multi-VI build by several agents | `docs/build-contract.md` | `lvai_check_build_contract` |
| Where tokens and time go | `docs/token-economy.md`, `docs/workflow-economics.md`, `docs/ideen-schneller-und-weniger-tokens.md` | — |
| Tool descriptions before shortening; this file before the rewrite | `docs/tool-descriptions-archive.md`, `docs/claude-md-archive.md` | — |
| VI Server properties and methods | `docs/vi-server-reference.md` | `lvai_vi_server_reference` |
| Call targets; NI examples | `docs/example-corpus.md` | `lvai_palette_index`, `lvai_example_index` |
| Terminal names; pane positions | `docs/aixml-reference.md` §2/§8 | `lvai_vi_terminals`, `lvai_connector_pane` |
| Calling loaded project code | `docs/aixml-call-loaded-vi.md` | `lvai_generate_vi` |
| Generate one or many VIs | `docs/bulk-operations.md` | `lvai_generate_vi`, `lvai_generate_vis` |
| Run a VI and read every value; drive events | `docs/vi-server-reference.md` | `lvai_run_vi_and_read_values` |
| Is a VI broken, and why | `docs/error-list.md` | `lvai_exec_state`, `lvai_error_list` |
| LabVIEW's outside state; VIs in memory | `docs/labview-state.md` | `lvai_labview_state`, `lvai_vis_in_memory` |
| Diagram size; layout | `docs/diagram-size.md`, `docs/diagram-layout.md` | `lvai_layout_diagram` |
| Diagram comments; rendering | `docs/diagram-comments.md` | `lvai_render_diagrams` |
| Keep a supplied front panel | `docs/keep-supplied-front-panel.md` | `lvai_graft_diagram`, `lvai_bind_control_references` |
| Control references | `docs/control-reference-binding.md` | — |
| Event structures, user events, `.vit` templates | `docs/labview-vit-templates.md`, `scripts/aixml-skeletons/user-event-two-loops.md` | `lvai_generate_vi_with_events`, `lvai_wire_dynamic_events`, `lvai_set_event_data_fields` |
| Malleable VIs | `docs/malleable-vis.md` | `lvai_make_malleable` |
| VI execution settings: reentrancy, inline, priority, ... | `docs/vi-execution-settings.md` | `lvai_set_vi_execution` |
| Icons of VIs, classes, libraries | `docs/class-icons.md` | `lvai_set_vi_icon`, `lvai_set_owner_icon`, `lvai_read_icons` |
| Classes and their private data | `docs/lvclass-creation.md` | `lvai_create_class`, `lvai_add_class_field`, `lvai_create_accessors`, `lvai_describe_class` |
| Class housekeeping: description, folders, members, VI descriptions | `docs/lvclass-creation.md` §10 | `lvai_set_class_description`, `lvai_move_to_folder`, `lvai_add_to_class`, `lvai_set_vi_descriptions` |
| Interfaces | `docs/lvclass-interfaces.md` | `lvai_create_interface` |
| Class methods | `docs/class-method-tooling.md` | `lvai_add_class_method` |
| Libraries | `docs/library-creation.md` | `lvai_create_library`, `lvai_add_to_library` |
| Actor Framework; overrides | `docs/labview-actor-framework.md`, `docs/af-override-tools.md` | `lvai_create_message_class`, `lvai_create_override`, `lvai_af_add_hook` |
| Typedefs | `docs/typedef-constants.md`, `docs/typedef-disconnect.md` | `lvai_create_typedef`, `lvai_describe_ctl`, `lvai_bind_*`, `lvai_coercion_dots` |
| DQMH | `docs/dqmh-patterns.md`, `docs/dqmh-scripting.md` | `lvai_dqmh_reference`, `lvai_dqmh_*` |
| `.lvproj`, `.lvlib`, `.lvclass` structure | `docs/lvproj-structure.md`, `docs/lvlib-lvclass-structure.md` | `lvai_lvproj_reference`, `lvai_lvlib_reference` |
| Unit tests (Caraya) | `docs/labview-unit-testing.md` | `lvai_generate_test`, `lvai_generate_class_test`, `lvai_generate_method_test`, `lvai_run_caraya_tests` |
| LUnit; mocks | `docs/labview-lunit-testing.md`, `docs/labview-lmock-mocking.md` | `lvai_lunit_*`, `lvai_generate_mock_class` |
| Placeholders and swaps | `docs/labview-unit-testing.md` §3a | `lvai_placeholder_subvi`, `lvai_swap_subvis` |
| Change one constant | `docs/cold-build-typedef-gdevcon.md` §7 | `lvai_set_constant` |
| pylabview route and edits | `docs/pylabview-controls.md`, `docs/connector-pane-repair.md` | `pylv_route`, `pylv_apply` |
| Stale compiled code | `docs/pylabview-stale-compiled-code.md` | `pylv_apply` |
| Network streams; tag refnums; IVI on pylabview | `docs/network-streams.md`, `docs/aixml-backends.md` | — |
| What cold builds found | `docs/cold-build-*.md` | — |
| Why a tool call failed with no detail | `docs/tool-argument-errors.md` | — |
| Station check; crashes | `docs/aixml-backends.md`, `docs/labview-crash-signatures.md` | `pylv_status` (`labviewHealth`, `grpcService`), `lvai_status` |
| Releases and versions | `docs/release-versioning.md` | `LabVIEWMCP --version` |

The served documents are embedded in the assembly and byte-verified on every build; all of `docs\`
is copied beside the exe as well.

## The agent definitions

The `labview-*` agents in `.claude/agents/` are read at SESSION START: a change needs a client
restart. `plugin/agents/` is GENERATED - run `scripts/Sync-PluginAgents.ps1`, never edit it by
hand (`PluginAgentTests` fails on drift). Keep `description:` a folded block scalar
(`description: >-`): an unquoted `: ` in it makes the agent vanish with "Agent type not found". A
tool a phase needs must be in the agent's roster, AND a phase must say when to use it. LUnit is
installed in the 32-bit tree; resolve the install root from the running process.

## Build and test

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File .githooks/run-tests.ps1
```

Use the second rather than a bare `dotnet test`: it stops a server running from THIS tree (which
locks the exe), and after either command the `lvai_*` tools are gone until the client restarts.

- `dotnet` needs the per-user SDK (`%USERPROFILE%\.dotnet`) first on `PATH`; the gate finds it.
- **Never run `dotnet msbuild -t:Compile`** and never build to a redirected output path: both
  produce a DLL without embedded resources. Recovery: delete `obj`/`bin` of both projects and
  build normally, then read `build.ps1`'s whole tail.
- Never identify this repository by `.git` (a worktree's is a file); a test that shells out to
  `git` strips `GIT_*` first.
- On a dev build, `serverCommit` is the HEAD at build time: ask the DLL (`grep -a`), not the id.
- **A release tag is `vX.Y.Z`**, checked with `scripts/Assert-ReleaseTag.ps1`. **Never publish
  `build.ps1`'s output** - only the release workflow's archive. The plugin IS that archive
  unpacked. Two lists decide what ships, the `.csproj` and `release.yml`'s staging step: a file
  named individually must be added to both. `docs/release-versioning.md`.
