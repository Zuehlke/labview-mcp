using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>One reading of a VI's executability.</summary>
/// <param name="State">
/// <c>Execution:State</c>: 0 eBad, 1 eIdle, 2 eRunTopLevel, 3 eRunning - and -1 for "the reference
/// never opened", which is NOT the same thing and must never be reported as broken.
/// </param>
internal sealed record ExecStateReading(int State, string LinkerErrors, int Code, string Source)
{
    /// <summary>The VI is not executable. Only 0 means this.</summary>
    internal bool Broken => State == 0;

    /// <summary>The reference could not be opened, so nothing was learned about the VI.</summary>
    internal bool CouldNotOpen => State < 0;
}

/// <summary>
/// Is a VI EXECUTABLE? Nothing else in the generate-or-retarget chain answers that.
///
/// WHY THIS EXISTS. Measured 2026-09-09 over a ten-VI build: after a <c>pylv_apply</c> retarget the
/// caller was Error 1003, not executable - and <c>pylv_apply</c> answered <c>ok: true</c>, its
/// verify step listed all eight expected callTargets, the AIXML export showed every terminal wired,
/// <c>lvai_check_aixml</c> found nothing and <c>lvai_validate_aixml</c> answered errorCode 0 on the
/// source. The only way to notice was to RUN the VI, and a top-level UI loop never ends on its own,
/// so noticing cost 2400 s and nine bisection probes. <c>pylv_rebuild</c> has always NAMED the gap
/// in its own <c>gatesNotChecked</c> list - "ExecState was not read - do it through VI Server; 0
/// means eBad" - so the hint was there and the reading was not.
///
/// THE -1 CASE IS THE PART WORTH KEEPING. Pointing the helper at a .xml by mistake gave error 1059
/// and left <c>Execution:State</c> unread at 0 - which this contract calls eBad. A sensor whose only
/// job is broken-or-not must not answer "broken" when it means "could not look", so a failed open
/// answers -1 and callers are expected to branch on it.
/// </summary>
[McpServerToolType]
internal sealed class ExecStateTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_exec_state.xml";

    [McpServerTool(Name = "lvai_exec_state", ReadOnly = true, OpenWorld = true,
                   Title = "Is this VI executable?")]
    [Description("""
        READ-ONLY: reads a VI's {LV.VI} Execution:State and VI Linker Errors through VI Server and
        says whether it is executable. This is the only thing in the toolset that answers that
        question without RUNNING the VI - which matters because a top-level UI loop never ends on
        its own, so "just run it" is not available for the VI that most needs checking.
        WHAT THE NUMBER MEANS: 0 eBad - BROKEN, 1 eIdle - fine and not running, 2 eRunTopLevel,
        3 eRunning. And -1 means the REFERENCE NEVER OPENED, which is not a verdict about the VI at
        all: read `code` and `source` for why. Those two are kept apart on purpose, because a failed
        open leaves the state property unread at 0 and would otherwise read as eBad.
        WHEN TO REACH FOR IT: after any pylv_apply retarget, after generating a VI that calls
        project-local code, and whenever a chain of green answers still leaves you unsure. Measured:
        pylv_apply reported ok with every callTarget correct over a caller that was Error 1003.
        Opening and closing a VI REFERENCE does not burn the path for a later regeneration - only
        lvai_open_file does that - and the helper opens WITHOUT an application instance, so this
        also answers while pylv_apply has the project closed.
        """)]
    public async Task<string> ExecStateAsync(
        [Description("Absolute path to the .vi to interrogate")] string viPath,
        [Description("The helper's AIXML source. Defaults to lvai_exec_state.xml in scriptsDirectory.")]
        string? helperAixmlPath = null,
        [Description("Where to keep the generated helper VI. Defaults to a per-user temp directory.")]
        string? helperViPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(viPath);
        if (!File.Exists(full))
            return Json.Error("viNotFound", $"No file at '{full}'.", new { viPath = full });

        var reading = await ReadAsync(full, helperAixmlPath, helperViPath, regenerateHelper,
            timeoutSeconds, ct: ct);

        if (reading is null)
            return Json.Error("helperUnavailable",
                "The executability helper could not be generated or run, so nothing is known " +
                "about this VI. That is not a clean bill of health.",
                new { viPath = full });

        return Json.Object(new
        {
            ok = true,
            viPath = full,
            execState = reading.State,
            broken = reading.Broken,
            couldNotOpen = reading.CouldNotOpen,
            meaning = Meaning(reading.State),
            linkerErrors = reading.LinkerErrors,
            code = reading.Code,
            source = reading.Source,
            note = reading.CouldNotOpen
                ? "The reference never opened, so this says NOTHING about the VI - read code and " +
                  "source. It is reported as -1 rather than 0 precisely so it cannot be mistaken " +
                  "for eBad."
                : reading.Broken
                    ? "eBad: the VI is NOT executable. A retarget that breaks the connector pane " +
                      "contract shows up exactly here and nowhere else - callTargets and a clean " +
                      "AIXML export are both green in that state."
                    : "Executable. This says nothing about whether it does the right thing; it " +
                      "says LabVIEW can run it.",
        });
    }

    /// <summary>Human-readable name for an Execution:State value.</summary>
    internal static string Meaning(int state) => state switch
    {
        < 0 => "the reference never opened - not a verdict about the VI",
        0 => "eBad - not executable",
        1 => "eIdle - executable, not running",
        2 => "eRunTopLevel - running as a top-level VI",
        3 => "eRunning - running as a subVI",
        _ => $"unknown state {state}",
    };

    /// <summary>
    /// Reads one VI's executability, or null when the helper itself could not be made to run.
    /// Null is deliberately distinct from a reading of 0: "no measurement" is not "broken".
    /// </summary>
    internal async Task<ExecStateReading?> ReadAsync(
        string viPath, string? helperAixmlPath, string? helperViPath, bool regenerateHelper,
        int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperAixmlFileName)
                : null);
            if (aixml is null || !File.Exists(aixml)) return null;

            var helperVi = helperViPath ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_exec_state.vi");
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            if ((regenerateHelper || !File.Exists(helperVi)) &&
                await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct) is not null)
                return null;

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi,
                new JsonObject { ["vi path"] = Path.GetFullPath(viPath) }.ToJsonString(),
                includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct: ct);

            if (JsonNode.Parse(answer) is not JsonObject payload ||
                payload["values"] is not JsonObject values)
                return null;

            var state = Value(values, "exec state");
            if (state is null || !int.TryParse(state, out var parsed)) return null;

            return new ExecStateReading(
                parsed,
                Value(values, "linker errors xml") ?? "",
                int.TryParse(Value(values, "code"), out var code) ? code : 0,
                Value(values, "source") ?? "");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? Value(JsonObject values, string name) =>
        values[name] is JsonObject entry ? entry["value"]?.GetValue<string>() : null;

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
            new { helperViPath = helperVi, errorCode = generation.ErrorCode });
    }
}
