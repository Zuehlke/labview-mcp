using System.Text;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// Two layers, neither needing LabVIEW: the &lt;ExampleProgram&gt; extractor and parser, and a scan
/// over a synthetic examples folder. One opportunistic test additionally checks the real
/// installation when there is one.
/// </summary>
public class ExampleToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lvmcp-examples-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string Block = """
        <ExampleProgram>
        <Title><Text Locale="US">Scale TDMS Data.vi</Text></Title>
        <Description><Text Locale="US">Creates scaling information on <B>TDMS</B> objects
        	and reads the scaled data.</Text></Description>
        <Keywords><Item>files</Item><Item>files</Item><Item>TDMS</Item><Item>scale</Item></Keywords>
        <Navigation><Item>2997</Item></Navigation>
        <FileType>VI</FileType>
        <RequiredSoftware><NiSoftware MinVersion="13.0">LabVIEW</NiSoftware></RequiredSoftware>
        </ExampleProgram>
        """;

    /// <summary>A .vi as it really looks: binary noise with the metadata block buried inside.</summary>
    private static byte[] ViBytes(string? block)
    {
        var buffer = new List<byte> { 0x52, 0x53, 0x52, 0x43, 0x00, 0xFF, 0x80, 0x91 };
        if (block is not null) buffer.AddRange(Encoding.ASCII.GetBytes(block));
        buffer.AddRange(new byte[] { 0x00, 0xFE, 0x12 });
        return buffer.ToArray();
    }

    private string WriteExample(string relative, string? block = Block)
    {
        var path = Path.Combine(_root, "examples",
                                relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ViBytes(block));
        return path;
    }

    // ---------- extractor and parser ----------

    [Fact]
    public void TheBlockIsFoundInsideBinaryNoise()
    {
        var extracted = ExampleIndex.ExtractBlock(ViBytes(Block));

        Assert.NotNull(extracted);
        Assert.StartsWith("<ExampleProgram>", extracted);
        Assert.EndsWith("</ExampleProgram>", extracted);
    }

    [Fact]
    public void AViWithoutTheBlockYieldsNull() =>
        Assert.Null(ExampleIndex.ExtractBlock(ViBytes(null)));

    /// <summary>
    /// The wrapper is optional and most examples omit it. Anchoring on &lt;ExampleProgram&gt; alone
    /// found 373 examples on this station where anchoring on the fields finds over 800 - two
    /// thirds of the index silently missing, which is exactly what an index must never do.
    /// </summary>
    [Fact]
    public void ABlockWithoutTheExampleProgramWrapperIsStillFound()
    {
        var unwrapped = Block
            .Replace("<ExampleProgram>", "")
            .Replace("</ExampleProgram>", "");

        var extracted = ExampleIndex.ExtractBlock(ViBytes(unwrapped));

        Assert.NotNull(extracted);
        Assert.StartsWith("<ExampleProgram>", extracted);      // wrapper supplied by the extractor
        var (description, keywords, _) = ExampleIndex.Parse(extracted);
        Assert.StartsWith("Creates scaling information", description);
        Assert.Contains("TDMS", keywords);
    }

    [Fact]
    public void AnUnwrappedBlockStopsAtTheLastKnownClosingTag()
    {
        // Trailing binary must not be dragged in: the block ends at </RequiredSoftware> here.
        var unwrapped = Block
            .Replace("<ExampleProgram>", "")
            .Replace("</ExampleProgram>", "");

        var extracted = ExampleIndex.ExtractBlock(ViBytes(unwrapped))!;

        Assert.EndsWith("</RequiredSoftware></ExampleProgram>", extracted);
    }

    [Fact]
    public void DescriptionIsOneLineWithMarkupRemoved()
    {
        var (description, _, _) = ExampleIndex.Parse(Block);

        Assert.Equal("Creates scaling information on TDMS objects and reads the scaled data.",
                     description);
    }

    [Fact]
    public void KeywordsAreDeduplicated()
    {
        // NI's own blocks really do list a keyword twice - "files" appears in both slots.
        var (_, keywords, _) = ExampleIndex.Parse(Block);

        Assert.Equal(["files", "TDMS", "scale"], keywords);
    }

    [Fact]
    public void RequiredSoftwareCarriesItsMinimumVersion()
    {
        var (_, _, software) = ExampleIndex.Parse(Block);

        Assert.Equal("LabVIEW >= 13.0", software);
    }

    [Fact]
    public void AMalformedBlockStillYieldsAnEntryRatherThanVanishing()
    {
        // Losing the example would make a parser bug look like a missing example.
        var (description, keywords, software) =
            ExampleIndex.Parse("<ExampleProgram><Description>unclosed</ExampleProgram>");

        Assert.Equal("", description);
        Assert.Empty(keywords);
        Assert.Null(software);
    }

    // ---------- the scan ----------

    [Fact]
    public void OnlyBlockCarryingVisAreListedButAllAreScanned()
    {
        WriteExample("File IO/TDMS/Scale TDMS Data.vi");
        WriteExample("File IO/TDMS/support/Helper SubVI.vi", block: null);

        var index = ExampleIndex.Build(_root);

        Assert.Equal(2, index.ViFilesScanned);
        Assert.Single(index.Examples);
        Assert.Equal("Scale TDMS Data.vi", index.Examples[0].Name);
    }

    [Fact]
    public void CategoryIsTheFolderPathBelowTheExamplesRoot()
    {
        WriteExample("File IO/TDMS/Scale TDMS Data.vi");

        var example = ExampleIndex.Build(_root).Examples.Single();

        Assert.Equal(Path.Combine("File IO", "TDMS"), example.Category);
        Assert.Equal("", example.Source);
        Assert.True(File.Exists(example.Path), "the path must be usable verbatim");
    }

    [Fact]
    public void ResultsAreSortedSoOutputIsStable()
    {
        WriteExample("z/Zebra.vi");
        WriteExample("a/Alpha.vi");
        WriteExample("m/middle.vi");

        var names = ExampleIndex.Build(_root).Examples.Select(e => e.Name).ToList();

        Assert.Equal(["Alpha.vi", "middle.vi", "Zebra.vi"], names);
    }

    [Fact]
    public void RefreshRescansWhileTheDefaultServesTheCache()
    {
        WriteExample("a/First.vi");
        Assert.Single(ExampleIndex.Build(_root).Examples);

        WriteExample("a/Second.vi");
        Assert.Single(ExampleIndex.Build(_root).Examples);                  // cached
        Assert.Equal(2, ExampleIndex.Build(_root, refresh: true).Examples.Count);
    }

    [Fact]
    public void AnInstallationWithoutExamplesIsAClearErrorNotAnEmptyList()
    {
        Directory.CreateDirectory(_root);

        var text = ExampleTools.ExampleIndexTool(installRoot: _root);

        Assert.Contains("DirectoryNotFoundException", text);
        Assert.Contains("examples", text);
    }

    // ---------- add-on examples ----------

    private string WriteAddonExample(string addon, string? minimumVersion, string relative)
    {
        var addonsRoot = Path.Combine(_root, "LVAddons");
        var api = Path.Combine(addonsRoot, addon, "1");
        var path = Path.Combine(api, "examples", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ViBytes(Block));
        if (minimumVersion is not null)
            File.WriteAllText(Path.Combine(api, "lvaddoninfo.json"),
                $"{{\"AddonName\":\"{addon}\",\"MinimumSupportedLVVersion\":\"{minimumVersion}\"}}");
        return addonsRoot;
    }

    [Fact]
    public void AddonExamplesAreScannedAndLabelledWithTheirAddon()
    {
        // Measured: 299 of the 796 examples on this station live in add-on trees, so scanning
        // <LabVIEW>\examples alone would hide entire toolkits.
        WriteExample("File IO/Scale TDMS Data.vi");
        var addons = WriteAddonExample("dfdt", "22.0", "Filter Design/Design a Filter.vi");

        var index = ExampleIndex.Build(_root, refresh: true, addonsRoot: addons);

        Assert.Equal(2, index.Examples.Count);
        Assert.Contains("dfdt", index.AddonsScanned);
        Assert.Equal("dfdt", index.Examples.Single(e => e.Name == "Design a Filter.vi").Source);
        Assert.Equal("", index.Examples.Single(e => e.Name == "Scale TDMS Data.vi").Source);
    }

    [Fact]
    public void TheSameExampleFromTwoAddonVariantsIsOneEntry()
    {
        // aspt32 and aspt64 ship an identical set of 104 examples.
        WriteExample("a/Something.vi");
        var addons = Path.Combine(_root, "LVAddons");
        WriteAddonExample("aspt32", "22.0", "Wavelet/Denoise.vi");
        WriteAddonExample("aspt64", "22.0", "Wavelet/Denoise.vi");

        var index = ExampleIndex.Build(_root, refresh: true, addonsRoot: addons);

        Assert.Equal(2, index.Examples.Count);
        Assert.Single(index.Examples, e => e.Name == "Denoise.vi");
    }

    [Fact]
    public void AddonsAreNotDiscoveredWhenAnInstallRootIsGivenWithoutOne()
    {
        // Otherwise a synthetic-tree test would silently pick up the real machine's drivers.
        WriteExample("a/Something.vi");
        WriteAddonExample("dfdt", "22.0", "b/Other.vi");

        var index = ExampleIndex.Build(_root, refresh: true);

        Assert.Single(index.Examples);
        Assert.Empty(index.AddonsScanned);
    }

    // ---------- the gap that must stay visible ----------

    [Fact]
    public void AnExternalBinaryIndexIsReportedRatherThanIgnored()
    {
        // NI-DAQmx registers its 69 examples through exbins\daq82mxw.bin4 and carries no in-VI
        // block at all. Staying quiet about that reads as "DAQmx ships no examples".
        WriteExample("a/Something.vi");
        var exbins = Path.Combine(_root, "examples", "exbins");
        Directory.CreateDirectory(exbins);
        File.WriteAllBytes(Path.Combine(exbins, "daq82mxw.bin4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(exbins, "DQMH.bin3"), [1, 2, 3]);

        var index = ExampleIndex.Build(_root, refresh: true);
        var text = ExampleTools.ExampleIndexTool(installRoot: _root);

        Assert.Equal(2, index.ExternalIndexes.Count);
        Assert.Contains("NOT COVERED", text);
        Assert.Contains("daq82mxw.bin4", text);
    }

    // ---------- the tool ----------

    [Fact]
    public void WithoutAQueryItReportsTotalsInsteadOfEveryExample()
    {
        WriteExample("File IO/Scale TDMS Data.vi");

        var text = ExampleTools.ExampleIndexTool(installRoot: _root);

        Assert.Contains("1 examples", text);
        Assert.DoesNotContain("Creates scaling information", text);
    }

    [Fact]
    public void AQueryReturnsThePathAndDescriptionSoTheHitIsActionable()
    {
        var path = WriteExample("File IO/TDMS/Scale TDMS Data.vi");

        var text = ExampleTools.ExampleIndexTool(query: "TDMS", installRoot: _root);

        Assert.Contains(path, text);
        Assert.Contains("Creates scaling information on TDMS objects", text);
        Assert.Contains("keywords:", text);
        Assert.Contains("LabVIEW >= 13.0", text);
    }

    [Fact]
    public void KeywordsAndDescriptionAreSearchedNotJustTheFileName()
    {
        WriteExample("File IO/Totally Unrelated Name.vi");

        // "scale" appears only as a keyword, "scaled data" only in the description.
        Assert.Contains("Totally Unrelated Name.vi",
            ExampleTools.ExampleIndexTool(query: "scale", installRoot: _root));
        Assert.Contains("Totally Unrelated Name.vi",
            ExampleTools.ExampleIndexTool(query: "scaled data", installRoot: _root));
    }

    [Fact]
    public void TruncationIsReportedRatherThanSilent()
    {
        for (var i = 0; i < 12; i++) WriteExample($"File IO/Example {i}.vi");

        var text = ExampleTools.ExampleIndexTool(query: "TDMS", limit: 4, installRoot: _root);

        Assert.Contains("12 of 12 match", text);
        Assert.Contains("8 more", text);
    }

    [Fact]
    public void AMissPointsAtTheFallbackInsteadOfStoppingDead()
    {
        WriteExample("a/Something.vi");

        var text = ExampleTools.ExampleIndexTool(query: "carburettor", installRoot: _root);

        Assert.Contains("No example matches", text);
        Assert.Contains("lvai_palette_index", text);
    }

    // ---------- the real installation, when this machine has one ----------

    [Fact]
    public void RealInstallationYieldsExamplesWithDescriptions()
    {
        ExampleIndex.Result index;
        try { index = ExampleIndex.Build(); }
        catch { return; }        // no LabVIEW here; the suite must not require one

        Assert.True(index.Examples.Count > 300, $"only {index.Examples.Count} examples found");
        Assert.True(index.ViFilesScanned > index.Examples.Count,
            "support VIs should be scanned but not listed");

        // Verified by hand on LabVIEW 2026: this example exists and carries a description.
        var tdms = index.Examples
            .Where(e => e.Name.Contains("TDMS", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(tdms);
        Assert.Contains(tdms, e => e.Description.Length > 0);

        Assert.All(index.Examples, e =>
        {
            Assert.EndsWith(".vi", e.Name, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(e.Path), $"stale path: {e.Path}");
        });
    }
}
