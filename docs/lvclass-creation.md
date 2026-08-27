# Creating a `.lvclass` from outside LabVIEW

How a class hierarchy was built on 2026-08-26 with no IDE interaction, what worked, and the three
places where the route runs out. `lvlib-lvclass-structure.md` is the census of what a `.lvclass`
*is*; this is the recipe for making one, and the measured boundary of it.

NI puts `.lvclass` and `.ctl` on the AIXML generator's unsupported list independently, so nothing
below is a supported interface. Everything here was measured against LabVIEW 2026 Q3.

## 0. Use `lvai_create_class`

The recipe below is what that tool does. Call it instead of following the steps by hand:

```
lvai_create_class  className=Bus  directory=…\Bus  parentClassPath=…\Auto\Auto.lvclass
                   fields="uint32.Passenger Capacity,string.Route Number,bool.Has Ramp"
```

`lvai_describe_class` reads one back — ancestry, members, scope, private data size — with no LabVIEW
running. Both have CLI forms, `--create-class` and `--describe-class`, for CI and for timing without
a client in the way.

**Measured 2026-08-26, the same three classes both ways:**

| | by hand | through the tool |
|---|---|---|
| wall clock | **18 min 20 s** | **9.4 s** (3.7 s + 2.9 s + 2.8 s) |
| round trips | ~12 | 3 |
| silent failures survived | 1, costing most of the time | 0 — the load check is a step |

The first call carries process start-up and gRPC port discovery; the two after it are the honest
per-class cost. The tool refuses to report success without a project describe coming back with a
non-empty `libraryName`, which is the only signal that means anything (§2).

**Those 9.4 s are a COLD run** — LabVIEW freshly started (35.7 s to the gRPC service on its own),
the AIXML export cache moved out of the way, and every scratch directory under
`%TEMP%\LabVIEWMCP` deleted. Against a warm run of the same three classes at 9.0 s, so the caches
buy **about 4 %**, and that is the expected answer rather than a surprising one: nothing on this
path reads the export cache. `lvai_convert_vi_to_aixml` is the only reader, and creating a class
never reads a VI — it writes AIXML, validates it and generates. Worth knowing before wiping 1 168
exports to make a measurement honest; the measurement was already honest.

## 1. What works: the whole recipe

Three classes — a base plus two derived — went from nothing to `errorCode 0` out of
`lvai_describe_project`, each with a private data cluster carrying its own named, typed fields, in
**18 minutes** including the research. That is the number the tool above replaces.

1. **Author the private data cluster as a VI, with AIXML.** A `Control` whose `type` is
   `cluster{string.Manufacturer,int32.Year Of Manufacture,…}` generates without complaint —
   `lvai_generate_vi`, 163 ms. This is the step that matters: it means the cluster's types and its
   front-panel heap are built by LabVIEW itself, so nothing downstream has to add a type to `VCTP`
   from scratch (the case `pylabview-controls.md` §5 flags as untested).
   Name the control **`Cluster of class private data`** — that is what LabVIEW calls it.
2. **`pylv_extract` that VI and turn it into a class private data control.** Eight attribute edits
   and one in-place wrap, all in the bundle's main XML:

   | element | attribute | VI | class private data control |
   |---|---|---|---|
   | `<Instrument>` | `Type` | `Standard` | `Control` |
   | `<Instrument>` | `InStBit13` / `InStBit23` | `0` / `1` | `1` / `0` |
   | `<Execution>` | `TypeDefVI` / `StrictTypeDefVI` | `0` / `0` | `1` / `1` |
   | `<Execution2>` | `IsPrivateDataForUDClass` | `0` | **`1`** |
   | `<Execution2>` | `InlinableDiagram` | `1` | `0` |
   | `<Execution2>` | `DefaultErrorHandling` | `0` | `1` |

   `IsPrivateDataForUDClass` is the flag that makes it private data rather than an ordinary strict
   typedef. The `InStBit13`/`InStBit23`/`StrictTypeDefVI` trio is the strictness recipe already
   verified in `pylabview-controls.md` §2 — a class private data control reads `3`, strict.

   Then wrap the cluster's `TypeDesc` **in place**:

   ```xml
   <TypeDesc Type="TypeDef" Flag1="0x0" Format="inline">
     <TypeDesc Type="Cluster" Nested="True" Format="inline" Label="Cluster of class private data">
       …the field TypeID references, unchanged…
       </TypeDesc>
     <Label Text="Bus.lvclass" />
     <Label Text="Bus.ctl" />
     </TypeDesc>
   ```

   **In place is the point.** The `TypeDef` takes over the cluster's own `FlatTypeID`, and the
   nested cluster carries `Nested="True"` with no id of its own, so no `TypeID` anywhere else moves
   and the `TopLevel` mapping stays valid. Adding a type would have moved all of them.

   A class owned by a `.lvlib` carries **three** labels — library, class, file — as
   `Circle Message.ctl` does. A standalone class needs the last two.
3. **`pylv_rebuild` to a `.ctl` path.**
4. **Encode the `.ctl` into the class file** as `NI.LVClass.FlattenedPrivateDataCTL` (§2).
5. **Write the `.lvclass` XML** — the grammar in `lvlib-lvclass-structure.md`. The minimum that
   loads clean:

   ```xml
   <?xml version='1.0' encoding='UTF-8'?>
   <LVClass LVVersion="26008000">
       <Property Name="NI.Lib.SourceVersion" Type="Int">637566976</Property>
       <Property Name="NI.Lib.Version" Type="Str">1.0.0.0</Property>
       <Property Name="NI.LV.All.SourceOnly" Type="Bool">true</Property>
       <Property Name="NI.LVClass.ClassNameVisibleInProbe" Type="Bool">false</Property>
       <Property Name="NI.LVClass.FlattenedPrivateDataCTL" Type="Bin">…</Property>
       <Property Name="NI.LVClass.LowestCompatibleVersion" Type="Str">1.0.0.0</Property>
       <Item Name="Parent Libraries" Type="Parent Libraries">
           <Item Name="Auto.lvclass" Type="Parent" URL="../Auto/Auto.lvclass"/>
       </Item>
       <Item Name="Bus.ctl" Type="Class Private Data" URL="Bus.ctl">
           <Property Name="NI.LibItem.Scope" Type="Int">2</Property>
       </Item>
   </LVClass>
   ```

   No `NI.Lib.Icon` and no `NI.LVClass.Geneology` are needed; LabVIEW does not add them on save
   either, and it did not rewrite one byte of the three files it had just loaded.
6. **The `.ctl` named in that `URL` must NOT exist on disk.** LabVIEW keeps the private data control
   inside the class file and materialises the path only in memory — `Circle Message.lvclass` ships
   with no `.ctl` beside it, and `describe_project` reports the item at the synthetic path
   `…\Bus.lvclass\Bus.ctl`.

### `../` pops the referencing FILE NAME — and this shipped wrong

A `URL` resolves against the file that carries it, treated as a directory. So the leading `..`
removes the *file name*, not a directory:

| in | pointing at | correct `URL` |
|---|---|---|
| `…\fahrzeuge\Fahrzeuge.lvproj` | `…\fahrzeuge\Auto\Auto.lvclass` | `../Auto/Auto.lvclass` |
| `…\fahrzeuge\Bus\Bus.lvclass` | `…\fahrzeuge\Auto\Auto.lvclass` | `../../Auto/Auto.lvclass` |
| any `.lvclass` | its own private data | `Bus.ctl` — bare, it lives *inside* the file |

`lvproj-structure.md` §5 says this, and NI's own `Circle Message.lvclass` writes
`URL="../../Draw Message/Draw Message.lvclass"` for a parent in the sibling folder. The first
version of `lvai_create_class` computed the path from a *directory* and emitted
`Auto/Auto.lvclass` with no `../` at all — one level too deep, inside the `.lvproj` treated as a
folder.

