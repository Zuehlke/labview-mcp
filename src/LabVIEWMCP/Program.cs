using LabVIEWMcp.Cli;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// LabVIEW MCP - exposes LabVIEW's private lvai.LVAI gRPC interface as MCP tools.
//
//   LabVIEWMCP                        run as an MCP server over stdio (default)
//   LabVIEWMCP --selftest             probe every read-only RPC and print a verdict table
//   LabVIEWMCP --dump-schema [file]   print/write the schema the running LabVIEW serves
//   LabVIEWMCP --watch <monitor>      wait for inbound LabVIEW events, minutes at a time
//
//   --port <n>        pin LabVIEW's gRPC port instead of discovering it
//   --vi <path>       VI used by --selftest (defaults to a shipped LabVIEW example)
//   --project <path>  .lvproj used by --selftest
//   --timeout <s>     how long --watch waits (default 300)

var portOverride = CommandLine.IntArg(args, "--port");

if (CommandLine.HasFlag(args, "--selftest"))
    return await SelfTest.RunAsync(
        portOverride, CommandLine.StringArg(args, "--vi"), CommandLine.StringArg(args, "--project"));

if (CommandLine.HasFlag(args, "--dump-schema"))
    return await DumpSchemaAsync(portOverride, CommandLine.StringArg(args, "--dump-schema"));

if (CommandLine.HasFlag(args, "--watch"))
    return await Watch.RunAsync(portOverride, CommandLine.StringArg(args, "--watch"),
        CommandLine.IntArg(args, "--timeout") ?? 300);

if (CommandLine.HasFlag(args, "--diagram"))
    return await Diagram.RunAsync(portOverride, CommandLine.StringArg(args, "--diagram"),
        CommandLine.StringArg(args, "--out"));

if (CommandLine.HasFlag(args, "--ensure-labview"))
    return await EnsureLabView.RunAsync(portOverride, CommandLine.IntArg(args, "--timeout") ?? 300);

// ---- default: MCP server over stdio ----
var builder = Host.CreateApplicationBuilder(args);

// stdout belongs to the MCP protocol. Every log line must go to stderr or the
// transport gets corrupted by our own diagnostics.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(serviceProvider => new LvaiConnection(
    serviceProvider.GetRequiredService<ILogger<LvaiConnection>>(), portOverride));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "labview-mcp", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
return 0;

static async Task<int> DumpSchemaAsync(int? port, string? outputPath)
{
    var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
    await using var _ = connection;

    var result = await new StatusTools(connection).DumpSchemaAsync("summary", outputPath);
    Console.WriteLine(result);
    return result.Contains("\"ok\": false") ? 1 : 0;
}
