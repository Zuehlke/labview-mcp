# Cold build: the CLD ATM exam, producer/consumer, 2026-09-16

A whole CLD sample exam (NI/Emerson 100928D-01, *Automated Teller Machine*) built cold from the
PDF: 8 application VIs, a project, a Caraya suite of 43 assertions, in one session. The user asked
for the producer/consumer pattern and for the run to be measured.

Unlike the earlier cold builds in this folder, this one has **no class in it at all** — it is the
first measurement of the plain-VI half of the toolchain at application scale, and the things it
found are all in AIXML expressiveness and in the MCP argument layer rather than in `.lvclass`
handling.

Deliverable: `C:\Temp\CLD-ATM\Automatic Teller Machine\`.

---

## 1. What was measured

| phase | wall | active | turns | output tok | new input tok |
|---|---|---|---|---|---|
| P0 spec reading (PDF → text) | 0:54 | 0:54 | 11 | 4 544 | 13 562 |
| P1 inspect template, route decision | 60:56 | **4:04** | 12 | 18 408 | 20 033 |
| P2 design + batched reference lookups + risk probe | 7:17 | 7:16 | 46 | 61 220 | 162 768 |
| P3 the seven subVIs | 17:29 | 17:28 | 69 | 208 427 | 208 707 |
| P4 main VI, producer/consumer + event structure | 3:36 | 3:36 | 23 | 22 397 | 41 428 |
| P5 functional verification (Caraya agent) | 21:02 | 6:16 | 22 | 24 984 | 41 575 |
| P6 cleanup, icons, green suite | 3:16 | 3:15 | 26 | 15 399 | 37 243 |
| **total, main session** | **114:34** | **42:57** | **209** | **355 379** | **525 316** |
| Caraya sub-agent (inside P5) | 19:29 | — | 71 tool uses | — | 290 602 total |

`active` excludes any gap longer than 180 s. P1's 57 minutes of dead time is the user answering one
blocking question; **productive wall clock was 57:42**, of which the sub-agent's 19:29 ran with the
main session idle. Cached input reads were 64.2 M over the session and are not a cost signal.

**P3 is 40 % of the whole run and 59 % of the output tokens**, and it is authoring: seven VIs, of
which `Handle ATM Action.vi` alone carries an 11-frame case structure with 11 tunnels per frame.
That is where the next optimisation has to land — see §5.

---

## 2. A NEWLINE IS `&#10;` IN A `value=` ATTRIBUTE, AND IT SURVIVES

Measured on one probe VI, generated and run: `value="Line One&#10;Line Two&#10;Line Three"` on a
string `<Constant>` comes back out of the running VI as `Line One\nLine Two\nLine Three`, three real
lines.

This is worth a section because the alternative is expensive and was the obvious first design. The
exam prescribes ten message texts of which six are multi-line, and without this the only route is a
`Concatenate Strings` chain per message with a `Line Feed Constant` between every pair — roughly
forty extra elements across `Build ATM Message.vi`. With it, each message is one labelled constant.

`&#10;` is an ordinary XML character reference, and XML attribute-value normalisation keeps a
character reference while it folds a *literal* newline to a space. So this is the standard-conformant
behaviour rather than a quirk — but nothing in `docs/aixml-reference.md` said so, and §6 (escaping)
is about `\3A` and `\5C`, which sends the reader looking for an AIXML-specific escape that does not
exist.

---

## 3. AN IMPLICITLY LINKED PROPERTY NODE IS AUTHORABLE — `link=` PLUS `type=`, AND NO REFERENCE

The whole enable/disable/focus half of this application hangs on it, and the shape is not in the
reference document — it was found by grepping the export cache for `Property Node` and reading what
NI's own VIs carry:

```xml
<Node _name="Property Node" fields="write+Disabled"
      inputs="error in (no error):7121.error out,Disabled:7162.s? t\3Af"
      link="User Input" outputs="error out:7170.error out"
      type="{LV.String}" uid="7170" uid_parent="7000"/>
```

`link` is the control's **front-panel label**, `type` is the control's class, and there is **no
`reference` input at all** — that is what makes it implicit. Measured working for `write+Disabled`,
`write+Key Focus`, `write+Value` and `write+Value (Signaling)`, on `{LV.String}`, `{LV.Boolean}` and
`{LV.Cluster}`. Two properties on one node also work: `fields="write+Value,write+Key Focus"` with
both value terminals in `inputs`.

The class strings that occur with `link=` in this station's 1 402 cached exports, most frequent
first: `{LV.Boolean}` 37, `{LV.WaveformGraph}` 34, `{LV.Ring}` 29, `{LV.ListBox}` 20, `{LV.String}`
18, … `{LV.Cluster}` 7. Grep the cache for the class you need rather than guessing it.

**`Value (Signaling)` is what makes a clean single-exit producer/consumer possible.** The consumer
writes `Card Simulator` = FALSE with it, which raises the *Card Simulator: Value Change* event in the
producer, which queues `Terminate` and stops. So the inactivity timeout, the *Return Card and
Terminate* menu item and the user releasing the button by hand all leave through one path, with no
user event, no extra control, and no race between the two loops over who releases the queue.

---

## 4. AIXML CANNOT EXPRESS AN EVENT STRUCTURE'S TIMEOUT — AND THE QUEUE MAKES THAT FREE

The spec wants a ten-second inactivity timeout. The obvious construction is a `Timeout` frame with
10000 wired to the structure's timeout terminal. **That terminal does not exist in the format.**
Measured over the export cache: 79 `<Structure _name="Event Structure">` elements, and every single
one is

