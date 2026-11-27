// =============================================================================
// CloseRestoreTests.cs - lvai_close_active_project's opt-in restoreProjectFile:
// the bytes taken before LabVIEW's save go back only after a save that changed them.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class CloseRestoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("close-restore").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Described(JsonObject? rewrite) =>
        new JsonObject { ["closed"] = rewrite is not null, ["projectRewrite"] = rewrite }.ToJsonString();

    [Fact]
    public void A_save_that_changed_the_file_is_undone_byte_for_byte()
    {
        var project = Path.Combine(_dir, "App.lvproj");
        File.WriteAllText(project, "rewritten by LabVIEW");
        var before = new ProjectRewrite.Snapshot(project, "as the user had it"u8.ToArray());

        var answer = (JsonObject)JsonNode.Parse(CloseTools.RestoreProjectFile(
            Described(new JsonObject { ["changed"] = true }), before))!;

        Assert.True(answer["projectRewrite"]!["restored"]!.GetValue<bool>());
        Assert.Equal("as the user had it", File.ReadAllText(project));
    }

    [Fact]
    public void An_unchanged_file_and_a_close_that_did_not_happen_are_left_alone()
    {
        var project = Path.Combine(_dir, "App.lvproj");
        File.WriteAllText(project, "as saved");
        var before = new ProjectRewrite.Snapshot(project, "older"u8.ToArray());

        var unchanged = (JsonObject)JsonNode.Parse(CloseTools.RestoreProjectFile(
            Described(new JsonObject { ["changed"] = false }), before))!;
        var notClosed = (JsonObject)JsonNode.Parse(CloseTools.RestoreProjectFile(Described(null), before))!;

        Assert.False(unchanged["projectRewrite"]!["restored"]!.GetValue<bool>());
        Assert.Contains("not done", notClosed["restore"]!.GetValue<string>());
        Assert.Equal("as saved", File.ReadAllText(project));
    }
}
