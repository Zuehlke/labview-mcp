using LabVIEWMcp.Layout;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;
using Xunit.Abstractions;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// A dense flat diagram: MP3 Synthesize.vi (C:\Temp\MP3_ugly, 2026-11-04), 70 primitives on one
/// level, with no subVI of its own and no error chain. The user's review of our layout: "genau so,
/// wie es nicht sein dürfte". The fixture is the VI as the user handed it over; our layout plans it
/// to 2526 x 568 px, as the live run laid it out. Such a diagram is also given to LabVIEW's Clean
/// Up Diagram, and the layout that crosses fewer data wires is kept (docs/diagram-layout.md, 6p).
/// </summary>
public class LayoutDenseTests(ITestOutputHelper output)
{
    private const string Fixture = "mp3-synthesize-original.txt";

    /// <summary>The wires of the five numeric constants fanned out to 4 to 12 terminals each,
    /// across the whole diagram (dump wire indices).</summary>
    private static readonly int[] FanWires = [13, 20, 22, 25, 28];

    private static DiagramModel Load(string name, IEnumerable<int>? dropWires = null)
    {
        var drop = (dropWires ?? []).Select(i => $"W|0|{i}|").ToList();
        return DiagramModel.Parse(File.ReadAllLines(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "layout", name))
                                      .Where(l => !drop.Any(l.StartsWith)).ToArray());
    }

    /// <summary>
    /// WHERE THE CROSSINGS COME FROM. Measured 2026-11-04: 522 data-wire crossings planned, 94
    /// without the five constants' fans - more than four in five crossings are those five wires.
    /// Ordering each column's nodes by their neighbours (barycentre sweeps, one to eight, each
    /// plan measured) took not one crossing off; the order is not the cause.
    /// </summary>
    [Fact]
    public void Five_fanned_constants_make_most_of_the_crossings()
    {
        var model = Load(Fixture);
        var all = DiagramLayout.DataCrossings(model, DiagramLayout.Plan(model));
        var thin = Load(Fixture, FanWires);
        var withoutFans = DiagramLayout.DataCrossings(thin, DiagramLayout.Plan(thin));
        output.WriteLine($"{all} crossings, {withoutFans} without the fans");
        Assert.True(all - withoutFans > all * 3 / 4, $"the fans make {all - withoutFans} of {all}");
    }

    /// <summary>The two fixtures our plan crosses at least one data wire per data wire: MP3
    /// Synthesize (522 on 127) and ATM's Update Panel (48 on 25).</summary>
    [Theory]
    [InlineData("mp3-synthesize-original.txt")]
    [InlineData("atm-update-panel-original.txt")]
    public void A_dense_plan_is_too_dense_for_our_engine_alone(string fixture)
    {
        var model = Load(fixture);
        Assert.True(DiagramLayout.TooDense(model, DiagramLayout.Plan(model), out var crossings, out var wires),
                    $"{crossings} crossings on {wires} wires");
    }

    /// <summary>Every other fixture stays with our engine without asking NI - above all the DQMH
    /// Main.vis, whose layout NI's clean-up spreads over 3805 x 1692 px. The highest of them,
    /// LayoutCheck's Main, plans 13 crossings on 17 wires.</summary>
    [Theory]
    [InlineData("atm-main-original.txt")] [InlineData("atm-test-session-start-original.txt")]
    [InlineData("dqmh-bank-main-original.txt")] [InlineData("dqmh-bank-stop-module-original.txt")]
    [InlineData("dqmh-main-original.txt")] [InlineData("dqmh-main-ni-cleanup.txt")] [InlineData("dqmh-bank-main-ours.txt")]
    [InlineData("find-account-ours.txt")] [InlineData("layoutcheck-evaluate-raw.txt")]
    [InlineData("layoutcheck-grpc-format-report.txt")] [InlineData("layoutcheck-grpc-release.txt")]
    [InlineData("layoutcheck-main-raw.txt")] [InlineData("layoutcheck-send-block-raw.txt")]
    [InlineData("layoutcheck-write-samples-raw.txt")] [InlineData("read-csv-baseline-ours.txt")]
    [InlineData("test-find-account-ours.txt")] [InlineData("test-heater-accessors-grpc.txt")]
    [InlineData("test-heater-methods-grpc.txt")] [InlineData("test-heater1-generated.txt")]
    [InlineData("test-heater3-generated.txt")] [InlineData("test-pump-eventlog-four-asserts.txt")]
    [InlineData("test-sensor-generated.txt")] [InlineData("test-tdms-logger-methods-ours.txt")]
    public void Every_other_fixture_stays_with_our_engine(string fixture)
    {
        var model = Load(fixture);
        Assert.False(DiagramLayout.TooDense(model, DiagramLayout.Plan(model), out var crossings, out var wires),
                     $"{crossings} crossings on {wires} wires");
    }

    /// <summary>
    /// The user's verdicts on the MP3 VIs (2026-11-04), our result and NI's Clean Up both measured
    /// from the saved files with lvbd_wire_metrics.py: wires, bends, backward wires. NI was preferred
    /// for Parse Frames and Granule Info, ours for the rest - Huffman Decode by name, Read
    /// Scalefactors and Huffman Symbol "both good", the others "keep ours".
    /// </summary>
    [Theory]
    [InlineData("MP3 Parse Frames", 139, 484, 8, 254, 0, true)]
    [InlineData("MP3 Granule Info", 92, 300, 3, 203, 0, true)]
    [InlineData("MP3 Huffman Decode", 95, 263, 2, 167, 0, false)]
    [InlineData("MP3 Read Scalefactors", 51, 116, 0, 67, 0, false)]
    [InlineData("MP3 Huffman Symbol", 39, 86, 1, 43, 0, false)]
    [InlineData("MP3 Read Bits", 42, 77, 0, 43, 0, false)]
    [InlineData("MP3 Decode Channel", 32, 81, 1, 55, 0, false)]
    [InlineData("MP3 Decode", 29, 52, 1, 35, 0, false)]
    [InlineData("MP3 Spectrum To Bands", 35, 36, 1, 25, 0, false)]
    [InlineData("MP3 Read Fields", 10, 14, 0, 6, 0, false)]
    [InlineData("MP3 Huffman Value", 19, 42, 0, 38, 0, false)]
    public void A_tangled_result_of_ours_goes_to_NI_as_the_user_judged(string vi, int wires, int bends, int backward,
                                                                       int niBends, int niBackward, bool takeNi)
    {
        var ours = new DiagramCleanup.WireMetrics(wires, bends, 0, backward, 0);
        var ni = new DiagramCleanup.WireMetrics(wires, niBends, 0, niBackward, 0);
        var picked = DiagramLayoutTools.Tangled(ours) && DiagramLayoutTools.KeepNiMeasured(ours, ni);
        Assert.True(picked == takeNi, $"{vi}: {ours.BendsPerWire:F2} bends a wire, {backward} backward");
    }

    [Fact]
    public void NI_replaces_a_tangled_result_only_with_better_drawn_wires()
    {
        var ours = new DiagramCleanup.WireMetrics(100, 400, 0, 5, 0);
        Assert.True(DiagramLayoutTools.KeepNiMeasured(ours, new(100, 300, 0, 0, 0)));
        Assert.False(DiagramLayoutTools.KeepNiMeasured(ours, new(100, 400, 0, 0, 0)));   // no fewer bends
        Assert.False(DiagramLayoutTools.KeepNiMeasured(ours, new(100, 300, 0, 6, 0)));   // more backward
        Assert.False(DiagramLayoutTools.KeepNiMeasured(ours, null));                      // not measured
    }

    [Fact]
    public void The_metrics_script_line_is_read()
    {
        var m = DiagramCleanup.ParseWireMetrics(
            "{\"vi\": \"x.vi\", \"wires\": 92, \"bends\": 203, \"length\": 63016, \"backward\": 0, \"undecoded\": 0}");
        Assert.Equal(new DiagramCleanup.WireMetrics(92, 203, 63016, 0, 0), m);
        Assert.Null(DiagramCleanup.ParseWireMetrics("{\"vi\": \"x.vi\", \"error\": \"no diagram heap\"}"));
    }

    [Theory]
    [InlineData(100, 522, true)]
    [InlineData(522, 522, false)]   // a tie keeps ours
    [InlineData(600, 522, false)]
    [InlineData(null, 522, false)]  // NI did not run or could not be read back
    public void NI_is_kept_only_when_it_crosses_fewer_wires(int? ni, int ours, bool keep) =>
        Assert.Equal(keep, DiagramLayoutTools.KeepNi(ni, ours));
}
