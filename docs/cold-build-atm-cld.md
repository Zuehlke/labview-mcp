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

### 12c. AN EVENT DATA NODE'S FIELDS CANNOT FEED A SUBVI CALL — and the control's own terminal is better

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

### 12d. THE AGENT ROSTER GAP: `labview-vi-generator` CANNOT REACH `lvai_placeholder_subvi`

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
