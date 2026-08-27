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
    public static async Task<int> RunAsync(int? port, int timeoutSeconds, bool keepAutoSave = false)
    {
        Console.WriteLine("LabVIEW readiness");
        Console.WriteLine("=================");

        var running = LabViewLocator.RunningInstances();
        if (running.Count > 0)
        {
            Console.WriteLine($"  {running.Count} LabVIEW process(es) already running - using those.");
            Console.WriteLine("  auto-save store left alone - only cleared before a start we make.");
        }
        else
        {
            // BEFORE the launch, never after: leftover auto-save data makes LabVIEW raise a
            // recovery dialog on start, and a modal dialog stops the whole gRPC service until a
            // human dismisses it. Clearing it afterwards would be too late to prevent that.
            if (keepAutoSave)
            {
                Console.WriteLine("  --keep-autosave: recovery store left as it is.");
            }
            else
            {
                var cleared = AutoSaveRecovery.Clear();
                Console.WriteLine($"  {cleared.Describe()}");
                foreach (var failure in cleared.Failures)
                    Console.WriteLine($"     could not delete {failure}");
            }

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

            // Same launcher as the MCP tool: strategies tried in order, each judged by whether a
            // LabVIEW process is still alive afterwards rather than by the launch call's return.
            var launch = await LabViewLauncher.StartAndConfirmAsync(pick);
            foreach (var attempt in launch.Attempts)
                Console.WriteLine(
                    $"     {attempt.Method,-10} started={attempt.Started,-5} " +
                    $"appeared={attempt.Appeared,-5} survived={attempt.Survived,-5} {attempt.Detail}");

            if (!launch.Ok)
            {
                Console.Error.WriteLine("  no LabVIEW process stayed alive - see the attempts above.");
                Console.Error.WriteLine("  a process that vanishes within a second is being terminated");
                Console.Error.WriteLine("  from outside; start LabVIEW by hand and re-run.");
                return 1;
            }

            Console.WriteLine($"  launched via '{launch.Method}' (pid {launch.Pid}).");
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
