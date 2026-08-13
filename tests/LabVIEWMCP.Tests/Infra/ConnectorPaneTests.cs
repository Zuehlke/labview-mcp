using System.Text;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Slot geometry, and the two patterns that made this code necessary.
///
/// EVERY FIXTURE HERE IS A MEASUREMENT, not a model. 4815 is `Drop Message Core.vi` out of
/// vi.lib\ActorFramework and 4833 is `C:\Temp\DaqReadAndTDMS.vi`, both read through
/// `{LV.ConnectorPane}` -> `Terminal Bounds[]` on LabVIEW 2026. That matters because the whole class
/// of bug being fixed here came from a plausible model of the numbering that happened to be wrong on
/// one of the two patterns.
/// </summary>
public class ConnectorPaneGeometryTests
{
    /// <summary>
    /// The measured slots of pattern 4815, in conIdx order - a 32x32 pane, four columns.
    /// Left edge top to bottom is 11, 10, 9, 8; right edge 3, 2, 1, 0.
    /// </summary>
    internal static readonly (int L, int T, int R, int B)[] Pattern4815 =
    [
        (24, 24, 32, 32), (24, 16, 32, 24), (24, 8, 32, 16), (24, 0, 32, 8),
        (16, 16, 24, 32), (16, 0, 24, 16), (8, 16, 16, 32), (8, 0, 16, 16),
        (0, 24, 8, 32), (0, 16, 8, 24), (0, 8, 8, 16), (0, 0, 8, 8),
    ];

    /// <summary>
    /// The measured slots of pattern 4833. Nothing about this follows 4815: the corners come first
    /// (0 top left, 4 top right, 11 bottom left, 15 bottom right) and the edges then zig-zag.
    /// </summary>
    internal static readonly (int L, int T, int R, int B)[] Pattern4833 =
    [
        (0, 0, 7, 7), (7, 0, 13, 16), (13, 0, 19, 16), (19, 0, 25, 16),
        (25, 0, 32, 7), (0, 7, 7, 13), (25, 7, 32, 13), (0, 13, 7, 19),
        (25, 13, 32, 19), (0, 19, 7, 25), (25, 19, 32, 25), (0, 25, 7, 32),
        (7, 16, 13, 32), (13, 16, 19, 32), (19, 16, 25, 32), (25, 25, 32, 32),
    ];

    /// <summary>
    /// `Terminal Bounds[]` as `Flatten To XML` writes it. The shape is copied from a real payload -
    /// see <see cref="Parses_a_verbatim_LabVIEW_payload"/>, which pins it against one.
    /// </summary>
    internal static string BoundsXml(IReadOnlyList<(int L, int T, int R, int B)> slots)
    {
        var sb = new StringBuilder();
        sb.Append("<Array>\n<Name>TermBnds[]</Name>\n<Dimsize>").Append(slots.Count)
          .Append("</Dimsize>\n");

        foreach (var (left, top, right, bottom) in slots)
        {
            sb.Append("<Cluster>\n<Name>Rectangle</Name>\n<NumElts>4</NumElts>\n");
            foreach (var (name, value) in new[]
                     { ("Left", left), ("Top", top), ("Right", right), ("Bottom", bottom) })
                sb.Append("<I16>\n<Name>").Append(name).Append("</Name>\n<Val>").Append(value)
                  .Append("</Val>\n</I16>\n");
            sb.Append("</Cluster>\n");
        }

        return sb.Append("</Array>\n").ToString();
    }

    internal static ConnectorPane.Geometry Geometry(
        int pattern, IReadOnlyList<(int L, int T, int R, int B)> slots) =>
        ConnectorPane.ParseBounds(pattern, BoundsXml(slots))!;

