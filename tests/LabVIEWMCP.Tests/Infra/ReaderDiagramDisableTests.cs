using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// vi2aixml.py on a Diagram Disable Structure: which frame is " Enabled " (issue #129).
///
/// THE FIXTURES ARE REAL ARTEFACTS: three VIs composed on 2026-11-09, then laid out and SAVED by
/// LabVIEW 2026, and RUN - each returns the index of the frame that executes (0, 2 and 10).
/// LabVIEW saved the frame-0 VI with no node-level activeDiag at all (it holds its default), the
/// frame-1 VI with activeDiag 01 and the frame-10 VI with 0A. The reader took a missing
/// activeDiag for "the last frame" and labelled the running frame " Disabled " - a JKI VI Tester
/// skip template read back as skipping its test body - and parsed the value as decimal.
/// </summary>
public sealed class ReaderDiagramDisableTests
{
    private static async Task<XElement?> ReadAsync(string fixture)
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return null;
        var path = Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "disable", fixture);
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, path, output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("Disable Frame0 Enabled.vi", 0, "1")]
    [InlineData("Disable Frame1 Enabled.vi", 1, "2")]
    [InlineData("Disable Frame10 Enabled.vi", 10, "10")]
    public async Task The_frame_that_runs_is_the_enabled_one(string fixture, int enabled, string itsConstant)
    {
        if (await ReadAsync(fixture) is not { } vi) return;

        var frames = vi.Descendants("Node")
            .Single(n => (string?)n.Attribute("_name") == "Diagram Disable Structure")
            .Elements("Diagram").ToList();

        Assert.Equal(enabled, frames.FindIndex(f => (string?)f.Attribute("selector") == " Enabled "));
        Assert.Single(frames, f => (string?)f.Attribute("selector") == " Enabled ");
        // the enabled frame holds the constant the VI returned when it was run
        Assert.Equal(itsConstant, (string?)frames[enabled].Element("Constant")!.Attribute("value"));
    }

    /// <summary>
    /// The Conditional Disable twin, checked 2026-11-09 for the same fault and not found: the
    /// reader takes each frame's selector from its own condition, never from activeDiag. Four
    /// VIs composed, saved by LabVIEW and RUN (each frame returns its index; the Default frame
    /// first, last, in the middle, and a `!=` condition that holds on the desktop), exported by
    /// the reader and by NI's converter: identical selectors, frame by frame.
    /// </summary>
    [Theory]
    [InlineData("Cond A Default First.vi", " Default | RUN_TIME_ENGINE==True ")]
    [InlineData("Cond B Cond First.vi", " RUN_TIME_ENGINE==False | Default ")]
    [InlineData("Cond C Default Middle.vi", " TARGET_TYPE==RT | Default | RUN_TIME_ENGINE!=False ")]
    [InlineData("Cond D Ne Runs.vi", " TARGET_TYPE==RT | RUN_TIME_ENGINE!=True | Default ")]
    public async Task Each_conditional_frame_keeps_its_own_condition(string fixture, string niSelectors)
    {
        if (await ReadAsync(fixture) is not { } vi) return;

        var frames = vi.Descendants("Node")
            .Single(n => (string?)n.Attribute("_name") == "Conditional Disable Structure")
            .Elements("Diagram").ToList();

        Assert.Equal(niSelectors.Split('|').Select(s => " " + s.Trim() + " "),
                     frames.Select(f => (string?)f.Attribute("selector")));
        // frame i holds the constant i: the conditions are not shifted against the frames
        for (var i = 0; i < frames.Count; i++)
            Assert.Equal(i.ToString(), (string?)frames[i].Element("Constant")!.Attribute("value"));
    }
}