**Nothing caught it.** LabVIEW is lenient enough to find the class anyway, so the load check
returned `loaded: true` with the correct `libraryName`, and `lvai_describe_class` reads the parent
out of the file rather than resolving it. The person who opened the project file saw it. The check
that *would* have caught it is three lines — resolve every `URL` against `os.path.join(str(file),
url)` and assert the target exists — and it is now what `RelativeUrl`'s tests pin.

The formatting went the same way and for the same reason: `AddToProject` round-tripped the project
through `XDocument.Save`, so the *second* class written into a project reformatted what the first
one wrote — a BOM appeared, `<?xml version='1.0'` became `"1.0"`, tabs became two spaces and every
self-closing tag gained a space. It loads fine; §8 of `lvproj-structure.md` keeps those rules so a
diff against a LabVIEW-saved file is signal rather than noise. It parses to decide and edits as
text to write now.

## 2. The `FlattenedPrivateDataCTL` blob is a whole `.ctl` file with a 37-byte wrapper

Decoded with the 6-bit scheme in `lvlib-lvclass-structure.md` §3, the property is:

| bytes | content |
|---|---|
| 0–28 | a 29-byte header, opaque; starts with the `LVVersion` (`26 00 80 00`) |
| 29–32 | **`u32` big-endian: the length of the `.ctl` that follows** |
| 33 … | the `.ctl`, a complete `RSRC` container, byte for byte |
| last 4 | `00 00 00 00` |

Reusing the 29-byte header verbatim from a LabVIEW-authored class works. The encoder is the decoder
run backwards, padding the final group with zero bits; the text is then XML-escaped for `&`, `<`
and `>`.

**Verify an encoder by round-tripping NI's own blob to a byte-identical string** — 7 364 characters
in, 7 364 identical characters out. That check is worth the two minutes it costs, because the way
this goes wrong is silent: a first attempt took the header as 31 bytes rather than 29, which put the
length field two bytes late. Nothing complained. `lvai_describe_project` returned three class
entries with **every field blank** and `errorCode 1` from a `Property Node` in NI's own
`Get library info.vi`, whose text — *"An input parameter is invalid. For example if the input is a
path…"* — points at paths and says nothing about the blob.

**Bisect a class that will not load by swapping in NI's own blob.** A hand-written skeleton carrying
`Circle Message.lvclass`'s blob verbatim loaded with `errorCode 0`, which separated "my XML skeleton
is wrong" from "my blob is wrong" in one call. LabVIEW even renamed the private data control to the
new class's name, so a donor blob is a legitimate way to get an empty-cluster class.

## 3. Where the route ends

### `lvai_describe_project` does not report class inheritance

Its `parent` field is the **owning library**, not the base class. Measured on NI's own hierarchy:
`Circle Message.lvclass`, which derives from `Draw Message.lvclass`, reports
`"parent": "Draw Messages.lvlib"`. So a class with no owning library reports `""` whatever it
inherits from, and nothing in the gRPC interface confirms that a `Parent Libraries` item resolved.
The `.lvclass` file stays the only source, as `lvlib-lvclass-structure.md` says.

### AIXML cannot author a class-typed terminal — so it cannot author a member VI

Both spellings are refused by name, `Error 53`:

| `type=` | message |
|---|---|
| `Auto.lvclass` | `Unrecognized or unsupported attribute set in Control with UID 10` |
| `ref{UDClassInst}` | **`Control with type=UDClassInst is not supported`** |

The type grammar in `aixml-reference.md` §5 lists `ref{UDClassInst}` as *a reference to a user-defined
class instance*, which reads as though it were available. It is not, for a `Control` or an
`Indicator`. Every class member VI — accessor, constructor, dynamic dispatch method, override — has
a class terminal on its connector pane, so **no class member VI can be generated through AIXML.**

### The donor route for a member VI is blocked by two blocks pylabview cannot parse

Copying an existing class member VI and renaming the class looks like a string edit, and in the main
XML it is — three occurrences, one of them the readable type descriptor:

```xml
<Library>Circle Message.lvclass</Library>
<TypeDesc Type="Refnum" RefType="UDClassInst" MultiItem="1" Label="Circle Message">
  <Item Text="Circle Message.lvclass" />
```

But the class link also lives in `LIfp` and `LIvi`, and pylabview parses **neither**:

```
Block b'LIfp' section 0 parse exception:
  LinkObjUDClassDDOToUDClassAPILink b'FPPI' Offset List length 16711680 exceeds limit
Block b'LIvi' section 0 parse exception:
  List of LinkObjects incorrectly ended with 0 after b'VIPI'
```

Both are copied through verbatim — which is what makes the round trip lossless, and what makes the
rename impossible here. The two blocks hold four more copies of the name, in length-prefixed `PTH0`
path records inside offset lists that pylabview never decoded:

```
LIfp 194 B -> ['FPHP', 'FPPI', 'PTH0', 'Circle Message.lvclass', 'DDPI', 'PTH0', 'Circle Message.lvclass']
LIvi 144 B -> ['LVIN', 'VILB', 'PTH0', 'Circle Message.lvclass', 'VIPI', 'PTH0', 'Circle Message.lvclass']
```

A same-length name would swap byte for byte. Any other name means rewriting length prefixes and the
offsets that point past them, by hand, in a block nothing here can validate. That is the wall.

**So a class can be created with its data, and its methods cannot** — but the scripting route is no
longer untested. §5 records what it can and cannot reach.

## 5. Accessors: the scripting route, measured 2026-08-26

The question is narrow: **an accessor needs a front-panel control whose type is the class.** AIXML
refuses to author one (§3), so the only remaining door is VI scripting from a generated helper. It
is open further than expected and still not far enough.

### VI scripting IS authorable in AIXML

NI's own `vi.lib\Utility\traverseref.llb\Get Class Hierarchy from Class Name.vi` does it, and its
export gives the pattern away: a `Constant` of type `ref{LV.ClassSpecifierConstant}`, a
`Property Node` on `{LV.VI}` reading `Block Diagram`, then

| node | inputs | outputs |
|---|---|---|
| `Open VI Object Reference` | `owner refnum`, `name/order`, `error in (no error)`, `vi object class` | `object refnum`, `error out` |
| `New VI Object` | `owner refnum`, `style`, `position/next to`, `error in (no error)`, `vi object class`, `auto wire? (F)`, **`path`**, `bounds` | `object refnum`, `error out` |

`New VI Object`'s `path` input is the documented way to create a control from a file, which is how
the IDE drops a class control — so this is the shape a solution would take.

### The class list is 296, not 153

`{LV.ClassSpecifierConstant}` carries an `All Types[]` property returning **every VI Server class
name on the station with its parent id**. Read through `scripts\lvai_class_names.xml` it answers
296 classes, of which **143 are absent from `vi-server-methods.tsv`** — that catalogue is harvested
from the AI add-on's own usage, so it only ever contained what the add-on happened to call. The full
list is `vi-server-classes.tsv`; re-harvest it after a LabVIEW upgrade by generating that helper and
running it with `lvai_run_vi_and_read_values`.

Three of the 143 are the ones that matter here:

| class | parent id | what it is |
|---|---|---|
| `LVClassLibrary` | 80 (`Library`) | a `.lvclass` itself |
| **`LabVIEWClassControl`** | 6 | **a front-panel control whose type is a LabVIEW class** |
| `LabVIEWClassConstant` | 16386 | the diagram constant for a class |

### What validates, and what does not

The oracle is `ValidateAIXML` against a negative control, and it is sharp: an invented class answers
`Property Node: Invalid property` with *"The type of the source is void"*, while a real one
complains only about the unwired `reference`. An invented method answers `Invoke Node: Invalid
method`. **The message does not name the offending node, so batch N candidates in one file and
COUNT the lines** — N failures rules out N names in one round trip.

