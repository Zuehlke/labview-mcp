using System.Text.Json;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A path that is not there must be refused BEFORE LabVIEW gets to misdescribe it.
///
/// Measured 2026-09-18 as an A/B inside ONE directory: `OpenProbe.lvproj` (exists) answered
/// errorCode 0 with projectBecameActive true, while `GibtsGarNicht.lvproj` beside it answered
/// **Error 1025, Application Reference is invalid** - a message about the IDE's application
/// reference, for a missing file. Three calls, a LabVIEW restart, a client restart and two
/// written-up diagnoses went past that before anyone checked the file was there.
/// </summary>
public class OpenFileMissingPathTests
{
    private static JsonElement Refusal(string? viPath = null, string? projectPath = null)
    {
        var json = ActionTools.OpenFilePrecheck(
            viPath, viPath is null ? null : "x.vi",
            projectPath, projectPath is null ? null : "x.lvproj");
        Assert.NotNull(json);
        return JsonDocument.Parse(json!).RootElement;
    }

    [Fact]
    public void AProjectThatDoesNotExistIsRefusedAndTheMisleadingCodeIsNamed()
    {
        var refusal = Refusal(projectPath: @"C:\temp\NoSuchPlace\GibtsGarNicht.lvproj");

        Assert.Equal("fileNotFound", refusal.GetProperty("errorKind").GetString());
        var text = refusal.ToString();
        // Whoever reads this is about to blame LabVIEW - connect the two for them.
        Assert.Contains("1025", text);
        Assert.Contains("GibtsGarNicht.lvproj", text);
    }

    /// <summary>
    /// A missing VI is refused too, but it must NOT borrow the project's story: LabVIEW answers
    /// the honest `Error 7, File not found` for a VI, and claiming 1025 there would be a new
    /// wrong rule in place of the old one.
    /// </summary>
    [Fact]
    public void AMissingViIsRefusedWithoutBorrowingTheProjectDiagnosis()
    {
        var text = Refusal(viPath: @"C:\temp\NoSuchPlace\Nope.vi").ToString();

        Assert.Contains("Error 7", text);
        Assert.DoesNotContain("1025", text);
    }

    /// <summary>
    /// THE CONTROL ARM, and the one that matters: a guard that refused everything would pass the
    /// two tests above while making the tool useless. A real file must go through.
    /// </summary>
    [Fact]
    public void APathThatExistsIsNotRefused()
    {
        var real = Path.Combine(Path.GetTempPath(), $"lvmcp-open-{Guid.NewGuid():N}.lvproj");
        File.WriteAllText(real, "<Project/>");
        try
        {
            Assert.Null(ActionTools.OpenFilePrecheck(null, null, real, "x.lvproj"));
        }
        finally { File.Delete(real); }
    }

    /// <summary>
    /// The existing guards must still fire, and must fire FIRST: a `.lvproj` handed to viPath is a
    /// swapped-parameter fault whether or not the file is there, and saying "not found" about a
    /// file that exists in the other parameter would send the reader hunting for it.
    /// </summary>
    [Fact]
    public void TheSwappedParameterGuardStillWinsOverTheExistenceCheck()
    {
        var refusal = Refusal(viPath: @"C:\temp\NoSuchPlace\Thing.lvproj");

        Assert.Equal("badArguments", refusal.GetProperty("errorKind").GetString());
    }
}