    [Fact]
    public void Parses_a_verbatim_LabVIEW_payload()
    {
        // Copied out of a live lvpane_probe run, first two slots of DaqReadAndTDMS.vi, untouched.
        const string verbatim =
            "<Array>\n<Name>TermBnds[]</Name>\n<Dimsize>2</Dimsize>\n" +
            "<Cluster>\n<Name>Rectangle</Name>\n<NumElts>4</NumElts>\n" +
            "<I16>\n<Name>Left</Name>\n<Val>0</Val>\n</I16>\n" +
            "<I16>\n<Name>Top</Name>\n<Val>0</Val>\n</I16>\n" +
            "<I16>\n<Name>Right</Name>\n<Val>7</Val>\n</I16>\n" +
            "<I16>\n<Name>Bottom</Name>\n<Val>7</Val>\n</I16>\n</Cluster>\n" +
            "<Cluster>\n<Name>Rectangle</Name>\n<NumElts>4</NumElts>\n" +
            "<I16>\n<Name>Left</Name>\n<Val>7</Val>\n</I16>\n" +
            "<I16>\n<Name>Top</Name>\n<Val>0</Val>\n</I16>\n" +
            "<I16>\n<Name>Right</Name>\n<Val>13</Val>\n</I16>\n" +
            "<I16>\n<Name>Bottom</Name>\n<Val>16</Val>\n</I16>\n</Cluster>\n</Array>\n";

        var geometry = ConnectorPane.ParseBounds(4833, verbatim);

        Assert.NotNull(geometry);
        Assert.Equal(2, geometry.Terminals);
        Assert.Equal(new ConnectorPane.Slot(0, 0, 0, 7, 7), geometry.Slots[0]);
        Assert.Equal(new ConnectorPane.Slot(1, 7, 0, 13, 16), geometry.Slots[1]);
    }

    /// <summary>
    /// A documented trap in this format: Dimsize 0 is still followed by one child element carrying
    /// the element TYPE, so counting children reports a phantom slot. A pane with no terminals must
    /// come back empty, not with a slot at conIdx 0.
    /// </summary>
    [Fact]
    public void An_empty_array_has_no_slots_despite_its_template_element()
    {
        var geometry = ConnectorPane.ParseBounds(4800, BoundsXml([]) .Replace(
            "<Dimsize>0</Dimsize>\n",
            "<Dimsize>0</Dimsize>\n<Cluster>\n<Name>Rectangle</Name>\n<NumElts>4</NumElts>\n" +
            "<I16>\n<Name>Left</Name>\n<Val>0</Val>\n</I16>\n</Cluster>\n"));

        Assert.NotNull(geometry);
        Assert.Empty(geometry.Slots);
    }

    [Fact]
    public void Rejects_a_payload_that_is_not_the_bounds_array() =>
        Assert.Null(ConnectorPane.ParseBounds(4815, "<Cluster><Name>error out</Name></Cluster>"));

    [Fact]
    public void Rejects_unparsable_text() =>
        Assert.Null(ConnectorPane.ParseBounds(4815, "not xml at all"));

    [Fact]
    public void Classifies_4815_edges_top_to_bottom()
    {
        var geometry = Geometry(4815, Pattern4815);

        Assert.Equal([11, 10, 9, 8], geometry.LeftEdge.Select(s => s.ConIdx));
        Assert.Equal([3, 2, 1, 0], geometry.RightEdge.Select(s => s.ConIdx));
        Assert.Equal("4x2x2x4", geometry.ColumnProfile);
        Assert.Equal(12, geometry.Terminals);
    }

    [Fact]
    public void Classifies_4833_edges_top_to_bottom()
    {
        var geometry = Geometry(4833, Pattern4833);

        Assert.Equal([0, 5, 7, 9, 11], geometry.LeftEdge.Select(s => s.ConIdx));
        Assert.Equal([4, 6, 8, 10, 15], geometry.RightEdge.Select(s => s.ConIdx));
        Assert.Equal(16, geometry.Terminals);
    }

