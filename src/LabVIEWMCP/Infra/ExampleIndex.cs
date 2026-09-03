using System.Text;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>One shipping example, as the Example Finder knows it.</summary>
/// <param name="Name">
/// The bare file name, e.g. "Scale TDMS Data.vi". Usually a .vi, but an exbins index also
/// registers whole example PROJECTS - 37 .lvproj against 528 .vi on this station.
/// </param>
/// <param name="Path">
/// Absolute path. For a .vi hand it to ConvertVIToAIXML to read the diagram; for a .lvproj that
/// call is the wrong one and DescribeProject is the follow-up.
/// </param>
/// <param name="Category">Folder path below the examples root, e.g. "File IO\TDMS".</param>
/// <param name="Source">Empty for LabVIEW's own tree, otherwise the add-on that ships it.</param>
/// <param name="Description">NI's own one-paragraph description, tags stripped.</param>
/// <param name="Keywords">Search keywords the example declares, de-duplicated.</param>
/// <param name="RequiredSoftware">e.g. "LabVIEW >= 13.0", or null when unstated.</param>
internal sealed record ExampleVi(
    string Name, string Path, string Category, string Source,
    string Description, IReadOnlyList<string> Keywords, string? RequiredSoftware);

/// <summary>An examples tree to scan, and the label its entries are reported under.</summary>
internal sealed record ExampleSource(string Label, string ExamplesFolder);

/// <summary>
/// The shipping LabVIEW examples of this installation, read from the example VIs themselves.
///
/// Why this exists: designing a new VI from primitives when a working example already does the
/// job is the expensive mistake, and until now there was no index to check. The palette index
/// answers "which VI may I call"; this answers "has NI already wired this up", one level higher -
/// a whole diagram rather than a single node.
///
/// FORMAT, measured 2026-08-07 against LabVIEW 2026 (26.3f0). A listed example carries its
/// Example Finder metadata as a PLAIN-TEXT XML block inside the .vi binary, root element
/// &lt;ExampleProgram&gt;:
///
///   &lt;Title&gt;&lt;Text Locale="US"&gt;Scale TDMS Data.vi&lt;/Text&gt;&lt;/Title&gt;
///   &lt;Description&gt;&lt;Text Locale="US"&gt;This example demonstrates ...&lt;/Text&gt;&lt;/Description&gt;
///   &lt;Keywords&gt;&lt;Item&gt;TDMS&lt;/Item&gt; ...&lt;/Keywords&gt;
///   &lt;Navigation&gt;&lt;Item&gt;2997&lt;/Item&gt;&lt;/Navigation&gt;      &lt;- node id into the dtree category tree
///   &lt;RequiredSoftware&gt;&lt;NiSoftware MinVersion="13.0"&gt;LabVIEW&lt;/NiSoftware&gt;&lt;/RequiredSoftware&gt;
///
/// So no LabVIEW has to be running to build this index, and no gRPC call is needed: it is a file
/// scan. That matters because the one official search RPC, SearchInfoCache, did not return at all
/// for the term "TDMS" on this station - it blocked until the deadline and recovered afterwards.
///
/// THE &lt;ExampleProgram&gt; WRAPPER IS OPTIONAL, and assuming otherwise cost a first version of
/// this class two thirds of its results. Measured on this station: 498 VIs under
/// &lt;LabVIEW&gt;\examples carry &lt;Title&gt; and &lt;Description&gt;, but only 180 wrap them in
/// &lt;ExampleProgram&gt;; the other 318 start straight at &lt;Title&gt;. Anchoring on the wrapper
/// found 373 examples in total where anchoring on the fields finds well over 800. The extractor
/// therefore opens at the earliest of &lt;ExampleProgram&gt;, &lt;Title&gt; or &lt;Description&gt;,
/// closes at the last known closing tag, and adds the wrapper when the file omits it.
///
/// THE BLOCK IS ALSO THE FILTER. Of 1687 .vi files under &lt;LabVIEW&gt;\examples only 498 carry it;
/// the rest are subVIs and support code living inside an example's folder. Listing those would
/// bury the ones that are actually meant to be opened.
///
/// A VI WITHOUT THE BLOCK IS NOT UNDOCUMENTED. Queued Message Handler Fundamentals.vi has no
/// &lt;ExampleProgram&gt; yet FilterExampleSearchCandidates returns a full description for it, because
/// that RPC reads the VI's own description property - it works on any VI, including vi.lib. The
/// two sources are complementary: this index covers what the Example Finder lists, the RPC covers
/// everything else.
///
/// ADD-ONS SHIP EXAMPLES TOO, under %ProgramFiles%\NI\LVAddons\&lt;addon&gt;\&lt;api&gt;\examples - 299
/// further examples across 14 add-ons here. Missing those would hide entire toolkits, so both
/// roots are scanned; see <see cref="AddonTree"/>.
///
/// SOME EXAMPLES CARRY NO BLOCK AT ALL and register through an external binary index instead -
/// `exbins\*.bin4`, and older `*.bin3` under &lt;LabVIEW&gt;\examples\exbins. NI-DAQmx is the case
/// that matters: 56 examples, not one of them findable by scanning VIs, so a query for "DAQmx"
/// used to come back empty while they sat on disk. <see cref="ExternalExampleIndex"/> reads those
/// files; the count that came from them is <see cref="Result.FromExternalIndexes"/>.
///
/// An index file that does not fit that format is skipped WHOLE and named in
/// <see cref="Result.ExternalIndexes"/>, never half-read: a mis-paired description is worse than
/// a missing one, and a silently absent driver is the bug this scan exists to avoid.
/// </summary>
internal static class ExampleIndex
{
    private static readonly object Gate = new();
    private static Dictionary<string, Result> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Largest block accepted, so a stray tag cannot drag in half the binary.</summary>
    private const int MaxBlockBytes = 64 * 1024;

