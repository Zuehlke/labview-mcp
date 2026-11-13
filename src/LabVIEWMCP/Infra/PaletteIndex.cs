using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>One palette-reachable VI, and the palette file it was found in.</summary>
/// <param name="Name">
/// The bare file name, e.g. "General Error Handler.vi" - or, for a member of one of NI Vision's
/// old .llb files, the member name with NO extension ("IMAQ Create"), which is what LabVIEW calls
/// it and what NI's export writes as the `Call` target.
/// </param>
/// <param name="PaletteFile">
/// Path of the .mnu relative to its menus folder, prefixed with the add-on name when the entry
/// came from an LVAddon rather than from LabVIEW itself.
/// </param>
/// <param name="Target">
/// The `Call` target when it is not the bare name: `NI_AALPro.lvlib:FFT.vi` for a library member.
/// Null when the bare name is the target, or when the owner could not be established.
/// </param>
internal sealed record PaletteVi(string Name, string PaletteFile, string? Target = null)
{
    /// <summary>What a `Call` names: the qualified target where there is one, else the name.</summary>
    public string CallTarget => Target ?? Name;
}

/// <summary>A palette tree to scan: a menus folder and the label its entries are reported under.</summary>
/// <param name="AnchorRoot">
/// The folder a `&lt;vilib&gt;` in this tree's palette paths is relative to before the
/// installation's own: an add-on's version (or Targets) folder. Null for the installation's trees.
/// </param>
internal sealed record PaletteSource(string Label, string MenusFolder, string? AnchorRoot = null);

