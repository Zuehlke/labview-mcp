using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Rendering a VI's block diagram to PNG, for SEVERAL VIs in one call.
///
/// WHY IT EXISTS, and the number is the whole argument. Nothing programmatic can see whether a
/// diagram comment reads well - measured three times on 2026-09-08, the render was the only check
/// that caught a clipped caption, an obscured Case selector, and a comment pushed off the visible
/// diagram, all of which had passed validation, rebuild, export, link check and a run. So looking
/// is mandatory, and it was costing more than the work it verified: in one agent run that built
/// three small VIs, driving the print helper by hand took 7 <c>lvai_run_vi_and_read_values</c>
/// calls plus 3 <c>mkdir</c> calls - and that run spent 1000 s of wall clock against 120 s inside
/// its tools, so a round trip is worth about 9.8 s and those 10 calls were about 100 s of pure
/// latency.
///
/// TWO TRAPS ARE ENCODED HERE rather than left to be rediscovered. The image directory MUST EXIST
/// - LabVIEW answers <c>Error 118</c> and creates nothing - so this creates it. And the helper's
/// own return value is unusable, exactly as in <see cref="IconTools"/>: RunVIAsTopLevel cannot
/// marshal an int32 indicator back, so the verdict comes from the PNG files that appeared during
/// the call, never from <c>errorCode</c>.
///
/// The image FILE NAMES come from the HTML file's base name, not the VI's: LabVIEW writes
/// <c>&lt;base&gt;d.png</c> for the top-level diagram and <c>&lt;base&gt;d1..dN.png</c> one per Case
/// frame. That is why each VI gets its own HTML base here - a shared one would have the VIs
/// overwrite each other's pictures.
/// </summary>
[McpServerToolType]
internal sealed class RenderTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvdoc_print.xml";

    [McpServerTool(Name = "lvai_render_diagrams", Destructive = false, OpenWorld = true,
                   Title = "Render VI block diagrams to PNG")]
    [Description("""
        Renders the BLOCK DIAGRAM of one or more VIs to PNG through LabVIEW's own
        Print.VI To HTML, and reports the image files per VI so you know which ones to read.
        SEVERAL VIs IN ONE CALL - one absolute path per line in viPaths. LabVIEW serialises the
        work either way, so what this saves is round trips, which is the only thing that costs
        real time: measured 2026-09-08, one agent run spent 1000 s of wall clock against 120 s
        inside its tools, so a call is worth about 9.8 s and doing three VIs by hand cost ten.
        USE IT AFTER PLACING DIAGRAM COMMENTS, and LOOK at what comes back. Nothing else in this
        interface can see a comment that is clipped, sitting on a Case selector, or off the
        visible diagram - all three were measured passing validation, rebuild, export and a run
        on the same day, and only the picture disagreed.
        THE IMAGE DIRECTORY IS CREATED FOR YOU. LabVIEW does not create it and answers Error 118
        instead, which reads like a rendering failure and is a missing folder.
        DO NOT judge this by errorCode: RunVIAsTopLevel cannot read the helper's indicators back,
        so a successful render can still report 91. The verdict is `rendered` per VI, taken from
        the PNG files that actually appeared during the call.
        Each VI gets `diagrams`: the top-level diagram first, then one entry per Case frame
        (LabVIEW names them <base>d.png and <base>d1..dN.png). A VI whose diagram has no Case
        structure yields exactly one.
        IT LOADS EACH VI INTO LABVIEW, which is the one side effect worth knowing: do any
        pylabview edit (pylv_apply) BEFORE rendering, not after, or the rebuild writes the file
        while LabVIEW keeps serving the copy it just loaded.
        """)]
    public async Task<string> RenderDiagramsAsync(
        [Description("""
            The VIs to render, ONE ABSOLUTE PATH PER LINE, plain text and not JSON. A single
            path is fine.
            """)]
        string viPaths,
        [Description("""
            Directory for the HTML and PNG output. CREATED IF MISSING, which is the point -
            LabVIEW's own writer does not create it and fails with Error 118. Defaults to a
            per-user temp directory.
            """)]
        string? outputDirectory = null,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user cache directory,
            because the scripts folder next to the exe may be read-only. Generated once and
            reused; pass regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvdoc_print.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds, per VI")]
        int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        var targets = viPaths
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (targets.Length == 0)
            return Json.Error("badArguments",
                "viPaths was empty. Pass one absolute .vi path per line.",
                new { viPaths });

        if (targets.FirstOrDefault(t => !File.Exists(t)) is { } missing)
            return Json.Error("viNotFound",
                $"No file at '{missing}'. Every path is checked before LabVIEW is asked to " +
                "render anything, so a typo costs a message rather than a half-finished set.",
                new { missing, requested = targets.Length });

        var aixml = helperAixmlPath ?? DefaultHelperAixmlPath()
            ?? throw new FileNotFoundException(
                "The helper's AIXML source could not be located: no scripts folder next to the " +
                "exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                $"explicitly, pointing at {HelperAixmlFileName}.");
        if (!File.Exists(aixml))
            throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

        var outputs = Path.GetFullPath(outputDirectory ?? DefaultOutputDirectory());
        Directory.CreateDirectory(outputs);

        var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath());
        if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperDirectory)
            Directory.CreateDirectory(helperDirectory);

        var helperGenerated = false;
        if (regenerateHelper || !File.Exists(helperVi))
        {
            if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                is { } generationFailure) return generationFailure;
            helperGenerated = true;
        }

        var results = new JsonArray();
        var renderedCount = 0;

        foreach (var target in targets)
        {
            var viPath = Path.GetFullPath(target);
            // The HTML base name decides the PNG names, so it has to be unique per VI and free
            // of characters LabVIEW strips. Spaces are the common case: "Clamp Array.vi" would
            // otherwise collide with anything else whose name differs only by them.
            var htmlBase = SafeBaseName(Path.GetFileNameWithoutExtension(viPath));
            var htmlPath = Path.Combine(outputs, htmlBase + ".html");

            var startedUtc = DateTime.UtcNow;

            var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
            request.Inputs["VI Path"] = viPath;
            request.Inputs["HTML File Path"] = htmlPath;
            request.Inputs["Image Directory"] = outputs;

            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            var diagrams = DiagramImages(outputs, htmlBase, startedUtc);
            var rendered = diagrams.Count > 0;
            if (rendered) renderedCount++;

            var entry = new JsonObject
            {
                ["viPath"] = viPath,
                ["rendered"] = rendered,
                ["htmlPath"] = File.Exists(htmlPath) ? htmlPath : null,
                ["diagrams"] = new JsonArray([.. diagrams.Select(d => JsonValue.Create(d))]),
                ["diagramCount"] = diagrams.Count,
                ["helperErrorCode"] = response.ErrorCode,
            };

            if (!rendered)
                entry["hint"] =
                    "No diagram PNG appeared during this call. LabVIEW answers Error 118 when the " +
                    "image directory does not exist - this tool creates it, so that is not the " +
                    "cause here; the likelier one is that the VI could not be loaded. Read " +
                    "helperErrorCode's message, and check the VI opens.";

            results.Add(entry);
        }

        return Json.Document(new JsonObject
        {
            // `ok` is every VI rendering, not the helper's errorCode - which is 91 on success.
            ["ok"] = renderedCount == targets.Length,
            ["requested"] = targets.Length,
            ["rendered"] = renderedCount,
            ["failed"] = targets.Length - renderedCount,
            ["outputDirectory"] = outputs,
            ["helperViPath"] = helperVi,
            ["helperAixmlPath"] = Path.GetFullPath(aixml),
            ["helperGenerated"] = helperGenerated,
            ["results"] = results,
            ["note"] =
                "The top-level diagram is the FIRST entry of each `diagrams` list; the rest are " +
                "Case frames. Read them - `rendered: true` says a picture exists, never that the " +
                "diagram looks right, and looking is the only check that sees a clipped or " +
                "obscured comment. errorCode 91 from the helper is the known RunVIAsTopLevel " +
                "read-back artefact and does NOT mean the render failed.",
        });
    }

    /// <summary>
    /// The diagram PNGs LabVIEW wrote for one HTML base, top-level first then one per Case frame.
    ///
    /// MATCHED BY NAME AND BY WRITE TIME, both deliberately. By name because the same directory
    /// holds every VI's pictures plus a control glyph per data type (cdbl.png, i1dbool.png and so
    /// on), and those are not diagrams. By time because a re-render into a directory that already
    /// holds an older set would otherwise report stale files as this call's output - which is the
    /// same class of mistake as trusting an in-memory copy over the file on disk.
    /// </summary>
    internal static List<string> DiagramImages(string directory, string htmlBase, DateTime startedUtc)
    {
        var found = new List<(int Frame, string Path)>();
        var cutoff = startedUtc.AddSeconds(-2);

        foreach (var file in Directory.EnumerateFiles(directory, htmlBase + "d*.png"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff) continue;

            // "<base>d.png" is the top level; "<base>d<N>.png" is Case frame N.
            var tail = Path.GetFileNameWithoutExtension(file)[(htmlBase.Length + 1)..];
            if (tail.Length == 0) found.Add((0, file));
            else if (int.TryParse(tail, out var frame)) found.Add((frame + 1, file));
        }

        return [.. found.OrderBy(f => f.Frame).Select(f => f.Path)];
    }

    /// <summary>
    /// A base name LabVIEW will not alter, so the PNG names are predictable. Anything outside
    /// letters and digits goes, which also collapses the space in "Clamp Array" the way LabVIEW
    /// itself does in its generated image names.
    /// </summary>
    internal static string SafeBaseName(string name)
    {
        var kept = new string([.. name.Where(char.IsLetterOrDigit)]);
        return kept.Length > 0 ? kept : "diagram";
    }

    /// <summary>
    /// Generate the helper VI from its AIXML. Validated first - the shipped file is known-good
    /// but it is a plain file a user can edit, and validation is the cheap failure path.
    /// </summary>
    private async Task<string?> GenerateHelperAsync(
        string aixml, string helperVi, int timeoutSeconds, CancellationToken ct)
    {
        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (validation.ErrorCode != 0)
            return Json.Error("helperAixmlInvalid",
                $"The helper AIXML at '{aixml}' does not validate: {validation.ErrorMessage}",
                new { aiXmlPath = Path.GetFullPath(aixml), errorCode = validation.ErrorCode });

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = helperVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (generation.ErrorCode != 0)
            return Json.Error("helperGenerationFailed",
                $"The helper VI could not be generated: {generation.ErrorMessage}",
                new { helperViPath = helperVi, errorCode = generation.ErrorCode });

        return null;
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvdoc_print.vi");

    private static string DefaultOutputDirectory() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "renders");
}
