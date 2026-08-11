using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Keeps the palette index on disk, so the scan of 582 palette files is paid once per machine
/// instead of once per server process.
///
/// WHAT IT IS WORTH, measured rather than assumed - `LabVIEWMCP --palette` over 582 palette files,
/// three runs each:
///
/// | | scan | served from here |
/// |---|---|---|
/// | | 146, 150, 148 ms | 93, 94, 90 ms |
///
/// So about 55 ms, and most of the 90 ms that remains is process start-up rather than the index.
/// State that plainly because the neighbouring <see cref="ExampleIndexStore"/> earns its cache by a
/// factor of 68 - 55 SECONDS cold against 804 ms - and someone comparing the two files should not
/// conclude the same argument applies here. It does not; this is a small win kept for symmetry and
/// for the cache date the tool can then report.
///
/// THE PRICE IS STALENESS, and it is higher here than for examples. A stale palette either hides a
/// VI that exists, which sends a generator off to rebuild from primitives, or offers one that has
/// been uninstalled, which fails as `Unsupported SubVI` at validation. That is why the guards below
/// are not optional decoration: the file is keyed by the roots it was built from and carries a
/// format version bumped whenever the records change shape, and a cache failing either test is
/// ignored rather than repaired.
///
/// NOT time-expired, deliberately, and the same argument as for the example index: a scan that
/// re-runs on its own schedule reintroduces the delay at an unpredictable moment. Rebuild it when
/// something is installed - `refresh` on the tool, `--palette --refresh` from the shell.
/// </summary>
internal static class PaletteIndexStore
{
    /// <summary>
    /// Bump when <see cref="PaletteIndex.Result"/> or <see cref="PaletteVi"/> changes shape. An
    /// older file then reads as a miss and is rebuilt, rather than deserialising into a record whose
    /// fields have quietly moved.
    /// </summary>
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private sealed record Envelope(
        int Version, string Key, string BuiltUtc, PaletteIndex.Result Index);

    /// <summary>Where the cache lives; see <see cref="CacheDirectory"/>.</summary>
    public static string Directory => CacheDirectory.Root;

    public static string PathFor(string key) =>
        Path.Combine(Directory, $"palette-index-{Fingerprint(key)}.json");

    /// <summary>The stored index for this key, or null when there is none worth trusting.</summary>
    public static PaletteIndex.Result? TryLoad(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Options);
            if (envelope is null || envelope.Version != FormatVersion) return null;

            // The file name is a hash, so collisions are conceivable; the key is stored in full and
            // compared, which makes them harmless rather than merely unlikely.
            return string.Equals(envelope.Key, key, StringComparison.OrdinalIgnoreCase)
                ? envelope.Index
                : null;
        }
        catch
        {
            // A corrupt or unreadable cache must never take the tool down with it: the scan is
            // always available as the answer, and here it costs 150 ms.
            return null;
        }
    }

    /// <summary>Store an index. Failure is reported as false, never thrown.</summary>
    public static bool Save(string key, PaletteIndex.Result index)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var envelope = new Envelope(FormatVersion, key, DateTime.UtcNow.ToString("O"), index);

            // Written beside the target and moved into place, so a reader never sees half a file and
            // a crash mid-write leaves the previous cache intact.
            var path = PathFor(key);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(envelope, Options));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>When this key's cache was written, or null when there is none.</summary>
    public static DateTime? BuiltUtc(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Options);
            return envelope is null || envelope.Version != FormatVersion
                ? null
                : DateTime.Parse(envelope.BuiltUtc).ToUniversalTime();
        }
        catch { return null; }
    }

    private static string Fingerprint(string key) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..12];
}
