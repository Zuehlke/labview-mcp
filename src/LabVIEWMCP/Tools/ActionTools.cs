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
        CancellationToken ct = default,
        [Description(AixmlBackend.ParameterDescription + """
             On pylabview the VI runs over LabVIEW's ActiveX server - the route of
            lvai_run_vi_and_read_values, whose answer comes back: inputs converted to each
            control's own type, every indicator read back with its type, NI's service not asked.
            """)]
        string? backend = null)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_run_vi_as_top_level", out var choice, out var bad))
            return AixmlBackend.Refusal(bad);
        if (choice.Name == AixmlBackend.PyLabview)
        {
            // the ActiveX run: refused on a broken VI, run with a deadline and an Abort
            var run = await new RunTools(connection).RunViAndReadValuesAsync(
                viPath, inputsJson, timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct);
            if (JsonNode.Parse(run) is JsonObject answer)
            {
                answer["ranAs"] = "lvai_run_vi_and_read_values over ActiveX - NI's RunVIAsTopLevel is " +
                                  "not reached on this backend, so values come back typed, not as strings";
                return AixmlTools.WithBackend(Json.Document(answer), choice);
            }
            return run;
        }
        return AixmlTools.WithBackend(await RunViAsTopLevelGrpcAsync(viPath, inputsJson, timeoutSeconds, ct), choice);
    }

    private async Task<string> RunViAsTopLevelGrpcAsync(string viPath, string? inputsJson, int timeoutSeconds,
                                                        CancellationToken ct) =>
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
                   : "this call could NOT try that: LabVIEW could not be fronted - it has no " +
                     "main window, or Windows refused SetForegroundWindow to a background " +
                     "process - so no retry ran and foregroundRetry is null. Front LabVIEW and " +
                     "call again.") +
               " Checked with scripts/lvai_active_project.xml, which only reads.";
    }

    /// <summary>The paths in a viPaths argument: one per line, blank lines and padding dropped.</summary>
    /// <summary>
    /// A rooted path in Windows form - backslashes, no `..` - or the input unchanged when it is
    /// empty or relative. LabVIEW's OpenFile does not accept forward slashes (Error 1025).
    /// </summary>
    internal static string? NormalizedPath(string? path) =>
        path is { Length: > 0 } && Path.IsPathRooted(path.Trim()) ? Path.GetFullPath(path.Trim()) : path;

    internal static List<string> ViPathList(string? viPaths) =>
        [.. (viPaths ?? "").Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

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

        // TWO SAME-NAMED SIBLING FOLDERS ARE THE SAME Error 74, and no file is listed twice. Measured
        // 2026-09-26 on the eighth ATM build as an A/B on two throwaway projects: two empty `SubVIs`
        // folders gave 74, one opened with errorCode 0. It happened twice in that build, both times
        // an empty `<Item … Type="Folder"/>` beside the populated folder a listing step had added.
        if (projectPath is { Length: > 0 }
            && LvClass.DuplicateSiblingFolders(projectPath) is { Count: > 0 } folders)
            return Json.Error("duplicateProjectFolders",
                $"'{Path.GetFileName(projectPath)}' has two sibling folders of one name: " +
                string.Join(", ", folders.Select(d => $"'{d.Name}' (line {d.Line})")) +
                ". LabVIEW answers Error 74 for such a project and opens nothing.",
                new
                {
                    duplicates = folders.Select(d => new { name = d.Name, line = d.Line }),
                    hint = "Remove the EMPTY one of each pair - the listed line is the second "
                           + "occurrence; keep whichever holds items. The project is not open, so "
                           + "editing the file now is safe.",
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
        CancellationToken ct = default,
        [Description("""
            MORE VIs to open in the same call, ONE ABSOLUTE PATH PER LINE - each through the same
            project pair when one is given, which is what makes project-local code resolvable as
            an AIXML Call target. The first one stands in for viPath when that is empty. Added
            2026-09-25: loading five callees for a direct Call cost five round trips in the third
            ATM cold build. `alsoOpened` reports each one's own error code.
            """)]
        string? viPaths = null,
        [Description(AixmlBackend.ParameterDescription + """
             On pylabview everything goes over LabVIEW's ActiveX server: a project with a composed
            helper (Project.Open plus its window), checked for being ACTIVE the same way; a VI
            with no project has its front panel opened in LabVIEW's main instance, and with a
            project it is opened THROUGH it (lvai_open_vi_in_project). LabVIEW must already be
            running.
            """)]
        string? backend = null)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_open_file", out var choice, out var badBackend))
            return AixmlBackend.Refusal(badBackend);
        if (choice.Name == AixmlBackend.PyLabview)
            return AixmlTools.WithBackend(await Rpc.GuardAsync(() =>
                OpenProjectOverComAsync(viPath, projectPath, viPaths, checkActive, timeoutSeconds, ct)),
                choice);
        return AixmlTools.WithBackend(await Rpc.GuardAsync(async () =>
        {
            // FORWARD SLASHES ARE NORMALISED FIRST. Measured 2026-09-30 as an A/B on one existing
            // project: `C:/Temp/.../Accept.lvproj` answered Error 1025 twice - the code LabVIEW gives
            // a project it cannot find - and the same path with backslashes opened at once. .NET's
            // File.Exists accepts both, so the pre-check passed and the 1025 read like a broken IDE;
            // it cost a LabVIEW restart that was never needed.
            viPath = NormalizedPath(viPath);
            projectPath = NormalizedPath(projectPath);
            var extra = ViPathList(viPaths).Select(p => NormalizedPath(p)!).ToList();
            if (viPath is not { Length: > 0 } && extra.Count > 0)
            {
                viPath = extra[0];
                viName = Path.GetFileName(viPath);
                extra.RemoveAt(0);
            }
            foreach (var more in extra.Where(p => !File.Exists(p)))
                return Json.Error("fileNotFound", $"viPaths names a file that does not exist: {more}",
                    new { checkedPath = more, openedNothing = true });

            if (extra.Count > 0)
            {
                // the backend this call resolved to, passed on: left out, the inner call resolved it
                // afresh as a CLIENT call of lvai_open_file and took the server's default - pylabview
                // on this station - and a grpc call with viPaths was refused as "not built for the
                // pylabview backend" (the gRPC cold build of LayoutCheck, 2026-10-16)
                var first = await OpenFileAsync(viPath, viName, projectPath, projectName,
                                                checkActive, timeoutSeconds, ct, backend: choice.Name);
                var answer = JsonNode.Parse(first) as JsonObject;
                if (answer is null || answer["errorCode"]?.GetValue<int>() is not 0)
                    return first;                        // the first refusal says why; stop there
                var also = new JsonArray();
                foreach (var more in extra)
                {
                    var r = await connection.InvokeAsync((c, t) =>
                        c.OpenFileAsync(new OpenFileRequest
                        {
                            ViPath = more,
                            ViName = Path.GetFileName(more),
                            ProjectPath = projectPath ?? "",
                            ProjectName = projectName ?? "",
                        }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
                    also.Add(new JsonObject
                    {
                        ["viPath"] = more,
                        ["errorCode"] = r.ErrorCode,
                        ["errorMessage"] = r.ErrorMessage,
                    });
                }
                answer["alsoOpened"] = also;
                answer["allOpened"] = also.All(a => a!["errorCode"]!.GetValue<int>() == 0);
                return Json.Document(answer);
            }

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
        }), choice);
    }

    /// <summary>
    /// lvai_open_file on the pylabview backend: a PROJECT opened by the composed lvai_open_project
    /// helper over ActiveX - Project.Open and Open Window, in LabVIEW's main instance, which is the
    /// IDE's. Measured 2026-10-10: the project was the active one at once, and the composed close
    /// closed it again. The answer keeps lvai_open_file's fields.
    /// </summary>
    private async Task<string> OpenProjectOverComAsync(string? viPath, string? projectPath,
                                                       string? viPaths, bool checkActive,
                                                       int timeoutSeconds, CancellationToken ct)
    {
        viPath = NormalizedPath(viPath);
        projectPath = NormalizedPath(projectPath);
        // the VIs, viPath first: opened LOOSE in LabVIEW's main instance with no project, and
        // THROUGH the project once it is open with one - NI's OpenFile does the same with a pair.
        // Refused here until 2026-10-16, which sent every caller that needed a VI loaded to NI's
        // service.
        var vis = new List<string>();
        if (viPath is { Length: > 0 }) vis.Add(viPath);
        vis.AddRange(ViPathList(viPaths).Select(p => NormalizedPath(p)!).Where(p => !vis.Contains(p)));
        foreach (var vi in vis.Where(p => !File.Exists(p)))
            return Json.Error("fileNotFound", $"A VI to open does not exist: {vi}",
                new { checkedPath = vi, openedNothing = true });
        if (projectPath is not { Length: > 0 })
        {
            if (vis.Count == 0)
                return Json.Error("badArguments", "Name the .lvproj in projectPath, or a VI in viPath / viPaths.");
            return Json.Document(await OpenVisOverComAsync(vis, null, timeoutSeconds, ct));
        }
        if (OpenFilePrecheck(null, null, projectPath, Path.GetFileName(projectPath)) is { } refusal)
            return refusal;

        var (helper, problem) = await ComposedHelpers.EnsureAsync("lvai_open_project.xml", timeoutSeconds, ct);
        if (helper is null)
            return Json.Error("helperNotComposable", problem!, new { projectPath });

        async Task<(int Code, string Source, string? Failure)> OpenAsync()
        {
            var run = await new RunTools(connection).RunViAndReadValuesAsync(
                helper, new JsonObject { ["project path"] = projectPath }.ToJsonString(),
                timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct);
            var answer = JsonNode.Parse(run) as JsonObject;
            if (answer?["ok"]?.GetValue<bool>() is not true) return (-1, "", run);
            var xml = answer["values"]?["error out"]?["xml"]?.GetValue<string>() ?? "";
            var code = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>code</Name>\s*<Val>(-?\d+)");
            var source = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>source</Name>\s*<Val>([^<]*)");
            return (code.Success ? int.Parse(code.Groups[1].Value) : -1,
                    source.Success ? source.Groups[1].Value : "", null);
        }

        var (errorCode, errorSource, failure) = await OpenAsync();
        if (failure is not null) return failure;
        var result = new JsonObject
        {
            ["errorCode"] = errorCode,
            ["errorMessage"] = errorCode == 0 ? "No Error" : errorSource,
            ["via"] = "ActiveX",
        };
        if (errorCode != 0) return Json.Document(result);
        if (!checkActive)
        {
            if (vis.Count > 0) AddVisOpened(result, await OpenVisOverComAsync(vis, projectPath, timeoutSeconds, ct));
            return Json.Document(result);
        }

        var (active, note, activePath) = await ProjectIsActiveAsync(timeoutSeconds, ct, AixmlBackend.PyLabview);
        JsonObject? retry = null;
        if (active is false && LabViewWindow.BringToFront() is { } fronted)
        {
            var (againCode, _, _) = await OpenAsync();
            (active, note, activePath) = await ProjectIsActiveAsync(timeoutSeconds, ct, AixmlBackend.PyLabview);
            retry = new JsonObject
            {
                ["ran"] = true, ["frontedProcessId"] = fronted, ["openErrorCode"] = againCode,
                ["projectBecameActive"] = active,
            };
        }
        // A PROJECT ALREADY LOADED BUT NOT ACTIVE stays inactive however often it is opened:
        // Project.Open hands back LabVIEW's existing reference and Open Window activates nothing.
        // Measured 2026-10-11, 5 of 5 opens after lvai_create_class had left the project in that
        // state, with fronting refused by Windows. A fresh open made it active at once
        // (2026-10-10), so save and close it BY PATH - nothing is active for the usual close to
        // reach - and open it again.
        JsonObject? reopen = null;
        if (active is false)
        {
            var (closer, closerProblem) = await ComposedHelpers.EnsureAsync(
                CloseProjectHelperAixmlFileName, timeoutSeconds, ct);
            if (closer is null)
                reopen = new JsonObject { ["ran"] = false, ["reason"] = closerProblem };
            else
            {
                var closed = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(
                    closer, new JsonObject { ["project path"] = projectPath }.ToJsonString(),
                    timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct)) as JsonObject;
                var closeXml = closed?["values"]?["error out"]?["xml"]?.GetValue<string>() ?? "";
                var closeCode = System.Text.RegularExpressions.Regex.Match(
                    closeXml, @"<Name>code</Name>\s*<Val>(-?\d+)");
                var (againCode, _, _) = await OpenAsync();
                (active, note, activePath) = await ProjectIsActiveAsync(timeoutSeconds, ct, AixmlBackend.PyLabview);
                reopen = new JsonObject
                {
                    ["ran"] = true,
                    ["closeErrorCode"] = closeCode.Success ? int.Parse(closeCode.Groups[1].Value) : null,
                    ["openErrorCode"] = againCode,
                    ["projectBecameActive"] = active,
                };
            }
        }
        result["projectBecameActive"] = active;
        result["activeProjectPath"] = activePath;
        result["activeProjectCheck"] = note;
        result["foregroundRetry"] = retry;
        result["reopenRetry"] = reopen;
        if (active is false)
        {
            result["errorKind"] = "projectDidNotBecomeActive";
            result["hint"] = NoActiveProjectHint(errorCode, retry is not null);
        }
        if (vis.Count > 0) AddVisOpened(result, await OpenVisOverComAsync(vis, projectPath, timeoutSeconds, ct));
        return Json.Document(result);
    }

    /// <summary>The composed helper that opens a VI through its project (pylabview backend).</summary>
    internal const string OpenViInProjectHelperAixmlFileName = "lvai_open_vi_in_project.xml";

    /// <summary>
    /// Opens each VI's front panel over ActiveX: with no project in LabVIEW's main instance,
    /// with one through the composed <c>lvai_open_vi_in_project</c> helper, in the project's own
    /// instance. The answer has lvai_open_file's shape for a VI: errorCode/errorMessage of the
    /// first, <c>alsoOpened</c> and <c>allOpened</c> for the rest.
    /// </summary>
    private async Task<JsonObject> OpenVisOverComAsync(List<string> vis, string? projectPath,
                                                       int timeoutSeconds, CancellationToken ct)
    {
        string? helper = null;
        if (projectPath is not null)
        {
            var (built, problem) = await ComposedHelpers.EnsureAsync(OpenViInProjectHelperAixmlFileName, timeoutSeconds, ct);
            if (built is null)
                return new JsonObject { ["errorCode"] = -1, ["errorMessage"] = problem, ["errorKind"] = "helperNotComposable" };
            helper = built;
        }
        var each = new JsonArray();
        foreach (var vi in vis)
        {
            int code;
            string message;
            if (helper is null)
            {
                try
                {
                    var open = await LabViewCom.OpenFrontPanelAsync(vi, timeoutSeconds, ct);
                    (code, message) = open ? (0, "No Error") : (-1, "The front panel did not read open afterwards.");
                }
                catch (LabViewCom.ComException ex)
                {
                    (code, message) = (LabViewCom.ErrorCodeOf(ex.Message), ex.Message);
                }
            }
            else
            {
                var run = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(
                    helper, new JsonObject { ["project path"] = projectPath, ["vi path"] = vi }.ToJsonString(),
                    timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct)) as JsonObject;
                var xml = run?["values"]?["error out"]?["xml"]?.GetValue<string>() ?? "";
                var c = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>code</Name>\s*<Val>(-?\d+)");
                var src = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>source</Name>\s*<Val>([^<]*)");
                if (run?["ok"]?.GetValue<bool>() is not true)
                    (code, message) = (-1, run?.ToJsonString() ?? "the helper did not run");
                else if (c.Success && int.Parse(c.Groups[1].Value) is var n && n != 0)
                    (code, message) = (n, src.Success ? src.Groups[1].Value : "");
                else
                    (code, message) = (0, "No Error");
            }
            each.Add(new JsonObject { ["viPath"] = vi, ["errorCode"] = code, ["errorMessage"] = message });
        }
        var first = (JsonObject)each[0]!;
        var answer = new JsonObject
        {
            ["errorCode"] = first["errorCode"]!.GetValue<int>(),
            ["errorMessage"] = first["errorMessage"]!.GetValue<string>(),
            ["via"] = "ActiveX",
            ["openedThrough"] = projectPath is null ? "LabVIEW's main instance, no project" : "the project's own instance",
        };
        if (each.Count > 1)
        {
            answer["alsoOpened"] = new JsonArray([.. each.Skip(1).Select(e => e!.DeepClone())]);
            answer["allOpened"] = each.All(e => e!["errorCode"]!.GetValue<int>() == 0);
        }
        return answer;
    }

    /// <summary>A project open's answer, with the VIs opened after it folded in.</summary>
    private static void AddVisOpened(JsonObject result, JsonObject vis)
    {
        result["viErrorCode"] = vis["errorCode"]?.DeepClone();
        result["viErrorMessage"] = vis["errorMessage"]?.DeepClone();
        result["openedThrough"] = vis["openedThrough"]?.DeepClone();
        if (vis["alsoOpened"] is { } also) result["alsoOpened"] = also.DeepClone();
        if (vis["allOpened"] is { } all) result["allOpened"] = all.DeepClone();
        if (vis["errorCode"]?.GetValue<int>() is not 0 && result["errorCode"]?.GetValue<int>() is 0)
        {
            result["errorCode"] = vis["errorCode"]!.DeepClone();
            result["errorMessage"] = vis["errorMessage"]!.DeepClone();
        }
    }

    /// <summary>Saves and closes a project by path - the composed route's reopen step.</summary>
    internal const string CloseProjectHelperAixmlFileName = "lvai_close_project.xml";

    /// <summary>
    /// Save and close the project at <paramref name="projectPath"/> whether or not it is the active
    /// one - the composed <c>lvai_close_project</c> helper over ActiveX, LabVIEW's main instance,
    /// which is where projects are opened on either backend. Used before a FILE edit of a project
    /// LabVIEW may hold loaded but inactive, which no "close the active project" reaches. A project
    /// LabVIEW does not hold is opened, saved and closed again - harmless, a few seconds.
    /// </summary>
    internal async Task<JsonObject> CloseProjectByPathAsync(string projectPath, int timeoutSeconds,
                                                            CancellationToken ct)
    {
        var (closer, problem) = await ComposedHelpers.EnsureAsync(
            CloseProjectHelperAixmlFileName, timeoutSeconds, ct);
        if (closer is null) return new JsonObject { ["ran"] = false, ["reason"] = problem };

        var closed = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(
            closer, new JsonObject { ["project path"] = projectPath }.ToJsonString(),
            timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct)) as JsonObject;
        var xml = closed?["values"]?["error out"]?["xml"]?.GetValue<string>() ?? "";
        var code = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>code</Name>\s*<Val>(-?\d+)");
        return new JsonObject
        {
            ["ran"] = closed?["ok"]?.GetValue<bool>() == true,
            ["errorCode"] = code.Success ? int.Parse(code.Groups[1].Value) : null,
            ["via"] = "ActiveX",
        };
    }

    /// <summary>
    /// Whether a project is active, by reading <c>Project:Active Project</c> and closing the
    /// reference again. <c>Error 1055</c> is the ANSWER - no project active - and not a fault.
    ///
    /// Returns null when the check itself could not run, which must never be reported as "no
    /// project": a missing helper is not evidence about the IDE's state.
    /// </summary>
    internal async Task<(bool? Active, string Note, string? Path)> ProjectIsActiveAsync(
        int timeoutSeconds, CancellationToken ct, string? backend = null)
    {
        // unset means the route's own choice - inherited when the calling tool runs on
        // pylabview, grpc otherwise
        backend ??= AixmlBackend.Inherited.Value?.Name ?? AixmlBackend.Grpc;
        if (backend == AixmlBackend.PyLabview)
            return await ProjectIsActiveOverComAsync(timeoutSeconds, ct);

        var source = StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, "lvai_active_project.xml") : null;
        if (source is null || !File.Exists(source))
            return (null, "not checked - lvai_active_project.xml was not found beside the exe.", null);

        var helper = Path.Combine(AixmlBackend.HelperFolder(),
                                  "lvai_active_project.vi");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
            if (!File.Exists(helper))
            {
                await new BulkTools(connection).GenerateViAsync(
                    source, helper, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct: ct, measureDiagram: false);
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

    /// <summary>
    /// The same check with the COMPOSED lvai_active_project helper over ActiveX - the pylabview
    /// backend's. The run answer has the gRPC shape, so it is read the same way.
    /// </summary>
    private async Task<(bool? Active, string Note, string? Path)> ProjectIsActiveOverComAsync(
        int timeoutSeconds, CancellationToken ct)
    {
        var (helper, problem) = await ComposedHelpers.EnsureAsync("lvai_active_project.xml", timeoutSeconds, ct);
        if (helper is null) return (null, "not checked - " + problem, null);
        var run = await new RunTools(connection).RunViAndReadValuesAsync(
            helper, "{}", timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct);
        var values = (JsonNode.Parse(run) as JsonObject)?["values"] as JsonObject;
        var code = (values?["code"] as JsonObject)?["value"]?.GetValue<string>();
        if (!int.TryParse(code, out var errorCode))
            return (null, "not checked - the helper returned no error code over ActiveX.", null);
        var activePath = (values?["project path"] as JsonObject)?["value"]?.GetValue<string>();
        return errorCode == 0
            ? (true, "a project is active: " +
                     (string.IsNullOrWhiteSpace(activePath) ? "(path not reported)" : activePath), activePath)
            : (false, $"NO project is active - Project:Active Project answered {errorCode}. " +
                      "1055 is the expected code for that state.", null);
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
