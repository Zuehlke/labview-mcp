using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// The block diagram window's view (pBounds) over the code - the user's screenshot of 2026-10-31,
/// a generated VI opening on an EMPTY diagram because the view stayed at the composer skeleton's
/// (243, 336). The composer fits it, and lvbd_fit_view.py fits it again after the layout moved the
/// code. Composed with the pylabview backend; SKIPS where the bundled interpreter is missing.
/// </summary>
public sealed class FitViewTests
{
    private const string Probe = """
        <VI _name="View Probe.vi" description="One addition.">
          <Control _name="x" outputs="value:4201.v" type="double" uid="4201" uid_parent="root" value="0"/>
          <Control _name="y" outputs="value:4202.v" type="double" uid="4202" uid_parent="root" value="0"/>
          <Node _name="Add" inputs="x:4201.v,y:4202.v" outputs="x+y:4203.s" uid="4203" uid_parent="root"/>
          <Indicator _name="sum" inputs="value:4203.s" type="double" uid="4204" uid_parent="root" value="0"/>
        </VI>
        """;

    [Theory]
    [InlineData("view fitted (0, -42, 520, 775)", "(0, -42, 520, 775)")]
    [InlineData("view unchanged", null)]
    [InlineData("", null)]
    public void The_script_answer_is_read(string stdout, string? view) =>
        Assert.Equal(view, DiagramCleanup.ParseFitView(stdout));

    [Fact]
    public async Task The_view_starts_at_zero_and_follows_code_the_layout_moved()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "fitview-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "View Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "ViewProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single();

            // the composer lays out from (40, 40): the view starts at 0/0, the skeleton's size kept
            var text = await File.ReadAllTextAsync(heap);
            Assert.Contains("<pBounds>(0, 0, 520, 817)</pBounds>", text);

            // the script finds nothing to do on it
            var script = RepoTree.Path("scripts", "lvbd_fit_view.py");
            var unchanged = await PyLabview.RunAsync(bundle, script, [heap], 120, default);
            Assert.True(unchanged.ExitCode == 0, unchanged.StdErr + unchanged.StdOut);
            Assert.Null(DiagramCleanup.ParseFitView(unchanged.StdOut));

            // code moved left of 0 (the layout put a comment at -22): the view follows, by the margin
            // every bounds left by 100 - nested ones are relative, so moving them too changes nothing
            var shifted = Regex.Replace(text, @"(<bounds>)\((-?\d+), (-?\d+), (-?\d+), (-?\d+)\)",
                m => string.Format("{0}({1}, {2}, {3}, {4})", m.Groups[1].Value, m.Groups[2].Value,
                    int.Parse(m.Groups[3].Value) - 100, m.Groups[4].Value, int.Parse(m.Groups[5].Value) - 100));
            Assert.NotEqual(text, shifted);
            await File.WriteAllTextAsync(heap, shifted);
            var moved = await PyLabview.RunAsync(bundle, script, [heap], 120, default);
            Assert.True(moved.ExitCode == 0, moved.StdErr + moved.StdOut);
            var view = DiagramCleanup.ParseFitView(moved.StdOut);
            Assert.NotNull(view);
            var left = int.Parse(Regex.Match(view!, @"^\(-?\d+, (-?\d+)").Groups[1].Value);
            Assert.True(left < 0, view);
            Assert.Contains("<pBounds>" + view + "</pBounds>", await File.ReadAllTextAsync(heap));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
