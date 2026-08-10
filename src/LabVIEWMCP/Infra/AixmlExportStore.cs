using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Keeps AIXML exports of INSTALLATION VIs on disk, so reading the same example or palette VI a
/// second time costs a file copy instead of a LabVIEW round trip.
///
/// MEASURED over the whole examples tree of LabVIEW 2026 - 1677 VIs exported and validated, one
/// RPC pair each (`--corpus`, results in roundtrip.tsv):
///
/// | | export + validate | export size |
/// |---|---|---|
/// | median | 331 ms | 4.1 kB |
/// | p90 | 3.3 s | 14.6 kB |
/// | p99 | 24 s | 161 kB |
/// | max | 93 s | 5.8 MB |
/// | total | 52 min | 28.8 MB |
///
/// THE COST IS NOT THE SERIALISATION, IT IS LOADING THE VI. Correlation between export size and
/// export duration over those 1677 rows is r = 0.002 - none. The 5.8 MB export finished in 1.9 s;
/// the slowest VI took 93 s and produced 20 kB. What the slow ones have in common is dependencies
/// LabVIEW has to pull in first: DQMH libraries, malleable VIs, 3D graph controls. That is why the
/// cache is on DISK rather than in the process: it removes the VI load, which no amount of memory
/// in this process can, and it removes it across server restarts, which is when it hurts.
///
/// WHY NOT PRE-WARM THE WHOLE CORPUS. 52 minutes of LabVIEW time, and the sweep that measured it
/// had to recycle LabVIEW along the way - about 130 handles leaked per VI, 116 000 handles and
/// 1.3 GB after 900 of them, and one VI killed the process outright. An agent reads a handful of
/// examples per session. So entries are written as they are asked for, never in advance.
///
/// ONLY INSTALLATION VIs ARE CACHED, and that boundary is the whole safety argument. An export
/// depends not just on the VI but on its subVIs, and the key below cannot see them: a subVI edited
/// in place would leave a stale entry behind a VI whose own timestamp never moved. Under a LabVIEW
/// installation or LVAddons that does not happen - those trees change when something is installed
/// or upgraded, which rewrites the files and moves the timestamps, and an upgrade lands in a new
/// versioned directory anyway. User code is the opposite: it changes constantly, one subVI at a
/// time. It is therefore never cached, and the tool says so in its answer rather than leaving the
/// caller to wonder.
///
/// A HIT NEEDS NO RUNNING LabVIEW. The lookup is two file reads, so an example's diagram stays
/// readable on a machine where LabVIEW is closed or still starting.
/// </summary>
internal static class AixmlExportStore
{
    /// <summary>
    /// Bump when the sidecar changes shape or the key is computed differently. Older entries then
    /// read as a miss and are re-exported, rather than being trusted on a rule that has moved.
    /// </summary>
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <param name="Version">Format of this record; see <see cref="FormatVersion"/>.</param>
    /// <param name="Key">The full key, stored so a hash collision is harmless rather than wrong.</param>
    /// <param name="ViPath">The source VI, for anyone reading the cache directory by hand.</param>
    /// <param name="CachedUtc">When the export was taken.</param>
    /// <param name="XmlBytes">Size of the payload, re-checked on load so a truncated file misses.</param>
    private sealed record Sidecar(
        int Version, string Key, string ViPath, string CachedUtc, long XmlBytes);

    /// <summary>
    /// Where entries live: a subfolder of the shared cache directory, so exports and the example
    /// index stay apart - one is a handful of files, the other grows with every VI ever read. See
    /// <see cref="CacheDirectory"/> for the root and how to move it.
    /// </summary>
    public static string Directory => Path.Combine(CacheDirectory.Root, "aixml");

    /// <summary>The payload for a key. The sidecar sits beside it under <c>.json</c>.</summary>
    public static string PathFor(string key) =>
        Path.Combine(Directory, $"{Fingerprint(key)}.xml");

    private static string SidecarFor(string key) => Path.ChangeExtension(PathFor(key), ".json");

    /// <summary>
    /// What identifies one export: the VI's normalised path, its last-write time and its size.
    /// Null when the VI cannot be read at all - there is then nothing to key on, and every
    /// operation below turns into a miss.
    ///
    /// The path is normalised through <see cref="Path.GetFullPath"/> first. LabVIEW itself rejects
    /// forward slashes (`Error 7`), but .NET accepts them and a shell hands them over without
    /// comment, so the same VI can arrive spelled two ways; without normalising, each spelling
    /// would get its own entry and neither would ever hit the other's.
    /// </summary>
    internal static string? KeyFor(string viPath)
    {
        try
        {
            var info = new FileInfo(Path.GetFullPath(viPath));
            if (!info.Exists) return null;

            return string.Join('|',
                info.FullName.ToLowerInvariant(),
                info.LastWriteTimeUtc.Ticks,
                info.Length);
        }
        catch { return null; }
    }

