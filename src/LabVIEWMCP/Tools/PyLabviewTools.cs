using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The pylabview route: reading and rewriting LabVIEW's binary VI format directly, with no
/// LabVIEW running and no Python installed.
///
/// These tools do NOT replace the lvai_* surface, and the dependency runs one way. AIXML creates
/// and names - every primitive name and terminal role these tools annotate with was harvested by
/// joining against AIXML exports - while pylabview edits and reads. pylabview cannot author a VI
/// from nothing: it has no empty starting point, so every VI it produces descends from one
/// LabVIEW or NI made first. Full argument in experiments/pylabview/FINDINGS.md section 5.
/// </summary>
[McpServerToolType]
internal sealed class PyLabviewTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = Json.Answer;

    // NOT a const: the right advice differs between a checkout and a binary-only install, and
    // only the running exe can tell them apart - see PyLabview.NotProvisionedMessage. Naming
    // provision.ps1 unconditionally is what sent one binary install's owner looking for a folder
    // that install does not have.
    private static string NotProvisioned => PyLabview.NotProvisionedMessage();

    // ---------------------------------------------------------------- status

    [McpServerTool(Name = "pylv_status", ReadOnly = true, Title = "pylabview bundle status")]
    [Description("""
        Status: whether the bundled pylabview is present and usable - directory, Python version,
        pinned commit, name tables, the server version and the AIXML backend default. Start here:
        every other pylv_* tool needs the bundle. A source checkout has none until
        tools\pylabview\provision.ps1 has run; a binary install carries it (if missing, update the
        plugin). Needs NO running LabVIEW and no Python installation.
        """)]
    public Task<string> StatusAsync(CancellationToken ct = default) =>
        Rpc.GuardAsync(() =>
        {
            var bundle = PyLabview.Locate();
            if (bundle is null)
                return Task.FromResult(Json.Error("notProvisioned", NotProvisioned, new
                {
                    // On the failure path especially: "the bundle is missing" is usually "this
                    // install is older than v0.9.2", and the version is what settles that without
                    // a second install to compare against.
                    serverVersion = ServerVersion.Current.Display,
                    serverCommit = ServerVersion.Current.Commit,
                    searched = new[]
                    {
                        PyLabview.DirectoryVariable + " (environment)",
                        Path.Combine(AppContext.BaseDirectory, "pylabview"),
                        "tools\\pylabview\\runtime, walking up from the exe",
                    },
                }));

            return Task.FromResult(new JsonObject
            {
                ["ok"] = true,
                // Which build of the SERVER is answering, not of the bundle. Reported here as
                // well as in lvai_status because this tool needs no running LabVIEW, and a stale
                // or half-extracted install is suspected exactly when LabVIEW is not up.
                ["serverVersion"] = ServerVersion.Current.Display,
                ["serverCommit"] = ServerVersion.Current.Commit,
                ["directory"] = bundle.Directory,
                ["pythonVersion"] = bundle.PythonVersion,
                ["pythonArch"] = bundle.PythonArch,
                ["pylabviewCommit"] = bundle.PylabviewCommit,
                ["provisionedUtc"] = bundle.ProvisionedUtc,
                // Without these two the annotation is a no-op and the XML stays numeric, which is
                // the difference between a readable diagram and a heap of integers.
                ["primitiveNamesTable"] = bundle.PrimitiveNamesTsv,
                ["terminalNamesTable"] = bundle.TerminalNamesTsv,
                ["canAnnotate"] = bundle.AnnotatePy is not null &&
                                  bundle.PrimitiveNamesTsv is not null,
                // Which converter the AIXML tools use when a call names none, and whether the
                // pylabview one can run here. docs/aixml-backends.md.
                ["aixmlBackend"] = AixmlComposer.Describe(),
            }.ToJsonString(Indented));
        });

    // ---------------------------------------------------------------- extract

    [McpServerTool(Name = "pylv_extract", ReadOnly = true, Title = "VI to XML with pylabview")]
    [Description("""
        pylabview: extracts a .vi, .ctl or .llb into a directory of XML plus binary sidecars
        (lossless round trip with pylv_rebuild). Read-only, needs NO running LabVIEW. Reads what
        AIXML cannot: icons, panel images, layout, decorations, typedef bindings. A .lvclass,
        .lvlib or .lvproj is plain XML already and is refused - read it directly. `annotate` writes
        primitive and terminal names in as inert XML comments (a trailing `?` marks a weakly
        attested name). `rawFallbackWarnings` lists blocks copied through unparsed - not damage.
        For a whole edit cycle use pylv_apply instead.
        """)]
    public Task<string> ExtractAsync(
        [Description("Absolute path to the .vi/.ctl/.llb to read")] string viPath,
        [Description("Directory to write the XML bundle into; created if absent")]
        string outDirectory,
        [Description("Write primitive and terminal names in as XML comments")] bool annotate = true,
        [Description("Local budget in seconds; clamped to what an MCP client will await")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        Rpc.GuardAsync(async () =>
        {
            var bundle = PyLabview.Locate();
            if (bundle is null) return Json.Error("notProvisioned", NotProvisioned);
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            var extension = Path.GetExtension(viPath);
            if (extension is ".lvclass" or ".lvlib" or ".lvproj")
                return Json.Error("notAnRsrcFile",
                    $"'{extension}' files are already plain XML in LabVIEW 2026 - pylabview " +
                    "correctly refuses them because they are not RSRC containers. Read the file " +
                    "with an XML parser, or use lvai_describe_project for a .lvproj.");

            Directory.CreateDirectory(outDirectory);
            var mainXml = Path.Combine(outDirectory,
                Path.GetFileNameWithoutExtension(viPath).Replace(" ", "") + ".xml");

            var budget = Rpc.ClampToolWait(timeoutSeconds);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", viPath, "-m", mainXml], budget, ct);
            if (extract.ExitCode != 0)
                return Json.Error("extractFailed",
                    $"pylabview exited {extract.ExitCode}.", new { stderr = extract.StdErr });

            JsonNode? annotation = null;
            if (annotate && bundle.AnnotatePy is not null)
            {
                var run = await PyLabview.RunAsync(bundle, bundle.AnnotatePy,
                    [outDirectory], budget, ct);
                annotation = new JsonObject
                {
                    ["ran"] = true,
                    ["exitCode"] = run.ExitCode,
                    ["output"] = run.StdOut.Trim(),
                };
            }
            else if (annotate)
            {
                annotation = new JsonObject
                {
                    ["ran"] = false,
                    ["why"] = "annotate_names.py did not travel with this bundle; re-provision.",
                };
            }

            var files = new JsonArray();
            foreach (var f in Directory.GetFiles(outDirectory).OrderBy(f => f))
                files.Add(Path.GetFileName(f));

            var warnings = new JsonArray();
            foreach (var w in extract.Warnings) warnings.Add(w);

            return new JsonObject
            {
                ["ok"] = true,
                ["viPath"] = viPath,
                ["bundleDirectory"] = Path.GetFullPath(outDirectory),
                ["mainXml"] = Path.GetFullPath(mainXml),
                ["fileCount"] = files.Count,
                ["files"] = files,
                ["annotation"] = annotation,
                ["rawFallbackWarnings"] = warnings,
                ["elapsedMs"] = extract.ElapsedMs,
                ["note"] = "Blocks named in rawFallbackWarnings were copied through unparsed. " +
                           "That is why the round trip is lossless, not a sign of damage.",
            }.ToJsonString(Indented);
        });

    // ---------------------------------------------------------------- rebuild

    [McpServerTool(Name = "pylv_rebuild", Destructive = true, OpenWorld = true,
                   Title = "XML back to a VI with pylabview (writes a .vi)")]
    [Description("""
        pylabview: rebuilds a .vi from a pylv_extract bundle, OVERWRITING viPath. Needs NO running
        LabVIEW. Prefer pylv_apply, which runs the whole cycle with its gates.
        THE PATH MUST NOT BE LOADED IN LABVIEW: the write succeeds anyway and LabVIEW keeps serving
        its stale copy, so a later check confirms the VI you REPLACED. Close the project first
        (lvai_close_active_project), or rebuild to a fresh path. Byte equality with the original is
        the wrong test (different zlib level). Verify afterwards - this tool cannot: lvai_exec_state
        (0 is broken), then AIXML-export and look; `gatesNotChecked` lists what is left.
        """)]
    public Task<string> RebuildAsync(
        [Description("Absolute path to the bundle's MAIN .xml (the one pylv_extract reports as mainXml)")]
        string mainXmlPath,
        [Description("Absolute path of the .vi to write - WILL BE OVERWRITTEN")] string viPath,
        [Description("Local budget in seconds; clamped to what an MCP client will await")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        Rpc.GuardAsync(async () =>
        {
            var bundle = PyLabview.Locate();
            if (bundle is null) return Json.Error("notProvisioned", NotProvisioned);
            if (!File.Exists(mainXmlPath))
                return Json.Error("badArguments", $"No file at mainXmlPath '{mainXmlPath}'.");

            var existed = File.Exists(viPath);
            var run = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-c", "-m", mainXmlPath, "-i", viPath], Rpc.ClampToolWait(timeoutSeconds), ct);

            if (run.ExitCode != 0)
                return Json.Error("rebuildFailed",
                    $"pylabview exited {run.ExitCode}.", new { stderr = run.StdErr });

            var warnings = new JsonArray();
            foreach (var w in run.Warnings) warnings.Add(w);

            return new JsonObject
            {
                ["ok"] = true,
                ["viPath"] = Path.GetFullPath(viPath),
                ["viExisted"] = existed,
                ["viBytes"] = File.Exists(viPath) ? new FileInfo(viPath).Length : 0,
                ["rawFallbackWarnings"] = warnings,
                ["elapsedMs"] = run.ElapsedMs,
                ["gatesNotChecked"] = new JsonArray
                {
                    "the path was not released from LabVIEW's memory - call lvai_close_vi first",
                    "ExecState was not read - do it through VI Server; 0 means eBad",
                    "the intended change was not confirmed - AIXML-export the result and look",
                },
            }.ToJsonString(Indented);
        });

    // ---------------------------------------------------------------- route

    [McpServerTool(Name = "pylv_route", ReadOnly = true,
                   Title = "Which route can edit this VI, AIXML or pylabview")]
    [Description("""
        pylabview: decides whether an EXISTING VI can be edited through AIXML or must go through
        pylabview, and says why (`route`, `routeReason`). Call it BEFORE planning an edit. Check A
        validates the untouched export (a failure means AIXML cannot rebuild it - typically a Call
        to the VI's own subVIs); check B scans for node families that validate but come back
        GUTTED (`silentlyUnsupported`). Neither sees a TYPEDEF - an AIXML edit replaces every
        typedef with a de-linked copy; check with pylv_extract separately. It decides, it does not
        edit. Needs a running LabVIEW.
        """)]
    public Task<string> RouteAsync(
        [Description("Absolute path to the .vi to assess")] string viPath,
        [Description("Local budget in seconds")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "route",
                Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(scratch);
            var exportPath = Path.Combine(scratch, "export.xml");

            try
            {
                var export = await connection.InvokeAsync((c, t) =>
                    c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                    {
                        ViPath = viPath,
                        AiXMLFilePath = exportPath,
                    }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync,
                    ct);

                if (export.ErrorCode != 0 || !File.Exists(exportPath))
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["route"] = "pylabview",
                        ["routeReason"] = "The VI could not be exported to AIXML at all " +
                            $"(errorCode {export.ErrorCode}): {export.ErrorMessage}",
                        ["aixmlExported"] = false,
                        ["aixmlRoundTrip"] = false,
                    }.ToJsonString(Indented);

                var aixml = await File.ReadAllTextAsync(exportPath, ct);
                var silent = PyLabview.ScanSilentlyUnsupported(aixml);

                var symbolic = SymbolicUids.Prepare(exportPath);
                var validate = await connection.InvokeAsync((c, t) =>
                    c.ValidateAIXMLAsync(
                        new ValidateAIXMLRequest { AiXMLFilePath = symbolic.PathForLabview },
                        deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync,
                    ct);
                var message = SymbolicUids.Annotate(validate.ErrorMessage, symbolic.Map);

                var roundTrips = validate.ErrorCode == 0;
                var reasons = new List<string>();
                if (!roundTrips)
                    reasons.Add("check A failed: ValidateAIXML on the untouched export returned " +
                                $"errorCode {validate.ErrorCode}: {Summarise(message)}");
                foreach (var family in silent)
                    reasons.Add($"check B: the diagram contains a {family}, which validates and " +
                                "then loses its configuration on regeneration");

                var route = reasons.Count == 0 ? "aixml" : "pylabview";
                var reasonText = reasons.Count == 0
                    ? "AIXML round-tripped its own export of this VI cleanly and the diagram " +
                      "contains no silently-unsupported node family."
                    : string.Join("; ", reasons);

                var silentJson = new JsonArray();
                foreach (var family in silent) silentJson.Add(family);

                return new JsonObject
                {
                    ["ok"] = true,
                    ["route"] = route,
                    ["routeReason"] = reasonText,
                    ["aixmlExported"] = true,
                    ["aixmlRoundTrip"] = roundTrips,
                    ["silentlyUnsupported"] = silentJson,
                    ["validateErrorCode"] = validate.ErrorCode,
                    ["validateErrorMessage"] = message,
                    ["exportBytes"] = aixml.Length,
                    ["note"] = route == "pylabview"
                        ? "pylv_extract can read and rewrite this VI, but it cannot invent the " +
                          "edit: author the heap change yourself, and release the path with " +
                          "lvai_close_vi before rebuilding."
                        : "Editing through AIXML is available. It is also 37x smaller than the " +
                          "pylabview bundle and needs no name tables.",
                }.ToJsonString(Indented);
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
            }
        });

    /// <summary>First meaningful line of a LabVIEW error blob, which is where the cause is.</summary>
    private static string Summarise(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "(no message)";
        var interesting = message
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("Unsupported SubVI", StringComparison.Ordinal) ||
                                 l.Contains("Event", StringComparison.Ordinal) ||
                                 l.Contains("not found", StringComparison.Ordinal));
        return interesting ?? message.Split('\n')[0].Trim();
    }
}
