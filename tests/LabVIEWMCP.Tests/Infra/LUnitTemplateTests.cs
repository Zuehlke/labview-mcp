using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The shipped LUnit test-method skeletons in <c>scripts/templates/lunit/</c>.
///
/// WHY THEY ARE TESTED. They were lifted from six files that had actually been generated, run and
/// verified — `tests="6" failures="0"` with a negative control — and the value of shipping them is
/// entirely that they are still that. A well-meant edit that breaks the wiring would not fail
/// anywhere until somebody spent a whole run finding out, which is the cost the templates exist to
/// remove. So this pins the structure rather than the prose: fill every placeholder with a dummy and
/// the result must still be the AIXML shape §§3–6 of `docs/labview-lunit-testing.md` prescribes.
///
/// What is deliberately NOT pinned: the descriptions, the field names, the values. Those are the
/// placeholders.
/// </summary>
public sealed class LUnitTemplateTests
{
    private static readonly string[] Templates =
        ["round-trip.xml", "defaults.xml", "independence.xml"];

    private static string Load(string name)
    {
        var path = Res.FindRepoFile(Path.Combine("scripts", "templates", "lunit", name));
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    /// <summary>Every placeholder filled with something type-plausible, so the result parses.</summary>
    private static string Fill(string template)
    {
        var filled = template;
        foreach (var (token, value) in new (string, string)[]
        {
            ("TESTCLASS", "Auto Test"), ("CLASS", "Auto"),
            ("VI_DESCRIPTION", "What this test pins."), ("DESCRIPTION", "One assertion."),
            ("FIELD", "Top Speed"), ("TYPE", "double"), ("VALUE", "212.5"),
        })
            filled = filled.Replace("{{" + token + "}}", value);

        // The four-field templates number everything.
        string[] fields = ["Marke", "Top Speed", "Baujahr", "Bereit"];
        string[] types = ["string", "double", "int32", "bool"];
        string[] values = ["Zuehlke", "212.5", "2026", "true"];
        string[] defaults = ["", "0", "0", "false"];
        for (var i = 1; i <= 4; i++)
        {
            filled = filled
                .Replace($"{{{{FIELD{i}}}}}", fields[i - 1])
                .Replace($"{{{{TYPE{i}}}}}", types[i - 1])
                .Replace($"{{{{VALUE{i}}}}}", values[i - 1])
                .Replace($"{{{{DEFAULT{i}}}}}", defaults[i - 1])
                .Replace($"{{{{DESC{i}}}}}", $"Assertion {i}.")
                .Replace($"{{{{STUB_WRITE{i}}}}}", $"LVMCP Stub w{i}.vi")
                .Replace($"{{{{STUB_READ{i}}}}}", $"LVMCP Stub r{i}.vi");
        }
        return filled.Replace("{{STUB_WRITE}}", "LVMCP Stub w.vi")
                     .Replace("{{STUB_READ}}", "LVMCP Stub r.vi");
    }

    [Theory]
    [InlineData("round-trip.xml")]
    [InlineData("defaults.xml")]
    [InlineData("independence.xml")]
    public void EveryPlaceholderIsFillableAndTheResultIsValidXml(string name)
    {
        var filled = Fill(Load(name));

        var leftover = Regex.Matches(filled, @"\{\{[A-Z0-9_]+\}\}")
                            .Select(m => m.Value).Distinct().ToList();
        Assert.Empty(leftover);

        // AIXML is XML, and a template that does not parse cannot be generated from.
        var root = XElement.Parse(filled);
        Assert.Equal("VI", root.Name.LocalName);
    }

    /// <summary>
    /// The connector pane, which is the half of this that nothing else checks. LUnit needs pattern
    /// 4815 with the class terminals at 11 and 3 and the error terminals at 8 and 0; a pane whose
    /// numbers drift validates, generates and runs, and is wrong in a way only a human notices.
    /// </summary>
    [Theory]
    [InlineData("round-trip.xml")]
    [InlineData("defaults.xml")]
    [InlineData("independence.xml")]
    public void ThePaneIsLUnitsFourTwoTwoFourAssignment(string name)
    {
        var root = XElement.Parse(Fill(Load(name)));

        (string Name, string ConIdx, string Type) Terminal(string kind, string conIdx) =>
            root.Elements(kind)
                .Where(e => e.Attribute("conIdx")?.Value == conIdx)
                .Select(e => (e.Attribute("_name")!.Value, conIdx, e.Attribute("type")!.Value))
                .Single();

        // The class terminals go in as `path` stand-ins - the whole reason the route exists.
        var classIn = Terminal("Control", "11");
        var classOut = Terminal("Indicator", "3");
        Assert.Equal("Auto Test In", classIn.Name);
        Assert.Equal("Auto Test Out", classOut.Name);
        Assert.Equal("path", classIn.Type);
        Assert.Equal("path", classOut.Type);

        Assert.Equal("error in (no error)", Terminal("Control", "8").Name);
        Assert.Equal("error out", Terminal("Indicator", "0").Name);
    }

    /// <summary>
    /// The assertion target, with its escape intact. `\3A` is AIXML's colon; a template that lost it
    /// - to a shell, an editor or a well-meant "fix" - fails with `Unsupported SubVI` and the message
    /// names the target rather than the escaping.
    /// </summary>
    [Theory]
    [InlineData("round-trip.xml", 1)]
    [InlineData("defaults.xml", 4)]
    [InlineData("independence.xml", 4)]
    public void AssertionsCallTheBaseClassMalleableWithItsEscapeIntact(string name, int expected)
    {
        var template = Load(name);

        Assert.DoesNotContain("Test Case.lvclass:Pass If Equal", template);
        Assert.Equal(expected,
            Regex.Matches(template, @"target=""Test Case\.lvclass\\3APass If Equal\.vim""").Count);
    }

    /// <summary>
    /// The seed constant, which becomes a class constant on the swap and is found BY LABEL. A
    /// dynamic dispatch input is a required terminal, so a renamed seed means `lvai_swap_subvis`
    /// cannot find it and the finished VI is Error 1003 after everything reported success.
    /// </summary>
    [Theory]
    [InlineData("round-trip.xml")]
    [InlineData("defaults.xml")]
    [InlineData("independence.xml")]
    public void TheSeedConstantIsAPathNamedAfterTheClass(string name)
    {
        var root = XElement.Parse(Fill(Load(name)));

        var seed = root.Elements("Constant")
                       .Single(c => c.Attribute("_name")?.Value == "Auto seed");

        Assert.Equal("path", seed.Attribute("type")!.Value);
    }

    /// <summary>
    /// The difference between the two four-field templates, and it is the one that decides whether
    /// they test anything: `defaults` reads the UNTOUCHED seed four times, `independence` reads the
    /// object all four writes ran on. Swapping these produces tests that pass and prove nothing.
    /// </summary>
    [Fact]
    public void DefaultsReadsTheSeedWhileIndependenceReadsTheWrittenObject()
    {
        var defaults = XElement.Parse(Fill(Load("defaults.xml")));
        var independence = XElement.Parse(Fill(Load("independence.xml")));

        var defaultReads = defaults.Elements("Call")
            .Where(c => c.Attribute("target")!.Value.StartsWith("LVMCP Stub r"))
            .Select(c => c.Attribute("inputs")!.Value).ToList();
        Assert.Equal(4, defaultReads.Count);
        Assert.All(defaultReads, i => Assert.Contains("Auto in:110.value", i));

        var writtenReads = independence.Elements("Call")
            .Where(c => c.Attribute("target")!.Value.StartsWith("LVMCP Stub r"))
            .Select(c => c.Attribute("inputs")!.Value).ToList();
        Assert.Equal(4, writtenReads.Count);
        Assert.All(writtenReads, i => Assert.Contains("Auto in:123.Auto out", i));
    }

    /// <summary>
    /// Every `Call` after the first takes the previous one's `error out`. That chain is what forces
    /// execution order — in `defaults` the four reads share one class input and are otherwise
    /// independent, so without it LabVIEW may reorder them and the assertions read whatever ran
    /// first.
    /// </summary>
    [Theory]
    [InlineData("defaults.xml")]
    [InlineData("independence.xml")]
    public void TheErrorChainIsUnbroken(string name)
    {
        var calls = XElement.Parse(Fill(Load(name))).Elements("Call").ToList();

        for (var i = 1; i < calls.Count; i++)
        {
            var previous = calls[i - 1].Attribute("uid")!.Value;
            Assert.Contains($"error in (no error):{previous}.error out",
                            calls[i].Attribute("inputs")!.Value);
        }
    }

    /// <summary>The README is the recipe; shipping the skeletons without it is shipping a puzzle.</summary>
    [Fact]
    public void TheFolderShipsAReadmeAndNothingUndocumented()
    {
        Assert.NotNull(Res.FindRepoFile(Path.Combine("scripts", "templates", "lunit", "README.md")));

        var readme = Load("README.md");
        foreach (var template in Templates)
            Assert.Contains(template, readme);
    }
}
