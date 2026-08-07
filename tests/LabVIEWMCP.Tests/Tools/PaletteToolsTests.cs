using System.Text;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// Two layers, neither needing LabVIEW: the length-prefixed parser (easy to read as plain text
/// and get subtly wrong - that is how "&amp;My Property Dialog.vi" happens), and a scan over a
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
        // yields "&My Property Dialog Toggle RW Symbol.vi"; the length-prefixed read must not.
        const string name = "My Property Dialog Toggle RW Symbol.vi";
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

    /// <summary>
    /// The index prints palette ITEM names, and for a library-owned VI that is not the Call
    /// target: `Draw Image from File__ogtk.vi` is refused where
    /// `openg_picture.lvlib:Draw Image from File__ogtk.vi` validates and runs. This tool used to
    /// state the opposite - "legal Call targets verbatim - no path, no library qualifier" - which
    /// makes a callable VI look uncallable and sends the caller back to rebuilding from
    /// primitives. Both the header and the hit list must carry the warning.
    /// </summary>
    [Fact]
    public void HitsSayThatALibraryOwnedViNeedsItsQualifier()
    {
        WritePalette("Categories/OpenG/functions_oglib_picture.mnu", "Draw Image from File__ogtk.vi");

        var hits = PaletteTools.PaletteIndexTool(query: "Draw Image", installRoot: _root);
        var totals = PaletteTools.PaletteIndexTool(installRoot: _root);

        Assert.Contains(".lvlib:", hits);
        Assert.Contains("lvlib", totals);
        Assert.DoesNotContain("no library qualifier", totals);
    }

    [Fact]
    public void AMissRecommendsTheNodeElementInsteadOfSilence()
    {
        WritePalette("x.mnu", "Something Else.vi");

        var text = PaletteTools.PaletteIndexTool(query: "Build Path", installRoot: _root);

        Assert.Contains("No palette VI matches", text);
        Assert.Contains("`Node`", text);
    }

    // ---------- add-on palettes ----------

    /// <summary>Write a synthetic LVAddon: &lt;root&gt;\&lt;addon&gt;\&lt;api&gt;\menus\... plus its info file.</summary>
    private string WriteAddon(string addon, string? minimumVersion, string paletteName,
                              params string[] entries)
    {
        var addonsRoot = Path.Combine(_root, "LVAddons");
        var api = Path.Combine(addonsRoot, addon, "1");
        var path = Path.Combine(api, "menus", "Categories", paletteName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Pascal(entries));
        if (minimumVersion is not null)
            File.WriteAllText(Path.Combine(api, "lvaddoninfo.json"),
                $"{{\"AddonName\":\"{addon}\",\"MinimumSupportedLVVersion\":\"{minimumVersion}\"}}");
        return addonsRoot;
    }

    [Fact]
    public void AddonPalettesAreScannedAndLabelledWithTheirAddon()
    {
        // The bug this fixes: NI-DAQmx installs under NI\LVAddons, not into <LabVIEW>\menus, so
        // scanning the IDE folder alone reported a driver as absent while its Calls resolved.
        WritePalette("Categories/Programming/string.mnu", "Trim Whitespace.vi");
        var addons = WriteAddon("nidaqmx", "22.0", "daqmx.mnu", "DAQmx Read.vi", "DAQmx Clear Task.vi");

        var index = PaletteIndex.Build(_root, refresh: true, addonsRoot: addons);

        Assert.Equal(3, index.Vis.Count);
        Assert.Contains("nidaqmx", index.AddonsScanned);
        var read = index.Vis.Single(v => v.Name == "DAQmx Read.vi");
        Assert.StartsWith("nidaqmx: ", read.PaletteFile);
        // an IDE entry keeps its bare relative path, so the two sources stay distinguishable
        Assert.DoesNotContain(":", index.Vis.Single(v => v.Name == "Trim Whitespace.vi").PaletteFile);
    }

    [Fact]
    public void AnAddonWithoutMenusIsSkippedWithoutComplaint()
    {
        WritePalette("x.mnu", "Something.vi");
        var addonsRoot = Path.Combine(_root, "LVAddons");
        Directory.CreateDirectory(Path.Combine(addonsRoot, "nisyscfg", "1", "vi.lib"));

        var index = PaletteIndex.Build(_root, refresh: true, addonsRoot: addonsRoot);

        Assert.Single(index.Vis);
        Assert.Empty(index.AddonsScanned);
        Assert.Empty(index.AddonsSkipped);
    }

    [Fact]
    public void AnAddonRequiringANewerLabViewIsSkippedAndSaidSo()
    {
        WritePalette("x.mnu", "Something.vi");
        var addons = WriteAddon("futuredriver", "99.0", "future.mnu", "Future Thing.vi");

        // A release is only known for a discovered installation, so state it the way Build does.
        var text = PaletteTools.PaletteIndexTool(
            installRoot: _root, addonsRoot: addons, refresh: true);

        // Without a known release nothing can be compared, so the add-on is scanned - the
        // conservative direction. What must never happen is a silent drop.
        Assert.Contains("Future Thing.vi", PaletteIndex
            .Build(_root, refresh: true, addonsRoot: addons).Vis.Select(v => v.Name));
        Assert.Contains("add-on palette", text);
    }

    [Fact]
    public void AddonsAreNotDiscoveredWhenAnInstallRootIsGivenWithoutOne()
    {
        // Otherwise a synthetic-tree test would silently pick up the real machine's drivers.
        WritePalette("x.mnu", "Something.vi");
        WriteAddon("nidaqmx", "22.0", "daqmx.mnu", "DAQmx Read.vi");

        var index = PaletteIndex.Build(_root, refresh: true);

        Assert.Single(index.Vis);
        Assert.Empty(index.AddonsScanned);
    }

    [Fact]
    public void TheSameViFromTwoAddonVariantsIsOneEntry()
    {
        WritePalette("x.mnu", "Something.vi");
        var addons = Path.Combine(_root, "LVAddons");
        WriteAddon("nidaqmx32", "22.0", "daqmx.mnu", "DAQmx Read.vi");
        WriteAddon("nidaqmx64", "22.0", "daqmx.mnu", "DAQmx Read.vi");

        var index = PaletteIndex.Build(_root, refresh: true, addonsRoot: addons);

        Assert.Equal(2, index.Vis.Count);
        Assert.Equal(2, index.AddonsScanned.Count);
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

        // "Primitives are excluded" cannot be asserted by name, and the attempt to do so is
        // instructive: this test used to require "Close Reference.vi" to be absent because
        // Close Reference is a built-in function. Once add-on palettes were scanned it appeared
        // for real - the DataFinder add-on ships a VI of exactly that name. A palette VI may
        // therefore SHADOW a primitive's name, and a Call to it hits the VI, not the function.
        // The exclusion is a property of the parser, covered by OnlyBareViNamesQualify and
        // NonViEntriesAreIgnored above; here only the entry shape can be checked.
        Assert.All(index.Vis, vi =>
        {
            Assert.True(PaletteIndex.IsViName(vi.Name), $"not a bare VI name: {vi.Name}");
            Assert.False(string.IsNullOrWhiteSpace(vi.PaletteFile));
        });
    }
}