    /// <summary>
    /// The catalogue - i.e. the LabVIEW Wiki - calls 4833 "5x3x3x3x5", which sums to 19 while the
    /// same row says 16 terminals. Measured, the columns hold 5, 2, 2, 2 and 5.
    /// </summary>
    [Fact]
    public void Derives_4833s_column_profile_from_the_measurement_not_the_catalogue() =>
        Assert.Equal("5x2x2x2x5", Geometry(4833, Pattern4833).ColumnProfile);

    /// <summary>
    /// Pattern 4817's measured slots include one that spans two columns, `(16,11,32,21)`. It counts
    /// once, in the column it starts in, and - because it reaches x=32 - it belongs to the OUTPUT
    /// edge. Getting that wrong would put an output terminal in the middle and invent a warning.
    /// </summary>
    [Fact]
    public void A_slot_spanning_two_columns_still_belongs_to_the_edge_it_touches()
    {
        var geometry = Geometry(4817,
        [
            (24, 21, 32, 32), (16, 21, 24, 32), (16, 11, 32, 21), (24, 0, 32, 11),
            (16, 0, 24, 11), (0, 16, 16, 32), (0, 0, 16, 16),
        ]);

        Assert.Equal("2x3x2", geometry.ColumnProfile);
        Assert.Equal([6, 5], geometry.LeftEdge.Select(s => s.ConIdx));
        Assert.Equal([3, 2, 0], geometry.RightEdge.Select(s => s.ConIdx));
        Assert.Equal(6, geometry.FirstInput);
        Assert.Equal(5, geometry.ErrorIn);
        Assert.Equal(0, geometry.ErrorOut);
    }

    /// <summary>
    /// The four numbers a VI generator actually needs, and the reason a remembered map is useless:
    /// they have nothing in common between the two patterns.
    /// </summary>
    [Fact]
    public void Names_the_style_guide_slots_per_pattern()
    {
        var p4815 = Geometry(4815, Pattern4815);
        Assert.Equal(11, p4815.FirstInput);
        Assert.Equal(8, p4815.ErrorIn);
        Assert.Equal(3, p4815.FirstOutput);
        Assert.Equal(0, p4815.ErrorOut);

        var p4833 = Geometry(4833, Pattern4833);
        Assert.Equal(0, p4833.FirstInput);
        Assert.Equal(11, p4833.ErrorIn);
        Assert.Equal(4, p4833.FirstOutput);
        Assert.Equal(15, p4833.ErrorOut);
    }

    /// <summary>
    /// A single-column pane - pattern 4803 has three full-width slots - cannot express "inputs left,
    /// outputs right". Saying so is the point; picking an edge anyway would be the bug.
    /// </summary>
    [Fact]
    public void A_full_width_pane_cannot_express_the_style_guide()
    {
        var geometry = Geometry(4803, [(0, 0, 32, 11), (0, 11, 32, 22), (0, 22, 32, 32)]);

        Assert.False(geometry.CanExpressStyleGuide);
        Assert.Empty(geometry.LeftEdge);
        Assert.Equal(3, geometry.FullWidthSlots.Count);
        Assert.Contains("cannot", ConnectorPane.RenderRoles(geometry));
    }

    [Fact]
    public void Slots_round_trip_through_the_tsv_encoding()
    {
        var geometry = Geometry(4833, Pattern4833);
        var again = ConnectorPane.ParseSlots(4833, geometry.Encode());

        Assert.NotNull(again);
        Assert.Equal(geometry.Slots.OrderBy(s => s.ConIdx), again.Slots.OrderBy(s => s.ConIdx));
    }

    [Fact]
    public void An_unmeasured_slots_field_parses_as_nothing()
    {
        Assert.Null(ConnectorPane.ParseSlots(4806, "-"));
        Assert.Null(ConnectorPane.ParseSlots(4806, ""));
        Assert.Null(ConnectorPane.ParseSlots(4806, "0:1,2,3"));
    }
}

