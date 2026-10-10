// ============================================================================================
// OwnerIconToolsTests.cs - lvai_set_owner_icon, lvai_read_icons and lvai_set_vi_icon keepOwnLayer:
// every refusal that must happen before LabVIEW, and the pure rules that read the helpers' answers.
// ============================================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// EVERY CHECK HERE RUNS BEFORE LABVIEW IS TOUCHED - the connection is <c>null!</c>, so a check that
/// slipped behind a LabVIEW call would fail with a NullReferenceException rather than pass. That is
/// the point of the checks: the icon route saves the owner and each member, and a member path that
/// is not a member, a missing file, or a PNG path LabVIEW would silently not write must be refused
/// while nothing has been changed yet.
///
/// The rest pins the rules that turn the helpers' answers into a verdict - the trailing-';' rule
/// that differs between the two list helpers, Dimsize on the code arrays, and the stage order.
/// </summary>
public sealed class OwnerIconToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-owner-icon-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Png(string relative, int size = 32)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var png = IconImage.Render("AB", null, null, new IconImage.Rgb(0, 0x66, 0x99),
            new IconImage.Rgb(0xFF, 0xFF, 0xFF), new IconImage.Rgb(0, 0, 0),
            new IconImage.Rgb(0xFF, 0xFF, 0xFF), new IconImage.Rgb(0, 0, 0));
        if (size != 32)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), size);
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>A class Foo with two .vi members and a .ctl, all files present.</summary>
    private string Class()
    {
        Write(@"Foo\Read.vi", "vi");
        Write(@"Foo\Write.vi", "vi");
        Write(@"Foo\Type.ctl", "ctl");
        return Write(@"Foo\Foo.lvclass", IconOwnersTests.ClassXml("../Read.vi", "../Write.vi", "../Type.ctl"));
    }

    private static OwnerIconTools Tools() => new(null!);

    private static JsonObject Answer(Task<string> call) => (JsonObject)JsonNode.Parse(call.GetAwaiter().GetResult())!;

    private static string? Kind(JsonObject answer) => answer["errorKind"]?.GetValue<string>();

    // ---------------------------------------------------------------- lvai_set_owner_icon

    [Fact]
    public void A_missing_owner_is_refused()
    {
        Assert.Equal("ownerMissing", Kind(Answer(Tools().SetOwnerIconAsync(
            Path.Combine(_dir, "Nope.lvclass"), Png("i.png")))));
    }

    [Fact]
    public void A_file_that_is_not_a_class_or_library_is_refused()
    {
        var project = Write("P.lvproj", "<Project/>");
        Assert.Equal("ownerUnreadable", Kind(Answer(Tools().SetOwnerIconAsync(project, Png("i.png")))));
    }

    [Fact]
    public void A_png_that_is_not_32x32_or_not_a_png_is_refused()
    {
        var cls = Class();
        Assert.Equal("pngNot32x32", Kind(Answer(Tools().SetOwnerIconAsync(cls, Png("big.png", 48)))));
        Assert.Equal("notAPng", Kind(Answer(Tools().SetOwnerIconAsync(cls, Write("t.png", "text, not an image")))));
        Assert.Equal("pngMissing", Kind(Answer(Tools().SetOwnerIconAsync(cls, Path.Combine(_dir, "none.png")))));
    }

    [Fact]
    public void A_member_path_the_owner_does_not_list_is_refused_and_the_members_are_named()
    {
        var cls = Class();
        var stranger = Write(@"Foo\Stranger.vi", "vi");

        var answer = Answer(Tools().SetOwnerIconAsync(cls, Png("i.png"), applyToMembers: true,
                                                      memberPaths: stranger));

        Assert.Equal("notAMember", Kind(answer));
        Assert.Contains(Path.Combine(_dir, @"Foo\Read.vi"),
            answer["detail"]!["members"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    [Fact]
    public void A_ctl_member_is_refused_because_only_vi_members_were_measured()
    {
        var answer = Answer(Tools().SetOwnerIconAsync(Class(), Png("i.png"), applyToMembers: true,
                                                      memberPaths: Path.Combine(_dir, @"Foo\Type.ctl")));
        Assert.Equal("memberNotAVi", Kind(answer));
    }

    [Fact]
    public void A_listed_member_whose_file_is_gone_is_refused_before_anything_is_saved()
    {
        var cls = Class();
        File.Delete(Path.Combine(_dir, @"Foo\Write.vi"));

        var answer = Answer(Tools().SetOwnerIconAsync(cls, Png("i.png"), applyToMembers: true));

        Assert.Equal("memberFileMissing", Kind(answer));
    }

    [Fact]
    public void Member_paths_without_applyToMembers_are_refused_rather_than_ignored()
    {
        var answer = Answer(Tools().SetOwnerIconAsync(Class(), Png("i.png"), applyToMembers: false,
                                                      memberPaths: Path.Combine(_dir, @"Foo\Read.vi")));
        Assert.Equal("badArguments", Kind(answer));
    }

    [Fact]
    public void A_read_back_folder_too_deep_for_MAX_PATH_is_refused()
    {
        var deep = Path.Combine(_dir, new string('d', 120), new string('e', 120));

        var answer = Answer(Tools().SetOwnerIconAsync(Class(), Png("i.png"), readBackDir: deep));

        Assert.Equal("pngPathTooLong", Kind(answer));
        Assert.Equal(OwnerIconTools.MaxPngPath, answer["detail"]!["limit"]!.GetValue<int>());
        Assert.False(Directory.Exists(deep));     // refused before any folder was made
    }

    [Fact]
    public void A_path_holding_a_semicolon_is_refused()
    {
        Write(@"A;B\X.vi", "vi");
        var cls = Write(@"A;B\Foo.lvclass", IconOwnersTests.ClassXml("../X.vi"));

        Assert.Equal("pathHasSemicolon", Kind(Answer(Tools().SetOwnerIconAsync(cls, Png("i.png")))));
    }

    // ---------------------------------------------------------------- lvai_read_icons

    [Fact]
    public void Read_icons_refuses_what_it_cannot_read()
    {
        Assert.Equal("pathMissing", Kind(Answer(Tools().ReadIconsAsync(Path.Combine(_dir, "No.vi"), _dir))));
        Assert.Equal("unsupportedPath", Kind(Answer(Tools().ReadIconsAsync(Write("x.ctl", "ctl"), _dir))));
        Assert.Equal("badArguments", Kind(Answer(Tools().ReadIconsAsync("\n\n", _dir))));
    }

    [Fact]
    public void Read_icons_refuses_a_long_output_path_before_creating_any_folder()
    {
        var vi = Write(@"src\Sub\A.vi", "vi");
        var outDir = Path.Combine(_dir, new string('o', 150), new string('p', 100));

        var answer = Answer(Tools().ReadIconsAsync(vi, outDir));

        Assert.Equal("pngPathTooLong", Kind(answer));
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void Read_icons_refuses_a_path_outside_baseDir_and_a_missing_expectedDir()
    {
        var vi = Write(@"src\A.vi", "vi");

        Assert.Equal("notUnderBase", Kind(Answer(Tools().ReadIconsAsync(vi, Path.Combine(_dir, "rb"),
            baseDir: Path.Combine(_dir, "elsewhere")))));
        Assert.Equal("expectedDirMissing", Kind(Answer(Tools().ReadIconsAsync(vi, Path.Combine(_dir, "rb"),
            expectedDir: Path.Combine(_dir, "nothing-here")))));
    }

    // ---------------------------------------------------------------- keepOwnLayer

    [Fact]
    public void KeepOwnLayer_with_an_image_is_refused_because_the_image_would_flatten()
    {
        var vi = Write(@"Foo\Read.vi", "vi");
        var answer = Answer(new IconTools(null!).SetViIconAsync(vi, iconImagePath: Png("i.png"),
                                                                keepOwnLayer: true));
        Assert.Equal("conflictingArguments", Kind(answer));

        answer = Answer(new IconTools(null!).SetViIconAsync(vi, line1: "AB", keepOwnLayer: true));
        Assert.Equal("conflictingArguments", Kind(answer));
    }

    [Fact]
    public void An_owner_path_without_keepOwnLayer_is_refused()
    {
        var vi = Write(@"Foo\Read.vi", "vi");
        var answer = Answer(new IconTools(null!).SetViIconAsync(vi, line1: "AB", ownerPath: Class()));
        Assert.Equal("badArguments", Kind(answer));
    }

    [Fact]
    public void KeepOwnLayer_needs_an_owner_that_lists_the_vi()
    {
        var loose = Write(@"Loose\Alone.vi", "vi");
        Assert.Equal("noOwner", Kind(Answer(new IconTools(null!).SetViIconAsync(loose, keepOwnLayer: true))));

        var cls = Class();
        var stranger = Write(@"Foo\Stranger.vi", "vi");
        Assert.Equal("notAMember", Kind(Answer(new IconTools(null!).SetViIconAsync(
            stranger, keepOwnLayer: true, ownerPath: cls))));
    }

    [Fact]
    public void KeepOwnLayer_refuses_to_guess_between_two_owners()
    {
        Class();
        Write(@"Foo\Second.lvclass", IconOwnersTests.ClassXml("../Read.vi"));

        var answer = Answer(new IconTools(null!).SetViIconAsync(Path.Combine(_dir, @"Foo\Read.vi"),
                                                                keepOwnLayer: true));

        Assert.Equal("ownerAmbiguous", Kind(answer));
    }

    // ---------------------------------------------------------------- the pure rules

    [Fact]
    public void The_apply_list_has_no_trailing_semicolon_and_the_read_list_has_one_per_entry()
    {
        // Measured: the apply helper splits with Spreadsheet String To Array, so a trailing ';' is
        // an empty last path and Error 7; the read helper's Match Pattern loop expects one per entry.
        Assert.Equal(@"C:\a.vi;C:\b.vi", OwnerIconTools.JoinForApply([@"C:\a.vi", @"C:\b.vi"]));
        Assert.Equal(@"C:\a.vi;C:\b.vi;", OwnerIconTools.JoinForReadBack([@"C:\a.vi", @"C:\b.vi"]));
    }

    [Fact]
    public void The_read_back_report_pairs_codes_with_png_paths()
    {
        var codes = OwnerIconTools.ParseReport("0\tC:\\rb\\a.png\n7\tC:\\rb\\b.png\nnoise\n");

        Assert.Equal(0, codes[@"C:\rb\a.png"]);
        Assert.Equal(7, codes[@"c:\RB\B.PNG"]);
        Assert.Equal(2, codes.Count);
    }

    private static JsonObject Values(params (string Name, string Xml)[] indicators)
    {
        var values = new JsonObject();
        foreach (var (name, xml) in indicators)
            values[name] = new JsonObject { ["type"] = "x", ["value"] = null, ["xml"] = xml };
        return values;
    }

    private static string Codes(params int[] codes) =>
        $"<Array><Name>c</Name><Dimsize>{codes.Length}</Dimsize>" +
        string.Concat(codes.Select(c => $"<I32><Name></Name><Val>{c}</Val></I32>")) +
        (codes.Length == 0 ? "<I32><Name></Name><Val>0</Val></I32>" : "") + "</Array>";

    private static string Error(int code) =>
        $"<Cluster><Name>e</Name><NumElts>3</NumElts><Boolean><Name>status</Name><Val>{(code != 0 ? 1 : 0)}</Val></Boolean>" +
        $"<I32><Name>code</Name><Val>{code}</Val></I32><String><Name>source</Name><Val>here</Val></String></Cluster>";

    [Fact]
    public void An_empty_code_array_is_empty_although_LabVIEW_writes_a_template_element()
    {
        Assert.Empty(OwnerIconTools.Int32Array(Values(("open codes", Codes())), "open codes")!);
        Assert.Equal([0, 7], OwnerIconTools.Int32Array(Values(("open codes", Codes(0, 7))), "open codes"));
        Assert.Null(OwnerIconTools.Int32Array(new JsonObject(), "open codes"));
    }

    private static JsonObject Apply(int sent, int[] open, int[] save, int apply = 0, int openLib = 0, int? count = null)
    {
        var v = Values(("open codes", Codes(open)), ("save codes", Codes(save)), ("apply error", Error(apply)),
                       ("open library error", Error(openLib)), ("error out", Error(0)));
        v["vi count"] = new JsonObject { ["value"] = (count ?? sent).ToString() };
        return v;
    }

    [Fact]
    public void A_clean_apply_names_no_failure()
    {
        var r = OwnerIconTools.InterpretApply(Apply(2, [0, 0], [0, 0]), 2);
        Assert.Null(r.FailedAt);
    }

    [Fact]
    public void The_apply_answer_names_the_first_failing_stage()
    {
        Assert.Equal("openOwner", OwnerIconTools.InterpretApply(Apply(2, [0, 0], [0, 0], openLib: 1562), 2).FailedAt);
        // more paths than were sent: the list was split somewhere it should not have been
        Assert.Equal("memberListSplit", OwnerIconTools.InterpretApply(Apply(2, [0, 0, 7], [0, 0, 7], count: 3), 2).FailedAt);
        Assert.Equal("applyIconToVIs", OwnerIconTools.InterpretApply(Apply(2, [0, 0], [0, 0], apply: 1562), 2).FailedAt);
        Assert.Equal("openMembers", OwnerIconTools.InterpretApply(Apply(2, [0, 7], [0, 7]), 2).FailedAt);
        Assert.Equal("saveMembers", OwnerIconTools.InterpretApply(Apply(2, [0, 0], [0, 8]), 2).FailedAt);
        Assert.Equal("runApplyHelper", OwnerIconTools.InterpretApply(null, 2).FailedAt);
    }

    [Fact]
    public void A_stage_failure_carries_its_code_and_source()
    {
        var values = Values(("carrier error", Error(0)), ("set icon error", Error(1562)), ("error out", Error(1562)));

        var failure = OwnerIconTools.FirstFailure(values,
            [("carrier error", "loadPngIntoCarrier"), ("set icon error", "setIcon"), ("error out", "closeReferences")]);

        Assert.Equal(new OwnerIconTools.StageFailure("setIcon", 1562, "here"), failure);
        Assert.Contains("restart", OwnerIconTools.Hint(1562));
    }

    [Fact]
    public void Output_pngs_follow_the_measured_layout_and_owners_keep_their_extension()
    {
        Assert.Equal(@"C:\rb\Sub\A.png", OwnerIconTools.OutputPngFor(@"C:\src\Sub\A.vi", @"C:\src", @"C:\rb"));
        Assert.Equal(@"C:\rb\Foo\Foo.lvclass.png",
                     OwnerIconTools.OutputPngFor(@"C:\src\Foo\Foo.lvclass", @"C:\src", @"C:\rb"));
    }

    [Fact]
    public void Member_pngs_never_collide()
    {
        var pngs = OwnerIconTools.MemberPngPaths(@"C:\p\Foo\Foo.lvclass",
            [@"C:\p\Foo\A.vi", @"C:\p\Other\A.vi", @"C:\p\Foo\Sub\B.vi"], @"C:\rb\members");

        Assert.Equal([@"C:\rb\members\A.png", @"C:\rb\members\A (2).png", @"C:\rb\members\Sub\B.png"], pngs);
    }

    [Fact]
    public void An_expected_png_is_found_by_path_then_by_unique_name()
    {
        var expected = Path.Combine(_dir, "exp");
        Png(@"exp\Sub\A.png");
        Png(@"exp\_classes\Foo.lvclass.png");
        Png(@"exp\x\Twice.png");
        Png(@"exp\y\Twice.png");
        var index = OwnerIconTools.PngsByName(expected);

        Assert.Equal(Path.Combine(expected, @"Sub\A.png"), OwnerIconTools.ExpectedFor(@"Sub\A.png", expected, index).Path);
        Assert.Equal(Path.Combine(expected, @"_classes\Foo.lvclass.png"),
                     OwnerIconTools.ExpectedFor(@"Foo\Foo.lvclass.png", expected, index).Path);
        Assert.Equal("AMBIGUOUS", OwnerIconTools.ExpectedFor(@"Twice.png", expected, index).Status);
        Assert.Equal("NO REFERENCE", OwnerIconTools.ExpectedFor(@"None.png", expected, index).Status);
    }

    [Fact]
    public void A_folder_expands_to_its_vis_and_the_common_folder_is_the_deepest_shared_one()
    {
        var a = Write(@"src\One\A.vi", "vi");
        var b = Write(@"src\Two\Deep\B.vi", "vi");
        Write(@"src\Two\Deep\C.ctl", "ctl");

        var (items, problem) = OwnerIconTools.ExpandPaths($"{Path.Combine(_dir, "src")}\n{a}");

        Assert.Null(problem);
        Assert.Equal([a, b], items);              // sorted, the .ctl left out, the duplicate dropped
        Assert.Equal(Path.Combine(_dir, "src"), OwnerIconTools.CommonFolder(items));
    }
}
