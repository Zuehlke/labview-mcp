using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The AIXML backend switch - NI's gRPC service or our own pylabview composer - added 2026-10-10.
///
/// THE PYLABVIEW HALF RUNS THE REAL COMPOSER AND READER through the real bundled interpreter, on a
/// document from the composer's own suite (experiments/aixml-without-grpc/tests/t01_add.xml,
/// verbatim). It needs no LabVIEW, so it is a real end-to-end check rather than a fixture; with no
/// bundle or no composer in the checkout it is a SKIP, the same contract as the other pylabview
/// tests. Every tool test also counts the RPCs the fake service saw, because "pylabview" that
/// quietly reached NI's converter would pass every other assertion here.
/// </summary>
public sealed class AixmlBackendTests
{
    private const string Add = """
        <VI _name="T01 Add.vi" description="Composed without LabVIEW: sum = a + b.">
          <Control _name="a" type="double" uid="4200" uid_parent="root" value="0" conIdx="0" connection="recommended" outputs="value:4200.value"/>
          <Control _name="b" type="double" uid="4210" uid_parent="root" value="0" conIdx="1" connection="recommended" outputs="value:4210.value"/>
          <Node _name="Add" inputs="x:4200.value,y:4210.value" outputs="x+y:4220.x+y" uid="4220" uid_parent="root"/>
          <Indicator _name="sum" type="double" uid="4230" uid_parent="root" value="0" conIdx="4" connection="recommended" inputs="value:4220.x+y"/>
        </VI>
        """;

    /// <summary>Measured: the composer answers `UNSUPPORTED: Node '...' is not a primitive this prototype knows`, exit 2.</summary>
    private const string Unbuildable = """
        <VI _name="Refuse.vi" description="A node no part exists for.">
          <Control _name="a" type="double" uid="4200" uid_parent="root" value="0" outputs="value:4200.value"/>
          <Node _name="Definitely Not A LabVIEW Primitive" inputs="x:4200.value" outputs="y:4220.y" uid="4220" uid_parent="root"/>
          <Indicator _name="r" type="double" uid="4230" uid_parent="root" value="0" inputs="value:4220.y"/>
        </VI>
        """;

    private const string Tool = "lvai_convert_aixml_to_vi";

    private static AixmlBackend.Choice Resolve(string? perCall, string? clientTool = Tool,
                                               string? flag = null, string? environment = null)
    {
        Assert.True(AixmlBackend.TryResolve(perCall, Tool, clientTool, flag, environment,
                                            out var choice, out var error), error);
        return choice;
    }

    // ------------------------------------------------------------ precedence

    [Fact]
    public void The_call_beats_the_flag_beats_the_environment_beats_the_default()
    {
        Assert.Equal(new AixmlBackend.Choice("grpc", "call"),
                     Resolve("grpc", flag: "pylabview", environment: "pylabview"));
        Assert.Equal(new AixmlBackend.Choice("pylabview", AixmlBackend.Flag),
                     Resolve(null, flag: "pylabview", environment: "grpc"));
        Assert.Equal(new AixmlBackend.Choice("pylabview", AixmlBackend.Variable),
                     Resolve(null, environment: "pylabview"));
        Assert.Equal(new AixmlBackend.Choice("grpc", "default"), Resolve(null));
    }

    /// <summary>
    /// The class tools call lvai_generate_vi from C# and depend on what only NI's converter does,
    /// so a server-wide pylabview must not reach them. Only an explicit argument does.
    /// </summary>
    [Fact]
    public void The_server_default_reaches_only_the_tool_the_client_invoked()
    {
        var nested = Resolve(null, clientTool: "lvai_create_class", environment: "pylabview");
        Assert.Equal("grpc", nested.Name);
        Assert.StartsWith("internal call", nested.Source);

        Assert.Equal("pylabview", Resolve("pylabview", clientTool: "lvai_create_class").Name);
        Assert.Equal("grpc", Resolve(null, clientTool: null, flag: "pylabview").Name);
    }

