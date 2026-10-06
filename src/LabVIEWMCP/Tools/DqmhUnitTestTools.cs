using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a DQMH unit test with NO dialog - the first DQMH scripter driven headless.
///
/// WHY THIS NEEDS NO DIALOG, WHEN lvai_dqmh_new_event DOES. Every Delacor menu function is "parse
/// the project, pick a module, script, close" (docs/dqmh-scripting.md section 9), and the scripter
/// needs `Module Info`, whose thirteen refnums die when a parse run as its own top-level VI stops.
/// The dialog was the only caller that kept parse and scripter in ONE hierarchy - until a generated
/// wrapper did the same. Measured 2026-10-06: an AIXML `Call` to a project-library member such as
/// `DQMH New Unit Test.lvlib:Script Unit Test.vi` resolves once that VI is OPEN in LabVIEW, the
/// wrapper converts with the links written into the file, and one run scripted the unit test with
/// `error out` 0. No latched button, no keystroke, no foreground - unattended-safe.
///
/// WHAT DELACOR'S DIALOG CONTRIBUTED is two ring choices, so the names are matched here instead:
/// the wrapper searches exactly, and this tool retries with the spelling the wrapper reported
/// (`UTPump.lvlib`, `Do Something.vi`) when the caller gave a bare or differently-cased name. A run
/// that matches nothing scripts nothing - measured, no file touched - so the retry is free.
/// </summary>
[McpServerToolType]
internal sealed class DqmhUnitTestTools(LvaiConnection connection)
{
    private const string HelperName = "lvdqmh_new_unit_test";

    /// <summary>
    /// The four Delacor VIs the wrapper calls, relative to the LabVIEW installation. They must be
    /// OPEN when the wrapper is converted, or the converter answers Error 53 naming them.
    /// </summary>
    internal static readonly string[] Targets =
    [
        @"project\Delacor\DQMH\_DQMH New Event\Parse Project for DQMH Modules.vi",
        @"project\Delacor\DQMH\_DQMH New Event\Close Scripting References.vi",
        @"project\Delacor\DQMH\_DQMH New Unit Test\Get All Events in Module.vi",
        @"project\Delacor\DQMH\_DQMH New Unit Test\Script Unit Test.vi",
    ];

    [McpServerTool(Name = "lvai_dqmh_new_unit_test", Destructive = true, OpenWorld = true,
        Title = "Create a DQMH unit test")]
    [Description("""
        MUTATING: creates Delacor's unit test for ONE request event of a DQMH module in the ACTIVE
        project - the result of Tools > DQMH > Testing Tools > New DQMH Unit Test, with NO dialog
        and NO keystroke, so it is safe unattended. Writes three VIs under
        `<project folder>\Unit Tests\<Module>\` - `Test - <Module> - <Event> <n>.vi`,
        `<Module> setup.vi`, `<Module> teardown.vi` - and Delacor saves the .lvproj itself with a
        `Unit Tests` folder listing them.

        THE TEST VI IS NOT EXECUTABLE WHEN IT IS CREATED, and that is Delacor's design, not a
        failure: its event frames carry `#CodeNeeded` notes asking for the module's broadcasts to be
        configured, and Delacor's own template is broken the same way (measured). The answer
        reports each created VI's exec state so this is visible rather than discovered.

        ONLY REQUEST EVENTS get a unit test - Delacor's own filter. A name not found comes back as
        `eventNotFound` with the request events the module has.

        Names may be given bare (`Heater`, `Do Something`) or as Delacor spells them
        (`Heater.lvlib`, `Do Something.vi`); case does not matter. Requires a project OPEN AND
        ACTIVE and no unsaved changes in any module (Delacor refuses those, and so does this).

        How: opens four of Delacor's scripting VIs (unmodified, left open) so the generated wrapper
        `scripts/lvdqmh_new_unit_test.xml` can call them as static subVIs, then runs it.
        """)]
    public async Task<string> NewUnitTestAsync(
        [Description("DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Request event to test, e.g. 'Do Something' or 'Do Something.vi'")]
        string eventName,
        [Description("Local budget in seconds; the scripting itself takes about a second")]
        int timeoutSeconds = 600,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                return Json.Error("badArguments", "moduleName is required.");
            if (string.IsNullOrWhiteSpace(eventName))
                return Json.Error("badArguments", "eventName is required.");

            if (StatusTools.ScriptsDirectory() is not { } scripts
                || !File.Exists(Path.Combine(scripts, HelperName + ".xml")))
                return Json.Error("scriptsMissing",
                    $"{HelperName}.xml was not found in the scripts folder next to the exe - " +
                    "lvai_status reports that folder as scriptsDirectory.");

            if (TargetPaths() is not { } targets)
                return Json.Error("dqmhMissing",
                    "Delacor DQMH is not installed: Script Unit Test.vi was not found under any " +
                    "LabVIEW installation's project\\Delacor\\DQMH folder.",
                    new { lookedFor = Targets });

            var stopwatch = Stopwatch.StartNew();
            var steps = new JsonArray();

            var (active, activeNote, projectPath) =
                await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
            if (active is false)
                return Json.Error("noActiveProject",
                    "No project is active. Delacor's scripting works on the ACTIVE project - open " +
                    "it with lvai_open_file (projectPath + projectName) first.",
                    new { activeProjectCheck = activeNote });

            // ---- 1. the wrapper, generated against LOADED Delacor VIs --------------------------
            var dqmh = new DqmhTools(connection);
            var helperVi = Path.Combine(DqmhTools.HelperDirectory(), HelperName + ".vi");
            var aixml = Path.Combine(scripts, HelperName + ".xml");
            if (HelperCache.NeedsRebuild(aixml, helperVi))
            {
                var opened = await OpenTargetsAsync(targets, projectPath, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "openDelacorVIs", ["opened"] = opened });

                var generated = await GenerateAsync(aixml, helperVi, timeoutSeconds, ct);
                steps.Add(generated);
                if (generated["errorCode"]?.GetValue<int>() is not 0 || !File.Exists(helperVi))
                    return Json.Error("wrapperGenerationFailed",
                        "The wrapper could not be generated. Error 53 naming a Delacor VI means it " +
                        "was not loaded - see `opened` for each open's own error code.",
                        new { steps, helperVi });
            }

