using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_exec_state on the pylabview backend names LINKER ERRORS since 2026-10-12. Until then it read
/// <c>ExecState</c> directly over ActiveX and answered <c>linkerErrors: null</c> for every VI,
/// because ActiveX has no linker-error property - while the grpc route named the cause. It now runs
/// the SAME helper, lvai_exec_state.xml, composed by our composer and run over ActiveX; the direct
/// read is the fallback and says so.
///
/// Measured that day on a caller whose subVI file had been removed, both backends: execState 0 and
/// <c>Missing subVI ESA Gone 7f3a.vi in VI ESA Orphan 7f3a.vi.</c> in linkerErrors; on the intact
/// caller execState 1 and an empty <c>&lt;Val&gt;</c> on both. The strings below are those answers.
/// Every route test uses fakes for the composer and LabVIEW, so the rules - when to trust the
/// helper, when to fall back, what never falls back - are checked without either.
/// </summary>
public sealed class ExecStateComRouteTests
{
    private const string Vi = @"C:\fixtures\ESA Orphan 7f3a.vi";

    /// <summary>The helper's answer over ActiveX for the orphan, CRLF as GetControlValue returned it.</summary>
    private const string MissingSubViOverCom =
        "<String>\r\n<Name>VILoadErr</Name>\r\n<Val>Missing subVI ESA Gone 7f3a.vi in VI ESA Orphan 7f3a.vi.\r\n" +
        "</Val>\r\n</String>\r\n";

    /// <summary>The same VI over grpc - LF.</summary>
    private const string MissingSubViOverGrpc =
        "<String>\n<Name>VILoadErr</Name>\n<Val>Missing subVI ESA Gone 7f3a.vi in VI ESA Orphan 7f3a.vi.\n" +
        "</Val>\n</String>\n";

    private static LabViewCom.RunResult Ran(string state, string linker = "", string code = "0",
                                            string source = "", string? viPath = Vi) =>
        new(1, true, true, false, 40, [],
        [
            new("exec state", state, null),
            new("linker errors xml", linker, null),
            new("code", code, null),
            new("source", source, null),
            new("vi path", viPath, null),
        ]);

    // ------------------------------------------------------------ reading the helper's answer

    [Fact]
    public void A_missing_subVI_is_read_from_the_helper_with_the_grpc_line_ends()
    {
        var (reading, why) = ExecStateTools.FromHelperRun(Ran("0", MissingSubViOverCom), Vi);

        Assert.Null(why);
        Assert.NotNull(reading);
        Assert.True(reading!.Broken);
        Assert.True(reading.LinkerErrorsRead);
        Assert.Equal(MissingSubViOverGrpc, reading.LinkerErrors);
    }

    [Fact]
    public void A_reference_the_helper_could_not_open_is_minus_one_with_code_and_source()
    {
        var (reading, _) = ExecStateTools.FromHelperRun(
            Ran("-1", code: "1059", source: "Open VI Reference in lvai_exec_state.vi"), Vi);

        Assert.True(reading!.CouldNotOpen);
        Assert.False(reading.Broken);
        Assert.Equal(1059, reading.Code);
        Assert.Equal("Open VI Reference in lvai_exec_state.vi", reading.Source);
    }

