# Where the tokens go - and whether JSON instead of XML would help

Measured 2026-10-30 over every Claude Code transcript of this repository
(`~/.claude/projects/C--Projects-LabVIEWMCP/`, 163 main sessions plus their subagents, 712 files).
The scripts are in `scripts/token-economy/`; they read the transcripts and print aggregates only.
Character counts are exact; token figures are estimated at about 4 characters per token, because no
tokenizer was run.

The question that started it: would doing some pylabview steps in JSON instead of XML save tokens,
since JSON carries less overhead? **Short answer: no - pylabview XML is too small a share of the
context to matter, and a generic JSON rendering of the heap is not even smaller.** The levers that
do matter are elsewhere, and three of them are cheap.

## 1. What a request costs

| | requests | average context per request | total |
|---|---|---|---|
| main sessions | 46 394 | **484 k tokens** | 22.4 G |
| subagents | 19 524 | 206 k tokens | 4.0 G |

The context is re-read on every request (as a cache read), so **anything that enters the context
costs once per remaining turn of the session**, not once. A 10 kB answer early in a 300-turn
session costs about 750 k tokens. Removing a round trip (`docs/workflow-economics.md`) and keeping
what enters the context small are the same lever seen from two sides.

## 2. What enters the context

Tool results over all transcripts, 137 M characters:

| source | M chars | share |
|---|---|---|
| `Bash` | 43.8 | 31.9 % |
| `Read` | 13.9 | 10.2 % |
| `lvai_run_vi_and_read_values` | 10.5 | 7.6 % |
| `lvai_aixml_reference` | 7.5 | 5.5 % |
| `lvai_generate_vi` | 4.5 | 3.3 % |
| `lvai_add_class_method` | 4.0 | 2.9 % |
| all five `pylv_*` tools | 2.2 | 1.6 % |

`Bash` split by command: `grep` 16.0 M, `cat`/`head`/`sed` 15.6 M, other Python 6.3 M, and
**everything touching pylabview bundles or heap XML 2.6 M (5.8 % of Bash, about 1.9 % of the
total)**. `Read` of `.xml` files - AIXML and heap bundles together - is another 2.1 M.

So **pylabview XML is at most about 3-4 % of what enters the context.** Halving it would save
under 2 %.

## 3. JSON instead of the heap XML, measured

`Run Tests (Path)_BDHb.xml`, one block-diagram heap as pylabview writes it:

| rendering | chars |
|---|---|
| XML as written (indented) | 37 170 |
| XML without indentation | 23 616 |
| generic JSON, compact (`{"t":tag,"a":attrs,"x":text,"c":[children]}`) | 27 608 |
| generic JSON, indented | 89 226 |

The heap is structure plus values; a faithful JSON carries the same structure with different
punctuation and comes out **larger** than the same XML without indentation. A tailored JSON (a
leaf element as `"objFlags": 543360`) would win perhaps 20 %, at the price of a second converter
in front of a format whose lossless round trip is the whole reason pylabview is usable here
(`pylv_rebuild` reads the XML back). **The win is a PROJECTION, not a format**: `pylv_apply`'s
inspect mode already answers with three short listings instead of the bundle, and every
`scripts/pylv-*.py` that prints what it found rather than the heap is the same idea.

**Recommendation: do not convert pylabview to JSON.** Where an agent greps a heap, a script that
prints the few fields it needs is the cheaper route.

## 4. What does pay off, ranked

### 4.1 CLAUDE.md is about 70-75 k tokens in EVERY request - the largest single item

`CLAUDE.md` is 296 kB. It is part of every main-session request and every subagent request, so it
is roughly **15 % of an average main request and over a third of an average subagent request**
(206 k), before the agent's own definition (28-91 kB each) is added. Most of its length is history:
how a rule was found, which earlier version was wrong, which build measured it. That history
already lives in `docs/` - nearly every paragraph ends with a pointer to the document holding it.

