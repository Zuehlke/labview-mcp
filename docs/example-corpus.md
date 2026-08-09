# Where LabVIEW keeps its examples

Measured 2026-08-07 against LabVIEW 2026 (26.3f0, 32-bit) with the `lvai` add-on at version 26.3.
Everything here came from reading files on disk and from calling the RPCs; NI documents none of it.
Re-derive after a LabVIEW or add-on upgrade.

The short answer: **there is no example list to read.** The Example Finder builds its list by
scanning, and the per-example data lives inside the example VIs themselves. That is why
`lvai_example_index` is a file scan and needs no running LabVIEW.

## 1. The five places, and what each one holds

| What | Where | Format |
|---|---|---|
| which examples exist | `<LabVIEW>\examples` and `%ProgramFiles%\NI\LVAddons\<addon>\<api>\examples` | directory tree |
| title, description, keywords, category id, requirements | **inside each example .vi** | plain-text XML |
| the category tree those ids point into | `…\Shared\Example Finder\1.0\BIN\*dtree*.dat` | length-prefixed binary |
| examples of some drivers | `…\examples\exbins\*.bin4`, older `*.bin3` | parallel arrays, §3 |
| the user's "most recent" list | `%LOCALAPPDATA%\National Instruments\Example Finder\1.0\mostrecent.bin4` | parallel arrays, §3 |

Counts on this station: 2510 `.vi` files across both roots and 14 add-on trees, yielding **951
listed examples** — 808 from the in-VI block (§2) and a further 143 from the external indexes (§3).
By type: 922 `.vi` and 29 `.lvproj`. Of the 951, **609 are listed by default** — the rest need
LabVIEW FPGA, LabVIEW Real-Time or a licensed toolkit; see `ExampleScope`.

**A cold scan takes about 55 seconds, not 400 ms.** Measured three times in a row from a fresh
process: 55 s, then 0.8 s, then 0.8 s. Nothing is cached between processes — the index lives for
the lifetime of the server that built it — so the difference is first-touch file I/O: 2510 files
are opened and read, and on a Windows machine with on-access scanning the first pass through them
is slow. An earlier revision of this document and of `CLAUDE.md` claimed "about 400 ms" flatly,
which is the warm figure and sets the wrong expectation: the first `lvai_example_index` call after
a reboot looks like a hang for the better part of a minute. Budget for the cold case, and note
that `refresh=true` pays it again.

**An example can be a whole project.** 37 of the external registrations point at a `.lvproj` rather
than a VI — `Active Noise Control (cRIO).lvproj` is an FPGA/RT application, not a diagram. Those
need `lvai_describe_project`; `lvai_convert_vi_to_aixml` is the wrong follow-up for them. No
`.lvproj` carries an in-VI block, so this is the only way they appear at all.

## 2. The in-VI metadata block

A listed example carries this as **plain text inside the .vi binary**, so it is readable without
LabVIEW:

```xml
<ExampleProgram>
<Title><Text Locale="US">Scale TDMS Data.vi</Text></Title>
<Description><Text Locale="US">This example demonstrates how to create scaling information …</Text></Description>
<Keywords><Item>files</Item><Item>TDMS</Item><Item>scale</Item></Keywords>
<Navigation><Item>2997</Item></Navigation>
<FileType>VI</FileType>
<Metadata><Item Name="RTSupport">LabVIEW RT</Item></Metadata>
<ProgrammingLanguages><Item>LabVIEW</Item></ProgrammingLanguages>
<RequiredSoftware><NiSoftware MinVersion="13.0">LabVIEW</NiSoftware></RequiredSoftware>
</ExampleProgram>
```

**The `<ExampleProgram>` wrapper is optional, and assuming otherwise loses most of the corpus.**
Under `<LabVIEW>\examples`, 498 VIs carry `<Title>` and `<Description>` but only **180** wrap them;
the other 318 begin straight at `<Title>`. The first version of `ExampleIndex` anchored on the
wrapper and reported 373 examples where the correct answer is 808 — a miss of two thirds that
looked entirely plausible from the outside. `State Machine Fundamentals.vi` is one of the ones it
lost. Anchor on the earliest of `<ExampleProgram>`, `<Title>` or `<Description>`, close at the last
known closing tag.

Other observations worth keeping:

- Only `.vi` carries the block. Never `.lvproj`, `.lvclass`, `.lvlib`, `.vim` or `.ctl` — checked
  on all five.
- Fields are individually optional: 498 files have `<Description>`, 497 have `<Keywords>`, 397 have
  `<FileType>`.
- Keywords repeat verbatim — NI's own blocks list `files` twice — so de-duplicate.
- Descriptions contain markup (`<B>File Fragmented?</B>`), newlines and tabs. Strip and collapse.
- **The block is also the filter.** The 1702 VIs without one are subVIs and support code living
  inside an example's folder. Listing them buries the examples that are meant to be opened.

## 3. The external indexes — `.bin3` and `.bin4`

Some products register their examples in an external binary index and put **no block in the VI at
all**. **NI-DAQmx is the important one**: 56 examples under `NI\LVAddons\nidaqmx\1\examples\DAQmx`,
not one of them findable by scanning VIs, so a query for "DAQmx" came back empty while they sat on
disk.

There are 23 such files on this station. Nineteen are distinct — `aspt32`/`aspt64` and
`utf32`/`utf64` ship identical copies. Four are `.bin3` under `<LabVIEW>\examples\exbins` for
VIPM-installed packages (DQMH, MGI, Asciidoc), and **one sits outside any `exbins` folder** —
`examples\JKI\EasyXML\EasyXML.bin3` — so scan the examples tree recursively, not just `exbins`.
`.bin3` and `.bin4` are the same format.

