using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Putting NI's assistant into "Discuss" mode on a VI or a project - from a third-party client,
/// which docs/aixml-reference.md spent a whole section establishing was impossible.
///
/// IT ISN'T, AND THE REASON THAT SECTION SAYS OTHERWISE IS THAT IT ASKS THE OTHER QUESTION. It
/// measures whether the `MonitorDiscussVI` stream can be INTERCEPTED - whether a third-party
/// watcher can receive the event the IDE's right-click emits - and the answer there is a firm no:
/// the monitors are single-subscriber streams and NI's own service always wins. Emitting the event
/// is a different door, and it is open.
///
/// TWO STAGES, WHICH IS NI'S OWN SHAPE. The IDE's callback
/// `LV AI Plugins.lvlib:lv_discuss_file_with_nigel.vi` holds a Flat Sequence:
///
///     [frame 1]  LV AI Core.lvlibp:Launch Nigel Chat.vi
///     [frame 2]  LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:InvokeDiscussVI.vi
///
/// THIS DRIVES THOSE TWO DIRECTLY, OVER VI SERVER, AND THAT IS THE WHOLE POINT. The callback is
/// reachable as an AIXML `Call` - it was the first route this tool shipped with - but it returns
/// NOTHING, discarding InvokeDiscussVI's `error code` and `error message` by NI's own design. So
/// the tool could only ever say "look at the chat window". Driving the two packed-library VIs
/// ourselves gets the verdict: they are unreachable as AIXML Call targets and reachable by
/// qualified name through `Open VI Reference`.
///
/// Measured 2026-09-10, and four things had to be established before this was more than an idea:
/// both VIs open by qualified name and report `Execution:State` 1; `Ctrl Val.Set` accepts all
/// three of InvokeDiscussVI's controls, including the PATH and the `uint32` ENUM - the enum
/// needed an AIXML-authored constant, because Ctrl Val.Set type-checks the variant against the
/// control; `Run VI` works on a packed-library member; and `Run VI` on `Launch Nigel Chat.vi`
/// does NOT block - 181 ms for the whole helper, against 667 ms through the callback, with the
/// chat appearing from a closed state.
///
/// WHAT IT STILL DOES NOT DO. It does not unlock `ApplyAIXMLToVI`. Firing Discuss on a VI and
/// applying AIXML to that same VI 30 s later still answered `Error 42` - which settles the one
/// variable that section left open. The gate is on the CALLER, not on the VI.
/// </summary>
[McpServerToolType]
internal sealed class DiscussTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_discuss.xml";

    /// <summary>
    /// The chat's own process, as MEASURED on this station - LabVIEW 2026 Q3 x86 with lvai 26.3.
    /// It is corroboration, not the verdict: `discussErrorCode` is the verdict now. The name is
    /// evidence rather than a contract, so its absence is reported as "not found".
    /// </summary>
    internal const string ChatProcessName = "LVNigelChat";

    [McpServerTool(Name = "lvai_discuss_file", Destructive = true, OpenWorld = true,
                   Title = "Put Nigel into Discuss mode on a VI or project")]
    [Description("""
        MUTATING: launches NI's Nigel chat application if it is not already running and attaches
        the given VI or project to it - the same two stages as the IDE's "Discuss with Nigel"
        command, driven over VI Server. The chat then shows the file with its diagram, description
        and terminal table.
        IT REPORTS A REAL VERDICT, which is why it drives NI's two packed-library VIs rather than
        the menu callback that wraps them: the callback returns nothing at all, discarding
        InvokeDiscussVI's own error code. `discussErrorCode` and `discussErrorMessage` are that
        discarded verdict, and `ok` is gated on them.
        This is the EMITTING side of the Discuss feature. Intercepting it is a different question
        and remains impossible: lvai_monitor_discuss_vi cannot receive the event, because the
        monitors are single-subscriber streams and NI's own service always wins.
        IT DOES NOT UNLOCK lvai_apply_aixml_to_vi. Measured: Discuss fired on a VI, Apply on that
        same VI 30 s later, still Error 42. The gate is on the caller, not on the VI.
        The chat also validates the file ITSELF, in its window - handed a path that is not a VI it
        shows "The file you selected is not a VI." - so the extension is checked here, before the
        call.
        """)]
    public async Task<string> DiscussFileAsync(
        [Description("Absolute path of the .vi or .lvproj to discuss")] string targetPath,
        [Description("""
            The name Nigel shows for the file. Defaults to the file name, which is what NI's own
            menu callback passes.
            """)]
        string? targetName = null,
        [Description("""
            'VI' or 'PROJECT', case-insensitive. Defaults to VI, which is what the IDE's own
            Discuss command hardcodes. It must agree with the file's extension.
            """)]
        string fileType = "VI",
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory.
            Generated once and reused - but regenerated automatically when the shipped AIXML is
            newer, so an upgrade cannot leave a stale helper behind.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvai_discuss.xml inside the folder lvai_status
            reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists and is current")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")]
        int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(targetPath))
                throw new FileNotFoundException($"No file at '{targetPath}'.", targetPath);

            if (NormaliseFileType(fileType) is not { } wanted)
                return Json.Error("badArguments",
                    $"fileType '{fileType}' is neither VI nor PROJECT. NI's enum has a third " +
                    "item, DISCUSS_FILE_TYPE_UNSPECIFIED, and it is not offered here because " +
                    "the IDE never sends it.",
                    new { fileType, accepted = new[] { "VI", "PROJECT" } });

            if (ExtensionComplaint(targetPath, wanted) is { } complaint)
                return Json.Error("targetDoesNotMatchFileType", complaint,
                    new
                    {
                        targetPath = Path.GetFullPath(targetPath),
                        extension = Path.GetExtension(targetPath),
                        fileType = wanted,
                        note = "Checked here because the chat makes this check in its own window, " +
                               "where no caller can read it - a red banner reading 'The file you " +
                               "selected is not a VI.'",
                    });

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

            var stale = IsStale(aixml, helperVi);
            var helperGenerated = false;
            if (regenerateHelper || stale || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            var chatWasRunning = ChatIsRunning();

            var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
            request.Inputs["Target Path"] = Path.GetFullPath(targetPath);
            request.Inputs["Target Name"] = Name(targetName, targetPath);
            request.Inputs["File Type"] = wanted;

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            response.Outputs.TryGetValue("File Type Used", out var fileTypeUsed);
            response.Outputs.TryGetValue("Launch Error", out var launchErrorXml);
            response.Outputs.TryGetValue("Invoke Values", out var invokeValuesXml);
            response.Outputs.TryGetValue("Invoke Error", out var invokeErrorXml);

            // InvokeDiscussVI's OWN two indicators, which NI's callback throws away. Ctrl Val.Get
            // All returns indicators only - measured - so this array is exactly those two.
            var invoked = LvValuesXml.Parse(invokeValuesXml);
            var discussCode = Scalar(invoked, "error code") is { } c && int.TryParse(c, out var dc)
                ? dc : (int?)null;
            var discussMessage = Scalar(invoked, "error message");

            var launch = ErrorCluster(launchErrorXml);
            var chain = ErrorCluster(invokeErrorXml);

            var chatIsRunning = ChatIsRunning();
            var ok = response.ErrorCode == 0
                     && discussCode == 0
                     && chain.Code == 0;

            var payload = Json.Node(response).AsObject();
            payload.Remove("outputs");

            payload["ok"] = JsonValue.Create(ok);
            payload["targetPath"] = JsonValue.Create(Path.GetFullPath(targetPath));
            payload["targetName"] = JsonValue.Create(Name(targetName, targetPath));
            payload["fileTypeRequested"] = JsonValue.Create(wanted);
            payload["fileTypeUsed"] = JsonValue.Create(fileTypeUsed);
            payload["discussErrorCode"] = discussCode is { } dcode
                ? JsonValue.Create(dcode) : null;
            payload["discussErrorMessage"] = JsonValue.Create(discussMessage);
            payload["launchErrorCode"] = launch.Code is { } lcode ? JsonValue.Create(lcode) : null;
            payload["launchErrorSource"] = JsonValue.Create(launch.Source);
            payload["helperChainErrorCode"] = chain.Code is { } ccode
                ? JsonValue.Create(ccode) : null;
            payload["helperChainErrorSource"] = JsonValue.Create(chain.Source);
            payload["chatProcessName"] = JsonValue.Create(ChatProcessName);
            payload["chatWasRunning"] = JsonValue.Create(chatWasRunning);
            payload["chatIsRunning"] = JsonValue.Create(chatIsRunning);
            payload["chatStartedByThisCall"] = JsonValue.Create(!chatWasRunning && chatIsRunning);
            payload["helperViPath"] = JsonValue.Create(helperVi);
            payload["helperAixmlPath"] = JsonValue.Create(Path.GetFullPath(aixml));
            payload["helperGenerated"] = JsonValue.Create(helperGenerated);
            payload["helperWasStale"] = JsonValue.Create(stale);
            payload["elapsedMs"] = JsonValue.Create(stopwatch.ElapsedMilliseconds);
            payload["note"] = JsonValue.Create(
                Verdict(ok, discussCode, chatWasRunning, chatIsRunning));

            // The raw flattening is withheld only when everything parsed AND everything was
            // clean. A missing code or a non-zero one is exactly when the caller needs the text.
            if (!ok || discussCode is null || launch.Code is null || chain.Code is null)
            {
                payload["launchErrorXml"] = JsonValue.Create(launchErrorXml);
                payload["invokeValuesXml"] = JsonValue.Create(invokeValuesXml);
                payload["invokeErrorXml"] = JsonValue.Create(invokeErrorXml);
            }

            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>
    /// What the answer is worth. Unlike the first version of this tool, the primary evidence is
    /// InvokeDiscussVI's OWN error code; the chat process only corroborates, and when the chat
    /// was already up it corroborates nothing.
    /// </summary>
    internal static string Verdict(bool ok, int? discussCode, bool chatWasRunning, bool chatIsRunning)
    {
        if (discussCode is null)
            return "InvokeDiscussVI's error code could not be read back out of the helper's " +
                   "flattened XML, which is a contract break rather than a failure of the " +
                   "request - the raw text is returned so you can see what arrived.";

        if (discussCode != 0)
            return $"InvokeDiscussVI itself reported error {discussCode}. NI's own menu callback " +
                   "discards this code, so this is a failure the IDE would have shown you as " +
                   "nothing at all.";

        if (!ok)
            return "InvokeDiscussVI reported no error, but a stage around it did - read " +
                   "helperChainErrorCode and errorCode. The request may not have gone out.";

        var chat = (chatWasRunning, chatIsRunning) switch
        {
            (false, true) => $" The {ChatProcessName} process was not running before this call " +
                             "and is now, so the launch happened too.",
            (true, true) => $" The {ChatProcessName} process was already running, so its presence " +
                            "says nothing about this call.",
            _ => $" No {ChatProcessName} process is running even after the call, which is odd " +
                 "given the clean error code - the process name is a measurement from LabVIEW " +
                 "2026 Q3 x86 with lvai 26.3, not a contract.",
        };

        return "InvokeDiscussVI reported no error, which is the verdict NI's own callback throws " +
               "away." + chat;
    }

    /// <summary>
    /// Whether the cached helper VI predates the shipped AIXML. Without this an upgrade leaves
    /// the OLD helper in place - it is generated once and reused - and its outputs no longer
    /// match what this tool reads, which would look like a broken tool rather than a stale file.
    /// </summary>
    internal static bool IsStale(string aixmlPath, string helperViPath)
    {
        if (!File.Exists(helperViPath) || !File.Exists(aixmlPath)) return false;
        return File.GetLastWriteTimeUtc(aixmlPath) > File.GetLastWriteTimeUtc(helperViPath);
    }

    /// <summary>The scalar of one named value out of a parsed Ctrl Val.Get All array.</summary>
    internal static string? Scalar(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        values.FirstOrDefault(v => v.Name == name).Scalar;

    /// <summary>
    /// `code` and `source` out of a flattened LabVIEW error cluster. Null code means the text was
    /// absent or did not parse - never silently 0, because "no error" and "no answer" are
    /// opposite verdicts and this tool gates `ok` on the difference.
    /// </summary>
    internal static (int? Code, string? Source) ErrorCluster(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return (null, null);

        XElement root;
        try { root = XElement.Parse(xml); }
        catch (XmlException) { return (null, null); }

        var code = root.Elements("I32")
            .FirstOrDefault(e => (string?)e.Element("Name") == "code")
            ?.Element("Val")?.Value;
        var source = root.Elements("String")
            .FirstOrDefault(e => (string?)e.Element("Name") == "source")
            ?.Element("Val")?.Value;

        return (int.TryParse(code, out var parsed) ? parsed : null, source);
    }

    /// <summary>
    /// 'VI' or 'PROJECT' folded to NI's spelling, or null when it is neither. The helper compares
    /// the string on its diagram with a case-SENSITIVE `Equal?`, so folding here is what keeps a
    /// lowercase 'project' from silently falling through to VI.
    /// </summary>
    internal static string? NormaliseFileType(string? requested) =>
        (requested ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "" or "VI" => "VI",
            "PROJECT" => "PROJECT",
            _ => null,
        };

    /// <summary>
    /// Why the file cannot be what the caller says it is, or null when it can. NI's chat makes
    /// exactly this check and reports it where only a human can see it.
    /// </summary>
    internal static string? ExtensionComplaint(string targetPath, string fileType)
    {
        var extension = Path.GetExtension(targetPath);
        var wanted = fileType == "PROJECT" ? ".lvproj" : ".vi";
        if (extension.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return null;

        return $"fileType is {fileType}, so the target must be a {wanted} file, and " +
               $"'{Path.GetFileName(targetPath)}' has extension " +
               $"'{(extension.Length == 0 ? "(none)" : extension)}'. The chat window would " +
               "answer this with a red banner of its own.";
    }

    /// <summary>The name to show, defaulting to the file name as NI's own callback does.</summary>
    internal static string Name(string? targetName, string targetPath) =>
        string.IsNullOrWhiteSpace(targetName) ? Path.GetFileName(targetPath) : targetName;

    private static bool ChatIsRunning()
    {
        try
        {
            return Process.GetProcessesByName(ChatProcessName).Length > 0;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
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
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_discuss.vi");

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload. Same shape as RunTools and IconTools; the composed tools fail the same two ways.
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
                    _ => null,
                },
            });
    }
}
