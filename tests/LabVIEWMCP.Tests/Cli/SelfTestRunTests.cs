using LabVIEWMcp.Cli;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Grpc;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

/// <summary>
/// Covers the --selftest entry point end to end. It is the first thing anyone runs, so its
/// exit code has to be trustworthy: 0 only when every probed tool really answered.
/// </summary>
public class SelfTestRunTests
{
    [Fact]
    public async Task Succeeds_against_a_responsive_server_and_probes_every_read_only_tool()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = """<VI _name="Fake.vi"/>""";
        var vi = server.TempPath("Fake.vi");
        await File.WriteAllTextAsync(vi, "vi");

        var exitCode = await SelfTest.RunAsync(server.Port, vi, @"C:\p\App.lvproj");

        Assert.Equal(0, exitCode);
        Assert.Equal(1, server.Service.CountOf("GetDescribeVIPromptInfo"));
        Assert.Equal(1, server.Service.CountOf("ConvertVIToAIXML"));
        Assert.Equal(1, server.Service.CountOf("ValidateAIXML"));
        Assert.Equal(1, server.Service.CountOf("SearchInfoCache"));
        Assert.Equal(1, server.Service.CountOf("GetDescribeProjectPromptInfo"));
        Assert.Equal(1, server.Service.CountOf("FilterExampleSearchCandidates"));
    }

    [Fact]
    public async Task Never_calls_a_mutating_rpc()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var vi = server.TempPath("Fake.vi");
        await File.WriteAllTextAsync(vi, "vi");

        await SelfTest.RunAsync(server.Port, vi, @"C:\p\App.lvproj");

        foreach (var mutating in new[]
                 {
                     "ConvertAIXMLToVI", "ApplyAIXMLToVI", "RunVIAsTopLevel",
                     "BuildFromBuildSpecification", "OpenFile", "FindPaletteItem",
                     "DropPaletteItem", "LogUsageData",
                 })
            Assert.Equal(0, server.Service.CountOf(mutating));
    }

    [Fact]
    public async Task Skips_the_vi_scoped_probes_when_no_vi_is_available()
    {
        await using var server = await LvaiTestServer.StartAsync();

        // A path that does not exist is still honoured - the point is that the tool is asked
        // and its answer reported, not that the file is real.
        var exitCode = await SelfTest.RunAsync(server.Port, null, null);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, server.Service.CountOf("GetDescribeProjectPromptInfo"));
    }

    [Fact]
    public async Task Fails_fast_with_exit_code_one_when_the_server_is_unreachable()
    {
        var exitCode = await SelfTest.RunAsync(PortDiscoveryTests.FindFreePort(), null, null);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Reports_a_failing_rpc_as_a_non_zero_exit_code()
    {
        await using var server = await LvaiTestServer.StartAsync();
        // Qualified globally: the LabVIEWMcp.Tests.Grpc using would otherwise shadow Grpc.Core.
        server.Service.FailWith = global::Grpc.Core.StatusCode.Internal;
        server.Service.FailOnMethod = "SearchInfoCache";

        var exitCode = await SelfTest.RunAsync(server.Port, null, null);

        Assert.Equal(1, exitCode);
    }
}