| probed | result |
|---|---|
| `{LV.LVClassLibrary}` → `Name` | **accepted** |
| `{LV.LabVIEWClassControl}` → `Label` | **accepted** — returns a `Label Refnum`, so the class is real |
| `{LV.LVClassLibrary}` → `Add Item`, `Save` | **both real methods** |
| `{LV.LVClassLibrary}` → `Create Accessor VIs`, `Create Accessors`, `New Accessor VI`, `Add Accessor VI`, `Create Data Member Access VIs`, `Generate Accessors`, `New Member VI`, `Create Data Accessors` | **all 8 refused** |
| `{LV.LVClassLibrary}` → `Remove Item`, `Apply Changes`, `New VI`, `Create VI`, `Add VI`, `Get Items` | all 6 refused |

So **LabVIEW's "VI for Data Member Access" wizard is not exposed as a scripting method** under any
plausible name — it is IDE dialog code, not VI Server. A class can be *modified and saved* through
VI Server (`Add Item`, `Save`), which is more than was known, but the accessor VI itself still has
to come from somewhere.

### The link that is still missing

A `Class Specifier Constant`'s **class is not expressible in AIXML**. NI's own export writes it as
`<Constant _name="csc" type="ref{LV.ClassSpecifierConstant}" value=""/>` — the value is empty, and
which class it specifies was configured in the IDE. Every scripting call that needs a
`vi object class` therefore needs a constant AIXML cannot configure. That is the same shape as the
Timed Loop problem in `CLAUDE.md`: the construct can be created, not configured, and the escape
there was one IDE action per socket.

**Two untried routes remain, in order of promise:**

1. **`New VI Object` with `path`** pointing at the `.lvclass`, if `vi object class` can be satisfied
   another way — `To More Specific Class` takes a `target class` input, and `Open VI Object
   Reference` resolves a constant that already exists on the helper's own diagram, which is how NI's
   VI sidesteps it. A helper generated *once* with a correctly-configured constant would then be
   reusable for every class.
2. **Teaching pylabview to parse `LinkObjUDClassDDOToUDClassAPILink`** (§3), which would make the
   donor-clone route work.

## 5.1 `lvai_create_accessors` - verified against the wizard's own output

**It works, end to end, and the output is indistinguishable from the IDE's.** Ten accessors for
`Rennauto` (five fields, Read and Write each) on 2026-08-26: `ok: true`, `errorCode: 0`, all ten
saved beside the class and all ten registered as members. 74.8 s for the run.

The acceptance test was a pylabview diff of a tool-made accessor against a hand-made one of the same
field type - `Read Team Name.vi` against `Read Manufacturer.vi`:

| | hand-made by the wizard | made by the tool |
|---|---|---|
| front-panel heap DDOs | 2 `stdClust`, 1 `stdString`, **2 `udClassDDO`** | identical |
| connector pane pattern | `0x78` | `0x78` |
| `UDClassInst` refnums | 2 | 2 |
| `LIfp` / `LIvi` | present | present |
| `<Library>` | `Auto.lvclass` | `Rennauto.lvclass` |

Only the library name differs. Note also that `dynamicDispatch` reads `null` for all of them: neither
class file carries `NI.ClassItem.IsStaticMethod` at all, including the fourteen the user made by
hand, so that is the wizard's own behaviour and not a gap in the tool.

`Has Halo` is worth naming: a Boolean field, and `Auto` has no Boolean, so the clone route of the
previous section could never have produced it for want of a type-matched donor. The wizard does not
care.

Confirmed a second time on `Bus` the same afternoon: `ok: true`, ten accessors for
`Passenger Capacity`, `Standing Places`, `Route Number`, `Has Wheelchair Ramp` and
`Vehicle Length m`, all registered. The whole hierarchy then reads

| class | inherits | members | private data |
|---|---|---|---|
| `Auto` | `LabVIEW Object` | 14 (made in the IDE by hand) | 5 329 B |
| `Bus` | `Auto.lvclass` | **10 (this tool)** | 5 376 B |
| `Rennauto` | `Auto.lvclass` | **10 (this tool)** | 5 361 B |

`Read Has Wheelchair Ramp.vi` is the sharpest single piece of evidence that the wizard route was
necessary. Its front-panel heap is `1 stdBool, 2 stdClust, 2 udClassDDO` where the String accessor
carries `1 stdString` in that slot - the DDO class follows the FIELD TYPE. So a clone-and-patch route
would have to both create a `udClassDDO` and swap a `stdString` DDO for a `stdBool` one, and
pylabview does neither.

### LabVIEW GOES DOWN a few minutes after a run, and that is expected

**Predicted by the user before it was measured** - "I suspect that at the end, once everything is
generated, LabVIEW closes itself" - and the process watcher confirms it: a successful run at 13:34
was followed by `GONE` at 13:38:58, with no interaction in between.

The session log for that run carries **14 minidumps** and exactly one repeated signature:

```
source\ThEvent.cpp(213) : DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42.   (x15)
```

No `OMAutoClasses` fault this time, so this is a different mechanism from the validation crash in
`labview-crash-signatures.md`: an accumulating failure to destroy platform event objects, one
minidump each, and the process leaves a few minutes later. The three faulting sites seen across
sessions are all NI's - `VI generator.vi`, `Open project application ref.vi`, `ThEvent.cpp` - but the
run is what triggers them.

**Operationally: treat a restart of LabVIEW as part of the operation.** Verify the result
IMMEDIATELY after the call - `lvai_describe_class` reads the file and needs no LabVIEW, so the check
survives the shutdown - and expect to bring LabVIEW back before doing anything else. For a
once-per-class operation that is tolerable; it is not something to run in a loop over a large
hierarchy without restarting between classes.

### Are the helpers actually IN MEMORY? Measured, and the answer is no

The user pushed back on the restart: taking the VIs out of the project should be enough, and *you can
check what is still in memory*. That last part was the useful half - `{LV.Application}` carries
`Application:All VIs In Memory`, and `scripts/lvai_vis_in_memory.xml` reads it in both instances.

| instance | VIs held | what they are |
|---|---|---|
| the addon's (default, no application reference) | 404 | **every one** from `LV AI gRPC Service.lvlibp`; not a single loose VI |
| the IDE's (`Project:Active Project` then `Application`) | 37 | **all loose, and all class members** - `Auto.lvclass:Read …`, the `.ctl`s, nothing else |

**Neither helper is in the IDE's instance** - the instance the Project Explorer belongs to. So the
project item is a stale entry rather than a loaded VI, which is what the user suspected, and
`Close Reference` in the runner does release the target after all.

**One caveat that stops this being a complete answer.** The probe VI itself was running while it
measured and appears in *neither* list, so there is a third application instance neither query
reached. For the addon list that means absence proves nothing. For the IDE list it changes nothing:
that list is complete, readable, and contains no helper.

**And the class needed to act on it is real.** `{LV.ProjectItem}` is absent from all 154 catalogued
classes, but NI's own `CLSUIP_GetProjItemOfMemberVI.vi` exports as
`type="{LV.ProjectItem}"` with `fields="read+Name"` and a `target="Get All Descendents"`. So the
spelling is settled without guessing; what is still unknown is the method that takes an item out of
its project.

**Could the helper skip the project entirely? No - `Open VI Reference` refuses a `.lvclass`.** Worth
testing, because the project is what attaches the helper as an item AND what forces the
"project must be active" precondition: if the class reference came from the path, both problems
would vanish together. Measured 2026-08-26 with `scratchpad/probe_classref.xml`, which opens the
path and casts to `{LV.LVClassLibrary}`:

```
Error 1059 at Open VI Reference     VI Path: C:	emp\demoahrzeuge\Auto\Auto.lvclass
```

So a class reference is only reachable through `Project:Active Project` and
`CLSUIP_GetAllClassesInProject.vi`, the helper cannot avoid touching the project, and the adoption
follows from that rather than from anything avoidable.

**The NI forum confirms the approach; the installed code does not name the method.** The user
supplied
`forums.ni.com/t5/Example-Code/Programmatically-Delete-Item-from-Project-or-LVLIB-in-LabVIEW/ta-p/3498653`,
which describes exactly this - find the item by name in the project or library, then delete it
through Invoke and Property nodes - but the names live in an attached VI rather than in the page,
and a VI from a forum is not something to download onto this machine.

