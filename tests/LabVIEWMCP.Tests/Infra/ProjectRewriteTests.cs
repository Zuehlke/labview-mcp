// =============================================================================
// ProjectRewriteTests.cs - what a project close reports about LabVIEW's own save
// of the .lvproj: the measured rewrite of a 21.0 64-bit project by LabVIEW 2026
// 32-bit, an unchanged save, a lost entry, and the copy kept for putting it back.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// THE FIXTURES are cut down from a real pair, 2026-11-13: a field project pinned to 21.0 for
/// LabVIEW 2021 64-bit, before and after lvai_restart_labview's close saved it in LabVIEW 2026
/// 32-bit. Kept as LabVIEW wrote them: the emptied Dependencies element, and the installer's
/// DistPart / InstSpecBitness / InstSpecVersion values. Names are neutral; the NI values are real.
/// </summary>
public sealed class ProjectRewriteTests : IDisposable
{
    private readonly TempTree _tree = new("projectrewrite");

    public void Dispose() => _tree.Dispose();

    private static string Fixture(string name) =>
        RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "project-files", name);

    /// <summary>The project at "before", a snapshot of it, then the file replaced by "after".</summary>
    private (ProjectRewrite.Snapshot Before, string Path) Saved(string before, string after)
    {
        var path = _tree.Copy(Fixture(before), "Project.lvproj");
        var snapshot = ProjectRewrite.Take(path)!;
        File.Copy(Fixture(after), path, overwrite: true);
        return (snapshot, path);
    }

    [Fact]
    public void The_measured_rewrite_names_the_build_spec_damage_and_the_emptied_dependencies()
    {
        var (before, _) = Saved("rewrite-21-64bit.before.lvproj", "rewrite-21-64bit.after.lvproj");
        var backups = _tree.PathOf("backups");

        var report = ProjectRewrite.Report(before, backups);

        Assert.True(report["changed"]!.GetValue<bool>());
        Assert.Equal(4, report["dependencies"]!["before"]!.GetValue<int>());
        Assert.Equal(0, report["dependencies"]!["after"]!.GetValue<int>());
        Assert.Equal(0, report["items"]!["removedCount"]!.GetValue<int>());
        Assert.Equal("21.0", report["lvVersion"]!["after"]!.GetValue<string>());
        Assert.Equal("21.0", report["saveVersion"]!["after"]!.GetValue<string>());

        var specs = report["buildSpecifications"]!;
        Assert.Equal(6, specs["changedPropertyCount"]!.GetValue<int>());   // 4 DistPart[4] + 2 InstSpec
        var changes = specs["changedProperties"]!.AsArray().Select(c => c!.AsObject()).ToList();
        var name = changes.Single(c => (string?)c["property"] == "DistPart[4].productName");
        Assert.Equal("My Computer/Build Specifications/App Installer", name["spec"]!.GetValue<string>());
        Assert.Equal("NI LabVIEW Runtime 2021 SP1 f6 (64-bit)", name["before"]!.GetValue<string>());
        Assert.Equal("NI LabVIEW Run-Time Engine 2021", name["after"]!.GetValue<string>());
        var bitness = changes.Single(c => (string?)c["property"] == "InstSpecBitness");
        Assert.Equal("64-bit", bitness["before"]!.GetValue<string>());
        Assert.Equal("32-bit", bitness["after"]!.GetValue<string>());

        var warning = report["warning"]!.GetValue<string>();
        Assert.Contains("REWROTE", warning);
        Assert.Contains("6 build-specification properties", warning);
        Assert.Contains("Dependencies went from 4 to 0", warning);
        Assert.DoesNotContain("project entr", warning);
    }

    [Fact]
    public void The_copy_holds_the_file_as_it_was_before_the_save()
    {
        var (before, _) = Saved("rewrite-21-64bit.before.lvproj", "rewrite-21-64bit.after.lvproj");

        var report = ProjectRewrite.Report(before, _tree.PathOf("backups"));

        var backup = report["backupPath"]!.GetValue<string>();
        Assert.Equal(File.ReadAllBytes(Fixture("rewrite-21-64bit.before.lvproj")), File.ReadAllBytes(backup));
        Assert.Contains(backup, report["warning"]!.GetValue<string>());
    }

    [Fact]
    public void An_unchanged_file_writes_no_copy_and_no_warning()
    {
        var (before, _) = Saved("rewrite-21-64bit.before.lvproj", "rewrite-21-64bit.before.lvproj");
        var backups = _tree.PathOf("backups");

        var report = ProjectRewrite.Report(before, backups);

        Assert.False(report["changed"]!.GetValue<bool>());
        Assert.Null(report["warning"]);
        Assert.False(Directory.Exists(backups));
    }

    /// <summary>An entry of the project's own tree going missing is named by its tree path.</summary>
    [Fact]
    public void A_lost_project_entry_is_named_by_its_tree_path()
    {
        var path = _tree.Copy(Fixture("rewrite-21-64bit.before.lvproj"), "Project.lvproj");
        var before = ProjectRewrite.Take(path)!;
        var text = File.ReadAllText(path).Replace(
            "\t\t\t<Item Name=\"Scale.vi\" Type=\"VI\" URL=\"../Helpers/Scale.vi\"/>\r\n", "");
        File.WriteAllText(path, text);

        var report = ProjectRewrite.Report(before, _tree.PathOf("backups"));

        Assert.Equal(1, report["items"]!["removedCount"]!.GetValue<int>());
        Assert.Equal("My Computer/Helpers/Scale.vi", report["items"]!["removed"]![0]!.GetValue<string>());
        Assert.Contains("1 project entry outside Dependencies", report["warning"]!.GetValue<string>());
        Assert.Equal(0, report["buildSpecifications"]!["changedPropertyCount"]!.GetValue<int>());
    }

    [Fact]
    public void A_file_that_is_no_longer_XML_is_compared_by_size_only()
    {
        var path = _tree.Copy(Fixture("rewrite-21-64bit.before.lvproj"), "Project.lvproj");
        var before = ProjectRewrite.Take(path)!;
        File.WriteAllText(path, "<Project");

        var report = ProjectRewrite.Report(before, _tree.PathOf("backups"));

        Assert.True(report["changed"]!.GetValue<bool>());
        Assert.Null(report["dependencies"]);
        Assert.Contains("REWROTE", report["warning"]!.GetValue<string>());
    }

    [Fact]
    public void No_snapshot_of_a_project_that_is_not_there()
    {
        Assert.Null(ProjectRewrite.Take(_tree.PathOf("missing.lvproj")));
        Assert.Null(ProjectRewrite.Take(null));
    }

    [Fact]
    public void Only_the_newest_copies_are_kept()
    {
        var backups = Directory.CreateDirectory(_tree.PathOf("backups")).FullName;
        for (var i = 0; i < ProjectRewrite.BackupsKept + 5; i++)
        {
            var old = Path.Combine(backups, $"Old.{i:D3}.lvproj");
            File.WriteAllText(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1).AddMinutes(i));
        }
        var (before, _) = Saved("rewrite-21-64bit.before.lvproj", "rewrite-21-64bit.after.lvproj");

        var report = ProjectRewrite.Report(before, backups);

        Assert.Equal(ProjectRewrite.BackupsKept, Directory.GetFiles(backups, "*.lvproj").Length);
        Assert.True(File.Exists(report["backupPath"]!.GetValue<string>()));
        Assert.False(File.Exists(Path.Combine(backups, "Old.000.lvproj")));
    }

    // ---- the close and the restart carry it ----

    private static string Runner(string status, string code = "0") =>
        new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject
            {
                ["status"] = new JsonObject { ["value"] = status },
                ["code"] = new JsonObject { ["value"] = code },
                ["source"] = new JsonObject { ["value"] = "" },
            },
        }.ToJsonString();

    [Fact]
    public void A_close_that_happened_reports_the_rewrite_before_its_sweep()
    {
        var (before, path) = Saved("rewrite-21-64bit.before.lvproj", "rewrite-21-64bit.after.lvproj");

        var answer = (JsonObject)JsonNode.Parse(CloseTools.DescribeProjectClose(
            Runner("0"), "helper.vi", "helper.xml", false, projectPath: path, before: before))!;

        var keys = answer.Select(p => p.Key).ToList();
        Assert.True(keys.IndexOf("projectRewrite") < keys.IndexOf("projectSweep"));
        Assert.True(answer["projectRewrite"]!["changed"]!.GetValue<bool>());
        Assert.Contains("REWROTE", answer["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_close_that_did_not_happen_reports_no_rewrite()
    {
        var (before, path) = Saved("rewrite-21-64bit.before.lvproj", "rewrite-21-64bit.after.lvproj");

        var answer = (JsonObject)JsonNode.Parse(CloseTools.DescribeProjectClose(
            Runner("1", "1055"), "helper.vi", "helper.xml", false, projectPath: path, before: before))!;

        Assert.Null(answer["projectRewrite"]);
    }

    [Fact]
    public void An_entry_that_became_a_library_member_is_moved_not_missing()
    {
        string[] removed = ["My Computer/SubVIs/Find Account.vi", "My Computer/Old Notes.txt", "My Computer/Get Message.vi"];
        string[] owned = [@"C:\app\SubVIs\Find Account.vi", @"C:\app\SubVIs\get message.vi"];

        Assert.Equal(["My Computer/SubVIs/Find Account.vi", "My Computer/Get Message.vi"],
                     ProjectRewrite.MovedIntoOwner(removed, owned));
        Assert.Empty(ProjectRewrite.MovedIntoOwner(removed, []));
    }
}
