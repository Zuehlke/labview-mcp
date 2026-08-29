using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Two fixed-order sequences, each collapsed into one call.
///
/// WHY THESE TWO AND NOT A GENERAL BULK ENDPOINT. Measured over one full generation session
/// (DaqReadAndTDMS.vi plus its subVI, 2026-08-25): 55 tool-call turns, 810 s of wall clock, and
/// the LabVIEW-side time the tools themselves reported for every writing step added up to
/// **19 s** - 4.2 s across three validates, 11.4 s across three generations, 1.0 s across two
/// extracts, 2.3 s across three rebuilds. The rest was turn latency, which CLAUDE.md already
/// measures at about 7 s per turn. So the thing worth removing is round trips.
///
/// But only where the ORDER IS KNOWN IN ADVANCE. Most of that session's sequence was not bulkable
/// and still is not: `--list` the anchors, then choose a mapping; measure a pane, then decide
/// whether the pattern or the assignment is the wrong half. Each of those needs a human or a model
/// to read the previous answer. Two sequences never do:
///
///   validate -> convert -> measure the pane        (<see cref="GenerateViAsync"/>)
///   close project -> extract -> edit -> rebuild    (<see cref="PyApplyAsync"/>)
///
/// LATENCY IS THE SMALLER HALF OF THE REASON. Both sequences have a step that is easy to skip and
/// expensive to skip. `lvai_convert_aixml_to_vi` cannot see a badly placed connector pane -
/// CLAUDE.md records that shipping twice - so <see cref="GenerateViAsync"/> will not report success
/// without the measurement. `pylv_rebuild` answers with a `gatesNotChecked` list because it cannot
/// check them itself; <see cref="PyApplyAsync"/> is the composition that can, and closing the
/// project first is the gate whose omission wedged a session on 2026-08-24.
///
/// WHAT IS DELIBERATELY NOT HERE: parallel fan-out. Six generate calls issued together took 559 ms
/// against 543 ms one after another - LabVIEW serialises the work - so concurrency buys nothing and
/// costs one slow VI blocking the rest.
/// </summary>
[McpServerToolType]
internal sealed class BulkTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ---------------------------------------------------------------- generate

    [McpServerTool(Name = "lvai_generate_vi", Destructive = true, OpenWorld = true,
                   Title = "Validate, generate and pane-check a VI in one call")]
    [Description("""
        MUTATING: the whole create-a-VI sequence in one round trip - lvai_validate_aixml, then
        lvai_convert_aixml_to_vi, then lvai_connector_pane on the result. Stops at the first step
        that fails and reports which one, so a failure reads the same as calling the three by hand.
        USE THIS INSTEAD OF THE THREE SEPARATE CALLS when you are generating from AIXML you have
        just authored. It is not a shortcut past them: each step's own answer is returned under
        `steps`, including elapsedMs, so nothing is hidden.
        THE PANE CHECK IS THE POINT, not the saved turns. lvai_convert_aixml_to_vi cannot see a
        badly placed connector pane and neither can a run: a VI whose inputs sit on the output edge
        validates, generates and executes. That defect has shipped twice. `ok` here is false when
        the pane breaches NI's style guide, and the corrected conIdx values come back ready to
        paste - author them into the AIXML and call this again.
        `ok` false with `paneViolations` > 0 still means the .vi FILE WAS WRITTEN. The generation
        succeeded; it is the pane that needs another pass.
        Pass measurePane=false to skip the measurement - only worth it for a scratch probe with no
        connector pane at all.
        Pass panePattern to put the result on a SPECIFIC pane pattern instead of the station
        default - needed when the VI must match a pane that already exists, because a caller's
        wires bind to terminal positions and the same conIdx means different edges on a different
        pattern. It runs before the measurement, so what comes back describes the final pane.
        """)]
    public async Task<string> GenerateViAsync(
        [Description(@"Absolute path to the source AIXML .xml file")] string aiXmlFilePath,
        [Description(@"Absolute path of the .vi to create - WILL BE OVERWRITTEN")] string viPath,
        [Description("Open the created VI in the LabVIEW editor")] bool openVI = false,
        [Description("Measure the connector pane afterwards and gate `ok` on it")]
        bool measurePane = true,
        [Description("""
            Connector pane PATTERN the generated VI must end up on, e.g. 4815. Omit to keep
            whatever LabVIEW.ini's DefaultConPane gives it, which is the normal case.
            Pass it when the VI has to match a pane that already exists - a caller's wires bind to
            terminal positions, and the same conIdx means different edges on a different pattern.
            Applied after generation by the same pylabview step as
            pylv_apply {"op":"conpane"}, so it moves NO terminal and no caller has to change; it
            also closes the active project first, because a rebuild under a loaded VI writes the
            file while LabVIEW keeps serving its stale copy.
            """)]
        int? panePattern = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var total = Stopwatch.StartNew();
            var steps = new JsonArray();
            var aixml = new AixmlTools(connection);

            var validate = await aixml.ValidateAixmlAsync(aiXmlFilePath, timeoutSeconds, ct);
            steps.Add(Step("validate", validate));
            if (Failed(validate))
                return Outcome(false, "validate", steps, total, viPath, null,
                    "The AIXML was refused, so nothing was written. Read the message under " +
                    "steps[0]: \"Unsupported SubVI\" is an unresolvable Call target, \"Object " +
                    "terminal not found\" a misspelled terminal name.");

            var convert = await aixml.ConvertAixmlToViAsync(aiXmlFilePath, viPath, openVI,
                                                            timeoutSeconds, ct);
            steps.Add(Step("convert", convert));
            if (Failed(convert))
                return Outcome(false, "convert", steps, total, viPath, null,
                    "Validation passed but generation did not. The two errors that get here are " +
                    "different and the wording is the tell. Error 1357, \"from that path\": " +
                    "LabVIEW holds THIS path in memory - close the project " +
                    "(lvai_close_active_project) rather than opening it. Error 1051, \"of that " +
                    "name\": something else carries the VI's internal name, and the commonest " +
                    "source is YOUR OWN LAST FAILED VALIDATION of the same _name - measured, " +
                    "twice in a row on the same document. Generate under a fresh name; the old " +
                    "one stays poisoned until LabVIEW restarts.");

            // The pattern repair goes BEFORE the measurement, so what gets reported is the pane
            // the caller will actually get. Doing it the other way round measures a pane that is
            // about to change, which is worse than not measuring at all.
            if (panePattern is { } pattern)
            {
                var repair = await PyApplyAsync(
                    viPath, $$"""[{"op":"conpane","pattern":{{pattern}}}]""",
                    closeProject: true, verify: false, bundleDirectory: null,
                    timeoutSeconds: timeoutSeconds, ct: ct);
                steps.Add(Step("panePattern", repair));
                if (Parsed(repair)?["ok"]?.GetValue<bool>() is not true)
                    return Outcome(false, "panePattern", steps, total, viPath, null,
                        "THE VI WAS WRITTEN and its diagram is sound - what failed is putting it " +
                        "on pattern " + pattern + ". It is still on the station default, so a " +
                        "caller whose wires expect the other pattern will report itself as not " +
                        "executable. Read the panePattern step.");
            }

            if (!measurePane)
                return Outcome(true, null, steps, total, viPath, null,
                    "Generated. The connector pane was NOT measured because measurePane was " +
                    "false, so nothing here says the terminals are on the right edges.");

            var verdict = await new PaneTools(connection).MeasureViAsync(
                viPath, helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
                timeoutSeconds, ct);
            steps.Add(new JsonObject
            {
                ["step"] = "connectorPane",
                ["pattern"] = verdict.Pattern,
                ["violations"] = verdict.Measured ? verdict.Violations : null,
                ["warnings"] = verdict.Measured ? verdict.Warnings : null,
                ["answer"] = verdict.Text,
            });

            if (!verdict.Measured)
                return Outcome(true, null, steps, total, viPath, verdict,
                    "Generated, but the pane could not be measured - so this answer does NOT " +
                    "confirm the terminals are placed correctly. Call lvai_connector_pane " +
                    "yourself to find out why.");

            return verdict.Clean
                ? Outcome(true, null, steps, total, viPath, verdict,
                    "Generated, and the connector pane follows NI's style guide.")
                : Outcome(false, "connectorPane", steps, total, viPath, verdict,
                    "THE VI WAS WRITTEN - generation succeeded. What failed is the connector " +
                    "pane: the terminals are not on the edges NI's style guide puts them on. The " +
                    "corrected conIdx values are in the connectorPane step; write them into the " +
                    "AIXML and call this again. Nothing else about the VI has to change.");
        });

    /// <summary>One entry in the `steps` array: the sub-tool's own answer, kept whole.</summary>
    private static JsonObject Step(string name, string answer) => new()
    {
        ["step"] = name,
        ["errorCode"] = ErrorCode(answer),
        ["answer"] = Parsed(answer),
    };

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                  string viPath, PaneTools.PaneVerdict? pane, string note)
    {
        total.Stop();
        var result = new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["viPath"] = Path.GetFullPath(viPath),
            ["viExistsNow"] = File.Exists(viPath),
            ["viBytes"] = File.Exists(viPath) ? new FileInfo(viPath).Length : 0,
        };
        if (pane is { Measured: true })
        {
            result["panePattern"] = pane.Pattern;
            result["paneViolations"] = pane.Violations;
            result["paneWarnings"] = pane.Warnings;
        }
        result["steps"] = steps;
        // Wall clock for the whole sequence, against the per-step elapsedMs inside `steps`. The
        // difference between them is this server's own overhead, which is the only way to tell a
        // slow LabVIEW from a slow composition without bracketing the call from outside - and
        // bracketing from outside measures the model's turn, not the work.
        result["totalElapsedMs"] = total.ElapsedMilliseconds;
        result["note"] = note;
        return result.ToJsonString(Indented);
    }

    // ---------------------------------------------------------------- pylv_apply

    // ---------------------------------------------------------------- generate, plural

    [McpServerTool(Name = "lvai_generate_vis", Destructive = true, OpenWorld = true,
                   Title = "Generate several VIs from AIXML in one call")]
    [Description("""
        MUTATING: several AIXML files through the whole lvai_generate_vi sequence - validate,
        convert, measure the pane - in ONE call, applied in the order given.
        USE IT FOR BOILERPLATE SETS, which is where it pays: the socket VIs a class unit test needs
        are one per test slot and fully determined by the subject's pane. Measured 2026-08-29 over a
        three-class hierarchy, generating them one at a time cost 34 calls of lvai_generate_vi for
        29 distinct targets - about four minutes, nearly all of it turn latency rather than LabVIEW.
        pairsJson is a JSON ARRAY, one object per VI:
          [{"aixml":"C:\\t\\a.xml","vi":"C:\\t\\a.vi","panePattern":4815},
           {"aixml":"C:\\t\\b.xml","vi":"C:\\t\\b.vi"}]
        `panePattern` is optional and per VI - a socket that has to match an accessor's pane needs
        4815 while the test beside it takes the station default.
        SEQUENTIAL ON PURPOSE, exactly like lvai_convert_vis_to_aixml: six generate calls issued
        together took 559 ms against 543 ms one after another, because LabVIEW serialises the RPC.
        The saving here is round trips, not throughput.
        IT DOES NOT STOP AT THE FIRST FAILURE. Each entry gets its own answer under `results`, so
        one bad AIXML does not cost you the other nine; `ok` is true only when every entry
        generated. A failure reads exactly as lvai_generate_vi's would.
        THE AIXML IS DELETED ON SUCCESS and KEPT ON FAILURE, the same bargain pylv_apply makes with
        its bundle: the intermediate is noise once the .vi exists, and it is the only evidence when
        the .vi does not. Pass keepAixml to keep it either way.
        """)]
    public async Task<string> GenerateVisAsync(
        [Description("JSON array of {aixml, vi, panePattern?} objects, applied in order")]
        string pairsJson,
        [Description("Open each created VI in the LabVIEW editor")] bool openVI = false,
        [Description("Measure each connector pane afterwards and gate `ok` on it")]
        bool measurePane = true,
        [Description("Keep the AIXML sources instead of deleting the ones that succeeded")]
        bool keepAixml = false,
        [Description("Local budget in seconds, per VI")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            List<GenerationRequest> requests;
            try { requests = GenerationRequest.ParseAll(pairsJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            var total = Stopwatch.StartNew();
            var results = new JsonArray();
            int generated = 0, failed = 0, removed = 0;

            foreach (var request in requests)
            {
                if (!File.Exists(request.Aixml))
                {
                    failed++;
                    results.Add(new JsonObject
                    {
                        ["vi"] = request.Vi,
                        ["aixml"] = request.Aixml,
                        ["ok"] = false,
                        ["note"] = $"No AIXML at '{request.Aixml}', so this VI was not attempted.",
                    });
                    continue;
                }

                var answer = await GenerateViAsync(request.Aixml, request.Vi, openVI, measurePane,
                                                   request.PanePattern, timeoutSeconds, ct);
                var parsed = Parse(answer);
                // THE SUB-ANSWER'S OWN VERDICT, NOT `viExistsNow`. That field is true for a file
                // some EARLIER run left behind, so a batch over targets that already existed
                // reported `generated: 6, failed: 0` while five of the six entries carried
                // `ok: false` - LabVIEW had gone down mid-batch and not one AIXML was even
                // validated. Measured 2026-08-29. A green summary over red entries is the worst
                // thing this tool could do, because the entries are exactly what nobody re-reads.
                //
                // `failedAtStep: connectorPane` is the one failure that still WROTE the file: the
                // diagram is sound and the pane needs another pass, which lvai_generate_vi says in
                // those words. It counts as generated here and keeps its own answer.
                var wrote = CountsAsGenerated(parsed as JsonObject);
                if (wrote) generated++; else failed++;

                // Deleted only when the .vi is actually there. A kept AIXML after a failure is the
                // only thing that says WHY - the answer names the node, the file is what you fix.
                if (wrote && !keepAixml)
                {
                    try { File.Delete(request.Aixml); removed++; }
                    catch (Exception failure) when (failure is IOException
                                                    or UnauthorizedAccessException) { }
                }

                results.Add(new JsonObject
                {
                    ["vi"] = request.Vi,
                    ["aixml"] = wrote && !keepAixml ? null : request.Aixml,
                    ["ok"] = wrote,
                    ["answer"] = parsed,
                });
            }

            return Json.Document(new JsonObject
            {
                ["ok"] = failed == 0 && requests.Count > 0,
                ["requested"] = requests.Count,
                ["generated"] = generated,
                ["failed"] = failed,
                ["aixmlDeleted"] = removed,
                ["results"] = results,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = failed == 0
                    ? $"{generated} VI(s) generated in order. " + (keepAixml
                        ? "The AIXML sources were kept."
                        : $"{removed} AIXML source(s) deleted - the .vi is the artefact.")
                    : $"{failed} of {requests.Count} did NOT generate; their AIXML was kept and is " +
                      "named in `results`. Each entry carries the same answer lvai_generate_vi " +
                      "would have given, so read that rather than this summary.",
            });
        });

    /// <summary>
    /// Did THIS run write the VI? Read from <see cref="GenerateViAsync"/>'s own verdict, never from
    /// <c>viExistsNow</c> — that field is true for a file some EARLIER run left behind.
    ///
    /// Measured 2026-08-29: a batch of six sockets that all already existed reported
    /// <c>generated: 6, failed: 0</c> while five of the six entries carried <c>ok: false</c>,
    /// because LabVIEW had gone down mid-batch and not one AIXML was even validated. A green
    /// summary over red entries is the worst thing a batch tool can do, since the entries are
    /// exactly what nobody re-reads.
    ///
    /// <c>failedAtStep: connectorPane</c> is the one failure that still WROTE the file — the
    /// diagram is sound and only the pane needs another pass — so it counts, and keeps its own
    /// answer for the reader.
    /// </summary>
    internal static bool CountsAsGenerated(JsonObject? answer) =>
        answer?["ok"]?.GetValue<bool>() is true
        || (answer?["failedAtStep"]?.GetValue<string>() == "connectorPane"
            && answer["viExistsNow"]?.GetValue<bool>() is true);

    /// <summary>One entry of <see cref="GenerateVisAsync"/>'s <c>pairsJson</c>.</summary>
    internal sealed record GenerationRequest(string Aixml, string Vi, int? PanePattern)
    {
        public static List<GenerationRequest> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("pairsJson is empty; name at least one VI to generate.");

            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (JsonException bad)
            { throw new ArgumentException($"pairsJson is not JSON: {bad.Message}"); }

            if (parsed is not JsonArray array || array.Count == 0)
                throw new ArgumentException(
                    "pairsJson must be a non-empty JSON array, e.g. " +
                    "[{\"aixml\":\"C:\\\\t\\\\a.xml\",\"vi\":\"C:\\\\t\\\\a.vi\"}].");

            return [.. array.Select((entry, i) => One(entry, i))];
        }

        private static GenerationRequest One(JsonNode? entry, int index)
        {
            if (entry is not JsonObject o)
                throw new ArgumentException($"pairsJson[{index}] is not an object.");

            var aixml = o["aixml"]?.GetValue<string>();
            var vi = o["vi"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(aixml) || string.IsNullOrWhiteSpace(vi))
                throw new ArgumentException(
                    $"pairsJson[{index}] needs both \"aixml\" and \"vi\" as absolute paths.");

            int? pattern = o["panePattern"] is { } p && int.TryParse(p.ToString(), out var value)
                ? value : null;
            return new GenerationRequest(Path.GetFullPath(aixml), Path.GetFullPath(vi), pattern);
        }
    }

    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return null; }
    }

    [McpServerTool(Name = "pylv_apply", Destructive = true, OpenWorld = true,
                   Title = "Close, extract, edit and rebuild a VI in one call")]
    [Description("""
        MUTATING: the whole pylabview edit cycle in one round trip - close the active project,
        pylv_extract, run the helper scripts you name, pylv_rebuild, and AIXML-export the result to
        check the edit landed. The bundle is an implementation detail; it is deleted on success and
        KEPT (and named) on failure.
        WITH NO OPERATIONS IT IS READ-ONLY AND INSPECTS: extract, then every helper script's listing
        mode at once - the pane (`--show`), the subVI link table (`--list`), and the diagram
        comments with their placeable anchors (`--list`). That is the call to make FIRST, because
        the mapping you pass back in as operations is only knowable from those listings. Nothing is
        rebuilt, the .vi is not touched, and the active project is NOT closed - inspect mode has no
        side effects at all.
        operationsJson is a JSON ARRAY, applied IN ORDER:
          {"op":"conpane","pattern":4815}
              change the connector pane PATTERN, moving no terminal - so no caller has to change.
              This is the fix for a pane whose conIdx values were cloned from a VI on another
              pattern. A genuinely wrong ASSIGNMENT is not repairable here: regenerate from AIXML.
          {"op":"retarget","from":"Old.vi","to":"New.vi","path":"C:\\dir\\New.vi"}
              point a subVI Call at a different subVI. Give `path` whenever the new VI is not in
              the same folder as the old one. A POLYMORPHIC target needs TWO entries, one for the
              instance and one for the wrapper, or the diagram caption keeps naming NI's wrapper.
              `from` takes the qualified name the inspect listing prints - a library-owned subVI
              reads `NI_Gmath.lvlib:Error Function.vi` - or just the file name when only one link
              ends in it. `to` is always a plain file name; a library-owned NEW target is refused,
              because its owning-library path cannot be derived.
              The new subVI must keep the connector pane contract - check both with
              lvai_connector_pane first; this tool cannot. When it is breached the symptom does
              not mention linking at all: LabVIEW reports the CALLER as not executable.
          {"op":"placeLabels","place":"9001:130,9002:140","side":"auto","gap":20}
              move diagram comments onto the nodes they describe. `side` is auto|above|below and
              auto is right nearly always: a comment about a subVI CALL goes below it, because the
              subVI's own label already occupies the space above.
        WHY THIS IS ONE TOOL AND NOT FOUR CALLS. pylv_rebuild answers with a `gatesNotChecked` list
        because it cannot check them from where it stands. The first of those gates is the
        expensive one: LabVIEW does not lock a .vi, so a rebuild under a loaded VI succeeds and
        LabVIEW then keeps serving its stale in-memory copy - a verification run afterwards
        confirms the VI you REPLACED. Worse, the usual way to release it (lvai_open_file) makes
        LabVIEW COMPILE the VI, and the VICD blocks that leaves are copied through unparsed, so the
        rebuilt file carries compiled code describing the state before your edit. Measured
        2026-08-24 on one VI pair: 0 VICD blocks generated, ran and wrote its files; 3 VICD blocks
        returned 1039 "VI was aborted" and then wedged LabVIEW into a restart. Hence closeProject
        defaults to true and runs FIRST.
        Needs no running LabVIEW for the pylabview half. closeProject and verify do; when LabVIEW
        is unreachable both are reported as skipped, with why - an unreachable LabVIEW cannot be
        holding the VI in memory either, so skipping the close is safe rather than silent.
        """)]
    public async Task<string> PyApplyAsync(
        [Description(@"Absolute path to the .vi to edit - WILL BE OVERWRITTEN unless operations is empty")]
        string viPath,
        [Description("""
            JSON array of operations, applied in order. Omit or pass [] for the read-only inspect
            mode, which extracts and returns every listing instead of rebuilding.
            """)]
        string? operationsJson = null,
        [Description("Save and close the project active in the IDE before extracting")]
        bool closeProject = true,
        [Description("AIXML-export the rebuilt VI and report its Call targets")]
        bool verify = true,
        [Description("""
            Where to put the extracted bundle. Defaults to a fresh temp directory, deleted on
            success. Pass a path to keep it - useful when an operation needs debugging.
            """)]
        string? bundleDirectory = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            if (PyLabview.Locate() is not { } bundle)
                return Json.Error("notProvisioned",
                    "The pylabview bundle is not present. Run tools\\pylabview\\provision.ps1.");

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory",
                    "No scripts folder next to the exe - lvai_status reports it as " +
                    "scriptsDirectory. The helper scripts this tool drives live there.");

            List<Operation> operations;
            try { operations = Operation.ParseAll(operationsJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();
            var keepBundle = bundleDirectory is not null;
            var directory = Path.GetFullPath(bundleDirectory ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "bundles",
                $"{Path.GetFileNameWithoutExtension(viPath)}-{Environment.ProcessId}-{total.GetHashCode():x}"));

            // 1. the gate. First, because everything after it is worthless under a loaded VI - but
            // ONLY when something is going to be written. Inspect mode must not close the project
            // somebody is working in: it reads the VI and rebuilds nothing, so the gate it protects
            // (LabVIEW serving a stale in-memory copy of a file we replaced) cannot be reached.
            if (closeProject && operations.Count > 0)
                steps.Add(await CloseStepAsync(timeoutSeconds, ct));

            // 2. extract
            var extract = await new PyLabviewTools(connection).ExtractAsync(
                viPath, directory, annotate: true, timeoutSeconds, ct);
            steps.Add(Step("extract", extract));
            if (!Succeeded(extract))
                return PyOutcome(false, "extract", steps, total, viPath, directory, true,
                    "pylabview could not read the VI, so nothing was changed.");

            if (Field(extract, "mainXml") is not { } mainXml)
                return PyOutcome(false, "extract", steps, total, viPath, directory, true,
                    "The extract reported no mainXml, which this tool cannot continue without.");

            var heaps = Directory.GetFiles(directory, "*_BDHb.xml");

            // 3a. no operations: every listing at once, and stop before touching the .vi
            if (operations.Count == 0)
            {
                foreach (var listing in Listings(mainXml, heaps, directory))
                    steps.Add(await RunScriptAsync(bundle, scripts, listing.Script, listing.Args,
                                                   listing.Label, timeoutSeconds, ct));
                return PyOutcome(true, null, steps, total, viPath, directory, keepBundle,
                    "INSPECTED ONLY - no operations were given, so the VI is untouched and the " +
                    "active project was NOT closed. The listings above are what an operations " +
                    "array is written from: uids on the left of a placeLabels pair are comments, " +
                    "uids on the right are anchors.");
            }

            // 3b. apply, in the order given
            foreach (var operation in operations)
            {
                if (operation.NeedsSingleHeap && heaps.Length != 1)
                    return PyOutcome(false, operation.Op, steps, total, viPath, directory, true,
                        heaps.Length == 0
                            ? $"'{operation.Op}' edits the block diagram, and this bundle has no " +
                              "diagram heap (no *_BDHb.xml). A VI with no block diagram cannot be " +
                              "edited this way."
                            : $"'{operation.Op}' needs one diagram heap and this bundle has " +
                              $"{heaps.Length}. Name the heap yourself and drive the script " +
                              "directly - this tool will not guess which one you meant.");

                var step = await RunScriptAsync(bundle, scripts, operation.Script,
                    operation.Arguments(mainXml, heaps.FirstOrDefault(), directory),
                    operation.Op, timeoutSeconds, ct);
                steps.Add(step);
                if (step["exitCode"]?.GetValue<int>() != 0)
                    return PyOutcome(false, operation.Op, steps, total, viPath, directory, true,
                        $"The '{operation.Op}' operation failed, so the rebuild was NOT run and " +
                        "the .vi on disk is untouched. The bundle is kept - the operations " +
                        "before this one have been applied to it.");
            }

            // 4. rebuild
            var rebuild = await new PyLabviewTools(connection).RebuildAsync(mainXml, viPath, timeoutSeconds, ct);
            steps.Add(Step("rebuild", rebuild));
            if (!Succeeded(rebuild))
                return PyOutcome(false, "rebuild", steps, total, viPath, directory, true,
                    "The edits applied but the rebuild failed. The bundle is kept.");

            // 5. verify - the third gate pylv_rebuild names and cannot check
            if (verify) steps.Add(await VerifyStepAsync(viPath, timeoutSeconds, ct));

            return PyOutcome(true, null, steps, total, viPath, directory, keepBundle,
                verify
                    ? "Rebuilt. The verify step is LabVIEW's OWN export of the result - read its " +
                      "callTargets and confirm the change you meant is there. A rebuild reporting " +
                      "ok says nothing about whether the edit was sound."
                    : "Rebuilt. NOTHING HERE CONFIRMS THE EDIT - verify was false, so LabVIEW has " +
                      "not read the file back. Export it yourself before believing this.");
        });

    /// <summary>
    /// The close, as a step rather than a precondition. An unreachable LabVIEW is reported as a
    /// skip with the reason, not as a failure: nothing can be holding the VI in memory when the
    /// IDE is not running, so the gate this protects is already satisfied.
    /// </summary>
    private async Task<JsonObject> CloseStepAsync(int timeoutSeconds, CancellationToken ct)
    {
        var answer = await new CloseTools(connection).CloseActiveProjectAsync(
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct);

        // 1055 is "no project was active", which is the desired end state, not a problem.
        var code = ErrorCode(answer);
        return new JsonObject
        {
            ["step"] = "closeActiveProject",
            ["ran"] = Parsed(answer) is JsonObject o && o["ok"]?.GetValue<bool>() != false,
            ["errorCode"] = code,
            ["note"] = code == 1055
                ? "Error 1055 means no project was active - nothing to close, which is the state " +
                  "this step is trying to reach."
                : null,
            ["answer"] = Parsed(answer),
        };
    }

    /// <summary>
    /// LabVIEW's own reading of the rebuilt file. The distinct `target=` values are pulled out
    /// because a retarget is the operation whose success is least visible otherwise - the script
    /// reports how many strings it rewrote, which is not the same as LabVIEW resolving them.
    /// </summary>
    private async Task<JsonObject> VerifyStepAsync(string viPath, int timeoutSeconds,
                                                   CancellationToken ct)
    {
        var exportPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            $"{Path.GetFileNameWithoutExtension(viPath)}-verify.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);

        var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
            viPath, exportPath, returnContent: false, maxContentChars: 0, timeoutSeconds,
            refresh: true, ct);

        var targets = new JsonArray();
        if (File.Exists(exportPath))
            foreach (var target in Regex.Matches(await File.ReadAllTextAsync(exportPath, ct),
                                                 @"target=""([^""]*)""")
                         .Select(m => m.Groups[1].Value).Distinct().Order())
                targets.Add(target);

        var step = new JsonObject
        {
            ["step"] = "verify",
            ["ran"] = Parsed(answer) is JsonObject o && o["ok"]?.GetValue<bool>() != false,
            ["errorCode"] = ErrorCode(answer),
            ["callTargets"] = targets,
            ["exportPath"] = exportPath,
        };

        // The export above proves the call POINTS at the right VI. It cannot prove the wires into
        // it still carry the right type, and after a retarget onto a subVI whose pane holds
        // typedefs they do not: the stub was cloned with the bare underlying type, so every wired
        // input comes out coerced. Nothing else in this cycle sees that - validation, the
        // retarget and a run all pass - which is why the check belongs here, at the step that
        // creates the defect, rather than at generation time where the call still targets the stub
        // and no dot can exist yet.
        var dots = await new TypedefTools(connection).CoercedTerminalsAsync(viPath,
            timeoutSeconds, ct);

        if (dots is null)
        {
            step["coercionDots"] = null;
        }
        else
        {
            step["coercionDots"] = dots.Count;
            if (dots.Count > 0)
            {
                var where = new JsonArray();
                foreach (var d in dots) where.Add(d);
                step["coercedTerminals"] = where;
            }
        }

        step["note"] = "Exporting does not keep the VI in memory - measured - so the path can " +
                       "still be regenerated afterwards. Only lvai_open_file burns it." +
                       (dots is { Count: > 0 }
                           ? $" {dots.Count} terminal(s) now wear a COERCION DOT: the retarget " +
                             "linked, but the subVI's pane carries typedefs the stub could not. " +
                             "Repair with lvai_bind_typedef_constants."
                           : "");
        return step;
    }

    private static string PyOutcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                    string viPath, string bundleDirectory, bool keepBundle,
                                    string note)
    {
        total.Stop();
        if (!keepBundle && Directory.Exists(bundleDirectory))
            try { Directory.Delete(bundleDirectory, recursive: true); }
            catch (IOException) { keepBundle = true; }
            catch (UnauthorizedAccessException) { keepBundle = true; }

        return new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["viPath"] = Path.GetFullPath(viPath),
            ["viBytes"] = File.Exists(viPath) ? new FileInfo(viPath).Length : 0,
            ["bundleDirectory"] = keepBundle ? bundleDirectory : null,
            ["bundleKept"] = keepBundle,
            ["steps"] = steps,
            ["totalElapsedMs"] = total.ElapsedMilliseconds,
            ["note"] = note,
        }.ToJsonString(Indented);
    }

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// One entry of operationsJson. Parsed into a record rather than handled as free JSON so a
    /// misspelling is refused HERE, by name, before anything on disk has been touched - a bad
    /// operation discovered after the extract would leave a half-applied bundle behind.
    /// </summary>
    internal sealed record Operation(string Op, string Script, bool NeedsSingleHeap,
                                     IReadOnlyList<string> Tail)
    {
        public IEnumerable<string> Arguments(string mainXml, string? heapXml, string directory) =>
            Op switch
            {
                // conpane reads the whole bundle; the other two are diagram edits and need the heap.
                "conpane" => [directory, .. Tail],
                "retarget" => [mainXml, heapXml!, .. Tail],
                _ => [heapXml!, .. Tail],
            };

        public static List<Operation> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];

            JsonNode? node;
            try { node = JsonNode.Parse(json); }
            catch (JsonException bad)
            {
                throw new ArgumentException(
                    $"operationsJson is not valid JSON: {bad.Message}. It is a JSON array, for " +
                    """example [{"op":"conpane","pattern":4815}].""");
            }

            if (node is not JsonArray array)
                throw new ArgumentException(
                    "operationsJson must be a JSON ARRAY of operations, even for a single one.");

            return [.. array.Select((entry, i) => Parse(entry, i))];
        }

        private static Operation Parse(JsonNode? entry, int index)
        {
            if (entry is not JsonObject o)
                throw new ArgumentException($"operationsJson[{index}] is not an object.");

            var op = o["op"]?.GetValue<string>()
                ?? throw new ArgumentException($"operationsJson[{index}] has no \"op\".");

            switch (op)
            {
                case "conpane":
                    var pattern = Int(o, "pattern", index, op);
                    return new Operation(op, "pylv-conpane.py", false,
                        ["--pattern", pattern.ToString()]);

                case "retarget":
                    var tail = new List<string> { Text(o, "from", index, op), Text(o, "to", index, op) };
                    if (o["path"]?.GetValue<string>() is { Length: > 0 } path)
                        tail.AddRange(["--path", path]);
                    return new Operation(op, "pylv-retarget-subvi.py", true, tail);

                case "placeLabels":
                    var place = new List<string> { "--place", Text(o, "place", index, op) };
                    if (o["side"]?.GetValue<string>() is { Length: > 0 } side)
                    {
                        if (side is not ("auto" or "above" or "below"))
                            throw new ArgumentException(
                                $"operationsJson[{index}] \"side\" is '{side}'; it must be auto, " +
                                "above or below.");
                        place.AddRange(["--side", side]);
                    }
                    if (o["gap"] is { } gap) place.AddRange(["--gap", gap.GetValue<int>().ToString()]);
                    return new Operation(op, "pylv-place-labels.py", true, place);

                default:
                    throw new ArgumentException(
                        $"operationsJson[{index}] has op '{op}', which this build does not know. " +
                        "The three are conpane, retarget and placeLabels.");
            }
        }

        private static string Text(JsonObject o, string key, int index, string op) =>
            o[key]?.GetValue<string>() is { Length: > 0 } value ? value
                : throw new ArgumentException(
                    $"operationsJson[{index}] is a '{op}' operation and needs \"{key}\".");

        private static int Int(JsonObject o, string key, int index, string op) =>
            o[key] is { } value ? value.GetValue<int>()
                : throw new ArgumentException(
                    $"operationsJson[{index}] is a '{op}' operation and needs \"{key}\".");
    }

    /// <summary>Every helper script's listing mode, which is what inspect mode runs.</summary>
    private static IEnumerable<(string Label, string Script, string[] Args)> Listings(
        string mainXml, string[] heaps, string directory)
    {
        yield return ("connectorPane", "pylv-conpane.py", [directory, "--show"]);
        if (heaps.Length == 1)
        {
            yield return ("subViLinks", "pylv-retarget-subvi.py", [mainXml, heaps[0], "--list"]);
            yield return ("diagramLabels", "pylv-place-labels.py", [heaps[0], "--list"]);
        }
    }

    private static async Task<JsonObject> RunScriptAsync(
        PyLabview.Bundle bundle, string scriptsDirectory, string script, IEnumerable<string> args,
        string label, int timeoutSeconds, CancellationToken ct)
    {
        var path = Path.Combine(scriptsDirectory, script);
        if (!File.Exists(path))
            return new JsonObject
            {
                ["step"] = label,
                ["exitCode"] = -1,
                ["stderr"] = $"No helper script at '{path}'. It ships under scripts\\ next to the " +
                             "exe; a source checkout has it under scripts\\ in the repository.",
            };

        var run = await PyLabview.RunAsync(bundle, path, args, Rpc.ClampToolWait(timeoutSeconds), ct);
        return new JsonObject
        {
            ["step"] = label,
            ["script"] = script,
            ["exitCode"] = run.ExitCode,
            ["stdout"] = run.StdOut.TrimEnd(),
            ["stderr"] = run.StdErr.Length == 0 ? null : run.StdErr.TrimEnd(),
            ["elapsedMs"] = run.ElapsedMs,
        };
    }

    // ---------------------------------------------------------------- reading sub-answers

    /// <summary>
    /// A sub-tool's answer as a node, or the raw string when it is not JSON at all. Kept whole
    /// rather than summarised: the whole point of composing these is that a failure must read the
    /// same as it would from calling the tool directly.
    /// </summary>
    private static JsonNode? Parsed(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static int? ErrorCode(string answer) =>
        Parsed(answer) is JsonObject o && o.TryGetPropertyValue("errorCode", out var code)
            ? code?.GetValue<int>() : null;

    private static string? Field(string answer, string key) =>
        Parsed(answer) is JsonObject o ? o[key]?.GetValue<string>() : null;

    /// <summary>An lvai RPC answer failed when its errorCode is not 0, or when Rpc.Guard wrapped it.</summary>
    private static bool Failed(string answer) => ErrorCode(answer) is not 0 || Guarded(answer);

    /// <summary>A pylv tool answer succeeded when it says so - those carry `ok`, not `errorCode`.</summary>
    private static bool Succeeded(string answer) =>
        Parsed(answer) is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    private static bool Guarded(string answer) =>
        Parsed(answer) is JsonObject o && o.TryGetPropertyValue("ok", out var ok) &&
        ok?.GetValue<bool>() == false;
}
