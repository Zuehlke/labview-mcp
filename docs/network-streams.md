# Network Streams on the pylabview backend

Issue #96, measured 2026-10-21 on NI's own *Simple Network Streams* example
(`examples\Data Communication\Network Streams\Simple Network Streams`, LabVIEW 2026 32-bit) and on
composed probes. The issue's project (LabVIEW 18.0, myRIO) was not available; the example
reproduces the same failure on a stock install.

## 1. Reading: what failed, and why

`lvai_convert_vi_to_aixml` / `pylv_extract` on the pylabview backend died for every VI holding a
Network Stream endpoint refnum, with `TypeError: object of type 'NoneType' has no len()` in
`TDObjectTag.expectedRSRCSize`. That is the third error of a chain:

1. **Root cause.** An endpoint refnum is a `UsrDefndTag` refnum holding a `Tag` type whose
   `LVVariant` stores its types INLINE (`varcount` + type descriptors, not VCTP indices). Its
   `NI_VAR_DATATYPE` attribute carries the stream's data type with a default value. When that data
   type contains a variant - the example streams `cluster{string Message, variant Message Data}` -
   the nested variant was read with `useConsolidatedTypes=True`, took a `varcount` for a VCTP index,
   found no type, and raised `LVVariant has no linked TD but is supposed to have DataFill`.
   Upstream's own comment at that spot: *"the lack of use of ConsolidatedTD should be propagated"*.
2. **Masking.** The TypeDef data-fill's error handler names an undefined `client`, so the real
   exception surfaced as `NameError: name 'client' is not defined`.
3. **Follow-up.** `TDObjectTag.parseRSRCData` aborted before reading its `ident`, and the sanity
   check then called `len(None)`.

Fixed by four entries in `tools/pylabview/patches/patches.json` (`typedef-datafill-error-name`,
`variant-consolidation-inherit`, `-rsrc`, `-xml`): a variant read inside the fill of an inline-typed
variant inherits `useConsolidatedTypes=False`, on the binary and on the XML path alike.

| measurement | before | after |
|---|---|---|
| extract Host / Target | exit 1, 0 files | 39 / 34 files |
| rebuild, re-extract | - | every file identical |
| rebuilt VI in LabVIEW | - | `execState 1`, no linker error |
| 1 824 VIs (all examples + stream VIs in `vi.lib`), old vs new runtime | | 2 fixed, 0 broken, every other extract byte-identical |

A guard on `ident is None` (the issue's suggestion 2) was NOT added: with the root cause fixed it
never fires, and writing a tag without its ident would produce a file LabVIEW refuses rather than an
error that names the fault.

## 2. Reading: what the reader then missed

With the extract working, a uid join of `vi2aixml.py`'s output against NI's export showed:

- **Write/Read Single Element to/from Stream were dropped**, left only as a `vi2aixml:` comment. Their
  heap class is `sharedVariableDynamicWrite` / `Read`, not `prim`, but the shape is a primitive's:
  `SharedVariableDynamicResID` in hex (5CF6 = 23798 Write, 5CF5 = 23797 Read, beside the stream
  prims 23794-23811) and each slot as a hex `terminal_ID` where a prim writes a decimal
  `parmIndex`. `aixml2vi.PRIM_CLASSES`, `prim_res_id` and `term_slot` now treat them as prims - in
  the reader, the composer and `harvest_parts.py` alike.
- **Six primitives had no name** (`?primResID 23808`, terminals `?parmN`). Names and slots learned
  by uid join (`find_prim_ids.py`, `learn_names_full.py`); the four slots no example wires were
  named by NI's export of a composed probe that wires `?parmN`, and the last two by elimination plus
  the heap's own type label - `terminal-names-extra.tsv` says which.
- **The property node's class was wrong**: `{GenClassTag.Network Stream Endpoint.DataExchange}`
  against NI's `{GenClassTag.NetworkStream.DataExchange}`. The rule `GenClassTag.<node name>.<TypeName>`
  was derived from DAQmx alone. Over 21 VIs and 98 tag-refnum nodes in the export cache only DAQmx
  (`Ident=Task`, `TypeName=NIDAQ`, one refnum carrying five property classes) is named by the node;
  Network Streams and NI System Configuration (`nisyscfgSession.nisyscfg`, also wrong before) are
  named by the `Ident`. Before: 101 of 113 node `type`s right; after: 113 of 113. The four property
  names (`Available Elements for Reading` for `AvallableElementsForReading` ...) are in
  `parts/property-export-names.json`.

Still open, and NOT stream-specific: the reader reads no XNode (`Error Ring.xnode` is noted in a
comment and left out). It wrote no `FreeLabel` either when this was measured; it reads diagram
comments since the same day - `docs/aixml-backends.md`, "Diagram comments".

## 3. Writing: gRPC against the composer

| | NI's converter (gRPC) | our composer (pylabview) |
|---|---|---|
| Create Writer / Reader Endpoint, Flush, Destroy | accepted | composes (6 parts, harvested from the example) |
| Write / Read Single Element | **refused**: `Unrecognized node type` - NI exports what it cannot generate | composes |
| property node on an endpoint (`{GenClassTag.NetworkStream...}`) | accepted | composes since the same day - section 4 |
| the whole Host example | refused (the two nodes above, then the Event Structure) | refused at the XNode first; without XNodes and property nodes only the errors of the removed nodes remain |

**A stream round trip composes and RUNS with no NI service** - suite case
`experiments/aixml-without-grpc/tests/t171_network_stream.xml`: a reader and a writer endpoint in
one VI, one double written, flushed, read back, both destroyed; `data out=42.5`, `error out` clean.
The same document with the example's `cluster{string, variant}` returns `Set Frequency`.

**One part per primitive serves every data type.** The endpoint refnum carries the stream's data
type inside it, and the harvested part carries the DONOR's (the example's cluster). LabVIEW
re-derives the endpoint type from the `data type` input on load, so a double stream composed from
that part ran correctly. Measured with `double` and with the donor's own type; other types
(arrays, waveforms) are not measured.

