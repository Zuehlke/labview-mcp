using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The installed-Caraya adaptation of the test generators' assertions, offline. An older Caraya
/// names Assert Equal Value_Variant's inputs X and Y where the generators write Expected and
/// Actual - the user's bug report of 2026-10-12 - so the names are read off the installed VI and
/// the AIXML rewritten. The fixture is a real generator's output, not a hand-written call.
/// </summary>
public sealed class CarayaAssertTests
{
    private static string ClassTest() =>
        TestTools.ClassTestAixml(@"C:\cls\Test Netzteil.vi", "Netzteil",
        [
            new TestTools.ClassCase(0, "Voltage", "double", "12.5", "Voltage round trip",
                @"C:\cls\Write Voltage.vi", @"C:\cls\Read Voltage.vi", @"C:\cls\Netzteil.lvclass"),
            new TestTools.ClassCase(1, "Name", "string", "PSU", "Name round trip",
                @"C:\cls\Write Name.vi", @"C:\cls\Read Name.vi", @"C:\cls\Netzteil.lvclass"),
        ]);

    private static IEnumerable<string> AssertInputNames(string aixml) =>
        XElement.Parse(aixml).Descendants("Call")
            .Where(c => ((string?)c.Attribute("target") ?? "").EndsWith("Assert Equal Value_Variant.vi"))
            .SelectMany(c => ((string)c.Attribute("inputs")!).Split(','))
            .Select(i => i[..i.IndexOf(':')]);

    [Fact]
    public void AnOlderCarayaGetsXAndYOnEveryAssertion()
    {
        var adapted = CarayaAssert.Adapt(ClassTest(), CarayaAssert.Older);

        var names = AssertInputNames(adapted).ToList();
        Assert.Equal(2, names.Count(n => n == "X"));
        Assert.Equal(2, names.Count(n => n == "Y"));
        Assert.DoesNotContain("Expected", names);
        Assert.DoesNotContain("Actual", names);
        // the other inputs keep their names - both versions in the report carry them
        Assert.Contains("Register with caller test (F)", names);
        Assert.Contains("error in (no error)", names);
    }

    [Fact]
    public void TheNetsStayOnTheSameSide()
    {
        var original = ClassTest();
        var adapted = CarayaAssert.Adapt(original, CarayaAssert.Older);

        static Dictionary<string, string> Nets(string aixml, string name) =>
            XElement.Parse(aixml).Descendants("Call")
                .Where(c => ((string?)c.Attribute("target") ?? "").EndsWith("Assert Equal Value_Variant.vi"))
                .ToDictionary(c => (string)c.Attribute("uid")!,
                              c => ((string)c.Attribute("inputs")!).Split(',')
                                   .Single(i => i.StartsWith(name + ":"))[(name.Length + 1)..]);

        Assert.Equal(Nets(original, "Expected"), Nets(adapted, "X"));
        Assert.Equal(Nets(original, "Actual"), Nets(adapted, "Y"));
    }

    [Fact]
    public void ACurrentCarayaLeavesTheDocumentUntouched()
    {
        var original = ClassTest();
        Assert.Same(original, CarayaAssert.Adapt(original, CarayaAssert.Current));
    }

    [Fact]
    public void OnlyTheAssertCallIsRewritten()
    {
        const string aixml = """
            <VI _name="t.vi" description="">
              <Call target="Other.vi" inputs="Expected:1.value,Actual:2.value" outputs="" uid="4200" uid_parent="root"/>
              <Call target="Caraya.lvlib\3AAssert.lvclass\3AAssert Equal Value_Variant.vi" inputs="Expected:1.value,Actual:2.value,Label:3.value" outputs="" uid="4201" uid_parent="root"/>
            </VI>
            """;
        var adapted = CarayaAssert.Adapt(aixml, CarayaAssert.Older);

        Assert.Contains("target=\"Other.vi\" inputs=\"Expected:1.value,Actual:2.value\"", adapted);
        Assert.Contains("inputs=\"X:1.value,Y:2.value,Label:3.value\"", adapted);
    }

    [Theory]
    [InlineData("Expected,Actual,Label,error in (no error)", "Expected")]
    [InlineData("X,Y,Label,error in (no error)", "X")]
    [InlineData("Label,error in (no error)", null)]
    [InlineData("Expected,Y", null)]
    public void ThePairIsReadOffTheInputs(string inputs, string? expectedName)
    {
        var names = CarayaAssert.FromInputs(inputs.Split(','));
        Assert.Equal(expectedName, names?.Expected);
    }
}
