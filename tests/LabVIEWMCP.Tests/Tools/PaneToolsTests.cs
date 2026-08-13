using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Reading the probe helper's answer. The pattern arrives as a STRING, because
/// `lvai_run_vi_and_read_values` renders every indicator as text - so "0" and "" and "not a number"
/// all have to be told apart from a real measurement.
/// </summary>
public class PaneToolsMeasurementTests
{
    private static string Runner(string? pattern, string? bounds) =>
        new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject
            {
                ["pattern"] = pattern is null
                    ? null
                    : new JsonObject { ["type"] = "String", ["value"] = pattern },
                ["bounds"] = bounds is null
                    ? null
                    : new JsonObject { ["type"] = "String", ["value"] = bounds },
            },
        }.ToJsonString();

    [Fact]
    public void Reads_the_pattern_and_the_bounds()
    {
        var measurement = PaneTools.Measurement(Runner("4833", "<Array>...</Array>"));

        Assert.NotNull(measurement);
        Assert.Equal(4833, measurement.Value.Pattern);
        Assert.Equal("<Array>...</Array>", measurement.Value.Bounds);
    }

    [Fact]
    public void A_missing_pattern_is_not_a_measurement() =>
        Assert.Null(PaneTools.Measurement(Runner(null, "<Array/>")));

    [Fact]
    public void A_missing_bounds_array_is_not_a_measurement() =>
        Assert.Null(PaneTools.Measurement(Runner("4833", null)));

    [Fact]
    public void A_non_numeric_pattern_is_not_a_measurement() =>
        Assert.Null(PaneTools.Measurement(Runner("", "<Array/>")));

    /// <summary>A guard failure carries ok:false, and must not be read as a pane.</summary>
    [Fact]
    public void A_failed_call_is_not_a_measurement() =>
        Assert.Null(PaneTools.Measurement(
            new JsonObject { ["ok"] = false, ["error"] = "no LabVIEW" }.ToJsonString()));

    [Fact]
    public void Nonsense_is_not_a_measurement() => Assert.Null(PaneTools.Measurement("<html>"));
}

/// <summary>
/// The whole answer for one VI. Pinned on `DaqReadAndTDMS.vi` in both states, because the point of
/// the tool is that it would have caught what shipped.
/// </summary>
public class PaneToolsRenderTests
{
    private const string Vi = @"C:\Temp\DaqReadAndTDMS.vi";

    private static string Bounds4833 =>
        ConnectorPaneGeometryTests.BoundsXml(ConnectorPaneGeometryTests.Pattern4833);

    private static readonly ConnectorPane.Terminal[] AsShipped =
    [
        new("AI Physical Channels", false, 11),
        new("TDMS File Path", false, 10),
        new("error in (no error)", false, 8),
        new("Waveforms", true, 3),
        new("error out", true, 0),
    ];

    private static readonly ConnectorPane.Terminal[] Corrected =
    [
        new("AI Physical Channels", false, 0),
        new("TDMS File Path", false, 5),
        new("error in (no error)", false, 11),
        new("Waveforms", true, 4),
        new("error out", true, 15),
    ];

    [Fact]
    public void Reports_the_pattern_and_shape_up_front()
    {
        var rendered = PaneTools.Render(Vi, 4833, Bounds4833, Corrected);

        Assert.Contains("pattern 4833", rendered);
        Assert.Contains("16 terminals", rendered);
        Assert.Contains("5x2x2x2x5", rendered);
    }

    [Fact]
    public void Passes_a_pane_that_follows_the_guide()
    {
        var rendered = PaneTools.Render(Vi, 4833, Bounds4833, Corrected);

        Assert.Contains("follows NI's style guide", rendered);
        Assert.DoesNotContain("[violation]", rendered);
    }