/// <summary>
/// The verdict and the correction, on the VI that caused all this.
///
/// `DaqReadAndTDMS.vi` was generated on 2026-08-13 with the assignment the repository's own
/// reference prescribed - first input 11, error in 8, first output 3, error out 0 - which is correct
/// on pattern 4815. It came out on 4833, where those numbers put two inputs on the OUTPUT edge and
/// `error out` in the top-left corner. Both states are pinned below.
/// </summary>
public class ConnectorPaneReviewTests
{
    private static ConnectorPane.Geometry Pane4833 =>
        ConnectorPaneGeometryTests.Geometry(4833, ConnectorPaneGeometryTests.Pattern4833);

    /// <summary>What was shipped and rejected on sight.</summary>
    private static readonly ConnectorPane.Terminal[] AsShipped =
    [
        new("AI Physical Channels", false, 11),
        new("TDMS File Path", false, 10),
        new("error in (no error)", false, 8),
        new("Waveforms", true, 3),
        new("error out", true, 0),
    ];

    /// <summary>What the measurement said it should be.</summary>
    private static readonly ConnectorPane.Terminal[] Corrected =
    [
        new("AI Physical Channels", false, 0),
        new("TDMS File Path", false, 5),
        new("error in (no error)", false, 11),
        new("Waveforms", true, 4),
        new("error out", true, 15),
    ];

    [Fact]
    public void Catches_inputs_on_the_output_edge()
    {
        var findings = ConnectorPane.Review(Pane4833, AsShipped);

        var offenders = findings
            .Where(f => f.Problem.Contains("INPUT sitting on the output edge"))
            .Select(f => f.Terminal)
            .ToList();

        Assert.Contains("TDMS File Path", offenders);
        Assert.Contains("error in (no error)", offenders);
    }

    [Fact]
    public void Catches_an_output_on_the_input_edge()
    {
        var findings = ConnectorPane.Review(Pane4833, AsShipped);

        Assert.Contains(findings, f =>
            f.Terminal == "error out" && f.Problem.Contains("OUTPUT sitting on the input edge"));
    }

    /// <summary>
    /// Three real breaches and one merely avoidable placement, which is exactly what the shipped VI
    /// had: `TDMS File Path` and `error in` on the output edge and `error out` on the input edge are
    /// violations, while `Waveforms` at conIdx 3 is on neither edge - conIdx 3 is a middle column on
    /// 4833 - so it is a warning. Separating the two is what keeps the report readable.
    /// </summary>
    [Fact]
    public void Grades_the_shipped_pane_as_three_violations_and_one_warning()
    {
        var findings = ConnectorPane.Review(Pane4833, AsShipped);

        Assert.Equal(3, findings.Count(f => f.Severity == "violation"));
        var warnings = findings.Where(f => f.Severity == "warning").ToList();
        Assert.Single(warnings);
        Assert.Equal("Waveforms", warnings[0].Terminal);
        // Violations first, so a caller reading top down fixes the real breaches first.
        Assert.Equal("violation", findings[0].Severity);
    }

    [Fact]
    public void The_corrected_pane_has_nothing_to_report() =>
        Assert.Empty(ConnectorPane.Review(Pane4833, Corrected));

    /// <summary>
    /// The same VI on the pattern its indices were written for. This is what makes the failure
    /// legible: the assignment is not sloppy, it is right on the other pattern.
    /// </summary>
    [Fact]
    public void The_shipped_assignment_would_have_been_clean_on_4815()
    {
        var pane = ConnectorPaneGeometryTests.Geometry(
            4815, ConnectorPaneGeometryTests.Pattern4815);

        Assert.Empty(ConnectorPane.Review(pane, AsShipped));
    }

    [Fact]
    public void Suggests_the_assignment_the_measurement_implies()
    {
        var suggestion = ConnectorPane.Suggest(Pane4833, AsShipped);

        Assert.Equal(0, suggestion["AI Physical Channels"]);
        Assert.Equal(5, suggestion["TDMS File Path"]);
        Assert.Equal(11, suggestion["error in (no error)"]);
        Assert.Equal(4, suggestion["Waveforms"]);
        Assert.Equal(15, suggestion["error out"]);
    }

