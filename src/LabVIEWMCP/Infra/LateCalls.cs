using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LabVIEWMcp.Infra;

/// <summary>
/// EVERY tool call answers before the client's 60 s ceiling, so that no call can make the client
/// drop the connection.
///
/// Measured 2026-11-22 (docs/dqmh-scripting.md §9g): a parallel agent's `lvai_generate_vi` waited
/// behind a DQMH scripting run for the one LabVIEW and passed 60 s; the client answered `Request
/// timed out`, closed the stdio connection five to seven seconds later - every call still in flight
/// on it failed with `Connection closed` - and started a new server process. Twice in one day, and
/// each time a running `lvai_dqmh_new_module` lost its answer with it.
///
/// So a call that has not finished after <see cref="AnswerWithin"/> answers `stillRunning` and goes
/// on in the server; the SAME call again (same tool, same arguments) waits on it and collects its
/// answer. It is <see cref="ResumableCall"/> for every tool, without the one-job-per-tool rule: two
/// agents' different `lvai_generate_vi` calls may still run side by side. A late call's answer is
/// also written to <see cref="JobStore"/>, so a replacement process can hand it out.
///
/// A call that finishes in time is untouched: its result, its exceptions and its cancellation are
/// exactly what they were. The client's cancellation reaches the work only while the client still
/// waits; once the call has answered `stillRunning` nothing is waiting to cancel it.
/// </summary>
internal static class LateCalls
{
    /// <summary>
    /// Below the client's 60 s with room for the transport; tests shorten it, and so does
    /// <c>LABVIEWMCP_ANSWER_WITHIN_SECONDS</c> (1-55) for a live check over raw stdio.
    /// </summary>
    internal static TimeSpan AnswerWithin { get; set; } = FromEnvironment();

