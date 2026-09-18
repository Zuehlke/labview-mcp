using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A LOW `dwarnCount` cannot be read without knowing how old the instance is.
///
/// The note has always warned that the log is reset at start and never said WHEN, so a zero eight
/// seconds old printed identically to one earned over four hours.
///
/// AND SCOPING THE FIX TO ZERO WAS TOO NARROW - acceptance showed it rather than argument.
/// Measured 2026-09-18 on two freshly restarted instances, 98 s and 94 s old: BOTH answered
/// `dwarnCount: 1`, one benign `DestroyPlatformEvent failed with MgErr 42`. So the zero branch is
/// nearly unreachable on this station, and the caveat was landing where nobody would read it.
/// </summary>
public class ZeroDwarnNoteTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void AFreshInstanceSaysTheCountMeansAlmostNothing(int warnings)
    {
        var note = StatusTools.LowDwarnNote(warnings, upSeconds: 9 * 60);

        Assert.Contains("9 MINUTE(S) OLD", note);
        Assert.Contains("says almost nothing", note);
        Assert.Contains("labviewUpSeconds", note);
    }

    /// <summary>
    /// THE ARM THAT MADE THIS CHANGE NECESSARY: 1 is what a fresh LabVIEW really reports here, and
    /// before the widening it got the unqualified "Low enough to be ordinary" on its own.
    /// </summary>
    [Fact]
    public void TheCountThatAFreshInstanceActuallyReportsIsQualified()
    {
        var note = StatusTools.LowDwarnNote(warnings: 1, upSeconds: 94);

        Assert.Contains("1 DWarn events", note);
        Assert.Contains("Low enough to be ordinary", note);
        Assert.Contains("says almost nothing", note);
    }

    /// <summary>
    /// THE CONTROL ARM. A note that always cried "fresh" would be exactly as useless as one that
    /// never did, and the original warning is correct for a long-lived instance.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ALongLivedInstanceIsNotAccusedOfHavingJustResetItsLog(int warnings)
    {
        var note = StatusTools.LowDwarnNote(warnings, upSeconds: 4 * 60 * 60);

        Assert.Contains("up 240 minutes", note);
        Assert.DoesNotContain("says almost nothing", note);
    }

    /// <summary>
    /// The measured warning that was always there stays there on the zero branch - widening the
    /// caveat must not cost the sentence it was attached to.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(100000)]
    [InlineData(null)]
    public void TheMeasuredZeroWarningSurvivesEveryArm(int? upSeconds)
    {
        var note = StatusTools.LowDwarnNote(warnings: 0, upSeconds);

        Assert.Contains("NOT a promise of health", note);
        Assert.Contains("log is reset at start", note);
    }

    /// <summary>
    /// An unreadable start time is reported as unreadable rather than silently treated as old,
    /// which would be the reassuring answer and the wrong one.
    /// </summary>
    [Fact]
    public void AnUnreadableStartTimeIsSaidOutrightRatherThanAssumed()
    {
        var note = StatusTools.LowDwarnNote(warnings: 1, upSeconds: null);

        Assert.Contains("could not be read", note);
        Assert.Contains("not established", note);
    }

    /// <summary>
    /// THE RETRACTED STORY MUST NOT COME BACK. This note pointed at `Error 1025` as a stale-
    /// reference symptom for a day; 1025 means the `.lvproj` does not exist, and `lvai_open_file`
    /// refuses a missing path before LabVIEW ever sees it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheRetracted1025DiagnosisIsNotRepeatedHere(int warnings)
    {
        var note = StatusTools.LowDwarnNote(warnings, upSeconds: 60);

        Assert.DoesNotContain("1025", note);
        Assert.DoesNotContain("1154", note);
    }

    /// <summary>
    /// The boundary is a reading aid, so it is asserted only as being consistent on both sides of
    /// itself - a test that pinned 30 minutes as a fact would be claiming a measurement.
    /// </summary>
    [Fact]
    public void TheBoundaryIsConsistentOnBothSides()
    {
        Assert.Contains("says almost nothing", StatusTools.LowDwarnNote(1, 30 * 60 - 1));
        Assert.DoesNotContain("says almost nothing", StatusTools.LowDwarnNote(1, 30 * 60));
    }
}
