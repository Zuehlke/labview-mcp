using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
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

    /// <summary>
    /// Occurrences that are ordinary technical English, not the customer's name. One of the
    /// forbidden tokens is a common word on its own — it is hashed because the product name
    /// is two words and either half alone must trip the guard — so the benign uses have to
    /// be named here rather than by weakening the denylist.
    ///
    /// Keyed by document and token, so the same word appearing in a NEW document still fails.
    /// Add an entry only after reading the surrounding sentence and confirming it is generic.
    /// </summary>
    private static readonly HashSet<(string Document, string Token)> BenignOccurrences =
    [
        // "89 50 4E 47 magic" - the PNG file signature, in the icon section.
        ("vi-server-reference.md", "magic"),
    ];

    [Theory]
    // Every embedded document, not just the two derived from the customer application:
    // a leak can be pasted into any of them, and the guard is worthless where it does not run.
    [InlineData("dqmh-patterns.md")]
    [InlineData("aixml-reference.md")]
    [InlineData("lvproj-structure.md")]
    [InlineData("lvlib-lvclass-structure.md")]
    [InlineData("vi-server-reference.md")]
    [InlineData("vi-server-methods.tsv")]
    [InlineData("vi-server-properties.tsv")]
    [InlineData("connector-pane-patterns.tsv")]
    [InlineData("labview-doc-generator.md")]
    [InlineData("labview-vi-generator.md")]
    [InlineData("labview-vi-editor.md")]
    [InlineData("CLAUDE.md")]
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

            if (!ForbiddenTokenHashes.Contains(hash)) continue;
            Assert.True(BenignOccurrences.Contains((document, token.ToLowerInvariant())),
                $"{document} contains the forbidden identifier \"{token}\" - anonymise it, "
                + "or add it to BenignOccurrences if the sentence around it is generic "
                + "technical English rather than the customer's name.");
        }
    }

    /// <summary>
    /// The theory above can only reach documents an lvai_*_reference tool serves, because that is
    /// what KnowledgeTools.Load reads. Since 2026-08-23 the build also COPIES all of docs\ next to
    /// the exe, so files that are shipped but unserved - aixml-gap-census.md, aixml-node-gaps.tsv,
    /// lvai-internal-vis.tsv and the rest - now travel to other machines while sitting outside
    /// that guard. This walks the folder itself, so coverage follows what ships rather than what
    /// happens to be embedded, and a document added to docs\ is covered the moment it exists.
    /// </summary>
    [Fact]
    public void NoCustomerOrProductIdentifiersAnywhereInTheDocsFolder()
    {
        var anchor = Res.FindRepoFile("docs/aixml-reference.md");
        Assert.NotNull(anchor);
        var folder = Path.GetDirectoryName(anchor!)!;

        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        Assert.True(files.Length >= 15, $"only {files.Length} files found in {folder}");

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            foreach (var token in Regex.Split(File.ReadAllText(path), "[^A-Za-z0-9]+"))
            {
                if (token.Length < 3) continue;

                var hash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(token.ToLowerInvariant()))).ToLowerInvariant();

                if (!ForbiddenTokenHashes.Contains(hash)) continue;
                Assert.True(BenignOccurrences.Contains((name, token.ToLowerInvariant())),
                    $"docs/{name} contains the forbidden identifier \"{token}\" - anonymise it, "
                    + "or add it to BenignOccurrences if the sentence around it is generic "
                    + "technical English rather than the customer's name.");
            }
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

    /// <summary>
    /// This used to assert "No section matched". That answer plus a heading list reads as "the
    /// content is not here", which was measured sending a caller off to re-derive a documented
    /// fact - so the AIXML tool started falling through to a term lookup instead, and every
    /// document tool now does the same. A term genuinely absent still says so, and still gets
    /// the heading list.
    /// </summary>
    [Fact]
    public void UnknownSectionExplainsItself()
    {
        var result = KnowledgeTools.DqmhReference("nope");

        Assert.Contains("Nothing in the DQMH reference mentions \"nope\"", result);
        Assert.Contains("Sections (pass one as `section`)", result);
    }

    /// <summary>A term that IS in the document is now shown rather than denied.</summary>
    [Fact]
    public void ASectionThatIsNotAHeadingFallsBackToATermLookup()
    {
        var result = KnowledgeTools.DqmhReference("Broadcast");

        Assert.DoesNotContain("Nothing in the DQMH reference mentions", result);
        Assert.Contains("Broadcast", result);
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
