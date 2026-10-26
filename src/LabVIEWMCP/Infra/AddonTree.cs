namespace LabVIEWMcp.Infra;

/// <summary>One add-on subtree to scan: the folder, and the add-on it belongs to.</summary>
internal sealed record AddonFolder(string Addon, string Folder);

/// <summary>
/// Walking %ProgramFiles%\NI\LVAddons, shared by every index that has to look there.
///
/// Drivers and toolkits no longer install into the IDE folder: they land under
/// &lt;root&gt;\&lt;addon&gt;\&lt;api version&gt;\&lt;something&gt;, and LabVIEW merges them in at run time.
/// Scanning the IDE folder alone therefore reports a whole driver as absent while its VIs resolve
/// perfectly well - the bug that made <see cref="PaletteIndex"/> scan both roots in the first place.
///
/// MEASURED 2026-08-07 on this station: the two subtrees are NOT the same set. Twelve add-ons ship
/// a `menus` folder and fourteen ship an `examples` folder - `dct32` and `dct64` have examples with
/// no palette, and no add-on has the reverse. So each index must enumerate for itself rather than
/// reuse the other's add-on list.
/// </summary>
internal static class AddonTree
{
    /// <summary>
    /// %ProgramFiles%\NI\LVAddons, or null when absent. Never a hardcoded drive: the 64-bit root
    /// is the right one even though LabVIEW itself is a 32-bit application under the "(x86)" tree.
    /// </summary>
    public static string? DefaultRoot()
    {
        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var programFiles = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(programFiles)) continue;

            var candidate = Path.Combine(programFiles, "NI", "LVAddons");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Every add-on subtree named <paramref name="folderName"/> that supports this release.
    /// Add-ons needing a newer LabVIEW are returned in <c>Skipped</c> - never dropped quietly,
    /// because a silently missing driver is worse than no index at all.
    /// </summary>
    public static (List<AddonFolder> Folders, List<string> Skipped) Enumerate(
        string? addonsRoot, string folderName, int? release)
    {
        var folders = new List<AddonFolder>();
        var skipped = new List<string>();
        if (addonsRoot is null || !Directory.Exists(addonsRoot)) return (folders, skipped);

        foreach (var addonDirectory in Directory.EnumerateDirectories(addonsRoot).OrderBy(d => d))
        {
            var addon = Path.GetFileName(addonDirectory);
            foreach (var apiDirectory in Directory.EnumerateDirectories(addonDirectory).OrderBy(d => d))
            {
                var folder = Path.Combine(apiDirectory, folderName);
                if (!Directory.Exists(folder)) continue;   // plenty of add-ons ship neither

                var minimum = MinimumSupportedRelease(apiDirectory);
                if (release is not null && minimum is not null && minimum > release)
                {
                    skipped.Add($"{addon} (needs LabVIEW {minimum}, this is {release})");
                    continue;
                }
                folders.Add(new AddonFolder(addon, folder));
            }
        }
        return (folders, skipped);
    }

    /// <summary>
    /// Per add-on folder of <paramref name="location"/>, the ONE version folder LabVIEW loads: the
    /// highest whose lvaddoninfo.json admits this LabVIEW - <c>MinimumSupportedLVVersion</c> against
    /// <paramref name="major"/>.<paramref name="minor"/>, <c>SupportedBitness</c> against
    /// <paramref name="bits"/>; an add-on with no usable lvaddoninfo.json is not loaded at all
    /// (<see cref="Admits"/>). The same rule as scripts\aixml-pylabview\resolve.py's
    /// select_addons, and LabViewContextTests holds the two to one fixture. Measured 2026-10-26 on LabVIEW 26.3.1 (32-bit): of lvai\1, 26.1 and 26.3 its log
    /// names files under 26.3 only, and nothing under a Targets\win64.
    /// <c>AddonFolder.Folder</c> is the VERSION folder; <see cref="Trees"/> gives what overlays.
    /// </summary>
    public static List<AddonFolder> Select(string location, int? major, int? minor, int? bits)
    {
        var chosen = new List<AddonFolder>();
        if (!Directory.Exists(location)) return chosen;
        foreach (var addonDirectory in Directory.EnumerateDirectories(location).Order(StringComparer.Ordinal))
        {
            (int[] Key, string Folder)? best = null;
            foreach (var versionDirectory in Directory.EnumerateDirectories(addonDirectory))
            {
                if (VersionKey(Path.GetFileName(versionDirectory)) is not { } key) continue;
                if (!Admits(versionDirectory, major, minor, bits)) continue;
                if (best is null || Compare(key, best.Value.Key) > 0) best = (key, versionDirectory);
            }
            if (best is { } b) chosen.Add(new AddonFolder(Path.GetFileName(addonDirectory), b.Folder));
        }
        return chosen;
    }

