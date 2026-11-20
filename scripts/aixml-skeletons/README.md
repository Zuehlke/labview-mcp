# AIXML skeletons - complete, run VIs to copy shapes from

Read THIS index first and then only the one file that fits. Each `.xml` is a whole VI that was
generated on BOTH backends and RUN; copying an element out of it costs no lookup. One read of a
skeleton replaces the `lvai_aixml_reference` lookups for every node in it.

**Why these six (2026-11-18).** Counted over the 762 AIXML files that 148 agent runs wrote since
2026-11-01: fields by name in 376 files, a For Loop in 218, a Case Structure in 184, a file read in
46, a queue in 40, a key search in 38, a control property in 94. And the terms agents looked up
most were `Merge Errors` (122 calls, 60 agents), `Select` (114), `Unbundle By Name` (108),
`Case Structure` (82), `Build Array`, `Format Into String`, `Bundle By Name`, `Index Array`.

| You need | File | Nodes it spells out |
|---|---|---|
| walk an array of clusters, total and count | `cluster-array-loop.xml` | For Loop with an indexing tunnel, two shift registers, `Unbundle By Name`, `Bundle By Name`, `Select`, `Greater Or Equal?`, `Add`, `Increment`, `Array Size` |
| read a text or CSV file into clusters | `text-table-read.xml` | `Read from Text File` with no dialog, `Spreadsheet String To Array` (2D string), `Index Array` with two outputs, `Fract/Exp String To Number`, an indexing OUTPUT tunnel |
| write clusters to a text or CSV file | `text-table-write.xml` | `Format Into String` (one input), `Concatenate Strings` (many inputs, and an array into one), `Write to Text File` with no dialog |
| find one record by its key | `find-by-key.xml` | `Search 1D Array`, `Greater Or Equal To 0?`, `Index Array`, a not-found answer |
| disable or enable a group of controls | `control-group-disable.xml` | `array{ref{LV.Control}}`, a `Property Node` writing `Disabled`, the error chain on a shift register |
| change one record in an array by its key | `record-update.xml` | `find-by-key` plus `Bundle By Name` on the found element and `Replace Array Subset`; an unknown key leaves the array as it was |
| a file beside the VI | `path-beside-vi.xml` | `Current VI's Path`, `Strip Path`, `Build Path` |
| append a line to a log file | `log-append.xml` | a file REFNUM: `Open/Create/Replace File` with the `operation` enum (`open or create` = 3), `Get File Size`, `Set File Position`, `Write to Text File` on a refnum, `Close File` |
| work only when no error came in, raise your own | `error-case.xml` | Case Structure on the error cluster (`No Error` / `Error` frames), `Error Cluster From Error Code.vi` with its literal terminal names, ONE error tunnel out |
| a producer and a consumer over a queue | `string-command-queue.xml` | `Obtain`/`Enqueue`/`Dequeue`/`Release Queue`, a While Loop with `Condition`, a Case Structure on a STRING with a `Default` frame and two output tunnels, `Or` on an error cluster, `Merge Errors` |
| accumulate blocks across iterations | `accumulate-across-a-loop.xml` (+ `.md`) | `maxin` For Loop, seed/append/keep case pair |
| two loops joined by a user event with data | `user-event-two-loops.xml` (+ `.md`) | Event Structure, user event payload |
| a Web Browser control | `web-browser-title-poll.xml` (+ `.md`), `web-browser-read-page-title.xml` | `ref{LV.…}` stand-ins, polling |
| name a graph's plots, its axis, any plot/scale/cursor property | `graph-plot-names.xml` | `VI Server Reference` on a graph, `write+Value` FIRST, `write+Active Plot,write+Plot.Plot Name` in a For Loop, `read+Plot.Plot Name`, `Y Scale.Name Label.Text` |
| end an event loop when the user closes the panel | `panel-close-event.xml` | ` Panel Close? ` frame (no source), `Event Filter Node` `Discard?`, a latched Stop, `Or` on the error cluster - **pylabview only** |

## What every one of these ten shows

- `error in` on conIdx 11 `required`, `error out` on 15 `recommended`, the house error type.
- One position-independent `FreeLabel`; what an iteration or a frame does is the structure's `label=`.
- uids from 4200. They are plain numbers here: renumber when you copy two skeletons into one VI.
- A comma in `description`, `comment` or `label` is written RAW (`log-append.xml`). An export
  shows it as `\2C`; that is the reader's spelling, both generate the same VI (measured on both
  backends, 2026-11-18). Only inside a `value` is a comma structure.
