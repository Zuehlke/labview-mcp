using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The two composer gaps the producer/consumer CSV-log cold build of 2026-11-01 hit
/// (docs/cold-build-pc-userevent-csv.md):
///
/// 1. <c>Set File Position</c> (primResID 8073) had a part but no name, so the composer refused it
///    and the subVI went to NI's service. The terminal names were read off NI's converter's build
///    of a probe (<c>offset (in bytes) (0)</c> is the only spelling ValidateAIXML takes).
/// 2. A user-event payload field named like the event's own (<c>Source</c>): the Event Data Node
///    picked fields by first match, so <c>fields="Source"</c> read the EVENT's Source, and the build
///    needed lvai_set_event_data_fields to undo it. The payload's field now wins.
///
/// Both run the bundled interpreter and SKIP without it, as the other composer tests do.
/// </summary>
public sealed class PylabviewSetFilePositionAndPayloadClashTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

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

    private const string SetFilePositionProbe = """
        <VI _name="SFP Probe.vi" description="Probe.">
          <Control _name="Log File" outputs="value:4200.value" type="path" uid="4200" uid_parent="root" value="" conIdx="0" connection="required"/>
          <Control _name="error in" outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]" conIdx="11" connection="required"/>
          <Constant _name="op" outputs="value:4202.value" type="int32" uid="4202" uid_parent="root" value="4"/>
          <Constant _name="off" outputs="value:4203.value" type="int64" uid="4203" uid_parent="root" value="0"/>
          <Constant _name="from" outputs="value:4204.value" type="uint16" uid="4204" uid_parent="root" value="1"/>
          <Node _name="Open/Create/Replace File" inputs="file path (use dialog):4200.value,operation (0\3Aopen):4202.value,error in:4201.value" outputs="refnum out:4205.refnum out,error out:4205.error out" uid="4205" uid_parent="root"/>
          <Node _name="Set File Position" inputs="refnum:4205.refnum out,error in:4205.error out,offset (in bytes) (0):4203.value,from (0\3Astart):4204.value" outputs="refnum out:4206.refnum out,error out:4206.error out" uid="4206" uid_parent="root"/>
          <Node _name="Close File" inputs="refnum:4206.refnum out,error in:4206.error out" outputs="path:,error out:4207.error out" uid="4207" uid_parent="root"/>
          <Indicator _name="error out" inputs="value:4207.error out" type="cluster{bool.status,int32.code,string.source}" uid="4208" uid_parent="root" value="[false,0,]" conIdx="15" connection="recommended"/>
        </VI>
        """;

    [Fact]
    public async Task Set_File_Position_composes_and_reads_back_with_NIs_terminal_names()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("sfp.xml");
        var vi = server.TempPath("sfp.vi");
        await File.WriteAllTextAsync(xml, SetFilePositionProbe);

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        var node = (await ReadAsync(vi)).Descendants("Node")
            .Single(n => (string?)n.Attribute("_name") == "Set File Position");
        var inputs = (string)node.Attribute("inputs")!;
        Assert.Contains("refnum:", inputs);
        Assert.Contains("error in:", inputs);
        Assert.Contains("offset (in bytes) (0):", inputs);
        Assert.Contains("from (0\\3Astart):", inputs);
        Assert.Contains("refnum out:", (string)node.Attribute("outputs")!);
    }

    /// <summary>The shipped user-event skeleton, its payload turned into {Source, Last} and read
    /// by name: the composer must take the payload's Source (field 4), not the event's (field 0).</summary>
    [Fact]
    public async Task A_payload_field_named_like_the_events_own_is_read_from_the_payload()
    {
        if (!ComposerHere()) return;
        var skeleton = await File.ReadAllTextAsync(RepoTree.Path("scripts", "aixml-skeletons", "user-event-two-loops.xml"));
        var doc = skeleton
            .Replace("""<Constant _name="Tick" outputs="value:4201.value" type="bool" uid="4201" uid_parent="root" value="false"/>""",
                     """<Constant _name="Tick" outputs="value:4201.value" type="cluster{string.Source,bool.Last}" uid="4201" uid_parent="root" value="[,false]"/>""")
            .Replace("event data cluster:4218.x &gt;= y?", "event data cluster:4211.value")
            .Replace("""<Node _name="Event Data Node" fields="Source,Type,Time" outputs="Source:,Type:,Time:" uid="4244" uid_parent="4240"/>""",
                     """<Node _name="Event Data Node" fields="Source,Last" outputs="Source:4244.Source,Last:4244.Last" uid="4244" uid_parent="4240"/>""")
            .Replace("""<Constant _name="Payload Placeholder" outputs="value:4280.value" type="bool" uid="4280" uid_parent="4240" value="false"/>""", "")
            .Replace("y:4280.value", "y:4244.Last");
        Assert.DoesNotContain("4280", doc);

        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("clash.xml");
        await File.WriteAllTextAsync(xml, doc);
        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("clash.vi"), checkExecutable: false, backend: "pylabview");

        Assert.Equal(0, Res.Int(composed, "errorCode"));
        var report = Res.Arr(composed, "composerReport").Select(r => r!.GetValue<string>()).ToList();
        Assert.Contains(report, r => r.Contains("'Source' names an event field and a payload field")
                                     && r.Contains("field 4"));
    }
}
