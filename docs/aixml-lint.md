# Linting AIXML offline

`scripts/aixml_lint.py` checks an AIXML document for the faults that are decidable
**without LabVIEW** — no gRPC service, no licence, no network, Python 3 standard library
only, so it also runs in CI and on a machine that has no LabVIEW at all.

```bash
python scripts/aixml_lint.py <file.xml> [...]          # human readable
python scripts/aixml_lint.py --json <file.xml>         # machine readable
python scripts/aixml_lint.py --min-severity warning <file.xml>
```

Exit code 1 when at least one finding is an error, 0 otherwise.

It is a **pre-filter, not a replacement**: run it before `lvai_validate_aixml`, never instead
of it. Anything needing types — terminal names against a node's signature, wire type
compatibility, case completeness — is LabVIEW's job and a second implementation would drift.
`lvai_check_aixml` remains the in-server counterpart; this tool overlaps it deliberately on
duplicate uids and enum defaults and goes past it on the three checks below.

## Why: the three faults nothing reported

Measured over one session, **~24 of ~80 tool calls were corrective**, and three of those fault
classes were offline-decidable while both `lvai_validate_aixml` and `lvai_check_aixml`
answered clean.

### A — `uid_parent` names an element that EXISTS but is the wrong one

The expensive one, because nothing anywhere reports it. Measured: a `CaseFrame` of a boolean
Case Structure carried the `uid_parent` of an entirely different state-dispatch structure.

```xml
<Structure _name="Case Structure" selectin="420.state" uid="500" uid_parent="root">
  ... 9 CaseFrames ...
</Structure>
<Structure _name="Case Structure" selectin="dec.Do_Write" uid="960" uid_parent="root">
  <CaseFrame selector="True"  uid="970" uid_parent="500"/>   <!-- wrong: 960 -->
  <CaseFrame selector="False" uid="980" uid_parent="960"/>
</Structure>
```

`lvai_validate_aixml` answered `errorCode 0` and so did `lvai_check_aixml`, whose check is only
*does `uid_parent` name some element* — and `500` does exist. LabVIEW follows `uid_parent` and
puts the frame on the diagram it names, silently.

The rule that catches it exactly: **an element lexically nested inside an element with
`uid="X"` must carry `uid_parent="X"`**, and `uid_parent="root"` at top level. A `uid_parent`
may only name a kind that can hold children — `Structure`, `CaseFrame`, `Diagram`, `ShiftReg`,
`Node` (a disable structure is a `Node` with `Diagram` children) or `root`; one naming a
`Constant` or `Indicator` is always wrong.

### B — a net is consumed but never produced

LabVIEW reports `<Element>: Contains unwired or bad terminal` and names **the consumer**, which
sends you to the wrong end of the wire. Measured on frame tunnels producing `1000.first` while
the `Concatenate Strings` beside them read `1001.first`.

The producer and consumer sets are built **document-wide, not per scope**. An input tunnel is
implicit: a node inside a Case frame may read a root net directly and LabVIEW creates the
tunnel itself, measured and executed. A scope-by-scope analysis reports those as unproduced and
is wrong.

### C — a net is produced but never consumed

Warning only: an unused output is legitimate. It earns its place because a **mistyped net name
shows up as a B and a C together**, so that pair is reported as one `net-typo` finding with the
likely intended name (`difflib.get_close_matches`, cutoff 0.8).

## The direction table, and the one row of it that was wrong

Which attribute produces and which consumes is the whole analysis; get it wrong and the tool is
worthless. The implementation takes the direction from the **attribute**, not from the element
kind or its position:

| attribute | direction |
|---|---|
| `outputs` | produces every net it names |
| `inputs` | consumes every net it names |
| `Structure` `count` | produces — it is the loop's own `i` output net |
| `Structure` `maxin` | consumes — this is what wires `N`, `count` does not |
| `Structure` `maxout` | produces (assumed; empty in every document measured here) |
| `Structure` `selectin` | consumes |
| `CaseFrame` `selectout` | produces |

One rule for `inputs`/`outputs` covers every shape a `Tunnel` is written in — `inputs` only at
structure level for an `In` tunnel, `outputs` only inside the frame, the mirror image for `Out`,
and **both in the single element a loop tunnel is written as**. It covers `ShiftReg` too.

