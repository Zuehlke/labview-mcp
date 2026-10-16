using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A diagram constant whose label sits LEFT of its value - a Boolean, an enum, a ring. The composer
/// widened the label to its text and moved nothing after it, so in the LayoutCheck project
/// (2026-10-16) `use system decimal point` ran across its own T/F and `create or replace` across
/// its enum, and the enum's item field kept the donor's 12 px: LabVIEW drew the glyph and the arrow
/// and no item at all. The composed VI is read back with pylabview and the parts' bounds checked.
/// SKIPS where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewInlineLabelTests
{
    private const string Enum = "uint16{open,open or create,create or replace,create,open (read-only)}";

    private static readonly string Probe = $"""
        <VI _name="Fit Probe.vi" description="Inline-labelled constants.">
          <Constant _name="create or replace" type="{Enum}" value="2" outputs="value:4201.v" uid="4201" uid_parent="root"/>
          <Constant _name="use system decimal point" type="bool" value="false" outputs="value:4202.v" uid="4202" uid_parent="root"/>
          <Indicator _name="Op" type="{Enum}" value="0" inputs="value:4201.v" uid="4203" uid_parent="root"/>
          <Indicator _name="Flag" type="bool" value="false" inputs="value:4202.v" uid="4204" uid_parent="root"/>
        </VI>
        """;

    private static (int T, int L, int B, int R) Bounds(XElement e)
    {
        var n = Regex.Matches(e.Element("bounds")!.Value, "-?\\d+").Select(m => int.Parse(m.Value)).ToArray();
        return (n[0], n[1], n[2], n[3]);
    }

    [Fact]
    public async Task An_inline_label_ends_where_the_value_begins_and_an_enum_shows_its_items()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "inline-label-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Fit Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);

            Directory.CreateDirectory(Path.Combine(dir, "x"));
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "FitProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());
            var constants = heap.Descendants("ddo")
                .Where(d => (string?)d.Attribute("class") is "stdRing" or "stdBool").ToList();
            Assert.Equal(2, constants.Count);

            foreach (var c in constants)
            {
                var parts = c.Element("partsList")!.Elements().Where(p => p.Element("bounds") is not null).ToList();
                var label = parts.Single(p => (string?)p.Attribute("class") == "label" && p.Element("partID")?.Value == "16");
                var value = parts.Where(p => p != label).Select(Bounds).ToList();
                Assert.True(Bounds(label).R <= value.Min(b => b.L),
                    $"{c.Attribute("class")}: label ends at {Bounds(label).R}, the value starts at {value.Min(b => b.L)}");
            }

            var ring = constants.Single(c => (string?)c.Attribute("class") == "stdRing");
            var items = ring.Element("partsList")!.Elements()
                .Single(p => (string?)p.Attribute("class") == "multiLabel" && p.Element("buf") is not null);
            var longest = Regex.Matches(items.Element("buf")!.Value, "\"([^\"]*)\"").Max(m => m.Groups[1].Value.Length);
            var field = Bounds(items);
            // a proportional font draws about 5 px a character here (CLAUDE.md, clipped comments)
            Assert.True(field.R - field.L >= longest * 5, $"item field {field.R - field.L} px for {longest} characters");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
