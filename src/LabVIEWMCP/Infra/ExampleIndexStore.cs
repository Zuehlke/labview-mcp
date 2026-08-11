using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Keeps the example index on disk, so the 55-second scan is paid once per machine instead of once
/// per server process.
///
/// MEASURED, three runs from a fresh process: 55 000 ms, then 804 ms, then 819 ms. Nothing was
/// cached between processes - the in-memory index lives for the lifetime of the server that built
/// it - so the difference is first-touch I/O over 2510 files, which on a Windows machine with
/// on-access scanning is slow the first time and free afterwards. The consequence was that every
/// restart of the MCP server reintroduced a 55-second first call, which reads as a hang: long
/// enough that somebody starts debugging a connection that is fine.
///
/// The cache is deliberately NOT time-expired. It is rebuilt when asked and not otherwise, because
/// a scan that re-runs on its own schedule brings the 55 seconds back at an unpredictable moment,
/// which is the whole problem. After installing or upgrading LabVIEW or an add-on, ask for the
/// rebuild - `refresh` on the tool, `--examples --refresh` from the shell.
///
/// Two things guard against reading a cache that cannot mean what it says: the file is keyed by
/// the roots it was built from, and it carries a format version that is bumped whenever the
/// records below change shape. A cache that fails either test is ignored, not repaired - a wrong
/// index is worse than a slow one.
/// </summary>
internal static class ExampleIndexStore
{
    /// <summary>
    /// Bump when <see cref="ExampleIndex.Result"/> or <see cref="ExampleVi"/> changes shape. An
    /// older file then reads as a miss and is rebuilt, rather than deserialising into a record
    /// whose fields have quietly moved.
    /// </summary>
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private sealed record Envelope(int Version, string Key, string BuiltUtc, ExampleIndex.Result Index);

    /// <summary>Where the cache lives; see <see cref="CacheDirectory"/>.</summary>
    public static string Directory => CacheDirectory.Root;

    public static string PathFor(string key) =>
        Path.Combine(Directory, $"example-index-{Fingerprint(key)}.json");

    /// <summary>The stored index for this key, or null when there is none worth trusting.</summary>
    public static ExampleIndex.Result? TryLoad(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Options);
            if (envelope is null || envelope.Version != FormatVersion) return null;

            // The file name is a hash, so collisions are conceivable; the key is stored in full
            // and compared, which makes them harmless rather than merely unlikely.
            return string.Equals(envelope.Key, key, StringComparison.OrdinalIgnoreCase)
                ? envelope.Index
                : null;
        }
        catch
        {
            // A corrupt or unreadable cache must never take the tool down with it: the scan is
            // always available as the answer.
            return null;
        }
    }

    /// <summary>Store an index. Failure is reported as false, never thrown.</summary>
    public static bool Save(string key, ExampleIndex.Result index)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var envelope = new Envelope(FormatVersion, key,
                DateTime.UtcNow.ToString("O"), index);

            // Written beside the target and moved into place, so a reader never sees half a file
            // and a crash mid-write leaves the previous cache intact.
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
