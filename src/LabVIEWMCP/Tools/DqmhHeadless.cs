using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Grpc.Core;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Driving Delacor's DQMH scripters WITHOUT their dialogs, through a generated wrapper VI.
///
/// THE SHAPE, measured 2026-10-06 (docs/dqmh-scripting.md section 9): every DQMH menu function is
/// "parse the project, pick a module, script, close", and the scripters need `Module Info`, whose
/// thirteen refnums die when a parse run as its own top-level VI stops. A generated wrapper that
/// calls the parse and the scripter as STATIC subVIs of one caller keeps them alive - and an AIXML
/// `Call` reaches Delacor's project-library members once they are OPEN in LabVIEW, opened THROUGH
/// the active project when one is active. No latched button, no keystroke, no foreground.
///
/// This class holds what every such tool shares: opening the targets, generating the wrapper
/// under a throwaway name, matching names the way Delacor spells them, and reading back what the
/// scripting wrote and what LabVIEW adopted into the project when Delacor saved it.
/// </summary>
internal sealed class DqmhHeadless(LvaiConnection connection)
{
    // ------------------------------------------------------------------ the wrapper

    /// <summary>
    /// Absolute paths of the wrapper's Delacor targets in the first LabVIEW installation that has
    /// all of them, or null when DQMH is not installed.
    /// </summary>
    internal static string[]? TargetPaths(IReadOnlyList<string> relative)
    {
        foreach (var install in LabViewLocator.Discover())
        {
            var root = Path.GetDirectoryName(install.ExePath) ?? "";
            var paths = relative.Select(t => Path.Combine(root, t)).ToArray();
            if (paths.All(File.Exists)) return paths;
        }
        return null;
    }

    /// <summary>
    /// Generate the wrapper when its AIXML is newer than the cached VI. Null on success, otherwise
    /// the error answer. The targets are opened first because the converter resolves a `Call` to
    /// them only while they are loaded.
    /// </summary>
    internal async Task<string?> EnsureWrapperAsync(
        string scripts, string helperName, string[] targets, string? projectPath,
        JsonArray steps, int timeoutSeconds, CancellationToken ct, string[]? subHelpers = null)
    {
        // THE WRAPPER MAY CALL HELPERS OF OUR OWN - the edit wrappers share `lvdqmh_pick_event`
        // and `lvdqmh_close_modules`, folded out to keep each diagram inside the size budget. A
        // Call to one of them resolves only once IT is loaded too, so the order is: Delacor's VIs
        // through the project, then each sub-helper generated and opened, then the wrapper.
        var chain = (subHelpers ?? []).Append(helperName)
            .Select(h => (Name: h, Aixml: Path.Combine(scripts, h + ".xml"),
                          Vi: Path.Combine(DqmhTools.HelperDirectory(), h + ".vi")))
            .ToList();
        if (chain.FirstOrDefault(h => !File.Exists(h.Aixml)) is { Name: { } missing })
            return Json.Error("scriptsMissing", $"{missing}.xml was not found in the scripts folder.");

        if (DqmhTools.OnPyLabview)
        {
            // NOTHING IS OPENED: the composer resolves a Call by FILE - Delacor's VIs under
            // LabVIEW's project\ folder, our sub-helpers in the folder they are composed into - so
            // composing the sub-helpers first is the whole of the ordering. Measured 2026-10-13:
            // all 17 lvdqmh_*.xml compose that way and load executable over ActiveX.
            foreach (var (name, _, _) in chain)
            {
                var (vi, problem) = await ComposedHelpers.EnsureAsync(name + ".xml", timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "composeWrapper", ["helper"] = name, ["helperVi"] = vi,
                                           ["problem"] = problem });
                if (vi is null)
                    return Json.Error("wrapperGenerationFailed",
                        $"{name} could not be composed: {problem}", new { steps });
            }
            return null;
        }

        if (!chain.Any(h => HelperCache.NeedsRebuild(h.Aixml, h.Vi))) return null;

        var opened = await OpenTargetsAsync(targets, projectPath, timeoutSeconds, ct);
        steps.Add(new JsonObject { ["step"] = "openDelacorVIs", ["opened"] = opened });

