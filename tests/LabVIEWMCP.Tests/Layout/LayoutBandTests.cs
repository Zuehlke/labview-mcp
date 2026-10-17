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

    /// <summary>The error wires too run forward: Merge Errors and error out come after the last
    /// band column, where the first Merge Errors of Test Heater.vi had sat among the round-trip
    /// assertions and LabVIEW routed two error wires back round it.</summary>
    [Theory]
    [MemberData(nameof(GeneratedTests))]
    public void No_wire_of_a_banded_test_runs_backwards(string fixture)
    {
        var model = Load(fixture);
        var plan = DiagramLayout.Plan(model);
        var backward = DiagramLayout.BackwardWires(model, plan);
        foreach (var w in backward) output.WriteLine(w);
        Assert.Empty(backward);
    }

    /// <summary>The grpc twin's two tests as they came out of the generators (2026-10-17). The
    /// method test tied with columns at 0 crossings and kept them, both Reads stacked in one
    /// column; the shorter vertical run now decides that tie for chains fed by constants.</summary>
    [Theory]
    [InlineData("test-heater-methods-grpc.txt")]
    [InlineData("test-heater-accessors-grpc.txt")]
    public void A_test_built_on_grpc_is_laid_out_in_bands(string fixture)
    {
        var columns = DiagramLayout.Plan(Load(fixture), bands: false);
        var model = Load(fixture);
        var plan = DiagramLayout.Plan(model);
        Assert.Equal(0, DiagramLayout.DataCrossings(model, plan));
        Assert.Empty(DiagramLayout.BackwardWires(model, plan));
        Assert.NotEqual(columns.Moves.Select(m => (m.ApplyPath, m.Index, m.X, m.Y)),
                        plan.Moves.Select(m => (m.ApplyPath, m.Index, m.X, m.Y)));
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
