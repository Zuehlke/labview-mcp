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

    /// <summary>
    /// A minimal exbins index registering one example: the five parallel arrays the reader needs,
    /// in the order the real files use. See <see cref="LabVIEWMcp.Infra.ExternalExampleIndex"/>.
    /// </summary>
    private static byte[] ExbinsFile(string relativePath, string description)
    {
        var name = relativePath[(relativePath.LastIndexOf('\\') + 1)..];
        var bytes = new List<byte>();

        void U32(int value) => bytes.AddRange(
            [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        void Text(string s) { U32(1); U32(s.Length); bytes.AddRange(Encoding.ASCII.GetBytes(s)); }

        Text(name);                                                     // 0 names

        var parts = relativePath.Split('\\');                           // 1 PTH0 paths
        var body = new List<byte> { 0x00, 0x01, 0x00, (byte)parts.Length };
        foreach (var part in parts)
        {
            body.Add((byte)part.Length);
            body.AddRange(Encoding.ASCII.GetBytes(part));
        }
        U32(1);
        bytes.AddRange("PTH0"u8.ToArray());
        U32(body.Count);
        bytes.AddRange(body);

        Text("");                                                       // 2 empty
        U32(1); U32(1); U32(1088);                                      // 3 navigation ids
        Text(description);                                              // 4 descriptions
        return [.. bytes];
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

    // ---------- examples that carry no in-VI block ----------

    /// <summary>
    /// NI-DAQmx registers its 56 examples through exbins\daq82mxw.bin4 and not one of them carries
    /// an in-VI block, so a VI-only scan answered "DAQmx" with nothing while they sat on disk.
    /// </summary>
    [Fact]
    public void ExamplesRegisteredThroughAnExbinsIndexAreListedToo()
    {
        WriteExample("a/Has Its Own Block.vi");

        // A real example VI the index points at - the entry is dropped if the file is absent.
        var registered = Path.Combine(_root, "examples", "DAQmx", "Analog Input",
                                      "Voltage - Finite Input.vi");
        Directory.CreateDirectory(Path.GetDirectoryName(registered)!);
        File.WriteAllBytes(registered, ViBytes(null));

        var exbins = Path.Combine(_root, "examples", "exbins");
        Directory.CreateDirectory(exbins);
        File.WriteAllBytes(Path.Combine(exbins, "daq82mxw.bin4"), ExbinsFile(
            @"DAQmx\Analog Input\Voltage - Finite Input.vi",
            "Acquires a finite amount of voltage data from a DAQmx device."));

        var index = ExampleIndex.Build(_root, refresh: true);
        var text = ExampleTools.ExampleIndexTool(query: "voltage", installRoot: _root);

        Assert.Equal(2, index.Examples.Count);
        Assert.Equal(1, index.FromExternalIndexes);
        Assert.Empty(index.ExternalIndexes);                  // nothing left uncovered
        Assert.Contains("Acquires a finite amount of voltage data", text);
        Assert.Contains(Path.Combine("DAQmx", "Analog Input"), text);
    }

    [Fact]
    public void AnInVIBlockWinsOverTheSameExamplesExternalRegistration()
    {
        // Both can describe one example; the block is authoritative and carries RequiredSoftware.
        WriteExample("File IO/Scale TDMS Data.vi");
        var exbins = Path.Combine(_root, "examples", "exbins");
        Directory.CreateDirectory(exbins);
        File.WriteAllBytes(Path.Combine(exbins, "x.bin4"), ExbinsFile(
            @"File IO\Scale TDMS Data.vi", "A different description from the external index."));

        var example = ExampleIndex.Build(_root, refresh: true).Examples.Single();

        Assert.StartsWith("Creates scaling information", example.Description);
        Assert.Equal("LabVIEW >= 13.0", example.RequiredSoftware);
    }

    // ---------- query words ----------

    /// <summary>
    /// The regression this guards. The query used to be passed to Contains as ONE literal
    /// string, so a multi-word query only matched when that exact phrase occurred. Measured on
    /// the real installation: "waveform" gave 74 hits, "build waveform array" gave none. An
    /// empty result does not read as "clumsy query", it reads as "NI has no example for this" -
    /// and sends the caller off to rebuild from primitives.
    /// </summary>
    [Fact]
    public void AMultiWordQueryNeedNotBeALiteralPhrase()
    {
        WriteExample("File IO/TDMS/Scale TDMS Data.vi");

        // Both words are in the description, in the opposite order; the phrase never occurs.
        Assert.Contains("Scale TDMS Data.vi",
            ExampleTools.ExampleIndexTool(query: "reads creates", installRoot: _root));
    }

    [Fact]
    public void WordsMayBeSatisfiedByDifferentFields()
    {
        WriteExample("File IO/TDMS/Scale TDMS Data.vi");

        // "scale" is a keyword, "reads" is only in the description, "File" only in the category.
        Assert.Contains("Scale TDMS Data.vi",
            ExampleTools.ExampleIndexTool(query: "scale reads File", installRoot: _root));
    }

    [Fact]
    public void OneAbsentWordExcludesTheExample()
    {
        WriteExample("File IO/TDMS/Scale TDMS Data.vi");

        var text = ExampleTools.ExampleIndexTool(query: "TDMS unicorn", installRoot: _root);

        Assert.Contains("No example matches", text);
        // AND, not OR: "TDMS" alone would have hit, so the second word has to do the excluding.
        Assert.DoesNotContain("Scale TDMS Data.vi", text);
    }

    /// <summary>
    /// With every word required, the commonest cause of nothing is one word too many. Saying so
    /// is what stops an empty answer being read as "this does not exist".
    /// </summary>
    [Fact]
    public void AnEmptyMultiWordResultSuggestsDroppingAWord()
    {
        WriteExample("File IO/TDMS/Scale TDMS Data.vi");

        var text = ExampleTools.ExampleIndexTool(query: "TDMS unicorn", installRoot: _root);

        Assert.Contains("All 2 words must appear", text);
        Assert.Contains("\"TDMS\"", text);
    }

    [Fact]
    public void ASingleWordQueryBehavesAsBefore()
    {
        WriteExample("File IO/TDMS/Scale TDMS Data.vi");

        Assert.Contains("Scale TDMS Data.vi",
            ExampleTools.ExampleIndexTool(query: "TDMS", installRoot: _root));
    }

    [Theory]
    [InlineData("  TDMS  ", 1)]
    [InlineData("TDMS   scale", 2)]
    public void SurplusWhitespaceIsNotAWord(string query, int expected) =>
        Assert.Equal(expected, ExampleTools.Words(query).Count);

    // ---------- the gap that must stay visible ----------

    [Fact]
    public void AnUnreadableExternalIndexIsReportedRatherThanIgnored()
    {
        // A format this reader does not know must never pass as "nothing to see".
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

        // MEASURED across all 18 exbins indexes: 528 .vi and 37 .lvproj. A project-based example
        // is a whole application, not a diagram - the caller needs lvai_describe_project for it,
        // not lvai_convert_vi_to_aixml, so the two must stay distinguishable rather than filtered.
        Assert.All(index.Examples, e =>
        {
            Assert.True(e.Name.EndsWith(".vi", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".lvproj", StringComparison.OrdinalIgnoreCase),
                        $"unexpected example type: {e.Name}");
            Assert.True(File.Exists(e.Path), $"stale path: {e.Path}");
        });
        Assert.Contains(index.Examples, e =>
            e.Name.EndsWith(".lvproj", StringComparison.OrdinalIgnoreCase));
    }
}