    /// <summary>
    /// The corners are reserved before the edges are handed out. Without that, a VI with four inputs
    /// on a 4833 pane would fill the left edge top to bottom and push `error in` off the pane.
    /// </summary>
    [Fact]
    public void Error_terminals_keep_the_bottom_corners_even_when_the_edge_is_full()
    {
        ConnectorPane.Terminal[] many =
        [
            new("a", false, 0), new("b", false, 1), new("c", false, 2), new("d", false, 3),
            new("e", false, 4), new("error in", false, 5),
            new("out", true, 6), new("error out", true, 7),
        ];

        var suggestion = ConnectorPane.Suggest(Pane4833, many);

        Assert.Equal(11, suggestion["error in"]);
        Assert.Equal(15, suggestion["error out"]);
        Assert.Equal([0, 5, 7, 9], new[] { suggestion["a"], suggestion["b"], suggestion["c"], suggestion["d"] });
        // The fifth input has no left-edge slot left, so it lands in the middle - which the guide
        // tolerates - rather than displacing error in.
        Assert.Contains(suggestion["e"], new[] { 1, 2, 3, 12, 13, 14 });
    }

    [Fact]
    public void Catches_two_terminals_on_one_slot()
    {
        ConnectorPane.Terminal[] clash =
            [new("a", false, 0), new("b", false, 0), new("out", true, 4)];

        Assert.Contains(ConnectorPane.Review(Pane4833, clash),
            f => f.Problem.Contains("share conIdx 0"));
    }

    /// <summary>
    /// A clash is reported ONCE, naming both terminals, rather than twice from each side. Two
    /// inputs sharing a left-edge slot are each on the correct edge, so there is nothing else wrong
    /// with either of them and a second finding would only be noise.
    /// </summary>
    [Fact]
    public void A_clash_between_two_otherwise_correct_terminals_is_one_finding()
    {
        ConnectorPane.Terminal[] clash =
            [new("a", false, 0), new("b", false, 0), new("out", true, 4)];

        var finding = Assert.Single(ConnectorPane.Review(Pane4833, clash));

        Assert.Equal("a + b", finding.Terminal);
        Assert.Equal("give each terminal its own conIdx", finding.Fix);
    }

    /// <summary>
    /// More terminals than the pane can hold on the right edges: the ones that fit are told where to
    /// go, and the rest are told plainly that nothing is free rather than being given a slot that
    /// would displace something else.
    /// </summary>
    [Fact]
    public void Says_so_when_the_pane_cannot_hold_every_terminal_correctly()
    {
        var small = ConnectorPaneGeometryTests.Geometry(4801, [(0, 0, 16, 32), (16, 0, 32, 32)]);

        ConnectorPane.Terminal[] tooMany =
        [
            new("a", false, 0), new("b", false, 1), new("c", false, 0),
        ];

        var findings = ConnectorPane.Review(small, tooMany);

        Assert.Contains(findings, f => f.Fix.Contains("no slot is free"));
    }

    [Fact]
    public void Catches_a_conIdx_that_is_not_a_slot_on_this_pattern()
    {
        ConnectorPane.Terminal[] offEdge = [new("a", false, 31)];

        Assert.Contains(ConnectorPane.Review(Pane4833, offEdge),
            f => f.Problem.Contains("is not a slot on pattern 4833"));
    }

    /// <summary>
    /// A middle column is not wrong, only avoidable - and only while the terminal's own edge still
    /// has room. Reported as a warning so a caller can tell it apart from a real breach.
    /// </summary>
    [Fact]
    public void A_middle_slot_with_a_free_edge_is_only_a_warning()
    {
        ConnectorPane.Terminal[] middle = [new("a", false, 1), new("out", true, 4)];

        var findings = ConnectorPane.Review(Pane4833, middle);

        Assert.Single(findings);
        Assert.Equal("warning", findings[0].Severity);
        Assert.Equal("a", findings[0].Terminal);
    }

