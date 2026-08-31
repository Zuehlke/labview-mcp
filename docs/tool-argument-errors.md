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
2. **Report.** A required key still absent answers with data:

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
  exists; `path` or `file` is still an unknown key, and an unknown key is still ignored - that is the
  MCP contract, not something this layer overrides.
- **On a tool whose parameters are ALL optional, a dropped key produces no diagnostic at all - and
  the error that surfaces blames the wrong thing.** Measured 2026-08-27: `lvai_open_file` was called
  with `filePath`, which is not a near-miss of `viPath` or `projectPath`, so it was dropped. Nothing
  was missing - every parameter defaults to empty - so `badArguments` never fired, LabVIEW received
  `<Not A Path>`, and the answer was

  ```
  Error 7 occurred at ... OpenFile.vi
  LabVIEW: (Hex 0x7) File not found. The file might be in a different location or deleted.
  ```

  on a `.lvproj` that was present, well-formed and readable - `lvai_describe_project` read it in the
  same minute. Two calls and a file inspection went into that before the argument name was checked.
  **So a "file not found" from a path you can `ls` means read the tool's parameter names first**, and
  the diagnostic to reach for is not the filesystem: it is the tool schema. This layer cannot do
  better on its own - an unknown key is dropped by the MCP contract and an all-optional tool has
  nothing to report as missing.
- **The wrapper must be registered last.** `WithArgumentDiagnostics()` rewrites the tool
  registrations that are already in the collection, so a tool registered after it is not wrapped.
  `DiagnosingToolTests` asserts that every served tool comes back wrapped, so an SDK upgrade that
  registers tools differently fails there rather than silently restoring the masked sentence.

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