        foreach (var (name, aixml, vi) in chain)
        {
            if (HelperCache.NeedsRebuild(aixml, vi))
            {
                var generated = await GenerateAsync(name, aixml, vi, timeoutSeconds, ct);
                steps.Add(generated);
                if (generated["errorCode"]?.GetValue<int>() is not 0 || !File.Exists(vi))
                    return Json.Error("wrapperGenerationFailed",
                        $"{name} could not be generated. Error 53 naming a VI means it was not " +
                        "loaded in the context the wrapper is generated in - see `opened` for each " +
                        "open's own error code.",
                        new { steps, helperVi = vi });
            }
            if (name != helperName)
                steps.Add(new JsonObject
                {
                    ["step"] = "openHelper",
                    ["opened"] = await OpenTargetsAsync([vi], projectPath, timeoutSeconds, ct),
                });
        }
        return null;
    }

    /// <summary>
    /// Open the wrapper's targets THROUGH THE ACTIVE PROJECT. Measured 2026-10-06 as an A/B on one
    /// project: opened loose while the project was active, the conversion answered Error 53 three
    /// times; opened with the project pair, the same document converted clean. A VI generated with
    /// a project active lives in the PROJECT's application instance (docs/aixml-call-loaded-vi.md
    /// section 3), so its subVIs have to be resolvable there.
    /// </summary>
    private async Task<JsonArray> OpenTargetsAsync(
        string[] targets, string? projectPath, int timeoutSeconds, CancellationToken ct)
    {
        var project = projectPath is { Length: > 0 } && File.Exists(projectPath) ? projectPath : "";
        var opened = new JsonArray();
        foreach (var target in targets)
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.OpenFileAsync(new OpenFileRequest
                {
                    ViPath = target,
                    ViName = Path.GetFileName(target),
                    ProjectPath = project,
                    ProjectName = project.Length > 0 ? Path.GetFileName(project) : "",
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            opened.Add(new JsonObject
            {
                ["viPath"] = target,
                ["errorCode"] = response.ErrorCode,
                ["errorMessage"] = response.ErrorMessage,
            });
        }
        return opened;
    }

    /// <summary>
    /// Convert the wrapper under a THROWAWAY VI name. A failed convert burns the name it was
    /// given for the rest of the session (Error 1051 on the next try, docs/aixml-call-loaded-vi.md
    /// section 3), and the saved VI is named after its file anyway.
    /// </summary>
    private async Task<JsonObject> GenerateAsync(
        string helperName, string aixml, string helperVi, int timeoutSeconds, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            $"{helperName}.{Guid.NewGuid():N}.xml");
        var throwaway = $"LVMCP DQMH {Guid.NewGuid().ToString("N")[..8]}.vi";
        File.WriteAllText(scratch, File.ReadAllText(aixml).Replace(
            $"_name=\"{helperName}.vi\"", $"_name=\"{throwaway}\""));
        try
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
                {
                    AiXMLFilePath = scratch,
                    ViPath = helperVi,
                    OpenVI = false,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return new JsonObject
            {
                ["step"] = "generateWrapper",
                ["errorCode"] = response.ErrorCode,
                ["errorMessage"] = response.ErrorMessage,
                ["helperVi"] = helperVi,
            };
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    // ------------------------------------------------------------------ names

    /// <summary>Delacor lists a module as `Heater.lvlib` - its library file name.</summary>
    internal static string DelacorModuleName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase)
            ? trimmed : trimmed + ".lvlib";
    }

    /// <summary>Delacor lists an event as `Do Something.vi` - its request VI's file name.</summary>
    internal static string DelacorEventName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)
            ? trimmed : trimmed + ".vi";
    }

    /// <summary>
    /// The exact entry of <paramref name="listed"/> the caller meant, ignoring case and the file
    /// extension, or null when there is none. Exact matches are the wrapper's job; this is for the
    /// spellings it cannot see past.
    /// </summary>
    internal static string? FindListed(IEnumerable<string> listed, string wanted)
    {
        static string Bare(string s) => Path.GetFileNameWithoutExtension(s.Trim());
        return listed.FirstOrDefault(l =>
            string.Equals(Bare(l), Bare(wanted), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A string array indicator's entries. An EMPTY array still flattens one blank element - the
    /// first acceptance run answered `requestEvents: [""]` for a module that was never selected -
    /// and no DQMH module or event has an empty name, so blanks are dropped.
    /// </summary>
    internal static List<string> Listed(IReadOnlyList<LvValuesXml.Value> run, string name) =>
        [.. DqmhTools.Strings(run, name).Where(s => !string.IsNullOrWhiteSpace(s))];

    internal static int Index(IReadOnlyList<LvValuesXml.Value> run, string name) =>
        int.TryParse(DqmhTools.Scalar(run, name), out var index) ? index : -1;

    internal static bool IsTrue(string? value) =>
        value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    internal static JsonArray Array(IEnumerable<string> values) =>
        new([.. values.Select(v => JsonValue.Create(v))]);

    internal static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var merged = (JsonObject)a.DeepClone();
        foreach (var (key, value) in b) merged[key] = value?.DeepClone();
        return merged;
    }

    // ------------------------------------------------------------------ reading the result

    /// <summary>
    /// The project items that point into this server's helper directory. LabVIEW adopts every VI
    /// it has open in the project's application instance when the project is saved, and Delacor's
    /// scripting saves it - measured 2026-10-06: `lvai_run_and_read.vi` and the wrapper itself
    /// appeared in the fixture's .lvproj after one run. Read-only: the file may not be edited while
    /// LabVIEW holds the project.
    /// </summary>
    internal static List<string> AdoptedHelpers(string lvproj) =>
        [.. Regex.Matches(lvproj,
                "<Item Name=\"([^\"]+)\"[^>]*URL=\"[^\"]*LabVIEWMCP/(?:helpers|helpers-pylabview|dqmh-carriers)/[^\"]*\"")
            .Select(m => m.Groups[1].Value)];

    internal static List<string> AdoptedHelpersOf(string? projectPath) =>
        projectPath is { Length: > 0 } && File.Exists(projectPath)
            ? AdoptedHelpers(File.ReadAllText(projectPath))
            : [];

    internal const string AdoptedHelpersNote =
        "Delacor SAVED the project while this tool's helper VIs were open in it, so LabVIEW listed " +
        "them in the .lvproj. Close the project with lvai_close_active_project AND projectPath - " +
        "its sweep removes entries under %TEMP% - and never edit the .lvproj while LabVIEW holds " +
        "it open.";

    /// <summary>
    /// What is on disk under the project folder, so the answer can name what the scripting wrote
    /// instead of trusting a clean error cluster. Delacor's output folder is its own business; a
    /// before/after comparison does not need to know it.
    /// </summary>
    internal sealed class Snapshot
    {
        private readonly Dictionary<string, (DateTime Written, long Bytes)> _files;

        private Snapshot(Dictionary<string, (DateTime, long)> files) => _files = files;

        internal static Snapshot? TakeOf(string? projectPath)
        {
            var directory = Path.GetDirectoryName(projectPath ?? "");
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
                ? Take(directory) : null;
        }

        internal static Snapshot Take(string directory) =>
            new(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f =>
                {
                    var info = new FileInfo(f);
                    return (info.LastWriteTimeUtc, info.Length);
                }, StringComparer.OrdinalIgnoreCase));

        internal (List<string> Created, List<string> Modified) Diff(Snapshot after)
        {
            var created = after._files.Keys.Where(f => !_files.ContainsKey(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var modified = after._files
                .Where(f => _files.TryGetValue(f.Key, out var old) && old != f.Value)
                .Select(f => f.Key).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            return (created, modified);
        }

        /// <summary>Files that were there before and are gone now - a remove or a rename.</summary>
        internal List<string> Deleted(Snapshot after) =>
            [.. _files.Keys.Where(f => !after._files.ContainsKey(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Each created file with its size, and its exec state when it is a VI.</summary>
    internal async Task<JsonArray> DescribeAsync(
        IEnumerable<string> files, int timeoutSeconds, CancellationToken ct)
    {
        var described = new JsonArray();
        foreach (var file in files)
        {
            var entry = new JsonObject
            {
                ["path"] = file,
                ["bytes"] = new FileInfo(file).Length,
            };
            if (file.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
                entry["execState"] = await ExecStateAsync(file, timeoutSeconds, ct);
            described.Add(entry);
        }
        return described;
    }

    internal async Task<int?> ExecStateAsync(string vi, int timeoutSeconds, CancellationToken ct) =>
        (JsonNode.Parse(await new ExecStateTools(connection)
            .ExecStateAsync(vi, timeoutSeconds: timeoutSeconds, ct: ct)) as JsonObject)?
            ["execState"] is JsonValue value && value.TryGetValue<int>(out var state)
            ? state : null;

    // ------------------------------------------------------------------ event checks

    /// <summary>
    /// Labels Delacor refuses in an arguments window, read off `Check if OK to Proceed.vi`
    /// (2026-10-06): `[module id,error in (no error),error out,timed out?]`, compared ignoring case.
    /// </summary>
    internal static readonly string[] ReservedArgumentNames =
        ["module id", "error in (no error)", "error out", "timed out?"];

    /// <summary>
    /// The checks Delacor's dialog makes before it scripts, made here instead - because each of its
    /// own checks answers a failure with a MODAL dialog, and a modal stops the gRPC service.
    /// `Check if OK to Proceed.vi`: names not blank, valid file names, argument labels unique and
    /// not reserved. `Verify Event Names.vi`: a Round Trip's two names differ. Null when clean.
    /// </summary>
    internal static string? EventRequestProblem(
        string eventName, int typeIndex, string roundTripBroadcastName,
        IReadOnlyList<DqmhTools.Argument> arguments, IReadOnlyList<DqmhTools.Argument> replyArguments)
    {
        if (FileNameProblem(eventName, "The event name") is { } eventProblem) return eventProblem;
        if (DqmhTools.IsRoundTrip(typeIndex))
        {
            if (FileNameProblem(roundTripBroadcastName, "roundTripBroadcastName") is { } rt)
                return rt;
            if (string.Equals(eventName.Trim(), roundTripBroadcastName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return "The request and broadcast names of a Round Trip cannot be the same.";
        }
        return LabelProblem(arguments, "argumentsJson") ?? LabelProblem(replyArguments, "replyArgumentsJson");
    }

    private static string? FileNameProblem(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"{what} cannot be blank.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return $"{what} '{name}' contains a character that is not allowed in a file name - " +
                   "it becomes the event VI's file name.";
        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
            return $"{what} '{name}' starts or ends with a space or ends with a dot, which Windows " +
                   "strips from a file name.";
        return null;
    }

    private static string? LabelProblem(IReadOnlyList<DqmhTools.Argument> arguments, string which)
    {
        var reserved = arguments.FirstOrDefault(a => ReservedArgumentNames.Contains(
            a.Name.Trim(), StringComparer.OrdinalIgnoreCase));
        if (reserved.Name is not null)
            return $"{which}: '{reserved.Name}' is reserved by DQMH's scripting - " +
                   $"{string.Join(", ", ReservedArgumentNames)} cannot be argument names.";
        var duplicate = arguments.GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null
            : $"{which}: '{duplicate.Key}' appears {duplicate.Count()} times - DQMH needs argument " +
              "names that are unique ignoring case.";
    }

    /// <summary>
    /// Every individual case name in an AIXML export, decoded: `selector="&amp;quot;A&amp;quot;\2C
    /// &amp;quot;B&amp;quot;"` gives `A` and `B`. `Preflight Main VI.vi` refuses an event whose
    /// name matches a frame of the message-handling case structure, ignoring case - through a modal.
    /// This reads EVERY case structure's frames, which is stricter than Delacor's one structure and
    /// can only refuse a name Delacor would have taken, never script one it would have refused.
    /// </summary>
    internal static List<string> CaseNames(string aixml)
    {
        var names = new List<string>();
        foreach (Match m in Regex.Matches(aixml, "<CaseFrame [^>]*selector=\"([^\"]*)\""))
        {
            var selector = WebUtility.HtmlDecode(m.Groups[1].Value);
            foreach (var part in selector.Split("\\2C"))
            {
                var name = Regex.Replace(part.Trim().Trim('"'), @"\\([0-9A-Fa-f]{2})",
                    h => ((char)Convert.ToInt32(h.Groups[1].Value, 16)).ToString());
                if (name.Length > 0) names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// New .vi and .ctl files a scripted event adds to the module folder, measured over the dialog
    /// route (docs/dqmh-scripting.md section 6.11a): the event VI and its argument cluster for a
    /// Request or Broadcast, plus a reply cluster for Request and Wait for Reply, plus the
    /// broadcast half for a Round Trip.
    /// </summary>
    internal static int ExpectedNewFiles(int typeIndex) => typeIndex switch
    {
        0 or 1 => 2,
        2 => 3,
        3 => 4,
        _ => -1,
    };

    // ------------------------------------------------------------------ events, headless

    private const string EventHelper = "lvdqmh_script_new_event";

    internal static readonly string[] EventTargets =
    [
        @"project\Delacor\DQMH\_DQMH New Event\Parse Project for DQMH Modules.vi",
        @"project\Delacor\DQMH\_DQMH New Event\Close Scripting References.vi",
        @"project\Delacor\DQMH\_DQMH Remove Event\Get All Events in Module.vi",
        @"project\Delacor\DQMH\_DQMH New Event\Script New Event.vi",
    ];

    /// <summary>
    /// Create one DQMH event through the wrapper: a DRY run reports the module, its folder and its
    /// events; the checks Delacor's dialog would make run here; then the scripted run. Arguments
    /// have been parsed and type-checked by the caller.
    /// </summary>
    internal async Task<string> NewEventAsync(
        DqmhTools dqmh, string scripts, string moduleName, string eventName, int typeIndex,
        string typeName, List<DqmhTools.Argument> arguments, List<DqmhTools.Argument> replyArguments,
        string roundTripBroadcastName, string description, bool addTesterButton,
        int timeoutSeconds, CancellationToken ct, int requestScoping = 0,
        string consumingLoop = DqmhTools.DefaultConsumingLoop)
    {
        if (!File.Exists(Path.Combine(scripts, EventHelper + ".xml")))
            return Json.Error("scriptsMissing",
                $"{EventHelper}.xml was not found in the scripts folder next to the exe.");
        if (EventRequestProblem(eventName, typeIndex, roundTripBroadcastName, arguments, replyArguments)
            is { } problem)
            return Json.Error("badArguments", problem);
        if (DqmhLayout.Resolve(scripts, EventHelper, EventTargets) is not { } route)
            return Json.Error("dqmhMissing", DqmhLayout.MissingMessage("Script New Event.vi"),
                new { lookedFor = EventTargets });
        var (targets, eventHelper) = (route.Targets, route.Helper);
        var scoped = requestScoping != 0 || consumingLoop != DqmhTools.DefaultConsumingLoop;
        if (scoped && route.Generation == DqmhGeneration.Classic)
            return Json.Error("badArguments",
                "requestScoping and consumingLoop are DQMH 7.x inputs - the classic Script New Event.vi " +
                "on this station creates public requests only.");

        var stopwatch = Stopwatch.StartNew();
        var steps = new JsonArray();
        var (active, activeNote, projectPath) =
            await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
        if (active is false)
            return Json.Error("noActiveProject",
                "No project is active. Delacor's scripting works on the ACTIVE project - open it " +
                "with lvai_open_file (projectPath + projectName) first.",
                new { activeProjectCheck = activeNote });

        if (await EnsureWrapperAsync(scripts, eventHelper, targets, projectPath, steps,
                timeoutSeconds, ct) is { } wrapperError)
            return wrapperError;

        // ---- 1. dry run: which module, where it lives, which events it has ------------------
        var module = DelacorModuleName(moduleName);
        var inputs = new Dictionary<string, string>
        {
            ["Module Name"] = module,
            ["Event Name"] = eventName.Trim(),
            ["Event Type"] = typeIndex.ToString(),
            ["Round Trip Broadcast Name"] = roundTripBroadcastName.Trim(),
            ["Event Description"] = description,
            ["Add Tester Button"] = addTesterButton ? "true" : "false",
            ["Arguments VI Path"] = "",
            ["Reply Payload VI Path"] = "",
            ["Script?"] = "false",
        };
        // only a 7.x wrapper has these controls, and they are sent only when they differ from its defaults
        if (scoped)
        {
            inputs["Request Scoping"] = requestScoping.ToString();
            inputs["Request Consuming Loop"] = consumingLoop;
        }
        var (dry, dryHelperError) = await dqmh.RunDetailedAsync(
            scripts, eventHelper, Sendable(inputs), timeoutSeconds, ct);
        if (dry is null)
            return Json.Error("helperMissing", $"{eventHelper} could not be run.", new { steps });
        if (dryHelperError is not null)
            return Json.Error("helperFailed",
                "The run helper stopped before the wrapper ran - an input could not be set.",
                new { helperError = dryHelperError, steps });
        steps.Add(RunStep("dryRun", module, dry));
        if (Index(dry, "Module Index") < 0
            && FindListed(Listed(dry, "Module Names"), module) is { } listed && listed != module)
        {
            inputs["Module Name"] = module = listed;
            dry = await dqmh.RunAsync(scripts, eventHelper, Sendable(inputs), timeoutSeconds, ct);
            if (dry is null)
                return Json.Error("helperMissing", $"{eventHelper} could not be run.", new { steps });
            steps.Add(RunStep("dryRun", module, dry));
        }

        var moduleNames = Listed(dry, "Module Names");
        var existing = Listed(dry, "Event Names");
        var detail = new JsonObject
        {
            ["moduleNames"] = Array(moduleNames),
            ["existingEvents"] = Array(existing),
            ["steps"] = steps,
        };
        if (DqmhTools.Failed(dry) is { } dryError)
            return Json.Error("parseFailed", "Delacor's project parse answered an error.",
                Merge(detail, new JsonObject { ["error"] = dryError }));
        if (moduleNames.Count == 0)
            return Json.Error("noDqmhModules",
                "The active project holds no DQMH module that Delacor recognises.",
                Merge(detail, new JsonObject { ["activeProject"] = projectPath }));
        if (Index(dry, "Module Index") < 0)
            return Json.Error("moduleNotFound",
                $"No DQMH module '{moduleName}' in the active project - `moduleNames` lists them.",
                detail);
        // LOCKED blocks, DIRTY does not: Delacor's own event dialog leaves `Any Dirty Modules?`
        // unwired (read off its export 2026-10-06), and scripting an event leaves members dirty,
        // so a dirty gate would refuse every second event of a session - measured.
        if (IsTrue(DqmhTools.Scalar(dry, "Any Locked Modules?")))
            return Json.Error("modulesLocked",
                "A DQMH module in this project is locked or read-only. Delacor cannot script it; " +
                "nothing was written.", detail);

        // NOTHING IN THE PROJECT MAY BE UNSAVED - Delacor's create-event dialog does not ask, but
        // the dialog watch below answers LabVIEW's save prompt with Save - All, and that is only
        // right when the prompt can list nothing but this call's own changes.
        var (dirtyBefore, dirtyError) = await ProjectDirtyAsync(
            dqmh, scripts, projectPath, timeoutSeconds, ct);
        if (dirtyError is not null)
            return Json.Error("dirtyCheckFailed",
                "Could not read which VIs of the project have unsaved changes.",
                Merge(detail, new JsonObject { ["error"] = dirtyError }));
        if (dirtyBefore is { Count: > 0 })
            return Json.Error("unsavedChangesInProject", UnsavedBeforeMessage(dirtyBefore),
                Merge(detail, new JsonObject { ["unsaved"] = Array(dirtyBefore) }));

        // ---- 2. the checks Delacor's dialog would answer with a modal ----------------------
        var folder = DqmhTools.Scalar(dry, "Library Owning Folder") ?? "";
        var names = DqmhTools.IsRoundTrip(typeIndex)
            ? new[] { eventName.Trim(), roundTripBroadcastName.Trim() }
            : [eventName.Trim()];
        foreach (var name in names)
        {
            if (FindListed(existing, name) is { } taken)
                return Json.Error("eventNameInUse",
                    $"Module {module} already has an event '{taken}'.", detail);
            if (folder.Length > 0 && File.Exists(Path.Combine(folder, name + ".vi")))
                return Json.Error("eventNameInUse",
                    $"The module folder already holds '{name}.vi', the file this event would " +
                    "create. Delacor's own dialog refuses the same name.",
                    Merge(detail, new JsonObject { ["moduleFolder"] = folder }));
        }

        var mainVi = Path.Combine(folder, "Main.vi");
        var bareModule = Path.GetFileNameWithoutExtension(module);
        var tester = Path.Combine(folder, $"Test {bareModule} API.vi");
        if (File.Exists(mainVi))
        {
            var (frames, exportCode) = await MainViCaseNamesAsync(mainVi, timeoutSeconds, ct);
            steps.Add(new JsonObject
            {
                ["step"] = "readMainViFrames",
                ["errorCode"] = exportCode,
                ["frames"] = frames.Count,
            });
            if (names.Select(n => frames.FirstOrDefault(f =>
                    string.Equals(f, n, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(f => f is not null) is { } frame)
                return Json.Error("eventNameInUse",
                    $"Main.vi already has a case named '{frame}'. Delacor's dialog refuses an event " +
                    "whose name matches a message-handling frame.", detail);
        }

        // ---- 3. the argument carriers, fresh per run - Delacor deletes them ------------------
        var carriers = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "dqmh-carriers",
            Guid.NewGuid().ToString("N")[..12]);
        var argumentsVi = Path.Combine(carriers, "Arguments.vi");
        var replyVi = Path.Combine(carriers, "Reply Payload.vi");
        if (await dqmh.BuildCarrierAsync(arguments, argumentsVi, timeoutSeconds, ct) is { } a)
            return a;
        if (await dqmh.BuildCarrierAsync(replyArguments, replyVi, timeoutSeconds, ct) is { } r)
            return r;

        // ---- 4. script ----------------------------------------------------------------------
        // ISSUE #112: `createdFiles: []` and `ok: false` for events whose files were plainly
        // there. A snapshot needs a folder; when the active-project check names no project path
        // there was none, and every count read 0. The module folder is the fallback - it is
        // where Delacor writes an event, and it is known from the dry run.
        var snapshotRoot = SnapshotRoot(projectPath, folder);
        var before = snapshotRoot is null ? null : Snapshot.Take(snapshotRoot);
        var mainBefore = File.Exists(mainVi) ? new FileInfo(mainVi).Length : -1;
        var mainExecBefore = File.Exists(mainVi) ? await ExecStateAsync(mainVi, timeoutSeconds, ct) : null;
        var testerExecBefore = File.Exists(tester) ? await ExecStateAsync(tester, timeoutSeconds, ct) : null;
        inputs["Arguments VI Path"] = argumentsVi;
        inputs["Reply Payload VI Path"] = replyVi;
        inputs["Script?"] = "true";

        IReadOnlyList<LvValuesXml.Value>? run;
        JsonArray dialogs;
        var watch = DqmhDialogWatch.Start();
        try
        {
            string? runHelperError;
            (run, runHelperError) = await dqmh.RunDetailedAsync(scripts, eventHelper,
                Sendable(inputs), Math.Min(timeoutSeconds, 300), ct);
            if (runHelperError is not null)
                return Json.Error("helperFailed",
                    "The run helper stopped before the wrapper ran - an input could not be set; " +
                    "nothing was scripted.", new { helperError = runHelperError, steps });
        }
        catch (Exception e) when (DqmhTools.IsTimeout(e))
        {
            return Json.Error("scriptingTimedOut",
                "The scripted run did not return. Script New Event.vi reports a scripting error " +
                "through Simple Error Handler's MODAL dialog, which stops the run until someone " +
                "dismisses it - look at LabVIEW for an open error dialog before calling anything " +
                "else.",
                Merge(detail, new JsonObject
                {
                    ["visibleWindows"] = Array(DqmhTools.Win32.VisibleTitles()),
                    ["dialogs"] = watch.Events(),
                }));
        }
        finally
        {
            dialogs = watch.Events();
            await watch.DisposeAsync();
        }
        if (run is null)
            return Json.Error("helperMissing", $"{eventHelper} could not be run.", new { steps });
        steps.Add(RunStep("script", module, run));

        if (DqmhTools.Failed(run) is { } error)
            return Json.Error("scriptingFailed",
                "Delacor's scripting answered an error - `error` is its cluster.",
                Merge(detail, new JsonObject { ["error"] = error }));
        if (!IsTrue(DqmhTools.Scalar(run, "Scripted?")))
            return Json.Error("notScripted",
                "The wrapper did not call Script New Event.vi although the dry run found the " +
                "module - read `steps`.", detail);

        // ---- 5. complete what Delacor leaves open, save, verify from the files ------------
        JsonObject? testerWiring = null;
        if (addTesterButton && typeIndex != 1 && File.Exists(tester))
        {
            // The user's rule of 2026-10-06: Delacor drops the new request VI into the tester's
            // frame with its REQUIRED argument inputs unwired, which leaves the tester eBad -
            // measured on the first Request with arguments. A control on each of them is the
            // DQMH tester's own convention: the frame reads its controls when the button fires.
            var helperError = await EnsureTesterHelperAsync(scripts, projectPath, steps,
                timeoutSeconds, ct);
            var wired = helperError is null
                ? await dqmh.RunAsync(scripts, TesterHelper, new()
                {
                    ["Tester VI Path"] = tester,
                    // {LV.SubVI} VI Name reads the QUALIFIED name of a library member - measured.
                    ["Event VI Name"] = $"{module}:{DelacorEventName(eventName)}",
                    ["Create Constants?"] = "false",
                }, timeoutSeconds, ct)
                : null;
            testerWiring = wired is null
                ? new JsonObject
                {
                    ["ran"] = false,
                    ["reason"] = helperError ?? $"{TesterHelper} could not be run",
                }
                : new JsonObject
                {
                    ["ran"] = true,
                    ["eventNodesFound"] = DqmhTools.Scalar(wired, "Event Nodes Found"),
                    ["terminalsWired"] = Array(Listed(wired, "Terminals Wired")),
                    ["error"] = DqmhTools.Failed(wired),
                };
        }

        // Nothing in the project was unsaved before this call - it refuses otherwise - so
        // whatever is unsaved now is the scripting's own work: module members, the tester, its
        // unit tests. Save it, as a person would after Delacor's dialog.
        var (dirtyAfter, _) = await ProjectDirtyAsync(dqmh, scripts, projectPath, timeoutSeconds, ct);
        var saved = await SaveNamedAsync(dqmh, scripts, dirtyAfter ?? [], timeoutSeconds, ct);
        var unsaved = (await ProjectDirtyAsync(dqmh, scripts, projectPath, timeoutSeconds, ct)).Dirty ?? [];

        List<string> created = [], modified = [];
        if (before is not null)
            (created, modified) = before.Diff(Snapshot.Take(snapshotRoot!));
        var scripted = created.Where(f => f.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase))
            .Where(f => folder.Length == 0 || f.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var expected = ExpectedNewFiles(typeIndex);
        var mainExec = File.Exists(mainVi) ? await ExecStateAsync(mainVi, timeoutSeconds, ct) : null;
        var testerExec = File.Exists(tester) ? await ExecStateAsync(tester, timeoutSeconds, ct) : null;
        var adopted = AdoptedHelpersOf(projectPath);

        var completion = new JsonArray();
        foreach (var item in EventCompletion(eventName.Trim(), typeIndex, requestScoping,
                     roundTripBroadcastName.Trim(), bareModule, route.Generation, addTesterButton,
                     mainExecBefore, mainExec, testerExecBefore, testerExec))
            completion.Add(item);

        var answer = new JsonObject
        {
            ["ok"] = scripted.Count == expected,
            ["route"] = "headless",
            ["dqmhGeneration"] = route.GenerationName,
            ["wrapper"] = eventHelper,
            ["module"] = module,
            ["event"] = eventName.Trim(),
            ["eventType"] = typeName,
            ["requestScoping"] = DqmhTools.RequestScopings[requestScoping],
            ["consumingLoop"] = consumingLoop,
            ["moduleExecutable"] = mainExec is 1 && testerExec is 1 or null,
            ["completionNeeded"] = completion,
            ["createdFiles"] = await DescribeAsync(created, timeoutSeconds, ct),
            ["expectedNewModuleFiles"] = expected,
            ["newModuleFiles"] = scripted.Count,
            ["modifiedFiles"] = Array(modified),
            ["mainViChanged"] = File.Exists(mainVi) && new FileInfo(mainVi).Length != mainBefore,
            ["mainViExecState"] = mainExec,
            ["mainViExecStateBefore"] = mainExecBefore,
            ["testerExecStateBefore"] = testerExecBefore,
            ["testerExecState"] = testerExec,
            ["testerWiring"] = testerWiring,
            ["savedMembers"] = Array(saved),
            ["unsavedMembers"] = Array(unsaved),
            ["dialogsAnswered"] = dialogs,
            ["adoptedHelpers"] = Array(adopted),
            ["adoptedHelpersNote"] = adopted.Count == 0 ? null : AdoptedHelpersNote,
            ["projectHygiene"] =
                "This call's helpers and argument carriers stay in memory in the project's " +
                "application instance, and LabVIEW lists every such VI in the .lvproj at the " +
                "project's NEXT save - the carriers as entries whose file Delacor has already " +
                "deleted. Measured 2026-10-06. Close the project with lvai_close_active_project " +
                "AND projectPath, whose sweep removes both kinds.",
            ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
        };
        if (scripted.Count != expected)
            answer["warning"] =
                $"{scripted.Count} new .vi/.ctl in the module folder where a {typeName} has " +
                $"always produced {expected}. Read `createdFiles` before trusting this event.";
        foreach (var (key, value) in detail)
            answer[key] = value?.DeepClone();
        return Json.Document(answer);
    }

    /// <summary>
    /// What Delacor leaves for a person after scripting one event. Each event names its OWN
    /// placeholders even when Main.vi or the tester was already broken: the DQMH7 demo build of
    /// 2026-10-30 left a private request's loose call and a Round Trip's frame unnamed, because
    /// the "already broken" note used to replace them.
    /// </summary>
    internal static List<string> EventCompletion(string eventName, int typeIndex, int requestScoping,
        string roundTripBroadcastName, string bareModule, DqmhGeneration generation,
        bool addTesterButton, int? mainExecBefore, int? mainExec, int? testerExecBefore, int? testerExec)
    {
        var items = new List<string>();
        var v7 = generation == DqmhGeneration.V7;
        if (mainExec is 0)
        {
            var own = items.Count;
            if (typeIndex == 1)
                items.Add($"Main.vi: Delacor dropped a call to {eventName}.vi loose on its diagram " +
                          "with a #CodeNeeded comment and its arguments unwired, which leaves Main.vi " +
                          "broken until that call is placed where the module FIRES the broadcast and " +
                          "wired. Only the module's author knows where that is (docs/dqmh-scripting.md " +
                          "section 6.10).");
            else if (typeIndex == 0 && requestScoping == 1 && v7)
                items.Add($"Main.vi: DQMH 7.x dropped a call to {eventName}.vi loose on its diagram " +
                          $"with a #DQMH_CodeNeeded comment ('Drop this VI wherever you need to fire the " +
                          $"Private.{eventName} event') and its arguments unwired, which leaves Main.vi " +
                          "broken until that call is placed where the module SENDS the private request " +
                          "and wired (measured on 7.1.2).");
            else if (typeIndex is 2 or 3)
                items.Add($"Main.vi: Delacor's \"{eventName}\" message frame carries a Merge Errors " +
                          (v7 ? "and a Bundle By Name " : "") +
                          "with an unwired input, which leaves Main.vi broken until the frame gets its " +
                          "code. lvai_dqmh_place_handler puts a handler subVI there and wires that input " +
                          "(docs/dqmh-scripting.md section 9h).");
            if (items.Count > own && mainExecBefore is 0)
                items.Add("Main.vi was already NOT executable before this call as well - the item " +
                          "above is this event's own share, not the whole cause.");
            else if (items.Count == own && mainExecBefore is 0)
                items.Add("Main.vi was already NOT executable before this call, so this event " +
                          "did not cause it. The measured causes are an earlier Broadcast's or private " +
                          "request's loose #CodeNeeded call and an earlier reply frame's unwired " +
                          "Merge Errors input (docs/dqmh-scripting.md sections 9c, 9h).");
            else if (items.Count == own)
                items.Add("Main.vi is not executable after scripting, and this event type has " +
                          "never been measured to cause that - read its errors before going on.");
        }

        if (testerExec is 0)
        {
            var broadcast = typeIndex == 1 ? eventName : typeIndex == 3 ? roundTripBroadcastName : null;
            if (v7 && broadcast is { Length: > 0 })
                items.Add($"Test {bareModule} API.vi: DQMH 7.x gives every broadcast a tester frame - " +
                          $"here the one for {broadcast}" + (typeIndex == 3 ? ", the Round Trip's broadcast half" : "") +
                          " - whose Format Into String carries a #DQMH_CodeNeeded format string " +
                          "'Response Received - %s' with nothing wired to its %s, which leaves the " +
                          "tester NOT executable until broadcast data from the Event Data Node is " +
                          "wired there or the %s is removed (measured on 7.1.2; DQMH 5 did not do " +
                          "this). Each broadcast adds one such frame.");
            else if (!addTesterButton && typeIndex != 1)
                items.Add($"Test {bareModule} API.vi is not executable: with addTesterButton false " +
                          "Delacor still adds an event case to the tester, with no button and nothing " +
                          "wired (issue #112). Assign that case to a control in the tester, or delete " +
                          "it - or create the event with addTesterButton true.");
            else if (testerExecBefore is 0)
                items.Add($"Test {bareModule} API.vi was already NOT executable before this call; " +
                          "an earlier broadcast's Format Into String is the measured cause.");
            else
                items.Add($"Test {bareModule} API.vi is not executable - `testerWiring` says " +
                          "what was connected.");
        }
        return items;
    }

    /// <summary>
    /// The folder a before/after file comparison watches: the project's own folder, else the
    /// module folder the dry run named. Null when neither exists.
    /// </summary>
    internal static string? SnapshotRoot(string? projectPath, string? moduleFolder)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath ?? "");
        if (!string.IsNullOrEmpty(projectDirectory) && Directory.Exists(projectDirectory))
            return projectDirectory;
        return !string.IsNullOrEmpty(moduleFolder) && Directory.Exists(moduleFolder) ? moduleFolder : null;
    }

    /// <summary>Every case name in Main.vi's AIXML export - the input of the frame-name check.</summary>
    internal async Task<(List<string> Frames, int ExportErrorCode)> MainViCaseNamesAsync(
        string mainVi, int timeoutSeconds, CancellationToken ct)
    {
        var (aixml, errorCode, _) = await ExportAsync(mainVi, "dqmh-main", timeoutSeconds, ct);
        return (aixml is null ? [] : CaseNames(aixml), errorCode);
    }

    /// <summary>
    /// A VI's AIXML, as text - NI's ConvertVIToAIXML on grpc, our reader (vi2aixml.py) on
    /// pylabview. The reader writes a case frame's selector in NI's spelling
    /// (`&amp;quot;Initialize&amp;quot;`), measured 2026-10-13 on a DQMH Main.vi, and reads
    /// Delacor's locked VIs as well, constants included. Null text with the error code and message
    /// when the export failed.
    /// </summary>
    internal Task<(string? Aixml, int ErrorCode, string Message)> ExportAsync(
        string vi, string tag, int timeoutSeconds, CancellationToken ct) =>
        BackendRoute.ExportAsync(connection, vi, tag, timeoutSeconds, ct);

    internal const string ProjectHygieneNote =
        "This call's helpers stay in memory in the project's application instance, and LabVIEW " +
        "lists every such VI in the .lvproj at the project's NEXT save. Close the project with " +
        "lvai_close_active_project AND projectPath, whose sweep removes them.";

    private const string TesterHelper = "lvdqmh_wire_tester_event";
    private const string FillHelper = "lvbd_fill_unwired_inputs";

    /// <summary>
    /// The tester helper is TWO VIs - the DQMH-specific traversal and a generic
    /// `lvbd_fill_unwired_inputs.vi` it calls per node, split because one VI came out at 28
    /// dependency stages. The caller's `Call` resolves only once the subVI is LOADED, so the
    /// order is: generate the subVI, open it through the active project, generate the caller.
    /// Null when both are ready.
    /// </summary>
    private async Task<string?> EnsureTesterHelperAsync(
        string scripts, string? projectPath, JsonArray steps, int timeoutSeconds, CancellationToken ct)
    {
        var fillXml = Path.Combine(scripts, FillHelper + ".xml");
        var fillVi = Path.Combine(DqmhTools.HelperDirectory(), FillHelper + ".vi");
        if (!File.Exists(fillXml) || !File.Exists(Path.Combine(scripts, TesterHelper + ".xml")))
            return $"{FillHelper}.xml or {TesterHelper}.xml is missing from the scripts folder";
        if (DqmhTools.OnPyLabview)
            // the fill helper composed first, so the tester helper's Call finds it by file
            return await EnsureWrapperAsync(scripts, TesterHelper, [], projectPath, steps,
                timeoutSeconds, ct, subHelpers: [FillHelper]) is { } composeFailure
                ? $"{TesterHelper} could not be composed - {composeFailure}"
                : null;
        if (HelperCache.NeedsRebuild(fillXml, fillVi))
        {
            var generated = await GenerateAsync(FillHelper, fillXml, fillVi, timeoutSeconds, ct);
            steps.Add(generated);
            if (generated["errorCode"]?.GetValue<int>() is not 0 || !File.Exists(fillVi))
                return $"{FillHelper} could not be generated: {generated["errorMessage"]}";
        }
        return await EnsureWrapperAsync(scripts, TesterHelper, [fillVi], projectPath, steps,
            timeoutSeconds, ct) is { } failure
            ? $"{TesterHelper} could not be generated - {failure}"
            : null;
    }
    private const string DirtyHelper = "lvdqmh_dirty_members";

    /// <summary>
    /// Which members of the module carry unsaved changes - Delacor's own `Is Project Item
    /// Dirty.vi` test, the front-panel and block-diagram modification bitsets - and with
    /// <paramref name="save"/> each of them saved in place. Read in the ACTIVE project's
    /// application instance, where the scripting left them.
    /// </summary>
    /// <summary>
    /// The VIs and controls IN THE PROJECT FOLDER that carry unsaved changes - library members
    /// (`UTPump.lvlib:Main.vi`) and loose files alike (`Test UTPump API.vi`, unit tests). The
    /// module-member pattern alone was not enough: the API tester is not a library member, it was
    /// left unsaved after a rename, and Delacor's next project save raised a MODAL over it. Our own
    /// helpers and everything outside the project folder - vi.lib, Delacor's scripting VIs - are
    /// never in this set, so nothing outside the user's project is ever saved by these tools.
    /// </summary>
    internal static async Task<(List<string>? Dirty, string? Error)> ProjectDirtyAsync(
        DqmhTools dqmh, string scripts, string? projectPath, int timeoutSeconds, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(projectPath ?? "");
        if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory)) return ([], null);
        var (all, error) = await DirtyMembersAsync(dqmh, scripts, ".", save: false, timeoutSeconds, ct);
        if (all is null) return (null, error);
        var files = System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ([.. all.Where(name => InProject(name, files))], null);
    }

    /// <summary>A qualified name counts by its outermost library's file, a loose VI by its own.</summary>
    internal static bool InProject(string name, IReadOnlySet<string> files) =>
        !IsHelperName(name) && files.Contains(name.Contains(':') ? name.Split(':')[0] : name);

    internal static bool IsHelperName(string name) =>
        name.StartsWith("LVMCP", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("lvdqmh_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("lvai_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("lvbd_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Save exactly these, one exact-name pattern each; returns what was saved.</summary>
    internal static async Task<List<string>> SaveNamedAsync(
        DqmhTools dqmh, string scripts, IEnumerable<string> names, int timeoutSeconds, CancellationToken ct)
    {
        var saved = new List<string>();
        foreach (var name in names)
        {
            var (members, _) = await DirtyMembersAsync(dqmh, scripts,
                "^" + MatchPatternEscape(name) + "$", save: true, timeoutSeconds, ct);
            if (members is not null && members.Contains(name, StringComparer.Ordinal)) saved.Add(name);
        }
        return saved;
    }

    internal static string UnsavedBeforeMessage(IEnumerable<string> dirty) =>
        "These VIs in the project have unsaved changes: " + string.Join(", ", dirty) + ". Save or " +
        "revert them first. Delacor's scripters save the project as they go, and LabVIEW answers an " +
        "unsaved VI there with a MODAL dialog; the tools answer that dialog themselves only when " +
        "what it lists can be nothing but their own scripted changes.";

    internal static async Task<(List<string>? Members, string? Error)> DirtyMembersAsync(
        DqmhTools dqmh, string scripts, string pattern, bool save, int timeoutSeconds,
        CancellationToken ct)
    {
        var run = await dqmh.RunAsync(scripts, DirtyHelper, new()
        {
            ["name pattern"] = pattern,
            ["save?"] = save ? "true" : "false",
        }, timeoutSeconds, ct);
        if (run is null) return (null, $"{DirtyHelper}.xml is missing");
        return DqmhTools.Failed(run) is { } error ? (null, error) : (Listed(run, "dirty VIs"), null);
    }

    /// <summary>
    /// Escape a literal for LabVIEW's Match Pattern, whose special characters are
    /// `. * + ? [ ] ^ $ \ ~` and whose escape is a backslash.
    /// </summary>
    internal static string MatchPatternEscape(string literal) =>
        string.Concat(literal.Select(c => ".*+?[]^$\\~".Contains(c) ? "\\" + c : c.ToString()));

    /// <summary>
    /// The inputs worth sending. The run helper pairs names and values BY POSITION and an empty
    /// value does not survive its split (RunTools, measured 2026-08-27), so empty ones are left
    /// out - the helper opens the wrapper fresh from disk, where every control holds its default,
    /// and the defaults are the empty values.
    /// </summary>
    internal static Dictionary<string, string> Sendable(Dictionary<string, string> inputs) =>
        inputs.Where(i => i.Value.Length > 0).ToDictionary(i => i.Key, i => i.Value);

    private static JsonObject RunStep(string step, string module, IReadOnlyList<LvValuesXml.Value> run) =>
        new()
        {
            ["step"] = step,
            ["moduleName"] = module,
            ["moduleIndex"] = DqmhTools.Scalar(run, "Module Index"),
            ["scripted"] = DqmhTools.Scalar(run, "Scripted?"),
        };
}
