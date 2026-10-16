using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The panel the pylabview reader lists for a VI, kept per file state. The ActiveX run route asked
/// the reader before every run, and for lvai_layout_diagram's two helpers that was about 2 s of a
/// small VI's 2.3 (measured 2026-10-16).
/// </summary>
public sealed class PanelCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests", "panels-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _vi;

    private static readonly List<AixmlComposer.PanelItem> Items =
    [
        new("vi path", "path", false),
        new("lines", "array{string}", true),
        new("error out", "cluster{bool.status,int32.code,string.source}", true),
    ];

    public PanelCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _vi = Path.Combine(_dir, "helper.vi");
        File.WriteAllBytes(_vi, new byte[2048]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_panel_read_once_is_answered_again_from_disk()
    {
        PanelCache.Put(_vi, "reader1", Items, _dir);
        PanelCache.ForgetMemory();
        var hit = PanelCache.TryGet(_vi, "reader1", _dir);
        Assert.Equal(Items, hit);
    }

    [Fact]
    public void A_vi_saved_again_is_a_miss()
    {
        PanelCache.Put(_vi, "reader1", Items, _dir);
        File.AppendAllText(_vi, "x");
        File.SetLastWriteTimeUtc(_vi, DateTime.UtcNow.AddSeconds(5));
        Assert.Null(PanelCache.TryGet(_vi, "reader1", _dir));
    }

    /// <summary>The first key was size and write time: a copy that keeps an old timestamp, at the
    /// same size, answered the panel of whatever the path held before.</summary>
    [Fact]
    public void Same_size_and_timestamp_with_other_content_is_a_miss()
    {
        var when = File.GetLastWriteTimeUtc(_vi);
        PanelCache.Put(_vi, "reader1", Items, _dir);
        var other = new byte[2048];
        other[100] = 1;
        File.WriteAllBytes(_vi, other);
        File.SetLastWriteTimeUtc(_vi, when);
        Assert.Equal(2048, new FileInfo(_vi).Length);
        Assert.Null(PanelCache.TryGet(_vi, "reader1", _dir));
    }

    [Fact]
    public void A_new_state_replaces_the_entry_of_the_same_vi_and_no_other()
    {
        var second = Path.Combine(_dir, "other.vi");
        File.WriteAllBytes(second, new byte[1500]);
        PanelCache.Put(_vi, "reader1", Items, _dir);
        PanelCache.Put(second, "reader1", Items, _dir);
        File.AppendAllText(_vi, "saved again");
        PanelCache.Put(_vi, "reader1", Items, _dir);

        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
        PanelCache.ForgetMemory();
        Assert.NotNull(PanelCache.TryGet(_vi, "reader1", _dir));
        Assert.NotNull(PanelCache.TryGet(second, "reader1", _dir));
    }

    [Fact]
    public void A_changed_reader_is_a_miss()
    {
        PanelCache.Put(_vi, "reader1", Items, _dir);
        Assert.Null(PanelCache.TryGet(_vi, "reader2", _dir));
    }

    [Fact]
    public void With_no_reader_stamp_nothing_is_kept()
    {
        PanelCache.Put(_vi, null, Items, _dir);
        Assert.Null(PanelCache.TryGet(_vi, null, _dir));
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }
}
