using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Repointing subVI nodes and diagram constants through LabVIEW's own <c>Replace</c>, many at a time.
///
/// WHY THIS EXISTS RATHER THAN A LOOP IN THE CALLER. Measured 2026-08-29 building a Caraya suite for
/// a three-class hierarchy: 19 of the run's 44 <c>lvai_run_vi_and_read_values</c> calls were driving
/// this one operation, alternating a list call and a replace call almost every time. That
/// alternation is forced from outside - <c>{LV.Diagram}</c> <c>SubVIs[]</c> re-orders after every
/// <c>Replace</c> and the replaced node's reference dies - so an index read before a swap means a
/// different node after it. Inside one helper run the ordering problem does not arise: the
/// references and their names are collected once, and each swap is addressed by name.
///
/// WHY <c>Replace</c> AND NOT A PYLABVIEW RETARGET. A retarget rewrites the link record only, so it
/// needs the two connector panes to be type-identical and answers <c>Error 7, Bad Linkage</c>
/// otherwise. <c>Replace</c> is the IDE's own "Replace with a VI from disk": it relinks AND
/// **re-types the wires**, which is the single property that lets a generated test reach class code
/// at all. AIXML cannot author a class-typed terminal, so the test is authored against a socket
/// whose class terminals are <c>path</c> stand-ins and the real accessor is swapped in afterwards.
///
/// THE ORDER IS NOT ARBITRARY: nodes first, constants last. With the nodes already swapped the wire
/// into a class constant has a class sink and <c>Replace</c> re-types it; the other way round the
/// wire has a class source and a path sink and simply breaks. The helper enforces it.
/// </summary>
[McpServerToolType]
internal sealed class SwapTools(LvaiConnection connection)
{
    /// <summary>Name of the swap helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_swap_subvis.xml";

