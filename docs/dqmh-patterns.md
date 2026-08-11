# DQMH module structure, as seen through AIXML

DQMH (Delacor Queued Message Handler) is a public LabVIEW framework for building modules
that communicate by user events. This note records what a DQMH module looks like *in AIXML*,
so generated or analysed code can follow the same shape.

Derived empirically from a production application containing four DQMH modules, read through
`ConvertVIToAIXML`. All module, message, project and product identifiers are replaced with
placeholders — `<Module>` for a module name, `<Request>` / `<Broadcast>` for event names.
Read together with [aixml-reference.md](aixml-reference.md), which covers the format itself.

## 1. What a module consists of

A module is an `.lvlib` whose members form a fixed public API. The framework VIs are the same
in every module; only the request and broadcast VIs differ.

| VI | Role |
|---|---|
| `Main.vi` | the module body — two parallel loops, see §2 |
| `Start Module.vi` | launches `Main.vi` asynchronously, returns the event refnums |
| `Stop Module.vi` | fires the stop request and optionally waits |
| `Obtain Broadcast Events.vi` | create-or-return the broadcast event cluster |
| `Obtain Request Events.vi` | create-or-return the request event cluster |
| `Synchronize Module Events.vi` | blocks until the module has registered its events |
| `Module Did Init.vi` | broadcasts "I am initialised" |
| `Show Panel.vi` / `Hide Panel.vi` | front-panel control from outside |
| `<Request>.vi` | one VI per request, called by consumers |
| `Broadcast <Broadcast>.vi` | one VI per broadcast, called from inside the module |

Supporting typedefs follow a naming convention that is worth recognising:

```
Broadcast Events--cluster.ctl              the broadcast refnum bundle
Request Events--cluster.ctl                the request refnum bundle
<Request> (Reply Payload)--cluster.ctl     typed reply of a request-and-wait-for-reply
Did <Event> Argument--cluster.ctl          payload of a broadcast
Module Name--constant.vi                   the module's own name as a constant
```

## 2. `Main.vi` — the two-loop core

Structurally:

```
root
└── Case Structure                     "did initialisation succeed?"
    └── CaseFrame (success)
        ├── Structure While Loop       Event Handling Loop
        │   └── Event Structure
        │       ├── CaseFrame  " &quot;<Control>&quot;\3A Value Change "
        │       └── CaseFrame  " Panel Close? "
        └── Structure While Loop       Message Handling Loop
            └── Case Structure         one frame per message
                ├── CaseFrame  &quot;Exit&quot;
                ├── CaseFrame  &quot;Error&quot;
                ├── CaseFrame  Default
                └── CaseFrame  &quot;<Message>&quot;   … one per handled message
```

Two things to note when reading such a VI:

- **Both loops live inside a `CaseFrame`**, not at `root`. The enclosing case is the
  init guard: if initialisation failed, neither loop runs. A tree walk that only looks at
  `uid_parent="root"` will therefore find the guard and nothing else.
- **Messages are quoted strings** in the `selector`, e.g. `&quot;Exit&quot;`. The framework
  contributes `"Exit"`, `"Error"` and `Default`; everything else is application-specific.
  A real module in the corpus had 23 frames, so expect the message case to dominate.

The observed module `Main.vi` exported to ~200 KB of AIXML with ~1400 elements: 744 tunnels,
214 nodes, 117 subVI calls, 83 case frames, 57 structures. Nesting is deep; treat the case
selectors as the index into it.

## 3. The typed event contract

This is the part worth getting right, because the types encode the whole protocol.

**Broadcast cluster** — one `UserEvent` refnum per broadcast, each carrying a payload cluster:

```
cluster{
  ref{UserEvent}{cluster{bool.Init?,string.Info,int32.Module ID}}.Did Init,
  ref{UserEvent}{cluster{...}}.<Broadcast>,
  ...
}
```

**Request cluster** — same shape, but every request payload starts with the caller identity:

```
cluster{
  ref{UserEvent}{cluster{string.Origin,int32.Module ID, ...}}.<Request>,
  ...
}
```

`Origin` (a string naming the caller) and `Module ID` (int32) appear in request payloads by
convention — that is how a module knows who asked and which clone should answer. Broadcast
payloads carry `Module ID` too, so a consumer can tell clones apart.

## 4. How the framework VIs are built

