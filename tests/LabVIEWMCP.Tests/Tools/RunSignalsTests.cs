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

    // ---------------------------------------------- a bracket literal for a cluster or array

    private static readonly List<LabVIEWMcp.Infra.AixmlComposer.PanelItem> Panel =
    [
        new("Buttons", "cluster{bool.Start,bool.Stop,bool.Reset}", false),
        new("Levels", "array{double}", false),
        new("Name", "string", false),
        new("Mode", "uint16{Off,On}", false),
        new("Status", "cluster{bool.A,bool.B}", true),
    ];

    /// <summary>
    /// THE BENCHMARK CASE: three CLD rounds signalled a cluster of buttons as `[false,true,false]`,
    /// the form `set` and inputsJson take, and the helper answered Error 1103. It now crosses as
    /// the LvVariant XML the helper unflattens, element by element.
    /// </summary>
    [Fact]
    public void A_bracket_literal_for_a_cluster_or_array_control_becomes_LabVIEW_XML()
    {
        var signals = RunTools.ParseSignals(
            """[{"control":"Buttons","value":[false,true,false]},{"control":"Levels","value":"[1.5, 2]"}]""");

        var converted = RunTools.SignalLiterals(signals, Panel, out var problem);

        Assert.Null(problem);
        var cluster = System.Xml.Linq.XElement.Parse(converted![0].Value).Element("Cluster")!;
        Assert.Equal(["0", "1", "0"], // LabVIEW's own XML writes a Boolean so
                     cluster.Elements("Boolean").Select(b => b.Element("Val")!.Value).ToArray());
        Assert.Equal(["Start", "Stop", "Reset"],
                     cluster.Elements("Boolean").Select(b => b.Element("Name")!.Value).ToArray());
        var array = System.Xml.Linq.XElement.Parse(converted[1].Value).Element("Array")!;
        Assert.Equal("2", array.Element("Dimsize")!.Value);
        Assert.DoesNotContain("\n", converted[0].Value + converted[1].Value); // paired by line in the helper
    }

    /// <summary>Only a compound CONTROL is converted: a string's text, a scalar, XML and an unknown name pass as written.</summary>
    [Fact]
    public void Everything_else_passes_as_written()
    {
        var signals = RunTools.ParseSignals(
            """
            [{"control":"Name","value":"[draft]"},{"control":"Mode","value":"On"},
             {"control":"Buttons","value":"<Cluster><Name>Buttons</Name></Cluster>"},
             {"control":"Nowhere","value":"[1,2]"},{"control":"Status","value":"[true,false]"}]
            """);

        var converted = RunTools.SignalLiterals(signals, Panel, out var problem);

        Assert.Null(problem);
        Assert.Equal(signals, converted);
    }

    /// <summary>A literal that does not fit is refused by name before anything is signalled, not answered 1103 after.</summary>
    [Fact]
    public void A_literal_that_does_not_fit_the_type_is_refused_by_name()
    {
        var signals = RunTools.ParseSignals("""[{"control":"Buttons","value":"[false,maybe,false]"}]""");

        Assert.Null(RunTools.SignalLiterals(signals, Panel, out var problem));
        Assert.Contains("'Buttons'", problem);
        Assert.Contains("Nothing was signalled", problem);
        Assert.DoesNotContain("..", problem);
    }
}
