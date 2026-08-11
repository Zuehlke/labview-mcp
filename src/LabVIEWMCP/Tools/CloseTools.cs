using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Releasing a VI from LabVIEW's memory, so its path can be generated over again.
///
/// WHY THIS IS A TOOL. `ConvertAIXMLToVI` refuses to overwrite a path LabVIEW has loaded -
/// `Error 1357`, "a LabVIEW file from that path already exists in memory" - and `lvai_open_file`
/// alone is enough to cause it. Every iteration on a generated VI therefore ends in a VI that
/// cannot be regenerated, which made "look at it, then change it" impossible without restarting
/// LabVIEW. There is no RPC for this: the whole lvai surface has nothing named close, quit,
/// release or unload, and the VI Server catalogue has no unload method in its 3 078 entries
/// either. So this composes the VI Server route, exactly as <see cref="IconTools"/> does.
///
/// THE RECIPE, and it is not the obvious one. `FP.Close` and `FP.Set Close If Lonely` sit in the
/// catalogue and read like the answer; measured, they report `errorCode 0` and do nothing,
/// because a generated helper runs in the ADDON's application instance, where the VI's windows do
/// not exist. The way out of that instance is the active project:
///
///   {LV.Application} -> Project:Active Project -> {LV.Project} -> Application
///
/// and then, inside THAT instance, writing `Front Panel Window:State` = `Closed`. A discriminating
/// A/B settled which half does the work: the same chain WITHOUT the State write leaves the
/// regeneration failing with 1357, so reaching the right instance is not what releases the VI -
/// closing the panel while inside it is.
///
/// TWO PRECONDITIONS, both measured, and both reported as hints rather than left to be discovered.
/// A project must be ACTIVE in the IDE, or `Project:Active Project` answers `Error 1055`. And the
/// VI must be a MEMBER of that project, opened through it: a VI opened loose is loaded where the
/// project's application cannot see its panel, and the write fails naming
/// `Front Panel Window:State`. That is why the repository's rule is to generate every VI into a
/// project in the first place - retrofitting one afterwards is too late.
///
/// Verified end to end before this tool existed: open the VI -> regenerate -> `1357`; run this ->
/// regenerate -> `errorCode 0`. The measurements are in docs/vi-server-reference.md, "Unloading a
/// VI so its path can be regenerated".
/// </summary>
[McpServerToolType]
internal sealed class CloseTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_close_vi.xml";

    [McpServerTool(Name = "lvai_close_vi", Destructive = true, OpenWorld = true,
                   Title = "Close a VI in the IDE so its path can be regenerated")]
    [Description("""
        MUTATING (IDE state): closes a VI's front panel INSIDE the IDE's application instance,
        which releases the VI from memory. Call this when lvai_convert_aixml_to_vi answers
        Error 1357, "a LabVIEW file from that path already exists in memory" - opening a VI with
        lvai_open_file is enough to cause that, so it is the normal state after looking at
        generated code.
        There is NO RPC for this: nothing in the lvai surface closes anything, and the VI Server
        catalogue has no unload method, so this composes the VI Server route - generate the helper
        from scripts\lvai_close_vi.xml (once, then reused) and run it.
        TWO PRECONDITIONS, both measured. A project must be ACTIVE in the IDE, or the chain fails
        with Error 1055. And the VI must be a MEMBER of that project, opened through it - a VI
        opened loose is loaded where the project's application cannot see its panel, and the write
        fails naming Front Panel Window:State. Generate VIs into a project and this holds by
        construction.
        Do NOT reach for FP.Close or FP.Set Close If Lonely instead: measured, they report no error
        and do nothing, because a generated helper runs in the ADDON's instance where the VI's
        windows do not exist.
        `closed` says the chain raised no error. The decisive proof is the regeneration itself,
        which this tool deliberately does not perform.
        """)]
    public async Task<string> CloseViAsync(
        [Description(@"Absolute path to the .vi to release from memory")] string viPath,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory, because
            the scripts folder next to the exe may be read-only. Generated once and reused; pass
            regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvai_close_vi.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath());
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            // Through lvai_run_vi_and_read_values rather than RunVIAsTopLevel: the helper reports
            // an error CLUSTER, and the plain call returns non-string outputs empty with
            // errorCode 91 after the VI has run. IconTools works around that with a read-back
            // file; here the values themselves are the answer, so the reading runner is the
            // honest route and needs no second artifact on disk.
            var inputs = new JsonObject { ["VI Path"] = Path.GetFullPath(viPath) }.ToJsonString();
            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs, includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct);

            return Describe(answer, viPath, helperVi, aixml, helperGenerated);
        });

    /// <summary>
    /// Turn the runner's payload into this tool's answer: did the chain raise an error, and if so
    /// what does it most likely mean. Separated from the RPC work so the two failures that have
    /// their own advice are unit-testable without LabVIEW.
    /// </summary>
    internal static string Describe(
        string runnerAnswer, string viPath, string helperVi, string aixml, bool helperGenerated)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(runnerAnswer); }
        catch (JsonException) { root = null; }

        // A guard failure never reached LabVIEW; pass it through rather than dressing it up as a
        // close that did not happen.
        if (root is not JsonObject payload ||
            (payload.TryGetPropertyValue("ok", out var ok) && ok?.GetValue<bool>() == false))
            return runnerAnswer;

        var values = payload["values"] as JsonObject;
        var status = Value(values, "status");
        var code = Value(values, "code");
        var source = Value(values, "source") ?? "";

        // The helper's own error cluster decides, not the runner's errorCode - that one belongs to
        // the runner and is 0 whenever the target merely ran.
        var raised = status is not null && status != "0";
        var closed = status is not null && !raised;

        var result = new JsonObject
        {
            ["closed"] = closed,
            ["viPath"] = Path.GetFullPath(viPath),
            ["helperViPath"] = helperVi,
            ["helperAixmlPath"] = Path.GetFullPath(aixml),
            ["helperGenerated"] = helperGenerated,
            ["errorCode"] = int.TryParse(code, out var parsed) ? parsed : 0,
            ["errorSource"] = source,
        };

        if (Hint(code, source) is { } hint) result["hint"] = hint;

        result["note"] = closed
            ? "The chain raised no error, so the panel was closed inside the IDE's instance and " +
              "the VI should now be regenerable. The decisive proof is the regeneration itself, " +
              "which this tool does not perform."
            : status is null
                ? "The helper returned no status, so nothing can be concluded about the VI. " +
                  "Check that the helper VI generated correctly."
                : "The chain raised an error, so the VI was almost certainly NOT released and " +
                  "lvai_convert_aixml_to_vi will still answer Error 1357.";

        return Json.Document(result);
    }

    /// <summary>
    /// What a failing chain most likely means. Both cases are preconditions rather than faults,
    /// and both were measured - see the class remarks.
    /// </summary>
    internal static string? Hint(string? code, string source) =>
        code == "1055"
            ? "Error 1055 is 'Project:Active Project' finding no ACTIVE project in the IDE. Open " +
              "the VI's .lvproj and make it the active project, then try again. Note that " +
              "lvai_open_file was measured to make a project active on one occasion and not on " +
              "another, so treat 'active' as the user's IDE state."
        : source.Contains("Front Panel Window", StringComparison.OrdinalIgnoreCase)
            ? "The write to 'Front Panel Window:State' failed, which is what happens when the VI " +
              "is not a MEMBER of the active project. A VI opened loose is loaded where the " +
              "project's application cannot see its panel. Add it to the project and open it " +
              "through the project - a VI already loaded in the wrong place cannot be rescued, so " +
              "this is a rule about generation rather than repair."
        : null;

    /// <summary>One indicator's plain value out of the runner's `values` map, or null.</summary>
    private static string? Value(JsonObject? values, string name) =>
        values?[name] is JsonObject entry ? entry["value"]?.GetValue<string>() : null;

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload - the same two failures IconTools documents, and Error 1051 in particular is
    /// unrecoverable without changing the target name.
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

        if (generation.ErrorCode == 0 && File.Exists(helperVi)) return null;

        return Json.Error("helperGenerationFailed",
            $"Could not generate the helper VI at '{helperVi}': {generation.ErrorMessage}",
            new
            {
                helperViPath = helperVi,
                errorCode = generation.ErrorCode,
                viExistsNow = File.Exists(helperVi),
                hint = generation.ErrorCode switch
                {
                    1051 => "Error 1051 means a VI of that name is already in LabVIEW's memory - " +
                            "and a failed generation leaves the name occupied for the rest of the " +
                            "session. Pass a different helperViPath, or restart LabVIEW.",
                    7 => "Error 7 is LabVIEW refusing to save into " +
                         $"'{Path.GetDirectoryName(helperVi)}'. The directory does exist - this " +
                         "tool creates it - so the location itself is being refused; that has been " +
                         "measured under %LOCALAPPDATA%. Pass helperViPath somewhere else, " +
                         "somewhere under %TEMP% for instance.",
                    _ => null,
                },
            });
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP for the reason IconTools measured: LabVIEW's Save:Instrument fails with Error 7
    /// when saving a generated VI under %LOCALAPPDATA%, with the directory present and writable,
    /// while %TEMP% accepts it.
    /// </summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_close_vi.vi");
}
