using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a MALLEABLE VI (<c>.vim</c>) in one call.
///
/// AIXML cannot do it alone - <see cref="MalleableVi"/> carries the five-arm measurement that
/// settles why - so the route is four steps that never vary: generate an ordinary <c>.vi</c>,
/// <c>pylv_extract</c> it, set four LVSR flags, <c>pylv_rebuild</c> to the <c>.vim</c> path. That
/// is the shape <c>CLAUDE.md</c> calls a tool waiting to be written: cheap for LabVIEW, expensive
/// in turns, and identical every time. Driven by hand it is five round trips plus a shell call,
/// and each round trip is a model turn worth about seven seconds.
///
/// IT VERIFIES, which is the part a caller cannot skip. <c>execState</c> is the only cheap check
/// that sees this failure at all: the generate, the convert, the pane measurement and both
/// linters are green on a broken <c>.vim</c>.
/// </summary>
[McpServerToolType]
internal sealed class MalleableTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = Json.Answer;

    [McpServerTool(Name = "lvai_make_malleable", Destructive = true, OpenWorld = true,
                   Title = "Create a malleable VI (.vim) from AIXML in one call")]
    [Description("""
        Generate: creates a MALLEABLE VI (.vim) from AIXML in one call - generates an ordinary .vi,
        sets the .vim flags with pylabview, rebuilds to the .vim path and reads execState back. Use
        it for every .vim: lvai_generate_vi refuses a .vim path, and only execState sees a broken
        malleable VI. `ok` is false unless the result is eIdle; the intermediate .vi is gated too,
        so a diagram broken as an ordinary VI fails there with the real fault.
        The intermediate .vi is KEPT beside the .vim (`intermediateViPath`) - regenerate from it, and
        use it as the pane donor for lvai_placeholder_subvi. The project is NOT closed: when the
        target already existed LabVIEW may still serve its old copy, which `staleReadRisk` reports
        with the remedy. docs/malleable-vis.md.
        """)]
    public async Task<string> MakeMalleableAsync(
        [Description(@"Absolute path to the source AIXML .xml file")] string aiXmlFilePath,
        [Description(@"Absolute path of the .vim to create - WILL BE OVERWRITTEN")] string viPath,
        [Description("""
            Where to put the intermediate ordinary .vi; defaults to the .vim path with a .vi extension.
            """)]
        string? intermediateViPath = null,
        [Description("""
            Keep the pylabview bundle and report where (for debugging a patch step).
            """)]
        bool keepBundle = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_make_malleable", async () =>
        {
            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            if (!MalleableVi.IsMalleableTarget(viPath))
                return Json.Error("badArguments",
                    $"viPath '{viPath}' does not end in .vim. This tool exists to create a "
                    + "MALLEABLE VI; for an ordinary one use lvai_generate_vi, which is the same "
                    + "generate-validate-pane sequence without the flag patch.",
                    new { viPath = Path.GetFullPath(viPath) });

            if (!File.Exists(aiXmlFilePath))
                return Json.Error("badArguments", $"No file at aiXmlFilePath '{aiXmlFilePath}'.");

            var bundleTool = PyLabview.Locate();
            if (bundleTool is null)
                return Json.Error("notProvisioned", PyLabview.NotProvisionedMessage());

            var vim = Path.GetFullPath(viPath);
            var intermediate = Path.GetFullPath(
                intermediateViPath ?? Path.ChangeExtension(vim, ".vi"));
            var targetExisted = File.Exists(vim);

            // ---------------------------------------------------------------- 1. the ordinary VI
            var generate = await new BulkTools(connection)
                .GenerateViAsync(aiXmlFilePath, intermediate, timeoutSeconds: timeoutSeconds, ct: ct, measureDiagram: false,
                                  cleanUpDiagram: true);
            steps.Add(new JsonObject { ["step"] = "generate", ["answer"] = Parse(generate) });
            if (Parse(generate)?["ok"]?.GetValue<bool>() != true)
                return Outcome(false, "generate", steps, total, vim, intermediate, targetExisted,
                    "The intermediate .vi was not generated, so nothing malleable was attempted. "
                    + "Read steps[0]: this is the ordinary lvai_generate_vi answer, and a "
                    + "connectorPane failure there still means the .vi was written.");

            // ---------------------------------------------------------------- 2. it must RUN first
            // A diagram that is broken as an ordinary VI is not rescued by any flag below. Gating
            // here is what makes a failure name the real fault instead of arriving later as a
            // malleable VI that will not load.
            var sourceState = await new ExecStateTools(connection)
                .ExecStateAsync(intermediate, timeoutSeconds: timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "execStateSource", ["answer"] = Parse(sourceState) });
            if (Parse(sourceState)?["execState"]?.GetValue<int>() != 1)
                return Outcome(false, "execStateSource", steps, total, vim, intermediate,
                    targetExisted,
                    "The intermediate .vi is not executable, so the flag patch was not attempted - "
                    + "it would only have produced a broken .vim with a less useful message. Fix "
                    + "the diagram first; the .vi is on disk at intermediateViPath.");

            // ---------------------------------------------------------------- 3. extract
            var bundleDirectory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "malleable",
                                               Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(bundleDirectory);
            var mainXml = Path.Combine(bundleDirectory,
                Path.GetFileNameWithoutExtension(intermediate).Replace(" ", "") + ".xml");

            var extract = await PyLabview.RunAsync(bundleTool, bundleTool.ReadRsrcPy,
                ["-x", "-i", intermediate, "-m", mainXml], Rpc.ClampToolWait(timeoutSeconds), ct);
            steps.Add(new JsonObject
            {
                ["step"] = "extract",
                ["exitCode"] = extract.ExitCode,
                ["mainXml"] = mainXml,
                ["elapsedMs"] = extract.ElapsedMs,
            });
            if (extract.ExitCode != 0 || !File.Exists(mainXml))
                return Outcome(false, "extract", steps, total, vim, intermediate, targetExisted,
                    $"pylabview could not read the intermediate .vi back: exit {extract.ExitCode}. "
                    + $"The bundle directory is {bundleDirectory}.", bundleDirectory);

            // ---------------------------------------------------------------- 4. the four flags
            var patch = MalleableVi.PatchBundle(mainXml, Path.GetFileName(vim));
            var flagsSet = new JsonArray();
            foreach (var flag in patch.FlagsSet) flagsSet.Add(flag);
            var flagsNotFound = new JsonArray();
            foreach (var flag in patch.FlagsNotFound) flagsNotFound.Add(flag);
            steps.Add(new JsonObject
            {
                ["step"] = "flags",
                ["flagsSet"] = flagsSet,
                ["flagsNotFound"] = flagsNotFound,
                ["renamedFrom"] = patch.OldName,
                ["renamedTo"] = patch.NewName,
            });
            if (patch.FlagsNotFound.Length > 0)
                return Outcome(false, "flags", steps, total, vim, intermediate, targetExisted,
                    "The bundle does not carry "
                    + string.Join(", ", patch.FlagsNotFound)
                    + " - so it is not the LVSR record this patch expects, and writing the rest "
                    + "would produce a file whose state nobody can reason about. The bundle is "
                    + $"kept at {bundleDirectory}.", bundleDirectory);

            // ---------------------------------------------------------------- 5. rebuild
            var rebuild = await PyLabview.RunAsync(bundleTool, bundleTool.ReadRsrcPy,
                ["-c", "-m", mainXml, "-i", vim], Rpc.ClampToolWait(timeoutSeconds), ct);
            steps.Add(new JsonObject
            {
                ["step"] = "rebuild",
                ["exitCode"] = rebuild.ExitCode,
                ["elapsedMs"] = rebuild.ElapsedMs,
            });
            if (rebuild.ExitCode != 0)
                return Outcome(false, "rebuild", steps, total, vim, intermediate, targetExisted,
                    $"pylabview exited {rebuild.ExitCode} writing the .vim. The bundle is kept at "
                    + $"{bundleDirectory}.", bundleDirectory);

            // ---------------------------------------------------------------- 6. the only real check
            var state = await new ExecStateTools(connection)
                .ExecStateAsync(vim, timeoutSeconds: timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "execState", ["answer"] = Parse(state) });
            var executable = Parse(state)?["execState"]?.GetValue<int>() == 1;

            if (!keepBundle && Directory.Exists(bundleDirectory))
                try { Directory.Delete(bundleDirectory, recursive: true); }
                catch (IOException) { keepBundle = true; }
                catch (UnauthorizedAccessException) { keepBundle = true; }

            return Outcome(executable, executable ? null : "execState", steps, total, vim,
                intermediate, targetExisted,
                executable
                    ? "The .vim is executable. That says LabVIEW can load it and NOT that it "
                      + "adapts - only a caller wiring two genuinely different types into it shows "
                      + "malleability, and from inside a project that caller needs "
                      + "lvai_placeholder_subvi plus lvai_swap_subvis."
                    : "The .vim was written and is NOT executable. If the target already existed, "
                      + "read staleReadRisk first - this check may be reading the copy LabVIEW "
                      + "still holds rather than the file just written.",
                keepBundle ? bundleDirectory : null);
        }));

    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                  string vimPath, string intermediate, bool targetExisted,
                                  string note, string? bundleDirectory = null)
    {
        total.Stop();
        var result = new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["viPath"] = vimPath,
            ["viExistsNow"] = File.Exists(vimPath),
            ["viBytes"] = File.Exists(vimPath) ? new FileInfo(vimPath).Length : 0,
            ["intermediateViPath"] = intermediate,
        };

        // NOT a verdict and not something this tool can settle - LabVIEW's memory is not readable
        // from here. It is reported because the failure it warns about is silent: the rebuild
        // writes the file, LabVIEW keeps serving the copy it already had, and every check
        // afterwards confirms the VI that was replaced. Measured; it cost a wrong conclusion.
        if (targetExisted)
            result["staleReadRisk"] =
                "The .vim already existed, so LabVIEW may hold it and execState above may describe "
                + "the copy it replaced rather than the file just written. Close the project "
                + "(lvai_close_active_project) and read lvai_exec_state again, or build to a path "
                + "LabVIEW has never loaded.";

        if (bundleDirectory is not null) result["bundleDirectory"] = bundleDirectory;
        result["steps"] = steps;
        result["totalElapsedMs"] = total.ElapsedMilliseconds;
        result["note"] = note;
        return result.ToJsonString(Indented);
    }
}
