using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// Same drift guard as for the AIXML reference, plus one content check that matters: the DQMH
/// note exists mainly to record findings that are easy to get wrong, so if those statements
/// vanish the document has lost its point.
/// </summary>
public class DqmhKnowledgeTests
{
    [Fact]
    public void EmbeddedDqmhNoteIsByteIdenticalToTheFileInDocs()
    {
        var onDisk = FindRepoFile("docs/dqmh-patterns.md");
        Assert.NotNull(onDisk);

        Assert.Equal(
            File.ReadAllText(onDisk!).Replace("\r\n", "\n"),
            KnowledgeTools.Load("dqmh-patterns.md").Replace("\r\n", "\n"));
    }

    [Fact]
    public void NoArgumentReturnsASectionList()
    {
        var result = KnowledgeTools.DqmhReference();

        Assert.Contains("Sections (pass one as `section`)", result);
        Assert.True(result.Length < 2000, $"section list is too big ({result.Length} chars)");
    }

    [Fact]
    public void AllReturnsTheWholeDocument() =>
        Assert.Equal(KnowledgeTools.Load("dqmh-patterns.md"), KnowledgeTools.DqmhReference("all"));

    [Fact]
    public void ResourceAccessorMatchesTheTool() =>
        Assert.Equal(KnowledgeTools.Load("dqmh-patterns.md"), KnowledgeTools.DqmhReferenceResource());

    [Theory]
    // the load-bearing findings: losing any of these makes the note misleading
    [InlineData("Main.vi")]                 // two-loop core
    [InlineData("Obtain Broadcast Events")] // the fixed module API
    [InlineData("UserEvent")]               // the typed event contract
    [InlineData("Unsupported SubVI")]       // why a module cannot be generated
    [InlineData("lvlibp")]                  // packed libraries are readable
    public void KeyFindingsArePresent(string needle) =>
        Assert.Contains(needle, KnowledgeTools.Load("dqmh-patterns.md"));

    [Fact]
    public void NoCustomerOrProductIdentifiersLeakedIntoTheNote()
    {
        // The note was derived from a customer application. It must carry the framework's
        // vocabulary only - never the names of that project, its products or its modules.
        var text = KnowledgeTools.Load("dqmh-patterns.md");
        foreach (var forbidden in new[]
                 {
                     "Medela", "TFW_", "Magic", "EOL_DRW", "FEASA",
                     "DataStore", "DiagnosticCom", "Kingfisher",
                 })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownSectionExplainsItself()
    {
        var result = KnowledgeTools.DqmhReference("nope");

        Assert.Contains("No section matched", result);
        Assert.Contains("Sections (pass one as `section`)", result);
    }

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
