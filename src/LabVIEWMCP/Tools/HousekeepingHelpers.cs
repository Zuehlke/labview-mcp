// ============================================================================================
// HousekeepingHelpers.cs - the LabVIEW-side plumbing the class housekeeping tools share: build a
// helper VI from its scripts\ AIXML when it is missing or stale, open the project, run the helper
// through the typed run helper, and read its error clusters back.
// ============================================================================================
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;

namespace LabVIEWMcp.Tools;

/// <summary>
/// One helper run per question, the pattern lvai_add_to_library already uses: a small VI in
/// <c>scripts\</c>, generated once into <c>%TEMP%\LabVIEWMCP\helpers</c> and rebuilt only when its
/// AIXML is newer (<see cref="HelperCache"/>), driven through the TYPED run helper so a path, a
/// boolean and an array arrive as their own types and a line break inside a value survives.
///
/// INPUTS ARE NEVER SENT EMPTY. The run helper pairs names with values by position and an empty
/// value does not survive its split (RunTools, measured 2026-08-27), so every caller here sends a
/// value for every control it relies on and refuses an empty one before LabVIEW is reached.
/// </summary>
internal sealed class HousekeepingHelpers(LvaiConnection connection)
{
    /// <summary>A helper VI ready to run, or the error answer that stopped it.</summary>
    internal async Task<(string? HelperVi, bool Generated, string? Failure)> EnsureAsync(
        string helperName, int timeoutSeconds, CancellationToken ct)
    {
        if (StatusTools.ScriptsDirectory() is not { } scripts)
            return (null, false, Json.Error("scriptsMissing",
                "No scripts folder next to the exe (lvai_status reports it as scriptsDirectory), " +
                $"so {helperName}.xml cannot be found."));

        var aixml = Path.Combine(scripts, helperName + ".xml");
        if (!File.Exists(aixml))
            return (null, false, Json.Error("scriptsMissing", $"No helper AIXML at '{aixml}'."));

        // ON pylabview THE HELPER IS COMPOSED, not built by NI's converter, and runs over ActiveX
        // (RunAsync below routes the same way) - so none of these tools needs NI's service there.
        if (DqmhTools.OnPyLabview)
        {
            var (composed, problem) = await ComposedHelpers.EnsureAsync(helperName + ".xml", timeoutSeconds, ct);
            return composed is not null
                ? (composed, false, null)
                : (null, false, Json.Error("helperNotComposable",
                    $"The helper {helperName}.xml could not be composed for the pylabview backend: {problem}",
                    new { aiXmlPath = aixml }));
        }

        var helperVi = Path.Combine(DqmhTools.HelperDirectory(), helperName + ".vi");
        if (!HelperCache.NeedsRebuild(aixml, helperVi)) return (helperVi, false, null);
        Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);

        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return (null, false, Json.Error("helperAixmlInvalid",
                $"The helper AIXML at '{aixml}' does not validate: {validation.ErrorMessage}",
                new { aiXmlPath = aixml, errorCode = validation.ErrorCode }));

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = helperVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        return generation.ErrorCode == 0 && File.Exists(helperVi)
            ? (helperVi, true, null)
            : (null, false, Json.Error("helperGenerationFailed",
                $"Could not generate the helper VI at '{helperVi}': {generation.ErrorMessage}",
                new { helperViPath = helperVi, errorCode = generation.ErrorCode }));
    }

    /// <summary>
    /// Run a helper. Null values mean the run helper itself did not answer; HelperError is the
    /// run helper's OWN error - the target's error clusters are among the values.
    /// </summary>
    internal async Task<(IReadOnlyList<LvValuesXml.Value>? Values, string? HelperError)> RunAsync(
        string helperVi, Dictionary<string, string> inputs, int timeoutSeconds, CancellationToken ct)
    {
        if (StatusTools.ScriptsDirectory() is not { } scripts) return (null, null);
        return await new DqmhTools(connection).RunViDetailedAsync(
            scripts, helperVi, inputs, timeoutSeconds, ct);
    }

    /// <summary>
    /// Open the project and confirm it became ACTIVE, which every helper here needs: they reach
    /// LabVIEW through <c>Project:Active Project</c> and answer Error 1055 without one.
    /// </summary>
    internal async Task<(JsonNode? Answer, string? Failure)> OpenProjectAsync(
        string projectPath, int timeoutSeconds, CancellationToken ct)
    {
        var full = Path.GetFullPath(projectPath);
        if (!File.Exists(full))
            return (null, Json.Error("projectMissing", $"No .lvproj at '{full}'.", new { projectPath = full }));

        var opened = await new ActionTools(connection).OpenFileAsync(
            viPath: null, viName: null, projectPath: full, projectName: Path.GetFileName(full),
            checkActive: true, timeoutSeconds, ct: ct);
        var answer = Parse(opened);
        if ((answer as JsonObject)?["projectBecameActive"]?.GetValue<bool>() is false)
            return (answer, Json.Error("projectDidNotBecomeActive",
                "The project did not become active, and these helpers reach LabVIEW through " +
                "Project:Active Project - every one would answer Error 1055. The measured cause is " +
                "LabVIEW not having the foreground.", new { open = answer }));
        return (answer, null);
    }

    /// <summary>Open VIs through the project, so they are loaded in the project's application instance.</summary>
    internal async Task<JsonArray> OpenThroughProjectAsync(
        IEnumerable<string> viPaths, string projectPath, int timeoutSeconds, CancellationToken ct)
    {
        // THROUGH lvai_open_file, which follows the calling tool's backend: NI's OpenFile on grpc,
        // the composed lvai_open_vi_in_project over ActiveX on pylabview.
        var opened = new JsonArray();
        var actions = new ActionTools(connection);
        foreach (var vi in viPaths)
        {
            var answer = Parse(await actions.OpenFileAsync(viPath: vi, viName: Path.GetFileName(vi),
                projectPath: projectPath, projectName: Path.GetFileName(projectPath), checkActive: false,
                timeoutSeconds, ct: ct)) as JsonObject;
            var code = answer?["errorCode"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : -1;
            opened.Add(new JsonObject
            {
                ["viPath"] = vi,
                ["errorCode"] = code,
                ["errorMessage"] = answer?["errorMessage"]?.DeepClone() ?? answer?["error"]?.DeepClone(),
            });
        }
        return opened;
    }

    // ---------------------------------------------------------------- reading helper output

    internal static string? Scalar(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        DqmhTools.Scalar(values, name);

    internal static List<string> Strings(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        DqmhTools.Strings(values, name);

    internal static bool IsTrue(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        DqmhHeadless.IsTrue(Scalar(values, name));

    /// <summary>The code of an error-cluster indicator, or null when the helper did not report it.</summary>
    internal static int? Code(IReadOnlyList<LvValuesXml.Value> values, string indicator) =>
        Xml(values, indicator) is { } xml
        && Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>") is { Success: true } m
        && int.TryParse(m.Groups[1].Value, out var code) ? code : null;

    internal static string? Source(IReadOnlyList<LvValuesXml.Value> values, string indicator) =>
        Xml(values, indicator) is { } xml
        && Regex.Match(xml, "<Name>source</Name>\\s*<Val>(.*?)</Val>", RegexOptions.Singleline)
            is { Success: true } m && m.Groups[1].Value.Length > 0
            ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    private static string? Xml(IReadOnlyList<LvValuesXml.Value> values, string indicator) =>
        values.FirstOrDefault(v => string.Equals(v.Name, indicator, StringComparison.OrdinalIgnoreCase)).Xml;

    /// <summary>
    /// The first error-cluster indicator, in chain order, that carries a nonzero code - which is
    /// the STAGE that failed, because each helper wires one indicator per stage.
    /// </summary>
    internal static (string Stage, int Code, string? Source)? FirstFailure(
        IReadOnlyList<LvValuesXml.Value> values, params string[] stages)
    {
        foreach (var stage in stages)
            if (Code(values, stage) is { } code && code != 0)
                return (stage, code, Source(values, stage));
        return null;
    }

    internal static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(answer); }
    }

    /// <summary>The answer for a helper that failed at the PROJECT hop - 1055 named as what it is.</summary>
    internal static string ProjectHopFailure(int code, string? source, JsonArray steps) =>
        code == 1055
            ? Json.Error("noActiveProject",
                "No project is active in the IDE (Error 1055 at Project:Active Project). Pass " +
                "projectPath and this call opens it, or open it yourself with lvai_open_file; if an " +
                "open answered No Error, LabVIEW probably did not have the foreground.",
                new { errorCode = code, errorSource = source, steps })
            : Json.Error("projectHopFailed",
                $"Reaching the active project's application instance failed with Error {code}.",
                new { errorCode = code, errorSource = source, steps });
}
