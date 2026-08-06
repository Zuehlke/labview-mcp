using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// The VI Server catalogue is the one embedded resource that must never be served whole: the
/// two TSVs are 400 kB together, so every test here is really about the same promise - a query
/// returns ROWS, the cap is honest about what it dropped, and the bytes still match docs/.
/// </summary>
public class ViServerKnowledgeTests
{
    [Theory]
    [InlineData("vi-server-reference.md", "docs/vi-server-reference.md")]
    [InlineData("vi-server-methods.tsv", "docs/vi-server-methods.tsv")]
    [InlineData("vi-server-properties.tsv", "docs/vi-server-properties.tsv")]
    [InlineData("lvlib-lvclass-structure.md", "docs/lvlib-lvclass-structure.md")]
    // Not documents but assets: a binary-only install must be able to hand out the agent and
    // the working rules, because neither is reachable through a tool.
    [InlineData("labview-doc-generator.md", ".claude/agents/labview-doc-generator.md")]
    [InlineData("CLAUDE.md", "CLAUDE.md")]
    public void EmbeddedResourceIsByteIdenticalToTheFileInDocs(string resource, string relative)
    {
        var onDisk = Res.FindRepoFile(relative);
        Assert.NotNull(onDisk);

        var expected = File.ReadAllText(onDisk!).Replace("\r\n", "\n");
        var embedded = KnowledgeTools.Load(resource).Replace("\r\n", "\n");

        Assert.Equal(expected, embedded);
    }

    [Fact]
    public void CataloguesCarryTheExpectedHeaderAndBulk()
    {
        var methods = KnowledgeTools.Load("vi-server-methods.tsv");
        var properties = KnowledgeTools.Load("vi-server-properties.tsv");

        Assert.StartsWith("class\tmethod\tparameters\treturns", methods);
        Assert.StartsWith("class\tproperty\taccess", properties);
        Assert.True(methods.Length > 100_000, $"methods look truncated ({methods.Length} chars)");
        Assert.True(properties.Length > 100_000, $"properties look truncated ({properties.Length} chars)");
    }

    [Fact]
    public void WithoutArgumentsItGuidesInsteadOfDumpingTheCatalogue()
    {
        var text = KnowledgeTools.ViServerReference();

        Assert.Contains("Classes:", text);
        Assert.Contains("{LV.VI}", text);
        // The whole point: the caller must not receive 400 kB of rows.
        Assert.True(text.Length < 20_000, $"no-argument answer is too large ({text.Length} chars)");
    }

    [Fact]
    public void TheMethodThatUnlockedIconAndConnectorPaneIsFindable()
    {
        var text = KnowledgeTools.ViServerReference(query: "To HTML", cls: "LV.VI");

        Assert.Contains("Print VI To HTML", text);
        Assert.Contains("Image Directory", text);   // its terminal names come with it
    }

    [Fact]
    public void ExactClassWinsOverClassesThatMerelyContainTheText()
    {
        // {LV.VIRefnum} sorts before {LV.VI} because '}' sorts after every letter. A substring
        // filter plus the row cap would return only the near-misses.
        var text = KnowledgeTools.ViServerReference(cls: "LV.VI", kind: "methods");

        Assert.Contains("{LV.VI}\t", text);
        Assert.DoesNotContain("{LV.VIRefnum}", text);
    }

    [Fact]
    public void ClassFilterAcceptsBracedAndBareAndWrongCase()
    {
        foreach (var cls in new[] { "{LV.VI}", "LV.VI", "lv.vi" })
        {
            var text = KnowledgeTools.ViServerReference(query: "Print VI To HTML", cls: cls);
            Assert.Contains("Print VI To HTML", text);
        }
    }

    [Fact]
    public void QueryMatchesTheNameColumnRatherThanParameters()
    {
        // "Image Directory" is a PARAMETER of Print VI To HTML, never a method name. Matching
        // whole lines would return that row and mislead the caller into wiring a method that
        // does not exist, so the correct answer is that nothing matched.
        var text = KnowledgeTools.ViServerReference(query: "Image Directory", kind: "methods");

        Assert.Contains("Nothing matched", text);
    }

