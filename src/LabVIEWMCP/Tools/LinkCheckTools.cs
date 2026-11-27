// =============================================================================
// LinkCheckTools.cs - lvai_check_links: which installation links (<vilib> and kin)
// of a set of VIs an installed TARGET LabVIEW lacks - read from the files, no LabVIEW.
// =============================================================================
using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// WHY. A VI saved in a newer LabVIEW can link to `vi.lib` VIs an older target lacks - a save
/// relinks some of them to newer versions - and the target says so only when it LOADS the VI,
/// on the target PC. Two field projects found it there (2026-11): one saving for LabVIEW 2018 from
/// 2025/2026, one for 2021 from 2026. A save version alone does not catch it: the file can be
/// written for the old version and still name a VI only the new vi.lib has. This reads every VI's
/// link records (<see cref="ViLinks"/>) and looks each installation path up in a LabVIEW that is
/// INSTALLED on this PC; shipping file lists of every LabVIEW version was left out on purpose.
/// </summary>
[McpServerToolType]
internal sealed class LinkCheckTools
{
    internal const int MaxFiles = 5000;

    private static readonly string[] Extensions = [".vi", ".vim", ".ctl", ".vit", ".ctt", ".vix"];

    [McpServerTool(Name = "lvai_check_links", ReadOnly = true,
                   Title = "Find vi.lib links an older target LabVIEW lacks")]
    [Description("""
        Save version: which links of a set of VIs into LabVIEW's own folders (<vilib>, <userlib>,
        <instrlib>, <resource>, <menus>) an INSTALLED target LabVIEW does not have - read from the
        files' link records, NO running LabVIEW. Use it before shipping to an older LabVIEW: a save
        in a newer one can link a vi.lib VI the target lacks, and the target only says so when it
        loads the VI. `labview` names the target ("2021", "21.0", or an install folder or exe);
        omitted, it is the save version the nearest .lvproj pins. `missing` lists each absent path
        with the files that link it; an .llb member is checked by its name in the .llb (a
        heuristic). Relative links (your own code) and <extravilib> (generated channel instances)
        are counted, not checked.
        """)]
    public Task<string> CheckLinksAsync(
        [Description("""
            Files or folders to check, ONE ABSOLUTE PATH PER LINE: .vi/.vim/.ctl/.vit files, folders
            (searched recursively), or a .lvproj (its folder).
            """)]
        string paths,
        [Description("""
            The target LabVIEW: a release ("2021", "21.0", "21"), an install folder or its LabVIEW.exe.
            Omitted: the save version pinned by the nearest .lvproj above the first path.
            """)]
        string? labview = null,
        [Description("With a release: '32' or '64' to pick the bitness when both are installed (default: 32-bit when present)")]
        string? bitness = null) =>
        Rpc.GuardAsync(() => Task.FromResult(Run(paths, labview, bitness)));

    private static string Run(string paths, string? labview, string? bitness)
    {
        var requested = (paths ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0)
            return Json.Error("badArguments", "paths was empty. Pass one absolute file or folder per line.");
        if (requested.FirstOrDefault(p => !File.Exists(p) && !Directory.Exists(p)) is { } absent)
            return Json.Error("badArguments", $"No file or folder at '{absent}'.", new { absent });

        var (target, chosenBy, problem) = ResolveTarget(labview, bitness, requested[0], LabViewLocator.Discover());
        if (target is null)
            return Json.Error("noTargetLabview", problem!, new
            {
                labview,
                installed = LabViewLocator.Discover().Select(i => $"{i.Release} {(i.Is32Bit ? "32" : "64")}-bit {Path.GetDirectoryName(i.ExePath)}").ToArray(),
            });

        var files = Files(requested, out var truncated);
        return Json.Document(Check(files, target.Value.Root, target.Value.Release, chosenBy, truncated));
    }

