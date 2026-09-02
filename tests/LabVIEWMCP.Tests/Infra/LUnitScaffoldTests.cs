using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// <see cref="LUnitScaffold"/> — the emitter behind `lvai_lunit_scaffold_class_tests`.
///
/// THE SHIPPED TEMPLATES ARE THE SPECIFICATION. `scripts/templates/lunit/*.xml` were lifted
/// line-for-line from six files that ran green with a negative control, so the emitter is checked
/// against them rather than against itself. Drift becomes a test failure here instead of a wasted
/// LabVIEW run.
///
/// The comparison is uid-NORMALISED. A uid names a wire and nothing else; holding the numbers
/// identical would pin an accident of how the first files happened to be written, and would have
/// blocked the emitter from using collision-free bands — which it needs, because the templates'
/// organically-grown numbering would have overlapped its read and assert ranges at six fields.
/// What must match is the graph, and that is what this compares.
/// </summary>
public sealed class LUnitScaffoldTests
{
    private static readonly LUnitScaffold.Field[] Apfel =
    [
        new("Sorte", "string", "LVMCP Stub 9da62de196.vi", "LVMCP Stub 7c60a599c4.vi",
            "Cox Orange  Renette"),
        new("Gewicht g", "double", "LVMCP Stub 57df820953.vi", "LVMCP Stub 265773b388.vi", "167.75"),
        new("Erntejahr", "int32", "LVMCP Stub 8e69384849.vi", "LVMCP Stub 9d3efc7bac.vi", "2023"),
        new("Bio", "bool", "LVMCP Stub 6038bd4ca0.vi", "LVMCP Stub 301a066816.vi", "true"),
    ];

    private static string Template(string name)
    {
        var path = Res.FindRepoFile(Path.Combine("scripts", "templates", "lunit", name));
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    /// <summary>The shipped skeleton with the same class, fields, stubs and values filled in.</summary>
    private static string FilledTemplate(string name)
    {
        // round-trip.xml is the single-field skeleton, so its placeholders are UNNUMBERED. It is
        // filled with the same field the emitter is handed below.
        var one = Apfel[1];
        var filled = Template(name)
            .Replace("{{TESTCLASS}}", "Apfel Test")
            .Replace("{{CLASS}}", "Apfel")
            .Replace("{{VI_DESCRIPTION}}", "What this test pins.")
            .Replace("{{DESCRIPTION}}", "One assertion.")
            .Replace("{{FIELD}}", one.Name)
            .Replace("{{TYPE}}", one.Type)
            .Replace("{{VALUE}}", one.Value)
            .Replace("{{STUB_WRITE}}", one.WriteStub)
            .Replace("{{STUB_READ}}", one.ReadStub);
        for (var i = 0; i < Apfel.Length; i++)
        {
            var f = Apfel[i];
            filled = filled
                .Replace($"{{{{FIELD{i + 1}}}}}", f.Name)
                .Replace($"{{{{TYPE{i + 1}}}}}", f.Type)
                .Replace($"{{{{VALUE{i + 1}}}}}", f.Value)
                .Replace($"{{{{DEFAULT{i + 1}}}}}", LUnitScaffold.DefaultFor(f.Type))
                .Replace($"{{{{STUB_WRITE{i + 1}}}}}", f.WriteStub)
                .Replace($"{{{{STUB_READ{i + 1}}}}}", f.ReadStub);
        }
        return filled;
    }

    /// <summary>
    /// The graph, with every uid replaced by its order of first appearance and every free-text
    /// attribute dropped. Two AIXML files agreeing on this wire the same diagram.
    /// </summary>
    private static string Shape(string aixml)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var normalised = Regex.Replace(aixml, @"\b\d{2,4}\b", m =>
        {
            if (!map.TryGetValue(m.Value, out var index))
                map[m.Value] = index = map.Count;
            return "u" + index;
        });

        var root = XElement.Parse(normalised);
        var lines = root.Elements().Select(e => string.Join("|",
            new[] { e.Name.LocalName }
                .Concat(new[] { "type", "conIdx", "connection", "target", "inputs", "outputs", "uid" }
                    .Select(a => a + "=" + (e.Attribute(a)?.Value ?? "")))));
        return string.Join("\n", lines);
    }

    [Fact]
    public void TheRoundTripMatchesTheShippedSkeleton()
    {
        var emitted = LUnitScaffold.RoundTrip("Apfel Test", "Apfel", Apfel[1]);

        Assert.Equal(Shape(FilledTemplate("round-trip.xml")), Shape(emitted));
    }

    [Fact]
    public void TheDefaultsTestMatchesTheShippedSkeleton()
    {
        var emitted = LUnitScaffold.Defaults("Apfel Test", "Apfel", Apfel);

        Assert.Equal(Shape(FilledTemplate("defaults.xml")), Shape(emitted));
    }

    [Fact]
    public void TheIndependenceTestMatchesTheShippedSkeleton()
    {
        var emitted = LUnitScaffold.Independence("Apfel Test", "Apfel", Apfel);

        Assert.Equal(Shape(FilledTemplate("independence.xml")), Shape(emitted));
    }

