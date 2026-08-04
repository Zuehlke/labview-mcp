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

    [Fact]
    public void UnknownSectionExplainsItselfInsteadOfFailing()
    {
        var result = KnowledgeTools.AixmlReference("does-not-exist");

        Assert.Contains("No section matched", result);
        Assert.Contains("Sections (pass one as `section`)", result);
    }

    [Fact]
    public void ResourceAccessorReturnsTheSameDocument() =>
        Assert.Equal(KnowledgeTools.Load(), KnowledgeTools.AixmlReferenceResource());

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
