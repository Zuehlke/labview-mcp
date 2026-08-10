using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The on-disk cache of AIXML exports. What it protects is a measured median of 331 ms and a
/// worst case of 93 s per export, paid again on every server restart because nothing survived the
/// process.
///
/// The tests exercise the shipping code path - temp file, move into place, sidecar last - rather
/// than a test-only seam that could pass while the real one fails, under keys derived from a temp
/// tree nobody else uses. The cache directory itself is redirected for the whole suite by
/// <c>CacheRedirect</c>, so this no longer writes into the developer's own cache; it used to, and
/// left one file per run behind forever. The installation roots are passed in explicitly, which is
/// the same parameter the tool uses, so nothing here depends on whether this machine has LabVIEW.
/// </summary>
public class AixmlExportStoreTests : IDisposable
{
    private readonly string _tree;
    private readonly string _viPath;
    private readonly string _exportPath;
    private readonly IReadOnlyList<string> _roots;

    public AixmlExportStoreTests()
    {
        _tree = Path.Combine(Path.GetTempPath(), "lvai-export-tests", Guid.NewGuid().ToString("N"));

        // Shaped like the real thing: an installation root with an examples tree under it.
        var install = Path.Combine(_tree, "LabVIEW 2026");
        var examples = Path.Combine(install, "examples", "File IO");
        Directory.CreateDirectory(examples);

        _viPath = Path.Combine(examples, "Scale TDMS Data.vi");
        File.WriteAllText(_viPath, "PRETEND-VI-BYTES");

        _exportPath = Path.Combine(_tree, "export.xml");
        File.WriteAllText(_exportPath, Xml);

        _roots = [install];
    }

    private const string Xml =
        """<VI _name="Scale TDMS Data.vi"><Node _name="Add" uid="143"/></VI>""";

    private string Destination(string name = "out.xml") => Path.Combine(_tree, "dest", name);

    /// <summary>Payloads written through <see cref="SaveEntry"/>, so Dispose can remove them.</summary>
    private readonly List<string> _payloads = [];

    /// <summary>
    /// Save this test's export and remember where it landed.
    ///
    /// Remembering is not belt-and-braces: several tests below deliberately delete or rewrite the
    /// sidecar, and a payload whose sidecar is gone can no longer be found by searching for the
    /// temp path. Without this the suite left one orphan behind per run, in a directory that
    /// nothing prunes - the exact unbounded-growth smell this cache is supposed to avoid.
    /// </summary>
    private bool SaveEntry()
    {
        var saved = AixmlExportStore.Save(_viPath, _exportPath, _roots);
        if (saved) _payloads.Add(AixmlExportStore.PathFor(Key()));
        return saved;
    }

