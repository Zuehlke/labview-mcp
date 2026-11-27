// ============================================================================================
// InPlaceEditTools.cs - lvai_set_representation and lvai_replace_primitive: two edits of code
// that ALREADY EXISTS, made through VI Server so the panel, the layout and every typedef link
// survive - where an AIXML edit is a full regeneration that discards all three.
// ============================================================================================
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// IN-PLACE EDITS (docs/non-grpc-gaps.md §6, from a field report: an integer widened from U16 to
/// I32 across a typedef, constants, indicators and conversion primitives of existing code).
/// Measured 2026-11-20 on LabVIEW 2026 and 2020, docs/in-place-edits.md:
/// <list type="bullet">
/// <item><c>Representation</c> of <c>{LV.Numeric}</c> and <c>{LV.NumericConstant}</c> is writable
/// in edit mode; value, default and wires survive, a sink of the old type gets a coercion dot. A
/// typedef field is written on the .ctl; its callers follow on their next load and need a save.
/// </item>
/// <item><c>{LV.Node}</c> <c>Replace</c> with the new primitive's NAME in <c>PaletteString</c>
/// replaces a primitive and keeps every wire when the shape is the same.</item>
/// </list>
/// Both helpers save through one error chain, so a refused write leaves the file as it was.
/// </summary>
[McpServerToolType]
internal sealed class InPlaceEditTools(LvaiConnection connection)
{
    internal const string RepresentationHelper = "lvai_set_representation";
    internal const string ReplaceHelper = "lvai_replace_primitive";

    private static readonly string[] EditableExtensions = [".vi", ".vit", ".vim", ".ctl", ".ctt"];

    // ================================================================== lvai_set_representation

