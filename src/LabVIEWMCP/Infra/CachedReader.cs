namespace LabVIEWMcp.Infra;

/// <summary>
/// The pylabview backend's VI -> AIXML read, THROUGH the export cache. Every tool that reads with
/// vi2aixml.py comes here, so the cache rules are one implementation rather than four.
///
/// WHAT IT SERVES, in this order (the user's choice of 2026-10-15):
///   1. NI's converter export of the VI, when one is cached - it is the higher-fidelity reading
///      (the reader matches it in every fact for 392 of 747 VIs), and serving it needs no NI
///      service, so a pylabview call loses nothing by taking it. The answer says whose it was.
///   2. The reader's own earlier export, keyed on the VI AND the reader's code hash, so a fixed
///      reader never serves what the old one wrote.
///   3. A fresh read, written to the cache under the reader's key.
/// A grpc call never reaches this class, and is never served a reader entry: the store keys the
/// two apart. Only installation VIs are cached, as for NI's exports - <see cref="AixmlExportStore"/>
/// has why.
/// </summary>
internal static class CachedReader
{
    /// <param name="Result">The read's own result; a cache hit is a written, clean one.</param>
    /// <param name="ServedFrom">Null for a fresh read, else the backend whose cached export it was.</param>
    /// <param name="Note">
    /// Whole sentences saying what came back and from where, ready to stand alone. They used to be
    /// a clause each callers prefixed with their own "read by vi2aixml.py", which printed that
    /// twice and ended "re-read.." - seen in the live check of 2026-10-15.
    /// </param>
    internal sealed record Outcome(AixmlComposer.Result Result, string? ServedFrom, bool Stored,
                                   string Note);

    /// <summary>
    /// The reader's own words for an export it made: whose it is and how far to trust it. Not part
    /// of <see cref="Outcome.Note"/>, which says only where the export came from - a caller with its
    /// own provenance field (lvai_convert_vi_to_aixml's readerNote) would print it twice.
    /// </summary>
    internal const string ReaderProvenance =
        "Read by vi2aixml.py, the pylabview backend's reader - NOT by NI's converter, and no " +
        "LabVIEW was asked. A name or fact that differs from NI's export is the reader's until " +
        "shown otherwise.";

    public static async Task<Outcome> ReadAsync(
        AixmlComposer.Setup setup, string viPath, string aixmlPath, int timeoutSeconds,
        CancellationToken ct, bool refresh = false, IReadOnlyList<string>? roots = null)
    {
        if (!refresh)
            foreach (var backend in new[] { AixmlBackend.Grpc, AixmlBackend.PyLabview })
                if (AixmlExportStore.TryCopyTo(viPath, aixmlPath, roots, backend))
                {
                    var taken = AixmlExportStore.CachedUtc(viPath, backend);
                    var when = taken is { } t ? $", taken {t:yyyy-MM-dd HH:mm}Z" : "";
                    var note = backend == AixmlBackend.Grpc
                        ? $"NI's converter export of this VI, served from the export cache{when}. " +
                          "The pylabview backend prefers it to its own reader's - it is the " +
                          "higher-fidelity reading, and serving it needs no NI service. Pass " +
                          "refresh to read the VI with vi2aixml.py instead."
                        : $"Served from the export cache{when}: the pylabview reader's own earlier read, " +
                          "made by the reader code installed now. " +
                          "Pass refresh to read again.";
                    return new Outcome(new AixmlComposer.Result(true, false, "No Error", [], 0, 0),
                                       backend, false, note);
                }

        var read = await AixmlComposer.ReadAsync(setup, viPath, aixmlPath, timeoutSeconds, ct);
        if (!read.Written)
            return new Outcome(read, null, false, "Not cached: the read failed.");

        var stored = AixmlExportStore.Save(viPath, aixmlPath, roots, AixmlBackend.PyLabview);
        var outcome = stored
            ? $"Written to the export cache under the reader's key - kept apart " +
              "from NI's entries, so a grpc call is never served it."
            : AixmlExportStore.IsCacheable(viPath, roots)
                ? "Not cached: the entry could not be written - another call " +
                  "may have been storing the same VI at that moment; the next read retries."
                : "Not cached: this VI is outside the LabVIEW installation, and " +
                  "an export depends on its subVIs too - your own code is always read afresh.";
        return new Outcome(read, null, stored, outcome);
    }
}