| VI | Terminals | Mechanism |
|---|---|---|
| `Start Module.vi` | in `Run as Singleton? (F)` bool, `Module ID Overwrite` int32 · out broadcast cluster, `Wait for Event Sync?` bool, `Module ID` int32 | `Static VI Reference` → `Start Asynchronous Call` → `Close Reference`, wrapped in a `Flat Sequence Frame` and eight case structures for the singleton/clone decision |
| `Stop Module.vi` | in `Origin` string, `Module ID` int32, `Wait for Module to stop? (F)` bool | `Generate User Event` on the stop request, then optionally `Wait on Stop Sync.vi` |
| `Obtain Broadcast/Request Events.vi` | in `Create New? (F)` bool · out the event cluster | `Create User Event` per field plus `VI Server Reference`, inside `Flat Sequence Frame`s and a case that either creates or returns the existing set |
| `Synchronize Module Events.vi` | in `Wait for Event Sync?`, `Module ID` · out `Module ID (dup)` | `Wait on Event Sync.vi`, then `Destroy Sync Refnums.vi` |
| `Module Did Init.vi` | in `Origin` string, `Initialized?` bool, `Module ID` int32 | `Obtain Broadcast Events.vi` → `Generate User Event` |

Recurring framework calls, visible as `Call` targets: `Delacor_lib_QMH_Reset.vi`,
`Acquire Module Semaphore.vi`, `Get or Create Master Reference.vi`,
`Get Module Running State.vi`, `Get Module Execution Status.vi`, `Clear Errors.vi`,
`Wait on Event Sync.vi`, `Destroy Sync Refnums.vi`, `Status Updated.vi`.

Note the `\3A` escape in call targets: a library-qualified subVI appears as
`<Library>.lvlib\3A<VI>.vi`.

## 5. Consequences for generating code

- **You cannot generate a DQMH module** — and this is not merely an observed limit.
  NI's published "not-yet-supported" list for the LabVIEW Coding Agent names **DQMH**
  explicitly, alongside QMH-with-Event-Structure and the Actor Framework. Four further
  entries on that list each block a module on their own: `.lvlib`, `.lvclass`, `.ctl`
  typedefs, and "user VIs outside the supported node catalog". A fifth, `Event Structure`,
  rules out the event handling loop. See the AIXML reference, §9.
- The measurements below were taken before that list was known. They agree with it, and they
  add the part NI does not spell out: *which* error you get, and how far a single VI gets.
- **Measured, on a freshly scripted singleton module.** `ConvertVIToAIXML` on its `Main.vi`
  succeeds (47 kB, `errorCode 0`) — reading works. Feeding that byte-exact export straight back
  into `ValidateAIXML` **fails**, which kills the obvious workaround of *regenerating* a whole VI
  instead of editing it. Three distinct causes in one report:
  - ~20 × `Unsupported SubVI: <Module>.lvlib:<X>.vi` — every framework call is library-qualified.
    One is class-qualified (`Delacor_lib_QMH_Message Queue.lvclass:…`).
  - `Control with type=UDClassInst is not supported` and `Property Node with type=UDClassInst is
    not supported` — DQMH 5's `Module Admin` is a **class object**, which AIXML's type grammar
    cannot express at all. This wall stands even if every subVI were self-contained.
  - `Object terminal not found for input: …` and `Could not find control with name "Pane" to apply
    fixup` — fallout from the two above.

  Two further obstacles apply even to a hypothetically valid regeneration: overwriting `Main.vi`
  would break its `.lvlib` membership and linker info (DQMH ships
  `Write VI Linker Info to Change Library.vi` precisely because relinking is non-trivial), and
  AIXML carries no coordinates, so the two-loop diagram would be re-laid-out from scratch.
- **How close a *request VI* is — measured on `Show Panel.vi`.** Unlike `Main.vi`, a public API
  request VI has no class object on its connector pane, only `error in`/`error out` clusters, so
  the `UDClassInst` wall does not apply. Its export is complete and small (4.3 kB), and validating
  that export for regeneration reports **exactly two errors and nothing else**:
  ```
  Unsupported SubVI: <Module>.lvlib:Obtain Request Events.vi
  Unsupported SubVI: <Module>.lvlib:Module Not Running--error.vi
  ```
  The silence on everything else is the finding: the whole surrounding structure **is** expressible
  in AIXML — the case structure with its tunnels and two frames, `Generate User Event`,
  `Bundle`/`Unbundle By Name`, `Not A Number/Path/Refnum?`, `Merge Errors`, the cluster type
  strings and the typedef'd argument constant. A request VI is therefore two resolved call targets
  away from being generatable, not architecturally out of reach like `Main.vi`.
- **Which makes one hypothesis worth testing.** `ApplyAIXMLToVI` fails standalone but is the RPC
  behind LabVIEW's own code completion, where LabVIEW applies the AIXML *from inside the target
  VI's own context*. A VI that already belongs to the module library may well resolve
  `<Module>.lvlib:…` targets that a standalone generation cannot. Untested — but it is the only
  route that would close the two-error gap above.
