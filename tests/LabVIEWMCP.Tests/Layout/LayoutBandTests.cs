using LabVIEWMcp.Layout;
using LabVIEWMcp.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// Independent chains as bands - the generated Caraya tests of the AFDemo builds (2026-10-17), whose
/// cases share nothing but the error wire. The test-*.txt fixtures are dumps of three generated
/// test VIs as our layout had left them, every case's wires across every other's.
/// </summary>
public class LayoutBandTests(ITestOutputHelper output)
{
    private static DiagramModel Load(string name) =>
        DiagramModel.Parse(File.ReadAllLines(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "layout", name)));

    public static TheoryData<string> GeneratedTests => new()
    {
        "test-heater3-generated.txt", "test-heater1-generated.txt", "test-sensor-generated.txt",
    };

    public static TheoryData<string> Others => new()
    {
        "atm-test-session-start-original.txt", "atm-main-original.txt", "atm-update-panel-original.txt",
        "dqmh-main-original.txt", "layoutcheck-main-raw.txt", "layoutcheck-evaluate-raw.txt",
        "layoutcheck-send-block-raw.txt", "layoutcheck-write-samples-raw.txt",
        "layoutcheck-grpc-format-report.txt", "layoutcheck-grpc-release.txt",
    };

    /// <summary>Measured 2026-10-17: 8, 7 and 5 crossings in columns, 0 in bands, none wider.</summary>
    [Theory]
    [MemberData(nameof(GeneratedTests))]
    public void A_generated_test_is_laid_out_in_bands_with_no_crossing(string fixture)
    {
        var columns = DiagramLayout.Plan(Load(fixture), bands: false);
        var model = Load(fixture);
        var bands = DiagramLayout.Plan(model);
        var before = DiagramLayout.DataCrossings(Load(fixture), columns);
        var after = DiagramLayout.DataCrossings(model, bands);
        output.WriteLine($"{fixture}: {before} -> {after} crossings, {columns.PredictedTop.W} -> {bands.PredictedTop.W} px wide");
        Assert.True(before > 0, "the fixture shows the defect");
        Assert.Equal(0, after);
        Assert.True(bands.PredictedTop.W <= columns.PredictedTop.W, "and it is no wider");
    }

    /// <summary>Every other fixture keeps exactly the plan it had: bands are tried only on the top
    /// level, and kept only where they cross fewer wires.</summary>
    [Theory]
    [MemberData(nameof(Others))]
    public void Any_other_diagram_keeps_its_plan(string fixture)
    {
        var columns = DiagramLayout.Plan(Load(fixture), bands: false);
        var now = DiagramLayout.Plan(Load(fixture));
        Assert.Equal(columns.PredictedTop, now.PredictedTop);
        Assert.Equal(columns.Moves.Select(m => (m.ApplyPath, m.Index, m.X, m.Y)),
                     now.Moves.Select(m => (m.ApplyPath, m.Index, m.X, m.Y)));
    }
}
