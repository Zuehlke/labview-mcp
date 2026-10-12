// LvTextFileTests.cs - .lvproj/.lvclass/.lvlib edits keep the BOM and line endings LabVIEW saved,
// proven on byte copies of real LabVIEW-saved files, plus the guard that keeps every writer on it.
using System.Text;
using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Every edit path that writes a project-tree file keeps the file's BYTE STYLE.
///
/// THE FIXTURES ARE REAL, byte for byte (.gitattributes keeps git from converting them):
/// `Strassenkarte.lvproj`, `.lvclass` and `.lvlib` are an Actor Framework experiment LabVIEW 2026
/// saved on this station - BOM + CRLF, the shape LabVIEW writes - and `Strassenkarte Basis
/// Msg.lvclass` carries LabVIEW's own `\r\r\n` inside `NI.LVClass.Geneology`. NI's shipped
/// `Lever.lvclass` (Basic Interfaces example) is BOM + CRLF too. The LF and the mixed variants
/// are DERIVED from those real files at run time, because no LabVIEW-saved file found on this
/// station has either shape - the derivation is a byte transformation of a real file, not an
/// invented one.
///
/// THE DEFECT: every writer used File.ReadAllText / File.WriteAllText, which strip the BOM and
/// write none, and LvClass.Project wrote a new project with no BOM - so each edit turned into a
/// whole-file diff against LabVIEW's next save.
/// </summary>
public sealed class LvTextFileTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private readonly string _root = Directory.CreateTempSubdirectory("lvtextfile").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name) =>
        RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "project-files", name);

    private string Copy(string fixture, string? asName = null)
    {
        var target = Path.Combine(_root, asName ?? Path.GetFileName(fixture));
        File.Copy(fixture, target, overwrite: true);
        return target;
    }

    private static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    private static int BareLf(byte[] b) =>
        b.Select((x, i) => (x, i)).Count(p => p.x == (byte)'\n' && (p.i == 0 || b[p.i - 1] != '\r'));

    private static int Crlf(byte[] b) =>
        b.Select((x, i) => (x, i)).Count(p => p.x == (byte)'\n' && p.i > 0 && b[p.i - 1] == '\r');

    /// <summary>
    /// The edited bytes equal the original with exactly <paramref name="expectedAdded"/> lines
    /// added - compared as LINES WITH THEIR ENDINGS, so a single changed line ending anywhere
    /// fails it. (A removal is checked as exact bytes instead: original minus the block.)
    /// </summary>
    private static void AssertOnlyIntendedLines(byte[] before, byte[] after, string[] expectedAdded)
    {
        static List<string> Lines(byte[] b) =>
            [.. Regex.Split(Encoding.UTF8.GetString(b), "(?<=\n)").Where(l => l.Length > 0)];

        var a = Lines(before);
        var b2 = Lines(after);
        Assert.Equal(HasBom(before), HasBom(after));

        var added = b2.ToList();
        foreach (var line in a)
        {
            var at = added.IndexOf(line);
            Assert.True(at >= 0, $"an untouched line changed or vanished: {line.TrimEnd()}");
            added.RemoveAt(at);
        }
        Assert.Equal(expectedAdded.Length, added.Count);
        foreach (var want in expectedAdded)
            Assert.Contains(added, l => l.Contains(want));
        Assert.Equal(a.Count + expectedAdded.Length, b2.Count);
    }

    // ---------------------------------------------------------------- the fixtures are what they claim

    [Fact]
    public void Every_real_fixture_is_BOM_and_CRLF_NIs_shipped_class_included()
    {
        foreach (var path in new[] { "Strassenkarte.lvproj", "Strassenkarte.lvclass",
                                     "Strassenkarte.lvlib", "Strassenkarte Basis Msg.lvclass" }
                     .Select(Fixture).Append(Lever))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(HasBom(bytes), path);
            Assert.Equal(0, BareLf(bytes));
            Assert.True(Crlf(bytes) > 0, path);
        }
    }

    private static string Lever => RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures",
                                                 "basic-interfaces", "Lever", "Lever.lvclass");

    // ---------------------------------------------------------------- the primitive

    [Theory]
    [InlineData("Strassenkarte.lvproj")]
    [InlineData("Strassenkarte.lvclass")]
    [InlineData("Strassenkarte.lvlib")]
    [InlineData("Strassenkarte Basis Msg.lvclass")]
    public void A_read_and_write_round_trip_is_byte_identical(string name)
    {
        var path = Copy(Fixture(name));
        var before = File.ReadAllBytes(path);

        var (text, style) = LvTextFile.Read(path);
        LvTextFile.Write(path, text, style);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>LabVIEW's own `\r\r\n` in the Geneology property survives a CRLF write.</summary>
    [Fact]
    public void A_lone_CR_is_never_a_line_break_and_survives()
    {
        var bytes = File.ReadAllBytes(Fixture("Strassenkarte Basis Msg.lvclass"));
        Assert.Contains("\r\r\n", Encoding.UTF8.GetString(bytes));   // the control: it is there

        var (text, style) = LvTextFile.Read(Fixture("Strassenkarte Basis Msg.lvclass"));

        Assert.False(style.Mixed);
        Assert.Equal("\r\n", style.Newline);
        Assert.Equal(bytes, LvTextFile.Encode(text, style));
    }

    [Fact]
    public void A_bare_LF_inserted_into_a_CRLF_file_is_written_as_CRLF()
    {
        var (text, style) = LvTextFile.Read(Fixture("Strassenkarte.lvproj"));
        var edited = text.Replace("</Project>", "<!-- x -->\n</Project>");

        var bytes = LvTextFile.Encode(edited, style);

        Assert.True(HasBom(bytes));
        Assert.Equal(0, BareLf(bytes));
    }

    [Fact]
    public void A_new_file_gets_LabVIEWs_style()
    {
        var path = Path.Combine(_root, "New.lvproj");
        LvTextFile.WriteNew(path, LvClass.Project([("A.lvclass", "A/A.lvclass")], "26008000"));

        var bytes = File.ReadAllBytes(path);
        Assert.True(HasBom(bytes));
        Assert.Equal(0, BareLf(bytes));
        Assert.Equal(1, Encoding.UTF8.GetString(bytes).Count(c => c == '\uFEFF'));
    }

    /// <summary>A caller still holding a U+FEFF cannot make the file carry two BOMs.</summary>
    [Fact]
    public void A_leading_FEFF_in_the_text_is_not_written_twice()
    {
        var bytes = LvTextFile.Encode("\uFEFF<Project/>\r\n", LvTextFile.Style.LabView);
        Assert.Equal([.. Bom, .. Encoding.UTF8.GetBytes("<Project/>\r\n")], bytes);
    }

    /// <summary>
    /// A MIXED file is written back as given: a line nobody edited keeps its own ending, and the
    /// majority ending is what the caller joins new lines with.
    /// </summary>
    [Fact]
    public void A_mixed_file_keeps_every_untouched_line_ending()
    {
        var original = Encoding.UTF8.GetString(File.ReadAllBytes(Fixture("Strassenkarte.lvproj")));
        // Two of the nineteen lines LF, the rest CRLF - derived from the real file.
        var lines = Regex.Split(original, "(?<=\r\n)").Where(l => l.Length > 0).ToList();
        lines[3] = lines[3].Replace("\r\n", "\n");
        lines[4] = lines[4].Replace("\r\n", "\n");
        var path = Path.Combine(_root, "Mixed.lvproj");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Concat(lines)));
        var before = File.ReadAllBytes(path);

        var (text, style) = LvTextFile.Read(path);
        Assert.True(style.Mixed);
        Assert.Equal("\r\n", style.Newline);

        Assert.True(LvClass.AddToProject(path, "Neu", "../Neu/Neu.lvclass"));

        var after = File.ReadAllBytes(path);
        AssertOnlyIntendedLines(before, after, ["Neu.lvclass"]);
        Assert.Equal(2, BareLf(after));                               // the two LF lines, untouched
        LvTextFile.Write(path, text, style);
        Assert.Equal(before, File.ReadAllBytes(path));                // as given, not normalised
    }

    // ---------------------------------------------------------------- every edit path

    [Fact]
    public void AddToProject_keeps_the_BOM_and_CRLF_and_adds_one_line()
    {
        var path = Copy(Fixture("Strassenkarte.lvproj"));
        var before = File.ReadAllBytes(path);

        Assert.True(LvClass.AddToProject(path, "Strassenkarte Basis Msg",
                                         "../Strassenkarte Basis Msg/Strassenkarte Basis Msg.lvclass"));

        var after = File.ReadAllBytes(path);
        Assert.True(HasBom(after));
        Assert.Equal(0, BareLf(after));
        AssertOnlyIntendedLines(before, after, ["Strassenkarte Basis Msg.lvclass"]);
    }

    /// <summary>A file with no BOM and LF only stays that way - nothing is invented.</summary>
    [Fact]
    public void AddToProject_on_a_no_BOM_LF_project_keeps_it_no_BOM_LF()
    {
        var real = File.ReadAllBytes(Fixture("Strassenkarte.lvproj"));
        var lf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(real, 3, real.Length - 3)
                                                .Replace("\r\n", "\n"));
        var path = Path.Combine(_root, "Plain.lvproj");
        File.WriteAllBytes(path, lf);

        Assert.True(LvClass.AddToProject(path, "Neu", "../Neu/Neu.lvclass"));

        var after = File.ReadAllBytes(path);
        Assert.False(HasBom(after));
        Assert.Equal(0, Crlf(after));
        AssertOnlyIntendedLines(lf, after, ["Neu.lvclass"]);
    }

    [Fact]
    public void AddVisToProject_keeps_the_BOM_and_CRLF()
    {
        var path = Copy(Fixture("Strassenkarte.lvproj"));
        var before = File.ReadAllBytes(path);

        var added = LvClass.AddVisToProject(path, "Tests",
            [("Test A.vi", "../Tests/Test A.vi"), ("Test B.vi", "../Tests/Test B.vi")]);

        Assert.Equal(2, added);
        var after = File.ReadAllBytes(path);
        Assert.True(HasBom(after));
        Assert.Equal(0, BareLf(after));
        // A new folder: its opening line, the two VIs, its closing line.
        AssertOnlyIntendedLines(before, after,
            ["Name=\"Tests\"", "Test A.vi", "Test B.vi", "</Item>"]);
    }

    /// <summary>Add then remove: the file comes back byte for byte, BOM included.</summary>
    [Fact]
    public void RemoveTargetLevelVis_after_an_add_restores_the_file_byte_for_byte()
    {
        var path = Copy(Fixture("Strassenkarte.lvproj"));
        var before = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(before);
        var withVi = text.Replace("\t\t<Item Name=\"Dependencies\"",
            "\t\t<Item Name=\"Stray.vi\" Type=\"VI\" URL=\"../Stray.vi\"/>\r\n\t\t<Item Name=\"Dependencies\"");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(withVi));

        var removed = LvClass.RemoveTargetLevelVis(path, ["Stray.vi"]);

        Assert.Equal(["Stray.vi"], removed);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>The close's sweep - StripHelperItems plus the duplicate repair - on a real project.</summary>
    [Fact]
    public void The_close_sweep_removes_a_helper_and_leaves_the_file_byte_identical()
    {
        var path = Copy(Fixture("Strassenkarte.lvproj"));
        var before = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(before);
        var withHelper = text.Replace("\t\t<Item Name=\"Dependencies\"",
            "\t\t<Item Name=\"lvai_close_active_project.vi\" Type=\"VI\" " +
            "URL=\"../../../Temp/LabVIEWMCP/helpers/lvai_close_active_project.vi\"/>\r\n" +
            "\t\t<Item Name=\"Dependencies\"");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(withHelper));

        var sweep = CloseTools.SweepClosedProject(path);

        Assert.True(sweep["swept"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RemoveMemberEntries_on_a_class_keeps_the_BOM_and_CRLF_and_drops_one_block()
    {
        var path = Copy(Fixture("Strassenkarte.lvclass"));
        var before = File.ReadAllBytes(path);
        var removed = ClassMethodTools.RemoveMemberEntries(path, [@"C:\any\Read Name.vi"]);

        Assert.Equal(["Read Name.vi"], removed);
        Assert.Equal(WithoutItem(before, "Read Name.vi"), File.ReadAllBytes(path));
    }

    /// <summary>
    /// The original bytes with one member's whole item block - its lines and their endings - cut
    /// out. A removal is checked against this EXACTLY, so the BOM and every other line ending
    /// are part of the assertion.
    /// </summary>
    private static byte[] WithoutItem(byte[] original, string member)
    {
        var text = Encoding.UTF8.GetString(original);   // keeps the U+FEFF, so the BOM is compared too
        var block = Regex.Match(text,
            $"\t<Item Name=\"{Regex.Escape(member)}\" Type=\"VI\"[^\n]*\n(?:\t\t[^\n]*\n)*?\t</Item>\r?\n");
        Assert.True(block.Success, $"{member} block not found");
        return Encoding.UTF8.GetBytes(text.Remove(block.Index, block.Length));
    }

    /// <summary>NI's own shipped class, BOM + CRLF: one block goes, every other byte stays.</summary>
    [Fact]
    public void RemoveMemberEntries_on_NIs_shipped_class_drops_one_block_and_nothing_else()
    {
        var path = Copy(Lever);
        var before = File.ReadAllBytes(path);

        Assert.Equal(["Pry.vi"], ClassMethodTools.RemoveMemberEntries(path, [@"C:\x\Pry.vi"]));

        Assert.Equal(WithoutItem(before, "Pry.vi"), File.ReadAllBytes(path));
    }

    /// <summary>BOM + LF, derived from NI's class: the LF stays LF and the BOM stays.</summary>
    [Fact]
    public void RemoveMemberEntries_on_a_BOM_LF_class_keeps_it_BOM_LF()
    {
        var real = File.ReadAllBytes(Lever);
        var lf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(real).Replace("\r\n", "\n"));
        var path = Path.Combine(_root, "Lever.lvclass");
        File.WriteAllBytes(path, lf);

        Assert.Equal(["Pry.vi"], ClassMethodTools.RemoveMemberEntries(path, [@"C:\x\Pry.vi"]));

        var after = File.ReadAllBytes(path);
        Assert.True(HasBom(after));
        Assert.Equal(0, Crlf(after));
        Assert.Equal(WithoutItem(lf, "Pry.vi"), after);
    }

    // ---------------------------------------------------------------- the guard

    /// <summary>
    /// The write calls the guard looks at, with the FIRST argument captured. A write whose target
    /// names a project-tree file must go through LvTextFile.
    /// </summary>
    private static readonly Regex WriteCall = new(
        @"(?:File\.(?:WriteAll(?:Text|Lines|Bytes)|AppendAll(?:Text|Lines))(?:Async)?|new\s+StreamWriter|\.Save)\s*\(\s*(?<target>[^,)]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// NAMES, because a static scan cannot follow a value: the target expression mentions a
    /// project, class or library - a variable such as `projectPath`, `classPath`, `lvclassPath`, or
    /// a literal extension. A writer that hands such a file to a variable called `target` is not
    /// caught; that is the limit of this guard, stated rather than hidden.
    /// </summary>
    private static readonly Regex ProjectTreeTarget = new(
        @"(?i)proj|lvclass|lvlib|classPath|classFile|libraryPath|libPath",
        RegexOptions.Compiled);

    internal static IEnumerable<string> Offenders(string source, string fileName) =>
        WriteCall.Matches(source)
            .Where(m => ProjectTreeTarget.IsMatch(m.Groups["target"].Value))
            .Select(m => $"{fileName}: {m.Value.Trim()}");

    /// <summary>THE CONTROL ARM: the guard does catch the shape it bans.</summary>
    [Fact]
    public void The_guard_catches_the_shapes_it_bans_and_passes_the_ones_it_allows()
    {
        Assert.Single(Offenders("File.WriteAllText(projectPath, tidied);", "x.cs"));
        Assert.Single(Offenders("await File.WriteAllTextAsync(\n    classPath, text, ct);", "x.cs"));
        Assert.Single(Offenders("document.Save(lvclassPath);", "x.cs"));
        Assert.Empty(Offenders("File.WriteAllText(carrierAixml, xml);", "x.cs"));
        Assert.Empty(Offenders("LvTextFile.Write(projectPath, text, style);", "x.cs"));
    }

    [Fact]
    public void No_project_class_or_library_file_is_written_outside_LvTextFile()
    {
        var src = RepoTree.Path("src");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (Path.GetFileName(file) == "LvTextFile.cs") continue;
            var code = string.Join("\n", File.ReadAllLines(file)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            offenders.AddRange(Offenders(code, Path.GetFileName(file)));
        }

        Assert.True(offenders.Count == 0,
            "a .lvproj/.lvclass/.lvlib is written outside LvTextFile, which drops the BOM: " +
            string.Join(" | ", offenders));
    }
}
