// ============================================================================================
// lvai_save_for_previous: brings VIs and .ctl files a newer LabVIEW re-saved back to the
// version their project pins, in place, through LabVIEW's own Save for Previous Version.
// ============================================================================================
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Every tool that saves through LabVIEW writes the running LabVIEW's version, also into a project
/// pinned to an older one (docs/save-version.md §5), and `saveVersionCheck` reports it. LabVIEW's
/// own Save for Previous Version, in place, brings such a file back (§6, measured 2026-11-20 and
/// 2026-11-23: a VI and a typedef `.ctl`, each to 20.0, one file per save; the project opened clean
/// afterwards). This tool does that for every file named, newer than its target and nothing else.
///
/// THROUGH A VI SERVER HELPER, not ActiveX: the ActiveX method `VI.SaveForPrevious(path, "20.0")`
/// answered no error and wrote 25.0 on LabVIEW 2026 (live check, 2026-11-23 - and FINDINGS.md had
/// recorded the same for "24.0" and "20.0" earlier), while the Invoke Node `Save.For Previous`
/// with the same string wrote 20.0. The read-back after each save catches a route that misses.
/// </summary>
[McpServerToolType]
internal sealed class SaveForPreviousTools(LvaiConnection connection)
{
    /// <summary>The helper's AIXML in the scripts folder, without its extension.</summary>
    internal const string Helper = "lvai_save_for_previous";

    /// <summary>More files than this in one call are refused: one save each, about a second apiece.</summary>
    internal const int MaxFiles = 300;

    /// <summary>The save itself - null, or LabVIEW's error text; a test replaces it and needs no LabVIEW.</summary>
    internal Func<string, string, int, CancellationToken, Task<string?>>? SaveOverride { get; init; }

