using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Making sure LabVIEW is up, so the other tools have something to talk to.
///
/// Every lvai_* tool needs a running LabVIEW: the gRPC server lives inside LabVIEW.exe. This
/// starts one when none is running, choosing the installation without knowing any version
/// number in advance (see <see cref="LabViewLocator"/>).
///
/// Deliberately a separate, explicit tool rather than something the connection does on its
/// own. Launching an IDE is a heavyweight, visible side effect, and LabVIEW needs far longer
/// to become answerable than the MCP client is willing to wait for a tool call - so an
/// implicit auto-start would turn every read into a minute-long gamble.
/// </summary>
[McpServerToolType]
internal sealed class LifecycleTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [McpServerTool(Name = "lvai_ensure_labview", Destructive = true, OpenWorld = true,
                   Title = "Make sure a LabVIEW instance is running")]
    [Description("""
        Start a LabVIEW instance. If one is already running it is used as-is and nothing is
        started. Otherwise the newest installed LabVIEW is launched, preferring the 32-bit build
        - that is the one hosting the AI gRPC service. No version number is hardcoded; LabVIEW
        NXG is ignored.
        MUTATING: may start an IDE, which is visible to whoever is at the machine.
        IT CANNOT FINISH THE JOB ALONE. Measured: the 'LV AI gRPC Service' starts with NIGEL,
        the AI assistant - not with the IDE. A LabVIEW that has been up for twenty minutes can
        hold 30 open listener ports and serve lvai.LVAI on none of them. So a persistent
        'starting' with LabVIEW visibly running is not this tool being slow: ask whoever is at
        the machine to open Nigel, then call lvai_status once.
        Otherwise, when LabVIEW is still coming up, calling again is right - the second call
        finds the instance and waits. For an unattended long wait use the CLI:
        LabVIEWMCP --ensure-labview.
        """)]
    public async Task<string> EnsureLabViewAsync(
        [Description("How long to wait for the gRPC service to answer, in seconds (capped at 45)")]
        int waitSeconds = 40,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var budget = Rpc.ClampToolWait(waitSeconds);
            var running = LabViewLocator.RunningInstances();
            var startedNow = false;
            string? launched = null;

            if (running.Count == 0)
            {
                var installs = LabViewLocator.Discover();
                var pick = LabViewLocator.Select(installs);
                if (pick is null)
                    return Json.Error("no-labview",
                        "No LabVIEW installation found under the program-files roots.", new
                        {
                            searched = "…\\National Instruments\\LabVIEW*\\LabVIEW.exe",
                            // Naming the roots turns "found nothing" into something diagnosable:
                            // an empty or unexpected list points at the environment, not at LabVIEW.
                            rootsProbed = LabViewLocator.ProgramFilesRoots(),
                            note = "LabVIEW NXG is excluded - it does not host the lvai service.",
                        });

                if (LabViewLocator.Start(pick) is null)
                    return Json.Error("start-failed", $"Could not start {pick.ExePath}.");

                startedNow = true;
                launched = pick.Describe();
            }

            // Poll until the service answers. A cold LabVIEW start is far slower than this
            // budget, hence the explicit "call again" outcome rather than a failure.
            var stopwatch = Stopwatch.StartNew();
            var deadline = TimeSpan.FromSeconds(budget);
            Exception? last = null;

            while (stopwatch.Elapsed < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    connection.Invalidate();   // the port changes with every LabVIEW start
                    var client = await connection.GetClientAsync(ct);
                    var config = await client.GetApplicationConfigurationAsync(
                        new GetApplicationConfigurationRequest(),
                        deadline: Rpc.Deadline(10), cancellationToken: ct);

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["state"] = "ready",
                        ["startedByThisCall"] = startedNow,
                        ["launched"] = launched,
                        ["port"] = connection.Port,
                        ["discoveredVia"] = connection.DiscoveredVia,
                        ["applicationLanguage"] = config.Language,
                        ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                    }.ToJsonString(Indented);
                }
                catch (Exception e)
                {
                    last = e;
                    await Task.Delay(2000, ct);
                }
            }

            return new JsonObject
            {
                ["ok"] = false,
                ["state"] = "starting",
                ["startedByThisCall"] = startedNow,
                ["launched"] = launched,
                ["runningProcesses"] = LabViewLocator.RunningInstances().Count,
                ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                ["lastError"] = last?.Message,
                ["next"] = "LabVIEW is up but its AI gRPC service is not answering yet. " +
                           "Call this tool again, or use the CLI --ensure-labview for a long wait.",
            }.ToJsonString(Indented);
        });

    [McpServerTool(Name = "lvai_list_labview_installations", ReadOnly = true,
                   Title = "List installed LabVIEW versions")]
    [Description("""
        List the LabVIEW installations found on this machine and which one would be started,
        without starting anything. Bitness is read from each executable's PE header rather than
        guessed from its folder. Useful to check the choice before calling lvai_ensure_labview.
        """)]
    public static string ListInstallations()
    {
        var installs = LabViewLocator.Discover();
        var pick = LabViewLocator.Select(installs);
        var running = LabViewLocator.RunningInstances();

        var list = new JsonArray();
        foreach (var i in installs.OrderByDescending(i => i.Release).ThenByDescending(i => i.Is32Bit))
            list.Add(new JsonObject
            {
                ["folder"] = i.FolderName,
                ["release"] = i.Release,
                ["bitness"] = i.Is32Bit ? "32-bit" : "64-bit",
                ["exe"] = i.ExePath,
                ["wouldBeStarted"] = pick is not null && pick.ExePath == i.ExePath,
            });

        // The roots are reported alongside the result: an empty or unexpected list is the
        // signature of a scrubbed environment rather than of a missing LabVIEW.
        var rootsProbed = new JsonArray();
        foreach (var root in LabViewLocator.ProgramFilesRoots())
            rootsProbed.Add(root);

        var runningPaths = new JsonArray();
        foreach (var p in running)
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* access denied is fine */ }
            runningPaths.Add(new JsonObject { ["pid"] = p.Id, ["path"] = path });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["installationCount"] = installs.Count,
            ["rootsProbed"] = rootsProbed,
            ["alreadyRunning"] = runningPaths,
            ["selection"] = pick?.Describe(),
            ["rule"] = "newest release first, 32-bit preferred within a release, NXG excluded",
            ["installations"] = list,
        }.ToJsonString(Indented);
    }
}
