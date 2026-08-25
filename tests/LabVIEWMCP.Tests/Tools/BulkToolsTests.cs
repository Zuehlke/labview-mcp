using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The composed generate sequence: validate, convert, measure the pane.
///
/// What is worth testing without LabVIEW is the CONTROL FLOW, because that is where a composite
/// tool can quietly do the wrong thing - carry on past a failed step, or report success for a
/// step it never ran. The pane measurement itself needs a real VI and a real helper, so these stop
/// at measurePane: false; the pane half is covered by <see cref="PaneVerdictTests"/> and was
/// measured end to end against LabVIEW on 2026-08-25.
/// </summary>
public class BulkGenerateViTests
{
    [Fact]
    public async Task A_refused_validation_stops_before_anything_is_generated()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCodeByMethod["ValidateAIXML"] = 1;
        server.Service.ErrorMessage = "Unsupported SubVI: C:\\p\\Local.vi";

        var result = await new BulkTools(server.Connection)
            .GenerateViAsync(server.TempPath("in.xml"), server.TempPath("Out.vi"));

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("validate", Res.Str(result, "failedAtStep"));
        // The point of the gate: no .vi was asked for.
        Assert.Equal(0, server.Service.CountOf("ConvertAIXMLToVI"));
    }

    [Fact]
    public async Task A_refused_generation_is_reported_against_the_convert_step()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCodeByMethod["ValidateAIXML"] = 0;
        server.Service.ErrorCodeByMethod["ConvertAIXMLToVI"] = 1357;

        var result = await new BulkTools(server.Connection)
            .GenerateViAsync(server.TempPath("in.xml"), server.TempPath("Out.vi"));

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("convert", Res.Str(result, "failedAtStep"));
        Assert.Equal(1, server.Service.CountOf("ValidateAIXML"));
        Assert.Equal(1, server.Service.CountOf("ConvertAIXMLToVI"));
    }

    [Fact]
    public async Task Both_steps_run_in_order_and_each_answer_is_kept_whole()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "a generated VI";

        var result = await new BulkTools(server.Connection).GenerateViAsync(
            server.TempPath("in.xml"), server.TempPath("Out.vi"), measurePane: false);

        Assert.True(Res.Bool(result, "ok"));
        Assert.True(Res.IsNull(result, "failedAtStep"));
        Assert.Equal(["ValidateAIXML", "ConvertAIXMLToVI"],
            server.Service.Received.Select(r => r.Method).Where(
                m => m is "ValidateAIXML" or "ConvertAIXMLToVI"));

        // Each sub-answer survives intact - a failure has to read the same as it would from
        // calling the tool by hand, which is only true if nothing is summarised away.
        var steps = Res.Arr(result, "steps");
        Assert.Equal(2, steps.Count);
        Assert.Equal("validate", steps[0]!["step"]!.GetValue<string>());
        Assert.Equal("convert", steps[1]!["step"]!.GetValue<string>());
        Assert.NotNull(steps[1]!["answer"]!["viBytes"]);
    }

    [Fact]
    public async Task Skipping_the_pane_measurement_says_so_instead_of_implying_it_passed()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new BulkTools(server.Connection).GenerateViAsync(
            server.TempPath("in.xml"), server.TempPath("Out.vi"), measurePane: false);

        Assert.True(Res.Bool(result, "ok"));
        Assert.False(Res.Has(result, "paneViolations"));
        Assert.Contains("NOT measured", Res.Str(result, "note"));
    }
}

