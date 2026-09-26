using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// `signalsJson` on `lvai_run_vi_and_read_values`: fire Value Change events while a runForMs run
/// is in progress, then take the snapshot.
///
/// WHY IT EXISTS. The seventh ATM build (2026-09-26) passed validation, execState, 58 unit tests
/// and a runForMs start-up snapshot while its consumer loop read `User Input` BEFORE the user had
/// typed, so `Enter` verified stale text and the menus never filled. A start-up snapshot cannot
/// see a fault that needs an event to happen; the user found it by pressing the buttons. The
/// signal step is the probe that was hand-built to confirm the fix, productised.
/// </summary>
public sealed class RunSignalsTests
{
    [Fact]
    public void NoSignalsIsAnEmptyList()
    {
        Assert.Empty(RunTools.ParseSignals(null));
        Assert.Empty(RunTools.ParseSignals("  "));
        Assert.Empty(RunTools.ParseSignals("[]"));
    }

    /// <summary>ORDER IS THE POINT - a card goes in before its account number is typed.</summary>
    [Fact]
    public void SignalsKeepTheirOrderAndTakeAnyScalarAsText()
    {
        var signals = RunTools.ParseSignals(
            """[{"control":"Card Simulator","value":true},{"control":"User Input","value":"23456"},{"control":"Amount","value":12.5}]""");

        Assert.Equal(["Card Simulator", "User Input", "Amount"], signals.Select(s => s.Key));
        Assert.Equal(["true", "23456", "12.5"], signals.Select(s => s.Value));
    }

    /// <summary>An object cannot say which signal comes first, so it is refused rather than ordered by guess.</summary>
    [Fact]
    public void AnObjectIsRefused() =>
        Assert.Throws<ArgumentException>(() => RunTools.ParseSignals("""{"Card Simulator":"true"}"""));

    /// <summary>An unknown key is refused by name - dropping it in silence is the defect this repository keeps meeting.</summary>
    [Fact]
    public void AnUnknownKeyIsRefusedByName()
    {
        var bad = Assert.Throws<ArgumentException>(() =>
            RunTools.ParseSignals("""[{"control":"Enter","value":"true","waitMs":100}]"""));
        Assert.Contains("waitMs", bad.Message);
    }

    /// <summary>
    /// Names and values are paired BY LINE inside the helper, so an empty value or a line break
    /// would shift every later signal onto the wrong control - refused, like the inputs.
    /// </summary>
    [Theory]
    [InlineData("""[{"control":"User Input","value":""}]""")]
    [InlineData("""[{"control":"User Input","value":"a\nb"}]""")]
    [InlineData("""[{"value":"true"}]""")]
    public void AValueThatWouldMisalignThePairsIsRefused(string json) =>
        Assert.Throws<ArgumentException>(() => RunTools.ParseSignals(json));

    /// <summary>
    /// The shipped timed helper carries the signal step. Read from the real file, because the claim
    /// is about what `scripts\lvai_run_for_ms.xml` contains.
    /// </summary>
    [Fact]
    public void ShippedTimedHelperCanSignal() =>
        Assert.True(RunTools.CanSignal(
            Path.Combine(RepoTree.Root, "scripts", RunTools.TimedHelperAixmlFileName)));

    /// <summary>The control arm: the untimed helper cannot, and a guard that accepted everything would pass the test above.</summary>
    [Fact]
    public void ShippedUntimedHelperCannotSignal() =>
        Assert.False(RunTools.CanSignal(
            Path.Combine(RepoTree.Root, "scripts", RunTools.HelperAixmlFileName)));
}
