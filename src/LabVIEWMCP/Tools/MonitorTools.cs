using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The Monitor* RPCs run INVERTED: LabVIEW is the one pushing work.
///
///   - the stream RESPONSE is the inbound work item ("the user asked for X")
///   - the stream REQUEST is OUR answer back into LabVIEW's UI
///
/// This is how NI's own NigelLocalService plugs in - the AI assistant log shows it
/// opening exactly these five monitors ("[Code Completion] Started monitoring for
/// requests."). So these tools let this MCP server occupy the same hooks.
///
/// MEASURED, and it decides how useful these tools are: the monitors are
/// SINGLE-SUBSCRIBER streams, and NI's own service always wins.
///
/// "Discuss with Nigel..." was triggered while a watch was subscribed and the hook was free -
/// NI's service had been stopped beforehand. The click STARTED the service (up at 10:55:43,
/// "[Discuss VI] Started monitoring" at 10:55:44) and the event went to it, not to the
/// already-waiting subscriber. Stopping the service therefore does not free the hook; LabVIEW
/// brings its own consumer along.
///
/// So a timeout here is the normal outcome whenever NI's assistant is installed, and these
/// tools are diagnostic rather than a usable integration point. See section 14 of
/// docs/aixml-reference.md.
///
/// A single MCP tool call cannot hold a stream open across calls, so the shape is
/// "wait for one item, optionally answer it, hang up".
/// </summary>
[McpServerToolType]
internal sealed class MonitorTools(LvaiConnection connection)
{
    private const string Waits = """

        Blocks until LabVIEW pushes an item or the timeout expires; trigger the feature in the IDE
        while it waits. A timeout is normal - NI's own service usually takes the event. Diagnostic.
        """;

    private static readonly JsonSerializerOptions Indented = Json.Answer;

    [McpServerTool(Name = "lvai_monitor_project_changes", ReadOnly = true,
                   Title = "Watch project changes")]
    [Description("""
        Monitor: RPC MonitorProjectChanges - an event per project item added, modified or removed
        (one-way stream).
        """ + Waits)]
    public async Task<string> MonitorProjectChangesAsync(
        [Description("Max events to collect before returning")] int maxMessages = 5,
        [Description("Seconds to wait, capped at 45 (use the CLI --watch mode for longer)")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var wait = Rpc.ClampToolWait(timeoutSeconds);
            // GetClientAsync, NOT InvokeAsync - deliberately, and unlike every other tool.
            // InvokeAsync retries the whole lambda after re-discovering, which for a stream
            // that CONSUMES events means replaying a wait that may already have delivered
            // some. A monitor is better off failing honestly and letting the caller decide.
            var client = await connection.GetClientAsync(ct);
            using var call = client.MonitorProjectChanges(
                new MonitorProjectChangesRequest(),
                deadline: Rpc.Deadline(wait + 15), cancellationToken: ct);

            var (items, reason) = await Rpc.CollectAsync(
                call.ResponseStream, maxMessages, wait, ct);
            return Json.Stream(items, reason, maxMessages);
        });

