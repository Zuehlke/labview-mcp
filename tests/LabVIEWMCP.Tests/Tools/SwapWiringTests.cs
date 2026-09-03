using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The wiring half of `lvai_swap_subvis`' verify, added 2026-09-03.
///
/// WHY IT EXISTS. Until then verify compared CALL TARGETS only, and a swap between accessors of
/// different types was measured leaving the value wire attached to the CLASS terminal - both are
/// refnums, so LabVIEW's `Replace` re-attached it there - while verify reported a clean restore.
/// The diagram linked, compiled, ran, and asserted against the wrong terminal. That is worse than
/// a hard error, because the suite went green.
///
/// EVERY FIXTURE BELOW IS THE MEASURED EXPORT, not a plausible one. This repository has fixed one
/// parser three times and been wrong four times because the fixture was the shape that is easy to
/// write; the `Call` snippets here are copied from the export that carried the defect.
/// </summary>
public sealed class SwapWiringTests
{
    /// <summary>
    /// The AFTER state, verbatim from the export that carried the defect: `AnalogInput in` wired,
    /// `AnalogInput out` wired, and `Sample Rate:` / `error out:` UNWIRED.
    /// </summary>
    private const string AfterTheDefect = """
        <VI _name="Test AnalogInput.vi">
          <Call inputs="AnalogInput in:265.AnalogInput out,error in (no error):"
                outputs="AnalogInput out:796.AnalogInput out,Sample Rate:,error out:"
                target="AnalogInput.lvclass\3ARead Sample Rate.vi" uid="796"/>
        </VI>
        """;

    /// <summary>The same node while it was still sound: `Sample Rate` carried a net.</summary>
    private const string BeforeTheDefect = """
        <VI _name="Test AnalogInput.vi">
          <Call inputs="AnalogInput in:265.AnalogInput out,error in (no error):"
                outputs="AnalogInput out:796.AnalogInput out,Sample Rate:796.Sample Rate,error out:"
                target="AnalogInput.lvclass\3ARead Maximum Value.vi" uid="796"/>
        </VI>
        """;

    // ------------------------------------------------------------------ reading the wiring

    [Fact]
    public void AnEmptyNetMeansUNWIRED_andThatIsTheWholeDistinction()
    {
        var wiring = SwapTools.WiringByUid(AfterTheDefect)["796"];

        // `AnalogInput in:265.AnalogInput out` is wired; `error in (no error):` is not.
        Assert.Equal(["AnalogInput in"], wiring.WiredInputs);
        // `AnalogInput out` is wired; `Sample Rate:` and `error out:` are not.
        Assert.Equal(["AnalogInput out"], wiring.WiredOutputs);
        Assert.Equal(2, wiring.Count);
    }

    [Fact]
    public void TheSoundStateCarriesOneMoreNet()
    {
        var wiring = SwapTools.WiringByUid(BeforeTheDefect)["796"];

        Assert.Equal(["AnalogInput out", "Sample Rate"], wiring.WiredOutputs);
        Assert.Equal(3, wiring.Count);
    }

    [Fact]
    public void TheUidIsTheIdentityAndTheTargetIsJustData()
    {
        // Replace swaps which VI sits in an existing node, so the uid survives and the target does
        // not. Keying by target would compare nothing to nothing.
        var before = SwapTools.WiringByUid(BeforeTheDefect);
        var after = SwapTools.WiringByUid(AfterTheDefect);

        Assert.Equal(before.Keys, after.Keys);
        Assert.NotEqual(before["796"].Target, after["796"].Target);
    }

    [Fact]
    public void MalformedXmlYieldsNothingRatherThanThrowing() =>
        // This runs inside a tool that has already SAVED the VI. Throwing here would turn a
        // reporting problem into a lost answer about a completed edit.
        Assert.Empty(SwapTools.WiringByUid("<not xml"));

    // ---------------------------------------------- real export text from a CORRECT suite

    /// <summary>
    /// A read accessor as it appears in a suite that WORKS - copied verbatim from
    /// `Test DAQmxAnalogInput.vi`'s export, 2026-09-03, after a swap this repository verified green.
    ///
    /// TWO THINGS HERE WOULD FOOL A NAIVER CHECK. The CLASS OUTPUT is legitimately unwired -
    /// `DAQmxAnalogInput out:` - because the read chain ends at this node, so "an unwired
    /// output is suspicious" would fire on correct code. And the real target name carries
    /// LabVIEW's own length-prefix CONTROL BYTES between the class and the member name, so
    /// anything matching a target by string would miss it - the wiring check keys on `uid` and
    /// never reads the target. Those bytes are left out of this literal deliberately: they are
    /// not what is under test, and embedding them is how four patches today lost an escape to a
    /// shell.
    /// </summary>
    private const string CorrectReadAccessor = """
        <VI _name="Test DAQmxAnalogInput.vi">
          <Call inputs="DAQmxAnalogInput in:265.DAQmxAnalogInput out,error in (no error):"
                outputs="DAQmxAnalogInput out:,Minimum Value:240.Minimum Value,error out:"
                target="DAQmxAnalogInput.lvclass-Read Minimum Value.vi" uid="240"/>
        </VI>
        """;