- **You can read one.** Analysis, review, documentation and dependency mapping all work,
  including inside packed libraries — see §6.
- What is realistic today: generate self-contained leaf VIs (pure computation from
  primitives) and let a human wire them into a module. Anything touching the event
  clusters needs the module's typedefs, which means a `Call`, which currently fails.
- Use the DQMH scripting tools in LabVIEW for creating modules, events and requests. They
  maintain the naming conventions, the typedefs and the connector panes together; producing
  that by hand through AIXML would be both impossible (see above) and pointless.
- **The scripting tools cannot be driven through this server either — they are password
  protected.** `lvai_describe_vi` on `<LabVIEW>\project\Delacor\DQMH\Module\Add New DQMH
  Module.vi` and on the engine behind it, `_DQMH New Module\Script New Module.vi`, both return
  `errorCode 5002 — Password protected VI without cached password`. So they cannot be inspected,
  and running an uninspected interactive dialog VI through `lvai_run_vi_as_top_level` would at
  best block on a modal dialog in the user's IDE. Two further dead ends, checked: there is **no
  static module template to copy** — `_DQMH New Module` holds 16 *scripting* VIs, not a file tree,
  so the module is generated at run time via VI Server; and `RunVIAsTopLevel` passes inputs as
  strings, while the scripting engine needs a live project reference, which no string can carry.
- **The same applies to events.** `Event\Create New DQMH Event.vi` is password protected too
  (`errorCode 5002`), and an event is not an additive file drop: it *edits* existing code — a new
  refnum field in `Request Events--cluster.ctl`, a new case in `Main.vi`, and matching changes in
  `Obtain`/`Destroy Request Events.vi`. The only RPC that edits an existing VI is
  `ApplyAIXMLToVI`, which is non-functional (see the README caveats), and the new request VI would
  itself `Call` library-local framework subVIs, which generation rejects. So there is no partial
  path either — and a half-wired event leaves the module broken, which is worse than no event.
