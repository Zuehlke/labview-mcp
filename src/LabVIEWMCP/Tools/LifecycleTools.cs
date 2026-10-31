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
    private static readonly JsonSerializerOptions Indented = Json.Answer;

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
        THE SERVICE COMES UP WITH LabVIEW ITSELF, about 45 s after a cold start, with no Nigel
        chat opened - measured 2026-10-17 with NI.NigelLocalService running. This text said for
        weeks that the 'LV AI gRPC Service' starts with NIGEL and not with the IDE; that is
        refuted. What still stands is the earlier observation that a LabVIEW up for twenty
        minutes once held 30 listener ports and served lvai.LVAI on none of them - so a
        'starting' that persists for more than about two minutes, with LabVIEW visibly
        running, is not this tool being slow: then ask whoever is at the machine to open
        Nigel, and call lvai_status once.
        Otherwise, when LabVIEW is still coming up, calling again is right - the second call
        finds the instance and waits. For an unattended long wait use the CLI:
        LabVIEWMCP --ensure-labview.
        """)]
    public async Task<string> EnsureLabViewAsync(
        [Description("How long to wait for the gRPC service to answer, in seconds (capped at 45)")]
        int waitSeconds = 40,
        [Description("""
            Do NOT clear Documents\\LabVIEW Data\\LVAutoSave before starting LabVIEW. It is
            cleared by default because leftover recovery data makes LabVIEW raise a modal
            recovery dialog on start, and a modal dialog stops this gRPC service until a
            human dismisses it. Only ever cleared when this tool starts LabVIEW itself.
            """)]
        bool keepAutoSave = false,
        CancellationToken ct = default,
        [Description(AixmlBackend.ParameterDescription + """
             On pylabview READY means LabVIEW answers over its ActiveX server - what that backend
            uses - not that NI's gRPC service answers: a LabVIEW older than 2026 has no such
            service, and on pylabview nothing waits for Nigel.
            """)]
        string? backend = null)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_ensure_labview", out var choice, out var bad))
            return AixmlBackend.Refusal(bad);
        return AixmlTools.WithBackend(await EnsureCoreAsync(waitSeconds, keepAutoSave,
            choice.Name == AixmlBackend.PyLabview, ct), choice);
    }

    private async Task<string> EnsureCoreAsync(int waitSeconds, bool keepAutoSave, bool overCom,
                                               CancellationToken ct) =>
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
            AutoSaveRecovery.Result? autoSave = null;

            if (running.Count == 0)
            {
                // BEFORE the launch. Leftover auto-save data makes LabVIEW raise a modal recovery
                // dialog on start, and a modal dialog stops this very gRPC service until somebody
                // dismisses it - so an unattended start clears the store first. Only when WE start
                // LabVIEW: a process already running owns its own recovery data.
                if (!keepAutoSave) autoSave = AutoSaveRecovery.Clear();

                // the LabVIEW to start: the settings file's, else the one last seen running WITH
                // its -pref, else the newest installed (LabViewContext.ToStart, issue #99)
                var (pick, pref, startContext) = LabViewContext.ToStart();
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
                launch = await LabViewLauncher.StartAndConfirmAsync(pick, token, pref);
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
                launched = pick.Describe() + (pref is null ? "" : $" -pref {pref}") +
                           $" (chosen: {startContext.Source})";
                RecordOwnership(launch.Pid);
            }

            // Poll until the service answers. A cold LabVIEW start is far slower than this
            // budget, hence the explicit "call again" outcome rather than a failure.
            Exception? last = null;

            while (!token.IsCancellationRequested)
            {
                if (overCom)
                {
                    // ready = a running LabVIEW with a window answers over ActiveX; NotReady keeps
                    // COM away from a LabVIEW still starting, which would launch a second one
                    if (LabViewCom.NotReady() is { } notYet)
                        last = new InvalidOperationException(notYet);
                    else
                    {
                        try
                        {
                            var version = await LabViewCom.WithLabViewAsync(app =>
                                app.GetType().InvokeMember("Version", System.Reflection.BindingFlags.GetProperty,
                                    null, app, null, System.Globalization.CultureInfo.InvariantCulture)?.ToString(),
                                10, token);
                            return new JsonObject
                            {
                                ["ok"] = true,
                                ["state"] = "ready",
                                ["readyMeans"] = "LabVIEW answers over ActiveX (the pylabview backend's route); NI's gRPC service was not asked",
                                ["startedByThisCall"] = startedNow,
                                ["launched"] = launched,
                                ["launchMethod"] = launch?.Method,
                                ["autoSaveCleared"] = autoSave?.Describe(),
                                ["labviewVersion"] = version,
                                ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                            }.ToJsonString(Indented);
                        }
                        catch (Exception e) when (!ct.IsCancellationRequested)
                        {
                            last = e;
                        }
                    }
                    try { await Task.Delay(2000, token); }
                    catch (OperationCanceledException) { /* budget spent */ }
                    continue;
                }
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
                    ["autoSaveCleared"] = autoSave?.Describe(),
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
                    ["autoSaveCleared"] = autoSave?.Describe(),
                ["runningProcesses"] = stillRunning,
                ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                // THE BUDGET IS CLAMPED AND SAYING SO IS THE POINT. waitSeconds is capped at
                // Rpc.MaxToolWaitSeconds, so asking for 90 waits 45 - and four runs on 2026-09-01
                // read `waitedMs: 45009` against a nominal 90 and took it for a fault. The cap is
                // deliberate: past roughly 60 s the MCP client stops waiting and the caller learns
                // nothing at all, so waiting longer inside ONE call is not available. That is also
                // why this tool cannot retry internally, which was proposed and does not work.
                ["waitSecondsRequested"] = waitSeconds,
                ["waitSecondsUsed"] = (int)budget.TotalSeconds,
                ["lastError"] = last?.Message,
                // "LabVIEW is up" was asserted unconditionally here, and it was wrong exactly when
                // it mattered: with runningProcesses 0 the advice sent the reader after the AI
                // service while the IDE itself was gone.
                ["next"] = stillRunning == 0
                    ? "No LabVIEW process is running any more - it was started and did not stay up. " +
                      "Start LabVIEW by hand and call this tool again; the README's Troubleshooting " +
                      "table has the measurement behind this."
                    : "LabVIEW is up but its AI gRPC service is not answering yet. THIS IS THE " +
                      "NORMAL COLD-START ANSWER, not a fault - CALL THIS TOOL AGAIN and it will " +
                      "almost certainly answer `ready` at once. Measured four times on 2026-09-01: " +
                      "the first call spent its whole 45 s budget and the second answered ready in " +
                      "1.2 s, 12.1 s, 27.7 s and 1.2 s. Do NOT read `lastError: \"The operation " +
                      "was canceled.\"` as a failure; it is this call's own budget expiring. Only " +
                      "if a SECOND call also comes back `starting` is something wrong - the service " +
                      "comes up with LabVIEW itself, about 45 s after a cold start (measured " +
                      "2026-10-17); if it is still missing after about two minutes, ask the user to " +
                      "open Nigel, or use the CLI --ensure-labview for an unattended long wait.",
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
        var (pick, pickPref, pickContext) = LabViewContext.ToStart();
        var running = LabViewLocator.RunningInstances();

        var list = new JsonArray();
        foreach (var i in installs.OrderByDescending(i => i.Release).ThenByDescending(i => i.Is32Bit))
            list.Add(new JsonObject
            {
                ["folder"] = i.FolderName,
                ["release"] = i.Release,
                ["bitness"] = i.Is32Bit ? "32-bit" : "64-bit",
                ["exe"] = i.ExePath,
                ["wouldBeStarted"] = pick is not null && LabViewContext.SamePath(pick.ExePath, i.ExePath),
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
            ["selectionPref"] = pickPref,
            ["selectionSource"] = pickContext.Source,
            ["rule"] = "the running LabVIEW, else \"labviewExe\"/\"labviewPref\" in the settings file, " +
                       "else the LabVIEW last seen running with the -pref it ran with, else the newest " +
                       "installed release (32-bit preferred, NXG excluded)",
            ["labviewContext"] = System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(pickContext.Describe())),
            ["installations"] = list,
        }.ToJsonString(Indented);
    }

    /// <summary>
    /// Record the LabVIEW this server just started, with its start time (pids are reused), so
    /// lvai_labview_state can say `startedByThisServer`. From the colleague's fork, where the
    /// restart's ownership rule reads it; here only the state tool does.
    /// </summary>
    private static void RecordOwnership(int? pid)
    {
        if (pid is not { } id) return;
        var process = Win32LabViewMachine.Instance.Processes(LabViewState.LabViewProcess)
            .FirstOrDefault(p => p.Id == id);
        if (process is not null) LabViewOwnership.Record(id, process.StartTime);
    }
}