    [Fact]
    public void Fails_the_pane_that_shipped_and_says_what_to_write_instead()
    {
        var rendered = PaneTools.Render(Vi, 4833, Bounds4833, AsShipped);

        Assert.Contains("[violation]", rendered);
        Assert.Contains("CORRECTED ASSIGNMENT", rendered);
        Assert.Contains("conIdx=\"0\"", rendered);
        Assert.Contains("conIdx=\"11\"", rendered);
        Assert.Contains("conIdx=\"15\"", rendered);
        Assert.Contains("(was 8)", rendered);
    }

    /// <summary>
    /// A pane that passes must not be handed a list of changes. Suggest() has its own canonical order
    /// - inputs down the left edge in document order - and disagrees with plenty of correct panes that
    /// merely order their terminals differently. Measured on NI's own
    /// `XML Script - CompoundArithmetic.vi`, whose pane is a mirrored 4815: the answer said the pane
    /// follows the style guide and then proposed nine conIdx changes, which reads as a defect report.
    /// </summary>
    [Fact]
    public void A_clean_pane_is_not_given_a_corrected_assignment()
    {
        // Both inputs are on the left edge, so nothing is wrong - but they are in the opposite order
        // to the one Suggest would pick, which is what used to trigger the block.
        ConnectorPane.Terminal[] reordered =
            [new("first", false, 5), new("second", false, 0), new("out", true, 4)];

        var rendered = PaneTools.Render(Vi, 4833, Bounds4833, reordered);

        Assert.Contains("follows NI's style guide", rendered);
        Assert.DoesNotContain("CORRECTED ASSIGNMENT", rendered);
    }

    /// <summary>
    /// The pattern is chosen by the generator, so an answer that did not say "measure again" would
    /// invite the caller to reuse these numbers on the next VI - which is exactly how the bug got in.
    /// </summary>
    [Fact]
    public void Always_warns_that_regenerating_can_move_every_number() =>
        Assert.Contains("measure again after regenerating",
            PaneTools.Render(Vi, 4833, Bounds4833, Corrected));

    [Fact]
    public void Says_so_when_no_terminal_is_on_the_pane()
    {
        var rendered = PaneTools.Render(Vi, 4833, Bounds4833, []);

        Assert.Contains("no terminals on its connector pane", rendered);
        Assert.DoesNotContain("VERDICT", rendered);
    }

    [Fact]
    public void An_unreadable_bounds_payload_is_a_named_failure()
    {
        var rendered = PaneTools.Render(Vi, 4833, "not xml", Corrected);

        Assert.Contains("boundsUnparsable", rendered);
    }
}

/// <summary>
/// The pattern catalogue as a caller sees it. The gap between "measured" and "catalogued" is the
/// interesting part: a pattern with no geometry must not produce a plausible-looking map.
/// </summary>
public class PaneToolsCatalogueTests
{
    private static ConnectorPanePatterns.Row Measured4833 => new(
        4833, 16, "5x2x2x2x5",
        ConnectorPaneGeometryTests.Geometry(4833, ConnectorPaneGeometryTests.Pattern4833),
        "DaqReadAndTDMS.vi", SeenVis: 59, Variants: 1);

    private static ConnectorPanePatterns.Row Unmeasured4806 => new(4806, 4, "4", null, null);

    [Fact]
    public void A_measured_pattern_shows_its_map_and_its_four_roles()
    {
        var rendered = PaneTools.DescribePattern(Measured4833, 4833);

        Assert.Contains("Pattern 4833", rendered);
        Assert.Contains("Measured on DaqReadAndTDMS.vi", rendered);
        Assert.Contains("error in      conIdx 11", rendered);
        Assert.Contains("error out     conIdx 15", rendered);
    }

    [Fact]
    public void An_unmeasured_pattern_refuses_to_guess()
    {
        var rendered = PaneTools.DescribePattern(Unmeasured4806, 4806);

        Assert.Contains("NOT MEASURED", rendered);
        Assert.Contains("UNKNOWN", rendered);
        Assert.Contains("read-only", rendered);
        Assert.DoesNotContain("conIdx 0", rendered);
    }