/// <summary>
/// The set of VIs reachable from LabVIEW's palettes, read from the .mnu files of the installed
/// LabVIEW.
///
/// Why this matters: it is the searchable catalogue of what this station HAS. Enumerating vi.lib
/// instead gives 19 616 files, the vast majority of them internal implementation VIs nobody should
/// call, and the info cache comes back empty on a station whose cache is not populated.
///
/// THIS COMMENT CLAIMED THE INDEX WAS THE SET OF LEGAL TARGETS - "AIXML generation accepts a
/// `Call` only to a PALETTE-REACHABLE VI" - AND THAT IS FALSE. Measured 2026-08-27 over eight
/// probes: generation resolves a target BY NAME against what the installation can FIND, and the
/// palette has nothing to do with it. A library member resolves by its qualifier with no palette
/// entry; a loose .vi under vi.lib or user.lib resolves by its bare name with no palette entry and
/// no library - which is exactly what makes a generated placeholder in user.lib callable. What
/// does not resolve: a VI inside an .llb by bare name, a path in any spelling, and project-local
/// code. Read literally, the old sentence argued away 600 usable VIs. Section 9 of
/// docs/aixml-reference.md has the table.
///
/// THE NAMES REPORTED HERE ARE NOT ALWAYS THE CALL TARGET. A palette VI that a LIBRARY owns needs
/// its `lvlib:` qualifier and is refused by bare name - measured on LabVIEW 2026:
/// `Draw Image from File__ogtk.vi` gives "Unsupported SubVI", while
/// `openg_picture.lvlib:Draw Image from File__ogtk.vi` validates, generates and runs. A .mnu
/// records only the bare item name, so the qualifier CANNOT be recovered from this scan: the
/// palette file is functions_oglib_picture.mnu and the VI lives in picture.llb, neither of which
/// names openg_picture.lvlib. Callers have to get it elsewhere - see PaletteTools' tool text and
/// §9 of docs/aixml-reference.md. Reporting a bare name as though it were the target is what makes
/// a perfectly callable VI look uncallable, and sends the caller back to rebuilding from
/// primitives.
///
/// Why it is scanned at call time rather than shipped as a table: the palette is
/// STATION-SPECIFIC. Installed toolkits and add-ons hook themselves into it - a stock
/// flattenstring.mnu here carries a sub-menu reference to the JSONtext add-on - so a checked-in
/// list would be wrong on the next machine.
///
/// IT IS CACHED ON DISK by <see cref="PaletteIndexStore"/>, and the payoff is MEASURED rather than
/// assumed - `LabVIEWMCP --palette` over 743 palette files, three runs each: 221, 217, 218 ms to
/// scan against 102, 97, 130 ms served from the cache. About 110 ms, most of the remainder being
/// process start-up. It was 146/150/148 against 582 files before the loose trees were added, so
/// the sweep roughly doubled the scan and the cache absorbs it. That is still a far smaller win
/// than the example index's, which is 55 SECONDS cold against 804 ms, so do not read the two
/// caches as resting on the same argument.
///
/// The price is staleness, and it bites harder here: a stale palette either hides a VI that exists,
/// which sends a generator off to rebuild from primitives, or offers one that has been uninstalled,
/// which fails as `Unsupported SubVI` at validation. Hence `refresh` after anything is installed,
/// and hence the store's key and format-version guards.
///
/// Format, verified by hex-dump against LabVIEW 2026: a .mnu is an "RSRC"/"LMNU" resource file.
/// Item file names are stored as LENGTH-PREFIXED strings (one leading byte, then that many
/// characters). A PTH0 record - 4-byte tag, big-endian uint32 size, big-endian uint16 type and
/// uint16 component count, then length-prefixed components - names a CONTAINER the items live in.
///
/// THE CONTAINERS ARE READ TOO, since 2026-11-13; this comment said they "carry only the palette's
/// base directory" and were not worth reading. Two kinds of palette were invisible or useless
/// without them:
///
/// - A container can be a LIBRARY: Signal Processing's antransform.mnu lists
///   `&lt;vilib&gt;\Analysis\NI_AALPro.lvlib`, and FFT.vi is that library's member. The bare name
///   `FFT.vi` is refused as a `Call` target, the qualified `NI_AALPro.lvlib:FFT.vi` is not - a
///   colleague's generator, handed the bare name, concluded FFT was uncallable and rebuilt it from
///   primitives. The qualifier IS in the .mnu, as the container. Items in a directory or .llb
///   container are looked up in the libraries of the installation (<see cref="LibraryOwners"/>),
///   which is how `Draw Image from File__ogtk.vi` gets its `openg_picture.lvlib:`.
/// - NI Vision's palettes (Management.mnu: IMAQ Create, IMAQ Dispose ...) store item names with
///   NO `.vi`, because the members of its old .llb files have none: Basics.llb carries
///   `IMAQ Create`, and NI's export of an IMAQdx example writes `target="IMAQ Create"`. Only
///   `.vi` strings were read, so the whole Vision palette was missing. An extension-less string
///   counts only when it precedes the first PTH0 (the item list - titles and help text follow) AND
///   a container really holds a member of that name.
///
/// PRIMITIVES ARE DELIBERATELY EXCLUDED. A palette entry for a built-in function carries only
/// its display label, and that label is not the AIXML node name: the Flatten/Unflatten palette
/// shows "To XML" for the node AIXML calls "Flatten To XML". Listing those would invite exactly
/// the wrong string. Only .vi and .vim entries are reported.
///
/// ADD-ONS CARRY THEIR OWN PALETTE TREES, and scanning only &lt;LabVIEW&gt;\menus misses them
/// entirely. Drivers now install under %ProgramFiles%\NI\LVAddons\&lt;addon&gt;\&lt;api&gt;\menus, and
/// LabVIEW merges those into the palette at run time. The first version of this class scanned the
/// IDE folder alone and therefore answered a query for "DAQmx" with a single Express-VI stub -
/// while a Call to `DAQmx Read.vi` resolved perfectly well, because NI-DAQmx was installed all
/// along. A silent omission of a whole driver is worse than no index, so both roots are scanned.
///
/// An add-on declares the oldest LabVIEW it supports in lvaddoninfo.json
/// (MinimumSupportedLVVersion, e.g. "22.0" for LabVIEW 2022). One newer than the installed IDE is
/// skipped and REPORTED - never dropped quietly. Bitness-specific variants (nidaqmx, nidaqmx32,
/// nidaqmx64) are all scanned: the name suffix is a convention, not a documented contract, and
/// guessing it wrong would hide exactly the VIs the caller is looking for. Entries are deduplicated
/// by name, so a VI shipped by several variants appears once, labelled with whichever was read
/// first.
/// </summary>
internal static class PaletteIndex
{
    private static readonly object Gate = new();
    private static Dictionary<string, Result> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record Result(
        string LabViewRoot, string MenusFolder, int PaletteFilesScanned,
        IReadOnlyList<PaletteVi> Vis,
        IReadOnlyList<string> AddonsScanned, IReadOnlyList<string> AddonsSkipped);