    [McpServerTool(Name = "lvai_monitor_discuss_vi", ReadOnly = true,
                   Title = "Wait for a Discuss-VI request")]
    [Description("""
        Monitor: RPC MonitorDiscussVI - fires when the user invokes "Discuss VI"; the item names
        the VI or project. Reply (replyJson): {"errorCode":0,"errorMessage":""}. Expect a TIMEOUT
        even when the feature fires: NI's service always takes the event. lvai_discuss_file triggers
        the feature but does not make the event arrive here.
        """ + Waits)]
    public async Task<string> MonitorDiscussViAsync(
        [Description("Send an empty request first to register the monitor")] bool sendReady = false,
        [Description("Optional protobuf-JSON MonitorDiscussVIRequest to answer with")]
        string? replyJson = null,
        [Description("Max items to collect")] int maxMessages = 1,
        [Description("Seconds to wait, capped at 45 (use the CLI --watch mode for longer)")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        await WaitBidiAsync<MonitorDiscussVIRequest, MonitorDiscussVIResponse>(
            (c, o) => c.MonitorDiscussVI(o), sendReady, replyJson, maxMessages, timeoutSeconds, ct: ct);

    [McpServerTool(Name = "lvai_monitor_palette_searches", ReadOnly = true,
                   Title = "Wait for a palette-search request")]
    [Description("""
        Monitor: RPC MonitorPaletteSearches - fires on LabVIEW's AI palette search (searchString +
        guid). Reply echoing the SAME guid:
        {"items":[{"itemGuid":"...","rationale":"why"}],"guid":"<echo>","errorCode":0}
        """ + Waits)]
    public async Task<string> MonitorPaletteSearchesAsync(
        [Description("Send an empty request first to register the monitor")] bool sendReady = false,
        [Description("Optional protobuf-JSON MonitorPaletteSearchesRequest to answer with")]
        string? replyJson = null,
        [Description("Max items to collect")] int maxMessages = 1,
        [Description("Seconds to wait, capped at 45 (use the CLI --watch mode for longer)")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        await WaitBidiAsync<MonitorPaletteSearchesRequest, MonitorPaletteSearchesResponse>(
            (c, o) => c.MonitorPaletteSearches(o), sendReady, replyJson, maxMessages, timeoutSeconds, ct: ct);

    [McpServerTool(Name = "lvai_monitor_example_searches", ReadOnly = true,
                   Title = "Wait for an example-search request")]
    [Description("""
        Monitor: RPC MonitorExampleSearches - fires on LabVIEW's AI example search (searchString +
        guid). Reply: {"examples":[{"examplePath":"...","rationale":"why"}],"guid":"<echo>","errorCode":0}
        """ + Waits)]
    public async Task<string> MonitorExampleSearchesAsync(
        [Description("Send an empty request first to register the monitor")] bool sendReady = false,
        [Description("Optional protobuf-JSON MonitorExampleSearchesRequest to answer with")]
        string? replyJson = null,
        [Description("Max items to collect")] int maxMessages = 1,
        [Description("Seconds to wait, capped at 45 (use the CLI --watch mode for longer)")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        await WaitBidiAsync<MonitorExampleSearchesRequest, MonitorExampleSearchesResponse>(
            (c, o) => c.MonitorExampleSearches(o), sendReady, replyJson, maxMessages, timeoutSeconds, ct: ct);

    [McpServerTool(Name = "lvai_monitor_code_completion", ReadOnly = true,
                   Title = "Wait for a code-completion request")]
    [Description("""
        Monitor: RPC MonitorCodeCompletion - fires when the user asks LabVIEW for AI code
        completion ('request' = prompt, guid, 'incomplete' when more follows). Answer with AIXML
        changes: {"guid":"<echo>","suggestions":[{"changes":"<aixml>","rationale":"why"}],"errorCode":0}
        """ + Waits)]
    public async Task<string> MonitorCodeCompletionAsync(
        [Description("Send an empty request first to register the monitor")] bool sendReady = false,
        [Description("Optional protobuf-JSON MonitorCodeCompletionRequest to answer with")]
        string? replyJson = null,
        [Description("Max items to collect")] int maxMessages = 1,
        [Description("Seconds to wait, capped at 45 (use the CLI --watch mode for longer)")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        await WaitBidiAsync<MonitorCodeCompletionRequest, MonitorCodeCompletionResponse>(
            (c, o) => c.MonitorCodeCompletion(o), sendReady, replyJson, maxMessages, timeoutSeconds, ct: ct);

    [McpServerTool(Name = "lvai_monitor_front_panel_cleanup", ReadOnly = true,
                   Title = "Wait for a front-panel-cleanup request")]
    [Description("""
        Monitor: RPC MonitorFrontPanelCleanup - fires when the user asks LabVIEW to tidy a front
        panel ('request' = panel state, guid). Reply: {"guid":"<echo>","panelInfo":"<layout>","errorCode":0}
        """ + Waits)]
    public async Task<string> MonitorFrontPanelCleanupAsync(
        [Description("Send an empty request first to register the monitor")] bool sendReady = false,
        [Description("Optional protobuf-JSON MonitorFrontPanelCleanupRequest to answer with")]
        string? replyJson = null,
        [Description("Max items to collect")] int maxMessages = 1,
        [Description("Seconds to wait, capped at 45 (use the CLI --watch mode for longer)")]
        int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        await WaitBidiAsync<MonitorFrontPanelCleanupRequest, MonitorFrontPanelCleanupResponse>(
            (c, o) => c.MonitorFrontPanelCleanup(o), sendReady, replyJson, maxMessages, timeoutSeconds, ct: ct);

    // ---------- shared bidi wait ----------

    private async Task<string> WaitBidiAsync<TRequest, TResponse>(
        Func<LVAI.LVAIClient, CallOptions, AsyncDuplexStreamingCall<TRequest, TResponse>> open,
        bool sendReady, string? replyJson, int maxMessages, int timeoutSeconds, CancellationToken ct)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage =>
        await Rpc.GuardAsync(async () =>
        {
            // Parse the reply BEFORE opening the stream: a malformed reply should not
            // consume a real event that we then cannot answer.
            var reply = replyJson is null ? null : Rpc.ParseJson<TRequest>(replyJson);

            var wait = Rpc.ClampToolWait(timeoutSeconds);
            // GetClientAsync, NOT InvokeAsync - see the note on the other monitor. A bidi
            // exchange that has already sent a reply must never be replayed by a retry.
            var client = await connection.GetClientAsync(ct);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(wait));

            using var call = open(client, new CallOptions(cancellationToken: budget.Token));

            if (sendReady)
                await call.RequestStream.WriteAsync(new TRequest(), budget.Token);

            var (items, reason) = await Rpc.CollectAsync(
                call.ResponseStream, maxMessages, wait, ct);

            var replied = false;
            if (reply is not null && items.Count > 0)
            {
                try
                {
                    await call.RequestStream.WriteAsync(reply, budget.Token);
                    replied = true;
                }
                catch (Exception e) when (e is RpcException or OperationCanceledException
                                              or InvalidOperationException)
                {
                    // Stream already torn down - say so rather than hiding the received item.
                    reason += $"; reply failed ({e.GetType().Name})";
                }
            }

            try { await call.RequestStream.CompleteAsync(); }
            catch { /* the server may have closed first; nothing to salvage */ }

            if (replied || sendReady)
            {
                // Hanging up straight after the write DISCARDS it: disposing an unfinished call
                // sends RST_STREAM, and the peer can cancel out before it has read our answer.
                // Draining lets the call end normally, which is what actually delivers the reply.
                // The ready message is the same case: without the drain a server that had not
                // read it yet lost it - MonitorBidiTests.sendReady_... failed on a slow CI runner
                // (2026-11-05), and a 300 ms delay before the fake's first read reproduced it.
                try
                {
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    grace.CancelAfter(TimeSpan.FromSeconds(5));
                    while (await call.ResponseStream.MoveNext(grace.Token)) { }
                }
                catch (Exception e) when (e is RpcException or OperationCanceledException)
                {
                    // Server still had more to say, or took too long to close. The reply is out.
                }
            }

            var payload = JsonNode.Parse(Json.Stream(items, reason, maxMessages))!.AsObject();
            payload["direction"] = "inbound (LabVIEW -> client); the reply travels the request stream";
            payload["readyMessageSent"] = sendReady;
            payload["replySent"] = replied;
            return payload.ToJsonString(Indented);
        });
}
