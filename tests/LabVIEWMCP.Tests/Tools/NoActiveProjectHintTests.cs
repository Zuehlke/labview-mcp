using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// THE HINT MUST NOT ASSERT WHAT IT HAS NOT CHECKED.
///
/// `lvai_open_file` answered `Error 1025, Application Reference is invalid` on 2026-09-18 and
/// printed "The open itself reported no error" beside it, then sent the reader after the
/// foreground window - the measured cause of a DIFFERENT failure - while claiming a retry had run
/// that never did. The branch was keyed on `projectBecameActive == false` alone.
///
/// docs/typedef-disconnect.md section 13g.
/// </summary>
public class NoActiveProjectHintTests
{
    [Fact]
    public void AFailedOpenIsNotDescribedAsHavingReportedNoError()
    {
        var hint = ActionTools.NoActiveProjectHint(openErrorCode: 1025, retryRan: false);

        Assert.DoesNotContain("reported no error", hint);
        Assert.Contains("FAILED", hint);
    }

    /// <summary>
    /// 1025 gets its own diagnosis: the split between the two application instances is the whole
    /// finding, and a caller who reads only "not the foreground" still does not know what to do.
    /// </summary>
    [Fact]
    public void The1025CaseNamesTheSiblingFaultAndSaysRetryingCannotHelp()
    {
        var hint = ActionTools.NoActiveProjectHint(openErrorCode: 1025, retryRan: false);

        // The ONE measured cause is a missing .lvproj - name it, and say the guard already
        // excluded it so the reader does not re-check what the tool checked.
        Assert.Contains("does not exist", hint);
        Assert.Contains("NOT explained", hint);
        // Two retracted stories must not come back: the restart, and the foreground.
        Assert.DoesNotContain("restarted under", hint);
        Assert.DoesNotContain("labviewUpSeconds", hint);
    }

    /// <summary>
    /// Any other non-zero code has no diagnosis here, so the hint sends the reader to the message
    /// rather than inventing one.
    /// </summary>
    [Fact]
    public void AnotherFailingCodeIsNamedWithoutBorrowingThe1025Story()
    {
        var hint = ActionTools.NoActiveProjectHint(openErrorCode: 7, retryRan: false);

        Assert.Contains("Error 7", hint);
        Assert.Contains("errorMessage", hint);
        Assert.DoesNotContain("1154", hint);
        Assert.DoesNotContain("foreground", hint);
    }

    /// <summary>
    /// THE CONTROL ARM. The foreground story is correct for the case it was written for - an open
    /// that really did report no error - and a fix that suppressed it everywhere would replace one
    /// wrong answer with another.
    /// </summary>
    [Fact]
    public void ACleanOpenWithNoActiveProjectStillGetsTheForegroundDiagnosis()
    {
        var hint = ActionTools.NoActiveProjectHint(openErrorCode: 0, retryRan: true);

        Assert.Contains("reported no error", hint);
        Assert.Contains("foreground", hint);
        Assert.Contains("already tried fronting", hint);
    }

    /// <summary>
    /// The second false claim in the same sentence: the retry is skipped when LabVIEW's window is
    /// not found, and the hint said it had run regardless.
    /// </summary>
    [Fact]
    public void ASkippedRetryIsNotReportedAsHavingRun()
    {
        var hint = ActionTools.NoActiveProjectHint(openErrorCode: 0, retryRan: false);

        Assert.DoesNotContain("already tried fronting", hint);
        Assert.Contains("no retry ran", hint);
        Assert.Contains("foregroundRetry is null", hint);
    }
}