The installed provider tree was the better source and is now exhausted:

| VI | what it gave |
|---|---|
| `CLSUIP_CopyVIProjectItemHierarchy.vi` | `{LV.ProjectItem}` `target="AddItem"`, `read+Owned Items[]` |
| `CLSUIP_GetProjItemOfMemberVI.vi` | `{LV.ProjectItem}` `read+Name`; `{LV.LVClassLibrary}` `Get All Descendents` |
| `lvdesktop.llb/CDP_DeleteItems.vi` | interface only: `Project Ref` is `ref{LV.Project}`, items arrive as `array{string}` |
| `ZBUIP_Remove_Include_Item_Full.vi` | interface only |

**Provider diagrams do not export** - both delete-related VIs came back with a connector pane and no
nodes at all - so the method name cannot be read out of them. `CDP_DeleteItems.vi` taking a
`ref{LV.Project}` rather than an item reference is still a useful hint about where the method sits.

**A warning about aggregating exports.** Grepping every `target=` in the scratch directory produced a
list of twenty-odd methods on `{LV.LVClassLibrary}` and `{LV.ProjectItem}` that looked like a
discovery and was mostly this session's own discarded guesses sitting in probe files. Only two
entries in that list are NI's, and only because they were read from a named export. Attribute a
harvested name to the file it came from, or it is worth nothing.

**And it is now a one-liner, because a client round trip is too slow for this station.** The probe
needs one second of live service and could not get it: an MCP call after `--ensure-labview` was too
late, chaining `--validate` in the same shell run was too late, and pinning the port with `--port` was
too late as well. The service reports ready and is gone before the next process starts.

So `LabVIEWMCP --validate <file.xml>` exists now - it prints `errorCode 0` or `1`, reports an
unreachable service as `-1` instead of throwing, and loops over a directory of probe files. When the
addon behaves, the whole sweep is:

```
for f in cand/c*.xml; do LabVIEWMCP --validate "$f"; done
```

`c0` must answer `1`. Any candidate answering `0` is the method.

**The experiment is written and ready to run in seconds** - `scratchpad/cand/c0..c4.xml`, one
candidate per file so `errorCode` alone decides, `c0` being a nonsense method that proves the check
is live. Candidates: `RemoveItem`, `Remove From Project` and `Destroy` on `{LV.ProjectItem}`, and
`Remove Item` on `{LV.Project}`.

**Every probe of an uncatalogued class has cost a LabVIEW life.** Four attempts at the `RemoveItem`
question, four disappearances with the `OMAutoClasses` signature - including this one, which died
right after the `{LV.LVClassLibrary}` cast above ran successfully enough to return its error. The
probe file is written and waiting (`scratchpad/probe_removeitem.xml`, one candidate and one negative
control) but it has never reached LabVIEW alive.

**The method vocabulary is harvestable from NI's exports, and that needs no probing.** Exporting
provider VIs that touch project items and reading their `target=` and `fields=` attributes gives the
names verbatim:

| on | what NI uses | from |
|---|---|---|
| `{LV.ProjectItem}` | `target="AddItem"` - **no space** | `CLSUIP_CopyVIProjectItemHierarchy.vi` |
| `{LV.ProjectItem}` | `fields="read+Name"`, `fields="read+Owned Items[]"` | same, and `CLSUIP_GetProjItemOfMemberVI.vi` |
| `{LV.LVClassLibrary}` | `target="Get All Descendents"` | `CLSUIP_GetProjItemOfMemberVI.vi` |

`AddItem` settles the naming convention, so the removal counterpart is almost certainly `RemoveItem`
in the same style rather than `Remove From Project`. **That last step is still unverified** - the
probe for it needs a live LabVIEW and had not run when this was written.

**Finding that method costs a crash, and the crash happened while finding this out.** Probing means
validating AIXML that names uncatalogued VI Server classes, which is precisely the `OMAutoClasses`
trigger in `labview-crash-signatures.md` - and LabVIEW left again minutes after this measurement,
with that signature twice in the log. So the restart route stays the shipped one until someone is
willing to spend a few deliberate crashes on the probe.

### `--finish-project`: the end-of-job step that actually removes them

The user pressed the point twice, and rightly: BOTH helpers get adopted -
`lvai_create_accessors.vi` and `lvai_run_and_read.vi`, the runner too, since `Run VI` is called on
it. Leaving them in the project means leaving them in memory, which is the state every other
problem on this page grows out of.

```
LabVIEWMCP --finish-project <path.lvproj>
```

does the whole sequence and reports the resulting item list:

1. strip helper items from the `.lvproj` (idempotent; anchored on the URL pointing into the helpers
   directory, never on the name)
2. close LabVIEW - **this is the "Close"**, and the only one available
3. start it again and wait for the service
4. reopen the project, whose tree is now rebuilt from the stripped file

Measured end to end: 26 s, and the answer came back with `Auto.lvclass`, `Bus.lvclass`,
`Rennauto.lvclass`, `Dependencies`, `Build Specifications` and nothing else.

**It deliberately does not save the project**, which is the one part of the user's proposal that was
not implemented as asked. Saving it right after an accessor run killed LabVIEW once, and there is
nothing legitimate pending anyway: the classes are written by `Save All This Library` and the
`.lvproj` by `lvai_create_class`. The only thing a save would persist is the helper items - the very
thing being removed.

**A restart rather than a close, because a close is not reachable.** `lvai_close_vi` writes a
front-panel window's `State`, and a helper run through a VI reference has no window - Error 1149.
The catalogue has no unload method across 3 078 entries and no project-item class at all, so there
is nothing to invoke. What makes the restart sufficient is that the items live only in memory: the
file never carries them unless something saves it.

### LabVIEW adds every VI it generates to the active project, and that is the real mess

The helper item was only the visible half. Measured 2026-08-27 while running an additive load test -
forty rounds, each creating a new class - the Project Explorer filled up with

```
Load1-privatedata.vi   [Warning: has been deleted, renamed...]
Load2-privatedata.vi   [Warning: has been deleted, renamed...]
...
```

**Nothing in this repository listed those VIs.** `lvai_create_class` generates its scratch cluster VI
into `%TEMP%\LabVIEWMCP\classes\...` and deletes it straight after; it never touches the project
with it. LabVIEW puts every VI that `ConvertAIXMLToVI` produces while a project is ACTIVE into that
project's tree, and when the file then goes away the entry dangles.

So the rule is general and worth knowing before generating anything in bulk: **a generation is a
project modification.** Forty classes produced eighty stray rows - forty dangling `-privatedata.vi`
VIs and forty `Load<n>.lvclass` items whose directories had been removed.

`StripHelperItems` now handles both, and the second kind cannot be caught by name:

| kind | how it is recognised |
|---|---|
| our helper VIs | URL under `LabVIEWMCP/helpers` - the path, never the name, so a user VI called `lvai_something.vi` survives |
| anything dangling | the URL resolves to a file that is not there, whatever the item's Type |

The dangling check needs the project's own path, because a `URL` resolves against the FILE that
carries it treated as a directory - the leading `..` pops the `.lvproj`'s name rather than a
directory. Resolving it wrong would delete live items, so the check is skipped entirely when no
project path is passed.

**And a process lesson from the same run, which was worse than the mess it made.** The load test
reported `cls=false acc=false ec=1055` on round ONE - no active project - and then carried on for
forty more rounds doing nothing useful while polluting the tree. A loop that measures something must
stop when its own precondition fails; this one had no such check, and forty wasted rounds is the
cheap version of that mistake.

### Why the helper sticks in the project, and what cannot be done about it

The user saw `lvai_create_accessors.vi` as a top-level item in the Project Explorer and asked for it
to be gone at the end. Chasing it produced two measurements worth keeping.

**The item is real but in memory only.** A check of the `.lvproj` on disk called the project clean,
and that was true at the time: LabVIEW holds the item unsaved. Forcing a save wrote it out as
`URL="../../../../Users/…/Temp/LabVIEWMCP/helpers/lvai_create_accessors.vi"` - a path that dangles
on any other machine. So the on-disk strip is worth doing, and `lvai_create_accessors` does it.