    /// <summary>
    /// The index for the newest installed LabVIEW, or for <paramref name="labviewRoot"/> when
    /// given. Cached per root for the process lifetime; <paramref name="refresh"/> rescans.
    ///
    /// Add-on palettes are discovered automatically only when <paramref name="labviewRoot"/> is
    /// left to default: a caller pointing at a specific tree - a test with a synthetic menus
    /// folder, say - gets exactly that tree unless it passes <paramref name="addonsRoot"/> too.
    /// </summary>
    public static Result Build(string? labviewRoot = null, bool refresh = false,
                               string? addonsRoot = null)
    {
        var plan = AddonTree.PlanIndex(labviewRoot, addonsRoot, "menus", "palettes");
        var key = plan.Key;

        lock (Gate)
        {
            if (!refresh)
            {
                // Memory first, then the machine-wide cache on disk; see PaletteIndexStore for what
                // the second one is actually worth, which is less than the example index's.
                if (_cache.TryGetValue(key, out var cached)) return cached;

                if (plan.Persist && PaletteIndexStore.TryLoad(key) is { } stored)
                {
                    _cache = Remember(key, stored);
                    return stored;
                }
            }

            var result = Scan(plan.Root, plan.Folders, plan.Skipped);
            _cache = Remember(key, result);
            if (plan.Persist) PaletteIndexStore.Save(key, result);
            return result;
        }
    }

    private static Dictionary<string, Result> Remember(string key, Result result) =>
        new(_cache, StringComparer.OrdinalIgnoreCase) { [key] = result };

    /// <summary>
    /// Build the index off the hot path, swallowing every failure, so the first caller after a
    /// restart does not pay the scan. A caller arriving mid-scan blocks on the same lock and then
    /// gets the finished index, so warming never causes a second scan. A machine with no LabVIEW
    /// must still serve every other tool, hence the empty catch.
    /// </summary>
    public static Task WarmAsync(string? labviewRoot = null, string? addonsRoot = null) =>
        Task.Run(() =>
        {
            try { Build(labviewRoot, addonsRoot: addonsRoot); }
            catch { /* no LabVIEW, no palettes, no problem - the tool reports it when asked */ }
        });

