using LabVIEWMcp.Grpc;
using Xunit;

namespace LabVIEWMCP.Tests.Grpc;

/// <summary>
/// The selection rule and the folder parsing are pure, so they are pinned here without needing
/// LabVIEW. Discovery itself touches the filesystem and is only checked for self-consistency.
///
/// Version independence is the point: no test names a concrete release, and a hypothetical
/// future one must win automatically.
/// </summary>
public class LabViewLocatorTests
{
    private static LabViewInstall Install(string folder, int release, bool is32) =>
        new($@"C:\x\{folder}\LabVIEW.exe", release, is32, folder);

    // ---------- folder parsing ----------

    [Theory]
    [InlineData("LabVIEW 2026", 2026)]
    [InlineData("LabVIEW 2031", 2031)]
    [InlineData("LabVIEW 2025 SP1", 2025)]
    [InlineData("labview 2024", 2024)]
    public void ReleaseIsParsedFromAnyFolderName(string folder, int expected)
    {
        Assert.True(LabViewLocator.TryParseRelease(folder, out var release));
        Assert.Equal(expected, release);
    }

    [Theory]
    [InlineData("LabVIEW NXG 5.0")]      // different product, hosts no lvai service
    [InlineData("LabVIEW NXG")]
    [InlineData("labview nxg 6.1")]
    public void NxgIsNeverAcceptedHoweverItIsCased(string folder) =>
        Assert.False(LabViewLocator.TryParseRelease(folder, out _));

    [Theory]
    [InlineData("LabVIEW")]              // no release number
    [InlineData("LabVIEW Runtime")]
    [InlineData("Measurement Studio")]
    [InlineData("")]
    public void FoldersWithoutAReleaseAreRejected(string folder) =>
        Assert.False(LabViewLocator.TryParseRelease(folder, out _));

    // ---------- selection ----------

    [Fact]
    public void NewestReleaseWins()
    {
        var pick = LabViewLocator.Select([
            Install("LabVIEW 2023", 2023, is32: true),
            Install("LabVIEW 2026", 2026, is32: true),
            Install("LabVIEW 2024", 2024, is32: true),
        ]);

        Assert.Equal(2026, pick!.Release);
    }

    [Fact]
    public void WithinTheNewestRelease32BitIsPreferred()
    {
        var pick = LabViewLocator.Select([
            Install("LabVIEW 2026", 2026, is32: false),
            Install("LabVIEW 2026", 2026, is32: true),
        ]);

        Assert.True(pick!.Is32Bit);
    }

    [Fact]
    public void ANewer64BitStillBeatsAnOlder32Bit()
    {
        // Release dominates bitness: the rule is "newest, and 32-bit only to break a tie".
        var pick = LabViewLocator.Select([
            Install("LabVIEW 2025", 2025, is32: true),
            Install("LabVIEW 2026", 2026, is32: false),
        ]);

        Assert.Equal(2026, pick!.Release);
        Assert.False(pick.Is32Bit);
    }

    [Fact]
    public void SelectingFromNothingYieldsNothing() =>
        Assert.Null(LabViewLocator.Select([]));

    // ---------- PE bitness ----------

    [Fact]
    public void BitnessOfAKnownAssemblyIsReadFromThePeHeader()
    {
        // This test host is a .NET binary; whatever it is, the reader must not guess null.
        var self = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        Assert.NotNull(self);
        Assert.NotNull(LabViewLocator.ReadIs32Bit(self!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\does\not\exist\nothing.exe")]
    public void UnreadableFilesYieldNullRatherThanThrowing(string path) =>
        Assert.Null(LabViewLocator.ReadIs32Bit(path));

    [Fact]
    public void ATextFileIsNotMistakenForAnExecutable()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"lvloc_{Guid.NewGuid():N}.txt");
        File.WriteAllText(temp, "this is definitely not a PE image");
        try
        {
            Assert.Null(LabViewLocator.ReadIs32Bit(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // ---------- discovery ----------

    [Fact]
    public void DiscoveryIsSelfConsistent()
    {
        foreach (var install in LabViewLocator.Discover())
        {
            Assert.True(File.Exists(install.ExePath), $"reported a missing exe: {install.ExePath}");
            Assert.True(install.Release > 1900, $"implausible release {install.Release}");
            Assert.DoesNotContain("NXG", install.FolderName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DiscoveryAgreesWithTheSelectionRule()
    {
        var installs = LabViewLocator.Discover();
        var pick = LabViewLocator.Select(installs);

        if (installs.Count == 0)
        {
            Assert.Null(pick);
            return;
        }

        Assert.NotNull(pick);
        Assert.Equal(installs.Max(i => i.Release), pick!.Release);
        // If any 32-bit build of that release exists, the pick must be 32-bit.
        if (installs.Any(i => i.Release == pick.Release && i.Is32Bit))
            Assert.True(pick.Is32Bit);
    }
}
