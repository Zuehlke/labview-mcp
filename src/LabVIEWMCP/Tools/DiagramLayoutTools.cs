using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Layout;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// lvai_layout_diagram: our own block-diagram layout, replacing LabVIEW's Clean Up Diagram.
///
/// THE SPLIT. LabVIEW reads and moves, the server decides. lvbd_layout_dump.xml walks every
/// diagram of the VI breadth first and hands back one text line per diagram, object and wire;
/// <see cref="DiagramLayout"/> computes where everything goes; lvbd_layout_apply.xml moves the
/// objects outer first, writes each structure's Frame Size, moves the tunnels, cleans up every
/// wire and saves. LabVIEW keeps wires and tunnels consistent through every Move, which is why
/// this is VI Server scripting and not a heap edit.
///
/// MEASURED 2026-10-16 before the engine existed, with an ORACLE: the original positions of a DQMH
/// Main.vi written back onto the copy NI's Clean Up had spread out. 219 objects moved, 217 landed to
/// the pixel (a loop's i and a subdiagram label are placed by LabVIEW), Frame Size set every structure
/// exactly - Bounds is read-only on a structure (error 1029), and Auto Size and ShrinkWrapStructs
/// grow but never shrink - and the render matched the original.
/// docs/diagram-layout.md has the measurements.
/// </summary>
[McpServerToolType]
internal sealed class DiagramLayoutTools(LvaiConnection connection)
{
    internal const string DumpHelper = "lvbd_layout_dump.xml";
    internal const string ApplyHelper = "lvbd_layout_apply.xml";

