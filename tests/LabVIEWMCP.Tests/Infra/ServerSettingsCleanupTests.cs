using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The start-up clean-up of the removed backend switch (the user's decision of 2026-11-17): the
/// retired keys leave an old settings file, the optional LabVIEW entries stay, and a file left
/// empty is deleted - always with a .bak of what was there.
/// </summary>
public class ServerSettingsCleanupTests
{
    // the template the server wrote from 2026-11-05 on, comments included
    private const string OldFile = """
        {
          // LabVIEW MCP server settings.
          //   "pylabview"  The DEFAULT.
          "aixmlBackend": "pylabview",

          // setDefaultDone - set by the server.
          "setDefaultDone": true

          // labviewExe, labviewPref - OPTIONAL.
          //   "labviewExe": "C:\\Program Files\\National Instruments\\LabVIEW 2024\\LabVIEW.exe",
        }
        """;

    private static void WithFile(string content, Action<string> test)
    {
        var dir = Directory.CreateTempSubdirectory("lvmcp-settings-cleanup-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, content);
            test(path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_file_holding_only_the_retired_keys_is_deleted_with_a_backup()
    {
        WithFile(OldFile, path =>
        {
            Assert.Equal(ServerSettings.Cleanup.Deleted, ServerSettings.RemoveRetiredKeys(path));
            Assert.False(File.Exists(path));
            Assert.Equal(OldFile, File.ReadAllText(path + ".bak"));
            Assert.Equal(ServerSettings.Cleanup.NoFile, ServerSettings.RemoveRetiredKeys(path));
        });
    }

    [Fact]
    public void The_labview_entries_stay_and_the_retired_keys_go()
    {
        const string content = """
            { // mine
              "aixmlBackend": "grpc",
              "labviewExe": "C:\\LV\\LabVIEW.exe",
              "setDefaultDone": true,
              "labviewPref": "C:\\p\\labview.ini",
            }
            """;
        WithFile(content, path =>
        {
            Assert.Equal(ServerSettings.Cleanup.Rewritten, ServerSettings.RemoveRetiredKeys(path));
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("aixmlBackend", text);
            Assert.DoesNotContain("setDefaultDone", text);
            Assert.Equal(@"C:\LV\LabVIEW.exe", ServerSettings.ReadString("labviewExe", path));
            Assert.Equal(@"C:\p\labview.ini", ServerSettings.ReadString("labviewPref", path));
            Assert.Equal(content, File.ReadAllText(path + ".bak"));

            // every later start finds nothing to do and leaves the file as it is
            Assert.Equal(ServerSettings.Cleanup.NothingToDo, ServerSettings.RemoveRetiredKeys(path));
            Assert.Equal(text, File.ReadAllText(path));
        });
    }

    [Fact]
    public void A_file_without_the_retired_keys_or_one_that_does_not_parse_is_left_alone()
    {
        WithFile("{ \"labviewExe\": \"x\" }", path =>
        {
            Assert.Equal(ServerSettings.Cleanup.NothingToDo, ServerSettings.RemoveRetiredKeys(path));
            Assert.False(File.Exists(path + ".bak"));
        });
        WithFile("{ \"aixmlBackend\": ", path =>
        {
            Assert.Equal(ServerSettings.Cleanup.Unreadable, ServerSettings.RemoveRetiredKeys(path));
            Assert.Equal("{ \"aixmlBackend\": ", File.ReadAllText(path));
        });
    }
}