**And that is where a hand-written direction table got one row wrong.** A specification handed to
this tool said `ShiftReg`/`Right` produces nothing and only consumes its `inputs`. Measured
2026-09-09 against `scripts/lvai_run_and_read.xml`, a shipped helper that runs constantly:

```xml
<Right inputs="value:121.error out" outputs="value:140.value" uid="140" uid_parent="130"/>
```

`140.value` is read by the `Run VI` invoke node *after* the loop, and nothing else writes it —
because a `Right` terminal's output net leaves the loop on its own, which is also why adding an
`Out` tunnel for one produces `Is a member of a cycle`. Under the table's rule the linter
answered:

```
ERROR net-unproduced - net "140.value" is read by Node 'Invoke Node'
       inputs:error in (no error) but no element writes it.
```

a false error on working, shipped code. The shape rule answers `0 errors` for the same file. The
lesson is the ordinary one: **a direction table derived by reading is a hypothesis; the corpus
is the authority.**

## The parsing trap, and what it actually is

In `inputs`/`outputs`/`fields`, `:` separates a terminal name from a net name and `,` separates
entries, so those characters are escaped inside names — `\3A` colon, `\2C` comma, `\0A`
newline, `\5C` backslash. Real examples: the `Select` primitive's output is `s? t\3Af` (15
occurrences in `ATM State Machine.xml`); a `Property Node` carries
`fields="write+Front Panel Window\3AState"`; terminal names read `error in (no error)` and
`max queue size (-1\2C unlimited)` (8 occurrences in `scripts/lvdqmh_dlg_keyfocus.xml`).

**The trap is the ORDER, not the split.** The escape *replaces* the character, so the raw
attribute value holds no literal colon for those names and a plain split is already correct.
What breaks is unescaping first: `s? t\3Af` becomes `s? t:f` and then splits into two phantom
entries. So: **split, then unescape** — asserted in the suite as its own case.

A net name is an **opaque token**. The `uid.terminal` convention is only a convention —
`dec.Do_Write` and `banana.value` are both valid — so net names are compared, never decomposed.

## The type grammar, and the message that names nothing

A `type=` is parsed against the grammar of `docs/aixml-reference.md` §5. The failure it exists
for, measured 2026-09-09: renumbering some uids turned `int32.Record Index` into
`int82.Record Index` inside a cluster type string, and LabVIEW answered

```
Error 53 ... Unrecognized or unsupported attribute set in Constant with UID 90
```

— the **element**, not the attribute and not the place in the string. Finding it cost a separate
bisection probe. The linter names the substring instead: `unknown base type 'int82' - did you
mean 'int32'?`

The base-type set is an **allowlist measured against the real validator** — one Constant of each
in a single probe, `errorCode 0`:

`bool string path double single int8 int16 int32 int64 uint8 uint16 uint32 uint64 variant timestamp`

Also refused, both re-measured: `array{array{double}}` and unbalanced braces (`array{string`),
each with the same shapeless Error 53.

**Enum labels, ref kinds and `{LV.Class}` bodies are parsed as OPAQUE.** They hold names, not type
tokens, and a scan that treats them as tokens "discovers" thirty unknown types in a corpus that
works — `uint32{not a typedef,typedef,strict typedef,class private data}` alone yields eight.

## Property and Invoke nodes — and why the catalogue cannot settle the vocabulary

Two shapes are refused and both are checked, measured 2026-09-09:

| written | LabVIEW |
|---|---|
| `link="User Input"` on a `Property Node` | `Error 53 ... Node "Property Node" with UID 4300` — an implicit/linked property node is not authorable |
| `fields="read+Label\3AText"` on `{LV.Control}` | `Property Node: Invalid property`, **plus a cascade** onto innocent nodes (`the type of the source is void`) |
| `fields="read+Label.Text"` on `{LV.Control}` | **`errorCode 0`** |

**So a nested property IS authorable, and the separator is a DOT.** The report this work came from
concluded "two nodes, no nested property" — the two-node chain (`{LV.Control}`→`Label`→
`{LV.Text}`→`Text`) does validate, but it is not required, and `scripts/lvdqmh_args_paste2.xml`
has been shipping `Label.Text` all along.