    [Fact]
    public void The_map_prints_terminal_names_when_it_has_them()
    {
        var rendered = ConnectorPane.RenderMap(Pane4833, Corrected);

        Assert.Contains("AI Physical Channels", rendered);
        Assert.Contains("error out", rendered);
    }
}

/// <summary>
/// The harvest: sweep output in, one row per pattern out, and an honest gap where nothing was
/// measured.
/// </summary>
public class ConnectorPanePatternsTests
{
    /// <summary>
    /// The record shape lvpane_sweep.vi actually writes: `pattern | path | xml`, with NO newline
    /// before the XML - the helper concatenates the three, so `&lt;Array&gt;` ends the first line.
    /// This fixture was originally written with a newline there, which is a shape LabVIEW never
    /// produces, and the harvest passed its tests while turning 213 measured VIs into 0 patterns.
    /// </summary>
    private static string Record(int pattern, string path,
        IReadOnlyList<(int L, int T, int R, int B)> slots) =>
        $"{pattern}|{path}|" + ConnectorPaneGeometryTests.BoundsXml(slots) + "@@@";

    private static string Sweep() =>
        Record(4815, @"C:\vi.lib\Drop Message Core.vi", ConnectorPaneGeometryTests.Pattern4815) +
        Record(4833, @"C:\Temp\DaqReadAndTDMS.vi", ConnectorPaneGeometryTests.Pattern4833) +
        // A VI that would not load. The sweep records it as pattern 0 rather than stopping.
        "0|C:\\vi.lib\\Broken.vi|<Array>\n<Name>TermBnds[]</Name>\n<Dimsize>0</Dimsize>\n</Array>\n@@@";

    [Fact]
    public void Harvests_one_row_per_measured_pattern_and_counts_the_failures()
    {
        var (tsv, patterns, measured, failed) =
            ConnectorPanePatterns.Harvest(Sweep(), "test provenance");

        Assert.Equal(2, patterns);
        Assert.Equal(2, measured);
        Assert.Equal(1, failed);
        Assert.Contains("test provenance", tsv);
        Assert.Contains("4815\t12\t4x2x2x4\t4x2x2x4\tmeasured", tsv);
        Assert.Contains("4833\t16\t5x2x2x2x5\t5x2x2x2x5\tmeasured", tsv);
        // seenVis and variants travel with the row, both 1 here.
        Assert.Contains("Drop Message Core.vi\t1\t1\t", tsv);
    }

    [Fact]
    public void Lists_every_catalogued_pattern_even_unmeasured_ones()
    {
        var (tsv, _, _, _) = ConnectorPanePatterns.Harvest(Sweep(), "test");
        var rows = ConnectorPanePatterns.Build(tsv);

        Assert.Equal(36, rows.Count);
        Assert.All(rows.Values, row => Assert.InRange(row.Pattern, 4800, 4835));
        Assert.True(rows[4815].Measured);
        Assert.False(rows[4806].Measured);
        Assert.Null(rows[4806].Geometry);
        // An unmeasured pattern still knows how big it is - that much is catalogue knowledge.
        Assert.Equal(4, rows[4806].Terminals);
    }

    [Fact]
    public void A_measured_row_carries_the_vi_it_was_measured_on()
    {
        var (tsv, _, _, _) = ConnectorPanePatterns.Harvest(Sweep(), "test");

        Assert.Equal("DaqReadAndTDMS.vi", ConnectorPanePatterns.Build(tsv)[4833].SampleVi);
    }

