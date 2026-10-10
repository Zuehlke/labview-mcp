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
