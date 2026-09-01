# Where the time actually goes — and what to build next

Measured 2026-08-29 by aggregating the Claude Code session transcripts for this repository
(`~/.claude/projects/C--Projects-LabVIEWMCP/*.jsonl`), with `scripts/` untouched — the two throwaway
tally scripts are in the session scratchpad, and only aggregates were ever loaded into context.

**Sample: 2 641 tool calls over 2 549 model turns, six sessions.** That matters, because the figure
this repository has been steering by until now came from *three* calls in one session.

## 1. The headline: latency dominates, by 3.6 to 1

| | total |
|---|---|
| model latency between turns | **12.90 h** |
| time spent inside tools | 3.63 h |
| ratio | **3.6 : 1** |
| median latency per turn | **7.1 s** |

Per session, the same shape holds and the ratio only gets worse on the sessions that did the most
LabVIEW work:

| session | tool calls | wall clock | latency | inside tools | ratio |
|---|---|---|---|---|---|
| the class + unit-test run (this one) | 303 | 103.5 min | **75.0 min** | 18.0 min | 4.2 : 1 |
| `51d3a271` | 432 | 318.7 min | 102.9 min | 37.6 min | 2.7 : 1 |
| `caa1c04f` | 336 | 344.6 min | 114.5 min | 14.7 min | **7.8 : 1** |
| `1041200b` | 1 420 | 1588.7 min | 419.9 min | 136.8 min | 3.1 : 1 |

**This confirms CLAUDE.md's rule and sharpens its number.** That file says "about **7 s per turn**,
all of it latency… Optimise the number of calls, not the cost of one", derived from three calls in
one session. The median over 2 549 turns is **7.1 s**. The rule was right; it now has a sample.

The corollary is uncomfortable and worth stating plainly: **LabVIEW is not the slow part, and neither
is the server.** `lvai_validate_aixml` runs in 0.08 s, `lvai_aixml_reference` and
`lvai_vi_server_reference` in 0.01 s. Shaving milliseconds off a lookup is worthless; removing one
round trip is worth seven seconds.

## 2. Where the round trips pile up

The generation and editing tools are cheap per call and numerous:

| tool | calls, this session | median duration |
|---|---|---|
| `lvai_run_vi_and_read_values` | **44** | 1.88 s |
| `lvai_generate_vi` | **34** | 2.54 s |
| `lvai_generate_test` | 6 | 27.06 s |
| `lvai_validate_aixml` | 8 | 0.08 s |

44 calls at 1.88 s is **83 s of LabVIEW** and about **5 minutes of latency**. That is the whole
argument in one line.

### 2a. The node-swap loop is the worst offender, and it is mechanical

Of the 44 `lvai_run_vi_and_read_values` calls, **19 were driving the swap machinery** — and the
transcript records **32 helper switches**, i.e. the tool alternated between helpers almost every
call:

| helper | runs |
|---|---|
| `lvmcp_replace_subvis_by_name2.vi` | 6 |
| `lvmcp_list_subvis.vi` | 5 |
| `lvmcp_replace_subvi2.vi` | 4 |
| `lvmcp_replace_path_constants2.vi` | 4 |

That alternation is not incidental — it is forced. `{LV.Diagram}` `SubVIs[]` **re-orders after every
`Replace` and the old references die**, so an index-driven swap has to re-list between every single
swap. The route only became cheap once the helper matched by *name* and re-read the array itself.

**The remaining 19 calls should be 3.** Nothing about the swap needs a model turn between steps: the
mapping is known before the first call.

### 2b. One-off VIs, generated one call at a time

`lvai_generate_vi` was called **34 times for 29 distinct targets**. A large share were the socket VIs
the class-test route needs — one per test slot, because two nodes sharing a socket name cannot be
told apart. Those are pure boilerplate, fully determined by the subject's pane, and there is no batch
form: `lvai_convert_vis_to_aixml` batches *exports*, and nothing batches *generation*.

### 2c. The accessor wizard is the one place where tool time is real

Across sessions `lvai_create_accessors` runs at a median of **15–21 s**, the slowest tool in the set,
and it is called repeatedly because a class has to be done in slices (3 fields, then 2 at a time,
against a client that gives up near 60 s). One session shows **52 calls at a 21.4 s median — 18.6
minutes inside the tool**, plus 52 turns of latency on top.

This is the exception to §1: here the tool really is the cost, because
`Save All This Library` re-checks the whole library on every field.

## 3. What to build, in order of measured payoff