    private static TimeSpan FromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable("LABVIEWMCP_ANSWER_WITHIN_SECONDS"), out var s) && s is >= 1 and <= 55
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(50);

    private const string Family = "late-call";

    private sealed record Late(Task<CallToolResult> Work, Stopwatch Clock, DateTimeOffset Started);

    private static readonly ConcurrentDictionary<string, Late> Pending = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>The call's identity: tool name and its arguments, with keys in a fixed order.</summary>
    internal static string Key(string tool, IDictionary<string, JsonElement>? arguments)
    {
        var sorted = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments is not null)
            foreach (var (k, v) in arguments) sorted[k] = v;
        return Family + "|" + tool + "|" + JsonSerializer.Serialize(sorted);
    }

    /// <summary>
    /// Runs <paramref name="work"/>, or collects the late call already running under
    /// <paramref name="key"/>. <paramref name="hold"/> is disposed when the work ends, not when this
    /// call answers - the parallel-call counter must see a late call as still running.
    /// </summary>
    internal static async Task<CallToolResult> RunAsync(string tool, string key,
        Func<CancellationToken, Task<CallToolResult>> work, IDisposable hold, CancellationToken ct)
    {
        // 1. the same call, late in this process
        if (Pending.TryGetValue(key, out var late))
        {
            if (late.Work.IsCompleted
                && ResumableCall.IsStale(late.Started, true, late.Clock.Elapsed, DateTimeOffset.Now, null))
                Pending.TryRemove(new KeyValuePair<string, Late>(key, late));
            else
            {
                hold.Dispose();
                return await WaitAsync(tool, key, late, attached: true, ct);
            }
        }

        // 2. the same call, late in the process this one replaced
        var (entry, collected) = await JobStore.FromOtherProcessAsync(key, AnswerWithin, ct, null);
        if (collected is not null)
        {
            hold.Dispose();
            return FromDisk(collected, entry!) ?? Running(tool, DateTimeOffset.Now - entry!.Started, entry.Started, true, entry.Pid);
        }
        if (entry is not null)
        {
            hold.Dispose();
            return Running(tool, DateTimeOffset.Now - entry.Started, entry.Started, true, entry.Pid);
        }

        // 3. run it
        var cts = new CancellationTokenSource();
        var forward = ct.Register(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
        var clock = Stopwatch.StartNew();
        var started = DateTimeOffset.Now;
        Task<CallToolResult> task;
        try
        {
            task = work(cts.Token);
        }
        catch
        {
            forward.Dispose(); cts.Dispose(); hold.Dispose();
            throw;
        }
        _ = task.ContinueWith(_ =>
        {
            clock.Stop();
            forward.Dispose();
            cts.Dispose();
            hold.Dispose();
        }, TaskScheduler.Default);

        try
        {
            await Task.WhenAny(task, Task.Delay(AnswerWithin, ct));
        }
        catch (OperationCanceledException) { }

        // in time, or the client cancelled while it still waited: exactly the result as it was
        if (task.IsCompleted || ct.IsCancellationRequested)
            return await task;

        // late: the client stops waiting here, so it no longer cancels the work
        forward.Dispose();
        var entryLate = new Late(task, clock, started);
        lock (Gate)
        {
            Pending[key] = entryLate;
            JobStore.Running(key, Family, started, tool);
        }
        // the write and the collection share one gate: a write landing after the collection removed
        // the entry left a collected answer on disk for the next same call (measured live, 2026-11-22)
        _ = task.ContinueWith(t =>
        {
            lock (Gate)
                if (Pending.TryGetValue(key, out var mine) && ReferenceEquals(mine, entryLate))
                    JobStore.Finished(key, Family, started, tool, Serialize(ResultOf(t)), clock.Elapsed);
        }, TaskScheduler.Default);
        return Running(tool, clock.Elapsed, started, false, null);
    }

    private static async Task<CallToolResult> WaitAsync(string tool, string key, Late late, bool attached,
                                                        CancellationToken ct)
    {
        try
        {
            await Task.WhenAny(late.Work, Task.Delay(AnswerWithin, ct));
        }
        catch (OperationCanceledException) { }
        if (!late.Work.IsCompleted)
            return Running(tool, late.Clock.Elapsed, late.Started, attached, null);
        lock (Gate)
        {
            Pending.TryRemove(new KeyValuePair<string, Late>(key, late));
            JobStore.Remove(key);
        }
        return Annotated(ResultOf(late.Work), late.Started, null);
    }

    private static CallToolResult ResultOf(Task<CallToolResult> t) =>
        t.IsCompletedSuccessfully
            ? t.Result
            : new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock
                {
                    Text = Json.Error("exception", t.Exception?.GetBaseException().Message ?? "The call failed."),
                }],
            };

    private static string Serialize(CallToolResult r) => JsonSerializer.Serialize(r, McpJsonUtilities.DefaultOptions);

    private static CallToolResult? FromDisk(string text, JobStore.Entry entry)
    {
        try
        {
            return JsonSerializer.Deserialize<CallToolResult>(text, McpJsonUtilities.DefaultOptions) is { } r
                ? Annotated(r, entry.Started, entry.Pid)
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A collected answer says that it comes from an earlier call - and process.</summary>
    private static CallToolResult Annotated(CallToolResult r, DateTimeOffset started, int? process)
    {
        for (var i = 0; i < r.Content.Count; i++)
        {
            if (r.Content[i] is not TextContentBlock text) continue;
            try
            {
                if (JsonNode.Parse(text.Text) is not JsonObject o) continue;
                o["answeredFromEarlierCall"] = true;
                o["jobStartedAt"] = started.ToString("HH:mm:ss");
                o[ResumableCall.JobStartedUtcKey] = started.UtcDateTime.ToString("o");
                if (process is { } pid) o["answeredFromEarlierServerProcess"] = pid;
                r.Content[i] = new TextContentBlock { Text = o.ToJsonString(Json.Answer) };
                break;
            }
            catch (JsonException) { }
        }
        return r;
    }

    private static CallToolResult Running(string tool, TimeSpan ranFor, DateTimeOffset started, bool attached,
                                          int? process)
    {
        var o = new JsonObject
        {
            ["ok"] = false,
            ["errorKind"] = "stillRunning",
            ["error"] = $"The call is still running in the server; nothing has failed. It answers now " +
                        "because the client gives up at 60 s and then drops the connection. Call " +
                        $"{tool} again with EXACTLY the same arguments: it waits for this run and " +
                        "returns its answer. Do not start the same work another way meanwhile.",
            ["runningForSeconds"] = Math.Round(ranFor.TotalSeconds, 1),
            ["startedAt"] = started.ToString("HH:mm:ss"),
            ["attachedToEarlierCall"] = attached,
        };
        if (process is { } pid) o["runningInEarlierServerProcess"] = pid;
        return new CallToolResult { Content = [new TextContentBlock { Text = o.ToJsonString(Json.Answer) }] };
    }

    /// <summary>The work of every late call still running, for the shutdown drain (JobStore.DrainAsync).</summary>
    internal static Task[] Unfinished() =>
        Pending.Values.Where(l => !l.Work.IsCompleted).Select(Task (l) => l.Work).ToArray();

    /// <summary>For tests: forget every late call.</summary>
    internal static void Reset()
    {
        foreach (var key in Pending.Keys) JobStore.Remove(key);
        Pending.Clear();
    }
}
