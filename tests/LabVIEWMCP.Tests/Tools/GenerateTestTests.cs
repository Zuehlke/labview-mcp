using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The test-VI generator, offline: case parsing and the AIXML it authors. No LabVIEW involved.
///
/// The one that earns its keep is <see cref="EveryAssertionHangsOffDefineTest_NotOffThePreviousOne"/>.
/// Chaining the assertions is the tidier-looking diagram and it silently loses cases - measured, a
/// broken subject reported one failure where two were due, because the later cases never ran. That
/// is invisible in a green run and nearly invisible in a red one, so it is pinned here.
/// </summary>
public sealed class GenerateTestTests
{
    private static readonly List<TestTools.Terminal> Terminals =
    [
        new("celsius", "double", IsInput: true),
        new("fahrenheit", "double", IsInput: false),
    ];

    private static string Author(params TestTools.Case[] cases) =>
        TestTools.TestAixml(@"C:\t\Test Celsius To Fahrenheit.vi", "Celsius To Fahrenheit",
                            "LVMCP Stub abc.vi", cases, Terminals);

    private static TestTools.Case OneCase(string label, string input, string expected) =>
        new(label, new() { ["celsius"] = input }, new() { ["fahrenheit"] = expected });

    // ------------------------------------------------------------------ the diagram

    [Fact]
    public void EveryAssertionHangsOffDefineTest_NotOffThePreviousOne()
    {
        var xml = Author(OneCase("boiling", "100", "212"),
                         OneCase("freezing", "0", "32"),
                         OneCase("crossover", "-40", "-40"));
        var root = System.Xml.Linq.XElement.Parse(xml);

        var define = root.Elements("Call")
            .First(c => ((string?)c.Attribute("target"))!.Contains("Define Test"))
            .Attribute("uid")!.Value;

        var assertions = root.Elements("Call")
            .Where(c => ((string?)c.Attribute("target"))!.Contains("Assert Equal"))
            .ToList();

        Assert.Equal(3, assertions.Count);
        Assert.All(assertions, a =>
            Assert.Contains($"error in (no error):{define}.error out",
                            (string?)a.Attribute("inputs")));
    }

    [Fact]
    public void TheDocumentCarriesNoXmlComment_BecauseTheGeneratorRefusesOne()
    {
        // Measured 2026-08-27: a `<!-- case 1 -->` between the groups makes the WHOLE document
        // unparseable - `Error 42 ... Generic error`, which names nothing and points nowhere. The
        // same file with the comment lines stripped validates and generates. Easy to reintroduce
        // while making the output readable, and the error gives no clue, so it is pinned here.
        Assert.DoesNotContain("<!--", Author(OneCase("boiling", "100", "212")));
    }

    [Fact]
    public void TheAssertionsAreMergedIntoOneErrorOut()
    {
        var xml = Author(OneCase("a", "100", "212"), OneCase("b", "0", "32"));
        var root = System.Xml.Linq.XElement.Parse(xml);

        // Two assertions need one merge; three need two. The chain is what collapses them.
        Assert.Single(root.Elements("Node"),
            n => (string?)n.Attribute("_name") == "Merge Errors");
        Assert.Single(root.Elements("Indicator"),
            i => (string?)i.Attribute("_name") == "error out");
    }

    [Fact]
    public void OneCallToTheSubjectPerCase_NotOnePerAssertion()
    {
        var twoOutputs = new List<TestTools.Terminal>
        {
            new("celsius", "double", IsInput: true),
            new("fahrenheit", "double", IsInput: false),
            new("kelvin", "double", IsInput: false),
        };
        var oneCaseTwoChecks = new TestTools.Case(
            "both scales", new() { ["celsius"] = "100" },
            new() { ["fahrenheit"] = "212", ["kelvin"] = "373.15" });

        var xml = TestTools.TestAixml(@"C:\t\Test X.vi", "X", "LVMCP Stub abc.vi",
                                      [oneCaseTwoChecks], twoOutputs);
        var root = System.Xml.Linq.XElement.Parse(xml);

        Assert.Single(root.Elements("Call"),
            c => (string?)c.Attribute("target") == "LVMCP Stub abc.vi");
        Assert.Equal(2, root.Elements("Call")
            .Count(c => ((string?)c.Attribute("target"))!.Contains("Assert Equal")));
    }

