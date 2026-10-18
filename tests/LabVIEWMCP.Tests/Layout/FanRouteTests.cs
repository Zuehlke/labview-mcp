using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// lvbd_fan_route.py, the step after lvai_layout_diagram's save that gives a constant fan its own
/// wire geometry (docs/diagram-layout.md section 6g). Composed with the pylabview backend, so it
/// needs no LabVIEW; SKIPS where the bundled interpreter is missing, as the composer tests do.
/// </summary>
public sealed class FanRouteTests
{
    private const string Probe = """
        <VI _name="Fan Probe.vi" description="Three constants into one Index Array.">
          <Control _name="Row" outputs="value:4201.v" type="array{string}" uid="4201" uid_parent="root" value="[]"/>
          <Constant _name="column 1" outputs="value:4202.value" type="int32" uid="4202" uid_parent="root" value="1"/>
          <Constant _name="column 2" outputs="value:4203.value" type="int32" uid="4203" uid_parent="root" value="2"/>
          <Constant _name="column 3" outputs="value:4204.value" type="int32" uid="4204" uid_parent="root" value="3"/>
          <Node _name="Index Array" inputs="array:4201.v,index:4202.value,index:4203.value,index:4204.value" outputs="element:4210.a,element:4210.b,element:4210.c" uid="4210" uid_parent="root"/>
          <Indicator _name="A" inputs="value:4210.a" type="string" uid="4211" uid_parent="root" value=""/>
          <Indicator _name="B" inputs="value:4210.b" type="string" uid="4212" uid_parent="root" value=""/>
          <Indicator _name="C" inputs="value:4210.c" type="string" uid="4213" uid_parent="root" value=""/>
        </VI>
        """;

    [Theory]
    [InlineData("fans 1, wires rewritten 3\n  signal 20 -> 0408", 1, 3)]
    [InlineData("fans 0, wires rewritten 0", 0, 0)]
    [InlineData("", 0, 0)]
    public void The_script_summary_is_read(string stdout, int fans, int rewritten) =>
        Assert.Equal((fans, rewritten), DiagramCleanup.ParseFanRoute(stdout));

    /// <summary>Three constants into one Index Array: each wire is east, vertical, east, and the
    /// verticals stand apart, ordered so that none crosses another's horizontal.</summary>
    [Fact]
    public async Task A_constant_fan_gets_staggered_verticals()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "fan-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Fan Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "FanProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single();
            var run = await PyLabview.RunAsync(bundle, RepoTree.Path("scripts", "lvbd_fan_route.py"), [heap], 120, default);
            Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
            Assert.Equal((1, 3), DiagramCleanup.ParseFanRoute(run.StdOut));
            // each table: 04 08 <dir> 00 <first> <vertical>, single-byte lengths here
            var tables = Regex.Matches(run.StdOut, @"-> (0408[0-9A-F]+)").Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(3, tables.Count);
            var firsts = tables.Select(t => Convert.ToInt32(t.Substring(8, 2), 16)).ToList();
            var verticals = tables.Select(t => Convert.ToInt32(t.Substring(10, 2), 16)).ToList();
            Assert.Equal(3, firsts.Distinct().Count());
            // the longer the vertical, the farther right it turns: no vertical crosses a horizontal
            var order = verticals.Zip(firsts).OrderBy(p => p.First).Select(p => p.Second).ToList();
            Assert.Equal(order.OrderBy(x => x), order);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
