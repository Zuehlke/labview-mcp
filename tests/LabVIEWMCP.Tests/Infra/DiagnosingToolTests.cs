using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// That the wrapper actually reaches every served tool.
///
/// This is the half that cannot be argued from the schema: WithArgumentDiagnostics rewrites the
/// service descriptors that WithToolsFromAssembly left behind, so it depends on the SDK registering
/// tools as McpServerTool services. If a future SDK version registers them some other way, the loop
/// finds nothing and the server quietly goes back to answering "An error occurred invoking '...'".
/// That regression is invisible in production and obvious here.
/// </summary>
public class DiagnosingToolTests
{
    private static ServiceProvider ServedTools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The tools take a connection; nothing here connects, and the port is never discovered
        // because no call is made.
        services.AddSingleton(provider => new LvaiConnection(
            provider.GetRequiredService<ILogger<LvaiConnection>>(), 1));

        services
            .AddMcpServer()
            // Explicitly the SERVER assembly: the argument-less overload would scan this test
            // assembly instead and register nothing.
            .WithToolsFromAssembly(typeof(InspectTools).Assembly)
            .WithArgumentDiagnostics();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Every_served_tool_is_wrapped()
    {
        using var provider = ServedTools();

        var tools = provider.GetServices<McpServerTool>().ToList();

        // 41 tools on 2026-08-14. Asserted as a floor rather than an equality so that adding a tool
        // is not a test failure, while losing the registration wholesale still is.
        Assert.True(tools.Count >= 41, $"only {tools.Count} tools registered");
        Assert.All(tools, tool => Assert.IsType<DiagnosingTool>(tool));
    }

    /// <summary>
    /// The wrapper reads the inner tool's name and schema to build its report, so the delegation of
    /// ProtocolTool is load-bearing rather than incidental.
    /// </summary>
    [Fact]
    public void The_wrapper_still_serves_the_inner_name_and_schema()
    {
        using var provider = ServedTools();

        var describe = provider.GetServices<McpServerTool>()
            .Single(tool => tool.ProtocolTool.Name == "lvai_describe_vi");

        var (properties, required) = ToolArguments.Shape(describe.ProtocolTool.InputSchema);
        Assert.Contains("viPath", properties);
        Assert.Equal(["viPath"], required);
    }
}
