using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Everything that acts on the running IDE or on disk: open, run, build, palette drops.
/// These are the RPCs Nigel itself never uses - running and building are the capabilities
/// that make this more than an editor assistant.
/// </summary>
[McpServerToolType]
internal sealed class ActionTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_run_vi_as_top_level", Destructive = true, OpenWorld = true,
                   Title = "Run a VI as top level")]
    [Description("""
        RPC RunVIAsTopLevel. MUTATING: actually EXECUTES the VI in LabVIEW with the given
        control values and returns its indicator values. Side effects are whatever the VI does
        - it can drive hardware, write files or move a stage.
        inputs/outputs are string maps, and the values really do cross as STRINGS: measured,
        LabVIEW does NOT coerce them to the control's type. String controls work; a numeric or
        path control fails with errorCode 91 at Control Value:Set BEFORE the VI runs - as "42"
        and as 42 alike. So take numbers and paths in as strings and convert them on the
        diagram. On the way out an array or cluster indicator also fails to marshal, and there
        errorCode 91 arrives AFTER the VI has run correctly - it is not proof of failure.
        Details and the measurements in lvai_aixml_reference section 10.
        Never run a VI you have not inspected with lvai_describe_vi first.
        """)]
    public async Task<string> RunViAsTopLevelAsync(
        [Description(@"Absolute path to the .vi to run")] string viPath,
        [Description("""
            Control values as JSON object, e.g. {"X":"3","Y":"4"}. Keys are control labels.
            Omit for a VI that needs no inputs.
            """)]
        string? inputsJson = null,
        [Description("Local budget in seconds - raise it for long-running VIs")]
        int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var request = new RunVIAsTopLevelRequest { ViPath = viPath };
            foreach (var (key, value) in Rpc.ParseStringMap(inputsJson, nameof(inputsJson)))
                request.Inputs[key] = value;

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            return Json.Message(response,
                ("inputsSent", JsonValue.Create(request.Inputs.Count)),
                ("elapsedMs", JsonValue.Create(stopwatch.ElapsedMilliseconds)));
        });

    [McpServerTool(Name = "lvai_build_from_build_specification", Destructive = true, OpenWorld = true,
                   Title = "Build a project build specification")]
    [Description("""
        RPC BuildFromBuildSpecification. MUTATING: runs a build specification of a .lvproj and
        returns the generated files. Writes build output to disk and can take minutes - raise
        timeoutSeconds accordingly. This is the CI-shaped capability of the interface.
        """)]
    public async Task<string> BuildFromBuildSpecificationAsync(
        [Description(@"Absolute path to the .lvproj")] string projectPath,
        [Description("Exact name of the build specification as it appears in the project")]
        string buildSpecificationName,
        [Description("Local budget in seconds - builds are slow")] int timeoutSeconds = 900,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.BuildFromBuildSpecificationAsync(new BuildFromBuildSpecificationRequest
                {
                    ProjectPath = projectPath,
                    BuildSpecificationName = buildSpecificationName,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            return Json.Message(response, ("elapsedMs", JsonValue.Create(stopwatch.ElapsedMilliseconds)));
        });

    /// <summary>
    /// The path when it carries the extension it does NOT belong to, else null. Compared on the
    /// extension alone: a caller who swapped the two parameters still passed a real path, so there is
    /// nothing else to go on.
    /// </summary>
    internal static string? SwappedPath(string? path, string wrongExtension) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).Equals(wrongExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;

    [McpServerTool(Name = "lvai_open_file", Destructive = true, OpenWorld = true,
                   Title = "Open a VI or project in the LabVIEW IDE")]
    [Description("""
        RPC OpenFile. MUTATING (IDE state): opens a VI and/or a project in the running LabVIEW
        editor. Pass the VI pair, the project pair, or both. Harmless but visible to whoever is
        sitting in front of LabVIEW.
        THERE IS NO `filePath` PARAMETER, and getting that wrong is expensive because the failure
        lies. A near-miss argument name is folded onto the closest declared one, so `filePath` lands
        on `viPath` - and a .lvproj passed as a VI comes back as `Error 7, File not found` for a file
        that plainly exists. Measured 2026-08-27: three identical Error 7 answers, while
        lvai_describe_project read the very same path with errorCode 0. A .lvproj goes in
        `projectPath` WITH `projectName`; that pair returned No Error immediately.
        """)]
    public async Task<string> OpenFileAsync(
        [Description(@"Absolute path to the .vi, or empty")] string? viPath = null,
        [Description("VI name, or empty")] string? viName = null,
        [Description(@"Absolute path to the .lvproj, or empty")] string? projectPath = null,
        [Description("Project name, or empty")] string? projectName = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // The path/parameter swap is worth catching here rather than letting LabVIEW answer
            // "File not found" about a file that exists - that answer sends the reader to check the
            // disk, which is the one place the fault is not.
            if (SwappedPath(viPath, ".lvproj") is { } projectAsVi)
                return Json.Error("badArguments",
                    $"viPath is a project file ({projectAsVi}). A .lvproj must go in projectPath, "
                    + "with projectName alongside it; passed as a VI, LabVIEW answers 'Error 7, File "
                    + "not found'. Note there is no filePath parameter - a near-miss name is folded "
                    + "onto viPath, which is how this usually happens.");

            if (SwappedPath(projectPath, ".vi") is { } viAsProject)
                return Json.Error("badArguments",
                    $"projectPath is a VI ({viAsProject}). A .vi must go in viPath, with viName "
                    + "alongside it.");

            var response = await connection.InvokeAsync((c, t) =>
                c.OpenFileAsync(new OpenFileRequest
                {
                    ViPath = viPath ?? "",
                    ViName = viName ?? "",
                    ProjectPath = projectPath ?? "",
                    ProjectName = projectName ?? "",
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_find_palette_item", Destructive = true,
                   Title = "Highlight a palette item in the IDE")]
    [Description("""
        RPC FindPaletteItem. MUTATING (IDE state): makes LabVIEW reveal/highlight the palette
        item with the given GUID. Purely a UI action - useful to confirm a GUID resolves.
        """)]
    public async Task<string> FindPaletteItemAsync(
        [Description("Palette item GUID")] string guid,
        [Description("Local budget in seconds")] int timeoutSeconds = 60,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.FindPaletteItemAsync(new FindPaletteItemRequest { Guid = guid },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_drop_palette_item", Destructive = true, OpenWorld = true,
                   Title = "Drop a palette item onto a VI")]
    [Description("""
        RPC DropPaletteItem. MUTATING: places the palette item with the given GUID onto the
        block diagram of the target VI. This edits real code. Prefer the AIXML path
        (lvai_apply_aixml_to_vi) when you need control over placement and wiring - a drop
        gives you neither.
        """)]
    public async Task<string> DropPaletteItemAsync(
        [Description("Palette item GUID")] string guid,
        [Description(@"Absolute path to the target .vi")] string? viPath = null,
        [Description("VI name, or empty")] string? viName = null,
        [Description(@"Absolute path to the owning .lvproj, or empty")] string? projectPath = null,
        [Description("Project name, or empty")] string? projectName = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.DropPaletteItemAsync(new DropPaletteItemRequest
                {
                    Guid = guid,
                    ViPath = viPath ?? "",
                    ViName = viName ?? "",
                    ProjectPath = projectPath ?? "",
                    ProjectName = projectName ?? "",
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_log_usage_data", Destructive = true, Idempotent = false,
                   Title = "Write a usage-telemetry key/value")]
    [Description("""
        RPC LogUsageData. Writes a key/value pair into LabVIEW's usage telemetry. Included for
        completeness of the interface; it emits analytics data, so there is rarely a reason to
        call it.
        """)]
    public async Task<string> LogUsageDataAsync(
        [Description("Telemetry key")] string key,
        [Description("Telemetry value")] string value,
        [Description("Local budget in seconds")] int timeoutSeconds = 30,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.LogUsageDataAsync(new LogUsageDataRequest { Key = key, Value = value },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

}
