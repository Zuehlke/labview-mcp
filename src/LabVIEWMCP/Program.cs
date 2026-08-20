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

if (CommandLine.HasFlag(args, "--palette"))
    return Palette.Run(CommandLine.StringArg(args, "--palette"),
        CommandLine.IntArg(args, "--limit"),
        CommandLine.HasFlag(args, "--refresh"));

if (CommandLine.HasFlag(args, "--pane"))
    return await Panes.RunOneAsync(portOverride, CommandLine.StringArg(args, "--pane"));

if (CommandLine.HasFlag(args, "--panes"))
    return Panes.Run(CommandLine.StringArg(args, "--panes"), CommandLine.StringArg(args, "--out"));

if (CommandLine.HasFlag(args, "--corpus"))
    return await Corpus.RunAsync(portOverride, CommandLine.StringArg(args, "--corpus"),
        CommandLine.StringArg(args, "--out"), CommandLine.IntArg(args, "--limit"),
        CommandLine.IntArg(args, "--timeout") ?? 90, CommandLine.StringArg(args, "--skip"),
        CommandLine.IntArg(args, "--restart-every") ?? 40);

if (CommandLine.HasFlag(args, "--ensure-labview"))
    return await EnsureLabView.RunAsync(portOverride, CommandLine.IntArg(args, "--timeout") ?? 300);

if (CommandLine.HasFlag(args, "--pylv-status"))
    return PyLabviewCli.Status();

if (CommandLine.HasFlag(args, "--pylv-extract"))
    return await PyLabviewCli.ExtractAsync(CommandLine.StringArg(args, "--pylv-extract"),
        CommandLine.StringArg(args, "--out"), !CommandLine.HasFlag(args, "--no-annotate"));

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
    .WithResourcesFromAssembly()
    // LAST, and it has to be: it wraps the tool registrations the line above just added. Without
    // it a misspelled or missing argument answers "An error occurred invoking 'lvai_describe_vi'."
    // and nothing else - our own masking, measured, not the client's. See Infra/ToolArguments.cs.
    .WithArgumentDiagnostics();

// Has NI's AI add-on been upgraded since last time? The AIXML export cache is keyed on the source
// VI, which cannot see that the GENERATOR changed - so an add-on upgrade would leave entries built
// by the previous one, and a stale export is wrong rather than slow. Checked from disk, before the
// index warms and before any tool can serve a hit, and it needs no running LabVIEW.
{
    var log = LoggerFactory.Create(b =>
    {
        b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        b.SetMinimumLevel(LogLevel.Information);
    }).CreateLogger("lvai-version");

    // Before anything reads the cache: bring one left in the old %LOCALAPPDATA% location across.
    // Starting cold instead would cost a silent 55-second example scan on the next first call.
    if (CacheDirectory.MigrateLegacy() is > 0 and var movedFiles)
        log.LogInformation("Moved {Count} cache file(s) from {From} to {To}.",
            movedFiles, CacheDirectory.LegacyRoot, CacheDirectory.Root);

    var verdict = LvaiVersion.Check();

    if (verdict.Changed) log.LogWarning("{Message}", verdict.Describe());
    else log.LogInformation("{Message}", verdict.Describe());

    // Sweep debris a killed or racing writer left in the export cache. Not needed for correctness -
    // half an entry already reads as a miss - but a cache directory full of .tmp files makes a
    // working cache look broken to whoever opens it.
    if (AixmlExportStore.Reap() is > 0 and var reaped)
        log.LogInformation("Reaped {Count} stray file(s) from the AIXML export cache.", reaped);
}

// Build the example index before anyone asks for it. MEASURED: a cold scan costs 55 seconds and
// the in-memory index does not outlive the process, so without this the first lvai_example_index
// call after every restart is a 55-second silence - long enough to read as a hang. The result is
// also written to disk, so this only actually scans on a machine that has never done it, or after
// an explicit refresh. Fire-and-forget on purpose: a machine with no LabVIEW must still serve
// every other tool.
_ = ExampleIndex.WarmAsync();

// Same treatment for the palette index. It reads a comparably large tree - 582 palette files on
// this station - and until it got a disk cache it was rescanned on every single start-up, which
// nothing ever argued for; the example index had a measurement behind it and this one did not.
_ = PaletteIndex.WarmAsync();

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