    private static AixmlBackend.Choice ResolveWith(int? labviewInUse, string? perCall = null,
                                                   string? clientTool = Tool, string? flag = null,
                                                   string? environment = null)
    {
        Assert.True(AixmlBackend.TryResolve(perCall, Tool, clientTool, flag, environment, null,
                                            () => labviewInUse, out var choice, out var error), error);
        return choice;
    }

    /// <summary>
    /// The user's rule of 2026-10-12: a LabVIEW OLDER than 2026 in use makes pylabview the default -
    /// even 2025, which has NI's service - while an explicit choice anywhere above still wins and an
    /// internal call still stays grpc.
    /// </summary>
    [Fact]
    public void An_older_LabVIEW_makes_pylabview_the_default_and_nothing_more()
    {
        var older = ResolveWith(2025);
        Assert.Equal("pylabview", older.Name);
        Assert.Contains("2025", older.Source);
        Assert.Equal("pylabview", ResolveWith(2019).Name);

        Assert.Equal(new AixmlBackend.Choice("grpc", "default"), ResolveWith(2026));
        Assert.Equal(new AixmlBackend.Choice("grpc", "default"), ResolveWith(2027));
        Assert.Equal(new AixmlBackend.Choice("grpc", "default"), ResolveWith(null));

        Assert.Equal(new AixmlBackend.Choice("grpc", "call"), ResolveWith(2025, perCall: "grpc"));
        Assert.Equal(new AixmlBackend.Choice("grpc", AixmlBackend.Flag), ResolveWith(2025, flag: "grpc"));
        Assert.Equal(new AixmlBackend.Choice("grpc", AixmlBackend.Variable), ResolveWith(2025, environment: "grpc"));
        Assert.Equal("grpc", ResolveWith(2025, clientTool: "lvai_create_class").Name);
    }

