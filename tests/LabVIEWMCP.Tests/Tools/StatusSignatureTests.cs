using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// `looksDegraded` now asks WHICH DWarn, not just how many.
///
/// WHY. `0xECE53844 DestroyPlatformEvent failed with MgErr 42` is LabVIEW failing to release an OS
/// event handle during its own housekeeping - `[ExecSys:0; NOT InExec]`, no VI call stack, NI's own
/// frames. Measured across three full cold class builds on 2026-09-03/04: 17, 26 and 18 of them,
/// with no crash, no restart, and every artefact correct. Counting them made `looksDegraded` read
/// `true` through an entirely clean run, which is worse than not having the field: a health flag
/// that cries wolf is one a reader learns to ignore.
///
/// IT IS A DENY-LIST OF ONE, on purpose. The signatures that have preceded real deaths keep
/// counting, and so does anything unrecognised - a new signature is exactly the case where a
/// warning is worth most.
/// </summary>
public sealed class StatusSignatureTests
{
    private const string BenignTeardown = """
        <DEBUG_OUTPUT>
        04.09.2026 09:04:10.269
        DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42.
        source\ThEvent.cpp(213) : DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42.
        [ExecSys:0; NOT InExec]
        </DEBUG_OUTPUT>
        """;

    /// <summary>The signature that DID precede a death, verbatim from the preserved log.</summary>
    private const string RealTrouble = """
        source\ole\OMAutoClasses.cpp(74) : DWarn 0x762E6013:
            Out of bounds TypedObjList access (index: -1, nObj: 0)
        [Executing: "LV AI Core.lvlibp:VI generator.vi"]
        """;

    [Fact]
    public void AThousandBenignTeardownsAreNotDegradation()
    {
        var many = string.Join("\n", Enumerable.Repeat(BenignTeardown, 200));

        Assert.False(StatusTools.HasSignatureOtherThanBenignTeardown(many));
    }

    [Fact]
    public void ONESignatureThatIsNotTheBenignOneCounts()
    {
        // Deliberately buried among benign ones: the check must not be satisfied by the first
        // line it recognises and stop looking.
        var mixed = BenignTeardown + "\n" + RealTrouble + "\n" + BenignTeardown;

        Assert.True(StatusTools.HasSignatureOtherThanBenignTeardown(mixed));
    }

    [Fact]
    public void ANEWUnrecognisedSignatureCountsToo()
    {
        // The list is a deny-list of one rather than an allow-list, so a signature nobody has
        // characterised yet still raises the flag. That is the case where a warning is worth most.
        const string unknown =
            "source\\somewhere\\New.cpp(1) : DWarn 0x00000001: something nobody has seen yet.";

        Assert.True(StatusTools.HasSignatureOtherThanBenignTeardown(unknown));
    }

    [Fact]
    public void ALogWithNoDWarnAtAllIsNotDegraded() =>
        Assert.False(StatusTools.HasSignatureOtherThanBenignTeardown(
            "04.09.2026 09:04:10.269\nNetLoop: select: sysErr = 10038\n"));

    [Fact]
    public void TheWordDWarnMustBePRESENT_aMentionOfTheCodeAloneIsNotAnEntry() =>
        // Guards against matching prose - this document's own text names 0xECE53844 repeatedly,
        // and a log line is identified by carrying "DWarn", not by carrying a hex code.
        Assert.False(StatusTools.HasSignatureOtherThanBenignTeardown(
            "the benign signature is 0x762E6013 and it is discussed at length here"));
}
