using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_discuss_file is a COMPOSED tool - validate, generate the helper, run it - so what needs
/// pinning is what actually reaches LabVIEW and how the answer is judged. The judging matters
/// more here than in most tools, because the whole reason this drives NI's two packed-library
/// VIs instead of the menu callback that wraps them is to GET a verdict: the callback returns
/// nothing.
///
/// THE FIXTURES BELOW ARE THE REAL FLATTENING, copied verbatim out of a measured run on
/// 2026-09-10, not an invention. `CLAUDE.md` records three tools that failed on first real use
/// because their fixture agreed with the defect instead of with LabVIEW.
///
/// Three tests exist to keep a measurement from being undone by a later edit: the extension
/// check that runs before any RPC, the ban on an enum-typed indicator in the shipped helper, and
/// the helper's target spellings.
/// </summary>
public sealed class DiscussToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-discuss-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string At(string name) => Path.Combine(_dir, name);

    private string Write(string name)
    {
        var path = At(name);
        File.WriteAllText(path, "not really a LabVIEW file, but a real one");
        return path;
    }

    private static string ShippedHelperAixml() =>
        Res.FindRepoFile($"scripts/{DiscussTools.HelperAixmlFileName}")
        ?? throw new InvalidOperationException(
            $"scripts/{DiscussTools.HelperAixmlFileName} is missing from the repository.");

    /// <summary>A flattened LabVIEW error cluster, exactly as the helper returns one.</summary>
    private static string ErrorClusterXml(int code, string source = "") =>
        $"""
        <Cluster>
        <Name>error out</Name>
        <NumElts>3</NumElts>
        <Boolean>
        <Name>status</Name>
        <Val>{(code == 0 ? 0 : 1)}</Val>
        </Boolean>
        <I32>
        <Name>code</Name>
        <Val>{code}</Val>
        </I32>
        <String>
        <Name>source</Name>
        <Val>{source}</Val>
        </String>
        </Cluster>
        """;

    /// <summary>
    /// InvokeDiscussVI's own two indicators as Ctrl Val.Get All flattens them. Ctrl Val.Get All
    /// returns INDICATORS ONLY - measured - so this array is exactly those two and never the
    /// three controls.
    /// </summary>
    private static string InvokeValuesXml(int code, string message = "") =>
        $"""
        <Array>
        <Name>Get All Control Values Variant</Name>
        <Dimsize>2</Dimsize>
        <Cluster>
        <Name></Name>
        <NumElts>2</NumElts>
        <String>
        <Name>Name</Name>
        <Val>error code</Val>
        </String>
        <LvVariant>
        <Name>Variant Data</Name>
        <I32>
        <Name>error code</Name>
        <Val>{code}</Val>
        </I32>
        </LvVariant>
        </Cluster>
        <Cluster>
        <Name></Name>
        <NumElts>2</NumElts>
        <String>
        <Name>Name</Name>
        <Val>error message</Val>
        </String>
        <LvVariant>
        <Name>Variant Data</Name>
        <String>
        <Name>error message</Name>
        <Val>{message}</Val>
        </String>
        </LvVariant>
        </Cluster>
        </Array>
        """;

    private static void GiveCleanOutputs(FakeLvaiService service, int discussCode = 0,
                                         string discussMessage = "")
    {
        service.ViFileContent = "generated helper";
        service.Outputs["File Type Used"] = "DISCUSS_FILE_TYPE_VI";
        service.Outputs["Launch Error"] = ErrorClusterXml(0);
        service.Outputs["Invoke Values"] = InvokeValuesXml(discussCode, discussMessage);
        service.Outputs["Invoke Error"] = ErrorClusterXml(0);
    }

    // ---- what reaches LabVIEW -------------------------------------------------------------

    [Fact]
    public async Task Sends_the_three_inputs_the_helper_declares()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);
        var target = Write("Target.vi");
        var helper = At("helper.vi");

        await new DiscussTools(server.Connection).DiscussFileAsync(
            target, "Some Name", "VI", helper, ShippedHelperAixml());

        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal(helper, request.ViPath);
        Assert.Equal(target, request.Inputs["Target Path"]);
        Assert.Equal("Some Name", request.Inputs["Target Name"]);
        Assert.Equal("VI", request.Inputs["File Type"]);
    }

    [Fact]
    public async Task An_omitted_name_defaults_to_the_file_name()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);

        await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Motor Control.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal("Motor Control.vi", request.Inputs["Target Name"]);
    }

    /// <summary>
    /// The helper's diagram compares the string with a case-SENSITIVE `Equal?` against
    /// "PROJECT", so anything not folded here falls silently through to VI.
    /// </summary>
    [Fact]
    public async Task A_lowercase_file_type_is_folded_before_it_reaches_the_diagram()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);

        await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Thing.lvproj"), null, "project", At("helper.vi"), ShippedHelperAixml());

        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal("PROJECT", request.Inputs["File Type"]);
    }

    [Fact]
    public async Task Validates_the_helper_aixml_before_generating_and_running_it()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);

        await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        var order = server.Service.Received.Select(r => r.Method)
            .Where(m => m is "ValidateAIXML" or "ConvertAIXMLToVI" or "RunVIAsTopLevel").ToList();
        Assert.Equal(["ValidateAIXML", "ConvertAIXMLToVI", "RunVIAsTopLevel"], order);
    }

    [Fact]
    public async Task The_shipped_helper_aixml_is_what_gets_generated()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);

        await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        var request = server.Service.Last<ConvertAIXMLToVIRequest>("ConvertAIXMLToVI");
        Assert.Equal(ShippedHelperAixml(), request.AiXMLFilePath);
        Assert.Equal(At("helper.vi"), request.ViPath);
    }

    // ---- the verdict, which is the reason this route exists -------------------------------

    [Fact]
    public async Task Reports_the_error_code_nis_own_callback_discards()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        Assert.True(Res.Bool(result, "ok"));
        Assert.Equal(0, Res.Int(result, "discussErrorCode"));
        Assert.Equal(0, Res.Int(result, "launchErrorCode"));
        Assert.Equal(0, Res.Int(result, "helperChainErrorCode"));
        Assert.Contains("reported no error", Res.Str(result, "note"));
    }

    /// <summary>
    /// The failure NI's callback would have shown as nothing at all. `ok` has to follow
    /// InvokeDiscussVI's own code, or this tool is back to reporting a silent success.
    /// </summary>
    [Fact]
    public async Task A_failure_inside_invokediscussvi_makes_the_call_not_ok()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service, discussCode: 7, discussMessage: "file not found");

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal(7, Res.Int(result, "discussErrorCode"));
        Assert.Equal("file not found", Res.Str(result, "discussErrorMessage"));
        Assert.Contains("discards this code", Res.Str(result, "note"));
        // The raw text comes back whenever the verdict is not clean.
        Assert.True(Res.Has(result, "invokeValuesXml"));
    }

    /// <summary>
    /// "No error" and "no answer" are opposite verdicts. An unreadable readback must never be
    /// folded into 0 - that is precisely how a silent success gets manufactured.
    /// </summary>
    [Fact]
    public async Task An_unreadable_readback_is_not_treated_as_success()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);
        server.Service.Outputs["Invoke Values"] = "this is not XML at all";

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.True(Res.IsNull(result, "discussErrorCode"));
        Assert.Contains("could not be read back", Res.Str(result, "note"));
        Assert.Equal("this is not XML at all", Res.Str(result, "invokeValuesXml"));
    }

    /// <summary>
    /// NI leaves the launch's error unwired, so a launch failure does not stop the push - but it
    /// must still be reported rather than lost the way NI loses it.
    /// </summary>
    [Fact]
    public async Task A_launch_failure_is_reported_without_failing_the_push()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);
        server.Service.Outputs["Launch Error"] = ErrorClusterXml(1055, "Launch Nigel Chat.vi");

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        Assert.Equal(1055, Res.Int(result, "launchErrorCode"));
        Assert.Equal("Launch Nigel Chat.vi", Res.Str(result, "launchErrorSource"));
        Assert.True(Res.Bool(result, "ok"));
    }

    [Fact]
    public async Task A_broken_stage_around_the_push_makes_the_call_not_ok()
    {
        await using var server = await LvaiTestServer.StartAsync();
        GiveCleanOutputs(server.Service);
        server.Service.Outputs["Invoke Error"] = ErrorClusterXml(1057, "Ctrl Val.Set");

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal(1057, Res.Int(result, "helperChainErrorCode"));
    }

    // ---- the measured refusals -----------------------------------------------------------

    /// <summary>
    /// THE MEASUREMENT THIS TEST PROTECTS, 2026-09-10: handed an `.xml` path, the chain answered
    /// errorCode 0 and the only complaint was a red banner inside the chat window - "The file you
    /// selected is not a VI." - where no caller can read it. It was proven LabVIEW-free by
    /// accident: it refused correctly while the gRPC service was down.
    /// </summary>
    [Fact]
    public async Task A_target_that_is_not_a_vi_is_refused_before_labview_is_touched()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("helper.xml"), null, "VI", At("helper.vi"), ShippedHelperAixml());

        Assert.Equal("targetDoesNotMatchFileType", Res.Str(result, "errorKind"));
        Assert.Empty(server.Service.Received);
    }

    [Fact]
    public async Task A_project_file_type_wants_an_lvproj()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "PROJECT", At("helper.vi"), ShippedHelperAixml());

        Assert.Equal("targetDoesNotMatchFileType", Res.Str(result, "errorKind"));
        Assert.Empty(server.Service.Received);
    }

    [Fact]
    public async Task An_unknown_file_type_is_refused_and_names_what_is_accepted()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new DiscussTools(server.Connection).DiscussFileAsync(
            Write("Target.vi"), null, "UNSPECIFIED", At("helper.vi"), ShippedHelperAixml());

        Assert.Equal("badArguments", Res.Str(result, "errorKind"));
        Assert.Empty(server.Service.Received);
    }

    // ---- pure logic ----------------------------------------------------------------------

    [Theory]
    [InlineData("VI", "VI")]
    [InlineData("vi", "VI")]
    [InlineData(" Vi ", "VI")]
    [InlineData("", "VI")]
    [InlineData(null, "VI")]
    [InlineData("PROJECT", "PROJECT")]
    [InlineData("project", "PROJECT")]
    public void Recognised_file_types_fold_to_nis_spelling(string? requested, string expected) =>
        Assert.Equal(expected, DiscussTools.NormaliseFileType(requested));

    [Theory]
    [InlineData("UNSPECIFIED")]
    [InlineData("DISCUSS_FILE_TYPE_VI")]
    [InlineData("lvclass")]
    public void An_unrecognised_file_type_is_not_guessed(string requested) =>
        Assert.Null(DiscussTools.NormaliseFileType(requested));

    [Theory]
    [InlineData(@"C:\x\A.vi", "VI")]
    [InlineData(@"C:\x\A.VI", "VI")]
    [InlineData(@"C:\x\A.lvproj", "PROJECT")]
    public void A_matching_extension_draws_no_complaint(string path, string fileType) =>
        Assert.Null(DiscussTools.ExtensionComplaint(path, fileType));

    [Theory]
    [InlineData(@"C:\x\A.xml", "VI")]
    [InlineData(@"C:\x\A.lvproj", "VI")]
    [InlineData(@"C:\x\A.vi", "PROJECT")]
    [InlineData(@"C:\x\A", "VI")]
    public void A_mismatched_extension_is_complained_about(string path, string fileType) =>
        Assert.NotNull(DiscussTools.ExtensionComplaint(path, fileType));

    [Fact]
    public void An_error_cluster_yields_its_code_and_source()
    {
        var (code, source) = DiscussTools.ErrorCluster(ErrorClusterXml(1055, "Open.vi"));
        Assert.Equal(1055, code);
        Assert.Equal("Open.vi", source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Cluster><Name>error out</Name></Cluster>")]
    public void An_unreadable_error_cluster_yields_no_code_rather_than_zero(string? xml) =>
        Assert.Null(DiscussTools.ErrorCluster(xml).Code);

    /// <summary>
    /// The verdict must not claim a launch when the chat was ALREADY running, and must not read
    /// as success when InvokeDiscussVI itself failed.
    /// </summary>
    [Fact]
    public void The_verdict_follows_invokediscussvis_own_code_first()
    {
        Assert.Contains("could not be read back", DiscussTools.Verdict(false, null, false, true));
        Assert.Contains("discards this code", DiscussTools.Verdict(false, 7, false, true));
        Assert.Contains("the launch happened too", DiscussTools.Verdict(true, 0, false, true));
        Assert.Contains("says nothing about this call",
            DiscussTools.Verdict(true, 0, true, true));
    }

    /// <summary>
    /// An upgrade ships new AIXML beside an OLD cached helper VI, and the helper is generated
    /// once and reused - so without this check the tool would read outputs the stale helper does
    /// not produce, which looks like a broken tool rather than a stale file.
    /// </summary>
    [Fact]
    public void A_helper_older_than_its_aixml_is_stale()
    {
        var aixml = Write("helper.xml");
        var vi = Write("helper.vi");

        File.SetLastWriteTimeUtc(vi, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(aixml, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(DiscussTools.IsStale(aixml, vi));

        File.SetLastWriteTimeUtc(vi, new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(DiscussTools.IsStale(aixml, vi));
    }

    [Fact]
    public void A_helper_that_does_not_exist_yet_is_not_called_stale() =>
        Assert.False(DiscussTools.IsStale(Write("helper.xml"), At("nothing.vi")));

    // ---- the shipped helper --------------------------------------------------------------

    /// <summary>
    /// An enum indicator on this helper's pane comes back EMPTY with errorCode 91 under
    /// RunVIAsTopLevel - measured 2026-09-10, after the VI had already run correctly - so echoing
    /// the enum would make every successful call report an error. The readback is a STRING
    /// carrying the enum's item name instead, and the two `Select` nodes share one boolean so the
    /// text cannot disagree with the enum that reaches InvokeDiscussVI.
    /// </summary>
    [Fact]
    public void The_shipped_helper_declares_no_enum_indicator()
    {
        var indicators = File.ReadAllLines(ShippedHelperAixml())
            .Where(line => line.Contains("<Indicator", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(indicators);
        Assert.All(indicators, line =>
            Assert.DoesNotContain("type=\"uint32{", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// The helper drives NI's two packed-library VIs BY QUALIFIED NAME through Open VI Reference,
    /// and deliberately not through the menu callback that wraps them: the callback is reachable
    /// as an AIXML `Call` and returns nothing, which is the whole limitation this route removes.
    /// A `Call` element reappearing here means someone went back to the callback and lost the
    /// error code again.
    /// </summary>
    [Fact]
    public void The_shipped_helper_opens_nis_two_vis_by_name_rather_than_calling_the_wrapper()
    {
        var helper = File.ReadAllText(ShippedHelperAixml());

        Assert.Contains(@"LV AI Core.lvlibp\3ALaunch Nigel Chat.vi", helper,
            StringComparison.Ordinal);
        Assert.Contains(
            @"LV AI gRPC Service.lvlibp\3AgRPC Implementations.lvlib\3AInvokeDiscussVI.vi",
            helper, StringComparison.Ordinal);
        // A `Call` element is the only way to invoke the wrapper, so its absence is the real
        // assertion. NOT the absence of the NAME: the helper's own description explains why the
        // wrapper is bypassed, and that prose is worth keeping - it is how the next reader learns
        // that the callback exists, is reachable, and costs the error code.
        Assert.DoesNotContain("<Call ", helper, StringComparison.Ordinal);
        Assert.DoesNotContain(@"target=""LV AI Plugins", helper, StringComparison.Ordinal);
        Assert.Contains("lv_discuss_file_with_nigel", helper, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four outputs this tool reads by name. A rename on either side is a silent break: the
    /// tool would read null and report a contract failure for a call that worked.
    /// </summary>
    [Theory]
    [InlineData("File Type Used")]
    [InlineData("Launch Error")]
    [InlineData("Invoke Values")]
    [InlineData("Invoke Error")]
    public void The_shipped_helper_declares_the_output_the_tool_reads(string name) =>
        Assert.Contains($"_name=\"{name}\"", File.ReadAllText(ShippedHelperAixml()),
            StringComparison.Ordinal);
}