    /// <summary>
    /// The reason the emitter does not reuse the templates' numbering: theirs collides. This is the
    /// case the shipped four-field skeletons cannot express, so nothing else would catch it.
    /// </summary>
    [Fact]
    public void SixFieldsProduceNoDuplicateUid()
    {
        LUnitScaffold.Field Extra(int n) =>
            new($"Feld {n}", "int32", $"LVMCP Stub w{n}.vi", $"LVMCP Stub r{n}.vi", $"{n}");
        LUnitScaffold.Field[] six = [.. Apfel, Extra(5), Extra(6)];

        foreach (var aixml in new[]
                 {
                     LUnitScaffold.Defaults("X Test", "X", six),
                     LUnitScaffold.Independence("X Test", "X", six),
                 })
        {
            var uids = XElement.Parse(aixml).Elements()
                               .Select(e => e.Attribute("uid")!.Value).ToList();
            Assert.Equal(uids.Count, uids.Distinct().Count());
        }
    }

    /// <summary>
    /// Every emitted file must parse and carry LUnit's pane, whatever the field count — the pane is
    /// the half neither validation nor a run can see.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void EveryShapeParsesAndCarriesTheLUnitPane(int count)
    {
        LUnitScaffold.Field[] fields =
            [.. Enumerable.Range(0, count).Select(i =>
                new LUnitScaffold.Field($"F{i}", "double", $"w{i}.vi", $"r{i}.vi", $"{i + 1}.5"))];

        foreach (var aixml in new[]
                 {
                     LUnitScaffold.RoundTrip("T Test", "T", fields[0]),
                     LUnitScaffold.Defaults("T Test", "T", fields),
                     LUnitScaffold.Independence("T Test", "T", fields),
                 })
        {
            var root = XElement.Parse(aixml);
            Assert.Equal("T Test In", Pane(root, "Control", "11"));
            Assert.Equal("error in (no error)", Pane(root, "Control", "8"));
            Assert.Equal("T Test Out", Pane(root, "Indicator", "3"));
            Assert.Equal("error out", Pane(root, "Indicator", "0"));
        }

        static string Pane(XElement root, string kind, string conIdx) =>
            root.Elements(kind).Single(e => e.Attribute("conIdx")?.Value == conIdx)
                .Attribute("_name")!.Value;
    }

    /// <summary>
    /// A comma in a description is not exotic — nearly every one this route writes has one — and an
    /// unescaped comma silently splits an `inputs=` list at the wrong place.
    /// </summary>
    [Fact]
    public void ReservedCharactersAreEscaped()
    {
        Assert.Equal(@"a\2Cb", LUnitScaffold.Escape("a,b"));
        Assert.Equal(@"a\3Ab", LUnitScaffold.Escape("a:b"));
        Assert.Equal(@"a\5Cb", LUnitScaffold.Escape(@"a\b"));
        Assert.Equal("&amp;&lt;&gt;&quot;", LUnitScaffold.Escape("&<>\""));

        // Backslash first, or the escapes introduced above get escaped again.
        Assert.Equal(@"\5C2C", LUnitScaffold.Escape(@"\2C"));
    }

    [Theory]
    [InlineData("string", "")]
    [InlineData("bool", "false")]
    [InlineData("double", "0")]
    [InlineData("int32", "0")]
    public void DefaultsAreThePerTypeZero(string type, string expected)
        => Assert.Equal(expected, LUnitScaffold.DefaultFor(type));

    /// <summary>
    /// The distinction that decides whether the two whole-class tests mean anything: `defaults`
    /// reads the untouched seed, `independence` reads the object every write ran on.
    /// </summary>
    [Fact]
    public void DefaultsReadsTheSeedAndIndependenceReadsTheWrittenObject()
    {
        var defaults = XElement.Parse(LUnitScaffold.Defaults("A Test", "A", Apfel));
        var independence = XElement.Parse(LUnitScaffold.Independence("A Test", "A", Apfel));

        var seed = defaults.Elements("Constant")
                           .Single(c => c.Attribute("_name")!.Value == "A seed")
                           .Attribute("uid")!.Value;

        var defaultReads = Reads(defaults);
        Assert.Equal(4, defaultReads.Count);
        Assert.All(defaultReads, i => Assert.Contains($"A in:{seed}.value", i));

        var lastWrite = independence.Elements("Call")
            .Last(c => c.Attribute("target")!.Value.StartsWith("LVMCP Stub 9da62de196")
                    || c.Attribute("target")!.Value.StartsWith("LVMCP Stub 57df820953")
                    || c.Attribute("target")!.Value.StartsWith("LVMCP Stub 8e69384849")
                    || c.Attribute("target")!.Value.StartsWith("LVMCP Stub 6038bd4ca0"))
            .Attribute("uid")!.Value;
        Assert.All(Reads(independence), i => Assert.Contains($"A in:{lastWrite}.A out", i));

        static List<string> Reads(XElement root) =>
            [.. root.Elements("Call")
                    .Where(c => c.Attribute("target")!.Value is "LVMCP Stub 7c60a599c4.vi"
                             or "LVMCP Stub 265773b388.vi" or "LVMCP Stub 9d3efc7bac.vi"
                             or "LVMCP Stub 301a066816.vi")
                    .Select(c => c.Attribute("inputs")!.Value)];
    }
}
