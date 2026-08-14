using System.Buffers.Binary;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_set_vi_icon is the one COMPOSED tool: validate, generate the helper, run it, then judge
/// the outcome from the filesystem. Two things therefore need pinning that a thin RPC wrapper
/// would not: what actually reaches LabVIEW, and the verdict logic - because the RPC's own
/// return value is unusable (errorCode 91 arrives on success), so `verified` is the contract.
/// </summary>
public sealed class IconToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-icon-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string At(string name) => Path.Combine(_dir, name);

    private string WriteVi(string name = "Target.vi")
    {
        var path = At(name);
        File.WriteAllText(path, "not really a VI, but a real file");
        return path;
    }

    /// <summary>
    /// A 24-byte file that is a valid-enough PNG for the header reader: signature plus the
    /// start of IHDR. Writing a real image would need an imaging dependency the server does
    /// not have - and does not need, since only width and height are ever read.
    /// </summary>
    private string WritePng(string name, int width = 32, int height = 32)
    {
        var path = At(name);
        var bytes = new byte[24];
        ReadOnlySpan<byte> header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                                     0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
        header.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The shipped helper, located in the repository rather than next to the exe.</summary>
    private static string ShippedHelperAixml() =>
        Res.FindRepoFile($"scripts/{IconTools.HelperAixmlFileName}")
        ?? throw new InvalidOperationException(
            $"scripts/{IconTools.HelperAixmlFileName} is missing from the repository.");

    /// <summary>
    /// Stand in for the read-back PNG the helper writes as its last act. The fake LabVIEW
    /// runs no VI, so the file has to be planted - what the tool must then decide is whether
    /// it is fresh enough to count as evidence.
    /// </summary>
    private string PlantReadBack(string name = "readback.png", TimeSpan? age = null)
    {
        var path = WritePng(name);
        if (age is { } old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow - old);
        return path;
    }

    [Fact]
    public async Task Sends_the_three_paths_the_helper_expects()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var vi = WriteVi();
        var icon = WritePng("icon.png");
        var helper = At("helper.vi");

        await new IconTools(server.Connection).SetViIconAsync(
            vi, icon, At("readback.png"), helper, ShippedHelperAixml());

        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal(helper, request.ViPath);
        Assert.Equal(vi, request.Inputs["VI Path"]);
        Assert.Equal(icon, request.Inputs["Icon File Path"]);
        Assert.Equal(At("readback.png"), request.Inputs["Read Back Path"]);
    }

    [Fact]
    public async Task Validates_the_helper_aixml_before_generating_it()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("readback.png"), At("helper.vi"),
            ShippedHelperAixml());

        var order = server.Service.Received.Select(r => r.Method)
            .Where(m => m is "ValidateAIXML" or "ConvertAIXMLToVI" or "RunVIAsTopLevel").ToList();
        Assert.Equal(["ValidateAIXML", "ConvertAIXMLToVI", "RunVIAsTopLevel"], order);
    }

    [Fact]
    public async Task The_shipped_helper_aixml_is_what_gets_generated()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("readback.png"), At("helper.vi"),
            ShippedHelperAixml());

        var request = server.Service.Last<ConvertAIXMLToVIRequest>("ConvertAIXMLToVI");
        Assert.Equal(ShippedHelperAixml(), request.AiXMLFilePath);
        Assert.Equal(At("helper.vi"), request.ViPath);
        Assert.False(request.OpenVI);
    }

    [Fact]
    public async Task The_helper_is_generated_once_and_then_reused()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var tools = new IconTools(server.Connection);
        var vi = WriteVi();
        var icon = WritePng("icon.png");

        var first = await tools.SetViIconAsync(
            vi, icon, At("readback.png"), At("helper.vi"), ShippedHelperAixml());
        var second = await tools.SetViIconAsync(
            vi, icon, At("readback.png"), At("helper.vi"), ShippedHelperAixml());

        Assert.True(Res.Bool(first, "helperGenerated"));
        Assert.False(Res.Bool(second, "helperGenerated"));
        Assert.Equal(1, server.Service.CountOf("ConvertAIXMLToVI"));
        Assert.Equal(2, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task regenerateHelper_forces_a_rebuild_of_an_existing_helper()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var tools = new IconTools(server.Connection);
        var vi = WriteVi();
        var icon = WritePng("icon.png");

        await tools.SetViIconAsync(vi, icon, At("r.png"), At("helper.vi"), ShippedHelperAixml());
        var again = await tools.SetViIconAsync(
            vi, icon, At("r.png"), At("helper.vi"), ShippedHelperAixml(), regenerateHelper: true);

        Assert.True(Res.Bool(again, "helperGenerated"));
        Assert.Equal(2, server.Service.CountOf("ConvertAIXMLToVI"));
    }

    [Fact]
    public async Task A_fresh_read_back_file_is_what_makes_the_run_verified()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        server.Service.ErrorCodeByMethod["RunVIAsTopLevel"] = 91;   // the success-looking failure
        var readBack = PlantReadBack();

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), readBack, At("helper.vi"), ShippedHelperAixml());

        Assert.Equal(91, Res.Int(result, "errorCode"));      // LabVIEW's own verdict is useless...
        Assert.True(Res.Bool(result, "verified"));           // ...so this is the contract
        Assert.Equal("32x32", Res.Str(result, "readBackSize"));
    }

    [Fact]
    public async Task A_stale_read_back_file_is_not_mistaken_for_success()
    {
        // The dangerous case: a leftover file from an earlier run would otherwise "prove"
        // that an icon was applied when nothing happened at all.
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var stale = PlantReadBack(age: TimeSpan.FromHours(1));

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), stale, At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "verified"));
        Assert.Contains("NOT applied",
            string.Join(" ", Res.Arr(result, "warnings").Select(w => w!.GetValue<string>())));
    }

    [Fact]
    public async Task A_missing_read_back_file_is_reported_as_unverified()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("never-written.png"), At("helper.vi"),
            ShippedHelperAixml());

        Assert.False(Res.Bool(result, "verified"));
        Assert.Equal(0, Res.Long(result, "readBackBytes"));
    }

    [Fact]
    public async Task An_icon_that_is_not_32x32_is_applied_but_warned_about()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("big.png", 256, 256), PlantReadBack(), At("helper.vi"),
            ShippedHelperAixml());

        Assert.Equal("256x256", Res.Str(result, "iconImageSize"));
        Assert.Contains("256x256",
            string.Join(" ", Res.Arr(result, "warnings").Select(w => w!.GetValue<string>())));
        Assert.Equal(1, server.Service.CountOf("RunVIAsTopLevel"));   // warned, not refused
    }

    [Fact]
    public async Task A_non_png_icon_is_warned_about_and_still_attempted()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var bitmap = At("icon.bmp");
        File.WriteAllBytes(bitmap, [0x42, 0x4D, 0x01, 0x02, 0x03, 0x04]);

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), bitmap, PlantReadBack(), At("helper.vi"), ShippedHelperAixml());

        Assert.True(Res.IsNull(result, "iconImageSize"));
        Assert.Contains("not a PNG",
            string.Join(" ", Res.Arr(result, "warnings").Select(w => w!.GetValue<string>())));
        Assert.Equal(1, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task A_missing_vi_fails_before_labview_is_touched()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new IconTools(server.Connection).SetViIconAsync(
            At("no-such.vi"), WritePng("icon.png"), At("r.png"), At("helper.vi"),
            ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("FileNotFoundException", Res.Str(result, "errorKind"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
        Assert.Equal(0, server.Service.CountOf("ConvertAIXMLToVI"));
    }

    [Fact]
    public async Task A_missing_icon_image_fails_before_labview_is_touched()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), At("no-such.png"), At("r.png"), At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task A_missing_helper_aixml_says_which_file_it_wanted()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("r.png"), At("helper.vi"), At("gone.xml"));

        Assert.False(Res.Bool(result, "ok"));
        Assert.Contains("gone.xml", Res.Str(result, "error"));
        Assert.Equal(0, server.Service.CountOf("ValidateAIXML"));
    }

    [Fact]
    public async Task An_invalid_helper_aixml_stops_before_anything_is_generated()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = 1;
        server.Service.ErrorMessage = "Unsupported SubVI: whatever";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("r.png"), At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("helperAixmlInvalid", Res.Str(result, "errorKind"));
        Assert.Equal(0, server.Service.CountOf("ConvertAIXMLToVI"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task Error_1051_while_generating_carries_the_name_in_memory_hint()
    {
        // 1051 is unrecoverable by retrying: a failed generation keeps the name occupied in
        // LabVIEW for the rest of the session, so the advice has to be "use another name".
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCodeByMethod["ConvertAIXMLToVI"] = 1051;
        server.Service.ErrorMessage = "A LabVIEW file of that name already exists in memory";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("r.png"), At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("helperGenerationFailed", Res.Str(result, "errorKind"));
        Assert.Contains("restart LabVIEW", Res.Obj(result)["detail"]!["hint"]!.GetValue<string>());
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task Error_7_while_generating_blames_the_directory_not_the_missing_file()
    {
        // Measured: LabVIEW refuses to save into some directories that demonstrably exist and
        // are writable - %LOCALAPPDATA%\LabVIEWMCP\helpers did, %TEMP% did not. Reporting the
        // documented "the directory does not exist" cause would send the reader the wrong way,
        // because this tool creates the directory itself.
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCodeByMethod["ConvertAIXMLToVI"] = 7;
        server.Service.ErrorMessage = "File not found";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("r.png"), At(@"helpers\helper.vi"),
            ShippedHelperAixml());

        var hint = Res.Obj(result)["detail"]!["hint"]!.GetValue<string>();
        Assert.Contains("does exist", hint);
        Assert.Contains(At("helpers"), hint);
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task A_helper_that_reports_success_but_writes_no_file_is_a_failure()
    {
        // The silent-failure shape this interface is known for: errorCode 0 and nothing on disk.
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = null;                      // no .vi is written

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), At("r.png"), At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("helperGenerationFailed", Res.Str(result, "errorKind"));
        Assert.False(Res.Obj(result)["detail"]!["viExistsNow"]!.GetValue<bool>());
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task The_read_back_directory_is_created_because_labview_will_not()
    {
        // LabVIEW's file write fails with Error 7 rather than creating a directory.
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var nested = At(@"deep\deeper\readback.png");

        await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), WritePng("icon.png"), nested, At(@"helpers\helper.vi"),
            ShippedHelperAixml());

        Assert.True(Directory.Exists(Path.GetDirectoryName(nested)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(At(@"helpers\helper.vi"))));
    }

    [Fact]
    public async Task Reports_the_vi_size_before_and_after_so_a_resave_is_visible()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var vi = WriteVi();

        var result = await new IconTools(server.Connection).SetViIconAsync(
            vi, WritePng("icon.png"), PlantReadBack(), At("helper.vi"), ShippedHelperAixml());

        Assert.Equal(new FileInfo(vi).Length, Res.Long(result, "viBytesBefore"));
        Assert.Equal(new FileInfo(vi).Length, Res.Long(result, "viBytesAfter"));
        Assert.False(Res.Bool(result, "viResaved"));   // the fake LabVIEW never touches it
    }

    // ---- drawing the icon here instead of making the caller produce a PNG ----
    // Measured motive: an agent drawing the bitmap through PowerShell and System.Drawing cost
    // 12.5 s of a 455 s generation, plus a whole extra tool call. See docs/aixml-reference.md.

    private static string Warnings(string result) =>
        string.Join(" ", Res.Arr(result, "warnings").Select(w => w!.GetValue<string>()));

    [Fact]
    public async Task Draws_the_icon_from_lines_and_sends_that_file_to_labview()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var vi = WriteVi();

        var result = await new IconTools(server.Connection).SetViIconAsync(
            vi, iconImagePath: null, readBackPath: PlantReadBack(), helperViPath: At("helper.vi"),
            helperAixmlPath: ShippedHelperAixml(), line1: "DAQ", line2: "3AI", line3: "TDMS");

        var drawn = Res.Str(result, "iconImagePath")!;
        Assert.True(Res.Bool(result, "iconRendered"));
        Assert.Equal("32x32", Res.Str(result, "iconImageSize"));
        Assert.True(File.Exists(drawn), $"no icon was written at '{drawn}'");

        // What reaches LabVIEW must be the file that was drawn, not a path that only exists in
        // the response - this is the one contract the fake can check.
        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal(drawn, request.Inputs["Icon File Path"]);
        Assert.Equal("", Warnings(result));   // a plain 5-char, 3-line icon warrants no warning
    }

    [Fact]
    public async Task A_supplied_file_wins_over_lines_and_says_so()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        var icon = WritePng("icon.png");

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), icon, At("readback.png"), At("helper.vi"), ShippedHelperAixml(),
            line1: "DAQ");

        Assert.False(Res.Bool(result, "iconRendered"));
        Assert.Equal(icon, Res.Str(result, "iconImagePath"));
        Assert.Contains("lines were ignored", Warnings(result));
    }

    [Fact]
    public async Task Refuses_to_run_with_neither_a_file_nor_a_line()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), iconImagePath: null, readBackPath: At("readback.png"),
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("ArgumentException", Res.Str(result, "errorKind"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task Warns_about_a_line_too_long_to_fit_but_still_applies_the_icon()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), iconImagePath: null, readBackPath: At("readback.png"),
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml(),
            line1: "ACQUISITION");

        Assert.True(Res.Bool(result, "iconRendered"));
        Assert.Contains("Only the first 5", Warnings(result));
        Assert.Contains("'ACQUI'", Warnings(result));
        Assert.Equal(1, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task Warns_about_an_undrawable_character_and_about_a_bad_colour()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), iconImagePath: null, readBackPath: At("readback.png"),
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml(),
            line1: "A_B", bannerColor: "octarine");

        var warnings = Warnings(result);
        Assert.Contains("cannot draw", warnings);
        Assert.Contains("'_'", warnings);
        Assert.Contains("not RRGGBB", warnings);
        Assert.Contains("006699", warnings);          // names the fallback it actually used
        Assert.True(Res.Bool(result, "iconRendered"));
    }

    [Fact]
    public async Task Says_what_labview_will_do_to_a_colour_outside_the_web_safe_cube()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), iconImagePath: null, readBackPath: PlantReadBack(),
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml(),
            line1: "DAQ", bannerColor: "005A9C");   // real NI blue: not web-safe

        var warnings = Warnings(result);
        Assert.Contains("not web-safe", warnings);
        Assert.Contains("005A9C", warnings);
        Assert.Contains("006699", warnings);          // what it will read back as
        Assert.True(Res.Bool(result, "iconRendered"));
    }

    [Fact]
    public async Task The_default_colours_are_web_safe_so_nothing_is_warned_about()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";

        var result = await new IconTools(server.Connection).SetViIconAsync(
            WriteVi(), iconImagePath: null, readBackPath: PlantReadBack(),
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml(), line1: "DAQ");

        Assert.Equal("", Warnings(result));
    }
}
