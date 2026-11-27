// =============================================================================
// SaveVersionsTests.cs - the save-version check every tool answer gets: reading a
// file's saved version, a project's pinned one, and what is reported (or not).
// =============================================================================
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public sealed class SaveVersionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-saveversions").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A VI LabVIEW 2026 saved (LVSR 26008000), copied fresh into the test folder.</summary>
    private string Vi(string name = "X.vi")
    {
        var source = RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "basic-interfaces", "Lever", "Pry.vi");
        var target = Path.Combine(_dir, name);
        File.Copy(source, target, overwrite: true);
        return target;
    }

    private string Project(string? saveVersion, params string[] urls)
    {
        var path = Path.Combine(_dir, "P.lvproj");
        var items = string.Concat(urls.Select(u => $"""<Item Name="{Path.GetFileName(u)}" Type="VI" URL="{u}"/>"""));
        var pin = saveVersion is null ? "" : $"""<Property Name="NI.LV.All.SaveVersion" Type="Str">{saveVersion}</Property>""";
        File.WriteAllText(path, $"""
            <?xml version='1.0' encoding='UTF-8'?>
            <Project Type="Project" LVVersion="21008000">{pin}<Item Name="My Computer" Type="My Computer">{items}</Item></Project>
            """);
        return path;
    }

    private static IEnumerable<KeyValuePair<string, JsonElement>> Args(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.Clone()));

    [Theory]
    [InlineData(0x26008000u, "26.0")]
    [InlineData(0x20008000u, "20.0")]
    [InlineData(0x18008000u, "18.0")]
    [InlineData(0x08608000u, "8.6")]
    public void A_version_word_reads_as_major_dot_minor(uint word, string expected) =>
        Assert.Equal(expected, SaveVersions.FromWord(word));

    [Fact]
    public void A_vi_reads_its_saved_version_and_an_xml_file_its_LVVersion()
    {
        Assert.Equal("26.0", SaveVersions.Of(Vi()));
        Assert.Equal("21.0", SaveVersions.Of(Project(null)));
        Assert.Null(SaveVersions.Of(Path.Combine(_dir, "nothing.vi")));
    }

    [Theory]
    [InlineData("21.0", "21.0")]
    [InlineData("18.0", "18.0")]
    [InlineData("Editor version", null)]
    [InlineData(null, null)]
    public void A_project_pins_a_save_version_or_saves_in_the_editors(string? property, string? expected) =>
        Assert.Equal(expected, SaveVersions.Pinned(Project(property)));

    [Theory]
    [InlineData("26.0", "21.0", 1)]
    [InlineData("21.0", "26.0", -1)]
    [InlineData("8.6", "26.0", -1)]
    [InlineData("20.0", "20.0", 0)]
    public void Versions_compare_as_numbers(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(SaveVersions.Compare(a, b)));

    /// <summary>
    /// The 21.0 field case (2026-10-30): a VI of a project pinned to 21.0 came out of a tool at 26.0, and
    /// LabVIEW 2021 refused it on the target station. The answer now says so.
    /// </summary>
    [Fact]
    public void A_file_written_newer_than_its_project_pins_is_reported()
    {
        var vi = Vi();
        var project = Project("21.0", "../X.vi");
        var before = new SaveVersions.Before(DateTime.UtcNow.AddSeconds(-1), new Dictionary<string, string?>());
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow);

        var check = SaveVersions.Check(before, new JsonObject { ["viPath"] = vi }, null)!;

        var file = check["files"]![0]!;
        Assert.Equal("26.0", file["savedAs"]!.GetValue<string>());
        Assert.Equal("21.0", file["projectSaveVersion"]!.GetValue<string>());
        Assert.Equal(project, file["project"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal("listed", file["pinFrom"]!.GetValue<string>());
        Assert.Contains("older than 26.0 cannot open", check["warning"]!.GetValue<string>());
    }

    /// <summary>
    /// THE PREVIOUS CALL'S SAVE IS NOT THIS CALL'S. Measured 2026-11-13: lvai_add_vis_to_project,
    /// run 0.8 s after lvai_set_vi_icon saved a VI at 26.0 in a 21.0 project, answered that IT had
    /// saved the VI - the 2 s margin for coarse file systems took the icon's write for its own.
    /// A file named before the call is now judged by whether its write time changed.
    /// </summary>
    [Fact]
    public void A_file_written_just_before_the_call_and_not_since_is_not_reported()
    {
        var vi = Vi();
        Project("21.0", "../X.vi");
        var savedByThePreviousCall = DateTime.UtcNow.AddSeconds(-1);
        File.SetLastWriteTimeUtc(vi, savedByThePreviousCall);
        var args = Args(new { viPaths = vi });

        var before = SaveVersions.Capture(args);

        Assert.Equal(savedByThePreviousCall, before.WriteTimes![vi]);
        Assert.Null(SaveVersions.Check(before, new JsonObject(), args));

        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow);                 // the control: this call saves it
        var file = SaveVersions.Check(before, new JsonObject(), args)!["files"]![0]!;
        Assert.Equal("21.0", file["projectSaveVersion"]!.GetValue<string>());
    }

    /// <summary>
    /// A COLLECTED answer (a ResumableCall job answered `stillRunning` first): the collecting call
    /// read the file's write time AFTER the job wrote it, so that reading must not clear it.
    /// </summary>
    [Fact]
    public void A_collected_answer_reports_a_file_its_job_saved_before_the_collecting_call()
    {
        var vi = Vi();
        Project("21.0", "../X.vi");
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow.AddSeconds(-30));   // the job saved it
        var args = Args(new { viPath = vi });
        var before = SaveVersions.Capture(args);                         // the collecting call

        Assert.Null(SaveVersions.Check(before, new JsonObject(), args));

        var collected = new JsonObject { [ResumableCall.JobStartedUtcKey] = DateTime.UtcNow.AddMinutes(-2).ToString("o") };
        var file = SaveVersions.Check(before, collected, args)!["files"]![0]!;
        Assert.Equal("21.0", file["projectSaveVersion"]!.GetValue<string>());
    }

    /// <summary>
    /// The same for a FOLDER the call names and files only its answer names - live check of
    /// lvai_save_for_previous, 2026-11-23: a dryRun over a folder one second after another call
    /// saved its files was told it had saved them.
    /// </summary>
    [Fact]
    public void A_file_under_a_named_folder_written_just_before_the_call_is_not_reported()
    {
        var vi = Vi();
        Project("21.0", "../X.vi");
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow.AddSeconds(-1));
        var args = Args(new { paths = _dir });

        var before = SaveVersions.Capture(args);
        var answer = new JsonObject { ["files"] = new JsonArray(new JsonObject { ["path"] = vi }) };

        Assert.Null(SaveVersions.Check(before, answer, args));

        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow);                 // the control: this call saves it
        Assert.NotNull(SaveVersions.Check(before, answer, args));
    }

    /// <summary>
    /// THE E2E CASE, 2026-11-13: a VI generated into a 21.0 project's folder (composed 21.0 by the
    /// folder rule) got its icon BEFORE lvai_add_vis_to_project listed it. The check reported
    /// `was 21.0, savedAs 26.0` and named no pin; it now names the one the composer used.
    /// </summary>
    [Fact]
    public void A_file_no_project_lists_yet_is_judged_by_the_pin_of_the_project_above_it()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "generated")).FullName;
        var project = Project("21.0");                      // lists nothing
        var vi = Path.Combine(sub, "New.vi");
        File.Copy(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "basic-interfaces", "Lever", "Pry.vi"), vi);
        var before = new SaveVersions.Before(DateTime.UtcNow.AddSeconds(-1),
                                             new Dictionary<string, string?> { [vi] = "21.0" });
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow);

        var file = SaveVersions.Check(before, new JsonObject { ["viPath"] = vi }, null)!["files"]![0]!;

        Assert.Equal("21.0", file["was"]!.GetValue<string>());
        Assert.Equal("21.0", file["projectSaveVersion"]!.GetValue<string>());
        Assert.Equal(project, file["project"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal("folder", file["pinFrom"]!.GetValue<string>());
    }

    /// <summary>The 18.0 field case: no pin found, but the file itself went up a version during the call.</summary>
    [Fact]
    public void A_file_saved_newer_than_it_was_before_the_call_is_reported()
    {
        var vi = Vi();
        var before = new SaveVersions.Before(DateTime.UtcNow.AddSeconds(-1),
                                             new Dictionary<string, string?> { [vi] = "18.0" });
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow);

        var file = SaveVersions.Check(before, new JsonObject(), null)!["files"]![0]!;

        Assert.Equal("18.0", file["was"]!.GetValue<string>());
        Assert.Equal("26.0", file["savedAs"]!.GetValue<string>());
        Assert.Null(file["projectSaveVersion"]);
    }

    [Fact]
    public void A_file_not_written_during_the_call_or_saved_at_the_same_version_is_not_reported()
    {
        var vi = Vi();
        Project("26.0", "../X.vi");
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow.AddMinutes(-10));
        var before = new SaveVersions.Before(DateTime.UtcNow, new Dictionary<string, string?> { [vi] = "21.0" });
        Assert.Null(SaveVersions.Check(before, new JsonObject { ["viPath"] = vi }, null));   // older than the call

        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow);
        var same = new SaveVersions.Before(DateTime.UtcNow.AddSeconds(-1), new Dictionary<string, string?> { [vi] = "26.0" });
        Assert.Null(SaveVersions.Check(same, new JsonObject { ["viPath"] = vi }, null));     // 26.0 -> 26.0, pin 26.0
    }

    [Fact]
    public void A_member_is_judged_by_the_project_that_lists_its_library()
    {
        var vi = Vi("Member.vi");
        File.WriteAllText(Path.Combine(_dir, "Lib.lvlib"), """
            <?xml version='1.0' encoding='UTF-8'?>
            <Library LVVersion="21008000"><Item Name="Member.vi" Type="VI" URL="../Member.vi"/></Library>
            """);
        var project = Path.Combine(_dir, "P.lvproj");
        File.WriteAllText(project, """
            <?xml version='1.0' encoding='UTF-8'?>
            <Project Type="Project" LVVersion="21008000"><Property Name="NI.LV.All.SaveVersion" Type="Str">21.0</Property><Item Name="My Computer" Type="My Computer"><Item Name="Lib.lvlib" Type="Library" URL="../Lib.lvlib"/></Item></Project>
            """);

        Assert.Equal(("21.0"), SaveVersions.PinFor(vi)?.Version);
    }

    [Fact]
    public void Paths_are_found_in_lines_and_in_json_inside_strings()
    {
        var a = Path.Combine(_dir, "A.vi");
        var b = Path.Combine(_dir, "B.ctl");
        var c = Path.Combine(_dir, "C.lvclass");
        var args = Args(new
        {
            viPaths = a + "\n" + b,
            iconsJson = JsonSerializer.Serialize(new[] { new { viPath = c } }),
            note = "not a path.vi",
        });

        var found = SaveVersions.PathsIn(args.Select(p => p.Value)).ToList();

        Assert.Equal(3, found.Count);
        Assert.Contains(a, found, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(c, found, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The wrapper every served tool passes through adds the block to the answer text.</summary>
    [Fact]
    public void The_tool_wrapper_adds_the_check_to_an_answer()
    {
        var vi = Vi();
        Project("21.0", "../X.vi");
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow);
        var before = new SaveVersions.Before(DateTime.UtcNow.AddSeconds(-1), new Dictionary<string, string?>());
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = true, ["viPath"] = vi }.ToJsonString() }],
        };

        var text = ((TextContentBlock)DiagnosingTool.WithSaveVersionCheck(result, before, null).Content[0]).Text;

        var check = JsonNode.Parse(text)!["saveVersionCheck"]!;
        Assert.Equal("21.0", check["files"]![0]!["projectSaveVersion"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(text)!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void A_project_description_carries_its_save_version()
    {
        var pinned = Project("21.0");
        var text = LabVIEWMcp.Tools.InspectTools.WithSaveVersion("""{"messages":[]}""", pinned);
        Assert.Equal("21.0", JsonNode.Parse(text)!["saveVersion"]!.GetValue<string>());
        Assert.Equal("21.0", JsonNode.Parse(text)!["projectFileSavedBy"]!.GetValue<string>());

        var editor = LabVIEWMcp.Tools.InspectTools.WithSaveVersion("""{"messages":[]}""", Project("Editor version"));
        Assert.Equal("Editor version", JsonNode.Parse(editor)!["saveVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("21.0", 2021)]
    [InlineData("18.0", 2018)]
    [InlineData("9.0", 2009)]
    [InlineData("8.6", null)]
    public void A_save_version_names_its_release_year(string version, int? year) =>
        Assert.Equal(year, SaveVersions.Release(version));

    /// <summary>A VI about to be generated is listed nowhere yet: the projects above it decide, if they agree.</summary>
    [Fact]
    public void A_new_file_takes_the_pin_every_project_above_agrees_on()
    {
        var target = Path.Combine(_dir, "sub", "New.vi");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Project("21.0");
        Assert.Equal("21.0", SaveVersions.PinAbove(target)?.Version);

        File.WriteAllText(Path.Combine(_dir, "sub", "Other.lvproj"), """
            <?xml version='1.0' encoding='UTF-8'?>
            <Project Type="Project" LVVersion="26008000"><Property Name="NI.LV.All.SaveVersion" Type="Str">Editor version</Property></Project>
            """);
        Assert.Null(SaveVersions.PinAbove(target));
    }

    [Fact]
    public void The_composer_writes_a_pinned_older_version_from_2020_on_and_says_why()
    {
        var target = Path.Combine(_dir, "New.vi");
        Assert.Equal((null, null), AixmlComposer.PinnedTarget(target, 2026));     // no project at all

        Project("21.0");
        var (release, note) = AixmlComposer.PinnedTarget(target, 2026);
        Assert.Equal(2021, release);
        Assert.Contains("pins save version 21.0", note);

        Assert.Equal((null, null), AixmlComposer.PinnedTarget(target, 2021));     // already that old
    }

    [Fact]
    public void A_pin_older_than_the_composer_can_write_is_reported_not_honoured()
    {
        Project("18.0");
        var (release, note) = AixmlComposer.PinnedTarget(Path.Combine(_dir, "New.vi"), 2025);
        Assert.Null(release);
        Assert.Contains("older than the oldest the composer writes", note);
    }

    [Fact]
    public void An_answer_with_nothing_to_report_is_returned_as_it_was()
    {
        var text = """{"ok":true,"note":"nothing written"}""";
        var before = SaveVersions.Capture(null);
        Assert.Same(text, SaveVersions.Annotate(text, before, null));
        Assert.Same("not json", SaveVersions.Annotate("not json", before, null));
    }
    /// <summary>
    /// A TYPEDEF'S CALLER, saved by the call without being named in it: measured 2026-11-23, a 20.0
    /// caller re-saved as 26.0 by lvai_set_representation was missing from saveVersionCheck, which
    /// named only the .ctl. The tool hands the caller's version over; the check reports it and the
    /// hand-over key leaves the answer.
    /// </summary>
    [Fact]
    public void A_caller_the_tool_saved_unnamed_is_reported_from_the_version_it_handed_over()
    {
        var caller = Vi("Caller.vi");
        var before = new SaveVersions.Before(DateTime.UtcNow.AddSeconds(-1), new Dictionary<string, string?>());
        File.SetLastWriteTimeUtc(caller, DateTime.UtcNow);
        var answer = new JsonObject
        {
            ["ok"] = true,
            ["callersSaved"] = new JsonArray(caller),
            [SaveVersions.VersionsBeforeKey] = new JsonObject { [caller] = "20.0" },
        };

        var annotated = JsonNode.Parse(SaveVersions.Annotate(answer.ToJsonString(), before, null))!.AsObject();

        var file = annotated["saveVersionCheck"]!["files"]![0]!;
        Assert.Equal(caller, file["path"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal("20.0", file["was"]!.GetValue<string>());
        Assert.Equal("26.0", file["savedAs"]!.GetValue<string>());
        Assert.False(annotated.ContainsKey(SaveVersions.VersionsBeforeKey));

        // nothing to report: the hand-over key still leaves the answer
        var same = new JsonObject { ["ok"] = true, [SaveVersions.VersionsBeforeKey] = new JsonObject { [caller] = "26.0" } };
        var quiet = JsonNode.Parse(SaveVersions.Annotate(same.ToJsonString(),
            new SaveVersions.Before(DateTime.UtcNow.AddMinutes(1), new Dictionary<string, string?>()), null))!.AsObject();
        Assert.False(quiet.ContainsKey(SaveVersions.VersionsBeforeKey));
        Assert.False(quiet.ContainsKey("saveVersionCheck"));
    }
}
