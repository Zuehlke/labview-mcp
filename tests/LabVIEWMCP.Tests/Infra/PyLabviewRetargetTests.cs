using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards pylv-retarget-subvi.py against a bundle whose placeholder is LIBRARY-OWNED, which is
/// the normal case rather than the exotic one: almost everything on a modern LabVIEW palette
/// belongs to a .lvlib.
///
/// Two defects these tests exist for, both measured 2026-08-27 while retargeting a generated
/// Caraya unit test off NI_Gmath.lvlib:Error Function.vi:
///
///   1. A qualified name is a SEGMENT LIST. Reading only its first segment made --list print
///      "NI_Gmath.lvlib" for a bundle that calls Error Function.vi three times, and then reject
///      the name off the diagram as "not a subVI link in this bundle" - which reads as though
///      the VI were not called at all, and sends the reader looking in the wrong place.
///   2. A library-owned link record carries a VILSPathRef naming the owning library. Two subVIs
///      of the SAME library each carry their own, so a global replace strips the library from
///      the record that was NOT retargeted. That is why the edits are record-scoped, and it is
///      the one thing here that no single-subVI fixture would catch.
///
/// The tests run the real script through the real bundled interpreter. Both are optional in a
/// fresh checkout, so an absent bundle is a skip, not a failure.
/// </summary>
public sealed class PyLabviewRetargetTests : IDisposable
{
    private readonly List<string> _temporaryDirectories = [];

    public void Dispose()
    {
        foreach (var dir in _temporaryDirectories)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
                                "pylv-retarget-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    private static string? ScriptPath()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "pylv-retarget-subvi.py");
        return File.Exists(script) ? script : null;
    }

    /// <summary>Run the script, or return null when the bundle or the script is not there.</summary>
    private static async Task<PyLabview.Run?> RunAsync(params string[] args)
    {
        var bundle = PyLabview.Locate();
        var script = ScriptPath();
        if (bundle is null || script is null) return null;
        return await PyLabview.RunAsync(bundle, script, args, 30, CancellationToken.None);
    }

    private const string Indent = "            ";

    private static string Segments(params string[] parts) =>
        string.Concat(parts.Select(p => $"\n{Indent}<String>{p}</String>"));

    /// <summary>One link record, in the shape pylabview writes it.</summary>
    private static string Record(string element, string[] qualified, string[] path,
                                 string[]? library = null)
    {
        var text =
            "        <" + element + " LinkSaveFlag=\"0\" VILinkLibVersion=\"14\">\n" +
            "          <LinkSaveQualName>" + Segments(qualified) + "\n" +
            Indent + "</LinkSaveQualName>\n" +
            "          <LinkSavePathRef Ident=\"PTH0\" TpVal=\"0\">" + Segments(path) + "\n" +
            Indent + "</LinkSavePathRef>\n" +
            "          <TypeDesc TypeID=\"0\" />\n";
        if (library is not null)
            text +=
                "          <VILSPathRef Ident=\"PTH0\" TpVal=\"0\">" + Segments(library) + "\n" +
                Indent + "</VILSPathRef>\n";
        return text + "          </" + element + ">\n";
    }

    /// <summary>
    /// A bundle calling two subVIs OF THE SAME LIBRARY plus one plain VI. The two siblings are
    /// the point: retargeting one must leave the other's owning-library path alone.
    /// </summary>
    private string LibraryOwnedBundle(out string mainXml, out string heapXml)
    {
        var dir = NewDirectory();
        mainXml = Path.Combine(dir, "Fixture.xml");
        heapXml = Path.Combine(dir, "Fixture_BDHb.xml");

        File.WriteAllText(mainXml,
            "<RSRC>\n      <LVIN>\n" +
            Record("VIVI", ["NI_Gmath.lvlib", "Error Function.vi"],
                   ["&lt;vilib&gt;", "gmath", "SpecialFunctions.llb", "Error Function.vi"],
                   ["&lt;vilib&gt;", "gmath", "NI_Gmath.lvlib"]) +
            Record("IUVI", ["NI_Gmath.lvlib", "Error Function Complement.vi"],
                   ["&lt;vilib&gt;", "gmath", "SpecialFunctions.llb",
                    "Error Function Complement.vi"],
                   ["&lt;vilib&gt;", "gmath", "NI_Gmath.lvlib"]) +
            Record("IUVI", ["Simple Error Handler.vi"],
                   ["&lt;vilib&gt;", "Utility", "error.llb", "Simple Error Handler.vi"]) +
            "      </LVIN>\n</RSRC>\n");

        File.WriteAllText(heapXml,
            "<heap>\n  <text>\"Error Function.vi\"</text>\n" +
            "  <text>\"Error Function.vi\"</text>\n" +
            "  <text>\"Error Function Complement.vi\"</text>\n</heap>\n");
        return dir;
    }

