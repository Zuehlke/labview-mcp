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

    /// <summary>
    /// A case whose assertion checks an ERROR CODE is one band too. IfaceUE_TDMS3's method test
    /// (2026-10-18): `Open`'s error out feeds `Unbundle By Name` -> `code` -> the Assert, and that
    /// wire counted as part of the error chain, so the case split in two - class constant and `Open`
    /// at the bottom, unbundle and Assert at the top, 520 px apart. Pairs are (class constant,
    /// Assert) by object index in the dump.
    /// </summary>
    [Theory]
    [InlineData(34, 10)]   // Open creates file without error (error code)
    [InlineData(29, 6)]    // Open stores File Path
    [InlineData(23, 4)]    // Group Name survives Close
    [InlineData(23, 5)]    // Group Name survives Close (error code)
    public void A_case_that_asserts_an_error_code_stays_in_one_band(int constant, int assert)
    {
        var model = Load("test-tdms-logger-methods-ours.txt");
        var plan = DiagramLayout.Plan(model);
        foreach (var o in model.Top.Objects.Where(o => !o.IsText && plan.Predicted.ContainsKey(o.Key)))
            output.WriteLine($"{o.Index,3} {o.Class,-24} {plan.Predicted[o.Key]}");
        var c = plan.Predicted[$"0#{constant}"];
        var a = plan.Predicted[$"0#{assert}"];
        output.WriteLine($"constant {c}, assert {a}");
        Assert.True(Math.Abs((c.T + c.B) / 2 - (a.T + a.B) / 2) <= 120,
            $"the class constant ({c}) sits in the band of its Assert ({a})");
        Assert.Equal(0, DiagramLayout.DataCrossings(model, plan));
        Assert.Empty(DiagramLayout.BackwardWires(model, plan));
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
