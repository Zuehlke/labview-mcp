using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A STRICTLY TYPED control refnum on a connector pane - <c>ref{LV.WaveformChart}{double}</c>, the
/// spelling NI's exporter gives the <c>Chart Reference</c> terminal of the Control References
/// example's <c>Helper - Modify Chart.vi</c> - was refused by aixml2vi.py with "is not composable
/// yet" until 2026-10-20, while the plain <c>ref{LV.String}</c> and a VI Server Reference node to the
/// same chart both composed. The run behaviour is in the composer's own suite (t165-t168); these
/// check the grammar and the reader's round trip. They run the bundled interpreter and SKIP without
/// it, as the other composer tests do.
/// </summary>
public sealed class PylabviewTypedControlRefnumTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static string Probe(string refType) => $"""
        <VI _name="TypedRef.vi" description="A strictly typed control refnum on the pane.">
          <Control _name="Ref in" conIdx="5" connection="required" type="{refType}" value="" outputs="value:4201.v" uid="4201" uid_parent="root"/>
          <Indicator _name="Ref out" conIdx="6" connection="recommended" type="{refType}" value="" inputs="value:4201.v" uid="4202" uid_parent="root"/>
        </VI>
        """;

    private static async Task<XElement> ReadAsync(string vi)
    {
        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("ref{LV.WaveformChart}{double}")]
    [InlineData("ref{LV.Digital}{double}")]
    [InlineData("ref{LV.Boolean}{bool}")]
    public async Task A_strictly_typed_control_refnum_composes_and_reads_back_unchanged(string refType)
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("typedref.xml");
        var vi = server.TempPath("typedref.vi");
        await File.WriteAllTextAsync(xml, Probe(refType));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        var read = await ReadAsync(vi);
        Assert.Equal(refType, (string?)read.Descendants("Control").Single().Attribute("type"));
        Assert.Equal(refType, (string?)read.Descendants("Indicator").Single().Attribute("type"));
    }

    [Fact]
    public async Task An_unknown_class_is_refused_by_name()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("typedref-bad.xml");
        await File.WriteAllTextAsync(xml, Probe("ref{LV.NoSuchClass}{double}"));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("typedref-bad.vi"), checkExecutable: false, backend: "pylabview");

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains("LV.NoSuchClass", Res.Str(composed, "errorMessage"));
    }
}
