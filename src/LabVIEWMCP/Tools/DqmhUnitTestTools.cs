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

            if (DqmhHeadless.TargetPaths(Targets) is not { } targets)
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
            var headless = new DqmhHeadless(connection);
            if (await headless.EnsureWrapperAsync(scripts, HelperName, targets, projectPath, steps,
                    timeoutSeconds, ct) is { } wrapperError)
                return wrapperError;

            // ---- 2. run, then retry ONCE with Delacor's own spelling ---------------------------
            var module = DqmhHeadless.DelacorModuleName(moduleName);
            var evt = DqmhHeadless.DelacorEventName(eventName);
            var before = DqmhHeadless.Snapshot.TakeOf(projectPath);

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

            var answer = new JsonObject
            {
                ["ok"] = created.Count > 0,
                ["module"] = module,
                ["event"] = evt,
                ["createdFiles"] = createdJson,
                ["modifiedFiles"] = DqmhHeadless.Array(modified),
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
            return Json.Document(answer);
        });

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
