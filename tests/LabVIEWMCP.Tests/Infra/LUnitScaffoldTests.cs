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

    // ----------------------------------------------------------------------------------------
    // A TYPE WHOSE LITERAL CANNOT SURVIVE THE CONVERSION
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A class with one ordinary field and one `timestamp`, which ConvertAIXMLToVI empties -
    /// measured 2026-09-15 against five types that keep their literal.
    /// </summary>
    private static readonly LUnitScaffold.Field[] Sensor =
    [
        new("Tag", "string", "wt.vi", "rt.vi", "PT-101"),
        new("Last Seen", "timestamp", "wl.vi", "rl.vi", "3800000000"),
    ];

    /// <summary>
    /// ONE FIELD IS THE DEGENERATE CASE, and this is the rule the fourth cold build found by
    /// building a class that had it. With a single field there is no OTHER Write for an
    /// independence test to catch, so the file restates the round trip beside it - and when that
    /// field's literal is discarded as well, both sides are the default and it asserts nothing
    /// while its description promises otherwise.
    /// </summary>
    [Fact]
    public void IndependenceIsNotWorthGeneratingForASingleField()
    {
        Assert.False(LUnitScaffold.IndependenceAssertsSomething([Sensor[1]]));   // the timestamp
        Assert.False(LUnitScaffold.IndependenceAssertsSomething([Sensor[0]]));   // an ordinary one
        Assert.True(LUnitScaffold.IndependenceAssertsSomething(Sensor));         // the pair
    }

    /// <summary>
    /// THE LOOP BETWEEN THE GENERATOR AND THE CHECKER, closed in one assertion. The real AlarmGate
    /// suite shipped `tm_independence.xml` carrying `value="3800000000"` on the timestamp's write
    /// constant AND on its Expected - both silently emptied, so the assertion compared empty with
    /// empty. Whatever the generator emits must now pass the check that would have caught it.
    /// </summary>
    [Fact]
    public void IndependenceAuthorsNoLiteralTheConverterWouldDiscard() =>
        Assert.DoesNotContain(
            AixmlCheck.Check(LUnitScaffold.Independence("Sensor Test", "Sensor", Sensor)),
            f => f.Code == "timestampValueDiscarded");

    /// <summary>
    /// And the SENTENCE has to move with the constant. Asserting the default while the description
    /// still reads `must still read the value it was given` is a true result explained by a false
    /// claim - which is how `must be 0` came to describe a path field one fix earlier.
    /// </summary>
    [Fact]
    public void ItSaysWhatItIsActuallyAsserting()
    {
        var emitted = LUnitScaffold.Independence("Sensor Test", "Sensor", Sensor);

        Assert.Contains("Tag must still read the value it was given", emitted);
        Assert.DoesNotContain("Last Seen must still read the value it was given", emitted);
        Assert.Contains("Last Seen must still be empty", emitted);
    }

    /// <summary>
    /// The WRITE is kept, which is the opposite call from the round trip: this test's claim is that
    /// no OTHER Write disturbed the field, and a Write storing where it does not own still puts a
    /// non-default value here and fails the compare. Dropping the field would lose that.
    /// </summary>
    [Fact]
    public void TheFieldIsStillWrittenAndStillAsserted()
    {
        var emitted = LUnitScaffold.Independence("Sensor Test", "Sensor", Sensor);

        Assert.Contains("wl.vi", emitted);
        Assert.Contains("Expected Last Seen", emitted);
    }

    /// <summary>The ordinary field is untouched by any of this.</summary>
    [Fact]
    public void AnOrdinaryFieldKeepsItsValueOnBothSides()
    {
        var emitted = LUnitScaffold.Independence("Sensor Test", "Sensor", Sensor);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(emitted, "PT-101").Count);
        Assert.DoesNotContain("3800000000", emitted);
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

    /// <summary>
    /// THE `path` ROW IS THE ONE THAT MATTERS, and it was wrong until 2026-09-14. This table used
    /// to end `_ => "0"`, so the generated `Test Field Defaults.vi` asserted `Expected:0(Path)`
    /// against a class whose default is the empty path - measured on a cold build, where the suite
    /// reported a defect that did not exist. A generator that fails correct code is worse than one
    /// that fails loudly, because it reads as a fault in the code under test.
    ///
    /// The compound rows are here to pin the DELEGATION: this now calls TestTools.DefaultFor
    /// rather than keeping a second table, which is CLAUDE.md's rule about two implementations of
    /// one rule applied to literals instead of to the uid base.
    /// </summary>

    /// <summary>
    /// EVERY uid THIS GENERATOR EMITS MUST CLEAR LabVIEW's RESERVED PANEL-HEAP RANGE, and until
    /// 2026-09-14 none of them did. The bands ran from 100, so a five-file suite over a three-field
    /// class emitted 55 uids that LabVIEW logs `trying to override with non-reserved UID` for and
    /// renumbers anyway - measured on a cold build, where scripts/aixml_lint.py flagged all five
    /// files. `CLAUDE.md` records TestTools.UidBase = 4200 as numbering "everything the TOOLS
    /// emit"; this generator was simply not reached by that change.
    ///
    /// Asserted over the GENERATED TEXT rather than against the constants, so moving a band back
    /// under the floor fails here however it is spelled.
    /// </summary>
    [Fact]
    public void EveryEmittedUidClearsLabVIEWsReservedRange()
    {
        string[] emitted =
        [
            LUnitScaffold.RoundTrip("Apfel Test", "Apfel", Apfel[1]),
            LUnitScaffold.Defaults("Apfel Test", "Apfel", Apfel),
            LUnitScaffold.Independence("Apfel Test", "Apfel", Apfel),
        ];

        var low = emitted
            .SelectMany(x => System.Text.RegularExpressions.Regex.Matches(x, @"uid=""(\d+)"""))
            .Select(m => int.Parse(m.Groups[1].Value))
            .Where(uid => uid != 0 && uid < AixmlCheck.SafeUidBase)
            .Distinct().Order().ToList();

        Assert.Empty(low);
    }

    [Theory]
    [InlineData("string", "")]
    [InlineData("path", "")]
    [InlineData("bool", "false")]
    [InlineData("double", "0")]
    [InlineData("int32", "0")]
    [InlineData("array{double.Samples}", "[]")]
    [InlineData("cluster{bool.status,int32.code,string.source}", "[false,0,]")]
    public void DefaultsAreThePerTypeZero(string type, string expected)
        => Assert.Equal(expected, LUnitScaffold.DefaultFor(type));

    [Theory]
    [InlineData("string")]
    [InlineData("path")]
    [InlineData("bool")]
    [InlineData("double")]
    [InlineData("cluster{bool.status,int32.code,string.source}")]
    public void TheScaffoldAndTheClassTestGeneratorAgree(string type)
        => Assert.Equal(LabVIEWMcp.Tools.TestTools.DefaultFor(type), LUnitScaffold.DefaultFor(type));

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
