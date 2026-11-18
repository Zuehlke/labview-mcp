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
        DQMH: create Delacor's unit test for ONE request event of a module (Tools > DQMH > New
        DQMH Unit Test) with no dialog - safe unattended. MUTATING. Writes
        `Test - <Module> - <Event> <n>.vi`, `<Module> setup.vi` and `<Module> teardown.vi` under
        `<project folder>\Unit Tests\<Module>\`; Delacor lists them in the .lvproj itself.
        The test VI is NOT executable as Delacor writes it (`#CodeNeeded`) - unless you give
        `argumentsJson` and `expectedJson`: then it is FILLED (request inputs as constants, each
        expected reply field compared, `timed out?` FALSE, all ANDed into `Pass`) and regenerated
        over the same path; `runTest` then runs setup, test and teardown and answers `pass`. Each
        VI's exec state is reported. Only REQUEST events qualify; an unknown name answers
        `eventNotFound` with the module's request events. Names bare or Delacor-spelt (`Heater` /
        `Heater.lvlib`), case-insensitive.
        Requires a project OPEN and ACTIVE and no unsaved changes in any module. Opens four of
        Delacor's scripting VIs and leaves them open.
        """)]
    public async Task<string> NewUnitTestAsync(
        [Description("DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Request event to test, e.g. 'Do Something' or 'Do Something.vi'")]
        string eventName,
        [Description("Local budget in seconds; the scripting itself takes about a second")]
        int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        [Description("""
            Fill the test: the request's arguments as a JSON object of name -> value text,
            e.g. {"Item Name":"Washer","Quantity":"12"}. An argument left out keeps its default.
            """)]
        string? argumentsJson = null,
        [Description("""
            Fill the test: the reply fields to compare, name -> expected value text, e.g.
            {"Found":"true"}. Needed to fill; a plain Request has no reply and cannot be filled.
            """)]
        string? expectedJson = null,
        [Description("After filling: run setup.vi, the test and teardown.vi and answer `pass`")]
        bool runTest = false,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_new_unit_test", async () =>
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                return Json.Error("badArguments", "moduleName is required.");
            if (string.IsNullOrWhiteSpace(eventName))
                return Json.Error("badArguments", "eventName is required.");
            Dictionary<string, string> fillArguments, fillExpected;
            try
            {
                fillArguments = DqmhUnitTestFill.ParseValues(argumentsJson, "argumentsJson");
                fillExpected = DqmhUnitTestFill.ParseValues(expectedJson, "expectedJson");
            }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }
            if (fillArguments.Count > 0 && fillExpected.Count == 0)
                return Json.Error("badArguments",
                    "argumentsJson without expectedJson fills nothing a test can check - give the reply fields to compare.");
            if (runTest && fillExpected.Count == 0)
                return Json.Error("badArguments", "runTest needs a filled test - give expectedJson.");

            if (StatusTools.ScriptsDirectory() is not { } scripts
                || !File.Exists(Path.Combine(scripts, HelperName + ".xml")))
                return Json.Error("scriptsMissing",
                    $"{HelperName}.xml was not found in the scripts folder next to the exe - " +
                    "lvai_status reports that folder as scriptsDirectory.");

            if (DqmhLayout.Resolve(scripts, HelperName, Targets) is not { } route)
                return Json.Error("dqmhMissing", DqmhLayout.MissingMessage("Script Unit Test.vi"),
                    new { lookedFor = Targets });
            var (targets, helperName) = (route.Targets, route.Helper);

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
            var headless = new DqmhHeadless(connection);
            if (await headless.EnsureWrapperAsync(scripts, helperName, targets, projectPath, steps,
                    timeoutSeconds, ct) is { } wrapperError)
                return wrapperError;

            // ---- 1b. a fill is checked against the request VI BEFORE anything is scripted --------
            if (fillExpected.Count > 0 && projectPath is not null)
            {
                var requestFolder = DqmhFrameTools.ModuleFolder(projectPath, moduleName);
                var requestVi = requestFolder is null ? null
                    : Path.Combine(requestFolder, DqmhHeadless.DelacorEventName(eventName));
                if (requestVi is not null && File.Exists(requestVi))
                {
                    var (requestXml, _, _) = await headless.ExportAsync(requestVi, "dqmh-request", timeoutSeconds, ct);
                    if (ViTerminals.Parse(requestXml) is { } requestTerminals)
                    {
                        try
                        {
                            DqmhUnitTestFill.CheckNames(requestTerminals,
                                Path.GetFileNameWithoutExtension(requestVi), fillArguments, fillExpected);
                        }
                        catch (ArgumentException bad)
                        {
                            return Json.Error("badArguments", bad.Message + " Nothing was scripted.",
                                new { requestVi });
                        }
                    }
                }
            }

            // ---- 2. run, then retry ONCE with Delacor's own spelling ---------------------------
            var module = DqmhHeadless.DelacorModuleName(moduleName);
            var evt = DqmhHeadless.DelacorEventName(eventName);
            var before = DqmhHeadless.Snapshot.TakeOf(projectPath);

            // This wrapper scripts on its first matching run, so the project-wide precondition of
            // the dialog watch is checked before ANY run: nothing in the project may be unsaved.
            var (dirtyBefore, dirtyError) = await DqmhHeadless.ProjectDirtyAsync(
                dqmh, scripts, projectPath, timeoutSeconds, ct);
            if (dirtyError is not null)
                return Json.Error("dirtyCheckFailed",
                    "Could not read which VIs of the project have unsaved changes.",
                    new { error = dirtyError, steps });
            if (dirtyBefore is { Count: > 0 })
                return Json.Error("unsavedChangesInProject",
                    DqmhHeadless.UnsavedBeforeMessage(dirtyBefore),
                    new { unsaved = dirtyBefore, steps });

            IReadOnlyList<LvValuesXml.Value>? run = null;
            var runs = 0;
            var watch = DqmhDialogWatch.Start();
            JsonArray dialogs;
            try
            {
            while (true)
            {
                run = await dqmh.RunAsync(scripts, helperName,
                    new() { ["Module Name"] = module, ["Event Name"] = evt }, timeoutSeconds, ct);
                runs++;
                if (run is null)
                    return Json.Error("helperMissing",
                        $"{helperName} could not be run - the wrapper or lvai_run_and_read is missing.",
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
            }
            finally
            {
                dialogs = watch.Events();
                await watch.DisposeAsync();
            }

            var moduleNames = DqmhHeadless.Listed(run, "Module Names");
            var eventNames = DqmhHeadless.Listed(run, "Event Names");
            var detail = new JsonObject
            {
                ["moduleNames"] = DqmhHeadless.Array(moduleNames),
                ["requestEvents"] = DqmhHeadless.Array(eventNames),
                ["runs"] = runs,
                ["steps"] = steps,
            };

            if (DqmhTools.Failed(run) is { } error)
                return Json.Error("scriptingFailed",
                    "Delacor's scripting answered an error - `error` is its cluster.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["error"] = error }));
            if (DqmhHeadless.IsTrue(DqmhTools.Scalar(run, "Any Dirty Modules?")))
                return Json.Error("modulesHaveUnsavedChanges",
                    "A DQMH module in this project has unsaved changes. Delacor refuses to script " +
                    "until everything is saved, typedef changes included; nothing was written.",
                    detail);
            if (moduleNames.Count == 0)
                return Json.Error("noDqmhModules",
                    "The active project holds no DQMH module that Delacor recognises.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["activeProject"] = projectPath }));
            if (DqmhHeadless.Index(run, "Module Index") < 0)
                return Json.Error("moduleNotFound",
                    $"No DQMH module '{moduleName}' in the active project - `moduleNames` lists them.",
                    detail);
            if (DqmhHeadless.Index(run, "Event Index") < 0)
                return Json.Error("eventNotFound",
                    $"Module {module} has no REQUEST event '{eventName}' - `requestEvents` lists " +
                    "them. Delacor creates unit tests for request events only.",
                    detail);

            // ---- 3. verify from the files -------------------------------------------------------
            List<string> created = [], modified = [];
            if (before is not null)
                (created, modified) = before.Diff(DqmhHeadless.Snapshot.TakeOf(projectPath)!);
            var createdJson = await headless.DescribeAsync(created, timeoutSeconds, ct);
            var adopted = DqmhHeadless.AdoptedHelpersOf(projectPath);
            // Whatever the scripting left unsaved in the project is its own work - save it.
            var (dirtyAfter, _) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts, projectPath,
                timeoutSeconds, ct);
            var saved = await DqmhHeadless.SaveNamedAsync(dqmh, scripts, dirtyAfter ?? [],
                timeoutSeconds, ct);

            var answer = new JsonObject
            {
                ["ok"] = created.Count > 0,
                ["dqmhGeneration"] = route.GenerationName,
                ["module"] = module,
                ["event"] = evt,
                ["createdFiles"] = createdJson,
                ["modifiedFiles"] = DqmhHeadless.Array(modified),
                ["savedMembers"] = DqmhHeadless.Array(saved),
                ["dialogsAnswered"] = dialogs,
                ["adoptedHelpers"] = DqmhHeadless.Array(adopted),
                ["adoptedHelpersNote"] = adopted.Count == 0 ? null : DqmhHeadless.AdoptedHelpersNote,
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

            if (fillExpected.Count > 0 && created.Count > 0 && projectPath is not null)
            {
                var fill = await FillAsync(projectPath, module, evt, created, fillArguments, fillExpected, runTest,
                                           timeoutSeconds, ct);
                answer["fill"] = fill;
                if (fill["ok"]?.GetValue<bool>() != true) answer["ok"] = false;
                if (fill["pass"] is { } pass) answer["pass"] = pass.DeepClone();
                answer["note"] = fill["note"]?.DeepClone();
            }
            return Json.Document(answer);
        }));

    /// <summary>
    /// Fills the test VI Delacor just wrote and regenerates it over its own path. The test VI is
    /// loaded in the project's instance, so the generation's pane step (4815, Delacor's pane)
    /// closes the project first; it is opened again afterwards, because the run and the next DQMH
    /// call need it active.
    /// </summary>
    private async Task<JsonObject> FillAsync(string projectPath, string module, string evt, List<string> created,
        Dictionary<string, string> arguments, Dictionary<string, string> expected, bool run,
        int timeoutSeconds, CancellationToken ct)
    {
        var step = new JsonObject { ["step"] = "fill" };
        var testVi = created.FirstOrDefault(f => Path.GetFileName(f).StartsWith("Test - ", StringComparison.OrdinalIgnoreCase)
                                                 && f.EndsWith(".vi", StringComparison.OrdinalIgnoreCase));
        var folder = DqmhFrameTools.ModuleFolder(projectPath, module);
        var requestVi = folder is null ? null : Path.Combine(folder, evt);
        if (testVi is null || requestVi is null || !File.Exists(requestVi))
        {
            step["ok"] = false;
            step["note"] = "The test VI or the request VI was not found, so the test was not filled.";
            step["testVi"] = testVi;
            step["requestVi"] = requestVi;
            return step;
        }
        step["testVi"] = testVi;
        var headless = new DqmhHeadless(connection);
        var (testXml, _, testError) = await headless.ExportAsync(testVi, "dqmh-unit-test", timeoutSeconds, ct);
        var (requestXml, _, requestError) = await headless.ExportAsync(requestVi, "dqmh-request", timeoutSeconds, ct);
        if (testXml is null || ViTerminals.Parse(requestXml) is not { } request)
        {
            step["ok"] = false;
            step["note"] = $"The test or the request VI could not be read: {testError ?? requestError}.";
            return step;
        }
        string filled;
        try { filled = DqmhUnitTestFill.Fill(testXml, Path.GetFileNameWithoutExtension(evt), request, arguments, expected); }
        catch (ArgumentException bad)
        {
            step["ok"] = false;
            step["errorKind"] = "badArguments";
            step["note"] = bad.Message + " The test VI was left as Delacor wrote it.";
            return step;
        }
        var source = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", $"dqmh-unit-test-filled.{Guid.NewGuid():N}.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, filled, ct);
        // THE PROJECT IS CLOSED BEFORE THE TEST VI IS WRITTEN, and the VI is opened THROUGH the
        // project before its state is read. Measured 2026-11-18: read the other way round, a filled
        // test that ran and passed answered execState 0 - it calls `Inventory.lvlib:...`, and a VI
        // read outside the project's instance does not see the library the project holds.
        var closed = await new CloseTools(connection).CloseActiveProjectAsync(
            projectPath: Path.GetFullPath(projectPath), timeoutSeconds: timeoutSeconds, ct: ct,
            backend: BackendRoute.Current);
        step["closeProject"] = (JsonNode.Parse(closed) as JsonObject)?["closed"]?.DeepClone();
        string generated;
        try
        {
            generated = await new BulkTools(connection).GenerateViAsync(source, testVi, panePattern: 4815,
                timeoutSeconds: timeoutSeconds, ct: ct, measureDiagram: true, icon: "none", cleanUpPanel: false);
        }
        finally { if (File.Exists(source)) File.Delete(source); }
        var generation = JsonNode.Parse(generated) as JsonObject;
        var opened = JsonNode.Parse(await new ActionTools(connection).OpenFileAsync(
            viPath: testVi, viName: Path.GetFileName(testVi), projectPath: Path.GetFullPath(projectPath),
            projectName: Path.GetFileName(projectPath), checkActive: true, timeoutSeconds: timeoutSeconds,
            ct: ct)) as JsonObject;
        var reopenFailure = opened?["projectBecameActive"]?.GetValue<bool>() == true ? null : "project not active";
        var exec = await headless.ExecStateAsync(testVi, timeoutSeconds, ct);
        step["generated"] = generation?["ok"]?.DeepClone();
        step["execState"] = exec;
        step["projectActive"] = reopenFailure is null;
        if (generation?["ok"]?.GetValue<bool>() != true || exec != 1)
        {
            step["ok"] = false;
            step["generation"] = generation?.DeepClone();
            step["note"] = "The filled test VI did not come out executable - `generation` holds the generator's answer.";
            return step;
        }

        if (run)
        {
            var runs = new JsonArray();
            var runner = new RunTools(connection);
            var dir = Path.GetDirectoryName(testVi)!;
            bool? pass = null;
            foreach (var vi in new[] { Path.Combine(dir, module.Replace(".lvlib", "") + " setup.vi"), testVi,
                                       Path.Combine(dir, module.Replace(".lvlib", "") + " teardown.vi") })
            {
                if (!File.Exists(vi)) { runs.Add(new JsonObject { ["vi"] = vi, ["ran"] = false, ["why"] = "not on disk" }); continue; }
                var answer = JsonNode.Parse(await runner.RunViAndReadValuesAsync(vi, timeoutSeconds: timeoutSeconds,
                    ct: ct, nativeValues: true, includeXml: false)) as JsonObject;
                var entry = RunBatch.CompactRun(runs.Count, new JsonObject(), answer);
                entry["vi"] = Path.GetFileName(vi);
                if (vi == testVi && entry["values"]?["Pass"] is JsonValue p && p.TryGetValue<bool>(out var b)) pass = b;
                runs.Add(entry);
            }
            step["runs"] = runs;
            step["pass"] = pass;
        }
        step["ok"] = true;
        step["note"] = run
            ? (step["pass"]?.GetValue<bool?>() == true
                ? "Filled, executable and PASSED: setup, test and teardown ran."
                : "Filled and executable, but the test did NOT pass - read `runs` (the test's error out and Pass).")
            : "Filled and executable. Run it with setup.vi before and teardown.vi after, or pass runTest: true.";
        return step;
    }

    /// <summary>
    /// The names to retry with when the first run matched nothing only because of spelling, or
    /// null when a retry would change nothing.
    /// </summary>
    private static (string Module, string Event)? Respell(
        string module, string evt, IReadOnlyList<LvValuesXml.Value> run)
    {
        if (DqmhHeadless.Index(run, "Module Index") < 0)
        {
            var listed = DqmhHeadless.FindListed(DqmhHeadless.Listed(run, "Module Names"), module);
            return listed is null || listed == module ? null : (listed, evt);
        }
        if (DqmhHeadless.Index(run, "Event Index") < 0)
        {
            var listed = DqmhHeadless.FindListed(DqmhHeadless.Listed(run, "Event Names"), evt);
            return listed is null || listed == evt ? null : (module, listed);
        }
        return null;
    }
}