    [Fact]
    public void Two_vis_on_the_same_orientation_are_one_row()
    {
        var sweep = Record(4815, @"C:\first.vi", ConnectorPaneGeometryTests.Pattern4815) +
                    Record(4815, @"C:\second.vi", ConnectorPaneGeometryTests.Pattern4815);

        var (tsv, patterns, measured, _) = ConnectorPanePatterns.Harvest(sweep, "test");

        Assert.Equal(1, patterns);
        Assert.Equal(2, measured);

        var row = ConnectorPanePatterns.Build(tsv)[4815];
        Assert.Equal("first.vi", row.SampleVi);
        Assert.Equal(2, row.SeenVis);
        Assert.Equal(1, row.Variants);
        Assert.False(row.OrientationVaries);
    }

    /// <summary>
    /// A rotated pane, as it actually occurs: 4 of the 1 026 VIs measured on 4815 carry it turned on
    /// its side, and the same id then numbers its slots along other edges. The MAJORITY orientation
    /// has to win, because the alternative - keeping whichever VI came first - makes the table depend
    /// on the order of the sweep files. It did: harvesting the same six sweeps in a different order
    /// flipped 4829 from one orientation to the other, which is how this was found.
    /// </summary>
    [Fact]
    public void The_majority_orientation_wins_and_the_minority_is_counted()
    {
        // The real 4815 rotated a quarter turn: four-terminal groups become rows, not columns.
        (int L, int T, int R, int B)[] turned =
        [
            (0, 24, 8, 32), (8, 24, 16, 32), (16, 24, 24, 32), (24, 24, 32, 32),
            (0, 16, 16, 24), (16, 16, 32, 24), (0, 8, 16, 16), (16, 8, 32, 16),
            (0, 0, 8, 8), (8, 0, 16, 8), (16, 0, 24, 8), (24, 0, 32, 8),
        ];

        // The minority comes FIRST in the sweep, so first-wins would have picked it.
        var sweep = Record(4815, @"C:\turned.vi", turned) +
                    Record(4815, @"C:\upright1.vi", ConnectorPaneGeometryTests.Pattern4815) +
                    Record(4815, @"C:\upright2.vi", ConnectorPaneGeometryTests.Pattern4815);

        var row = ConnectorPanePatterns.Build(
            ConnectorPanePatterns.Harvest(sweep, "test").Tsv)[4815];

        Assert.Equal("upright1.vi", row.SampleVi);
        Assert.Equal(11, row.Geometry!.LeftEdge[0].ConIdx);   // upright: left edge top is 11
        Assert.Equal(3, row.SeenVis);
        Assert.Equal(2, row.Variants);
        Assert.True(row.OrientationVaries);
    }

    /// <summary>
    /// Same sweeps, different order, same answer. This is the property first-wins did not have.
    /// </summary>
    [Fact]
    public void The_harvest_does_not_depend_on_the_order_of_the_records()
    {
        var a = Record(4833, @"C:\a.vi", ConnectorPaneGeometryTests.Pattern4833);
        var b = Record(4815, @"C:\b.vi", ConnectorPaneGeometryTests.Pattern4815);
        var c = Record(4815, @"C:\c.vi", ConnectorPaneGeometryTests.Pattern4815);

        var forwards = ConnectorPanePatterns.Harvest(a + b + c, "test").Tsv;
        var backwards = ConnectorPanePatterns.Harvest(c + b + a, "test").Tsv;

        Assert.Equal(
            ConnectorPanePatterns.Build(forwards)[4815].Geometry!.Encode(),
            ConnectorPanePatterns.Build(backwards)[4815].Geometry!.Encode());
    }

    /// <summary>
    /// A row this build cannot read is skipped, not thrown. The alternative is a server that will
    /// not start because a data file it can work without is stale.
    /// </summary>
    [Fact]
    public void A_malformed_row_is_skipped_rather_than_fatal()
    {
        var rows = ConnectorPanePatterns.Build(
            "pattern\tterminals\tdesignation\tsource\tsampleVi\tslots\n" +
            "4815\t12\t4x2x2x4\tmeasured\tx.vi\tnonsense\n" +
            "not-a-number\t1\t1\tmeasured\ty.vi\t0:0,0,8,8\n");

        Assert.Equal(36, rows.Count);
        Assert.False(rows[4815].Measured);
    }

