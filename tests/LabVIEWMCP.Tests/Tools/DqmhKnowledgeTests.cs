using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// SHA-256 of the lowercased customer, product and module tokens this repo must never
    /// contain. They are hashed on purpose: a plain denylist would make THIS file the one
    /// place where the customer and their products are named together — the guard would be
    /// the leak. Hashes still fail the build on a real leak, and a failure prints the
    /// offending token, which by then is in the document anyway.
    ///
    /// To extend: hash the lowercased token with SHA-256 and add it here.
    /// </summary>
    private static readonly HashSet<string> ForbiddenTokenHashes =
    [
        "aa7e5b234e1d55967bf0a316395a2eab6cb3370332c0f251f0e44a5afb84fc68",
        "15e389361ec9345a955d2d1832275a7885b20c663ab535cffd134174cdec9dc1",
        "0e9dbf302dc48c67dfb50487c3e7473b1f5cdb0cd4e57d1bac600d3043cc38e8",
        "3aecd5bfd5c9a1afad2768b60682b97110c79a959203bb91a5487d0633ff8e71",
        "980a0b90bc158a429b9233476b5f4fb751f145871bcfddb9d54d40079a3edf63",
        "5050945cdfa28646d140e7765f89567ad52f8f3585c37effbab5ad575da8d012",
        "3be7a505483c0050243c5cbad4700da13925aa4137a55e9e33efd8bc4d05850f",
        "62b0f1b617cae30dc776251b674fc4b7a5b6fd31647e4c297f6af72214e3831e",
        "0c873ecbd3c57a0116ca9190d67c9d72bc0154efc4f81cca5d11c62181846184",
        "27301716ef81140f720afd91224766d14e49fd37854a3a5ad1b9d270f4dd9634",
    ];

    [Theory]
    [InlineData("dqmh-patterns.md")]
    [InlineData("aixml-reference.md")]
    public void NoCustomerOrProductIdentifiersLeakedIntoTheNotes(string document)
    {
        // Both notes were derived from a customer application. They must carry framework
        // vocabulary only - never the names of that project, its products or its modules.
        // Split on anything that is not a letter or digit, so a compound like
        // "Prefix_SomeName" decomposes into the atomic tokens the hashes cover.
        var text = KnowledgeTools.Load(document);

        foreach (var token in Regex.Split(text, "[^A-Za-z0-9]+"))
        {
            if (token.Length < 3) continue;

            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(token.ToLowerInvariant()))).ToLowerInvariant();

            Assert.False(ForbiddenTokenHashes.Contains(hash),
                $"{document} contains the forbidden identifier \"{token}\" - anonymise it.");
        }
    }

    [Fact]
    public void TheHashSetIsIntact()
    {
        // A truncated or corrupted list would pass every document silently.
        Assert.Equal(10, ForbiddenTokenHashes.Count);
        Assert.All(ForbiddenTokenHashes, h =>
            Assert.Matches("^[0-9a-f]{64}$", h));
    }

    [Fact]
    public void TheGuardMechanismActuallyCatchesALeak()
    {
        // Proves tokenising + hashing + matching works, using a made-up canary so that no
        // real identifier has to appear here. A guard that cannot fail is not a guard.
        const string canary = "zzzcanaryidentifier";
        var canaryHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canary))).ToLowerInvariant();

        var document = $"Some prose mentioning MyLib_{canary}.lvlib in passing.";
        var caught = Regex.Split(document, "[^A-Za-z0-9]+")
            .Where(t => t.Length >= 3)
            .Select(t => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(t.ToLowerInvariant()))).ToLowerInvariant())
            .Any(h => h == canaryHash);

        Assert.True(caught, "the underscore-splitting tokeniser missed an embedded identifier");
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