> **Status 2026-08-29: 1–4 are built.** `lvai_generate_class_test`, `lvai_swap_subvis`,
> `lvai_generate_vis` and the self-slicing `lvai_create_accessors` are in the server, 1 050 tests
> green. What is **verified against LabVIEW** is the swap helper — `scripts/lvai_swap_subvis.xml`
> ran end to end, collected the diagram's node names, turned a labelled path constant into a
> `ref{UDClassInst}` class constant with its wire intact, and saved. What is **not yet verified** is
> everything reached only through the new C# tools, because a build takes the `lvai_*` tools away
> until the client restarts: the authoring is pinned by offline tests over the AIXML it emits, and
> the orchestration follows a sequence measured by hand the same day. Exercise all four after a
> restart before trusting them.

1. **Teach `lvai_generate_test` about class subjects.** Today it composes the whole plain-VI route
   (placeholder → generate → retarget) into one call, and for a class it cannot help at all, so the
   entire §3d route is hand-driven — sockets, test AIXML, node swaps, constant swaps, verification.
   Folding that in turns roughly **40 calls into 1**. It is also exactly what CLAUDE.md's
   productisation rule asks for: a repeatable operation on the user's own code, not a one-shot
   investigation.

2. **Ship the swap helpers as a tool.** `lvmcp_list_subvis` / `lvmcp_replace_subvis_by_name` /
   `lvmcp_replace_path_constants` were written from scratch in this session and are now only in a
   `c:\temp` scratch folder — they will be rebuilt next time, which is the failure mode CLAUDE.md
   describes for the Timed Loop slot pattern. A single `lvai_swap_subvis` taking a name→path map plus
   a constant→class map, applied in one run, replaces 19 calls with 1.

3. **Add `lvai_generate_vis`, plural**, mirroring `lvai_convert_vis_to_aixml`. Cached/trivial VIs
   generate concurrently; LabVIEW serialises the rest anyway, so the win is round trips, not
   throughput — which is precisely the win that counts.

4. **Make `lvai_create_accessors` slice itself.** It already reports `nextFromField`; the caller then
   spends a turn per slice for no decision. A mode that loops internally until done, reporting each
   slice, removes the only place where the 60 s client limit leaks into the workflow's shape.

5. **Batch the reference lookups by default.** Already documented (`node=` and `query=` take
   comma-separated lists, 38.9 % fewer characters over one call instead of 18) and already cheap
   server-side at 0.01 s — the point is the 17 turns, worth about two minutes.

## 3a. The LUnit route, measured three times over one afternoon (2026-09-01)

The clearest before/after this document has, because the task was held constant: three classes of
four fields, six test methods each, 12 assertions, same shape. Only the tooling changed.

| | Brille (hand) | Weinglas (hand) | Banane (tools) |
|---|---|---|---|
| class phase | 6.0 min · 29 calls | 8.7 min · 34 calls | **1.8 min · 10 calls** |
| test phase | 21.0 min · 85 calls | 22.9 min · 100 calls | **10.1 min · 42 calls** |
| total calls | 114 | 134 | **52** |

The hand route got **worse** between the first two runs — 85 to 100 calls for the same shape —
because each run rediscovered a little more of the route. That is the cost of a measured-but-unbuilt
capability, and it is not stable: it grows with what the session learns.

`lvai_lunit_add_test_method` and `lvai_run_lunit_tests` took the test phase from 100 calls to 42.
Inside those 42: **528 s wall clock against 102.8 s in tools, a ratio of 5.1 : 1** — *worse* than the
3.6 : 1 baseline, and that is the expected direction. Removing tool-bound work raises the ratio; what
is left is authoring.

### A fourth class, `Apfel`, with the fixes in (2026-09-01)

| | Brille (hand) | Weinglas (hand) | Banane (tools) | Apfel (tools + fixes) |
|---|---|---|---|---|
| class phase | 29 calls | 34 calls | 10 calls | **8 calls** |
| test phase | 85 calls | 100 calls | 42 calls | **40 calls** |
| **total calls** | **114** | **134** | **52** | **48** |

Test phase wall clock: 21.0 → 22.9 → 10.1 → **8.0 min**. Ratio wall : tool held at **5.5 : 1**
against Banane's 5.1 — as expected, removing tool-bound work raises the ratio.

**WALL CLOCK IS NOT COMPARABLE BETWEEN RUNS UNLESS LabVIEW'S WARMTH IS.** The `Apfel` class phase
took *longer* in wall clock than `Banane`'s while using two fewer calls, and the whole difference is
one tool: `lvai_create_accessors` cost **56.3 s against 21.4 s** for an identical four-field class.
LabVIEW had been up 100 s for `Apfel` and 75 minutes for `Banane`. Within the run the second slice
cost 37.2 s against the first slice's 19.1 s, because `Save All This Library` re-checks the growing
library per field. **Use the call count as the primary metric and treat wall clock as valid only
within a run**, or a cold start reads as a regression.

