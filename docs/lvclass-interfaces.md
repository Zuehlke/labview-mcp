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

## 3. What is NOT scriptable: interface METHODS

An interface method needs a **dynamic dispatch input whose type is the interface**. That is a
class-typed connector pane, and every route to one is closed:

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
