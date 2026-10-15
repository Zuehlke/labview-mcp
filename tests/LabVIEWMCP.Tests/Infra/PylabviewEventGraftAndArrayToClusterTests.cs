using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The two pylabview-backend defects the DQMH ATM cold build of 2026-10-15 hit, each of which sent
/// that build back to NI's service:
///
/// 1. vi2aixml.py wrote an Event Structure selector with a RAW colon, where NI writes <c>\3A</c>.
///    lvai_graft_diagram's EventFrames found no frame in the scaffold's export, skipped the
///    re-registration and left the grafted VI eBad. THE FIXTURE IS THE REAL SCAFFOLD of that build.
/// 2. aixml2vi.py refused <c>Array To Cluster elements="3"</c>, and without the attribute composed
///    the part donor's three-field cluster where NI's converter writes nine. The reader did not
///    write the attribute at all, so a round trip lost the size.
///
/// Both run the bundled interpreter and SKIP without it, as the other composer tests do.
/// </summary>
public sealed class PylabviewEventGraftAndArrayToClusterTests
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

    [Fact]
    public async Task The_reader_escapes_event_selectors_as_NI_does_and_EventFrames_reads_them()
    {
        if (!ComposerHere()) return;
        var fixture = RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "ATM Scaffold.vi");
        var vi = await ReadAsync(fixture);

        var selectors = vi.Descendants("Structure")
            .Single(s => (string?)s.Attribute("_name") == "Event Structure")
            .Elements("CaseFrame").Select(f => (string)f.Attribute("selector")!).ToList();
        Assert.Equal(5, selectors.Count);
        Assert.All(selectors, s => Assert.Contains("\\3A", s));
        Assert.Contains(" \"Card Simulator\"\\3A Value Change ", selectors);

        // and the graft's own reader finds every frame in that export
        var path = Path.Combine(Path.GetTempPath(), "scaffold-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            vi.Save(path);
            var reading = EventFrames.Read(path);
            Assert.Null(reading.Refusal);
            Assert.Equal(["Card Simulator", "Enter", "Left Buttons", "Right Buttons", "User Input"],
                         reading.Frames.Select(f => f.Control));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static string ArrayToCluster(string? elements, int fields) => $"""
        <VI _name="A2C.vi" description="Array To Cluster probe.">
          <Control _name="Items" type="array{"{"}double{"}"}" value="[]" outputs="value:4201.v" uid="4201" uid_parent="root"/>
          <Node _name="Array To Cluster" {(elements is null ? "" : $"elements=\"{elements}\" ")}inputs="array:4201.v" outputs="cluster:4202.c" uid="4202" uid_parent="root"/>
          <Indicator _name="Out" type="cluster{"{"}{string.Join(",", Enumerable.Range(0, fields).Select(i => $"double.f{i}"))}{"}"}" value="[{string.Join(",", Enumerable.Repeat("0", fields))}]" inputs="value:4202.c" uid="4203" uid_parent="root"/>
        </VI>
        """;

    /// <summary>
    /// The size NI's converter gives the output - measured on its builds of the same probes: 3
    /// fields with elements="3", 9 without - comes back through the reader as elements="N".
    /// </summary>
    [Theory]
    [InlineData("3", 3)]
    [InlineData("5", 5)]
    [InlineData(null, 9)]
    public async Task Array_To_Cluster_composes_its_size_and_reads_back_with_it(string? elements, int expected)
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("a2c.xml");
        var vi = server.TempPath("a2c.vi");
        await File.WriteAllTextAsync(xml, ArrayToCluster(elements, expected));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        var node = (await ReadAsync(vi)).Descendants("Node")
            .Single(n => (string?)n.Attribute("_name") == "Array To Cluster");
        Assert.Equal(expected.ToString(), (string?)node.Attribute("elements"));
    }

    [Fact]
    public async Task Array_To_Cluster_refuses_a_size_that_is_not_a_number()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("a2c-bad.xml");
        await File.WriteAllTextAsync(xml, ArrayToCluster("three", 3));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("a2c-bad.vi"), checkExecutable: false, backend: "pylabview");

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains("is not a number", Res.Str(composed, "errorMessage"));
        Assert.Contains("three", Res.Str(composed, "errorMessage"));
    }
}
