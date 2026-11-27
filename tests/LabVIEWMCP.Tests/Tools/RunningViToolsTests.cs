// =============================================================================
// RunningViToolsTests.cs - lvai_running_vi's parts that need no LabVIEW: the action
// check, control names one per line, and the error code read from the helper.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class RunningViToolsTests
{
    [Fact]
    public async Task An_unknown_action_is_refused_before_LabVIEW_is_asked()
    {
        var answer = JsonNode.Parse(await RunningViTools.RunningViAsync(@"C:\nowhere\X.vi", "launch"))!;
        Assert.Equal("badArguments", answer["errorKind"]!.GetValue<string>());
        Assert.Contains("start, read, set, press, signal, stop", answer["error"]!.GetValue<string>());
    }

    /// <summary>A label may hold a comma, so names split on lines only.</summary>
    [Fact]
    public void Control_names_are_one_per_line() =>
        Assert.Equal(["Gain, dB", "stop"], RunningViTools.Lines("Gain, dB\r\n\n  stop  \n").ToArray());

    /// <summary>The helper's error cluster, as Flatten To XML writes it.</summary>
    [Theory]
    [InlineData("<Cluster><Name>error out</Name><NumElts>3</NumElts><Boolean><Name>status</Name><Val>1</Val></Boolean>" +
                "<I32><Name>code</Name><Val>1193</Val></I32><String><Name>source</Name><Val>Property Node</Val></String></Cluster>", 1193)]
    [InlineData("<Cluster><Name>error out</Name><NumElts>3</NumElts><Boolean><Name>status</Name><Val>0</Val></Boolean>" +
                "<I32><Name>code</Name><Val>0</Val></I32><String><Name>source</Name><Val></Val></String></Cluster>", 0)]
    [InlineData("", null)]
    public void The_helper_error_code_is_read_from_its_flattened_cluster(string xml, int? code) =>
        Assert.Equal(code, RunningViTools.ErrorCode(xml));
}
