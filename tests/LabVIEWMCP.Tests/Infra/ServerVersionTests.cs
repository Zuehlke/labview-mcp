using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Reading this build's own version. The input shapes are all produced by the build, so
/// <see cref="ServerVersion.Read"/> is exercised directly - there is no way to make the running
/// test assembly carry six different InformationalVersions.
///
/// The rule the tests encode: a version reader must NEVER throw. It is called by lvai_status and
/// pylv_status, and those are the two tools someone reaches for when an install is already
/// suspected of being broken. A reader that can fail there turns "your bundle is missing" into
/// an unhandled exception with no diagnosis at all.
/// </summary>
public class ServerVersionTests
{
    [Fact]
    public void AStampedReleaseVersionSplitsIntoItsVersionAndCommit()
    {
        var info = ServerVersion.Read("1.3.0+e08bf93921b708d94e32ceb6e519e5a3b58085cf");

        Assert.Equal("1.3.0", info.Version);
        Assert.Equal("e08bf93921b708d94e32ceb6e519e5a3b58085cf", info.Commit);
        Assert.True(info.IsRelease);
        Assert.Equal("1.3.0 (e08bf939)", info.Display);
    }

    /// <summary>
    /// 0.0.0 is the csproj default and means "this did not come from the release workflow". The
    /// distinction matters because until 2026-09-11 nothing set Version at all, so a release and
    /// a local debug build both reported 1.0.0 - indistinguishable, which is exactly how three
    /// installs on one machine came to be identifiable only by hashing 800 files.
    /// </summary>
    [Fact]
    public void TheDevelopmentSentinelIsReportedAsADevBuild()
    {
        var info = ServerVersion.Read("0.0.0+4e71ce408c8f586c7a9e54b23d6eef8effe096fc");

        Assert.Equal("0.0.0", info.Version);
        Assert.False(info.IsRelease);
        Assert.Contains("-dev", info.Display, StringComparison.Ordinal);
        // The commit survives, and on a dev build it is the ONLY identifier there is.
        Assert.Equal("4e71ce408c8f586c7a9e54b23d6eef8effe096fc", info.Commit);
    }

    [Fact]
    public void AVersionWithNoCommitIsReadWithoutInventingOne()
    {
        var info = ServerVersion.Read("1.3.0");

        Assert.Equal("1.3.0", info.Version);
        Assert.Null(info.Commit);
        Assert.True(info.IsRelease);
        Assert.Contains("no commit recorded", info.Display, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]                     // a trailing separator with nothing after it
    [InlineData("+abc123")]               // a commit with no version
    [InlineData("not-a-version")]
    public void AnUnreadableInformationalVersionDegradesInsteadOfThrowing(string? informational)
    {
        var info = ServerVersion.Read(informational);

        Assert.NotNull(info.Version);
        Assert.NotEmpty(info.Version);
        Assert.NotNull(info.Display);
        // Whatever it is, it must not claim to be a release.
        if (info.Version == "0.0.0") Assert.False(info.IsRelease);
    }

    /// <summary>
    /// A short commit must not be sliced past its end - the Display property takes the first
    /// eight characters and a hand-set or truncated SHA is the input that would throw.
    /// </summary>
    [Theory]
    [InlineData("1.3.0+ab", "ab")]
    [InlineData("1.3.0+abc1234", "abc1234")]
    public void AShortCommitIsShownWholeRatherThanSliced(string informational, string commit)
    {
        var info = ServerVersion.Read(informational);

        Assert.Equal(commit, info.Commit);
        Assert.NotNull(info.Display);
    }

    /// <summary>
    /// The real assembly, read the way the tools read it. It cannot assert a particular number -
    /// that depends on how this build was invoked - only that the path works and produces
    /// something a human can read.
    /// </summary>
    [Fact]
    public void TheRunningAssemblyReportsAReadableVersion()
    {
        var info = ServerVersion.Current;

        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(string.IsNullOrWhiteSpace(info.Display));
        Assert.Same(info, ServerVersion.Current);   // cached, not re-read per call
    }

    /// <summary>
    /// The csproj must carry an explicit Version, and it must be the 0.0.0 sentinel. Checked
    /// against the file rather than the built assembly, because a build invoked with
    /// -p:Version=X.Y.Z - which is what the release workflow does - would hide its absence.
    /// </summary>
    [Fact]
    public void TheProjectDeclaresTheDevelopmentVersionSentinel()
    {
        var csproj = Res.FindRepoFile("src/LabVIEWMCP/LabVIEWMCP.csproj");
        Assert.True(csproj is not null, "cannot find the project file");

        Assert.Contains("<Version>0.0.0</Version>", File.ReadAllText(csproj!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// plugin.json must carry the placeholder the release workflow substitutes. Without it the
    /// stamping step fails the release - by design - but this says so at test time instead.
    /// </summary>
    [Fact]
    public void ThePluginManifestCarriesTheVersionPlaceholder()
    {
        var manifest = Res.FindRepoFile("plugin/.claude-plugin/plugin.json");
        Assert.True(manifest is not null, "cannot find plugin/.claude-plugin/plugin.json");

        Assert.Contains("\"version\": \"0.0.0\"", File.ReadAllText(manifest!),
            StringComparison.Ordinal);
    }
}