    private static LabViewCom.RunResult UntrustedRun(string name) => name switch
    {
        "helper broken" => new(0, false, false, false, 0, [], []),
        "input refused" => new(1, false, false, false, 0, ["vi path = 'x': refused"], []),
        "aborted" => new(1, true, false, true, 5000, [], []),
        "unread value" => new(1, true, true, false, 40, [],
            [new("exec state", null, "no such control"), new("linker errors xml", "", null),
             new("code", "0", null), new("source", "", null), new("vi path", Vi, null)]),
        "another caller's path" => Ran("1", viPath: @"C:\other\Some.vi"),
        "not a number" => Ran("eIdle"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Each of these is NO answer - a reading from them could be about nothing, or another VI.</summary>
    [Theory]
    [InlineData("helper broken", "not executable")]
    [InlineData("input refused", "refused the helper's input")]
    [InlineData("aborted", "did not finish")]
    [InlineData("unread value", "exec state")]
    [InlineData("another caller's path", "another caller")]
    [InlineData("not a number", "not a number")]
    public void An_answer_the_route_cannot_trust_is_no_reading(string name, string reason)
    {
        var (reading, why) = ExecStateTools.FromHelperRun(UntrustedRun(name), Vi);

        Assert.Null(reading);
        Assert.Contains(reason, why);
    }

    // ------------------------------------------------------------ the route

    private sealed class Fake
    {
        public (string? Vi, string? Problem) Composed = (@"C:\helpers-pylabview\lvai_exec_state.vi", null);
        public Func<LabViewCom.RunResult>? Run;
        public Func<int>? Direct;
        public int RunCalls, DirectCalls;
        public IReadOnlyList<LabViewCom.Input>? Inputs;
        public IReadOnlyList<string>? Names;

        public ExecStateTools.ComRoute Route => new(
            (_, _) => Task.FromResult(Composed),
            (_, inputs, names, _, _) =>
            {
                RunCalls++;
                Inputs = inputs;
                Names = names;
                return Task.FromResult(Run!());
            },
            (_, _, _) =>
            {
                DirectCalls++;
                return Task.FromResult(Direct!());
            });
    }

    [Fact]
    public async Task The_helper_answers_when_it_runs_and_the_direct_read_is_not_taken()
    {
        var fake = new Fake { Run = () => Ran("0", MissingSubViOverCom) };

        var read = await ExecStateTools.ReadOverComAsync(Vi, 30, fake.Route, default);

        Assert.Equal("helper", read.Route);
        Assert.Null(read.WhyNoHelper);
        Assert.True(read.Reading.LinkerErrorsRead);
        Assert.Contains("Missing subVI ESA Gone 7f3a.vi", read.Reading.LinkerErrors);
        Assert.Equal(0, fake.DirectCalls);
        // the target goes in as its full path, and the input is read back to catch a second caller
        Assert.Equal(Vi, Assert.Single(fake.Inputs!).Text);
        Assert.Contains(ExecStateTools.HelperInput, fake.Names!);
    }

    [Fact]
    public async Task A_helper_that_cannot_be_composed_falls_back_and_says_why()
    {
        var fake = new Fake
        {
            Composed = (null, "the pylabview composer refuses lvai_exec_state.xml: Node 'X'"),
            Direct = () => 0,
        };

        var read = await ExecStateTools.ReadOverComAsync(Vi, 30, fake.Route, default);

        Assert.Equal("direct", read.Route);
        Assert.Equal(0, read.Reading.State);
        Assert.False(read.Reading.LinkerErrorsRead);
        Assert.Contains("refuses lvai_exec_state.xml", read.WhyNoHelper);
        Assert.Equal(0, fake.RunCalls);
    }

    [Fact]
    public async Task A_helper_run_that_fails_falls_back_to_the_direct_read()
    {
        var fake = new Fake
        {
            Run = () => throw new LabViewCom.ComException("comCallFailed", "VI is already running"),
            Direct = () => 1,
        };

        var read = await ExecStateTools.ReadOverComAsync(Vi, 30, fake.Route, default);

        Assert.Equal("direct", read.Route);
        Assert.Equal(1, read.Reading.State);
        Assert.Contains("comCallFailed", read.WhyNoHelper);
        Assert.Equal(1, fake.DirectCalls);
    }

    [Fact]
    public async Task An_untrusted_helper_answer_falls_back_to_the_direct_read()
    {
        var fake = new Fake { Run = () => Ran("1", viPath: @"C:\other\Some.vi"), Direct = () => 1 };

        var read = await ExecStateTools.ReadOverComAsync(Vi, 30, fake.Route, default);

        Assert.Equal("direct", read.Route);
        Assert.Contains("another caller", read.WhyNoHelper);
    }

    /// <summary>A COM call that hung waits behind a dialog; a direct read would wait behind it too.</summary>
    [Theory]
    [InlineData("comCallTimedOut")]
    [InlineData("labviewNotRunning")]
    public async Task A_hang_or_no_LabVIEW_ends_the_call_instead_of_falling_back(string kind)
    {
        var fake = new Fake { Run = () => throw new LabViewCom.ComException(kind, "x"), Direct = () => 1 };

        var e = await Assert.ThrowsAsync<LabViewCom.ComException>(
            () => ExecStateTools.ReadOverComAsync(Vi, 30, fake.Route, default));

        Assert.Equal(kind, e.Kind);
        Assert.Equal(0, fake.DirectCalls);
    }

    /// <summary>The -1 contract holds on the fallback too: refused to open is "could not look", never eBad.</summary>
    [Fact]
    public async Task A_VI_the_direct_read_could_not_open_is_minus_one()
    {
        var fake = new Fake
        {
            Composed = (null, "not composable"),
            Direct = () => throw new LabViewCom.ComException(LabViewCom.ViNotOpened,
                @"LabVIEW could not open 'C:\x.vi': LabVIEW: (Hex 0x4A) Memory or data structure corrupt."),
        };

        var read = await ExecStateTools.ReadOverComAsync(Vi, 30, fake.Route, default);

        Assert.True(read.Reading.CouldNotOpen);
        Assert.Equal(74, read.Reading.Code);
        Assert.False(read.Reading.Broken);
    }

    // ------------------------------------------------------------ the answer

    [Fact]
    public void The_answer_names_linker_errors_when_read_and_says_why_not_when_not()
    {
        var helper = JsonNode.Parse(ExecStateTools.ComAnswer(Vi, new ExecStateTools.ComReading(
            new ExecStateReading(0, MissingSubViOverGrpc, 0, ""), "helper", @"C:\h.vi", null)))!;
        Assert.Equal(MissingSubViOverGrpc, helper["linkerErrors"]!.GetValue<string>());
        Assert.True(helper["linkerErrorsAvailable"]!.GetValue<bool>());
        Assert.Equal("helper", helper["route"]!.GetValue<string>());
        Assert.True(helper["broken"]!.GetValue<bool>());

        var direct = JsonNode.Parse(ExecStateTools.ComAnswer(Vi, new ExecStateTools.ComReading(
            new ExecStateReading(0, "", 0, "", LinkerErrorsRead: false), "direct", null, "no composer")))!;
        Assert.Null(direct["linkerErrors"]);
        Assert.False(direct["linkerErrorsAvailable"]!.GetValue<bool>());
        Assert.Equal("no composer", direct["linkerErrorsUnavailableBecause"]!.GetValue<string>());
        Assert.Contains("NOT read", direct["note"]!.GetValue<string>());
    }

    // ------------------------------------------------------------ the helper itself

    /// <summary>
    /// The property the whole route hangs on. Harvested from NI's own build of this helper
    /// ("P3 ni lvai_exec_state.vi", FINDINGS §7a) - not guessed: LabVIEW's label VILoadErr, code
    /// 599, a string. A table that lost it would make the helper refuse to compose, and the tool
    /// would quietly fall back to the read with no linker errors.
    /// </summary>
    [Fact]
    public void The_props_table_carries_VI_Linker_Errors_from_NIs_build_of_the_helper()
    {
        var table = JsonNode.Parse(File.ReadAllText(
            RepoTree.Path("scripts", "aixml-pylabview", "parts", "vi-server-props.json")))!;
        var row = table["props"]!["LV.VI"]!["VI Linker Errors"]!;

        Assert.Equal(599, row["code"]!.GetValue<int>());
        Assert.Equal("VILoadErr", row["label"]!.GetValue<string>());
        Assert.Contains("Type=\"String\"", row["type"]!.GetValue<string>());
        Assert.Contains("lvai_exec_state", row["source"]!.GetValue<string>());
    }

    /// <summary>
    /// The shipped helper composes with the shipped parts, and the composed VI still reads VI
    /// Linker Errors. Composed into a scratch folder, NOT the shared helpers-pylabview folder a
    /// running server executes from. Skips where the composer is not installed.
    /// </summary>
    [Fact]
    public async Task The_shipped_helper_composes_and_keeps_its_linker_error_read()
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return;
        var aixml = RepoTree.Path("scripts", ExecStateTools.HelperAixmlFileName);
        var folder = Path.Combine(Path.GetTempPath(), "lvaimcp-tests", Guid.NewGuid().ToString("N"));
        var vi = Path.Combine(folder, "lvai_exec_state.vi");
        try
        {
            var composed = await AixmlComposer.ComposeAsync(setup, aixml, vi, [RepoTree.Path("scripts")], 120, default);
            Assert.True(composed.Written, composed.Message);
            Assert.False(AixmlTools.ContainsCompiledCode(vi));

            var back = Path.Combine(folder, "back.xml");
            var read = await AixmlComposer.ReadAsync(setup, vi, back, 120, default);
            Assert.True(read.Written, read.Message);
            var text = await File.ReadAllTextAsync(back);
            Assert.Contains("VI Linker Errors", text);
            foreach (var name in ExecStateTools.HelperReadBack)
                Assert.Contains($"_name=\"{name}\"", text);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>The descriptions said "no linker errors" on pylabview; they must not any more.</summary>
    [Fact]
    public void The_descriptions_no_longer_promise_no_linker_errors_on_pylabview()
    {
        var method = typeof(ExecStateTools).GetMethod(nameof(ExecStateTools.ExecStateAsync))!;
        var backend = method.GetParameters().Single(p => p.Name == "backend")
                            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.DoesNotContain("no linker errors", backend);
        Assert.Contains("linkerErrorsAvailable", backend);
        Assert.Contains("helpers-pylabview", backend);
    }
}