    /// <summary>
    /// A pattern found in several orientations must warn, in both the single-pattern answer and the
    /// listing. Without it the majority orientation reads as the only one - and a rotated pane really
    /// does occur, four times in a thousand on 4815.
    /// </summary>
    [Fact]
    public void A_pattern_found_in_several_orientations_says_so()
    {
        var varying = Measured4833 with { SeenVis = 5, Variants = 2 };

        var one = PaneTools.DescribePattern(varying, 4833);
        Assert.Contains("CAUTION", one);
        Assert.Contains("2 distinct ORIENTATIONS", one);
        Assert.Contains("majority orientation", one);
        Assert.Contains("and on 4 other VI(s)", one);

        var listing = PaneTools.DescribeAll([varying, Unmeasured4806]);
        Assert.Contains("(*)", listing);
        Assert.Contains("1 pattern(s) were found in more than one ORIENTATION", listing);
    }

    [Fact]
    public void A_pattern_with_one_orientation_stays_quiet_about_it()
    {
        Assert.DoesNotContain("CAUTION", PaneTools.DescribePattern(Measured4833, 4833));
        Assert.DoesNotContain("(*)", PaneTools.DescribeAll([Measured4833]));
    }

    [Fact]
    public void A_number_that_is_not_a_pattern_says_which_range_is()
    {
        var rendered = PaneTools.DescribePattern(null, 1234);

        Assert.Contains("4800 to 4835", rendered);
    }

    [Fact]
    public void The_listing_counts_measured_against_unmeasured()
    {
        var rendered = PaneTools.DescribeAll([Measured4833, Unmeasured4806]);

        Assert.Contains("2 connector pane patterns, 1 with measured slot geometry, 1 without",
            rendered);
        Assert.Contains("0 / 11 / 4 / 15", rendered);
        Assert.Contains("not measured", rendered);
    }

    /// <summary>
    /// The listing must lead with the pattern a NEW VI gets here, and with the four numbers to write.
    /// That is the answer a generator needs before it authors any conIdx, and the reason the tool can
    /// be called before the first generation instead of only after it.
    /// </summary>
    [Fact]
    public void The_listing_leads_with_this_stations_default_and_its_four_slots()
    {
        var rendered = PaneTools.DescribeAll([Measured4833],
            new StationPaneDefault.Reading(4833, @"C:\LV\LabVIEW.ini",
                "LabVIEW.ini: DefaultConPane=4833"));

        Assert.Contains("THIS STATION", rendered);
        Assert.Contains("pattern 4833", rendered);
        Assert.Contains("DefaultConPane=4833", rendered);
        Assert.Contains("first input 0, error in 11, first output 4, error out 15", rendered);
        // And it must not let that be read as permission to stop measuring.
        Assert.Contains("Still measure the VI afterwards", rendered);
    }

    [Fact]
    public void An_unknown_station_default_is_said_to_be_unknown()
    {
        var rendered = PaneTools.DescribeAll([Measured4833],
            new StationPaneDefault.Reading(null, null, "No LabVIEW.ini found"));

        Assert.Contains("unknown", rendered);
        Assert.DoesNotContain("write these:", rendered);
    }

    /// <summary>
    /// A station default whose pattern has no measured geometry must not be dressed up with numbers
    /// from somewhere else - the four slots are only printable when they were measured.
    /// </summary>
    [Fact]
    public void A_station_default_with_no_geometry_asks_for_a_measurement_instead()
    {
        var rendered = PaneTools.DescribeAll([Unmeasured4806],
            new StationPaneDefault.Reading(4806, @"C:\LV\LabVIEW.ini", "LabVIEW.ini: DefaultConPane=4806"));

        Assert.Contains("No measured geometry for 4806", rendered);
        Assert.DoesNotContain("write these:", rendered);
    }

    /// <summary>The shipped resource must actually carry every pattern, not just the catalogue in code.</summary>
    [Fact]
    public void The_embedded_table_lists_all_thirty_six_patterns()
    {
        var rendered = PaneTools.DescribeAll();

        Assert.Contains("36 connector pane patterns", rendered);
        Assert.Contains("4800", rendered);
        Assert.Contains("4835", rendered);
    }
}