    /// <summary>The check itself, apart from discovery so a test can name the installation folder.</summary>
    internal static JsonObject Check(IReadOnlyList<string> files, string installRoot, int? release, string chosenBy,
                                     bool truncated = false)
    {
        var missing = new Dictionary<string, (string Root, ViLinks.Presence Presence, List<string> Files)>(StringComparer.OrdinalIgnoreCase);
        var notChecked = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unreadable = new JsonArray();
        var linksChecked = 0;
        foreach (var file in files)
        {
            IReadOnlyList<ViLinks.PathRef> links;
            try { links = ViLinks.Read(File.ReadAllBytes(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                          or OverflowException or ArgumentOutOfRangeException)
            {
                unreadable.Add(new JsonObject { ["file"] = file, ["problem"] = e.Message });
                continue;
            }
            foreach (var link in links.DistinctBy(l => l.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var root = link.Root;
                if (root is null || !ViLinks.InstallationRoots.ContainsKey(root))
                {
                    var kind = root ?? (link.Type == 1 ? "(relative)" : "(absolute)");
                    notChecked[kind] = notChecked.GetValueOrDefault(kind) + 1;
                    continue;
                }
                linksChecked++;
                var presence = ViLinks.Check(installRoot, link);
                if (presence == ViLinks.Presence.Present) continue;
                var key = link.ToString();
                if (!missing.TryGetValue(key, out var entry)) missing[key] = entry = (root, presence, []);
                entry.Files.Add(file);
            }
        }

        var missingJson = new JsonArray([.. missing.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(m => (JsonNode)new JsonObject
        {
            ["link"] = m.Key,
            ["why"] = m.Value.Presence == ViLinks.Presence.MissingFromContainer
                ? "the container is there, the member name is not in it" : "no such file in the target",
            ["linkedBy"] = new JsonArray([.. m.Value.Files.Take(5).Select(f => (JsonNode)f)]),
            ["linkedByCount"] = m.Value.Files.Count,
        })]);
        return new JsonObject
        {
            ["ok"] = missing.Count == 0 && unreadable.Count == 0,
            ["target"] = new JsonObject
            {
                ["release"] = release,
                ["installFolder"] = installRoot,
                ["chosenBy"] = chosenBy,
            },
            ["filesChecked"] = files.Count - unreadable.Count,
            ["filesTruncated"] = truncated ? true : null,
            ["linksChecked"] = linksChecked,
            ["missingCount"] = missing.Count,
            ["missing"] = missingJson,
            ["notChecked"] = new JsonObject([.. notChecked.Select(n => new KeyValuePair<string, JsonNode?>(n.Key, n.Value))]),
            ["unreadable"] = unreadable.Count > 0 ? unreadable : null,
            ["note"] = missing.Count == 0
                ? "Every installation link names a file the target installation has. That says nothing " +
                  "about the files' own save version (saveVersionCheck) or about relative links."
                : "The target LabVIEW lacks these files, so each VI in linkedBy will not load there as " +
                  "it is. Replace the call with one the target has (lvai_palette_index of that version), " +
                  "or rebuild it from primitives.",
        };
    }

    /// <summary>The files to read: the given ones, and the LabVIEW files under the given folders.</summary>
    internal static IReadOnlyList<string> Files(IEnumerable<string> requested, out bool truncated)
    {
        var files = new List<string>();
        foreach (var item in requested)
        {
            var path = Path.GetFullPath(item);
            var folder = Directory.Exists(path) ? path
                : Path.GetExtension(path).Equals(".lvproj", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) : null;
            if (folder is null) { files.Add(path); continue; }
            files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
        }
        var distinct = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        truncated = distinct.Count > MaxFiles;
        return truncated ? distinct[..MaxFiles] : distinct;
    }

    /// <summary>
    /// The target installation's folder and release: an explicit folder or exe, a release (with
    /// bitness), or the pin of the nearest project. A problem text when none can be chosen.
    /// </summary>
    internal static ((string Root, int? Release)? Target, string ChosenBy, string? Problem) ResolveTarget(
        string? labview, string? bitness, string firstPath, IReadOnlyList<LabViewInstall> installs)
    {
        var given = labview?.Trim().Trim('"');
        if (given is { Length: > 0 } && (given.Contains('\\') || given.Contains('/')))
        {
            var folder = File.Exists(given) ? Path.GetDirectoryName(Path.GetFullPath(given))! : Path.GetFullPath(given);
            if (!Directory.Exists(Path.Combine(folder, "vi.lib")))
                return (null, "", $"'{given}' is not a LabVIEW installation: no vi.lib in '{folder}'.");
            var known = installs.FirstOrDefault(i => string.Equals(Path.GetDirectoryName(i.ExePath), folder, StringComparison.OrdinalIgnoreCase));
            return ((folder, known?.Release), "the given folder", null);
        }

        string chosenBy;
        int? release;
        if (given is { Length: > 0 })
        {
            release = SaveVersions.Release(given.Length == 4 && given.StartsWith("20", StringComparison.Ordinal) ? given[2..] : given);
            chosenBy = "the given release";
            if (release is null) return (null, "", $"labview '{given}' is neither a release (2021, 21.0) nor a folder.");
        }
        else
        {
            var start = Directory.Exists(firstPath) ? Path.Combine(firstPath, "_") : firstPath;
            var pin = firstPath.EndsWith(".lvproj", StringComparison.OrdinalIgnoreCase) && SaveVersions.Pinned(firstPath) is { } own
                ? (firstPath, own) : SaveVersions.PinFor(start) ?? SaveVersions.PinAbove(start);
            if (pin is not { } p)
                return (null, "", "No labview was given and no .lvproj above the first path pins a save version. Name the target.");
            release = SaveVersions.Release(p.Version);
            chosenBy = $"the save version {p.Version} pinned by {Path.GetFileName(p.Project)}";
        }

        var candidates = installs.Where(i => i.Release == release).ToList();
        if (bitness is { Length: > 0 })
            candidates = [.. candidates.Where(i => i.Is32Bit == bitness.Trim().StartsWith("32", StringComparison.Ordinal))];
        var pick = candidates.OrderByDescending(i => i.Is32Bit).FirstOrDefault();
        if (pick is null)
            return (null, "", $"LabVIEW {release}{(bitness is { Length: > 0 } ? $" {bitness}-bit" : "")} is not installed on this PC " +
                              "(chosen by " + chosenBy + "). The check needs the target's own vi.lib.");
        return ((Path.GetDirectoryName(pick.ExePath)!, pick.Release), $"{chosenBy}, {(pick.Is32Bit ? "32" : "64")}-bit", null);
    }
}