    /// <summary>
    /// The release is read only when every rule above it is silent: it costs a look at the process
    /// table, and a call that names its backend must not pay for it.
    /// </summary>
    [Fact]
    public void The_LabVIEW_release_is_read_only_when_nothing_above_decided()
    {
        var asked = 0;
        int? Count() { asked++; return 2025; }
        AixmlBackend.TryResolve("grpc", Tool, Tool, null, null, null, Count, out _, out _);
        AixmlBackend.TryResolve(null, Tool, Tool, "grpc", null, null, Count, out _, out _);
        AixmlBackend.TryResolve(null, Tool, "lvai_create_class", null, null, null, Count, out _, out _);
        Assert.Equal(0, asked);
        AixmlBackend.TryResolve(null, Tool, Tool, null, null, null, Count, out _, out _);
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// A composed VI carries the skeleton's version stamp, and LabVIEW opens no file of a later
    /// version - whether an older LabVIEW really refuses one is unmeasured, so the composer WARNS
    /// and still writes (the user's correction of 2026-10-12; it refused until then).
    /// </summary>
    [Fact]
    public void The_composer_warns_for_a_LabVIEW_older_than_its_skeleton_and_says_it_is_unmeasured()
    {
        var why = AixmlComposer.WarningForOlderLabView(2025, 2026);
        Assert.NotNull(why);
        Assert.Contains("LabVIEW 2025", why);
        Assert.Contains("LabVIEW 2026", why);
        Assert.Contains("written as a LabVIEW 2025 file", why);
        Assert.Null(AixmlComposer.WarningForOlderLabView(2026, 2026));
        Assert.Null(AixmlComposer.WarningForOlderLabView(null, 2026));
        Assert.Null(AixmlComposer.WarningForOlderLabView(2025, null));
    }

    /// <summary>
    /// LabVIEW 2025 refused every composed VI with Error 1125 until the composer wrote files AS the
    /// release in use; from 2020 on, the first release whose class-link records it writes.
    /// </summary>
    [Theory]
    [InlineData(2025, 2026, 2025)]
    [InlineData(2020, 2026, 2020)]
    [InlineData(2019, 2026, null)]
    [InlineData(2026, 2026, null)]
    [InlineData(null, 2026, null)]
    [InlineData(2025, null, null)]
    public void The_composer_writes_for_an_older_LabVIEW_from_2020_on(int? inUse, int? parts, int? expected) =>
        Assert.Equal(expected, AixmlComposer.TargetRelease(inUse, parts));

    [Fact]
    public void A_composer_warning_reaches_the_answer_and_the_VI_still_counts_as_written()
    {
        var choice = new AixmlBackend.Choice(AixmlBackend.PyLabview, "test");
        var warned = AixmlTools.ComposerAnswer(
            new AixmlComposer.Result(true, false, "No Error", [], 5, 0, Warning: "older LabVIEW"), choice, []);
        var plain = AixmlTools.ComposerAnswer(
            new AixmlComposer.Result(true, false, "No Error", [], 5, 0), choice, []);

        Assert.Equal(0, Res.Int(warned, "errorCode"));
        Assert.Equal("older LabVIEW", Res.Str(warned, "versionWarning"));
        Assert.DoesNotContain("versionWarning", plain);
    }

    /// <summary>The shipped parts are LabVIEW 2026 parts - read off the skeleton, not assumed.</summary>
    [Fact]
    public void The_shipped_parts_say_which_release_they_were_cut_from()
    {
        var parts = RepoTree.Path("scripts", AixmlComposer.ComposerFolder, "parts");
        Assert.Equal(2026, AixmlComposer.PartsRelease(parts));
        Assert.Null(AixmlComposer.PartsRelease(RepoTree.Path("scripts")));
    }

    [Fact]
    public void An_unknown_name_is_refused_and_a_known_one_is_read_loosely()
    {
        Assert.False(AixmlBackend.TryResolve("nigel", Tool, Tool, null, null, out _, out var fromCall));
        Assert.Contains("grpc, pylabview", fromCall);

        Assert.False(AixmlBackend.TryResolve(null, Tool, Tool, null, "pylv", out _, out var fromEnv));
        Assert.Contains(AixmlBackend.Variable, fromEnv);

        Assert.Equal("pylabview", Resolve(" PyLabVIEW ").Name);
    }

    [Fact]
    public void The_flag_is_known_to_the_command_line()
    {
        Assert.Empty(LabVIEWMcp.Cli.CommandLine.UnknownFlags(["--aixml-backend", "pylabview"]));
        Assert.Contains("--aixml-backend", LabVIEWMcp.Cli.CommandLine.Usage);
    }

    // ------------------------------------------------------------ the tools, grpc side

    [Fact]
    public async Task An_unknown_backend_is_refused_before_any_RPC()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        await File.WriteAllTextAsync(xml, Add);

        var result = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("add.vi"), backend: "nigel");