### Format

A series of **parallel arrays**, each introduced by a big-endian `uint32` count followed by that
many records. Index *i* of every array describes the same example:

| # | Kind | Holds |
|---|---|---|
| 0 | text | bare file name, `Analog Input - Filtering.vi` |
| 1 | `PTH0` | path **relative to the `examples` folder**, `DAQmx\Analog Input\Analog Input - Filtering.vi` |
| 2 | text | empty in every file measured |
| 3 | numbers | navigation node ids into the `dtree` tree of §4 |
| 4 | text | the description |
| 5 | text | this file's keyword vocabulary — optional |
| 6 | numbers | per keyword, the indices of the examples carrying it — optional |

A text record is a `uint32` length then that many bytes. A `PTH0` record is the tag, a `uint32`
size, a `uint16` type, a `uint16` component count, then components each with a **one-byte** length
prefix.

> **Corrected.** This section previously said these files "use the same length-prefixed-string
> convention as `.mnu` — `!Analog Input - Synchronization.vi` is a `0x21` length byte before 33
> characters". That reading is wrong: `0x21` is the **low byte of a 4-byte big-endian length**, and
> the printable character before each name is an artefact of that. The one-byte convention does
> appear, but only inside `PTH0` components — two prefix widths in one file.

### Why the pairing is safe

Index alignment is a measurement, not an assumption. Across all 23 files: every name equals the
last component of its path, all 565 registrations resolve to a file that exists on disk, and the
descriptions match their example's subject. `ExternalExampleIndex` re-checks the name-against-path
rule at run time and **returns nothing at all** for a file that fails it — a mis-paired description
is worse than a missing one. Such a file is then named in the tool's output.

Of the 565 registrations only 143 are new: the rest duplicate examples that already carry an in-VI
block. Where both describe one example the block wins — it is authoritative and the only source
carrying `RequiredSoftware`.

Descriptions here carry markup too (`in the <b>RT CompactRIO Target</b> folder`), so they are
tag-stripped exactly like the in-VI ones.

## 4. The category tree

`<Navigation><Item>2997</Item></Navigation>` is a node id into `dtree*.dat` under
`…\Shared\Example Finder\1.0\BIN`. Those files are the Example Finder's **browse taxonomy and
keyword vocabulary — not an example list**; they contain no file names at all.

Layout, verified against `dtree875daq.dat` (142 518 bytes):

| Bytes | Content |
|---|---|
| `0x000000` | uint32 BE `0x00000e5c` = 3676, the node count |
| `0x000004`–`0x0000c7` | 49 words of `0xffffffff` |
| `0x0000c8`–`0x0072ee` | ~29 kB of uint32 tables — the tree edges, **not decoded** |
| `0x0072ef`–end | the string pool, length-prefixed as in a `.mnu` |

3813 strings, 1356 distinct; `General` appears 111 times, `CompactRIO` 103, `R Series` 100 — they
are tags on many nodes. The tree is cross-product: LabWindows/CVI and Multisim categories sit in
the same file. Localised variants (`de_`, `fr_`, `ja_`, `ko_`, `zhcn_`) are the same tree with
translated labels — identical header, identical string-pool offset.

The index does not read these files: category comes free from the folder path, which is what a
caller actually wants to see.

## 5. What the RPCs do and do not give you

`lvai_filter_example_search_candidates` — **not a search.** It takes paths you already have and
returns a description per path. Measured:

- empty input returns `{"examples": []}`, not the full corpus
- a path that does not exist is **dropped from the response without an error** — three paths in,
  two rows out. Count the rows or a typo looks like "no description"
- it works on **any** VI, not just examples: a `vi.lib` VI returned its description fine, because
  it reads the VI's own description property

That last point makes it complementary to this index rather than redundant. A VI with no
`<ExampleProgram>` block is not undocumented: `Queued Message Handler Fundamentals.vi` has no block
yet the RPC returns a full description for it.

`lvai_search_info_cache` — the only real search RPC, and on this station it **did not return** for
the term `TDMS`. First call hit the MCP timeout; a second found the service answering
`DeadlineExceeded` with 30 fresh `LabVIEW.exe` listeners all `Unavailable`. The service recovered
afterwards, so it blocks rather than breaks. The tool description warns only that the cache may be
*empty*; not returning at all is a different failure and the reason discovery has to be ours.

`lvai_monitor_example_searches` — a callback subscription, not a query. LabVIEW pushes
`searchString` + `guid`, we answer with chosen examples. It has no filter parameter and never
returns example paths.

## 6. Where the add-on's own code sits

For anyone wanting to go further: `FilterExampleSearchCandidates.vi` lives in
`gRPC Implementations.lvlib` inside

```
C:\Program Files\NI\LVAddons\lvai\26.3\Targets\win32\resource\AI\LV AI gRPC Service\LV AI gRPC Service.lvlibp
```

and is only dispatch. The work is next door in `LV AI Core.lvlibp`, which also exports
`Generate example search content.vi` (`jsonl string`, `include ads?`) — NI's own generator for the
whole example corpus, and `VI Info Cache.lvlib:SearchInfoCache.vi`, the implementation that hangs
above. See `docs/lvai-internal-vis.tsv`; the version folder must match the running LabVIEW
(`26.3` needs `MinimumSupportedLVVersion 26.3`, satisfied by 26.3f0 here) and the bitness must
match too.

Reaching into those is the back door described in `CLAUDE.md`. It is not needed for the index: the
source data is on disk.
