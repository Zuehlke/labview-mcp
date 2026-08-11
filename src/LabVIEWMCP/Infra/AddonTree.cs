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