```
<Structure _name="Event Structure" uid="N" uid_parent="P">
```

with no other attribute — NI's own exporter drops the timeout too, so it is not an authoring
restriction that a better spelling gets round. The 12 ` Timeout ` case frames in the cache all sit on
structures whose timeout value is invisible.

**`Dequeue Element`'s `timeout in ms` is the answer, and it is better than the thing it replaces.**
In a producer/consumer the consumer is idle exactly when the user is idle, so the dequeue timeout
*is* the inactivity timer; any front-panel activity resets it simply by queueing a command, with no
timer to restart. It is carried on a shift register at −1 before a card goes in and 10000 afterwards,
which is the "arm the timer only during a session" requirement for one `Select`.

Worth stating as a general move: **when AIXML cannot reach a structure's configuration, look for a
node whose ordinary wired input does the same job.** That was also the fix for the Timed Loop
(`scripts/templates/README.md`, the slot pattern) and it is the same shape here.

---

## 5. THE COST IS CASE-FRAME TUNNEL BOILERPLATE, AND A GENERATOR SCRIPT PAID FOR ITSELF

`Handle ATM Action.vi` is one case structure with 11 frames over 5 input and 6 output tunnels. Every
frame must declare every tunnel, so that is **121 `<Tunnel>` elements** plus the frame bodies — about
280 lines of the VI's 27 900 bytes of AIXML, none of it interesting and all of it a place for a typo
that surfaces as `Contains unwired or bad terminal` naming the wrong element.

It was emitted by a 260-line Python script (`gen_core.py` in the session scratchpad) from a compact
frame table, and **validated first try**. The two VIs it wrote took 2 generate calls between them.
For comparison, the five hand-written leaf subVIs took 1 batch call and 1 retry, and the hand-written
main VI took 3 validate/fix rounds — two of which were faults a generator would not have made.

**This is a tool waiting to be written**, by the rule `docs/workflow-economics.md` states: the step is
cheap for LabVIEW (1.4 s of convert) and expensive in authoring tokens (P3's 208 k output). A
`lvai_generate_case_machine` taking `{selector: {output: net}}` and emitting the tunnels would remove
most of the largest phase of this run.

---

## 6. FOUR SMALLER THINGS, EACH MEASURED

- **A multi-value string case selector works**: `selector="&quot;Initialize&quot;, &quot;Card
  Inserted&quot;"` validated, converted and dispatched on both. Useful whenever two actions differ
  only in something the caller decides.
- **`Merge Errors` requires BOTH `error in` terminals.** `inputs="error in:X,error in:"` is
  `Contains unwired or bad terminal`. Obvious in hindsight; it cost one validate round.
- **A `Node` with no inputs must OMIT `inputs` entirely.** `<Node _name="Current VI's Path"
  inputs="" …/>` is `Object terminal not found for input: :` — an empty attribute is parsed as one
  unnamed terminal, not as none. The rule "every terminal needs its net attribute"
  (`docs/cold-build-conveyorrig.md` §2) does not extend to a node that has no terminals on that side.
- **An XML comment between `<VI>`'s children is still `Error 42`**, and it is still the first thing to
  suspect when a document that looks right is refused generically. Two `<!-- … -->` section headers
  cost one round trip.

---

## 7. THE ARGUMENT LAYER, FROM A CLIENT THAT MARKS EVERY PARAMETER REQUIRED

`docs/tool-argument-errors.md` records that `lvai_placeholder_subvi`'s `viPath` and
`lvai_swap_subvis`' `viPath` were made **nullable** so the documented batch modes (`viPaths`,
`editsJson`) would be reachable. **From the Claude desktop client that fix does not work**, measured
here on both tools:

```
MCP error -32602: Invalid arguments for tool lvai_placeholder_subvi:
  { "code": "invalid_type", "expected": "nonoptional", "path": ["viPath"] }
```

This client validates against the served schema and requires **every declared parameter**, nullable or
not. The workaround is to pass the ignored parameter anyway (any valid path), which works — but the
lesson generalises past these two tools:

- **A parameter cannot be made optional for this client by giving it a null default.** Only removing
  it from the schema, or splitting the batch mode into its own tool, actually reaches the caller.
- **An empty string is not null for a path parameter.** `helperAixmlPath: ""` is
  `FileNotFoundException: No helper AIXML at ''`, and `viPath: ""` on `lvai_connector_pane` is
  `No VI at ''` — so the station-default pane lookup (the no-argument mode) is **unreachable from this
  client**. The numbers had to come from `LabVIEW.ini` plus `docs/connector-pane-patterns.tsv` instead.
  `lvai_aixml_reference` is the counter-example: it treats `""` as absent and works.
- **`panePattern: 0` is not "no pattern".** Passed as the client-mandated value for an optional
  integer it reached pylabview and failed at `conpane` with `no <cons> array in the front-panel heap`,
  on a probe VI that has no connector pane. `ok: false` there still means the VI was written.

