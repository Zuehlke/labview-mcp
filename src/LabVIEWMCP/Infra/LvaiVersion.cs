using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Which build of NI's AI add-on is installed, and dropping the export cache when it changes.
///
/// WHY THIS EXISTS. <see cref="AixmlExportStore"/> keys an entry on the source VI - path, last-write
/// time, size - and argues that installation trees only change when something is installed, which
/// rewrites the files. That argument covers the VI. It does not cover the GENERATOR: NI's add-on is
/// what turns a VI into AIXML, and an upgrade can change how it does that while every source VI on
/// disk stays byte-identical. The per-VI key is then unchanged and the cache happily serves exports
/// produced by the previous generator. That is the silent-wrongness failure this repository spends
/// most of its effort avoiding, so the add-on's identity has to be part of what the cache is keyed
/// to - and the cheapest correct form of that is to drop the cache when it moves.
///
/// WHAT IS ACTUALLY AVAILABLE. There is no version anywhere in the gRPC interface: the proto has no
/// version field and <c>GetApplicationConfiguration</c> returns nothing but <c>language</c>. What
/// exists is on disk. Each add-on API version installs into its own folder under
/// <c>%ProgramFiles%\NI\LVAddons\lvai\&lt;api&gt;</c> with an <c>lvaddoninfo.json</c> carrying an
/// <c>ApiVersion</c>, and MEASURED on this station three of them coexist - <c>1</c> (v1),
/// <c>26.1</c> (v26.1) and <c>26.3</c> (v26.3), whose <c>LV AI Core.lvlibp</c> are 813 kB, 1.39 MB
/// and 1.89 MB. So the fingerprint below is every installed version plus the size and timestamp of
/// its two service binaries.
///
/// WHY NOT HASH THE REFLECTED SCHEMA. It was the obvious alternative and it is worse here on two
/// counts. It needs a running LabVIEW, and this check runs at start-up where the standing rule is
/// that a machine with no LabVIEW must still serve every other tool. And it only sees the
/// INTERFACE: an add-on that changes how it emits AIXML without touching a single rpc or field
/// leaves the schema hash identical, which is exactly the case that makes a cached export wrong.
/// The binaries move in that case; the schema does not.
///
/// WHAT IT DOES NOT CATCH, stated so nobody reads more into it: a change that touches neither the
/// add-on files nor a source VI. A LabVIEW patch that alters the generator without reinstalling the
/// add-on would pass unnoticed. `refresh` on the tool remains the manual override, and after any
/// LabVIEW upgrade it is still the right thing to run.
/// </summary>
internal static class LvaiVersion
{
    /// <summary>Bump when <see cref="Compute"/> changes what it puts in the string.</summary>
    private const int FormatVersion = 1;

    private sealed record Record(int Version, string Fingerprint, string SeenUtc);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Beside the caches it guards, not inside either of them.</summary>
    public static string PathFor() => Path.Combine(ExampleIndexStore.Directory, "lvai-version.json");

    /// <summary>
    /// The add-on root, <c>%ProgramFiles%\NI\LVAddons\lvai</c>, or null when there is none. Derived
    /// from <see cref="AddonTree.DefaultRoot"/> so both agree on where LVAddons is.
    /// </summary>
    public static string? DefaultRoot() =>
        AddonTree.DefaultRoot() is { } addons ? Path.Combine(addons, "lvai") : null;

