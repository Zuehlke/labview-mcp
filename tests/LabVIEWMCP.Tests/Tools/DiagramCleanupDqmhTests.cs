using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The user's rule of 2026-10-13: a DQMH module's Main.vi is never cleaned up. The DQMH tools do
/// not call the clean-up at all; this guards the routes that might reach the file anyway
/// (lvai_swap_subvis, a regeneration). Recognised by name plus the two event clusters every
/// module folder carries - a plain Main.vi elsewhere is still cleaned.
/// </summary>
public sealed class DiagramCleanupDqmhTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cleanup-dqmh-" + Guid.NewGuid().ToString("N"));

    public DiagramCleanupDqmhTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Main_vi_beside_both_event_clusters_is_a_DQMH_main()
    {
        Touch("Request Events--cluster.ctl");
        Touch("Broadcast Events--cluster.ctl");
        Assert.True(DiagramCleanup.IsDqmhMain(Touch("Main.vi")));
        Assert.True(DiagramCleanup.IsDqmhMain(Path.Combine(_dir, "main.VI")));
    }

    [Fact]
    public void Another_VI_in_a_module_folder_is_not()
    {
        Touch("Request Events--cluster.ctl");
        Touch("Broadcast Events--cluster.ctl");
        Assert.False(DiagramCleanup.IsDqmhMain(Touch("Handle Start.vi")));
    }

    [Fact]
    public void A_plain_Main_vi_is_not()
    {
        Assert.False(DiagramCleanup.IsDqmhMain(Touch("Main.vi")));
        Touch("Request Events--cluster.ctl");
        Assert.False(DiagramCleanup.IsDqmhMain(Path.Combine(_dir, "Main.vi")));
    }

    [Fact]
    public async Task The_cleanup_skips_a_DQMH_main_without_touching_LabVIEW()
    {
        Touch("Request Events--cluster.ctl");
        Touch("Broadcast Events--cluster.ctl");
        var main = Touch("Main.vi");

        // A null connection: anything past the guard would throw.
        var step = await new DiagramCleanup(null!).RunAsync(main, "pylabview", 10, CancellationToken.None);

        Assert.True(step["ok"]!.GetValue<bool>());
        Assert.True(step["skipped"]!.GetValue<bool>());
        Assert.Contains("DQMH", step["note"]!.GetValue<string>());
    }
}