**FIXED THE SAME DAY, and the diagnosis above was aiming at the wrong half.** The three bullets are
all true as observations and the *cause* is none of them: `required` was correct in every one of the
75 served schemas, and what the client actually reads is the `default` KEY - a property carrying one
becomes non-optional in its view. `ClientSchema.WithoutDefaults` strips it from what is served and
writes the value into the description instead. So the workaround below ("pass the ignored parameter
anyway") is history rather than advice, and the `lvai_connector_pane` no-argument mode should be
reachable again. Verified as far as this station can: 0 of 75 schemas carry a `default`, `required`
unchanged at 79. NOT verified from a client, because a client fetches the tool list once at session
start - the first call that proves it is in the next session.
`docs/tool-argument-errors.md` has the measurement and the control.

This is the same shape the document already records one layer in for `casesJson`: **a mode that one
parameter is supposed to disable must not be expressible only by omitting that parameter.**

---

## 8. FOUR TRANSPORT DROPS, AND WHY RETRYING IS THE RIGHT MOVE

Four calls in this session answered

```
Unavailable: ... IOException: Unable to read data from the transport connection:
An existing connection was forcibly closed by the remote host.
```

LabVIEW was alive throughout — same port, same PID, `dwarnCount` 1 → 3 over the whole run, nothing in
NI's log. In **every** case the work had actually completed and only the answer was lost:

| call | what the answer said | what was on disk |
|---|---|---|
| `lvai_generate_vis` entry 3 | convert failed | `Write Account Balance.vi`, 9 177 bytes, pane correct |
| `lvai_placeholder_subvi` × 3 | `stubNotWritten` | all three stubs present, `reused: true` on retry |
| `lvai_generate_vis` entry 2 | validate failed, nothing written | correct — nothing was written |

So the rule is: **on `Unavailable`, ask the artefact before believing the answer.** `lvai_status`
first (LabVIEW is usually fine), then measure the file — `lvai_connector_pane` on the VI, or re-run
the placeholder call and read `reused`. Treating the first row as a real failure would have meant
regenerating a VI that was already correct, and a regeneration over a path LabVIEW may hold is how
`Error 1357` sessions start.

---

## 9. THE SUPPLIED-PANEL GAP, STATED AS A COST

The exam supplies a front panel and forbids renaming its controls. AIXML regenerates a VI whole, so
there is no route that writes a diagram into the supplied VI and keeps its panel — that is
`CLAUDE.md`'s "a VI whose EXISTING FRONT PANEL must survive cannot be edited at all", met in the
field. The user chose the hybrid: generate beside it, merge by hand.

**The merge is not one paste, and the plan should say so up front.** Pasting a block diagram into
another VI creates a *new control on the target panel for every pasted terminal*, so the eight
template controls arrive duplicated and the eight wires have to be re-made against the originals.
That is perhaps five minutes in the IDE and it is unavoidable today — but it was not part of the
estimate when the route was chosen, and a caller picking between routes deserves it.

---

## 10. What the suite proves, and what it does not

`Tests\Run ATM Tests.vi` → `ATM-TestReport.xml`: **43 assertions, 0 failures, 0 errors** over three
suites, re-run green after every VI was re-saved by `lvai_set_vi_icon`. The sub-agent ran a
deliberate negative control (assert `Terminate` resolves to `Deposit`), confirmed it fails, and it was
removed from the deliverable afterwards — so the suite is demonstrably able to report a failure rather
than silently passing.

The two assertions that carry the most weight are the ones about the file *not* changing: a withdrawal
of 10 000 against a balance of 550, and a Fast Cash $50 against a balance of 20, both leave
`ATM accounts.txt` byte-identical. A controller that reports "insufficient funds" and debits anyway
would pass every message assertion.

Not covered, and named rather than implied: unknown commands, out-of-range button indices,
non-numeric or negative amounts, a transaction against a non-existent account, `Fast Cash $50` at
exactly 50, and every error-path behaviour — each call is fed a fresh `no error`. None of those are in
the exam specification, and the agent was told not to invent an expectation.

**The end-to-end UI path is NOT covered by the suite** and cannot be: the event frames need real
front-panel activity. It was verified by running the application for 2 s with
`lvai_run_vi_and_read_values runForMs` and reading the panel back — Welcome message exact, both menus
empty, `error out` clean — which establishes that the queue, both loops, all four subVI calls and the
file read work together, and nothing beyond that.

---

## 11. DIAGRAM SIZE: FACTORING OUT PARALLEL NODES BUYS HEIGHT, NOT WIDTH

The user's standing rule is that a block diagram should stay around 1920 x 1080 and that cohesive
groups belong in subVIs, with a worked example: the six `Property Node`s writing `Disabled`, one per
front-panel object, should be one subVI taking an **array of control references**.

**That subVI is buildable, and the missing piece is not what it looks like.** A control reference
bound to a named control is NOT expressible in AIXML — measured over the 1 402 cached exports,
`link=` occurs on **273 `<Node>` elements and on no `<Constant>` at all**, so there is no way to drop
a reference to *this* control onto a diagram. What works instead is two property nodes the caller
already owns:

```xml
<Node _name="Property Node" fields="read+Front Panel"  inputs="reference:,…" type="{LV.VI}"    …/>
<Node _name="Property Node" fields="read+Controls[]"   inputs="reference:<Panel>,…" type="{LV.Panel}" …/>
```

**The unwired `reference` is the whole trick** — a `{LV.VI}` property node with nothing wired to it
means *the VI it sits on*, and NI's own exports carry exactly that shape. So the caller reads its own
panel in two nodes, hands `array{ref{LV.Control}}` to the subVI, and the subVI matches
`read+Label.Text` against two label lists and writes `Disabled`. `ref{LV.Control}` and
`array{ref{LV.Control}}` are both valid AIXML type literals.

Two rounds of this were applied to the ATM main VI — the six property nodes into
`Apply ATM UI State.vi`, and the card/timeout/menu logic into `Track ATM Session.vi` — taking **25
objects off the consumer loop and replacing them with 3**. The result, rendered both times:

| | width | height |
|---|---|---|
| before | 2665 | 1094 |
| after  | **2688** | **880** |

**Height fell by 20 % and width did not move.** That is the finding worth keeping: LabVIEW's
auto-layout places nodes by data dependency, so **width follows the longest dependency CHAIN and
height follows how much sits in parallel**. The ATM consumer is an inherently sequential pipeline —
dequeue, index, resolve, handle, track, panel, controls, apply, menu, cluster, case, indicators — and
every subVI extracted so far removed parallel work, which is exactly the half that sets height.

So the 1920 guideline splits into two different problems:

- **Height** is what factoring fixes, and it works well — one call in place of ten nodes.
- **Width** only comes down by making the chain SHORTER, which means merging sequential stages back
  into one subVI. That is in direct tension with the instruction that produced the improvement, so
  it is a trade to put to the caller rather than to optimise silently. The remaining candidates here
  are small: `Get ATM Menu.vi` could return the two menu CLUSTERS instead of arrays and save one
  stage of `Array To Cluster`.
- **Neither is reachable by layout**, because AIXML carries no coordinates — the same limitation as
  diagram comments (§ `docs/diagram-comments.md`). A developer would wrap a long pipeline onto a
  second row; a generated diagram cannot.

### 11a. A BOUND CONTROL REFERENCE CANNOT BE AUTHORED — and the generic split that replaces it

The user's follow-up was sharper than "use a subVI": *put the references for the controls you mean
on the diagram, bundle them into an array, hand that over — then the VI is generic and reusable.*
That is the right design and it is **blocked by the format**, refused twice and explicitly:

| what was probed | answer |
|---|---|
| `<Constant _name="…" link="User Input" type="ref{LV.String}" …/>` | `-2628`, **`attribute 'link' is not declared for element 'Constant'`** |
| implicit `Property Node`, `link="User Input"`, reading `reference out` | `Error 1`, **`Object terminal not found for output: reference out`** |

The second is the more informative one: an implicitly linked property node has **no reference
terminals at all** — that is what "implicit" means, the link stands *instead of* them. And the first
settles the question for good, because the AIXML schema is closed (§ `cold-build-conveyorrig.md` §2):
`link` is declared for `Node` and for nothing else. Grepping the export cache agrees from the other
side — `link=` occurs on **273 `<Node>` elements and on no `<Constant>`** in 1 402 exports.

**The workaround the user approved is an array of NAMES**, and it keeps the generic VI generic as
long as the naming is not baked into it. The shape shipped here is a three-way split:

```
main VI     Front Panel -> Controls[]        (both property nodes, reference UNWIRED = this VI)
              -> Apply ATM UI State.vi       the only VI that knows an ATM exists
                   -> Get Controls By Label.vi   generic: references + names -> references
                   -> Set Controls Disabled.vi   generic: references + one boolean -> done
```

`Set Controls Disabled.vi` is what the user asked for — an array of control references and one
boolean, no application knowledge, reusable in any VI that can produce references.
`Get Controls By Label.vi` is the workaround isolated into its own reusable VI rather than welded
into the worker. The ATM's two control groups are two string constants in
`Apply ATM UI State.vi`, and the main VI is unchanged at two property nodes plus one call.

**The pane of the application-specific VI did not change**, which is why this second round cost no
regeneration of the main VI at all — worth designing for: when a refactor keeps a subVI's connector
pane, the caller is not touched and its event registration does not have to be redone.

**Two nodes of one socket cost two swap calls**, as `docs/cold-build-kilnrig.md` §2 records:
`Apply ATM UI State.vi` calls each generic VI twice, and `lvai_swap_subvis` answered
`socketsLeft: 2` on the first call and `0` on the second, with `diagramSubVis` showing the four
nodes with repetition throughout.

## 12. THE SECOND COLD BUILD, 2026-09-16 — four new measurements

The same exam rebuilt from scratch a few hours later, agent-driven, into
`C:\Temp\CLD-ATM-2`. Four things the first run never met, each measured rather than reasoned.

### 12a. AN EVENT-STRUCTURE `CaseFrame` MUST NOT CARRY `selectout` — and the error names a diagram

An ordinary `Case Structure` frame takes `selectout=""` when the selector value is unused, and §7
shows exactly that. Copying the habit into an `Event Structure` frame is refused:

```
Missing CaseSelector output terminal for diagram with UID 4330
```

Removing the attribute from all four event frames — changing nothing else — took the document
straight past it. §7's own `Event Structure` example is right: it simply has no `selectout`, and the
absence is load-bearing rather than incidental. Worth stating because the error blames the FRAME's
uid, which reads as a problem with the frame's contents, and the contents are fine.

### 12b. A FAILED `lvai_generate_vi_with_events` BURNS THE VI NAME, AND `lvai_close_vi` CANNOT FREE IT

`lvai_validate_aixml` validates under a throwaway `_name` precisely so a refusal cannot burn the
document's real name. **`lvai_generate_vi_with_events` does not** — it skips validation on purpose,
so its convert step runs under the real name, and a failure there registers that name. The next
attempt answers `Error 1051, A LabVIEW file of that name already exists in memory` for a file that
has never existed on disk.

The documented eviction route does not apply: `lvai_close_vi` answered **`Error 1149`**, *"the VI has
no front-panel WINDOW to close"*, because the VI was never opened in the IDE. Two ways out, both
measured: rename the VI, or — once the VI does exist and is a project member — `lvai_open_file` it
through the project to give it a window, then `lvai_close_vi`, which then answered `closed: true`
and the regeneration went through. The second is the one to use for an ordinary regeneration; the
first is the only one for a name burned by a failure.

### 12c. AN EVENT DATA NODE'S FIELDS CANNOT FEED A SUBVI CALL — and for a FRONT-PANEL event the control's own terminal replaces it

`ConvertAIXMLToVI` drops an `Event Data Node`'s field selection, and the repair route
(`lvai_set_event_data_fields`) wants a labelled placeholder constant on a **prim** input. Wiring
`NewVal` straight into a `Call` is therefore not reachable: the tool reported
`dataFieldsDroppedAndWired: ["NewVal","OldVal"]` and validation answered
`SubVI '…': required input 'new value' is not wired`.

**The simpler route needs no event data node at all.** A control's own terminal placed inside its
event frame already reads the value the event just produced, so `Not(Card Simulator)` and
`Get Pressed Button Index(Left Buttons)` do the same work with one element fewer per frame. It also
removes the third build step entirely. Measured: with every `Event Data Node` deleted and the
control terminals wired instead, the only errors left were the three expected pre-registration ones
plus `Event Structure: One or more event cases have no events defined`, which is the refusal
`lvai_generate_vi_with_events` exists to step around — and it then registered 4 of 4 events,
`execState 1`.

The one thing this gives up is `OldVal`. A helper that wanted *which button changed* becomes one
that reports *which button is TRUE*, which is correct for latched buttons and not for switches.
That is a design consequence, so say it in the VI's own description rather than leaving it implied.

**AND THE SHORTCUT STOPS AT THE FRONT PANEL — the user's correction of 2026-09-16.** This section
was written as a general statement about event structures, and it is not one. The control terminal
only carries the event's value because there IS a control; **a USER EVENT has none.** Its payload
exists solely in the `Event Data Node`, so for a user event the route this section calls "the repair
route" is the only route there is: a labelled placeholder constant wired into a **prim** input, plus
`lvai_set_event_data_fields` as a third build step.

So the two halves are:

| frame kind | how the handler gets the data |
|---|---|
| front-panel event (`"Control": Value Change`) | the control's own terminal inside the frame — no data node, no third build step |
| **user event** (`<RegRef.Field>: User Event`) | placeholder constant on a prim input + `lvai_set_event_data_fields` — **the terminal route does not exist** |

The two frames look alike on a diagram, which is why the boundary has to be written down rather than
left to be inferred. Generalising the shortcut would have produced exactly the failure
`CLAUDE.md` already records for user events: a handler that reads the panel instead of the event,
and therefore reads what the panel holds at that instant rather than what the event carried.

### 12d. THE AGENT ROSTER GAP: `labview-vi-generator` COULD NOT REACH `lvai_placeholder_subvi` — FIXED 2026-09-16

An agent told to author a `Call` to a sibling project VI answered `No such tool available` for
`lvai_placeholder_subvi` and **hand-built a socket VI from AIXML that cloned the subject's pane**,
installing it into `user.lib\LV_MCP\` itself. It worked — the clone was exact, the swap landed,
`socketsLeft: 0` — and it is the wrong thing to have to invent, because an inexact clone is
`Error 7, Bad Linkage` and the agent had no way to know the rule it was re-deriving.

Neither `lvai_placeholder_subvi` nor `lvai_swap_subvis` is in that agent's `tools:` list, while
`docs/labview-unit-testing.md` §3a calls the pair "the only route by which a generated VI calls
project-local code". **A capability the definition describes and the roster withholds reads as a
missing capability**, which is the same shape as an embedded document nothing serves. The workaround
used here was to have the agents author the `Call` and STOP, and to do every swap centrally
afterwards — which is worth doing anyway when several agents share one LabVIEW, because a swap needs
an active project and agents cannot share one.

**Closed the same day.** `lvai_placeholder_subvi` and `lvai_swap_subvis` are now in the `tools:`
roster of `labview-vi-generator`, `labview-vi-editor` and `labview-vitester-unit-test` — the three
that author a `Call` to project code and lacked them; the other unit-test agents and
`labview-class-generator` already had the pair. `labview-doc-generator` and `labview-dqmh-module`
are deliberately left without, because neither authors a call to project-local code. A stale
duplicate entry in `labview-class-generator`'s list was removed in the same pass. **The
generate-then-swap protocol is now a written rule rather than a workaround**, in `CLAUDE.md` beside
"ONE AGENT, ONE OUTPUT DIRECTORY" and in an identical block in all eight agent definitions.

**The acceptance test is a restart, not this session.** Agent definitions are read at SESSION
START, so an agent spawned before the edit still carries the old roster — the same shape as the
served-schema finding in §7. Until a client restart, the correct claim is that the files are right
and the wiring is untested.

### 12e. DIAGRAM SIZE, SECOND DATA POINT: WIDTH STILL FOLLOWS THE CHAIN

| build | top-level diagram |
|---|---|
| first, after factoring | 2819 x 982 |
| second, designed for it from the start | **2865 x 856** |

Height fell 126 px and width rose 46 px, on a build that was decomposed for size from the first
sketch rather than refactored into it. That is §11's finding again from the other direction:
**factoring buys height; width is the longest dependency chain and only merging shortens it.** The
consumer loop here is a genuine twelve-stage pipeline (dequeue, resolve, handle, menu, two enable
calls, focus, stop), and no amount of further factoring moves it.

### 12f. THE CLIPPED COMMENT CAME BACK, AND ONLY THE PICTURE SAW IT AGAIN

A 79-character comment landed in a five-line box and shipped as *"…it reports which control the
user"* — the final word gone. `lvai_check_aixml`, `lvai_validate_aixml`, the convert, nine subVI
swaps and `execState 1` all passed it. Cropping the rendered PNG showed it in one look.

The repair is the expensive one this file already warns about: regenerate, re-swap every node,
re-set the icon. **Shorter is the whole defence** — the three comments were cut to 30, 43 and 44
characters and all three then rendered complete. There is no cheap in-place edit for a `<FreeLabel>`'s
text.

One practical note: this station has no PIL, so the crop was done with `zlib` and `struct` out of the
standard library — PNG colour type 3 (palette), unfilter, crop, nearest-neighbour upscale, re-emit.
About 25 lines, and it is the only way the last word was ever going to be read.

## 13. THE THIRD COLD BUILD, 2026-09-16 — what the previous day's fixes were worth

The same exam rebuilt a third time, into `C:\Temp\CLD-ATM-3`, in the first session after the
agent-roster and typed-input changes of §12d landed. Agent definitions are read at SESSION START and
a client fetches the tool schema once, so this run is their acceptance test as much as a build.

**The design was not re-derived.** The four leaf agents built their ten subVIs from scratch, but the
three VIs the orchestrator owns - `Apply ATM Transaction`, `Handle ATM Action` and the producer/
consumer main VI - were generated from run 2's AIXML rather than re-authored. Their cost is
therefore not comparable and is excluded from every figure below.

### 13a. THE TYPED-INPUT FIX PAID OFF EXACTLY WHERE IT APPLIES, AND NOWHERE ELSE

Four agents, same four scopes as run 2, measured end to end:

| agent scope | run 2 | run 3 | tool calls |
|---|---|---|---|
| messages + menus | 416 s | **219 s** | 34 → 22 |
| decision tables | 632 s | **306 s** | 63 → 34 |
| file layer | 493 s | **493 s** | 40 → 46 |
| control references | 408 s (3 VIs) | 503 s (**5** VIs) | 37 → 56 |

The two that halved are the two whose subjects take only strings, numbers and paths: both drove the
VI under test DIRECTLY, where run 2 had to generate throwaway copies with the values baked into the
control defaults. The file-layer agent did not move at all, and its own report says why - `records`
is a **2D array control**, which the typed setter still cannot reach, so it built scratch copies
exactly as before. The control-reference agent was given five VIs instead of three; per VI it went
from 136 s to 101 s.

**Stopping after the first two would have produced "a factor of two" and that would have been
wrong.** The honest statement is narrower and more useful: the fix removes the workaround for path,
numeric and boolean inputs, and changes nothing for array or cluster inputs — which is precisely
what its own limits say, now confirmed from the cost side rather than from a probe.

### 13b. THE ROSTER FIX IS ACCEPTED

The control-reference agent called `lvai_placeholder_subvi` and reported a socket
(`LVMCP Stub 7433359d5c.vi`) for the orchestrator to swap. In run 2 the same agent answered
`No such tool available` and **hand-built a socket VI from AIXML**. Nothing else about the task
changed, so this is the roster entry and not the prompt.

### 13c. `Bundle` DOES NOT EXPAND FROM AIXML — a second node with fixed arity

Measured by an agent building a three-element cluster: `Bundle` with three repeated `element:`
inputs produced a **two**-element cluster and validation refused the document. The working shape is
`Bundle By Name`, fields first and `input cluster` last, seeded from an empty cluster constant of
the target type.

This is the same class of trap as `Format Into String`, whose arity §8 of the AIXML reference
already records as fixed at one `input 1`. **The generalisation to carry is that "expandable in the
IDE" does not imply "expandable from AIXML"**, and the failure is a wrong-sized value rather than an
error naming the node — the refusal that follows names the *wire*, one step downstream.

### 13d. THE `error in` FLAG IS NOT PINNED BY ANY RULE, AND AGENTS DIVERGE ON IT

Of the ten agent-built subVIs, the four from two agents declared `error in` as `recommended` and the
six from the other two declared it **`optional`** — one agent also marked `button index` optional.
Every pane passed `lvai_connector_pane` with 0 violations, and `lvai_check_aixml` is silent, because
the house rule requires `connection=` to be PRESENT on a `conIdx` terminal and only repairs a
missing one on an **output**. Both values behave identically at the call site.

**The visible consequence is the placeholder cache.** `PlaceholderTools.Signature` includes the
connection flag, so `error in: optional` and `error in: recommended` hash differently: five of the
eleven sockets this run were fresh clones of a pane that already had one, and the orchestrator's
carried-over AIXML had to be re-pointed at the new names. Harmless here, and it would not stay
harmless in a build that reused stubs deliberately.

Not fixed in this run — normalising it means regenerating seven VIs for a cosmetic flag, which the
"do not regenerate for a comment" rule argues against just as strongly. The cheap repair is one
sentence in the error-cluster rule naming `recommended` for `error in`, so that agents stop choosing.

### 13e. A SWAP CAN REPORT `Error 1055` FOR WORK IT ALREADY DID

One of five swaps answered `ok: false`, `errorCode 1055`, `errorKind: noActiveProject`,
`nodesSwapped: 0` — with a project demonstrably active, because the four calls issued alongside it
succeeded. Re-running it alone gave the identical answer. The tell is in the same payload:
`diagramSubVis` listed **`Get Controls By Label.vi`**, the real target, so the swap had landed and
saved; the 1055 comes from the traversal that runs afterwards, on a diagram with no socket left to
find. `lvai_exec_state` on the VI answered 1.

So the rule this file already states applied cleanly: **ask the file, not the tool's verdict.**
`socketsNotOnDiagram` naming a socket while `diagramSubVis` names the real target is the signature
of a swap that succeeded, not one that failed.

### 13f. THE `error in` FLAG IS ENFORCED NOW — and the Python half shipped DEAD for one revision

§13d recorded the divergence and proposed one sentence in `CLAUDE.md`. **That would not have fixed
it**, and the reason is already in this repository's own notes: an agent's system prompt is its own
definition, and `CLAUDE.md` is not in it. The four agents diverged precisely because the rule they
were meant to follow is invisible to them. So the fix went where every route passes:

| | |
|---|---|
| `lvai_check_aixml` | `errorInNotRecommended`, Warning, and `fix: true` repairs it |
| `scripts/aixml_lint.py` | `conn-error-in-not-recommended`, warning |
| `CLAUDE.md` | one bullet, for the direct route that spawns no agent |

**A second, larger gap fell out of reading the code for it.** `CheckTerminalWireRules` returned early
on *any* `connection` attribute, so it only ever caught an OMITTED one — an output written
`connection="required"` **on purpose** travelled through untouched. `scripts/aixml_lint.py` had
caught exactly that since 2026-09-15 (`conn-required-output`). So the two implementations of one
rule had drifted again, in the same direction and for the same reason as `SafeUidBase`: nobody
compared them. The C# side now has `outputTerminalIsRequired`, warned and repaired.

**AND THE NEW LINT RULE SHIPPED DEAD, which is the part worth keeping.** It compared
`e.label() == "error in"`, and `label()` renders as `Control 'error in'` — so it matched nothing,
ever. Everything around it was green: five new C# tests passed including a control arm, the C# twin
worked, and `aixml_lint.py` over all 45 shipped scripts was unchanged. **Nothing exercised the
Python rule at all**, because no file under `scripts/` carries the fault it looks for, and the
repository has no unit test for the lint's rules — only a pass that lints the folder.

What caught it was running it against a **real artefact of the build it came from**: the placeholder
stub `lvai_placeholder_subvi` had cloned from `Plan ATM Response.vi`'s actual pane, carrying
`error in: optional`. The lint said `[clean]`. Fixed to compare `e.name`, it warns, and the stub
cloned from a `recommended` pane stays clean — a control arm made of two real files rather than two
fixtures.

This is "a tool tested against a plausible fixture is not tested" one step further on: **the Python
rule was not tested against anything**, and its C# twin being green is what made that invisible.
The Python half still has no unit test — verified here by real-artefact runs, and said plainly
rather than implied.

## 14. THE FOURTH COLD BUILD, 2026-09-17 — what a documented design is worth

The same exam rebuilt into `C:\Temp\CLD-ATM-4`, agent-driven, with the notes above available and
nothing else carried over: no `.vi`, no `.lvproj` and no AIXML file from any earlier run was
copied. What *was* reused is the DESIGN — the 14-VI split and every connector pane — and
harvesting it is the single largest change in the numbers.

| phase | wall | calls | output tok | new input tok |
|---|---|---|---|---|
| P0 spec reading (PDF to text) | 1:01 | 9 | 3 091 | 23 345 |
| P1 design + contract harvest | 3:35 | 11 | 15 745 | 35 754 |
| P2 build, 14 VIs, 4 agents in parallel | 10:50 | 24 | 56 534 | 98 680 |
| P3 integration, 11 swaps, smoke run | 2:44 | 21 | 8 779 | 23 951 |
| P4 verification (Caraya agent + one correction) | 39:41 | 43 | 20 545 | 36 335 |
| **orchestrator total** | **57:51** | **108** | **104 694** | **218 065** |

Sub-agents, all concurrent inside their phase: presentation 204 618 tok / 33 calls / 255 s,
decision 210 397 / 33 / 326 s, control references 209 707 / 40 / 419 s, file layer 249 563 / 55 /
617 s — **874 285 tokens and 1 617 s of agent time inside 650 s of wall clock**. The Caraya agent
cost 475 497 tokens over 195 calls in P4.

**Application complete and executable in 18:10**, against run 1's 114:34 (42:57 active). The suite
is `55 assertions, 0 failures, 0 errors` over 13 test VIs, with a negative control run and
reverted.

### 14a. THE CONTRACT HARVEST IS TWO CALLS AND IT REPLACES A DESIGN PHASE

`lvai_convert_vis_to_aixml` over the previous build's 14 VIs answered in **400 ms**, and one
`grep` for the `Control`/`Indicator` elements carrying a `conIdx` printed every connector pane with
its type literals, `connection` flags and terminal descriptions — about 90 lines. That is the whole
interface contract of the application, with no implementation read and no design re-derived.

It is worth naming as a move because the obvious alternative is worse in two ways.
`lvai_vi_terminals` is the tool that looks right for it and **cannot do it**: it declares `viPaths`
(plural) but *requires* `viPath`, and given both it answers for `viPath` alone — 14 paths in, 1
answered, no note that the other 13 were dropped. Same shape as the `runForMs` finding of
2026-09-16: **a parameter that one mode ignores must not silently defeat that mode.** And even if
it worked, its output carries terminal names and types but not the `value` literals, which the
batch export does.

### 14b. THE PANE SIGNATURES CAME BACK IDENTICAL — 9 OF 10 PLACEHOLDERS REUSED

Four agents, working from contracts alone and never seeing each other's or the previous build's
code, produced connector panes whose `PlaceholderTools.Signature` matched a stub already in
`user.lib\LV_MCP` in **9 of 10** cases. The tenth, `Resolve ATM Action.vi`, differed because run 3
had authored `error in` as `optional` and the `errorInNotRecommended` check added on 2026-09-16 now
forces `recommended`.

That is the check of section 13f measured from the cost side: 13d recorded four agents diverging on
that flag and five of eleven sockets being cloned afresh for panes that already had one. With the
rule enforced in `lvai_check_aixml` rather than only in `CLAUDE.md`, the divergence is gone —
**because an agent's system prompt is its own definition, and a check is in every route.**

### 14c. FIRST-TRY GENERATION ON BOTH HAND-AUTHORED VIs

`Handle ATM Action.vi` (11 kB of AIXML, a 2-frame case over 10 tunnels, three placeholder calls):
validate + convert + pane in **800 ms**, `paneViolations: 0`, no retry.

`ATM Main.vi` (18 kB, two While loops, an Event Structure with four frames, three shift registers,
two nested Case structures, seven placeholder calls): `lvai_validate_aixml` returned **only** the
four expected pre-registration complaints — three `Event Data Node: Cluster is invalid or empty`
and one `Event Structure: One or more event cases have no events defined` — and nothing else.
`lvai_generate_vi_with_events` then registered 4 of 4 and answered `execState 1`.

Run 1 needed 3 validate/fix rounds on its main VI and run 2 met the `selectout` trap on an event
frame (12a). Nothing new was learned here, which is the point: the traps in 2, 3, 4, 12a and 12c
were all avoided by reading them, and each had cost a round trip when it was first met.

### 14d. THE DEPOSIT PROMPT IS TWO LINES, AND THE TEST BRIEF IS WHERE THAT WENT WRONG

The suite came back `2 failures`, both on the deposit and withdrawal prompt texts. The subject was
right and the **expectation written into the test agent's brief was wrong**: the exam's message
table reads, verbatim,

```
Deposit Message      Please enter amount to deposit and
                     press Enter (E) when done
```

— the break falls after `and`, and the continuation is a lower-case `press`, unlike the Welcome
message's capitalised `Press Enter (E) when done.` The orchestrator had flattened both to one line
when writing the test brief, while the design brief the *generator* agents read carried the break
correctly.

Two things worth keeping. **The discriminator was the PDF, not either agent's reasoning** — one
`extract_text()` on page 6 settled it in one call. And **the same specification was paraphrased
into two agent prompts and they disagreed**, which is the multi-agent version of "two
implementations of one rule drift": the message texts should have been quoted once, from the brief
file both sides read, rather than retyped for the test side.

### 14e. A FAILED CARAYA ASSERT ERRORS EVERY LATER ASSERT IN THE SAME VI

Found by the test agent in its own first build, and general. Chaining each assert's `error in` to
the previous assert's `error out` is the natural-looking shape, and a **failed** assert emits
error 1 — so every later assert in that VI reports as an *error* rather than running. Its first run
read `1 failure, 5 errors` and five real assertions had silently not executed; one of them was the
second prompt mismatch, which only appeared after the fix.

The shape that works: every assert takes `error in` from `Define Test`, and one `Merge Errors`
collects them. The subject and file calls keep their own chain, which is what orders
seed to act to read.

### 14f. DIAGRAM SIZE, THIRD DATA POINT — WIDTH STILL FOLLOWS THE CHAIN

| build | top-level diagram |
|---|---|
| run 1, after factoring | 2665 x 1094 |
| run 2, designed for it | 2865 x 856 |
| **run 4** | **2639 x 921** |

Still over the 1920 guideline in width and comfortably under it in height, on a build whose
consumer loop is the same twelve-stage pipeline. Section 11's finding holds at a third point:
factoring buys height, width is the longest dependency chain, and AIXML carries no coordinates so
the pipeline cannot be wrapped onto a second row.

### 14g. THREE SMALLER THINGS

- **`lvai_check_aixml`'s parameter is `aiXmlFilePath`.** `aixmlPath` is refused rather than folded
  — the near-miss fold normalises `_`, `-` and case, and that pair is past it. One round trip.
- **A repeated socket still costs one `lvai_swap_subvis` call per node.** `ATM Main.vi` calls
  `Get Pressed Button Index.vi` and `Set Controls Disabled.vi` twice each, so the seven-entry swap
  answered `socketsLeft: 2` and a second call finished it — exactly as `docs/cold-build-kilnrig.md`
  section 2 measured, and still two avoidable calls.
- **The controller rewrites `ATM accounts.txt` with CRLF** where the supplied file uses LF
  (107 to 111 bytes). `Array To Spreadsheet String` emits the platform's line ending. The data reads back
  correctly either way; flagged rather than changed, because the exam says nothing about it.

### 14h. THE SUPPLIED PANEL, UNCHANGED AFTER FOUR RUNS

Section 9's gap is exactly where it was. AIXML writes a VI whole, so the exam's own
`Automatic Teller Machine (ATM).vi` cannot be given a generated diagram and keep its artwork. The
deliverable ships `ATM Main.vi` beside it, carrying the same eight panel objects under the same
names and types, plus a four-step IDE merge written into
`README - assumptions and structure.md`. **This is the only part of the exam the toolchain cannot
finish**, and it is worth stating as a stable limit rather than a per-run surprise: the route that
would close it is VI Server diagram scripting, which is unmeasured here.
