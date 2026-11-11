using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// An error cluster constant is composed the way NI places one - viewed as an icon, framed in the
/// error colour - not as a full cluster in pink (user-forum feedback, 2026-11-11: "an Error Cluster
/// constant viewed as an Icon but the color is pink, not brown"). The values are NI's own, read off
/// Constant-to-Constant Resampling.vi and rendered on LabVIEW 2026; the flags alone gave a PINK icon,
/// so the frame colour is asserted too. SKIPS where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class ErrorConstantLookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "errconst-" + Guid.NewGuid().ToString("n")[..8]);

    public ErrorConstantLookTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<XElement?> ComposedClusterAsync(string type)
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return null;
        var xml = Path.Combine(_dir, "probe.xml");
        var vi = Path.Combine(_dir, "Probe.vi");
        await File.WriteAllTextAsync(xml, $"""
            <VI _name="Probe.vi" description="x">
              <Constant _name="c" outputs="value:4230.value" type="{type}" uid="4230" uid_parent="root" value="[false,0,]"/>
              <Indicator _name="o" inputs="value:4230.value" type="{type}" uid="4231" uid_parent="root" value="[false,0,]"/>
            </VI>
            """);
        var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
        Assert.True(composed.Written, composed.Message);
        var x = Path.Combine(_dir, "x");
        Directory.CreateDirectory(x);
        var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy, ["-x", "-i", vi, "-m", Path.Combine(x, "Probe.xml")], 120, default);
        Assert.True(extract.ExitCode == 0, extract.StdErr);
        var heap = XElement.Load(Directory.GetFiles(x, "*_BDHb.xml").Single());
        return heap.Descendants("ddo").Single(d => (string?)d.Attribute("class") == "stdClust" && (string?)d.Attribute("uid") == "4230");
    }

    private static string Frame(XElement ddo) =>
        ddo.Element("partsList")!.Elements().Single(p => (string?)p.Element("partID") == "9").Element("fgColor")!.Value;

    [Fact]
    public async Task An_error_cluster_constant_is_an_icon_in_the_error_colour()
    {
        if (await ComposedClusterAsync("cluster{bool.status,int32.code,string.source}") is not { } ddo) return;
        var flags = long.Parse(ddo.Element("objFlags")!.Value);
        Assert.NotEqual(0, flags & 0x10000000);                     // View Cluster as Icon
        Assert.Equal("007F7F00", Frame(ddo));
        Assert.Equal("(16, 22)", Size(ddo.Element("bounds")!.Value));
    }

    [Fact]
    public async Task Any_other_cluster_keeps_the_cluster_look()
    {
        if (await ComposedClusterAsync("cluster{bool.flag,int32.n,string.name}") is not { } ddo) return;
        Assert.Equal(0, long.Parse(ddo.Element("objFlags")!.Value) & 0x10000000);
        Assert.Equal("00FF00FF", Frame(ddo));
    }

    private static string Size(string bounds)
    {
        var v = bounds.Trim('(', ')').Split(',').Select(s => int.Parse(s.Trim())).ToArray();
        return $"({v[2] - v[0]}, {v[3] - v[1]})";
    }
}
