using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// `Read from Text File` with <c>convertEol="true"</c> was refused by the composer ("mode
/// convertEol=true is not composable yet") - a test helper of the second PC AI DI TDMS cold build
/// dropped the attribute to get past it (docs/cold-build-pc-ai-di-tdms.md). The bit is 0x2000000
/// of the node's objFlags: 640 -> 33555072 on NI's converter's build of a probe pair differing only
/// in the attribute, 2026-11-01. The probe here is that pair's document.
///
/// Runs the bundled interpreter and SKIPS without it, as the other composer tests do.
/// </summary>
public sealed class PylabviewConvertEolTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static string Probe(string mode) => $$"""
        <VI _name="EOL Probe.vi" description="Probe.">
          <Control _name="File" conIdx="0" connection="required" outputs="value:4200.value" type="path" uid="4200" uid_parent="root" value=""/>
          <Control _name="error in" conIdx="11" connection="required" outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Node _name="Read from Text File"{{mode}} inputs="file (use dialog):4200.value,error in:4201.value" outputs="refnum out:,text:4202.text,cancelled:,error out:4202.error out" uid="4202" uid_parent="root"/>
          <Indicator _name="Text" conIdx="4" connection="recommended" inputs="value:4202.text" type="string" uid="4203" uid_parent="root" value=""/>
          <Indicator _name="error out" conIdx="15" connection="recommended" inputs="value:4202.error out" type="cluster{bool.status,int32.code,string.source}" uid="4204" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    private static async Task<XElement> ComposeAndReadAsync(string mode)
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("eol.xml");
        var vi = server.TempPath("eol.vi");
        await File.WriteAllTextAsync(xml, Probe(mode));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        var setup = AixmlComposer.Locate(out _)!;
        var output = server.TempPath("eol.read.xml");
        var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
        Assert.True(read.Written, read.Message);
        return XElement.Load(output).Descendants("Node")
            .Single(n => (string?)n.Attribute("_name") == "Read from Text File");
    }

    [Fact]
    public async Task ConvertEol_composes_and_reads_back()
    {
        if (!ComposerHere()) return;
        var node = await ComposeAndReadAsync(" convertEol=\"true\"");
        Assert.Equal("true", (string?)node.Attribute("convertEol"));
        Assert.Null(node.Attribute("readLines"));        // its own bit, not a neighbour's
    }

    [Fact]
    public async Task Both_modes_together_keep_both_bits()
    {
        if (!ComposerHere()) return;
        var node = await ComposeAndReadAsync(" convertEol=\"true\" readLines=\"true\"");
        Assert.Equal("true", (string?)node.Attribute("convertEol"));
        Assert.Equal("true", (string?)node.Attribute("readLines"));
    }

    [Fact]
    public async Task Without_the_attribute_the_node_reads_back_without_it()
    {
        if (!ComposerHere()) return;
        Assert.Null((await ComposeAndReadAsync("")).Attribute("convertEol"));
    }
}
