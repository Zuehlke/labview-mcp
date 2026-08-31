# LabVIEW interfaces

What an interface is, what can be scripted about one and what cannot, and the traps that were
measured rather than assumed. Companion to `docs/lvclass-creation.md`, which covers classes; almost
everything there applies here, because **an interface is a `.lvclass`**.

Measurements dated 2026-08-31 unless said otherwise, on LabVIEW 2026 (32-bit).

## 1. An interface is a class without private data

NI's own manual is the definition: an interface "can be thought of as a class without a private data
control, but that small difference enables an interface to serve entirely different purposes in
software architectures than classes. Specifically, interfaces enable a form of multiple
inheritance."

The consequences that matter for tooling:

| | class | interface |
|---|---|---|
| file extension | `.lvclass` | `.lvclass` — there is **no** `.lvinterface` |
| `NI.LVClass.IsInterface` | absent | `true` |
| `Type="Class Private Data"` item | present, even when empty | **absent** |
| `.lvproj` item type | `Type="LVClass"` | `Type="LVClass"` — identical |
| may inherit from | one class, any number of interfaces | interfaces only |
| glyph in the IDE | solid cube | faces of a cube |

**The flag is what to trust, not the absence of the item.** A class with no fields still carries its
`Class Private Data` item, so "no private data item" alone would misread one. `LvClass.Read` reports
both and `lvai_describe_class` now surfaces `isInterface`.

**Nothing reported the flag until this was written.** Measured on a generated interface:
`lvai_describe_project` lists it as `Type="LVClass"`, exactly like a class, and
`lvai_describe_class` had no field for it — so a real interface read back as an ordinary empty class
through every tool while the flag sat in the file. That is the whole reason `ClassInfo.IsInterface`
exists.

### Naming: avoid the leading `I`

From NI's manual, quoted because it cuts against the habit every text language installs:

> Avoid using a leading capital letter "I". Although most text programming languages commonly name
> interfaces with a leading capital letter "I" to differentiate interfaces from classes, LabVIEW
> distinguishes interfaces and classes using glyphs. Additionally, most parts of the LabVIEW
> development environment intentionally treat classes and interfaces identically. Callers of a
> method generally do not care whether the underlying type is an interface or a class. Therefore,
> avoiding the "I" enables you to convert a class to an interface or vice versa without refactoring
> caller code.

NI's two recommended shapes: a **capability** (`Can Measure Voltage.lvclass`) or a **category**
(`Lever.lvclass`). `lvai_create_interface` says so in its description and then uses the name given —
it is advice, and a caller who wants `IHaustier` gets `IHaustier`.

### NI's own examples are the reference implementation

Both ship with LabVIEW and neither needs a licence beyond base LabVIEW:

- `examples\Object-Oriented Programming\Basic Interfaces\Basic Interfaces.lvproj`
- `examples\Object-Oriented Programming\Actors and Interfaces\Actors and Interfaces.lvproj`

`Basic Interfaces` is the compact one. `Lever.lvclass`, `Poundable.lvclass` and `Pryable.lvclass` are
interfaces (`IsInterface=true`); `Tool.lvclass` is an ordinary class, which makes it a control case.
`Lever` declares two methods and seven classes override `Multiply Force.vi`, so the override shape is
there to read as well.

## 2. What IS scriptable

### 2.1 Creating an interface — `lvai_create_interface`

