using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Running a VI and actually READING ITS RESULT - the second COMPOSED tool, after
/// lvai_set_vi_icon, and for the same kind of reason: the plain RPC cannot do it.
///
/// RunVIAsTopLevel marshals only strings, so a boolean, cluster, array or waveform indicator
/// comes back empty with errorCode 91 AFTER the VI has already run correctly. Every generated
/// VI with a non-string output therefore verifies to an empty answer, and the repository's own
/// rule - "never report success from an empty answer" - leaves the caller building a VI Server
/// harness by hand. This is that harness, shipped: scripts\lvai_run_and_read.xml sets the
/// inputs, runs the target and flattens all its values to XML, so everything returns through a
/// single STRING indicator that RunVIAsTopLevel can carry.
///
/// Measured, and the reason set-run-read must be ONE call: a RunVIAsTopLevel followed by a
/// separate read of the same VI returns the target's DEFAULTS, not the values of that run. The
/// two calls do not share the VI's data space. Details in docs/vi-server-reference.md.
/// </summary>
[McpServerToolType]
internal sealed class RunTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_run_and_read.xml";

    [McpServerTool(Name = "lvai_run_vi_and_read_values", Destructive = true, OpenWorld = true,
                   Title = "Run a VI and read every control and indicator value back")]
    [Description("""
        MUTATING: EXECUTES the VI, with whatever side effects it has.
        Use this instead of lvai_run_vi_as_top_level whenever the VI has ANY output that is not
        a string - boolean, numeric, cluster, array, waveform. That tool returns those empty
        with errorCode 91 after the VI has run; this one returns their real values, because the
        helper reads them through VI Server and flattens them to XML before they cross back.
        Values are returned per control name with a type and, for scalars, a plain text value;
        compound values keep their flattened XML.
        The limit is on the way IN, not out: inputs still cross as STRINGS, so only string
        controls can be set - take numbers and paths in as strings and convert them on the
        diagram. A newline in a name or value is rejected, because the helper's wire format
        separates them by newlines.
        Reading is done through a VI REFERENCE, which is released afterwards, so this does not
        burn the target's path for a later lvai_convert_aixml_to_vi.
        """)]
    public async Task<string> RunViAndReadValuesAsync(
        [Description(@"Absolute path to the .vi to run")] string viPath,
        [Description("""
            Control values as a JSON object, e.g. {"file name":"C:\\data\\in.csv"}. Keys are
            control labels. String controls only. Omit for a VI that needs no inputs.
            """)]
        string? inputsJson = null,
        [Description("""
            Also return the helper's raw flattened XML under valuesXml. Off by default because
            it repeats everything in values; it is returned regardless when parsing yields
            nothing, so a parse failure never costs you the data.
            """)]
        bool includeRawXml = false,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user cache directory,
            because the scripts folder next to the exe may be read-only. Generated once and
            reused; pass regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvai_run_and_read.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds - raise it for long-running VIs")]
        int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            var inputs = Rpc.ParseStringMap(inputsJson, nameof(inputsJson)).ToList();
            if (Offending(inputs) is { } offender)
                return Json.Error("inputContainsNewline",
                    $"The control name or value for '{offender}' contains a line break. The " +
                    "helper pairs names with values by line, so a newline in either would " +
                    "silently shift every later pair onto the wrong control.",
                    new { controlName = offender });

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    $"The helper's AIXML source could not be located: no scripts folder next to " +
                    $"the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
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
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            // AN EMPTY VALUE MISALIGNS EVERY INPUT AFTER IT, so it is refused rather than sent.
            // The two lists below are joined with newlines and paired BY POSITION inside the
            // helper, where Spreadsheet String To Array does not yield an element for an empty
            // line - so one empty value shortens the Values list and every later name receives
            // its neighbour's value. Measured 2026-08-27: an accessor call passing an empty
            // `virtual folder` in the middle of nine inputs left the two after it unset, the
            // helper kept their defaults, and a request for 2 fields silently built as many as
            // the clock allowed. It went unnoticed for as long as the empty value happened to be
            // LAST, which is exactly the kind of latent fault that surfaces on an unrelated
            // change.
            if (inputs.FirstOrDefault(i => i.Value.Length == 0) is { Key: { Length: > 0 } empty })
                return Json.Error("badArguments",
                    $"Input '{empty}' has an empty value. Names and values are paired by " +
                    "POSITION, and an empty value does not survive the helper's split - it would " +
                    "shift every input after it onto the wrong control. Omit the input instead: a " +
                    "control that is not set keeps its own default.",
                    new { inputName = empty, inputCount = inputs.Count });

            var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
            request.Inputs["VI Path"] = Path.GetFullPath(viPath);
            request.Inputs["Input Names"] = string.Join("\n", inputs.Select(i => i.Key));
            request.Inputs["Input Values"] = string.Join("\n", inputs.Select(i => i.Value));

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            response.Outputs.TryGetValue("values xml", out var valuesXml);
            response.Outputs.TryGetValue("error xml", out var errorXml);

            var values = LvValuesXml.Parse(valuesXml);
            // Withheld only when it would be pure duplication. If nothing parsed, the raw text
            // is the entire result and hiding it would be the empty answer this tool exists
            // to prevent.
            var keepRaw = includeRawXml || values.Count == 0;

            var payload = Json.Node(response).AsObject();
            // The helper's two indicators are re-exposed below as `values`/`valuesXml` and
            // `helperErrorXml`, so leaving the protobuf map in place would ship the whole
            // flattened XML a second time - doubling the payload on exactly the big waveforms
            // this tool exists to return, and quietly breaking includeRawXml's promise.
            payload.Remove("outputs");

            payload["values"] = LvValuesXml.ToJson(values);
            payload["valueCount"] = JsonValue.Create(values.Count);
            payload["valuesXml"] = keepRaw ? JsonValue.Create(valuesXml) : null;
            payload["helperErrorXml"] = JsonValue.Create(errorXml);
            payload["helperViPath"] = JsonValue.Create(helperVi);
            payload["helperAixmlPath"] = JsonValue.Create(Path.GetFullPath(aixml));
            payload["helperGenerated"] = JsonValue.Create(helperGenerated);
            payload["inputsSent"] = JsonValue.Create(inputs.Count);
            payload["elapsedMs"] = JsonValue.Create(stopwatch.ElapsedMilliseconds);
            payload["note"] = JsonValue.Create(
                "errorCode here is the HELPER's. A target VI that itself reported an error " +
                "shows that in its own error out under values - read it there, not from " +
                "errorCode.");

            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>The first name or value carrying a line break, or null when all are clean.</summary>
    private static string? Offending(IEnumerable<KeyValuePair<string, string>> inputs) =>
        inputs.FirstOrDefault(i => Breaks(i.Key) || Breaks(i.Value)).Key;

    private static bool Breaks(string? s) =>
        s is not null && (s.Contains('\n') || s.Contains('\r'));

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload - Error 1051 in particular is unrecoverable without changing the target name.
    /// Same shape as IconTools; the two composed tools fail in the same two ways.
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
    /// Under TEMP, not %LOCALAPPDATA%: LabVIEW's Save:Instrument fails there with Error 7 even
    /// though the directory exists. Measured for lvai_set_vi_icon; see IconTools for the detail.
    /// </summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_run_and_read.vi");
}