    private const string Root = "<ExampleProgram>";

    // Earliest of these opens the block. <ExampleProgram> is OPTIONAL - see the class remarks.
    private static readonly byte[][] Openers =
        [.. new[] { Root, "<Title>", "<Description>" }.Select(Encoding.ASCII.GetBytes)];

    // Latest of these closes it. The schema's element order is fixed but every part is optional,
    // so the end is whichever known closing tag sits furthest into the window.
    private static readonly byte[][] Closers =
        [.. new[]
        {
            "</ExampleProgram>", "</RequiredSoftware>", "</ProgrammingLanguages>", "</Metadata>",
            "</FileType>", "</Navigation>", "</Keywords>", "</Description>", "</Title>",
        }.Select(Encoding.ASCII.GetBytes)];

    /// <param name="FromExternalIndexes">
    /// How many of <paramref name="Examples"/> came from an exbins index rather than from a VI.
    /// </param>
    /// <param name="ExternalIndexes">
    /// Index files that did NOT fit the format and were therefore skipped whole - the remaining
    /// gap, named so it stays visible.
    /// </param>
    internal sealed record Result(
        string LabViewRoot, string ExamplesFolder, int ViFilesScanned,
        IReadOnlyList<ExampleVi> Examples,
        IReadOnlyList<string> AddonsScanned, IReadOnlyList<string> AddonsSkipped,
        IReadOnlyList<string> ExternalIndexes, IReadOnlyList<string> Unreadable,
        int FromExternalIndexes);

