// =============================================================================
// ProjectRewrite.cs - what LabVIEW's own SAVE of a project changed in the .lvproj:
// a copy of the file is taken before a project close, compared after it, and kept
// so the caller can put it back.
// =============================================================================
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// EVERY PROJECT CLOSE SAVES (the close helper runs Save, then Close, because an unsaved close
/// would raise a modal prompt), AND A SAVE IN ANOTHER LabVIEW REWRITES THE FILE. Measured
/// 2026-11-13 on a field project pinned to 21.0 and kept for LabVIEW 2021 64-bit: LabVIEW 2026
/// 32-bit marked it changed (`*`) the moment it opened it, and lvai_restart_labview's close saved
/// it - 154 402 -> 100 287 bytes, Dependencies emptied (392 entries), and an installer
/// specification's `DistPart` for "NI LabVIEW Runtime 2021 SP1 f6 (64-bit)" replaced by a generic
/// "Run-Time Engine 2021" with an empty productID. The pin stayed 21.0, so the save-version check
/// was silent, and the only trace in the answer was "the close may rewrite the .lvproj".
///
/// So the close takes a copy first and reports here what LabVIEW's save changed - entries gone and
/// added, Dependencies before and after, build-specification properties, the version stamps - and
/// where the copy is. Whether to put it back is the caller's decision: a save that carries this
/// session's own project edits must stay.
/// </summary>
internal static class ProjectRewrite
{
    /// <summary>The .lvproj as it was before the close.</summary>
    internal sealed record Snapshot(string Path, byte[] Bytes);

    /// <summary>At most this many names per list in the answer; the counts are always complete.</summary>
    internal const int MaxListed = 20;

    /// <summary>Copies older than the newest this many are deleted when a new one is written.</summary>
    internal const int BackupsKept = 50;

    /// <summary>Where the copies go: under the cache root, never beside the user's project.</summary>
    internal static string BackupDirectory => System.IO.Path.Combine(CacheDirectory.Root, "project-backups");