    [Fact]
    public void AConstantTakesTheTerminalsOwnType_NotAGuessedOne()
    {
        var strings = new List<TestTools.Terminal>
        {
            new("text in", "string", IsInput: true),
            new("text out", "string", IsInput: false),
        };
        var xml = TestTools.TestAixml(@"C:\t\Test X.vi", "X", "LVMCP Stub abc.vi",
            [new("round trip", new() { ["text in"] = "abc" }, new() { ["text out"] = "abc" })],
            strings);

        Assert.DoesNotContain("type=\"double\"", xml);
        Assert.Contains("type=\"string\" ", xml);
    }

    [Fact]
    public void AnInputWithNoValueIsLeftUnwired_SoTheSubjectsOwnDefaultStands()
    {
        var xml = TestTools.TestAixml(@"C:\t\Test X.vi", "X", "LVMCP Stub abc.vi",
            [new("defaults", [], new() { ["fahrenheit"] = "32" })], Terminals);
        var root = System.Xml.Linq.XElement.Parse(xml);

        var call = root.Elements("Call").First(c => (string?)c.Attribute("target") == "LVMCP Stub abc.vi");
        Assert.Equal("celsius:", (string?)call.Attribute("inputs"));
    }

    [Fact]
    public void TheLabelIsQualifiedPerOutput_OnlyWhenACaseChecksMoreThanOne()
    {
        var single = Author(OneCase("boiling point", "100", "212"));
        Assert.Contains("value=\"boiling point\"", single);
        Assert.DoesNotContain("boiling point - fahrenheit", single);

        var twoOutputs = new List<TestTools.Terminal>
        {
            new("celsius", "double", IsInput: true),
            new("fahrenheit", "double", IsInput: false),
            new("kelvin", "double", IsInput: false),
        };
        var multi = TestTools.TestAixml(@"C:\t\Test X.vi", "X", "LVMCP Stub abc.vi",
            [new("both", new() { ["celsius"] = "100" },
                 new() { ["fahrenheit"] = "212", ["kelvin"] = "373.15" })],
            twoOutputs);

        // A JUnit report with two testcases called "both" is unreadable when one of them fails.
        Assert.Contains("value=\"both - fahrenheit\"", multi);
        Assert.Contains("value=\"both - kelvin\"", multi);
    }

    // ------------------------------------------------------------------ the cases

    [Fact]
    public void ACaseNamingATerminalTheSubjectDoesNotHaveIsCaughtBeforeGeneration()
    {
        var typo = new TestTools.Case("typo", new() { ["celcius"] = "100" },
                                      new() { ["fahrenheit"] = "212" });

        // Caught here because AIXML's own complaint would name the generated constant, not the
        // case that produced it - and 'celcius' for 'celsius' is exactly the mistake that gets made.
        Assert.Equal("'celcius'", TestTools.Unknown([typo], Terminals));
        Assert.Null(TestTools.Unknown([OneCase("fine", "100", "212")], Terminals));
    }

    [Theory]
    [InlineData("", "at least one case")]
    [InlineData("not json", "not JSON")]
    [InlineData("[]", "non-empty JSON array")]
    [InlineData("""[{"inputs":{"celsius":"100"},"expect":{"fahrenheit":"212"}}]""", "\"label\"")]
    [InlineData("""[{"label":"x","inputs":{"celsius":"100"}}]""", "asserts nothing")]
    [InlineData("""[{"label":"x","expect":"212"}]""", "not an object")]
    public void AMalformedCaseListIsRefusedByName(string json, string expected) =>
        Assert.Contains(expected,
            Assert.Throws<ArgumentException>(() => TestTools.Case.ParseAll(json)).Message);

    [Fact]
    public void AWellFormedCaseListParses()
    {
        var cases = TestTools.Case.ParseAll(
            """[{"label":"boiling point","inputs":{"celsius":"100"},"expect":{"fahrenheit":"212"}}]""");

        var one = Assert.Single(cases);
        Assert.Equal("boiling point", one.Label);
        Assert.Equal("100", one.Inputs["celsius"]);
        Assert.Equal("212", one.Expect["fahrenheit"]);
    }
}
