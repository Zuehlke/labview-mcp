using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// lvai_set_vi_execution: every setting of VI Properties > Execution, in one call.
///
/// The user's request of 2026-11-11 - "Inline und reentrant können grosse Vorteile bei
/// Performance-Problemen bieten". AIXML carries none of these settings and VI Server writes only
/// seven, so the route is the pylv_apply cycle with a field patch in the middle: close the project,
/// extract, patch the LVSR fields (<see cref="ViExecutionSettings"/>), strip compiled code,
/// rebuild - and then LabVIEW is ASKED: its executability and Error List, and over COM the seven
/// settings it answers. A request LabVIEW reads back differently fails the call.
/// </summary>
[McpServerToolType]
internal sealed class ExecutionSettingsTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_set_vi_execution", Destructive = true, OpenWorld = true,
                   Title = "Set a VI's execution properties (reentrancy, inline, priority, ...)")]
    [Description("""
        Edit: sets the settings of VI Properties > Execution on an EXISTING VI - reentrancy (none /
        shared / preallocated), inline subVI into callers, allow debugging, priority, preferred
        execution system, automatic error handling, run when opened, suspend when called, clear
        indicators when called, auto handle menus at launch. Give only what should change.
        `inline: true` implies allowDebugging false and preallocated reentrancy (LabVIEW breaks an
        inlined VI otherwise); an explicit contradiction is refused. Route: closes the active
        project (closeProject), patches the file with pylabview, strips compiled code, then asks
        LabVIEW: `execState` (eBad fails the call, with LabVIEW's Error List) and `labviewReads`
        over COM - a setting LabVIEW reads back otherwise fails the call too. `fileAfter` shows all
        ten as the file holds them. Call with no settings to READ them only. A NEW VI's reentrancy
        can come from the AIXML trailer instead (docs/aixml-reference.md §9).
        docs/vi-execution-settings.md.
        """)]
    public async Task<string> SetViExecutionAsync(
        [Description("Absolute path to the .vi - WILL BE OVERWRITTEN when a setting is given")] string viPath,
        [Description("\"none\", \"shared\" (shared clones) or \"preallocated\" (preallocated clones)")]
        string? reentrancy = null,
        [Description("Inline subVI into calling VIs")] bool? inline = null,
        [Description("Allow debugging")] bool? allowDebugging = null,
        [Description("background, normal, above normal, high, time critical, subroutine")]
        string? priority = null,
        [Description("""
            same as caller, user interface, standard, instrument I/O, data acquisition, other 1,
            other 2
            """)]
        string? executionSystem = null,
        [Description("Enable automatic error handling")] bool? automaticErrorHandling = null,
        [Description("Run when opened")] bool? runWhenOpened = null,
        [Description("Suspend when called")] bool? suspendWhenCalled = null,
        [Description("Clear indicators when called")] bool? clearIndicators = null,
        [Description("Auto handle menus at launch")] bool? autoHandleMenus = null,
        [Description("""
            Save and close the active project first (default true) - a VI LabVIEW holds keeps
            serving its old settings after the file is rewritten.
            """)]
        bool closeProject = true,
        [Description("Local budget in seconds, per step (default 120)")] int timeoutSeconds = 120,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_set_vi_execution", async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");
            viPath = Path.GetFullPath(viPath);

            var request = new ViExecutionSettings.Request(allowDebugging, reentrancy, inline, priority,
                executionSystem, automaticErrorHandling, runWhenOpened, suspendWhenCalled, clearIndicators,
                autoHandleMenus);
            var plan = ViExecutionSettings.Resolve(request);
            if (plan.Problems.Count > 0)
                return Json.Error("badArguments", string.Join(" ", plan.Problems), new { viPath });

            if (PyLabview.Locate() is not { } bundle)
                return Json.Error("notProvisioned", PyLabview.NotProvisionedMessage());
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory", "No scripts folder next to the exe - lvai_status " +
                                                        "reports it as scriptsDirectory.");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();
            var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "exec-settings",
                                         Guid.NewGuid().ToString("N")[..12]);
            var readOnly = request.IsEmpty;

            if (closeProject && !readOnly)
            {
                var close = await new CloseTools(connection).CloseActiveProjectAsync(
                    helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
                    timeoutSeconds: timeoutSeconds, ct: ct);
                steps.Add(new JsonObject { ["step"] = "closeActiveProject", ["answer"] = Parse(close) });
            }

            // extract
            var extract = await new PyLabviewTools(connection).ExtractAsync(viPath, directory, annotate: false,
                                                                            timeoutSeconds, ct: ct);
            var mainXml = Parse(extract) is JsonObject eo ? eo["mainXml"]?.GetValue<string>() : null;
            if (mainXml is null || !File.Exists(mainXml))
                return Outcome(false, "extract", steps, total, viPath, null, null,
                    "pylabview could not read the VI, so nothing was changed.", directory, extract);
            var utf8 = new UTF8Encoding(false);
            var before = ViExecutionSettings.Read(utf8.GetString(File.ReadAllBytes(mainXml)));

            if (readOnly)
            {
                TryDelete(directory);
                return Outcome(true, null, steps, total, viPath, before, null,
                    "READ ONLY - no setting was given, so the VI is untouched. `fileAfter` is the file as it is.");
            }

            // patch
            var (set, notFound) = ViExecutionSettings.Patch(mainXml, plan.Fields);
            var implied = new JsonArray();
            foreach (var i in plan.Implied) implied.Add(i);
            steps.Add(new JsonObject
            {
                ["step"] = "patch",
                ["fieldsSet"] = new JsonArray([.. set.Select(s => (JsonNode)s)]),
                ["fieldsNotFound"] = new JsonArray([.. notFound.Select(s => (JsonNode)s)]),
                ["implied"] = implied,
            });
            if (notFound.Length > 0)
                return Outcome(false, "patch", steps, total, viPath, before, null,
                    "The VI's LVSR record lacks " + string.Join(", ", notFound) + ", so it is not the record " +
                    "this patch was measured on. Nothing was written; the bundle is kept.", directory);

            // strip compiled code: a changed reentrancy or inline under old VICD is the stale-code
            // crash pylv_apply records (docs/pylabview-stale-compiled-code.md)
            var strip = await BulkTools.RunScriptAsync(bundle, scripts, "pylv-strip-compiled.py",
                [directory, Path.GetFileNameWithoutExtension(mainXml)], "stripCompiled", timeoutSeconds, ct);
            steps.Add(strip);
            if (strip["exitCode"]?.GetValue<int>() != 0)
                return Outcome(false, "stripCompiled", steps, total, viPath, before, null,
                    "Stripping compiled code failed, so nothing was rebuilt. The bundle is kept.", directory);

            // rebuild
            var rebuild = await new PyLabviewTools(connection).RebuildAsync(mainXml, viPath, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "rebuild", ["answer"] = Parse(rebuild) });
            if (Parse(rebuild) is not JsonObject ro || ro["ok"]?.GetValue<bool>() != true)
                return Outcome(false, "rebuild", steps, total, viPath, before, null,
                    "The rebuild failed. The bundle is kept.", directory);
            if (BulkTools.ContainsCompiledCode(viPath))
                return Outcome(false, "stripCompiled", steps, total, viPath, before, null,
                    "The rebuilt file STILL carries compiled code (VICD) - do not use it.", directory);

            // the file, read back
            var reread = Path.Combine(directory, "after");
            var extractAfter = await new PyLabviewTools(connection).ExtractAsync(viPath, reread, annotate: false,
                                                                                 timeoutSeconds, ct: ct);
            var afterXml = Parse(extractAfter) is JsonObject ao ? ao["mainXml"]?.GetValue<string>() : null;
            var after = afterXml is not null && File.Exists(afterXml)
                ? ViExecutionSettings.Read(utf8.GetString(File.ReadAllBytes(afterXml))) : null;

            // LabVIEW
            var reading = await new ExecStateTools(connection).ReadAsync(viPath, helperAixmlPath: null,
                helperViPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            var state = new JsonObject
            {
                ["step"] = "execState",
                ["execState"] = reading?.State,
                ["meaning"] = reading is null ? "not read" : ExecStateTools.Meaning(reading.State),
            };
            if (reading?.Errors is { Count: > 0 }) state["errors"] = ExecStateTools.ErrorsNode(reading);
            steps.Add(state);

            JsonObject? labview = null;
            var mismatches = new List<string>();
            try
            {
                var r = await LabViewCom.ExecutionAsync(viPath, timeoutSeconds, ct);
                labview = new JsonObject
                {
                    ["allowDebugging"] = r.AllowDebugging,
                    ["reentrancy"] = !r.Reentrant ? ViExecutionSettings.NotReentrant
                        : r.ReentrancyType == 0 ? ViExecutionSettings.Shared : ViExecutionSettings.Preallocated,
                    ["priority"] = ViExecutionSettings.PriorityName(r.Priority),
                    ["executionSystem"] = ViExecutionSettings.ExecutionSystemName(r.ExecutionSystem),
                    ["runWhenOpened"] = r.RunOnOpen,
                    ["suspendWhenCalled"] = r.SuspendOnCall,
                };
                var e = plan.Effective;
                void Check(string key, object? wanted)
                {
                    if (wanted is null) return;
                    var got = labview[key]?.ToString();
                    var want = wanted is bool b ? (b ? "true" : "false") : wanted.ToString();
                    if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add($"{key}: asked {want}, LabVIEW reads {got}");
                }
                Check("allowDebugging", e.AllowDebugging);
                Check("reentrancy", e.Reentrancy);
                Check("priority", e.Priority is null ? null
                    : ViExecutionSettings.PriorityNames[ViExecutionSettings.PriorityFileValue(e.Priority)!.Value]);
                Check("executionSystem", e.ExecutionSystem is null ? null
                    : ViExecutionSettings.ExecutionSystemFileValue(e.ExecutionSystem) is int sv and >= 0
                        ? ViExecutionSettings.ExecutionSystemNames[sv] : ViExecutionSettings.SameAsCaller);
                Check("runWhenOpened", e.RunWhenOpened);
                Check("suspendWhenCalled", e.SuspendWhenCalled);
            }
            catch (LabViewCom.ComException c)
            {
                labview = new JsonObject { ["notReadBecause"] = $"{c.Kind}: {c.Message}" };
            }

            TryDelete(directory);
            var broken = reading?.Broken == true;
            if (broken)
                return Outcome(false, "execState", steps, total, viPath, after, labview,
                    "Written, and LabVIEW reads the VI as NOT EXECUTABLE - its Error List is under the execState " +
                    "step. Nothing was rolled back.");
            if (mismatches.Count > 0)
                return Outcome(false, "labviewReads", steps, total, viPath, after, labview,
                    "Written, but LabVIEW reads " + string.Join("; ", mismatches) + ". The usual cause is a copy " +
                    "LabVIEW still holds in memory (a project other than the active one, an open panel): close " +
                    "it and call again with no settings to read.");
            return Outcome(true, null, steps, total, viPath, after, labview,
                "Set and read back. `labviewReads` is LabVIEW's own reading of the seven settings COM answers; " +
                "inline, automatic error handling, clear indicators and auto handle menus are confirmed from the " +
                "file (`fileAfter`) and, for inline, by the VI staying executable.");
        }));

    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total, string viPath,
                                  JsonObject? file, JsonObject? labview, string note,
                                  string? bundleDirectory = null, string? rawAnswer = null)
    {
        var o = new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["viPath"] = viPath,
            ["fileAfter"] = file,
            ["labviewReads"] = labview,
            ["note"] = note,
        };
        if (bundleDirectory is not null) o["bundleDirectory"] = bundleDirectory;
        if (rawAnswer is not null) o["answer"] = Parse(rawAnswer);
        o["steps"] = steps;
        o["totalElapsedMs"] = total.ElapsedMilliseconds;
        return o.ToJsonString(Json.Answer);
    }
}