    [Fact]
    public void PropertiesForTheCallGraphAreThere()
    {
        var text = KnowledgeTools.ViServerReference(query: "Callees", kind: "properties");

        Assert.Contains("Callees' Names", text);
        Assert.Contains("{LV.VI}", text);
    }

    [Fact]
    public void KindSelectsOneCatalogue()
    {
        var methodsOnly = KnowledgeTools.ViServerReference(cls: "LV.VI", kind: "methods");
        var propertiesOnly = KnowledgeTools.ViServerReference(cls: "LV.VI", kind: "properties");

        Assert.Contains("METHODS", methodsOnly);
        Assert.DoesNotContain("PROPERTIES", methodsOnly);
        Assert.Contains("PROPERTIES", propertiesOnly);
        Assert.DoesNotContain("METHODS", propertiesOnly);
    }

    [Fact]
    public void TruncationIsReportedRatherThanSilent()
    {
        // {LV.VI} has 77 methods; asking for 5 must say so.
        var text = KnowledgeTools.ViServerReference(cls: "LV.VI", kind: "methods", limit: 5);

        Assert.Contains("more method rows match", text);
    }

    [Fact]
    public void LimitIsClampedRatherThanTrusted()
    {
        foreach (var limit in new[] { 0, -7, 10_000 })
        {
            var text = KnowledgeTools.ViServerReference(cls: "LV.VI", kind: "methods", limit: limit);
            Assert.Contains("{LV.VI}", text);
        }
    }

    [Fact]
    public void NoMatchExplainsTheTwoSpellingsInsteadOfReturningNothing()
    {
        var text = KnowledgeTools.ViServerReference(query: "definitely no such method");

        Assert.Contains("Nothing matched", text);
        Assert.Contains("Print.VI To Printer", text);   // the dotted/space spelling hint
        Assert.Contains("Classes:", text);
    }

    [Fact]
    public void LvlibReferenceServesSectionsAndTheScopeRule()
    {
        var toc = KnowledgeTools.LvlibReference();
        Assert.NotEmpty(toc);

        var all = KnowledgeTools.LvlibReference("all");
        Assert.Contains("NI.LibItem.Scope", all);
        Assert.Contains("NI.ClassItem.MethodScope", all);
    }

    [Fact]
    public void ScriptsDirectoryIsEitherAbsentOrARealDirectory()
    {
        // The test project does not copy scripts/, so null is the expected answer here; what
        // must never happen is a path that is reported and does not exist.
        var path = StatusTools.ScriptsDirectory();
        if (path is not null) Assert.True(Directory.Exists(path), $"reported but missing: {path}");
    }

    [Fact]
    public void ClaudeAssetsDirectoryIsEitherAbsentOrARealDirectory()
    {
        var path = StatusTools.ClaudeAssetsDirectory();
        if (path is not null) Assert.True(Directory.Exists(path), $"reported but missing: {path}");
    }

    [Fact]
    public void TheAgentTravelsWithTheBinaryAndNamesItsOwnTools()
    {
        // A binary-only install has no repository, so the agent has to come out of the assembly.
        var agent = KnowledgeTools.Load("labview-doc-generator.md");

        Assert.Contains("name: labview-doc-generator", agent);
        Assert.Contains("lvai_convert_vi_to_aixml", agent);
        Assert.Contains("scriptsDirectory", agent);   // it must not hardcode a repository path
    }

    [Fact]
    public void TheWorkingRulesTravelWithTheBinary()
    {
        var rules = KnowledgeTools.Load("CLAUDE.md");

        Assert.Contains("lvai_palette_index", rules);
        Assert.Contains("palette reachability", rules, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryEmbeddedResourceDeclaredInTheCsprojIsLoadable()
    {
        // build.ps1 verifies the bytes; this verifies the LogicalName actually resolves at run
        // time, which is what a tool call depends on.
        var csproj = Res.FindRepoFile("src/LabVIEWMCP/LabVIEWMCP.csproj");
        Assert.NotNull(csproj);

        var names = System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(csproj!), "LogicalName=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(names.Count >= 9, $"expected at least 9 embedded resources, found {names.Count}");
        foreach (var name in names)
            Assert.False(string.IsNullOrWhiteSpace(KnowledgeTools.Load(name)),
                         $"embedded resource '{name}' did not load");
    }
}