**Evicting the helper from memory is NOT reachable through this interface.** That is the root cause
rather than the symptom, so it was worth an attempt, and the attempt failed for an instructive
reason. `lvai_run_and_read.vi` opens the helper by path and runs it through a VI reference; a VI run
that way never gets a front-panel WINDOW. `lvai_close_vi` works by writing
`Front Panel Window:State` = `Closed`, so it answers **Error 1149** - no window to close.

Measured on a helper the project HAD adopted, so the usual suspect is ruled out: this is not the
membership precondition failing. And the VI Server catalogue carries no unload method across its
3 078 entries. What remains is closing the project **discarding changes**, or restarting LabVIEW.

`lvai_close_vi`'s hint used to fold 1149 into the membership failure and now reports its own cause.

**`Run VI` takes TWO parameters and the runner was wiring one.** The user asked whether the method's
own documentation had been taken into account; it had not. The harvested catalogue is unambiguous and
needed no web page:

```
{LV.VI}  |  Run VI  |  Wait until done; Auto Dispose Ref
```

`Auto Dispose Ref` was left at its default. It **must** be false here, because `Ctrl Val.Get All`
reads through the same reference AFTER the run and a disposed reference would take the values with
it - so the default happened to be right. It is now wired explicitly anyway, with a diagram comment
saying why, because relying on an unstated default is the same shape of mistake as setting
`readLines` without `count`.

**It is not the leak.** Measured immediately after: one accessor run plus two helper generations
produced **9** `DestroyPlatformEvent` failures, against roughly 12.7 per run before the change. The
operation mixes differ too much for that to be a real reduction, and the failures are still there.
Worth recording as a ruled-out hypothesis rather than a fix.

**The `Open VI Reference` in the runner takes no `options`, and that is the open question.** Opening
by path with default options returns a reference to whatever is already in memory, so the run uses
the loaded copy rather than the file - which is also the "two versions in memory" trap. Whether an
options bit avoids the association is unmeasured, and the value was NOT guessed: `0x08`
("prepare for reentrant run") was proposed and rejected by the user, the NI page for the function
renders as a navigation shell with no content, and `glang.chm`'s pages are compressed beyond a
`grep`. It needs the value read off Context Help.

### Building a big class in slices, and four traps on the way there

`lvai_create_accessors` takes `fromField` and `fieldCount`, because one call for a 7-field class
does not fit an MCP client's patience. The
`field count` answer is the WHOLE cluster and `fields in this call` is the slice, so a caller can see
how many calls are still needed.

**The per-field cost is not constant, and the first measurement of it was misleading.** 11.1 s per
field was measured on an empty class and read as a flat rate - "so 4 fields is already marginal and 3
is comfortable", which this section said until 2026-08-27. It grows, because `Save All This Library`
checks the whole library on every field:

| members in the class when the field is built | cost of that field |
|---|---|
| 0 | ~11.5 s |
| 6 | ~20 s |
| 12 | >30 s |

So against a client that gives up near 60 s, **three fields fit the FIRST call and two fit any of
them** - which is why `fieldCount` defaults to 2 rather than 3. A 7-field class is `fromField` 0 with
3, then 3 and 5 with 2. `Array Subset` clamps a length past the end, so a bigger number is harmless
in itself; it may just not come back in time.

**Do not compute the next offset by hand.** Every answer carries `membersBefore`, `membersAfter` and
`nextFromField`, all read off the `.lvclass` FILE rather than from the run - so they are true whether
or not the answer arrived, and `nextFromField == fieldCount` is how a caller knows the class is
finished. On a timeout the recovery is the same call with `fromField` = `memberCount / 2` from
`lvai_describe_class`, never a retry of the slice that timed out.

**The client's ceiling is EXACTLY 60 s, and it is not raisable from `.claude/settings.json`.**
This was written as "a client that gives up near 60 s" for weeks, from inference. Measured 2026-08-27
off the client's own MCP log at
`%LOCALAPPDATA%\claude-cli-nodejs\Cache\<project>\mcp-logs-labview\*.jsonl`:

```
Tool 'lvai_create_accessors' failed after 60s: Error: Request timed out
```

**28 aborts in that log, every one of them at 60 s** - 26 reading `60s`, 2 reading `61s`, which is
rounding - across nine different tools. No other value ever appears, so it is a fixed ceiling rather
than a load-dependent one. Three things follow, and each rules out a suspect:

- **It is not ours.** `lvai_create_accessors` carries `timeoutSeconds = 600`, and a deadline of ours
  surfaces as JSON with `errorKind: "rpc"`, `DeadlineExceeded` and a hint. The bare
  `Error: Request timed out` is the client's wording, in the client's log.
- **Raising it in project settings does not work.** `"env": {"MCP_TOOL_TIMEOUT": "600000"}` in
  `.claude/settings.json` reaches the process - it is visible in the environment of a tool subprocess -
  and aborts still land at 60 s in the same session afterwards. The likely mechanism, stated as a
  hypothesis rather than a finding: the `env` block is applied to tool subprocesses while the client's
  own timeout logic never reads it. The client's bundle is not readable from here, so this is where
  the evidence stops.
- **Server start-up is a separate timer** and also unmoved: the log opens each connection with
  `Starting connection with timeout of 30000ms`.

So slicing is the fix, not a workaround for a mistake of ours: the ceiling cannot be lifted from this
side, therefore a call has to fit under it. That is what `fieldCount = 2` buys.

**While a timed-out call may still be running, poll the CLASS FILE, not LabVIEW.** Firing the next
slice into a helper that has not finished is the concurrent-access case that returns
`errorCode -1 unreachable`, and the class file settles at the true count within seconds of the helper
finishing - six samples ten seconds apart is enough to see it stop moving. Two readings that look
alarming during that window are not:

- **`Get-Process LabVIEW` reporting `Responding = False`.** The UI thread is not pumping messages,
  which a long scripting operation does routinely. `lvai_status` answering normally at the same moment
  is what says LabVIEW is working rather than wedged - measured on round 4, where `Responding` was
  back to `True` thirty seconds later and the next slice ran in full.
- **A member count lower than the slice asked for.** Read it again. Round 4's timeout showed 4
  members immediately and 6 a few seconds later; the helper had finished all three fields.

**Verified end to end over MCP alone: five cold rounds, 7 slices each, 34 of 35 answered** - one
timeout in round 4, recovered from `memberCount / 2` with no duplicate and no gap, and every round
ending on the same numbers -
`Auto` 14 VIs and 14 members, `Bus` and `Rennauto` 10 and 10, zero `" 2"` duplicates, zero VI items
left in the `.lvproj`, zero dangling members, LabVIEW up after every round. `docs/labview-crash-signatures.md` has the run
table and, more usefully, why round 3's single minidump and two `DestroyPlatformEvent` warnings are
the helper's own cross-context refnum cleanup rather than a fault.

**The library is saved after every field**, not once at the end. Before that, two client timeouts
each left 12 of 14 accessor VIs on disk with **0** registered in the `.lvclass` - files no class file
mentioned. After it, the same interruption left 12 on disk and **12 registered**: a smaller class,
not a broken one.

Getting there cost four mistakes worth writing down, because each one looked like the feature not
working:

| what happened | actual cause |
|---|---|
| asked for 4 fields, got 6 | the new CONTROL was called `field count` and an INDICATOR already had that name. `Ctrl Val.Set` reached the indicator, the control kept its default. **A control and an indicator may not share a name.** Renamed to `take fields` |
| asked for 2 fields, got 4 | `virtual folder` was empty and sat BEFORE the new inputs. The runner joins names and values with newlines and pairs them BY POSITION, and an empty value does not survive the helper's split - so every later input landed on the wrong control. Latent for as long as the empty value happened to be LAST |
| asked for 4 fields, got 14 | the MCP tool SCHEMA in the session predated the new parameters, so `fieldCount` was dropped before it reached the server and the default 1000000 applied. A new parameter needs a client restart, exactly like a new tool |
| a reset class produced `Read Manufacturer 2.vi` | deleting the VIs on disk does not remove them from LabVIEW's memory. The wizard still saw the name taken. **Stop LabVIEW before resetting**, or the next run suffixes everything |