    /// <summary>The folders of one add-on version that overlay the installation: itself, and
    /// Targets\win32 or Targets\win64 by bitness - both when it is unknown.</summary>
    public static IEnumerable<string> Trees(string versionDirectory, int? bits)
    {
        yield return versionDirectory;
        foreach (var target in bits switch { 32 => ["win32"], 64 => ["win64"], _ => new[] { "win32", "win64" } })
        {
            var folder = Path.Combine(versionDirectory, "Targets", target);
            if (Directory.Exists(folder)) yield return folder;
        }
    }

    /// <summary>
    /// Every subtree named <paramref name="folderName"/> (menus, examples) of the add-ons the
    /// context's LabVIEW loads, in location order. Replaces <see cref="Enumerate"/> for callers
    /// that know the LabVIEW: no other version of an add-on, no other bitness.
    /// </summary>
    public static (List<AddonFolder> Folders, List<string> Skipped) Enumerate(LabViewContext context, string folderName)
    {
        var folders = new List<AddonFolder>();
        var chosen = context.SelectedAddons();
        foreach (var addon in chosen)
            foreach (var tree in Trees(addon.Folder, context.Bits))
            {
                var folder = Path.Combine(tree, folderName);
                if (Directory.Exists(folder)) folders.Add(new AddonFolder(addon.Addon, folder));
            }

        // every OTHER version folder carrying such a subtree - named, never dropped quietly
        var skipped = new List<string>();
        if (context.AddonsEnabled)
            foreach (var location in context.AddonLocations)
                try
                {
                    foreach (var addonDirectory in Directory.EnumerateDirectories(location).Order(StringComparer.Ordinal))
                        foreach (var versionDirectory in Directory.EnumerateDirectories(addonDirectory).Order(StringComparer.Ordinal))
                            if (!chosen.Any(c => LabViewContext.SamePath(c.Folder, versionDirectory)) &&
                                Trees(versionDirectory, null).Any(t => Directory.Exists(Path.Combine(t, folderName))))
                                skipped.Add($"{Path.GetFileName(addonDirectory)}\\{Path.GetFileName(versionDirectory)} " +
                                            $"(not the version LabVIEW {context.Major}.{context.Minor} {context.Bits}-bit loads)");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (folders, skipped);
    }

    /// <summary>What an index over an installation and its add-ons reads, and how it is cached.</summary>
    /// <param name="Root">The LabVIEW installation folder.</param>
    /// <param name="Key">The cache key: equal keys read the same trees.</param>
    /// <param name="Persist">False for a context with locations of its own (an lvpm venv): such an
    /// index lives in memory only, under a key carrying the locations' write times, because those
    /// trees change between two calls where the default root changes on an install.</param>
    /// <param name="Folders">The add-on subtrees to read.</param>
    /// <param name="Skipped">Add-on versions not read, each with why.</param>
    internal sealed record IndexPlan(string Root, string Key, bool Persist,
                                     List<AddonFolder> Folders, List<string> Skipped);

    /// <summary>
    /// The plan for the palette or example index. With an explicit <paramref name="labviewRoot"/>
    /// (a test's synthetic tree) exactly that root and <paramref name="addonsRoot"/>, as before.
    /// Otherwise the LabVIEW the server works for (<see cref="LabViewContext"/>): its installation,
    /// and per add-on the one version it loads, from every location its INI names - issue #99.
    /// </summary>
    internal static IndexPlan PlanIndex(string? labviewRoot, string? addonsRoot, string folderName, string what)
    {
        if (labviewRoot is not null)
        {
            var (f, s) = Enumerate(addonsRoot, folderName, null);
            return new IndexPlan(labviewRoot, labviewRoot + "|" + (addonsRoot ?? ""), true, f, s);
        }

        var context = LabViewContext.Resolve();
        var root = context.Root ?? throw new InvalidOperationException(
            $"No LabVIEW installation found, so its {what} cannot be read. Pass installRoot " +
            "explicitly, or check that LabVIEW is installed under a Program Files root.");
        if (addonsRoot is not null)
        {
            var (f, s) = Enumerate(addonsRoot, folderName, context.Release);
            return new IndexPlan(root, root + "|" + addonsRoot, true, f, s);
        }

        var (folders, skipped) = Enumerate(context, folderName);
        var persist = context.OnlyDefaultLocations;
        // "sel1": the add-on SELECTION is part of the key since 2026-10-26 - an index cached before
        // read every version of every add-on, and is rebuilt once rather than served
        var key = root + "|sel1|" + (persist ? context.LocationsKey : "ctx|" + context.Fingerprint());
        return new IndexPlan(root, key, persist, folders, skipped);
    }

    /// <summary>
    /// May this LabVIEW load that add-on version? Measured 2026-10-26 on LabVIEW 26.3.1 through
    /// -pref, a call into each probe add-on: it is IGNORED without lvaddoninfo.json, with text that
    /// is not JSON, with a UTF-8 BOM before the JSON (PowerShell 5's `-Encoding utf8` writes one; NI's
    /// files carry none) and without an <c>AddonName</c>. An AddonName other than the folder name
    /// loads. An absent version or bitness field does not exclude it.
    /// </summary>
    internal static bool Admits(string versionDirectory, int? major, int? minor, int? bits)
    {
        try
        {
            var info = Path.Combine(versionDirectory, "lvaddoninfo.json");
            if (!File.Exists(info)) return false;
            var bytes = File.ReadAllBytes(info);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return false;
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!root.TryGetProperty("AddonName", out var name) ||
                name.ValueKind != System.Text.Json.JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
                return false;

            if (bits is { } b && root.TryGetProperty("SupportedBitness", out var bitness) &&
                bitness.ToString().Trim() is { Length: > 0 } want && want != b.ToString())
                return false;

            if (major is { } m && root.TryGetProperty("MinimumSupportedLVVersion", out var minimum) &&
                VersionKey(minimum.ToString()) is { } need &&
                Compare([need[0], need.Length > 1 ? need[1] : 0], [m, minor ?? 99]) > 0)
                return false;
            return true;
        }
        catch
        {
            return false;   // not JSON: LabVIEW ignores the add-on (measured)
        }
    }

    /// <summary>"26.3" -&gt; [26, 3], "1" -&gt; [1], "v26.1" -&gt; [26, 1]; null when it is no version.</summary>
    internal static int[]? VersionKey(string? text)
    {
        var match = System.Text.RegularExpressions.Regex.Match((text ?? "").Trim(), @"^v?(\d+(?:\.\d+)*)$");
        return match.Success ? match.Groups[1].Value.Split('.').Select(int.Parse).ToArray() : null;
    }

    /// <summary>Tuple order, as Python compares (26,) &lt; (26, 1).</summary>
    private static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// lvaddoninfo.json's MinimumSupportedLVVersion as a LabVIEW release year: "22.0" -&gt; 2022.
    /// Null when the file is missing or unreadable - in which case the add-on is scanned, because
    /// omitting a driver is the more expensive mistake.
    /// </summary>
    public static int? MinimumSupportedRelease(string apiDirectory)
    {
        try
        {
            var info = Path.Combine(apiDirectory, "lvaddoninfo.json");
            if (!File.Exists(info)) return null;

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
            if (!document.RootElement.TryGetProperty("MinimumSupportedLVVersion", out var value))
                return null;

            var text = value.GetString();
            var dot = text?.IndexOf('.') ?? -1;
            var major = dot > 0 ? text![..dot] : text;
            return int.TryParse(major, out var version) ? 2000 + version : null;
        }
        catch
        {
            return null;
        }
    }
}