Proposal: a rules-only `CLAUDE.md` of about 30-40 kB - each rule in two or three sentences plus its
`docs/` pointer, the "Where the knowledge lives" table kept whole - and the narrative moved into
the documents it already cites. Estimated saving about 60 k tokens per request, main and subagent
alike. **This is the user's decision, not done on this branch**: the file is the project's memory,
and what counts as rule versus history is a judgement call.

The agent definitions are the same question one level down (the three largest are 72-91 kB).

### 4.2 The server's JSON escapes every `<`, `>` and `&` - 12 % of every LabVIEW answer

System.Text.Json's default encoder writes `<` as `<` (six characters, several tokens), and
the same for `>`, `&`, `'`, `"` inside strings and `+`. Our answers carry XML, AIXML and paths, so
it bites everywhere. Re-serialising every `lvai_*`/`pylv_*` answer in the transcripts:

| variant | M chars | saving |
|---|---|---|
| as served | 59.5 | - |
| relaxed escaping (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`) | 52.3 | **12.1 %** |
| relaxed escaping and no indentation | 43.6 | 26.7 % |

Per tool, relaxed escaping alone: `lvai_run_vi_and_read_values` 30 %, `lvai_describe_project`
28 %, `lvai_create_message_class` 26 %, `lvai_convert_vi_to_aixml` 20 %, `lvai_add_class_method`
17 %. The answers stay valid JSON - the encoder only stops escaping characters that are harmless
outside HTML. The options live in `Infra/Json.cs` plus about twenty local `JsonSerializerOptions`
in the tools, so the change is mechanical.

*Built 2026-10-30: `Json.Answer` in `Infra/Json.cs` carries the relaxed encoder, every tool's
indented options point at it, a test refuses a new private copy, and the CLI sets the console to
UTF-8 so an umlaut written as itself is not garbled on a legacy code page. Indentation kept.*

Dropping the indentation saves fewer TOKENS than characters (a run of spaces tokenizes cheaply), so
it is the weaker half; measure it with a tokenizer before doing it.

### 4.3 `lvai_run_vi_and_read_values` returns LabVIEW's flattened XML inside JSON

The largest LabVIEW tool by volume (10.5 M). A 14-element string array comes back as `"value":
null` beside an `xml` string of `<Array><Name>…</Name><Dimsize>14</Dimsize><String><Name></Name>
<Val>Car Wash Indicators</Val></String>…` - JSON-escaped on top. One sample answer: 4 753 chars
served, 2 922 with relaxed escaping and compact output, and the same values as a native JSON array
would be a few hundred. Proposal: decode the flattened XML into a native JSON `value` (string,
number, bool, array, cluster as object) and send `xml` only on request. **The `xml` field must stay
reachable**: the ActiveX run path was aligned to it so that one parser reads both backends
(`docs/aixml-backends.md`), and `lvai_set_constant`/compound inputs take that XML back in.

### 4.4 Output tokens: AIXML authored by hand

The model wrote 8.1 M characters of `.xml` through `Write` - AIXML, by far the largest kind of file
it writes. Output tokens are the expensive and the slow ones. The tools that GENERATE AIXML from a
short description (`lvai_generate_test`, `lvai_generate_caraya_test_runner`, the class tools) are
the proven pattern; the next candidates are the shapes agents re-derive often - an error-chained
subVI call sequence, a state machine frame. A compact JSON authoring format that the server expands
into AIXML is possible but adds a second language to learn and to get wrong; measure which shapes
recur before building one.

### 4.5 Reading files whole

`grep` and `cat`/`head`/`sed` together put 31.6 M characters into contexts - more than all LabVIEW
tools combined. Much of it is `docs/` and `.cs` read in full. Two habits help without code: a
`grep -n` with a narrow pattern before a read, and delegating wide reads to a subagent, whose
context ends with its report (subagent contexts average 206 k against 484 k in main sessions).

## 4a. How much, and does it get faster?

Estimated share of all context tokens saved (main and subagent requests together):

| measure | saving | basis |
|---|---|---|
| CLAUDE.md 296 kB -> about 35 kB | **about 16 %** (main 13 %, subagent about 31 %) | about 65 k tokens fewer in each of 65 900 requests |
| relaxed JSON escaping | about 3 % | 7.2 M of 137 M tool-result chars, scaled to the dynamic part of the context |
| native values in `lvai_run_vi_and_read_values` | about 2 % more | about 5.5 M chars beyond the escaping |
| together | **about 20 %** | |

**It does NOT get noticeably faster.** Median turn latency against context size, from the same
transcripts (`scripts/token-economy/latency.py`):

| context | output < 200 tokens | output 200-1000 tokens |
|---|---|---|
| 100-200 k | 2.8 s | 5.0 s |
| 300-400 k | 2.9 s | 4.9 s |
| 500-700 k | 3.2 s | 5.2 s |
| 700-1100 k | 3.1 s | 5.4 s |

A cached context costs about 0.3 s per 500 k tokens, so 65 k fewer is well under a tenth of a
second per turn. **Latency follows OUTPUT tokens and the NUMBER of turns** - about 2 s for the
step from under 200 to a few hundred output tokens. Speed comes from fewer round trips and less
hand-written AIXML (4.4), not from a smaller context. A smaller context does push back the
point where a long session runs into the context limit and gets summarised.

## 4b. What to do first for SPEED

Wall clock over all transcripts (`scripts/token-economy/speed.py`): **271 h of model latency
against 138 h inside tools.** "repeat" is the share of calls made straight after a call to the same
tool - a batch candidate. The transcripts mix LabVIEW work with development of this server, which
is most of the `grep`/`python`/`cat` rows.

| tool | calls | repeat | latency h | in tool h |
|---|---|---|---|---|
| `ToolSearch` | 989 | 1 % | **12.8** | 0.0 |
| `lvai_status` | 1 024 | 0 % | 3.5 | 0.6 |
| `lvai_ensure_labview` | 375 | 20 % | 0.7 | **3.0** |
| `lvai_exec_state` | 1 910 | **54 %** | 1.6 | 1.8 |
| `lvai_set_vi_icon` | 1 450 | **70 %** | 1.0 | 2.3 |
| `lvai_run_vi_and_read_values` | 3 020 | 41 % | 4.2 | 3.9 |
| `lvai_aixml_reference` | 1 456 | 47 % | 4.2 | 0.0 |
| `Write` (AIXML, scripts) | 4 142 | 17 % | **30.0** | 1.2 |

In order:

1. **Batch what is called back to back.** *Built 2026-10-30: `lvai_exec_state` takes `viPaths`,
   `lvai_set_vi_icon` takes `iconsJson`; one answer, every VI's full answer under `results`.*
    `lvai_exec_state` and `lvai_set_vi_icon` take one VI
   each; a path list (as `lvai_convert_vis_to_aixml` does) or the icon step folded into
   `lvai_generate_vi` removes about 2 000 turns. `lvai_aixml_reference` already takes a list and is
   still called back to back 47 % of the time - the agents need telling.
2. **Loading deferred tools costs a turn each time**: 989 `ToolSearch` calls, 12.8 h. Agents and
   sessions should load every tool they will need in ONE `select:` call.
3. **A status turn at the start**: `lvai_status` 1 024 times, `lvai_ensure_labview` 29 s a call.
   Keep LabVIEW warm, and let the working tools say when LabVIEW is not there instead of asking
   first.
4. **Output**: `Write` alone is 30 h of latency - AIXML and helper scripts written out. More
   generators from short specs (4.4).

## 4c. The point missed above: 91 % of all requests run ABOVE 200 k context

Re-read 2026-10-30 after a model switch, with one more number: of 46 802 main-session requests,
**42 356 (91 %) carried more than 200 k tokens of context, and they hold 97 % of all context
tokens.** 123 of 163 sessions grew past 200 k, 61 past 500 k; the median session makes 118
requests, the longest 7 031.

That matters for two reasons that are not in the tables above:

- **Cost**: above 200 k tokens the input - cache reads included - is billed at the long-context
  rate, roughly double. The 20 % of 4a saves 20 % of that; **keeping the context UNDER 200 k saves
  half of it outright**, on every request after the threshold.
- **Quality**: a session at 500 k-1 M tokens carries dozens of stale tool answers, superseded plans
  and corrected mistakes beside the live ones. Every rule in CLAUDE.md about "the first diagnosis
  was wrong" was written in such a session.

Latency does NOT change with it (4a), so this is a cost and quality lever, not a speed lever.
What keeps a session short is already the standing rule - LabVIEW work in a `labview-*` agent,
whose context ends with its report (subagent requests average 206 k against 484 k) - plus two
habits: a NEW session for a new task instead of continuing a long one, and tool answers that say
what happened rather than returning everything they saw (the `verbose: false` default).

**Ranking after this measurement**: (1) short sessions and delegation, no code; (2) batch the
back-to-back calls and load tools in one step, for speed; (3) a rules-only CLAUDE.md; (4) relaxed
escaping and native run values. The JSON-for-pylabview idea stays last.

## 5. Not measured

- Token counts: everything above is characters; a tokenizer run would sharpen 4.2 and the
  indentation question in particular.
- Whether a smaller CLAUDE.md changes behaviour: the saving is arithmetic, the risk is a rule an
  agent stops following. An A/B over one cold build would settle it.

## 6. Acceptance, 2026-10-30 - the branch build over raw stdio

Driven with `scripts/mcp_drive.py` against `bin\Debug
et8.0\LabVIEWMCP.exe` (commit after
64eaaed4), pylabview backend, LabVIEW 2026 up 74 min: seven calls in one server process over the
ATM_Pylab subVIs and three copies of `Build ATM Message.vi` for the icons.

| call | s | served chars | same JSON, default escaping (emulated) |
|---|---|---|---|
| `lvai_status` | 0.2 | 1 525 | 1 493 |
| `lvai_exec_state`, one VI | 0.3 | 968 | 1 005 |
| `lvai_exec_state`, `viPaths` x5 (one missing on purpose) | 0.4 | 5 659 | 5 796 |
| `lvai_run_vi_and_read_values` | 0.7 | 2 623 | 3 545 (**-26 %**) |
| `lvai_set_vi_icon`, one VI | 0.2 | 785 | 767 |
| `lvai_set_vi_icon`, `iconsJson` x2 | 0.3 | 2 371 | 2 328 |

- **Stable**: every call `ok` (the batch `ok: false` only for the file that does not exist, named
  under `failed` with `viNotFound`, the four real VIs under `executableVis`); both icons
  `verified` by their read-back; LabVIEW the same pid before and after, `dwarnCount` 4 -> 4.
- **Escaping works end to end**: not one `<` or `&` in any served answer, where the
  transcripts' answers carried them in every XML value. The saving lands where XML is - 26 % on
  the run answer, nothing on answers that are paths and flags (the emulation's -2 % there is
  its own rounding, not a cost). The 12 % over all answers in 4.2 stays the figure to quote; it
  was measured on real served text, this emulation only confirms the direction.
- **Batch**: five VIs in 0.4 s against 0.3 s for one - about 0.08 s per VI inside the batch,
  because the helper is loaded once. The batch answer is about 17 % LONGER than five single
  answers (5 659 against 5 x 968) since it carries the summary lists on top; what it saves is
  four model turns, about 28 s by the transcripts' median, which no single call can show.
- **Not measured here**: tokens (characters only), the gRPC backend (settings pin pylabview), and
  a run inside a real client session, which needs the client restarted for the new parameters.

