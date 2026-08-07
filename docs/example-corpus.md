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
| examples of some drivers | `…\examples\exbins\*.bin4`, older `*.bin3` | length-prefixed binary |
| the user's "most recent" list | `%LOCALAPPDATA%\National Instruments\Example Finder\1.0\mostrecent.bin4` | length-prefixed binary |

Counts on this station: 2510 `.vi` files across both roots, of which **808 are listed examples**;
1687 files and 14 add-on trees. A full scan takes about 400 ms.

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

## 3. Not every example is covered

Some drivers register through an external binary index instead of the in-VI block. **NI-DAQmx is
the important one**: 69 example VIs under `NI\LVAddons\nidaqmx\1\examples\DAQmx`, none carrying a
block, all described inside `exbins\daq82mxw.bin4`.

There are 23 such files on this station (`.bin4` in add-on trees, older `.bin3` under
`<LabVIEW>\examples\exbins` for VIPM-installed packages such as DQMH and MGI). They use the same
length-prefixed-string convention as `.mnu` — `!Analog Input - Synchronization.vi` is a `0x21`
length byte before 33 characters — and they contain names, category labels, `PTH0` path records and
descriptions up to 521 characters.

**What is not decoded is which description belongs to which example.** Guessing the pairing would
attach wrong text to real examples, which is worse than no text, so `lvai_example_index` counts and
names these files instead and says their examples are absent. Decoding them is the obvious next
step if DAQmx coverage matters.

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
