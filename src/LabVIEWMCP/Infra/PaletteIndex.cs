using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>One palette-reachable VI, and the palette file it was found in.</summary>
/// <param name="Name">The bare file name, e.g. "General Error Handler.vi".</param>
/// <param name="PaletteFile">
/// Path of the .mnu relative to its menus folder, prefixed with the add-on name when the entry
/// came from an LVAddon rather than from LabVIEW itself.
/// </param>
internal sealed record PaletteVi(string Name, string PaletteFile);

/// <summary>A palette tree to scan: a menus folder and the label its entries are reported under.</summary>
internal sealed record PaletteSource(string Label, string MenusFolder);

/// <summary>
/// The set of VIs reachable from LabVIEW's palettes, read from the .mnu files of the installed
/// LabVIEW.
///
/// Why this matters: AIXML generation accepts a `Call` only to a PALETTE-REACHABLE VI, by bare
/// file name. Anything else - project-local, library-local, even a loose .vi in a folder - is
/// rejected as "Unsupported SubVI". So "what may a generated VI call" is exactly "what is on the
/// palette", and until now there was no way to ask: the info cache came back empty on a station
/// whose cache is not populated, and enumerating vi.lib gives 19 322 files of which the vast
/// majority are internal implementation VIs that are NOT legal Call targets.
///
/// Why it is scanned at call time rather than shipped as a table: the palette is
/// STATION-SPECIFIC. Installed toolkits and add-ons hook themselves into it - a stock
/// flattenstring.mnu here carries a sub-menu reference to the JSONtext add-on - so a checked-in
/// list would be wrong on the next machine.
///
/// Format, verified by hex-dump against LabVIEW 2026: a .mnu is an "RSRC"/"LMNU" resource file.
/// Item file names are stored as LENGTH-PREFIXED strings (one leading byte, then that many
/// characters). A PTH0 record - 4-byte tag, big-endian uint32 size, big-endian uint32 component
/// count, then length-prefixed components - carries only the palette's BASE DIRECTORY, not the
/// item names, which is why this reads the length-prefixed strings instead of the PTH0 records.
///
/// PRIMITIVES ARE DELIBERATELY EXCLUDED. A palette entry for a built-in function carries only
/// its display label, and that label is not the AIXML node name: the Flatten/Unflatten palette
/// shows "To XML" for the node AIXML calls "Flatten To XML". Listing those would invite exactly
/// the wrong string. Only .vi and .vim entries are reported, and those are usable verbatim.
///
/// ADD-ONS CARRY THEIR OWN PALETTE TREES, and scanning only &lt;LabVIEW&gt;\menus misses them
/// entirely. Drivers now install under %ProgramFiles%\NI\LVAddons\&lt;addon&gt;\&lt;api&gt;\menus, and
/// LabVIEW merges those into the palette at run time. The first version of this class scanned the
/// IDE folder alone and therefore answered a query for "DAQmx" with a single Express-VI stub -
/// while a Call to `DAQmx Read.vi` resolved perfectly well, because NI-DAQmx was installed all
/// along. A silent omission of a whole driver is worse than no index, so both roots are scanned.
///
/// An add-on declares the oldest LabVIEW it supports in lvaddoninfo.json
/// (MinimumSupportedLVVersion, e.g. "22.0" for LabVIEW 2022). One newer than the installed IDE is
/// skipped and REPORTED - never dropped quietly. Bitness-specific variants (nidaqmx, nidaqmx32,
/// nidaqmx64) are all scanned: the name suffix is a convention, not a documented contract, and
/// guessing it wrong would hide exactly the VIs the caller is looking for. Entries are deduplicated
/// by name, so a VI shipped by several variants appears once, labelled with whichever was read
/// first.
/// </summary>
internal static class PaletteIndex
{
    private static readonly object Gate = new();
    private static Dictionary<string, Result> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record Result(
        string LabViewRoot, string MenusFolder, int PaletteFilesScanned,
        IReadOnlyList<PaletteVi> Vis,
        IReadOnlyList<string> AddonsScanned, IReadOnlyList<string> AddonsSkipped);

