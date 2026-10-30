using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A tool call that may outlive the MCP client's request ceiling - measured at EXACTLY 60 s and not
/// raisable from this side (docs/lvclass-creation.md) - keeps running HERE, and the client collects
/// its answer by calling again with the same arguments.
///
/// Measured 2026-10-06: two `lvai_dqmh_new_module` calls on a freshly started LabVIEW took about two
/// minutes and one, the client answered `Request timed out`, and the server finished both - so the
/// modules were on disk while their answers, with the verification in them, were lost. The work
/// itself was never the problem; the transport was. So the job runs detached from the request's
/// cancellation, the call waits at most <c>answerWithin</c>, and a later call with the same key
/// waits on the SAME job instead of starting a second one into the same LabVIEW.
/// </summary>
internal static class ResumableCall
{
    private sealed record Job(string Key, Task<string> Work, Stopwatch Clock, DateTimeOffset Started);

    private static readonly ConcurrentDictionary<string, Job> Jobs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// Runs <paramref name="work"/> under <paramref name="key"/> or attaches to the job already
    /// running under it, and answers within <paramref name="answerWithin"/>. <paramref name="family"/>
    /// keeps two DIFFERENT jobs of one tool from running at once: they would script the same LabVIEW.
    /// </summary>
    public static async Task<string> RunAsync(string family, string key, TimeSpan answerWithin,
        Func<Task<string>> work, string retryHint, CancellationToken ct)
    {
        var fullKey = family + "|" + key;
        Job job;
        bool attached;
        lock (Gate)
        {
            var other = Jobs.Values.FirstOrDefault(j =>
                j.Key.StartsWith(family + "|", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(j.Key, fullKey, StringComparison.OrdinalIgnoreCase)
                && !j.Work.IsCompleted);
            if (other is not null)
                return Json.Error("anotherCallStillRunning",
                    $"An earlier call ({other.Key[(family.Length + 1)..]}) is still running in this " +
                    $"server since {other.Clock.Elapsed.TotalSeconds:F0} s and scripts the same " +
                    "LabVIEW. Call again with ITS arguments to collect its answer first.",
                    new { runningKey = other.Key[(family.Length + 1)..] });
            attached = Jobs.TryGetValue(fullKey, out var existing);
            // a FINISHED job nobody collected is not this call's answer when LabVIEW restarted after
            // it began, or when it finished long ago. Measured 2026-10-30: a module call whose
            // client was stopped failed when LabVIEW was killed, and the same call on the fresh
            // LabVIEW minutes later was handed that "no port serving lvai.LVAI" as its answer
            if (existing is not null && IsStale(existing, DateTimeOffset.Now, NewestLabviewStart()))
            {
                existing = null;
                attached = false;
            }
            job = existing ?? Start(fullKey, work);
            Jobs[fullKey] = job;
        }

        var budget = answerWithin <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : answerWithin;
        try
        {
            await Task.WhenAny(job.Work, Task.Delay(budget, ct));
        }
        catch (OperationCanceledException)
        {
            // The client gave up on THIS call; the job carries on for the next one.
        }

        if (!job.Work.IsCompleted)
            return Indent(new JsonObject
            {
                ["ok"] = false,
                ["errorKind"] = "stillRunning",
                ["error"] = "The work is still running in the server; nothing has failed. " + retryHint,
                ["runningForSeconds"] = Math.Round(job.Clock.Elapsed.TotalSeconds, 1),
                ["startedAt"] = job.Started.ToString("HH:mm:ss"),
                ["attachedToEarlierCall"] = attached,
            });

        Jobs.TryRemove(new KeyValuePair<string, Job>(fullKey, job));
        var answer = job.Work.IsCompletedSuccessfully
            ? job.Work.Result
            : Json.Error("exception", job.Work.Exception?.GetBaseException().Message ?? "The job failed.");
        return attached ? Annotate(answer, job) : answer;
    }

    // the clock stops when the work does, so it says how long the work RAN
    private static Job Start(string key, Func<Task<string>> work)
    {
        var clock = Stopwatch.StartNew();
        var task = Task.Run(work);
        _ = task.ContinueWith(_ => clock.Stop(), TaskScheduler.Default);
        return new Job(key, task, clock, DateTimeOffset.Now);
    }

    /// <summary>How long a finished, uncollected answer is kept for the call that collects it.</summary>
    internal static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A finished job is stale when LabVIEW started after the job did - the answer is about an
    /// instance that is gone - or when it began longer ago than <see cref="KeepFinished"/> plus the
    /// work's own run time, so nobody can still be waiting for it. A running job is never stale.
    /// </summary>
    internal static bool IsStale(DateTimeOffset started, bool completed, TimeSpan ranFor, DateTimeOffset now,
                                 DateTimeOffset? labviewStarted) =>
        completed && (labviewStarted > started || now - started > ranFor + KeepFinished);

    private static bool IsStale(Job job, DateTimeOffset now, DateTimeOffset? labviewStarted) =>
        IsStale(job.Started, job.Work.IsCompleted, job.Clock.Elapsed, now, labviewStarted);

    private static DateTimeOffset? NewestLabviewStart()
    {
        try
        {
            DateTimeOffset? newest = null;
            foreach (var p in Process.GetProcessesByName("LabVIEW"))
                using (p)
                    try
                    {
                        var s = new DateTimeOffset(p.StartTime);
                        if (newest is null || s > newest) newest = s;
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
            return newest;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A collected answer says that it comes from an earlier call, and how long it took.</summary>
    private static string Annotate(string answer, Job job)
    {
        try
        {
            if (JsonNode.Parse(answer) is JsonObject o)
            {
                o["answeredFromEarlierCall"] = true;
                o["jobStartedAt"] = job.Started.ToString("HH:mm:ss");
                return Indent(o);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return answer;
    }

    /// <summary>For tests: forget every job.</summary>
    internal static void Reset() => Jobs.Clear();

    private static string Indent(JsonNode node) =>
        node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