    [McpServerTool(Name = "lvai_set_representation", Destructive = true, OpenWorld = true,
                   Title = "Change the representation of numerics in place")]
    [Description("""
        Edit: changes the REPRESENTATION (U16 -> I32, I32 -> DBL, ...) of numerics of an EXISTING
        .vi or .ctl in place and saves it - no regeneration, so panel, layout and typedef links
        stay. `controlsJson`: front-panel controls and indicators by LABEL, cluster elements and
        the FIELDS OF A TYPEDEF .ctl included (any depth); `constantsJson`: block diagram numeric
        constants by label. Values: I8 I16 I32 I64 U8 U16 U32 U64 SGL DBL EXT (or int32, double,
        ...); an enum takes U8/U16/U32 only. A map's or set's key and value types are NOT reached
        (`labelNotFound`). Value, default and wires survive; a sink of the old
        type gets a coercion dot. A label on no numeric (`labelNotFound`) or on several
        (`labelAmbiguous`) is refused BEFORE anything is written, with the labels there are. One error chain: a write LabVIEW refuses leaves the
        file untouched. For a .ctl with `projectPath`, the files under the project's folder that
        link the typedef are loaded and saved (`callersSaved`) - they follow in memory only - each
        backed up first (`callerBackups`). A VI or caller NOT EXECUTABLE before is refused
        (`brokenBefore`) unless `allowBroken`. `execStateBefore`/`execStateAfter`: a VI this call
        BROKE answers ok false with LabVIEW's errors; `backupPath` is the file as it was.
        A TYPEDEF CHANGE EMPTIES A CALLER'S MAP CONSTANT of that typedef, with the caller still
        executable and ok true (LabVIEW 2020, measured from the saved file: 6 entries -> 0; an
        array constant of the same typedef kept its data): run the callers' tests afterwards, or
        build such a map from an array in code. docs/in-place-edits.md.
        """)]
    public async Task<string> SetRepresentationAsync(
        [Description("Absolute path to the .vi or .ctl - it is saved in place")] string filePath,
        [Description("""
            Front-panel numerics as a JSON object label -> representation, e.g.
            {"Result":"I32","Max":"I32"}. A cluster element or typedef field is named by its own label
            """)]
        string? controlsJson = null,
        [Description("""Block diagram numeric constants as a JSON object label -> representation, e.g. {"Offset":"I32"}""")]
        string? constantsJson = null,
        [Description("""
            The .lvproj to open (and make active) first; the file is then edited in the project's
            instance, and for a .ctl its callers under the project's folder are saved
            """)]
        string? projectPath = null,
        [Description("For a .ctl with projectPath: load and save the files that link it (default true)")]
        bool saveCallers = true,
        [Description("Edit and save even when the VI, or a typedef caller, is NOT EXECUTABLE before the call (default false: refused as brokenBefore)")]
        bool allowBroken = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_set_representation", async () =>
        {
            if (Target(filePath, EditableExtensions) is { } bad) return bad;
            var file = Path.GetFullPath(filePath);
            var isCtl = Path.GetExtension(file).ToLowerInvariant() is ".ctl" or ".ctt";

            List<RepresentationRequest> controls, constants;
            try
            {
                controls = RepresentationRequest.ParseAll(controlsJson, nameof(controlsJson));
                constants = RepresentationRequest.ParseAll(constantsJson, nameof(constantsJson));
            }
            catch (ArgumentException e) { return Json.Error("badArguments", e.Message); }
            if (controls.Count == 0 && constants.Count == 0)
                return Json.Error("badArguments",
                    "Neither controlsJson nor constantsJson names a numeric, so this call asks for nothing.");
            if (isCtl && constants.Count > 0)
                return Json.Error("badArguments", "A .ctl has no block diagram, so constantsJson cannot apply to it.");

            var helpers = new HousekeepingHelpers(connection);
            var (helperVi, _, failure) = await helpers.EnsureAsync(RepresentationHelper, timeoutSeconds, ct);
            if (failure is not null) return failure;

            var steps = new JsonArray();
            var throughProject = projectPath is { Length: > 0 };
            if (throughProject)
            {
                var (opened, openFailure) = await helpers.OpenProjectAsync(projectPath!, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = opened?.DeepClone() });
                if (openFailure is not null) return openFailure;
            }

            // ---- 1. LIST: which labels there are, so a label on none or on several is refused
            // before the first write. The helper writes only exactly-one matches anyway; asking
            // first makes the call all or nothing and the refusal name what exists.
            var listed = await RunAsync(helpers, helperVi!, file, throughProject, [], timeoutSeconds, ct);
            if (listed.Failure is not null) return listed.Failure;
            var controlLabels = HousekeepingHelpers.Strings(listed.Values!, "control labels seen");
            var constantLabels = HousekeepingHelpers.Strings(listed.Values!, "constant labels seen");
            if (Unmatched(controls, controlLabels, "controlsJson", "front-panel numeric") is { } noControl) return noControl;
            if (Unmatched(constants, constantLabels, "constantsJson", "block diagram numeric constant") is { } noConstant)
                return noConstant;

            var execState = new ExecStateTools(connection);
            var before = isCtl ? null : await execState.ReadAsync(
                file, helperAixmlPath: null, helperViPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);

            // A TYPEDEF'S CALLERS are found and read BEFORE the write: a caller that is broken
            // already is refused like a broken target, and each one is compared after its save.
            var withCallers = isCtl && throughProject && saveCallers;
            List<string> callers = withCallers ? Callers(file, projectPath!) : [];
            var callerStates = new List<SavedFileState>();
            foreach (var caller in callers.Where(IsVi))
                callerStates.Add(new SavedFileState(caller, await execState.ReadAsync(
                    caller, helperAixmlPath: null, helperViPath: null, regenerateHelper: false, timeoutSeconds, ct: ct)));
            if (BrokenBeforeRefusal([new SavedFileState(file, before), .. callerStates], allowBroken) is { } brokenRefusal)
                return brokenRefusal;

            var backupFolder = BackupFolder();
            var backup = Backup(file, backupFolder);
            var projectFolder = withCallers ? Path.GetDirectoryName(Path.GetFullPath(projectPath!))! : null;
            var callerBackups = callers.Count(c => Backup(c, backupFolder,
                Path.Combine("callers", Path.GetRelativePath(projectFolder!, c))) is not null);

            // ---- 2. WRITE
            var inputs = new Dictionary<string, string>();
            if (controls.Count > 0)
            {
                inputs["control labels"] = string.Join("|", controls.Select(c => c.Label));
                inputs["control representations"] = string.Join("|", controls.Select(c => c.Representation.Number));
            }
            if (constants.Count > 0)
            {
                inputs["constant labels"] = string.Join("|", constants.Select(c => c.Label));
                inputs["constant representations"] = string.Join("|", constants.Select(c => c.Representation.Number));
            }
            var stamp = File.GetLastWriteTimeUtc(file);
            var written = await RunAsync(helpers, helperVi!, file, throughProject, inputs, timeoutSeconds, ct);
            if (written.Failure is not null) return written.Failure;
            var values = written.Values!;
            var code = HousekeepingHelpers.Code(values, "error out") ?? 0;
            var source = HousekeepingHelpers.Source(values, "error out");
            var saved = File.GetLastWriteTimeUtc(file) != stamp;

            // ---- 3. THE VERDICT: LabVIEW's own read-back per request, and the saved file
            var evidence = saved ? await FileEvidenceAsync(file, isCtl, timeoutSeconds, ct) : null;
            var asked = controls.Select(c => ("control", c)).Concat(constants.Select(c => ("constant", c))).ToList();
            var results = Results(asked,
                [.. Ints(values, "control codes"), .. Ints(values, "constant codes")],
                [.. Ints(values, "control after"), .. Ints(values, "constant after")],
                saved, (kind, label) => evidence?.TypeOf(label, kind == "constant"));
            var allWritten = code == 0 && results.All(r => r?["ok"]?.GetValue<bool>() == true);

            // ---- 4. A TYPEDEF'S CALLERS follow in memory only (measured: byte-identical until
            // loaded and saved, and the modification bitsets read 0, so nothing tells which need it).
            JsonArray? callersSaved = null, callersFailed = null;
            JsonObject? callerVersions = null;
            if (withCallers && allWritten)
            {
                callersSaved = new JsonArray();
                callersFailed = new JsonArray();
                callerVersions = new JsonObject();
                foreach (var caller in callers)
                {
                    // read before the save, so saveVersionCheck can say a caller went up a version
                    if (SaveVersions.Of(caller) is { } callerWas) callerVersions[caller] = callerWas;
                    var resaved = await RunAsync(helpers, helperVi!, caller, throughProject: true, [], timeoutSeconds, ct,
                                                 save: true);
                    var callerCode = resaved.Values is null ? -1 : HousekeepingHelpers.Code(resaved.Values, "error out") ?? 0;
                    if (callerCode == 0) callersSaved.Add(caller);
                    else callersFailed.Add(new JsonObject { ["path"] = caller, ["errorCode"] = callerCode });
                }
            }

            var after = isCtl || !saved ? null : await execState.ReadAsync(
                file, helperAixmlPath: null, helperViPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            var broke = before is { Broken: false } && after is { Broken: true };

            return Json.Document(new JsonObject
            {
                ["ok"] = allWritten && saved && !broke && callersFailed is not { Count: > 0 },
                ["filePath"] = file,
                ["saved"] = saved,
                ["results"] = results,
                ["errorCode"] = code == 0 ? (int?)null : code,
                ["errorSource"] = code == 0 ? null : source,
                ["execStateBefore"] = before?.State,
                ["execStateAfter"] = after?.State,
                ["errors"] = broke ? ExecStateTools.ErrorsNode(after) : null,
                ["callersSaved"] = callersSaved,
                ["callersFailed"] = callersFailed is { Count: > 0 } ? callersFailed : null,
                [SaveVersions.VersionsBeforeKey] = callerVersions is { Count: > 0 } ? callerVersions : null,
                ["brokenBefore"] = before?.Broken == true || callerStates.Any(c => c.Before is { Broken: true }) ? true : null,
                ["backupPath"] = backup,
                ["callerBackups"] = callerBackups > 0 ? Path.Combine(backupFolder, "callers") : null,
                ["steps"] = steps.Count > 0 ? steps : null,
                ["note"] = RepresentationNote(code, saved, allWritten, broke, isCtl, throughProject, saveCallers) +
                           (throughProject ? " The project is left open." : ""),
            });
        }));

    /// <summary>
    /// One row per request, in the order of the helper's error chain (controls, then constants).
    /// The chain decides what a row may claim: after a refused write NOTHING was saved, so a row
    /// before it was written in memory only, the row itself carries LabVIEW's code, and the rows
    /// after it never ran. The helper's read-back is not served for any of them - it takes its
    /// error in from the write, and after a refusal it answered 0, which is also EXT (live check of
    /// 2026-11-20: a refused U16 read "EXT").
    /// </summary>
    internal static JsonArray Results(IReadOnlyList<(string Kind, RepresentationRequest Request)> asked,
                                      IReadOnlyList<int> codes, IReadOnlyList<int> reads, bool saved,
                                      Func<string, string, string?> fileType)
    {
        var failedAt = Enumerable.Range(0, asked.Count).FirstOrDefault(i => i < codes.Count && codes[i] != 0, -1);
        var results = new JsonArray();
        for (var i = 0; i < asked.Count; i++)
        {
            var (kind, request) = asked[i];
            var landed = failedAt < 0 && saved;
            var readBack = landed && i < reads.Count ? NumericRepresentation.FromNumber(reads[i]) : null;
            var inFile = landed ? fileType(kind, request.Label) : null;
            results.Add(new JsonObject
            {
                ["kind"] = kind,
                ["label"] = request.Label,
                ["asked"] = request.Representation.Name,
                ["outcome"] = landed ? "written"
                    : failedAt < 0 || i < failedAt ? "notSaved"
                    : i == failedAt ? "refused" : "skipped",
                ["labviewReads"] = readBack?.Name,
                ["fileType"] = inFile,
                ["errorCode"] = i == failedAt ? codes[i] : (int?)null,
                ["ok"] = landed && readBack?.Number == request.Representation.Number
                         && (inFile is null || request.Representation.Matches(inFile)),
            });
        }
        return results;
    }

    private static string RepresentationNote(int code, bool saved, bool allWritten, bool broke, bool isCtl,
                                             bool throughProject, bool saveCallers) =>
        code == 1077
            ? "LabVIEW refused a representation (Error 1077) - an enum takes U8, U16 or U32 only. One error " +
              "chain: nothing was saved, the file is as it was."
        : code == 1082
            ? "LabVIEW refused the write (Error 1082): the numeric belongs to a control that is an INSTANCE of " +
              "a typedef, and its type is the typedef's. Edit the typedef's .ctl with this tool instead (pass " +
              "projectPath, so its callers are saved). Nothing was saved, the file is as it was."
        : !saved
            ? "The file was NOT saved - read errorCode and each result's `outcome` (refused = the write " +
              "LabVIEW rejected, notSaved = written in memory and dropped with it, skipped = never ran). One " +
              "error chain: the file is as it was."
        : broke
            ? "Written and saved, and the VI is NOT EXECUTABLE now although it was before - `errors` is " +
              "LabVIEW's list. Nothing was rolled back; backupPath is the file as it was."
        : !allWritten
            ? "Saved, but not every request reads back as asked - compare `asked`, `labviewReads` and " +
              "`fileType` per result."
        : isCtl && !throughProject
            ? "The typedef is saved. Its CALLERS follow in memory only: each is unchanged on disk until it " +
              "is loaded and saved - pass projectPath and this call saves the ones under the project's folder."
        : isCtl && !saveCallers
            ? "The typedef is saved. Its callers were NOT saved (saveCallers false): each is unchanged on " +
              "disk until it is loaded and saved."
        : (isCtl ? "A MAP constant of this typedef in a caller is EMPTIED by the change while the caller stays " +
                   "executable (measured on LabVIEW 2020; arrays keep their data): run the callers' tests, and " +
                   "restore a caller from callerBackups if one lost data. " : "") +
          "Written and read back, from LabVIEW and from the saved file. A sink that still has the old type " +
          "shows a coercion dot now (lvai_coercion_dots); widen it with another call, or replace the " +
          "conversion in front of it with lvai_replace_primitive.";

    /// <summary>
    /// The refusal for labels that match no numeric (labelNotFound) or several (labelAmbiguous) -
    /// null when every one matches once.
    /// </summary>
    internal static string? Unmatched(IReadOnlyList<RepresentationRequest> requests, IReadOnlyList<string> present,
                                      string parameter, string what)
    {
        var problems = new JsonArray();
        foreach (var request in requests)
        {
            var count = present.Count(l => string.Equals(l, request.Label, StringComparison.Ordinal));
            if (count != 1) problems.Add(new JsonObject { ["label"] = request.Label, ["matches"] = count });
        }
        if (problems.Count == 0) return null;
        var missing = problems.Any(p => p?["matches"]?.GetValue<int>() == 0);
        return Json.Error(missing ? "labelNotFound" : "labelAmbiguous",
            $"{parameter}: each label must name exactly ONE {what}; {problems.Count} label(s) here name none " +
            "or several (labels are exact; `matches` 0 = no such numeric, 2 or more = cannot tell which is " +
            "meant). Nothing was written.",
            new JsonObject
            {
                ["problems"] = problems,
                ["labels"] = new JsonArray([.. present.Where(l => l.Length > 0).Distinct().Select(l => (JsonNode)l)]),
            });
    }

    /// <summary>
    /// The files under the project's folder whose link records name the typedef - .ctl files
    /// first, because a typedef nested in another must be saved before the VIs using that one.
    /// A HEURISTIC by file name (ViFile.Links), which is what it says in the docs.
    /// </summary>
    internal static List<string> Callers(string ctl, string projectPath)
    {
        var name = Path.GetFileName(ctl);
        return [.. LinkCheckTools.Files([projectPath], out _)
            .Where(f => !string.Equals(f, ctl, StringComparison.OrdinalIgnoreCase))
            .Where(f => { try { return ViFile.Links(f, name); } catch (IOException) { return false; } })
            .OrderBy(f => (Path.GetExtension(f).ToLowerInvariant() is ".ctl" or ".ctt") ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<(IReadOnlyList<LvValuesXml.Value>? Values, string? Failure)> RunAsync(
        HousekeepingHelpers helpers, string helperVi, string file, bool throughProject,
        Dictionary<string, string> inputs, int timeoutSeconds, CancellationToken ct, bool save = false)
    {
        // The helper saves only when it has something to write, or when asked (`save`, a caller's
        // resave) - a listing run must not rewrite the file in the running LabVIEW's version.
        var all = new Dictionary<string, string>(inputs) { ["vi path"] = file };
        if (throughProject) all["through active project"] = "true";
        if (save || inputs.Count > 0) all["save"] = "true";
        var (values, helperError) = await helpers.RunAsync(helperVi, all, timeoutSeconds, ct);
        return values is null
            ? (null, Json.Error("helperDidNotAnswer",
                "The helper VI returned no values, so nothing is known about the edit.",
                new { helperViPath = helperVi, helperError }))
            : (values, null);
    }

    // ================================================================== lvai_replace_primitive

    [McpServerTool(Name = "lvai_replace_primitive", Destructive = true, OpenWorld = true,
                   Title = "Replace a primitive node in place")]
    [Description("""
        Edit: replaces PRIMITIVE nodes of an EXISTING VI in place - the IDE's right-click >
        Replace - and saves it: `from` "To Unsigned Word Integer", `to` "To Long Integer". Names
        are the primitive's own (what an AIXML export shows as `_name`). A primitive of the SAME
        SHAPE keeps every wire (conversions, Add <-> Subtract, comparisons); a different shape
        does not (Increment -> Add leaves `x` open) - then `execStateAfter` is 0, the answer is ok
        false with LabVIEW's errors, and `backupPath` holds the file as it was. Replaces EVERY
        node named `from` unless `occurrences` picks some: call with `dryRun` first to get each
        match's index and diagram position (`matches`). A `from` no node carries is refused with
        the names the diagram has; an unknown `to` is Error 1054 and nothing is saved. A VI that is
        NOT EXECUTABLE before the call is refused (`brokenBefore`, with LabVIEW's errors) unless
        `allowBroken`. Verified by listing the saved VI again (`fromAfter`, `toAfter`). SubVI calls and constants are
        lvai_swap_subvis' job. docs/in-place-edits.md.
        """)]
    public async Task<string> ReplacePrimitiveAsync(
        [Description("Absolute path to the .vi - it is saved in place")] string viPath,
        [Description("The primitive to replace, by name, e.g. 'To Unsigned Word Integer'")] string from,
        [Description("The primitive it becomes, by name, e.g. 'To Long Integer'")] string to,
        [Description("""
            Which matches: omitted = all; or indices from a dryRun's `matches`, comma separated ("0,2").
            Indices count the matches of THIS call - after a replace the remaining ones renumber
            """)]
        string? occurrences = null,
        [Description("List the matches (index, position) and change nothing (default false)")] bool dryRun = false,
        [Description("The .lvproj to open (and make active) first; the VI is then edited in the project's instance")]
        string? projectPath = null,
        [Description("Edit and save the VI even when it is NOT EXECUTABLE before the call (default false: refused as brokenBefore)")]
        bool allowBroken = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_replace_primitive", async () =>
        {
            if (Target(viPath, [".vi", ".vit", ".vim"]) is { } bad) return bad;
            var vi = Path.GetFullPath(viPath);
            from = from?.Trim() ?? "";
            to = to?.Trim() ?? "";
            if (from.Length == 0 || to.Length == 0)
                return Json.Error("badArguments", "`from` and `to` each name a primitive; one of them is empty.");
            if (string.Equals(from, to, StringComparison.Ordinal))
                return Json.Error("badArguments", $"`from` and `to` are both '{from}', so this call asks for nothing.");
            if (from.Contains('|') || to.Contains('|'))
                return Json.Error("badArguments", "A primitive's name holds no '|' - that separates the helper's lists.");

            var helpers = new HousekeepingHelpers(connection);
            var (helperVi, _, failure) = await helpers.EnsureAsync(ReplaceHelper, timeoutSeconds, ct);
            if (failure is not null) return failure;

            var steps = new JsonArray();
            var throughProject = projectPath is { Length: > 0 };
            if (throughProject)
            {
                var (opened, openFailure) = await helpers.OpenProjectAsync(projectPath!, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = opened?.DeepClone() });
                if (openFailure is not null) return openFailure;
            }

            // ---- 1. LIST
            var listed = await ListAsync(helpers, helperVi!, vi, from, throughProject, timeoutSeconds, ct);
            if (listed.Failure is not null) return listed.Failure;
            var found = listed.Listing!;
            if (found.Matches.Count == 0)
                return Json.Error("primitiveNotFound",
                    $"No node of '{Path.GetFileName(vi)}' is named '{from}'. Names are exact; `nodes` lists " +
                    "what the diagram has (subVI calls and structures included). Nothing was changed.",
                    new JsonObject
                    {
                        ["from"] = from,
                        ["nodes"] = new JsonArray([.. found.Styles.Where(s => s.Length > 0).Distinct()
                                                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                                         .Select(s => (JsonNode)s)]),
                    });

            var (indices, indexProblem) = Occurrences(occurrences, found.Matches.Count);
            if (indexProblem is not null)
                return Json.Error("badArguments", indexProblem, new JsonObject { ["matches"] = found.MatchesNode() });

            if (dryRun)
                return Json.Document(new JsonObject
                {
                    ["ok"] = true,
                    ["dryRun"] = true,
                    ["viPath"] = vi,
                    ["from"] = from,
                    ["to"] = to,
                    ["matches"] = found.MatchesNode(),
                    ["wouldReplace"] = new JsonArray([.. indices!.Select(i => (JsonNode)i)]),
                    ["note"] = "Nothing was changed. `matches` is in the order `occurrences` counts; " +
                               "`left`/`top` are each node's position on its diagram (lvai_render_diagrams shows it).",
                });

            var before = await new ExecStateTools(connection).ReadAsync(
                vi, helperAixmlPath: null, helperViPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            if (BrokenBeforeRefusal([new SavedFileState(vi, before)], allowBroken) is { } brokenRefusal)
                return brokenRefusal;
            var backup = Backup(vi);

            // ---- 2. REPLACE AND SAVE
            var stamp = File.GetLastWriteTimeUtc(vi);
            var inputs = new Dictionary<string, string>
            {
                ["vi path"] = vi,
                ["from style"] = from,
                ["to palette string"] = to,
                ["indices"] = string.Join("|", indices!),
                ["save"] = "true",
            };
            if (throughProject) inputs["through active project"] = "true";
            var (values, helperError) = await helpers.RunAsync(helperVi!, inputs, timeoutSeconds, ct);
            if (values is null)
                return Json.Error("helperDidNotAnswer",
                    "The helper VI returned no values, so nothing is known about the replace.",
                    new { helperViPath = helperVi, helperError, backupPath = backup });
            var code = HousekeepingHelpers.Code(values, "error out") ?? 0;
            var source = HousekeepingHelpers.Source(values, "error out");
            var saved = File.GetLastWriteTimeUtc(vi) != stamp;

            // ---- 3. THE VERDICT, from a fresh listing of the saved VI
            var relisted = saved ? await ListAsync(helpers, helperVi!, vi, from, throughProject, timeoutSeconds, ct) : default;
            var after = relisted.Listing;
            var toBefore = found.Styles.Count(s => string.Equals(s, to, StringComparison.Ordinal));
            var toAfter = after?.Styles.Count(s => string.Equals(s, to, StringComparison.Ordinal));
            var verified = after is not null
                           && after.Matches.Count == found.Matches.Count - indices!.Count
                           && toAfter == toBefore + indices.Count;

            var state = saved ? await new ExecStateTools(connection).ReadAsync(
                vi, helperAixmlPath: null, helperViPath: null, regenerateHelper: false, timeoutSeconds, ct: ct) : null;
            var broke = before is { Broken: false } && state is { Broken: true };

            return Json.Document(new JsonObject
            {
                ["ok"] = code == 0 && saved && verified && !broke,
                ["viPath"] = vi,
                ["from"] = from,
                ["to"] = to,
                ["replaced"] = saved && verified ? indices!.Count : 0,
                ["asked"] = indices!.Count,
                ["saved"] = saved,
                ["verified"] = verified,
                ["fromBefore"] = found.Matches.Count,
                ["fromAfter"] = after?.Matches.Count,
                ["toBefore"] = toBefore,
                ["toAfter"] = toAfter,
                ["errorCode"] = code == 0 ? (int?)null : code,
                ["errorSource"] = code == 0 ? null : source,
                ["execStateBefore"] = before?.State,
                ["execStateAfter"] = state?.State,
                ["brokenBefore"] = before?.Broken == true ? true : null,
                ["errors"] = broke || state is { Broken: true } ? ExecStateTools.ErrorsNode(state) : null,
                ["backupPath"] = backup,
                ["steps"] = steps.Count > 0 ? steps : null,
                ["note"] = (before?.Broken == true
                    ? "The VI was NOT EXECUTABLE before this call (allowBroken) and is saved as it now is; `errors` " +
                      "is LabVIEW's list after the edit. "
                    : "") + (code == 1054
                    ? $"LabVIEW knows no primitive named '{to}' (Error 1054). Nothing was saved. The name is the " +
                      "one the Functions palette and an AIXML export show."
                : !saved
                    ? "The VI was NOT saved - read errorCode. One error chain: a Replace that fails stops the save, " +
                      "so the file is as it was."
                : broke
                    ? $"Replaced and saved, and the VI is NOT EXECUTABLE now although it was before: '{to}' has " +
                      "another shape than '" + from + "', so a terminal is left open or a wire loose - `errors` is " +
                      "LabVIEW's list. Nothing was rolled back; backupPath is the file as it was."
                : !verified
                    ? "Saved, but a fresh listing does not show the counts asked for - compare fromAfter and toAfter."
                : before?.Broken == true
                    ? "Replaced and saved; a fresh listing of the saved VI shows the new counts." +
                      (throughProject ? " The project is left open." : "")
                : "Replaced and saved; a fresh listing of the saved VI shows the new counts and the VI is as " +
                  "executable as before. Where the new primitive has a terminal the old one lacked, it is " +
                  "unwired; where its output type differs, the sink shows a coercion dot (lvai_coercion_dots)." +
                  (throughProject ? " The project is left open." : "")),
            });
        }));

    /// <summary>One listing run: every node's name, and the position of each node named <paramref name="from"/>.</summary>
    private static async Task<(Listing? Listing, string? Failure)> ListAsync(
        HousekeepingHelpers helpers, string helperVi, string vi, string from, bool throughProject,
        int timeoutSeconds, CancellationToken ct)
    {
        var inputs = new Dictionary<string, string> { ["vi path"] = vi, ["from style"] = from };
        if (throughProject) inputs["through active project"] = "true";
        var (values, helperError) = await helpers.RunAsync(helperVi, inputs, timeoutSeconds, ct);
        if (values is null)
            return (null, Json.Error("helperDidNotAnswer",
                "The helper VI returned no values, so the diagram's nodes could not be listed.",
                new { helperViPath = helperVi, helperError }));
        var code = HousekeepingHelpers.Code(values, "error out") ?? 0;
        if (code != 0)
            return (null, Json.Error("listFailed",
                $"Listing the diagram's nodes failed with Error {code}. Nothing was changed.",
                new { errorCode = code, errorSource = HousekeepingHelpers.Source(values, "error out") }));
        var lefts = Ints(values, "match lefts");
        var tops = Ints(values, "match tops");
        var matches = lefts.Zip(tops).Select(p => (p.First, p.Second)).ToList();
        return (new Listing(HousekeepingHelpers.Strings(values, "styles seen"), matches), null);
    }

    internal sealed record Listing(IReadOnlyList<string> Styles, IReadOnlyList<(int Left, int Top)> Matches)
    {
        internal JsonArray MatchesNode() => new([.. Matches.Select((m, i) => (JsonNode)new JsonObject
        {
            ["index"] = i,
            ["left"] = m.Left,
            ["top"] = m.Top,
        })]);
    }

    /// <summary>
    /// The match indices to replace: all of them when nothing is asked for, else the listed ones -
    /// or the reason they cannot be used.
    /// </summary>
    internal static (List<int>? Indices, string? Problem) Occurrences(string? occurrences, int matches)
    {
        if (string.IsNullOrWhiteSpace(occurrences) || occurrences.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return ([.. Enumerable.Range(0, matches)], null);
        var indices = new List<int>();
        foreach (var part in occurrences.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                return (null, $"`occurrences` holds '{part}', which is not an index. Give indices from a dryRun's " +
                              "`matches`, comma separated, or omit it for all.");
            if (index >= matches)
                return (null, $"`occurrences` names index {index}, and the diagram has {matches} such node(s) " +
                              $"(indices 0..{matches - 1}).");
            if (indices.Contains(index))
                return (null, $"`occurrences` names index {index} twice.");
            indices.Add(index);
        }
        return indices.Count > 0
            ? (indices, null)
            : (null, "`occurrences` names no index. Omit it to replace every match.");
    }

    // ================================================================== shared

    /// <summary>The refusal for a path that is no file of the kinds given - null when it is one.</summary>
    private static string? Target(string? path, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Json.Error("fileNotFound", $"No file at '{path}'.", new { path });
        return extensions.Contains(Path.GetExtension(path).ToLowerInvariant())
            ? null
            : Json.Error("badArguments",
                $"'{Path.GetFileName(path)}' is not one of {string.Join(", ", extensions)}.", new { path });
    }

    /// <summary>One backup folder per call, beside nothing of the user's.</summary>
    internal static string BackupFolder() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "inplace-backup",
                     DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));

    /// <summary>
    /// A copy of the file as it is, in <paramref name="folder"/> (a new one when null) under
    /// <paramref name="relative"/> (its file name when null) - null when it could not be made.
    /// </summary>
    internal static string? Backup(string file, string? folder = null, string? relative = null)
    {
        try
        {
            var copy = Path.Combine(folder ?? BackupFolder(), relative ?? Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ broken before, broken after

    /// <summary>A file this call saves, with its executability before the edit.</summary>
    internal sealed record SavedFileState(string Path, ExecStateReading? Before);

    /// <summary>
    /// LabVIEW 2020's text for a VI that cannot follow a changed typedef. A field report
    /// (2026-11-24) met it on a VI with a map constant of the typedef's cluster; the IDE's
    /// "Review and Update from Type Def" repaired it and kept the data. Nothing in VI Server was
    /// found to do the same.
    /// </summary>
    internal const string TypedefUpdateText = "update from type definition";

    /// <summary>
    /// The refusal for an edit whose target or typedef callers are NOT EXECUTABLE before it - null
    /// when every one is executable or unread, or when <paramref name="allowBroken"/>. The field
    /// report of 2026-11-24: lvai_replace_primitive saved into a VI that was already broken and
    /// answered ok true (it compared only executable-before with broken-after), and the break
    /// that was there first cost most of a day.
    /// </summary>
    internal static string? BrokenBeforeRefusal(IReadOnlyList<SavedFileState> files, bool allowBroken)
    {
        if (allowBroken) return null;
        var broken = new JsonArray([.. files.Where(f => f.Before is { Broken: true })
            .Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["errors"] = ExecStateTools.ErrorsNode(f.Before) })]);
        if (broken.Count == 0) return null;
        var typedefText = files.Any(f => f.Before?.Errors?.Any(e => Mentions(e, TypedefUpdateText)) == true);
        return Json.Error("brokenBefore",
            $"{broken.Count} file(s) this call would save are NOT EXECUTABLE before the edit - `broken` lists them " +
            "with LabVIEW's errors. Saved on top of that, the edit's own effect cannot be told from the break that " +
            "was there first. Nothing was written. Repair them first, or pass allowBroken true to edit anyway." +
            (typedefText
                ? " \"Cannot update from type definition\" is repaired in the IDE: right-click the constant or " +
                  "control > Review and Update from Type Def, which keeps its data. Saving the VI as it is may not."
                : ""),
            new JsonObject { ["broken"] = broken });
    }

    private static bool Mentions(ErrorListTools.ErrorLine line, string text) =>
        line.Error.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (line.Detail?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsVi(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".vi" or ".vit" or ".vim";

    private static List<int> Ints(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        [.. HousekeepingHelpers.Strings(values, name)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MinValue)];

    // ------------------------------------------------------------------ the file's own word

    /// <summary>What the saved file says each labelled numeric's type is.</summary>
    internal sealed record FileEvidence(XElement? Export, JsonObject? Ctl)
    {
        /// <summary>
        /// The type as the file names it - an AIXML type (<c>int32</c>) for a VI, pylabview's
        /// (<c>NumInt32</c>) for a .ctl - or null when the file does not show that label.
        /// </summary>
        internal string? TypeOf(string label, bool constant)
        {
            if (Ctl is not null) return CtlType(Ctl["fields"] as JsonArray, label);
            if (Export is null) return null;
            var own = Export.Descendants()
                .Where(e => constant ? e.Name.LocalName == "Constant" : e.Name.LocalName is "Control" or "Indicator")
                .FirstOrDefault(e => (string?)e.Attribute("_name") == label);
            if (own is not null) return Bare((string?)own.Attribute("type"));
            if (constant) return null;
            // a CLUSTER ELEMENT lives in its owner's type string: cluster{int32.Max,double.Gain}
            var element = new Regex(@"(?<![\w])(?<type>[a-z]+[0-9]*)(\{[^{}]*\})?\." + Regex.Escape(label) + @"(?=[,}\]]|$)");
            return Export.Descendants()
                .Where(e => e.Name.LocalName is "Control" or "Indicator")
                .Select(e => element.Match((string?)e.Attribute("type") ?? ""))
                .FirstOrDefault(m => m.Success)?.Groups["type"].Value;
        }

        private static string? Bare(string? type) =>
            type is null ? null : type.IndexOf('{') is var brace and >= 0 ? type[..brace] : type;

        private static string? CtlType(JsonArray? fields, string label)
        {
            foreach (var field in fields?.OfType<JsonObject>() ?? [])
            {
                if (field["label"]?.GetValue<string>() == label && field["members"] is null)
                    return field["type"]?.GetValue<string>();
                if (CtlType(field["members"] as JsonArray, label) is { } nested) return nested;
            }
            return null;
        }
    }

    private async Task<FileEvidence?> FileEvidenceAsync(string file, bool isCtl, int timeoutSeconds, CancellationToken ct)
    {
        if (isCtl)
        {
            var folder = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "ctl", Path.GetRandomFileName());
            try
            {
                var (mainXml, failure) = await CtlTools.ExtractAsync(file, folder, timeoutSeconds, ct);
                return failure is null && mainXml is not null
                    ? new FileEvidence(null, CtlTools.Describe(XDocument.Load(mainXml).Root!, file))
                    : null;
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException) { return null; }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
            }
        }

        var target = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "inplace",
                                  Path.GetFileNameWithoutExtension(file) + ".xml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
            file, target, returnContent: true, maxContentChars: 0, timeoutSeconds, refresh: true, ct: ct);
        try
        {
            var xml = (JsonNode.Parse(answer) as JsonObject)?["xml"]?.GetValue<string>();
            return xml is null ? null : new FileEvidence(XElement.Parse(xml), null);
        }
        catch (Exception e) when (e is JsonException or System.Xml.XmlException) { return null; }
    }
}

