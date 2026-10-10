using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// vi2aixml.py on a VI LabVIEW SAVED with calls into classes - a Caraya test whose four accessor
/// calls are DYNAMIC DISPATCH (dynIUse nodes) and whose asserts are members of a class inside a
/// library.
///
/// THE FIXTURE IS THE REAL ARTEFACT: `Test Rectangle Accessors.vi` exactly as the pylabview route
/// left it on 2026-10-11 - composed, its sockets swapped by LabVIEW's own Replace, saved by
/// LabVIEW. On that file the reader used to name EVERY call target `?` and drop the four accessor
/// calls with their wires: pylabview leaves such a link block binary, the binary fallback looked
/// for the name count at one fixed offset (LabVIEW wrote it at two others, one after a pad byte),
/// and a dynIUse node was not read at all. The swap tool's verify read that export and called a
/// correct swap a failure. NI's own export of the same file named all seven calls.
/// </summary>
public sealed class ReaderClassCallTests
{
    private static string Fixture =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader",
                     "Test Rectangle Accessors.vi");

    private static async Task<XElement?> ReadAsync()
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return null;
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, Fixture, output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Every_call_into_a_class_is_named_as_NI_names_it()
    {
        if (await ReadAsync() is not { } vi) return;

        var targets = vi.Descendants("Call").Select(c => (string?)c.Attribute("target")).ToList();

        Assert.DoesNotContain("?", targets);
        Assert.Equal(
            [
                "Caraya.lvlib:Assert.lvclass:Assert Equal Value_Variant.vi",
                "Caraya.lvlib:Assert.lvclass:Assert Equal Value_Variant.vi",
                "Caraya.lvlib:Test.lvclass:Define Test.vi",
                "Rectangle.lvclass:Read Height.vi",
                "Rectangle.lvclass:Read Width.vi",
                "Rectangle.lvclass:Write Height.vi",
                "Rectangle.lvclass:Write Width.vi",
            ],
            targets.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_assert_reads_the_accessors_output_and_not_nothing()
    {
        if (await ReadAsync() is not { } vi) return;

        // the wire NI's export shows: Read Width's Width into the first assert's Actual
        var readWidth = vi.Descendants("Call")
            .Single(c => (string?)c.Attribute("target") == "Rectangle.lvclass:Read Width.vi");
        var widthNet = ((string)readWidth.Attribute("outputs")!).Split(',')
            .Single(p => p.StartsWith("Width:", StringComparison.Ordinal))["Width:".Length..];

        Assert.Contains(vi.Descendants("Call"),
            c => ((string?)c.Attribute("inputs") ?? "").Split(',').Contains("Actual:" + widthNet));
    }
}
