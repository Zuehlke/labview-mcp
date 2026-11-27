// =============================================================================
// ViLinksTests.cs - the link records of a real VI, and lvai_check_links' verdict
// against an installation folder built to hold some of them and not others.
// =============================================================================
using System.Text;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public sealed class ViLinksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vilinks").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Launcher => OwnerHousekeepingTests.Fixture("Launcher.vi");

    /// <summary>
    /// The housekeeping fixture's Launcher.vi (an Actor Framework launcher saved by LabVIEW) links
    /// three installation paths and one relative class; read from its own bytes.
    /// </summary>
    [Fact]
    public void The_link_records_of_a_real_vi_are_read()
    {
        var links = ViLinks.Read(File.ReadAllBytes(Launcher)).Select(l => l.ToString()).ToHashSet();

        Assert.Contains(@"<vilib>\ActorFramework\Actor\Launch Root Actor.vi", links);
        Assert.Contains(@"<vilib>\Utility\error.llb\Simple Error Handler.vi", links);
        Assert.Contains(@"<vilib>\ActorFramework\Actor\Actor.lvclass", links);
        Assert.Contains(@"\Classes\Coffee Shop\Coffee Shop.lvclass", links);   // relative: an empty first part
    }

    /// <summary>An installation with the plain VI, an .llb that names its member, and no class.</summary>
    private string Install(bool llbHoldsMember)
    {
        var root = Path.Combine(_dir, llbHoldsMember ? "lv-a" : "lv-b");
        var actor = Path.Combine(root, "vi.lib", "ActorFramework", "Actor");
        Directory.CreateDirectory(actor);
        File.WriteAllText(Path.Combine(actor, "Launch Root Actor.vi"), "x");
        var utility = Path.Combine(root, "vi.lib", "Utility");
        Directory.CreateDirectory(utility);
        var member = Encoding.Latin1.GetBytes(llbHoldsMember ? "Simple Error Handler.vi" : "General Error Handler.vi");
        File.WriteAllBytes(Path.Combine(utility, "error.llb"), [0, 1, (byte)member.Length, .. member, 2, 3]);
        return root;
    }

    [Fact]
    public void A_missing_class_and_a_member_absent_from_its_llb_are_named()
    {
        var answer = LinkCheckTools.Check([Launcher], Install(llbHoldsMember: false), 2020, "test");

        Assert.False(answer["ok"]!.GetValue<bool>());
        var missing = answer["missing"]!.AsArray().Select(m => (Link: m!["link"]!.GetValue<string>(), Why: m["why"]!.GetValue<string>()))
            .ToDictionary(m => m.Link, m => m.Why);
        Assert.Equal(2, missing.Count);
        Assert.Contains("no such file", missing[@"<vilib>\ActorFramework\Actor\Actor.lvclass"]);
        Assert.Contains("member name is not in it", missing[@"<vilib>\Utility\error.llb\Simple Error Handler.vi"]);
        Assert.Equal(1, answer["notChecked"]!["(relative)"]!.GetValue<int>());
    }

    /// <summary>The control: the .llb naming its member and the class present leave nothing missing.</summary>
    [Fact]
    public void Everything_present_is_ok()
    {
        var root = Install(llbHoldsMember: true);
        File.WriteAllText(Path.Combine(root, "vi.lib", "ActorFramework", "Actor", "Actor.lvclass"), "<LVClass/>");

        var answer = LinkCheckTools.Check([Launcher], root, 2020, "test");

        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.Equal(3, answer["linksChecked"]!.GetValue<int>());
    }

    /// <summary>
    /// A class's private data control is linked THROUGH its .lvclass (`Actor.lvclass\Actor.ctl`) and
    /// lives inside that XML file: the first run against NI's examples called all 15 such links
    /// missing.
    /// </summary>
    [Fact]
    public void A_private_data_control_through_its_class_file_is_present_when_the_class_is()
    {
        var root = Install(llbHoldsMember: true);
        var lvclass = Path.Combine(root, "vi.lib", "ActorFramework", "Actor", "Actor.lvclass");
        File.WriteAllText(lvclass, "<LVClass/>");

        var presence = ViLinks.Check(root, new ViLinks.PathRef(0, ["<vilib>", "ActorFramework", "Actor", "Actor.lvclass", "Actor.ctl"]));

        Assert.Equal(ViLinks.Presence.Present, presence);
    }

    private static LabViewInstall Lv(int release, bool is32, string folder) =>
        new(Path.Combine(folder, "LabVIEW.exe"), release, is32, Path.GetFileName(folder));

    [Fact]
    public void The_target_is_the_pinned_save_version_or_a_named_release_and_bitness()
    {
        var project = Path.Combine(_dir, "App.lvproj");
        File.WriteAllText(project, """
            <?xml version='1.0' encoding='UTF-8'?>
            <Project Type="Project" LVVersion="21008000">
            	<Property Name="NI.LV.All.SaveVersion" Type="Str">21.0</Property>
            </Project>
            """);
        IReadOnlyList<LabViewInstall> installs = [Lv(2021, false, @"C:\lv21-64"), Lv(2021, true, @"C:\lv21"), Lv(2026, true, @"C:\lv26")];

        var pinned = LinkCheckTools.ResolveTarget(null, null, project, installs);
        var named64 = LinkCheckTools.ResolveTarget("2021", "64", project, installs);
        var absent = LinkCheckTools.ResolveTarget("2018", null, project, installs);

        Assert.Equal((@"C:\lv21", 2021), pinned.Target!.Value);
        Assert.Contains("21.0 pinned by App.lvproj", pinned.ChosenBy);
        Assert.Equal(@"C:\lv21-64", named64.Target!.Value.Root);
        Assert.Null(absent.Target);
        Assert.Contains("not installed", absent.Problem);
    }
}