    [Fact]
    public void RealExportTextFromAWorkingSuiteReadsTwoWiredTerminals()
    {
        var wiring = SwapTools.WiringByUid(CorrectReadAccessor)["240"];

        Assert.Equal(["DAQmxAnalogInput in"], wiring.WiredInputs);
        // The class output is UNWIRED and that is correct - the read chain ends here.
        Assert.Equal(["Minimum Value"], wiring.WiredOutputs);
        Assert.Equal(2, wiring.Count);
    }

    [Fact]
    public void AWorkingSuiteComparedWithItselfReportsNoLoss() =>
        // The false-positive question, asked against real data rather than a fixture I designed:
        // if this fired, every correct swap would start answering ok: false.
        Assert.Empty(SwapTools.LostWiring(
            SwapTools.WiringByUid(CorrectReadAccessor),
            SwapTools.WiringByUid(CorrectReadAccessor)));

    // ------------------------------------------------------------------ the comparison

    [Fact]
    public void ALostNetIsREPORTED_withBothCountsAndBothNameLists()
    {
        var lost = SwapTools.LostWiring(
            SwapTools.WiringByUid(BeforeTheDefect), SwapTools.WiringByUid(AfterTheDefect));

        var entry = Assert.Single(lost)!.AsObject();
        Assert.Equal("796", entry["uid"]!.GetValue<string>());
        Assert.Equal(3, entry["wiredBefore"]!.GetValue<int>());
        Assert.Equal(2, entry["wiredAfter"]!.GetValue<int>());
        // Both lists, because the reader has to see WHICH net went missing and the names change
        // legitimately across a retarget.
        Assert.Contains("Sample Rate", entry["wiredNamesBefore"]!.ToJsonString());
        Assert.DoesNotContain("Sample Rate", entry["wiredNamesAfter"]!.ToJsonString());
    }

    [Fact]
    public void ARENAMEDButEquallyWiredNodeIsNotAFalseAlarm()
    {
        // THE FALSE POSITIVE THIS DESIGN AVOIDS. Every successful swap renames terminals - a
        // socket's `value` becomes the accessor's `Sample Rate` - so a check that compared NAMES
        // would fire on every correct run and be switched off within a day.
        const string socket = """
            <VI><Call inputs="obj in:10.value,value:11.value"
                      outputs="obj out:20.obj out" target="LVMCP ClsW1.vi" uid="20"/></VI>
            """;
        const string real = """
            <VI><Call inputs="AnalogInput in:10.value,Sample Rate:11.value"
                      outputs="AnalogInput out:20.AnalogInput out"
                      target="AnalogInput.lvclass\3AWrite Sample Rate.vi" uid="20"/></VI>
            """;

        Assert.Empty(SwapTools.LostWiring(
            SwapTools.WiringByUid(socket), SwapTools.WiringByUid(real)));
    }

    [Fact]
    public void GAININGANetIsNotALoss()
    {
        Assert.Empty(SwapTools.LostWiring(
            SwapTools.WiringByUid(AfterTheDefect), SwapTools.WiringByUid(BeforeTheDefect)));
    }

    [Fact]
    public void ANodeTheSwapLegitimatelyREMOVEDIsNotReportedAsALoss()
    {
        // Only nodes present in BOTH snapshots are compared. A regeneration that drops a node is a
        // different event, and reporting it here would blame the swap for it.
        Assert.Empty(SwapTools.LostWiring(
            SwapTools.WiringByUid(BeforeTheDefect), SwapTools.WiringByUid("<VI/>")));
    }

    [Fact]
    public void ATerminalNameContainingAColonStillParses()
    {
        // Terminal names in the wild carry punctuation - `max characters/row  (no limit\3A0)` is a
        // real one from Read Delimited Spreadsheet.vi. The net is after the LAST colon, so a name
        // with an escaped colon in it must not be split at the wrong place.
        const string xml = """
            <VI><Call inputs="max characters/row  (no limit\3A0):11.value"
                      outputs="" target="X.vi" uid="30"/></VI>
            """;

        var wiring = SwapTools.WiringByUid(xml)["30"];
        Assert.Equal([@"max characters/row  (no limit\3A0)"], wiring.WiredInputs);
    }
}
