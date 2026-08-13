using System.Diagnostics;
using System.Reflection;
using System.Text;
using LabVIEWMcp.Tools;
using Xunit;
using Xunit.Abstractions;

namespace LabVIEWMCP.Tests.Tools;

/// <summary>
/// What the batched lookup and the document cache actually cost in TIME, as opposed to in text.
///
/// This is deliberately a separate file from KnowledgeToolsTests: it exists to PRINT NUMBERS.
///
/// It asserts nothing about time, and that is a correction rather than an omission. The first
/// version asserted the direction of the result - batched faster than N singles, cached faster
/// than rebuilding - "with a wide margin". It passed in isolation and **failed inside the full
/// suite**, where 0.795 ms against 1.376 ms is close enough to invert under contention from the
/// other 698 tests. Gating a build on wall-clock measured next to a parallel test run is wrong
/// regardless of the margin chosen, so the timing assertions are gone.
///
/// What is pinned instead is the thing that would actually be a bug: the caches must not change
/// the ANSWER. Speed is reported for humans; correctness is asserted.
///
/// Run it with:
///   dotnet test --filter FullyQualifiedName~ReferenceLookupTiming --logger "console;verbosity=detailed"
/// </summary>
public class ReferenceLookupTimingTests(ITestOutputHelper output)
{
    /// <summary>The 18 terms one VI generation looked up, one call at a time.</summary>
    private static readonly string[] Terms = [
        "Build Waveform", "Index Array", "Array Size", "disabled index", "Unbundle By Name",
        "Select", "String To Path", "waveform", "Empty String", "Greater?", "Subtract",
        ".and.", ".not. x", "Match Pattern", "Array Subset", "Read Delimited Spreadsheet",
        "Time Stamp", "Not An Error"];

    private const int Repeats = 20;

    [Fact]
    public void CachesDoNotChangeTheAnswer_AndTheCostIsReported()
    {
        // Warm up: JIT, and populate the caches so the steady state is what gets measured.
        for (var i = 0; i < 3; i++)
        {
            foreach (var t in Terms) KnowledgeTools.AixmlReference(node: t);
            KnowledgeTools.AixmlReference(node: string.Join(',', Terms));
        }

        var document = KnowledgeTools.Load();

        // --- what the cache removed: the per-call cost every lookup used to pay ---
        var rawRead = Time(Repeats, () => ReadResourceUncached("aixml-reference.md"));
        var split = Time(Repeats, () => KnowledgeTools.Split(document));

        // A fresh string instance defeats the by-reference index cache, so this is the old
        // per-call index build plus the lookup itself.
        var uncachedLookup = Time(Repeats, () =>
            KnowledgeTools.Lookup(Fresh(document), "Build Waveform", 40));
        var cachedLookup = Time(Repeats, () =>
            KnowledgeTools.Lookup(document, "Build Waveform", 40));

        // --- the two shapes of the real workload ---
        var singles = Time(Repeats, () =>
        {
            foreach (var t in Terms) KnowledgeTools.AixmlReference(node: t);
        });
        var batched = Time(Repeats, () => KnowledgeTools.AixmlReference(node: string.Join(',', Terms)));

        // The reconstructed pre-change cost: every one of the 18 calls re-read the resource,
        // re-split it and rebuilt the index.
        var oldStyle = Terms.Length * (rawRead + uncachedLookup);

        output.WriteLine($"per-call costs the cache removed   (mean of {Repeats})");
        output.WriteLine($"  read embedded resource (146 kB) : {rawRead,8:F3} ms");
        output.WriteLine($"  split into sections             : {split,8:F3} ms");
        output.WriteLine($"  lookup, index rebuilt each time : {uncachedLookup,8:F3} ms");
        output.WriteLine($"  lookup, index cached            : {cachedLookup,8:F3} ms  " +
                         $"({uncachedLookup / cachedLookup:F0}x faster)");
        output.WriteLine("");
        output.WriteLine($"the 18-term workload               (mean of {Repeats})");
        output.WriteLine($"  BEFORE: 18 calls, nothing cached : {oldStyle,8:F3} ms  (reconstructed)");
        output.WriteLine($"  18 separate calls, cached        : {singles,8:F3} ms");
        output.WriteLine($"  1 batched call, cached           : {batched,8:F3} ms");
        output.WriteLine("");
        output.WriteLine($"  batch vs 18 cached calls         : {singles / batched,8:F1}x faster");
        output.WriteLine($"  batch vs before the change       : {oldStyle / batched,8:F1}x faster");
        output.WriteLine("");
        output.WriteLine("NOTE: server-side only. It excludes MCP round-trip overhead, which is");
        output.WriteLine("      what 17 fewer calls actually saves end to end - and that dwarfs");
        output.WriteLine("      everything above: a turn costs seconds, this table costs ms.");

        // The assertions: caching must not alter the result. A by-reference index cache is exactly
        // the kind of thing that could serve a stale or mismatched index, and unlike the timings
        // this is deterministic.
        foreach (var term in Terms)
            Assert.Equal(KnowledgeTools.Lookup(Fresh(document), term, 40),
                         KnowledgeTools.Lookup(document, term, 40));

        Assert.Same(KnowledgeTools.Load(), KnowledgeTools.Load());
        Assert.Equal(ReadResourceUncached("aixml-reference.md"), KnowledgeTools.Load());
    }

    private static double Time(int repeats, Action work)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < repeats; i++) work();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / repeats;
    }

    /// <summary>A distinct string with the same content, to miss the by-reference index cache.</summary>
    private static string Fresh(string s) => new(s.ToCharArray());

    /// <summary>What Load() did on every single call before it was cached.</summary>
    private static string ReadResourceUncached(string resourceName)
    {
        var assembly = typeof(KnowledgeTools).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"missing resource {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