**`writer buffer size` / `reader buffer size` is a REQUIRED input** of the Create nodes. Without it
both converters write the VI and LabVIEW reports `Create Network Stream ... Endpoint: Contains
unwired or bad terminal` - NI's converter exactly as ours, so it is the document, not the route.
`timeout in ms (-1)` is NOT required, and two Create nodes in one VI each waiting for ever did not
deadlock.

Composer side: the stale-type-id guard in `Parts.get` no longer descends into an `LVVariant` - a
typedef there names the variant's OWN inline types by index, which the endpoint part does.

## 4. Property nodes on a tag refnum - Network Streams, DAQmx, NI System Configuration

The composer built `{LV.*}` property nodes only. A node on a user-defined TAG refnum - a Network
Stream endpoint, a DAQmx task, an NI System Configuration session - was refused by class. It
composes now; read off NI's heaps, it differs from a VI Server node in four places:

- **The rows** come from NI's examples like every other row (`harvest_parts.py --props`, new
  `tag_props`), keyed by the name NI's EXPORT writes (`Available Elements for Reading`) with the
  heap's own `AvallableElementsForReading` as `heapName`. 60 rows over 7 classes were harvested from
  the 42 cached exports holding such a node, every one an addition to `vi-server-props.json`.
- **The terminal's type and the item's `dataTypeDesc` differ** (an I32 terminal, a U32 data type
  on `Available Elements for Reading`), and the item carries `objFlags 4096`: kept as `dataType` and
  `infoFlags`.
- **The reference terminals are typed with the refnum WIRED in**, because it carries more than the
  class - an endpoint holds its stream's data type in `NI_VAR_DATATYPE`. So does `oMId` for a class
  named by its refnum. **DAQmx is the exception**: its class names the NODE (`DAQmx Task`, `DAQmx
  Timing`), and NI's node names a refnum of THAT class - `Ident="DAQmx Task"`, `FirstClient="0"`,
  no Tag - in `oMId` and `omidTypeDesc`, with node `objFlags 4608` against 131584 for a stream.
  The wired Task refnum there was `Invalid property` on every node, measured before the fix.
- **NI names the reference terminals after the wired source** (`writer name`, `task out`,
  `System Session`/`System Session out`); on a tag node the one input that is not the error
  cluster is the reference, and an output that is neither a field nor `error out` its
  `reference out`. A DAQmx node with nothing wired into `reference` is refused by name.

**Measured 2026-10-21.** Suite case `t172_network_stream_property`: a double stream composed with
no NI service, the reader endpoint's `Buffer Size` and `Available Elements for Reading` read
through the node - `buffer=10, available=0, data out=42.5`, no error. A first version of that test
read the property between Flush and Read and HUNG: Flush waits until the reader has taken the
elements, and the Read waited on the Flush's error wire - a deadlock in the document, not in the
route. Over the 42 cached exports with a tag node, **20 compose and 17 of those load executable**
(DAQmx Timing, Trigger, Channel, Read and Task, nisyscfg). The other three (`Bridge`, `RTD or
Thermistor`, `Strain - Continuous Input`) use exactly the property nodes of `Voltage` and `Current -
Continuous Input`, which load executable, so their break lies elsewhere and was not traced. Of the
22 that do not compose, 13 stop at other gaps (XNodes, DataFinder refnums, an Unbundle field) and 9
at a node on a `{GenClass.<Ident>.<TypeName>}` - a user-defined refnum WITHOUT a tag (nisysapi),
which is the next family and not done.


## 5. The whole example regenerated - pylabview against gRPC (2026-10-21)

NI's cached exports of Host and Target, each changed identically for both routes: the requested
edit (`writer buffer size` 10 -> 20 in the Host, 100 -> 200 in the Target), every `Error Ring.xnode`
replaced by an error constant "no error" on the same net (its `Select` takes it only when -314220 is
ignored), and in the Target the Express VI `Simulate Signal` plus `DDT to Waveform` replaced by
`NI_MABase.lvlib:Sine Waveform.vi` / `Square Waveform.vi`. XNodes and Express VIs are generated by
neither route. Each route got its own copy of the example folder, `.lvproj` and `support\` included.

| | gRPC (`lvai_generate_vi`) | pylabview (composer) |
|---|---|---|
| Host | refused at validate: `Unrecognized node type` for Read Single Element and 3x Write Single Element | composed, `execState 1` |
| Target | refused the same way | composed, `execState 1` - after one fix, below |
| run together | - | Host's `Acquired Data` filled with 2 002 sine samples sent by the Target; the Target's `Frequency` read **10.1**, the Host's `Set Frequency` command |
| the edit, read back | - | `writer buffer size` 20 and 200 |

**The fix the Target needed**: `Unbundle By Name 402: input is Array, not a cluster`. The stream
parts carry their DONOR's types, and the Read part came from the Host, which reads waveform arrays;
LabVIEW re-derives them on load (that is why `t172`'s double ran), but a node AFTER `Read Single
Element` needs `data out` typed while composing. The composer now follows an endpoint back through
the document - shift registers, tunnels, Write/Read/Flush, a tag property node - to the `data type`
wired into its Create node, and types `data out` with it (`Composer._stream_type`). The Target's
endpoint reaches its Read through a shift register, which is why the lookup reads the AIXML rather
than what is composed so far.
