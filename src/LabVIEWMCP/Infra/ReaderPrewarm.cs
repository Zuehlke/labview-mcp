using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Fills the export cache with the pylabview READER's exports of the shipping examples, in the
/// background after a start - so on the pylabview backend an example an agent opens is already a
/// file copy, not a read.
///
/// WHY NOW, WHEN <see cref="AixmlExportStore"/> ARGUES AGAINST PRE-WARMING. Its argument is NI's
/// converter: 52 minutes of LabVIEW time for the examples tree, ~130 handles leaked per VI, LabVIEW
/// recycled along the way and one VI that killed it. The reader needs no LabVIEW at all: 40 random
/// examples took 48 s as fresh subprocesses (2026-11-07), and a resident worker reads in a fraction
/// of that (<see cref="PythonWorkers"/>). And the reader's entries are keyed on its code's hash, so
/// every reader update empties them - without a warm-up, each update made every example cold again
/// on the next session of every user.
///
/// WHAT IT READS: the example index's VIs (what lvai_example_index answers, so what an agent opens
/// next), each only when it has no NI entry and no entry of the current reader - NI's export is
/// served in preference anyway (<see cref="CachedReader"/>). Never vi.lib (~20 000 VIs, read one
/// at a time when asked).
///
/// WHAT IT COSTS A SESSION: at most <see cref="Parallel"/> reads at once out of the worker pool's
/// four slots, so a tool call never waits behind it; it starts after <see cref="StartDelay"/> so
/// the start-up's own work goes first. A VI the reader failed on is written to the marker and not
/// retried until the reader changes.
///
/// It runs on the pylabview backend only, and <see cref="Variable"/>=0 switches it off. Before it
/// reads, the reader entries of older reader code are deleted (they can never be hit again).
/// </summary>
internal static class ReaderPrewarm
{
    public const string Variable = "LABVIEWMCP_READER_PREWARM";

    /// <summary>Reads at once - half the worker pool, so tool calls always find a slot.</summary>
    public const int Parallel = 2;

    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    /// <summary>Per VI. The examples' slowest reads measured in the censuses stay well below it.</summary>
    private const int ReadTimeoutSeconds = 120;

    /// <summary>Bump when the marker changes shape.</summary>
    private const int MarkerVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <param name="Stamp">The reader code's hash these failures belong to.</param>
    /// <param name="Failed">VIs the reader could not read, not retried under this stamp.</param>
    internal sealed record Marker(int Version, string Stamp, List<string> Failed, string? FinishedUtc);

    /// <summary>Where the run stands, for pylv_status. Null until a run has begun.</summary>
    internal sealed record Progress(string State, int Targets, int Done, int Read, int AlreadyCached,
                                    int Failed, int StaleRemoved, string? Note);

    private static volatile Progress? _progress;

    public static Progress? Current => _progress;

    public static bool Enabled =>
        Environment.GetEnvironmentVariable(Variable) is not { } v ||
        !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase));

    public static string MarkerPath() => Path.Combine(CacheDirectory.Root, "reader-prewarm.json");

    /// <summary>Fire-and-forget from the start-up; every failure is swallowed into the progress note.</summary>
    public static Task RunAsync(ILogger log) =>
        Task.Run(async () =>
        {
            try
            {
                if (!Enabled) { _progress = Skipped($"{Variable} switches it off."); return; }
                var backend = AixmlBackend.ServerDefault().Name;
                if (backend != AixmlBackend.PyLabview)
                {
                    _progress = Skipped($"The server's default backend is {backend}; reader entries would not be served.");
                    return;
                }
                if (AixmlComposer.Locate(out _) is not { } setup || AixmlExportStore.ReaderStamp() is not { } stamp)
                {
                    _progress = Skipped("No pylabview bundle or composer here - nothing to read with.");
                    return;
                }

                var stale = AixmlExportStore.ReapStaleReader(stamp);
                if (stale > 0) log.LogInformation("Removed {Count} reader cache entr(ies) of older reader code.", stale);

                await Task.Delay(StartDelay);
                var targets = ExampleIndex.Build().Examples
                    .Select(e => e.Path)
                    .Where(p => p.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var summary = await RunCoreAsync(setup, stamp, targets, MarkerPath(), null, stale, default);
                log.LogInformation("Reader pre-warm: {Read} read, {Cached} already cached, {Failed} failed, of {Targets}.",
                                   summary.Read, summary.AlreadyCached, summary.Failed, summary.Targets);
            }
            catch (Exception ex)
            {
                _progress = Skipped("Stopped: " + ex.Message);
            }
        });

    private static Progress Skipped(string why) => new("skipped", 0, 0, 0, 0, 0, 0, why);

    /// <summary>
    /// The run itself, over <paramref name="targets"/>: each VI not cached for NI nor for the
    /// current reader is read through <see cref="CachedReader"/>, which stores it. A test passes its
    /// own targets, marker and roots.
    /// </summary>
    internal static async Task<Progress> RunCoreAsync(
        AixmlComposer.Setup setup, string stamp, IReadOnlyList<string> targets, string markerPath,
        IReadOnlyList<string>? roots, int staleRemoved, CancellationToken ct)
    {
        var marker = LoadMarker(markerPath);
        var known = marker is not null && marker.Stamp == stamp
            ? new HashSet<string>(marker.Failed, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>(known);
        var gate = new object();
        int done = 0, read = 0, cached = 0;
        _progress = new Progress("running", targets.Count, 0, 0, 0, failed.Count, staleRemoved, null);

        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "reader-prewarm", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var slots = new SemaphoreSlim(Parallel);
        try
        {
            var work = targets.Select(async (vi, i) =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var outcome = 0;   // 0 failed before (already listed), 1 read, 2 cached, 3 failed
                    if (known.Contains(vi)) { }
                    else if (!AixmlExportStore.IsCacheable(vi, roots)
                        || AixmlExportStore.CachedUtc(vi, AixmlBackend.Grpc) is not null
                        || AixmlExportStore.CachedUtc(vi, AixmlBackend.PyLabview) is not null)
                        outcome = 2;
                    else
                    {
                        try
                        {
                            var o = await CachedReader.ReadAsync(setup, vi, Path.Combine(scratch, $"{i}.xml"),
                                                                 ReadTimeoutSeconds, ct, roots: roots);
                            outcome = o.Result.Written && o.Stored ? 1 : o.Result.Written ? 2 : 3;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            outcome = 3;
                        }
                        try { File.Delete(Path.Combine(scratch, $"{i}.xml")); } catch { /* scratch */ }
                    }
                    lock (gate)
                    {
                        done++;
                        if (outcome == 1) read++;
                        else if (outcome == 2) cached++;
                        else if (outcome == 3) failed.Add(vi);
                        _progress = new Progress("running", targets.Count, done, read, cached, failed.Count, staleRemoved, null);
                        // a killed server keeps what it learned: every 25 VIs the marker is written
                        if (done % 25 == 0) SaveMarker(markerPath, new Marker(MarkerVersion, stamp, [.. failed], null));
                    }
                }
                finally
                {
                    slots.Release();
                }
            });
            await Task.WhenAll(work);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch */ }
        }

        SaveMarker(markerPath, new Marker(MarkerVersion, stamp, failed, DateTime.UtcNow.ToString("O")));
        return _progress = new Progress("finished", targets.Count, done, read, cached, failed.Count, staleRemoved, null);
    }

    internal static Marker? LoadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), Options);
            return m is not null && m.Version == MarkerVersion ? m : null;
        }
        catch { return null; }
    }

    private static void SaveMarker(string path, Marker marker)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(marker, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* the marker only saves retries */ }
    }
}
