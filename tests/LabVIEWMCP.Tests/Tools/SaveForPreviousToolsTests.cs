// =============================================================================
// SaveForPreviousToolsTests.cs - lvai_save_for_previous without LabVIEW: which files
// it saves, for which target, and how it reads the result back from disk.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class SaveForPreviousToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-sfp").FullName;
    private readonly List<(string Path, string Version)> _saves = [];

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A VI LabVIEW 2026 saved (LVSR 26008000).</summary>
    private string Vi(string name)
    {
        var target = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "basic-interfaces", "Lever", "Pry.vi"), target, overwrite: true);
        return target;
    }

    private void Project(string? pin, params string[] files)
    {
        var items = string.Concat(files.Select(f => $"""<Item Name="{Path.GetFileName(f)}" Type="VI" URL="../{Path.GetRelativePath(_dir, f).Replace('\\', '/')}"/>"""));
        var property = pin is null ? "" : $"""<Property Name="NI.LV.All.SaveVersion" Type="Str">{pin}</Property>""";
        File.WriteAllText(Path.Combine(_dir, "P.lvproj"), $"""
            <?xml version='1.0' encoding='UTF-8'?>
            <Project Type="Project" LVVersion="20008000">{property}<Item Name="My Computer" Type="My Computer">{items}</Item></Project>
            """);
    }

    /// <summary>Stands in for LabVIEW's save: the version word 26.0 becomes the target's.</summary>
    private SaveForPreviousTools Tool(bool reachesTarget = true, string? error = null) => new(null!)
    {
        SaveOverride = (path, version, _, _) =>
        {
            _saves.Add((path, version));
            if (error is not null || !reachesTarget) return Task.FromResult(error);
            var bytes = File.ReadAllBytes(path);
            var major = int.Parse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            var bcd = (byte)((major / 10 << 4) | (major % 10));
            for (var i = 0; i + 3 < bytes.Length; i++)
                if (bytes[i] == 0x26 && bytes[i + 1] == 0x00 && bytes[i + 2] == 0x80 && bytes[i + 3] == 0x00)
                    bytes[i] = bcd;
            File.WriteAllBytes(path, bytes);
            return Task.FromResult<string?>(null);
        },
    };

    private static JsonNode Run(SaveForPreviousTools tool, string paths, string target = "", bool dryRun = false) =>
        JsonNode.Parse(tool.SaveForPreviousAsync(paths, target, dryRun).GetAwaiter().GetResult())!;

    [Fact]
    public void A_vi_newer_than_its_projects_pin_is_saved_back_to_it_and_read_back()
    {
        var vi = Vi("A.vi");
        Project("20.0", vi);

        var answer = Run(Tool(), vi);

        Assert.True(answer["ok"]!.GetValue<bool>());
        var row = answer["files"]![0]!;
        Assert.Equal("saved", row["action"]!.GetValue<string>());
        Assert.Equal("26.0", row["was"]!.GetValue<string>());
        Assert.Equal("20.0", row["savedAs"]!.GetValue<string>());
        Assert.Equal("listed", row["pinFrom"]!.GetValue<string>());
        Assert.Equal((vi, "20.0"), Assert.Single(_saves));
    }

    [Fact]
    public void A_folder_takes_every_vi_and_ctl_under_it_and_ctl_files_go_first()
    {
        var a = Vi(@"sub\A.vi");
        var t = Vi(@"sub\deeper\T.ctl");
        File.WriteAllText(Path.Combine(_dir, "sub", "notes.txt"), "x");

        var answer = Run(Tool(), Path.Combine(_dir, "sub"), "2020");

        Assert.Equal(2, answer["saved"]!.GetValue<int>());
        Assert.Equal([t, a], _saves.Select(s => s.Path));
        Assert.All(_saves, s => Assert.Equal("20.0", s.Version));
    }

    [Fact]
    public void A_file_at_its_target_or_with_no_pin_is_not_touched()
    {
        var vi = Vi("A.vi");

        var noPin = Run(Tool(), vi);
        Assert.Equal("noPin", noPin["files"]![0]!["why"]!.GetValue<string>());
        Assert.True(noPin["ok"]!.GetValue<bool>());

        var same = Run(Tool(), vi, "26.0");
        Assert.Equal("atOrBelowTarget", same["files"]![0]!["why"]!.GetValue<string>());
        Assert.Empty(_saves);
    }

    [Fact]
    public void A_save_that_leaves_the_file_newer_is_a_failure_not_a_success()
    {
        var vi = Vi("A.vi");

        var answer = Run(Tool(reachesTarget: false), vi, "20.0");

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal("targetNotReached", answer["files"]![0]!["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void A_labview_error_is_a_failed_row_with_the_version_the_file_still_has()
    {
        var vi = Vi("A.vi");

        var answer = Run(Tool(error: "LabVIEW error 1: x"), vi, "20.0");

        var row = answer["files"]![0]!;
        Assert.Equal("saveFailed", row["errorKind"]!.GetValue<string>());
        Assert.Equal("26.0", row["savedAs"]!.GetValue<string>());
        Assert.False(answer["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void A_dry_run_saves_nothing_and_says_what_it_would()
    {
        var vi = Vi("A.vi");

        var answer = Run(Tool(), vi, "20.0", dryRun: true);

        Assert.Equal(1, answer["wouldSave"]!.GetValue<int>());
        Assert.Equal("wouldSave", answer["files"]![0]!["action"]!.GetValue<string>());
        Assert.Empty(_saves);
        Assert.Equal("26.0", SaveVersions.Of(vi));
    }

    [Theory]
    [InlineData("20.0", "20.0")]
    [InlineData("2020", "20.0")]
    [InlineData("18", "18.0")]
    [InlineData(" 21.0 ", "21.0")]
    [InlineData("twenty", null)]
    [InlineData("2020.1", null)]
    public void A_target_version_is_read_as_major_dot_minor(string text, string? expected) =>
        Assert.Equal(expected, SaveForPreviousTools.NormaliseVersion(text));

    [Fact]
    public void Missing_and_unsupported_paths_are_named_and_a_bad_target_is_refused()
    {
        var lib = Path.Combine(_dir, "L.lvlib");
        File.WriteAllText(lib, "<Library/>");

        var answer = Run(Tool(), Path.Combine(_dir, "nothing.vi") + "\n" + lib, "20.0");
        var whys = answer["files"]!.AsArray().Select(r => r!["why"]!.GetValue<string>()).ToList();
        Assert.Equal(["notFound", "notAViOrCtl"], whys);

        Assert.Equal("badArguments", Run(Tool(), lib, "twenty")["errorKind"]!.GetValue<string>());
    }
}
