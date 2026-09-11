using System.Diagnostics;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

/// <summary>
/// The release tag IS the version: it is stamped into the assembly's version resource,
/// plugin.json's `version` and VERSION.txt at the archive root. A tag MSBuild cannot parse either
/// fails a release halfway through or is silently coerced - and coercion is the dangerous half,
/// because two different tags then stamp the same version into every artefact.
///
/// WHY THIS EXISTS. The tags in this repository did not agree on a format. Measured 2026-09-11:
/// v1.3.0, v1.1.1 and v1.0.7 lower-case; V1.2.8, V1.2.5, V1.2.2, V1.2.0 and V1.1.5 upper-case;
/// and v10.4 with two components - which also sorts ABOVE every three-part tag in
/// `git tag --sort=-v:refname`, so the newest-looking entry in the list was for ten days neither
/// the newest release nor a valid version.
///
/// These tests drive the REAL script rather than a re-implementation of its regex, because a rule
/// re-stated in a test is a rule that can agree with the test and disagree with the release. That
/// is the trap CLAUDE.md records twice - "a tool tested against a plausible fixture is not
/// tested" - and here the fixture would have been the pattern itself.
///
/// They also assert the MESSAGE, not just the exit code. The point of the script is telling a
/// maintainer which of eight plausible mistakes they made; an exit code of 1 with unreadable text
/// is a check that gets worked around rather than obeyed.
/// </summary>
public class ReleaseTagTests
{
    private static string ScriptPath()
    {
        var path = Res.FindRepoFile("scripts/Assert-ReleaseTag.ps1");
        Assert.True(path is not null, "cannot find scripts/Assert-ReleaseTag.ps1");
        return path!;
    }

    /// <summary>
    /// Run the script the way the workflow does - powershell.exe -File - and return both streams
    /// together, because the diagnosis goes to the host and the assertions below read it.
    /// </summary>
    private static (int ExitCode, string Output) Run(string tag)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath());
        psi.ArgumentList.Add("-Tag");
        psi.ArgumentList.Add(tag);

        using var process = Process.Start(psi);
        Assert.True(process is not null, "powershell.exe did not start");
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return (process.ExitCode, stdout + stderr);
    }

    [Theory]
    [InlineData("v1.3.0", "1.3.0")]
    [InlineData("v0.0.0", "0.0.0")]          // the dev-build sentinel is still a legal tag
    [InlineData("v12.7.31", "12.7.31")]
    [InlineData("v10.0.0", "10.0.0")]        // two-digit major, the shape v10.4 should have had
    [InlineData("refs/tags/v1.3.0", "1.3.0")] // GITHUB_REF as well as GITHUB_REF_NAME
    public void AValidTagIsAcceptedAndItsVersionParsed(string tag, string version)
    {
        var (exit, output) = Run(tag);
        Assert.True(exit == 0, $"'{tag}' was refused:{Environment.NewLine}{output}");
        Assert.Contains($"version {version}", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// One case per plausible mistake, each asserting the text NAMES that mistake. A single
    /// "refused" assertion would pass even if every message said the same unhelpful thing.
    /// </summary>
    [Theory]
    [InlineData("V1.2.8", "upper-case")]
    [InlineData("V1.2.0", "upper-case")]
    [InlineData("1.2.3", "does not start with a lower-case")]
    [InlineData("v10.4", "component(s) rather than 3")]
    [InlineData("v2", "component(s) rather than 3")]
    [InlineData("v1.2.3.4", "component(s) rather than 3")]
    [InlineData("v1.2.3-rc1", "pre-release or build suffix")]
    [InlineData("v1.2.3+build7", "pre-release or build suffix")]
    [InlineData("v1.2.x", "not a decimal number")]
    [InlineData("vone.two.three", "not a decimal number")]
    [InlineData("v1.02.3", "leading zero")]
    [InlineData("v01.2.3", "leading zero")]
    [InlineData(" v1.2.3", "whitespace")]
    [InlineData("release-1.2.3", "does not start with a lower-case")]
    public void AnInvalidTagIsRefusedWithAMessageNamingTheActualMistake(string tag, string expected)
    {
        var (exit, output) = Run(tag);
        Assert.True(exit == 1, $"'{tag}' should have been refused but exited {exit}");
        Assert.Contains(expected, output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every refusal must carry the four things that make it actionable without opening a file.
    /// This is the part that decays first when a message is edited.
    /// </summary>
    [Theory]
    [InlineData("V1.2.8")]
    [InlineData("v10.4")]
    [InlineData("v1.2.x")]
    public void EveryRefusalNamesTheTagTheRuleAndTheRemedy(string tag)
    {
        var (exit, output) = Run(tag);
        Assert.Equal(1, exit);
        Assert.Contains(tag, output, StringComparison.Ordinal);
        Assert.Contains("vX.Y.Z", output, StringComparison.Ordinal);
        Assert.Contains("git tag -d", output, StringComparison.Ordinal);
        Assert.Contains("git push origin", output, StringComparison.Ordinal);
        Assert.Contains("Nothing has been built or published", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The near-miss suggestion is the most useful line in the message, and it must be a tag that
    /// the script itself would then ACCEPT - a suggestion that is also invalid would send a
    /// maintainer round the loop twice.
    /// </summary>
    [Theory]
    [InlineData("V1.2.8", "v1.2.8")]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("v10.4", "v10.4.0")]
    [InlineData("v1.2.3-rc1", "v1.2.3")]
    [InlineData("v1.02.3", "v1.2.3")]
    public void TheSuggestedTagIsOfferedAndIsItselfValid(string tag, string suggestion)
    {
        var (exit, output) = Run(tag);
        Assert.Equal(1, exit);
        Assert.Contains($"you probably meant:  {suggestion}", output, StringComparison.Ordinal);

        var (suggestedExit, suggestedOutput) = Run(suggestion);
        Assert.True(suggestedExit == 0,
            $"the script suggests '{suggestion}' for '{tag}' but then refuses it:"
            + Environment.NewLine + suggestedOutput);
    }

    /// <summary>
    /// The tags already in the repository, as a record of what this check would have caught. Not
    /// a check on git - the list is inline on purpose, so it keeps documenting the motivation
    /// after the tags themselves are cleaned up or deleted.
    /// </summary>
    [Fact]
    public void TheExistingMalformedTagsInThisRepositoryWouldBeRefused()
    {
        foreach (var tag in new[] { "V1.2.8", "V1.2.5", "V1.2.2", "V1.2.0", "V1.1.5", "v10.4" })
            Assert.Equal(1, Run(tag).ExitCode);

        foreach (var tag in new[] { "v1.3.0", "v1.1.1", "v1.1.0", "v1.0.7", "v1.0.6" })
            Assert.Equal(0, Run(tag).ExitCode);
    }
}