- The item type is `cluster{string.Name,double.Value}` - replace it with yours in every place.

## Shapes that were got wrong while writing them

- **`Release Queue` wired straight from `Obtain Queue` runs IN PARALLEL with the loops** and
  releases the queue before the first `Enqueue` (Error 1 at `Enqueue Element`, `Handled` 0). The
  queue goes THROUGH the consumer loop - `Dequeue`'s `queue out` into an output tunnel - and only
  then into `Release Queue`. Validation and `execState` saw nothing; the run did.
- **A file node with an empty path opens a dialog** and stops the service. The `Select` with a
  relative path in front of `Read from Text File` / `Write to Text File` turns it into Error 1430.
- **`Format Into String` takes exactly one `input 1`.** Several values are one `Format Into
  String` each, joined by `Concatenate Strings`.
- **A case tunnel is written twice**: once on the `Structure` for the wire outside, once in
  EVERY `CaseFrame` for the wire inside (`string-command-queue.xml`).
- **A graph's data wired to its TERMINAL races the Property Node** (`graph-plot-names.xml`,
  2026-11-20): nothing orders the terminal write before the node, the pylabview build ran the
  node first, and `Active Plot` 1 on a graph that still had one plot answered Error 1077; the grpc
  build of the same document happened to run the other way. The data goes in through `write+Value`
  on the error chain instead. `Active Plot` past the last plot is 1077 in any case.
- **A plot's field is written bare** (`Plot Name`) by anyone who reads the catalogue row
  `{LV.Plot} Plot Name`; on the graph's node it is `Plot.Plot Name`, and every field needs its
  `read+`/`write+`. `lvai_check_aixml` names and repairs the first (`propertyOnSubObject`).

## What was measured, exactly

**An agent USES the index** (2026-11-18): a `labview-vi-generator` given "read a price list from a
CSV file and return one product's price" read this README, then `text-table-read.xml` and
`find-by-key.xml`, made NO `lvai_aixml_reference` call, and finished in 9 requests and 58 s, right
first time. Single-VI agents since 2026-11-01 made 1-3 lookups of 3-19 terms in 10-25 requests.
One run, not a series.

The four added after that run (`record-update`, `path-beside-vi`, `log-append`, `error-case`)
were generated on both backends and run the same day: `B` changed to 99 and an unknown `X` left
the array as it was; `settings.ini` landed beside the VI; three appends gave
`first\nsecond\nthird\n` and an empty path Error 1430; 21 gave 42, -1 gave error 5000 with its
message, and an incoming error 42 passed through with no result. In `error-case` the `Result` of a
refused value is still computed (-2) - the error, not the number, is the answer there.

2026-11-18, LabVIEW 2026 (32-bit): all six through `lvai_generate_vis` on pylabview AND on grpc
(NI's validation included) - executable, pane without findings, within the diagram budget. Run on
both builds: totals 2 / 15.5 for three items at minimum 5; the table written and read back equal;
`Beta` found at index 1 and `beta` not; three `Add` handled before `Exit`, one after it ignored.
`control-group-disable` was run with an EMPTY group only - its property write is the shape of
`Set Controls Disabled.vi`, which 60 generated callers used, but no control was disabled here.

The two added 2026-11-20, LabVIEW 2026 (32-bit). `graph-plot-names` on pylabview AND grpc,
executable, within budget, run: `[Delta,Echo,Foxtrot]` read back as written on both; four names
for three plots gave Error 1077 at `Active Plot` with nothing read back (the negative control).
`panel-close-event` is the one skeleton that is NOT built on both: NI's grpc converter refuses
the frame (`Event Structure: One or more event cases have no events defined`, validation), and the
pylabview composer builds it. Run there with the panel open (`lv-com.ps1 -OpenPanel -Post
sysclose`): the close box ended the loop after 1 556 ms and `Discard?` TRUE kept the panel open;
without the message the VI ran until it was aborted at 3 s. `lvai_run_vi_and_read_values` cannot
fire a window event, so an agent verifies such a VI by executability and an export only.

A skeleton is a frozen measurement. Terminal names of a node NOT in this table still come from
`lvai_aixml_reference` (all of them in one call) or `lvai_vi_terminals`.