Telling the two colon forms apart needs no catalogue completeness, only a **positive** lookup: the
head of a legitimate colon name is a category prefix that is not itself a property (`Project`,
`Connector Pane`, `Front Panel Window`, `Application`, `Typedef`), while `Label` is. That rule
fired on **0 of the 33** colon-bearing property names in the shipped helpers and correctly on
`Label:Text`. It is a warning, not an error, because the negative half of it rests on a catalogue
that is provably short.

**And that shortness is the reason there is no general membership check.** Measured over the 39
shipped helpers — every one of which generates and runs — **94 of their 240 Property/Invoke
references are absent from the catalogue**: 55 of 162 properties and 39 of 78 methods, exactly
half. The cause is that `docs/vi-server-*.tsv` is keyed by **display name** while AIXML wants the
scripting identifier: `{LV.VI} Set Control Value [Variant]` is listed and `Ctrl Val.Set` — what a
node must actually say — is listed nowhere. Even the class column is short; `{LV.Project}` and
`{LV.LVClassLibrary}` are used by shipped helpers and missing, which is the same gap
`docs/labview-crash-signatures.md` notes from the other side. A membership check would therefore
accuse working code, and that is worse than no check.

## Terminal lists: one shape is refused, three are not

`inputs=""` is not the same as leaving the attribute off — measured, it answers `Object terminal
not found for input: : on Current VI's Path`, while omitting it answers `errorCode 0`.

**The other three shapes the specification listed are accepted**, and two of them were about to
ship as false positives: `scripts/lvai_add_class_method.xml` and `scripts/lvlu_add_test_method.xml`
both carry `outputs="reference out:,error out:,"` and both run. One probe per shape:

| written | LabVIEW |
|---|---|
| `outputs="path:4200.path,"` trailing comma | `errorCode 0` |
| `outputs=",path:4200.path"` leading comma | `errorCode 0` |
| `outputs="path:4200.path,,"` doubled comma | `Error 1`, `Only 1 output object terminals named ''` |
| `outputs="path"` no colon at all | `errorCode 0` |

So the rule counts empty entries rather than hunting for a comma in the wrong place: one is
tolerated, a second is not.

## Only the FIRST validator error points at its own element

The most expensive single behaviour behind this tool, and it is about reading `ValidateAIXML`
rather than about the linter. Measured 2026-09-09 on the nested-property probe: one wrong
`fields` entry produced

```
Property Node: Invalid property
You have connected two terminals of different types.
  The type of the source is void.
  The type of the sink is string.
```

— the second complaint names a node that is perfectly correct, and only became typeless because
the first node failed to produce its output. The same shape was seen at larger scale in the
session this work came from: two `inputs=""` attributes made the validator additionally accuse a
15-field cluster `Constant` and an error-cluster `Indicator`, both of which validate with
`errorCode 0` in isolation.

**So fix the first error and validate again; do not work down the list.** Chasing the later
entries is chasing damage, not causes. This is also the strongest argument for the linter
existing at all: it reports every finding against the element that actually carries it, because
it never has to execute anything to find out.

## Calibrated against 668 exports LabVIEW wrote itself

The 39 shipped helpers are the corpus these checks were TUNED against, so a clean run over
them proves little. The independent corpus is the AIXML export cache under
`%USERPROFILE%\.labviewmcp\cacheixml` - 668 documents, 15 MB, produced by LabVIEW's own
exporter from installation VIs. Nothing there was used to tune anything, and the first sweep
was damning:

| | files with at least one error |
|---|---|
| first sweep | **316 of 668 (47.3 %)** |
| after the fixes below | **0 of 668 (0.0 %)** |

Five causes, one a real bug and four a dialect difference:

1. **The type parser split cluster fields on commas without counting braces**, so the labels of
   a nested enum tore the type apart - `cluster{uint16{Dark Roast,Decaf}.Kind,string.Name}`
   became four fragments and three "unknown base type" errors. 1166 findings, nearly all of
   them this. Fixed with a depth-aware split.