/// <summary>
/// A numeric representation as LabVIEW's <c>Representation</c> property numbers it - an enum of
/// uint32, read off controls of every type on 2026-11-20 (docs/in-place-edits.md). The complex and
/// fixed-point items (9..11, 14) exist and were not measured, so they are not offered: LabVIEW does
/// not refuse an out-of-range number, it CLAMPS it to fixed-point and drops the default.
/// </summary>
internal sealed record NumericRepresentation(string Name, int Number, string AixmlType, string CtlType)
{
    internal static readonly IReadOnlyList<NumericRepresentation> All =
    [
        new("EXT", 0, "extended", "NumFloatExt"),
        new("DBL", 1, "double", "NumFloat64"),
        new("SGL", 2, "single", "NumFloat32"),
        new("I32", 3, "int32", "NumInt32"),
        new("I16", 4, "int16", "NumInt16"),
        new("I8", 5, "int8", "NumInt8"),
        new("U32", 6, "uint32", "NumUInt32"),
        new("U16", 7, "uint16", "NumUInt16"),
        new("U8", 8, "uint8", "NumUInt8"),
        new("I64", 12, "int64", "NumInt64"),
        new("U64", 13, "uint64", "NumUInt64"),
    ];

    /// <summary>By short name (<c>I32</c>) or AIXML type (<c>int32</c>, <c>float</c> for SGL), any case.</summary>
    internal static NumericRepresentation? Parse(string? text)
    {
        var t = text?.Trim() ?? "";
        return All.FirstOrDefault(r => r.Name.Equals(t, StringComparison.OrdinalIgnoreCase)
                                       || r.AixmlType.Equals(t, StringComparison.OrdinalIgnoreCase))
               ?? (t.Equals("float", StringComparison.OrdinalIgnoreCase) ? All[2] : null);
    }