The one that took longest was the third, and the discriminator was worth the trouble: the same inputs
passed straight to `lvai_run_vi_and_read_values` honoured the slice (`fields in this call` = 1) while
the tool call ignored it. Same helper, same names - so the fault had to be above the helper, in the
argument binding.

### Round 6: two traps a green run never shows

Six cold rounds in, the procedure was believed settled. Round 6 produced the same 34 VIs and 34
members as the five before it - and exposed two faults that every earlier round had been quietly
walking past, because a passing result hides them.

**`classPathsSeen: []` means NO ACTIVE PROJECT, and that is a different fault from a misspelled
path.** After the timeout in round 6 the next call answered:

```
classIndex: -1,  classPathsSeen: [],  membersBefore: 6,  nextFromField: 3
```

`classPathsSeen` is the diagnosis, not `classIndex`. An EMPTY list means
`CLSUIP_GetAllClassesInProject.vi` found no classes at all - so no project is active - while a
POPULATED list means the path is misspelled. The two need opposite fixes, and the hint used to give
the path advice for both, which sends the reader hunting a spelling that is correct.

**It is NOT the timeout that causes it, and round 6's conclusion that it was is withdrawn here.**
That round reasoned: the client aborts at 60 s, LabVIEW kills the helper mid-run, the helper's three
reference closes sit at the END of the diagram, so the project reference is never released and the
project stops being active. Plausible, structural, and wrong - **round 7 timed out too and the next
call went straight through**, `classIndex: 2`, no re-open needed.

What actually differed is the round, not the timeout. **In round 6 the project had never been properly
opened at all**: every `lvai_open_file` call that round was malformed (see below), so the only thing
that ever loaded the project was `lvai_describe_project`, a READ tool. That is enough to read a
project and not enough to leave one active - so when the aborted helper let go, nothing held it. In
round 7 the project was opened through `lvai_open_file(projectPath, projectName)` and it survived the
abort untouched.

So the rule is about how the project was opened, not about what went wrong afterwards:

| how the project got into LabVIEW | survives an aborted helper run? |
|---|---|
| `lvai_open_file` with `projectPath` **and** `projectName` | **yes** - measured, round 7 |
| loaded incidentally by a read tool such as `lvai_describe_project` | no - measured, round 6 |

And the recovery is the same either way, because it is cheap and idempotent: **re-open the .lvproj,
then resume from `nextFromField`.** What changed is that re-opening is a fix for a project that was
never open, not a ritual to perform after every timeout.

This is the fifth time in this repository that one measurement has been generalised into a mechanism
and the next measurement has refused it. The tell was available and ignored: round 6 had a second,
unrelated fault running at the same time, so nothing that round should have been attributed to a
single cause.

**And the first slice after a LabVIEW restart is the slow one.** The 60 s abort in round 6 hit
`fromField 0, fieldCount 3` on an EMPTY class - the cheapest slice by the cost curve above, about
35 s warm. Cold it exceeded 60 s, because `MemberVICreation.lvlib` and `lv_icon.lvlibp` load on first
touch. So the cost curve has a second variable: **`fieldCount` 3 is safe only when the wizard is
already warm.** After a restart, make the first call a 2, or accept that the first one may time out
and be resumed. Its work is not lost - the poll showed 6 VIs and 6 members ten seconds later.

**`lvai_open_file` has no `filePath` parameter, and the failure lies about it.** Three calls of
`lvai_open_file(filePath: "…\Fahrzeuge.lvproj")` returned:

```
Error 7 occurred at ... OpenFile.vi
LabVIEW: (Hex 0x7) File not found.
```

for a file that exists, while `lvai_describe_project` read the very same path string with
`errorCode 0`. The cause is the server's own near-miss argument folding, which is a help for
`vi_path` -> `viPath` and a trap here: `filePath` folds onto **`viPath`**, so LabVIEW was asked to
open a `.lvproj` as a VI and correctly reported no VI there. `projectPath` **with** `projectName`
returned `No Error` immediately.

The diagnosis went to the disk, then the XML, then `.lvproj` URL resolution, before reaching the
argument name - so the tool now refuses a `.lvproj` in `viPath` (and a `.vi` in `projectPath`) before
the call leaves, and names the parameter to use. Both guards are tested.

### Descriptions yes, icons already correct

**The accessors get a VI description, written inside the loop.** `write+VI Description` on the same
property node that reads the name - the reference is in hand there, which is the cheap moment. Read
and Write get different text. Worth noting because the catalogue disagrees:
`docs/vi-server-properties.tsv` lists `{LV.VI}  VI Description` as **read**, and the write validates
and works. The TSV's access column is not authoritative.

**The icon is NOT missing, and this was nearly "fixed" on a false premise.** A tool-made
`Read Manufacturer.vi` and a wizard-made one have byte-identical `ICON.png` - md5 `2a24083b5bdc1226`,
915 bytes both. The wizard already draws it: `CLSUIP_OverlayRWOnIcon.vi`,
`CLSUIP_InitializeClassIcon.vi` and `CLSUIP_AddIconBorderIfNeccessary.vi` are all in the provider.

It *looks* empty because the R/W overlay sits on an **empty class icon**: this document's own recipe
says "No `NI.Lib.Icon` ... is needed" for a class to load, and `lvai_create_class` writes none. So the
gap is one level up - give the CLASS an icon and every accessor inherits it - and it belongs to
`lvai_create_class`, not here. Neither the hand-made accessors nor the generated ones carry a
description either, so that gap was real and is now closed; the icon gap was misattributed.

### Four traps, each of which cost a run

**1. The path match must be case-insensitive.** LabVIEW reported
`C:\Temp\demoahrzeuge\Rennauto\Rennauto.lvclass` while its two siblings came back as
`C:	emp\...`, and `Search 1D Array` is case-sensitive, so the lookup missed a class that was
plainly there. Both sides are upper-cased before the search now. The answer reports
`classPathsSeen` whenever the lookup fails, which is what made this visible at all - a bare
`classIndex: -1` is undiagnosable.

**2. `VI Path` on a freshly created VI is `<Not A Path>`.** So the save path cannot come from it.
It comes from `VI Name` instead - `Rennauto.lvclass:Read Power hp.vi` - with `Match Pattern` on a
greedy `.*A` taking everything after the LAST colon, which also handles a library-owned class
(`Lib.lvlib:Class.lvclass:Read X.vi`). Deriving it from `Strip Path` of `VI Path` gave
`Save:Instrument` an invalid path and it cancelled with error 43.

**3. Save each accessor INSIDE the loop, while its reference is still held.** The wizard leaves a
new VI in memory with no path; once the reference is gone nothing can place it, and
`Save All This Library` then fails with 43 while a project save gives 1019.

**4. Never run it twice without clearing memory.** An earlier failed run leaves accessors in memory
unsaved and unnamed. The next run then produces `Read Power hp 2.vi` - LabVIEW disambiguating
against the orphan - and `Save All This Library` trips over the orphan with error 43, so the class
file never gets its member list even though all ten VIs reached disk. Restart LabVIEW or close the
project, delete the half-made VIs, run once. This is the general rule the user stated: **close a VI
you created before creating or editing it again**, or two versions compete in memory.

### The accessor wizard IS callable - it is provider code, not IDE-only

Asked on 2026-08-26 whether the project provider carries the function, which is the right question
and turns out to be yes. The wizard behind "New >> VI for Data Member Access" is ordinary G code
under

```
resource/Framework/Providers/LVClassLibrary/NewAccessors/
```

