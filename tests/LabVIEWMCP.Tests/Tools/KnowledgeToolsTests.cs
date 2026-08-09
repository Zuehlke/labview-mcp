using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// The point of embedding the reference rather than duplicating it is that the served bytes
/// and the reviewable markdown cannot drift apart. The first test here is the one that
/// actually guards that promise.
/// </summary>
public class KnowledgeToolsTests
{
    [Fact]
    public void EmbeddedReferenceIsByteIdenticalToTheFileInDocs()
    {
        var onDisk = FindRepoFile("docs/aixml-reference.md");
        Assert.NotNull(onDisk);

        var expected = File.ReadAllText(onDisk!).Replace("\r\n", "\n");
        var embedded = KnowledgeTools.Load().Replace("\r\n", "\n");

        Assert.Equal(expected, embedded);
    }

    [Fact]
    public void EmbeddedResourceIsPresentAndSubstantial()
    {
        var text = KnowledgeTools.Load();
        Assert.Contains("AIXML", text);
        Assert.True(text.Length > 5000, $"reference looks truncated ({text.Length} chars)");
    }

    [Fact]
    public void SplitFindsEveryLevelTwoHeading()
    {
        var sections = KnowledgeTools.Split(KnowledgeTools.Load());
        var headings = sections.Select(s => s.Heading).ToList();

        Assert.True(headings.Count > 5, $"only {headings.Count} sections found");
        Assert.Contains(headings, h => h.Contains("core model", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Contains("Type grammar", StringComparison.OrdinalIgnoreCase));
        // every body must start with its own heading line
        foreach (var (heading, body) in sections.Where(s => s.Heading != "(preamble)"))
            Assert.StartsWith("## " + heading, body);
    }

    [Theory]
    [InlineData("wiring", "net")]
    [InlineData("nets", "net")]
    [InlineData("types", "cluster{")]
    [InlineData("escaping", "\\3A")]
    [InlineData("structures", "While Loop")]
    [InlineData("errors", "Unsupported SubVI")]
    [InlineData("workflow", "ValidateAIXML")]
    public void KeywordsResolveToTheRightSection(string keyword, string expectedContent)
    {
        var sections = KnowledgeTools.Split(KnowledgeTools.Load());
        var body = KnowledgeTools.Find(sections, keyword);

        Assert.NotNull(body);
        Assert.Contains(expectedContent, body!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SectionNumberResolves()
    {
        var sections = KnowledgeTools.Split(KnowledgeTools.Load());
        var body = KnowledgeTools.Find(sections, "5");

        Assert.NotNull(body);
        Assert.StartsWith("## 5.", body);
    }

    [Fact]
    public void NoArgumentReturnsEssentialsAndATableOfContents()
    {
        var result = KnowledgeTools.AixmlReference();

        // the traps that fail silently must be in the cheap default answer
        Assert.Contains("NETS, NOT POINTERS", result);
        Assert.Contains("LOOK UP TERMINAL NAMES", result);
        Assert.Contains("SELF-CONTAINED", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sections (pass one as `section`)", result);

        // ...but not the whole 19 KB document
        Assert.True(result.Length < 4000, $"default answer is too big ({result.Length} chars)");
    }

    [Fact]
    public void AllReturnsTheWholeDocument() =>
        Assert.Equal(KnowledgeTools.Load(), KnowledgeTools.AixmlReference("all"));

    /// <summary>
    /// A term that is in neither a heading nor the text still has to explain itself and point
    /// somewhere. It now arrives via the lookup fallback, so the wording is the lookup's.
    /// </summary>
    [Fact]
    public void UnknownSectionExplainsItselfInsteadOfFailing()
    {
        var result = KnowledgeTools.AixmlReference("does-not-exist");

        Assert.Contains("Nothing in the AIXML reference mentions", result);
        Assert.Contains("Sections (pass one as `section`)", result);
    }

    [Fact]
    public void ResourceAccessorReturnsTheSameDocument() =>
        Assert.Equal(KnowledgeTools.Load(), KnowledgeTools.AixmlReferenceResource());

    // ---------- node lookup ----------

    private const string Doc = """
        # Title

        ## 8. Multi-terminal nodes

        Some prose about Build Waveform in a paragraph.

        | Node | Inputs | Outputs |
        |---|---|---|
        | `Build Waveform` | `waveform`, `t0`, `dt`, `Y` | `output waveform` |
        | `Index Array` | `array`, `index` | `element` |

        ### Indexing a 2D array

        ```xml
        <Node _name="Index Array" dimensions="2"
              inputs="array:1.a,index (row):2.b,disabled index (col):" />
        ```

        ## 9. Something else
        """;

    /// <summary>
    /// A table row without its header is unusable - `| `Build Waveform` | `waveform`, `t0` |`
    /// does not say which column is inputs and which is outputs.
    /// </summary>
    [Fact]
    public void ATableRowComesBackWithItsHeader()
    {
        var result = KnowledgeTools.Lookup(Doc, "Build Waveform", 40);

        Assert.Contains("| Node | Inputs | Outputs |", result);
        Assert.Contains("`output waveform`", result);
        // and NOT the unrelated row
        Assert.DoesNotContain("`Index Array` | `array`", result);
    }

    /// <summary>Half an XML snippet cannot be copied, so a fence comes back whole.</summary>
    [Fact]
    public void AHitInsideACodeBlockReturnsTheWholeBlock()
    {
        var result = KnowledgeTools.Lookup(Doc, "disabled index", 40);

        Assert.Contains("<Node _name=\"Index Array\" dimensions=\"2\"", result);
        Assert.Contains("disabled index (col)", result);
    }

    [Fact]
    public void EachPassageIsLabelledWithItsHeading()
    {
        Assert.Contains("[Indexing a 2D array]", KnowledgeTools.Lookup(Doc, "disabled index", 40));
        Assert.Contains("[8. Multi-terminal nodes]", KnowledgeTools.Lookup(Doc, "Build Waveform", 40));
    }

    [Fact]
    public void AMultiLineFenceHitCountsOnce()
    {
        // "Index Array" appears on one fence line and one table row: two passages, not four.
        var result = KnowledgeTools.Lookup(Doc, "Index Array", 40);

        Assert.StartsWith("2 passage(s)", result);
    }

    [Fact]
    public void AnUnknownTermSaysWhatToDoNext()
    {
        var result = KnowledgeTools.Lookup(Doc, "Nonexistent Node", 40);

        Assert.Contains("Nothing in the AIXML reference mentions", result);
        Assert.Contains("export a VI that already uses it", result);
    }

    /// <summary>
    /// The regression this whole feature exists for: a VI generator could not find a subsection
    /// added to section 8 the same day, because section 8 comes back as 54 kB that the client
    /// spills into a one-line JSON file. It re-derived the fact by exporting a VI instead.
    /// </summary>
    [Fact]
    public void TheRealDocumentAnswersANodeQueryWithoutReturningSectionEight()
    {
        var whole = KnowledgeTools.AixmlReference(section: "8");
        var focused = KnowledgeTools.AixmlReference(node: "disabled index");

        Assert.Contains("disabled index (col)", focused);
        Assert.True(focused.Length < whole.Length / 10,
            $"node lookup returned {focused.Length} chars against {whole.Length} for the section");
    }

    [Fact]
    public void ABigSectionSaysThatNodeLookupExists()
    {
        var result = KnowledgeTools.AixmlReference(section: "8");

        Assert.Contains("node='<name>'", result);
    }

    /// <summary>
    /// `Split` cuts on `##` only, so a `###` subsection was invisible to section= even though the
    /// tool promises "part of a title" - measured on 'Polymorphic subVI calls'.
    /// </summary>
    [Fact]
    public void ASubsectionTitleResolvesToThatSubsection()
    {
        var body = KnowledgeTools.FindSubsection(KnowledgeTools.Load(), "Polymorphic subVI calls");

        Assert.NotNull(body);
        Assert.StartsWith("### ", body);
        Assert.Contains("instance", body!, StringComparison.OrdinalIgnoreCase);
        // stops at the next heading rather than running to the end of the document
        Assert.DoesNotContain("\n## ", body);
    }

    [Fact]
    public void SectionAcceptsASubsectionTitle()
    {
        var result = KnowledgeTools.AixmlReference(section: "Polymorphic subVI calls");

        Assert.DoesNotContain("No section matched", result);
        Assert.StartsWith("### ", result);
    }

    /// <summary>
    /// A term that exists but is not a heading used to answer "No section matched" plus a list of
    /// numbers, which reads as "not documented". Show the passages instead.
    /// </summary>
    [Fact]
    public void AnUnknownSectionFallsBackToLookingTheTermUp()
    {
        var result = KnowledgeTools.AixmlReference(section: "graph21703");

        Assert.DoesNotContain("No section matched", result);
        Assert.Contains("graph21703", result);
    }

    /// <summary>
    /// 'error in (no error)' hit 100 passages and filled a caller's context without answering it.
    /// A flooded lookup names the headings so the next call can be aimed.
    /// </summary>
    [Fact]
    public void AFloodedLookupOffersHeadingsInsteadOfEveryPassage()
    {
        var result = KnowledgeTools.AixmlReference(node: "error in (no error)");

        Assert.Contains("That term is everywhere", result);
        Assert.Contains("ask for one of them with section=", result);
        Assert.True(result.Length < 12_000, $"flooded answer is still {result.Length} chars");
    }

    // ---------- ranking ----------

    /// <summary>
    /// The measured failure: node='Select' returned 29 passages about `selector`, `selectin` and
    /// "selects", and the Select node's own terminal `s? t\3Af` was in none of the shown ones.
    /// The reference writes node names in backticks, so that beats a bare substring.
    /// </summary>
    [Theory]
    [InlineData("| `Select` | `s? t\\3Af` |", 3)]        // the row actually wanted
    [InlineData("a `Select node` in prose", 2)]
    [InlineData("the Select node picks one", 1)]         // whole word, no backticks
    [InlineData("CaseFrame carries `selector`", 0)]      // substring only - rank last
    [InlineData("`selectin` and `selectout`", 0)]
    public void BacktickedNamesOutrankSubstrings(string passage, int expected) =>
        Assert.Equal(expected, KnowledgeTools.Rank(passage, "Select"));

    [Fact]
    public void AFloodedLookupShowsTheExactMatchFirst()
    {
        var result = KnowledgeTools.AixmlReference(node: "Select");

        // `Select` in backticks must appear before the `selector` attribute rows it used to bury
        var exact = result.IndexOf("`Select`", StringComparison.Ordinal);
        var selector = result.IndexOf("`selector`", StringComparison.Ordinal);
        Assert.True(exact >= 0, "the Select node's own row is missing entirely");
        Assert.True(selector < 0 || exact < selector,
            "`selector` passages still come before the `Select` node itself");
    }

    [Fact]
    public void NodeTakesPrecedenceOverSection()
    {
        var result = KnowledgeTools.AixmlReference(section: "5", node: "Build Waveform");

        Assert.StartsWith("", result);
        Assert.Contains("passage(s) in the AIXML reference mention", result);
    }

    /// <summary>
    /// Locate a repo-relative file. Anchored on this source file's compile-time path rather
    /// than the output directory, because the build output is not always inside the repo -
    /// a redirected OutDir (used when a running MCP server locks bin/) would otherwise make
    /// this test fail for a reason that has nothing to do with the code.
    /// </summary>
    private static string? FindRepoFile(
        string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (var anchor in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var directory = string.IsNullOrEmpty(anchor) ? null : new DirectoryInfo(anchor);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }
        return null;
    }
}
