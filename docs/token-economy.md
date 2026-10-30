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

## 5. Not measured

- Token counts: everything above is characters; a tokenizer run would sharpen 4.2 and the
  indentation question in particular.
- Whether a smaller CLAUDE.md changes behaviour: the saving is arithmetic, the risk is a rule an
  agent stops following. An A/B over one cold build would settle it.
