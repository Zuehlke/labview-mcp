using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Grpc.Core;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Removing, renaming and converting a DQMH event, and validating a module - Delacor's menu
/// functions with NO dialog (docs/dqmh-scripting.md section 9).
///
/// THE THREE EVENT EDITS ARE ONE SHAPE. Delacor's menu VI parses the project, lets a person pick a
/// module and an event from two rings, and hands both to a scripter; each wrapper here makes the
/// same calls as static subVIs of one VI, so `Module Info`'s refnums stay alive, and takes the two
/// choices as NAMES. A dry run (`Script?` FALSE) finds them first and changes nothing, so a
/// misspelling costs a retry instead of a wrong edit. Delacor's remove, rename and convert dialogs
/// all refuse a project with unsaved modules - unlike its create-event dialog - so these do too.
///
/// VALIDATION needs no wrapper: Delacor ships `Validate DQMH Module (Headless).vi`, whose error
/// handlers are all wired to "no dialog".
/// </summary>
[McpServerToolType]
internal sealed class DqmhEventEditTools(LvaiConnection connection)
{
    private const string NewEvent = @"project\Delacor\DQMH\_DQMH New Event\";
    private const string RemoveEvent = @"project\Delacor\DQMH\_DQMH Remove Event\";

    internal static readonly string[] RemoveTargets =
    [
        NewEvent + "Parse Project for DQMH Modules.vi",
        NewEvent + "Close Scripting References.vi",
        NewEvent + "Open Main and Tester References.vi",
        RemoveEvent + "Get All Events in Module.vi",
        RemoveEvent + "Check for Dependent Broadcast.vi",
        RemoveEvent + "Remove Event.vi",
    ];

    internal static readonly string[] RenameTargets =
    [
        NewEvent + "Parse Project for DQMH Modules.vi",
        NewEvent + "Close Scripting References.vi",
        RemoveEvent + "Get All Events in Module.vi",
        @"project\Delacor\DQMH\_DQMH Rename Event\Rename Event.vi",
    ];

    internal static readonly string[] ConvertTargets =
    [
        NewEvent + "Parse Project for DQMH Modules.vi",
        NewEvent + "Close Scripting References.vi",
        RemoveEvent + "Get All Events in Module.vi",
        @"project\Delacor\DQMH\_DQMH Convert Event\Convert Event.vi",
    ];

    internal const string ValidateRelative =
        @"project\Delacor\DQMH\_DQMH Validate Module\Validate DQMH Module (Headless).vi";

    // ------------------------------------------------------------------ the tools

