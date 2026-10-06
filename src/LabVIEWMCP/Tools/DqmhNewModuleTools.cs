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
/// Creating a DQMH module - Tools > DQMH > Module > Add New DQMH Module, with NO dialog.
///
/// Delacor's `Script New Module.vi` takes plain values, so a module never needed the dialog; until
/// 2026-10-06 it was driven over VI Server by path from `scripts/lvdqmh_new_module.xml` (19 stages,
/// 2239 px, uids in LabVIEW's reserved range) by the DQMH agent, with no tool. The helper is now a
/// static wrapper like every other DQMH function (docs/dqmh-scripting.md section 9), and this is
/// its tool: a dry run reads the station's module-type CATALOGUE, the type is matched by NAME -
/// never by index, because module types are pluggable and the list differs per station - and the
/// module is written into `<project folder>\Libraries\<Name>`, Delacor's own layout.
/// </summary>
[McpServerToolType]
internal sealed class DqmhNewModuleTools(LvaiConnection connection)
{
    private const string Helper = "lvdqmh_new_module";
    private const string NewModuleDir = @"project\Delacor\DQMH\_DQMH New Module\";

    internal static readonly string[] Targets =
    [
        NewModuleDir + "Get Module Type Info.vi",
        NewModuleDir + "Default Module Icon.vi",
        NewModuleDir + "Script New Module.vi",
    ];

    [McpServerTool(Name = "lvai_dqmh_new_module", Destructive = true, OpenWorld = true,
        Title = "Create a DQMH module")]
    [Description("""
        MUTATING: creates a DQMH module in the ACTIVE project with NO dialog - Delacor's own
        Script New Module.vi through a generated wrapper, the result of Tools > DQMH > Module > Add
        New DQMH Module. About fifty files are written into `<project folder>\Libraries\<Name>`
        (Delacor's layout; `saveFolder` overrides it) and the project lists the library under a
        `<Name> Module` folder with the API tester at target level.
        THE MODULE TYPE IS A NAME from the station's catalogue - Singleton, Cloneable, and whatever
        add-ons or templates (lvai_dqmh_create_module_template) contribute; `catalogue` in every
        answer lists them, and a name that is not there is refused. Refused too while anything in
        the project is unsaved, or - Delacor's own checks - when the target folder already holds a
        LabVIEW file at its top level or the project a library of that name. The answer verifies the module from the files: library, Main.vi,
        tester, Do Something present or absent as asked, and both VIs' exec state.
        """)]
    public async Task<string> NewModuleAsync(
        [Description("Module name, e.g. 'Heater' - becomes Heater.lvlib")] string moduleName,
        [Description("Module type by NAME from the station's catalogue, e.g. 'Singleton' or 'Cloneable'")]
        string moduleType = "Singleton",
        [Description("Keep Delacor's Do Something example events")] bool includeDoSomething = true,
        [Description("Folder for the module's files, relative to the project folder or absolute; empty means <project folder>\\Libraries\\<Name>")]
        string saveFolder = "",
        [Description("Local budget in seconds; Delacor takes 20-45 s to script a module")]
        int timeoutSeconds = 600,
        [Description("Answer within this many seconds; past it the module is still scripted in the " +
                     "server and the SAME call again collects the answer (the client gives up at 60 s)")]
        int answerWithinSeconds = 45,
        CancellationToken ct = default)
    {
        var key = string.Join("|", (moduleName ?? "").Trim(), (moduleType ?? "").Trim(),
            includeDoSomething, (saveFolder ?? "").Trim());
        return await ResumableCall.RunAsync(Helper, key, TimeSpan.FromSeconds(answerWithinSeconds),
            () => CreateAsync(moduleName, moduleType, includeDoSomething, saveFolder, timeoutSeconds),
            "Call lvai_dqmh_new_module again with the SAME arguments: it waits for this run and " +
            "returns its answer. Do not start other LabVIEW work meanwhile, and do not take a " +
            "moduleExists refusal on a new call as a failure - the module may be the one this run wrote.",
            ct);
    }

