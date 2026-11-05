using System.Text.Json;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMCP.Tests.Infra;

/// <summary>
/// The one-time switch to the pylabview default (the user's decision of 2026-11-05): an existing
/// settings file whose setDefaultDone is not true gets "aixmlBackend": "pylabview" and the marker,
/// keeps everything else, and is never switched a second time.
/// </summary>
public class ServerSettingsMigrationTests
{
    /// <summary>The template every installation got before 2026-11-05, verbatim in its essentials.</summary>
    private const string OldTemplate = """
        {
          // LabVIEW MCP server settings.
          //   "grpc"       NI's AI service inside LabVIEW (Nigel).
          //   "pylabview"  Our own composer and reader.
          //   "auto"       "grpc", except "pylabview" when the LabVIEW in use is older than 2026.
          "aixmlBackend": "auto"

          // labviewExe, labviewPref - OPTIONAL.
          //   "labviewExe": "C:\\Program Files\\National Instruments\\LabVIEW 2024\\LabVIEW.exe",
        }
        """;

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static JsonElement Parse(string text) => JsonDocument.Parse(text, Lenient).RootElement.Clone();

    private static string WithFile(string content, Func<string, string> act)
    {
        var dir = Directory.CreateTempSubdirectory("lvmcp-migrate-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, content);
            return act(path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("grpc")]
    [InlineData("pylabview")]
    public void An_old_file_is_switched_to_pylabview_once_whatever_it_said(string before)
    {
        WithFile(OldTemplate.Replace("\"aixmlBackend\": \"auto\"", $"\"aixmlBackend\": \"{before}\""), path =>
        {
            Assert.Equal(ServerSettings.Migration.Migrated, ServerSettings.MigrateDefaultOnce(path));

            var after = File.ReadAllText(path);
            var root = Parse(after);
            Assert.Equal("pylabview", root.GetProperty(ServerSettings.BackendKey).GetString());
            Assert.Equal(JsonValueKind.True, root.GetProperty(ServerSettings.SetDefaultDoneKey).ValueKind);
            Assert.Equal("pylabview", ServerSettings.ReadBackend(path).Value);

            // the rest of the user's text stays, the stale "auto" promise is reworded
            Assert.Contains("labviewExe, labviewPref - OPTIONAL.", after);
            Assert.DoesNotContain("\"grpc\", except \"pylabview\"", after);
            Assert.Contains($"\"aixmlBackend\": \"{before}\"", File.ReadAllText(path + ".bak"));

            // never twice: a user who sets grpc afterwards keeps it
            File.WriteAllText(path, after.Replace("\"aixmlBackend\": \"pylabview\"", "\"aixmlBackend\": \"grpc\""));
            Assert.Equal(ServerSettings.Migration.AlreadyDone, ServerSettings.MigrateDefaultOnce(path));
            Assert.Equal("grpc", ServerSettings.ReadBackend(path).Value);
            return "";
        });
    }

    [Fact]
    public void A_false_marker_is_switched_and_set_true()
    {
        WithFile("{ \"aixmlBackend\": \"grpc\", \"setDefaultDone\": false }", path =>
        {
            Assert.Equal(ServerSettings.Migration.Migrated, ServerSettings.MigrateDefaultOnce(path));
            var root = Parse(File.ReadAllText(path));
            Assert.Equal("pylabview", root.GetProperty(ServerSettings.BackendKey).GetString());
            Assert.Equal(JsonValueKind.True, root.GetProperty(ServerSettings.SetDefaultDoneKey).ValueKind);
            return "";
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"labviewExe\": \"C:\\\\LabVIEW.exe\" }")]
    [InlineData("{ \"aixmlBackend\": \"grpc\", \"labviewExe\": \"x\" }")]
    [InlineData("{\r\n  \"aixmlBackend\": \"auto\"\r\n}")]
    public void Every_file_shape_comes_out_as_valid_json_with_both_entries(string content)
    {
        var migrated = ServerSettings.MigrateText(content);
        var root = Parse(migrated);
        Assert.Equal("pylabview", root.GetProperty(ServerSettings.BackendKey).GetString());
        Assert.Equal(JsonValueKind.True, root.GetProperty(ServerSettings.SetDefaultDoneKey).ValueKind);
        if (content.Contains("labviewExe"))
            Assert.True(root.TryGetProperty("labviewExe", out _));
    }

    [Fact]
    public void No_file_and_a_broken_file_are_left_alone()
    {
        var dir = Directory.CreateTempSubdirectory("lvmcp-migrate-").FullName;
        try
        {
            Assert.Equal(ServerSettings.Migration.NoFile,
                         ServerSettings.MigrateDefaultOnce(Path.Combine(dir, "missing.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        WithFile("{ not json", path =>
        {
            Assert.Equal(ServerSettings.Migration.Unreadable, ServerSettings.MigrateDefaultOnce(path));
            Assert.Equal("{ not json", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bak"));
            return "";
        });
    }

    [Fact]
    public void The_new_template_parses_and_needs_no_switch()
    {
        var root = Parse(ServerSettings.Template);
        Assert.Equal("pylabview", root.GetProperty(ServerSettings.BackendKey).GetString());
        Assert.Equal(JsonValueKind.True, root.GetProperty(ServerSettings.SetDefaultDoneKey).ValueKind);
    }
}
