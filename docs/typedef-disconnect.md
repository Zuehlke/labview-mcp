# Disconnecting typedefs in a placeholder stub — measured 2026-09-18

The goal was a placeholder stub (`lvai_placeholder_subvi`) whose terminal types are **exact** copies
of the subject's, with every typedef link removed at every depth — the user's rule of 2026-09-17:

> Wenn wir einen Typedef haben machen wir ein Disconnect des Types und kopieren diesen Type in das
> user.lib/LV_MCP directory. Wir müssen aber bei Clusters, Klassentypes usw. auch die eingebetteten
> Types disconnecten. Es darf in keinem der Types noch ein Typedef vorhanden sein.

Three things came out of measuring it. The second one rules out the obvious mechanism; the third is
the route that works, and it is the user's own second proposal rather than the one this document
started from.

## 1. The stub is ALREADY typedef-free — the disconnect step has nothing to disconnect

Measured on a purpose-built fixture with a typedef **inside** a typedef, with a control arm.

The fixture (`C:\temp\TypedefStub\`, built without the IDE):

| file | what it is | how it was made |
|---|---|---|
| `Inner Mode.ctl` | strict typedef, `uint16` enum `{Idle,Running,Stopped}` | AIXML → pylabview flag patch |
| `Outer Config.ctl` | strict typedef, `cluster{Mode,Gain}`, **`Mode` bound to `Inner Mode.ctl`** | as above, then `{LV.Control} Replace` |
| `Typedef Subject.vi` | pane control `Config` bound to `Outer Config.ctl` | `lvai_generate_vi`, then `Replace` |
| `Variants Subject.vi` | four terminals: a strict, a plain, a non-strict and an ARRAY of a typedef | as above; the array element is reached with `{LV.Array} Array Element` |
| `Deep2c Test.ctl`, `Deep3c Outer.ctl`, `Deep Subject.vi` | the THREE-level chain of §12e | as above, built inside out in a FRESH LabVIEW |
| `Stale Subject.vi` | a typedef-free VI whose FILE is swapped for one with three typedefs, §12f | `lvai_open_file`, then overwrite the file behind LabVIEW |

Flag patching is `scripts`-less on purpose — it is three regex substitutions on the extracted main
XML, and **`TypeDefVI="0"` is a SUBSTRING of `StrictTypeDefVI="0"`**, so an unanchored pattern
patches both and then reports a count that does not mean what it says. Anchor it with
`(?<!Strict)`. The same overlap is why `ClearTypedefFlagAsync` clears the two independently.

**AIXML cannot create a `.ctl` at all.** `lvai_convert_aixml_to_vi` writing to a `.ctl` path produces
a **Standard VI** with that extension: `lvai_describe_ctl` answers `isControl: false`,
`instrumentType: "Standard"`, `controlVIType: 0`. The two flags that make it a control live in the
save record and pylabview exposes both — `<Instrument Type="Standard">` → `"Control"` and
`TypeDefVI="0" StrictTypeDefVI="0"` → `"1"`. After `pylv_rebuild` the same file reads
`controlVIType: 2`, `bindable: true`. That is the cheapest way to get a real typedef fixture on a
station, and it needs no IDE gesture.

The binding then goes in through **`{LV.Control}` `Replace` with a `Path`** — the connect gesture,
which is not named after typedefs. The descent to a cluster element needs
`To More Specific Class` onto `ref{LV.Cluster}`, because `Controls[]` is declared on `{LV.Cluster}`
and **not** on `{LV.Control}`.

Then the measurement, from the saved files rather than from any session:

| file | `Type="TypeDef"` in `VCTP` | `class="typeDef"` in FP heap | `.ctl` named |
|---|---|---|---|
| `Typedef Subject.vi` (subject) | **2** | **2** | `Outer Config.ctl`, `Inner Mode.ctl` |
| `LVMCP Stub 73ec3e69e2.vi` (its stub) | **0** | **0** | **none** |

So the stub carries no typedef at any depth **today**, and the invariant the rule asks for already
holds. Not because anything disconnects it — because AIXML has no typedef in its grammar, so the
identity is gone before the stub is written. `lvai_placeholder_subvi` reports this correctly as
`typedefTerminals: 1` with `typedefPath` naming `Outer Config.ctl`.

**What is lost is FIDELITY, not link-freedom.** The stub gets AIXML's *approximation* of the type.
For this fixture the approximation happens to be exact — a cluster of an enum and a double is
expressible. It is not exact where AIXML cannot express the type, and the measured case is a class
terminal, which `PlaceholderTools.CloneTerminals` writes as a `path` stand-in.

**One reporting gap is real and was measured in passing.** `lvbd_pane_typedefs.xml` reads
`Panel → Controls[]` only, so it sees the outer typedef and **not** the one nested inside it: the
subject's `Mode` never appears. A cluster that is not itself a typedef but contains one reports
`typedefTerminals: 0`.

## 2. `Discon Typedef` kills LabVIEW **outside the IDE's application instance** — five deaths

> **CORRECTED 2026-09-18, and the correction came from the user, not from here.** This section
> originally concluded that `Discon Typedef` cannot be used at all. That was wrong, and it was wrong
> in the way this repository keeps warning about: an impossibility written in the same voice as the
> measurements around it. The user's objection was simply *"if I do it in LabVIEW by hand it works
> beautifully"* — and §2a is the probe that settles it. Everything below is still what was measured;
> only the conclusion drawn from it was too wide. **The deaths are real, and the condition for them
> is named in §2a.**


`{LV.Control}` `Discon Typedef` is in the catalogue on 65 classes, takes no parameters, and is the
documented disconnect. Invoked from a helper VI that reached the target through `Open VI Reference`
with **no application instance**, it terminates LabVIEW 2026 Q3 32-bit every time.

| probe | scope | reads back after? | target | result |
|---|---|---|---|---|
| `lvtd_discon_top.vi` | all 4 pane controls | yes | copy | **died** |
| `lvtd_discon_one.vi` | 1 control, really a typedef | yes | copy | **died** |
| `lvtd_discon_safe.vi` | 1 control, really a typedef | **no** | copy | **died** |
| `lvtd_discon_safe.vi` | 1 control, really a typedef | **no** | **original** | **died** |
| `lvtd_rep_top.vi` (`Replace`) | 1 control | no | original | survived |
| `lvtd_rep_nested.vi` (`Replace`) | nested cluster element | no | `.ctl` | survived |

The control arm is what makes this a finding rather than a guess: the **same helper shape** —
`Open VI Reference` → `Front Panel` → `Controls[]` → label loop → mutate → `Save.Instrument` —
survives twice with `Replace` in the mutate slot and dies four times with `Discon Typedef` there.
It is the method, not the shape, not the target file, not the read-back.

**Three hypotheses were tested and refuted, in this order:**

- *Unguarded call on a control that is not a typedef.* Refuted: the second probe called it on exactly
  one control whose `Is Typedef?` was non-zero, and died the same way.
- *Use-after-free from reading the reference back*, the documented `Replace` trap. Refuted: the third
  probe reads nothing back and died the same way.
- *Name collision from a byte copy* — `Subject Copy.vi` carries the internal name
  `Typedef Subject.vi`, and `Error 1051` is the documented sibling of that shape. Refuted: the fifth
  run targeted the ORIGINAL file and died the same way.

**The death leaves no evidence in the place you would look.** NI's own crash log
(`%TEMP%\LabVIEW_32_26.3.1f1_interactive_jcm_cur.txt`) for the last four runs has **0** `VI call
stack` blocks, **0** `minidump` lines and **0** `DWarn` lines; its tail is an orderly unload of
VIPM/JKI VIs, which reads as a clean exit. The only record is the **Nigel service log**
(`%ProgramData%\National Instruments\AIAssistants\Logs`), which timestamps it to the second:

```
07:56:28   LabVIEW starts
07:56:55   WRN [Palette Search] Stopped monitoring due to exception.
           Status(StatusCode="Unavailable", … An existing connection was forcibly closed …)
```

The target file is **never written** — `Typedef Subject.vi` stayed byte-identical to its backup
through all five runs. So there is no partial damage to clean up, and no chance that the disconnect
"half worked".

The FIRST death is a different one and should not be counted with these: it happened inside
`ConvertAIXMLToVI` while generating the dense probe helpers, with a real stack naming
`LV AI Core.lvlibp:VI generator.vi` and a repeated
`DWarnInternal 0x8226D7C1: we should have intersected an obstacle!` from
`source\diagram\routalgHelpers.cpp(229)` — the wire router. That is the known AIXML-generation crash
region, not this one.

### 2a. TWO preconditions, not one — measured 2026-09-18

**This section said "the variable was the application instance" for about an hour, on the strength
of ONE successful call.** That was the same mistake it had just finished criticising: generalising
from a single measurement. The instance is necessary and it is **not sufficient**. The full
condition is below; the history is kept because the intermediate wrong answer is the instructive
part.

`{LV.Control} Discon Typedef` needs BOTH of these, and missing either kills LabVIEW silently:

1. the VI reached through the **IDE's application instance** —
   `{LV.Application}` `Project:Active Project` → `{LV.Project}` `Application` → `Open VI Reference`,
   which needs a project open and active;
2. the VI **open in the editor** — `lvai_open_file` on it first.

The A/B that separates them, all in one LabVIEW session unless noted:

| instance | VI open in the editor | where the VI sits | result |
|---|---|---|---|
| addon (bare `Open VI Reference`) | no | `C:\temp` | **dead** (5 runs, §2) |
| IDE | **yes** (the user had it open) | `C:\temp` | **code 0**, reached the file |
| IDE | no | `user.lib\LV_MCP` | **dead** |
| IDE | no | `C:\temp`, same folder as the working case | **dead** |
| IDE | **yes**, via `lvai_open_file` | `C:\temp`, same file as the row above | **code 0** |

The last two rows are the discriminator: same file, same folder, same session, differing only in
whether the VI had been opened in the editor first.

**NI's own tool has the same constraint.** The Quick Drop shortcut *Disconnect from Typedef*
(forums.ni.com, Quick Drop Enthusiasts) operates on controls **selected in a VI open in the editor**,
and it ships a SEPARATE recursive shortcut (`Ctrl+Shift+Y`) for nested controls — so NI had to
implement the recursion too. Both of this document's findings line up with it.

### 2b. What the route looks like end to end

Every probe in the table above reached its target with a bare `Open VI Reference`, which resolves in
the application instance the **helper itself** runs in: the AI addon's. The IDE gesture a person
performs by hand runs in the **IDE's**. That difference was listed under "what is NOT ruled out" and
then not tested, because the five deaths made it look settled.

One probe, identical to `lvtd_discon_safe.vi` except that the VI is opened through
`{LV.Application}` `Project:Active Project` → `{LV.Project}` `Application` → `Open VI Reference`:

| condition | result |
|---|---|
| no project active | `Error 1055` at the first property node — a clean refusal, LabVIEW alive |
| project active, IDE instance | `typedef before: 1`, **`discon code: 0`**, `error out` clean, **LabVIEW alive** |

And it reached the file. `Subject Copy.vi`, before and after, counted from the saved bundle:

| | before | after `Discon Typedef` on `Config` |
|---|---|---|
| `class="typeDef"` heap objects | 2 | **1** |
| `.ctl` named | `Outer Config.ctl`, `Inner Mode.ctl` | **`Inner Mode.ctl` only** |

With both preconditions met the whole route runs, measured on the nested fixture:

| step | call | time |
|---|---|---|
| install the REAL typedef on the stub terminal | `lvai_typedef_bind.vi`, source = the ORIGINAL `.ctl` | 93 ms |
| find what is now linked | `lvai_typedef_tree.vi` at `""` and at `"0"` | 60 + 39 ms |
| disconnect outer | `lvai_typedef_discon.vi` `""`, child 0 | 174 ms |
| disconnect nested | `lvai_typedef_discon.vi` `"0"`, child 0 | 242 ms |

and the saved file then holds **0** `class="typeDef"` objects, down from 2, with `Mode` a plain
`UnitUInt16` carrying all three `EnumLabel`s. **No `.ctl` is copied anywhere** — that is the whole
attraction over §3. A stale `Type="TypeDef"` pool entry remains, exactly as it does on the flag
route, and reaches nothing.

**`Discon Typedef` IS NOT RECURSIVE.** Disconnecting the outer control left the inner one linked, so
a chain costs one call per level — the same walk §3 needs, arrived at from the other direction, and
the same conclusion NI reached when they shipped a second Quick Drop shortcut for it.

**What this does not excuse.** The five deaths were real and the condition that produces them is
ordinary — a generated helper opening a VI the plain way. Anything reaching for `Discon Typedef`
must satisfy BOTH preconditions, and a caller who forgets either loses the whole LabVIEW session
with no entry in NI's crash log. That is a materially worse failure mode than the flag route has,
which is the trade to weigh rather than the speed alone.

### 2c. Every variant, measured — 2026-09-18

`Variants Subject.vi` carries each shape on one pane: a **strict** typedef cluster that itself
contains a typedef, a **plain cluster that is not a typedef but holds one**, a **non-strict**
typedef (`TypeDefVI="1" StrictTypeDefVI="0"`, made by patching only the strict bit), and an
**array**. The class case is `Read Config Probe.vi`, a copy of a `Gadget.lvclass` accessor.

Fixture before: **4** `class="typeDef"` heap objects over three `.ctl` files.

| variant | where | `Discon Typedef` |
|---|---|---|
| strict typedef, cluster | terminal `Strict` | **code 0** |
| typedef nested INSIDE that typedef | `Strict` → `Mode` | **code 0**, and needs its own call |
| typedef inside a cluster that is NOT one | `Plain` → `Sel` | **code 0** |
| non-strict typedef | terminal `NonStrict` | **code 0** |
| array | terminal `Arr` | not reached by the descent, reported |
| **class terminal** | `Gadget in` | **REFUSED, `Error 1088`** |

After the four disconnects the saved file holds **0** typeDef heap objects, names **no** `.ctl` at
all, and all four sets of `EnumLabel`s are intact — the types survived, only the links went.

**The class case is where this route BEATS the flag route.** LabVIEW refuses the call itself:
`Error 1088`, with the method named in the message as *Disconnect From Typedef*. The error travels
down the wire into `Save.Instrument`, so the file is **not written** — verified byte-for-byte, with
both `class="udClassDDO"` objects intact. The flag route needs `IsClassPrivateData` to guard that;
here LabVIEW guards it.

**And the walk has a better discriminator than a path.** A class terminal reports
`Class Name` = **`LabVIEWClassControl`** (against `Cluster` for an ordinary typedef'd cluster), which
is a property of the object rather than a convention about the `Typedef:Path` string — worth using
in both routes.

#### There is NOTHING to disconnect on a class terminal, and the file says so

Asked whether pylabview could do what LabVIEW refused, the answer is no — not because pylabview is
too weak, but because the premise is wrong. `Read Config Probe.vi`'s type pool holds two different
things:

```
FlatTypeID 6:  <TypeDesc Type="TypeDef" …>          <- the CLASS'S PRIVATE DATA
                 <TypeDesc Type="Cluster" Label="Cluster of class private data">
                 <Label Text="Gadget.lvclass" /> <Label Text="Gadget.ctl" />

the terminals: <TypeDesc Type="Refnum" RefType="UDClassInst" Label="Gadget in" / "Gadget out">
```

The TERMINAL is a `UDClassInst` **refnum** — a reference to a class instance — and carries **no
TypeDef wrapper at all**. The typedef sits on the class's private data, which is a different type
in the pool. So `Error 1088` is correct rather than a limitation: there is no link to remove.

What pylabview *could* do is rewrite the terminal's type descriptor from `Refnum UDClassInst` to
that cluster. That is a TYPE CHANGE, not a disconnect: the terminal stops being a class, a caller's
class wire no longer binds, and `{LV.SubVI} Replace` has nothing left to re-type. It is exactly the
damage `IsClassPrivateData` guards against on the flag route, and the existing `path` stand-in
already achieves the same thing more honestly, because it does not pretend to be a class type.

**One wording of this document's own needed correcting.** "A class terminal reports
`Is Typedef? = 1`" is the BOOLEAN the probe emits: `lvai_typedef_tree.vi` compares the enum against
*not a typedef* (0) and squashes everything else to true. The raw value for a class terminal is
**unmeasured** here — plausibly 3, *class private data*, but that is not established, and reading
the boolean as "it is typedef-linked" is exactly the wrong conclusion. The file settles it without
needing the enum at all.

**Stability.** All of the above ran in ONE LabVIEW instance with no restart: 4 binds, 6 tree reads,
5 disconnects, one refusal, several extracts and two editor opens, every one green. A second full
bind-and-disconnect cycle on the same file behaved identically, and left exactly the **1** typedef
the re-bind had brought back nested — non-recursion confirmed a second time rather than assumed.

## 3. THE OTHER ROUTE: clear the typedef FLAG on the `.ctl`, recursively

The user's proposal of 2026-09-18 — *"du musst bei den Typedef CTL den Type auf Control umstellen"* —
avoids `Discon Typedef` altogether, and it is measured working. A typedef is a `.ctl` with
`TypeDefVI` set in its save record; clearing that bit makes it an ordinary custom control, and
`lvai_describe_ctl` already states the consequence: **a Replace against a non-typedef `.ctl` installs
the type and produces NO typedef link.** That is the disconnect, achieved from the source side.

It is the exact inverse of the flag patch that BUILT the fixture in §1, and it needs no LabVIEW:

```
TypeDefVI="1" StrictTypeDefVI="1"  ->  TypeDefVI="0" StrictTypeDefVI="0"
```

Patch the internal `<Section Name="…">` too when the copy sits beside the original, or two files
claim the same VI name.

**THE RECURSION IS MANDATORY, and this is the A/B that shows it.** Same stub, same subject, the only
difference being how deep the flag was cleared:

| flattened | `Type="TypeDef"` left in the stub | `class="typeDef"` | `.ctl` named |
|---|---|---|---|
| the OUTER `.ctl` only | **1** | **1** | `Inner Mode.ctl` |
| outer **and** inner, rebound bottom-up | **0** | **0** | none |

So clearing the flag on `Outer Config.ctl` disconnects `Config` and lets the typedef embedded in its
definition travel straight through into the stub — precisely the case the rule is about. With both
levels cleared the stub's `Config` is a plain `Cluster` whose `Mode` is a plain `UnitUInt16` carrying
its three `EnumLabel`s: **the exact type, no link, at any depth.**

The working sequence, bottom-up, using only operations measured stable here:

1. copy each `.ctl` of the chain into `user.lib\LV_MCP\`, deepest first
2. clear `TypeDefVI`/`StrictTypeDefVI` on the copy and rename its section — **pylabview, no LabVIEW**
3. `{LV.Control} Replace` the element inside the parent copy with the flattened child
4. repeat outwards, then `Replace` the stub's terminal with the outermost flattened copy

Steps 2 and 3 alternate, so a chain of depth N costs N flag patches and N Replaces — but the
flattened copies are cacheable in `LV_MCP` exactly like the stubs, so a repeat build pays one
`Replace` per terminal.

**One cosmetic artefact, measured and harmless.** A flattened copy keeps STALE `Type="TypeDef"`
entries in its own `VCTP` type pool — `Flat Config.ctl` read 2 wrappers naming `Inner Mode.ctl` while
its front-panel heap already held **0** `class="typeDef"` objects. They are unreferenced pool
entries, and nothing of them reaches the stub. **Count the heap objects, not the pool wrappers,** when
judging a `.ctl`; for the STUB both counts agree and either will do.

## 4. What is NOT ruled out

**The IDE's application instance.** Every probe here opened the target with `Open VI Reference` and
no application instance. This repository already records two operations that behave differently
there: `{LV.SubVI} Replace` is a **silent no-op** outside the IDE's own instance, and
`{LV.Control} Replace` on a class private data control answers `Error 1073` by one route and says
nothing by the other. So the route
`{LV.Application}` → `Project:Active Project` → `Application` → `Open VI Reference` is a genuinely
different condition and is the one thing left to try. It needs a project open and active.

Until that is measured, **do not build on `Discon Typedef`**, and do not describe the disconnect
route as available.

## 4. The CLASS case — measured 2026-09-18

Fixture: `Gadget.lvclass` with fields `Config` and `Scale`, then `lvai_bind_class_fields` binding
`Outer Config.ctl` onto `Config` — so the class's private data carries the two-level typedef nest of
§1. `lvai_create_accessors` then made `Read Config.vi`.

**A class's private data CAN carry nested typedefs.** `lvai_describe_class` reads the saved file:

```
fields: [ { label: "Config", type: "TypeDef", isTypedef: true, typedef: "Outer Config.ctl" },
          { label: "Scale",  type: "NumFloat64", isTypedef: false } ]
```

and `Outer Config.ctl` contains `Inner Mode.ctl`. So the case the rule names is real.

**But it reaches the stub as an ORDINARY typedef terminal, not as part of the class type.** The
accessor's stub reports five terminals, and they split into three kinds:

| terminal | type | what the stub gets today |
|---|---|---|
| `Gadget in`, `Gadget out` | `ref{UDClassInst}` | `path` stand-in, `classTerminals: 2` |
| `Outer Config` | the typedef from private data | bare cluster, `typedef: true`, `typedefPath` named |
| `error in`/`error out` | error cluster | exact |

So the class's typedef is handled by §3 like any other — nothing class-specific is needed for it. A
class REFNUM terminal carries no private data and drags no typedef with it: the finished stub read
`class="typeDef"` **0**.

### A stub terminal CAN take the real class type — which contradicts what the tool assumes

`{LV.Control} Replace` with the **`.lvclass` path itself** on the stub's `path` stand-in, measured
with a control arm inside the one file, because only `Gadget in` was replaced:

| terminal | `TypeDesc` after | FP heap |
|---|---|---|
| `Gadget in` (replaced) | `Refnum RefType="UDClassInst"` | `udClassDDO: 1` |
| `Gadget out` (untouched) | `Path` | `stdPath: 1` |

`execState 1`, no linker errors, and the file names `Gadget.lvclass`. So
`PlaceholderTools.CloneTerminals`' `path` stand-in is a limit of **AIXML**, not of the stub: the
pane can be made an exact clone after generation.

**Not acted on in this pass, and the reasons are worth writing down.** The stand-in is documented as
sound *because* `lvai_swap_subvis` re-types the wires through `{LV.SubVI} Replace`; making the pane
exact could enable the cheaper pylabview link retarget, but that is unmeasured, and an exact pane
changes the conditions the swap route was measured under. It also makes the stub reference the
user's `.lvclass` — a dependency from the LabVIEW installation into a project, which is exactly what
keeping stubs self-contained avoids. Decide that separately.

## 5. What shipped

`lvai_flatten_typedefs`, and `lvai_placeholder_subvi` runs it by default (`flattenTypedefs: false`
restores the old behaviour). It drives two new helpers, both in `scripts/`:

| helper | what it does |
|---|---|
| `lvai_typedef_tree.vi` | lists one container's children with label, `Is Typedef?`, `Typedef:Path` and `Class Name` |
| `lvai_typedef_bind.vi` | installs a `.ctl` onto one control and saves the file in place |

**Both descend by INDEX, not by label**, and that is a design constraint rather than a preference: a
label lookup per level needs a loop inside the descent loop, and AIXML auto-indexes a refnum array
tunnel into an inner loop. By index the descent is one flat loop. The caller already holds the
indices, because it got them from the previous listing.

The `.ctl` flattening itself needs **no LabVIEW** — copy, clear the flag with pylabview, rebuild —
so only the `Replace` per level costs a round trip, and the flattened copies are cached in `LV_MCP`
under a hash of the source's content.

**The guard that matters most is the class one.** A class terminal answers `Is Typedef?` non-zero,
so the same probe that finds a real typedef finds every class terminal too; flattening one would
install the class's private data as a plain cluster and the terminal would stop being a class. It is
checked twice — at the terminal level and on the nested walk — and named in the answer rather than
passed over. That is what the unit tests cover, because it is the one failure here that is
destructive rather than merely useless.

**An ARRAY is walked into as well, since 2026-09-18.** `Controls[]` is not declared on
`{LV.Array}`, so the helpers read `Array Element` beside it and pick with `Select` on `Class Name`;
from C# an array is then a container with exactly one child at index 0. It was reported under
`notDescended` and left linked until then — §12b has the measurements, including the 0 → 1 → 0
control that proves the walk enters it rather than merely agreeing with an already-clean stub.
A container of any OTHER kind is still passed over, silently, because nothing has been measured
about one.

**The tool could not be acceptance-tested in the session that added it** — a client fetches the tool
list at session start, so a tool declared later is not callable from that session. What IS measured
is the whole mechanism, by hand, through exactly the two helpers the tool drives: see §3 and §4.
The C# orchestration on top of them is unexercised against a live LabVIEW.

## 6. Reproducing

`C:\temp\TypedefStub\aixml\` holds every probe as AIXML: `inner-mode.xml`, `outer-config.xml`,
`subject.xml`, `rep-top.xml`, `rep-nested.xml`, `discon-top.xml`, `discon-one.xml`,
`discon-safe.xml`. The flag patch is two byte-substitutions in the pylabview bundle's main XML:

```
<Instrument Type="Standard"        ->  <Instrument Type="Control"
TypeDefVI="0" StrictTypeDefVI="0"  ->  TypeDefVI="1" StrictTypeDefVI="1"
```

**Running any of the three `discon-*` probes ends the LabVIEW session.** Keep the fixture backed up
before a run; `backup\` beside it is a copy of all three fixture files.

## 7. The acceptance run, and the two defects it found — 2026-09-18

The tool could not be called from the session that wrote it, so the acceptance test drove the built
exe over raw MCP stdio with no client in between. That settled the first question at once: the
artefact serves **80 tools**, `lvai_flatten_typedefs` among them, and `lvai_placeholder_subvi`
declares `flattenTypedefs`. Then it found two real defects, neither of which any unit test could
have.

### 7a. `Replace` refuses a source `.ctl` under `user.lib` — error 1154

The first version put the flattened copies beside the stubs in `user.lib\LV_MCP`. Every install
answered `error 1154` from `{LV.Control} Replace`.

Measured as an A/B **in one LabVIEW session, same helper, same stub, seconds apart**, differing only
in where the source sat:

| source `.ctl` | `Replace` |
|---|---|
| `…\user.lib\LV_MCP\LVMCP Flat 88fc689b10.ctl` | **error 1154** |
| `C:\temp\TypedefStub\Flat2 Config.ctl`, same content | **0** |

So it is the LOCATION, not the file. Note what this cost to see: the hand-written helper that had
succeeded three times that morning failed identically once pointed at a source inside the
installation — which is what ruled out "the production helper is different".

Nothing is lost by moving the copies out. The STUB must live in `user.lib\LV_MCP` because it is
resolved as a `Call` target by bare name; a flattened `.ctl` is only ever a Replace source, and
since the copy is not a typedef the Replace leaves no link, so the stub does not reference it
afterwards.

### 7b. A per-child read error was invisible, and the walk skipped it in silence

`lvai_typedef_tree.vi` chained one error per iteration and only the LAST could reach its error out.
A control whose property read failed therefore came back as a **plausible row**: empty label, empty
`Typedef:Path`, empty `Class Name`, `is typedef: true` — while the VI itself answered `code 0`. The
C# keyed on the empty path, skipped the child, and returned a copy that still carried the typedef.

That is the failure this whole document exists to avoid, reproduced inside the fix for it: the tool
reported `flatCopy` and a plausible outcome while installing nothing. The helper now returns a
`read codes` array, one entry per child, and the walk STOPS on a non-zero entry rather than
treating an empty path as "not a typedef".

With that in place the next run named the real cause immediately:

```
"problem": "childUnreadable",
"detail": "Reading control 0 of 'Outer Config.ctl' at index path '0' answered error 7"
```

### 7c. Error 7 was a third defect, and the fix was not either of the two candidate locations

**Error 7 is File Not Found, and it is the nested typedef.** A flattened copy moved away from its
original directory can no longer resolve the `.ctl` it embeds — LabVIEW finds a nested typedef
beside the file that references it. So it looked like a choice between two places to flatten:

- a **working directory beside the original**, where the chain siblings resolve — but then a copy
  carrying the ORIGINAL's VI name while the original is loaded is the shape behind `Error 1051`, so
  the names must differ, and differing names are what the nested reference cannot follow;
- a **cache directory**, where names are free — but every nested reference has to be rebound before
  the file can be moved, which is the thing that needs the resolution in the first place.

**It is neither, and the question that dissolved it was "is the working directory faster?".** It is
not — the LabVIEW round trips dominate and are the same either way, and the cache is the faster one
*across runs*. Writing that down made the real point visible: **the read that fails does not have to
happen on the copy at all.** All the nested level has to yield is WHICH `.ctl` sits at WHICH index,
and the ORIGINAL answers that with everything resolved. The `Replace` that follows does not need the
old binding to resolve either — it is replacing that control outright.

So the plan is read from the original and applied to the copy by index. The copy is never read from,
the names stay free, and the cache keeps working.

### 7d. Accepted

Cold — fresh LabVIEW, no cache, no stub — driving the built exe over stdio:

```
typedefTerminals: 1
flatCopy: …\.labviewmcp\cache\flat-typedefs\LVMCP Flat 88fc689b10.ctl
reusedFromCache: false      ok: true      typedefObjectsInStub: 0      8497 ms
```

and verified INDEPENDENTLY from the saved stub rather than from that answer: **0** `class="typeDef"`
heap objects, **0** `Type="TypeDef"` wrappers, **0** mentions of any `.ctl`, and `Mode` a plain
`UnitUInt16` carrying all three `EnumLabel`s.

A second run on the same instance: `reusedFromCache: true`, **1398 ms** — the flattened chain is
cached under the source's content hash, so a repeat costs one `Replace`.

**The originals are untouched**, checked byte-for-byte against a backup: `Inner Mode.ctl`,
`Outer Config.ctl` and `Typedef Subject.vi` all identical after the whole run.

**One thing to know that is not a defect.** A LabVIEW instance that has been through many
open/Replace/close cycles began reporting the subject's typedef terminal as `typedefTerminals: 0`,
while the files on disk were unchanged and a cold instance read `1` again. That is in-memory
staleness, and it is the reason this section's numbers are from a cold run: a pass that does not
reproduce cold is not a pass.

## 8. The requirement, restated — and why a pylabview TRANSPLANT is the answer

The user's acceptance criterion, 2026-09-18: *create the VI through the AIXML interface, then swap
the control or indicator back with **pylabview***. Both routes above fail that as stated — §3 and
§2a each do the swap with `{LV.Control} Replace`, which is LabVIEW.

**The structure says a transplant is possible, and that it is NOT the composition this repository
records as out of reach.** A front-panel control in the saved heap is two things:

```
<typeDesc>TypeID(13)</typeDesc>   <ddo class="udClassDDO" uid="135">    <- class terminal
<typeDesc>TypeID(5)</typeDesc>    <ddo class="stdPath"    uid="6">      <- the path stand-in
```

measured side by side in ONE file — the stub after exactly one of its two class terminals had been
swapped. So the shapes to turn one into the other are both present and comparable.

`docs/aixml-reference.md` §5 rules out *synthesising* a `typeDef` or `udClassDDO` heap object from
nothing. It does not rule out **copying one that already exists**, and the subject always has the
object the stub needs, by construction — the stub was cloned from that subject's pane.

**And the connector pane makes it easier than feared.** It binds by uid, not by position:

```
<SL__arrayElement class="ConpaneConnection" index="8"> <ConnectionDCO uid="108" /> </SL__arrayElement>
```

So a transplant that **keeps the replaced control's uid** leaves the pane untouched — no
re-indexing, which is the operation `docs/connector-pane-repair.md` records as having killed
LabVIEW twice.

The edit is therefore bounded:

1. append the subject's `TypeDesc` for that terminal into the stub's `VCTP` as a new FlatTypeID
2. replace the stub control's `<ddo …>` subtree with the subject's, **keeping the outer uid**
3. point that control's `<typeDesc>TypeID(n)</typeDesc>` at the new FlatTypeID
4. renumber only the INNER uids of the transplanted subtree so nothing collides

**Not built, and not to be assumed working.** This is heap surgery, and every previous heap edit in
this repository that moved more than a flag or a string has had to be measured against a real
LabVIEW load before it could be believed. What is established here is the shape of the edit and
that the two forms are available to copy between - not that LabVIEW accepts the result.

**If it does work it retires both routes above.** No `.ctl` copies, no flag patching, no IDE
instance, no VI open in the editor, no active project - and the class terminal, which §2c shows
LabVIEW itself refuses to disconnect, becomes an ordinary transplant like any other.

## 9. The prototype: the VCTP half alone is NOT enough — measured 2026-09-18

The smallest step that could settle §8 was the half `docs/aixml-reference.md` §5 calls a text edit:
**wrap the `TypeDesc` in place** so the wrapper takes over the FlatTypeID and no type id moves, and
change nothing else.

`Transplant Target.vi` was created by AIXML with the BARE cluster — the user's own sequence — and
its `VCTP` entry for `Config` was then wrapped, by hand, into exactly the descriptor the subject
carries:

```
  <TypeDesc Type="Cluster" Format="inline" Label="Config">      ->  <TypeDesc Type="TypeDef" Flag1="0xE6D280C0" …>
    <TypeDesc TypeID="0" />                                           <TypeDesc Type="Cluster" Nested="True" … Label="Config">
    <TypeDesc TypeID="1" />                                             …
    </TypeDesc>                                                       <Label Text="Outer Config.ctl" />
```

`pylv_rebuild` to a fresh path, then the verdict:

| check | result |
|---|---|
| wrapper present in the REBUILT file | **yes** — 1 `Type="TypeDef"`, 1 `Outer Config.ctl` |
| `class="typeDef"` objects in the FP heap | **0** (4 `stdClust`) |
| `lvai_exec_state` | **1, eIdle**, no linker errors |
| does LabVIEW call the control a typedef? | **NO** — `Is Typedef?` 0, `Class Name` `Cluster` |

**So the front-panel heap object's CLASS is what decides, and the `VCTP` wrapper on its own is
inert.** LabVIEW does not object to the inconsistency either — it loads the file happily and simply
ignores the wrapper, which is the worst of both: a change that looks applied in the file and means
nothing.

That refines §5 of `docs/aixml-reference.md`. It says the `VCTP` half is a text edit and the heap
half is composition; it does not say the text edit **alone accomplishes nothing**. It does not.

### What the next step would actually cost

A working transplant has to swap the `<ddo class="stdClust" …>` subtree for a `<ddo class="typeDef" …>`
one. The structure is known and friendlier than feared:

```xml
<SL__arrayElement class="fPDCO" uid="4275">
  <objFlags>65664</objFlags>
  <typeDesc>TypeID(15)</typeDesc>
  <ddo class="stdClust" uid="4200"> … </ddo>
  <conNum>0</conNum>
  </SL__arrayElement>
```

The connector pane binds to the **fPDCO** uid and the fPDCO carries its own `conNum`, so swapping
only what is INSIDE the element leaves the pane untouched — no re-indexing, which is the operation
`docs/connector-pane-repair.md` records as having killed LabVIEW twice. And the AIXML `uid` survives
into the heap (`uid="4200"` is the number authored in the document), so the control to edit can be
addressed deterministically rather than found by label.

**The unsolved piece is `<typeDesc>TypeID(n)</typeDesc>`.** It is NOT the `TopLevel` index: the
target's `Config` fPDCO says `TypeID(15)`, and `TopLevel` maps 15 to FlatTypeID 6, which is
`NumInt32 Label="code"`. Until that numbering is decoded, a transplant cannot point the new control
at the right type, and guessing it is the kind of heap edit this repository has already paid for
twice. That decoding is the next measurement, and it is a read-only one.

## 10. TypeID decoded, and the transplant blocked one layer deeper — 2026-09-18

### 10a. The addressing was never the problem

pylabview's own comment says *"When Consolidated Type is referred to in other blocks, the TypeID is
Index from this list"* — and for the FRONT PANEL heap that is **wrong**, or at least not this list.
Cross-referenced control by control on a VI whose four controls are known:

| fPDCO uid | `TypeID(n)` | ddo uid | conNum | label |
|---|---|---|---|---|
| 4427 | 1 | 4230 | 15 | error out |
| 4368 | 6 | 4220 | 4 | Config Out |
| 4331 | 10 | 4210 | 11 | error in |
| 4275 | 15 | 4200 | – | Config |

`TopLevel` maps 15 to FlatTypeID 6, a `NumInt32` labelled `code`. So the FP heap's TypeID indexes
something else. The spacing (5, 4, 5 against controls of 3, 2, 3, 2 elements) fits roughly
`1 + elements + 1`, and a nested typedef adds one more — `Plain`, a cluster of 2 with a typedef
child, has spacing 5 where the same-shaped `Config Out` has 4.

**But the number never had to be decoded, and that is the useful finding.** The bare target and the
typedef'd subject have **identical** fPDCO tables — same fPDCO uid, same `TypeID(15)`, same
`conNum` — differing only in `ddo class` (`stdClust` against `typeDef`) and the ddo's own uid. A
same-shape swap therefore shifts nothing. And the block diagram references the **fPDCO** uid, not
the ddo uid, so the ddo may be replaced outright.

### 10b. What actually blocks it: the heap's values are encoded against a TypeDesc

With the addressing settled the transplant was straightforward to write — lift the subject's
`<ddo class="typeDef" uid="80">` subtree, renumber its four uid collisions, drop it into the
target's fPDCO 4275, wrap the `VCTP` entry. `pylv_rebuild` then refused:

```
Tag 'OF__StdNumMin' of Class 'SL__stdNum' has non-hex content,
but cannot get related TypeDesc
```

The subtree's numeric tags are **decoded relative to their type**:

```
StdNumMin  '0'                          <- the uint16 enum: decimal only
StdNumMax  '65535'
StdNumMin  '-inf (0xFFF0000000000000)'  <- the double: decimal AND hex
```

Where pylabview wrote the hex it can re-encode without help. Where it wrote decimal only it must
consult the DCO's TypeDesc — and in the TARGET file that TypeDesc does not resolve, because the
subtree came from a file with different type tables.

**The control arm says this is about the transplant and not about the shape.** The subject's own
bundle rebuilds cleanly to a fresh path, and the result still reports `Is Typedef?` 1 with
`Typedef:Path` = `Outer Config.ctl`. So **pylabview round-trips a typedef'd control losslessly** —
it just cannot carry one into another file.

### 10c. Why the obvious workaround was not taken

Supplying the missing hex by hand would silence the message. It should not be done: the check is
reporting a REAL inconsistency, not a formatting nicety — the transplanted DCO would point at a type
the target's tables do not describe. That is §9's "looks applied and means nothing", except written
to disk instead of ignored, and it would be hand-encoded LabVIEW internals guessed from one example.

**Where that leaves the requirement.** *AIXML creates, pylabview swaps* is not reachable by copying
a control between files with pylabview as it stands. What pylabview CAN do here is the flag edit on
a `.ctl` (§3), which is why that route exists. The swap itself still needs LabVIEW — either
`{LV.Control} Replace` (§3, no preconditions) or `Discon Typedef` (§2, two preconditions, faster,
and refused by LabVIEW on a class terminal rather than needing a guard).

## 11. The disconnect route shipped as a second route — accepted 2026-09-18

`lvai_flatten_typedefs` and `lvai_placeholder_subvi` now take a route name. The default is
unchanged, so nothing that worked before behaves differently.

| | `"flag"` (default) | `"disconnect"` |
|---|---|---|
| mechanism | copy each `.ctl`, clear `TypeDefVI`, install the copy | install the ORIGINAL `.ctl`, then `Discon Typedef` |
| copies anything | yes, cached under a content hash | **no** |
| project open and active | not needed | **required** |
| VI open in the editor | not needed | **required** — the call opens it |
| class terminal | needs our `IsClassPrivateData` guard | LabVIEW refuses it itself, `Error 1088` |
| failure mode | reports and carries on | **missing a precondition ends the LabVIEW session** |

Accepted cold — fresh LabVIEW, project opened, stub deleted first — driving the built exe over
stdio:

```
route: "disconnect"
installed:     Config  <- C:\Temp\TypedefStub\Outer Config.ctl   ok
stubOpenedInEditor: true
disconnected:  ""  index 0  Config  ok
               "0" index 0  Mode    ok      <- the NESTED one, found by the walk
typedefObjectsInStub: 0      ok: true      5791 ms
```

and verified from the SAVED stub rather than that answer: **0** `class="typeDef"` heap objects,
**0** mentions of any `.ctl`, and `Mode` a plain `UnitUInt16` carrying all three `EnumLabel`s.

**The route name is refused rather than defaulted.** A typo silently running the other route is the
difference between "nothing was required of you" and "LabVIEW is gone", so `ParseRoute` returns null
for anything that is not one of the two and the call says so. That is what the unit tests cover.

**The cost this route carries, reported in `stubOpenedInEditor`.** Opening the stub in the editor
puts its path in LabVIEW's memory, so regenerating THAT stub afterwards can answer `Error 1357`
until it is closed or LabVIEW restarts. The flag route opens nothing.

**One thing to expect that is not a defect.** A LabVIEW instance that has been through many
open/Replace/Discon cycles begins reporting the subject's typedef terminal as `typedefTerminals: 0`
while the files on disk are unchanged — measured twice, and a cold instance reads 1 again. Both
acceptance runs in this document are therefore from cold instances. A pass that does not reproduce
cold is not a pass.

## 12. What still does NOT work — 2026-09-18

Both routes end with the subject's real type and no link for a typedef reached through **clusters
and arrays, at any depth**. Everything below is outside that, and is listed so it is not discovered
later. 12a, 12b, 12d, 12e and 12f were on this list and are dealt with — 12d and 12e needed no code
change at all, and 12f is DETECTION rather than a cure, which is the honest half of it. **Only 12c
remains, and it is a deliberate design choice rather than a defect.** All five are kept with their
measurements rather than deleted.

### 12a. A typedef only NESTED inside a non-typedef terminal is never reached — FIXED 2026-09-18

The biggest one, and it is structural rather than a bug in either route. Two things both look only
at the TOP level:

- `lvai_placeholder_subvi` decides whether to flatten at all from `PaneTypedefsAsync`, whose helper
  reads `Panel → Controls[]` and does not descend;
- `FlattenIntoAsync` takes its terminal list from the subject's ROOT children.

So a pane whose `Config` is a **plain cluster containing** a typedef reported
`typedefTerminals: 0`, the flatten never ran, and the stub kept AIXML's approximation. Both routes
handled nesting fine once the OUTER terminal was itself a typedef — it was the way IN that missed.

**Both halves are fixed.** `lvai_placeholder_subvi` now runs the flatten whenever it is asked to,
rather than gating on the shallow probe; and the engine collects typedef SITES with its own
descending walk instead of reading the subject's root children. A site carries the pane terminal it
sits under, because only that first segment has to be translated for the stub — everything below a
terminal came out of the same AIXML type string in the same field order, so those indices carry
over.

Accepted cold on `Variants Subject.vi`, whose two remaining typedefs are both this shape:

```
typedefTerminals: 0          <- what the old entry condition saw: nothing
typedefSites:     2          <- what the descending walk finds
  Strict -> Mode  indexPath "0"  ok
  Plain  -> Sel   indexPath "1"  ok   (cache hit on the same flattened .ctl)
ok: true     typedefObjectsInStub: 0     2683 ms
```

`typedefTerminals: 0` beside `typedefSites: 2` is the whole point: before this, that zero was the
answer and nothing happened. Both counts are reported so the difference stays visible.

### 12b. A typedef inside an ARRAY — FIXED 2026-09-18

The descent cast to `{LV.Cluster}` and stopped there, so a typedef sitting in an array's element was
named in `notDescended` and left linked.

**The two container kinds are reached by DIFFERENT properties, and that is the whole difficulty.**
`Controls[]` is declared on `{LV.Cluster}`, `{LV.Panel}`, `{LV.ConnectorPane}`,
`{LV.RadioButtonsControl}` and `{LV.WaveformData}` — **not** on `{LV.Array}`, whose single child
comes back from `Array Element` as **one reference** rather than an array, and with **no label of
its own**.

**A Case structure was the obvious shape and is the wrong one here**: its output tunnel would carry
a refnum ARRAY into the enclosing loop, which is the auto-indexing trap `docs/aixml-reference.md`
records. So all three helpers read **both** forms unconditionally and pick with `Select` on
`Class Name`, which keeps the descent one flat loop. The cast that does not apply fails harmlessly,
its error deliberately unwired — the branch it feeds is the one `Select` discards.

```xml
<Node _name="Property Node" fields="read+Class Name" type="{LV.Control}" …/>
<Node _name="Equal?" inputs="x:…cls,y:…word" …/>          <!-- word = "Array" -->
<Node _name="To More Specific Class" … target class:…cls"/>   <!-- {LV.Cluster} -->
<Node _name="Property Node" fields="read+Controls[]"    type="{LV.Cluster}" …/>
<Node _name="To More Specific Class" … target class:…acls"/>  <!-- {LV.Array}   -->
<Node _name="Property Node" fields="read+Array Element" type="{LV.Array}"   …/>
<Node _name="Build Array" inputs="element:…el" …/>
<Node _name="Select" inputs="t:…arr,s:…isarr,f:…arr" …/>
```

From C# an array is then a container with exactly one child at index 0, and `IsDescendable` is the
one predicate all three walks use.

**Accepted on `Variants Subject.vi`, whose `Arr` is an array of the `Inner Mode.ctl` enum:**

| route | before | after |
|---|---|---|
| flag | `typedefSites: 2`, `notDescended: [Arr]` | `typedefSites: 3`, `Arr` flattened at indexPath `3`, `ok: true`, `typedefObjectsInStub: 0` |
| disconnect | same | three `installed`, three `disconnected` including `indexPath "3"`, `ok: true`, `typedefObjectsInStub: 0` |

**And a green pass over an array proves nothing on its own**, because a walk that never enters the
array leaves the same 0. So both halves were measured directly against the saved stub, one shipped
helper at a time:

| step | `class="typeDef"` in the stub's FP heap |
|---|---|
| after the flatten | **0** |
| `lvai_typedef_bind.vi`, original `Inner Mode.ctl` at index path `3`, child `0` | **1** |
| `lvai_typedef_discon.vi` at the same address (`typedef before` = true, `discon code` 0) | **0** |

That 0 → 1 is the control: the array-element address is real, a `Replace` there creates a link, and
`Discon Typedef` removes it.

**An array element's `control` field in the answer is the EMPTY STRING**, because the element has no
label — not because a read failed. The two are told apart by `read codes`, which is per control and
stops the call when non-zero.

### 12c. A CLASS terminal keeps its `path` stand-in — CORRECT AS IT IS, and the stated reason was WRONG

Neither route makes it faithful, and §10a says why that is correct rather than missing: a class
terminal is a `Refnum RefType="UDClassInst"` with **no typedef link to remove**. What it does leave
is a stub terminal whose TYPE is a `path` where the subject has a class — measured, the stub reads
`Gadget in [path]` where `Read Config.vi` has `ref{UDClassInst}`.

**This clause used to say a pylabview link retarget on such a stub is `Error 7, Bad Linkage`. It is
not, and the difference matters.** Measured 2026-09-18 end to end — stub, caller, `pylv_apply`
retarget onto `Read Config.vi`:

```
callTargets   ["Read Config.vi"]        <- the retarget landed
execState     0, eBad
linkerErrors  Missing subVI Read Config.vi in VI Class Caller.vi.
```

`Missing subVI`, not a linkage error. `Read Config.vi` is a **class member**, addressed as
`Gadget.lvclass:Read Config.vi`, and a pylabview link retarget rewrites a file NAME — it cannot
resolve a member at all. **The control is in this same document**: §12d retargeted the identical way
onto `Exotic Subject.vi`, a loose VI, and got `execState 1` with 0 coercion dots. So the retarget op
is not broken; class MEMBERSHIP is what it cannot reach, and the `path` stand-in's type mismatch
never gets a chance to be the cause.

The conclusion is unchanged and the reasoning is now the measured one: **use `lvai_swap_subvis`**,
whose `{LV.SubVI} Replace` runs inside LabVIEW, resolves the member and RE-TYPES THE WIRES. That is
already what `CLAUDE.md` says — *"Generation is `lvai_swap_subvis`; the retarget op is for a
pane-compatible swap of something already linked."*

**So there is nothing to solve here.** Making the terminal faithful is possible — `{LV.Control}
Replace` with the `.lvclass` path produces a real `UDClassInst` terminal, measured — but it would buy
nothing: the route that works does not care about the stand-in, and the route that would care cannot
reach a class member anyway. It would also make every stub depend on the user's `.lvclass`, which is
a cost with no return.

**What WAS worth fixing is where the advice sits.** `lvai_placeholder_subvi` put the right warning in
`classTerminalNote` and then ended its answer with an unconditional *"hand `retarget` to pylv_apply's
operationsJson"*, plus a ready-to-paste `retarget` string — for a socket where that route cannot
work. Same shape as `lvai_aixml_reference` section 8 and `lvai_swap_subvis`' `diagramSubVis`: the
advice arriving inside the thing it warns about. The closing note is conditional now.

### 12d. A type AIXML cannot express and that is not a typedef — MEASURED 2026-09-18

The stub's terminal is AIXML's approximation and neither route touches it, because both are driven
by `Is Typedef?`. Six exotic types have now been put through it.

**The vehicle: NI's own `vi.lib\silver_ctls\IO\*.ctl`.** They are ordinary controls, not typedefs
(`typedefTerminals: 0`, `subjectTypedefObjects: 0` on the fixture), so they are exactly this case —
and `{LV.Control} Replace` installs one onto any terminal, which is how a subject with a VISA refnum
or a DAQmx task name gets built without an IDE and without a driver VI. `lvai_vi_terminals` cannot
read into an `.llb`, and every DAQmx and VISA VI lives in one, so this is the route that works.

**The comparison must be BELOW AIXML.** The stub is authored FROM the subject's AIXML type string, so
comparing type strings compares the clone with itself. What LabVIEW binds on is the type descriptor,
so the measurement is `pylv_extract` on both and a diff of the `VCTP` type pools.

| subject terminal | AIXML literal | descriptor in the subject | in the stub |
|---|---|---|---|
| VISA Resource Name | `ref{Visa.Instr}` | `Tag TagType=VISArsrcName` | **identical** |
| DAQmx Physical Channel | `tag{14}` | `Tag TagType=DAQmxPhysChannel` | **identical** |
| Waveform | `doublewaveform` | `MeasureData` | **identical** |
| Digital Waveform | `digitalwaveform` | `MeasureData` | **identical** |
| Digital Data | `digitaltable` | `MeasureData` | **identical** |
| **DAQmx Task Name** | `ref{GenClassTag.Task.NIDAQ}` | `Tag TagType=DAQmxTaskName` | **`Tag TagType=UserDefined`** |

**Five of six survive, and the diff of the whole type pool was otherwise empty** — which is what makes
the sixth a finding rather than noise. The five are the control: a measurement where everything
differs says nothing.

**It is NOT a general property of tag refnums**, and that is why one probe would have been
misleading. `DAQmx Physical Channel` is a tag refnum too and keeps `DAQmxPhysChannel`; so does VISA.
Only the task name degrades, and it is the one AIXML renders in the generic-class-tag form
`ref{GenClassTag.Task.NIDAQ}`, which has no slot for the tag type. The literal's FORM does not
predict it either — VISA is a `ref{…}` literal and keeps its tag.

**And the degradation is LINK-COMPATIBLE, which is the part that decides whether it matters.** A
caller was authored against the stub, generated, and retargeted onto the subject with `pylv_apply`:
`callTargets: ["Exotic Subject.vi"]`, `execState 1`, **`coercionDots: 0`**, not broken. So a stub
whose task-name terminal is a generic user-defined tag still binds to a subject whose terminal is a
DAQmx task name. What is lost is the control's IDE behaviour — the DAQmx dropdown — on a throwaway
socket nobody opens.

**So the one known BREAKING approximation is still the class terminal, and that is §12c.** Everything
measured here is either exact or harmless.

**A heap-reading gotcha worth keeping**: in the front-panel heap `<conNum>` is **omitted when it is
0**, so a naive parse silently loses the terminal at `conIdx 0` — which on NI's own style guide is
the first input. Six explicit `conNum` elements and one absent, and the absent one was the terminal
under test.

### 12e. Nesting deeper than two levels — TESTED 2026-09-18, BOTH ROUTES PASS

The fixture was two deep. A three-level one was built — `Deep Subject.vi`, whose `Config` terminal is
`Deep3c Outer.ctl` → `Deep2c Test.ctl` → `Inner Mode.ctl`, three `class="typeDef"` objects nested
three deep — and **no code change was needed**:

| route | result |
|---|---|
| flag | `typedefSites: 1`, `subjectTypedefObjects: 3`, one flat copy, `typedefObjectsInStub: 0`, **2 219 ms** |
| disconnect | one `installed`, **three** `disconnected`, `typedefObjectsInStub: 0`, **1 698 ms** |

**The disconnect route's answer is the evidence that the descent really goes three deep**, because it
names the index path it reached and the `.ctl` it found there:

```
indexPath ""     Config  ->  Deep3c Outer.ctl
indexPath "0"    Inner   ->  Deep2c Test.ctl
indexPath "0|0"  Mode    ->  Inner Mode.ctl
```

The flag route cannot show that in its answer — it reports one site, because the recursion happens
inside `FlattenCtlAsync` over the `.ctl` chain rather than over the stub — so `typedefSites: 1`
beside `subjectTypedefObjects: 3` is what shows the depth there. That is the clearest illustration of
why §12f's check is not an equality.

**An inference was made here and was WRONG, which is why it had to be run.** The reasoning was: the
flag route binds each inner flat copy into the outer one, a `.ctl` that has just been bound into is
eBad (see below), and `Replace` refuses an eBad source — so depth 3 must fail with `1154`. It does
not. The flat copies have their `TypeDefVI` bit CLEARED before anything is bound into them, and that
is apparently what the breakage needs.

### 12e-a. `Error 1154` from `{LV.Control} Replace` means the SOURCE `.ctl` is eBad

Building the fixture cost four wrong hypotheses, all refuted by measurement, and the answer was one
`lvai_exec_state` call away the whole time:

| hypothesis | refuted by |
|---|---|
| the source may not sit under `user.lib` | both files were under `C:	emp` — and §12d then installed six `.ctl` sources from `vi.lib`, inside the installation, every one `code 0` |
| a typedef may not contain a typedef | `Outer Config.ctl` does, and installs fine |
| the result may not nest three deep | `1154` also at depth 2 |
| LabVIEW's in-memory copy is stale | still `1154` after a full restart |

`Deep2 Inner.ctl` read **`execState 0`, eBad** while `Outer Config.ctl` read **1, eIdle** — same
type, same construction, same session. `Replace` refuses a broken source and says `1154`.

**And the breakage is ON DISK, not in memory: it survived a LabVIEW restart.** Every `.ctl` that had
a typedef bound into it in the LONG-LIVED instance came out broken; rebuilding the identical chain in
a FRESH instance, with the same AIXML, the same flag patch and the same helper, produced `eIdle`
files and the whole chain bound without one `1154`. Two samples each way.

**The attribution is a hypothesis, not a measurement.** All that was varied is before/after the
restart, which confounds the instance's age with everything that had happened in it — including a
deliberate §12f experiment that left a file on disk disagreeing with LabVIEW's copy of it. What is
established is the practical rule: **if a `.ctl` you just bound into reads eBad, rebuild it in a
fresh LabVIEW rather than looking for a reason in the file.** And read `lvai_exec_state` on a
`Replace` source before believing anything else about a `1154`.

### 12f. A long-lived LabVIEW instance makes BOTH routes silently do nothing — DETECTED 2026-09-18

Measured twice: an instance that has been through many open/Replace/Discon cycles reports the
subject's typedef terminal as `typedefTerminals: 0` while the files on disk are unchanged, and a
cold instance reads 1 again. The flatten then does not run and everything answers `ok`. This was the
most dangerous item on the list because **a green no-op and a green success are the same answer**.

**The mechanism is now reproduced on demand, not merely observed.** Everything the walk knows comes
through VI Server, and VI Server answers from LabVIEW's **in-memory copy**; `Open VI Reference` does
not re-read a file it already holds. This repository had recorded exactly that from the pylabview
side — *"pylabview writes the file happily while LabVIEW keeps serving its stale in-memory copy"* —
and nobody had connected it to a property READ.

The recipe, which is also the regression test:

1. Generate `Stale Subject.vi` with a **plain** enum on the pane and no typedef.
2. `lvai_open_file` it, so LabVIEW holds it. (A reference opened and closed by the tree helper is
   not enough — the VI is unloaded again. It has to be open in the editor, or a project member.)
3. Overwrite the FILE with one that carries three typedefs.
4. Call the flatten.

### The fix: ask the subject's FILE, with no LabVIEW in the way

`TypedefObjectsAsync` already counted `class="typeDef"` objects for the stub verdict; it is now run
against the **subject** too, and reported as `subjectTypedefObjects` on every call.

| arm | `typedefSites` (VI Server) | `subjectTypedefObjects` (file) | verdict |
|---|---|---|---|
| control — typedef-free subject, file agrees | 0 | 0 | `ok: true` |
| the stale subject above | 0 | **3** | `ok: false`, `errorKind: subjectTypedefsNotSeen` |
| `Variants Subject.vi`, healthy instance | 3 | 3 | `ok: true` |

**The two numbers are in DIFFERENT UNITS and only one comparison is a verdict.** `typedefSites`
counts the OUTERMOST typedef per branch — the walk stops there, because the routes handle what is
nested inside it themselves — while the file count is every typedef INSTANCE at every depth. So
`Read Config.vi` is 1 site against 2 objects, the ordinary shape of a typedef inside a typedef, and
an equality check would refuse the case the feature was built on. `SubjectDisagreesWithItsFile` is
therefore `sites == 0 && objectsInFile > 0`, and a negative count — the file could not be read — is
never an accusation, only a note that the cross-check did not run.

**The error names both causes it cannot tell apart**: a stale LabVIEW (restart and call again), or a
typedef inside a container the walk does not enter — clusters and arrays are walked, nothing else
is. Naming one as the cause would be a guess.

**A class terminal cannot false-alarm this**, and that was measured before the check was written
rather than assumed: `Read Config.vi` carries two class terminals and they are `class="udClassDDO"`
heap objects, contributing **0** to the typedef count, while both of its `typeDef` objects sit under
the one real typedef terminal. So a subject whose only "typedef" is the class terminal the tool
correctly skips reads 0 against 0.

**And the failure is named at the TOP level of `lvai_placeholder_subvi`, in
`typedefFlattenWarning`.** `ok` there is about the placeholder, which really was created and is
usable — but a green `ok` over a stub that quietly kept AIXML's approximation is the shape this
repository keeps paying for: a failure that is real, reported, and one level further down than
anybody looks.

## 13. A FLAG-PATCHED `.ctl` IS NOT FINISHED UNTIL LABVIEW HAS SAVED IT - 2026-09-18

§1 records the fixture route - `lvai_generate_vi` to a `.ctl` path, then patch `<Instrument Type>`
and `TypeDefVI` in the pylabview bundle - and calls the result a real typedef. It is one, for every
use §1 through §12 makes of it: `lvai_describe_ctl` answers `isTypedef: true, bindable: true`,
`{LV.Control}` `Replace` installs it, `lvai_bind_class_fields` binds it onto a class field, and
`lvai_placeholder_subvi` flattens it. **And NI's accessor wizard refuses it**, measured on a real
build (`docs/labview-actor-framework.md` §15a):

```
Error 1061 at New VI Object in
  MemberVICreation.lvlib:BaseAccessorScripter.lvclass:CreateControlFromReference.vi
```

only for the field the `.ctl` is bound to, on the Read side and the Write side alike.

**The discriminator is already in `lvai_describe_ctl`'s answer and nothing reads it: `wrappedType`.**

| `.ctl` | `wrappedType` | `fields` |
|---|---|---|
| flag-patched, never saved by LabVIEW | **`Function`** | 16 - one real, 15 `Void` |
| the same file after one `Save.Instrument` | **`TypeDef`** | **1** |

The 16 are the CONNECTOR PANE of the VI the file was generated from. `pylv_rebuild` writes the two
flags and nothing else, so the pane is still there, and `VCTP/TopLevel` index 1 is a pane slot
rather than the TypeDef wrapper a control has. Everything that only needs the TYPE reads through it;
`CreateControlFromReference.vi` wants the control and gets an invalid reference.

**The control arm is in the §1 fixture tree itself.** `Outer Config.ctl` reads `TypeDef` - it was
re-saved when `Replace` installed `Inner Mode.ctl` into it - while `Inner Mode.ctl` reads `Function`
to this day and has never been asked to do anything but be nested. So the two fixtures differ in
exactly this and the difference stayed invisible for as long as no accessor was generated from one.
**Strictness is not the variable**: the Ofen `.ctl` is a PLAIN typedef and works once saved.

**The fix is one save with the path unwired**, in the IDE's own application instance, which is how
`{LV.VI}` `Save.Instrument` writes a control in place: **`lvai_resave_ctl`**. 4 155 -> 4 367 bytes on
the measured file, and it reads the file back so `wrappedTypeAfter` is the verdict rather than an
`errorCode 0`.

**And `lvai_describe_ctl` says so itself now** - `needsLabviewSave` fires on `isTypedef` with
`wrappedType == "Function"`, a file-only check costing no LabVIEW, and it is what would have saved
this whole detour. It tests `Function` EXACTLY rather than `!= "TypeDef"`: only those two shapes have
been measured, and a flag raised on an unmeasured third would send a caller to a repair they do not
need, which is worse than the silence it replaces. §1's fixture recipe should end with the save, or
every fixture built from it carries the same trap.

### 13a. A GENERATED CLASS METHOD'S OWN PANE STILL FLATTENS, and `lvai_coercion_dots` misdirects the repair

Measured in the same build. The stub was flattened correctly - `typedefSites: 1`,
`typedefObjectsInStub: 0` - and after `lvai_swap_subvis` the caller still read `coerced: 1`, on the
typedef terminal of the accessor call. The coerced SOURCE is the generated method's own front-panel
CONTROL, which AIXML wrote as a bare cluster because it has no typedef in its grammar, and
`lvai_add_class_method` retypes only the class terminals.

`lvai_coercion_dots` closes its answer with *"Repair them with lvai_bind_typedef_constants"*, which
finds each source by its CONSTANT label and cannot reach a control. Right for the case it was
written for, silently wrong here. **The note should branch on what the coerced source actually is.**

The working repair is `{LV.Control}` `Replace` on the method's own pane plus the owning class's
`Save` in the same run - **`lvai_bind_pane_typedef`**, `terminals bound: 1`, after which
`lvai_coercion_dots` answered `clean: true`.

**`lvai_coercion_dots` returns `repairs` now**, both of them, with the question that picks between
them. **Branching it automatically was considered and NOT built**, and the reason is a hazard rather
than effort: telling a constant from a control means reading the coerced terminal's `Connected Wire`
and then that wire's source object, and **there is no `{LV.Wire}` class in the VI Server catalogue at
all** - the query answers nothing and lists 153 other classes. §"validation is not risk-free" in
`CLAUDE.md` records authoring AIXML against classes the catalogue does not list as the signature that
preceded three LabVIEW deaths, fired while LabVIEW PARSES the document. Naming both repairs is
strictly better than asserting the wrong one and costs no risk.

### 13b. `Save.Instrument` ALONE DOES NOT COMMIT A `Replace` ON A PLAIN VI EITHER - 2026-09-18

`docs/class-method-tooling.md` §1d has said since 2026-09-02 that a `Replace` on a **class member**
needs the owning class saved in the same run. The limit is wider, and this is the measurement:

| fixture | class save | `error out` at every stage | typedef objects in the SAVED file |
|---|---|---|---|
| `Profil Setzen.vi`, a class member | yes | 0 | **1**, and `Ofenprofil.ctl` named twice in `VCTP` |
| `Loose Probe.vi`, a plain VI in no project and no class | no | 0 | **0** |

LabVIEW demonstrably rewrote the loose file - a `VICD` block appeared where the generated one had
none - and dropped the binding on the way. So the class `Save` is what commits it. **What commits it
for a VI that is not a class member is NOT established**, and `lvai_bind_pane_typedef` therefore
requires the `.lvclass` instead of offering an arm that does not work. The helper had that arm for
about ten minutes, built behind a Case on an empty class path, and every stage of it answered zero.

**The only thing that saw it was reading the FILE.** That is why the tool's `ok` is gated on each
requested `.ctl` appearing as a `<Label>` in the saved VI's own type descriptors, and never on
`terminalsBound`, which is the helper's own count and read `1` for the file with none.

### 13b-a. THE FILE CHECK ALONE IS A FALSE PASS - found on ACCEPTANCE, 2026-09-18

The verdict above shipped as "the .ctl must be in the saved file", and the acceptance run in the
next session broke it in one call. Asked to bind a terminal named `Gibtsnicht` onto
`Profil Setzen.vi`, whose `Profil` was ALREADY bound to the same `.ctl`:

```
ok                       true          <- wrong
verified                 true          <- wrong
terminalsBound           0
terminalOnPanel          false
typedefInSavedFile       true
```

**Both disqualifying facts were in the answer and neither gated it.** The file check asks *is this
`.ctl` in the VI*, which a bind that happened yesterday satisfies exactly as well as one that just
happened - so it could never have caught this on its own. Same shape as `nodesSwapped` reporting the
REQUEST rather than the outcome, and as `wiringLost` being trusted as a verdict: **a field that
cannot distinguish the two cases must not be the whole verdict.**

`verified` is three conditions now - every requested `.ctl` in the saved file, every requested
terminal ON THE PANEL, and the helper matching as many as were asked for - and `terminalsNotOnPanel`
names what disqualified a run. The note branches on which of the three failed.

**The general lesson is about when this was found.** Everything in §13b was measured before the tool
existed, and the tool's own unit tests were green: the false pass needed a REAL call with a REAL
already-bound file, which is a shape no fixture had. This is the repository's "a tool tested against
a plausible fixture is not tested" rule reaching the verdict logic rather than the parsing.

**ACCEPTED in the next session, with the control arm that makes it mean anything.** Same VI, same
already-bound `.ctl`, two calls:

| binding asked for | `ok` | `verified` | `terminalsBound` | `terminalsNotOnPanel` |
|---|---|---|---|---|
| `Gibtsnicht` (not on the panel) | **false** | false | 0 | `["Gibtsnicht"]` |
| `Profil` (the real one) | **true** | true | 1 | `[]` |

`typedefsMissingFromSavedFile` was `[]` in BOTH - the file check passes on its own in the failing
arm too, which is the whole point. **The control arm is not decoration**: a guard that simply made
the tool inert would have "passed" the first row, and this repository has already shipped one of
those - see the `.lvproj` sweep, where every guard passed its synthetic test by making the pass do
nothing.

### 13c. A FIRST OPEN READS EVERY CONTROL NAME AS EMPTY - 2026-09-18

Measured twice on one fixture inside a minute, same inputs, nothing else changed:

| run | `terminal names seen` | `terminals bound` | `error out` |
|---|---|---|---|
| first, on a VI LabVIEW had never loaded | four EMPTY strings | 0 | 0 |
| second | `Profil`, `error in`, `Profil out`, `error out` | 1 | 0 |

`{LV.Control}` `Terminal` -> `{LV.Terminal}` `Name` is what comes back blank. Any helper that finds a
terminal BY NAME inherits this - and finding them by name is mandatory, because `Controls[]` order is
front-panel creation order for a generated VI and error-clusters-first for a template-built one.

So **a count of what was bound must be compared against what was asked for**, and a zero is a reason
to retry once rather than a verdict. `lvai_bind_pane_typedef` retries once and reports
`retriedAfterEmptyNames`; a second empty reading is reported rather than hammered at, because more
than one retry would be guessing at a mechanism nobody has established.

### 13d. THE `.ctl` FIXTURE ROUTE MUST RUN WITH THE PROJECT CLOSED - measured 2026-09-18

§13's repair has a precondition nobody had written down, and skipping it makes `lvai_resave_ctl`
answer `ok: false` for ever. The A/B is one variable - the same AIXML, the same destination path,
the same `lvai_generate_vi` call:

| project state during generate + extract | file | bundle | resave |
|---|---|---|---|
| **OPEN** | **6 148 bytes** | **17 files**, incl. `_VICD_code.bin`, `_VICD_patches.bin`, `BNID`, `GCDI`, `NUID`, `SUID` | `Function` -> `Function`. **Never converts** |
| **CLOSED** | **4 156 bytes** | **11 files**, no `VICD` | `Function` -> `TypeDef`, `needsLabviewSave` true -> false |

With the project open LabVIEW has COMPILED the control, so `pylv_extract` carries `VICD` blocks -
and pylabview copies those through unparsed, which is exactly the property CLAUDE.md warns about:
*the round trip is lossless, so it preserves compiled code describing the state BEFORE your edit.*
The flag patch then lands in a file whose compiled half still says "standard VI", and LabVIEW's own
save keeps that shape.

**The failure is stable, not transient.** A second `lvai_resave_ctl` was byte-identical
(5 668 -> 5 668). So retrying does not clear it.

**AND RESTARTING LabVIEW IS A CLEAN NEGATIVE.** The first hypothesis was the documented stale
in-memory copy, and it is WRONG here: LabVIEW killed, restarted, project reopened, resave run again -
`wrappedTypeBefore: Function`, `wrappedTypeAfter: Function`, byte-identical. **The remedy is the
documented ORDER - close the project, then extract, edit, rebuild - and nothing heavier.** Reaching
for a restart first cost three calls and a LabVIEW start, and the user's correction was explicit:
*"Ein Neustart von Labview sollte nicht nötig sein. Ein Schliessen des Projektes sollte genügen."*

The cheap tell before you spend anything: **count the files in the `pylv_extract` answer.** 11 is
clean, 17 means LabVIEW compiled it and the patch will not take.

### 13e. TWO TYPEDEFS ON ONE CLASS - measured 2026-09-18, everything holds

`Presse.lvclass` with `Pressprofil.ctl` on `Profil` and `Pressgrenzen.ctl` on `Grenzen`, and one
method (`Ruesten.vi`) carrying BOTH on its pane. Every multi-binding arm was new:

| step | result |
|---|---|
| `lvai_bind_class_fields`, two bindings in ONE call | both `boundInFile: true` |
| `lvai_create_accessors` over 4 fields | 8 accessors, `errorCode 0` - **no `Error 1061`**, because both `.ctl`s had been through `lvai_resave_ctl` |
| `lvai_coercion_dots` after the swap | **2** coerced, one per typedef terminal, and `repairs` fired live for the first time |
| `lvai_bind_pane_typedef`, two bindings in ONE call | `terminalsAsked 2`, `terminalsBound 2`, `verified: true` |
| `lvai_coercion_dots` again | `clean: true`, 4 calls, 19 terminals, **0** coerced |
| `lvai_create_message_class` on the two-typedef method | `payloadControlCount: 2`, and the message class's private data carries **both** as `TypeDef` |

So nothing about the typedef route is single-binding-only. The pipe-joined `terminal names` /
`ctl paths` inputs carry two pairs correctly.

### 13f. THE FOLLOW-UP PROBE, and what it RETRACTED - 2026-09-18

Two things were noticed during the build and written down here as findings. **One of them did not
survive the probe**, which is the reason the probe is worth its three calls.

**RETRACTED: `lvai_placeholder_subvi` does NOT systematically under-report the second typedef.**
During the build `Write Grenzen.vi` read `typedef: false` and `typedefTerminals: 0` while
`typedefFlatten` in the SAME answer found `typedefSites: 1`, and `Write Profil.vi` - built
identically - agreed with itself. That looked like a reporter that walks only the first VI's panel,
and the first draft of this section said so. **Re-probed both VIs in one call: BOTH read
`typedef: true`, `typedefPath` naming the right `.ctl`, `typedefTerminals: 1`.** So it is not a
property of the tool or of the second typedef. The remaining candidate is the one §13c already
measures for the pane binder - **the first open of a VI LabVIEW has never loaded reads its controls
as empty** - and by the time of the re-probe both VIs had been loaded many times. Not proven, and
not worth a LabVIEW restart to prove.

**The lesson is the one about writing, not about typedefs.** A single observation of two VIs
differing is a coincidence with a sample size of one each; it was written up in the same voice as
the measurements around it, which is exactly what this repository has already paid eighteen days for
once. **Probe before the paragraph, not after it.**

**STANDING: `lvai_coercion_dots` needs the VI loaded, and says so honestly.** Straight after
`lvai_swap_subvis` it answered `subViCalls: 0` and refused to call that clean - its `examinedNothing`
guard doing exactly what it exists for. One `lvai_exec_state` later (which loads the VI) it read all
4 calls. A third reading got 2 of 4 with `subViFound: ""` and again reported `ok: false`. The pane
binder retries once for this; this tool does not.

### 13g. `Error 1025` MEANS THE `.lvproj` IS NOT THERE - and it took two wrong diagnoses to find

This section has been rewritten twice, and both earlier versions are kept below because the wrong
answers are the ones a reader will reach for.

**THE MEASUREMENT, an A/B inside ONE directory:**

| call | answer |
|---|---|
| `lvai_open_file` on `C:\temp\OpenProbe\OpenProbe.lvproj` (exists) | `errorCode 0`, `projectBecameActive: true` |
| `lvai_open_file` on `C:\temp\OpenProbe\GibtsGarNicht.lvproj` (does not) | **`Error 1025, Application Reference is invalid`** |
| `lvai_open_file` on a `.vi` that does not exist | `Error 7, File not found` - honest |

**So LabVIEW reports a missing PROJECT FILE as a fault in the IDE's application reference.** The
message names nothing the caller did. A missing VI gets the honest code; only the project path
lies. `lvai_open_file` refuses a path that is not there now (`errorKind: fileNotFound`), naming
1025 in the refusal so the connection is made for whoever meets it elsewhere.

**WHAT IT COST, and this is the part worth reading.** The original symptom was three `1025`
answers for `C:\temp\ActorFW_first\Presse\Presse.lvproj`. Two diagnoses were written up, in the
voice of measurements, before anyone ran `ls`:

1. *"The IDE's application reference has gone invalid; no cheaper remedy than restarting LabVIEW
   is established."* Built on a real observation - calls in the ADDON's instance worked while
   anything needing the IDE's failed - and the remedy half was pure inference.
2. *"LabVIEW restarted under the session, so the references are stale."* Built on real evidence
   too: the Nigel service log has all six features of the running instance dropping at 14:45:15,
   and LabVIEW's process `StartTime` is 14:45:23. **Both facts are true and neither is the cause.**
   It was refuted by the next probe - a fresh LabVIEW, a fresh server and a fresh client answered
   `1025` again within two minutes.

**There is no `Presse.lvproj`.** That actor lives in `ActorFW_first.lvproj` one directory up; the
path had been invented and then reused across a LabVIEW restart, a client restart and two written
sections. **Every probe in both rounds was aimed at LabVIEW's state, and none at the argument.**

**The process lesson, which is why this is written at length.** A restart, a service log and a
process id are all *available* evidence, and reaching for available evidence before cheap evidence
is how a five-second `ls` came last. **Check that the file exists before concluding anything about
the machine** - and when a message names a subsystem (`Application Reference`) rather than the
input, treat that as a reason to doubt the message, not as a lead. The tool's guard exists so the
next reader never gets the chance to make this mistake.

**`lvai_status` reports `labviewUpSeconds` as of the same day**, and that field survives the
retraction on its own merits: the zero-DWarn note has always warned that the log is reset at start
and never said WHEN, so a zero eight seconds old printed identically to one earned over four hours.
It is a real gap and the field closes it. What is retracted is the CLAIM that it explains `1025`.

**AND SCOPING THAT CAVEAT TO A ZERO WAS TOO NARROW - ACCEPTANCE SHOWED IT, NOT ARGUMENT.** Measured
2026-09-18 on three freshly restarted instances, 98 s, 94 s and 91 s old: the first two answered
**`dwarnCount: 1`, not 0** - one `DestroyPlatformEvent failed with MgErr 42`, which this file
already records as benign teardown - and the third answered 0. So a fresh instance lands on EITHER
branch, and the branch beside the zero one - *"Low enough to be ordinary"* - carried the identical
defect on an instance ninety seconds old.

**The first write-up of this said the zero branch was "very nearly unreachable", from those two
samples, and the third sample refutes it** - the caveat now fires there too, verified live. The FIX
was right either way, because it covers both branches; only its justification was drawn from n=2.
Two samples that agree are not a distribution, which is the same objection this file already records
against reading 0, 1 and 2 as a trend. `StatusTools.LowDwarnNote` shares the age
caveat across both now. **The lesson is the one this file keeps relearning: a fix aimed at the case
that PROMPTED it is not aimed at the case that OCCURS** - and the thing that showed the difference
was running the tool against a real instance twice, not reasoning about the branch.


**And one field learned something from the failed opens.** `activeProjectPathDiffers` carried the
note *"This has never been seen happening"*. It has now: a FAILED open leaves the previous project
active, so the mismatch fires with the earlier project's path. The note says so rather than
implying a switch went wrong.

### 13h. A REPORTING DEFECT THE SAME PROBE EXPOSED, now fixed

`lvai_open_file`'s `hint` asserted two things it had not checked, in one sentence, exactly where a
reader is already confused. It opened *"The open itself reported no error"* beside an `errorCode`
of **1025**, because the branch was keyed on `projectBecameActive == false` ALONE - and in the same
breath claimed *"this call already tried fronting it and opening again"* while `foregroundRetry`
was `null` and no retry had run. It then sent the reader after the foreground window, which is the
measured cause of a completely different failure.

Both facts are arguments now (`ActionTools.NoActiveProjectHint`), with a control arm in the tests:
a clean open that really did leave no active project still gets the foreground diagnosis, because
that half was right for the case it was written for. The 1025 branch names the missing-file cause
and then says outright that the guard already excluded it, so a 1025 arriving anyway is
**unexplained** - it does not offer a third story.

**STANDING, unfixed: `lvai_coercion_dots` needs the VI loaded.** Straight after `lvai_swap_subvis`
it answered `subViCalls: 0` and refused to call that clean - its `examinedNothing` guard doing
exactly what it exists for. One `lvai_exec_state` later (which loads the VI) it read all 4 calls. A
third reading got 2 of 4 with `subViFound: ""` and again reported `ok: false`. The pane binder
retries once for this; this tool does not.

**RETRACTED HERE: the `1154` from `{LV.Control} Replace` is NOT "the same fault" as 1025.** That
claim was made only to tie two symptoms together and nothing supports it. The flatten was run with
NO project active, and `{LV.Control}` `Replace` needing the IDE's own application instance is
already documented; that is the ordinary explanation and it was never tested against.

### 13i. THE PROJECT-CLOSED RULE HELD ON A SECOND BUILD - and the precondition is the part to check

Reproduced 2026-09-18 on `ComputerMaus`, a fresh two-typedef actor, without a surprise. What is worth
recording is HOW it nearly went wrong: the first `.ctl` generation came out at **6025 bytes with a
17-file bundle carrying `VICD`**, which reads as the rule being false, because the project had been
closed several calls earlier and nothing in between opened one.

**It had. `lvai_close_active_project` with no arguments answered `nothingToClose: false`** - a
project WAS active - and regenerating the same AIXML to the same path then gave **4069 bytes and 11
files**, no `VICD`, and a resave that converted. So the rule is intact and the trap is the
precondition: **a project can be active without any call in your own transcript having opened it**,
and the cheap way to settle it is the close's own `nothingToClose` flag rather than reasoning back
through what you did.

`section 13d`'s file-count tell did the work here: 17 files said stop before anything was patched.
