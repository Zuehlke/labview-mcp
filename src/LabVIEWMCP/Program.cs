using LabVIEWMcp.Cli;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// LabVIEW MCP - exposes LabVIEW's private lvai.LVAI gRPC interface as MCP tools.
//
// The modes and flags are listed in CommandLine.Usage, which --help prints, so this file
// and the help text cannot drift apart.

if (CommandLine.HasFlag(args, "--help") ||
    CommandLine.HasFlag(args, "-h") ||
    CommandLine.HasFlag(args, "-?"))
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

// Reject a mistyped flag instead of falling through to the stdio server below. That server
// waits on stdin forever, so "-selftest" with one hyphen looked exactly like a hang and was
// reported as one (issue #7). Usage goes to stderr: stdout belongs to the MCP protocol.
if (CommandLine.UnknownFlags(args) is { Count: > 0 } unknownFlags)
{
    foreach (var token in unknownFlags)
    {
        var suggestion = CommandLine.Suggest(token);
        Console.Error.WriteLine(suggestion is null
            ? $"Unknown option: {token}"
            : $"Unknown option: {token} - did you mean {suggestion}?");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

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

if (CommandLine.HasFlag(args, "--examples"))
    return Examples.Run(CommandLine.StringArg(args, "--examples"),
        CommandLine.IntArg(args, "--limit"),
        CommandLine.HasFlag(args, "--include-specialised"),
        CommandLine.HasFlag(args, "--refresh"));

if (CommandLine.HasFlag(args, "--corpus"))
    return await Corpus.RunAsync(portOverride, CommandLine.StringArg(args, "--corpus"),
        CommandLine.StringArg(args, "--out"), CommandLine.IntArg(args, "--limit"),
        CommandLine.IntArg(args, "--timeout") ?? 90, CommandLine.StringArg(args, "--skip"),
        CommandLine.IntArg(args, "--restart-every") ?? 40);

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

// Build the example index before anyone asks for it. MEASURED: a cold scan costs 55 seconds and
// the in-memory index does not outlive the process, so without this the first lvai_example_index
// call after every restart is a 55-second silence - long enough to read as a hang. The result is
// also written to disk, so this only actually scans on a machine that has never done it, or after
// an explicit refresh. Fire-and-forget on purpose: a machine with no LabVIEW must still serve
// every other tool.
_ = ExampleIndex.WarmAsync();

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
