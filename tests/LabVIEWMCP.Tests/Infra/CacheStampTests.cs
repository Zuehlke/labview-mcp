using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// What makes the reader's caches and the composed helpers go stale (2026-10-16). Each case pairs
/// the change that must invalidate with the one that must not, so a rule that invalidates
/// everything cannot pass either.
/// </summary>
public sealed class CacheStampTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests", "stamps-" + Guid.NewGuid().ToString("N")[..8]);

    public CacheStampTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (string Composer, string Bundle) Reader()
    {
        var composer = Directory.CreateDirectory(Path.Combine(_dir, "composer")).FullName;
        foreach (var f in new[] { "vi2aixml.py", "aixml2vi.py", "lvheap.py", "resolve.py" })
            File.WriteAllText(Path.Combine(composer, f), "# " + f);
        var bundle = Directory.CreateDirectory(Path.Combine(_dir, "bundle")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(bundle, "app", "pylabview")).FullName;
        File.WriteAllText(Path.Combine(package, "LVheap.py"), "# heap");
        File.WriteAllText(Path.Combine(package, "LVblock.py"), "# block");
        return (composer, bundle);
    }

    /// <summary>The stamp covered our four scripts only: a pylabview update - a new commit, or a
    /// patch applied to its modules - kept every cached export and panel list valid.</summary>
    [Fact]
    public void The_reader_stamp_follows_the_bundled_pylabview()
    {
        var (composer, bundle) = Reader();
        string Stamp(string commit) => AixmlExportStore.StampOf(AixmlExportStore.ReaderFiles(composer, bundle), commit);

        var before = Stamp("abc");
        Assert.Equal(before, Stamp("abc"));
        Assert.NotEqual(before, Stamp("def"));

        File.WriteAllText(Path.Combine(bundle, "app", "pylabview", "LVheap.py"), "# heap, patched");
        Assert.NotEqual(before, Stamp("abc"));
    }

    [Fact]
    public void The_reader_files_name_every_pylabview_module()
    {
        var (composer, bundle) = Reader();
        var files = AixmlExportStore.ReaderFiles(composer, bundle).Select(Path.GetFileName).ToList();
        Assert.Contains("vi2aixml.py", files);
        Assert.Contains("LVheap.py", files);
        Assert.Contains("LVblock.py", files);
    }

    /// <summary>A helper was rebuilt when its AIXML was NEWER: an older AIXML restored with its own
    /// older timestamp left the helper composed from the newer one in place.</summary>
    [Fact]
    public void A_composed_helper_follows_its_aixml_content_whatever_the_timestamp()
    {
        var aixml = Path.Combine(_dir, "helper.xml");
        var vi = Path.Combine(_dir, "helper.vi");
        var stamp = vi + ".parts";
        File.WriteAllText(aixml, "<VI description=\"one\"/>");
        File.WriteAllBytes(vi, new byte[4096]);
        File.SetLastWriteTimeUtc(aixml, DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllText(stamp, ComposedHelpers.ExpectedStamp("parts|26", aixml));

        // nothing changed: current
        Assert.False(ComposedHelpers.IsStale(aixml, vi, stamp, ComposedHelpers.ExpectedStamp("parts|26", aixml)));

        // older timestamp, other content: stale
        File.WriteAllText(aixml, "<VI description=\"two\"/>");
        File.SetLastWriteTimeUtc(aixml, DateTime.UtcNow.AddMinutes(-20));
        Assert.True(ComposedHelpers.IsStale(aixml, vi, stamp, ComposedHelpers.ExpectedStamp("parts|26", aixml)));

        // other parts or release, same content: stale as before
        File.WriteAllText(aixml, "<VI description=\"one\"/>");
        File.SetLastWriteTimeUtc(aixml, DateTime.UtcNow.AddMinutes(-20));
        Assert.True(ComposedHelpers.IsStale(aixml, vi, stamp, ComposedHelpers.ExpectedStamp("parts|25", aixml)));
    }
}
