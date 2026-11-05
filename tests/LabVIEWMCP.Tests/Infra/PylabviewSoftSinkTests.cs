using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A receiving terminal flagged 0x4000 instead of 0x8000. Every input of the code left of MP3
/// Parse Frames.vi's loop carried 0x4000, the reader took each of those inputs for a second source,
/// noted "signal has 2 sources" and dropped the wire: 24 wires, File Data among them, and the
/// export read that half of the diagram as unwired (2026-11-04). THE FIXTURE IS THE REAL VI.
/// With the fix, the export read back and recomposed is executable and reads back fact for fact.
/// SKIPS where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewSoftSinkTests
{
    [Fact]
    public async Task Inputs_flagged_0x4000_keep_their_wires()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return;
        var fixture = RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "MP3 Parse Frames.vi");
        var output = Path.Combine(Path.GetTempPath(), "softsink-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, fixture, output, 120, default);
            Assert.True(result.Written, result.Message);
            var text = await File.ReadAllTextAsync(output);
            Assert.DoesNotContain("sources -->", text);
            var vi = XElement.Load(output);

            XElement ById(string uid) => vi.Descendants().Single(e => (string?)e.Attribute("uid") == uid);
            static string[] Wired(XElement e, string attr) =>
                ((string?)e.Attribute(attr) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => !x.EndsWith(':')).ToArray();

            // the five-input Add and the three-input AND left of the loop, each with its result
            Assert.Equal(5, Wired(ById("119"), "inputs").Length);
            Assert.Single(Wired(ById("119"), "outputs"));
            Assert.Equal(3, Wired(ById("111"), "inputs").Length);
            Assert.Single(Wired(ById("111"), "outputs"));
            // File Data into the first Read Fields call and into the loop
            Assert.Contains("Data:100.value", (string?)ById("103").Attribute("inputs"));
            Assert.Equal("value:100.value", (string?)ById("100").Attribute("outputs"));
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }
}