    /// <summary>When this index was last written to disk, or null when it has not been.</summary>
    public static DateTime? BuiltUtc(string? labviewRoot = null, string? addonsRoot = null)
    {
        try
        {
            var plan = AddonTree.PlanIndex(labviewRoot, addonsRoot, "menus", "palettes");
            return plan.Persist ? PaletteIndexStore.BuiltUtc(plan.Key) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The palette trees that are NOT under a folder called `menus`, and which scanning only
    /// `menus` therefore misses entirely.
    ///
    /// MEASURED 2026-09-07 on this installation: 94 `.mnu` files sit outside every `menus` folder,
    /// carrying roughly 374 VI names. The documented casualty was Caraya, whose 18 palette files
    /// live in `vi.lib\addons\_JKI Toolkits\dynamic_palette` - a query for `Caraya` answered "no
    /// match" for VIs that validate, generate and run. It is not alone:
    ///
    /// | tree | .mnu |
    /// |---|---|
    /// | `vi.lib\addons` itself | 27 |
    /// | `vi.lib\addons\_JKI Toolkits\dynamic_palette` (Caraya) | 18 |
    /// | `vi.lib\addons\LabVIEW Open Source Project` | 8 |
    /// | `vi.lib\addons\_JKI Toolkits\VI Tester` | 5 |
    /// | `user.lib\_OpenG.lib\lvzip` | 5 |
    /// | `vi.lib\addons\Wovalab`, `vi.lib\addons\Delacor` (DQMH) | 4 each |
    /// | NI's own `vi.lib\picture\3D Picture Control`, `vi.lib\net\SFTP`, `vi.lib\net\SSH` | ~12 |
    ///
    /// SCANNED BROADLY - each tree whole - rather than at the `addons` folders the pattern
    /// suggests. NI's 3D Picture, SFTP and SSH palettes are under neither, so the narrow reading
    /// would miss them; and "palette files live under a folder called X" is a convention this
    /// class has already been caught by twice, once for LVAddons and once here. The cost is a
    /// directory walk, not a read: only the `.mnu` files are opened, and there are 157.
    ///
    /// See <see cref="LooseTrees"/> for the full sweep and for the one `.mnu` deliberately left
    /// out.
    ///
    /// The LABEL reuses the add-on convention rather than inventing a second one: the source name
    /// then the path within it, so a Caraya hit reads
    /// `vi.lib: addons\_JKI Toolkits\dynamic_palette\caraya.mnu`. Labelling it relative to the
    /// tree alone would print `addons\...` and read like a `menus`-relative palette path, which is
    /// exactly the kind of plausible-but-wrong string this class exists to avoid handing out.
    /// </summary>
    /// <summary>
    /// The trees swept for `.mnu` files beyond `menus` and the LVAddons. Chosen by sweeping the
    /// WHOLE installation and reading what came back, 2026-09-07:
    ///
    /// | tree | .mnu | why |
    /// |---|---|---|
    /// | `vi.lib` | 68 | toolkits: Caraya, VI Tester, JSONtext, Wovalab, plus NI's 3D Picture, SFTP, SSH |
    /// | `user.lib` | 26 | OpenG, and anything the user installed |
    /// | `instr.lib` | 61 | INSTRUMENT DRIVERS - the standard location for them |
    /// | `Targets` | 2 | `Targets\NI\FPGA\menus\FPGAControls` - a real menus folder the scan never reached |
    ///
    /// `instr.lib` matters more than its count here suggests. This station carries one driver
    /// (`Agilent 34401`) and nine `_Template - *` skeletons, and still yields 68 VI names; a real
    /// test rig has a driver per instrument, and "which VI configures this DMM" is exactly the
    /// question a caller brings to this index.
    ///
    /// DELIBERATELY EXCLUDED: `resource\plugins\PopupMenus\...\Class Methods Shortcut Palette.mnu`.
    /// It is a right-click menu, not a palette of callable VIs, and feeding IDE menu actions into
    /// a catalogue of Call targets is precisely the plausible-but-wrong string this class exists
    /// to avoid handing out. It is the only `.mnu` under `resource`.
    /// </summary>
    private static readonly string[] LooseTrees = ["vi.lib", "user.lib", "instr.lib", "Targets"];

    private static List<PaletteSource> LooseSources(string root) =>
        [.. LooseTrees
            .Select(folder => (Name: folder, Path: Path.Combine(root, folder)))
            .Where(pair => Directory.Exists(pair.Path))
            .Select(pair => new PaletteSource(pair.Name, pair.Path))];

    /// <summary>
    /// The palette trees to read: the installation's `menus`, then each add-on's - the version
    /// folder the LabVIEW loads and its Targets subtree (<see cref="AddonTree.PlanIndex"/>) - then
    /// the loose trees.
    /// </summary>
    private static Result Scan(string root, List<AddonFolder> addonFolders, List<string> skipped)
    {
        var menus = Path.Combine(root, "menus");
        if (!Directory.Exists(menus))
            throw new DirectoryNotFoundException(
                $"'{menus}' does not exist, so this installation exposes no palette files.");

        var sources = new List<PaletteSource> { new("", menus) };
        sources.AddRange(addonFolders.Select(f =>
            new PaletteSource(f.Addon, f.Folder, Path.GetDirectoryName(f.Folder))));

        // LAST, so `menus` and the add-ons keep naming the palette path for any VI they also
        // carry - "first palette wins", and a menus-relative path is the more useful label.
        sources.AddRange(LooseSources(root));

        // First palette wins for the reported location; a VI on several palettes is one entry.
        //
        // WHICH one is first is now decided by sorting rather than by the filesystem. That matters
        // here in a way it does not for the example index: the key is the BARE VI NAME, so the same
        // VI really does turn up in several .mnu files, and the reported palette path used to be
        // whichever the enumeration happened to hand over first. Alphabetical order is arbitrary
        // too, but it is the same arbitrary answer on every run and on every machine.
        // Keyed by the CALL TARGET, not the name: two libraries' `Close.vi` are two VIs.
        var found = new Dictionary<string, PaletteVi>(StringComparer.OrdinalIgnoreCase);
        var qualifiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var containers = new ContainerLookup(root, sources);

        foreach (var source in sources)
        {
            var files = Directory
                .EnumerateFiles(source.MenusFolder, "*.mnu", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Concurrent read and byte-scan, sequential merge - see ParallelScan.
            var read = ParallelScan.Map(files, (_, bytes) => ReadPalette(bytes));

            for (var i = 0; i < files.Count; i++)
            {
                if (!read[i].Read) continue;     // a locked or unreadable palette is not fatal

                scanned++;
                if (read[i].Value is not { } palette) continue;

                var relative = Path.GetRelativePath(source.MenusFolder, files[i]);
                var label = source.Label.Length == 0 ? relative : source.Label + ": " + relative;
                foreach (var vi in containers.Entries(palette, source, label, files[i]))
                {
                    // A bare entry and a qualified one of the same name are one VI seen from two
                    // palettes, one of which named its library; keep the one that says how to
                    // call it, under the palette path seen first.
                    if (vi.Target is null)
                    {
                        if (!qualifiedNames.Contains(vi.Name)) found.TryAdd(vi.Name, vi);
                        continue;
                    }
                    if (found.Remove(vi.Name, out var bare))
                        found.TryAdd(vi.CallTarget, vi with { PaletteFile = bare.PaletteFile });
                    else
                        found.TryAdd(vi.CallTarget, vi);
                    qualifiedNames.Add(vi.Name);
                }
            }
        }

        var vis = found.Values
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.CallTarget, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Result(root, menus, scanned, vis,
                          addonFolders.Select(f => f.Addon).Distinct().ToList(), skipped);
    }

    /// <summary>
    /// Every length-prefixed string in the buffer that names a .vi or .vim.
    ///
    /// Reading these as plain text instead would drag the length byte in with the name -
    /// "&amp;My Property Dialog.vi" for a 38-character entry - which looks like a real name and
    /// is not one.
    /// </summary>
    internal static IEnumerable<string> PascalStrings(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            int length = bytes[i];
            if (length < 4 || length > 120 || i + 1 + length > bytes.Length) continue;

            var ok = true;
            for (var j = i + 1; j < i + 1 + length; j++)
                if (bytes[j] < 32 || bytes[j] >= 127) { ok = false; break; }
            if (!ok) continue;

            var text = System.Text.Encoding.ASCII.GetString(bytes, i + 1, length);
            if (!IsViName(text)) continue;

            yield return text;
            i += length;                         // do not rescan the payload as more prefixes
        }
    }

    /// <summary>A bare VI file name: no directory separators, no wildcards.</summary>
    internal static bool IsViName(string text)
    {
        if (!text.EndsWith(".vi", StringComparison.OrdinalIgnoreCase) &&
            !text.EndsWith(".vim", StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.AsSpan().IndexOfAny("\\/:*?\"<>|") >= 0) return false;

        var stem = text[..text.LastIndexOf('.')];
        return stem.Length > 0 && stem.Trim().Length == stem.Length;
    }

    /// <summary>What one .mnu carries: its VI names, the extension-less strings of its item list,
    /// and the containers its PTH0 records name.</summary>
    internal sealed record Palette(
        IReadOnlyList<string> ViNames, IReadOnlyList<string> BareItems,
        IReadOnlyList<string[]> Containers);

    private static readonly byte[] Pth0 = "PTH0"u8.ToArray();

    internal static Palette ReadPalette(byte[] bytes)
    {
        var containers = new List<string[]>();
        var firstPath = -1;
        for (var at = bytes.AsSpan().IndexOf(Pth0); at >= 0;)
        {
            if (firstPath < 0) firstPath = at;
            if (ReadPath(bytes, at + 4) is { Length: > 0 } comps) containers.Add(comps);
            var next = bytes.AsSpan(at + 4).IndexOf(Pth0);
            at = next < 0 ? -1 : at + 4 + next;
        }

        var bare = new List<string>();
        if (firstPath > 0)
            foreach (var text in PrintableStrings(bytes.AsSpan(0, firstPath).ToArray()))
                if (IsBareItemName(text)) bare.Add(text);

        return new Palette(PascalStrings(bytes).ToList(), bare, containers);
    }

    /// <summary>A PTH0 body: uint32 size, uint16 type, uint16 count, then count length-prefixed
    /// components. Null when the record does not fit.</summary>
    private static string[]? ReadPath(byte[] b, int p)
    {
        if (p + 8 > b.Length) return null;
        int count = (b[p + 6] << 8) | b[p + 7];
        var comps = new string[count];
        var q = p + 8;
        for (var i = 0; i < count; i++)
        {
            if (q >= b.Length) return null;
            int n = b[q];
            if (q + 1 + n > b.Length) return null;
            comps[i] = System.Text.Encoding.Latin1.GetString(b, q + 1, n);
            q += 1 + n;
        }
        return comps;
    }

    /// <summary>Every printable length-prefixed string, with no test of what it names.</summary>
    private static IEnumerable<string> PrintableStrings(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            int length = bytes[i];
            if (length < 4 || length > 120 || i + 1 + length > bytes.Length) continue;
            var ok = true;
            for (var j = i + 1; j < i + 1 + length; j++)
                if (bytes[j] < 32 || bytes[j] >= 127) { ok = false; break; }
            if (!ok) continue;
            yield return System.Text.Encoding.ASCII.GetString(bytes, i + 1, length);
            i += length;
        }
    }

    /// <summary>An item name with no extension at all - a candidate for an old .llb member.</summary>
    private static bool IsBareItemName(string text) =>
        text.AsSpan().IndexOfAny("\\/:*?\"<>|.") < 0 && text.Trim().Length == text.Length;

    /// <summary>
    /// Resolves a palette's containers against the installation and turns its strings into
    /// entries: a member of a library container is qualified with it; one found in a directory or
    /// an .llb is qualified with the library that lists that file, if any.
    /// </summary>
    private sealed class ContainerLookup(string root, IReadOnlyList<PaletteSource> sources)
    {
        private readonly Dictionary<string, HashSet<string>?> _libraryMembers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]?> _llbBytes = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string>? _owners;

        private Dictionary<string, string> Owners => _owners ??= LibraryOwners.Scan(root, sources);

        public IEnumerable<PaletteVi> Entries(Palette palette, PaletteSource source, string label, string mnuPath)
        {
            // The palette file's OWN folder is a container too: a dir.mnu - Bit Manipulation's
            // type assertion palette - names its VIs by a RELATIVE PTH0 of one empty component
            // (`this folder`), and other palettes sit beside their VIs naming none at all.
            var places = palette.Containers
                .SelectMany(c => Resolve(c, source.AnchorRoot, mnuPath))
                .Append(Path.GetDirectoryName(mnuPath)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in palette.ViNames)
                yield return Locate(name, places, label) ?? new PaletteVi(name, label);

            // An extension-less item is a VI only where a container holds it - and with no
            // extension, only in an .llb container FILE. The .llb FOLDERS of LabVIEW 2026 hold
            // extension-less stand-ins for PRIMITIVES: vi.lib\dlg_ctls.llb\Machine Epsilon and
            // \Start Asynchronous Call are real VIs (LVIN) behind built-in functions' palette
            // entries, and listing them hands out exactly the wrong Call (caught 2026-11-13 by
            // RealInstallationYieldsVisAndExcludesPrimitives).
            foreach (var item in palette.BareItems)
                if ((Locate(item + ".vi", places, label) ??
                     Locate(item, places, label, llbFilesOnly: true)) is { } vi)
                    yield return vi;
        }

        private PaletteVi? Locate(string name, List<string> places, string label, bool llbFilesOnly = false)
        {
            foreach (var place in places)
            {
                if (llbFilesOnly && !(place.EndsWith(".llb", StringComparison.OrdinalIgnoreCase) && File.Exists(place)))
                    continue;
                // a LIBRARY or a CLASS as the container: Report Generation's plat-nireport.mnu
                // names `<vilib>\Utility\NIReport.llb\NI_report.lvclass`, and its items are that
                // class's members - `NI_report.lvclass:Append Report Text.vi` (2026-11-13; only
                // .lvlib containers were read, and the hit came back bare)
                if (place.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase) ||
                    place.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase))
                {
                    if (Members(place)?.Contains(name) == true)
                        return new PaletteVi(name, label, LibraryOwners.Qualify(Owners, place) + ":" + name);
                    continue;
                }

                string? file = null;
                if (Directory.Exists(place))
                {
                    var candidate = Path.Combine(place, name);
                    if (File.Exists(candidate)) file = candidate;
                }
                else if (place.EndsWith(".llb", StringComparison.OrdinalIgnoreCase) && LlbHolds(place, name))
                    file = Path.Combine(place, name);

                if (file is null) continue;
                var full = Path.GetFullPath(file);
                return Owners.ContainsKey(full)
                    ? new PaletteVi(name, label, LibraryOwners.Qualify(Owners, full))
                    : new PaletteVi(name, label);
            }
            return null;
        }

