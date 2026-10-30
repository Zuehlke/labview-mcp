// =============================================================================
// LabViewStateTools.cs - lvai_labview_state and lvai_vis_in_memory: looking at a
// running LabVIEW from outside, and asking it which VIs carry unsaved changes.
// =============================================================================
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The two READ-ONLY tools of a colleague's restart route (fork, ported 2026-10-17), without the
/// restart: what a running LabVIEW looks like from outside - processes, windows sorted into kinds,
/// possible dialogs with a picture, LVAutoSave, CPU - and which VIs in memory carry unsaved
/// changes. The fork's lvai_restart_labview and lvai_answer_dialog are NOT ported: the machine
/// interface here has no call that closes a process or posts input.
/// </summary>
[McpServerToolType]
internal sealed class LabViewStateTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = Json.Answer;

    internal const string MemoryHelperAixmlFileName = "lvai_vis_modified.xml";

    [McpServerTool(Name = "lvai_labview_state", ReadOnly = true,
                   Title = "LabVIEW from outside: processes, windows, dialogs, CPU")]
    [Description("""
        Read-only and makes NO LabVIEW call, so it answers while the service is blocked - and it
        answers the same whichever AIXML backend is in force, because it reads Windows, not
        LabVIEW. Lists the LabVIEW processes (pid, start time, path) and whether THIS server
        started one (recorded by lvai_ensure_labview), the Nigel processes
        (NI.NigelLocalService, LVNigelChat), the LVAutoSave folder (resolved through the
        Documents known folder, which may be redirected) and its file count, every LabVIEW
        window sorted into kinds, and one CPU sample.
        `unsaved` = titles with `*`; labview-mcp's own scratch VIs (`LVMCP Validate…`) are
        marked `labviewMcpScratch`. Each possible dialog carries `screenshot`, a PNG path to
        READ: LabVIEW draws its dialogs, so the picture is the only way to see which one it is.
        `possibleDialogs` = VISIBLE titled windows that are not a VI, project or frame window,
        and VISIBLE UNTITLED windows that a LabVIEW frame owns or that sit beside a disabled
        frame or Project Explorer (measured in the fork 2026-10-10: LabVIEW's "has changed on
        disk" prompt is untitled and owned by the frame, which it disables), each with a
        `reason`, named with class, buttons and text where Windows exposes them.
        `hiddenUnrecognised` = HIDDEN titled windows nothing measured explains, named the same
        way: measured 2026-10-06, LabVIEW dialogs on screen reported themselves invisible
        (docs/scratch-vis-in-memory.md §2a). Hidden windows of measured system classes (IME,
        OLE, DDE, GDI+) and LabVIEW's measured hidden windows are not listed there. A VI panel
        with a custom window title is flagged too - look at it.
        CPU: quiet below 1 CPU s per 30 s; idle is not safe - a modal dialog leaves LabVIEW idle.
        NOTHING is pressed, fronted or closed. A modified VI with no open window does not show
        here: lvai_vis_in_memory reads the modification bits. docs/labview-state.md.
        """)]
    public static async Task<string> LabViewStateAsync(
        [Description("Seconds of CPU sampling, 0 to skip (capped at 30)")] int cpuSampleSeconds = 5,
        [Description("Also list hidden system windows (normally left out)")] bool includeHiddenWindows = false,
        [Description(@"A PNG of each VISIBLE possible dialog (PrintWindow: nothing is brought to the front), path under `screenshot`, in %TEMP%\LabVIEWMCP\dialogs. On by default: a few ms, and only when a dialog is up.")]
        bool screenshots = true,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
            (await StateAsync(Win32LabViewMachine.Instance, null, cpuSampleSeconds, includeHiddenWindows, ct,
                              screenshots)).ToJsonString(Indented));

    internal static async Task<JsonObject> StateAsync(ILabViewMachine machine, string? autoSaveDirectory,
        int cpuSampleSeconds, bool includeHiddenWindows, CancellationToken ct, bool screenshots = true)
    {
        var s = LabViewState.Take(machine, autoSaveDirectory);
        var now = machine.Now;

        var labview = new JsonArray();
        foreach (var p in s.LabView)
        {
            var row = LabViewState.ProcessJson(p, now);
            row["startedByThisServer"] = LabViewOwnership.IsServerStarted(p);
            labview.Add(row);
        }

        var counts = new JsonObject();
        foreach (var group in s.Windows.GroupBy(w => w.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            counts[group.Key] = group.Count();

        JsonObject? cpu = null;
        var seconds = Math.Clamp(cpuSampleSeconds, 0, 30);
        if (seconds > 0 && s.Single is { } one)
            cpu = (await LabViewState.SampleCpuAsync(machine, one.Id, TimeSpan.FromSeconds(seconds), ct)).ToJson();

        var visibleOrFlagged = s.Windows.Where(w => includeHiddenWindows || w.Kind != WindowKinds.HiddenSystem);
        var processes = s.LabView.Count switch
        {
            0 => "notRunning",
            1 => "one",
            _ => "several: which one is meant is the user's call",
        };

        return new JsonObject
        {
            ["ok"] = true,
            ["at"] = s.At.ToString("yyyy-MM-dd HH:mm:ss"),
            ["labviewProcesses"] = processes,
            ["labview"] = labview,
            ["recordedAsThisServers"] = LabViewOwnership.ToJson(),
            ["nigelLocalService"] = LabViewState.ProcessesJson(s.NigelService, now),
            ["nigelChat"] = LabViewState.ProcessesJson(s.NigelChat, now),
            ["autoSave"] = s.AutoSave.ToJson(),
            ["windowCounts"] = counts,
            ["unsaved"] = new JsonArray([.. s.Unsaved.Select(w => (JsonNode)LabViewState.WindowJson(w))]),
            ["possibleDialogs"] = new JsonArray([.. s.Dialogs.Select(d => (JsonNode)LabViewState.DescribeDialog(machine, d, screenshots))]),
            ["hiddenUnrecognised"] = new JsonArray([.. s.HiddenUnrecognised.Select(d => (JsonNode)LabViewState.DescribeDialog(machine, d, false))]),
            ["windows"] = new JsonArray([.. visibleOrFlagged.Select(w => (JsonNode)LabViewState.WindowJson(w))]),
            ["cpu"] = cpu,
            ["note"] = "No LabVIEW call was made, on either backend. A modified VI with no open window " +
                       "does not show here, and nothing measured detects a running VI from outside: " +
                       "lvai_vis_in_memory reads the modification bits.",
        };
    }

    [McpServerTool(Name = "lvai_vis_in_memory", ReadOnly = true, OpenWorld = true,
                   Title = "VIs in memory, and which carry unsaved changes")]
    [Description("""
        Every VI in memory of the APPLICATION INSTANCES you name, with each VI's front-panel and
        block-diagram modification bitsets (nonzero = unsaved), each sorted as service (NI's
        `LV AI …lvlibp`), labview (`GSW.lvlibp`, the start window), helper (labview-mcp's own
        helpers and LVMCP scratch VIs) or other. Only the modified, unreadable and `other` lines
        come back, never all 400. Classes and libraries are never listed, only VIs and controls.
        `context` CHOOSES THE INSTANCE - LabVIEW keeps one per context, and a VI is in exactly one:
          main    - LabVIEW's main application instance, where a VI opened loose lives. Reached
                    over LabVIEW's ActiveX server on BOTH backends (that IS the main instance);
                    on grpc the NI-built helper is run there, on pylabview the composed one.
          project - the ACTIVE project's instance (Project:Active Project, then its Application),
                    where a project's VIs live. Pass projectPath to make sure it is that project:
                    another active project is refused by name, and none answers noActiveProject.
                    Nothing is opened or activated here - this tool is read-only.
          addon   - NI's AI add-on's own instance, where a grpc helper runs (the service's VIs;
                    the fork measured 419). grpc only: ActiveX cannot reach it, so pylabview
                    refuses it by name.
          all     - main, the active project if there is one, and addon on grpc. The default.
        Each instance answers under `instances` with its own `onlyServiceAndHelpers`; `modified`
        at the top joins them, each line tagged with its context.
        A probe run while a project is open may be adopted into that project on its next save.
        Runs scripts\lvai_vis_modified.xml (main, addon) and lvai_vis_modified_project.xml
        (project). On grpc a helper is generated once into %TEMP%\LabVIEWMCP\helpers, and
        generating validates it, which leaves an `LVMCP Validate` VI in memory - generate at the
        start of a session. On pylabview the composer needs no validation. NI's packed-library
        .ctl lines answer 1035 and do not count against `onlyServiceAndHelpers`.
        docs/labview-state.md.
        """)]
    public async Task<string> VisInMemoryAsync(
        [Description("main, project, addon or all (default all). See the description.")] string? context = null,
        [Description("With context project or all: the .lvproj that must be the ACTIVE one. Another active project is refused, nothing is opened.")] string? projectPath = null,
        [Description("Regenerate the helper VIs even when they already exist")] bool regenerateHelper = false,
        [Description("Local budget in seconds, per instance")] int timeoutSeconds = 60,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_vis_in_memory", async () =>
            {
                var asked = (context ?? Contexts.All).Trim().ToLowerInvariant();
                if (!Contexts.Known.Contains(asked))
                    return Json.Error("badArguments", $"context '{context}' is not one of {string.Join(", ", Contexts.Known)}.",
                        new { context });
                if (asked == Contexts.Addon && BackendRoute.OnPyLabview)
                    return Json.Error("contextNotReachable",
                        "The add-on's instance is where NI's service runs a helper; on pylabview helpers run " +
                        "over ActiveX, which reaches LabVIEW's main instance only. Use backend grpc for it.",
                        new { context = asked });

                var targets = asked == Contexts.All
                    ? BackendRoute.OnPyLabview ? [Contexts.Main, Contexts.Project] : new[] { Contexts.Main, Contexts.Project, Contexts.Addon }
                    : [asked];

                var instances = new JsonArray();
                var modified = new JsonArray();
                var generatedAny = false;
                var allRead = true;
                var allClean = true;
                foreach (var target in targets)
                {
                    var one = await ReadInstanceAsync(target, projectPath, explicitly: asked != Contexts.All,
                                                      regenerateHelper, timeoutSeconds, ct);
                    generatedAny |= one["helperGenerated"]?.GetValue<bool>() is true;
                    var state = one["state"]!.GetValue<string>();
                    if (state == "read")
                    {
                        allClean &= one["onlyServiceAndHelpers"]?.GetValue<bool>() is true;
                        foreach (var line in one["modified"]?.AsArray() ?? [])
                        {
                            var tagged = line!.DeepClone().AsObject();
                            tagged["context"] = target;
                            modified.Add(tagged);
                        }
                    }
                    else if (state != "noActiveProject" || asked != Contexts.All)
                    {
                        allRead = false;
                    }
                    instances.Add(one);
                }

                var answer = new JsonObject
                {
                    ["ok"] = allRead,
                    ["context"] = asked,
                    ["modified"] = modified,
                    ["onlyServiceAndHelpers"] = allRead && allClean,
                    ["instances"] = instances,
                };
                if (generatedAny)
                    answer["note"] = "A helper was generated by this call; on grpc the validation that comes " +
                                     "with it leaves an `LVMCP Validate` VI in memory. Generate at the start of " +
                                     "a session, not right before a close.";
                return answer.ToJsonString(Indented);
            }));

    /// <summary>The instances lvai_vis_in_memory can read.</summary>
    internal static class Contexts
    {
        public const string Main = "main";
        public const string Project = "project";
        public const string Addon = "addon";
        public const string All = "all";
        public static readonly string[] Known = [Main, Project, Addon, All];
    }

    internal const string ProjectHelperAixmlFileName = "lvai_vis_modified_project.xml";

    /// <summary>
    /// One instance read: the right helper, run where that instance is reachable, its lines parsed.
    /// `state` is read, noActiveProject, otherProjectActive or notRead.
    /// </summary>
    private async Task<JsonObject> ReadInstanceAsync(string target, string? projectPath, bool explicitly,
                                                     bool regenerateHelper, int timeoutSeconds, CancellationToken ct)
    {
        var file = target == Contexts.Project ? ProjectHelperAixmlFileName : MemoryHelperAixmlFileName;
        var result = new JsonObject { ["context"] = target, ["instance"] = InstanceText(target) };

        var scripts = StatusTools.ScriptsDirectory();
        var aixml = scripts is null ? null : Path.Combine(scripts, file);
        var helperVi = Path.Combine(AixmlBackend.HelperFolder(), Path.ChangeExtension(file, ".vi"));
        if (aixml is null || !File.Exists(aixml))
            return NotRead(result, $"{file} was not found beside the exe (scriptsDirectory).");

        var (builtVi, failure, generated) = await BackendRoute.HelperAsync(aixml, helperVi, regenerateHelper,
            () => BackendRoute.ConvertAsync(connection, aixml, helperVi, timeoutSeconds, ct),
            timeoutSeconds, ct);
        result["helperViPath"] = builtVi;
        result["helperGenerated"] = generated;
        if (failure is not null) return NotRead(result, failure);

        // MAIN ON GRPC: the NI-built helper, run over ActiveX - a helper run through NI's service
        // runs in the add-on's instance, and ActiveX is the way into the main one.
        var overCom = target == Contexts.Main && !BackendRoute.OnPyLabview;
        var run = overCom
            ? await RunTools.RunOverComAsync(builtVi, null, 0, null, 0, timeoutSeconds, ct)
            : await new RunTools(connection).RunViAndReadValuesAsync(
                builtVi, inputsJson: null, includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct: ct);
        result["via"] = overCom || BackendRoute.OnPyLabview ? "ActiveX" : "NI's service";

        JsonObject? values = null;
        try { values = (JsonNode.Parse(run) as JsonObject)?["values"] as JsonObject; }
        catch (JsonException) { }
        if (values?["lines"] is null)
            return NotRead(result, "the helper answered no `lines` - " + Shorten(run));

        if (target == Contexts.Project)
        {
            var code = ErrorCodeOf(values);
            var activePath = values["project path"]?["value"]?.GetValue<string>();
            result["activeProject"] = string.IsNullOrEmpty(activePath) ? null : activePath;
            if (code == 1055 || string.IsNullOrEmpty(activePath))
            {
                result["state"] = "noActiveProject";
                result["problem"] = "No project is active, so there is no project instance to read" +
                                    (explicitly ? "." : " - skipped.");
                return result;
            }
            if (projectPath is { Length: > 0 } &&
                !string.Equals(Path.GetFullPath(projectPath), Path.GetFullPath(activePath),
                               StringComparison.OrdinalIgnoreCase))
            {
                result["state"] = "otherProjectActive";
                result["problem"] = $"The active project is '{activePath}', not '{projectPath}'. Its instance " +
                                    "was not read; nothing is activated here.";
                return result;
            }
            if (code is { } c && c != 0)
                return NotRead(result, $"the helper answered error {c} reading the project's instance.");
        }

        var lines = GraftTools.StringArray(values, "lines");
        // Both helper folders: a VI from either cache is ours, whichever backend left it in memory.
        string[] helperFolders = [Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers"), ComposedHelpers.Folder];
        var helperNames = VisInMemory.HelperNames(
            Directory.EnumerateFiles(scripts!, "*.xml"),
            helperFolders.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.vi")));
        // The running LabVIEW's own folder: what lies under it is the installation, not the user's.
        var installationRoot = LabViewState.Take(Win32LabViewMachine.Instance).Single?.Path is { } exe
            ? Path.GetDirectoryName(exe) : null;
        var reading = VisInMemory.Parse(lines, helperNames, installationRoot);
        result["state"] = "read";
        foreach (var (key, value) in reading.ToJson())
            result[key] = value?.DeepClone();
        return result;
    }

    private static JsonObject NotRead(JsonObject result, string problem)
    {
        result["state"] = "notRead";
        result["problem"] = problem;
        return result;
    }

    private static string InstanceText(string target) => target switch
    {
        Contexts.Main => "LabVIEW's main application instance - VIs opened loose",
        Contexts.Project => "the active project's application instance - the project's VIs",
        _ => "NI's AI add-on's application instance - where NI's service runs a helper",
    };

    /// <summary>The code of the helper's `error out`, or null when it was not read.</summary>
    private static int? ErrorCodeOf(JsonObject values)
    {
        var xml = (values["error out"] as JsonObject)?["xml"]?.GetValue<string>();
        if (xml is null) return null;
        try
        {
            var code = System.Xml.Linq.XElement.Parse(xml).Elements()
                .FirstOrDefault(e => e.Name.LocalName == "I32" && (string?)e.Element("Name") == "code");
            return int.TryParse((string?)code?.Element("Val"), out var v) ? v : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static string Shorten(string text) => text.Length <= 600 ? text : text[..600] + " …";
}
