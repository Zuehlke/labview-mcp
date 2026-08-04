using System.Diagnostics;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Start LabVIEW if needed and wait — properly — until its AI gRPC service answers.
///
/// The MCP tool of the same purpose is capped at 45 s because the client kills longer calls.
/// A cold LabVIEW start regularly exceeds that, so the unattended path lives here, where
/// nobody is waiting on a protocol timeout.
///
/// Usage:
///   LabVIEWMCP --ensure-labview [--timeout 300]
/// </summary>
internal static class EnsureLabView
{
    public static async Task<int> RunAsync(int? port, int timeoutSeconds)
    {
        Console.WriteLine("LabVIEW readiness");
        Console.WriteLine("=================");

        var running = LabViewLocator.RunningInstances();
        if (running.Count > 0)
        {
            Console.WriteLine($"  {running.Count} LabVIEW process(es) already running - using those.");
        }
        else
        {
            var installs = LabViewLocator.Discover();
            Console.WriteLine($"  {installs.Count} installation(s) found:");
            foreach (var i in installs.OrderByDescending(i => i.Release).ThenByDescending(i => i.Is32Bit))
                Console.WriteLine($"     {i.Describe(),-28} {i.ExePath}");

            var pick = LabViewLocator.Select(installs);
            if (pick is null)
            {
                Console.Error.WriteLine("  No LabVIEW installation found (NXG does not count).");
                return 1;
            }

            Console.WriteLine($"  starting {pick.Describe()} ...");
            if (LabViewLocator.Start(pick) is null)
            {
                Console.Error.WriteLine($"  could not start {pick.ExePath}");
                return 1;
            }
        }

        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;

        var budget = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 3600));
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"  waiting up to {budget.TotalSeconds:0}s for the AI gRPC service ...");

        while (stopwatch.Elapsed < budget)
        {
            try
            {
                connection.Invalidate();       // the port is new on every LabVIEW start
                var client = await connection.GetClientAsync();
                await client.GetApplicationConfigurationAsync(
                    new GetApplicationConfigurationRequest(), deadline: Rpc.Deadline(10));

                Console.WriteLine();
                Console.WriteLine($"  READY after {stopwatch.Elapsed.TotalSeconds:0.0}s " +
                                  $"- port {connection.Port} (via {connection.DiscoveredVia})");
                return 0;
            }
            catch
            {
                Console.Write(".");
                await Task.Delay(2000);
            }
        }

        Console.WriteLine();
        Console.Error.WriteLine($"  still not answering after {budget.TotalSeconds:0}s.");
        Console.Error.WriteLine("  LabVIEW may be showing a dialog, or the AI feature is not active.");
        return 1;
    }
}
