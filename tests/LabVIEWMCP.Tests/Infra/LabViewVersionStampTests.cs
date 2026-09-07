using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The `LVVersion` stamped into files this server CREATES.
///
/// THE BUG THESE GUARD. It was the constant `26008000` in three places, and that made every
/// class tool unusable on a LabVIEW 2025 station: each writes a throwaway `-loadcheck.lvproj`
/// and opens it, and LabVIEW 2025 answers `Error 1125, File version is later than the current
/// LabVIEW version` at `Project:Open`. Re-stamping the same file to `25008000` by hand made
/// `lvai_open_file` answer `errorCode: 0`, which is what identified it.
/// </summary>
public sealed class LabViewVersionStampTests
{
    [Theory]
    [InlineData(2025, "25008000")]
    [InlineData(2026, "26008000")]
    [InlineData(2020, "20008000")]
    public void AReleaseYearBecomesItsStamp(int release, string expected) =>
        Assert.Equal(expected, LabViewVersionStamp.Stamp(release));

    /// <summary>A release outside the plausible range is a caller's mistake, not a stamp.</summary>
    [Theory]
    [InlineData(26)]
    [InlineData(1999)]
    [InlineData(26008000)]
    public void AnImplausibleReleaseIsRefused(int release) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LabViewVersionStamp.Stamp(release));

    /// <summary>
    /// Both spellings a human reaches for. The stamp form is checked FIRST because `25008000`
    /// also parses as an integer, and reading it as a year would give an absurd release instead
    /// of the obvious answer.
    /// </summary>
    [Theory]
    [InlineData("2025", 2025)]
    [InlineData("25008000", 2025)]
    [InlineData("25", 2025)]
    [InlineData(" 2026 ", 2026)]
    [InlineData("26008000", 2026)]
    public void AnOverrideIsUnderstoodAsAYearOrAsAStamp(string text, int expected) =>
        Assert.Equal(expected, LabViewVersionStamp.ParseOverride(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LabVIEW 2025")]
    [InlineData("25008001")]      // not the stamp shape
    [InlineData("999999")]
    public void NonsenseIsNotSilentlyAccepted(string? text) =>
        Assert.Null(LabViewVersionStamp.ParseOverride(text));

    /// <summary>
    /// The binary header of `NI.LVClass.FlattenedPrivateDataCTL` opens with the SAME version in
    /// the same digits - `26008000` is the bytes `0x26 0x00 0x80 0x00` - so a 2025 class needs
    /// `0x25` there too. Leaving the captured 2026 header verbatim would put a 2026 stamp inside
    /// a 2025 class's private data.
    /// </summary>
    [Fact]
    public void TheFlattenedControlHeaderCarriesTheSameVersion()
    {
        Assert.Equal(new byte[] { 0x25, 0x00, 0x80, 0x00 }, LvClass.VersionBytes("25008000"));
        Assert.Equal(new byte[] { 0x26, 0x00, 0x80, 0x00 }, LvClass.VersionBytes("26008000"));
    }

    [Fact]
    public void AWrappedControlOpensWithTheStampItWasGiven()
    {
        var blob = LvClass.DecodeProperty(LvClass.Wrap(new byte[16], LabViewVersionStamp.Stamp(2025)));

        Assert.Equal(0x25, blob[0]);
        Assert.Equal(0x00, blob[1]);
        Assert.Equal(0x80, blob[2]);
        Assert.Equal(0x00, blob[3]);
    }

    [Theory]
    [InlineData(2025)]
    [InlineData(2026)]
    public void ANewProjectAndClassCarryTheStampTheyWereGiven(int release)
    {
        var stamp = LabViewVersionStamp.Stamp(release);

        Assert.Contains($"<Project Type=\"Project\" LVVersion=\"{stamp}\">",
                        LvClass.Project([], stamp), StringComparison.Ordinal);
        Assert.Contains($"<LVClass LVVersion=\"{stamp}\">",
                        LvClass.Document("Auto", LvClass.Wrap(new byte[16], stamp), null, null, stamp),
                        StringComparison.Ordinal);
    }

    // ---------- the requirement that runs AGAINST the change ----------

    /// <summary>
    /// EXISTING FILES ARE NEVER RE-STAMPED, and this is the one requirement that does not follow
    /// from "we need 2025-compatible files" - it opposes it. Tools that write into a library the
    /// user already has must leave its `LVVersion` byte-for-byte, even when it is OLDER than the
    /// connected LabVIEW. Otherwise every edit is a silent, irreversible version upgrade of
    /// somebody's file.
    ///
    /// `AddToProject` and `AddVisToProject` edit line by line and never touch the root element,
    /// so this passes today; the test is here so it keeps passing when someone reaches for
    /// `XDocument.Save`, which would rewrite the whole document.
    /// </summary>
    [Theory]
    [InlineData("19008000")]      // far older than anything this server would write
    [InlineData("25008000")]
    public void EditingAnExistingProjectLeavesItsVersionAlone(string original)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var project = Path.Combine(dir, "Keep.lvproj");
        File.WriteAllText(project, LvClass.Project([], original));

        LvClass.AddToProject(project, "Auto.lvclass", "../Auto/Auto.lvclass");
        LvClass.AddVisToProject(project, "Tests", [("Test Auto.vi", "../Tests/Test Auto.vi")]);

        var after = File.ReadAllText(project);
        Assert.Contains($"LVVersion=\"{original}\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("26008000", after, StringComparison.Ordinal);

        // And the edits really happened - a no-op would pass the assertion above trivially.
        Assert.Contains("Auto.lvclass", after, StringComparison.Ordinal);
        Assert.Contains("Test Auto.vi", after, StringComparison.Ordinal);

        Directory.Delete(dir, true);
    }

    // ---------- the lint ----------

    /// <summary>
    /// NO HARDCODED STAMP MAY COME BACK. The bug was one literal copied into three writers, and
    /// the next writer would copy it again - so the shape is banned outside the version code
    /// itself, its tests, and prose.
    ///
    /// Source only: `docs/` and `CLAUDE.md` legitimately quote `26008000` as a measured value.
    /// </summary>
    [Fact]
    public void NoHardcodedVersionStampSurvivesInTheSource()
    {
        var src = FindRepoFolder("src");
        Assert.NotNull(src);

        var allowed = new[] { "LabViewVersionStamp.cs" };

        // TWO SHAPES, and neither is "the digits appear somewhere". Help text that explains the
        // format - "give the year (2025) or the stamp (25008000)" - is not the bug and must not
        // trip this; the first version of this lint failed on exactly that and would have been
        // silenced by weakening it, which is how a lint stops meaning anything.
        //
        //   LVVersion="26008000"   the writer bug, verbatim
        //   "26008000"             a bare constant, the shape the next writer would copy
        var shapes = new[]
        {
            new Regex("LVVersion=\\\\?\"\\d\\d008000"),
            new Regex("\"\\d\\d008000\""),
        };
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src!, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (allowed.Contains(Path.GetFileName(file))) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!shapes.Any(s => s.IsMatch(lines[i]))) continue;
                // A comment may quote the value - that is how the measurement is recorded.
                var text = lines[i].TrimStart();
                if (text.StartsWith("//") || text.StartsWith("///") || text.StartsWith("*")) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "a LVVersion stamp is hardcoded outside LabViewVersionStamp: " +
            string.Join(" | ", offenders));
    }

    private static string? FindRepoFolder(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(dir.FullName, "docs")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
