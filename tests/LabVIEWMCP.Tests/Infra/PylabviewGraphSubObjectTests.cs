using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A graph's SUB-OBJECT fields on the pylabview composer (2026-11-20). Until then an XY graph
/// composed only its harvested `Plot` and `Cursor` fields: `Label.Text` was refused, because the
/// XY graph had no catalogue rows and `Label` could not be borrowed (it is tabled with more than one
/// type across the classes). The composer now takes the nearest ANCESTOR's row. The run behaviour
/// is in experiments/graph-property-sweep (952 fields loaded, NI's exporter read each back; written
/// fields read back at run time); these pin the composition and the reader's round trip. They run
/// the bundled interpreter and SKIP without it, as the other composer tests do.
/// </summary>
public sealed class PylabviewGraphSubObjectTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static string Probe(string style, string type, string cls, string fields) => $"""
        <VI _name="GraphSub.vi" description="Sub-object fields of a graph.">
          <Indicator _name="G" inputs="value:" style="{style}" type="{type}" uid="4200" uid_parent="root" value="[]"/>
          <Node _name="VI Server Reference" element="G" outputs="G:4201.ref" uid="4201" uid_parent="root"/>
          <Node _name="Property Node" fields="{fields}" inputs="reference:4201.ref" outputs="{string.Join(",", fields.Split(',').Select(f => f.Split('+')[1] + ":"))},reference out:,error out:" type="{cls}" uid="4210" uid_parent="root"/>
        </VI>
        """;

    [Theory]
    [InlineData("graph21716", "cluster{array{double}.X,array{double}.Y}", "{LV.XYGraph}",
                "read+Label.Text,read+Plot.Plot Name,read+Y Scale.Range\\3AMaximum")]
    [InlineData("graph21703", "array.2{double}", "{LV.WaveformGraph}",
                "read+Plot.Plot Name,read+Y Scale.Name Label.Text,read+Cursor.Cursor Name")]
    public async Task Nested_fields_compose_and_read_back_by_name(string style, string type, string cls, string fields)
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("graphsub.xml");
        var vi = server.TempPath("graphsub.vi");
        await File.WriteAllTextAsync(xml, Probe(style, type, cls, fields));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        var setup = AixmlComposer.Locate(out _);
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var read = await AixmlComposer.ReadAsync(setup!, vi, output, 120, default);
            Assert.True(read.Written, read.Message);
            var node = XElement.Load(output).Descendants("Node")
                .Single(n => (string?)n.Attribute("_name") == "Property Node");
            Assert.Equal(fields, (string?)node.Attribute("fields"));
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }
}