    /// <summary>
    /// Both halves of every entry this test wrote: the payloads it tracked, plus anything found by
    /// the VI path recorded in a sidecar. The second pass catches entries written under a copied
    /// key, which no test holds a path to.
    /// </summary>
    public void Dispose()
    {
        foreach (var payload in _payloads) Remove(payload);

        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(AixmlExportStore.Directory, "*.json"))
                if (File.ReadAllText(sidecar).Contains(
                        _tree.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase))
                    Remove(Path.ChangeExtension(sidecar, ".xml"));
        }
        catch { /* best effort */ }

        try { Directory.Delete(_tree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);

        static void Remove(string payload)
        {
            try { File.Delete(payload); } catch { /* best effort */ }
            try { File.Delete(Path.ChangeExtension(payload, ".json")); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void An_export_survives_a_save_and_a_copy_back()
    {
        Assert.True(SaveEntry());

        var destination = Destination();
        Assert.True(AixmlExportStore.TryCopyTo(_viPath, destination, _roots));
        Assert.Equal(Xml, File.ReadAllText(destination));
    }

    /// <summary>The destination is what callers pass on to ValidateAIXML, so it has to exist.</summary>
    [Fact]
    public void A_hit_creates_the_destination_directory()
    {
        SaveEntry();

        var destination = Path.Combine(_tree, "not", "yet", "there", "out.xml");
        Assert.True(AixmlExportStore.TryCopyTo(_viPath, destination, _roots));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public void A_vi_that_was_never_exported_is_a_miss() =>
        Assert.False(AixmlExportStore.TryCopyTo(_viPath, Destination(), _roots));

    /// <summary>
    /// The boundary the whole design rests on: an export depends on the VI's subVIs too, and for
    /// code outside the installation those change behind a caller whose own timestamp never moves.
    /// </summary>
    [Fact]
    public void User_code_outside_the_installation_is_never_cached()
    {
        var mine = Path.Combine(_tree, "MyProject", "Analysis.vi");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "PRETEND-VI-BYTES");

        Assert.False(AixmlExportStore.IsCacheable(mine, _roots));
        Assert.False(AixmlExportStore.Save(mine, _exportPath, _roots));
        Assert.False(AixmlExportStore.TryCopyTo(mine, Destination(), _roots));
    }

    [Fact]
    public void An_installation_vi_is_cacheable() =>
        Assert.True(AixmlExportStore.IsCacheable(_viPath, _roots));

    [Fact]
    public void A_rewritten_vi_no_longer_matches_its_entry()
    {
        SaveEntry();
        File.WriteAllText(_viPath, "PRETEND-VI-BYTES-BUT-LONGER");

        Assert.False(AixmlExportStore.TryCopyTo(_viPath, Destination(), _roots));
    }

    /// <summary>
    /// Size alone would miss a same-length edit, which is exactly what a recompile or a
    /// reinstall of the same version produces.
    /// </summary>
    [Fact]
    public void A_vi_touched_without_changing_size_no_longer_matches_either()
    {
        SaveEntry();
        File.SetLastWriteTimeUtc(_viPath, File.GetLastWriteTimeUtc(_viPath).AddMinutes(1));

        Assert.False(AixmlExportStore.TryCopyTo(_viPath, Destination(), _roots));
    }

    /// <summary>
    /// The sidecar is written last and is therefore the commit record. A crash between the two
    /// writes leaves an orphan payload, which must read as a miss rather than as an entry.
    /// </summary>
    [Fact]
    public void A_payload_without_its_sidecar_is_a_miss()
    {
        SaveEntry();
        File.Delete(Path.ChangeExtension(AixmlExportStore.PathFor(Key()), ".json"));

        Assert.False(AixmlExportStore.TryCopyTo(_viPath, Destination(), _roots));
    }

    [Fact]
    public void A_payload_that_is_not_the_recorded_size_is_a_miss()
    {
        SaveEntry();
        File.AppendAllText(AixmlExportStore.PathFor(Key()), "<!-- truncated or torn -->");

        Assert.False(AixmlExportStore.TryCopyTo(_viPath, Destination(), _roots));
    }

    [Fact]
    public void A_sidecar_from_another_format_version_is_a_miss()
    {
        SaveEntry();
        var sidecar = Path.ChangeExtension(AixmlExportStore.PathFor(Key()), ".json");
        File.WriteAllText(sidecar,
            $$"""
            {"Version":99,"Key":"{{Key().Replace(@"\", @"\\")}}","ViPath":"","CachedUtc":"2026-01-01T00:00:00Z","XmlBytes":{{Xml.Length}}}
            """);

        Assert.False(AixmlExportStore.TryCopyTo(_viPath, Destination(), _roots));
    }

    /// <summary>
    /// The file name is only a hash of the key, so a collision is conceivable. The key is stored
    /// in full and compared, which makes one harmless rather than merely unlikely.
    /// </summary>
    [Fact]
    public void An_entry_written_under_another_key_is_not_returned()
    {
        SaveEntry();

        var other = Path.Combine(Path.GetDirectoryName(_viPath)!, "Other.vi");
        File.WriteAllText(other, "PRETEND-VI-BYTES");
        var otherKey = AixmlExportStore.KeyFor(other)!;

        File.Copy(AixmlExportStore.PathFor(Key()), AixmlExportStore.PathFor(otherKey), true);
        File.Copy(Path.ChangeExtension(AixmlExportStore.PathFor(Key()), ".json"),
                  Path.ChangeExtension(AixmlExportStore.PathFor(otherKey), ".json"), true);

        Assert.False(AixmlExportStore.TryCopyTo(other, Destination(), _roots));
    }

    /// <summary>
    /// LabVIEW rejects forward slashes, but .NET accepts them and a shell passes them through, so
    /// the same VI can arrive spelled two ways. Without normalising, each spelling would get its
    /// own entry and neither would ever hit the other's.
    /// </summary>
    [Fact]
    public void The_same_vi_spelled_with_forward_slashes_hits_the_same_entry()
    {
        SaveEntry();

        Assert.True(AixmlExportStore.TryCopyTo(_viPath.Replace('\\', '/'), Destination(), _roots));
    }

    [Fact]
    public void A_missing_vi_is_a_miss_rather_than_a_crash()
    {
        var absent = Path.Combine(Path.GetDirectoryName(_viPath)!, "Never Existed.vi");

        Assert.Null(AixmlExportStore.KeyFor(absent));
        Assert.False(AixmlExportStore.Save(absent, _exportPath, _roots));
        Assert.False(AixmlExportStore.TryCopyTo(absent, Destination(), _roots));
    }

    [Fact]
    public void An_export_that_was_never_written_cannot_be_saved() =>
        Assert.False(AixmlExportStore.Save(
            _viPath, Path.Combine(_tree, "no-such-export.xml"), _roots));

    [Fact]
    public void Saving_twice_replaces_rather_than_appends()
    {
        SaveEntry();
        File.WriteAllText(_exportPath, "<VI _name=\"Second.vi\"/>");
        SaveEntry();

        var destination = Destination();
        Assert.True(AixmlExportStore.TryCopyTo(_viPath, destination, _roots));
        Assert.Equal("<VI _name=\"Second.vi\"/>", File.ReadAllText(destination));
    }

    [Fact]
    public void The_time_it_was_taken_is_recorded_and_recent()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        SaveEntry();

        var taken = AixmlExportStore.CachedUtc(_viPath);

        Assert.NotNull(taken);
        Assert.InRange(taken!.Value, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void No_temporary_files_are_left_behind()
    {
        SaveEntry();
        var payload = AixmlExportStore.PathFor(Key());

        Assert.False(File.Exists(payload + ".tmp"));
        Assert.False(File.Exists(Path.ChangeExtension(payload, ".json") + ".tmp"));
    }

    // ---------- debris ----------

    /// <summary>
    /// The intermediate name must be private to one writer. Three MCP server processes run at once
    /// on this machine, and a fixed `&lt;hash&gt;.xml.tmp` let two of them race: one moves it away,
    /// the other's Move throws onto a file that is gone. Observed as a stray `.json.tmp` plus a
    /// sidecar with no payload, both stamped the same second.
    /// </summary>
    /// <summary>
    /// Scoped to this test's own key: the cache directory is shared, and other classes run in
    /// parallel against it - which is the same reason the production code needed unique names.
    /// </summary>
    [Fact]
    public void The_intermediate_file_name_is_unique_per_write()
    {
        Assert.True(SaveEntry());

        var mine = Path.GetFileNameWithoutExtension(AixmlExportStore.PathFor(Key()));
        var strays = Directory.EnumerateFiles(AixmlExportStore.Directory, $"{mine}*.tmp").ToList();

        Assert.Empty(strays);
    }

    /// <summary>Its own directory: Reap deletes, and the real one is shared by every test class.</summary>
    private string ReapDir()
    {
        var dir = Path.Combine(_tree, "reap");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Reap_removes_a_stray_temp_file()
    {
        var dir = ReapDir();
        var stray = Path.Combine(dir, "deadbeefdeadbeef.xml.abc12345.tmp");
        File.WriteAllText(stray, "half a payload");
        var keepXml = Path.Combine(dir, "aaaabbbbccccdddd.xml");
        File.WriteAllText(keepXml, "<VI/>");
        File.WriteAllText(Path.ChangeExtension(keepXml, ".json"), "{}");

        Assert.Equal(1, AixmlExportStore.Reap(dir));

        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(keepXml));                       // the complete pair survives
        Assert.True(File.Exists(Path.ChangeExtension(keepXml, ".json")));
    }

    [Fact]
    public void Reap_removes_a_sidecar_whose_payload_is_gone()
    {
        var dir = ReapDir();
        var orphan = Path.Combine(dir, "1111222233334444.json");
        File.WriteAllText(orphan, "{}");

        Assert.Equal(1, AixmlExportStore.Reap(dir));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void Reap_removes_a_payload_whose_sidecar_is_gone()
    {
        var dir = ReapDir();
        var orphan = Path.Combine(dir, "5555666677778888.xml");
        File.WriteAllText(orphan, "<VI/>");

        Assert.Equal(1, AixmlExportStore.Reap(dir));
        Assert.False(File.Exists(orphan));
    }

    /// <summary>
    /// What matters is that a COMPLETE entry survives a sweep and is still readable. Asserting the
    /// reaped count is zero would be asserting that no other test class wrote a half entry in the
    /// meantime, and they run in parallel against the same directory.
    /// </summary>
    [Fact]
    public void Reap_leaves_a_complete_entry_intact()
    {
        var dir = ReapDir();
        var payload = Path.Combine(dir, "99990000aaaabbbb.xml");
        File.WriteAllText(payload, "<VI/>");
        File.WriteAllText(Path.ChangeExtension(payload, ".json"), "{}");

        Assert.Equal(0, AixmlExportStore.Reap(dir));

        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(Path.ChangeExtension(payload, ".json")));
    }

    private string Key() => AixmlExportStore.KeyFor(_viPath)!;
}

/// <summary>
/// Containment, which decides what counts as installation territory. Getting this wrong in the
/// permissive direction would cache user code; in the strict direction it would cache nothing.
/// </summary>
public class AixmlExportStoreContainmentTests
{
    [Theory]
    [InlineData(@"C:\LV 2026\examples\A.vi", @"C:\LV 2026", true)]
    [InlineData(@"C:\LV 2026\vi.lib\Utility\A.vi", @"C:\LV 2026", true)]
    [InlineData(@"C:\LV 2026\examples\A.vi", @"C:\LV 2026\", true)]     // trailing separator
    [InlineData(@"C:\lv 2026\examples\A.vi", @"C:\LV 2026", true)]      // Windows is case-blind
    [InlineData(@"C:\LV 2026-backup\examples\A.vi", @"C:\LV 2026", false)]
    [InlineData(@"C:\LV 2026", @"C:\LV 2026", false)]                   // the root is not under itself
    [InlineData(@"C:\Projects\Mine\A.vi", @"C:\LV 2026", false)]
    public void A_path_is_under_a_root_only_at_a_separator(string path, string root, bool expected) =>
        Assert.Equal(expected, AixmlExportStore.IsUnder(path, root));

    [Fact]
    public void Nothing_is_cacheable_when_there_are_no_roots() =>
        Assert.False(AixmlExportStore.IsCacheable(@"C:\LV 2026\examples\A.vi", []));
}
