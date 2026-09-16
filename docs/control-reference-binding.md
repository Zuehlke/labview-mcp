# A bound control reference: what it is, and what it would take to create one

Measured 2026-09-16, after a user asked for a subVI that takes an **array of control references**
rather than a whole front panel filtered by label. That is the right design; it is not reachable
from AIXML today. This page records everything established about the artefact, so the next attempt
starts from the mechanism instead of from the question.

## 1. AIXML CANNOT AUTHOR ONE — two refusals, both explicit

| probe | answer |
|---|---|
| `<Constant _name="…" link="User Input" type="ref{LV.String}" …/>` | `-2628`, **`attribute 'link' is not declared for element 'Constant'`** |
| implicit `Property Node`, `link="User Input"`, reading `reference out` | `Error 1`, **`Object terminal not found for output: reference out`** |

The second is the more informative: an implicitly linked property node has **no reference terminals
at all** — the link stands *instead of* them, which is what makes it implicit. The first settles the
question, because the AIXML schema is closed: `link` is declared for `Node` and for nothing else.
The export cache agrees from the other side — `link=` occurs on **273 `<Node>` elements and on no
`<Constant>`** across 1 402 exports.

**A `<Constant type="ref{LV.Numeric}" value=""/>` is NOT a control reference.** It is a refnum
**type specifier**, and that is what NI's scripting examples wire into `New VI Object`'s
`vi object class`. Reading one of those as "a control reference NI authored" is the wrong turn this
section exists to prevent.

## 2. WHAT IT ACTUALLY IS: `ctlRefConst` + `ctlRefDCO`, bound by `<ddo uid="N"/>`

pylabview already knows the classes — `SL__ctlRefConst = 182`, `SL__ctlRefDCO = 183` in `LVheap.py`,
distinct from the `SL__bDConstDCO = 19` / `SL__stdRefNum = 85` pair that an unbound refnum constant
produces. Taken out of NI's own
`examples\Application Control\VI Server\Control References\Control References.vi`:

```xml
<SL__arrayElement class="ctlRefConst" uid="233">
  <termList elements="1">
    <SL__arrayElement class="term" uid="196">
      <dco class="ctlRefDCO" uid="234">
        <typeDesc>TypeID(83)</typeDesc>      <!-- the CONTROL's type, not a refnum type -->
        …
      </dco></SL__arrayElement></termList>
  <label …><text>"Boolean"</text></label>    <!-- the control's label -->
  <ddo uid="513" />                          <!-- THE BINDING: the FP control's DDO uid -->
  <oMId>0006</oMId>
</SL__arrayElement>
```

**The binding is one field**, and it resolves into the front-panel heap. Verified three for three in
that one VI:

| `ctlRefConst` uid | → FP `ddo` uid | FP heap class | label |
|---|---|---|---|
| 233 | 513 | `stdBool` | Boolean |
| 231 | 921 | `stdString` | String |
| 280 | 5749 | `stdGraph` | Waveform Chart |

**`typeDesc` is the control's OWN type** — `TypeID(83)` is `<TypeDesc Type="Boolean" Format="inline"/>`,
not a refnum descriptor. That is what makes this more than a field edit: an unbound refnum constant
generated from AIXML carries a refnum type (`<TypeDesc Flags="-1"/>` in the probe), so turning one
into the other means composing an object of a **different class with a different child structure and
a different type**, not renaming a tag.

## 3. SO DO NOT PATCH THE HEAP FOR IT

pylabview is documented as unable to compose objects from nothing ("adds no nodes and no wires"), and
`docs/connector-pane-repair.md` records the cost of ignoring that: moving terminals through the heap
**killed LabVIEW twice**, on files that re-extracted cleanly and read back exactly as intended, and
the capability was removed rather than shipped. Building a `ctlRefConst` by hand is a larger
structural change than the one that crashed.

## 4. THE SAFE ROUTE IS NI'S OWN SCRIPTING PRIMITIVE, AND IT IS AUTHORABLE

`New VI Object` is a fully exported node — **19 occurrences** in the cache, clean terminal names, and
absent from `docs/aixml-node-gaps.tsv`:

```xml
<Node _name="New VI Object"
      inputs="owner refnum:…,style:…,position/next to:…,error in (no error):…,
              vi object class:…,auto wire? (F):,path:,bounds:…"
      outputs="object refnum:…,error out:…" …/>
```

So the tool would be a generated helper driving NI's own machinery — the pattern
`lvai_create_accessors` and `lvai_set_vi_icon` already use — rather than heap surgery:
open the target VI in the IDE's application instance, `Front Panel` → `Controls[]`, match the label,
take the block diagram as `owner refnum`, wire the control's reference into `style`, and save.

## 5. THE ONE THING STILL MISSING, AND WHY IT WAS NOT GUESSED

`vi object class` takes a **refnum type-specifier constant** — `ref{LV.Numeric}`, `ref{LV.Cluster}`,
`ref{LV.WhileLoop}` and so on are what NI wires there. The class for a *Control Reference Constant*
is **not in this station's VI Server catalogue** (153 classes; LabVIEW's scripting hierarchy is far
larger), and no cached export creates one.

Probing candidate spellings through `ValidateAIXML` is the documented way to settle a target name —
it is how `{LV.Project}`'s `Save` and `Close` were found. **It is also the exact activity
`docs/labview-crash-signatures.md` ties to three LabVIEW deaths**: every `OMAutoClasses` crash was
`ValidateAIXML` parsing a file that named a VI Server class the catalogue does not list. With this
station already at `dwarnCount 80` and `looksDegraded: true`, that is a decision to take
deliberately rather than in passing.

**The cheap alternative, no guessing:** find an NI VI that scripts a control reference constant and
read the class off its export — the same "export a VI that already calls it" rule that settles every
other target spelling here. `examples\Application Control\VI Scripting\Creating Objects\` is where
to look first.

## 6. Until then

`Get Controls By Label.vi` + `Set Controls Disabled.vi` (see `docs/cold-build-atm-cld.md` §11a) are
the working shape: the caller reads its own panel with `Front Panel` → `Controls[]` (both property
nodes with `reference` left **unwired**, which means *this VI*), and an array of **names** stands in
for the array of references. The user approved that as a workaround. It costs a label lookup at run
time and it keeps the worker VI generic, which was the point.