    /// <summary>
    /// May this VI's export be cached? True only under a LabVIEW installation or under LVAddons -
    /// see the class remarks for why that boundary is the safety argument rather than a detail.
    /// </summary>
    /// <param name="roots">
    /// The trees to treat as installation territory. Left null the real machine's installations are
    /// discovered; a test passes its own so it is not measuring whatever LabVIEW happens to be here.
    /// </param>
    public static bool IsCacheable(string viPath, IReadOnlyList<string>? roots = null)
    {
        try
        {
            var full = Path.GetFullPath(viPath);
            return (roots ?? InstallationRoots()).Any(root => IsUnder(full, root));
        }
        catch { return false; }
    }

    /// <summary>
    /// Copy a cached export to <paramref name="destination"/>. False means no usable entry, and
    /// the caller must go and ask LabVIEW.
    ///
    /// The destination file is written because callers depend on it existing: the path is passed
    /// on to ValidateAIXML, to other tools and to the caller's own reader. A hit that only returned
    /// the text would break every one of them.
    /// </summary>
    public static bool TryCopyTo(string viPath, string destination,
                                 IReadOnlyList<string>? roots = null)
    {
        try
        {
            if (!IsCacheable(viPath, roots)) return false;
            if (KeyFor(viPath) is not { } key) return false;
            if (ReadSidecar(key) is not { } sidecar) return false;

            var payload = PathFor(key);
            if (!File.Exists(payload)) return false;

            // A payload that is not the size the sidecar recorded was written by something other
            // than a completed Save - a crash mid-copy, a full disk. Re-export rather than serve it.
            if (new FileInfo(payload).Length != sidecar.XmlBytes) return false;

            var full = Path.GetFullPath(destination);
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);

            File.Copy(payload, full, overwrite: true);
            return true;
        }
        catch
        {
            // An unreadable cache must never take the tool down with it: asking LabVIEW is always
            // available as the answer.
            return false;
        }
    }

    /// <summary>
    /// Store the export at <paramref name="xmlPath"/> as this VI's entry. Returns false when the VI
    /// is not cacheable or the write failed - never throws, because failing to cache is not a
    /// reason to fail the export that already succeeded.
    /// </summary>
    public static bool Save(string viPath, string xmlPath, IReadOnlyList<string>? roots = null)
    {
        try
        {
            if (!IsCacheable(viPath, roots)) return false;
            if (KeyFor(viPath) is not { } key) return false;
            if (!File.Exists(xmlPath)) return false;

            System.IO.Directory.CreateDirectory(Directory);

            // Payload first, sidecar last: the sidecar is the commit record, so a crash between the
            // two leaves an orphan payload that reads as a miss rather than an entry that lies.
            // Each half is written beside its target and moved into place, so no reader ever sees
            // half a file.
            //
            // The intermediate name carries a GUID because the .tmp is NOT private to one writer
            // otherwise. Three MCP server processes run at once here - one per client connection -
            // and two of them exporting the same VI would have raced on a fixed `<hash>.xml.tmp`:
            // one moves it away, the other's Move throws onto a file that is no longer there.
            // Observed as exactly that kind of debris in a real cache directory - a stray
            // `<hash>.json.tmp` and a sidecar whose payload was missing, both stamped the same
            // second. Per-write names make the collision impossible rather than unlikely.
            var stamp = Guid.NewGuid().ToString("N")[..8];
            var payload = PathFor(key);
            var payloadTemp = $"{payload}.{stamp}.tmp";
            File.Copy(xmlPath, payloadTemp, overwrite: true);
            File.Move(payloadTemp, payload, overwrite: true);

            var sidecar = new Sidecar(FormatVersion, key, Path.GetFullPath(viPath),
                DateTime.UtcNow.ToString("O"), new FileInfo(payload).Length);

            var record = SidecarFor(key);
            var recordTemp = $"{record}.{stamp}.tmp";
            File.WriteAllText(recordTemp, JsonSerializer.Serialize(sidecar, Options));
            File.Move(recordTemp, record, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>When this VI's entry was taken, or null when there is none worth trusting.</summary>
    public static DateTime? CachedUtc(string viPath)
    {
        if (KeyFor(viPath) is not { } key) return null;
        if (ReadSidecar(key) is not { } sidecar) return null;

        return DateTime.TryParse(sidecar.CachedUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var when)
            ? when.ToUniversalTime()
            : null;
    }

    /// <summary>
    /// Remove debris: intermediate <c>.tmp</c> files a killed or racing writer left behind, and
    /// halves of an entry whose other half is missing. Returns how many files went.
    ///
    /// Nothing here is required for correctness - <see cref="TryCopyTo"/> already treats a sidecar
    /// without a payload as a miss, which is why the debris was harmless rather than wrong. It is
    /// required for the directory not to grow rubbish forever, and that is worth a sweep at
    /// start-up: an operator who looks in the cache should see entries, not litter, or the next
    /// person to look will suspect the cache of failing when it is working.
    /// </summary>
    /// <param name="directory">
    /// The directory to sweep; null means <see cref="Directory"/>. A test passes its own, and that
    /// is not a convenience: this is the only operation here that DELETES, and the cache location is
    /// one process-wide setting shared by every test class. Sweeping the shared directory made a
    /// reap in one class delete a half-written entry another class was still assembling - a failure
    /// in roughly one run in ten, never the same test twice.
    /// </param>
    public static int Reap(string? directory = null)
    {
        try
        {
            var target = directory ?? Directory;
            if (!System.IO.Directory.Exists(target)) return 0;

            var removed = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(target).ToList())
            {
                var remove = file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

                // A pair is <hash>.xml plus <hash>.json. Either alone is dead weight: an orphan
                // payload will never be found (the sidecar is the commit record) and an orphan
                // sidecar always reads as a miss.
                if (!remove && file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    remove = !File.Exists(Path.ChangeExtension(file, ".json"));
                if (!remove && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    remove = !File.Exists(Path.ChangeExtension(file, ".xml"));

                if (!remove) continue;
                try { File.Delete(file); removed++; }
                catch { /* one stuck file must not abort the sweep */ }
            }
            return removed;
        }
        catch { return 0; }
    }

    /// <summary>Entries currently held, and what they occupy. For reporting, never for a decision.</summary>
    public static (int Entries, long Bytes) Contents()
    {
        try
        {
            var files = System.IO.Directory.EnumerateFiles(Directory, "*.xml").ToList();
            return (files.Count, files.Sum(f => new FileInfo(f).Length));
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// The sidecar for this key, or null when there is none that can mean what it says. The file
    /// name is only a hash, so the key is stored in full and compared - which makes a collision
    /// harmless rather than merely unlikely.
    /// </summary>
    private static Sidecar? ReadSidecar(string key)
    {
        try
        {
            var record = SidecarFor(key);
            if (!File.Exists(record)) return null;

            var sidecar = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(record), Options);
            if (sidecar is null || sidecar.Version != FormatVersion) return null;

            return string.Equals(sidecar.Key, key, StringComparison.OrdinalIgnoreCase)
                ? sidecar
                : null;
        }
        catch { return null; }
    }

    private static IReadOnlyList<string>? _roots;

    /// <summary>
    /// Every LabVIEW installation directory on this machine, plus the LVAddons root. Computed once:
    /// it enumerates the filesystem, and installations do not appear while the server runs.
    /// </summary>
    internal static IReadOnlyList<string> InstallationRoots()
    {
        if (_roots is not null) return _roots;

        var roots = new List<string>();
        try
        {
            foreach (var install in LabViewLocator.Discover())
                if (Path.GetDirectoryName(install.ExePath) is { } folder)
                    roots.Add(folder);
        }
        catch { /* no installation readable - nothing is cacheable, which is safe */ }

        if (AddonTree.DefaultRoot() is { } addons) roots.Add(addons);
        return _roots = roots;
    }

    /// <summary>
    /// Is <paramref name="path"/> inside <paramref name="root"/>? The separator check is what keeps
    /// `C:\LabVIEW 2026-backup` from counting as inside `C:\LabVIEW 2026`.
    /// </summary>
    internal static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        return full.Length > trimmed.Length
            && full.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
            && (full[trimmed.Length] == Path.DirectorySeparatorChar ||
                full[trimmed.Length] == Path.AltDirectorySeparatorChar);
    }

    private static string Fingerprint(string key) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..16];
}
