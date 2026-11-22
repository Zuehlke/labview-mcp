using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Jobs that outlive their request (<see cref="ResumableCall"/>, <see cref="LateCalls"/>), written to
/// disk so that the NEXT server process can collect them.
///
/// Measured 2026-11-22 (docs/dqmh-scripting.md §9g): when one request passes the client's 60 s
/// ceiling, the client drops the stdio connection and starts a new server process; the old one is
/// orphaned, not ended, and finishes its jobs. Their answers lived only in the old process's memory,
/// so the same call on the new process ran the work again - `lvai_dqmh_new_module` answered
/// `moduleExists` for a module the old process had just written, twice in one day.
///
/// One small JSON file per job under <c>%TEMP%\LabVIEWMCP\jobs</c>, named by a hash of its key: the
/// owning process (id and start time, so a reused id is not taken for the owner), when the job
/// started, and its answer once it has one. Nothing here may break a call: every IO failure is
/// swallowed and the caller falls back to what it did before.
/// </summary>
internal static class JobStore
{
    internal sealed record Entry(string Key, string Family, int Pid, DateTimeOffset ProcessStart,
        DateTimeOffset Started, string? Label, string? Answer, double RanForSeconds);

    /// <summary>The folder; tests point it elsewhere.</summary>
    internal static string Folder { get; set; } = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "jobs");

    /// <summary>This process as an entry names it; tests pose as another process.</summary>
    internal static (int Pid, DateTimeOffset Start) Self { get; set; } = CurrentProcess();

    /// <summary>Whether an entry's owning process still runs; tests replace it.</summary>
    internal static Func<Entry, bool> IsAlive { get; set; } = DefaultIsAlive;

    /// <summary>An entry older than this is deleted whenever the folder is read.</summary>
    private static readonly TimeSpan Forget = TimeSpan.FromDays(1);

    internal static bool IsOwn(Entry e) =>
        e.Pid == Self.Pid && Math.Abs((e.ProcessStart - Self.Start).TotalSeconds) < 2;

    internal static void Running(string key, string family, DateTimeOffset started, string? label) =>
        Write(new Entry(key, family, Self.Pid, Self.Start, started, label, null, 0));

    internal static void Finished(string key, string family, DateTimeOffset started, string? label,
                                  string answer, TimeSpan ranFor) =>
        Write(new Entry(key, family, Self.Pid, Self.Start, started, label, answer, ranFor.TotalSeconds));

    internal static Entry? Read(string key)
    {
        try
        {
            var path = FileFor(key);
            if (!File.Exists(path)) return null;
            var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            return e is not null && string.Equals(e.Key, key, StringComparison.Ordinal) ? e : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Every entry of a family; on the way, entries past <see cref="Forget"/> are deleted.</summary>
    internal static List<Entry> OfFamily(string family)
    {
        var found = new List<Entry>();
        try
        {
            if (!Directory.Exists(Folder)) return found;
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                    if (e is null) continue;
                    if (DateTimeOffset.Now - e.Started > Forget) { File.Delete(path); continue; }
                    if (string.Equals(e.Family, family, StringComparison.OrdinalIgnoreCase)) found.Add(e);
                }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException
                                              or NotSupportedException) { }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>
    /// The job under <paramref name="key"/> as ANOTHER process left it, waited on for at most
    /// <paramref name="budget"/> while its owner still runs it. Answers (null, null) when there is
    /// nothing to collect - no entry, this process's own, an owner that died without an answer, or
    /// a stale answer (<see cref="ResumableCall.IsStale(DateTimeOffset,bool,TimeSpan,DateTimeOffset,DateTimeOffset?)"/>)
    /// - and the caller runs the work itself; (entry, answer) when it collected the answer, which
    /// removes the entry; (entry, null) when the other process is still at it.
    /// </summary>
    internal static async Task<(Entry? Entry, string? Answer)> FromOtherProcessAsync(string key,
        TimeSpan budget, CancellationToken ct, DateTimeOffset? labviewStarted)
    {
        var e = Read(key);
        if (e is null || IsOwn(e)) return (null, null);
        var clock = Stopwatch.StartNew();
        while (e.Answer is null)
        {
            if (!IsAlive(e)) { Remove(key); return (null, null); }
            if (clock.Elapsed >= budget || ct.IsCancellationRequested) return (e, null);
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct); }
            catch (OperationCanceledException) { return (e, null); }
            if (Read(key) is not { } next) return (null, null);  // collected meanwhile
            e = next;
        }
        Remove(key);
        if (ResumableCall.IsStale(e.Started, true, TimeSpan.FromSeconds(e.RanForSeconds), DateTimeOffset.Now, labviewStarted))
            return (null, null);
        return (e, e.Answer);
    }

    /// <summary>
    /// At shutdown: waits for every job still running in this process (<see cref="ResumableCall"/>,
    /// <see cref="LateCalls"/>) so that it writes its answer here before the process ends.
    /// </summary>
    internal static async Task DrainAsync(TimeSpan atMost)
    {
        var running = ResumableCall.Unfinished().Concat(LateCalls.Unfinished()).ToArray();
        if (running.Length == 0) return;
        Console.Error.WriteLine($"[jobs] stdin closed with {running.Length} job(s) running; waiting up to " +
                                $"{atMost.TotalMinutes:F0} min so their answers reach {Folder}");
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(atMost));
        // the answer is written by a continuation of the work; give it a moment
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    internal static void Remove(string key)
    {
        try { File.Delete(FileFor(key)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Write(Entry entry)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = FileFor(entry.Key);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    private static string FileFor(string key) =>
        Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json");

    private static (int, DateTimeOffset) CurrentProcess()
    {
        using var p = Process.GetCurrentProcess();
        return (p.Id, new DateTimeOffset(p.StartTime));
    }

    private static bool DefaultIsAlive(Entry e)
    {
        try
        {
            using var p = Process.GetProcessById(e.Pid);
            return !p.HasExited && Math.Abs((new DateTimeOffset(p.StartTime) - e.ProcessStart).TotalSeconds) < 2;
        }
        catch (Exception x) when (x is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