    [McpServerTool(Name = "lvai_dqmh_remove_event", Destructive = true, OpenWorld = true,
        Title = "Remove a DQMH event")]
    [Description("""
        MUTATING: removes one event from a DQMH module in the ACTIVE project with NO dialog -
        Delacor's own Remove Event.vi, driven through a generated wrapper. Removing the REQUEST of
        a Round Trip removes its broadcast half too, exactly as Delacor's dialog does; the answer
        names it as `dependentBroadcast`. DELACOR LEAVES CLEAN-UP FOR A PERSON, measured
        2026-10-06: Main.vi keeps a loose call to a removed Broadcast and a Round Trip's message
        frame with a #Code_Review_Todo label, and the API tester keeps the removed event's frame -
        so both are typically NOT executable afterwards (Delacor's dialog opens the Error List).
        `completionNeeded` says what to clean up. Names may be bare or as Delacor spells them (`Heater.lvlib`,
        `Do Something.vi`). Refused while any module has unsaved changes or is locked - Delacor's
        dialog refuses the same. Unit tests of the removed event are NOT removed; the answer lists
        files that were deleted.
        """)]
    public async Task<string> RemoveEventAsync(
        [Description("DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Event to remove, e.g. 'Do Something' or 'Do Something.vi'")] string eventName,
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_remove_event",
            () => RunAsync(new EditAction(
            "remove", "lvdqmh_remove_event", RemoveTargets, moduleName, eventName, [],
            ["Dependent Broadcast?", "Dependent Broadcast Name"], null,
            SubHelpers: EventSubHelpers, Completion: RemoveCompletion), timeoutSeconds, ct)));

    [McpServerTool(Name = "lvai_dqmh_rename_event", Destructive = true, OpenWorld = true,
        Title = "Rename a DQMH event")]
    [Description("""
        MUTATING: renames one event of a DQMH module in the ACTIVE project with NO dialog -
        Delacor's own Rename Event.vi through a generated wrapper. The new name is checked first
        the way Delacor's dialog would check it with a MODAL: not blank, a valid file name, no
        `<new>.vi` already in the module folder, no event and no Main.vi case of that name.
        Refused while any module has unsaved changes or is locked. Delacor does not
        save the module's .lvlib after a rename - measured: the file on disk still listed the old
        VIs - so this call saves it, and says so in `librarySaved`.
        """)]
    public async Task<string> RenameEventAsync(
        [Description("DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Event to rename, e.g. 'Do Something' or 'Do Something.vi'")] string eventName,
        [Description("New event name, without .vi")] string newEventName,
        [Description("DQMH 7.x only: the event's new description. Empty keeps the event VI's own " +
                     "description, as Delacor's dialog pre-fills it")] string description = "",
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_rename_event", async () =>
        {
            var newName = (newEventName ?? "").Trim();
            if (newName.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)) newName = newName[..^3];
            if (DqmhHeadless.EventRequestProblem(newName, 0, "", [], []) is { } problem)
                return Json.Error("badArguments", problem.Replace("The event name", "newEventName"));
            return await RunAsync(new EditAction(
                "rename", "lvdqmh_rename_event", RenameTargets, moduleName, eventName,
                new() { ["New Event Name"] = newName, ["Event Description"] = description ?? "" }, [],
                ctx => RenameConflict(ctx, newName), SubHelpers: EventSubHelpers,
                SaveLibrary: true, Completion: RenameCompletion,
                ClassicOnly: string.IsNullOrEmpty(description) ? null
                    : "description is a DQMH 7.x option - the classic Rename Event.vi keeps the " +
                      "event's description and takes none."), timeoutSeconds, ct);
        }));

    [McpServerTool(Name = "lvai_dqmh_convert_event", Destructive = true, OpenWorld = true,
        Title = "Convert a DQMH request into Request and Wait for Reply")]
    [Description("""
        MUTATING: converts a plain DQMH Request into a Request and Wait for Reply - or, on DQMH
        7.x, into a Round Trip (`toEventType`, with `roundTripBroadcastName` for the broadcast half
        it creates) - in the ACTIVE project, with NO dialog - Delacor's own Convert Event.vi
        through a generated wrapper. DQMH 5/6 offer only the first conversion, and a Round Trip is
        refused there by name. `eventNotFound` lists the plain requests the module has. Delacor's converter raises a MODAL when it meets a non-standard wiring in
        Main.vi's event loop; a module Delacor scripted does not, and a run that does not return is
        answered as `scriptingTimedOut` naming the windows on screen. Refused while any module has
        unsaved changes or is locked. Delacor KEEPS the old message frame, labelled
        #Code_Review_Todo, beside the new one of the same name - measured - and that frame is for a
        person to delete; `completionNeeded` says so.
        """)]
    public async Task<string> ConvertEventAsync(
        [Description("DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Plain Request to convert, e.g. 'Set Speed'")] string eventName,
        [Description("'Request and Wait for Reply' (default, every DQMH version) or 'Round Trip' " +
                     "(DQMH 7.x only)")] string toEventType = "Request and Wait for Reply",
        [Description("Round Trip only: the name of the broadcast half the conversion creates")]
        string roundTripBroadcastName = "",
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_convert_event",
            () =>
            {
                var target = DqmhTools.ParseEventType(toEventType ?? "");
                bool? roundTrip = target switch { 2 => false, 3 => true, _ => null };
                if (roundTrip is null)
                    return Task.FromResult(Json.Error("badArguments",
                        $"toEventType '{toEventType}' - a Request converts into 'Request and Wait for " +
                        "Reply' or 'Round Trip'."));
                var broadcast = (roundTripBroadcastName ?? "").Trim();
                if (roundTrip is true
                    && DqmhHeadless.EventRequestProblem(eventName ?? "", 3, broadcast, [], []) is { } problem)
                    return Task.FromResult(Json.Error("badArguments", problem));
                return RunAsync(new EditAction(
                    "convert", "lvdqmh_convert_event", ConvertTargets, moduleName, eventName,
                    roundTrip is true
                        ? new() { ["New Event Type"] = "1", ["Broadcast Event Name"] = broadcast }
                        : [],
                    [], roundTrip is true ? ctx => RenameConflict(ctx, broadcast) : null,
                    SubHelpers: EventSubHelpers, Completion: ConvertCompletion,
                    ClassicOnly: roundTrip is true
                        ? "Converting into a Round Trip is a DQMH 7.x conversion - the classic " +
                          "Convert Event.vi makes a Request and Wait for Reply only."
                        : null), timeoutSeconds, ct);
            }));

    [McpServerTool(Name = "lvai_dqmh_validate_module", ReadOnly = true, OpenWorld = true,
        Title = "Validate the DQMH modules of a project")]
    [Description("""
        READ-ONLY: runs Delacor's own `Validate DQMH Module (Headless).vi` over a project - the
        checks of Tools > DQMH > Module > Validate DQMH Module, with no dialog (its error handlers
        are wired to "no dialog"). Returns Delacor's `Validation Results` text as it reports it.
        Nothing is fixed: Delacor's fixers belong to its interactive validator.
        """)]
    public async Task<string> ValidateModuleAsync(
        [Description("The .lvproj to validate; empty means the ACTIVE project")] string projectPath = "",
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_validate_module", async () =>
        {
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing", "No scripts folder next to the exe.");
            if (DqmhHeadless.TargetPaths([ValidateRelative]) is not { } validator)
                return Json.Error("dqmhMissing",
                    "Delacor DQMH is not installed: Validate DQMH Module (Headless).vi was not found.",
                    new { lookedFor = ValidateRelative });

            var project = projectPath?.Trim() ?? "";
            if (project.Length == 0)
            {
                var (_, note, active) =
                    await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
                if (string.IsNullOrEmpty(active))
                    return Json.Error("noActiveProject",
                        "No projectPath was given and no project is active.", new { note });
                project = active;
            }
            project = Path.GetFullPath(project);
            if (!File.Exists(project))
                return Json.Error("fileNotFound", $"No project at {project}.");

            var stopwatch = Stopwatch.StartNew();
            var (values, helperError) = await new DqmhTools(connection).RunViDetailedAsync(
                scripts, validator[0], new() { ["Project"] = project }, timeoutSeconds, ct);
            if (values is null)
                return Json.Error("helperMissing", "The run helper could not be generated.");
            if (helperError is not null)
                return Json.Error("helperFailed", "The run helper stopped before the validator ran.",
                    new { helperError });

            var results = DqmhTools.Scalar(values, "Validation Results") ?? "";
            var (status, findings) = ParseValidation(results);
            return Json.Document(new JsonObject
            {
                ["ok"] = status == "PASS",
                ["status"] = status,
                ["findings"] = findings,
                ["project"] = project,
                ["validationResults"] = results,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] = "Delacor's validator checks DQMH STRUCTURE - required nodes, folders, " +
                           "tagging. DQMH 5 answered PASS for a module whose Main.vi and tester were " +
                           "not executable (measured 2026-10-06); DQMH 7.x reports them as `Main VI " +
                           "broken` / `Tester VI broken` (measured 2026-10-30). lvai_exec_state says " +
                           "WHY a VI is broken.",
            });
        }));

    [McpServerTool(Name = "lvai_dqmh_rename_module", Destructive = true, OpenWorld = true,
        Title = "Rename a DQMH module")]
    [Description("""
        MUTATING: renames a DQMH module in the ACTIVE project with NO dialog - Delacor's own
        Scripter - Rename Module.vi through a generated wrapper. The new name is checked first the
        way Delacor's dialog checks it with a MODAL: not blank, a valid file name, and no library of
        that name already in the project. Refused while any module has unsaved changes or is
        locked. The answer reads the module back under its new name. DELACOR SAVES THE WHOLE
        PROJECT at the end (Save All This Project) - measured: 52 files in other modules re-saved -
        so `modifiedFiles` is long; the project's virtual folder keeps the OLD `<Name> Module`
        label.
        """)]
    public async Task<string> RenameModuleAsync(
        [Description("DQMH module to rename, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("New module name, without .lvlib")] string newModuleName,
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_rename_module", async () =>
        {
            var newName = (newModuleName ?? "").Trim();
            if (newName.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase)) newName = newName[..^6];
            if (DqmhHeadless.EventRequestProblem(newName, 0, "", [], []) is { } problem)
                return Json.Error("badArguments", problem.Replace("The event name", "newModuleName"));
            return await RunAsync(new EditAction(
                "rename module", "lvdqmh_rename_module", RenameModuleTargets, moduleName, null,
                new() { ["New Module Name"] = newName }, [],
                ctx => Task.FromResult(LibraryNameTaken(ctx, newName)),
                PostModuleName: newName + ".lvlib", SubHelpers: EventSubHelpers,
                Completion: RenameModuleCompletion), timeoutSeconds, ct);
        }));

    [McpServerTool(Name = "lvai_dqmh_duplicate_module", Destructive = true, OpenWorld = true,
        Title = "Duplicate a DQMH module")]
    [Description("""
        MUTATING, DQMH 7.x ONLY: copies a DQMH module of the ACTIVE project into a NEW module under a
        new name, with NO dialog - Delacor's own Scripter - Duplicate Module.vi through a generated
        wrapper, the result of Tools > DQMH > Module > Duplicate DQMH Module. Every event, the API
        tester and Main.vi are copied and renamed; the RT tester is not (Delacor's own scope,
        measured). The new module goes into `<saveFolder>\<newModuleName>` - saveFolder is the
        PARENT folder, measured - and the project lists it. The new name is checked first the way
        Delacor's dialog checks it with a MODAL: not blank, a valid file name, no library of that
        name in the project, no non-empty folder of that name. A source module whose Main.vi still
        calls a VI that no longer exists makes LabVIEW open a file dialog while it copies - remove
        such leftovers first. Refused while any module has unsaved changes or is locked. On DQMH
        5/6 it answers `needsDqmh7`.
        """)]
    public async Task<string> DuplicateModuleAsync(
        [Description("DQMH module to copy, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Name of the new module, without .lvlib")] string newModuleName,
        [Description("PARENT folder of the new module's folder, relative to the project folder or " +
                     "absolute; empty means the folder that holds the source module's folder")]
        string saveFolder = "",
        [Description("Description of the new module's library")] string description = "",
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_duplicate_module", async () =>
        {
            var newName = (newModuleName ?? "").Trim();
            if (newName.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase)) newName = newName[..^6];
            if (DqmhHeadless.EventRequestProblem(newName, 0, "", [], []) is { } problem)
                return Json.Error("badArguments", problem.Replace("The event name", "newModuleName"));
            var (_, _, projectPath) = await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
            var projectDirectory = Path.GetDirectoryName(projectPath ?? "") ?? "";
            string? folder = string.IsNullOrWhiteSpace(saveFolder) ? null
                : Path.GetFullPath(Path.Combine(projectDirectory, saveFolder.Trim()));
            return await RunAsync(new EditAction(
                "duplicate module", "lvdqmh_duplicate_module", DuplicateModuleTargets, moduleName, null,
                new()
                {
                    ["New Module Name"] = newName,
                    ["Module Description"] = description ?? "",
                    ["Save Folder"] = folder ?? "",
                },
                [], ctx => Task.FromResult(DuplicateModuleConflict(ctx, newName, folder)),
                PostModuleName: newName + ".lvlib", SubHelpers: EventSubHelpers,
                Completion: DuplicateModuleCompletion, V7Only: true,
                LateInputs: ctx => folder is null
                    ? new() { ["Save Folder"] = Path.GetDirectoryName(ctx.Folder.TrimEnd('\\')) ?? "" }
                    : []), timeoutSeconds, ct);
        }));

    [McpServerTool(Name = "lvai_dqmh_add_helper_loop", Destructive = true, OpenWorld = true,
        Title = "Add a helper loop to a DQMH module")]
    [Description("""
        MUTATING, DQMH 7.x ONLY: adds a helper loop to a DQMH module's Main.vi in the ACTIVE
        project, with NO dialog - Delacor's own Script New Helper Loop.vi through a generated
        wrapper, the result of Tools > DQMH > Module > Add Helper Loop to DQMH Module. The loop gets
        its own event structure, so a PRIVATE request can name it as its consuming loop
        (lvai_dqmh_new_event requestScoping + consumingLoop). The name is checked first against the
        loops Main.vi already has, which Delacor's dialog refuses with a MODAL. Refused while any
        module has unsaved changes or is locked. On DQMH 5/6 it answers `needsDqmh7`.
        """)]
    public async Task<string> AddHelperLoopAsync(
        [Description("DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Name of the new helper loop, e.g. 'Acquisition Loop'")] string helperLoopName,
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_add_helper_loop", () =>
        {
            var loop = (helperLoopName ?? "").Trim();
            if (loop.Length == 0)
                return Task.FromResult(Json.Error("badArguments", "helperLoopName is required."));
            return RunAsync(new EditAction(
                "add helper loop", "lvdqmh_add_helper_loop", HelperLoopTargets, moduleName, null,
                new() { ["Helper Loop Name"] = loop }, [],
                ctx => Task.FromResult(HelperLoopConflict(ctx, loop)),
                SubHelpers: EventSubHelpers, Completion: HelperLoopCompletion, V7Only: true),
                timeoutSeconds, ct);
        }));

    internal static readonly string[] DuplicateModuleTargets =
    [
        DqmhLayout.Support + "Parse Project for DQMH Modules.vi",
        DqmhLayout.Support + "Close Scripting References.vi",
        DqmhLayout.Support + "Get All Events in Module.vi",
        @"project\Delacor\DQMH\_DQMH Duplicate Module\Scripter - Duplicate Module.vi",
    ];

    internal static readonly string[] HelperLoopTargets =
    [
        DqmhLayout.Support + "Parse Project for DQMH Modules.vi",
        DqmhLayout.Support + "Close Scripting References.vi",
        DqmhLayout.Support + "Get All Events in Module.vi",
        DqmhLayout.Support + "Get Private Event Consuming Loops.vi",
        @"project\Delacor\DQMH\_DQMH Add Helper Loop\Script New Helper Loop.vi",
    ];

    /// <summary>Delacor's OK to Proceed.vi: a free library name, and no folder full of files.</summary>
    private static string? DuplicateModuleConflict(Context ctx, string newName, string? saveFolder)
    {
        if (LibraryNameTaken(ctx, newName) is { } taken) return taken;
        var parent = saveFolder ?? Path.GetDirectoryName(ctx.Folder.TrimEnd('\\'));
        var target = parent is null ? null : Path.Combine(parent, newName);
        return target is not null && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()
            ? $"The folder {target} already exists and is not empty - the new module's files would go there."
            : null;
    }

    /// <summary>Delacor's dialog refuses a loop name Main.vi already has.</summary>
    private static string? HelperLoopConflict(Context ctx, string loop)
    {
        var loops = DqmhHeadless.Listed(ctx.Dry, "Loop Names");
        return DqmhHeadless.FindListed(loops, loop) is { } taken
            ? $"Main.vi of {ctx.Module} already has a loop '{taken}' (loops: {string.Join(", ", loops)})."
            : null;
    }

    private const string DuplicateModuleCompletion =
        "Delacor copies the module as it is - a Main.vi or tester that was not executable stays that " +
        "way in the copy - and does not copy the RT tester; create one with lvai_dqmh_create_rt_tester.";

    private const string HelperLoopCompletion =
        "Delacor's helper loop is a template: its event structure and case structure carry " +
        "#CodeNeeded labels for the loop's own work.";

    [McpServerTool(Name = "lvai_dqmh_create_rt_tester", Destructive = true, OpenWorld = true,
        Title = "Create a DQMH RT tester")]
    [Description("""
        MUTATING: creates the Real-Time API tester (`Test <Module> API-RT.vi`) of a DQMH module in
        the ACTIVE project with NO dialog - Delacor's own Scripter - Create RT Tester.vi through a
        generated wrapper. Delacor offers it for Singleton and Cloneable modules alike (measured:
        its menu VI asks Parse for Both). Its scripter answers two cases with a MODAL - an RT tester
        that already exists, and a standard tester it cannot find - so the dry run asks Delacor's
        own finders first and refuses both by name. THE NEW RT TESTER IS NOT EXECUTABLE, by
        Delacor's design: each request frame carries #CodeNeeded for its required inputs. Refused
        while any module has unsaved changes or is locked.
        """)]
    public async Task<string> CreateRtTesterAsync(
        [Description("Cloneable DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_create_rt_tester",
            () => RunAsync(new EditAction(
            "create RT tester", "lvdqmh_create_rt_tester", RtTesterTargets, moduleName, null, [],
            ["RT Tester Path"], ctx => Task.FromResult(RtTesterConflict(ctx)),
            SubHelpers: TesterSubHelpers, Completion: RtTesterCompletion), timeoutSeconds, ct)));

    [McpServerTool(Name = "lvai_dqmh_remove_do_something", Destructive = true, OpenWorld = true,
        Title = "Remove a DQMH module's Do Something examples")]
    [Description("""
        MUTATING: removes the "Do Something" example events from a DQMH module in the ACTIVE
        project with NO dialog - Delacor's own Remove Do Something.vi, the step Add New DQMH
        Module runs when its Do Something box is cleared, through a generated wrapper. It rescripts
        Main.vi and the API tester, which stay executable (measured). A module that has no Do
        Something event left is refused from the dry run - Delacor's VI answers Error 1055 for it.
        Refused while any module has unsaved changes or is locked.
        """)]
    public async Task<string> RemoveDoSomethingAsync(
        [Description("DQMH module, e.g. 'Heater' or 'Heater.lvlib'")] string moduleName,
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_remove_do_something",
            () => RunAsync(new EditAction(
            "remove Do Something", "lvdqmh_remove_do_something", RemoveDoSomethingTargets,
            moduleName, null, [], [], ctx => Task.FromResult(DoSomethingAbsent(ctx)),
            SubHelpers: TesterSubHelpers), timeoutSeconds, ct)));

    internal static readonly string[] RenameModuleTargets =
    [
        NewEvent + "Parse Project for DQMH Modules.vi",
        NewEvent + "Close Scripting References.vi",
        RemoveEvent + "Get All Events in Module.vi",
        @"project\Delacor\DQMH\_DQMH Rename Module\Scripter - Rename Module.vi",
    ];

    internal static readonly string[] RtTesterTargets =
    [
        NewEvent + "Parse Project for DQMH Modules.vi",
        NewEvent + "Close Scripting References.vi",
        RemoveEvent + "Get All Events in Module.vi",
        NewEvent + "Find Tester on Disk.vi",
        NewEvent + "Find RT Tester on Disk.vi",
        @"project\Delacor\DQMH\_DQMH Create RT Tester\Scripter - Create RT Tester.vi",
    ];

    internal static readonly string[] RemoveDoSomethingTargets =
    [
        NewEvent + "Parse Project for DQMH Modules.vi",
        NewEvent + "Close Scripting References.vi",
        RemoveEvent + "Get All Events in Module.vi",
        NewEvent + "Find Tester on Disk.vi",
        NewEvent + "Find RT Tester on Disk.vi",
        @"project\Delacor\DQMH\_DQMH Remove Do Something\Remove Do Something.vi",
    ];

    /// <summary>The tester-based wrappers also call `lvdqmh_find_testers` - Delacor's two finders,
    /// each path emptied unless its flag is TRUE (measured: an unrelated VI's path came back with
    /// `RT Tester Found?` FALSE).</summary>
    private static readonly string[] TesterSubHelpers =
        ["lvdqmh_close_modules", "lvdqmh_pick_event", "lvdqmh_find_testers"];

    /// <summary>The example events Add New DQMH Module scripts when Do Something is kept.</summary>
    internal static readonly string[] DoSomethingEvents =
        ["Do Something.vi", "Do Something Else.vi", "Do Something Else and Wait for Reply.vi",
         "Did Something.vi"];

    /// <summary>Delacor's rename dialog refuses a name a library of the project already has.</summary>
    private static string? LibraryNameTaken(Context ctx, string newName)
    {
        var libraries = DqmhHeadless.Listed(ctx.Dry, "Library Names");
        return DqmhHeadless.FindListed(libraries, newName + ".lvlib") is { } taken
            ? $"The project already holds a library '{taken}'."
            : null;
    }

    /// <summary>Delacor's Remove Do Something.vi answers Error 1055 when none is left.</summary>
    private static string? DoSomethingAbsent(Context ctx) =>
        ctx.Events.Any(e => DoSomethingEvents.Contains(e, StringComparer.OrdinalIgnoreCase))
            ? null
            : $"Module {ctx.Module} has no Do Something example event left - `events` lists what " +
              "it has. Delacor's Remove Do Something answers Error 1055 for such a module.";

    /// <summary>The two cases Delacor's RT-tester scripter answers with a MODAL.</summary>
    private static string? RtTesterConflict(Context ctx)
    {
        if (DqmhHeadless.IsTrue(DqmhTools.Scalar(ctx.Dry, "RT Tester Found?")))
            return $"Module {ctx.Module} already has an RT tester: " +
                   $"{DqmhTools.Scalar(ctx.Dry, "RT Tester Path")}.";
        if (!DqmhHeadless.IsTrue(DqmhTools.Scalar(ctx.Dry, "Tester Found?")))
            return $"Module {ctx.Module} has no standard API tester on disk, and Delacor builds the " +
                   "RT tester from it.";
        return null;
    }

    // ------------------------------------------------------------------ the shared route

    /// <param name="Verb">For messages: remove, rename, convert.</param>
    /// <param name="ExtraOutputs">Wrapper indicators copied into the answer as they are.</param>
    /// <param name="PreCheck">The checks Delacor's dialog answers with a MODAL, made here; null
    /// when the scripter itself carries no such check.</param>
    /// <param name="EventName">Null for an action on the whole MODULE - rename it, give it an RT
    /// tester, remove its Do Something events.</param>
    /// <param name="PostModuleName">The module's name AFTER the action, when the action renames
    /// it; the result is then read under that name.</param>
    /// <param name="Completion">What Delacor's scripter leaves for a person, said in the answer
    /// whenever Main.vi or the tester are not executable afterwards.</param>
    /// <param name="V7Only">The function exists for DQMH 7.x only - its wrapper is the `_dqmh7`
    /// document alone and `Targets` are 7.x locations.</param>
    /// <param name="LateInputs">Inputs that depend on the dry run - the module's folder - added to
    /// the scripted run only.</param>
    /// <param name="ClassicOnly">The refusal when the call asks for something only DQMH 7.x
    /// scripts and the station has the classic layout; null when the call works on both.</param>
    private sealed record EditAction(
        string Verb, string Helper, string[] Targets, string ModuleName, string? EventName,
        Dictionary<string, string> ExtraInputs, string[] ExtraOutputs,
        Func<Context, Task<string?>>? PreCheck, string? PostModuleName = null,
        string[]? SubHelpers = null, bool SaveLibrary = false, string? Completion = null,
        string? ClassicOnly = null, bool V7Only = false,
        Func<Context, Dictionary<string, string>>? LateInputs = null);

    /// <summary>The two helpers every event-edit wrapper calls - see EnsureWrapperAsync.</summary>
    private static readonly string[] EventSubHelpers = ["lvdqmh_close_modules", "lvdqmh_pick_event"];

    private sealed record Context(
        string Module, string? Event, string Folder, List<string> Events,
        IReadOnlyList<LvValuesXml.Value> Dry, Func<Task<List<string>>> MainViFrames);

    private async Task<string> RunAsync(EditAction action, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.ModuleName))
            return Json.Error("badArguments", "moduleName is required.");
        if (action.EventName is not null && string.IsNullOrWhiteSpace(action.EventName))
            return Json.Error("badArguments", "eventName is required.");
        var shippedHelper = action.V7Only ? action.Helper + DqmhLayout.Suffix : action.Helper;
        if (StatusTools.ScriptsDirectory() is not { } scripts
            || !File.Exists(Path.Combine(scripts, shippedHelper + ".xml")))
            return Json.Error("scriptsMissing",
                $"{shippedHelper}.xml was not found in the scripts folder next to the exe.");
        var classicInstalled = false;
        var route = action.V7Only
            ? DqmhLayout.ResolveV7Only(scripts, action.Helper, action.Targets, action.SubHelpers,
                out classicInstalled)
            : DqmhLayout.Resolve(scripts, action.Helper, action.Targets, action.SubHelpers);
        if (route is null && classicInstalled)
            return Json.Error("needsDqmh7",
                $"Delacor's {action.Verb} is driven for DQMH 7.x only, and this station has the classic " +
                "DQMH layout (5.x/6.x). Use Delacor's own menu entry there.");
        if (route is null)
            return Json.Error("dqmhMissing", DqmhLayout.MissingMessage("one of the scripting VIs"),
                new { lookedFor = action.Targets });
        if (action.ClassicOnly is { } classicOnly && route.Generation == DqmhGeneration.Classic)
            return Json.Error("badArguments", classicOnly);
        var targets = route.Targets;
        var helper = route.Helper;

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
        if (await headless.EnsureWrapperAsync(scripts, helper, targets, projectPath, steps,
                timeoutSeconds, ct, route.SubHelpers) is { } wrapperError)
            return wrapperError;

        // ---- 1. dry run, with Delacor's own spelling retried once per name ------------------
        var module = DqmhHeadless.DelacorModuleName(action.ModuleName);
        var evt = action.EventName is null ? null : DqmhHeadless.DelacorEventName(action.EventName);
        IReadOnlyList<LvValuesXml.Value>? dry = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (values, helperError) = await dqmh.RunDetailedAsync(scripts, helper,
                Inputs(action, module, evt, script: false), timeoutSeconds, ct);
            if (values is null)
                return Json.Error("helperMissing", $"{helper} could not be run.", new { steps });
            if (helperError is not null)
                return Json.Error("helperFailed",
                    "The run helper stopped before the wrapper ran - an input could not be set.",
                    new { helperError, steps });
            dry = values;
            steps.Add(Step("dryRun", module, evt, values));
            if (DqmhHeadless.Index(values, "Module Index") < 0)
            {
                var listed = DqmhHeadless.FindListed(DqmhHeadless.Listed(values, "Module Names"), module);
                if (listed is null || listed == module) break;
                module = listed;
            }
            else if (evt is not null && DqmhHeadless.Index(values, "Event Index") < 0)
            {
                var listed = DqmhHeadless.FindListed(DqmhHeadless.Listed(values, "Event Names"), evt);
                if (listed is null || listed == evt) break;
                evt = listed;
            }
            else break;
        }

        var moduleNames = DqmhHeadless.Listed(dry!, "Module Names");
        var events = DqmhHeadless.Listed(dry!, "Event Names");
        var detail = new JsonObject
        {
            ["moduleNames"] = DqmhHeadless.Array(moduleNames),
            ["events"] = DqmhHeadless.Array(events),
            ["steps"] = steps,
        };
        if (DqmhTools.Failed(dry!) is { } dryError)
            return Json.Error("parseFailed", "Delacor's project parse answered an error.",
                DqmhHeadless.Merge(detail, new JsonObject { ["error"] = dryError }));
        if (moduleNames.Count == 0)
            return Json.Error("noDqmhModules",
                "The active project holds no DQMH module that Delacor recognises.",
                DqmhHeadless.Merge(detail, new JsonObject { ["activeProject"] = projectPath }));
        if (DqmhHeadless.Index(dry!, "Module Index") < 0)
            return Json.Error("moduleNotFound",
                $"No DQMH module '{action.ModuleName}' in the active project - `moduleNames` lists them.",
                detail);
        if (evt is not null && DqmhHeadless.Index(dry!, "Event Index") < 0)
            return Json.Error("eventNotFound",
                $"Module {module} has no event '{action.EventName}' Delacor's {action.Verb} " +
                "tool offers - `events` lists the ones it does.", detail);
        if (DqmhHeadless.IsTrue(DqmhTools.Scalar(dry!, "Any Locked Modules?")))
            return Json.Error("modulesLocked",
                "A DQMH module in this project is locked or read-only; nothing was written.", detail);
        if (DqmhHeadless.IsTrue(DqmhTools.Scalar(dry!, "Any Dirty Modules?")))
            return Json.Error("modulesHaveUnsavedChanges",
                $"A DQMH module in this project has unsaved changes. Delacor's {action.Verb} " +
                "dialog refuses until everything is saved, typedef changes included, and so does " +
                "this; nothing was written.", detail);

        var folder = DqmhTools.Scalar(dry!, "Library Owning Folder") ?? "";
        var mainVi = Path.Combine(folder, "Main.vi");
        var tester = Path.Combine(folder, $"Test {Path.GetFileNameWithoutExtension(module)} API.vi");
        var context = new Context(module, evt, folder, events, dry!, async () =>
        {
            var (frames, code) = await headless.MainViCaseNamesAsync(mainVi, timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "readMainViFrames", ["errorCode"] = code, ["frames"] = frames.Count });
            return frames;
        });
        if (action.PreCheck is not null && await action.PreCheck(context) is { } conflict)
            return Json.Error("nameInUse", conflict, detail);

        // ---- 2. script ----------------------------------------------------------------------
        // Nothing in the project may be unsaved: Delacor checks its module members, but its
        // scripters SAVE THE PROJECT as they go, and an unsaved tester or unit test then raises a
        // modal - measured. The dialog watch answers that modal only because of this check.
        var (dirtyBefore, dirtyError) = await DqmhHeadless.ProjectDirtyAsync(
            dqmh, scripts, projectPath, timeoutSeconds, ct);
        if (dirtyError is not null)
            return Json.Error("dirtyCheckFailed",
                "Could not read which VIs of the project have unsaved changes.",
                DqmhHeadless.Merge(detail, new JsonObject { ["error"] = dirtyError }));
        if (dirtyBefore is { Count: > 0 })
            return Json.Error("unsavedChangesInProject", DqmhHeadless.UnsavedBeforeMessage(dirtyBefore),
                DqmhHeadless.Merge(detail, new JsonObject { ["unsaved"] = DqmhHeadless.Array(dirtyBefore) }));

        var before = DqmhHeadless.Snapshot.TakeOf(projectPath);
        var mainExecBefore = File.Exists(mainVi) ? await headless.ExecStateAsync(mainVi, timeoutSeconds, ct) : null;
        var testerExecBefore = File.Exists(tester) ? await headless.ExecStateAsync(tester, timeoutSeconds, ct) : null;
        IReadOnlyList<LvValuesXml.Value>? run;
        JsonArray dialogs;
        var watch = DqmhDialogWatch.Start();
        try
        {
            string? runHelperError;
            (run, runHelperError) = await dqmh.RunDetailedAsync(scripts, helper,
                Late(Inputs(action, module, evt, script: true), action.LateInputs?.Invoke(context)),
                Math.Min(timeoutSeconds, 300), ct);
            if (runHelperError is not null)
                return Json.Error("helperFailed",
                    "The run helper stopped before the wrapper ran; nothing was scripted.",
                    new { helperError = runHelperError, steps });
        }
        catch (Exception e) when (DqmhTools.IsTimeout(e))
        {
            return Json.Error("scriptingTimedOut",
                "The scripted run did not return. A Delacor scripter reports some conditions " +
                "through a MODAL dialog, which stops the run until someone dismisses it - " +
                "look at LabVIEW for an open dialog before calling anything else.",
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
        if (run is null)
            return Json.Error("helperMissing", $"{helper} could not be run.", new { steps });
        steps.Add(Step("script", module, evt, run));
        if (DqmhTools.Failed(run) is { } error)
            return Json.Error("scriptingFailed",
                "Delacor's scripting answered an error - `error` is its cluster.",
                DqmhHeadless.Merge(detail, new JsonObject { ["error"] = error }));
        if (!DqmhHeadless.IsTrue(DqmhTools.Scalar(run, "Scripted?")))
            return Json.Error("notScripted",
                "The wrapper did not call Delacor's scripter although the dry run found the module " +
                "and the event - read `steps`.", detail);

        // ---- 3. save what the scripting left dirty, verify from the files --------------------
        // A RENAMED module is read back under its new name - its folder, Main.vi and tester.
        if (action.PostModuleName is { } renamed)
        {
            var (post, _) = await dqmh.RunDetailedAsync(scripts, helper,
                Inputs(action, renamed, evt, script: false), timeoutSeconds, ct);
            if (post is not null)
            {
                steps.Add(Step("readBack", renamed, evt, post));
                if (DqmhHeadless.Index(post, "Module Index") >= 0)
                {
                    module = renamed;
                    folder = DqmhTools.Scalar(post, "Library Owning Folder") ?? folder;
                    mainVi = Path.Combine(folder, "Main.vi");
                    tester = Path.Combine(folder, $"Test {Path.GetFileNameWithoutExtension(module)} API.vi");
                }
            }
        }

        // RENAME LEAVES THE .lvlib UNSAVED, and nothing reports it: neither Parse's dirty flag nor
        // the members' modification bits - measured, the file still listed the old event's VIs.
        JsonObject? librarySave = null;
        if (action.SaveLibrary)
        {
            var library = Path.Combine(folder, module);
            var (saveRun, saveHelperError) = await dqmh.RunDetailedAsync(scripts,
                "lvdqmh_save_library", new() { ["library path"] = library }, timeoutSeconds, ct);
            librarySave = new JsonObject
            {
                ["library"] = library,
                ["saved"] = saveRun is not null && saveHelperError is null
                            && DqmhTools.Failed(saveRun) is null,
                ["error"] = saveHelperError ?? (saveRun is null ? null : DqmhTools.Failed(saveRun)),
            };
        }

        // Nothing in the project was unsaved before - the call refuses otherwise - so whatever is
        // unsaved now is the scripting's own work, tester and unit tests included; save it, as a
        // person would after the dialog.
        var (dirtyAfter, _) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts, projectPath,
            timeoutSeconds, ct);
        var saved = await DqmhHeadless.SaveNamedAsync(dqmh, scripts, dirtyAfter ?? [], timeoutSeconds, ct);
        var unsaved = (await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts, projectPath,
            timeoutSeconds, ct)).Dirty ?? [];

        var after = DqmhHeadless.Snapshot.TakeOf(projectPath);
        List<string> created = [], modified = [], deleted = [];
        if (before is not null && after is not null)
        {
            (created, modified) = before.Diff(after);
            deleted = before.Deleted(after);
        }
        var mainExec = File.Exists(mainVi) ? await headless.ExecStateAsync(mainVi, timeoutSeconds, ct) : null;
        var testerExec = File.Exists(tester) ? await headless.ExecStateAsync(tester, timeoutSeconds, ct) : null;
        var adopted = DqmhHeadless.AdoptedHelpersOf(projectPath);

        var answer = new JsonObject
        {
            ["ok"] = true,
            ["action"] = action.Verb,
            ["dqmhGeneration"] = route.GenerationName,
            ["wrapper"] = helper,
            ["module"] = module,
            ["event"] = evt,
            ["mainViExecStateBefore"] = mainExecBefore,
            ["mainViExecState"] = mainExec,
            ["testerExecStateBefore"] = testerExecBefore,
            ["testerExecState"] = testerExec,
            ["createdFiles"] = await headless.DescribeAsync(created, timeoutSeconds, ct),
            ["deletedFiles"] = DqmhHeadless.Array(deleted),
            ["modifiedFiles"] = DqmhHeadless.Array(modified),
            ["savedMembers"] = DqmhHeadless.Array(saved),
            ["unsavedMembers"] = DqmhHeadless.Array(unsaved),
            ["librarySaved"] = librarySave,
            ["dialogsAnswered"] = dialogs,
            ["completionNeeded"] = CompletionList(action, mainExec, testerExec, module),
            ["adoptedHelpers"] = DqmhHeadless.Array(adopted),
            ["projectHygiene"] = DqmhHeadless.ProjectHygieneNote,
            ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
        };
        foreach (var output in action.ExtraOutputs)
            answer[Camel(output)] = DqmhTools.Scalar(run, output);
        // 7.x answers the dependent broadcasts of a removed request as a LIST - a request may take
        // several with it - where the classic check named one.
        if (route.Generation == DqmhGeneration.V7
            && action.ExtraOutputs.Contains("Dependent Broadcast Name"))
        {
            var names = DqmhHeadless.Listed(run, "Dependent Broadcast Names");
            answer["dependentBroadcastNames"] = DqmhHeadless.Array(names);
            answer["dependentBroadcastName"] = names.Count == 0 ? "" : string.Join(", ", names);
        }
        if (mainExec is 0 && mainExecBefore is not 0)
            answer["warning"] = "Main.vi was executable before and is NOT after this call - read " +
                                "its errors before going on.";
        else if (testerExec is 0 && testerExecBefore is not 0)
            answer["warning"] = "The module's API tester was executable before and is NOT after " +
                                "this call.";
        foreach (var (key, value) in detail)
            answer[key] = value?.DeepClone();
        return Json.Document(answer);
    }

    private const string RtTesterCompletion =
        "Delacor's RT tester is a starting point: every request frame carries #CodeNeeded for its " +
        "required inputs, so the new `Test <Module> API-RT.vi` is not executable until they are wired.";

    private const string RenameModuleCompletion =
        "Delacor leaves the project's virtual folder named `<old name> Module`; rename it in the " +
        "project if the label matters.";

    private const string RemoveCompletion =
        "Delacor leaves the clean-up of a removal to a person, and its dialog opens the Error List " +
        "for it: a removed Broadcast's loose #CodeNeeded call stays in Main.vi pointing at the " +
        "deleted VI, a removed Round Trip's message frame stays with a #Code_Review_Todo label, " +
        "and the API tester keeps the removed event's frame (`Unknown Event`) and calls. Delete " +
        "those, then check both VIs again.";

    private const string ConvertCompletion =
        "Delacor keeps the request's OLD message frame in Main.vi, labelled #Code_Review_Todo, " +
        "beside the new one of the same name; delete the old frame.";

    private const string RenameCompletion =
        "Delacor renames the event's VIs, typedefs and registration; the message frame's selector, " +
        "its label and the tester's button keep the old name. Rename them if the old name matters.";

    /// <summary>What is left for a person - only said when something is actually not executable,
    /// apart from the rename note, which is cosmetic and always true.</summary>
    private static JsonArray CompletionList(EditAction action, int? mainExec, int? testerExec, string module)
    {
        var list = new JsonArray();
        if (action.Completion is null) return list;
        if (mainExec is 0 || testerExec is 0 || action.Completion is RenameCompletion
                or RtTesterCompletion or RenameModuleCompletion)
            list.Add(action.Completion);
        if (mainExec is 0) list.Add($"Main.vi of {module} is not executable (execState 0).");
        if (testerExec is 0) list.Add($"The API tester of {module} is not executable (execState 0).");
        return list;
    }

    /// <summary>
    /// Delacor's rename dialog runs Verify Event Names.vi, which answers a taken name with a
    /// MODAL; this makes the same check, and the create-event route's Main.vi case check, first.
    /// </summary>
    private static async Task<string?> RenameConflict(Context ctx, string newName)
    {
        if (DqmhHeadless.FindListed(ctx.Events, newName) is { } taken)
            return $"Module {ctx.Module} already has an event '{taken}'.";
        if (ctx.Folder.Length > 0 && File.Exists(Path.Combine(ctx.Folder, newName + ".vi")))
            return $"The module folder already holds '{newName}.vi', the file the renamed event " +
                   "would take. Delacor's own dialog refuses the same name.";
        if ((await ctx.MainViFrames()).FirstOrDefault(f =>
                string.Equals(f, newName, StringComparison.OrdinalIgnoreCase)) is { } frame)
            return $"Main.vi already has a case named '{frame}'.";
        return null;
    }

    /// <summary>The scripted run's inputs with the ones that depended on the dry run added.</summary>
    private static Dictionary<string, string> Late(Dictionary<string, string> inputs,
        Dictionary<string, string>? late)
    {
        foreach (var (key, value) in late ?? []) inputs[key] = value;
        return DqmhHeadless.Sendable(inputs);
    }

    private static Dictionary<string, string> Inputs(EditAction action, string module, string? evt, bool script)
    {
        var inputs = new Dictionary<string, string>
        {
            ["Module Name"] = module,
            ["Script?"] = script ? "true" : "false",
        };
        if (evt is not null) inputs["Event Name"] = evt;
        foreach (var (key, value) in action.ExtraInputs) inputs[key] = value;
        return DqmhHeadless.Sendable(inputs);
    }

    private static JsonObject Step(string step, string module, string? evt,
        IReadOnlyList<LvValuesXml.Value> run) => new()
    {
        ["step"] = step,
        ["moduleName"] = module,
        ["eventName"] = evt,
        ["moduleIndex"] = DqmhTools.Scalar(run, "Module Index"),
        ["eventIndex"] = DqmhTools.Scalar(run, "Event Index"),
        ["scripted"] = DqmhTools.Scalar(run, "Scripted?"),
    };

    /// <summary>
    /// Delacor's `Validation Results` text, read off its export 2026-10-06: a first line
    /// `PASS: <n> Modules Validated`, `FAIL: <n> Modules Analyzed` or `ERROR: <message>`, then for
    /// a FAIL one `Test Failure;<library>;<category>;<issue>` or `Test Error;<library>;<message>`
    /// line per finding.
    /// </summary>
    internal static (string Status, JsonArray Findings) ParseValidation(string results)
    {
        var lines = results.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var first = lines.FirstOrDefault() ?? "";
        var status = first.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "PASS"
            : first.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) ? "FAIL"
            : first.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ? "ERROR"
            : "UNKNOWN";
        var findings = new JsonArray();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(';');
            findings.Add(new JsonObject
            {
                ["kind"] = parts[0],
                ["library"] = parts.Length > 1 ? parts[1] : null,
                ["category"] = parts.Length > 3 ? parts[2] : null,
                ["issue"] = parts.Length > 3 ? string.Join(";", parts[3..]) : parts.Length > 2 ? parts[2] : null,
            });
        }
        return (status, findings);
    }

    /// <summary>`Dependent Broadcast Name` -> `dependentBroadcastName`; a trailing `?` is dropped.</summary>
    internal static string Camel(string label)
    {
        var words = label.TrimEnd('?').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select((w, i) =>
            i == 0 ? w.ToLowerInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
