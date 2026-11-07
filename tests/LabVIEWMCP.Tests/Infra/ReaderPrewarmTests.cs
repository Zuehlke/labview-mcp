using System.Text.Json;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The background read of the examples into the export cache, and the removal of reader entries
/// that older reader code wrote. The run works on a fake installation (a temp folder passed as the
/// root) holding copies of real NI example VIs, so what is read is what the reader really reads.
/// </summary>
public class ReaderPrewarmTests : IDisposable
{
    private readonly string _tree = Path.Combine(Path.GetTempPath(), "lvai-prewarm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // every cache entry whose sidecar names this test's tree
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(AixmlExportStore.Directory, "*.json"))
                if (File.ReadAllText(sidecar).Contains(_tree.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(Path.ChangeExtension(sidecar, ".xml"));
                    File.Delete(sidecar);
                }
        }
        catch { /* best effort */ }
        try { Directory.Delete(_tree, recursive: true); } catch { /* best effort */ }
    }

    private static void WritePair(string dir, string name, string key)
    {
        File.WriteAllText(Path.Combine(dir, name + ".xml"), "<VI/>");
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(new
        {
            Version = 1, Key = key, ViPath = @"C:\x.vi", CachedUtc = "2026-11-07T00:00:00Z", XmlBytes = 5,
        }));
    }

    /// <summary>
    /// A reader entry of other reader code can never be a hit again (its key carries the old hash);
    /// the cache of 2026-11-07 held 95 reader entries under 11 hashes. NI's entries and the current
    /// reader's stay.
    /// </summary>
    [Fact]
    public void Reader_entries_of_older_reader_code_are_removed_and_nothing_else()
    {
        var dir = Path.Combine(_tree, "cache");
        Directory.CreateDirectory(dir);
        WritePair(dir, "0000000000000001", @"pylabview@OLDSTAMP0001|c:\a.vi|1|2");
        WritePair(dir, "0000000000000002", @"pylabview@CURRENT00001|c:\a.vi|1|2");
        WritePair(dir, "0000000000000003", @"c:\a.vi|1|2");

        Assert.Equal(1, AixmlExportStore.ReapStaleReader("CURRENT00001", dir));

        Assert.False(File.Exists(Path.Combine(dir, "0000000000000001.xml")));
        Assert.False(File.Exists(Path.Combine(dir, "0000000000000001.json")));
        Assert.True(File.Exists(Path.Combine(dir, "0000000000000002.xml")));
        Assert.True(File.Exists(Path.Combine(dir, "0000000000000003.xml")));
    }

    /// <summary>
    /// The coverage pylv_status reports, read from the files with no read of a VI: an NI entry
    /// counts as cached, a VI the current reader failed on as unreadable (not missing), the rest
    /// as missing; a path outside the installations is no target. Cold, partial and warm follow.
    /// </summary>
    [Fact]
    public void Coverage_counts_ni_entries_known_failures_and_missing_targets()
    {
        var install = Path.Combine(_tree, "LabVIEW 2026");
        var examples = Path.Combine(install, "examples");
        Directory.CreateDirectory(examples);
        var targets = new[] { "A.vi", "B.vi", "C.vi" }.Select(n => Path.Combine(examples, n)).ToList();
        foreach (var t in targets) File.WriteAllText(t, "stand-in " + t);
        var outside = Path.Combine(_tree, "elsewhere", "D.vi");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "outside");
        var marker = Path.Combine(_tree, "reader-prewarm.json");
        IReadOnlyList<string> roots = [install];

        var cold = ReaderPrewarm.Measure([.. targets, outside], readerCounts: false, marker, roots);
        Assert.Equal(("cold", 3, 0, 3), (cold.State, cold.Targets, cold.Cached, cold.Missing));

        var niExport = Path.Combine(_tree, "ni.xml");
        File.WriteAllText(niExport, "<VI _name=\"A.vi\"/>");
        Assert.True(AixmlExportStore.Save(targets[0], niExport, roots));

        var partial = ReaderPrewarm.Measure(targets, readerCounts: false, marker, roots);
        Assert.Equal(("partial", 1, 1, 0, 2, 33), (partial.State, partial.Cached, partial.FromNi,
                                                   partial.FromReader, partial.Missing, partial.Percent));

        // a failure recorded by the CURRENT reader code is not missing - a run would not read it
        if (AixmlExportStore.ReaderStamp() is { } stamp)
        {
            File.WriteAllText(marker, JsonSerializer.Serialize(
                new ReaderPrewarm.Marker(1, stamp, [targets[1]], "2026-11-07T00:00:00Z")));
            var known = ReaderPrewarm.Measure(targets, readerCounts: true, marker, roots);
            Assert.Equal((1, 1, 66), (known.Unreadable, known.Missing, known.Percent));

            // ... but one recorded by other reader code is: the new reader may read it
            File.WriteAllText(marker, JsonSerializer.Serialize(
                new ReaderPrewarm.Marker(1, "OTHERSTAMP", [targets[1]], null)));
            Assert.Equal(2, ReaderPrewarm.Measure(targets, readerCounts: true, marker, roots).Missing);
        }

        foreach (var t in targets.Skip(1)) Assert.True(AixmlExportStore.Save(t, niExport, roots));
        var warm = ReaderPrewarm.Measure(targets, readerCounts: false, marker, roots);
        Assert.Equal(("warm", 3, 0, 100), (warm.State, warm.Cached, warm.Missing, warm.Percent));
    }

    private static string? Example(string relative) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine(d, "examples", relative)).FirstOrDefault(File.Exists)
            : null;

    /// <summary>
    /// Two readable VIs are read and stored under the reader's key, a VI with NI's entry is left to
    /// it, and a VI the reader fails on is recorded - and not read again by the next run under the
    /// same reader code, which finds the other three cached.
    /// </summary>
    [Fact]
    public async Task A_run_reads_what_is_missing_and_does_not_retry_a_failure()
    {
        if (AixmlComposer.Locate(out _) is not { } setup || AixmlExportStore.ReaderStamp() is not { } stamp) return;
        var sources = new[] { @"Numerics\Numeric Functions.vi", @"Strings\Concatenate Strings.vi", @"Numerics\Fixed-Point Cast.vi" }
            .Select(Example).ToList();
        if (sources.Any(s => s is null)) return;

        var install = Path.Combine(_tree, "LabVIEW 2026");
        var examples = Path.Combine(install, "examples");
        Directory.CreateDirectory(examples);
        var targets = new List<string>();
        foreach (var s in sources)
        {
            var to = Path.Combine(examples, Path.GetFileName(s!));
            File.Copy(s!, to);
            targets.Add(to);
        }
        var broken = Path.Combine(examples, "Broken.vi");
        File.WriteAllText(broken, "not a VI at all");
        targets.Add(broken);

        // the third VI has NI's entry: the pre-warm leaves it to that one
        var niExport = Path.Combine(_tree, "ni.xml");
        File.WriteAllText(niExport, "<VI _name=\"Fixed-Point Cast.vi\"/>");
        Assert.True(AixmlExportStore.Save(targets[2], niExport, [install]));

        var marker = Path.Combine(_tree, "reader-prewarm.json");
        var first = await ReaderPrewarm.RunCoreAsync(setup, stamp, targets, marker, [install], 0, default);

        Assert.Equal("finished", first.State);
        Assert.Equal(4, first.Done);
        Assert.Equal(2, first.Read);
        Assert.Equal(1, first.AlreadyCached);
        Assert.Equal(1, first.Failed);
        Assert.NotNull(AixmlExportStore.CachedUtc(targets[0], AixmlBackend.PyLabview));
        Assert.NotNull(AixmlExportStore.CachedUtc(targets[1], AixmlBackend.PyLabview));
        Assert.Null(AixmlExportStore.CachedUtc(targets[2], AixmlBackend.PyLabview));
        var saved = ReaderPrewarm.LoadMarker(marker)!;
        Assert.Equal(stamp, saved.Stamp);
        Assert.Equal(new[] { broken }, saved.Failed);
        Assert.NotNull(saved.FinishedUtc);

        var second = await ReaderPrewarm.RunCoreAsync(setup, stamp, targets, marker, [install], 0, default);
        Assert.Equal(0, second.Read);
        Assert.Equal(3, second.AlreadyCached);
        Assert.Equal(1, second.Failed);                     // listed once, not read and added again
        Assert.Equal(new[] { broken }, ReaderPrewarm.LoadMarker(marker)!.Failed);

        // a marker of other reader code is no reason to skip: the new reader may read it
        var other = await ReaderPrewarm.RunCoreAsync(setup, "OTHERSTAMP", [broken], marker, [install], 0, default);
        Assert.Equal(1, other.Failed);
        Assert.Equal("OTHERSTAMP", ReaderPrewarm.LoadMarker(marker)!.Stamp);
    }
}