2. **Eleven base types occur only in exports**: `doublewaveform`, `picture`, `dynamicdata`,
   `complexdouble`, `complexextended`, `digitaltable`, `digitalwaveform`, `set`, `map`,
   `extended`, `fixed`, plus `void` for an unwired pane slot and `function{...}` for a pane
   signature inside a `ref{LV.VI}` payload.
3. **A `{...}` class body is not always `LV.something`** - `GenClassTag.DAQmx Timing.NIDAQ` and
   `Visa.USB Raw` both occur, spaces included. The shape check was invented and is gone.
4. **A DOUBLED backslash is a valid escape for a literal one.** `value="[\t\n]*$"` in a
   regex constant answers `errorCode 0` - measured - and NI writes it 67 times. The rule now
   accepts `\` beside `\XX`.
5. **`link=` cannot be an error**, because LabVIEW's exporter writes it on 275 nodes. Authoring
   one has not been made to validate - one probe gave Error 53 without a `type=`, another gave
   `Invalid property` with one - so it is a warning that says both halves.

### The exporter spells "unwired" differently from the way you author it

The last 42 errors were all one convention. Authoring writes an unwired terminal as an empty
net, `inputs="value:"`. The EXPORTER writes a net named after the consuming element's own uid:

```xml
<Indicator _name="Bounds" inputs="value:121.value" uid="121" uid_parent="root" .../>
```

in a document whose only elements are one VI, two Controls and that Indicator - no diagram
content at all, so nothing can possibly write `121.value`. A net that names its own consumer
and has no producer feeds nothing in either dialect, so it is dropped rather than reported.

**ONE self-named consumer is enough to drop it, not all of them.** Requiring all left two more
false positives, both LabVIEW's own: in `Align and Subtract two Waveforms (continuous).vi` an
`Or` node reads `1481.x` - its own uid - and an output tunnel reads the same net alongside it;
in `Timed Loop Abort.vi` a Case Structure's `selectin` reads `228.value` and a
`Format Into String` reads it too. The extra reader does not make the net any more produced.

## A `.ctl` goes through, and that is exactly the trap

The linter does not care where a document came from - it lints AIXML. So the question "can I
lint a `.ctl`?" is really "can a `.ctl` be exported to AIXML?", and the answer is no in a way
that reports success. Re-measured 2026-09-09 on one of NI's own shipped controls, so anyone can
repeat it:

```
lvai_convert_vi_to_aixml  vi.lib\silver_ctls\IO\DAQmx Task Name NI_Silver.ctl
   ->  errorCode 0, 58 bytes
