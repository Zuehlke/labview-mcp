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
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private const string NotProvisioned =
        "The pylabview bundle is not provisioned. Run tools\\pylabview\\provision.ps1, which " +
        "assembles about 32 MB of interpreter, trimmed standard library and Pillow into " +
        "tools\\pylabview\\runtime. It is deliberately not committed. Set " +
        PyLabview.DirectoryVariable + " to point at a bundle in another location.";

    // ---------------------------------------------------------------- status

    [McpServerTool(Name = "pylv_status", ReadOnly = true, Title = "pylabview bundle status")]
    [Description("""
        Whether the bundled pylabview is present and usable, where it is, and what it is made of -
        Python version and architecture, the pinned upstream commit, when it was provisioned, and
        whether the two name tables travelled with it.
        Start here: every other pylv_* tool is unusable without the bundle, and the bundle is
        OPTIONAL - it is about 32 MB and deliberately not committed, so a fresh checkout has none
        until tools\\pylabview\\provision.ps1 has run.
        Needs NO running LabVIEW and no Python installation. The bundle is isolated by a
        pythonNNN._pth file beside its python.exe, python.org's own embeddable mechanism, so it
        ignores PYTHONPATH, PYTHONHOME, the registry and every site-packages directory.
        """)]
    public Task<string> StatusAsync(CancellationToken ct = default) =>
        Rpc.GuardAsync(() =>
        {
            var bundle = PyLabview.Locate();
            if (bundle is null)
                return Task.FromResult(Json.Error("notProvisioned", NotProvisioned, new
                {
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
            }.ToJsonString(Indented));
        });

    // ---------------------------------------------------------------- extract

    [McpServerTool(Name = "pylv_extract", ReadOnly = true, Title = "VI to XML with pylabview")]
    [Description("""
        Extract a .vi, .ctl or .llb into a directory of XML plus binary sidecars, and annotate the
        numbers with names. Does NOT modify the source file, and needs NO running LabVIEW.
        This reads what AIXML cannot: icons, panel images, layout, decorations, and the diagram of
        a VI whose constructs the AIXML generator refuses. Measured, 38 of 38 .vi and .ctl files
        round-tripped content-identical on LabVIEW 2026 Q3.
        A .lvclass, .lvlib or .lvproj is NOT an RSRC container in LabVIEW 2026 - those are already
        plain XML on disk, so read them with an XML parser instead. This tool reports that rather
        than failing obscurely.
        annotate (default true) writes primitive and terminal names in as XML comments -
        `<primResID>1050</primResID><!-- Add -->`. A trailing `?` marks a name resting on fewer
        than three sightings in the corpus; 218 of the 574 terminal roles rest on one. The comments
        are inert: a rebuild from an annotated bundle is byte-identical to one from an
        un-annotated bundle, measured.
        Expect warnings on stderr about blocks that could not be parsed - VITS alone falls back on
        nearly every file. Those blocks are copied through verbatim, which is why the round trip is
        lossless, so they are reported separately from an actual failure.
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
        MUTATING: rebuild a .vi from an XML bundle produced by pylv_extract, writing to viPath and
        overwriting whatever is there. Needs NO running LabVIEW.
        THE PATH MUST NOT BE LOADED IN LABVIEW, and the failure is silent rather than loud.
        Measured: LabVIEW does not lock a .vi file, so this write always succeeds - but LabVIEW
        keeps serving its in-memory copy, so a verification run afterwards confirms the VI you
        REPLACED, not the one you built. Call lvai_close_vi on the target first. That tool needs
        the VI to be a member of a project that is active in the IDE, which is why generating into
        a project matters; for a loose VI, rebuild to a fresh path instead.
        Annotation comments left by pylv_extract are ignored on the way back in, so an annotated
        bundle rebuilds byte-identically to an un-annotated one.
        Byte equality with the ORIGINAL is the wrong acceptance test: LabVIEW compresses block
        sections at zlib level 9 and pylabview at level 6, which shifts every offset after the
        first section. Compare decompressed block content, or just load the result.
        Verify afterwards - this tool cannot. Read ExecState through VI Server (1 is eIdle, 0 is
        eBad), then AIXML-export the result and confirm the change you intended is present.
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
        Decide, by measurement rather than guess, whether an existing VI can be edited through
        AIXML or has to go through pylabview - and say why. Call this BEFORE planning any edit.
        Two checks, because one is not sound:
        A. export the VI and hand the export straight back to ValidateAIXML without changing a
           byte. errorCode 1 means AIXML cannot rebuild this VI at all.
        B. scan the export for node families NI publishes as unsupported - Event Structure,
           Timed Loop. These pass check A with errorCode 0 and then come back GUTTED: measured on
           NI's own VI, every CaseFrame gone and one frame left labelled '[0] Timeout'. Check A
           alone would route them to AIXML and destroy them.
        Context for the answer: over 1687 of NI's VIs, 1679 export but only 627 regenerate. For
        editing existing code AIXML is the minority route, and 737 of the 1052 failures are a Call
        to the VI's own subVIs - which is a capability AIXML lacks entirely, not a bug.
        This tool decides; it does not switch. A pylabview edit is a surgical change to an object
        heap, so it cannot be synthesised from a high-level request - you still have to author it.
        Needs a running LabVIEW for check A.
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