    /// <summary>
    /// The index for the newest installed LabVIEW, or for <paramref name="labviewRoot"/> when
    /// given. Cached per root for the process lifetime; <paramref name="refresh"/> rescans.
    ///
    /// Add-on examples are discovered automatically only when <paramref name="labviewRoot"/> is
    /// left to default, so a test pointing at a synthetic tree does not pick up the real machine's
    /// drivers. Pass <paramref name="addonsRoot"/> to scan both.
    /// </summary>
    public static Result Build(string? labviewRoot = null, bool refresh = false,
                               string? addonsRoot = null)
    {
        var install = labviewRoot is null ? LabViewLocator.Select(LabViewLocator.Discover()) : null;
        var root = labviewRoot ?? (install is null ? null : Path.GetDirectoryName(install.ExePath))
            ?? throw new InvalidOperationException(
                "No LabVIEW installation found, so its examples cannot be read. Pass installRoot " +
                "explicitly, or check that LabVIEW is installed under a Program Files root.");

        var addons = addonsRoot ?? (labviewRoot is null ? AddonTree.DefaultRoot() : null);
        var key = root + "|" + (addons ?? "");

        lock (Gate)
        {
            if (!refresh)
            {
                // Memory first, then the machine-wide cache on disk. Without the second the
                // 55-second scan came back on every server restart; see ExampleIndexStore.
                if (_cache.TryGetValue(key, out var cached)) return cached;

                if (ExampleIndexStore.TryLoad(key) is { } stored)
                {
                    _cache = Remember(key, stored);
                    return stored;
                }
            }

            var result = Scan(root, addons, install?.Release);
            _cache = Remember(key, result);
            ExampleIndexStore.Save(key, result);
            return result;
        }
    }

    private static Dictionary<string, Result> Remember(string key, Result result) =>
        new(_cache, StringComparer.OrdinalIgnoreCase) { [key] = result };

    /// <summary>
    /// Build the index off the hot path, swallowing every failure.
    ///
    /// Called once as the server starts. The point is not speed but WHEN the cost is paid: a
    /// scan that runs at start-up delays nobody, where the same scan on the first tool call is a
    /// 55-second silence in the middle of a conversation. A caller arriving mid-scan blocks on
    /// the same lock and then gets the finished index, so warming never causes a second scan.
    /// </summary>
    public static Task WarmAsync(string? labviewRoot = null, string? addonsRoot = null) =>
        Task.Run(() =>
        {
            try { Build(labviewRoot, refresh: false, addonsRoot); }
            catch { /* no LabVIEW, no examples folder, no permissions - all fine, just no warm */ }
        });

    /// <summary>The cache key for a pair of roots, as <see cref="Build"/> computes it.</summary>
    public static string KeyFor(string? labviewRoot, string? addonsRoot)
    {
        var install = labviewRoot is null ? LabViewLocator.Select(LabViewLocator.Discover()) : null;
        var root = labviewRoot ?? (install is null ? null : Path.GetDirectoryName(install.ExePath));
        var addons = addonsRoot ?? (labviewRoot is null ? AddonTree.DefaultRoot() : null);
        return (root ?? "") + "|" + (addons ?? "");
    }

    /// <summary>One example read off disk: its key in <c>found</c>, and the entry itself.</summary>
    private sealed record ScannedExample(string Relative, ExampleVi Vi);