        /// <summary>Candidate paths for one PTH0: `&lt;vilib&gt;` in the add-on's tree first, then
        /// the installation's; `&lt;userlib&gt;` and `&lt;instrlib&gt;` the installation's. A
        /// path with no anchor is RELATIVE to the palette file treated as a directory, each empty
        /// component one level up - `['']` is the palette's own folder.</summary>
        private IEnumerable<string> Resolve(string[] comps, string? anchorRoot, string mnuPath)
        {
            if (comps.Length > 0 && !comps[0].StartsWith('<'))
            {
                var p = mnuPath;
                foreach (var c in comps)
                    p = c.Length == 0 ? Path.GetDirectoryName(p) ?? p : Path.Combine(p, c);
                yield return p;
                yield break;
            }
            if (comps.Length < 2) yield break;
            var rest = comps.Skip(1).ToArray();
            var folder = comps[0].ToLowerInvariant() switch
            {
                "<vilib>" => "vi.lib",
                "<userlib>" => "user.lib",
                "<instrlib>" => "instr.lib",
                _ => null,
            };
            if (folder is null) yield break;
            if (anchorRoot is not null && folder == "vi.lib")
                yield return Path.Combine([anchorRoot, folder, .. rest]);
            yield return Path.Combine([root, folder, .. rest]);
        }

