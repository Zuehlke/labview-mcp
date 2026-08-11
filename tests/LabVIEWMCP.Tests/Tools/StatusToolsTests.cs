using Grpc.Core;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class StatusToolsStatusTests
{
    [Fact]
    public async Task Reports_the_endpoint_language_and_service_list()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.Language = "German";

        var result = await new StatusTools(server.Connection).StatusAsync();

        Assert.True(Res.Bool(result, "ok"));
        Assert.Equal(server.Port, Res.Int(result, "port"));
        Assert.Equal($"http://127.0.0.1:{server.Port}", Res.Str(result, "address"));
        Assert.Equal("explicit override", Res.Str(result, "discoveredVia"));
        Assert.Equal("German", Res.Str(result, "applicationLanguage"));
        Assert.Contains("lvai.LVAI",
            Res.Arr(result, "services").Select(n => n!.GetValue<string>()));
        Assert.True(Res.IsNull(result, "reflectionError"));
    }

    [Fact]
    public async Task Still_succeeds_when_the_server_has_no_reflection()
    {
        // Reflection is a nice-to-have; losing it must not make the status tool fail.
        await using var server = await LvaiTestServer.StartAsync(withReflection: false);

        var result = await new StatusTools(server.Connection).StatusAsync();

        Assert.True(Res.Bool(result, "ok"));
        Assert.Empty(Res.Arr(result, "services"));
        Assert.False(Res.IsNull(result, "reflectionError"));
    }

    [Fact]
    public async Task Reports_an_unreachable_server_as_data()
    {
        var connection = new LabVIEWMcp.Grpc.LvaiConnection(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LabVIEWMcp.Grpc.LvaiConnection>.Instance,
            Grpc.PortDiscoveryTests.FindFreePort());
        await using var _ = connection;

        var result = await new StatusTools(connection).StatusAsync();

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("InvalidOperationException", Res.Str(result, "errorKind"));
    }
}

public class StatusToolsApplicationConfigurationTests
{
    [Fact]
    public async Task Returns_the_language_reported_by_the_server()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.Language = "Japanese";

        var result = await new StatusTools(server.Connection).GetApplicationConfigurationAsync();

        Assert.Equal("Japanese", Res.Str(result, "language"));
    }

    [Fact]
    public async Task An_empty_language_is_still_rendered_as_a_field()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.Language = "";

        var result = await new StatusTools(server.Connection).GetApplicationConfigurationAsync();

        Assert.True(Res.Has(result, "language"));
        Assert.Equal("", Res.Str(result, "language"));
    }

    [Fact]
    public async Task An_rpc_failure_becomes_a_structured_error_with_a_hint()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();          // probe first, then break the RPC
        server.Service.FailWith = StatusCode.Unimplemented;
        server.Service.FailOnMethod = "GetApplicationConfiguration";

        var result = await new StatusTools(server.Connection).GetApplicationConfigurationAsync();

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("rpc", Res.Str(result, "errorKind"));
        Assert.Contains("lvai_dump_schema", Res.Obj(result)["detail"]!["hint"]!.GetValue<string>());
    }
}

public class StatusToolsDumpSchemaTests
{
    [Fact]
    public async Task Summary_lists_the_service_and_all_its_rpcs()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new StatusTools(server.Connection).DumpSchemaAsync();

        Assert.True(Res.Bool(result, "ok"));
        Assert.Equal(1, Res.Int(result, "fileCount"));

        var schema = Res.Str(result, "schema");
        Assert.Contains("service LVAI {   // 23 rpcs", schema);
        Assert.Contains("rpc ConvertVIToAIXML(", schema);
        Assert.Contains("returns (stream GetDescribeVIPromptInfoResponse)", schema);
        Assert.Contains("rpc MonitorCodeCompletion(stream MonitorCodeCompletionRequest)", schema);
    }

    [Fact]
    public async Task Summary_skips_the_stock_grpc_services()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var schema = Res.Str(await new StatusTools(server.Connection).DumpSchemaAsync(), "schema");

        Assert.DoesNotContain("ServerReflection", schema);
    }

    [Fact]
    public async Task Json_format_returns_raw_descriptors()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var schema = Res.Str(await new StatusTools(server.Connection).DumpSchemaAsync("json"), "schema");

        Assert.StartsWith("[", schema);
        Assert.Contains("lvai_grpc_interface.proto", schema);
        Assert.Contains("messageType", schema);
    }

    [Fact]
    public async Task Format_matching_is_case_insensitive()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var schema = Res.Str(await new StatusTools(server.Connection).DumpSchemaAsync("JSON"), "schema");

        Assert.StartsWith("[", schema);
    }

    [Fact]
    public async Task An_unknown_format_falls_back_to_the_summary()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var schema = Res.Str(
            await new StatusTools(server.Connection).DumpSchemaAsync("nonsense"), "schema");

        Assert.Contains("service LVAI", schema);
    }

    [Fact]
    public async Task Writes_the_rendering_to_disk_when_asked()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var path = server.TempPath("schema.txt");

        var result = await new StatusTools(server.Connection).DumpSchemaAsync("summary", path);

        Assert.Equal(Path.GetFullPath(path), Res.Str(result, "writtenTo"));
        Assert.Contains("service LVAI", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Reports_no_output_file_when_none_was_requested()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new StatusTools(server.Connection).DumpSchemaAsync();

        Assert.True(Res.IsNull(result, "writtenTo"));
    }

    [Fact]
    public async Task Without_reflection_the_dump_fails_as_data()
    {
        await using var server = await LvaiTestServer.StartAsync(withReflection: false);

        var result = await new StatusTools(server.Connection).DumpSchemaAsync();

        Assert.False(Res.Bool(result, "ok"));
    }
}
