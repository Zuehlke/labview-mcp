using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The three changes made on 2026-09-03 after the third measured HAL build: a list parameter on
/// the two socket-route tools, and a LabVIEW health prior in <c>lvai_status</c>.
///
/// All of it is checked offline. The batch paths are thin compositions over the single-VI paths -
/// deliberately, because that is the shape <c>lvai_generate_vis</c> already proved and it keeps the
/// stub and swap logic in one place - so what is worth testing here is the ARGUMENT HANDLING, which
/// is where a batch tool actually goes wrong: a bad entry must cost a message rather than a
/// half-edited suite.
/// </summary>
public sealed class BatchAndHealthTests
{
    // ================================================================== lvai_status health

    [Fact]
    public void HealthReportsSomethingWhateverTheStationLooksLike()
    {
        // Runs against the real %TEMP%, so it cannot assert a count - only that the reader answers
        // in a shape a caller can act on, with or without a log present.
        var health = StatusTools.Health();

        Assert.NotNull(health["logFound"]);
        Assert.NotNull(health["note"]);

        if (health["logFound"]!.GetValue<bool>())
        {
            Assert.NotNull(health["dwarnCount"]);
            Assert.NotNull(health["looksDegraded"]);
            Assert.True(health["dwarnCount"]!.GetValue<int>() >= 0);
        }
    }

    // ------------------------------------------------ dwarnCount counts EVENTS, not log lines
    //
    // THE FIXTURE IS THE REAL LOG, copied verbatim out of
    // %TEMP%\LabVIEW_32_26.3.1f1_interactive_jcm_cur.txt on 2026-09-08 - two different
    // signatures, five events, ten lines. It is written out in full rather than shortened
    // because a plausible fixture is how three tools in this repository came to pass their own
    // tests while being wrong: the two-lines-per-event shape IS the thing under test, so it has
    // to be NI's own bytes and not a reconstruction of them.
    private const string RealLogExtract = """
        <DEBUG_OUTPUT>
        08.09.2026 12:07:19.505
        DWarnInternal 0x484DB723: bad parent in MoveItem
        source\project\ProjectItem.cpp(18606) : DWarnInternal 0x484DB723: bad parent in MoveItem
        [ExecSys:0; NOT InExec]
        minidump id: 95ca34b5-a4ab-4ef3-704c-7373ba1464d3
        </DEBUG_OUTPUT>

        <DEBUG_OUTPUT>
        08.09.2026 13:05:50.103
        DWarn 0xBB613420: trying to override with non-reserved UID, request: 10 res: 0 max: 42 sat: 42
        source\panel\HeapObjMapImpl.cpp(226) : DWarn 0xBB613420: trying to override with non-reserved UID, request: 10 res: 0 max: 42 sat: 42
        [ExecSys:0; Executing:"[VI "LV AI Core.lvlibp:VI generator.vi" (0x28314cd8)]"]
        minidump id: 25c46534-d7a8-4f6a-51fe-fbb742f38359
        </DEBUG_OUTPUT>

        DWarn 0xBB613420: trying to override with non-reserved UID, request: 11 res: 0 max: 56 sat: 56
        source\panel\HeapObjMapImpl.cpp(226) : DWarn 0xBB613420: trying to override with non-reserved UID, request: 11 res: 0 max: 56 sat: 56
        DWarn 0xBB613420: trying to override with non-reserved UID, request: 12 res: 0 max: 66 sat: 5
        source\panel\HeapObjMapImpl.cpp(226) : DWarn 0xBB613420: trying to override with non-reserved UID, request: 12 res: 0 max: 66 sat: 5
        DWarn 0xBB613420: trying to override with non-reserved UID, request: 13 res: 0 max: 83 sat: 3
        source\panel\HeapObjMapImpl.cpp(226) : DWarn 0xBB613420: trying to override with non-reserved UID, request: 13 res: 0 max: 83 sat: 3
        """;

    [Fact]
    public void FiveEventsWrittenAsTenLinesAreCountedAsFive()
    {
        // The regression this exists for: counting the substring "DWarn" answers 10 here, and
        // every threshold in Health() was calibrated against numbers inflated exactly that way.
        var (count, countedBy) = StatusTools.CountDwarnEvents(RealLogExtract);

        Assert.Equal(5, count);
        Assert.Equal("events", countedBy);
        // And the doubling is what it used to answer, so the fixture really does exercise it.
        Assert.Equal(10, RealLogExtract.Split("DWarn").Length - 1);
    }

    [Fact]
    public void ALogWithNoDwarnAtAllCountsZeroWithoutFallingBack() =>
        Assert.Equal((0, "events"), StatusTools.CountDwarnEvents(
            "<DEBUG_OUTPUT>\n08.09.2026 12:00:00.000\nnothing wrong here\n</DEBUG_OUTPUT>"));

    [Fact]
    public void AnUnrecognisedFormatFallsBackToTheRawCountRatherThanClaimingHealth()
    {
        // A format this was never measured against must not read 0. Reporting the raw count and
        // NAMING the rule is honest; a silent 0 would be the one answer that misleads, because a
        // caller reads it as "nothing has gone wrong".
        var (count, countedBy) = StatusTools.CountDwarnEvents("DWarn 0x1: something\nDWarn 0x2: else");

        Assert.Equal(2, count);
        Assert.Equal("rawFallback", countedBy);
    }

    [Fact]
    public void HealthNeverThrows() =>
        // It is called from lvai_status, which is the one tool that must answer when everything
        // else is broken. A locked or vanished log must not take the status call down with it.
        Assert.Null(Record.Exception(() => StatusTools.Health()));

    // ================================================================== lvai_swap_subvis batching

    private static readonly SwapTools Swap = new(null!);

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"vi\":\"C:\\\\a.vi\"}")]                 // an object, not an array
    [InlineData("[]")]                                      // nothing to do
    [InlineData("[{\"swaps\":[]}]")]                        // no vi
    public async Task AMalformedBatchIsRefusedBeforeAnyViIsTouched(string editsJson)
    {
        var answer = await Swap.SwapSubVisAsync(viPath: "", editsJson: editsJson);

        Assert.Contains("badArguments", answer);
    }

    [Fact]
    public async Task AViThatDoesNotExistStopsTheWholeBatch()
    {
        // NOT a per-entry failure: a swap SAVES IN PLACE, so discovering a typo halfway through
        // would leave some VIs swapped and some not. The check runs over every entry first.
        var answer = await Swap.SwapSubVisAsync(
            viPath: "",
            editsJson: """[{"vi":"C:\\definitely\\not\\here.vi","swaps":[]}]""");

        Assert.Contains("badArguments", answer);
        Assert.Contains("Nothing was swapped", answer);
    }

    // ================================================================== lvai_placeholder_subvi

    private static readonly PlaceholderTools Placeholders = new(null!);

    [Fact]
    public async Task AnEmptyPathListIsRefused()
    {
        var answer = await Placeholders.PlaceholderSubViAsync(viPath: "", viPaths: "   \r\n  \r\n");

        Assert.Contains("badArguments", answer);
    }

    [Fact]
    public async Task EveryMissingViIsNamedAtOnceRatherThanOneRunPerCall()
    {
        // Naming all of them in one answer is the point: the caller batched to save round trips,
        // and reporting the first bad path only would hand the saving straight back.
        var answer = await Placeholders.PlaceholderSubViAsync(
            viPath: "",
            viPaths: "C:\\nope\\one.vi\r\nC:\\nope\\two.vi");

        Assert.Contains("badArguments", answer);
        Assert.Contains("one.vi", answer);
        Assert.Contains("two.vi", answer);
    }
}