        private HashSet<string>? Members(string lvlib)
        {
            if (!_libraryMembers.TryGetValue(lvlib, out var set))
                _libraryMembers[lvlib] = set = File.Exists(lvlib) ? LibraryOwners.MemberNames(lvlib) : null;
            return set;
        }

        /// <summary>An .llb container FILE (Vision's Basics.llb) holds a member when its name is
        /// one of the file's length-prefixed strings.</summary>
        private bool LlbHolds(string llb, string name)
        {
            if (!_llbBytes.TryGetValue(llb, out var bytes))
            {
                try { bytes = File.Exists(llb) ? File.ReadAllBytes(llb) : null; }
                catch (IOException) { bytes = null; }
                _llbBytes[llb] = bytes;
            }
            if (bytes is null || name.Length > 255) return false;
            var needle = new byte[name.Length + 1];
            needle[0] = (byte)name.Length;
            System.Text.Encoding.Latin1.GetBytes(name, 0, name.Length, needle, 1);
            return bytes.AsSpan().IndexOf(needle) >= 0;
        }
    }
}

/// <summary>
/// Which library or CLASS lists which file, read from every `.lvlib` and `.lvclass` of the
/// installation and of the add-ons' vi.lib trees - so a palette VI found in a directory or .llb
/// gets the qualifier its `Call` needs. The map is file -> OWNER FILE; <see cref="Qualify"/> walks
/// it into the chain NI writes: `NI_ReportGenerationCore.lvlib:NI_report.lvclass:Append Report
/// Text.vi` for a class member whose class a library lists. Measured 2026-11-13: 249 libraries
/// listing 13 818 VIs, 257 of them palette VIs (openg_variant.lvlib 63, JSONtext.lvlib 46 ...); the
/// classes were added the same day, after 444 palette hits stayed bare whose name a class listed
/// (Report Generation, Caraya's Assert.lvclass, Actor Framework).
/// </summary>
internal static class LibraryOwners
{
    /// <summary>The qualified name of an owned file (a VI, a class, a library): every owner's
    /// file name, outermost first, then its own. A file nobody lists is its own name.</summary>
    public static string Qualify(IReadOnlyDictionary<string, string> owners, string file)
    {
        var chain = new List<string> { Path.GetFileName(file) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
        for (var at = file; owners.TryGetValue(at, out var owner) && seen.Add(owner); at = owner)
            chain.Insert(0, Path.GetFileName(owner));
        return string.Join(":", chain);
    }

    private static readonly string[] OwnerPatterns = ["*.lvlib", "*.lvclass"];

    public static Dictionary<string, string> Scan(string root, IReadOnlyList<PaletteSource> sources)
    {
        var trees = new List<string>
        {
            Path.Combine(root, "vi.lib"), Path.Combine(root, "user.lib"), Path.Combine(root, "instr.lib"),
        };
        trees.AddRange(sources
            .Where(s => s.AnchorRoot is not null)
            .Select(s => Path.Combine(s.AnchorRoot!, "vi.lib")));

        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in trees.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            List<string> ownerFiles;
            try
            {
                ownerFiles = [.. OwnerPatterns.SelectMany(p =>
                    Directory.EnumerateFiles(tree, p, SearchOption.AllDirectories))];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var ownerFile in ownerFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(ownerFile);
                foreach (var (_, url, _) in Items(ownerFile, OwnedKinds))
                    foreach (var file in UrlTargets(ownerFile, url, root, tree))
                        if (!string.Equals(file, full, StringComparison.OrdinalIgnoreCase))
                            owners.TryAdd(file, full);
            }
        }
        return owners;
    }

