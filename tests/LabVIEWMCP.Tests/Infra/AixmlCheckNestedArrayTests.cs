using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// `array{array{double}}` is refused as an ERROR (`arrayNested`), as scripts/aixml_lint.py has
/// refused it since 2026-09-09. Measured 2026-11-01: NI's converter answered Error 53 on it and the
/// pylabview composer wrote an eBad VI. The fixture is the real document - the reader's export of
/// that broken TDMS read-back from the tank-monitor cold build (docs/cold-build-pc-ai-di-tdms.md).
/// </summary>
public sealed class AixmlCheckNestedArrayTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(
        RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "aixml", "tdms-read-nested-array.xml"));

    [Fact]
    public void TheMeasuredDocumentIsRefusedOncePerNestedType()
    {
        var nested = AixmlCheck.Check(Fixture()).Where(f => f.Code == "arrayNested").ToList();
        Assert.Equal(4, nested.Count);                        // two indicators, two type constants
        Assert.All(nested, f => Assert.Equal(AixmlCheck.Severity.Error, f.Severity));
        Assert.Contains("array.2{double}", nested[0].Message);
    }

    [Fact]
    public void TheSameDocumentWithTwoDimensionalArraysIsClean()
    {
        var fixedDoc = Fixture().Replace("array{array{double}}", "array.2{double}")
                                .Replace("array{array{uint8}}", "array.2{uint8}");
        Assert.DoesNotContain(AixmlCheck.Check(fixedDoc), f => f.Code == "arrayNested");
    }

    [Fact]
    public void AnArrayOfClustersHoldingAnArrayIsNotNested()
    {
        const string doc = """
            <VI _name="T.vi" description="d">
              <Constant _name="c" outputs="value:4200.value" type="array{cluster{array{double}.Values,string.Name}}" uid="4200" uid_parent="root" value="[]"/>
            </VI>
            """;
        Assert.DoesNotContain(AixmlCheck.Check(doc), f => f.Code == "arrayNested");
    }
}
