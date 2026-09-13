using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The row-type check. EVERY FIXTURE BELOW IS A REAL LabVIEW 2026 EXPORT LINE, copied verbatim
/// from four files: two generated VIs and the same two after a human selected the unnamed row in
/// the IDE and saved. Measured 2026-09-13. The point of using the real pairs rather than invented
/// shapes is that `fields=` is CORRECT in all four - a fixture written to match the expectation
/// would have agreed with the defect, which is how three tools here shipped broken.
/// </summary>
public sealed class EventDataFieldToolsTests
{
    private const string BrokenInt32 =
        """<Node _name="Event Data Node" fields="Source,Tick,Time" outputs="Source:,element:4334.element,Time:" uid="4334" uid_parent="4330"/>""";

    private const string FixedInt32 =
        """<Node _name="Event Data Node" fields="Source,Tick,Time" outputs="Source:,Tick:4334.Tick,Time:" uid="4334" uid_parent="4330"/>""";

    private const string BrokenDouble =
        """<Node _name="Event Data Node" fields="Source,Level,Time" outputs="Source:,element:4244.element,Time:" uid="4244" uid_parent="4240"/>""";

    private const string FixedDouble =
        """<Node _name="Event Data Node" fields="Source,Level,Time" outputs="Source:,Level:4244.Level,Time:" uid="4244" uid_parent="4240"/>""";

    /// <summary>A frame LabVIEW created itself, carrying only the common fields - always clean.</summary>
    private const string StopFrameNode =
        """<Node _name="Event Data Node" fields="Source,Type,Time" outputs="Source:,Type:,Time:" uid="4355" uid_parent="4350"/>""";

    [Theory]
    [InlineData(BrokenInt32, "Tick", "element")]
    [InlineData(BrokenDouble, "Level", "element")]
    public void ARowThatKeptItsTypeIsNamed(string export, string field, string terminal)
    {
        var bad = Assert.Single(EventDataFieldTools.UnrepointedRows(export));

        Assert.Contains(field, bad);
        Assert.Contains(terminal, bad);
    }

    [Theory]
    [InlineData(FixedInt32)]
    [InlineData(FixedDouble)]
    [InlineData(StopFrameNode)]
    public void ARowWhoseTerminalMatchesItsFieldIsClean(string export) =>
        Assert.Empty(EventDataFieldTools.UnrepointedRows(export));

    /// <summary>
    /// The defect and its repair differ ONLY in `outputs`. If a future check is ever tempted back
    /// onto `fields`, this is the fact that refutes it.
    /// </summary>
    [Fact]
    public void FieldsIsIdenticalAcrossTheDefectAndItsRepair()
    {
        const string same = "fields=\"Source,Tick,Time\"";
        Assert.Contains(same, BrokenInt32);
        Assert.Contains(same, FixedInt32);
        Assert.NotEmpty(EventDataFieldTools.UnrepointedRows(BrokenInt32));
        Assert.Empty(EventDataFieldTools.UnrepointedRows(FixedInt32));
    }

    /// <summary>A whole export carries several nodes; only the bad row may be named.</summary>
    [Fact]
    public void OnlyTheBadRowIsReportedInAWholeDocument()
    {
        var doc = $"<VI _name=\"x.vi\" description=\"y\">{BrokenInt32}{StopFrameNode}</VI>";

        var bad = Assert.Single(EventDataFieldTools.UnrepointedRows(doc));

        Assert.Contains("uid 4334", bad);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<VI _name=\"x.vi\" description=\"y\"/>")]
    public void NothingToReadIsNotAFinding(string? export) =>
        Assert.Empty(EventDataFieldTools.UnrepointedRows(export));
}