            // ---- 2. run, then retry ONCE with Delacor's own spelling ---------------------------
            var module = DelacorModuleName(moduleName);
            var evt = DelacorEventName(eventName);
            Snapshot? before = null;
            var projectDirectory = Path.GetDirectoryName(projectPath ?? "");
            if (!string.IsNullOrEmpty(projectDirectory) && Directory.Exists(projectDirectory))
                before = Snapshot.Take(projectDirectory);

            IReadOnlyList<LvValuesXml.Value>? run = null;
            var runs = 0;
            while (true)
            {
                run = await dqmh.RunAsync(scripts, HelperName,
                    new() { ["Module Name"] = module, ["Event Name"] = evt }, timeoutSeconds, ct);
                runs++;
                if (run is null)
                    return Json.Error("helperMissing",
                        $"{HelperName} could not be run - the wrapper or lvai_run_and_read is missing.",
                        new { steps });
                steps.Add(new JsonObject
                {
                    ["step"] = "run",
                    ["moduleName"] = module,
                    ["eventName"] = evt,
                    ["moduleIndex"] = DqmhTools.Scalar(run, "Module Index"),
                    ["eventIndex"] = DqmhTools.Scalar(run, "Event Index"),
                });
                // Three runs at most: the module's spelling, then the event's, then the real one.
                if (runs > 2 || DqmhTools.Failed(run) is not null) break;

                var retry = Respell(module, evt, run);
                if (retry is null) break;
                (module, evt) = retry.Value;
            }

            var moduleNames = Listed(run, "Module Names");
            var eventNames = Listed(run, "Event Names");
            var detail = new JsonObject
            {
                ["moduleNames"] = new JsonArray([.. moduleNames.Select(n => JsonValue.Create(n))]),
                ["requestEvents"] = new JsonArray([.. eventNames.Select(n => JsonValue.Create(n))]),
                ["runs"] = runs,
                ["steps"] = steps,
            };

            if (DqmhTools.Failed(run) is { } error)
                return Json.Error("scriptingFailed",
                    "Delacor's scripting answered an error - `error` is its cluster.",
                    Merge(detail, new JsonObject { ["error"] = error }));
            if (IsTrue(DqmhTools.Scalar(run, "Any Dirty Modules?")))
                return Json.Error("modulesHaveUnsavedChanges",
                    "A DQMH module in this project has unsaved changes. Delacor refuses to script " +
                    "until everything is saved, typedef changes included; nothing was written.",
                    detail);
            if (moduleNames.Count == 0)
                return Json.Error("noDqmhModules",
                    "The active project holds no DQMH module that Delacor recognises.",
                    Merge(detail, new JsonObject { ["activeProject"] = projectPath }));
            if (Index(run, "Module Index") < 0)
                return Json.Error("moduleNotFound",
                    $"No DQMH module '{moduleName}' in the active project - `moduleNames` lists them.",
                    detail);
            if (Index(run, "Event Index") < 0)
                return Json.Error("eventNotFound",
                    $"Module {module} has no REQUEST event '{eventName}' - `requestEvents` lists " +
                    "them. Delacor creates unit tests for request events only.",
                    detail);