    /// <summary>
    /// A stable description of every installed add-on version, or null when the root is absent or
    /// unreadable. Null is deliberately NOT the same as "nothing installed": a fingerprint we could
    /// not take must not be recorded as one, or the next start-up would compare against a fiction.
    /// </summary>
    public static string? Compute(string? lvaiRoot = null)
    {
        var root = lvaiRoot ?? DefaultRoot();
        if (root is null || !Directory.Exists(root)) return null;

        try
        {
            var parts = new List<string>();
            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var api = ApiVersion(directory) ?? "?";
                parts.Add($"{Path.GetFileName(directory)}={api}");

                // Size AND timestamp: a rebuild that happens to produce the same length still moves
                // the timestamp, and a restore that preserves timestamps still changes the length.
                foreach (var binary in Directory
                             .EnumerateFiles(directory, "LV AI*.lvlibp", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(binary);
                    parts.Add($"{Path.GetRelativePath(root, binary).ToLowerInvariant()}" +
                              $":{info.Length}:{info.LastWriteTimeUtc.Ticks}");
                }
            }

            // No versions at all is a real answer - the add-on root exists but is empty - and must
            // be distinguishable from "could not look", which is null above.
            return parts.Count == 0
                ? "none"
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.Join('|', parts))))[..24];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The <c>ApiVersion</c> from an add-on version folder's lvaddoninfo.json.</summary>
    internal static string? ApiVersion(string versionDirectory)
    {
        try
        {
            var info = Path.Combine(versionDirectory, "lvaddoninfo.json");
            if (!File.Exists(info)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(info));
            return document.RootElement.TryGetProperty("ApiVersion", out var value)
                ? value.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>The fingerprint recorded at the last start-up, or null when none was.</summary>
    public static string? Recorded()
    {
        try
        {
            var path = PathFor();
            if (!File.Exists(path)) return null;

            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Options);
            return record is null || record.Version != FormatVersion ? null : record.Fingerprint;
        }
        catch { return null; }
    }

    /// <summary>What <see cref="Check"/> decided, so the caller can log or report it.</summary>
    /// <param name="Current">The fingerprint now, or null when it could not be taken.</param>
    /// <param name="Previous">What was recorded before, or null when nothing was.</param>
    /// <param name="Changed">True when the two differ and the cache was therefore dropped.</param>
    /// <param name="EntriesDropped">Export cache files removed. Zero when nothing was dropped.</param>
    internal readonly record struct Verdict(
        string? Current, string? Previous, bool Changed, int EntriesDropped)
    {
        public string Describe() => Changed
            ? $"LabVIEW AI add-on changed ({Previous ?? "not recorded"} -> {Current}); " +
              $"dropped {EntriesDropped} cached AIXML export(s). The example index was NOT " +
              "rebuilt - if an add-on was installed it may list new examples, so run " +
              "`LabVIEWMCP --examples --refresh` once."
            : Current is null
                ? "LabVIEW AI add-on not found under LVAddons; export cache left as it is."
                : $"LabVIEW AI add-on unchanged ({Current}).";
    }

    /// <summary>
    /// Compare the add-on's identity against what was recorded, and drop the AIXML export cache when
    /// it has moved. Never throws: a version check that fails must not stop the server starting.
    ///
    /// A MISSING record counts as changed. The entries were produced by an add-on we cannot name, so
    /// vouching for them would be a guess; re-exporting costs a median 331 ms per VI and only for
    /// VIs actually read again, which is the cheaper of the two mistakes by a wide margin.
    ///
    /// The example index is deliberately left alone. It is built by reading files rather than through
    /// the add-on, and <see cref="ExampleIndexStore"/> makes the case for never expiring it on its
    /// own - a 55-second rescan at an unpredictable moment is the problem it exists to remove. The
    /// verdict says so instead.
    /// </summary>
    public static Verdict Check(string? lvaiRoot = null, bool dropCache = true)
    {
        var current = Compute(lvaiRoot);
        var previous = Recorded();

        // Nothing to compare against, and nothing worth recording: leave the cache and say so.
        if (current is null) return new Verdict(null, previous, false, 0);
        if (current == previous) return new Verdict(current, previous, false, 0);

        var dropped = dropCache ? DropExportCache() : 0;
        Remember(current);
        return new Verdict(current, previous, true, dropped);
    }

    /// <summary>Every file in the export cache directory. Returns how many payloads went.</summary>
    private static int DropExportCache()
    {
        try
        {
            var directory = AixmlExportStore.Directory;
            if (!Directory.Exists(directory)) return 0;

            var payloads = Directory.EnumerateFiles(directory, "*.xml").Count();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                try { File.Delete(file); } catch { /* one stuck file must not abort the sweep */ }
            }
            return payloads;
        }
        catch { return 0; }
    }

    private static void Remember(string fingerprint)
    {
        try
        {
            Directory.CreateDirectory(ExampleIndexStore.Directory);
            var path = PathFor();
            var record = new Record(FormatVersion, fingerprint, DateTime.UtcNow.ToString("O"));
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch { /* an unrecordable check repeats next start-up; that is harmless */ }
    }
}