    [Fact]
    public async Task ListPrintsTheWholeQualifiedName_NotJustTheLibrary()
    {
        LibraryOwnedBundle(out var mainXml, out var heapXml);
        var run = await RunAsync(mainXml, heapXml, "--list");
        if (run is null) return;                       // no bundle: nothing to check

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("NI_Gmath.lvlib:Error Function.vi", run.StdOut);
        Assert.Contains("Simple Error Handler.vi", run.StdOut);
    }

    [Fact]
    public async Task TheBareVIName_IsAcceptedWhenItIsUnambiguous()
    {
        LibraryOwnedBundle(out var mainXml, out var heapXml);
        var run = await RunAsync(mainXml, heapXml, "Error Function.vi", "New.vi");
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        var text = File.ReadAllText(mainXml);
        Assert.Contains("<String>New.vi</String>", text);
        // The library qualifier goes with it: the new target is a plain VI.
        Assert.DoesNotContain("<String>NI_Gmath.lvlib</String>\n            <String>New.vi",
                              text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task RetargetingOneSubVI_LeavesItsLibrarySiblingsOwningPathAlone()
    {
        LibraryOwnedBundle(out var mainXml, out var heapXml);
        var run = await RunAsync(mainXml, heapXml, "Error Function.vi", "New.vi");
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        var text = File.ReadAllText(mainXml);

        // Exactly one VILSPathRef was cleared - the retargeted record's.
        Assert.Contains("""<VILSPathRef Ident="PTH0" TpVal="0" ZeroFill="True" />""", text);
        Assert.Contains("owning library    1 VILSPathRef cleared", run.StdOut);
        // The sibling from the same library still names it TWICE - once as its own qualifier
        // and once as its owning-library path. Both are what a global replace would have eaten.
        Assert.Contains("Error Function Complement.vi", text);
        Assert.Equal(2, CountOccurrences(text, "<String>NI_Gmath.lvlib</String>"));
        Assert.Equal(1, CountOccurrences(text, "ZeroFill=\"True\""));
    }

    [Fact]
    public async Task APlainSubVI_IsRetargetedWithNoLibraryStepAtAll()
    {
        LibraryOwnedBundle(out var mainXml, out var heapXml);
        var run = await RunAsync(mainXml, heapXml, "Simple Error Handler.vi", "Handler.vi");
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("owning library", run.StdOut);
        var text = File.ReadAllText(mainXml);
        // Only the file name changed; the symbolic root and folders are untouched.
        Assert.Contains("<String>&lt;vilib&gt;</String>", text);
        Assert.Contains("<String>error.llb</String>", text);
        Assert.Contains("<String>Handler.vi</String>", text);
    }

    [Fact]
    public async Task TheDiagramCaption_FollowsTheLinkSoTheDiagramDoesNotLie()
    {
        LibraryOwnedBundle(out var mainXml, out var heapXml);
        var run = await RunAsync(mainXml, heapXml, "Error Function.vi", "New.vi");
        if (run is null) return;

        var heap = File.ReadAllText(heapXml);
        Assert.Equal(2, CountOccurrences(heap, "\"New.vi\""));
        // The sibling's caption is a different string and must survive.
        Assert.Contains("\"Error Function Complement.vi\"", heap);
        Assert.Contains("node caption      2 replacement(s)", run.StdOut);
    }

    [Fact]
    public async Task AnUnknownName_AbortsAndListsWhatIsActuallyThere()
    {
        LibraryOwnedBundle(out var mainXml, out var heapXml);
        var run = await RunAsync(mainXml, heapXml, "Nope.vi", "New.vi");
        if (run is null) return;

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("is not a subVI link in this bundle", run.StdErr);
        Assert.Contains("NI_Gmath.lvlib:Error Function.vi", run.StdErr);
    }

    [Fact]
    public async Task ALibraryOwnedNewTarget_IsRefusedRatherThanHalfWritten()
    {
        LibraryOwnedBundle(out var mainXml, out var heapXml);
        var run = await RunAsync(mainXml, heapXml, "Error Function.vi", "MyLib.lvlib:New.vi");
        if (run is null) return;

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("names a library-owned target", run.StdErr);
        // Refused BEFORE writing: the file is untouched.
        Assert.Contains("<String>Error Function.vi</String>", File.ReadAllText(mainXml));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
