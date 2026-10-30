using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Duplicate DQMH Event with no dialog - DQMH 7.x only (docs/dqmh-scripting.md section 10e).
///
/// Its own route rather than the shared edit route, for one reason: Delacor's `Duplicate Event.vi`
/// takes an INDEX into the event list as `Correct Round Trip Events in List.vi` leaves it - the
/// list the dialog's ring shows, a Round Trip's two halves folded into one entry - and a source
/// and a destination MODULE, which may differ. The wrapper lists that corrected list in a dry
/// run; the index is matched here by name and handed to the scripted run.
/// </summary>
[McpServerToolType]
internal sealed class DqmhDuplicateEventTools(LvaiConnection connection)
{
    private const string Helper = "lvdqmh_duplicate_event";

    internal static readonly string[] Targets =
    [
        DqmhLayout.Support + "Parse Project for DQMH Modules.vi",
        DqmhLayout.Support + "Close Scripting References.vi",
        DqmhLayout.Support + "Get All Events in Module.vi",
        @"project\Delacor\DQMH\_DQMH Duplicate Event\Correct Round Trip Events in List.vi",
        @"project\Delacor\DQMH\_DQMH Duplicate Event\Duplicate Event.vi",
    ];

    [McpServerTool(Name = "lvai_dqmh_duplicate_event", Destructive = true, OpenWorld = true,
        Title = "Duplicate a DQMH event")]
    [Description("""
        MUTATING, DQMH 7.x ONLY: copies ONE event of a DQMH module in the ACTIVE project under a new
        name - into the same module or into another one (destinationModuleName) - with NO dialog:
        Delacor's own Duplicate Event.vi through a generated wrapper, the result of Tools > DQMH >
        Event > Duplicate DQMH Event. Arguments, reply payload and type are copied; the description
        too, unless `description` replaces it. A Round Trip is copied as a pair and needs
        roundTripBroadcastName for the new broadcast half. The names are checked first the way
        Delacor's dialog checks them with a MODAL. `events` in a refusal lists what the source module
        offers, as Delacor's ring shows it. What Delacor leaves for a person - a Broadcast's loose
        #CodeNeeded call, a reply frame's Merge Errors, a tester case - is the same as for a new event
        and named in `completionNeeded`. Refused while anything in the project is unsaved or a module
        is locked. On DQMH 5/6 it answers `needsDqmh7`.
        """)]
    public async Task<string> DuplicateEventAsync(
        [Description("Source DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Event to copy, e.g. 'Set Speed'")] string eventName,
        [Description("Name of the copy")] string newEventName,
        [Description("Module to copy into; empty means the source module")] string destinationModuleName = "",
        [Description("Round Trip only, and required for it: the name of the new broadcast half")]
        string roundTripBroadcastName = "",
        [Description("Description of the copy; empty copies the source event's own")] string description = "",
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_duplicate_event", () =>
            DqmhTools.Routed(() => DuplicateAsync(moduleName, eventName, newEventName,
                destinationModuleName, roundTripBroadcastName, description, timeoutSeconds, ct))));

    private async Task<string> DuplicateAsync(string moduleName, string eventName, string newEventName,
        string destinationModuleName, string roundTripBroadcastName, string description,
        int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(eventName))
            return Json.Error("badArguments", "moduleName and eventName are required.");
        var newName = (newEventName ?? "").Trim();
        if (newName.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)) newName = newName[..^3];
        var broadcast = (roundTripBroadcastName ?? "").Trim();

        if (StatusTools.ScriptsDirectory() is not { } scripts
            || !File.Exists(Path.Combine(scripts, Helper + DqmhLayout.Suffix + ".xml")))
            return Json.Error("scriptsMissing", $"{Helper}{DqmhLayout.Suffix}.xml was not found in the scripts folder.");
        if (DqmhLayout.ResolveV7Only(scripts, Helper, Targets, ["lvdqmh_close_modules"],
                out var classicInstalled) is not { } route)
            return classicInstalled
                ? Json.Error("needsDqmh7",
                    "Duplicate Event is driven for DQMH 7.x only, and this station has the classic DQMH " +
                    "layout (5.x/6.x). Use Delacor's own menu entry there.")
                : Json.Error("dqmhMissing", DqmhLayout.MissingMessage("Duplicate Event.vi"),
                    new { lookedFor = Targets });

        var stopwatch = Stopwatch.StartNew();
        var steps = new JsonArray();
        var (active, activeNote, projectPath) =
            await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
        if (active is false)
            return Json.Error("noActiveProject",
                "No project is active. Delacor's scripting works on the ACTIVE project - open it " +
                "with lvai_open_file (projectPath + projectName) first.",
                new { activeProjectCheck = activeNote });

        var headless = new DqmhHeadless(connection);
        var dqmh = new DqmhTools(connection);
        if (await headless.EnsureWrapperAsync(scripts, route.Helper, route.Targets, projectPath, steps,
                timeoutSeconds, ct, route.SubHelpers) is { } wrapperError)
            return wrapperError;

        // ---- 1. dry run: both modules, the source's events as Delacor's ring lists them ------
        var module = DqmhHeadless.DelacorModuleName(moduleName);
        var destination = string.IsNullOrWhiteSpace(destinationModuleName)
            ? "" : DqmhHeadless.DelacorModuleName(destinationModuleName);
        Dictionary<string, string> Inputs(string script, int index) => DqmhHeadless.Sendable(new()
        {
            ["Module Name"] = module,
            ["Destination Module Name"] = destination,
            ["Event Index"] = index.ToString(),
            ["New Event Name"] = newName,
            ["Round Trip Broadcast Name"] = broadcast,
            ["Event Description"] = description ?? "",
            ["Script?"] = script,
        });
        var (dry, dryError) = await dqmh.RunDetailedAsync(scripts, route.Helper, Inputs("false", -1),
            timeoutSeconds, ct);
        if (dry is null) return Json.Error("helperMissing", $"{route.Helper} could not be run.", new { steps });
        if (dryError is not null)
            return Json.Error("helperFailed", "The run helper stopped before the wrapper ran.",
                new { helperError = dryError, steps });
        var moduleNames = DqmhHeadless.Listed(dry, "Module Names");
        // Delacor's own spelling, retried once for each module name
        var retry = false;
        if (DqmhHeadless.Index(dry, "Module Index") < 0
            && DqmhHeadless.FindListed(moduleNames, module) is { } listed && listed != module)
            (module, retry) = (listed, true);
        if (destination.Length > 0 && DqmhHeadless.Index(dry, "Destination Index") < 0
            && DqmhHeadless.FindListed(moduleNames, destination) is { } listedDestination
            && listedDestination != destination)
            (destination, retry) = (listedDestination, true);
        if (retry)
        {
            (dry, _) = await dqmh.RunDetailedAsync(scripts, route.Helper, Inputs("false", -1), timeoutSeconds, ct);
            if (dry is null) return Json.Error("helperMissing", $"{route.Helper} could not be run.", new { steps });
        }

        var ring = DqmhHeadless.Listed(dry, "Event Ring Strings");
        var names = DqmhHeadless.Listed(dry, "Event Names");
        var destinationFolder = DqmhTools.Scalar(dry, "Destination Folder") ?? "";
        var detail = new JsonObject
        {
            ["dqmhGeneration"] = route.GenerationName,
            ["wrapper"] = route.Helper,
            ["moduleNames"] = DqmhHeadless.Array(moduleNames),
            ["events"] = DqmhHeadless.Array(ring),
            ["steps"] = steps,
        };
        if (DqmhTools.Failed(dry) is { } parseError)
            return Json.Error("parseFailed", "Delacor's project parse answered an error.",
                DqmhHeadless.Merge(detail, new JsonObject { ["error"] = parseError }));
        if (DqmhHeadless.Index(dry, "Module Index") < 0)
            return Json.Error("moduleNotFound",
                $"No DQMH module '{moduleName}' in the active project - `moduleNames` lists them.", detail);
        if (DqmhHeadless.Index(dry, "Destination Index") < 0)
            return Json.Error("moduleNotFound",
                $"No DQMH module '{destinationModuleName}' to copy into - `moduleNames` lists them.", detail);
        if (DqmhHeadless.IsTrue(DqmhTools.Scalar(dry, "Any Locked Modules?")))
            return Json.Error("modulesLocked",
                "A DQMH module in this project is locked or read-only; nothing was written.", detail);

        var index = EventIndex(names, eventName);
        if (index < 0)
            return Json.Error("eventNotFound",
                $"Module {module} has no event '{eventName}' that Delacor's duplicate dialog offers - " +
                "`events` lists the ones it does.", detail);
        var type = TypeOf(ring[index]);
        if (DqmhHeadless.EventRequestProblem(newName, type, broadcast, [], []) is { } problem)
            return Json.Error("badArguments", type == 3 && broadcast.Length == 0
                ? $"'{names[index]}' is a Round Trip - roundTripBroadcastName names the copy's broadcast half."
                : problem.Replace("The event name", "newEventName"), detail);
        foreach (var name in type == 3 ? new[] { newName, broadcast } : [newName])
            if (destinationFolder.Length > 0 && File.Exists(Path.Combine(destinationFolder, name + ".vi")))
                return Json.Error("eventNameInUse",
                    $"The destination module's folder already holds '{name}.vi'. Delacor's own dialog " +
                    "refuses the same name.", detail);

        var (dirtyBefore, dirtyCheckError) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts, projectPath,
            timeoutSeconds, ct);
        if (dirtyCheckError is not null)
            return Json.Error("dirtyCheckFailed", "Could not read which VIs of the project have unsaved changes.",
                DqmhHeadless.Merge(detail, new JsonObject { ["error"] = dirtyCheckError }));
        if (dirtyBefore is { Count: > 0 })
            return Json.Error("unsavedChangesInProject", DqmhHeadless.UnsavedBeforeMessage(dirtyBefore),
                DqmhHeadless.Merge(detail, new JsonObject { ["unsaved"] = DqmhHeadless.Array(dirtyBefore) }));

        // ---- 2. script -------------------------------------------------------------------------
        var snapshotRoot = DqmhHeadless.SnapshotRoot(projectPath, destinationFolder);
        var before = snapshotRoot is null ? null : DqmhHeadless.Snapshot.Take(snapshotRoot);
        var mainVi = Path.Combine(destinationFolder, "Main.vi");
        var bareDestination = Path.GetFileNameWithoutExtension(destination.Length > 0 ? destination : module);
        var tester = Path.Combine(destinationFolder, $"Test {bareDestination} API.vi");
        var mainExecBefore = File.Exists(mainVi) ? await headless.ExecStateAsync(mainVi, timeoutSeconds, ct) : null;

        IReadOnlyList<LvValuesXml.Value>? run;
        JsonArray dialogs;
        var watch = DqmhDialogWatch.Start();
        try
        {
            string? runError;
            (run, runError) = await dqmh.RunDetailedAsync(scripts, route.Helper, Inputs("true", index),
                Math.Min(timeoutSeconds, 300), ct);
            if (runError is not null)
                return Json.Error("helperFailed",
                    "The run helper stopped before the wrapper ran; nothing was scripted.",
                    new { helperError = runError, steps });
        }
        catch (Exception e) when (DqmhTools.IsTimeout(e))
        {
            return Json.Error("scriptingTimedOut",
                "The scripted run did not return. Duplicate Event.vi calls Script New Event.vi, which " +
                "reports a scripting error through a MODAL dialog - look at LabVIEW for an open dialog " +
                "before calling anything else.",
                DqmhHeadless.Merge(detail, new JsonObject
                {
                    ["visibleWindows"] = DqmhHeadless.Array(DqmhTools.Win32.VisibleTitles()),
                    ["dialogs"] = watch.Events(),
                }));
        }
        finally
        {
            dialogs = watch.Events();
            await watch.DisposeAsync();
        }
        if (run is null) return Json.Error("helperMissing", $"{route.Helper} could not be run.", new { steps });
        if (DqmhTools.Failed(run) is { } error)
            return Json.Error("scriptingFailed", "Delacor's scripting answered an error - `error` is its cluster.",
                DqmhHeadless.Merge(detail, new JsonObject { ["error"] = error }));
        if (!DqmhHeadless.IsTrue(DqmhTools.Scalar(run, "Scripted?")))
            return Json.Error("notScripted",
                "The wrapper did not call Duplicate Event.vi although the dry run found the event - read `steps`.",
                detail);

        // ---- 3. save, verify from the files ---------------------------------------------------
        var (dirtyAfter, _) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts, projectPath, timeoutSeconds, ct);
        var saved = await DqmhHeadless.SaveNamedAsync(dqmh, scripts, dirtyAfter ?? [], timeoutSeconds, ct);
        List<string> created = [], modified = [];
        if (before is not null) (created, modified) = before.Diff(DqmhHeadless.Snapshot.Take(snapshotRoot!));
        var eventVi = Path.Combine(destinationFolder, newName + ".vi");
        var mainExec = File.Exists(mainVi) ? await headless.ExecStateAsync(mainVi, timeoutSeconds, ct) : null;
        var testerExec = File.Exists(tester) ? await headless.ExecStateAsync(tester, timeoutSeconds, ct) : null;

        var completion = new JsonArray();
        if (mainExec is 0 && mainExecBefore is 0)
            completion.Add("Main.vi was already NOT executable before this call, so the copy did not cause it.");
        else if (mainExec is 0)
            completion.Add(type == 1
                ? "Main.vi: the copied Broadcast's call sits loose on the diagram with a #CodeNeeded " +
                  "comment, as for a new Broadcast - place it where the module fires it (section 9c)."
                : "Main.vi: the copied event's message frame needs its code - lvai_dqmh_place_handler " +
                  "(sections 9c, 9h).");
        if (testerExec is 0)
            completion.Add($"Test {bareDestination} API.vi is not executable - DQMH 7.x leaves a Broadcast's " +
                           "status string and a request's case for a person, as for a new event.");

        var answer = new JsonObject
        {
            ["ok"] = File.Exists(eventVi),
            ["module"] = module,
            ["destinationModule"] = destination.Length > 0 ? destination : module,
            ["event"] = names[index],
            ["eventType"] = ring[index].Split(':')[0],
            ["newEvent"] = newName,
            ["newEventVi"] = eventVi,
            ["completionNeeded"] = completion,
            ["createdFiles"] = await headless.DescribeAsync(created, timeoutSeconds, ct),
            ["modifiedFiles"] = DqmhHeadless.Array(modified),
            ["mainViExecStateBefore"] = mainExecBefore,
            ["mainViExecState"] = mainExec,
            ["testerExecState"] = testerExec,
            ["savedMembers"] = DqmhHeadless.Array(saved),
            ["dialogsAnswered"] = dialogs,
            ["adoptedHelpers"] = DqmhHeadless.Array(DqmhHeadless.AdoptedHelpersOf(projectPath)),
            ["projectHygiene"] = DqmhHeadless.ProjectHygieneNote,
            ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
        };
        if (!File.Exists(eventVi))
            answer["warning"] = $"Delacor answered no error, and {eventVi} is not there - read `createdFiles`.";
        foreach (var (key, value) in detail) answer[key] = value?.DeepClone();
        return Json.Document(answer);
    }

    /// <summary>
    /// The event's position in the corrected list, matched by name ignoring case and `.vi`. The
    /// wrapper's Event Names are the bare `Event Name` fields (`Read Status`), read 2026-10-30.
    /// </summary>
    internal static int EventIndex(IReadOnlyList<string> names, string wanted)
    {
        var bare = Path.GetFileNameWithoutExtension(wanted.Trim());
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(Path.GetFileNameWithoutExtension(names[i].Trim()), bare,
                    StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// The event type out of Delacor's ring string (`Round Trip: Set Target.vi`) as the index
    /// lvai_dqmh_new_event uses; a Private or Local Instance request counts as a Request.
    /// </summary>
    internal static int TypeOf(string ringString)
    {
        var prefix = ringString.Split(':')[0].Trim();
        return prefix switch
        {
            "Broadcast" => 1,
            "Request and Wait for Reply" => 2,
            "Round Trip" => 3,
            _ => 0,
        };
    }
}
