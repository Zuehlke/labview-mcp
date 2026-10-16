using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A VI's controls and indicators as the pylabview reader listed them, kept per FILE CONTENT.
///
/// The ActiveX run route asks the reader for a VI's panel before every run, because ActiveX can set
/// and get a control by name but cannot list them. For a helper that never changes that was the
/// whole cost of a call: measured 2026-10-16 on lvai_layout_diagram, the reader took 0.73 s for the
/// dump helper and 1.30 s for the apply helper on every VI, against 0.09 and 0.10 s inside LabVIEW -
/// about 2 s of a small VI's 2.3.
///
/// THE KEY IS WHAT THE READER READS: the file's bytes and the reader's own stamp
/// (<see cref="AixmlExportStore.ReaderStamp"/>, which covers the bundled pylabview too). The first
/// version keyed on size and write time, which a copy that keeps an older timestamp can defeat;
/// hashing the bytes costs well under a millisecond for a helper and a few for a large VI. What is
/// cached is only what the reader reads from those bytes, so a hit is as current as reading again.
///
/// Kept in memory for the process and on disk under the cache root, so a server restart keeps it.
/// On disk an entry is named after the VI's PATH and its content, and writing one removes the
/// entries the same path had before: a VI saved a hundred times leaves one file, not a hundred.
/// </summary>
internal static class PanelCache
{
    private static readonly ConcurrentDictionary<string, List<AixmlComposer.PanelItem>> Memory = new();

    public static string Folder => Path.Combine(CacheDirectory.Root, "panels");

    /// <summary>For a test that reads an entry back from disk, as a new process would.</summary>
    internal static void ForgetMemory() => Memory.Clear();

    private sealed record Entry(string Name, string Type, bool Indicator);

    /// <summary>
    /// The entry's name: the path's hash, a dash, the content's hash. Null when there is nothing
    /// to key - no reader stamp, or no file to read.
    /// </summary>
    internal static (string PathPart, string Name)? Key(string viPath, string? readerStamp)
    {
        if (readerStamp is null) return null;
        var full = Path.GetFullPath(viPath);
        byte[] content;
        try { content = File.ReadAllBytes(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var pathPart = Hex(Encoding.UTF8.GetBytes(full.ToUpperInvariant()))[..16];
        var statePart = Hex([.. SHA256.HashData(content), .. Encoding.UTF8.GetBytes("|" + readerStamp)])[..32];
        return (pathPart, pathPart + "-" + statePart);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static List<AixmlComposer.PanelItem>? TryGet(string viPath, string? readerStamp, string? folder = null)
    {
        if (Key(viPath, readerStamp) is not { } key) return null;
        if (Memory.TryGetValue(key.Name, out var hit)) return hit;
        try
        {
            var file = Path.Combine(folder ?? Folder, key.Name + ".json");
            if (!File.Exists(file)) return null;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file));
            if (entries is null) return null;
            var items = entries.Select(e => new AixmlComposer.PanelItem(e.Name, e.Type, e.Indicator)).ToList();
            Memory[key.Name] = items;
            return items;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;   // a damaged entry is a miss; the reader answers instead
        }
    }

    public static void Put(string viPath, string? readerStamp, List<AixmlComposer.PanelItem> items, string? folder = null)
    {
        if (Key(viPath, readerStamp) is not { } key) return;
        Memory[key.Name] = items;
        try
        {
            var dir = folder ?? Folder;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, key.Name + ".json");
            var tmp = Path.Combine(dir, key.Name + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.Select(i => new Entry(i.Name, i.Type, i.Indicator))));
            File.Move(tmp, target, overwrite: true);
            // what this path held before is no longer its content
            foreach (var old in Directory.GetFiles(dir, key.PathPart + "-*.json"))
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                    File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the memory entry stands; a disk that refuses only costs the next process a read
        }
    }
}