/// <summary>
/// The operations array of pylv_apply. Parsed into records BEFORE the extract runs, so that a
/// misspelling costs an error message rather than a half-applied bundle - which is why these tests
/// exist at all: the parse is the only part of that tool that can be wrong without a filesystem.
/// </summary>
public class BulkOperationParsingTests
{
    private const string Main = @"C:\b\Main.xml";
    private const string Heap = @"C:\b\Main_BDHb.xml";
    private const string Dir = @"C:\b";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("[]")]
    public void No_operations_means_inspect_only(string? json) =>
        Assert.Empty(BulkTools.Operation.ParseAll(json));

    [Fact]
    public void A_conpane_operation_drives_the_bundle_not_the_heap() =>
        Assert.Equal([Dir, "--pattern", "4815"],
            BulkTools.Operation.ParseAll("""[{"op":"conpane","pattern":4815}]""")[0]
                .Arguments(Main, Heap, Dir));

    [Fact]
    public void A_retarget_takes_both_xml_files_and_an_optional_path() =>
        Assert.Equal([Main, Heap, "Old.vi", "New.vi", "--path", @"C:\d\New.vi"],
            BulkTools.Operation.ParseAll(
                """[{"op":"retarget","from":"Old.vi","to":"New.vi","path":"C:\\d\\New.vi"}]""")[0]
                .Arguments(Main, Heap, Dir));

    [Fact]
    public void A_retarget_without_a_path_rewrites_only_the_last_segment() =>
        Assert.Equal([Main, Heap, "Old.vi", "New.vi"],
            BulkTools.Operation.ParseAll(
                """[{"op":"retarget","from":"Old.vi","to":"New.vi"}]""")[0]
                .Arguments(Main, Heap, Dir));

    [Fact]
    public void Place_labels_carries_side_and_gap_only_when_they_are_given()
    {
        Assert.Equal([Heap, "--place", "9001:130"],
            BulkTools.Operation.ParseAll("""[{"op":"placeLabels","place":"9001:130"}]""")[0]
                .Arguments(Main, Heap, Dir));

        Assert.Equal([Heap, "--place", "9001:130", "--side", "below", "--gap", "30"],
            BulkTools.Operation.ParseAll(
                """[{"op":"placeLabels","place":"9001:130","side":"below","gap":30}]""")[0]
                .Arguments(Main, Heap, Dir));
    }

    [Fact]
    public void The_order_written_is_the_order_applied() =>
        Assert.Equal(["retarget", "placeLabels", "conpane"],
            BulkTools.Operation.ParseAll("""
                [{"op":"retarget","from":"A.vi","to":"B.vi"},
                 {"op":"placeLabels","place":"1:2"},
                 {"op":"conpane","pattern":4815}]
                """).Select(o => o.Op));

    /// <summary>
    /// Only the two diagram edits need a heap. conpane rewrites the connector pane, which lives in
    /// the main resource - so a VI with no block diagram can still have its pattern repaired.
    /// </summary>
    [Fact]
    public void Only_the_diagram_edits_need_a_diagram_heap()
    {
        Assert.False(BulkTools.Operation.ParseAll(
            """[{"op":"conpane","pattern":4815}]""")[0].NeedsSingleHeap);
        Assert.True(BulkTools.Operation.ParseAll(
            """[{"op":"retarget","from":"A.vi","to":"B.vi"}]""")[0].NeedsSingleHeap);
        Assert.True(BulkTools.Operation.ParseAll(
            """[{"op":"placeLabels","place":"1:2"}]""")[0].NeedsSingleHeap);
    }

    [Theory]
    // Not JSON at all, and not an array - both easy to send and both silent if waved through.
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"op":"conpane","pattern":4815}""", "must be a JSON ARRAY")]
    [InlineData("[3]", "is not an object")]
    [InlineData("""[{"pattern":4815}]""", "has no \"op\"")]
    // A near miss on the op name, which is what a typo actually looks like.
    [InlineData("""[{"op":"conpain","pattern":4815}]""", "which this build does not know")]
    // Each operation's own required field, named rather than defaulted.
    [InlineData("""[{"op":"conpane"}]""", "needs \"pattern\"")]
    [InlineData("""[{"op":"retarget","from":"A.vi"}]""", "needs \"to\"")]
    [InlineData("""[{"op":"placeLabels"}]""", "needs \"place\"")]
    [InlineData("""[{"op":"placeLabels","place":"1:2","side":"left"}]""", "auto, above or below")]
    public void A_bad_operation_is_named_before_anything_is_touched(string json, string expected) =>
        Assert.Contains(expected,
            Assert.Throws<ArgumentException>(() => BulkTools.Operation.ParseAll(json)).Message);
}

/// <summary>
/// The pane verdict as NUMBERS beside the prose. <see cref="BulkTools"/> gates on these, so a
/// count that disagrees with the text it ships with would turn a defective pane into a pass.
/// </summary>
public class PaneVerdictTests
{
    // Pattern 4833's slot geometry - the shape this station's DefaultConPane produces.
    private static string Bounds4833 =>
        ConnectorPaneGeometryTests.BoundsXml(ConnectorPaneGeometryTests.Pattern4833);

    /// <summary>The 4815 index set on a 4833 pane: two inputs on the output edge.</summary>
    private static readonly ConnectorPane.Terminal[] AsShipped =
    [
        new("TDMS File Path", false, 11),
        new("Waveforms", false, 10),
        new("error in (no error)", false, 8),
        new("CSV File Path", true, 3),
        new("error out", true, 0),
    ];

    private static readonly ConnectorPane.Terminal[] Corrected =
    [
        new("TDMS File Path", false, 0),
        new("Waveforms", false, 5),
        new("error in (no error)", false, 11),
        new("CSV File Path", true, 4),
        new("error out", true, 15),
    ];

    [Fact]
    public void A_clean_pane_counts_zero_and_says_so()
    {
        var verdict = PaneTools.RenderVerdict(@"C:\p\Ok.vi", 4833, Bounds4833,
            Corrected);

        Assert.True(verdict.Measured);
        Assert.True(verdict.Clean);
        Assert.Equal(0, verdict.Violations);
        Assert.Contains("Nothing to change", verdict.Text);
    }

    [Fact]
    public void A_breached_pane_counts_what_its_own_text_reports()
    {
        var verdict = PaneTools.RenderVerdict(@"C:\p\Bad.vi", 4833, Bounds4833,
            AsShipped);

        Assert.True(verdict.Measured);
        Assert.False(verdict.Clean);
        Assert.Contains($"VERDICT: {verdict.Violations} violation(s), " +
                        $"{verdict.Warnings} warning(s).", verdict.Text);
    }

    /// <summary>
    /// An unmeasurable pane is neither pass nor fail, and must not be read as either. A caller
    /// that treated -1 as "no violations" would report a VI clean that nothing ever looked at.
    /// </summary>
    [Fact]
    public void An_unreadable_pane_is_not_a_pass()
    {
        var verdict = PaneTools.RenderVerdict(@"C:\p\Bad.vi", 4833, "not xml",
            Corrected);

        Assert.False(verdict.Measured);
        Assert.False(verdict.Clean);
        Assert.Equal(-1, verdict.Violations);
    }

    [Fact]
    public void Render_still_answers_with_exactly_the_verdict_text() =>
        Assert.Equal(
            PaneTools.RenderVerdict(@"C:\p\Ok.vi", 4833, Bounds4833, Corrected).Text,
            PaneTools.Render(@"C:\p\Ok.vi", 4833, Bounds4833, Corrected));
}