    [McpServerTool(Name = "lvai_save_for_previous", Destructive = true, OpenWorld = true,
                   Title = "Save VIs back to the project's pinned LabVIEW version")]
    [Description("""
        Save for Previous Version IN PLACE: every .vi/.ctl/.vim/.vit/.ctt named that is saved NEWER
        than its target is saved again for the target by LabVIEW's own Save for Previous - the
        undo of a tool's save in the running LabVIEW's version into a project pinned older
        (`saveVersionCheck` in a tool's answer names such files). paths: files or folders (every
        such file under a folder), one per line. The target is targetVersion, else each file's
        project pin (NI.LV.All.SaveVersion of a project listing it or its owner, else the pin every
        project above it agrees on); a file with neither is skipped (`noPin`). A file at or below
        its target is not touched. One file per save: a dependency is saved only if named too.
        Each file is read back from disk (`savedAs`); `ok` is false when one did not reach its target.
        Close the files' project first: measured with the project closed. Needs LabVIEW running
        (ActiveX). `dryRun` lists what would be saved.
        """)]
    public async Task<string> SaveForPreviousAsync(
        [Description("Files or folders, one absolute path per line; a folder takes every VI and .ctl under it")]
        string paths,
        [Description("Target version, e.g. '20.0' or '2020'; empty means each file's project pin")]
        string targetVersion = "",
        [Description("List what would be saved, and save nothing")] bool dryRun = false,
        [Description("Budget in seconds for each file's save")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        string? target = null;
        if (!string.IsNullOrWhiteSpace(targetVersion))
        {
            target = NormaliseVersion(targetVersion);
            if (target is null)
                return Refuse("badArguments", $"targetVersion '{targetVersion}' is not a version: write '20.0' or '2020'.");
        }

        var (files, missing) = Expand(paths);
        if (files.Count == 0 && missing.Count == 0)
            return Refuse("badArguments", "paths names no file: give .vi or .ctl files or folders, one absolute path per line.");
        if (files.Count > MaxFiles)
            return Refuse("tooManyFiles", $"{files.Count} files; at most {MaxFiles} in one call - name sub-folders.");

        var rows = new JsonArray();
        int saved = 0, failed = 0, skipped = 0, wouldSave = 0;
        HousekeepingHelpers? helpers = null;
        string? helperVi = null;
        foreach (var m in missing)
            rows.Add(new JsonObject { ["path"] = m.Path, ["action"] = "skipped", ["why"] = m.Why });
        skipped += missing.Count;

        // typedefs before the VIs that use them: the order a developer would save them in
        foreach (var file in files.OrderBy(f => f.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                  .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var row = new JsonObject { ["path"] = file };
            rows.Add(row);
            var was = SaveVersions.Of(file);
            var (goal, pinFrom) = target is not null ? (target, "targetVersion") : PinOf(file);
            row["was"] = was;
            row["target"] = goal;
            if (pinFrom is not null && target is null) row["pinFrom"] = pinFrom;

            if (was is null) { Skip(row, "unreadable"); skipped++; continue; }
            if (goal is null) { Skip(row, "noPin"); skipped++; continue; }
            if (SaveVersions.Compare(was, goal) <= 0) { Skip(row, "atOrBelowTarget"); skipped++; continue; }
            if (dryRun) { row["action"] = "wouldSave"; wouldSave++; continue; }

            string? error;
            if (SaveOverride is { } save) error = await save(file, goal, timeoutSeconds, cancellationToken);
            else
            {
                if (helperVi is null)
                {
                    helpers = new HousekeepingHelpers(connection);
                    var (vi, _, failure) = await helpers.EnsureAsync(Helper, timeoutSeconds, cancellationToken);
                    if (failure is not null) return failure;
                    helperVi = vi!;
                }
                var (values, helperError) = await helpers!.RunAsync(helperVi, new Dictionary<string, string>
                {
                    ["vi path"] = file,
                    ["save path"] = file,
                    ["target version"] = goal,
                }, timeoutSeconds, cancellationToken);
                error = values is null
                    ? $"The helper VI returned no values: {helperError ?? "no detail"}."
                    : HousekeepingHelpers.Code(values, "error out") is { } code and not 0
                        ? $"LabVIEW error {code}: {HousekeepingHelpers.Source(values, "error out")}"
                        : null;
            }
            if (error is not null)
            {
                row["action"] = "failed";
                row["errorKind"] = "saveFailed";
                row["error"] = error;
                row["savedAs"] = SaveVersions.Of(file);
                failed++;
                continue;
            }
            var now = SaveVersions.Of(file);
            row["savedAs"] = now;
            if (now is not null && SaveVersions.Compare(now, goal) <= 0) { row["action"] = "saved"; saved++; }
            else
            {
                row["action"] = "failed";
                row["errorKind"] = "targetNotReached";
                row["error"] = $"LabVIEW answered no error, but the file reads {now ?? "unreadable"} - not {goal}.";
                failed++;
            }
        }

        var answer = new JsonObject
        {
            ["ok"] = failed == 0,
            ["dryRun"] = dryRun,
            ["saved"] = saved,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["files"] = rows,
        };
        if (dryRun) answer["wouldSave"] = wouldSave;
        if (saved > 0)
            answer["note"] = "Each file was saved alone: LabVIEW's Save for Previous writes no dependency. " +
                             "A subVI or typedef still newer than the target needs its own entry.";
        return answer.ToJsonString(Json.Answer);
    }

    /// <summary>"20.0" from "20.0", "20" or "2020"; null when it is none of those.</summary>
    internal static string? NormaliseVersion(string text)
    {
        var t = text.Trim();
        if (Regex.IsMatch(t, @"^20\d\d$")) return $"{int.Parse(t[2..], System.Globalization.CultureInfo.InvariantCulture)}.0";
        if (Regex.IsMatch(t, @"^\d{1,2}$")) return $"{int.Parse(t, System.Globalization.CultureInfo.InvariantCulture)}.0";
        return Regex.IsMatch(t, @"^\d{1,2}\.\d$") ? t : null;
    }

    private static (string? Version, string? From) PinOf(string file) =>
        SaveVersions.PinFor(file) is { } listed ? (listed.Version, "listed")
        : SaveVersions.PinAbove(file) is { } above ? (above.Version, "folder")
        : (null, null);

    /// <summary>Every resource file named or under a named folder; missing paths apart.</summary>
    internal static (List<string> Files, List<(string Path, string Why)> Missing) Expand(string paths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<(string Path, string Why)>();
        foreach (var line in paths.Split('\n').Select(l => l.Trim().Trim('"')).Where(l => l.Length > 0))
        {
            string full;
            try { full = Path.GetFullPath(line); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                missing.Add((line, "notFound"));
                continue;
            }
            if (Directory.Exists(full))
            {
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    if (IsResource(f)) files.Add(f);
            }
            else if (File.Exists(full)) { if (IsResource(full)) files.Add(full); else missing.Add((full, "notAViOrCtl")); }
            else missing.Add((line, "notFound"));
        }
        return (files.ToList(), missing);
    }

    private static bool IsResource(string path) =>
        SaveVersions.ResourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static void Skip(JsonObject row, string why)
    {
        row["action"] = "skipped";
        row["why"] = why;
    }

    private static string Refuse(string kind, string message) =>
        new JsonObject { ["ok"] = false, ["errorKind"] = kind, ["error"] = message }.ToJsonString(Json.Answer);
}
