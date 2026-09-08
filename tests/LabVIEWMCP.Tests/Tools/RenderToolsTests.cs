using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <c>lvai_render_diagrams</c>, checked offline.
///
/// What is worth testing here is NOT that LabVIEW draws a picture - that needs LabVIEW and was
/// measured by hand. It is the two pieces of logic that decide WHICH files the caller is told to
/// look at, because getting either wrong makes the tool lie in a way the caller cannot detect: a
/// stale picture reported as this call's output reads exactly like a successful render, and it is
/// the same class of mistake as trusting an in-memory copy over the file on disk.
/// </summary>
public sealed class RenderToolsTests
{
    // ------------------------------------------------------------------ argument handling

    [Fact]
    public async Task AnEmptyPathListIsRefusedRatherThanRenderingNothingQuietly()
    {
        // null! for the connection: the check runs before LabVIEW is touched, and a test that
        // needed a live gRPC service to prove an argument check would prove nothing.
        var answer = await new RenderTools(null!).RenderDiagramsAsync(viPaths: "   \n  \n");

        Assert.Contains("badArguments", answer);
    }

    [Fact]
    public async Task AMissingViStopsTheWHOLEBatchBeforeAnythingIsRendered()
    {
        // Deliberately all-or-nothing. Rendering is a read, so a half-finished set would be
        // harmless in itself - but it would leave the caller reading pictures for two VIs and
        // silently missing the third, which is worse than a message.
        // The FIRST path exists, so this only passes if the check scans every entry up front
        // rather than failing when it reaches the bad one.
        var real = Touch(FreshDirectory(), "Real.vi");
        var answer = await new RenderTools(null!).RenderDiagramsAsync(
            viPaths: $"{real}\ndoes-not-exist-{Guid.NewGuid():N}.vi");

        Assert.Contains("viNotFound", answer);
        Assert.Contains("does-not-exist", answer);
    }

    // ------------------------------------------------------------------ which PNGs are diagrams

    [Fact]
    public void TheTopLevelDiagramComesFirstAndCaseFramesFollowInOrder()
    {
        var directory = FreshDirectory();
        var started = DateTime.UtcNow;

        // LabVIEW names them from the HTML base: <base>d.png then <base>d1..dN.png per Case frame.
        Touch(directory, "ClampArrayd1.png");
        Touch(directory, "ClampArrayd.png");
        Touch(directory, "ClampArrayd2.png");

        var diagrams = RenderTools.DiagramImages(directory, "ClampArray", started);

        Assert.Equal(
            ["ClampArrayd.png", "ClampArrayd1.png", "ClampArrayd2.png"],
            diagrams.Select(Path.GetFileName));
    }

    [Fact]
    public void ControlGlyphsAndOtherVisPicturesAreNotThisVisDiagrams()
    {
        var directory = FreshDirectory();
        var started = DateTime.UtcNow;

        Touch(directory, "ClampArrayd.png");
        Touch(directory, "cdbl.png");            // a control glyph, one per data type
        Touch(directory, "i1dbool.png");         // and an indicator glyph
        Touch(directory, "ClampArrayc.png");     // the connector pane picture, not a diagram
        Touch(directory, "ClampArrayp.png");     // the front panel picture
        Touch(directory, "RunningExtremesd.png");// another VI, rendered into the same directory

        var diagrams = RenderTools.DiagramImages(directory, "ClampArray", started);

        Assert.Equal(["ClampArrayd.png"], diagrams.Select(Path.GetFileName));
    }

    [Fact]
    public void APictureFromAnEARLIERRunIsNotReportedAsThisOne()
    {
        // THE ONE THAT MATTERS. Render twice into the same directory and the old files are still
        // sitting there; reporting them would tell the caller a failed render succeeded, and they
        // would then "look at the diagram" and see the previous version - which is precisely the
        // failure this whole render-and-look step exists to catch.
        var directory = FreshDirectory();

        var stale = Touch(directory, "ClampArrayd.png");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-10));

        var diagrams = RenderTools.DiagramImages(directory, "ClampArray", DateTime.UtcNow);

        Assert.Empty(diagrams);
    }

    // ------------------------------------------------------------------ the HTML base name

    [Theory]
    [InlineData("Clamp Array", "ClampArray")]
    [InlineData("Celsius To Fahrenheit", "CelsiusToFahrenheit")]
    [InlineData("Read Setpoint", "ReadSetpoint")]
    public void SpacesGoFromTheHtmlBaseSoThePngNamesArePredictable(string name, string expected) =>
        Assert.Equal(expected, RenderTools.SafeBaseName(name));

    [Fact]
    public void ANameWithNothingUsableStillYieldsABaseName() =>
        // Otherwise the glob would be "d*.png" and match every diagram in the directory.
        Assert.Equal("diagram", RenderTools.SafeBaseName("___"));

    // ------------------------------------------------------------------ helpers

    private static string FreshDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP.Tests",
                                     $"render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Touch(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }
}