### What the fixes bought, measured rather than assumed

Three changes shipped between `Banane` and `Apfel`, and the run checked all three:

- **Slimmed tool answers.** `lvai_lunit_add_test_method`'s answer had been 86 000 characters and cost
  three grep turns to read. It now arrives in one response with nothing missing — `methodsAdded`,
  `failedAtStep`, and per method `isClassMember`, `classTypedTerminals`, `pathStandInsLeftOnPane`.
  **A tool that saves round trips can spend them again in its own answer**, and that is not visible
  from the tool's design — only from a run that had to grep it.
- **Ordered `projectPhases`.** The `order` field alone would still have read as a prologue; it is the
  `thenWhat` string naming which loop runs after each entry that removed the ambiguity.
- **`lvai_placeholder_subvi` on class panes.** Eight accessors, one message, one turn — replacing
  eight hand-authored socket AIXML files.

### Two measurement gaps this run exposed, both now fixed

**`lvai_placeholder_subvi` reported no duration at all**, so a batch of eight was invisible in the
run's tool-time sum: the batch looked free, and the analysis could not see it either way. **A tool
with no `elapsedMs` cannot be chosen against, which makes it invisible to exactly the method this
document prescribes.** Same for `lvai_lunit_add_test_method`'s `verify` step. Both now report one.

### What is left, in order

1. **Authoring the six test methods' AIXML — ~110 s of the 528, three turns, no tool time.**
   Five of the six files were mechanical transpositions of the previous class's with four names and
   four values substituted. A generator taking `{class, fields, values, cases}` and emitting the
   round trips plus defaults plus independence removes most of it. Only the *choice* of values and
   the descriptions genuinely need a model. **This is now the largest single item in the route.**
2. **`lvai_create_class` should CREATE the `.lvproj` when `projectPath` does not exist.** It already
   edits the file in its `projectEntry` step. Hand-writing a 334-byte minimal project cost **14.8 s
   of a 70.6 s class phase — 21 %** — two turns for boilerplate that never varies.
3. **A `verifyOnly` mode on the class tools.** Closing, describing and grepping the dispatch flags
   cost ~15 s wall and ~0 s tool over three turns. One answer carrying `memberCount`, `inheritsFrom`
   and the `NI.ClassItem.Flags` histogram folds it to one.
4. **Halve the restart budget — already done, by measurement rather than by code.** The `Error 1562`
   lock is per-class and belongs only to the class you add members to, so a class-plus-tests run
   needs ONE restart (after creating the test case class), not two. Confirmed by linking eight
   accessors of a class created in the same session with no 1562 anywhere. At a 30–43 s
   `lvai_ensure_labview` that is ~40 s per run for free. **Finding the leak that causes the lock at
   all would remove the other one**; §4 already notes that the analogous fault was a one-line refnum
   fix.
5. **Batch per-VI `lvai_*` calls into one message.** Six `lvai_swap_subvis` calls in one message:
   LabVIEW serialised them, 16.4 s of tool time, but six turns became one. §4's "fanning out buys
   nothing" is about throughput *inside LabVIEW* and is not an argument against batching round
   trips — reading it that way costs a turn per VI.

### And one anti-pattern that is the orchestrator's, not the tooling's

The Banane class phase came in at 10 calls against Weinglas's 34 for the same result. Part of that is
tooling, but part is that the task prompt **fixed the data model and said not to improve it**. An
underdetermined prompt is paid for in exploration turns. Before building a tool to remove turns, check
whether the prompt is buying them.

## 4. What NOT to optimise

- **Server-side lookup speed.** The embedded-document cache took the 18-term workload from 23.3 ms to
  0.8 ms. Real, and worth nothing next to a 7 s turn.
- **Fanning out `lvai_*` calls for throughput.** Measured: six generate calls issued together took
  559 ms against 543 ms sequentially. LabVIEW serialises. Concurrency helps only where the work never
  reaches LabVIEW — file reading is about 21× on a cold tree, which is why the indexes are built that
  way.
- **Restarting LabVIEW "to be safe".** `lvai_ensure_labview` shows a **30.75 s median** and one
  session called it 48 times — 25 minutes. Most restarts in that session were chasing a leaked class
  refnum that turned out to be a one-line fix.

## 5. The measurement is repeatable

The two scripts are throwaway by design (CLAUDE.md: "Not every working measurement becomes a tool"),
but the method is worth keeping: parse the session JSONL, pair each `tool_use` with its
`tool_result` by id for the tool's own duration, and measure result→next-use gaps for model latency,
discarding gaps over 600 s as human pauses. Re-run it after any of §3 lands, and the ratio in §1 is
the number to watch.
