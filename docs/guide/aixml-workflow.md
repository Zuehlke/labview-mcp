# The AIXML workflow

How a VI is actually generated and changed, end to end. This page is the tutorial. The format
itself is specified in [aixml-reference.md](../aixml-reference.md), which `lvai_aixml_reference`
serves to an assistant at run time.

## The AIXML loop

AIXML is LabVIEW's textual block-diagram format — nodes with a `uid`, wires expressed as
`terminal:uid.terminal` references in `inputs`/`outputs`:

```xml
<Control _name="X" outputs="value:1306.value" type="int32" uid="1306" value="1"/>
<Node _name="Add" inputs="x:1306.value,y:1274.value" outputs="x+y:143.x+y" uid="143"/>
```

There is **no XSD anywhere in the install**, so the rules were derived empirically and written
down: [`docs/aixml-reference.md`](../aixml-reference.md), served by `lvai_aixml_reference`.
Read it before authoring AIXML — two of its rules fail silently. A `uid.terminal` string names
a **net**, not a pointer to an element, and terminal names are literal LabVIEW labels that must
be looked up rather than guessed (`Increment` → `x+1`, but `Greater?` → `x > y?`, with spaces).

The working loop:

1. `lvai_aixml_reference` → the rules, and the verified terminal-name table
2. `lvai_convert_vi_to_aixml` on a VI that already resembles the target → study the dialect
3. edit the XML
4. `lvai_validate_aixml` — the cheap failure path, always do this
5. `lvai_convert_aixml_to_vi` to a scratch path — `lvai_apply_aixml_to_vi` does **not** work and
   is refused by default, see [safety.md](safety.md#caveats)
6. `--diagram` on the result — AIXML has no coordinates, so LabVIEW picks the whole layout and
   looking is the only way to know what you got

[`docs/dqmh-patterns.md`](../dqmh-patterns.md) (served by `lvai_dqmh_reference`) does the
same for DQMH modules: the framework inventory, the two-loop `Main.vi`, the request/broadcast
VI internals, and what cannot be generated.

## Creating a project

The format itself — every element, attribute, item type, property scope, the containment grammar
and the build-specification vocabulary — is written up in
[`docs/lvproj-structure.md`](../lvproj-structure.md), derived by census over 65 production
`.lvproj` files. Read that before generating anything larger than the blank project below — it is
embedded in the assembly and served by `lvai_lvproj_reference`.

Libraries and classes are a separate format, written up the same way in
[`docs/lvlib-lvclass-structure.md`](../lvlib-lvclass-structure.md) (census over 318 `.lvlib` and
`.lvclass` files). It answers the two questions the gRPC interface cannot: **which members are
public**, and **which class derives from which** — `describe_project` reports `vis`, `libraries`
and `classes` but has no field for either.

**No RPC creates one.** The 23 RPCs act on VIs, and on projects that already exist:
`ConvertAIXMLToVI` writes a `.vi`, `OpenFile` opens a path that has to be there already, and
nothing writes a `.lvproj`. A new project is therefore made by writing the XML yourself and then
making LabVIEW confirm it:

1. Write the file (skeleton below) to the target path.
2. `lvai_open_file` with `projectPath` + `projectName` — `errorCode 0` means LabVIEW parsed it.
3. `lvai_describe_project` — the check that actually matters. `OpenFile` reports on *opening*;
   `describe_project` reports on *content*, so it is what catches a file that parses while
   saying the wrong thing. A blank project answers with one `My Computer` target and empty
   `vis`, `libraries`, `buildSpecifications` and `missingFiles`.

Verified end to end against a live LabVIEW 2026 (26.3f0) — this exact file loads clean:

```xml
<?xml version='1.0' encoding='UTF-8'?>
<Project Type="Project" LVVersion="26008000">
	<Property Name="NI.LV.All.SourceOnly" Type="Bool">false</Property>
	<Property Name="NI.Project.Description" Type="Str"></Property>
	<Item Name="My Computer" Type="My Computer">
		<Property Name="IOScan.Faults" Type="Str"></Property>
		<Property Name="IOScan.NetVarPeriod" Type="UInt">100</Property>
		<Property Name="IOScan.NetWatchdogEnabled" Type="Bool">false</Property>
		<Property Name="IOScan.Period" Type="UInt">10000</Property>
		<Property Name="IOScan.PowerupMode" Type="UInt">0</Property>
		<Property Name="IOScan.Priority" Type="UInt">9</Property>
		<Property Name="IOScan.ReportModeConflict" Type="Bool">true</Property>
		<Property Name="IOScan.StartEngineOnDeploy" Type="Bool">false</Property>
		<Property Name="server.app.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.control.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.tcp.enabled" Type="Bool">false</Property>
		<Property Name="server.tcp.port" Type="Int">0</Property>
		<Property Name="server.tcp.serviceName" Type="Str">My Computer/VI Server</Property>
		<Property Name="server.tcp.serviceName.default" Type="Str">My Computer/VI Server</Property>
		<Property Name="server.vi.callsEnabled" Type="Bool">true</Property>
		<Property Name="server.vi.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="specify.custom.address" Type="Bool">false</Property>
		<Item Name="Dependencies" Type="Dependencies"/>
		<Item Name="Build Specifications" Type="Build"/>
	</Item>
</Project>
```

- **`LVVersion` is the editor version** and has to match the LabVIEW being targeted —
  `26008000` for 2026. Read it off the first line of a shipped project rather than guessing the
  encoding: `<LabVIEW>\ProjectTemplates\Source\Core\` has one per template.
- **Formatting does not matter.** That file went to disk as UTF-8 with **bare LF and no BOM** —
  not what LabVIEW itself writes (CRLF, tabs) — and parsed anyway.
- **Only this skeleton was verified.** Whether a smaller subset loads, dropping the `IOScan.*`
  or `server.*` properties, was not tested. Add to it rather than trimming it.

Beyond blank: an `Item` with `Type="VI"` and a `URL` adds a VI, `Type="Folder"` nests, and
`describe_project`'s `missingFiles` finds a `URL` you got wrong. **A `URL` is resolved against the
`.lvproj` *file path*, not its directory** — so `../Main.vi` is the *sibling* of the project file,
which is why `../` prefixes 98.6 % of all URLs in the corpus. Getting this backwards puts every
reference one directory too high.

### Editing a project, and what verification cannot see

A virtual folder is a `Folder` item with **no** `URL` — `<Item Name="MyModule" Type="Folder"/>`.
Adding a `URL` makes it an auto-populating folder instead, which is a different thing.

Two limits found while adding one, both measured:

- **`describe_project` does not report folders at all.** Its `infoJson` has `vis`, `libraries`,
  `classes`, `otherFiles`, `missingFiles`, `ioItems` … and no folder field anywhere, so an empty
  virtual folder is invisible to it. Output before and after adding one is byte-identical. It
  confirms *files*, not project structure — for a folder, the file on disk and the IDE tree are
  the only evidence.
- **It parses from disk — including for a project that is currently open.** A never-opened
  `.lvproj` carrying a marker in `NI.Project.Description` came back with that marker, which is
  also the cheapest way to prove a hand-written file parses: `errorCode 0` plus a target. Later,
  a `VI` item added by hand to a project *while LabVIEW had it open* was reported on the next
  call. So the RPC reflects the file, not a stale in-memory copy.

The RPC being trustworthy does not make editing safe, though: **do not hand-edit a `.lvproj` that
is open in the IDE.** The IDE window keeps its own copy of the tree and **does not reload a project
changed underneath it** — observed directly: a `VI` item nested into a virtual folder on disk still
showed at target root in the tree. A save from that stale window writes its copy over the file,
which is when the edit is actually lost. Close the project first, or close it without saving and
reopen. There is no `CloseFile` RPC, so this step is manual.

**A trap that makes the stale tree look like a placement bug:** calling `lvai_open_file` with a
`viPath` while a stale project is loaded opens that VI and shows it under the **target root**,
because the in-memory project has no record of where the edited file puts it. The tree then looks
authoritative and wrong at the same time. When verifying an edit, open only the project — and
remember `describe_project` cannot settle it either, since it has no field for folders. For
nesting, the file on disk and a freshly reopened tree are the only evidence.