    private static Result Scan(string root, string? addonsRoot, int? release)
    {
        var examples = Path.Combine(root, "examples");
        if (!Directory.Exists(examples))
            throw new DirectoryNotFoundException(
                $"'{examples}' does not exist, so this installation ships no examples.");

        var sources = new List<ExampleSource> { new("", examples) };
        var (addonFolders, skipped) = AddonTree.Enumerate(addonsRoot, "examples", release);
        sources.AddRange(addonFolders.Select(f => new ExampleSource(f.Addon, f.Folder)));

        // Keyed by the path RELATIVE TO ITS OWN ROOT, deliberately without the source label:
        // aspt32 and aspt64 ship an identical set of 104 examples, and keying by source would
        // list every one of them twice. First source wins, as in PaletteIndex. The bare file name
        // would be the wrong key in the other direction - "Read Data.vi" is a distinct example in
        // each of several folders.
        var found = new Dictionary<string, ExampleVi>(StringComparer.OrdinalIgnoreCase);
        var externals = new List<string>();
        var unreadable = new List<string>();
        var scanned = 0;
        var fromExternal = 0;

        foreach (var source in sources)
        {
            // Materialised and sorted before reading. Sorting is not cosmetic: the merge below is
            // first-one-wins, and the order EnumerateFilesSafely produces is the filesystem's,
            // which is stable in practice and promised nowhere. Fixing it here means the scan
            // answers the same way twice whatever the readers do.
            // `"*.vi"` IS NOT AN EXACT EXTENSION MATCH ON WINDOWS. The pattern also matches every
            // extension that BEGINS with `vi` - `.vit` templates above all - because the file
            // system still matches against 8.3 short names. Latent from the start and harmless
            // until something dropped such a file into an examples tree: installing LUnit put five
            // `Test Method Template.vit` files under `examples\Astemes\LUnit\`, and they turned
            // up in the index as examples on 2026-09-02. A `.vit` is a template, not a diagram to
            // read, and feeding one to lvai_convert_vi_to_aixml is not what the caller asked for.
            var files = EnumerateFilesSafely(source.ExamplesFolder, "*.vi")
                .Where(IsListable)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Reading and parsing run concurrently - see ParallelScan for why that is worth about
            // 21x on a cold tree - but the merge that follows stays sequential and in file order.
            var read = ParallelScan.Map(files, (file, bytes) =>
            {
                var block = ExtractBlock(bytes);
                if (block is null) return null;            // a subVI, not a listed example

                var relative = Path.GetRelativePath(source.ExamplesFolder, file);
                var parsed = Parse(block);

                return new ScannedExample(relative, new ExampleVi(
                    Path.GetFileName(file), file, Path.GetDirectoryName(relative) ?? "",
                    source.Label, parsed.Description, parsed.Keywords, parsed.RequiredSoftware));
            });

            for (var i = 0; i < files.Count; i++)
            {
                if (!read[i].Read) { unreadable.Add(files[i]); continue; }  // a locked one is not fatal

                scanned++;
                if (read[i].Value is { } example) found.TryAdd(example.Relative, example.Vi);
            }

            // Examples that carry no in-VI block and register through an external index instead.
            // Read AFTER the VIs of the same source, so where both describe an example the in-VI
            // block wins - it is the authoritative one and the only one carrying RequiredSoftware.
            foreach (var external in EnumerateFilesSafely(source.ExamplesFolder, "*.bin3")
                         .Concat(EnumerateFilesSafely(source.ExamplesFolder, "*.bin4")))
            {
                var label = source.Label.Length == 0
                    ? Path.GetFileName(external)
                    : source.Label + ": " + Path.GetFileName(external);

                byte[] bytes;
                try { bytes = File.ReadAllBytes(external); }
                catch { unreadable.Add(external); continue; }

                var registered = ExternalExampleIndex.Read(bytes);
                if (registered.Count == 0)
                {
                    // Never drop a driver silently - that omission is the bug this scan exists
                    // to avoid, and an unreadable index is exactly how it would happen again.
                    externals.Add(label);
                    continue;
                }

                foreach (var entry in registered)
                {
                    var path = Path.Combine(source.ExamplesFolder, entry.RelativePath);
                    if (!File.Exists(path)) continue;      // a registration for an absent example

                    // AN EXTERNAL INDEX MAY REGISTER SOMETHING THAT IS NOT AN EXAMPLE TO READ.
                    // LUnit's does: five `Test Method Template.vit` entries under
                    // examples\Astemes\LUnit\, found 2026-09-02. A `.vit` is a template - there
                    // is no diagram to adapt and lvai_convert_vi_to_aixml is not the follow-up - so
                    // listing one answers a search with something the caller cannot use.
                    if (!IsListable(path)) continue;

                    if (found.TryAdd(entry.RelativePath, new ExampleVi(
                            Path.GetFileName(path), path,
                            Path.GetDirectoryName(entry.RelativePath) ?? "", source.Label,
                            entry.Description, entry.Keywords, null)))
                        fromExternal++;
                }
            }
        }

        var list = found.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Result(root, examples, scanned, list,
                          addonFolders.Select(f => f.Addon).Distinct().ToList(), skipped,
                          externals, unreadable, fromExternal);
    }

