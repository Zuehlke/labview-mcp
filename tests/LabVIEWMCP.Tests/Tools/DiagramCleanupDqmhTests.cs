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

    /// <summary>
    /// Branch_Retest, 2026-11-12: inside the answer of a layout that had just laid Main.vi out, the
    /// skipped NI fallback said Main.vi is never touched. It says now that only NI's is not tried.
    /// </summary>
    [Fact]
    public async Task The_NI_fallback_on_a_DQMH_main_says_our_layout_stands()
    {
        Touch("Request Events--cluster.ctl");
        Touch("Broadcast Events--cluster.ctl");
        var main = Touch("Main.vi");

        var step = await new DiagramCleanup(null!).RunNiDiagramAsync(main, "pylabview", 10, CancellationToken.None);

        Assert.True(step["skipped"]!.GetValue<bool>());
        var note = step["note"]!.GetValue<string>();
        Assert.Contains("Our layout ran and is kept", note);
        Assert.DoesNotContain("never touches", note);
    }

    /// <summary>
    /// Branch_Retest, 2026-11-12: the layout's note said the VI "still" carried compiled code, read
    /// beside a sourceOnly step that had just removed it. The note follows the strip now.
    /// </summary>
    [Fact]
    public void The_layout_note_follows_a_strip_that_ran_after_it()
    {
        System.Text.Json.Nodes.JsonObject Layout() => new()
        {
            ["note"] = "Our block diagram layout (lvai_layout_diagram) ran and the VI was saved in place (it carried compiled code before, and still does).",
        };

        var stripped = Layout();
        DiagramCleanup.NoteStripped(stripped, new() { ["ok"] = true, ["sourceOnly"] = true });
        Assert.DoesNotContain("still does", stripped["note"]!.GetValue<string>());
        Assert.Contains("compiled code was removed afterwards", stripped["note"]!.GetValue<string>());

        var skipped = Layout();
        DiagramCleanup.NoteStripped(skipped, new() { ["ok"] = true, ["skipped"] = "source only already" });
        Assert.Contains("still does", skipped["note"]!.GetValue<string>());

        var failed = Layout();
        DiagramCleanup.NoteStripped(failed, new() { ["ok"] = false, ["sourceOnly"] = false });
        Assert.Contains("still does", failed["note"]!.GetValue<string>());
    }

    /// <summary>Since 2026-10-16 the diagram step is our layout engine, not LabVIEW's BD.CleanUp, and
    /// every answer says which ran - a skip included.</summary>
    [Fact]
    public async Task The_diagram_step_names_our_layout_engine()
    {
        Touch("Request Events--cluster.ctl");
        Touch("Broadcast Events--cluster.ctl");
        var main = Touch("Main.vi");

        var step = await new DiagramCleanup(null!).RunAsync(main, "pylabview", 10, CancellationToken.None);

        Assert.Equal("cleanUpDiagram", step["step"]!.GetValue<string>());
        Assert.Equal("lvai_layout_diagram", step["engine"]!.GetValue<string>());
    }
}