NI's provider `Add Interface.lvlib:Add Interface to Project (path).vi` is an **exact mirror** of the
class one. Measured with `lvai_vi_terminals` against both, in the sibling directories
`Providers\AddInterface\Support\` and `Providers\AddClass\Support\` (same four VIs each):

```
Interface Path         [path]                            conIdx 9,  required
New Interface Owner    [ref{LV.ProjectItem}]             conIdx 10, required
error in               [cluster]                         conIdx 8,  recommended
Application            [ref{LV.Application}]             conIdx 7,  required
Parent Interfaces      [array{ref{LV.LVClassLibrary}}]   conIdx 11, required
--> error out, Interface [ref{LV.LVClassLibrary}]
```

Two differences from the class provider, both load-bearing: there is **no `Parent Class` terminal at
all**, and the returned refnum is called `Interface`. Everything else — the throwaway active
project, the refnum that must be closed, `Error 1055` without a project — carries over from
`docs/lvclass-creation.md` unchanged.

Verified end to end by driving the helper directly, with the project active:

| run | inputs | `error out` | file |
|---|---|---|---|
| root interface | `Lever.lvclass` | 0 | `IsInterface=true`, 0 private data items |
| inheriting | `Pryable.lvclass`, parent `Lever.lvclass` | 0 | `IsInterface=true`, `<Item Name="Lever.lvclass" Type="Parent">` |

### 2.2 A class implementing interfaces — `lvai_create_class`'s `parentInterfaces`

`Add Class to Project (path).vi` has always had a `Parent Interfaces` terminal. `lvai_create_class`
wired it to a **hardcoded empty array** until 2026-08-31, so the capability was unreachable through
the tool even though LabVIEW had always accepted it.

Verified on a class with real private data and **two** interfaces:

```
class path              C:\temp\iftest\Prybar.lvclass
carrier vi path         C:\temp\iftest\carrier.vi          (2 controls)
parent interface paths  ...\Lever.lvclass|...\Pryable.lvclass
parent interface count  2
--> error out 0, fields added 2, parent interfaces opened 2
```

and in the file:

```xml
<Item Name="Lever.lvclass"   Type="Parent" URL="../Lever.lvclass"/>
<Item Name="Pryable.lvclass" Type="Parent" URL="../Pryable.lvclass"/>
```

**THE LINK IS ONLY SETTABLE AT CREATION TIME.** NI's provider takes it as an input and there is no
scriptable way to add one afterwards — `CLSUIP_ChangeInterfaceInheritance.vi` is a **modal dialog**
(its pane carries `Cancelled` and `Selected Interface Items`), and a modal dialog stops the whole
gRPC service until a human dismisses it. So a class that should implement an interface has to be
*created* with it. A run that got this wrong deleted and rebuilt the class before generating
accessors, which is the correct remedy and worth knowing in advance.

**An interface link and a parent class link are the SAME kind of item.** Both arrive as
`<Item Type="Parent">` inside `Parent Libraries`, so `ClassInfo.Ancestors` mixes them and its
**order** decides what `inheritsFrom` reports — a class with one parent and one interface can read as
inheriting from the interface. Nothing in the file distinguishes them; the only way to tell is to
open each name and read its own `IsInterface`. Both verify checks in `lvai_create_class` are
therefore membership tests, not "is it first"; the parent check used to be the latter and would have
started failing the moment `parentInterfaces` was used alongside `parentClassPath`.

### 2.3 Verifying — from the file

`IsInterface` true, no private data item, and every requested parent present in `Parent Libraries`.
Not through `lvai_describe_project`: it cannot tell an interface from a class, and it answers
`errorCode 0` for a class whose private data does not compile.

## 3. Interface METHODS: scriptable, but by no route NI offers

This section was headed "What is NOT scriptable" until 2026-08-31, and that was true of every route
NI exposes and false of the composition below, which is now execution-verified over two cold
rebuilds. There is still no single tool for it - see the banner.

An interface method needs a **dynamic dispatch input whose type is the interface**. That is a
class-typed connector pane, and every route NI offers to one is closed:

| route | result |
|---|---|
| AIXML authoring the terminal | `Error 53 … Unrecognized or unsupported attribute set in Control with UID 10` — AIXML refuses a class-typed terminal |
| the accessor wizard (`CLSUIP_CreateNewAccessor.vi`) | works off **private data**, which an interface cannot have |
| `CLSUIP_MemberTemplate.vit` | instantiates, and gives two `udClassDDO` terminals typed on **`LabVIEW Object`**. `AddItemFromMemory` does not retype them |
| `CLSUIP_ReplaceLVClassControls.vi` — NI's retyper | **private scope** (`Scope=2`) in `MemberVICreation.lvlib`'s `Utility` folder. `AddVIToClass.vi` likewise |
| `CLSUIP_CreateOverride.vi` | needs an ancestor method to exist already, so it cannot bootstrap the first one |
| `CLSUIP_ChangeInterfaceInheritance.vi` | a modal dialog |

Public in that library: only `CLSUIP_CreateNewAccessor.vi`, `CLSUIP_CreateOverride.vi` and
`CLSUIP_NewAccessorDialog.vi`.

**There IS a working route, and it is not yet a tool.** Measured 2026-08-31 on `IHaustier`:
author the VI in AIXML with **`path` stand-ins** for the class terminals, then
`{LV.Control}` `Replace` with the `.lvclass` path (signature `Style; Path; PaletteString`), then
`{LV.LVClassLibrary}` `AddItemFromMemory` to make it a member, then `SetWireRule(TermIdx, 4)` for
dynamic dispatch. Four traps came with it:

- **`Controls[]` returns the ERROR CLUSTERS FIRST**, not the class terminals. Deriving the order
  from the front-panel heap gave exactly the inverted answer, and the first `Replace` turned the
  error clusters into class terminals. Find terminals **by name** (`{LV.Control}` `Terminal` →
  `{LV.Terminal}` `Name`), never by index.
- **Generate with the project CLOSED.** A VI generated while the project is open is adopted as a
  loose project item, and `AddItemFromMemory` then answers **Error 56002**, "An item with this path
  already exists in the project".
- **An override's wire rules must match the parent's.** AIXML terminals come out as rule **1**
  (optional); NI's wizard makes rule **2** (recommended). A mismatch is `Error 1003`, not
  executable. `SetWireRule(…, 2)` on the pane fixed it.
- `HasThrall` is **not** the dynamic dispatch marker — `Read Name.vi` has `HasThrall="0"`. The
  marker is flag bit `0x8000`; static dispatch reads `0x1000000` in `NI.ClassItem.Flags`.

> ## ✅ §3 WORKS — AND ONLY IN THE SAVE ORDER BELOW. A RESOLVED DEFECT, KEPT BECAUSE IT COST TWO RUNS
>
> **Current status: the route in §3 is execution-verified.** Two independent cold rebuilds in the
> corrected save order came out executable, the second with a green **10-test Caraya suite** over the
> result (4 accessor round trips, 4 defaults, both overrides). Follow §3 as written.
>
> What follows is the defect that made it fail, kept in full because the failure is invisible and
> would be re-derived otherwise. In the WRONG order the route writes VIs that **load, export to AIXML
> with the correct diagram, and read back correctly through every tool in this repository — and then
> do not compile.** Everything §3 originally offered as verification was file-level. That is not
> enough, and this is the second time in this repository that reading a file back has passed while
> LabVIEW's compiler disagreed (the first was the class private data control, §2a of
> `docs/lvclass-creation.md`).
>
> What was measured on the cold rebuild, all of it on `C:\temp\hund`:
>
> - An isolated probe — one `Hund` class constant, one `Read Name.vi` call, one string indicator,
>   no Caraya — returns **`Error 1003, VI is not executable`**. So the whole class is broken, not the
>   test harness: this rules out Caraya, the test structure and the socket/`Replace` route as causes.
> - `{LV.SubVI}` `Replace` **refuses all four scripted interface-method VIs with `Error 1154`** while
>   accepting all eight wizard-generated accessors on the same diagram, in the same pane position,
>   with the same wire types. Ruled out by control: the socket and diagram (a swap to `Read Name.vi`
>   succeeded on that exact node), wire re-typing (staged so the wire was already `Hund`-typed —
>   still 1154), the file location, and a stale save state (forced re-save via `lvai_set_vi_icon`,
>   `viResaved: true` — still 1154).
> - `pylv_apply` inspect shows a structural difference, and **it is NOT the fault** — see the
>   correction below. A scripted override has **12 blocks and no parsed `LIvi`** (the owning-library
>   link) plus a malformed front-panel class link (`LIfp …
>   LinkObjUDClassDDOToUDClassAPILink b'FPPI' Offset List length 7208960 exceeds limit`); a wizard
>   accessor has **20 blocks including `LIvi` and `VICD`** and only the benign `LIfp` warning.
>
> **CORRECTION, measured 2026-08-31 on the fixed run: the missing `LIvi` is CORRELATION, not the
> defect.** This bullet originally presented it as the file-level signature of a broken member. It is
> not. The *working* override — one that runs, that `{LV.SubVI}` `Replace` accepts, and whose class
> passes a green Caraya suite — extracts to **the same 12 blocks, still with no parsed `LIvi`, and the
> same `LIfp … exceeds limit` warning**, checked before and after every save. So that signature
> separates a *scripted* member VI from a *wizard-generated* one, and says nothing about whether it is
> healthy.
>
> The practical consequence is the important part: **`pylv_extract` cannot detect this defect at all,
> in either direction.** Only execution can. What did differ between the broken and the fixed override
> was in the class file — `NI.ClassItem.Flags` 11 → 3 and `NI.ClassItem.InvokeUsage` 4 → 1 — and
> neither is claimed as the cause; one file pair is not a measurement.
>
> **A raw byte grep for `LIvi` does NOT discriminate either** — the string occurs in the broken files
> too. A grep was tried first and was misleading, and the parsed block list then misled in a subtler
> way.
>
> ### ROOT CAUSE, from LabVIEW itself — the link is ONE-SIDED
>
> Settled 2026-08-31 by opening the project in the IDE, which is the one interface that answers this.
> LabVIEW puts up a dialog naming the fault exactly:
>
> > `"Lautgebung.vi" is at the expected path but is not part of "IHaustier.lvclass". Do you want to`
> > `update the VI to be part of the library or remove the item from the library?`
>
> and the Error list reports, against `IHaustier.lvclass` itself: **`Owning library has blocked
> execution of the VI.`** — *"This VI's owning library has some problem. The library has blocked the
> VIs that it owns from executing until the problem is resolved."*
>
> So the member link exists on ONE side only. `AddItemFromMemory` writes the member entry into the
> **library**; the VI on disk carries no owning-library record, which is the missing parsed `LIvi`
> block. LabVIEW sees library and VI disagree, marks the library broken, and the library then blocks
> **every** VI it owns.
>
> That explains three things that looked unrelated:
>
> - why the **healthy wizard accessors** answered `Error 1003` too — the library blocks all its
>   members, not just the malformed ones, so the isolated `Read Name.vi` probe was never about
>   `Read Name.vi`;
> - why `{LV.SubVI}` `Replace` refused exactly those four VIs with `Error 1154`;
> - why every file-level check passed: each file is internally well-formed, and the defect is the
>   *disagreement between two files*. No single-file check can see it.
>
> **The fix is the ORDER, and §3.0's own rule had it backwards.** §3.0 says `Save.Instrument` must
> come before `AddItemFromMemory` "or a failure there discards the retyping". That protects against a
> small failure and causes a larger one: saving first writes the VI while it is not yet a member, so
> the owning-library link never reaches the file. The correct sequence is
>
> 1. `{LV.LVClassLibrary}` `AddItemFromMemory` — make it a member **first**, in memory
> 2. `{LV.VI}` `Save.Instrument` on the VI — now the owning-library link is written into the file
> 3. `{LV.LVClassLibrary}` `Save` — write the library's side
>
> Both sides must be saved.
>
> **MEASURED AND CONFIRMED, 2026-08-31.** A full cold rebuild in that order produced a class that
> compiles: four applications with every per-stage error indicator zero, and both probes that failed
> before now pass — a wizard accessor (`Read Name.vi`) runs with `error out = 0` where it answered
> `Error 1003`, and `{LV.SubVI}` `Replace` accepts both scripted overrides where it refused them with
> `Error 1154`. Caraya over the result: **6 tests, 0 failures, 0 errors** in two suites, against the
> previous run's single synthetic `VI not in an executable state` per suite. The route in this section
> may now be presented as working.
>
> **Repairing files already written this way needs no regeneration**: answer that dialog with
> **Update** once per affected VI, which is LabVIEW writing the missing side itself. Dismiss it
> promptly either way — a modal stops the whole gRPC service while it is open.
>
> **Historical note on the earlier run.** An earlier run on 2026-08-30 reported
> running `Hund\Lautgebung.vi` and reading `Sound = "Wuff"` back, and the archived copy of that
> override at `C:\temp\hund_pre_cold_20260831-101115\Hund\Get Name.vi` does carry a `VICD` block
> where every VI from the cold run carries none. If that route worked and this one does not, the
> candidate difference is §3.0's own reordering — putting the VI's `Save.Instrument` **before**
> `AddItemFromMemory`, so the VI is saved standalone before it is ever a member and the owning-library
> link is never written. **That is a hypothesis from one archived file, not a measurement.** Settle it
> by probing the archived class for `Error 1003` before changing the order on a guess.
>
> The cheapest way to get LabVIEW's own reason, which no interface in this repository reaches: open
> `C:\temp\hund\Hund.lvproj` in the IDE and click the broken run arrow on `Hund\Lautgebung.vi`. The
> Error list names the exact fault.
>
> **Resolved.** The save order above is the fix and it holds on a cold rebuild, twice.
> `lvai_create_interface` and `lvai_create_class`'s `parentInterfaces` were never affected — both were
> verified by execution and by file from the start, and an interface with no members is valid.
>
> The rule to carry away, which is bigger than this bug: **a link between two artefacts cannot be
> verified by reading either one.** Every file here was well-formed; the fault was that two disagreed.
> Verify a member link, a subVI link, a typedef binding or a project entry by RUNNING something.

### 3.0 The scripted route, measured end to end on 2026-08-31 (cold rebuild)

A second cold run of the whole route settled four things §3 left open or got wrong. All four came
from driving it for real; none is visible from validation.

**`AddItemFromMemory` takes a STRING, and the string is the VI's BARE NAME IN MEMORY.** §3 above
implies a refnum. It is not one: wiring a `Generic VI Reference` into it fails at *validation* with
`You have connected two terminals of different types … The type of the sink is string`, and wiring
the VI's full PATH as a string fails at *run time* with **Error 1004**. `"Get Name.vi"` — the name
LabVIEW knows the VI by, once `Open VI Reference` has loaded it — returns `error out = 0` and the
member appears in the `.lvclass`. Four for four across two classes.

**Error 1004 is unattributable without per-stage indicators, and the fix is cheap.** The helper's
merged error chain reported only `Invoke Node in ifm_apply.vi` for a diagram with fifteen invoke
nodes. Fanning each stage's `error out` net to its own front-panel indicator — AIXML expresses that
by repeating the net string — named the culprit in one run. Do this before guessing.

**Order the VI's own `Save.Instrument` BEFORE `AddItemFromMemory`, not after.** With the save
downstream, the first failure of the membership step discards the `Replace` retyping and all five
`SetWireRule` calls, because they only ever existed in memory. Measured: one wasted iteration.

**`Save` and `AddItem` exist on `{LV.LVClassLibrary}`; `Save.Instrument`, `SaveLibrary` and
`Save Library` do not.** The class is absent from the VI Server catalogue, so the only way to settle
a name is `lvai_validate_aixml`, which answers `Invoke Node: Invalid method` for one that does not
exist and something else for one that does. Six candidates cost one call: put one node per candidate
in a file and count the `Invalid method` lines against the node order. **The reference input must be
WIRED for that to discriminate** — with it unwired every node answers `Contains unwired or bad
terminal` and a bogus name looks exactly like a real one.

### 3.0.1 `Controls[]` is NOT error-clusters-first for an AIXML-generated VI

§3's first trap says `Controls[]` returns the error clusters first. Measured on a VI generated from
AIXML, it returns them in **front-panel creation order, which is the AIXML declaration order**:

```
0 IHaustier in   1 error in (no error)   2 IHaustier out   3 Name   4 error out
```

The trap is real for `CLSUIP_MemberTemplate.vit`, whose panel LabVIEW built. It does not generalise
to a panel you authored. Either way the rule stands — **find the terminal by name** — and the cheap
way to do that is a nine-element helper: `Open VI Reference` → `{LV.VI}` `Front Panel` →
`{LV.Panel}` `Controls[]` → a For Loop with an indexed `In` tunnel, `{LV.Control}` `Terminal` →
`{LV.Terminal}` `Name`, and an indexed `Out` tunnel. One run prints the whole order.

### 3.0.2 A CLUSTER stand-in survives `Replace`, so an override CAN read a field

This is the part §3 records as impossible, and it is not. §3 prescribes `path` stand-ins for the
class terminals, which is correct for a declaration whose body is a pass-through — but a `path`
cannot be unbundled, so an override that must read private data looks unreachable.

Author the class terminals as a **cluster matching the private data exactly** instead, put an
`Unbundle By Name` on the diagram, and `Replace` as usual:

```xml
<Control _name="IHaustier in" conIdx="11" type="cluster{string.Name,string.Rasse,int32.Alter,double.Gewicht}" .../>
<Node _name="Unbundle By Name" fields="Name" inputs="input cluster:10.value" outputs="Name:20.Name" .../>
```

`{LV.Control}` `Replace` **re-types the wire and the unbundle rebinds to the class's private data** —
which is legal precisely because the VI is a class member. Verified by reading the result back
through `lvai_describe_vi`, whose export shows `type="ref{UDClassInst}"`, `connection="dynamic"` and
the `Unbundle By Name fields="Name"` still feeding the `Name` indicator, `errorCode 0`.

So a scripted override is not limited to returning constants.

### 3.0.3 Wire rules, and what the flags actually came out as

`SetWireRule(11, 4)` and `SetWireRule(3, 4)` on the two class terminals produce
`connection="dynamic"` in the export — that is the readable confirmation, and it is worth preferring
over the flag word. `SetWireRule(8|2|0, 2)` puts the error and value terminals on NI's `recommended`.
Ten of five SetWireRule calls across four VIs: `error out = 0` every time.

`NI.ClassItem.Flags` came out **inconsistent and unexplained**: `0` on nine of the twelve members,
`8` on `IHaustier:Lautgebung.vi` and `11` on `Hund:Get Name.vi`. None carries the static bit
`0x1000000`, so all twelve are dynamic, and the export's `connection="dynamic"` agrees. What the low
bits mean is **not established** — do not read them as a dispatch setting, and do not repeat this
paragraph as if it were one.

### 3.0.4 `lvai_describe_project` CAN now tell an interface from a class

§2.3 says it cannot. Measured 2026-08-31, it reports `"interface": true` on the interface and
`"interfaces": ["IHaustier.lvclass"]` on the implementing class. The tool was improved after §2.3
was written; the sentence in §2.3 is stale. It is still not evidence that anything COMPILES.

Until that is productised, the IDE steps are: right-click the interface → **New » VI from Dynamic
Dispatch Template**, add the outputs, put them on the connector pane, save beside the interface;
then right-click the implementing class → **New » VI for Override…**.

### 3.1 A class must override EVERY interface method

Measured with three controls, and the third one overturned the expected answer:

| control | setup | result |
|---|---|---|
| 1 | untouched copy | runs |
| 2 | override removed, `Flags = 1073741824` set on the interface method | whole class **Error 1003** |
| 3 | override removed, **both flags 0** before the first open | whole class **Error 1003** |

Control 3 is the informative one: the requirement holds **with or without** the flag. So
`1073741824` (`0x40000000`) is *not* demonstrated to be "Require Descendant Classes to Override This
VI" — on an interface it is behaviour-neutral and this test cannot measure it. What is established:
it is not the dispatch flag, it sits on all three interface declarations in NI's `Basic Interfaces`
corpus, and it is absent from the leaf overrides in `Flathead`, `Bottle Opener` and `Prybar`. The
string "Require Descendant Classes to Override This VI" is not greppable in LabVIEW's resources.
**Isolating it needs an ordinary class as the parent**, where a missing override is legal — that is
the next experiment, not a conclusion.

Also unexplained: LabVIEW writes `NI.ClassItem.Flags = 33554432` (`0x2000000`) onto generated
overrides by itself. NI's own overrides in `Basic Interfaces` carry `0`. No observed consequence.

### 3.2 Overrides need their own subfolder

An override has the **same file name** as the method it overrides, so the two cannot share a
directory. That is why NI keeps one folder per class in `Basic Interfaces` — `Lever\Multiply
Force.vi` beside `Flathead\Multiply Force.vi` — and why a generated override goes in `<Class>\`.

## 4. Two traps in the helper diagrams

Both cost real time on 2026-08-31 and neither announces itself.

**`maxin` wires a For Loop's `N`. `count` does not** — `count` names the loop's own `i` output net. A
net wired into `count` answers with two errors that point somewhere else entirely:

```
Wire: Wire connected to an undirected tunnel.        (twice)
You have connected two terminals of different types.
    The type of the source is 1D array of long ...   The type of the sink is long ...