    internal static NumericRepresentation? FromNumber(int number) => All.FirstOrDefault(r => r.Number == number);

    /// <summary>
    /// Whether a type as a FILE names it is this representation: the AIXML type of a VI's export,
    /// or pylabview's name in a .ctl - where an enum of this width reads <c>UnitUInt32</c>.
    /// </summary>
    internal bool Matches(string fileType) =>
        fileType.Equals(AixmlType, StringComparison.OrdinalIgnoreCase)
        || (AixmlType == "single" && fileType.Equals("float", StringComparison.OrdinalIgnoreCase))
        || fileType.Equals(CtlType, StringComparison.Ordinal)
        || fileType.Equals(CtlType.Replace("Num", "Unit"), StringComparison.Ordinal);
}

/// <summary>One "this label becomes that representation" of lvai_set_representation.</summary>
internal sealed record RepresentationRequest(string Label, NumericRepresentation Representation)
{
    /// <summary>The requests of a JSON object <c>{"label":"I32"}</c>; empty for null or blank.</summary>
    internal static List<RepresentationRequest> ParseAll(string? json, string parameter)
    {
        var requests = new List<RepresentationRequest>();
        if (string.IsNullOrWhiteSpace(json)) return requests;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { throw new ArgumentException($"{parameter} is not JSON: {e.Message}"); }
        if (node is not JsonObject map)
            throw new ArgumentException($"{parameter} must be a JSON object label -> representation, " +
                                        """e.g. {"Result":"I32"}.""");
        foreach (var (label, value) in map)
        {
            if (label.Length == 0) throw new ArgumentException($"{parameter} holds an empty label.");
            if (label.Contains('|'))
                throw new ArgumentException($"{parameter}: the label '{label}' holds a '|', which separates the " +
                                            "helper's lists.");
            var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString();
            var representation = NumericRepresentation.Parse(text)
                ?? throw new ArgumentException(
                    $"{parameter}: '{text}' for '{label}' is not a representation. Use one of " +
                    string.Join(", ", NumericRepresentation.All.OrderBy(r => r.Name).Select(r => r.Name)) +
                    " (complex and fixed-point are not offered).");
            requests.Add(new RepresentationRequest(label, representation));
        }
        return requests;
    }
}