            // ---- 3. verify from the files -------------------------------------------------------
            List<string> created = [], modified = [];
            if (before is not null)
                (created, modified) = before.Diff(Snapshot.Take(projectDirectory!));
            var createdJson = new JsonArray();
            foreach (var file in created)
            {
                var entry = new JsonObject
                {
                    ["path"] = file,
                    ["bytes"] = new FileInfo(file).Length,
                };
                if (file.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
                    entry["execState"] = (JsonNode.Parse(await new ExecStateTools(connection)
                        .ExecStateAsync(file, timeoutSeconds: timeoutSeconds, ct: ct))
                        as JsonObject)?["execState"]?.DeepClone();
                createdJson.Add(entry);
            }

            var adopted = projectPath is { Length: > 0 } && File.Exists(projectPath)
                ? AdoptedHelpers(File.ReadAllText(projectPath))
                : [];

            var answer = new JsonObject
            {
                ["ok"] = created.Count > 0,
                ["module"] = module,
                ["event"] = evt,
                ["createdFiles"] = createdJson,
                ["modifiedFiles"] = new JsonArray([.. modified.Select(f => JsonValue.Create(f))]),
                ["adoptedHelpers"] = new JsonArray([.. adopted.Select(f => JsonValue.Create(f))]),
                ["adoptedHelpersNote"] = adopted.Count == 0 ? null
                    : "Delacor SAVED the project while this tool's helper VIs were open in it, so " +
                      "LabVIEW listed them in the .lvproj. Close the project with " +
                      "lvai_close_active_project AND projectPath - its sweep removes entries under " +
                      "%TEMP% - and never edit the .lvproj while LabVIEW holds it open.",
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] = created.Count > 0
                    ? "A test VI reading execState 0 is expected: Delacor leaves its event frames " +
                      "for the module's broadcasts to be configured (#CodeNeeded on its diagram). " +
                      "Delacor saved the .lvproj itself."
                    : "The wrapper reported success but no new file appeared under the project " +
                      "folder - read the unit test folder by hand before trusting this run.",
            };
            foreach (var (key, value) in detail)
                answer[key] = value?.DeepClone();
            return Json.Document(answer);
        });

    // ------------------------------------------------------------------ pure helpers, tested

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
    /// The names to retry with when the first run matched nothing only because of spelling, or
    /// null when a retry would change nothing.
    /// </summary>
    private static (string Module, string Event)? Respell(
        string module, string evt, IReadOnlyList<LvValuesXml.Value> run)
    {
        if (Index(run, "Module Index") < 0)
        {
            var listed = FindListed(Listed(run, "Module Names"), module);
            return listed is null || listed == module ? null : (listed, evt);
        }
        if (Index(run, "Event Index") < 0)
        {
            var listed = FindListed(Listed(run, "Event Names"), evt);
            return listed is null || listed == evt ? null : (module, listed);
        }
        return null;
    }

    /// <summary>
    /// A string array indicator's entries. An EMPTY array still flattens one blank element - the
    /// first acceptance run answered `requestEvents: [""]` for a module that was never selected -
    /// and no DQMH module or event has an empty name, so blanks are dropped.
    /// </summary>
    private static List<string> Listed(IReadOnlyList<LvValuesXml.Value> run, string name) =>
        [.. DqmhTools.Strings(run, name).Where(s => !string.IsNullOrWhiteSpace(s))];

    /// <summary>
    /// The project items that point into this server's helper directory. LabVIEW adopts every VI
    /// it has open in the project's application instance when the project is saved, and Delacor's
    /// scripting saves it - measured 2026-10-06: `lvai_run_and_read.vi` and the wrapper itself
    /// appeared in the fixture's .lvproj after one run. Read-only: the file may not be edited while
    /// LabVIEW holds the project.
    /// </summary>
    internal static List<string> AdoptedHelpers(string lvproj) =>
        [.. System.Text.RegularExpressions.Regex.Matches(lvproj,
                "<Item Name=\"([^\"]+)\"[^>]*URL=\"[^\"]*LabVIEWMCP/helpers/[^\"]*\"")
            .Select(m => m.Groups[1].Value)];

    private static int Index(IReadOnlyList<LvValuesXml.Value> run, string name) =>
        int.TryParse(DqmhTools.Scalar(run, name), out var index) ? index : -1;

    private static bool IsTrue(string? value) =>
        value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var merged = (JsonObject)a.DeepClone();
        foreach (var (key, value) in b) merged[key] = value?.DeepClone();
        return merged;
    }

    private static string[]? TargetPaths()
    {
        foreach (var install in LabViewLocator.Discover())
        {
            var root = Path.GetDirectoryName(install.ExePath) ?? "";
            var paths = Targets.Select(t => Path.Combine(root, t)).ToArray();
            if (paths.All(File.Exists)) return paths;
        }
        return null;
    }

    // ------------------------------------------------------------------ LabVIEW steps

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
        string aixml, string helperVi, int timeoutSeconds, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            $"{HelperName}.{Guid.NewGuid():N}.xml");
        var throwaway = $"LVMCP DQMH UT {Guid.NewGuid().ToString("N")[..8]}.vi";
        var text = File.ReadAllText(aixml);
        File.WriteAllText(scratch, text.Replace(
            $"_name=\"{HelperName}.vi\"", $"_name=\"{throwaway}\""));
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

    /// <summary>
    /// What is on disk under the project folder, so the answer can name what the scripting wrote
    /// instead of trusting a clean error cluster. Delacor's output folder is its own business; a
    /// before/after comparison does not need to know it.
    /// </summary>
    internal sealed class Snapshot
    {
        private readonly Dictionary<string, (DateTime Written, long Bytes)> _files;

        private Snapshot(Dictionary<string, (DateTime, long)> files) => _files = files;

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
    }
}
