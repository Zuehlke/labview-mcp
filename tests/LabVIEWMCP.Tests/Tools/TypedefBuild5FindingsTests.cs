using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The findings of the fifth TypedefAfterGDevCon build (docs/cold-build-typedef-gdevcon.md §9),
/// offline: the runner's pane without pylabview, `error in` on the generated tests' own pane, and
/// the class test's summary and accessor listing. Each with a control.
/// </summary>
public sealed class TypedefBuild5FindingsTests
{
    private static string? Slot(XElement vi, string element, string name) => (string?)vi.Elements(element)
        .Single(e => (string?)e.Attribute("_name") == name).Attribute("conIdx");

    // ------------------------------------------------------------------ 1. the runner's pane

    [Theory]
    [InlineData(4815)]
    [InlineData(4833)]
    public void TheRunnerIsAuthoredOnTheGivenPatternsOwnBottomRow(int pattern)
    {
        var geometry = ConnectorPanePatterns.Find(pattern)!.Geometry!;
        var vi = XElement.Parse(TestTools.CarayaRunnerAixml(
            @"C:\t\Run.vi", ["Test A.vi"], "Run-TestReport.xml", geometry));

        Assert.Equal(geometry.ErrorIn.ToString(), Slot(vi, "Control", "error in"));
        Assert.Equal(geometry.ErrorOut.ToString(), Slot(vi, "Indicator", "error out"));
        Assert.Equal(geometry.FirstOutput.ToString(), Slot(vi, "Indicator", "Report Path used"));
    }

    [Fact]
    public void The4815AndThe4833NumbersDiffer()
    {
        // control: the two patterns really put the corners at different conIdx, so the test above
        // would catch a runner that hard-coded one of them
        var small = ConnectorPanePatterns.Find(4815)!.Geometry!;
        var large = ConnectorPanePatterns.Find(4833)!.Geometry!;
        Assert.Equal((8, 0), (small.ErrorIn, small.ErrorOut));
        Assert.NotEqual((small.ErrorIn, small.ErrorOut), (large.ErrorIn, large.ErrorOut));
    }

    // ------------------------------------------------------------------ 2. `error in` on our pane

    private static TestTools.ClassCase Case(int slot, string field, bool defaultOnly = false) =>
        new(slot, field, "double", "1", $"{field} case",
            $@"C:\c\Write {field}.vi", $@"C:\c\Read {field}.vi", @"C:\c\Channel.lvclass", defaultOnly);

    [Fact]
    public void TheClassTestsOwnInputIsErrorInWhileCarayasTerminalKeepsItsName()
    {
        var vi = XElement.Parse(TestTools.ClassTestAixml(@"C:\c\Test Channel.vi", "Channel", [Case(1, "Gain")]));
        var errorIn = vi.Elements("Control").Single(c => ((string?)c.Attribute("_name"))!.StartsWith("error in"));
        Assert.Equal("error in", (string?)errorIn.Attribute("_name"));

        // control: the CALLEE's terminal is Caraya's and keeps NI's spelling
        Assert.Contains(vi.Elements("Call"), c =>
            ((string?)c.Attribute("inputs"))!.Contains($"error in (no error):{errorIn.Attribute("uid")!.Value}.value"));
    }

    [Fact]
    public void TheMethodTestsOwnInputIsErrorIn()
    {
        var test = new MethodTestTools.MethodCase(1, "reads", "Read Value", @"C:\c\Read Value.vi",
            null, null, null, null, null, null, null, @"C:\c\Sensor.lvclass", [],
            "reading", "6", "double", 2, null, []);
        var vi = XElement.Parse(MethodTestTools.MethodTestAixml(@"C:\c\T.vi", "Sensor", [test]));
        Assert.Contains(vi.Elements("Control"), c => (string?)c.Attribute("_name") == "error in");
        Assert.DoesNotContain(vi.Elements("Control"), c => (string?)c.Attribute("_name") == "error in (no error)");
    }

    // ------------------------------------------------------------------ 4. the class test's answer

    [Fact]
    public void TheSummaryCountsADefaultCaseAsADefaultCase()
    {
        Assert.Equal("2 round trip(s) and 1 default case(s)",
            TestTools.CaseSummary([Case(1, "Config"), Case(2, "Gain"), Case(3, "Gain", defaultOnly: true)]));
        // control: a suite of round trips alone reads as before
        Assert.Equal("2 round trip(s)", TestTools.CaseSummary([Case(1, "Config"), Case(2, "Gain")]));
    }
}