<VI _name="DAQmx Task Name NI_Silver.ctl" description=""/>
```

No cluster, no field, no name - `docs/aixml-reference.md` section 16 measured the same thing in
August. The document is valid AIXML and the linter used to answer **`[clean]`** for it, which is
a bill of health for something that was never examined. It now reports `empty-document`, a
warning that names the cause and points at `pylv_extract` / `lvai_describe_ctl`, which read the
whole heap and need no LabVIEW.

**Not only controls.** 4 of the 668 cached exports are an empty `<VI/>` too, every one a
Tools-menu VI - DQMH's `Add New DQMH Module.vi` among them - which `CLAUDE.md` records as having
no connector pane at all.

## Four rules that measurement refused

Written down because the specification asserted each of them and the validator disagreed — and
because a check that reports what LabVIEW accepts is worse than no check:

| proposed rule | measured |
|---|---|
| a `cluster{}` field without `.Name` is an error | `cluster{bool,int32}` → `errorCode 0` |
| a leading comma in a terminal list is an error | `errorCode 0` |
| a trailing comma is an error | `errorCode 0`, and it is in two shipped helpers |
| a terminal entry without `:` is an error | `errorCode 0` |

## The cheap checks that come along

So that a local call is enough to release a document:

| check | severity | why |
|---|---|---|
| `uid-duplicate` | error | LabVIEW silently renumbers, so a later export stops matching the file. `uid="0"` is a sentinel that may be reused and is excluded |
| `uid-low` | info | LabVIEW logs `trying to override with non-reserved UID` and assigns its own id |
| `conn-missing-output` | **error** | an `Indicator` with `conIdx` and no `connection=` is read as `required`; every caller leaving it unwired is `Error 1003` while this VI validates, runs and exports perfectly |
| `conn-missing-input` | info | the same omission on a `Control` — a legitimate choice, but say it explicitly |
| `enum-label` | error | an enum `value` given as a LABEL is DISCARDED and written as `0`, silently |
| `enum-out-of-range` | warning | an index past the last item is CLAMPED, silently |
| `array-nested` | error | `array{array{…}}` is refused; a multidimensional array is `array.N{…}` |
| `value-raw-backslash` | error | a backslash in a `value` must be `\5C`; a raw one is `Error 42` and loses the WHOLE file |
| `conn-required-output` | error | an explicit `connection="required"` on an Indicator does the same damage as omitting it |
| `case-frame-missing-out-tunnel` | error | an `Out` tunnel a frame does not assign leaves that case's output unwired |
| `case-frame-orphan-tunnel` | error | a frame-level `_id` the structure never declared connects to nothing outside |

## Four checks were proposed; the corpus rewrote two of them and exposed a fifth

Measured 2026-09-09 over 52 proven documents — the ATM corpus plus every shipped `scripts\`
helper — **before** writing any of the four. Two survived as proposed, two did not, and the
measurement found a defect in a check that was already there. This is the whole argument for
deriving a rule from the corpus rather than from prose:

| proposed | what the corpus said |
|---|---|
| a raw backslash is an error in any escaped attribute | **narrowed to `value`.** Every backslash in a `value` is a valid `\XX` pair (26 of them) — but `description` carries three RAW ones in `scripts/lvai_class_names.xml`, a shipped helper that runs, quoting a Windows path in prose (`vi.lib\Utility\traverseref.llb\…`). So `description` tolerates one and flagging it there is a false positive on working code |
| `connection="required"` on an Indicator is an error | **kept.** 88 Indicators carry `conIdx` and every one says `recommended`; none says `required`, so the check costs no false positive |
| a Ring `value` outside its `values` is a finding | **not implemented.** No Ring occurs in any of the 52 documents, so its type spelling is unknown — and inventing a spelling is the documented failure mode this repository has been burned by. The enum check below covers it *if* the two share a spelling, at a severity that is safe either way |
| every frame must declare every tunnel, and a Case needs a Default frame | **both overturned.** `scripts/lvai_add_class_method.xml` and `scripts/lvlu_add_test_method.xml` each have a frame declaring only `Out1` and omitting `In1`–`In3`; both are shipped and run. And **not one** of the 8 case structures has a Default frame, including two with plain string selectors, because a fully covered enum needs none. Only the OUT direction is checked |

**And the fifth: `enum-label` and `enum-out-of-range` were DEAD CODE on real files.** They tested
`type.startswith("enum")`, and AIXML does not spell an enum that way — it is an integer type
carrying its items, `uint8{Init,Wait Card,Main Menu}`, 12 times in the ATM corpus and in six
shipped helpers. The checks fired only for the hand-written fixture that had agreed with the bug:
**a tool tested against a plausible fixture is not tested**, for the fourth time in this
repository. Fixed and then proven live rather than assumed — `scripts/lvai_close_vi.xml` carries
`uint32{Invalid,Standard,Closed,Hidden,Minimized,Maximized}` with `value="2"` and stays silent;
replace that `2` with its own label `Closed` and the check answers `ERROR enum-label`.

**`uid-low` is now BELOW the default output**, which is why `--min-severity` defaults to
`warning`. It fires 392 times on the shipped helpers, and acting on it has a measured cost: one
session renumbered 12 uids to silence it and that renumbering introduced the `int82` type typo
above, which then took a bisection probe to find. The information stays available with
`--min-severity info`; it is no longer put in front of anyone by default. A guard that provokes a
risky edit for an unproven benefit should not lead the report.

**The threshold is not settled either, and the tool says so rather than implying precision.**
It is set at 42, which is what the specification asked for. That does not contradict the
controlled pair in `docs/labview-crash-signatures.md` — uids `10,11,12,13` cost one DWarn event
each while `4200,4210,4220,4230` cost none — but it is not measured by it either: the real
ceiling is somewhere between 13 and 4200 and 42 sits in that gap by assertion. Severity is info
either way, so nothing depends on it.

## Acceptance, against real files

Run `python scripts/aixml_lint_test.py` — 41 checks with no external corpus, 46 with it, fixtures
written to a throwaway temp directory and never into a corpus. Negative cases are produced by **mutating** real documents
one fault at a time, and each is judged by *difference* against a control: the same document
round-tripped through `ElementTree` with no mutation. Comparing against the control rather than
the pristine file means the serialiser's own habits cannot be mistaken for a finding, so the
assertion is exactly "this mutation adds precisely this check and nothing else".

**THE STANDING POSITIVE CORPUS IS `scripts\*.xml`, IN THIS REPOSITORY — and that is a correction
made twice in one day.** The ATM corpus lives outside the repository and is edited by whoever is
generating VIs. Written against 8 documents, the suite ran the next day against 13 with several
renamed — failing on a hardcoded file name and an `== 8` for a reason that had nothing to do with
the linter — and an hour later the whole working tree had been cleaned back to the bare exam and
the suite ran **nothing at all**, returning exit 2, including the splitter unit tests that never
needed a corpus.

Both are now fixed, and the second matters more. Each mutation DISCOVERS a document carrying the
shape it needs, and it looks in the **in-repo helpers first** — they carry every shape the
mutations want (two Structures with a `CaseFrame`, frame tunnels, an `Indicator` with `conIdx` and
`connection`) and they cannot be cleaned up under the suite. Only the external positive corpus is
now optional. For a linter whose premise is *runs with no LabVIEW, in CI, on a machine that has
none*, a suite that needed an absolute path under `C:\Temp` to start was the wrong shape — and
leaving four mutation cases permanently skipped once that tree vanished was regression coverage
lost for four checks.

| corpus | files | result | when |
|---|---|---|---|
| `scripts\*.xml` | 39 | **0 errors**, 122 warnings, 370 info | standing; re-run on every change |
| `ATM (100928D-01)\Generator\*.xml` | 13 | **0 errors**, 4 warnings, 0 info | 2026-09-09, before that tree was cleaned |

Both are proven code: the ATM documents were all generated and several executed, and the 39 are the
helpers this server runs on. Every warning was hand-checked, which is the point of running against
files known to work — all are `net-unconsumed`, and all are true findings:

- Two are a placeholder stub `Call`'s `error out` and one a `Property Node`'s, none of them read.
  Everyday and legitimate.
- One is a **`Control`** whose value nothing reads, in `probe_props.xml` — a probe VI, where an
  unused control is exactly what you expect. This case earned the check a sharper sentence: an
  unread node output is a convenience, whereas an unread `Control` means the VI accepts an input
  on its front panel and ignores it, so the message now says that instead.
- The **122 warnings** over `scripts\` are the same check, overwhelmingly an Invoke Node's
  `reference out` that nobody reads. True, and legitimate.
- The **370 infos** are all `uid-low`, and they are noise **by construction** on that corpus:
  CLAUDE.md deliberately does not renumber the `scripts\` helpers, and each is generated once and
  then cached under `%TEMP%\LabVIEWMCP\helpers\`. Use `--min-severity warning` there.

An earlier run of this table, over the 8-document corpus, recorded 10 warnings from a single
`Unbundle By Name` that listed all 14 fields of the session cluster in `fields=` and wired 4. That
document has since been rewritten and the finding is gone — recorded here because it was the
clearest true positive check C produced, not because it still reproduces.

No finding of check A, B, `enum-*`, `array-nested`, `uid-duplicate` or `conn-missing-output`
occurred anywhere in either corpus — which is what a clean pre-filter should look like on code
that works.

**One gap, named rather than left to be discovered: this suite hangs off no gate.**
`.githooks/run-tests.ps1` runs `dotnet test`, and `ShippedHelperAixmlTests` enumerates
`scripts\**\*.xml` — so a new AIXML helper is checked automatically while these two `.py` files are
invisible to the build. Nothing will report it when the linter rots. Wiring it in is not free
either: the positive corpus lives at an absolute path outside the repository, so a C# wrapper would
have to skip when Python or that corpus is missing rather than fail. Until that exists, **run
`python scripts/aixml_lint_test.py` by hand after touching either file** — the same failure shape
this repository has recorded twice, where a file being present said nothing about anything checking
it.