    /// <summary>The file's bytes now; null when there is no such file or it cannot be read.</summary>
    internal static Snapshot? Take(string? projectPath)
    {
        if (projectPath is not { Length: > 0 }) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(projectPath);
            return File.Exists(full) ? new Snapshot(full, File.ReadAllBytes(full)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                    or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compare the file now against <paramref name="before"/>. Unchanged: a short answer and no
    /// copy. Changed: the copy is written to <paramref name="backupDirectory"/> (default
    /// <see cref="BackupDirectory"/>) and the differences are listed.
    /// </summary>
    internal static JsonObject Report(Snapshot before, string? backupDirectory = null)
    {
        byte[] after;
        try { after = File.ReadAllBytes(before.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new JsonObject
            {
                ["changed"] = null,
                ["projectPath"] = before.Path,
                ["reason"] = "projectNotReadable",
                ["detail"] = e.Message,
            };
        }

        if (before.Bytes.AsSpan().SequenceEqual(after))
            return new JsonObject
            {
                ["changed"] = false,
                ["projectPath"] = before.Path,
                ["note"] = "LabVIEW's save left the .lvproj byte for byte as it was.",
            };

        var result = new JsonObject
        {
            ["changed"] = true,
            ["projectPath"] = before.Path,
            ["backupPath"] = WriteBackup(before, backupDirectory),
            ["bytes"] = new JsonObject { ["before"] = before.Bytes.Length, ["after"] = after.Length },
        };

        var (old, now) = (Parse(before.Bytes), Parse(after));
        if (old is null || now is null)
        {
            result["note"] = "The file changed, and one side could not be read as XML, so only the " +
                             "sizes are compared.";
            result["warning"] = Warning((string?)result["backupPath"], 0, 0, 0, 0, 0, null, null);
            return result;
        }

        var stampBefore = Stamp(old);
        var stampAfter = Stamp(now);
        result["lvVersion"] = new JsonObject { ["before"] = stampBefore, ["after"] = stampAfter };
        result["saveVersion"] = new JsonObject { ["before"] = Pin(old), ["after"] = Pin(now) };

        var depsBefore = DependencyCount(old);
        var depsAfter = DependencyCount(now);
        result["dependencies"] = new JsonObject { ["before"] = depsBefore, ["after"] = depsAfter };

        var itemsBefore = ProjectItems(old);
        var itemsAfter = ProjectItems(now);
        var removed = itemsBefore.Except(itemsAfter, StringComparer.Ordinal).ToList();
        var added = itemsAfter.Except(itemsBefore, StringComparer.Ordinal).ToList();
        var moved = MovedIntoOwner(removed, LvClass.OwnedMemberPaths(before.Path));
        result["items"] = new JsonObject
        {
            ["removedCount"] = removed.Count,
            ["removed"] = Names(removed),
            ["movedIntoLibraryOrClassCount"] = moved.Count,
            ["movedIntoLibraryOrClass"] = Names(moved),
            ["addedCount"] = added.Count,
            ["added"] = Names(added),
        };

        var (specsRemoved, specsAdded, specChanges) = BuildSpecChanges(old, now);
        result["buildSpecifications"] = new JsonObject
        {
            ["changedPropertyCount"] = specChanges.Count,
            ["changedProperties"] = new JsonArray([.. specChanges.Take(MaxListed).Select(c => (JsonNode)new JsonObject
            {
                ["spec"] = c.Spec,
                ["property"] = c.Property,
                ["before"] = c.Before,
                ["after"] = c.After,
            })]),
            ["specsRemoved"] = Names(specsRemoved),
            ["specsAdded"] = Names(specsAdded),
        };

        var otherChanges = PropertyChanges(old, now);
        result["otherPropertiesChangedCount"] = otherChanges.Count;
        result["otherPropertiesChanged"] = Names(otherChanges);

        result["warning"] = Warning((string?)result["backupPath"], removed.Count - moved.Count, moved.Count,
                                    specChanges.Count + specsRemoved.Count, depsBefore, depsAfter,
                                    stampBefore, stampAfter);
        return result;
    }

    /// <summary>One sentence per kind of change, then where the copy is and when to use it.</summary>
    /// <summary>
    /// The removed entries that are now MEMBERS of a library or class the project lists, matched by
    /// file name: LabVIEW drops the loose entry of a VI that joined a library, and the note called
    /// that "went missing" - a false alarm in every benchmark build with a library step.
    /// </summary>
    internal static List<string> MovedIntoOwner(IEnumerable<string> removed, IEnumerable<string> ownedMemberPaths)
    {
        var owned = ownedMemberPaths.Select(p => Path.GetFileName(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. removed.Where(r => owned.Contains(r[(r.LastIndexOfAny(['/', '\\']) + 1)..]))];
    }

    private static string Warning(string? backupPath, int itemsRemoved, int itemsMoved, int specChanges, int depsBefore,
                                  int depsAfter, string? stampBefore, string? stampAfter)
    {
        var parts = new List<string> { "LabVIEW's save REWROTE the .lvproj." };
        if (stampBefore is not null && stampAfter is not null && stampBefore != stampAfter)
            parts.Add($"It was written by LabVIEW {stampBefore} and is now stamped {stampAfter}.");
        if (specChanges > 0)
            parts.Add($"{Count(specChanges, "build-specification property", "build-specification properties")} " +
                      "changed or went missing (see buildSpecifications): an application or installer built " +
                      "from this project can now differ - one measured case swapped a 64-bit run-time " +
                      "component for a generic one with no product ID.");
        if (itemsRemoved > 0)
            parts.Add($"{Count(itemsRemoved, "project entry", "project entries")} outside Dependencies " +
                      "went missing (see items.removed).");
        if (itemsMoved > 0)
            parts.Add($"{Count(itemsMoved, "project entry", "project entries")} left the project tree because " +
                      "the VI is now a member of a library or class the project lists (see " +
                      "items.movedIntoLibraryOrClass) - nothing is lost.");
        if (depsAfter < depsBefore)
            parts.Add($"Dependencies went from {depsBefore} to {depsAfter} entries (LabVIEW lists them " +
                      "again when it loads the VIs).");
        parts.Add(backupPath is null
            ? "The copy taken before the save could not be written."
            : $"The file as it was before this save: {backupPath}. If this session changed nothing in " +
              "the project, copy it back while the project is closed.");
        return string.Join(" ", parts);
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string? WriteBackup(Snapshot before, string? directory)
    {
        try
        {
            var dir = directory ?? BackupDirectory;
            Directory.CreateDirectory(dir);
            var name = System.IO.Path.GetFileNameWithoutExtension(before.Path);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var target = System.IO.Path.Combine(dir, $"{name}.{stamp}.lvproj");
            File.WriteAllBytes(target, before.Bytes);
            Prune(dir);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Prune(string dir)
    {
        try
        {
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.lvproj")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(BackupsKept))
                old.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static XDocument? Parse(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return XDocument.Load(stream);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>"21.0" from the root's LVVersion ("21008000"), else the raw attribute.</summary>
    private static string? Stamp(XDocument doc)
    {
        var raw = (string?)doc.Root?.Attribute("LVVersion");
        return raw is { Length: >= 4 } && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                   out var word) && SaveVersions.FromWord(word) is { } version
            ? version
            : raw;
    }

    private static string? Pin(XDocument doc) =>
        doc.Root?.Elements("Property")
            .FirstOrDefault(p => (string?)p.Attribute("Name") == "NI.LV.All.SaveVersion")?.Value.Trim();

    private static bool IsItem(XElement e, string type) =>
        e.Name.LocalName == "Item" && (string?)e.Attribute("Type") == type;

    /// <summary>Entries anywhere under an Item of Type Dependencies, folders not counted.</summary>
    private static int DependencyCount(XDocument doc) =>
        doc.Descendants("Item").Count(i => (string?)i.Attribute("Type") != "Folder"
                                           && i.Ancestors().Any(a => IsItem(a, "Dependencies")));

    /// <summary>The tree path of an element: its own and its Item ancestors' names.</summary>
    private static string PathOf(XElement e) =>
        string.Join("/", e.AncestorsAndSelf("Item").Reverse().Select(i => (string?)i.Attribute("Name") ?? "?"));

    /// <summary>
    /// The project's own entries: every Item outside Dependencies and Build Specifications, as tree
    /// paths. Those two are reported apart - LabVIEW regenerates the first, and the second is
    /// compared property by property.
    /// </summary>
    private static HashSet<string> ProjectItems(XDocument doc) =>
        doc.Descendants("Item")
            .Where(i => !IsItem(i, "Dependencies") && !IsItem(i, "Build")
                        && !i.Ancestors().Any(a => IsItem(a, "Dependencies") || IsItem(a, "Build")))
            .Select(PathOf)
            .ToHashSet(StringComparer.Ordinal);

    private sealed record SpecChange(string Spec, string Property, string? Before, string? After);

    private static Dictionary<string, Dictionary<string, string>> Specs(XDocument doc) =>
        doc.Descendants("Item")
            .Where(i => i.Parent is { } p && IsItem(p, "Build"))
            .GroupBy(PathOf, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.First().Elements("Property")
                    .GroupBy(p => (string?)p.Attribute("Name") ?? "", StringComparer.Ordinal)
                    .ToDictionary(p => p.Key, p => p.First().Value, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static (List<string> Removed, List<string> Added, List<SpecChange> Changes) BuildSpecChanges(
        XDocument old, XDocument now)
    {
        var before = Specs(old);
        var after = Specs(now);
        var changes = new List<SpecChange>();
        foreach (var (spec, props) in before)
        {
            if (!after.TryGetValue(spec, out var newProps)) continue;
            foreach (var key in props.Keys.Union(newProps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                props.TryGetValue(key, out var was);
                newProps.TryGetValue(key, out var isNow);
                if (!string.Equals(was, isNow, StringComparison.Ordinal))
                    changes.Add(new SpecChange(spec, key, was, isNow));
            }
        }
        return (before.Keys.Except(after.Keys, StringComparer.Ordinal).ToList(),
                after.Keys.Except(before.Keys, StringComparer.Ordinal).ToList(),
                changes);
    }

    /// <summary>
    /// Properties outside the build specifications - the project's own and each target's - keyed by
    /// owner path and name; one entry per property whose value differs or that exists on one side only.
    /// </summary>
    private static List<string> PropertyChanges(XDocument old, XDocument now)
    {
        static Dictionary<string, string> Props(XDocument doc) =>
            doc.Descendants("Property")
                .Where(p => !p.Ancestors().Any(a => IsItem(a, "Build") || IsItem(a, "Dependencies")))
                .GroupBy(p => (p.Parent is { Name.LocalName: "Item" } owner ? PathOf(owner) : "(project)")
                              + ":" + ((string?)p.Attribute("Name") ?? ""), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

        var before = Props(old);
        var after = Props(now);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b)
                        || !string.Equals(a, b, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static JsonArray Names(IEnumerable<string> names) =>
        new([.. names.Take(MaxListed).Select(n => (JsonNode)n)]);
}
