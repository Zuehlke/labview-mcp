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