- **Net effect:** creating a DQMH module or event is a manual step in the IDE (*Tools » Delacor »
  DQMH » …*, or the project's right-click menu). Nothing in the 23 RPCs substitutes for it — no RPC
  writes a `.lvlib` either.
- **What is verifiable afterwards, and what is not.** `lvai_describe_project` reports the module's
  library under `libraries` with its full item inventory — every framework VI, every
  `… Argument--cluster.ctl`, and each item's `scope` (public/private) — plus `missingItems`, which
  is the fastest check that scaffolding is complete. `describe_vi` works on `Main.vi`, so a new
  event case can be confirmed. But `describe_vi` **cannot read a `.ctl`** (`errorCode 5001`), so
  the *contents* of an argument cluster — whether the argument is actually a string — cannot be
  checked from here at all. That one needs eyes on the typedef.

## 6. Packed libraries are readable

A module consumed as `.lvlibp` still exports full AIXML:

```
<app>.lvlibp\<Module>\Main.vi   ->  ~200 KB of AIXML, complete block diagram
```

So a project that links compiled modules can still be analysed end to end — no source
checkout needed. Paths inside a `.lvlibp` are not real directories, so ordinary directory
listing fails while the RPC succeeds; address VIs by their path *through* the `.lvlibp`.

## 7. Reading a DQMH project efficiently

`.lvproj` files of this size (~240 KB, 1400+ items) are expensive to pull through a
describe call. Parsing the project XML locally is cheaper and gives the same inventory:
`Item` elements with `Type="VI"` and their `URL`. Count `Main.vi` occurrences to find how
many modules a project contains, then locate each module root from that VI's path.

Hallmark file names that identify a DQMH module without opening anything:
`Main.vi`, `Start Module.vi`, `Stop Module.vi`, `Obtain Broadcast Events.vi`,
`Obtain Request Events.vi`, `Synchronize Module Events.vi`, `Module Did Init.vi`.

**A module's own VIs are not listed in the `.lvproj`.** The project references the module
`.lvlib`, and the library file owns the members. Parsing only the project finds dependencies
and build actions — in one module source project, 404 VIs of which *none* were framework VIs.
Parse the `.lvlib` (also XML, same `Item` elements) to get the real member list.

That makes whole-codebase classification cheap and worth doing before opening anything: glob
`<root>/*/*.lvlib`, treat a library as a DQMH module when its members include `Main.vi`,
`Obtain Broadcast Events.vi` and `Obtain Request Events.vi`, then count core members, clone
members and extension members per module. Forty modules classify in well under a second, with
no LabVIEW involvement at all — and the counts are what turn "this is how modules look" into
a claim with a denominator.

## 8. The full framework inventory

Read from a module's `.lvlib`. This is the complete generated skeleton; only the request and
broadcast VIs and their typedefs differ between modules.

**Lifecycle** — `Main.vi`, `Start Module.vi`, `Stop Module.vi`, `Init Module.vi`,
`Close Module.vi`, `Handle Exit.vi`

**Event plumbing** — `Obtain Broadcast Events.vi`, `Obtain Request Events.vi`,
`Obtain Broadcast Events for Registration.vi`, `Wrapper_Obtain Broadcast Events.vi`,
`Wrapper_Obtain Request Events.vi`, `Destroy Broadcast Events.vi`,
`Destroy Request Events.vi`, `Get Sync Refnums.vi`, `Destroy Sync Refnums.vi`,
`Is Safe to Destroy Refnums.vi`, `Synchronize Module Events.vi`,
`Synchronize Caller Events.vi`, `Wait on Event Sync.vi`, `Wait on Module Sync.vi`,
`Wait on Stop Sync.vi`

**Execution status** — `Get Module Execution Status.vi`,
`Update Module Execution Status.vi`. (`Get Module Main VI Information.vi` appeared in the
singleton module only — do not assume it is universal.)

**Cloneable modules only** — a singleton module contains none of these:
`Obtain Module Semaphore.vi`, `Acquire Module Semaphore.vi`,
`Release Module Semaphore.vi`, `Destroy Module Semaphore Reference.vi`,
`Get Module Running State.vi`, `Module Running State--enum.ctl`,
`Addressed to This Module.vi`, `Wait on Stop Sync.vi`, `Is Safe to Destroy Refnums.vi`,
`Module Running as Cloneable--error.vi`, `Module Running as Singleton--error.vi`,
`Init Select Module Ring.vi`, `Update Select Module Ring.vi`,
`Test Clone Registration API.vi`, plus a `Clone Registration.lvlib` sub-library holding an
action engine (`Clone Registration AE.vi`) with `Init` / `Add` / `Remove` / `Is Empty` /
`Is First` / `List Instances` and a last-clone notifier.

### Telling singleton from cloneable

Measured over **40 DQMH modules** in one codebase (70 to 243 library members each):

- all **27 core framework members present in 40/40** — no exceptions
- the 14-member clone set is **strictly bimodal**: 14/14 or 0/14, never partial
- 13 cloneable, 27 singleton

So the core really is invariant, and module type is a clean binary. Four independent tells:

| | singleton | cloneable |
|---|---|---|
| admin class in `Start Module.vi` | `Delacor_lib_QMH_Module Admin.lvclass` | `Delacor_lib_QMH_Cloneable Module Admin.lvclass` |
| `Start Module.vi` inputs | error in only | `Run as Singleton? (F)`, `Module ID Overwrite` |
| `Start Module.vi` outputs | `Module Was Already Running?` | `Module ID` |
| `Start Module.vi` complexity | 1 case structure | 8 case structures + a flat sequence |

Establish which kind you are looking at before reasoning about message routing: in a
cloneable module every handler must consult `Addressed to This Module.vi`, and in a
singleton that VI does not exist.

**Standard broadcasts** — `Module Did Init.vi`, `Module Did Stop.vi`, `Status Updated.vi`,
`Error Reported.vi`

**Standard requests** — `Show Panel.vi`, `Hide Panel.vi`, `Show Diagram.vi`

**Custom error rings** — `Module Not Running--error.vi`, `Module Not Stopped--error.vi`,
`Module Not Synced--error.vi`, `Master Reference Not Closed--error.vi`,
`Request and Wait for Reply Timeout--error.vi` (two more are cloneable-only, see below)

**Constants** — `Module Name--constant.vi`, `Module Timeout--constant.vi`

Naming suffixes, beyond those in §1: `--error.vi` for a custom error ring, `--constant.vi`
for a scalar constant wrapper, `--enum.ctl` for an enum typedef, `Module Data--cluster.ctl`
for the module's internal state (carried in the message loop's shift register).

Some modules additionally prefix loop helper subVIs by the loop they serve —
`MHL_<Message>.vi` for the message handling loop, `EVL_<Control>ValueChange.vi` for the
event handling loop. This is **not a framework rule and not even common**: only 4 of 40
modules used it at all, with one to three such VIs each. Never rely on the prefix to find
handler code.

### A reading caveat when comparing modules