    [McpServerTool(Name = "lvai_swap_subvis", Destructive = true, OpenWorld = true,
                   Title = "Repoint many subVI nodes and class constants on one diagram")]
    [Description("""
        Edit: repoints subVI nodes and class constants on ONE block diagram through LabVIEW's own
        {LV.SubVI} / {LV.Constant} Replace, then saves the VI in place. This is how a generated VI
        comes to call class or project-local code: author the Call against a placeholder socket
        (lvai_placeholder_subvi), then swap. Replace RE-TYPES THE WIRES, which a pylabview
        retarget cannot - do not use pylv_apply's retarget for this.
        swapsJson: [{"socket":"LVMCP Acc W1.vi","target":"C:\cls\Write Hersteller.vi"}]
        constantsJson: [{"label":"seed 1","class":"C:\cls\Netzteil.lvclass"}]
        Nodes are swapped first, constants last. Nodes and constants are found at ANY DEPTH (loops,
        Case frames).
        PRECONDITION: the project must be OPEN and ACTIVE - Replace is a silent no-op outside the
        IDE's instance; Error 1055 is `errorKind: noActiveProject` (lvai_run_lunit_tests leaves no
        active project - call lvai_open_file between them).
        REFUSALS: a socket named twice in swapsJson; a socket not on the diagram (`diagramSubVis`
        then lists every subVI name on the diagram, with repetition, as it was before this call). A
        node already pointed at a class member carries its QUALIFIED name
        (`Pump.lvclass:Read.vi`); the bare name matches nothing. A constant label carried by more
        than one constant is not replaced (`constantsAmbiguous`, `ok: false`,
        `errorKind: constantLabelAmbiguous`) while the rest are; an absent label is
        `constantsNotOnDiagram`; `constantsSeen` 0 means the traversal matched nothing.
        FIELDS: `nodesSwapped` is the outcome (`nodesAsked` the request; an errored helper landed
        nothing). One socket on N nodes costs N calls: each call swaps one, `ok: false` until the
        last. `socketsLeft` counts swapsJson ENTRIES still present, not nodes - read
        `diagramSubVis` for nodes. `callTargets` from the verify export is the proof.
        Broken wires are cleared last (removeBadWires); a lost connection makes `ok` false.
        """)]
    public async Task<string> SwapSubVisAsync(
        [Description("""
            Absolute path to the .vi to edit - SAVED IN PLACE. Omit it when you pass `editsJson`.
            """)]
        string? viPath = null,
        [Description(@"JSON array of {socket, target} node swaps. Omit to swap no nodes.")]
        string? swapsJson = null,
        [Description(@"JSON array of {label, class} constant swaps. Omit to swap no constants.")]
        string? constantsJson = null,
        [Description("""
            Re-export the VI before and after and report `callTargets`, `socketsLeft` (the verdict)
            and `wiringLost` (REPORTING ONLY - uids are unstable across Replace; never branch on it).
            """)]
        bool verify = true,
        [Description("""
            Keep every sub-answer whole (large); a failed step is reported whole regardless.
            """)]
        bool verbose = false,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description($"The helper's AIXML source; defaults to {HelperAixmlFileName} in scriptsDirectory")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("""
            Several VIs in one call; then viPath, swapsJson and constantsJson are ignored and the
            answer has one entry per VI under `edits`:
              [{"vi":"C:\t\A.vi","swaps":[{"socket":"LVMCP Mth1.vi","target":"C:\c\M.vi"}],
                "constants":[{"label":"Seed1","class":"C:\c\Daq.lvclass"}]}]
            Each VI is saved in place; a failure STOPS the batch there (earlier VIs stay written).
            """)]
        string? editsJson = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        [Description("""
            Lay out each block diagram (lvai_layout_diagram's engine) after a successful swap.
            Reported, never gating; a DQMH Main.vi is never touched.
            """)]
        bool cleanUpDiagram = true,
        [Description("""
            After a saved swap, clear BROKEN wires and list them under `badWireCleanup`. A sink that
            lost its sourced wire is named in `inputsLeftUnwired` and `ok` is false
            (`errorKind: badWireCleanupLostConnection`).
            """)]
        bool removeBadWires = true)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_swap_subvis", out _, out var badBackend))
            return AixmlBackend.Refusal(badBackend);
        if (editsJson is { Length: > 0 })
            return await ManyAsync(editsJson, verify, verbose, helperViPath, helperAixmlPath,
                                   regenerateHelper, timeoutSeconds, cleanUpDiagram, removeBadWires, ct: ct);

        // Now that `viPath` is optional in the schema, its absence has to be caught here - and
        // named, rather than arriving as a null-reference somewhere further down.
        if (string.IsNullOrWhiteSpace(viPath))
            return Json.Error("badArguments",
                "Give either `viPath` (one VI) or `editsJson` (several). Neither arrived.",
                new { viPath, editsJson });

        return await OneAndCleanAsync(viPath, swapsJson, constantsJson, verify, verbose, helperViPath,
                                      helperAixmlPath, regenerateHelper, timeoutSeconds,
                                      cleanUpDiagram, removeBadWires, ct: ct);
    }

    /// <summary>
    /// One swap - which ends with Remove Bad Wires when asked - then our layout of the saved VI
    /// when the swap was ok. The clean-up is added to the answer as `cleanUpDiagram` and never
    /// changes its `ok`.
    /// </summary>
    private async Task<string> OneAndCleanAsync(string viPath, string? swapsJson, string? constantsJson,
                                                bool verify, bool verbose, string? helperViPath,
                                                string? helperAixmlPath, bool regenerateHelper,
                                                int timeoutSeconds, bool cleanUpDiagram,
                                                bool removeBadWires, CancellationToken ct)
    {
        var answer = await OneAsync(viPath, swapsJson, constantsJson, verify, verbose, helperViPath,
                                    helperAixmlPath, regenerateHelper, timeoutSeconds, removeBadWires,
                                    ct: ct);
        if (!cleanUpDiagram || Read(answer) is not JsonObject node
            || node["ok"]?.GetValue<bool>() is not true)
            return answer;
        node["cleanUpDiagram"] = await new DiagramCleanup(connection).RunAsync(
            viPath, BackendRoute.Current, timeoutSeconds, ct);
        return Json.Document(node);
    }

    /// <summary>
    /// One VI at a time, each through the single-VI path, stopping at the first failure.
    ///
    /// SEQUENTIAL AND STOPPING ARE BOTH DELIBERATE. LabVIEW serialises the work, so fanning
    /// out gains nothing; and a swap SAVES THE VI IN PLACE, so carrying on after one has
    /// failed would leave a half-swapped suite whose green run means nothing. The answer names
    /// how many were written before the stop.
    /// </summary>
    private async Task<string> ManyAsync(string editsJson, bool verify, bool verbose,
                                         string? helperViPath, string? helperAixmlPath,
                                         bool regenerateHelper, int timeoutSeconds,
                                         bool cleanUpDiagram, bool removeBadWires, CancellationToken ct)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(editsJson); }
        catch (System.Text.Json.JsonException bad)
        {
            return Json.Error("badArguments",
                $"editsJson is not JSON: {bad.Message}. It is a JSON ARRAY of " +
                "{vi, swaps, constants} objects.");
        }

        if (parsed is not JsonArray array || array.Count == 0)
            return Json.Error("badArguments", "editsJson must be a non-empty JSON array.");

        var edits = new List<(string Vi, string? Swaps, string? Constants)>();
        foreach (var element in array)
        {
            if (element is not JsonObject o)
                return Json.Error("badArguments", "Every entry in editsJson must be an object.");

            var vi = o["vi"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(vi))
                return Json.Error("badArguments", "Every entry needs a \"vi\" path.");
            if (!File.Exists(vi))
                return Json.Error("badArguments",
                    $"No file at '{vi}'. Nothing was swapped - a bad path costs a message " +
                    "here rather than a half-edited suite.");

            edits.Add((Path.GetFullPath(vi),
                       o["swaps"]?.ToJsonString(), o["constants"]?.ToJsonString()));
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var answers = new JsonArray();
        var written = 0;
        string? stoppedAt = null;
        foreach (var edit in edits)
        {
            var one = await OneAndCleanAsync(edit.Vi, edit.Swaps, edit.Constants, verify, verbose,
                                             helperViPath, helperAixmlPath, regenerateHelper,
                                             timeoutSeconds, cleanUpDiagram, removeBadWires, ct: ct);
            var node = Read(one);
            answers.Add(new JsonObject { ["vi"] = edit.Vi, ["answer"] = node });

            if ((node as JsonObject)?["ok"]?.GetValue<bool>() is not true)
            {
                stoppedAt = edit.Vi;
                break;
            }
            written++;
        }

        return Json.Document(new JsonObject
        {
            ["ok"] = stoppedAt is null,
            ["requested"] = edits.Count,
            ["swapped"] = written,
            ["stoppedAt"] = stoppedAt,
            ["edits"] = answers,
            ["elapsedMs"] = elapsed.ElapsedMilliseconds,
            ["note"] = stoppedAt is null
                ? $"Every VI was swapped and verified against {AixmlBackend.ExportName()}."
                : $"Stopped at '{Path.GetFileName(stoppedAt)}'. The {written} VI(s) before " +
                  "it are already SAVED - they were swapped successfully. That one and " +
                  "everything after it still call their sockets, so the suite tests nothing.",
        });
    }

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(answer); }
    }

    internal async Task<string> OneAsync(string viPath, string? swapsJson, string? constantsJson,
                                        bool verify, bool verbose, string? helperViPath,
                                        string? helperAixmlPath, bool regenerateHelper,
                                        int timeoutSeconds, bool removeBadWires,
                                        CancellationToken ct) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            List<NodeSwap> swaps;
            List<ConstantSwap> constants;
            try
            {
                swaps = NodeSwap.ParseAll(swapsJson);
                constants = ConstantSwap.ParseAll(constantsJson);
            }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            if (swaps.Count == 0 && constants.Count == 0)
                return Json.Error("badArguments",
                    "Neither swapsJson nor constantsJson names anything to do.");

            // A duplicate socket name is the silent-wrong-node case, so it is refused rather than
            // reported afterwards - afterwards there is nothing to see.
            if (swaps.GroupBy(s => s.Socket, StringComparer.OrdinalIgnoreCase)
                     .FirstOrDefault(g => g.Count() > 1) is { } repeated)
                return Json.Error("badArguments",
                    $"'{repeated.Key}' is named {repeated.Count()} times in swapsJson. Matching is " +
                    "by VI Name, so two nodes calling the same socket cannot be told apart and the " +
                    "wrong subject would land in the wrong place with no error. Generate one socket " +
                    "per slot.");

            // A pipe separates the helper's lists and is illegal in a Windows path, so a value
            // carrying one would silently split into two.
            if (swaps.SelectMany(s => new[] { s.Socket, s.Target })
                     .Concat(constants.SelectMany(c => new[] { c.Label, c.ClassPath }))
                     .FirstOrDefault(v => v.Contains('|')) is { } piped)
                return Json.Error("badArguments",
                    $"'{piped}' contains a '|', which is what separates the helper's lists.");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperAixmlFileName) : null);
            if (aixml is null || !File.Exists(aixml))
                return Json.Error("helperMissing",
                    $"The helper's AIXML source could not be located ({HelperAixmlFileName} in the " +
                    "folder lvai_status reports as scriptsDirectory). Pass helperAixmlPath.");

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                AixmlBackend.HelperFolder(), "lvai_swap_subvis.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            // NOT `!File.Exists` - see Infra/HelperCache. The helper gained `constant matches`
            // and `constants seen` on 2026-10-11; a VI cached before that has neither control, and
            // a missing output is not an error anywhere - the per-label verdict would read empty.
            var helperGenerated = false;
            if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    aixml, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct: ct, measureDiagram: false);
                steps.Add(new JsonObject { ["step"] = "helper", ["answer"] = Json.Slim(Parse(built), verbose) });
                if (!File.Exists(helperVi))
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = "helper",
                        ["steps"] = steps,
                        ["note"] = "The swap helper itself could not be generated, so nothing was " +
                                   "changed. Its AIXML is at " + aixml + ".",
                    });
                helperGenerated = true;
            }

            // EMPTY VALUES ARE OMITTED, not sent blank: the runner pairs names and values by
            // position and refuses an empty one outright, because a blank would shift every later
            // input onto the wrong control. An unset control keeps its own (empty) default, which
            // is exactly "swap nothing of this kind".
            var inputs = new JsonObject { ["vi path"] = Path.GetFullPath(viPath) };
            if (swaps.Count > 0)
            {
                inputs["socket names"] = string.Join("|", swaps.Select(s => s.Socket));
                inputs["target paths"] = string.Join("|", swaps.Select(s => s.Target));
            }
            if (constants.Count > 0)
            {
                inputs["constant labels"] = string.Join("|", constants.Select(c => c.Label));
                inputs["constant paths"] = string.Join("|", constants.Select(c => c.ClassPath));
            }

            // THE WIRING BEFORE THE SWAP, so afterwards we can tell a net that MOVED from a net
            // that was never there. Measured 2026-09-03: a swap between accessors of different
            // types left the value wire attached to the CLASS terminal - both are refnums, so
            // LabVIEW's Replace re-attached it there - `Sample Rate:` came out unwired, and this
            // tool reported a clean restore because it only ever compared CALL TARGETS. One export
            // is the whole cost, and it is an RPC rather than a model turn.
            Dictionary<string, NodeWiring>? wiringBefore = null;
            if (verify)
            {
                var beforePath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                    Path.ChangeExtension(Path.GetFileName(viPath), ".swap-before.xml"));
                Directory.CreateDirectory(Path.GetDirectoryName(beforePath)!);
                var beforeExport = await new AixmlTools(connection).ConvertViToAixmlAsync(
                    viPath, beforePath, returnContent: true, maxContentChars: 0, timeoutSeconds,
                    refresh: true, ct: ct);
                if ((Parse(beforeExport) as JsonObject)?["xml"]?.GetValue<string>() is { } beforeXml)
                    wiringBefore = WiringByUid(beforeXml);

                try { File.Delete(beforePath); }
                catch (Exception failure) when (failure is IOException
                                                or UnauthorizedAccessException) { }
            }

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "swap", ["answer"] = Json.Slim(Parse(answer), verbose) });

            var values = (Parse(answer) as JsonObject)?["values"] as JsonObject;
            var code = Scalar(values, "code");
            var source = Scalar(values, "source");
            var swapped = code == "0";

            // The names the diagram actually had, so an unmatched socket is visible rather than
            // silently swapping element 0.
            var present = Names(values, "node names found");
            var missing = swaps.Select(s => s.Socket)
                               .Where(n => !present.Contains(n, StringComparer.OrdinalIgnoreCase))
                               .ToList();

            // HOW MANY NODES REALLY CHANGED, WHICH IS NOT HOW MANY WERE ASKED FOR. This field was
            // `swaps.Count` - the REQUEST echoed back under an outcome's name - and the answer then
            // contradicted itself in the one case that matters: `nodesSwapped: 8` beside
            // `socketsNotOnDiagram` listing five of those eight, with the helper errored and the
            // file correctly not saved. docs/class-method-tooling.md D1 measured that at ~100 s of
            // wall clock for ~3 s of LabVIEW, spent proving the tool wrong about its own diagram,
            // and wrote down the remedy - "socketsNotOnDiagram should not be populated when
            // nodesSwapped > 0"; NOBODY CHANGED THE CODE, and the same contradiction turned up
            // again on 2026-09-15 in docs/cold-build-pumpstand.md as `nodesSwapped: 1` for a swap
            // that matched nothing. Third occurrence of one defect that a four-line fix retires.
            //
            // The count is now: nothing landed if the helper errored, because a Replace that fails
            // leaves its error on the wire and that stops Save.Instrument - so the file on disk is
            // untouched whatever happened in memory. Otherwise it is the swaps whose socket the
            // helper actually FOUND. `nodesAsked` keeps the request visible beside it.
            var nodesSwapped = NodesThatLanded(swapped, present, swaps.Select(s => s.Socket));

            // PER LABEL, NOT PER REQUEST. The helper counts how many diagram constants carry each
            // requested label - at ANY depth since 2026-10-11, where it used to see the top-level
            // diagram only - and replaces only a label with exactly one match. A label on two
            // constants cannot say which one is meant, so it is REFUSED rather than guessed: the
            // user's decision of 2026-10-11.
            var constantOutcome = ConstantOutcome.From(
                constants, Counts(values, "constant matches"), swapped,
                int.TryParse(Scalar(values, "constants seen"), out var seen) ? seen : null);

            // THE BROKEN-WIRE CLEANUP RUNS BEFORE THE VERIFY EXPORT, so `callTargets` and the
            // wiring comparison describe the diagram as it is left - not one that still carries
            // the wires the cleanup removed. Only after a SAVED swap: an errored helper wrote
            // nothing, so there is nothing of this call's to clean.
            var cleanup = !removeBadWires
                ? BadWireCleanup.Skipped("removeBadWires was false.")
                : !swapped
                    ? BadWireCleanup.Skipped("The swap did not save the VI, so this call left nothing to clean.")
                    : await new BadWireCleanup(connection).RunAsync(viPath, timeoutSeconds, ct);

            JsonNode? targets = null;
            // What the diagram calls AFTER the swap, with repetition, read off the verify export.
            // `present` is the helper's listing from BEFORE its Replace, and it used to be the
            // only one served as `diagramSubVis` - so a clean swap answered `callTargets` naming
            // the real accessors beside a `diagramSubVis` still naming the four sockets, measured
            // on two cold builds 2026-10-05.
            List<string>? after = null;
            var socketsLeft = -1;
            JsonArray? wiringLost = null;
            var wiringChecked = false;
            if (swapped && verify)
            {
                var exportPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                    Path.ChangeExtension(Path.GetFileName(viPath), ".swap-verify.xml"));
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
                // TIMED HERE, BY THE COMPOSER. The callee does not always carry an elapsedMs on
                // this path, so the slimmed sub-answer had none - and a step with no duration
                // cannot be chosen against when picking the next thing to optimise, which is the
                // whole method this repository uses. A composing tool always knows how long its
                // own step took, so it reports it rather than hoping the callee did.
                var verifyWall = Stopwatch.StartNew();
                var exported = await new AixmlTools(connection).ConvertViToAixmlAsync(
                    viPath, exportPath, returnContent: true, maxContentChars: 0, timeoutSeconds,
                    refresh: true, ct: ct);
                steps.Add(new JsonObject
                {
                    ["step"] = "verify",
                    ["elapsedMs"] = verifyWall.ElapsedMilliseconds,
                    ["answer"] = Json.Slim(Parse(exported), verbose),
                });

                if ((Parse(exported) as JsonObject)?["xml"]?.GetValue<string>() is { } xml)
                {
                    socketsLeft = swaps.Count(s => xml.Contains(s.Socket, StringComparison.Ordinal));
                    targets = new JsonArray([.. Targets(xml).Select(t => (JsonNode)t!)]);
                    after = SubVisInExport(xml);

                    if (wiringBefore is not null)
                    {
                        wiringLost = LostWiring(wiringBefore, WiringByUid(xml));
                        wiringChecked = true;
                    }
                }

                try { File.Delete(exportPath); }
                catch (Exception failure) when (failure is IOException
                                                or UnauthorizedAccessException) { }
            }

            // A LOST NET MAKES THIS NOT OK, even though the retarget itself succeeded. The
            // measured failure produced a diagram that linked, compiled and ran, and asserted
            // against the wrong terminal - which is worse than a hard error, because the suite
            // went green.
            // WIRING IS REPORTED, NEVER A VERDICT. It gated `ok` from 2026-09-03 to 2026-09-04 and
            // produced false negatives on three separate runs, for two independent reasons that
            // together make the comparison undecidable rather than merely buggy:
            //
            //   1. A uid is NOT stable across `Replace` when several nodes are swapped. Measured on
            //      a ten-node class suite: five Write nodes moved to 1070/273/269/230/124, so
            //      pairing before/after BY UID compares unrelated nodes - the entry read
            //      `targetBefore: LVMCP ClsW5.vi, targetAfter: ...Read Error Cluster.vi`. The single
            //      controlled pair this was designed against kept its uid, which is exactly why one
            //      measurement was not enough.
            //   2. A DROP IN NET COUNT IS THE INTENDED SHAPE HERE. A socket carries obj in / value /
            //      obj out; the real accessor carries a class wire plus error terminals that
            //      `lvai_generate_method_test` deliberately leaves unwired, feeding each method a
            //      fresh `no error` constant instead. So a correct swap legitimately ends with fewer
            //      wired terminals, and an aggregate comparison fails the same way a per-node one
            //      does.
            //
            // The damage was not cosmetic: `ok: false` suppressed the caller's own `projectEntry`
            // step, so two agents had to list their test VIs in the .lvproj by hand - about 135 s
            // of wall clock each. `wiringLost` stays in the answer because reading it against the
            // export is occasionally useful, but nothing may branch on it.
            var ok = swapped && missing.Count == 0 && socketsLeft <= 0
                     && constantOutcome.Ambiguous.Count == 0 && constantOutcome.Missing.Count == 0;
            var result = new JsonObject
            {
                ["ok"] = ok,
                ["viPath"] = Path.GetFullPath(viPath),
                ["wiringChecked"] = wiringChecked,
                ["wiringLost"] = wiringLost,
                ["nodesSwapped"] = nodesSwapped,
                ["nodesAsked"] = swaps.Count,
                ["constantsSwapped"] = constantOutcome.Swapped,
                ["constantsAsked"] = constants.Count,
                ["constantMatches"] = constantOutcome.MatchesJson(),
                ["constantsAmbiguous"] = constantOutcome.AmbiguousJson(),
                ["constantsNotOnDiagram"] = constantOutcome.Missing.Count == 0
                    ? null : new JsonArray([.. constantOutcome.Missing.Select(m => (JsonNode)m!)]),
                ["constantsSeen"] = constantOutcome.Seen,
                ["socketsLeft"] = socketsLeft < 0 ? null : socketsLeft,
                ["socketsNotOnDiagram"] = missing.Count == 0
                    ? null : new JsonArray([.. missing.Select(m => (JsonNode)m!)]),
                // WHAT IS ON THE DIAGRAM, so a socket that did not match can be corrected without
                // another round trip. The helper has reported this all along as `node names found`
                // and it was reachable only inside the swap step's sub-answer - which `verbose:
                // false` strips - while the note told the reader to take names from it. So the
                // advice arrived inside the thing it was warning about, the same shape
                // lvai_aixml_reference section 8 was caught by. Measured 2026-09-15: one extra
                // turn, on a node whose name had become class-qualified after an earlier swap.
                ["diagramSubVis"] = (after ?? present) is { Count: > 0 } now
                    ? new JsonArray([.. now.Select(n => (JsonNode)n!)]) : null,
                ["diagramSubVisFrom"] = after is not null
                    ? "LabVIEW's export AFTER the swap"
                    : present.Count > 0 ? "the helper, BEFORE its Replace - no verify export" : null,
                ["diagramSubVisBefore"] = after is not null && present.Count > 0
                    ? new JsonArray([.. present.Select(n => (JsonNode)n!)]) : null,
                ["callTargets"] = targets,
                ["helperGenerated"] = helperGenerated,
                ["errorCode"] = code,
                ["errorSource"] = source,
                // 1055 IS NOT AN OPAQUE NUMBER - it is LabVIEW's "no active project", and this
                // tool cannot work without one because {LV.SubVI} Replace is a SILENT NO-OP
                // outside the IDE's own application instance. The answer used to report the bare
                // code beside `Invoke Node in lvai_swap_subvis.vi`, which names the node rather
                // than the cause and reads like an internal fault.
                ["errorKind"] = code == "1055" ? "noActiveProject"
                    : constantOutcome.Ambiguous.Count > 0 ? "constantLabelAmbiguous"
                    : constantOutcome.Missing.Count > 0 ? "constantLabelNotOnDiagram" : null,
                ["steps"] = steps,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = Note(ok, swapped, missing, socketsLeft, verify, code, after ?? present,
                                constantOutcome),
            };
            BadWireCleanup.Attach(result, cleanup);
            return Json.Document(result);
        });

    /// <summary>
    /// HOW MANY NODES REALLY CHANGED. Three cases, and the first is the one that retires the
    /// defect: a helper that ERRORED saved nothing, because the error travels down the wire and
    /// stops <c>Save.Instrument</c>, so the count is zero however many were asked for. Otherwise it
    /// is the sockets the helper actually FOUND on the diagram. A reply carrying no name list at
    /// all - an older helper - falls back to the request rather than inventing a zero.
    /// </summary>
    internal static int NodesThatLanded(bool swapped, IReadOnlyList<string> present,
                                        IEnumerable<string> sockets)
    {
        var asked = sockets.ToList();
        if (!swapped) return 0;
        if (present.Count == 0) return asked.Count;
        return asked.Count(s => present.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    internal static string Note(bool ok, bool swapped, List<string> missing, int socketsLeft,
                               bool verify, string? code, List<string> present,
                               ConstantOutcome? constants = null)
    {
        // WHICH NAMES ARE AVAILABLE, appended wherever a name did not match. The old text said
        // "take names from `node names found`" and that field is inside the swap step's slimmed
        // sub-answer, so the reader could not see it - advice pointing at something the answer
        // hides. They are in `diagramSubVis` now, and named here too.
        var onDiagram = present.Count == 0 ? ""
            : " THE DIAGRAM'S OWN NAMES ARE IN `diagramSubVis`: "
              + string.Join(", ", present.Select(n => $"'{n}'")) + ".";

        if (!swapped)
            return (code == "1055"
                ? "NO PROJECT IS ACTIVE - Error 1055. {LV.SubVI} Replace is a silent no-op outside "
                  + "the IDE's own application instance, so the helper cannot reach the VI and the "
                  + "file was NOT saved. Open the project with lvai_open_file and call again. "
                  + "MEASURED 2026-09-15: lvai_run_lunit_tests leaves NO active project behind, so "
                  + "a swap issued straight after a test run lands here every time."
                : "The helper reported an error and the VI was NOT saved - a Replace that fails "
                  + "leaves its error on the wire, which also stops Save.Instrument. Read the swap "
                  + "step's `source`.") + onDiagram;
        if (missing.Count > 0)
            return "The VI was saved, but " + string.Join(", ", missing.Select(m => $"'{m}'")) +
                   " is not among the diagram's subVI names, so the helper's array search answered " +
                   "-1 and Index Array clamped to element 0 - A NODE YOU DID NOT NAME WAS SWAPPED. " +
                   "Regenerate the VI and swap again with a name that is on the diagram. A NODE " +
                   "ALREADY POINTED AT A CLASS MEMBER CARRIES ITS QUALIFIED NAME - " +
                   "`Centrifugal Pump.lvclass:Read Last Event.vi`, not `Read Last Event.vi` - " +
                   "which is what a second swap over the same diagram has to use." + onDiagram;
        // A REFUSED OR MISSING LABEL IS NAMED BEFORE THE SOCKET COUNT, because it is the one
        // failure here the caller fixes in its own AIXML: distinct `_name`s, then regenerate.
        if (constants is { } c && (c.Ambiguous.Count > 0 || c.Missing.Count > 0))
            return ConstantNote(c);
        if (socketsLeft > 0)
            return $"{socketsLeft} socket name(s) are STILL in {AixmlBackend.ExportName()} of the VI, so " +
                   "that many ENTRIES did not land. IT IS NOT A NODE COUNT: it counts swapsJson " +
                   "entries still present in the export, so one entry naming a repeated socket " +
                   "reads 1 until the LAST node is swapped. `diagramSubVis` lists the diagram's " +
                   "names with repetition and is the per-node view." + onDiagram;
        return verify
            ? $"Swapped, and verified against {AixmlBackend.ExportName()}: no socket name survives in "
              + "it, and `callTargets` is what the diagram now calls. `wiringLost` is REPORTING "
              + "ONLY and nothing branches on it - it gated `ok` for one day and produced false "
              + "negatives on three runs, because a uid is not stable across `Replace` when "
              + "several nodes move, and because a correct swap onto a real accessor "
              + "legitimately ends with FEWER wired terminals than the socket had. Read it "
              + "against the export if a diagram looks wrong; never treat it as a verdict."
            : "Swapped and saved. NOTHING HERE PROVES IT LANDED: verify was false, and a Replace " +
              "cannot be read back from the object it replaced. Export the VI and read target=.";
    }

    /// <summary>The note for a constant label that was refused or not found.</summary>
    internal static string ConstantNote(ConstantOutcome constants)
    {
        var parts = new List<string>();
        if (constants.Ambiguous.Count > 0)
            parts.Add("REFUSED, NOT REPLACED: " + string.Join(", ",
                          constants.Ambiguous.Select(a => $"'{a.Label}' ({a.Matches} constants)")) +
                      ". A constant is matched by its block diagram label, so a label several " +
                      "constants carry cannot say which one is meant - guessing would put the class " +
                      "into the wrong wire with no error. Give each constant its own `_name` in the " +
                      "AIXML, regenerate, and swap again. Every other label in this call that matched " +
                      "exactly one constant WAS replaced, and the VI was saved.");
        if (constants.Missing.Count > 0)
            parts.Add("NOT ON THE DIAGRAM: " + string.Join(", ", constants.Missing.Select(m => $"'{m}'")) +
                      " - no constant at any depth carries that label." +
                      (constants.Seen == 0
                          ? " THE TRAVERSAL RETURNED NO CONSTANT AT ALL (`constantsSeen: 0`), which " +
                            "points at the helper's `Constant Class Name` rather than at the label: " +
                            "whether a diagram constant answers to the class name `Constant` is not " +
                            "measured yet."
                          : " AIXML's `_name` on a Constant is what becomes its label."));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// What became of each requested constant label. The helper's per-label count arrives in
    /// request order; a reply WITHOUT it is an older helper, and then the request stands in for
    /// the outcome - the same fallback <see cref="NodesThatLanded"/> makes.
    /// </summary>
    internal sealed record ConstantOutcome(
        int Swapped, IReadOnlyList<(string Label, int Matches)> PerLabel,
        IReadOnlyList<(string Label, int Matches)> Ambiguous, IReadOnlyList<string> Missing, int? Seen)
    {
        internal static ConstantOutcome From(IReadOnlyList<ConstantSwap> constants,
                                             IReadOnlyList<int> matches, bool swapped,
                                             int? seen = null)
        {
            if (constants.Count == 0) return new(0, [], [], [], seen);
            // An EMPTY LabVIEW array still flattens one template element with an empty <Val>,
            // which parses as -1 here - so a count that is not a count is no answer either.
            if (matches.Count != constants.Count || matches.Any(m => m < 0))
                return new(swapped ? constants.Count : 0, [], [], [], seen);

            var perLabel = constants.Select((c, i) => (c.Label, matches[i])).ToList();
            var ambiguous = perLabel.Where(p => p.Item2 > 1).ToList();
            var missing = perLabel.Where(p => p.Item2 == 0).Select(p => p.Label).ToList();
            // AN ERRORED HELPER SAVED NOTHING, whatever it counted - the error on the wire stops
            // Save.Instrument, exactly as for the nodes.
            var landed = swapped ? perLabel.Count(p => p.Item2 == 1) : 0;
            return new(landed, perLabel, ambiguous, missing, seen);
        }

        internal JsonArray? MatchesJson() => PerLabel.Count == 0 ? null : Rows(PerLabel);

        internal JsonArray? AmbiguousJson() => Ambiguous.Count == 0 ? null : Rows(Ambiguous);

        private static JsonArray Rows(IEnumerable<(string Label, int Matches)> rows) =>
            new([.. rows.Select(p => (JsonNode)new JsonObject
                { ["label"] = p.Label, ["matches"] = p.Matches })]);
    }

    /// <summary>What one `Call` node had wired, keyed so it survives a retarget.</summary>
    internal sealed record NodeWiring(string Target, List<string> WiredInputs,
                                      List<string> WiredOutputs)
    {
        public int Count => WiredInputs.Count + WiredOutputs.Count;
    }

    /// <summary>
    /// Every `Call` node's wired terminals, by `uid`.
    ///
    /// THE UID IS THE IDENTITY AND THE NAMES ARE NOT. <c>{LV.SubVI}</c> <c>Replace</c> swaps which
    /// VI sits in an existing node, so the uid survives while every terminal may be renamed - a
    /// socket's `value` becomes the accessor's `Sample Rate`. So the comparison counts nets rather
    /// than matching names, which is the only thing that means the same before and after.
    ///
    /// A terminal is WIRED when its entry carries a net: <c>obj in:265.AnalogInput out</c> is
    /// wired, <c>Sample Rate:</c> is not. That is exactly the distinction the measured defect
    /// turned on.
    /// </summary>
    internal static Dictionary<string, NodeWiring> WiringByUid(string xml)
    {
        var byUid = new Dictionary<string, NodeWiring>(StringComparer.Ordinal);

        System.Xml.Linq.XElement root;
        try { root = System.Xml.Linq.XElement.Parse(xml); }
        catch (System.Xml.XmlException) { return byUid; }

        foreach (var call in root.Descendants("Call"))
        {
            if ((string?)call.Attribute("uid") is not { Length: > 0 } uid) continue;
            byUid[uid] = new NodeWiring(
                (string?)call.Attribute("target") ?? "",
                Wired((string?)call.Attribute("inputs")),
                Wired((string?)call.Attribute("outputs")));
        }
        return byUid;
    }

    /// <summary>The names in a `name:net,name:net` list whose net is not empty.</summary>
    private static List<string> Wired(string? attribute)
    {
        if (attribute is not { Length: > 0 }) return [];

        var wired = new List<string>();
        foreach (var entry in attribute.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = entry.LastIndexOf(':');
            if (colon < 0) continue;
            if (entry[(colon + 1)..].Trim() is { Length: > 0 })
                wired.Add(entry[..colon].Trim());
        }
        return wired;
    }

    /// <summary>
    /// The nodes that came out of the swap carrying FEWER wired terminals than they went in with.
    ///
    /// A DROP IS THE SIGNAL, not a changed name. Renaming is normal and expected; losing a net is
    /// the defect. Measured 2026-09-03 on the case this exists for: a node went from two wired
    /// outputs (`AnalogInput out`, `Maximum Value`) to one, because Replace re-attached the value
    /// wire onto the class output - and the assertion downstream then compared the class wire.
    ///
    /// Only nodes present BEFORE AND AFTER are compared, so a node the swap legitimately removed
    /// is not reported as a loss.
    /// </summary>
    internal static JsonArray LostWiring(Dictionary<string, NodeWiring> before,
                                         Dictionary<string, NodeWiring> after)
    {
        var lost = new JsonArray();
        foreach (var (uid, was) in before)
        {
            if (!after.TryGetValue(uid, out var now)) continue;
            if (now.Count >= was.Count) continue;

            lost.Add(new JsonObject
            {
                ["uid"] = uid,
                ["targetBefore"] = was.Target,
                ["targetAfter"] = now.Target,
                ["wiredBefore"] = was.Count,
                ["wiredAfter"] = now.Count,
                ["wiredNamesBefore"] = new JsonArray(
                    [.. was.WiredInputs.Concat(was.WiredOutputs).Select(n => (JsonNode)n)]),
                ["wiredNamesAfter"] = new JsonArray(
                    [.. now.WiredInputs.Concat(now.WiredOutputs).Select(n => (JsonNode)n)]),
                ["note"] = "This node lost a net across the swap. The measured cause is Replace "
                           + "re-attaching a value wire onto a terminal of the same TYPE - two "
                           + "refnums, typically the class wire - which links, compiles and runs "
                           + "while asserting against the wrong terminal. Read the export and put "
                           + "the wire back, or regenerate the VI rather than swapping again.",
            });
        }
        return lost;
    }

    /// <summary>
    /// Every subVI call in an export, WITH repetition and in document order, unescaped the way
    /// the helper spells names (`\3A` is `:`), so a second swap can copy a name straight from it.
    /// </summary>
    internal static List<string> SubVisInExport(string xml) =>
        [.. System.Text.RegularExpressions.Regex
            .Matches(xml, @"<Call\b[^>]*?\btarget=""([^""]+)""")
            .Select(m => m.Groups[1].Value.Replace(@"\3A", ":").Replace(@"\5C", @"\"))];

    internal static List<string> Targets(string xml) =>
        [.. System.Text.RegularExpressions.Regex
            .Matches(xml, "target=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)];

    internal sealed record NodeSwap(string Socket, string Target)
    {
        public static List<NodeSwap> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            var array = Array(json, "swapsJson");
            return [.. array.Select((entry, i) =>
            {
                var o = Object(entry, i, "swapsJson");
                var socket = Text(o, "socket", i, "swapsJson");
                var target = Text(o, "target", i, "swapsJson");
                return new NodeSwap(socket, Path.GetFullPath(target));
            })];
        }
    }

    internal sealed record ConstantSwap(string Label, string ClassPath)
    {
        public static List<ConstantSwap> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            var array = Array(json, "constantsJson");
            return [.. array.Select((entry, i) =>
            {
                var o = Object(entry, i, "constantsJson");
                var label = Text(o, "label", i, "constantsJson");
                var cls = Text(o, "class", i, "constantsJson");
                return new ConstantSwap(label, Path.GetFullPath(cls));
            })];
        }
    }

    private static JsonArray Array(string json, string argument)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch (JsonException bad)
        { throw new ArgumentException($"{argument} is not JSON: {bad.Message}"); }

        return parsed as JsonArray
            ?? throw new ArgumentException($"{argument} must be a JSON array of objects.");
    }

    private static JsonObject Object(JsonNode? entry, int index, string argument) =>
        entry as JsonObject
        ?? throw new ArgumentException($"{argument}[{index}] is not an object.");

    private static string Text(JsonObject o, string key, int index, string argument) =>
        o[key]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"{argument}[{index}] has no \"{key}\".");

    /// <summary>An int32 array the helper returned as flattened XML; empty when it is absent.</summary>
    internal static List<int> Counts(JsonObject? values, string name) =>
        [.. Names(values, name).Select(v => int.TryParse(v, out var n) ? n : -1)];

    private static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    /// <summary>The helper returns arrays as flattened XML, so the values are read out of that.</summary>
    private static List<string> Names(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["xml"]?.GetValue<string>() is { } xml
            ? [.. System.Text.RegularExpressions.Regex.Matches(xml, "<Val>([^<]*)</Val>")
                   .Select(m => m.Groups[1].Value)]
            : [];

    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return null; }
    }
}