    /// <summary>What an owner lists that can itself be named in a qualifier chain.</summary>
    private static readonly HashSet<string> OwnedKinds = ["VI", "LVClass", "Library"];

    /// <summary>
    /// The file(s) an Item URL can mean. Relative URLs are relative to the library FILE treated as
    /// a directory (`../picture.llb/x.vi`); symbolic ones name an anchor (NI_AALPro.lvlib:
    /// `/&lt;vilib&gt;/Analysis/2dsp.llb/FFT.vi`), which for an add-on's library may be the add-on's
    /// own vi.lib as well as the installation's - both are returned.
    /// </summary>
    private static IEnumerable<string> UrlTargets(string lvlib, string url, string root, string tree)
    {
        var parts = url.Split('/', StringSplitOptions.None);
        if (url.StartsWith("/<", StringComparison.Ordinal) && parts.Length > 2)
        {
            var folder = parts[1].ToLowerInvariant() switch
            {
                "<vilib>" => "vi.lib", "<userlib>" => "user.lib", "<instrlib>" => "instr.lib", _ => null,
            };
            if (folder is null) yield break;
            var rest = parts.Skip(2).ToArray();
            yield return Path.GetFullPath(Path.Combine([root, folder, .. rest]));
            // the tree is a vi.lib (an add-on's or the installation's); an add-on's <vilib> is it
            if (folder == "vi.lib")
                yield return Path.GetFullPath(Path.Combine([tree, .. rest]));
            yield break;
        }
        string? full = null;
        try { full = Path.GetFullPath(Path.Combine(lvlib, url.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        if (full is not null) yield return full;
    }

    public static HashSet<string> MemberNames(string lvlib) =>
        Items(lvlib, ViKind).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ViKind = ["VI"];

    /// <summary>Every `Item` of one of <paramref name="kinds"/> in a library or class, at any
    /// folder depth: (Name, URL, Type).</summary>
    private static List<(string Name, string Url, string Type)> Items(string owner, HashSet<string> kinds)
    {
        var items = new List<(string, string, string)>();
        try
        {
            using var reader = System.Xml.XmlReader.Create(owner,
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore });
            while (reader.Read())
                if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "Item" &&
                    reader.GetAttribute("Type") is { } type && kinds.Contains(type) &&
                    reader.GetAttribute("Name") is { } name && reader.GetAttribute("URL") is { } url)
                    items.Add((name, url, type));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // an unreadable library qualifies nothing; its VIs stay bare, as before
        }
        return items;
    }
}
