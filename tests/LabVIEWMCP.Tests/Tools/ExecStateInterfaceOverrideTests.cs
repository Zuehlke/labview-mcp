// ExecStateInterfaceOverrideTests.cs - lvai_exec_state's file-only check for interface methods a
// broken VI's class does not override, run over byte copies of NI's own Basic Interfaces example.
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Which interface methods a broken VI's owning class does not override.
///
/// THE FIXTURE IS NI'S OWN FILES, byte for byte: `Flathead.lvclass` implements the `Lever`
/// interface and derives from `Rotating Tool`, copied from
/// examples\Object-Oriented Programming\Basic Interfaces of LabVIEW 2026. `Lever` records no
/// IsStaticMethod for either member, so dispatch comes from each member's connector pane - the
/// real pylabview read, not a stand-in - and its two members are the two shapes that matter:
/// `Multiply Force.vi` dynamic, `Pry.vi` static. "One override removed" is that file with its
/// `Multiply Force.vi` item cut out, done here at run time so the committed copy stays NI's.
/// </summary>
public sealed class ExecStateInterfaceOverrideTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("iface-overrides").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures",
                                                   "basic-interfaces");

    /// <summary>A copy of the whole fixture tree, so the relative parent URLs still resolve.</summary>
    private string CopyTree()
    {
        foreach (var file in Directory.EnumerateFiles(Fixture, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_root, Path.GetRelativePath(Fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return _root;
    }

    /// <summary>Delete one member's item block from a copied class file - the "deleted override".</summary>
    private static void RemoveMember(string lvclass, string member)
    {
        var text = File.ReadAllText(lvclass);
        var start = text.IndexOf($"<Item Name=\"{member}\" Type=\"VI\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{member} is not listed in {lvclass}");
        var lineStart = text.LastIndexOf('\n', start) + 1;
        var end = text.IndexOf("</Item>", start, StringComparison.Ordinal) + "</Item>".Length;
        var lineEnd = text.IndexOf('\n', end) + 1;
        File.WriteAllText(lvclass, text[..lineStart] + text[lineEnd..]);
    }

    private static Func<string, Task<bool?>> RealPaneReader()
    {
        var bundle = PyLabview.Locate();
        return async vi => bundle is null
            ? null
            : (await PaneDispatch.ReadAsync(bundle, vi, 60, CancellationToken.None))?.DynamicDispatch;
    }

    private string TurnVi(string root) => Path.Combine(root, "Flathead", "Turn.vi");

    /// <summary>
    /// The case the check exists for: the dynamic interface method's override is gone, and the
    /// static one is not reported because a static member is not overridden at all.
    /// </summary>
    [Fact]
    public async Task A_deleted_override_of_a_dynamic_interface_method_is_named()
    {
        if (PyLabview.Locate() is null) return;
        var root = CopyTree();
        RemoveMember(Path.Combine(root, "Flathead", "Flathead.lvclass"), "Multiply Force.vi");

        var report = await ExecStateTools.InterfaceOverridesAsync(TurnVi(root), RealPaneReader());

        Assert.NotNull(report.Missing);
        var missing = Assert.Single(report.Missing);
        Assert.Equal("Lever.lvclass", missing.Interface);
        Assert.Equal("Multiply Force.vi", missing.Member);
        Assert.Null(missing.MustOverride);              // Lever records no MustOverride for it
        Assert.Empty(report.DispatchUnknown);
        Assert.Null(report.Reason);
        Assert.EndsWith("Lever.lvclass", Assert.Single(report.InterfacesChecked));
    }

    /// <summary>THE CONTROL ARM: NI's unmodified Flathead overrides it, so nothing is missing.</summary>
    [Fact]
    public async Task The_class_as_NI_ships_it_misses_nothing()
    {
        if (PyLabview.Locate() is null) return;
        var root = CopyTree();

        var report = await ExecStateTools.InterfaceOverridesAsync(TurnVi(root), RealPaneReader());

        Assert.NotNull(report.Missing);
        Assert.Empty(report.Missing);
        Assert.Empty(report.DispatchUnknown);
    }

    /// <summary>
    /// A member whose dispatch cannot be read is NOT missing - calling it missing would send the
    /// reader to write an override for a method that may be static.
    /// </summary>
    [Fact]
    public async Task An_unreadable_dispatch_goes_to_dispatchUnknown_and_never_to_missing()
    {
        var root = CopyTree();
        RemoveMember(Path.Combine(root, "Flathead", "Flathead.lvclass"), "Multiply Force.vi");

        var report = await ExecStateTools.InterfaceOverridesAsync(
            TurnVi(root), _ => Task.FromResult<bool?>(null));

        Assert.NotNull(report.Missing);
        Assert.Empty(report.Missing);
        Assert.Equal(["Multiply Force.vi", "Pry.vi"],
                     report.DispatchUnknown.Select(u => u.Member).OrderBy(n => n));
    }

    /// <summary>The interface's own Rotating Tool base is a CLASS and contributes no obligation.</summary>
    [Fact]
    public async Task A_class_with_no_interface_answers_null_with_a_reason()
    {
        var root = CopyTree();

        var report = await ExecStateTools.InterfaceOverridesAsync(
            Path.Combine(root, "Rotating Tool", "Turn.vi"), _ => Task.FromResult<bool?>(true));

        Assert.Null(report.Missing);
        Assert.StartsWith("noInterfaces", report.Reason);
    }

    [Fact]
    public async Task A_VI_in_no_class_answers_null_with_a_reason()
    {
        var loose = Path.Combine(_root, "loose", "Loose.vi");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllBytes(loose, [0]);

        var report = await ExecStateTools.InterfaceOverridesAsync(
            loose, _ => Task.FromResult<bool?>(true));

        Assert.Null(report.Missing);
        Assert.StartsWith("notAClassMember", report.Reason);
    }

    [Fact]
    public async Task A_member_of_the_interface_itself_is_not_checked()
    {
        var root = CopyTree();

        var report = await ExecStateTools.InterfaceOverridesAsync(
            Path.Combine(root, "Lever", "Pry.vi"), _ => Task.FromResult<bool?>(true));

        Assert.Null(report.Missing);
        Assert.StartsWith("ownerIsAnInterface", report.Reason);
    }

    [Fact]
    public void The_answer_carries_the_missing_list_and_says_a_missing_override_breaks_the_class()
    {
        var fields = ExecStateTools.DescribeInterfaceOverrides(new(
            @"C:\x\Flathead.lvclass", [@"C:\x\Lever.lvclass"],
            [new("Lever.lvclass", "Multiply Force.vi", null)], [], [], null));

        var missing = fields["missingInterfaceOverrides"]!.AsArray();
        Assert.Equal("Multiply Force.vi", missing[0]!["member"]!.GetValue<string>());
        Assert.Contains("EVERY MEMBER", fields["missingInterfaceOverridesNote"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_missing_carries_no_note()
    {
        var fields = ExecStateTools.DescribeInterfaceOverrides(new(
            @"C:\x\Flathead.lvclass", [@"C:\x\Lever.lvclass"], [], [], [], null));

        Assert.Empty(fields["missingInterfaceOverrides"]!.AsArray());
        Assert.Null(fields["missingInterfaceOverridesNote"]);
    }

    /// <summary>The fixture really is NI's: the committed copies are byte-identical to the install.</summary>
    [Fact]
    public void The_fixture_is_NIs_file_unmodified_where_the_install_is_present()
    {
        const string install =
            @"C:\Program Files (x86)\National Instruments\LabVIEW 2026\examples\Object-Oriented Programming\Basic Interfaces";
        if (!Directory.Exists(install)) return;

        foreach (var file in Directory.EnumerateFiles(Fixture, "*", SearchOption.AllDirectories))
        {
            var original = Path.Combine(install, Path.GetRelativePath(Fixture, file));
            Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(file));
        }
    }
}
