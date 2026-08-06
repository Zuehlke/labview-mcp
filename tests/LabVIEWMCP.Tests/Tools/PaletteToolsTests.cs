using System.Text;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// Two layers, neither needing LabVIEW: the length-prefixed parser (easy to read as plain text
/// and get subtly wrong - that is how "&amp;BM_Property Dialog.vi" happens), and a scan over a
/// synthetic menus folder, in the same spirit as the fake gRPC server the rest of the suite uses.
/// One opportunistic test additionally checks the real installation when there is one.
/// </summary>
public class PaletteToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lvmcp-palette-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A length-prefixed entry as a .mnu stores it: one length byte, then the text.</summary>
    private static byte[] Pascal(params string[] entries)
    {
        var buffer = new List<byte> { 0x00, 0xFF, 0x10 };       // leading noise
        foreach (var e in entries)
        {
            buffer.Add((byte)e.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(e));
        }
        buffer.AddRange(new byte[] { 0x00, 0x01, 0x02 });
        return buffer.ToArray();
    }

    /// <summary>Write one synthetic palette file under the fake installation's menus folder.</summary>
    private string WritePalette(string relative, params string[] entries)
    {
        var path = Path.Combine(_root, "menus", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Pascal(entries));
        return path;
    }

    // ---------- the parser ----------

    [Fact]
    public void LengthPrefixIsConsumedRatherThanReadAsPartOfTheName()
    {
        // 38 characters -> length byte 0x26, which is '&' in ASCII. Reading the buffer as text
        // yields "&BM_Property Dialog_Toggle RW Symbol.vi"; the length-prefixed read must not.
        const string name = "BM_Property Dialog_Toggle RW Symbol.vi";
        Assert.Equal(38, name.Length);

        Assert.Equal([name], PaletteIndex.PascalStrings(Pascal(name)).ToList());
    }

    [Fact]
    public void SeveralEntriesInSequenceAreAllFound()
    {
        var found = PaletteIndex.PascalStrings(
            Pascal("General Error Handler.vi", "Write PNG File.vi", "Filter 1D Array.vim")).ToList();

        Assert.Equal(3, found.Count);
        Assert.Contains("Write PNG File.vi", found);
        Assert.Contains("Filter 1D Array.vim", found);
    }

    [Fact]
    public void NonViEntriesAreIgnored()
    {
        var found = PaletteIndex.PascalStrings(
            Pascal("functions_JDP_Science_JSONtext.mnu", "dotnet.llb", "<vilib>")).ToList();

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("General Error Handler.vi", true)]
    [InlineData("Filter 1D Array (String).vim", true)]
    [InlineData("dotnet.llb", false)]
    [InlineData("palette.mnu", false)]
    [InlineData("vi.lib\\Utility\\Thing.vi", false)]     // a path, not a bare name
    [InlineData("Wild*card.vi", false)]
    [InlineData(" LeadingSpace.vi", false)]
    [InlineData(".vi", false)]
    public void OnlyBareViNamesQualify(string candidate, bool expected) =>
        Assert.Equal(expected, PaletteIndex.IsViName(candidate));

    // ---------- the scan ----------

    [Fact]
    public void ScanWalksSubfoldersAndReportsWhereEachViCameFrom()
    {
        WritePalette("Categories/Programming/string.mnu", "Trim Whitespace.vi", "dotnet.llb");
        WritePalette("Categories/Programming/File/file.mnu", "Write PNG File.vi");

        var index = PaletteIndex.Build(_root);

        Assert.Equal(2, index.PaletteFilesScanned);
        Assert.Equal(2, index.Vis.Count);
        var png = index.Vis.Single(v => v.Name == "Write PNG File.vi");
        Assert.Contains("file.mnu", png.PaletteFile);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "menus", png.PaletteFile);
    }

    [Fact]
    public void AViOnSeveralPalettesIsOneEntry()
    {
        WritePalette("a.mnu", "Close Config Data.vi");
        WritePalette("b.mnu", "Close Config Data.vi");

        var index = PaletteIndex.Build(_root);

        Assert.Equal(2, index.PaletteFilesScanned);
        Assert.Single(index.Vis);
    }

    [Fact]
    public void ResultsAreSortedSoOutputIsStable()
    {
        WritePalette("z.mnu", "Zebra Utility.vi", "Alpha Utility.vi", "middle utility.vi");

        var names = PaletteIndex.Build(_root).Vis.Select(v => v.Name).ToList();

        Assert.Equal(["Alpha Utility.vi", "middle utility.vi", "Zebra Utility.vi"], names);
    }

    [Fact]
    public void RefreshRescansWhileTheDefaultServesTheCache()
    {
        WritePalette("one.mnu", "First.vi");
        Assert.Single(PaletteIndex.Build(_root).Vis);

        WritePalette("two.mnu", "Second.vi");
        Assert.Single(PaletteIndex.Build(_root).Vis);                  // cached
        Assert.Equal(2, PaletteIndex.Build(_root, refresh: true).Vis.Count);
    }

    [Fact]
    public void AnInstallationWithoutMenusIsAClearErrorNotAnEmptyList()
    {
        Directory.CreateDirectory(_root);

        var text = PaletteTools.PaletteIndexTool(installRoot: _root);

        Assert.Contains("DirectoryNotFoundException", text);
        Assert.Contains("menus", text);
    }

    // ---------- the tool ----------

    [Fact]
    public void WithoutAQueryItReportsTotalsInsteadOfEveryName()
    {
        WritePalette("x.mnu", "General Error Handler.vi", "Write PNG File.vi");

        var text = PaletteTools.PaletteIndexTool(installRoot: _root);

        Assert.Contains("2 palette-reachable VIs", text);
        Assert.DoesNotContain("General Error Handler.vi", text);
    }

    [Fact]
    public void QueryReturnsTheNameWithItsPaletteFile()
    {
        WritePalette("Categories/err.mnu", "General Error Handler.vi", "Simple Error Handler.vi");

        var text = PaletteTools.PaletteIndexTool(query: "Error Handler", installRoot: _root);

        Assert.Contains("General Error Handler.vi", text);
        Assert.Contains("Simple Error Handler.vi", text);
        Assert.Contains("err.mnu", text);
    }

    [Fact]
    public void TruncationIsReportedRatherThanSilent()
    {
        WritePalette("many.mnu", Enumerable.Range(0, 12).Select(i => $"Utility {i}.vi").ToArray());

        var text = PaletteTools.PaletteIndexTool(query: "Utility", limit: 4, installRoot: _root);

        Assert.Contains("12 of 12 match", text);
        Assert.Contains("8 more", text);
    }

    [Fact]
    public void AMissRecommendsTheNodeElementInsteadOfSilence()
    {
        WritePalette("x.mnu", "Something Else.vi");

        var text = PaletteTools.PaletteIndexTool(query: "Build Path", installRoot: _root);

        Assert.Contains("No palette VI matches", text);
        Assert.Contains("`Node`", text);
    }

    // ---------- the real installation, when this machine has one ----------

    [Fact]
    public void RealInstallationYieldsVisAndExcludesPrimitives()
    {
        PaletteIndex.Result index;
        try { index = PaletteIndex.Build(); }
        catch { return; }        // no LabVIEW here; the suite must not require one

        Assert.True(index.PaletteFilesScanned > 100,
            $"only {index.PaletteFilesScanned} palette files scanned");
        Assert.True(index.Vis.Count > 500, $"only {index.Vis.Count} VIs found");

        var names = index.Vis.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Verified as a resolvable Call target in docs/aixml-reference.md.
        Assert.Contains("General Error Handler.vi", names);
        // Primitives must NOT appear: both of these are built-in functions, not VIs.
        Assert.DoesNotContain("Build Path.vi", names);
        Assert.DoesNotContain("Close Reference.vi", names);
    }
}