    /// <summary>
    /// The index for the newest installed LabVIEW, or for <paramref name="labviewRoot"/> when
    /// given. Cached per root for the process lifetime; <paramref name="refresh"/> rescans.
    ///
    /// Add-on palettes are discovered automatically only when <paramref name="labviewRoot"/> is
    /// left to default: a caller pointing at a specific tree - a test with a synthetic menus
    /// folder, say - gets exactly that tree unless it passes <paramref name="addonsRoot"/> too.
    /// </summary>
    public static Result Build(string? labviewRoot = null, bool refresh = false,
                               string? addonsRoot = null)
    {
        var install = labviewRoot is null ? LabViewLocator.Select(LabViewLocator.Discover()) : null;
        var root = labviewRoot ?? (install is null ? null : Path.GetDirectoryName(install.ExePath))
            ?? throw new InvalidOperationException(
                "No LabVIEW installation found, so its palettes cannot be read. Pass installRoot " +
                "explicitly, or check that LabVIEW is installed under a Program Files root.");

        var addons = addonsRoot ?? (labviewRoot is null ? DefaultAddonsRoot() : null);
        var key = root + "|" + (addons ?? "");

        lock (Gate)
        {
            if (!refresh && _cache.TryGetValue(key, out var cached)) return cached;

            var result = Scan(root, addons, install?.Release);
            _cache = new Dictionary<string, Result>(_cache, StringComparer.OrdinalIgnoreCase)
            {
                [key] = result,
            };
            return result;
        }
    }

    private static string? DefaultAddonsRoot() => AddonTree.DefaultRoot();

    /// <summary>
    /// The palette trees to read: every add-on that supports this release. An add-on's palette
    /// lives at &lt;root&gt;\&lt;addon&gt;\&lt;api version&gt;\menus. See <see cref="AddonTree"/>.
    /// </summary>
    private static (List<PaletteSource> Sources, List<string> Skipped) AddonSources(
        string? addonsRoot, int? release)
    {
        var (folders, skipped) = AddonTree.Enumerate(addonsRoot, "menus", release);
        return (folders.Select(f => new PaletteSource(f.Addon, f.Folder)).ToList(), skipped);
    }

    private static Result Scan(string root, string? addonsRoot, int? release)
    {
        var menus = Path.Combine(root, "menus");
        if (!Directory.Exists(menus))
            throw new DirectoryNotFoundException(
                $"'{menus}' does not exist, so this installation exposes no palette files.");

        var sources = new List<PaletteSource> { new("", menus) };
        var (addonSources, skipped) = AddonSources(addonsRoot, release);
        sources.AddRange(addonSources);

        // First palette wins for the reported location; a VI on several palettes is one entry.
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        foreach (var source in sources)
        {
            foreach (var file in Directory.EnumerateFiles(
                         source.MenusFolder, "*.mnu", SearchOption.AllDirectories))
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); }
                catch { continue; }              // a locked or unreadable palette is not fatal

                scanned++;
                var relative = Path.GetRelativePath(source.MenusFolder, file);
                var label = source.Label.Length == 0 ? relative : source.Label + ": " + relative;
                foreach (var name in PascalStrings(bytes))
                    found.TryAdd(name, label);
            }
        }

        var vis = found
            .Select(pair => new PaletteVi(pair.Key, pair.Value))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Result(root, menus, scanned, vis,
                          addonSources.Select(s => s.Label).ToList(), skipped);
    }

    /// <summary>
    /// Every length-prefixed string in the buffer that names a .vi or .vim.
    ///
    /// Reading these as plain text instead would drag the length byte in with the name -
    /// "&amp;My Property Dialog.vi" for a 38-character entry - which looks like a real name and
    /// is not one.
    /// </summary>
    internal static IEnumerable<string> PascalStrings(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            int length = bytes[i];
            if (length < 4 || length > 120 || i + 1 + length > bytes.Length) continue;

            var ok = true;
            for (var j = i + 1; j < i + 1 + length; j++)
                if (bytes[j] < 32 || bytes[j] >= 127) { ok = false; break; }
            if (!ok) continue;

            var text = System.Text.Encoding.ASCII.GetString(bytes, i + 1, length);
            if (!IsViName(text)) continue;

            yield return text;
            i += length;                         // do not rescan the payload as more prefixes
        }
    }

    /// <summary>A bare VI file name: no directory separators, no wildcards.</summary>
    internal static bool IsViName(string text)
    {
        if (!text.EndsWith(".vi", StringComparison.OrdinalIgnoreCase) &&
            !text.EndsWith(".vim", StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.AsSpan().IndexOfAny("\\/:*?\"<>|") >= 0) return false;

        var stem = text[..text.LastIndexOf('.')];
        return stem.Length > 0 && stem.Trim().Length == stem.Length;
    }
}