        Assert.Equal("unknownBackend", Res.Str(result, "errorKind"));
        Assert.Equal(0, server.Service.CountOf("ConvertAIXMLToVI"));
    }

    [Fact]
    public async Task The_grpc_route_names_itself_and_why()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        await File.WriteAllTextAsync(xml, Add);
        var tools = new AixmlTools(server.Connection);

        var named = await tools.ValidateAixmlAsync(xml, backend: "grpc");
        Assert.Equal("grpc", Res.Str(named, "backend"));
        Assert.Equal("call", Res.Str(named, "backendSource"));

        // From C#, as every internal caller reaches it: no client tool, so never the server default.
        var internalCall = await tools.ValidateAixmlAsync(xml);
        Assert.Equal("grpc", Res.Str(internalCall, "backend"));
        Assert.StartsWith("internal call", Res.Str(internalCall, "backendSource"));

        Assert.Equal(2, server.Service.CountOf("ValidateAIXML"));
    }

    // ------------------------------------------------------------ the tools, pylabview side

    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    /// <summary>
    /// Every test below SKIPS when the composer is not found, so a locator that lost it would
    /// leave this whole half green. Code and parts moved from experiments\aixml-without-grpc to
    /// scripts\aixml-pylabview on 2026-10-12 so that they ship: in a checkout with the bundle
    /// both must be found there, in the CHECKOUT (live) rather than in the build's copy - unless
    /// the environment names a parts folder of its own, which wins by design.
    /// </summary>
    [Fact]
    public void A_checkout_with_the_bundle_finds_the_composer_and_its_parts_in_scripts()
    {
        if (PyLabview.Locate() is null) return;   // no interpreter bundle in this checkout
        var setup = AixmlComposer.Locate(out var missing);
        Assert.True(setup is not null, missing);
        var composer = Path.GetFullPath(RepoTree.Path("scripts", AixmlComposer.ComposerFolder));
        Assert.Equal(composer, Path.GetFullPath(setup!.ComposerDirectory));
        if (Environment.GetEnvironmentVariable(AixmlComposer.PartsVariable) is { Length: > 0 }) return;
        Assert.Equal(Path.Combine(composer, "parts"), Path.GetFullPath(setup.PartsDirectory));
    }

    /// <summary>
    /// What ships must not name this workstation: a part's provenance is `&lt;labview&gt;\…` or a bare
    /// file name since 2026-10-12, and the harvester writes it that way. An absolute path in a
    /// part or a table is either a provenance the normaliser missed or a new harvest that did
    /// not go through it.
    /// </summary>
    [Fact]
    public void The_shipped_parts_carry_no_absolute_path()
    {
        var parts = RepoTree.Path("scripts", AixmlComposer.ComposerFolder, "parts");
        Assert.True(Directory.Exists(parts), parts);
        var absolute = new System.Text.RegularExpressions.Regex(@"[A-Za-z]:(\\\\|\\|/)[A-Za-z]");
        var offenders = Directory.EnumerateFiles(parts, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xml") || f.EndsWith(".json") || f.EndsWith(".tsv"))
            .Where(f => absolute.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0, "absolute paths in: " + string.Join(", ", offenders.Take(10)));
    }

    /// <summary>
    /// Every shipped part PARSES. Measured 2026-10-12: the provenance rewrite first wrote a raw
    /// `&lt;labview&gt;` into 856 `source` attributes, which no XML parser accepts - the absolute-path
    /// test above stayed green, and the composer died on the first such part it opened.
    /// </summary>
    [Fact]
    public void Every_shipped_part_is_well_formed()
    {
        var parts = RepoTree.Path("scripts", AixmlComposer.ComposerFolder, "parts");
        var broken = new List<string>();
        foreach (var file in Directory.EnumerateFiles(parts, "*", SearchOption.AllDirectories))
        {
            try
            {
                // from a STREAM: XDocument.Load(string) takes a URI and decodes the %2E that
                // part names use for a dot, so `…__%2ENET%2ELoad Assembly.xml` is "not found"
                if (file.EndsWith(".xml")) { using var s = File.OpenRead(file); System.Xml.Linq.XDocument.Load(s); }
                else if (file.EndsWith(".json")) System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).Dispose();
            }
            catch (Exception e) when (e is System.Xml.XmlException or System.Text.Json.JsonException)
            {
                broken.Add(Path.GetFileName(file) + ": " + e.Message);
            }
        }
        Assert.True(broken.Count == 0, string.Join("\n", broken.Take(10)));
    }

    [Fact]
    public async Task Pylabview_converts_with_no_RPC_and_writes_a_source_only_VI()
    {
        if (!ComposerHere()) return;   // no bundle or no composer in this checkout
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        var vi = server.TempPath("add.vi");
        await File.WriteAllTextAsync(xml, Add);

        var result = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");

        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.Equal("pylabview", Res.Str(result, "backend"));
        Assert.True(Res.Bool(result, "sourceOnly"));
        Assert.True(new FileInfo(vi).Length > 1000);
        Assert.False(AixmlTools.ContainsCompiledCode(vi));
        Assert.Equal(0, server.Service.CountOf("ConvertAIXMLToVI"));
        Assert.Equal(0, server.Service.CountOf("ValidateAIXML"));
    }

    [Fact]
    public async Task Pylabview_reads_the_VI_back_with_its_wiring()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        var vi = server.TempPath("add.vi");
        var back = server.TempPath("add-read.xml");
        await File.WriteAllTextAsync(xml, Add);
        var tools = new AixmlTools(server.Connection);
        await tools.ConvertAixmlToViAsync(xml, vi, checkExecutable: false, backend: "pylabview");

        var result = await tools.ConvertViToAixmlAsync(vi, back, backend: "pylabview");

        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.False(Res.Bool(result, "fromCache"));
        var text = Res.Str(result, "xml");
        Assert.Contains("_name=\"Add\"", text);
        Assert.Contains("x+y:4220.x+y", text);
        Assert.Contains("value:4220.x+y", text);
        Assert.Equal(0, server.Service.CountOf("ConvertVIToAIXML"));
    }

    [Fact]
    public async Task Pylabview_validates_by_composing_into_scratch_and_leaves_nothing_behind()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        await File.WriteAllTextAsync(xml, Add);
        var before = Directory.GetFiles(Path.GetDirectoryName(xml)!);

        var result = await new AixmlTools(server.Connection).ValidateAixmlAsync(xml, backend: "pylabview");

        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.Equal("composer", Res.Str(result, "validatedBy"));
        Assert.Equal(before, Directory.GetFiles(Path.GetDirectoryName(xml)!));
        Assert.Equal(0, server.Service.CountOf("ValidateAIXML"));
    }

    [Fact]
    public async Task A_composer_refusal_is_an_error_naming_the_node_and_writes_nothing()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("refuse.xml");
        var vi = server.TempPath("refuse.vi");
        await File.WriteAllTextAsync(xml, Unbuildable);

        var result = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");

        Assert.NotEqual(0, Res.Int(result, "errorCode"));
        Assert.Equal("composerRefused", Res.Str(result, "errorKind"));
        Assert.Contains("Definitely Not A LabVIEW Primitive", Res.Str(result, "errorMessage"));
        Assert.False(File.Exists(vi));
    }

    /// <summary>
    /// The pane step of the pylabview backend reads the FILE. Measured live the same day against
    /// NI's own probe on this VI: pattern 4833, 0 violations, 1 warning (b in a middle column) on
    /// both, the same corrected assignment.
    /// </summary>
    [Fact]
    public async Task The_pane_is_read_from_the_file_with_the_probe_verdict()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        var vi = server.TempPath("add.vi");
        await File.WriteAllTextAsync(xml, Add);
        await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");

        var verdict = await PaneTools.MeasureWithPylabviewAsync(vi, 60, default);

        Assert.Equal(4833, verdict.Pattern);
        Assert.Equal(0, verdict.Violations);
        Assert.Equal(1, verdict.Warnings);
        Assert.Contains("move it to conIdx 5", verdict.Text);
    }

    /// <summary>The composer writes no pane when no terminal has a conIdx - a reading, not a failure.</summary>
    [Fact]
    public async Task A_VI_with_no_pane_is_measured_as_having_none()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("nopane.xml");
        var vi = server.TempPath("nopane.vi");
        await File.WriteAllTextAsync(xml, Add.Replace(" conIdx=\"0\"", "").Replace(" conIdx=\"1\"", "")
                                             .Replace(" conIdx=\"4\"", ""));
        await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");

        var verdict = await PaneTools.MeasureWithPylabviewAsync(vi, 60, default);

        Assert.True(verdict.Measured);
        Assert.Equal(0, verdict.Pattern);
        Assert.Contains("NO connector pane", verdict.Text);
    }

    /// <summary>ActiveX can set and get a control by name but not list them, so the reader does.</summary>
    [Fact]
    public async Task The_panel_is_listed_from_the_file_with_its_types()
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        var vi = server.TempPath("add.vi");
        await File.WriteAllTextAsync(xml, Add);
        await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");

        var (panel, problem) = await AixmlComposer.ReadPanelAsync(setup, vi, 60, default);

        Assert.Null(problem);
        Assert.Equal(["a:double:False", "b:double:False", "sum:double:True"],
                     panel!.Select(i => $"{i.Name}:{i.Type}:{i.Indicator}").OrderBy(s => s));
    }

    /// <summary>ActiveX has no Value (Signaling); refused before LabVIEW is asked anything.</summary>
    [Fact]
    public async Task Signals_are_refused_on_the_ActiveX_route()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var vi = server.TempPath("any.vi");
        await File.WriteAllBytesAsync(vi, [0]);

        var result = await new RunTools(server.Connection).RunViAndReadValuesAsync(
            vi, runForMs: 300, signalsJson: """[{"control":"Stop","value":"true"}]""",
            backend: "pylabview");

        Assert.Equal("notAvailableOnBackend", Res.Str(result, "errorKind"));
        Assert.Equal("pylabview", Res.Str(result, "backend"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    /// <summary>
    /// The server's own helpers on the pylabview backend: composed once into their own folder,
    /// reused while neither their AIXML nor the parts change, and a helper that does not exist is
    /// a named problem, not a throw. lvai_active_project composes with the parts as committed.
    /// </summary>
    [Fact]
    public async Task A_helper_is_composed_once_and_reused()
    {
        if (!ComposerHere()) return;

        var (first, problem) = await ComposedHelpers.EnsureAsync("lvai_active_project.xml", 120, default);
        Assert.True(first is not null, problem);
        var written = File.GetLastWriteTimeUtc(first!);

        var (second, _) = await ComposedHelpers.EnsureAsync("lvai_active_project.xml", 120, default);
        Assert.Equal(first, second);
        Assert.Equal(written, File.GetLastWriteTimeUtc(second!));
        Assert.False(AixmlTools.ContainsCompiledCode(first!));

        var (none, why) = await ComposedHelpers.EnsureAsync("lvai_no_such_helper.xml", 120, default);
        Assert.Null(none);
        Assert.Contains("lvai_no_such_helper.xml", why);
    }

    /// <summary>On pylabview lvai_open_file opens projects only; a VI is refused before LabVIEW is asked.</summary>
    [Fact]
    public async Task Opening_a_VI_is_refused_on_the_ActiveX_route()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var vi = server.TempPath("any.vi");
        await File.WriteAllBytesAsync(vi, [0]);

        var result = await new ActionTools(server.Connection).OpenFileAsync(viPath: vi, backend: "pylabview");

        Assert.Equal("notAvailableOnBackend", Res.Str(result, "errorKind"));
        Assert.Equal("pylabview", Res.Str(result, "backend"));
        Assert.Equal(0, server.Service.CountOf("OpenFile"));
    }

    [Fact]
    public async Task Generate_vi_on_pylabview_skips_NI_validation_and_names_the_backend()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("add.xml");
        var vi = server.TempPath("add.vi");
        await File.WriteAllTextAsync(xml, Add);

        var result = await new BulkTools(server.Connection).GenerateViAsync(
            xml, vi, measurePane: false, measureDiagram: false, backend: "pylabview");

        Assert.Equal("pylabview", Res.Str(result, "backend"));
        var steps = Res.Arr(result, "steps");
        var validate = Assert.Single(steps, s => s?["step"]?.GetValue<string>() == "validate");
        Assert.True(validate!["skipped"]!.GetValue<bool>());
        Assert.True(File.Exists(vi));
        Assert.Equal(0, server.Service.CountOf("ValidateAIXML"));
    }
}
