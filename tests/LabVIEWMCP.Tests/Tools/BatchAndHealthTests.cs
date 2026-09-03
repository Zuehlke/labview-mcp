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
