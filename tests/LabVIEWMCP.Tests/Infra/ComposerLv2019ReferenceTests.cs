using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The composer's LabVIEW 2019 output against LabVIEW's OWN 2019 file (issue #149).
///
/// THE FIXTURE IS THE REAL ARTEFACT: `Fixtures/lv2019/t162_ten_class_links.vi` is the suite's
/// t162 (two classes, eight dynamic-dispatch calls, two class constants, one vi.lib call) composed
/// as 19, loaded by LabVIEW 2026 and saved by its IDE dialog File > Save for Previous Version >
/// 2019, 2026-11-23. (The ActiveX method `SaveForPrevious(path, version)` writes a 25 file for
/// EVERY version string, "19.0" and "2019" included - which is why this reference was missing.)
/// Composed afresh as 19, t162 must carry the same version stamps, the same SourceOnly variant
/// bytes and the same class-link records in the block diagram as LabVIEW's file. The differences
/// LabVIEW 2025's reference already showed and that load either way are left out: a VIVI LabVIEW
/// drops from LIvi, `APILinkLibVersion` 1.0.0.0 against the class's own, and the uninitialised
/// tail byte. SKIPS where the bundled interpreter or the composer is missing, and where no LabVIEW
/// installation holds the vi.lib callee - t162 calls `Trim Whitespace (string).vi` in
/// `vi.lib\Utility\error.llb`, and the release workflow's runner has no LabVIEW at all
/// (UNSUPPORTED: "neither near the caller nor in the installation", 2026-11-23).
/// </summary>
public sealed class ComposerLv2019ReferenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lv2019ref-" + Guid.NewGuid().ToString("n")[..8]);

    public ComposerLv2019ReferenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Suite => Path.Combine(RepoTree.Root, "experiments", "aixml-without-grpc", "tests");

    private static string Reference =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "lv2019", "t162_ten_class_links.vi");

    private sealed record Extracted(XElement Main, byte[] Vits);

    private async Task<Extracted> ExtractAsync(PyLabview.Bundle bundle, string vi, string name)
    {
        var x = Path.Combine(_dir, "x-" + name);
        Directory.CreateDirectory(x);
        var main = Path.Combine(x, "t162.xml");
        var run = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy, ["-x", "-i", vi, "-m", main], 120, default);
        Assert.True(File.Exists(main), run.StdErr);
        return new Extracted(XElement.Load(main), await File.ReadAllBytesAsync(Path.Combine(x, "t162_VITS.bin")));
    }

    /// <summary>Each block-diagram link: tag, qualified name, content, cache offsets.</summary>
    private static List<string> DiagramLinks(XElement main) =>
        main.Descendants("LIbd").Descendants("BDHP").Elements()
            .Select(rec => string.Join(" | ",
                rec.Name.LocalName,
                string.Join("/", rec.Element("LinkSaveQualName")?.Elements("String").Select(s => s.Value) ?? []),
                rec.Element("APILinkContent")?.Value ?? "",
                string.Join(",", rec.Descendants("Offset").Select(o => o.Value))))
            .ToList();

    private static IEnumerable<string> Versions(XElement main) =>
        main.Descendants("Version").Take(2).Select(v => (string?)v.Attribute("Major") + "." + (string?)v.Attribute("Minor"));

    /// <summary>Does an installed LabVIEW carry the vi.lib callee of t162?</summary>
    // error.llb is a FOLDER in LabVIEW 2026 and a container file in older releases, so either counts.
    private static bool ErrorLlbInstalled() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "National Instruments"))
            .Where(Directory.Exists)
            .SelectMany(ni => Directory.EnumerateDirectories(ni, "LabVIEW 20*"))
            .Select(lv => Path.Combine(lv, "vi.lib", "Utility", "error.llb"))
            .Any(llb => File.Exists(Path.Combine(llb, "Trim Whitespace (string).vi")) || File.Exists(llb));

    [Fact]
    public async Task Composed_as_19_the_class_links_are_LabVIEWs_own_2019_records()
    {
        if (!ErrorLlbInstalled()) return;
        if (AixmlComposer.Locate(out _) is not { } setup) return;
        var vi = Path.Combine(_dir, "t162_ten_class_links.vi");
        var compose = await PyLabview.RunAsync(setup.Bundle, Path.Combine(setup.ComposerDirectory, "aixml2vi.py"),
            ["--parts", setup.PartsDirectory, "--lv-version", "19", "--search", Path.Combine(Suite, "fixtures"),
             Path.Combine(Suite, "t162_ten_class_links.xml"), vi], 300, default);
        Assert.True(compose.ExitCode == 0, compose.StdErr);

        var ours = await ExtractAsync(setup.Bundle, vi, "composed");
        var labview = await ExtractAsync(setup.Bundle, Reference, "labview");

        Assert.Equal(["19.0", "19.0"], Versions(labview.Main));
        Assert.Equal(Versions(labview.Main), Versions(ours.Main));
        Assert.Equal(labview.Vits, ours.Vits);

        var expected = DiagramLinks(labview.Main);
        Assert.Equal(11, expected.Count);
        Assert.Equal(expected, DiagramLinks(ours.Main));
    }
}