    /// <summary>
    /// The catalogue must stay complete: the tool's promise is that every pattern is listed, so a
    /// gap here would be a silent hole in the answer.
    /// </summary>
    [Fact]
    public void The_catalogue_covers_4800_to_4835_with_no_gaps()
    {
        var ids = ConnectorPanePatterns.Catalogue.Select(entry => entry.Pattern).ToList();

        Assert.Equal(36, ids.Count);
        Assert.Equal(Enumerable.Range(4800, 36), ids.OrderBy(id => id));
    }

    /// <summary>
    /// Every catalogued shape string must sum to the terminal count it is listed with. This is the
    /// test that caught the wiki's 4833 row - 5+3+3+3+5 against 16 terminals - and it is here so a
    /// future edit cannot reintroduce it.
    /// </summary>
    [Fact]
    public void Every_catalogued_shape_sums_to_its_terminal_count()
    {
        foreach (var (pattern, terminals, shape) in ConnectorPanePatterns.Catalogue)
        {
            var sum = shape.Split('x').Sum(int.Parse);
            Assert.True(sum == terminals,
                $"pattern {pattern}: shape {shape} sums to {sum}, " +
                $"but the row says {terminals} terminals");
        }
    }

    /// <summary>
    /// The table that actually ships. A harvest is a generated file, so the thing worth testing is
    /// not its contents but its integrity: every measured pattern must have one slot per terminal,
    /// numbered 0..n-1 with no gaps and no duplicates, and every rectangle must be a rectangle. A
    /// bad sweep - one that half-parsed, say - fails here rather than in a VI six months from now.
    /// </summary>
    [Fact]
    public void The_shipped_table_is_internally_consistent()
    {
        var measured = ConnectorPanePatterns.All().Values.Where(r => r.Measured).ToList();

        Assert.NotEmpty(measured);

        foreach (var row in measured)
        {
            var slots = row.Geometry!.Slots;
            Assert.Equal(row.Terminals, slots.Count);
            Assert.Equal(Enumerable.Range(0, slots.Count), slots.Select(s => s.ConIdx).Order());
            Assert.All(slots, slot =>
            {
                Assert.True(slot.Right > slot.Left, $"pattern {row.Pattern}: slot {slot.ConIdx} has no width");
                Assert.True(slot.Bottom > slot.Top, $"pattern {row.Pattern}: slot {slot.ConIdx} has no height");
            });
        }
    }

    /// <summary>
    /// The measured column profile must sum to the terminal count as well - the same invariant the
    /// catalogue is held to, applied to the half that came from a machine.
    /// </summary>
    [Fact]
    public void Every_measured_column_profile_sums_to_its_terminal_count()
    {
        foreach (var row in ConnectorPanePatterns.All().Values.Where(r => r.Measured))
            Assert.Equal(row.Terminals, row.Shape.Split('x').Sum(int.Parse));
    }

    /// <summary>
    /// A measured row must keep the catalogue's shape string beside the measured profile rather than
    /// overwrite it: they are different notations, and the tool prints both when they differ.
    /// </summary>
    [Fact]
    public void A_measured_row_keeps_the_catalogue_shape_beside_the_measured_columns()
    {
        var sweep = Record(4817, @"C:\x.vi",
        [
            (24, 21, 32, 32), (16, 21, 24, 32), (16, 11, 32, 21), (24, 0, 32, 11),
            (16, 0, 24, 11), (0, 16, 16, 32), (0, 0, 16, 16),
        ]);

        var row = ConnectorPanePatterns.Build(
            ConnectorPanePatterns.Harvest(sweep, "test").Tsv)[4817];

        Assert.Equal("2x3x2", row.Shape);
        Assert.Equal("3x2x2", row.CatalogueShape);
    }
}