```

`docs/aixml-reference.md`'s For Loop section records exactly that pair and warns that anyone chasing
them is looking in the wrong place. Both helpers validated with `errorCode 0` the moment the net
moved to `maxin`, with nothing else changed.

**The separator is a PIPE, not a newline.** `lvai_run_vi_and_read_values` — the transport that sets
a helper's controls — **refuses any value containing a line break**, by name:

```json
{"ok": false, "errorKind": "inputContainsNewline",
 "error": "The control name or value for 'parent interface paths' contains a line break. ..."}
```

because it pairs control names with values *by line*. So a newline-separated list cannot reach the
VI at all. This is only visible by driving the helper for real: the AIXML validates either way and
the C# compiles either way. A pipe is in `Path.GetInvalidFileNameChars()` on Windows, so no real path
contains one — and `ParseInterfaceList` refuses one that does, so a future non-Windows host fails
with a sentence rather than two unopenable paths. The MCP-facing grammar stays **one path per line**;
the join to pipes happens in C#.

**Why a count control at all, rather than `Array Size` of the split list:** with `N` wired, a count
of 0 runs the loop zero times and the indexing output tunnel yields an **empty array**, which is what
`Parent Interfaces` needs for a class implementing nothing. Deriving `N` from the split array would
depend on what `Spreadsheet String To Array` returns for an empty string; if that is one empty
element, the loop runs once, `String To Path` yields an empty path, `LVClass.Open` fails, and the
array carries one **invalid** refnum. A conditional output tunnel would also solve it, but
`docs/aixml-reference.md` records those as never having been needed and therefore never measured, and
an unmeasured construct does not belong in a shipped helper.

**`Spreadsheet String To Array` needs `format string` wired**, even though it is empty — unwired it
reports `Contains unwired or bad terminal`.

## 5. The helper cache was not invalidated by an edit

Unrelated to interfaces and found because of them, so recorded here as well as in the code: both
helper-cache sites keyed on **existence alone** (`!File.Exists(helperVi)`), so editing a helper under
`scripts\` left LabVIEW running the *previous* build's VI — silently, with nothing in any answer to
say so. `HelperNeedsRebuild` now compares timestamps. The check is deliberately narrow: `CLAUDE.md`
warns against deleting that cache to force a rebuild, because validating a helper is what killed
LabVIEW three times in one afternoon, and an AIXML that has genuinely changed is the one sanctioned
case for paying that risk.

## 6. What is verified and what is not

Verified by execution: interface creation (root and inheriting), a class implementing two interfaces,
every file check above, and `error out = 0` on each run.

**Not** verified: that a two-interface class *compiles* under load. The files are right and NI's
provider reported no error, but `lvai_describe_project` answers `errorCode 0` for classes whose
private data does not compile, so it is not evidence either way. With no methods on the interfaces
there is nothing to override and therefore no known reason for it to fail — that is an argument, not
a measurement.
