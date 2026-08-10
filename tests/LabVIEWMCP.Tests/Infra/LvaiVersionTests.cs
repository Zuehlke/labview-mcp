using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The add-on fingerprint that guards the AIXML export cache. The fixtures mirror the real layout
/// measured on this station - %ProgramFiles%\NI\LVAddons\lvai\&lt;api&gt; with an lvaddoninfo.json and
/// two .lvlibp under Targets\&lt;arch&gt;\resource\AI - because the point is to notice an upgrade, and
/// an upgrade is exactly a new folder plus different binaries.
/// </summary>
public sealed class LvaiVersionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lvai-version").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>One add-on version folder, with its info file and one service binary.</summary>
    private string WriteVersion(string api, string binaryContent = "core")
    {
        var directory = Path.Combine(_root, api);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "lvaddoninfo.json"),
            $$"""{"AddonName":"LVAI","ApiVersion":"v{{api}}","MinimumSupportedLVVersion":"26.0"}""");

        var binaries = Path.Combine(directory, "Targets", "win64", "resource", "AI", "LV AI Core");
        Directory.CreateDirectory(binaries);
        File.WriteAllText(Path.Combine(binaries, "LV AI Core.lvlibp"), binaryContent);
        return directory;
    }

    [Fact]
    public void TheApiVersionIsReadFromTheAddonInfo()
    {
        var directory = WriteVersion("26.3");

        Assert.Equal("v26.3", LvaiVersion.ApiVersion(directory));
    }

    [Fact]
    public void AnAbsentRootYieldsNullRatherThanAFingerprint()
    {
        // Null must not be confused with "nothing installed": recording it would make the next
        // start-up compare against a fiction.
        Assert.Null(LvaiVersion.Compute(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void AnEmptyRootIsAnAnswerNotAFailure()
    {
        Assert.Equal("none", LvaiVersion.Compute(_root));
    }

    [Fact]
    public void TheSameInstallationFingerprintsTheSameTwice()
    {
        WriteVersion("26.3");

        Assert.Equal(LvaiVersion.Compute(_root), LvaiVersion.Compute(_root));
    }

    /// <summary>An upgrade installs a new version folder beside the old one - measured behaviour.</summary>
    [Fact]
    public void AddingAVersionChangesTheFingerprint()
    {
        WriteVersion("26.1");
        var before = LvaiVersion.Compute(_root);

        WriteVersion("26.3");

        Assert.NotEqual(before, LvaiVersion.Compute(_root));
    }

    /// <summary>
    /// The case a per-VI cache key cannot see: the generator is rebuilt, every source VI on disk is
    /// untouched. Different binary content is what has to move the fingerprint.
    /// </summary>
    [Fact]
    public void RebuildingAServiceBinaryChangesTheFingerprint()
    {
        WriteVersion("26.3", binaryContent: "old build");
        var before = LvaiVersion.Compute(_root);

        WriteVersion("26.3", binaryContent: "a rather longer new build");

        Assert.NotEqual(before, LvaiVersion.Compute(_root));
    }

    [Fact]
    public void AVersionWithoutAnInfoFileStillFingerprints()
    {
        var directory = Path.Combine(_root, "27.0");
        Directory.CreateDirectory(directory);

        // '?' stands in for the unknown ApiVersion; the folder must still count.
        Assert.NotNull(LvaiVersion.Compute(_root));
        Assert.NotEqual("none", LvaiVersion.Compute(_root));
        Assert.Null(LvaiVersion.ApiVersion(directory));
    }

    [Fact]
    public void CheckReportsUnchangedWhenTheRootCannotBeRead()
    {
        var verdict = LvaiVersion.Check(Path.Combine(_root, "absent"), dropCache: false);

        Assert.Null(verdict.Current);
        Assert.False(verdict.Changed);
        Assert.Equal(0, verdict.EntriesDropped);
        Assert.Contains("not found under LVAddons", verdict.Describe());
    }

    [Fact]
    public void AChangedVerdictExplainsWhatItDidAndWhatItDidNot()
    {
        var verdict = new LvaiVersion.Verdict("NEW", "OLD", Changed: true, EntriesDropped: 7);
        var text = verdict.Describe();

        Assert.Contains("OLD -> NEW", text);
        Assert.Contains("dropped 7 cached AIXML export(s)", text);
        // The example index is deliberately NOT rebuilt - saying so is part of the contract.
        Assert.Contains("example index was NOT", text);
        Assert.Contains("--examples --refresh", text);
    }

    [Fact]
    public void AMissingPreviousRecordIsDescribedAsSuch() =>
        Assert.Contains("not recorded",
            new LvaiVersion.Verdict("NEW", null, Changed: true, EntriesDropped: 0).Describe());
}
