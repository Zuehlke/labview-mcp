using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>
/// WHICH LabVIEW the server is working for, and where that LabVIEW looks for add-ons - issue #99.
///
/// lvpm starts LabVIEW with `-pref &lt;repo&gt;\.lv-venv\.lvpm\labview.ini`, whose
/// <c>LVAddons.AdditionalLocations</c> names the project's own add-on folder. NI's converter runs
/// inside that LabVIEW and sees the packages; our composer, reader and indexes looked at
/// <c>%ProgramFiles%\NI\LVAddons</c> only and answered `no Test.lvclass`. This record is the one
/// place that question is answered, so the composer, the reader and the palette and example
/// indexes cannot disagree about it.
///
/// THE LVAddons KEYS ARE OPTIONAL - the user's reminder of 2026-10-26, and a stock LabVIEW.ini
/// carries none of them. A missing key is LabVIEW's DEFAULT, never "no add-ons": enabled, the
/// default root, no additional locations. <see cref="Defaulted"/> names every key that was not in
/// the INI, so an answer can say so.
///
/// MEASURED 2026-10-26 on LabVIEW 26.3.1 (32-bit), each a LabVIEW started with its own `-pref`
/// INI and a caller whose &lt;vilib&gt; link only a probe add-on can satisfy (docs/aixml-backends.md):
/// <c>LVAddons.AdditionalLocations</c> is honoured and SEMICOLON-separated;
/// <c>LVAddons.CustomLocation</c> REPLACES the default root (its add-ons gone from LabVIEW's log);
/// <c>LVAddons.Enabled=False</c> loads nothing; no keys - the control - leaves the link broken.
/// For one file in two add-ons the alphabetically first ADD-ON wins, across locations and in
/// either INI order (<see cref="SelectedAddons"/>).
/// </summary>
/// <param name="ExePath">LabVIEW.exe of that LabVIEW, or null when there is none.</param>
/// <param name="Major">Version from the executable's version resource: 26 for 26.3.1f1.</param>
/// <param name="Minor">3 for 26.3.1f1.</param>
/// <param name="Bits">32 or 64, from the PE header.</param>
/// <param name="PrefFile">The `-pref` INI it was (or will be) started with, or null.</param>
/// <param name="IniFile">The INI the add-on keys were read from: PrefFile, else LabVIEW.ini beside the exe.</param>
/// <param name="Pid">The running process, or null.</param>
/// <param name="Source">How it was chosen: running, settings, remembered, installed, none.</param>
/// <param name="AddonsEnabled">False only when the INI says LVAddons.Enabled=False.</param>
/// <param name="AddonLocations">The base location first, then the additional ones; existing folders only.</param>
/// <param name="Defaulted">The LVAddons keys the INI did not carry.</param>
/// <param name="Notes">What a caller should know: missing folders, an ambiguous choice.</param>
internal sealed record LabViewContext(
    string? ExePath, int? Major, int? Minor, int? Bits,
    string? PrefFile, string? IniFile, int? Pid, string Source,
    bool AddonsEnabled, IReadOnlyList<string> AddonLocations,
    IReadOnlyList<string> Defaulted, IReadOnlyList<string> Notes)
{
    public const string EnabledKey = "LVAddons.Enabled";
    public const string CustomKey = "LVAddons.CustomLocation";
    public const string AdditionalKey = "LVAddons.AdditionalLocations";

    /// <summary>Settings keys that pin the LabVIEW, for a station with several or none running.</summary>
    public const string ExeSetting = "labviewExe";
    public const string PrefSetting = "labviewPref";

    public string? Root => ExePath is null ? null : Path.GetDirectoryName(ExePath);

    public int? Release => Major is { } m ? 2000 + m : null;

    /// <summary>Only the default root, nothing configured - the case every older cache was built for.</summary>
    public bool OnlyDefaultLocations =>
        AddonsEnabled && AddonLocations.All(l => AddonTree.DefaultRoot() is { } d && SamePath(l, d));

    /// <summary>
    /// What the add-on SELECTION depends on, without timestamps: the locations, the version and the
    /// bitness. Equal keys select the same add-on folders from the same trees.
    /// </summary>
    public string LocationsKey =>
        $"{Root}|{Major}.{Minor}|{Bits}|{(AddonsEnabled ? string.Join(';', AddonLocations) : "disabled")}";

    /// <summary>
    /// <see cref="LocationsKey"/> plus the write times of every location, add-on folder, version
    /// folder and lvaddoninfo.json under it - what an `lvpm install` moves. A file replaced inside a
    /// version folder that already existed does not move it; installing a package does.
    /// </summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder(LocationsKey);
        if (AddonsEnabled)
            foreach (var location in AddonLocations)
            {
                Stamp(sb, location);
                try
                {
                    foreach (var addon in Directory.EnumerateDirectories(location).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        Stamp(sb, addon);
                        foreach (var version in Directory.EnumerateDirectories(addon).Order(StringComparer.OrdinalIgnoreCase))
                        {
                            Stamp(sb, version);
                            Stamp(sb, Path.Combine(version, "lvaddoninfo.json"));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { sb.Append("|unreadable"); }
            }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];

        static void Stamp(StringBuilder sb, string path)
        {
            sb.Append('|').Append(path.ToLowerInvariant()).Append('@');
            try
            {
                var t = File.Exists(path) ? File.GetLastWriteTimeUtc(path)
                      : Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : default;
                sb.Append(t.Ticks);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { sb.Append('?'); }
        }
    }

    /// <summary>
    /// The add-on folders this LabVIEW loads (<see cref="AddonTree.Select"/>), from EVERY location,
    /// ordered by add-on name - measured 2026-10-26: for one &lt;vilib&gt; file in two add-ons LabVIEW
    /// took the alphabetically first add-on FOLDER (not AddonName), whichever location held it and
    /// whatever order the INI listed them in, the CustomLocation included; and the SAME folder name
    /// in two locations came from the location listed LAST (a CustomLocation before every
    /// additional one). resolve.py's ordered_addons is the same rule.
    /// </summary>
    public IReadOnlyList<AddonFolder> SelectedAddons() =>
        AddonsEnabled
            ? AddonLocations
                .SelectMany((l, i) => AddonTree.Select(l, Major, Minor, Bits).Select(a => (a, i)))
                .OrderBy(x => x.a.Addon, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.i)
                .Select(x => x.a).ToList()
            : [];

    /// <summary>For a tool answer: what was chosen and from where.</summary>
    public object Describe() => new
    {
        exe = ExePath,
        version = Major is null ? null : $"{Major}.{Minor}",
        bitness = Bits,
        pid = Pid,
        source = Source,
        pref = PrefFile,
        ini = IniFile,
        addonsEnabled = AddonsEnabled,
        addonLocations = AddonLocations,
        defaulted = Defaulted,
        notes = Notes,
    };

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The context now. Order: the settings file's <c>labviewExe</c> (and <c>labviewPref</c>); one
    /// running LabVIEW; of several, the one whose command line is nearest <paramref name="hintPath"/>
    /// (lvpm passes the project, and its INI lives in the repository), else the one serving
    /// <paramref name="connectedPort"/>, else the newest - flagged in <see cref="Notes"/>; with none
    /// running, the LabVIEW last seen running (<see cref="Remembered"/>), else the newest installed.
    /// </summary>
    public static LabViewContext Resolve(string? hintPath = null, int? connectedPort = null)
    {
        var notes = new List<string>();
        var running = RunningInstances();

        if (ServerSettings.ReadString(ExeSetting) is { Length: > 0 } pinned)
        {
            if (!File.Exists(pinned))
                notes.Add($"settings {ExeSetting} names {pinned}, which does not exist - ignored.");
            else
            {
                var same = running.Where(r => SamePath(r.Exe, pinned)).ToList();
                var pref = ServerSettings.ReadString(PrefSetting) is { Length: > 0 } p ? p
                         : same.Count == 1 ? same[0].Pref : null;
                return Build(pinned, pref, same.Count == 1 ? same[0].Pid : null, "settings", notes);
            }
        }
        else if (ServerSettings.ReadString(PrefSetting) is { Length: > 0 })
            notes.Add($"settings {PrefSetting} is only read together with {ExeSetting}.");

        if (running.Count > 0)
        {
            var pick = running.Count == 1 ? running[0] : Choose(running, hintPath, connectedPort, notes);
            Remember(pick.Exe, pick.Pref);
            return Build(pick.Exe, pick.Pref, pick.Pid, "running", notes);
        }

        if (Remembered() is { } last && File.Exists(last.Exe))
        {
            var pref = last.Pref is { } lp && File.Exists(lp) ? lp : null;
            if (last.Pref is not null && pref is null) notes.Add($"the remembered -pref file {last.Pref} is gone.");
            return Build(last.Exe, pref, null, "remembered", notes);
        }

        if (LabViewLocator.Select(LabViewLocator.Discover()) is { } installed)
            return Build(installed.ExePath, null, null, "installed", notes);

        notes.Add("no LabVIEW installation found.");
        return Build(null, null, null, "none", notes);
    }

    /// <summary>A context for a known executable and INI - tests, and every route above.</summary>
    internal static LabViewContext Build(string? exe, string? pref, int? pid, string source,
                                         List<string>? notes = null)
    {
        notes ??= [];
        int? major = null, minor = null, bits = null;
        if (exe is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (info.FileMajorPart > 0) (major, minor) = (info.FileMajorPart, info.FileMinorPart);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (major is null && LabViewLocator.TryParseRelease(Path.GetFileName(Path.GetDirectoryName(exe) ?? ""), out var year))
                (major, minor) = (year % 100, 99);
            bits = LabViewLocator.ReadIs32Bit(exe) switch { true => 32, false => 64, null => null };
        }

        var ini = pref ?? (exe is null ? null : Path.Combine(Path.GetDirectoryName(exe)!, "LabVIEW.ini"));
        if (pref is not null && !File.Exists(pref)) notes.Add($"the -pref file {pref} does not exist.");
        var keys = ini is not null && File.Exists(ini) ? ReadIniSection(ini, "LabVIEW") : new Dictionary<string, string>();

        var defaulted = new List<string>();
        var enabled = true;
        if (keys.TryGetValue(EnabledKey, out var enabledText))
            enabled = !string.Equals(enabledText.Trim(), "False", StringComparison.OrdinalIgnoreCase);
        else defaulted.Add(EnabledKey);

        var locations = new List<string>();
        string? baseRoot;
        if (keys.TryGetValue(CustomKey, out var custom) && custom.Trim().Length > 0)
            baseRoot = Absolute(custom.Trim(), ini);
        else
        {
            defaulted.Add(CustomKey);
            baseRoot = AddonTree.DefaultRoot();
        }
        if (baseRoot is not null)
        {
            if (Directory.Exists(baseRoot)) locations.Add(baseRoot);
            else notes.Add($"add-on location {baseRoot} does not exist.");
        }

        if (keys.TryGetValue(AdditionalKey, out var additional))
        {
            if (major is { } m && m < 24)
                notes.Add($"{AdditionalKey} needs LabVIEW 2024 Q1 or later; this is {2000 + m} - ignored.");
            else
                foreach (var part in additional.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var path = Absolute(part.Trim('"'), ini);
                    if (!Directory.Exists(path)) notes.Add($"add-on location {path} does not exist.");
                    else if (!locations.Any(l => SamePath(l, path))) locations.Add(path);
                }
        }
        else defaulted.Add(AdditionalKey);

        if (!enabled) notes.Add($"{EnabledKey}=False in {ini}: LabVIEW loads no add-ons.");
        return new LabViewContext(exe, major, minor, bits, pref, ini, pid, source, enabled,
                                  locations, defaulted, notes);
    }

    /// <summary>
    /// The [section] of a LabVIEW INI as key -> value, keys case-insensitive, a value's surrounding
    /// quotes removed (LabVIEW writes paths quoted). A later duplicate wins, as in LabVIEW.
    /// </summary>
    internal static Dictionary<string, string> ReadIniSection(string path, string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }

        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = string.Equals(line[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || line.Length == 0 || line.StartsWith(';')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            result[line[..eq].Trim()] = value;
        }
        return result;
    }

    private static string Absolute(string path, string? ini) =>
        Path.IsPathRooted(path) || ini is null
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ini)!, path));

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    // ---- running instances ----------------------------------------------------------------------

    internal sealed record Instance(int Pid, string Exe, string? Pref, IReadOnlyList<string> Args);

    internal static List<Instance> RunningInstances()
    {
        var list = new List<Instance>();
        foreach (var process in LabViewLocator.RunningInstances())
        {
            using (process)
            {
                int pid;
                try { pid = process.Id; } catch { continue; }
                if (ProcessCommandLine.ImagePath(pid) is not { } exe) continue;
                var args = ProcessCommandLine.Read(pid) is { } line ? ProcessCommandLine.Split(line) : [];
                var pref = ProcessCommandLine.ValueAfter(args, "pref") is { } p ? Absolute(p, exe) : null;
                list.Add(new Instance(pid, exe, pref, args));
            }
        }
        return list;
    }

    /// <summary>Several LabVIEWs run: the one nearest the hint, else the connected one, else the newest.</summary>
    internal static Instance Choose(IReadOnlyList<Instance> running, string? hintPath, int? connectedPort,
                                    List<string> notes)
    {
        if (hintPath is not null)
        {
            var scored = running.Select(r => (r, depth: Nearness(r, hintPath))).OrderByDescending(x => x.depth).ToList();
            if (scored[0].depth > 0 && scored[0].depth > scored[1].depth)
            {
                notes.Add($"{running.Count} LabVIEWs run; chose pid {scored[0].r.Pid}, whose command line is nearest {hintPath}.");
                return scored[0].r;
            }
        }
        if (connectedPort is { } port && PortDiscovery.PidOwningPort(port) is { } owner &&
            running.FirstOrDefault(r => r.Pid == owner) is { } connected)
        {
            notes.Add($"{running.Count} LabVIEWs run; chose pid {owner}, which serves gRPC port {port}.");
            return connected;
        }
        var newest = running.OrderByDescending(r => LabViewLocator.TryParseRelease(
            Path.GetFileName(Path.GetDirectoryName(r.Exe) ?? ""), out var y) ? y : 0).ThenBy(r => r.Pid).First();
        notes.Add($"{running.Count} LabVIEWs run ({string.Join(", ", running.Select(r => $"pid {r.Pid}{(r.Pref is null ? "" : " -pref " + r.Pref)}"))}) " +
                  $"and none is tied to this call; chose pid {newest.Pid}. Pin one with \"{ExeSetting}\" and \"{PrefSetting}\" in {ServerSettings.FilePath}.");
        return newest;
    }

    /// <summary>
    /// How many directory levels the deepest path in an instance's arguments shares with the hint,
    /// below the drive. lvpm's project and INI lie in the repository the hint is in; a plain
    /// LabVIEW's arguments share the drive at most.
    /// </summary>
    internal static int Nearness(Instance instance, string hintPath)
    {
        var hint = Segments(hintPath);
        var best = 0;
        foreach (var arg in instance.Args.Skip(1).Append(instance.Pref ?? ""))
        {
            if (arg.Length < 3 || !Path.IsPathRooted(arg)) continue;
            var other = Segments(arg);
            var shared = 0;
            while (shared < hint.Length && shared < other.Length &&
                   string.Equals(hint[shared], other[shared], StringComparison.OrdinalIgnoreCase)) shared++;
            best = Math.Max(best, shared - 1);   // the drive alone counts nothing
        }
        return best;

        static string[] Segments(string p)
        {
            try { return Path.GetFullPath(p).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries); }
            catch { return []; }
        }
    }

    /// <summary>
    /// What a start launches when no LabVIEW runs: the settings file's LabVIEW, else the one last
    /// seen running WITH the -pref it ran with, else the newest installed with none. Before
    /// 2026-10-26 a start was always the newest installed and never carried a -pref, so an
    /// lvpm-started LabVIEW 2024 came back as 2026 without its project's add-ons.
    /// </summary>
    internal static (LabViewInstall? Install, string? Pref, LabViewContext Context) ToStart()
    {
        var context = Resolve();
        if (context.ExePath is not { } exe) return (null, null, context);
        var folder = Path.GetFileName(Path.GetDirectoryName(exe) ?? "");
        var install = new LabViewInstall(exe, context.Release ?? 0, context.Bits != 64, folder);
        return (install, context.PrefFile, context);
    }

    // ---- the LabVIEW last seen running ----------------------------------------------------------

    internal sealed record LastSeen(string Exe, string? Pref, string SeenUtc);

    /// <summary>Beside the settings file: the LabVIEW (and its -pref) last seen running, for a start.</summary>
    public static string LastSeenPath =>
        Path.Combine(Path.GetDirectoryName(ServerSettings.FilePath)!, "last-labview.json");

    internal static LastSeen? Remembered()
    {
        try
        {
            return File.Exists(LastSeenPath)
                ? JsonSerializer.Deserialize<LastSeen>(File.ReadAllText(LastSeenPath))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Remember(string exe, string? pref)
    {
        try
        {
            if (Remembered() is { } last && SamePath(last.Exe, exe) &&
                (last.Pref is null ? pref is null : pref is not null && SamePath(last.Pref, pref))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(LastSeenPath)!);
            File.WriteAllText(LastSeenPath, JsonSerializer.Serialize(new LastSeen(exe, pref, DateTime.UtcNow.ToString("o"))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
