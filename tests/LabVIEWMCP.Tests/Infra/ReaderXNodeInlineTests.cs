using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// vi2aixml.py writes an XNODE as NI's export does - a Call of the .xnode - and, after &lt;/VI&gt;,
/// the code it GENERATED (`&lt;?lvmcp xnodeinline?&gt;`): a one-frame Flat Sequence labelled with the
/// .xnode, holding the xNode's own diagram, each tunnel naming the Call terminal it stands for
/// (`xterm`). The composer puts that frame in the Call's place (palette sweep, 2026-11-19;
/// docs/pylabview-palette-coverage.md). It cannot build an xNode, and {LV.SubVI} Replace builds one
/// only in its DEFAULT state - an Error Ring would lose the error it names. The generated code
/// keeps it: NI's own Check Color Table Size.vi (three Error Rings set to error 1), read, composed
/// and RUN, answered error 1 for depth 0, 1, 4 and 8 and no error for 24 - as the original did.
///
/// The fixture is the INSTALLED VI (vi.lib\picture\jpeg.llb), read in place: the test is skipped
/// on a machine without LabVIEW 2026 or without the bundled reader.
/// </summary>
public sealed class ReaderXNodeInlineTests
{
    private static string? Installed() =>
        LabViewRoots.InUse()
            .Select(root => Path.Combine(root, "vi.lib", "picture", "jpeg.llb", "Check Color Table Size.vi"))
            .FirstOrDefault(p => File.Exists(Path.GetDirectoryName(p)!));

    private static async Task<string?> ReadAsync()
    {
        if (AixmlComposer.Locate(out _) is not { } setup || Installed() is not { } path) return null;
        var output = Path.Combine(Path.GetTempPath(), "reader-xnode-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, path, output, 120, default);
            Assert.True(result.Written, result.Message);
            return await File.ReadAllTextAsync(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task An_error_ring_keeps_its_call_and_carries_the_code_it_generated()
    {
        if (await ReadAsync() is not { } text) return;

        var body = XElement.Parse(text[..(text.LastIndexOf("</VI>", StringComparison.Ordinal) + 5)]);
        var calls = body.Descendants("Call")
            .Where(c => (string?)c.Attribute("target") == "Error Ring.xnode").ToList();
        Assert.Equal(3, calls.Count);

        var frames = Regex.Matches(text, "<\\?lvmcp\\s+xnodeinline\\s+uid=\"([^\"]*)\"\\s+frame=\"([^\"]*)\"\\s*\\?>")
            .ToDictionary(m => m.Groups[1].Value, m => XElement.Parse(WebUtility.HtmlDecode(m.Groups[2].Value)));
        foreach (var call in calls)
        {
            var frame = frames[(string)call.Attribute("uid")!];
            Assert.Equal("Flat Sequence Frame", (string?)frame.Attribute("_name"));
            Assert.Equal("Error Ring.xnode", (string?)frame.Attribute("label"));
            // the Error Ring's configuration survives as the generated constants
            Assert.Contains(frame.Elements("Call"), c => (string?)c.Attribute("target") == "Error Cluster From Error Code.vi");
            Assert.Equal("1", (string?)frame.Elements("Constant")
                .Single(c => (string?)c.Attribute("_name") == "error code (0)").Attribute("value"));
            // its output leaves the frame through a tunnel standing for the Call's `error out`
            Assert.Contains(frame.Elements("Tunnel"), t => (string?)t.Attribute("xterm") == "error out");
        }
    }
}
