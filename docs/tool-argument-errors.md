# Why a tool call failed with no detail

Reported as [issue #19](https://github.com/Zuehlke/labview-mcp/issues/19), measured and fixed on
2026-08-14. Read this before diagnosing an argument problem, because the symptom points at the
wrong culprit and the report itself got it wrong.

## The symptom

A call with the wrong spelling of a parameter answered exactly this, and nothing else:

```json
{ "isError": true, "content": [ { "type": "text",
  "text": "An error occurred invoking 'lvai_describe_vi'." } ] }
```

No parameter named, no hint. What it cost the reporter: **nine identical failures**, and a session
spent on a LabVIEW-version-mismatch hypothesis before anyone suspected a parameter name.

## Where the message comes from — not where it looks like

The issue concluded that the client's schema validation rejected the call "before requests reach the
server". **It does not.** Measured by driving the built server over raw stdio with no client in
between: the same call, the same sentence. The chain is

1. the request arrives, and the SDK binds the JSON arguments to the C# parameters;
2. binding throws — inside `AIFunctionMcpServerTool.InvokeAsync`, which the stderr stack trace shows;
3. `McpServerImpl` masks the exception to `An error occurred invoking '<tool>'.`;
4. the exception and its stack go to **stderr**, which for a stdio server is the log — no client
   looks there.

So the useful diagnosis existed all along and was written to a channel nobody reads. That is also
the good news: because the call does reach us, a server-side answer is possible at all.

The same run measured the other half: **an undeclared argument key is dropped in silence.** So
`{"vi_path": "..."}` was indistinguishable from `{}` — hence "missing required argument", hence the
masked sentence.

## What the server does now

`Infra/DiagnosingTool.cs` wraps every registered tool (`DelegatingMcpServerTool`, the SDK's own
extension point) and `Infra/ToolArguments.cs` holds the logic. Two steps, in order:

1. **Fold.** A supplied key that differs from a declared one only in `_`, `-` or case is renamed to
   the declared spelling. `vi_path` → `viPath`, `max_content_chars` → `maxContentChars`. A key that
   is already declared is never overwritten by a variant, and two declared names sharing a fold are
   left alone rather than guessed between.
2. **Refuse an unrecognised name.** A supplied key that is not declared *and* whose fold matches no
   declared name is the caller believing they passed something that never arrived. The call is
   REFUSED by name rather than run without it — running it means doing something other than what was
   asked, and the error that follows then describes the wrong thing. A key whose fold *does* match a
   declared name is left alone, which is the caller who sent `viPath` **and** `vi_path`: the intended
   value is present, so refusing would be churn.
3. **Report a missing required key.** Still absent after all that, it answers with data:

```json
{ "ok": false, "errorKind": "badArguments",
  "error": "lvai_convert_vi_to_aixml was called without the required argument 'aiXmlFilePath'.",
  "detail": {
    "tool": "lvai_convert_vi_to_aixml",
    "missing": [ "aiXmlFilePath" ],
    "received": [ "viPath" ],
    "accepted": { "viPath": "string, required", "aiXmlFilePath": "string, required",
                  "returnContent": "boolean, default true", "maxContentChars": "integer, default 60000",
                  "timeoutSeconds": "integer, default 180", "refresh": "boolean, default false" },
    "hint": "Argument names are camelCase and are listed under 'accepted'. ..." } }
```

`accepted` is read out of the tool's own served schema, so it cannot drift from what `tools/list`
advertises. The schema keeps its `required` array: nothing was made optional to achieve this, and no
tool signature moved.

A call that fails **inside** the binding for any other reason - a wrong JSON type, mostly - gets the
same envelope with the exception message that would otherwise have been masked.

## The string-retry once destroyed the arguments beside it

Fixed 2026-08-31, and worth keeping because the symptom pointed at the wrong parameter.

The retry that makes a JSON-document argument reachable — a client sends `constantsJson` as a real
JSON array, the binder wants a `string`, so the arguments are re-sent as their own JSON text — used
to stringify **every** non-string value. A bool or a number in the same call was collateral: `true`
became `"true"`, which the bool binder then rejects.

| call | result before |
|---|---|
| `lvai_swap_subvis` with a valid `constantsJson` **and** `verify: true` | refused, `badArguments` |
| `lvai_run_vi_and_read_values` with `inputsJson` **and** `includeRawXml: false` | refused, same |
| either, with the bool **omitted** | worked |
| any tool with a bool and no document argument | worked — nothing triggered a retry |

Two things made this hard to read. The reported error is deliberately the **first** failure, because
that is the one naming the type the binder wanted — so it named the document parameter and never
mentioned the bool. And the advice that appeared to work, "omit `verify`", is a real workaround for
the wrong cause: the bool was never the problem, the retry's blast radius was.

`ToolArguments.StringTyped(schema)` now limits the retry to the parameters the schema declares as
`string`, plus any property carrying no `type` at all — the retry exists for text-hungry parameters,
and an untyped one is exactly where that cannot be ruled out. Regression tests in
`ToolArgumentsTests`; the shape of the fixture is `lvai_swap_subvis`' own.

**The general lesson: a repair applied to a whole argument set needs the schema to scope it.** The
original fix was written for the parameter that was broken and reached every parameter beside it.

## Limits worth knowing

- **A wrong type is reported but not located.** The SDK's own message is
  `The JSON value could not be converted to System.Int32. Path: $ | LineNumber: 0 |
  BytePositionInLine: 6.` — it does not name the argument. `received` plus `accepted` is how to find
  it; the types in `accepted` are the authority.
- **Folding covers near-misses of declared names, not synonyms.** `vi_path` works because `viPath`
  exists; `path` or `file` matches nothing, and until 2026-09-14 an unknown key was simply ignored.
  It is now refused by name — see the section below, which is the whole reason this paragraph
  changed.
- **The wrapper must be registered last.** `WithArgumentDiagnostics()` rewrites the tool
  registrations that are already in the collection, so a tool registered after it is not wrapped.
  `DiagnosingToolTests` asserts that every served tool comes back wrapped, so an SDK upgrade that
  registers tools differently fails there rather than silently restoring the masked sentence.

## AN UNKNOWN KEY WAS IGNORED FOR 18 DAYS BECAUSE THIS PAGE CALLED IT IMPOSSIBLE

The bullet above used to end: *"This layer cannot do better on its own — an unknown key is dropped
by the MCP contract and an all-optional tool has nothing to report as missing."* **That was wrong,
and being written down as a limit is what kept anyone from looking again.**

The wrapper holds the served schema and the supplied keys in the same method. Naming a key that
matches nothing is four lines. Nothing in the MCP contract requires a server to *run* a call whose
arguments it cannot honour — "an unknown key is ignored" describes the binder, not the tool.

**What the cost of that sentence was, measured twice on the same tool:**

| when | the key | what it cost |
|---|---|---|
| 2026-08-27 | `filePath` | two calls and a file inspection, spent on the disk and the XML |
| 2026-09-14 | `path` | five `Error 7` answers over three real paths, an A/B on the foreground window that refuted a hypothesis nobody needed, and a LabVIEW kill and restart |

The second session read this very page's 2026-08-27 bullet *after* the detour, and the paragraph it
found was the one saying the situation was unfixable.

**And the tool's own description compounded it with a false mechanism.** `lvai_open_file` said
`filePath` "is folded onto the closest declared one, so it lands on `viPath`". It is not: `Fold`
normalises `_`, `-` and case only, so `filePath` folds to `filepath`, which matches nothing and is
dropped. The distinction matters because the two produce different searches — "my project was opened
as a VI" sends you looking for a path that was passed, and nothing was passed at all. Corrected in
`ActionTools.cs`, in `ActionToolsTests.cs`, and here.

**Two rules come out of it, and they are the durable part:**

- **A limit is a measurement, not a conclusion.** "This layer cannot do better" was an inference from
  one failing case, written in the same voice as the measurements around it. Write what was measured
  (`an unknown key is dropped by the binder`) and leave the impossibility claim out, because that is
  the sentence that stops the next reader.
- **When a tool's description explains a MECHANISM, check the mechanism.** A description is read at
  the moment someone is already confused, so a wrong one costs more than no explanation would.

Both holes are closed: the argument layer refuses an unrecognised name, and `lvai_open_file` refuses
a call that names no file at all (`ActionTools.OpenFilePrecheck`, with `OpenFilePrecheckTests`
covering all three of its doors — the third had neither a guard nor a test).

### AND THE ARGUMENT LAYER NEVER FIRES FROM THE DESKTOP CLIENT — the tool guard is the only one that does

Measured on acceptance, 2026-09-14, and it changes which of the two fixes matters. **The Claude
desktop client validates arguments against the served schema and DROPS an undeclared key before
sending**, so the server never sees it:

| route | the same call, `{"path": "…RerunProbe.lvproj"}` |
|---|---|
| Claude desktop client | `received: {viPath: null, viName: null, projectPath: null, projectName: null}` — the tool's own **nothing-to-open** guard answers |
| raw stdio, no client | `unrecognised: ["path"]`, `received: ["path"]` — the **argument layer** answers, naming the key |

So from the client, a call with a made-up name and a call with no arguments are **indistinguishable**,
and only the tool-level guard stands between either and LabVIEW's misleading `Error 7`. The argument
layer is not redundant — it is what a client forwarding unknown keys gets, and the MCP contract does
not require a client to strip them — but it cannot be relied on to be reached.

**The rule that follows: a tool whose parameters are ALL optional needs its own guard for "these
arguments ask for nothing".** No argument layer can supply it, because there is nothing there to
misspell, and it is exactly the shape that produces a confident answer about the wrong thing.

The regression cases were measured over stdio in the same run, because the new refusal sits directly
in front of the fold it must not swallow:

| sent | expected | got |
|---|---|---|
| `vi_path` | folded onto `viPath`, then the swap guard speaks | swap guard — so the fold ran |
| `viPath` **and** `vi_path` | tolerated, the correct value wins | swap guard |
| `filePath` | refused as unrecognised | refused, naming `filePath` |

## Re-measuring it

No client and no test host needed - a built exe, and JSON-RPC lines on stdin. Send `initialize`,
then `notifications/initialized`, then:

```json
{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"lvai_describe_vi","arguments":{"vi_path":"C:\\x\\My.vi"}}}
```

Before the fix that answered the masked sentence; after it, the call runs with `viPath` folded in.
`{"name":"lvai_convert_vi_to_aixml","arguments":{"viPath":"C:\\x\\My.vi"}}` is the missing-argument
case, and `{"timeoutSeconds":"soon"}` the wrong-type one. Read the server's **stderr** alongside: it
is where the pre-fix detail always was.

The unrecognised-name case is `{"name":"lvai_open_file","arguments":{"path":"C:\\x\\App.lvproj"}}`.
Before 2026-09-14 that answered `Error 7, File not found` from LabVIEW; it now answers
`badArguments` naming `path`. And `{"name":"lvai_open_file","arguments":{}}` — which no argument
layer can catch, because there is nothing there to misspell — is refused by the tool itself.