**The entry point is `CLSUIP_CreateNewAccessor.vi`.** Its bundle names exactly four callees -
`Can Script Property Accessor.vi`, `CreateBaseScripter.vi`, `ScriptAccessorVIs.vi`, `GetVIRefs.vi` -
which are the four PUBLIC members of `AccessorCreation.lvlib:BaseAccessorScripter.lvclass`. So that
one VI is the whole wizard body, and it reads `Protected="0"`: not password protected, therefore
callable.

Calling the entry point rather than `ScriptAccessorVIs.vi` directly is the better move, and the
reason is structural: the scripter class object (`BaseAccessorScripter in`/`out`) is wired INSIDE
`CLSUIP_CreateNewAccessor.vi`. A caller never touches a class wire, so a helper VI is a single
`Call` node - which matters because AIXML cannot author a class terminal at all.

Its inputs, read out of the VCTP with pylabview (no LabVIEW needed):

| terminal | type | note |
|---|---|---|
| `LVClassLibrary Ref` | Refnum `LVObjCtl` `0x0055` | the `.lvclass` |
| `PDC Item Refnum` | Refnum `LVObjCtl` | the private data control's project item |
| `Ctl Refnum` | Refnum `LVObjCtl` `0x0006` | the field to accessorise |
| `VI Type` | enum `UnitUInt32` | **`Dynamic` = 0, `Static` = 1** |
| `Virtual Folder Name` | String | |
| `Create as Properties` | Boolean | |
| `Make available through Property Nodes` | Boolean | |
| `Include error terminals` | Boolean | |
| `Make Class Terminals Dynamic` | Boolean | |

and it returns `Read VI` and `Write VI` as VI refnums plus `error out`.

**What is still unmeasured is how to OBTAIN those three refnums.** The VI Server catalogue has no
library class and no project-item rows at all - `grep`ped both TSVs - which is the same hole
`{LV.Project}` sat in before it was probed. The technique that closed that one applies here:
`ValidateAIXML` accepts a real method name and rejects an invented one, so a throwaway AIXML file
settles a name for the cost of one call. That probe needs LabVIEW and the gRPC service up, so it is
the next step rather than a finished one.

This route is worth preferring over anything in this document that rebuilds an accessor by hand: the
VIs are produced by LabVIEW's own scripter, so they are correct by construction - no `VITS` writer,
no `PTH0` length arithmetic, no unverifiable heap edit.

### 5.0 Use `lvai_create_accessors`

Everything in 5.1 is what that tool does. Call it instead of following the steps by hand:

```
lvai_create_accessors  lvclassPath=…\Rennauto\Rennauto.lvclass
                       [dynamicDispatch=true] [accessUi=R/W] [includeErrorTerminals=true]
```

CLI form `--create-accessors <path.lvclass> [--static] [--access Read|Write|R/W]`, which needs an
active project in the IDE like the tool does - the class reference is found among
`Project:Active Project`'s classes, so a headless run against a closed project answers
`classIndex` -1 rather than working. Read the result back with `lvai_describe_class`.

### 5.1 The working recipe, end to end

Measured 2026-08-26 on `Rennauto.lvclass`: **ten accessors for five fields, created and saved from
generated helpers, structurally identical to ten the IDE wizard had made for the parent class.**
The acceptance test was a pylabview diff of one against the other:

| | IDE wizard, `Auto/Read Manufacturer.vi` | this recipe, `Rennauto/Read Power hp.vi` |
|---|---|---|
| FP heap DDOs | 2 `stdClust`, 2 `udClassDDO`, 1 `stdString` | 2 `stdClust`, 2 `udClassDDO`, 1 **`stdNum`** |
| class refnums | `Auto in` / `Auto out` | `Rennauto in` / `Rennauto out` |
| `<Library>` | `Auto.lvclass` | `Rennauto.lvclass` |
| conpane pattern | `0x78` | `0x78` |
| `.lvclass` item block | `ExecutionSystem`, `Flags`, `InvokeUsage`, `MethodScope` = 1 | identical |

`stdNum` against `stdString` is the only difference and it is the correct one - `Power hp` is a
Double where `Manufacturer` is a String. **Two `udClassDDO`s**, which is the heap object the
pylabview route cannot create.

**Step 1 - get the class reference.** The catalogue has no library class, so the route is NI's own
provider VI, called by bare name:

```xml
<Node _name="Property Node" fields="read+Project\3AActive Project" type="{LV.Application}"
      outputs="Project\3AActive Project:20.proj,reference out:20.reference out,error out:20.error out"
      uid="20" uid_parent="root"/>
<Node _name="Build Array" inputs="element:20.proj"
      outputs="appended array:50.appended array" uid="50" uid_parent="root"/>
<Call target="CLSUIP_GetAllClassesInProject.vi"
      inputs="Project Refs:50.appended array,error in (no error):20.error out"
      outputs="All Classes:52.All Classes,error out:52.error out" uid="52" uid_parent="root"/>
```

`All Classes` is `array{ref{LV.LVClassLibrary}}` in project order - on this project `Auto`, `Bus`,
`Rennauto`. Read each one's `Path` with a `{LV.LVClassLibrary}` property node to identify it.

**Step 2 - get a control reference for the field.** `CLSUIP_GetPDCCluster.vi` is the direct route
and is **`private` to `MemberVICreation.lvlib`** - an outside caller is refused at validation with
"This VI cannot access the referenced item in private scope". So open the private data control at
its **synthetic path** (`…\Rennauto.lvclass\Rennauto.ctl`, which does not exist on disk) through the
IDE's application instance, then `Front Panel` -> `{LV.Panel}` `Controls[]` -> element 0 is the
cluster -> `{LV.Cluster}` `Controls[]` -> element *i* is the field.

**A downcast is required in the middle and its absence reads as a wrong property name.**
`{LV.Panel}` `Controls[]` yields generic control refnums; a `{LV.Cluster}` property node on one
fails with `Property Node: Invalid property`, which sends you hunting through the catalogue for a
misspelling that is not there. The fix is `To More Specific Class` with a `ref{LV.Cluster}` constant:

```xml
<Constant type="ref{LV.Cluster}" value="" outputs="value:33.value" uid="33" uid_parent="root"/>
<Node _name="To More Specific Class"
      inputs="reference:36.element,error in:32.error out,target class:33.value"
      outputs="specific class reference:37.specific class reference,error out:37.error out"
      uid="37" uid_parent="root"/>
```

Note `error in`, not `error in (no error)`, on that node.

**`Controls[]` order equals the field order in the `.ctl`** - measured, all five: index 0 to 4 gave
`Power hp`, `Team Name`, `Start Number`, `Has Halo`, `Zero To Hundred s`, exactly as
`pylv_extract` of the decoded private data control listed them. So a field can be addressed by
literal index and the returned VI names verify it afterwards, which is cheaper than matching labels
on the diagram - and label matching is not available anyway, because `{LV.Control}`
`read+Label\3AText` is refused as an invalid property.

**Step 3 - call the wizard.** `Access UI` = `R/W` creates BOTH accessors in one call, so five calls
cover ten VIs:

```xml
<Constant _name="Dynamic" type="uint32{Dynamic,Static}" value="0" outputs="value:60.value" uid="60" uid_parent="root"/>
<Constant _name="R/W" type="uint16{Read,Write,R/W}" value="2" outputs="value:61.value" uid="61" uid_parent="root"/>
<Call target="MemberVICreation.lvlib\3ACLSUIP_CreateNewAccessor.vi"
      inputs="VI Type:60.value,Access UI:61.value,error in (no error):56.error out,
              Include error terminals:62.value,Virtual Folder Name:64.value,
              Make available through Property Nodes:63.value,
              PDC Item Refnum:43.element,LVClassLibrary Ref:55.element"
      outputs="error out:70.error out,Read VI:70.Read VI,Write VI:70.Write VI"
      uid="70" uid_parent="root"/>
```

Chain the calls through `error out` so they run in order, and read `VI Name` off each returned
reference - that is the verification that a field index meant what you thought.

**Step 4 - SAVE, and this is the step that costs a session if it is missed.** The wizard leaves the
new VIs **in memory with no path**, and it hands the caller references it expects to be saved
immediately. Once those references are gone:

| what you try | what you get |
|---|---|
| `Edit LVLibs.lvlib:Save All This Library.vi` | **error 43**, operation cancelled - `Save:Instrument` wants to prompt for a path and cannot |
| `lvai_close_active_project` (project `Save`) | **error 1019** on the Invoke Node |
| `Open VI Reference` by qualified name, no app reference | **error 1004**, "a path must be wired for the VI Path input" |

The way through is to reopen each VI **by its qualified name** - `Rennauto.lvclass\3ARead Power
hp.vi` - with the IDE's application reference wired to `application reference (local)`, then
`Save.Instrument` with a path built the way NI's own provider builds it: the class's `Path`,
`Strip Path` for the directory, `Build Path` with the file name. `Save All This Library.vi` then
succeeds and writes the member list into the `.lvclass`; its `Library in` is `ref{LV.Library}`, so
widen the class reference with `To More Generic Class` first.

**Three catalogue lessons worth carrying past this task.** `docs/vi-server-properties.tsv` is a
harvest of the VI Server class list, and presence there does not mean AIXML will bind it:

- `{LV.LVClassLibrary}` and `{LV.Project}` have **zero rows** and both work.
- `{LV.Cluster}` `Controls[]` **has** a row and fails - until the refnum is downcast.
- `{LV.Control}` `Label` has a row; `Label\3AText` is not a property, and there is no `{LV.Label}`
  class at all.

When a property node is refused, the cached AIXML exports under
`%USERPROFILE%\.labviewmcp\cache\aixml` are the better authority than the TSV: grep them for the
class and see what real NI code reads off it. That is how `All Objects[]` and the downcast were
found.

### Can pylabview generate them instead? No - and the reason is one heap object

Asked directly on 2026-08-26, because the earlier answer only covered the *donor rename* route.
The idea worth testing was a type substitution rather than a rename: let AIXML author the whole
accessor with the terminal typed as the private data CLUSTER - then `Unbundle By Name` validates and
wires correctly, and inside a member VI unbundling a class wire yields exactly those fields in
exactly that order - and afterwards rewrite the VCTP `TypeDesc` from `Cluster` to
`Refnum`/`UDClassInst`. In place, no composition.

**It fails on the front-panel heap.** Extracted from a real dynamic dispatch member
(`vi.lib/addons/_JKI.lib/.../Lexer/Write String.vi`), a class terminal is not a cluster wearing a
different type:

```
<ddo class="udClassDDO">      <- the class terminal, twice (in and out)
<ddo class="stdClust">        <- ordinary clusters, also twice
<ddo class="stdString">
```

`udClassDDO` is its own DDO class. An AIXML-generated VI only ever contains `stdClust`, `stdString`,
`stdNum` and friends, because AIXML refuses to author a class terminal at all - so turning one into
the other is not a field edit, it is inserting a heap object of a class pylabview never writes from
scratch. On top of that the DDO is bound to the class by `LIfp`/`LIvi`, the two blocks pylabview
cannot parse.

Two smaller facts from the same measurement, both worth keeping:

- **`<Execution DynamicDispatch="0">` is NOT the dynamic dispatch marker.** That VI reads `0` while
  its class records `NI.ClassItem.IsStaticMethod = false`. Dynamic dispatch lives in the `.lvclass`
  and in the terminal being a class type, not in a VI flag.
- The donor's diagram heap holds only `fPTerm` and `signal` objects - no `Bundle By Name` at all. So
  even a same-class clone of that VI is not an accessor: the node would have to be added, and adding
  a node is the one thing pylabview does not do.

**Seed-and-clone was then measured on a real seed, and it splits in two.** The user built all 14
accessors of `Auto` in the IDE on 2026-08-26, which is the donor the earlier note said was missing.

**Same class, different field: yes, and it is trivial.** `Read Manufacturer.vi` against
`Read Model.vi` - same class, same String type, differing only in which field - diff to *eleven
integers*. The field selection is nothing but a `TypeID` reference: `Manufacturer` is FlatTypeID 0,
`Model` is 1, and the VCTP is byte-identical between the two because it lists every field of the
class either way. Everything else that differs is two hash attributes (`Instrument/@Signature`,
`Unknown/@Field50Hash`, `@Field78Hash`).

One structural fact makes this safe, and it is the useful one: **the accessed field is the only slot
the front-panel heap references.** The other six field TypeDescs appear solely as cluster members, so
they can be relabelled and retyped freely - it is the accessed slot whose type must keep matching its
DDO (`stdString` for a String, `stdNum` for a numeric). So a donor must be picked BY TYPE, never by
convenience.

**Different class: no.** The class identity is not six text sites, it is four blocks:

| file | `Auto` | field name | editable? |
|---|---|---|---|
| main XML | 10x | 21x | yes, text |
| `_FPHb.xml` | 2x | 1x | yes, text |
| `_LIfp.bin` | 2x | - | `PTH0`, three length fields per site |
| `_LIvi.bin` | 4x | - | `PTH0`, same |
| `_VITS.bin` | 1x | 1x | **no** |

`PTH0` turned out readable from one sample - `u32` length, `u16` type, `u16` component count, then
`u8`-length-prefixed components, so `Auto.lvclass` (`0c`) to `Rennauto.lvclass` (`10`) is three
numbers. `VITS` is what stops it. Those 4 659 bytes carry a SECOND COMPLETE COPY of the type
definition - `000a "Mileage km"` under a `u16` length, `04 "Auto"` under a `u8`, then `0007` and the
field index list `0..6`. Retargeting to a class with five differently-typed fields means re-authoring
that structure and every enclosing length, in the one block pylabview fails to parse on nearly every
file and therefore copies verbatim. That is not a surgical edit; it is implementing a variant encoder
against no specification.

So the boundary is sharper than "pylabview cannot compose": **a same-class field change is eleven
integers, and a cross-class clone needs a `VITS` writer.** Accessors for a derived class's OWN fields
are always the cross-class case, because the parent's accessors are inherited already - which is
exactly the case that does not work.

**What remains is the seed-and-clone route.** Cloning WITHIN one class needs no edit to `LIfp`/`LIvi`
and no new DDO, because both are already correct and are copied through verbatim - the blocker was
only ever *changing* the class. So one real accessor per class, made once in the IDE, could be cloned
into the rest by changing three things: the file name, which field `Unbundle By Name` selects, and
the field terminal's type and label. Whether the field selection is editable is **not measured** -
it needs a genuine accessor donor, and no NI class on this station had one.

**Superseded 2026-08-26: the wizard is now driven from a generated helper, end to end.** The
recipe is in section 5.1 below; ten accessors for a five-field class came out structurally identical
to ten the IDE wizard had produced for its parent. Clicking is no longer the fastest correct route,
it is only the fallback when no project is active.

## 4. A JSON-valued argument was unreachable, and it was not a schema defect

`lvai_run_vi_and_read_values` declares `inputsJson` as a string, and its own description shows the
value as a JSON **object** — `{"file name":"C:\\data\\in.csv"}`. A client that sends what it was
shown is refused by the binder:

```
The JSON value could not be converted to System.String. Path: $ | LineNumber: 0
```

`lvai_aixml_reference`'s `section` fails identically when given a heading *number* rather than a
quoted one. This first read as a schema defect — the parameter looked untyped — and it is not: the
schema does say `string or null`, which `ToolArgumentsTests` has asserted since 2026-08-14. The
client simply does not validate against it, so the value arrives as an object and the binder is
strict about it. Nothing the schema could say would prevent that.

**Fixed 2026-08-26 in the diagnostics wrapper**, which is the only place that can: a failure whose
message names `System.String` is retried once with every non-string argument replaced by its own
JSON text. `inputsJson` then parses, `section: 14` reaches the lookup as `"14"`, and a value a tool
was happy to receive is never reshaped, because the fold only runs after a failure. The workaround
this replaces was to bake the value into the helper VI. See `tool-argument-errors.md` for the rest
of the machinery.
