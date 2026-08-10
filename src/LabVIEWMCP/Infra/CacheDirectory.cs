namespace LabVIEWMcp.Infra;

/// <summary>
/// Where every on-disk cache lives, in one place so a test can move all of them at once.
///
/// It became one place because of a real mess: <see cref="ExampleIndexStore"/> read
/// <c>%LOCALAPPDATA%</c> directly, and the test suite builds indexes over synthetic roots under
/// <c>%TEMP%</c>. Each synthetic root is a distinct cache key, so every <c>dotnet test</c> run wrote
/// a fresh file into the developer's REAL cache and never removed it. MEASURED on this machine:
/// 486 files, of which 485 were test litter keyed on <c>%TEMP%\lvmcp-examples-&lt;guid&gt;</c>. Never
/// wrong - the keys are distinct, so no test ever read another's index - just unbounded.
///
/// <c>LABVIEWMCP_CACHE_DIR</c> overrides it. That is what the tests set, and it doubles as the
/// answer for anyone who wants the cache off a roaming or space-constrained profile.
/// </summary>
internal static class CacheDirectory
{
    /// <summary>Environment variable that relocates every cache below.</summary>
    public const string OverrideVariable = "LABVIEWMCP_CACHE_DIR";

    /// <summary>Where the cache lived before <see cref="Root"/> moved out of AppData.</summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LabVIEWMCP", "cache");

    /// <summary>
    /// The cache root: <c>%USERPROFILE%\.labviewmcp\cache</c>.
    ///
    /// It used to be <c>%LOCALAPPDATA%\LabVIEWMCP\cache</c>, which is the conventional answer and was
    /// the wrong one here. MEASURED: when the server is started by the Claude desktop app it inherits
    /// that packaged app's filesystem redirection, and EVERY level created under
    /// <c>%LOCALAPPDATA%</c> becomes a reparse point into
    /// <c>%LOCALAPPDATA%\Packages\Claude_&lt;id&gt;\LocalCache\Local\…</c>. Probed side by side:
    /// a directory made under <c>%LOCALAPPDATA%</c> reported that target, one made under
    /// <c>%USERPROFILE%</c> reported none.
    ///
    /// Two things followed from that, and both are why this moved. File Explorer, running outside the
    /// container, refuses the redirected directory with "Location is not available" for a folder that
    /// demonstrably holds files - an hour went into believing the cache was broken when it was
    /// working. And more importantly the location was **not the same for every host**: launched from
    /// the Claude app the cache landed in the package's private store, launched from a terminal or
    /// another MCP client it landed in the plain path, so one machine ended up with two caches and
    /// warming one did nothing for the other. <c>%USERPROFILE%</c> is not redirected, so there is now
    /// one location regardless of who starts the server.
    ///
    /// It stays out of the roaming profile for the original reason: the cache is machine-specific and
    /// must not follow a user to a machine with a different LabVIEW. <c>%USERPROFILE%</c> itself does
    /// not roam; only <c>AppData\Roaming</c> does.
    ///
    /// Read on every call rather than cached in a static: a test sets the variable per fixture, and
    /// a value captured at first touch would leak the first fixture's directory into all the others.
    /// </summary>
    public static string Root
    {
        get
        {
            var over = Environment.GetEnvironmentVariable(OverrideVariable);
            return string.IsNullOrWhiteSpace(over) ? DefaultRoot : over;
        }
    }

    /// <summary>
    /// What <see cref="Root"/> is without an override. Exposed separately so a test can assert on
    /// the default WITHOUT clearing the override for the whole process - which is not a theoretical
    /// worry: a test that did exactly that leaked a synthetic-root index file into the real cache,
    /// and the leak then blocked <see cref="MigrateLegacy"/> on the next start-up, because a
    /// non-empty destination is how it declines to overwrite a real cache.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".labviewmcp", "cache");

    /// <summary>
    /// Move a cache left at <see cref="LegacyRoot"/> to <see cref="Root"/>. Returns how many files
    /// moved; 0 when there was nothing to do.
    ///
    /// Worth doing rather than starting cold: the example index alone is a 55-second rescan, and a
    /// silent one - the first caller after the move would sit through it wondering what hung. Only
    /// ever moves INTO an empty destination, so a cache already in the new place is never touched,
    /// and never throws: failing to migrate is not a reason to fail start-up.
    /// </summary>
    public static int MigrateLegacy()
    {
        try
        {
            // An explicit override means the operator chose a location; migrating into it would be
            // second-guessing them.
            if (!string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(OverrideVariable))) return 0;

            var from = LegacyRoot;
            var to = Root;
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 0;
            if (!Directory.Exists(from)) return 0;
            if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return 0;

            var moved = 0;
            foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, source);
                var target = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Move(source, target, overwrite: true); moved++; }
                catch { /* one stuck file must not abort the move */ }
            }

            // Leave the old tree behind if anything is still in it; an empty one is just clutter.
            try
            {
                if (!Directory.EnumerateFileSystemEntries(from).Any())
                    Directory.Delete(from, recursive: true);
            }
            catch { /* best effort */ }

            return moved;
        }
        catch { return 0; }
    }

    /// <summary>
    /// For transient files LabVIEW writes on our behalf and nobody keeps - an export whose only
    /// purpose is to be parsed, for instance. Under the cache root rather than <c>%TEMP%</c> so
    /// everything the server produces sits in one place that always exists.
    ///
    /// MEASURED, because there was a real reason to doubt it: LabVIEW's <c>Save\3AInstrument</c>
    /// fails with <c>Error 7</c> when saving a VI under <c>%LOCALAPPDATA%</c> - twice, minutes
    /// apart, with the directory present and writable - which is why the generated HELPER VIs still
    /// live in <c>%TEMP%</c> (see <c>IconTools</c>). That limit turns out to be specific to saving a
    /// VI: <c>ConvertVIToAIXML</c> wrote a 24 774-byte export straight into this directory with
    /// <c>errorCode 0</c>. So ordinary file writes are fine here and only VIs are not.
    /// </summary>
    public static string Scratch => Path.Combine(Root, "scratch");
}
