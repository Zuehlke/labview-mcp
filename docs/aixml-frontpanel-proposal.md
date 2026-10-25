# Proposal: a front-panel section in AIXML

Status: PROPOSAL, 2026-10-24. Nothing here is implemented. It builds on two measurements:
`docs/aixml-frontpanel-extension.md` (what NI's gRPC route tolerates) and
`docs/frontpanel-picture-controls.md` (what a front panel object is made of, and what can be
rebuilt from LabVIEW's base controls).

## 1. Goal and non-goal

AIXML describes a diagram and the TYPES of its panel objects - never where they sit, what they
look like, or which `.ctl` they are an instance of. That is right for almost every VI: a generated
subVI's panel is arranged by Clean Up Panel and nobody looks at it.

The section proposed here is for the two cases where the panel IS the deliverable:

1. **Reuse a GUI element** - a control on the panel is an instance of an existing `.ctl` (a
   customer's strict-typedef picture button, a typedef'd cluster), placed where it belongs.
2. **A special GUI** - a main VI whose panel is laid out on purpose: positions, sizes, labels and
   captions, colours, fonts, decorations, and controls customised with images.

**Non-goal:** describing every panel. The section is OPTIONAL and absent by default; a document
without it means exactly what it means today.

## 2. The constraint everything follows from

- **NI's schema is closed.** Any unknown element or attribute inside `<VI>`, in any namespace, is
  `-2628` and costs the whole document (measured). Anything after `</VI>` is ignored. Anything
  BEFORE `<VI>` empties the VI in silence.
- **Only our composer can write a panel.** The pylabview backend composes the panel heap itself;
  NI's converter places panel objects where it likes.
- **So for gRPC the section is ALWAYS removed before the document reaches NI** - by our server,
  in one place, every time.

## 3. Where the section lives

Inside `<VI>`, as its LAST child, in our own namespace:

```xml
<VI _name="Main.vi" description="Operator panel." xmlns:fp="urn:labviewmcp:frontpanel:1">
  <Control _name="Exit" type="bool" uid="4201" uid_parent="root" value="false" outputs="value:4201.value"/>
  <Control _name="Line Style" type="uint16" style="Ring" items="a,b" values="[0,1]" uid="4202" uid_parent="root" value="0" outputs="value:4202.value"/>
  <Node _name="Not" inputs="x:4201.value" outputs="not x:4210.out" uid="4210" uid_parent="root"/>
  <fp:FrontPanel window="0,0,480,640" background="#E2E2E2">
    <fp:Object uid="4201" bounds="40,30,80,234" ctl="..\Controls\Exit Button.ctl"/>
    <fp:Object uid="4202" bounds="100,30,148,100" label="hidden"/>
  </fp:FrontPanel>
</VI>
```

Why here and not in the two alternatives the measurement left open:

| | inside `<VI>`, own namespace (proposed) | processing instructions after `</VI>` | sidecar file |
|---|---|---|---|
| structure | real XML: nested objects, parts, image lists, schema-checkable | one flat line per fact, values escaped JSON | real XML |
| NI without our filter | **loud**: `-2628` naming `fp:FrontPanel` | ignored | not seen |
| failure if the filter is skipped | nothing is generated, error names the element | none | none |
| failure if an editor moves it | stays inside `<VI>` | moved BEFORE `<VI>` -> **empty VI, `errorCode 0`** | two files out of step |
| one file to hand around | yes | yes | no |

The decisive row is the last-but-one: a missed strip fails LOUD and names itself, where the
trailing-PI form fails SILENT one edit away. The filter is mandatory either way - the user's
premise - so the section may as well be the form that punishes forgetting it.

**The existing `<?lvmcp …?>` lines after `</VI>` stay as they are.** They carry single facts
the reader needs for a round trip (`pane`, `classes`, `calllink`, …) and three of them are
panel facts (`subpanel`, `fpclass`, `xcontrol`). Those three move into `fp:FrontPanel` once
it exists; the composer keeps reading the old lines so no stored export breaks.

## 4. What the section says

Objects are addressed by the `uid` AIXML already gives every `Control` and `Indicator`; the
section never repeats a type. Units are panel pixels, `top,left,bottom,right` as LabVIEW writes
bounds (`docs/frontpanel-picture-controls.md`).

| element / attribute | meaning | level |
|---|---|---|
| `fp:FrontPanel window=… background=… origin=…` | panel window bounds, colour, scroll origin | 1 |
| `fp:Object uid bounds` | where the object sits and how big it is | 1 |
| `label="hidden"`, `caption="…"`, `labelBounds` | label visibility and position, caption | 1 |
| `colors="…"`, `font="Segoe UI" size="15" bold=…` | the object's colours and font (a FONT is a name and size, never an index - the file's font table is ours to build) | 1 |
| `disabled`, `visible`, `tabOrder` | state and key navigation | 1 |
| `ctl="path\X.ctl"` | the object is an INSTANCE of that `.ctl` (typedef or strict typedef, linked) - **the reuse case** | 1 |
| `base="silver:Push Button"` | palette style the object starts from (`vi.lib\silver_ctls\…`, NXG, System) | 2 |
| `action="latch when released"` | mechanical action by NAME, mapped onto the `objFlags` bits | 2 |
| `fp:Part id="88" image="img/x.png" bounds=…` | an image into an EXISTING part - never a part the base lacks | 2 |
| `fp:Part id="21" images="f.png t.png t2f.png f2t.png hf.png ht.png"` | a state table in LabVIEW's order (False, True, T->F, F->T, hover F, hover T) | 2 |
| `fp:Text value=… visible=…` | boolean text: LabVIEW creates the part when made visible | 2 |
| `fp:Element name=…` inside an `fp:Object` | the same, for a cluster element | 2 |
| `fp:Decoration kind=… bounds=…` | free decorations, panel images | 3 |

Level 1 is what VI Server can do on ANY VI (see section 6); level 2 needs the composer or a
pylabview edit; level 3 is later. `ctl=` makes a strict typedef's appearance authoritative: a
`fp:Part` on such an object is refused, only `bounds` and label placement apply.

Images are FILES referenced relative to the AIXML document, never embedded: an embedded PNG
makes a diagram document unreadable for a model and unreviewable in a diff.

## 5. The filter for gRPC

One function - `FrontPanelSection.StripForNi(path) -> path` - and nothing else sends AIXML to
NI's service. It removes every element and attribute in the `fp` namespace and the namespace
declaration, and writes the result to a temporary file **with the same line numbers**: the
removed element is replaced by as many empty lines as it had. NI's `-2628` names a line and a
column; those must still point at the author's file.

The call sites that reach NI today and must all go through it: `Infra/BackendRoute.cs`
(validate, convert), `Tools/ActorFrameworkTools.cs` (validate, convert), `Cli/Validate.cs` and
`Cli/Corpus.cs`. A unit test asserts that `ValidateAIXMLAsync` / `ConvertAIXMLToVIAsync` appear
nowhere else - a filter with a second, unfiltered route is no filter.

The answer of every gRPC call whose document HAD a section says so: `frontPanel: "stripped"`,
with the uids it described. A section that is dropped without a word is the defect this
repository keeps recording.

## 6. What each backend does with it

| | pylabview | gRPC |
|---|---|---|
| level 1 | composed into the heap | stripped, then APPLIED afterwards through VI Server (`Position`, `Label.Visible`, `Caption`, colours, fonts, `{LV.Control} Replace` with `ctl=`) |
| level 2 | composed (image swap into existing parts, font table, action bits, decal into 88) | stripped; answer lists what was not applied |
| `lvai_generate_vi` Clean Up Panel | SKIPPED for a document with a section - it would undo the layout | skipped too |

The gRPC column's level 1 is optional - "pylabview only" is the premise and a valid first step.
It is listed because it is cheap: every property in it was used in the probes, and it would make
the reuse case (`ctl=`) work on both backends.

## 7. Reading it back

A section nobody can produce is half a format. `lvai_convert_vi_to_aixml` gains
`includeFrontPanel` (default false); with it the pylabview READER writes the section from the
panel heap - on either backend, because the reader needs no LabVIEW. Two things follow:

- The export cache keys on it (`AixmlExportStore` already separates backends; this is one more
  key component), or a cached plain export is served to a caller who asked for the panel.
- **An edit stops destroying the panel.** CLAUDE.md records that an AIXML edit is a full
  regeneration, so a VI whose panel must survive "cannot be edited at all". Export with the
  section, change the diagram, regenerate on pylabview - the panel comes back. That is the
  largest single gain of this proposal and the first thing to measure.

## 8. Checkers and tests

- `lvai_check_aixml` and `scripts/aixml_lint.py` learn the namespace: a section that is not the
  LAST child of `<VI>`, an `fp:Object uid` naming no `Control`/`Indicator`, a `fp:Part` on a
  `ctl=` object, an image file that does not exist. Both implementations, in step - the
  `SafeUidBase` lesson.
- Our own schema for the section (an `.xsd` under `docs/`), versioned by the namespace URI
  (`…:frontpanel:1`), so a later incompatible change is a new URI rather than a silent reread.
- Acceptance, each with a control arm: the probe's black menu button and icon button rebuilt
  from a section print byte-identical (they already do from the scripts); the same document
  over gRPC converts with `frontPanel: "stripped"`; the same document WITHOUT the strip is
  refused `-2628` (proves the strip is what makes it pass).

## 9. Order of work

1. The filter and its single route (section 5) - harmless, and it makes every later step safe.
2. `ctl=` and level 1 on pylabview, plus the Clean Up Panel skip.
3. Reader: `includeFrontPanel`, and the edit round trip of section 7, measured on a real panel.
4. Level 2 from the probe scripts (`experiments/picture-controls/`).
5. Level 1 on gRPC through VI Server (optional).
