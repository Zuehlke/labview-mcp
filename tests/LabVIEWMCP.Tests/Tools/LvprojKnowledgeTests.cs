using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// Same guarantees as the other two embedded documents: the served bytes are the reviewable
/// markdown, and a fresh clone cannot drift from the shipped binary without a test failing.
/// </summary>
public class LvprojKnowledgeTests
{
    [Fact]
    public void EmbeddedDocumentIsByteIdenticalToTheFileInDocs()
    {
        var onDisk = Res.FindRepoFile("docs/lvproj-structure.md");
        Assert.NotNull(onDisk);

        var expected = File.ReadAllText(onDisk!).Replace("\r\n", "\n");
        var embedded = KnowledgeTools.Load("lvproj-structure.md").Replace("\r\n", "\n");

        Assert.Equal(expected, embedded);
    }

    [Fact]
    public void DocumentIsSubstantialAndAboutTheProjectFormat()
    {
        var text = KnowledgeTools.Load("lvproj-structure.md");

        Assert.True(text.Length > 5000, $"document looks truncated ({text.Length} chars)");
        Assert.Contains("lvproj", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoArgumentReturnsATableOfContents()
    {
        var result = KnowledgeTools.LvprojReference();

        Assert.Contains("Sections (pass one as `section`)", result);
        // cheap by default - the caller asks for a section, not the whole grammar
        Assert.True(result.Length < 2000, $"section list is too big ({result.Length} chars)");
    }

    [Fact]
    public void AllReturnsTheWholeDocument() =>
        Assert.Equal(KnowledgeTools.Load("lvproj-structure.md"),
                     KnowledgeTools.LvprojReference("all"));

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("7")]
    public void SectionNumbersResolve(string number)
    {
        var body = KnowledgeTools.LvprojReference(number);

        Assert.StartsWith($"## {number}.", body);
    }

    /// <summary>
    /// Was "No section matched" - see the same test in DqmhKnowledgeTests for why every document
    /// tool now falls through to a term lookup. An absent term still says so.
    /// </summary>
    [Fact]
    public void UnknownSectionExplainsItselfInsteadOfFailing()
    {
        var result = KnowledgeTools.LvprojReference("does-not-exist");

        Assert.Contains("Nothing in the .lvproj reference mentions \"does-not-exist\"", result);
        Assert.Contains("Sections (pass one as `section`)", result);
    }

    [Fact]
    public void ASectionThatIsNotAHeadingFallsBackToATermLookup()
    {
        var result = KnowledgeTools.LvprojReference("LVVersion");

        Assert.DoesNotContain("Nothing in the .lvproj reference mentions", result);
        Assert.Contains("LVVersion", result);
    }

    [Fact]
    public void ResourceAccessorReturnsTheSameDocument() =>
        Assert.Equal(KnowledgeTools.Load("lvproj-structure.md"),
                     KnowledgeTools.LvprojReferenceResource());

    /// <summary>
    /// The three documents must stay distinct - a copy-paste slip in the resource names would
    /// otherwise make two tools serve the same text, and every other test here would still pass.
    /// </summary>
    [Fact]
    public void TheThreeEmbeddedDocumentsAreDifferentFromEachOther()
    {
        var aixml = KnowledgeTools.Load("aixml-reference.md");
        var dqmh = KnowledgeTools.Load("dqmh-patterns.md");
        var lvproj = KnowledgeTools.Load("lvproj-structure.md");

        Assert.NotEqual(aixml, dqmh);
        Assert.NotEqual(aixml, lvproj);
        Assert.NotEqual(dqmh, lvproj);
    }
}
