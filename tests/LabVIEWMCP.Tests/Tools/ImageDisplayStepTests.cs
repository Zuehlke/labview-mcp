using System.Text.Json.Nodes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// NI Vision's Image Display on a generated panel (user feedback of 2026-11-17): the composer
/// writes the plain IMAQ Image refnum and names each display in its report as
/// `imageDisplay &lt;label&gt;|&lt;ctl&gt;`; lvai_generate_vi reads those lines and replaces the
/// controls. These pin the hand-over between the two - the composer's report line and the step
/// that reads it - so a renamed prefix on either side cannot silently drop every display.
/// </summary>
public sealed class ImageDisplayStepTests
{
    [Fact]
    public void ReportLinesNameEachDisplayAndItsCtl()
    {
        var report = new JsonArray(
            "Structure 'Flat Sequence' (2 frames) <- pseq.xml",
            "imageDisplay Image|Image Display.ctl",
            "imageDisplay Overlay View|Image Display (Silver).ctl",
            "Indicator 'error out' <- AIXML-generated probe: pclu2.vi");

        var displays = DiagramCleanup.ImageDisplaysIn(report);

        Assert.Equal([("Image", "Image Display.ctl"), ("Overlay View", "Image Display (Silver).ctl")], displays);
    }

    [Fact]
    public void NoReportOrNoDisplayMeansNoStep()
    {
        Assert.Empty(DiagramCleanup.ImageDisplaysIn(null));
        Assert.Empty(DiagramCleanup.ImageDisplaysIn(new JsonArray("wires routed: 4, left generic: 0")));
    }

    [Fact]
    public void TheComposerWritesTheLineTheStepReads()
    {
        // the composer side of the hand-over, read from its source: the same prefix and separator
        var composer = File.ReadAllText(RepoTree.Path("scripts", "aixml-pylabview", "aixml2vi.py"));
        Assert.Contains("\"" + DiagramCleanup.ImageDisplayReportPrefix + "%s|%s\"", composer);
        // and the helper the step composes ships beside the server
        Assert.True(File.Exists(RepoTree.Path("scripts", DiagramCleanup.ImageDisplayHelperFileName)));
    }
}