    /// <summary>
    /// The whole creation, run detached from the request (CancellationToken.None): a client that
    /// gives up at 60 s must not cancel a scripting run half-way.
    /// </summary>
    private async Task<string> CreateAsync(string? moduleName, string? moduleType,
        bool includeDoSomething, string? saveFolder, int timeoutSeconds)
    {
        var ct = CancellationToken.None;
        return await Rpc.GuardAsync(async () =>
        {
            var name = (moduleName ?? "").Trim();
            if (name.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
            if (DqmhHeadless.EventRequestProblem(name, 0, "", [], []) is { } problem)
                return Json.Error("badArguments", problem.Replace("The event name", "moduleName"));

            if (StatusTools.ScriptsDirectory() is not { } scripts
                || !File.Exists(Path.Combine(scripts, Helper + ".xml")))
                return Json.Error("scriptsMissing", $"{Helper}.xml was not found in the scripts folder.");
            if (DqmhHeadless.TargetPaths(Targets) is not { } targets)
                return Json.Error("dqmhMissing",
                    "Delacor DQMH is not installed: Script New Module.vi was not found.",
                    new { lookedFor = Targets });

            var stopwatch = Stopwatch.StartNew();
            var steps = new JsonArray();
            var (active, activeNote, projectPath) =
                await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
            if (active is not true || string.IsNullOrEmpty(projectPath))
                return Json.Error("noActiveProject",
                    "No project is active. A DQMH module is created INTO the active project - open " +
                    "it with lvai_open_file (projectPath + projectName) first.",
                    new { activeProjectCheck = activeNote });
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var folder = string.IsNullOrWhiteSpace(saveFolder)
                ? Path.Combine(projectDirectory, "Libraries", name)
                : Path.GetFullPath(Path.Combine(projectDirectory, saveFolder.Trim()));

            if (FolderProblem(folder, name, projectPath) is { } folderProblem)
                return Json.Error("moduleExists", folderProblem, new
                {
                    folder,
                    hint = File.Exists(Path.Combine(folder, name + ".lvlib"))
                        ? $"{name}.lvlib is already in that folder - if an earlier call timed out on " +
                          "the client, that call created it. Check it with lvai_exec_state on its " +
                          "Main.vi and API tester rather than creating it again."
                        : null,
                });

            var headless = new DqmhHeadless(connection);
            var dqmh = new DqmhTools(connection);
            if (await headless.EnsureWrapperAsync(scripts, Helper, targets, projectPath, steps,
                    timeoutSeconds, ct) is { } wrapperError)
                return wrapperError;

            // ---- 1. dry run: the station's catalogue -------------------------------------------
            var inputs = new Dictionary<string, string>
            {
                ["Module Name"] = name,
                ["Module Save Path"] = folder,
                ["Module Type Index"] = "0",
                ["Include Do Something"] = includeDoSomething ? "true" : "false",
                ["Script?"] = "false",
            };
            var (dry, dryHelperError) = await dqmh.RunDetailedAsync(scripts, Helper, inputs,
                timeoutSeconds, ct);
            if (dry is null)
                return Json.Error("helperMissing", $"{Helper} could not be run.", new { steps });
            if (dryHelperError is not null)
                return Json.Error("helperFailed", "The run helper stopped before the wrapper ran.",
                    new { helperError = dryHelperError, steps });
            var catalogue = DqmhHeadless.Listed(dry, "Type Strings");
            var descriptions = DqmhHeadless.Listed(dry, "Descriptions");
            var typeIndex = MatchType(catalogue, moduleType);
            var detail = new JsonObject
            {
                ["catalogue"] = DqmhHeadless.Array(catalogue),
                ["descriptions"] = DqmhHeadless.Array(descriptions),
                ["steps"] = steps,
            };
            if (DqmhTools.Failed(dry) is { } dryError)
                return Json.Error("catalogueFailed", "Get Module Type Info.vi answered an error.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["error"] = dryError }));
            if (typeIndex < 0)
                return Json.Error("moduleTypeNotFound",
                    $"'{moduleType}' is not a module type on this station - `catalogue` lists them.",
                    detail);

            // A TEMPLATE type: Script New Module opens the template's library by path, and a
            // library of the same NAME already in the project makes that `Library.Open` answer
            // 56003 - measured 2026-10-06, failing beside the module the template was made from
            // and succeeding in an empty project. Refused here with the name, before anything runs.
            string? templateLibrary = null;
            if (typeIndex >= BuiltInTypes)
            {
                templateLibrary = await TemplateLibraryAsync(dqmh, scripts, catalogue[typeIndex],
                    timeoutSeconds, ct);
                if (templateLibrary is not null
                    && TemplateLibraryInProject(projectPath, templateLibrary) is { } clash)
                    return Json.Error("templateLibraryInProject", clash,
                        DqmhHeadless.Merge(detail, new JsonObject { ["templateLibrary"] = templateLibrary }));
            }

            // ---- 2. preconditions of the dialog watch ------------------------------------------
            var (dirtyBefore, dirtyError) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts,
                projectPath, timeoutSeconds, ct);
            if (dirtyError is not null)
                return Json.Error("dirtyCheckFailed",
                    "Could not read which VIs of the project have unsaved changes.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["error"] = dirtyError }));
            if (dirtyBefore is { Count: > 0 })
                return Json.Error("unsavedChangesInProject",
                    DqmhHeadless.UnsavedBeforeMessage(dirtyBefore),
                    DqmhHeadless.Merge(detail, new JsonObject { ["unsaved"] = DqmhHeadless.Array(dirtyBefore) }));

            // ---- 3. script - Script New Module creates the folder itself (measured) ---------------
            var before = DqmhHeadless.Snapshot.TakeOf(projectPath);
            inputs["Module Type Index"] = typeIndex.ToString();
            inputs["Script?"] = "true";
            IReadOnlyList<LvValuesXml.Value>? run;
            JsonArray dialogs;
            var watch = DqmhDialogWatch.Start();
            try
            {
                string? runHelperError;
                (run, runHelperError) = await dqmh.RunDetailedAsync(scripts, Helper, inputs,
                    timeoutSeconds, ct);
                if (runHelperError is not null)
                    return Json.Error("helperFailed",
                        "The run helper stopped before the wrapper ran; nothing was scripted.",
                        new { helperError = runHelperError, steps });
            }
            catch (RpcException e) when (e.StatusCode is StatusCode.DeadlineExceeded)
            {
                return Json.Error("scriptingTimedOut",
                    "The scripted run did not return. Script New Module.vi reports non-critical " +
                    "issues through a MODAL message - look at LabVIEW for an open dialog before " +
                    "calling anything else.",
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
                return Json.Error("helperMissing", $"{Helper} could not be run.", new { steps });
            if (DqmhTools.Failed(run) is { } error)
                return Json.Error("scriptingFailed",
                    error.Contains("56003") && typeIndex >= BuiltInTypes
                        ? "Delacor's Script New Module answered 56003 opening the library of the " +
                          $"template '{catalogue[typeIndex]}' - a library of the same name is in " +
                          "memory. Create the module in a project that does not hold " +
                          $"{templateLibrary ?? "that library"}, or close what holds it."
                        : "Delacor's Script New Module answered an error - `error` is its cluster.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["error"] = error }));

            // ---- 4. save what it left unsaved, verify from the files ----------------------------
            var (dirtyAfter, _) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts, projectPath,
                timeoutSeconds, ct);
            var saved = await DqmhHeadless.SaveNamedAsync(dqmh, scripts, dirtyAfter ?? [], timeoutSeconds, ct);

            List<string> created = [];
            if (before is not null && DqmhHeadless.Snapshot.TakeOf(projectPath) is { } after)
                (created, _) = before.Diff(after);
            var library = Path.Combine(folder, name + ".lvlib");
            var mainVi = Path.Combine(folder, "Main.vi");
            var tester = Path.Combine(folder, $"Test {name} API.vi");
            var doSomething = Directory.Exists(folder)
                && Directory.EnumerateFiles(folder, "Do Something*.vi").Any();
            var mainExec = File.Exists(mainVi) ? await headless.ExecStateAsync(mainVi, timeoutSeconds, ct) : null;
            var testerExec = File.Exists(tester) ? await headless.ExecStateAsync(tester, timeoutSeconds, ct) : null;
            var listed = File.Exists(projectPath)
                && File.ReadAllText(projectPath).Contains($"{name}.lvlib", StringComparison.OrdinalIgnoreCase);
            var adopted = DqmhHeadless.AdoptedHelpersOf(projectPath);

            var problems = new JsonArray();
            if (!File.Exists(library)) problems.Add($"{name}.lvlib is not in {folder}.");
            if (mainExec is not 1) problems.Add($"Main.vi is not executable (execState {mainExec?.ToString() ?? "missing"}).");
            if (testerExec is not 1) problems.Add($"The API tester is not executable (execState {testerExec?.ToString() ?? "missing"}).");
            if (doSomething != includeDoSomething)
                problems.Add(includeDoSomething
                    ? "Do Something was asked for and no Do Something VI was written."
                    : "Do Something was declined and Do Something VIs are present.");
            if (!listed) problems.Add($"The .lvproj does not list {name}.lvlib.");

            var answer = new JsonObject
            {
                ["ok"] = problems.Count == 0,
                ["module"] = name + ".lvlib",
                ["moduleType"] = catalogue[typeIndex],
                ["moduleTypeIndex"] = typeIndex,
                ["folder"] = folder,
                ["includeDoSomething"] = includeDoSomething,
                ["filesCreated"] = created.Count,
                ["mainViExecState"] = mainExec,
                ["testerExecState"] = testerExec,
                ["listedInProject"] = listed,
                ["problems"] = problems,
                ["savedMembers"] = DqmhHeadless.Array(saved),
                ["dialogsAnswered"] = dialogs,
                ["adoptedHelpers"] = DqmhHeadless.Array(adopted),
                ["projectHygiene"] = DqmhHeadless.ProjectHygieneNote,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] = "The type index belongs to THIS station's catalogue - say the type by name.",
            };
            foreach (var (key, value) in detail)
                answer[key] = value?.DeepClone();
            return Json.Document(answer);
        });
    }

    /// <summary>Get Module Type Info lists Singleton and Cloneable first, templates after them.</summary>
    internal const int BuiltInTypes = 2;

    /// <summary>
    /// The library FILE NAME a template type opens, read the way Delacor reads it - by regex out of
    /// the metadata files in the folder Delacor's own `Template Folders--constant.vi` names. Null
    /// when the type is not a template made there (an add-on's own types are not), which leaves
    /// the check to Delacor.
    /// </summary>
    private static async Task<string?> TemplateLibraryAsync(DqmhTools dqmh, string scripts,
        string title, int timeoutSeconds, CancellationToken ct)
    {
        if (DqmhHeadless.TargetPaths([DqmhTemplateTools.FoldersVi]) is not { } paths) return null;
        var (folders, _) = await dqmh.RunViDetailedAsync(scripts, paths[0], new(), timeoutSeconds, ct);
        var metadataFolder = folders is null ? null : DqmhTools.Scalar(folders, "MetaData Folder");
        if (string.IsNullOrEmpty(metadataFolder) || !Directory.Exists(metadataFolder)) return null;
        foreach (var file in Directory.EnumerateFiles(metadataFolder, "*.xml"))
            if (TemplateLibraryOf(File.ReadAllText(file, System.Text.Encoding.Latin1), title) is { } lib)
                return lib;
        return null;
    }

    /// <summary>The `LibraryPath` file name of a metadata text whose `Title` is the given one.</summary>
    internal static string? TemplateLibraryOf(string metadata, string title)
    {
        var t = System.Text.RegularExpressions.Regex.Match(metadata, "<Title>(.*?)</Title>");
        var l = System.Text.RegularExpressions.Regex.Match(metadata, "<LibraryPath>(.*?)</LibraryPath>");
        if (!t.Success || !l.Success || !string.Equals(t.Groups[1].Value.Trim(), title.Trim(),
                StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileName(l.Groups[1].Value.Trim().Replace('/', '\\'));
        return name.Length > 0 ? name : null;
    }

    /// <summary>Why a template whose library is the given one cannot be used in this project, or null.</summary>
    internal static string? TemplateLibraryInProject(string projectPath, string library) =>
        File.Exists(projectPath)
        && File.ReadAllText(projectPath).Contains($"\"{library}\"", StringComparison.OrdinalIgnoreCase)
            ? $"The template's library is {library}, and this project already holds a library of " +
              "that name - Delacor's Script New Module then fails with 56003 opening the template " +
              "(measured). Create the module in a project without it - a template is normally made " +
              "in one project and used in another."
            : null;

    /// <summary>
    /// The catalogue entry the caller named, ignoring case - `Singleton` is NOT `Singleton Panel`,
    /// the MGI add-on's type, which is why there is no prefix or fuzzy matching here.
    /// </summary>
    internal static int MatchType(IReadOnlyList<string> catalogue, string? wanted) =>
        catalogue.ToList().FindIndex(t =>
            string.Equals(t.Trim(), (wanted ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The extensions Delacor's `Check if OK to Proceed.vi` refuses at the TOP level of the save
    /// folder, read off its export 2026-10-06 - other files (source-control ones, per Delacor's
    /// DQMH-502) and sub-folders are allowed.
    /// </summary>
    internal static readonly string[] LabviewExtensions =
        [".vi", ".vit", ".ctl", ".ctt", ".lvclass", ".lvlib", ".lvproj", ".rtm", ".llb", ".lvlibp",
         ".xctl", ".xnode", ".vim"];

    /// <summary>
    /// Delacor's own two checks, made here because its dialog answers them with a MODAL: no
    /// LabVIEW file directly in the save folder (`Check if OK to Proceed.vi`), and no library of
    /// that name already in the project (`Generate Default Module Name.vi`'s collision rule).
    /// </summary>
    internal static string? FolderProblem(string folder, string name, string projectPath)
    {
        if (Directory.Exists(folder)
            && Directory.EnumerateFiles(folder).FirstOrDefault(f =>
                LabviewExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) is { } file)
            return $"{folder} already holds a LabVIEW file ({Path.GetFileName(file)}); Delacor's " +
                   "Add New DQMH Module refuses such a folder.";
        if (File.Exists(projectPath)
            && File.ReadAllText(projectPath).Contains($"\"{name}.lvlib\"", StringComparison.OrdinalIgnoreCase))
            return $"The project already lists a library named {name}.lvlib.";
        return null;
    }
}
