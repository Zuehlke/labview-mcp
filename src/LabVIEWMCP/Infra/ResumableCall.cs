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
///
/// Since 2026-11-22 a job is on disk too (<see cref="JobStore"/>): the client REPLACES the server
/// process after a request timeout, and the same call on the new process attaches to the old
/// process's job or collects its answer instead of running the work again.
/// </summary>
internal static class ResumableCall
{
    private sealed record Job(string Key, Task<string> Work, Stopwatch Clock, DateTimeOffset Started,
                              Func<JsonObject?>? Progress, string? Label);

    /// <summary>The longest description of a running call a refusal quotes.</summary>
    internal const int MaxDescription = 200;

    /// <summary>A running call as a refusal names it: cut to <see cref="MaxDescription"/>.</summary>
    internal static string Describe(string call) =>
        call.Length <= MaxDescription ? call : call[..(MaxDescription - 1)] + "…";

    private static readonly ConcurrentDictionary<string, Job> Jobs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// Runs <paramref name="work"/> under <paramref name="key"/> or attaches to the job already
    /// running under it, and answers within <paramref name="answerWithin"/>. <paramref name="family"/>
    /// keeps two DIFFERENT jobs of one tool from running at once: they would script the same LabVIEW.
    /// <paramref name="progress"/>, when given, is read into every `stillRunning` answer as
    /// `progress` - the job's own, so a call that attaches sees how far the first call's work got.
    /// <paramref name="label"/> names the job in another call's `anotherCallStillRunning` refusal
    /// in place of its key.
    /// </summary>
    public static async Task<string> RunAsync(string family, string key, TimeSpan answerWithin,
        Func<Task<string>> work, string retryHint, CancellationToken ct, Func<JsonObject?>? progress = null,
        string? label = null)
    {
        var fullKey = family + "|" + key;
        var budget = answerWithin <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : answerWithin;
        Job? job;
        bool attached;
        lock (Gate)
        {
            var other = Jobs.Values.FirstOrDefault(j =>
                j.Key.StartsWith(family + "|", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(j.Key, fullKey, StringComparison.OrdinalIgnoreCase)
                && !j.Work.IsCompleted);
            if (other is not null)
                // the job's own label, never its whole key: a layout batch's key is every VI path,
                // and the refusal named 57 of them twice (DQMH_Retest, 2026-11-12)
                return Refusal(Describe(other.Label ?? other.Key[(family.Length + 1)..]),
                    other.Clock.Elapsed, null);
            job = Existing(fullKey);
            attached = job is not null;
        }

        if (job is null)
        {
            // A job ANOTHER server process started - the client replaced the process after a
            // request timeout, and the old one is still at work (JobStore has the measurement).
            var otherFamily = JobStore.OfFamily(family).FirstOrDefault(e =>
                !JobStore.IsOwn(e) && e.Answer is null
                && !string.Equals(e.Key, fullKey, StringComparison.OrdinalIgnoreCase) && JobStore.IsAlive(e));
            if (otherFamily is not null)
                return Refusal(Describe(otherFamily.Label ?? otherFamily.Key[(family.Length + 1)..]),
                    DateTimeOffset.Now - otherFamily.Started, otherFamily.Pid);

            var (entry, collected) = await JobStore.FromOtherProcessAsync(fullKey, budget, ct, NewestLabviewStart());
            if (collected is not null)
                return AnnotateFromProcess(collected, entry!);
            if (entry is not null)
                return StillRunning(retryHint, DateTimeOffset.Now - entry.Started, entry.Started, true, null,
                    entry.Pid);

            lock (Gate)
            {
                job = Existing(fullKey);
                attached = job is not null;
                job ??= Start(family, fullKey, work, progress, label);
                Jobs[fullKey] = job;
            }
        }

        try
        {
            await Task.WhenAny(job.Work, Task.Delay(budget, ct));
        }
        catch (OperationCanceledException)
        {
            // The client gave up on THIS call; the job carries on for the next one.
        }

        if (!job.Work.IsCompleted)
            return StillRunning(retryHint, job.Clock.Elapsed, job.Started, attached, job.Progress, null);

        // under the gate, like the write in Start: a write landing after this removal would leave a
        // collected answer on disk, handed to the next same call (measured live, 2026-11-22)
        lock (Gate)
        {
            Jobs.TryRemove(new KeyValuePair<string, Job>(fullKey, job));
            JobStore.Remove(fullKey);
        }
        var answer = AnswerOf(job.Work);
        return attached ? Annotate(answer, job) : answer;
    }

    /// <summary>The uncollected, still fresh job under the key, or null. Called under <see cref="Gate"/>.</summary>
    private static Job? Existing(string fullKey)
    {
        // a FINISHED job nobody collected is not this call's answer when LabVIEW restarted after
        // it began, or when it finished long ago. Measured 2026-10-30: a module call whose
        // client was stopped failed when LabVIEW was killed, and the same call on the fresh
        // LabVIEW minutes later was handed that "no port serving lvai.LVAI" as its answer
        if (!Jobs.TryGetValue(fullKey, out var existing)) return null;
        return IsStale(existing, DateTimeOffset.Now, NewestLabviewStart()) ? null : existing;
    }

    private static string AnswerOf(Task<string> work) =>
        work.IsCompletedSuccessfully
            ? work.Result
            : Json.Error("exception", work.Exception?.GetBaseException().Message ?? "The job failed.");

    private static string Refusal(string running, TimeSpan since, int? otherProcess) =>
        Json.Error("anotherCallStillRunning",
            $"An earlier call ({running}) is still running " +
            (otherProcess is { } pid ? $"in an earlier server process ({pid}) " : "in this server ") +
            $"since {since.TotalSeconds:F0} s and scripts the same " +
            "LabVIEW. Call again with ITS arguments to collect its answer first.",
            new { runningCall = running });

    private static string StillRunning(string retryHint, TimeSpan ranFor, DateTimeOffset started, bool attached,
                                       Func<JsonObject?>? progress, int? otherProcess)
    {
        var running = new JsonObject
        {
            ["ok"] = false,
            ["errorKind"] = "stillRunning",
            ["error"] = "The work is still running in the server; nothing has failed. " + retryHint,
            ["runningForSeconds"] = Math.Round(ranFor.TotalSeconds, 1),
            ["startedAt"] = started.ToString("HH:mm:ss"),
            ["attachedToEarlierCall"] = attached,
        };
        if (otherProcess is { } pid) running["runningInEarlierServerProcess"] = pid;
        try
        {
            if (progress?.Invoke() is { } p) running["progress"] = p;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // progress is a courtesy; the answer stands without it
        }
        return Indent(running);
    }

    // the clock stops when the work does, so it says how long the work RAN; the answer goes to
    // disk too, for a server process that replaces this one before the answer is collected
    private static Job Start(string family, string key, Func<Task<string>> work, Func<JsonObject?>? progress,
                             string? label)
    {
        var clock = Stopwatch.StartNew();
        var started = DateTimeOffset.Now;
        JobStore.Running(key, family, started, label);
        var task = Task.Run(work);
        _ = task.ContinueWith(t =>
        {
            clock.Stop();
            lock (Gate)
                if (Jobs.TryGetValue(key, out var mine) && ReferenceEquals(mine.Work, t))
                    JobStore.Finished(key, family, started, label, AnswerOf(t), clock.Elapsed);
        }, TaskScheduler.Default);
        return new Job(key, task, clock, started, progress, label);
    }

    /// <summary>An answer collected from another server process says so.</summary>
    private static string AnnotateFromProcess(string answer, JobStore.Entry entry)
    {
        try
        {
            if (JsonNode.Parse(answer) is JsonObject o)
            {
                o["answeredFromEarlierCall"] = true;
                o["answeredFromEarlierServerProcess"] = entry.Pid;
                o["jobStartedAt"] = entry.Started.ToString("HH:mm:ss");
                return Indent(o);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return answer;
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

    /// <summary>The work of every job still running, for the shutdown drain (JobStore.DrainAsync).</summary>
    internal static Task[] Unfinished() =>
        Jobs.Values.Where(j => !j.Work.IsCompleted).Select(Task (j) => j.Work).ToArray();

    /// <summary>For tests: forget every job.</summary>
    internal static void Reset()
    {
        foreach (var key in Jobs.Keys) JobStore.Remove(key);
        Jobs.Clear();
    }

    private static string Indent(JsonNode node) =>
        node.ToJsonString(Json.Answer);
}
