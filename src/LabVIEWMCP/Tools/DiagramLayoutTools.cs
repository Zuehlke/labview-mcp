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
        MUTATING: rearranges a VI's whole block diagram and SAVES IT IN PLACE - our replacement for
        LabVIEW's Clean Up Diagram (BD.CleanUp), for a large existing VI as much as for a
        generated one. A copy of the VI from before is kept and named under backupPath.
        The layout: columns follow data flow; the error chain runs as one straight rail along the
        bottom of each diagram with everything else stacked above it; parallel structures stack
        vertically; on the top level controls go to the first column and indicators to the last; a
        constant hangs left of the terminal it feeds; a comment moves with the object it describes;
        every While/For Loop, Case and Event Structure is sized to its contents (Frame Size), its
        tunnels levelled with what they connect, and every wire cleaned up. After the save, each
        constant fan (two or more constants into one node) gets staggered wire geometry of its own.
        Measured on a DQMH Main.vi: NI's Clean Up spread its diagram to 3805 x 1692 px; the
        original Delacor layout was 1344 x 805.
        Structures of a kind not yet measured (sequences, timed loops, in-place, disable) move as a
        whole with their insides untouched - the answer names them.
        dryRun computes and reports the plan without touching the VI. render puts PNGs of the
        result beside the answer. verify (default) dumps the diagram again afterwards and reports
        how many objects landed where they were meant to.
        Works on the pylabview backend over ActiveX with no project open; the VI is opened by path.
        A DQMH Main.vi IS laid out when named here - only the automatic clean-up after generation
        skips it.
        """)]
    public async Task<string> LayoutDiagramAsync(
        [Description("Absolute path of the VI to lay out. Saved in place")] string viPath,
        [Description("Compute and report the plan, change nothing")] bool dryRun = false,
        [Description("Dump the diagram again afterwards and compare it with the plan")] bool verify = true,
        [Description("Render the laid-out diagram to PNG, into renderDirectory")] bool render = false,
        [Description("Where the PNGs go when render is true; a temp folder otherwise")] string? renderDirectory = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 240,
        [Description("After the save, give each CONSTANT FAN - two or more constants wired into one node - staggered wire geometry of our own through the heap, where Clean Up Wire may cross them. Skipped on a VI that carries compiled code")] bool routeFans = true,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_layout_diagram",
            () => LayoutCoreAsync(viPath, dryRun, verify, render, renderDirectory, timeoutSeconds, ct, routeFans)));

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
        var hold = await LabViewCom.HoldAsync(vi, timeoutSeconds, ct);
        try
        {
            // ---- 1. read
            var dump = await DumpAsync(Path.Combine(scripts, DumpHelper), vi, timeoutSeconds, ct);
            if (dump.Failure is not null) return dump.Failure;
            var model = DiagramModel.Parse(dump.Lines!);
            var before = TopOutline(model);

            // ---- 2. decide
            var plan = DiagramLayout.Plan(model);
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
            };
            if (dryRun)
            {
                answer["ok"] = true;
                answer["dryRun"] = true;
                answer["note"] = "Nothing was changed. Run again without dryRun to apply the plan.";
                return Json.Document(answer);
            }

            // ---- 3. apply, on a backed-up VI
            var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "layout", Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(work);
            var backup = Path.Combine(work, Path.GetFileName(vi));
            File.Copy(vi, backup, overwrite: true);
            answer["backupPath"] = backup;
            var compiledBefore = BulkTools.ContainsCompiledCode(vi);

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
            if (error.Code is not null && error.Code != 0)
            {
                answer["ok"] = false;
                answer["note"] = $"LabVIEW answered error {error.Code} ({error.Source}). The VI may be partly " +
                                 "rearranged; the copy from before is under backupPath.";
                return Json.Document(answer);
            }

            // ---- 4. no compiled code may stay in a VI we wrote - and a pylabview edit must not run
            // under a VI LabVIEW holds, or LabVIEW keeps serving the copy it holds
            if (!compiledBefore && BulkTools.ContainsCompiledCode(vi))
            {
                if (hold is not null) { await hold.DisposeAsync(); hold = null; }
                answer["compiledCodeStripped"] = await new DiagramCleanup(connection).StripCompiledAsync(vi, timeoutSeconds, ct);
            }

            // ---- 4b. constant fans: wire geometry of our own, after the save whose Clean Up Wire
            // would replace it (measured on Find Account, docs/diagram-layout.md section 6g)
            if (routeFans)
            {
                if (hold is not null) { await hold.DisposeAsync(); hold = null; }
                answer["fanRouting"] = await new DiagramCleanup(connection).FanRouteAsync(vi, timeoutSeconds, ct);
            }

            // ---- 5. what LabVIEW made of it
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
            }
            if (render)
            {
                var dir = renderDirectory ?? Path.Combine(work, "render");
                answer["render"] = JsonNode.Parse(await new RenderTools(connection).RenderDiagramsAsync(
                    vi, dir, timeoutSeconds: timeoutSeconds, backend: BackendRoute.Current, ct: ct));
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