`Call` targets are qualified with the container the VI was *read from*. The same logical
call appears as `<Module>.lvlibp\3A<VI>.vi` when read out of a packed library and as
`<Module>.lvlib\3A<VI>.vi` when read from source. That is an artefact of where you pointed
`ConvertVIToAIXML`, not a dependency difference — do not read it as one module calling
another's build output.

## 9. What the API VIs look like inside

**Broadcast VI** — straight-line, no structures:

```
Obtain Broadcast Events.vi -> Unbundle By Name (pick the refnum)
                           -> Bundle By Name  (build the payload)
                           -> Generate User Event
```
Its controls are exactly the payload fields plus `Module ID`. `Format Into String` and
`Module Name--constant.vi` appear alongside, feeding the debug/trace string.

**Fire-and-forget request VI** (e.g. `Show Panel.vi`) — the same shape via
`Obtain Request Events.vi`, wrapped in one case structure that emits
`Module Not Running--error.vi` when the module is not up.

**Request-and-wait-for-reply VI** — the elaborate one. Terminals:

```
in   wait for reply (T) : bool      Module ID : int32     <payload fields>
out  Reply Payload : cluster{...}   timed out? : bool
```

Four case structures and a flat sequence frame: send the request event, then wait for the
reply within `Module Timeout--constant.vi`, raising `Request and Wait for Reply Timeout--error.vi`
on expiry. The reply travels back as a broadcast, which is why such a VI also calls a
broadcast VI internally.

**`Addressed to This Module.vi`** — the clone filter, and worth knowing:

```
in   Module ID from Argument : int32     Module Admin : ref{UDClassInst}
out  Addressed to this instance? : bool  Addressed to ALL (-1) ? : bool
```

A `Module ID` of **-1 addresses every clone**. Any message handler that ignores this VI will
misbehave in a cloneable module.

**`Init Module.vi` / `Handle Exit.vi`** — these thread a **module admin object**, typed
`ref{UDClassInst}`, through the module. `Init Module.vi` registers the clone via the
`Clone Registration` action engine; `Handle Exit.vi` takes the stop argument
`cluster{string.Origin,int32.Module ID,bool.Exit via Stop Module Req?,...}`, hides the panel
and calls `Stop Module.vi`.

## 10. In-house extensions are common

**All 40** modules sit on top of a house extension library that adds a **third loop** beyond
DQMH's two — a process loop with its own command enum, message typedefs and a send-command VI
— plus **DVR-based shared state** (init / read / write wrappers around a data value reference,
sometimes polymorphic) instead of keeping everything in the message loop's shift register.
10 to 14 members per module belong to this extension, with no module below 10.

40 out of 40 makes this a house standard, not a variation: in this codebase the two-loop
shape describes the *framework*, never an actual module. Expect three loops and DVR state.

## 11. The consumer side

Everything above is the module. This is what a *caller* looks like — read from an
application's top-level VI (2 while loops, 5 case structures, 7 flat sequence frames, one
event structure, 27 subVI calls).

The sequence is short and always the same:

```
Start Module.vi                 launch it
Synchronize Module Events.vi    wait until its events exist
Register For Events             register the broadcast cluster -> registration refnum
   … event structure handling broadcasts and local controls …
Unregister For Events
Stop Module.vi
```

Requests need no ceremony: a request VI is just a subVI call
(`<Module>.lvlib\3AShow Panel.vi`), because the event refnums are obtained inside it.

The caller's event structure mixes the three selector forms — for a nine-frame example:

```
&lt;<Module> Broadcast Events.Module Did Init&gt;\3A User Event
&lt;<Module> Broadcast Events.Module Did Stop&gt;\3A User Event
&lt;<Module> Broadcast Events.Error Reported&gt;\3A User Event
&lt;<Module> Broadcast Events.<Broadcast>&gt;\3A User Event
&quot;<Control>&quot;\3A Value Change          … one per button
Panel Close?
```

So a broadcast is consumed by name through the registration refnum, and the framework's
`Module Did Init` / `Module Did Stop` / `Error Reported` appear as ordinary frames next to
application-specific broadcasts. One `Event Data Node` per frame extracts the payload; the
`Panel Close?` frame uses an `Event Filter Node`.

**Composition observation:** the top-level application drove exactly **one** module directly,
while linking 13 packed modules in total. The rest are started further down — a module can be
a consumer of other modules. Do not infer the module graph from the top-level VI alone; follow
`Start Module.vi` calls transitively.
Before concluding how a module works, list the `.lvlib` members and look for a second message
vocabulary (a separate `*MessageData.ctl` plus `*Cmd.ctl` pair is the tell) and for DVR
accessor VIs. Both change where state lives and which loop does the actual work.