    [McpServerTool(Name = "lvai_layout_diagram", Destructive = true, OpenWorld = true,
                   Title = "Lay out a VI's block diagram (our replacement for Clean Up Diagram)")]
    [Description("""
        Layout: rearranges a VI's whole block diagram and SAVES IT IN PLACE - our replacement for
        LabVIEW's Clean Up Diagram, for an existing VI as much as a generated one; a backup is kept
        under `backupPath`. Columns follow data flow, the error chain is one straight rail along the
        bottom, parallel structures stack, controls go left and indicators right, comments move with
        their anchor, loops and Case/Event structures are sized to their contents. Structure kinds
        not yet handled (sequences, timed loops, in-place, disable) move whole and are named in the
        answer. A DQMH Main.vi IS laid out when named here (only the automatic post-generation
        clean-up skips it). A plan too dense for our engine (`tooDense`: a crossing or more per data
        wire) is also tried with LabVIEW's Clean Up Diagram, and NI's is kept when it crosses fewer
        (`layoutEngine`, `niCleanup`); so is a saved result whose wires come out tangled (`tangledFallback`).
        Needs no project open; for many VIs use lvai_layout_diagrams.
        """)]
    public async Task<string> LayoutDiagramAsync(
        [Description("Absolute path of the VI to lay out. Saved in place")] string viPath,
        [Description("Compute and report the plan, change nothing")] bool dryRun = false,
        [Description("Dump the diagram again afterwards and compare it with the plan")] bool verify = true,
        [Description("Render the laid-out diagram to PNG, into renderDirectory")] bool render = false,
        [Description("Where the PNGs go when render is true; a temp folder otherwise")] string? renderDirectory = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 240,
        [Description("After the save, give each fan of two or more constants into one node staggered wires of our own. Skipped on a VI with compiled code")] bool routeFans = true,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_layout_diagram",
            () => LayoutCoreAsync(viPath, dryRun, verify, render, renderDirectory, timeoutSeconds, ct, routeFans)));

    [McpServerTool(Name = "lvai_layout_diagrams", Destructive = true, OpenWorld = true,
                   Title = "Lay out many VIs' block diagrams, in parallel workers")]
    [Description("""
        Layout: lvai_layout_diagram over MANY VIs in one call, each saved in place with a backup.
        viPaths: one absolute path per line; a FOLDER takes every .vi under it, so a whole project
        is one call. `parallel` workers run side by side on pylabview only; on grpc the VIs run one
        after another (`parallel` reported as 1). Answers one compact row per VI plus totals; a
        failed VI is named with its errorKind and the others go on.
        """)]
    public async Task<string> LayoutDiagramsAsync(
        [Description("The VIs, one absolute path per line; a folder takes every .vi under it")] string viPaths,
        [Description("Workers, 1-8. Parallel on pylabview only")] int parallel = 4,
        [Description("Dump each diagram again afterwards and compare it with the plan")] bool verify = true,
        [Description("Local budget in seconds, per step and VI")] int timeoutSeconds = 240,
        [Description("Give each constant fan wire geometry of our own, as lvai_layout_diagram does")] bool routeFans = true,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_layout_diagrams",
            () => LayoutManyAsync(viPaths, parallel, verify, timeoutSeconds, routeFans, ct)));

    internal async Task<string> LayoutManyAsync(string viPaths, int parallel, bool verify, int timeoutSeconds,
                                                bool routeFans, CancellationToken ct)
    {
        var targets = new List<string>();
        foreach (var line in (viPaths ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(line))
                targets.AddRange(Directory.EnumerateFiles(line, "*.vi", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(line)) targets.Add(Path.GetFullPath(line));
            else return Json.Error("viNotFound", $"No file or folder at '{line}'. Every line is checked before LabVIEW is asked anything.",
                                   new { line });
        }
        targets = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0)
            return Json.Error("badArguments", "viPaths named no VI. Pass one absolute .vi path or folder per line.", new { viPaths });

        var workers = BackendRoute.OnPyLabview ? Math.Clamp(parallel, 1, 8) : 1;
        workers = Math.Min(workers, targets.Count);
        var queue = new System.Collections.Concurrent.ConcurrentQueue<(int Index, string Vi)>(targets.Select((v, i) => (i, v)));
        var rows = new JsonObject?[targets.Count];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        async Task Worker(int w)
        {
            // a lane and helper VIs of its own; one worker alone keeps the default lane, so a
            // sequential run is exactly lvai_layout_diagram's
            if (workers > 1) LabViewCom.Lane.Value = $"lane{w}";
            while (queue.TryDequeue(out var job))
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                JsonObject row;
                try
                {
                    var answer = JsonNode.Parse(await LayoutCoreAsync(job.Vi, dryRun: false, verify, render: false,
                        renderDirectory: null, timeoutSeconds, ct, routeFans)) as JsonObject ?? new JsonObject();
                    row = new JsonObject
                    {
                        ["viPath"] = job.Vi,
                        ["ok"] = answer["ok"]?.DeepClone() ?? false,
                        ["errorKind"] = answer["errorKind"]?.DeepClone(),
                        ["before"] = answer["before"]?.DeepClone(),
                        ["after"] = (answer["after"] ?? answer["planned"])?.DeepClone(),
                        ["landedOff"] = answer["landedOff"]?.DeepClone(), ["layoutEngine"] = answer["layoutEngine"]?.DeepClone(), ["tangledFallback"] = answer["tangledFallback"]?.DeepClone(),
                        ["backupPath"] = answer["backupPath"]?.DeepClone(),
                        ["ms"] = t.ElapsedMilliseconds,
                        ["timingsMs"] = answer["timingsMs"]?.DeepClone(),
                    };
                    if (answer["ok"]?.GetValue<bool>() != true)
                        row["message"] = (answer["message"] ?? answer["note"])?.DeepClone();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    row = new JsonObject { ["viPath"] = job.Vi, ["ok"] = false, ["errorKind"] = "exception",
                                           ["message"] = e.Message, ["ms"] = t.ElapsedMilliseconds };
                }
                rows[job.Index] = row;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() => Worker(w), ct)));

        var failed = rows.Where(r => r?["ok"]?.GetValue<bool>() != true).ToList();
        var off = rows.Sum(r => r?["landedOff"]?.GetValue<int>() ?? 0);
        return Json.Document(new JsonObject
        {
            ["ok"] = failed.Count == 0 && off == 0,
            ["vis"] = targets.Count,
            ["laidOut"] = targets.Count - failed.Count,
            ["failed"] = failed.Count,
            ["landedOff"] = off,
            ["parallel"] = workers,
            ["parallelNote"] = workers == 1 && parallel > 1 && !BackendRoute.OnPyLabview
                ? "On grpc the VIs are laid out one after the other." : null,
            ["wallMs"] = clock.ElapsedMilliseconds,
            ["results"] = new JsonArray(rows.Select(r => (JsonNode?)r).ToArray()),
        });
    }

    /// <summary>
    /// The whole layout of one VI - hold, dump, plan, apply, strip, verify, render - on the backend
    /// the caller's route carries. The tool's body, and the `cleanUpDiagram` step every VI we write
    /// goes through (<see cref="DiagramCleanup"/>).
    /// </summary>
    internal async Task<string> LayoutCoreAsync(string viPath, bool dryRun, bool verify, bool render,
                                                string? renderDirectory, int timeoutSeconds, CancellationToken ct,
                                                bool routeFans = true)
    {
        if (string.IsNullOrWhiteSpace(viPath))
            return Json.Error("badArguments", "viPath is required.");
        var vi = Path.GetFullPath(viPath);
        if (!File.Exists(vi))
            return Json.Error("fileNotFound", $"No VI at '{vi}'.", new { viPath = vi });
        if (StatusTools.ScriptsDirectory() is not { } scripts)
            return Json.Error("noScriptsDirectory", "The scripts folder next to the exe is missing.");

        // ---- 0. the VI stays loaded from the dump to the apply: each helper opens it by path, and
        // a Caraya test VI took 1.6-2.5 s to load every time, twice per call (measured 2026-10-16)
        // where the time goes, per step, in the answer: a whole project is laid out VI by VI
        var timings = new JsonObject();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string step) { timings[step] = clock.ElapsedMilliseconds; clock.Restart(); }
        var hold = await LabViewCom.HoldAsync(vi, timeoutSeconds, ct);
        Lap("hold");
        try
        {
            // ---- 1. read
            var dump = await DumpAsync(Path.Combine(scripts, DumpHelper), vi, timeoutSeconds, ct);
            if (dump.Failure is not null) return dump.Failure;
            var model = DiagramModel.Parse(dump.Lines!);
            var before = TopOutline(model);
            Lap("dump");
            var arrows = await ArrowsAsync(vi, timeoutSeconds, ct);
            model.Arrows = arrows.Arrows;
            Lap("arrows");

            // ---- 2. decide
            var plan = DiagramLayout.Plan(model);
            Lap("plan");
            var answer = new JsonObject
            {
                ["viPath"] = vi,
                ["diagrams"] = model.Diagrams.Count,
                ["objects"] = model.AllObjects.Count(),
                ["wires"] = model.Diagrams.Values.Sum(d => d.Wires.Count),
                ["before"] = Outline(before),
                ["planned"] = Outline(plan.PredictedTop),
                ["moves"] = plan.Moves.Count,
                ["structuresSized"] = plan.Sizes.Count,
                ["tunnelsMoved"] = plan.Tunnels.Count,
                ["structuresKeptAsTheyAre"] = new JsonArray(plan.StructuresKeptAsTheyAre.Select(s => (JsonNode)s).ToArray()),
                ["heldInMemory"] = hold is not null,
                ["commentArrows"] = arrows.Note is null ? arrows.Arrows.Count : arrows.Note,
                ["timingsMs"] = timings,
            };
            // a diagram too dense for our engine is ALSO given to LabVIEW's Clean Up Diagram, and
            // the layout that crosses fewer data wires is kept (docs/diagram-layout.md, 6p)
            var dense = DiagramLayout.TooDense(model, plan, out var plannedCrossings, out var dataWires);
            answer["layoutEngine"] = LayoutEngineOurs;
            answer["plannedCrossings"] = plannedCrossings;
            answer["dataWires"] = dataWires;
            if (dense) answer["tooDense"] = true;
            if (dryRun)
            {
                answer["ok"] = true;
                answer["dryRun"] = true;
                answer["note"] = "Nothing was changed. Run again without dryRun to apply the plan." +
                                 (dense ? " The plan crosses " + plannedCrossings + " data wires on " + dataWires +
                                          ": a real run also tries LabVIEW's Clean Up Diagram and keeps the one " +
                                          "that crosses fewer." : "");
                return Json.Document(answer);
            }

            // ---- 3. apply, on a backed-up VI
            var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "layout", Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(work);
            var backup = Path.Combine(work, Path.GetFileName(vi));
            File.Copy(vi, backup, overwrite: true);
            answer["backupPath"] = backup;
            var compiledBefore = BulkTools.ContainsCompiledCode(vi);

            // ---- 3a. too dense for us: LabVIEW's own clean-up, measured with the same count. NI
            // ahead -> keep it and stop here; otherwise our plan is applied over it - every move,
            // size and tunnel in the plan is an absolute position, so where NI left things does
            // not matter (verify says so if it did)
            if (dense)
            {
                var kept = await TryNiCleanupAsync(vi, scripts, plannedCrossings, answer, timeoutSeconds, ct);
                Lap("niCleanup");
                if (kept)
                {
                    if (hold is not null) { await hold.DisposeAsync(); hold = null; }
                    answer["windowView"] = await new DiagramCleanup(connection).FitViewAsync(vi, timeoutSeconds, ct);
                    Lap("fitView");
                    if (render)
                    {
                        var dir = renderDirectory ?? Path.Combine(work, "render");
                        answer["render"] = JsonNode.Parse(await new RenderTools(connection).RenderDiagramsAsync(
                            vi, dir, timeoutSeconds: timeoutSeconds, backend: BackendRoute.Current, ct: ct));
                        Lap("render");
                    }
                    answer["ok"] = true;
                    answer["note"] = "Our plan was too dense, and LabVIEW's Clean Up Diagram crossed fewer data " +
                                     "wires, so its layout was kept and ours not applied. Read niCleanup.";
                    return Json.Document(answer);
                }
            }

            var helper = await GraftTools.HelperAsync(connection, Path.Combine(scripts, ApplyHelper), timeoutSeconds, ct);
            if (helper.Failure is not null) return helper.Failure;
            var run = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(helper.Vi!,
                ApplyInputs(vi, plan).ToJsonString(), timeoutSeconds: timeoutSeconds, ct: ct)) as JsonObject;
            var values = run?["values"] as JsonObject;
            var error = GraftTools.ReadHelperError(run);
            var moveResults = Records(values, "move results");
            var sizeResults = Records(values, "size results");
            var fitResults = Records(values, "size to text results");
            answer["commentsFitted"] = fitResults.Count(r => r.StartsWith("0;", StringComparison.Ordinal));
            var tunnelResults = Records(values, "tunnel results").Concat(Records(values, "shift register results"))
                .Concat(Records(values, "selector results")).Concat(Records(values, "dynamic left results"))
                .Concat(Records(values, "dynamic right results")).ToList();
            answer["helperErrorCode"] = error.Code;
            answer["moveErrors"] = moveResults.Count(r => !r.StartsWith("0;", StringComparison.Ordinal));
            answer["sizeErrors"] = sizeResults.Count(r => !r.StartsWith("0;", StringComparison.Ordinal))
                                   + fitResults.Count(r => !r.StartsWith("0;", StringComparison.Ordinal));
            answer["tunnelErrors"] = tunnelResults.Count(r => !r.StartsWith("0;", StringComparison.Ordinal));
            Lap("apply");
            if (error.Code is not null && error.Code != 0)
            {
                answer["ok"] = false;
                answer["note"] = $"LabVIEW answered error {error.Code} ({error.Source}). The VI may be partly " +
                                 "rearranged; the copy from before is under backupPath.";
                return Json.Document(answer);
            }

            // ---- 4. what LabVIEW made of it - while the VI is still held. The steps after it change
            // wire geometry and compiled code, never where an object is; run after them, the dump
            // had to load the VI cold again, and that was the largest step of a whole project:
            // 37 of 102 s over ATM_DQMH_Cold's 76 VIs, a Caraya test 1.8-2.1 s each (2026-10-18)
            if (verify)
            {
                var again = await DumpAsync(Path.Combine(scripts, DumpHelper), vi, timeoutSeconds, ct);
                if (again.Lines is not null)
                {
                    var after = DiagramModel.Parse(again.Lines);
                    answer["after"] = Outline(TopOutline(after));
                    var (exact, off, worst) = Compare(plan, after);
                    answer["landedExactly"] = exact;
                    answer["landedOff"] = off;
                    answer["worstOffsetPx"] = worst;
                }
                Lap("verify");
            }
            // ---- 5. no compiled code may stay in a VI we wrote - and a pylabview edit must not run
            // under a VI LabVIEW holds, or LabVIEW keeps serving the copy it holds
            if (!compiledBefore && BulkTools.ContainsCompiledCode(vi))
            {
                if (hold is not null) { await hold.DisposeAsync(); hold = null; }
                answer["compiledCodeStripped"] = await new DiagramCleanup(connection).StripCompiledAsync(vi, timeoutSeconds, ct);
                Lap("stripCompiled");
            }

            // ---- 5b. constant fans: wire geometry of our own, after the save whose Clean Up Wire
            // would replace it (measured on Find Account, docs/diagram-layout.md section 6g)
            if (routeFans)
            {
                if (hold is not null) { await hold.DisposeAsync(); hold = null; }
                answer["fanRouting"] = await new DiagramCleanup(connection).FanRouteAsync(vi, timeoutSeconds, ct);
                Lap("fanRouting");
            }

            // ---- 5c. the window's view over the code the layout moved: LabVIEW's save keeps the
            // old view, and a VI opened on an empty diagram (the user's screenshot, 2026-10-31)
            if (hold is not null) { await hold.DisposeAsync(); hold = null; }
            answer["windowView"] = await new DiagramCleanup(connection).FitViewAsync(vi, timeoutSeconds, ct);
            Lap("fitView");

            // ---- 5d. our result as SAVED, measured: a tangled one is given to LabVIEW's Clean Up
            // as well, and NI's layout kept where its wires are drawn better (docs/diagram-layout.md 6q)
            await TangledFallbackAsync(vi, scripts, work, answer, timeoutSeconds, ct);
            Lap("tangledCheck");

            if (render)
            {
                var dir = renderDirectory ?? Path.Combine(work, "render");
                answer["render"] = JsonNode.Parse(await new RenderTools(connection).RenderDiagramsAsync(
                    vi, dir, timeoutSeconds: timeoutSeconds, backend: BackendRoute.Current, ct: ct));
                Lap("render");
            }
            answer["ok"] = true;
            return Json.Document(answer);
        }
        finally
        {
            if (hold is not null) await hold.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>`layoutEngine` in the answer: whose layout the VI was saved with.</summary>
    internal const string LayoutEngineOurs = "lvai_layout_diagram";
    internal const string LayoutEngineNi = "LabVIEW Clean Up Diagram";

    /// <summary>At least this many bends per wire in OUR saved result, or this many backward
    /// wires, and the result counts as tangled.</summary>
    internal const double TangledBendsPerWire = 3.0;
    internal const int TangledBackward = 3;

    /// <summary>
    /// WHEN OUR SAVED RESULT IS TANGLED - set by the user's verdicts on the MP3 VIs (2026-11-04),
    /// both layouts measured from the saved files. NI's Clean Up was preferred for Parse Frames
    /// (ours: 3.5 bends a wire, 8 backward) and Granule Info (3.3, 3 backward); ours was kept for
    /// Huffman Decode (2.8, 2 backward) and for eight more, all at 2.5 bends a wire or less with at
    /// most one backward wire. Crossings counted on the plan had said the opposite for both.
    /// </summary>
    internal static bool Tangled(DiagramCleanup.WireMetrics ours) =>
        ours.BendsPerWire >= TangledBendsPerWire || ours.Backward >= TangledBackward;

    /// <summary>NI's layout replaces a tangled one of ours only when its wires are drawn better:
    /// fewer bends and no more backward wires.</summary>
    internal static bool KeepNiMeasured(DiagramCleanup.WireMetrics ours, DiagramCleanup.WireMetrics? ni) =>
        ni is not null && ni.Bends < ours.Bends && ni.Backward <= ours.Backward;

    /// <summary>
    /// Measures our saved result; when it is tangled, runs LabVIEW's Clean Up Diagram on the VI,
    /// measures that, and keeps whichever <see cref="KeepNiMeasured"/> picks - ours is put back
    /// from a copy otherwise. Writes `wireMetrics` and, when it ran NI, `tangledFallback` into the
    /// answer. The VI must not be held: both measurements read the file.
    /// </summary>
    private async Task TangledFallbackAsync(string vi, string scripts, string work, JsonObject answer,
                                            int timeoutSeconds, CancellationToken ct)
    {
        var cleanup = new DiagramCleanup(connection);
        var ours = await cleanup.WireMetricsAsync(vi, timeoutSeconds, ct);
        if (ours is null) { answer["wireMetrics"] = "not read"; return; }
        answer["wireMetrics"] = ours.ToJson();
        if (!Tangled(ours)) return;

        var report = new JsonObject { ["ours"] = ours.ToJson() };
        answer["tangledFallback"] = report;
        var oursCopy = Path.Combine(work, "ours-" + Path.GetFileName(vi));
        File.Copy(vi, oursCopy, overwrite: true);
        var step = await cleanup.RunNiDiagramAsync(vi, BackendRoute.Current, timeoutSeconds, ct);
        var ran = step["ok"]?.GetValue<bool>() == true && step["skipped"] is null;
        var ni = ran ? await cleanup.WireMetricsAsync(vi, timeoutSeconds, ct) : null;
        report["niRan"] = ran;
        // why the clean-up did not run is the first thing to read when NI was not kept
        if (!ran) report["niStep"] = step.DeepClone();
        if (ni is not null) report["ni"] = ni.ToJson();
        if (ran && KeepNiMeasured(ours, ni))
        {
            report["kept"] = "ni";
            answer["layoutEngine"] = LayoutEngineNi;
            answer["windowView"] = await cleanup.FitViewAsync(vi, timeoutSeconds, ct);
            var dump = await DumpAsync(Path.Combine(scripts, DumpHelper), vi, timeoutSeconds, ct);
            if (dump.Lines is not null) answer["after"] = Outline(TopOutline(DiagramModel.Parse(dump.Lines)));
            answer["note"] = "Our layout came out tangled (bends per wire or backward wires), and LabVIEW's " +
                             "Clean Up Diagram drew the wires better, so its layout was kept. Read tangledFallback.";
            return;
        }
        // ours back - the clean-up saved over it, and LabVIEW holds nothing once its helper closed
        report["kept"] = "ours";
        if (ran)
        {
            try { File.Copy(oursCopy, vi, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report["restoreFailed"] = e.Message;
            }
        }
    }

    /// <summary>NI's layout is kept only when it crosses FEWER data wires than our plan; a tie
    /// keeps ours, and a clean-up that did not run or could not be read again keeps ours.</summary>
    internal static bool KeepNi(int? niCrossings, int ourCrossings) => niCrossings is { } n && n < ourCrossings;

    /// <summary>
    /// LabVIEW's Clean Up Diagram on the held VI, then a dump of what it made, counted as our plan
    /// is counted (<see cref="DiagramLayout.DataCrossings"/> on the positions as they are). Writes
    /// `niCleanup` into the answer and answers whether NI's layout is to be kept; when it is,
    /// `layoutEngine` and `after` say so.
    /// </summary>
    private async Task<bool> TryNiCleanupAsync(string vi, string scripts, int ourCrossings, JsonObject answer,
                                               int timeoutSeconds, CancellationToken ct)
    {
        var report = new JsonObject { ["oursPlanned"] = ourCrossings };
        answer["niCleanup"] = report;
        var step = await new DiagramCleanup(connection).RunNiDiagramAsync(vi, BackendRoute.Current, timeoutSeconds, ct);
        var ran = step["ok"]?.GetValue<bool>() == true && step["skipped"] is null;
        report["ran"] = ran;
        if (!ran)
        {
            report["note"] = step["note"]?.DeepClone();
            report["kept"] = false;
            return false;
        }
        if (step["compiledCodeStripped"] is { } stripped) report["compiledCodeStripped"] = stripped.DeepClone();
        var dump = await DumpAsync(Path.Combine(scripts, DumpHelper), vi, timeoutSeconds, ct);
        int? niCrossings = null;
        if (dump.Lines is not null)
        {
            var niModel = DiagramModel.Parse(dump.Lines);
            niCrossings = DiagramLayout.DataCrossings(niModel, new LayoutPlan());
            report["crossings"] = niCrossings;
            report["size"] = Outline(TopOutline(niModel));
            if (KeepNi(niCrossings, ourCrossings))
            {
                answer["after"] = Outline(TopOutline(niModel));
                answer["layoutEngine"] = LayoutEngineNi;
            }
        }
        else report["note"] = "LabVIEW's layout could not be read back, so ours is applied.";
        report["kept"] = KeepNi(niCrossings, ourCrossings);
        return KeepNi(niCrossings, ourCrossings);
    }

    /// <summary>
    /// Where the diagram's arrowed comments point, read from the SAVED file through pylabview -
    /// no LabVIEW. A failure costs only the arrows: the layout then anchors by nearness as before,
    /// and the answer says why.
    /// </summary>
    internal async Task<(IReadOnlyList<CommentArrow> Arrows, string? Note)> ArrowsAsync(string vi, int timeoutSeconds,
                                                                                    CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "arrows", Path.GetRandomFileName());
        try
        {
            var extract = JsonNode.Parse(await new PyLabviewTools(connection).ExtractAsync(vi, directory,
                annotate: false, timeoutSeconds, ct: ct));
            if (extract?["mainXml"]?.GetValue<string>() is not { } mainXml)
                return ([], "not read: the pylabview extract failed");
            var heap = Path.Combine(directory, Path.GetFileNameWithoutExtension(mainXml) + "_BDHb.xml");
            return File.Exists(heap) ? (CommentArrows.Read(heap), null) : ([], null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ([], "not read: " + e.Message);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal async Task<(List<string>? Lines, string? Failure)> DumpAsync(string aixml, string vi, int timeoutSeconds,
                                                                         CancellationToken ct)
    {
        var helper = await GraftTools.HelperAsync(connection, aixml, timeoutSeconds, ct);
        if (helper.Failure is not null) return (null, helper.Failure);
        var run = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(helper.Vi!,
            new JsonObject { ["vi path"] = vi }.ToJsonString(), timeoutSeconds: timeoutSeconds, ct: ct)) as JsonObject;
        var error = GraftTools.ReadHelperError(run);
        var lines = GraftTools.StringArray(run?["values"] as JsonObject, "lines");
        if ((error.Code is not null && error.Code != 0) || lines.Count == 0)
            return (null, Json.Error("dumpFailed",
                $"The diagram could not be read (error {error.Code}: {error.Source}).", new { viPath = vi }));
        // LABVIEWMCP_LAYOUT_DUMP names a folder that receives each dump as a fixture for the
        // layout tests (tests/.../Fixtures/layout), "<VI name>.txt", the last dump winning
        if (Environment.GetEnvironmentVariable("LABVIEWMCP_LAYOUT_DUMP") is { Length: > 0 } dumpDir)
        {
            try
            {
                Directory.CreateDirectory(dumpDir);
                File.WriteAllLines(Path.Combine(dumpDir, Path.GetFileNameWithoutExtension(vi) + ".txt"), lines);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (lines, null);
    }

    /// <summary>The apply helper's inputs: one op string per move, size, tunnel, shift register,
    /// case selector and dynamic event terminal, and every diagram's path for the wire clean-up.</summary>
    internal static JsonObject ApplyInputs(string vi, LayoutPlan plan) => new()
    {
        ["vi path"] = vi,
        // comments whose text did not fit their box, fitted before anything moves
        ["size to text"] = GraftTools.ArrayXml("size to text", plan.SizeToText),
        ["moves"] = GraftTools.ArrayXml("moves", plan.Moves.Where(m => !m.Late).Select(m => $"{m.ApplyPath};{m.Index};{m.X};{m.Y}")),
        // a While Loop's stop and iteration terminals: after the sizes, since LabVIEW repositions
        // them itself when Frame Size shrinks the loop
        ["late moves"] = GraftTools.ArrayXml("late moves", plan.Moves.Where(m => m.Late).Select(m => $"{m.ApplyPath};{m.Index};{m.X};{m.Y}")),
        ["sizes"] = GraftTools.ArrayXml("sizes", plan.Sizes.Select(s => $"{s.ApplyPath};{s.Index};{s.FrameW};{s.FrameH}")),
        ["tunnels"] = GraftTools.ArrayXml("tunnels", plan.Tunnels.Where(t => !t.ShiftRegister && !t.Selector && !t.Dynamic)
            .Select(t => $"{t.ApplyPath};{t.Index};{t.Tunnel};{t.X};{t.Y}")),
        ["shift registers"] = GraftTools.ArrayXml("shift registers", plan.Tunnels.Where(t => t.ShiftRegister)
            .Select(t => $"{t.ApplyPath};{t.Index};{t.Tunnel};{t.X};{t.Y}")),
        // a case structure's selector, read through {LV.CaseStructure} Selector: the third field unused
        ["selectors"] = GraftTools.ArrayXml("selectors", plan.Tunnels.Where(t => t.Selector)
            .Select(t => $"{t.ApplyPath};{t.Index};0;{t.X};{t.Y}")),
        // an event structure's dynamic event terminals, one pass per side
        ["dynamic left"] = GraftTools.ArrayXml("dynamic left", plan.Tunnels.Where(t => t.Dynamic && t.Tunnel == 0)
            .Select(t => $"{t.ApplyPath};{t.Index};0;{t.X};{t.Y}")),
        ["dynamic right"] = GraftTools.ArrayXml("dynamic right", plan.Tunnels.Where(t => t.Dynamic && t.Tunnel == 1)
            .Select(t => $"{t.ApplyPath};{t.Index};0;{t.X};{t.Y}")),
        ["wires"] = GraftTools.ArrayXml("wires", plan.WireDiagrams),
    };

    /// <summary>One apply pass's answer: a string of records, each ending in '|', fields split by
    /// ';' with the error code first. One string rather than an array keeps the answer small - an
    /// array of 190 records came back as 25 000 characters of XML.</summary>
    internal static List<string> Records(JsonObject? values, string name) =>
        ((values?[name] as JsonObject)?["value"]?.GetValue<string>() ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal static Box TopOutline(DiagramModel model) =>
        Box.Union(model.Top.Objects.Where(o => !o.IsDecoration).Select(o => o.Box));

    private static JsonObject Outline(Box b) => new() { ["width"] = b.W, ["height"] = b.H, ["left"] = b.L, ["top"] = b.T };

    /// <summary>How many planned objects sit where the plan put them, after LabVIEW applied it.</summary>
    internal static (int Exact, int Off, int Worst) Compare(LayoutPlan plan, DiagramModel after)
    {
        int exact = 0, off = 0, worst = 0;
        foreach (var o in after.AllObjects)
        {
            if (!plan.Predicted.TryGetValue(o.Key, out var p)) continue;
            var d = Math.Abs(p.L - o.Box.L) + Math.Abs(p.T - o.Box.T);
            if (d == 0) exact++;
            else { off++; worst = Math.Max(worst, d); }
        }
        return (exact, off, worst);
    }
}
