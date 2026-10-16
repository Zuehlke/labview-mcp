using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A VI's controls and indicators as the pylabview reader listed them, kept per FILE STATE.
///
/// The ActiveX run route asks the reader for a VI's panel before every run, because ActiveX can set
/// and get a control by name but cannot list them. For a helper that never changes that was the
/// whole cost of a call: measured 2026-10-16 on lvai_layout_diagram, the reader took 0.73 s for the
/// dump helper and 1.30 s for the apply helper on every VI, against 0.09 and 0.10 s inside LabVIEW -
/// about 2 s of a small VI's 2.3.
///
/// The key is the file's full path, size and write time, and the reader's own code stamp: a VI
/// saved again is a miss, and so is every entry once the reader changes. What is cached is what
/// the reader reads from that file and nothing else, so this is as current as reading it again.
/// Kept in memory for the process and on disk under the cache root, so a server restart keeps it.
/// </summary>
internal static class PanelCache
{
    private static readonly ConcurrentDictionary<string, List<AixmlComposer.PanelItem>> Memory = new();

    public static string Folder => Path.Combine(CacheDirectory.Root, "panels");

    /// <summary>For a test that reads an entry back from disk, as a new process would.</summary>
    internal static void ForgetMemory() => Memory.Clear();

    private sealed record Entry(string Name, string Type, bool Indicator);

    /// <summary>The cache key for the file as it is now, or null when there is none to make.</summary>
    internal static string? Key(string viPath, string? readerStamp)
    {
        if (readerStamp is null) return null;
        var file = new FileInfo(Path.GetFullPath(viPath));
        if (!file.Exists) return null;
        var text = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{readerStamp}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    public static List<AixmlComposer.PanelItem>? TryGet(string viPath, string? readerStamp, string? folder = null)
    {
        if (Key(viPath, readerStamp) is not { } key) return null;
        if (Memory.TryGetValue(key, out var hit)) return hit;
        try
        {
            var file = Path.Combine(folder ?? Folder, key + ".json");
            if (!File.Exists(file)) return null;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file));
            if (entries is null) return null;
            var items = entries.Select(e => new AixmlComposer.PanelItem(e.Name, e.Type, e.Indicator)).ToList();
            Memory[key] = items;
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
        Memory[key] = items;
        try
        {
            var dir = folder ?? Folder;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, key + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.Select(i => new Entry(i.Name, i.Type, i.Indicator))));
            File.Move(tmp, Path.Combine(dir, key + ".json"), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the memory entry stands; a disk that refuses only costs the next process a read
        }
    }
}
