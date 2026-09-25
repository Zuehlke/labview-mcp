using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Everything that acts on the running IDE or on disk: open, run, build, palette drops.
/// These are the RPCs Nigel itself never uses - running and building are the capabilities
/// that make this more than an editor assistant.
/// </summary>
[McpServerToolType]
internal sealed class ActionTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_run_vi_as_top_level", Destructive = true, OpenWorld = true,
                   Title = "Run a VI as top level")]
    [Description("""
        RPC RunVIAsTopLevel. MUTATING: actually EXECUTES the VI in LabVIEW with the given
        control values and returns its indicator values. Side effects are whatever the VI does
        - it can drive hardware, write files or move a stage.
        inputs/outputs are string maps, and the values really do cross as STRINGS: measured,
        LabVIEW does NOT coerce them to the control's type. String controls work; a numeric or
        path control fails with errorCode 91 at Control Value:Set BEFORE the VI runs - as "42"
        and as 42 alike. So take numbers and paths in as strings and convert them on the
        diagram. On the way out an array or cluster indicator also fails to marshal, and there
        errorCode 91 arrives AFTER the VI has run correctly - it is not proof of failure.
        Details and the measurements in lvai_aixml_reference section 10.
        Never run a VI you have not inspected with lvai_describe_vi first.
        """)]
    public async Task<string> RunViAsTopLevelAsync(
        [Description(@"Absolute path to the .vi to run")] string viPath,
        [Description("""
            Control values as JSON object, e.g. {"X":"3","Y":"4"}. Keys are control labels.
            Omit for a VI that needs no inputs.
            """)]
        string? inputsJson = null,
        [Description("Local budget in seconds - raise it for long-running VIs")]
        int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var request = new RunVIAsTopLevelRequest { ViPath = viPath };
            foreach (var (key, value) in Rpc.ParseStringMap(inputsJson, nameof(inputsJson)))
                request.Inputs[key] = value;

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            return Json.Message(response,
                ("inputsSent", JsonValue.Create(request.Inputs.Count)),
                ("elapsedMs", JsonValue.Create(stopwatch.ElapsedMilliseconds)));
        });

    [McpServerTool(Name = "lvai_build_from_build_specification", Destructive = true, OpenWorld = true,
                   Title = "Build a project build specification")]
    [Description("""
        RPC BuildFromBuildSpecification. MUTATING: runs a build specification of a .lvproj and
        returns the generated files. Writes build output to disk and can take minutes - raise
        timeoutSeconds accordingly. This is the CI-shaped capability of the interface.
        """)]
    public async Task<string> BuildFromBuildSpecificationAsync(
        [Description(@"Absolute path to the .lvproj")] string projectPath,
        [Description("Exact name of the build specification as it appears in the project")]
        string buildSpecificationName,
        [Description("Local budget in seconds - builds are slow")] int timeoutSeconds = 900,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.BuildFromBuildSpecificationAsync(new BuildFromBuildSpecificationRequest
                {
                    ProjectPath = projectPath,
                    BuildSpecificationName = buildSpecificationName,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            return Json.Message(response, ("elapsedMs", JsonValue.Create(stopwatch.ElapsedMilliseconds)));
        });

    /// <summary>
    /// The path when it carries the extension it does NOT belong to, else null. Compared on the
    /// extension alone: a caller who swapped the two parameters still passed a real path, so there is
    /// nothing else to go on.
    /// </summary>
    internal static string? SwappedPath(string? path, string wrongExtension) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).Equals(wrongExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;

    /// <summary>
    /// Everything about an <c>lvai_open_file</c> call that can be judged WITHOUT LabVIEW: the error
    /// JSON when the arguments cannot mean what the caller intended, else null.
    ///
    /// EXTRACTED 2026-09-14, AND THE EXTRACTION IS THE POINT. These guards lived inside the async
    /// body, so none of the three was reachable from a test - and the third one did not exist,
    /// because nothing was watching that door. All three now fail here rather than in the field.
    ///
    /// They exist because LabVIEW's answer to every one of these is the same <c>Error 7, File not
    /// found</c>, which sends the reader to check the disk - the one place the fault is not.
    /// </summary>
    /// <summary>
    /// What to tell a caller whose open left NO ACTIVE PROJECT.
    ///
    /// THIS TEXT USED TO ASSERT TWO THINGS IT HAD NOT CHECKED, and it asserted them exactly where
    /// a reader is already confused. It opened "The open itself reported no error" while the
    /// branch was keyed on <c>projectBecameActive == false</c> ALONE - so on 2026-09-18 it said
    /// that beside an <c>errorCode</c> of <b>1025, Application Reference is invalid</b>, and then
    /// sent the reader after the foreground, which is the measured cause of a DIFFERENT failure.
    /// In the same sentence it claimed "this call already tried fronting it and opening again"
    /// while <c>foregroundRetry</c> was <c>null</c> and no retry had run.
    ///
    /// Same shape as every other field in this repository that could not tell two cases apart and
    /// was nevertheless the whole verdict. Both facts are now arguments.
    /// </summary>
    internal static string NoActiveProjectHint(int openErrorCode, bool retryRan)
    {
        // 1025 is its own diagnosis and does not belong in the foreground story at all. Measured
        // 2026-09-18: everything in the ADDON's application instance kept working - a placeholder
        // was generated, exported and installed in 382 ms - while everything needing the IDE's
        // instance failed, `{LV.Control} Replace` answering 1154 on a fresh stub and a reused one
        // alike. `lvai_status` said ok, dwarnCount 0, looksDegraded false throughout.
        // 1025 HAS A KNOWN CAUSE AND IT IS NOT THE ONE THIS HINT USED TO GIVE. Measured
        // 2026-09-18: a `.lvproj` that DOES NOT EXIST answers `Error 1025, Application Reference
        // is invalid`, proven by an A/B in one directory. The precheck refuses that before LabVIEW
        // sees it, so a 1025 arriving HERE is past the guard and unexplained - and this text says
        // so rather than repeating a story. The two diagnoses it replaces were both written in the
        // voice of a measurement and both wrong: "the IDE's application reference has gone
        // invalid" and "LabVIEW restarted under the session".
        if (openErrorCode == 1025)
            return "The open FAILED with Error 1025, Application Reference is invalid. The ONE "
                 + "measured cause of this is a .lvproj that does not exist (2026-09-18, A/B "
                 + "against a project in the same folder that opened cleanly) - and this tool "
                 + "refuses a missing path before calling LabVIEW, so that is not what happened "
                 + "here. Check the path is the project you meant and that it is readable; beyond "
                 + "that, this state is NOT explained. Do not read it as the foreground, and do "
                 + "not reach for a LabVIEW restart on the strength of it - a restart has been "
                 + "measured changing nothing for the missing-file case.";

        if (openErrorCode != 0)
            return $"The open FAILED with Error {openErrorCode} and NO PROJECT IS ACTIVE. Read " +
                   "errorMessage first: the active-project check below reports the state it " +
                   "found, not the reason the open did not take. Every call that reaches a class " +
                   "through Project:Active Project will now answer Error 1055.";

        return "The open itself reported no error and NO PROJECT IS ACTIVE, which is a different " +
               "failure - every call that reaches the class through Project:Active Project will " +
               "now answer Error 1055. This is NOT a path problem. The measured cause is LabVIEW " +
               "not having the foreground, and " +
               (retryRan
                   ? "this call already tried fronting it and opening again - see foregroundRetry."
                   : "this call could NOT try that: LabVIEW's main window was not found, so no " +
                     "retry ran and foregroundRetry is null. Front LabVIEW and call again.") +
               " Checked with scripts/lvai_active_project.xml, which only reads.";
    }

    internal static string? OpenFilePrecheck(
        string? viPath, string? viName, string? projectPath, string? projectName)
    {
        if (SwappedPath(viPath, ".lvproj") is { } projectAsVi)
            return Json.Error("badArguments",
                $"viPath is a project file ({projectAsVi}). A .lvproj must go in projectPath, "
                + "with projectName alongside it; passed as a VI, LabVIEW answers 'Error 7, File "
                + "not found'.");

        if (SwappedPath(projectPath, ".vi") is { } viAsProject)
            return Json.Error("badArguments",
                $"projectPath is a VI ({viAsProject}). A .vi must go in viPath, with viName "
                + "alongside it.");

        // NOTHING TO OPEN. The two guards above catch a path in the WRONG parameter; this one
        // catches a path in NO parameter, which is the same fault arriving through a door they do
        // not cover. Measured 2026-09-14: called with an undeclared argument name, all four fields
        // reached LabVIEW empty and LabVIEW answered `Error 7, File not found` - the exact
        // misleading answer those guards exist to prevent, about a file nobody named. Five calls
        // across three real paths, a refuted foreground hypothesis and a LabVIEW restart went into
        // believing it. The argument layer refuses an unrecognised NAME now, so this is the second
        // line rather than the first; it still earns its place, because an empty string passed
        // deliberately arrives here without ever having looked like a misspelling.
        if (viPath is not { Length: > 0 } && projectPath is not { Length: > 0 })
            return Json.Error("badArguments",
                "Neither viPath nor projectPath was given, so there is nothing to open. Passed "
                + "through, LabVIEW answers 'Error 7, File not found' about a file that was never "
                + "named, which reads as a broken installation rather than as a bad call.",
                new
                {
                    received = new { viPath, viName, projectPath, projectName },
                    hint = "A .vi goes in viPath with viName; a .lvproj goes in projectPath with "
                           + "projectName. There is no `path` or `filePath` parameter.",
                });

        // A PATH THAT DOES NOT EXIST, which is the fault this guard was missing and the most
        // expensive one it could have missed. Measured 2026-09-18 as a clean A/B in ONE directory:
        // `OpenProbe.lvproj` (exists) answers errorCode 0 with projectBecameActive true, and
        // `GibtsGarNicht.lvproj` beside it answers **Error 1025, Application Reference is
        // invalid** - a message about the IDE's application reference, for a missing FILE. It
        // reads as a broken LabVIEW, and it was believed: three calls, a LabVIEW restart, a client
        // restart and two written-up diagnoses ("the IDE's application reference has gone
        // invalid", then "LabVIEW restarted under the session") went past it before anyone checked
        // that the .lvproj was there. A VI is NOT the same case - a missing .vi answers the honest
        // `Error 7, File not found` - but both are checked here, because one File.Exists costs
        // nothing and a caller cannot be expected to know which of the two LabVIEW will lie about.
        foreach (var (label, path, sibling) in new[]
                 {
                     ("viPath", viPath, "viName"),
                     ("projectPath", projectPath, "projectName"),
                 })
        {
            if (path is not { Length: > 0 } || File.Exists(path)) continue;

            var isProject = label == "projectPath";
            return Json.Error("fileNotFound",
                $"{label} does not exist: {path}",
                new
                {
                    checkedPath = path,
                    directoryExists = Path.GetDirectoryName(path) is { Length: > 0 } dir
                                      && Directory.Exists(dir),
                    hint = isProject
                        ? "LabVIEW answers 'Error 1025, Application Reference is invalid' for a "
                          + ".lvproj that is not there - a message about the IDE, not about the "
                          + "file - so this is refused here instead. Measured 2026-09-18 against a "
                          + "project in the same folder that does exist and opened cleanly. Check "
                          + "the path before reading 1025 as a broken LabVIEW: a project may also "
                          + "live one directory up, or a class may belong to a project named after "
                          + "something else entirely."
                        : "LabVIEW answers 'Error 7, File not found' for a .vi that is not there, "
                          + $"which is honest - but the {sibling} you passed cannot make a missing "
                          + "file open, so the call is refused here rather than spent.",
                });
        }

        // A PROJECT LISTING ONE FILE TWICE IS REFUSED BEFORE LabVIEW SEES IT. LabVIEW answers
        // `Error 74` for it - a message about unflattening data, not about the project - and
        // loads nothing. Measured 2026-09-25 on the ATM cold build, three VIs listed both at
        // target level and in a folder. A file read costs nothing; the refusal names the entries.
        if (projectPath is { Length: > 0 }
            && LvClass.DuplicateViEntries(projectPath) is { Count: > 0 } twice)
            return Json.Error("duplicateProjectEntries",
                $"'{Path.GetFileName(projectPath)}' lists {twice.Count} file(s) a second time: " +
                string.Join(", ", twice.Select(d => $"'{d.Name}' (line {d.Line})")) +
                ". LabVIEW answers Error 74 for such a project and opens nothing.",
                new
                {
                    duplicates = twice.Select(d => new { name = d.Name, url = d.Url, line = d.Line }),
                    hint = "Remove the listed lines - they are the second entry for each file; the "
                           + "one inside a folder is the one to keep - or call "
                           + "lvai_add_vis_to_project with this projectPath, which removes them "
                           + "itself. The project is not open, so editing the file now is safe.",
                });

        return null;
    }

    [McpServerTool(Name = "lvai_open_file", Destructive = true, OpenWorld = true,
                   Title = "Open a VI or project in the LabVIEW IDE")]
    [Description("""
        RPC OpenFile. MUTATING (IDE state): opens a VI and/or a project in the running LabVIEW
        editor. Pass the VI pair, the project pair, or both. Harmless but visible to whoever is
        sitting in front of LabVIEW.
        THERE IS NO `path` OR `filePath` PARAMETER, and getting that wrong is expensive because the
        failure lies: the argument is DROPPED, all four fields reach LabVIEW empty, and LabVIEW
        answers `Error 7, File not found` about a file nobody named. Measured 2026-08-27 and again
        2026-09-14 - five identical Error 7 answers across three paths that plainly exist, a
        refuted foreground hypothesis and a pointless LabVIEW restart, while lvai_describe_project
        read the very same path with errorCode 0.
        THIS TEXT USED TO SAY `filePath` IS "folded onto the closest declared one" AND THAT IS FALSE.
        The fold normalises `_`, `-` and case only, so `vi_path` reaches `viPath` and `filePath`
        reaches nothing. Believing the old sentence sends you hunting for a path that was passed as
        a VI, which is the one thing that did not happen. Both holes are closed now - an unrecognised
        argument name is REFUSED by name, and a call with neither path is refused here - so this is
        history rather than a live trap.
        A .lvproj goes in `projectPath` WITH `projectName`; that pair returns No Error immediately.
        `No Error` DOES NOT MEAN A PROJECT BECAME ACTIVE, and that is not a quibble - almost
        everything that edits a class needs the project ACTIVE, not merely open. Measured
        2026-09-03: three opens in a row answered `No Error` and left no active project, so every
        following call answered `Error 1055`; what fixed it was giving the LabVIEW WINDOW the
        foreground, because Chrome had focus. Diagnosing that cost 270 s of wall clock for 2.9 s
        inside LabVIEW. So a project open now reads `Project:Active Project` back and reports
        `projectBecameActive`, with `errorKind: projectDidNotBecomeActive` and the cause named when
        it did not. Pass `checkActive: false` to skip the check, which costs one short helper run.
        THE FOREGROUND IS THE CAUSE OF ONE FAILURE, NOT OF EVERY ONE - read `errorCode` first.
        `Error 1025, Application Reference is invalid` means the IDE's application reference has
        gone invalid while the ADDON's instance keeps working, so a `1154` from a flatten, a bind
        or a retype in the same session is the same fault and re-running it cannot help; measured
        2026-09-18, with `lvai_status` green throughout. The `hint` used to open "The open itself
        reported no error" whatever the code said, because it was keyed on `projectBecameActive`
        alone.
        """)]
    public async Task<string> OpenFileAsync(
        [Description(@"Absolute path to the .vi, or empty")] string? viPath = null,
        [Description("VI name, or empty")] string? viName = null,
        [Description(@"Absolute path to the .lvproj, or empty")] string? projectPath = null,
        [Description("Project name, or empty")] string? projectName = null,
        [Description("""
            After opening a PROJECT, read Project:Active Project back and report whether one
            actually became active. On by default: `No Error` alone has been measured leaving no
            active project, and every class-editing call then fails with Error 1055 pointing
            nowhere useful. Ignored when no projectPath is given.
            """)]
        bool checkActive = true,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (OpenFilePrecheck(viPath, viName, projectPath, projectName) is { } refusal)
                return refusal;

            var response = await connection.InvokeAsync((c, t) =>
                c.OpenFileAsync(new OpenFileRequest
                {
                    ViPath = viPath ?? "",
                    ViName = viName ?? "",
                    ProjectPath = projectPath ?? "",
                    ProjectName = projectName ?? "",
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            if (projectPath is not { Length: > 0 } || !checkActive)
                return Json.Message(response);

            var (active, note, activePath) = await ProjectIsActiveAsync(timeoutSeconds, ct: ct);

            // THE MEASURED CAUSE IS THE FOREGROUND WINDOW, AND THAT IS AUTOMATABLE - this tool
            // reported the remedy as a human action ("bring its window to the front") from
            // 2026-09-03 until 2026-09-14, which stops an unattended run dead. Measured on a cold
            // build: the open answered No Error with projectBecameActive false, SetForegroundWindow
            // on LabVIEW's MainWindowHandle followed by one more open answered true.
            //
            // It is a RETRY, not a precondition: fronting a window is visible to whoever is at the
            // machine, so it happens only once the cheap read has already said the open did not
            // take. `foregroundRetry` says whether it ran and whether it helped.
            JsonNode? retry = null;
            if (active is false && LabViewWindow.BringToFront() is { } fronted)
            {
                var again = await connection.InvokeAsync((c, t) =>
                    c.OpenFileAsync(new OpenFileRequest
                    {
                        ViPath = viPath ?? "",
                        ViName = viName ?? "",
                        ProjectPath = projectPath ?? "",
                        ProjectName = projectName ?? "",
                    }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync,
                    ct);

                var (activeNow, noteNow, activePathNow) = await ProjectIsActiveAsync(timeoutSeconds, ct: ct);
                retry = new JsonObject
                {
                    ["ran"] = true,
                    ["frontedProcessId"] = fronted,
                    ["openErrorCode"] = again.ErrorCode,
                    ["projectBecameActive"] = activeNow,
                    ["note"] = activeNow is true
                        ? "The first open left no active project; fronting LabVIEW and opening " +
                          "again took. That is the measured cause, now handled here."
                        : "Fronting LabVIEW did not help either, so the cause is something else - " +
                          "read activeProjectCheck.",
                };
                active = activeNow;
                note = noteNow;
                activePath = activePathNow;
            }

            // WHICH project is active is REPORTED, not enforced. The reporting is the measured
            // win: an Error 1055 from a later class call used to say nothing about what LabVIEW
            // was looking at, and two arms of an A/B were spent on that on 2026-09-18.
            //
            // The MISMATCH below has never been observed firing, and that is why it only
            // annotates rather than turning `projectBecameActive` false. Probed the same day:
            // opening project A while B was active SWITCHED correctly, so the case this was
            // written for does not arise that way. A guard that refuses a working call on an
            // unproven rule is worse than the silence it replaces - and two paths can differ
            // by spelling alone.
            var wanted = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetFullPath(projectPath);
            var mismatch = active is true && wanted is not null &&
                           !string.IsNullOrWhiteSpace(activePath) &&
                           !string.Equals(Path.GetFullPath(activePath), wanted,
                                          StringComparison.OrdinalIgnoreCase);
            if (mismatch)
                note = $"A project is active but it is NOT the one asked for: '{activePath}' is " +
                       $"active, '{wanted}' was requested. OBSERVED 2026-09-18, and the trigger is " +
                       "an open that FAILED: the requested project never loaded and the one from " +
                       "before is still active, so read errorCode first - this line describes the " +
                       "state found, not a switch that went wrong. On a SUCCESSFUL open a second " +
                       "project switches correctly, which is why this is reported and not treated " +
                       "as a failure. If a later call answers Error 1055, close the active project " +
                       "and open again.";

            return Json.Message(response,
                ("projectBecameActive", JsonValue.Create(active)),
                ("activeProjectPathDiffers", mismatch ? JsonValue.Create(true) : null),
                ("activeProjectPath", JsonValue.Create(activePath)),
                ("activeProjectCheck", JsonValue.Create(note)),
                ("foregroundRetry", retry),
                ("errorKind", active is false
                    ? JsonValue.Create("projectDidNotBecomeActive") : null),
                ("hint", active is false
                    ? JsonValue.Create(NoActiveProjectHint(response.ErrorCode, retry is not null))
                    : null));
        });

    /// <summary>
    /// Whether a project is active, by reading <c>Project:Active Project</c> and closing the
    /// reference again. <c>Error 1055</c> is the ANSWER - no project active - and not a fault.
    ///
    /// Returns null when the check itself could not run, which must never be reported as "no
    /// project": a missing helper is not evidence about the IDE's state.
    /// </summary>
    internal async Task<(bool? Active, string Note, string? Path)> ProjectIsActiveAsync(
        int timeoutSeconds, CancellationToken ct)
    {
        var source = StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, "lvai_active_project.xml") : null;
        if (source is null || !File.Exists(source))
            return (null, "not checked - lvai_active_project.xml was not found beside the exe.", null);

        var helper = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers",
                                  "lvai_active_project.vi");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
            if (!File.Exists(helper))
            {
                await new BulkTools(connection).GenerateViAsync(
                    source, helper, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct: ct);
                if (!File.Exists(helper))
                    return (null, "not checked - the read-only helper could not be generated.", null);
            }

            var run = await new RunTools(connection).RunViAndReadValuesAsync(
                helper, "{}", includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct: ct);

            var values = (JsonNode.Parse(run) as JsonObject)?["values"] as JsonObject;
            var code = (values?["code"] as JsonObject)?["value"]?.GetValue<string>();
            if (!int.TryParse(code, out var errorCode))
                return (null, "not checked - the helper returned no error code.", null);

            // WHICH project, not merely whether one is active. `a project is active` is not the
            // question a caller is asking - it wants ITS project active, and the two differ after
            // any tool that opens and closes a project of its own.
            var activePath = (values?["project path"] as JsonObject)?["value"]?.GetValue<string>();
            return errorCode == 0
                ? (true, "a project is active: " +
                         (string.IsNullOrWhiteSpace(activePath) ? "(path not reported)" : activePath),
                   activePath)
                : (false, $"NO project is active - Project:Active Project answered {errorCode}. " +
                          "1055 is the expected code for that state.", null);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return (null, $"not checked - {failure.Message}", null);
        }
    }

    [McpServerTool(Name = "lvai_find_palette_item", Destructive = true,
                   Title = "Highlight a palette item in the IDE")]
    [Description("""
        RPC FindPaletteItem. MUTATING (IDE state): makes LabVIEW reveal/highlight the palette
        item with the given GUID. Purely a UI action - useful to confirm a GUID resolves.
        """)]
    public async Task<string> FindPaletteItemAsync(
        [Description("Palette item GUID")] string guid,
        [Description("Local budget in seconds")] int timeoutSeconds = 60,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.FindPaletteItemAsync(new FindPaletteItemRequest { Guid = guid },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_drop_palette_item", Destructive = true, OpenWorld = true,
                   Title = "Drop a palette item onto a VI")]
    [Description("""
        RPC DropPaletteItem. MUTATING: places the palette item with the given GUID onto the
        block diagram of the target VI. This edits real code. Prefer the AIXML path
        (lvai_apply_aixml_to_vi) when you need control over placement and wiring - a drop
        gives you neither.
        """)]
    public async Task<string> DropPaletteItemAsync(
        [Description("Palette item GUID")] string guid,
        [Description(@"Absolute path to the target .vi")] string? viPath = null,
        [Description("VI name, or empty")] string? viName = null,
        [Description(@"Absolute path to the owning .lvproj, or empty")] string? projectPath = null,
        [Description("Project name, or empty")] string? projectName = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.DropPaletteItemAsync(new DropPaletteItemRequest
                {
                    Guid = guid,
                    ViPath = viPath ?? "",
                    ViName = viName ?? "",
                    ProjectPath = projectPath ?? "",
                    ProjectName = projectName ?? "",
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_log_usage_data", Destructive = true, Idempotent = false,
                   Title = "Write a usage-telemetry key/value")]
    [Description("""
        RPC LogUsageData. Writes a key/value pair into LabVIEW's usage telemetry. Included for
        completeness of the interface; it emits analytics data, so there is rarely a reason to
        call it.
        """)]
    public async Task<string> LogUsageDataAsync(
        [Description("Telemetry key")] string key,
        [Description("Telemetry value")] string value,
        [Description("Local budget in seconds")] int timeoutSeconds = 30,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.LogUsageDataAsync(new LogUsageDataRequest { Key = key, Value = value },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

}
