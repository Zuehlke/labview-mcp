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
        The launch is VERIFIED, not assumed: a LabVIEW created as a child of this server is
        killed by the host's job object within about half a second, so the tool tries several
        launch routes and keeps only one whose process is still alive two seconds later. The
        winner is reported as launchMethod; if none survives you get errorKind
        'launch-did-not-survive' listing every attempt, never a hopeful 'starting'.
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
            var budget = TimeSpan.FromSeconds(Rpc.ClampToolWait(waitSeconds));
            var stopwatch = Stopwatch.StartNew();

            // The budget covers the WHOLE call - launch and poll alike - and it is enforced by a
            // token, not by a loop condition. It used to be a loop condition only, and that is not
            // the same thing: one port-discovery pass can take tens of seconds, so the deadline was
            // checked, a fresh pass started, and the call ran long past its promise. Measured
            // waitedMs of 53 812 and 47 242 against a nominal 45 000 - and past roughly 60 s the MCP
            // client stops waiting, answers "Request timed out", and the caller learns nothing at all.
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(budget);
            var token = budgetCts.Token;

            var running = LabViewLocator.RunningInstances();
            var startedNow = false;
            string? launched = null;
            LaunchOutcome? launch = null;

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

                // Not "did the call return without error" but "is a LabVIEW still alive" - see
                // LabViewLauncher for why those differ by about 500 ms.
                launch = await LabViewLauncher.StartAndConfirmAsync(pick, token);
                if (!launch.Ok)
                    return Json.Error("launch-did-not-survive",
                        $"Every launch strategy was tried for {pick.Describe()} and no LabVIEW " +
                        "process stayed alive.", new
                        {
                            exe = pick.ExePath,
                            attempts = launch.Attempts,
                            note = "A process that appears and vanishes within a second is being " +
                                   "terminated from outside - normally a job object this server " +
                                   "belongs to. Starting LabVIEW by hand is the workaround; the " +
                                   "attempts above say which routes were refused.",
                        });

                startedNow = true;
                launched = pick.Describe();
            }

            // Poll until the service answers. A cold LabVIEW start is far slower than this
            // budget, hence the explicit "call again" outcome rather than a failure.
            Exception? last = null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    connection.Invalidate();   // the port changes with every LabVIEW start
                    var client = await connection.GetClientAsync(token);
                    var config = await client.GetApplicationConfigurationAsync(
                        new GetApplicationConfigurationRequest(),
                        deadline: Rpc.Deadline(10), cancellationToken: token);

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["state"] = "ready",
                        ["startedByThisCall"] = startedNow,
                        ["launched"] = launched,
                        ["launchMethod"] = launch?.Method,
                        ["port"] = connection.Port,
                        ["discoveredVia"] = connection.DiscoveredVia,
                        ["applicationLanguage"] = config.Language,
                        ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                    }.ToJsonString(Indented);
                }
                // The caller's own cancellation must still escape; only the budget is absorbed.
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    last = e;
                    try { await Task.Delay(2000, token); }
                    catch (OperationCanceledException) { /* budget spent - the loop ends below */ }
                }
            }

            ct.ThrowIfCancellationRequested();

            var stillRunning = LabViewLocator.RunningInstances().Count;
            return new JsonObject
            {
                ["ok"] = false,
                ["state"] = "starting",
                ["startedByThisCall"] = startedNow,
                ["launched"] = launched,
                ["launchMethod"] = launch?.Method,
                ["runningProcesses"] = stillRunning,
                ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                ["lastError"] = last?.Message,
                // "LabVIEW is up" was asserted unconditionally here, and it was wrong exactly when
                // it mattered: with runningProcesses 0 the advice sent the reader after the AI
                // service while the IDE itself was gone.
                ["next"] = stillRunning == 0
                    ? "No LabVIEW process is running any more - it was started and did not stay up. " +
                      "Start LabVIEW by hand and call this tool again; the README's Troubleshooting " +
                      "table has the measurement behind this."
                    : "LabVIEW is up but its AI gRPC service is not answering yet. " +
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