    /// <summary>
    /// Recursive enumeration that survives a folder it may not read. The plain
    /// EnumerateFiles(..., AllDirectories) throws part-way through and loses everything already
    /// found, which on a Program Files tree is a real risk rather than a theoretical one.
    /// </summary>
    /// <summary>
    /// Whether a path is something this index should LIST: a `.vi` to read, or a `.lvproj` that is
    /// a whole example application.
    ///
    /// TWO WAYS A NON-EXAMPLE GETS IN, and this closes both. The scan's pattern `"*.vi"` is not an
    /// exact extension match on Windows - it also matches every extension BEGINNING with `vi`,
    /// because the file system still matches 8.3 short names - and an external exbins index can
    /// simply register one, which is what LUnit does with five `.vit` templates.
    /// </summary>
    private static bool IsListable(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".vi", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".lvproj", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string folder, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(folder);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(current, pattern); }
            catch { continue; }
            foreach (var file in files) yield return file;

            string[] directories;
            try { directories = Directory.GetDirectories(current); }
            catch { continue; }
            foreach (var directory in directories) stack.Push(directory);
        }
    }

    /// <summary>
    /// The Example Finder block as parseable XML, or null when the VI carries none. Always rooted
    /// in &lt;ExampleProgram&gt;: when the file omits that wrapper one is added, so callers see a
    /// single shape.
    ///
    /// Latin-1 rather than UTF-8 so a stray high byte elsewhere in the binary cannot throw; the
    /// block's own content is ASCII in every example measured here.
    /// </summary>
    internal static string? ExtractBlock(byte[] bytes)
    {
        var span = bytes.AsSpan();

        var start = -1;
        foreach (var opener in Openers)
        {
            var at = span.IndexOf(opener);
            if (at >= 0 && (start < 0 || at < start)) start = at;
        }
        if (start < 0) return null;

        var region = span.Slice(start, Math.Min(span.Length - start, MaxBlockBytes));

        var end = -1;
        foreach (var closer in Closers)
        {
            var at = region.LastIndexOf(closer);
            if (at >= 0 && at + closer.Length > end) end = at + closer.Length;
        }
        if (end < 0) return null;

        var text = Encoding.Latin1.GetString(bytes, start, end);
        return text.StartsWith(Root, StringComparison.Ordinal)
            ? text
            : Root + text + "</ExampleProgram>";
    }

    /// <summary>
    /// Description, keywords and required software from one block. A block that will not parse
    /// still yields an entry with an empty description: the example exists either way, and
    /// dropping it would make a parser bug look like a missing example.
    /// </summary>
    internal static (string Description, IReadOnlyList<string> Keywords, string? RequiredSoftware)
        Parse(string block)
    {
        try
        {
            var root = XDocument.Parse(block).Root!;

            var description = Collapse(
                root.Element("Description")?.Element("Text")?.Value ?? "");

            var keywords = root.Element("Keywords")?.Elements("Item")
                .Select(i => i.Value.Trim())
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)   // "files" is listed twice verbatim
                .ToList() ?? [];

            var software = root.Element("RequiredSoftware")?.Elements("NiSoftware")
                .Select(s =>
                {
                    var minimum = s.Attribute("MinVersion")?.Value;
                    var name = s.Value.Trim();
                    return minimum is null ? name : $"{name} >= {minimum}";
                })
                .Where(s => s.Length > 0)
                .ToList() ?? [];

            return (description, keywords,
                    software.Count == 0 ? null : string.Join(", ", software));
        }
        catch
        {
            return ("", [], null);
        }
    }

    /// <summary>One line of running text: NI's descriptions carry newlines, tabs and &lt;B&gt; tags.</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        var inTag = false;

        foreach (var c in text)
        {
            if (c == '<') { inTag = true; continue; }
            if (inTag) { if (c == '>') inTag = false; continue; }

            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
