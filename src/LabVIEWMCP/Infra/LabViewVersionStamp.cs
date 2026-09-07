using System.Diagnostics;
using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The <c>LVVersion</c> value stamped into LabVIEW files this server CREATES - `.lvproj`,
/// `.lvclass`, `.lvlib`, and any skeleton added later. One source of truth, because the same
/// stamp belongs in all of them: `docs/lvlib-lvclass-structure.md` §1 records that the three are
/// the same format with a different root element.
///
/// WHY IT IS NOT A CONSTANT. It was `26008000`, hardcoded in three places, and that made the whole
/// server unusable for class work on a LabVIEW 2025 station. Every class tool writes a throwaway
/// `<name>-loadcheck.lvproj` and opens it; LabVIEW 2025 answers
///
///     Error 1125: File version is later than the current LabVIEW version.
///     Method Name: Project:Open
///
/// and the call dies at `openProject`. Re-stamping the same file to `25008000` by hand made
/// `lvai_open_file` answer `errorCode: 0`, which is what identified the cause.
///
/// THE VERSION COMES FROM THE CONNECTED INSTANCE, NOT THE NEWEST INSTALLED. The 1125 is raised by
/// the LabVIEW at the other end of the gRPC connection, so that is the only instance whose opinion
/// matters - a station with 2025 and 2026 side by side, running 2025, must still get `25008000`.
///
/// AND THERE IS NO DEFAULT. When the release cannot be established this REFUSES rather than
/// guessing: a wrong stamp writes a file the station cannot load, and a file that fails to open is
/// a worse outcome than a call that declines to write one. `LABVIEWMCP_LVVERSION` / `--lvversion`
/// override the detection.
///
/// EXISTING FILES ARE NEVER RE-STAMPED - see <see cref="LvClass.AddToProject"/> and
/// <see cref="LvClass.AddVisToProject"/>, which edit line by line and never touch the root
/// element. Anything that writes into a library the user already has would otherwise perform a
/// silent, irreversible version upgrade of their file. The stamp is for NEW files only.
/// </summary>
internal static class LabViewVersionStamp
{
    /// <summary>Set by <c>--lvversion</c>; the environment variable is read on every resolve.</summary>
    private static int? _overrideRelease;

    /// <summary>The environment override, so a host that cannot pass CLI flags can still set it.</summary>
    internal const string OverrideVariable = "LABVIEWMCP_LVVERSION";

    /// <summary>
    /// The stamp for a LabVIEW release year: 2026 -> <c>26008000</c>, 2025 -> <c>25008000</c>.
    ///
    /// The shape is the two-digit release, then <c>008000</c>. Verified against files LabVIEW
    /// itself wrote: `26008000` in every LabVIEW 2026 `.lvproj`, `.lvclass` and `.lvlib` on this
    /// station.
    /// </summary>
    public static string Stamp(int release) =>
        release is < 2000 or > 2099
            ? throw new ArgumentOutOfRangeException(
                nameof(release), release,
                "A LabVIEW release is a four-digit year between 2000 and 2099.")
            : $"{release - 2000:00}008000";

    /// <summary>
    /// An override as a release year, from either spelling a human would reach for: the year
    /// (<c>2025</c>) or the stamp itself (<c>25008000</c>). Null when the text is absent or is
    /// neither.
    /// </summary>
    internal static int? ParseOverride(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        // The stamp form. Checked first: "25008000" also parses as an integer, and reading it as a
        // year would give an absurd release rather than an obvious mistake.
        if (trimmed.Length == 8 && trimmed.EndsWith("008000", StringComparison.Ordinal)
            && int.TryParse(trimmed[..2], out var twoDigit))
            return 2000 + twoDigit;

        if (!int.TryParse(trimmed, out var value)) return null;
        if (value is >= 2000 and <= 2099) return value;
        if (value is >= 0 and <= 99) return 2000 + value;      // "25"
        return null;
    }

    /// <summary>Record the <c>--lvversion</c> flag. Null clears it.</summary>
    public static void SetOverride(int? release) => _overrideRelease = release;

    /// <summary>What the resolver decided, and how - reported so a wrong stamp is traceable.</summary>
    /// <param name="Release">The LabVIEW release year, e.g. 2025.</param>
    /// <param name="Stamp">The value to write, e.g. <c>25008000</c>.</param>
    /// <param name="Source">Human-readable provenance, for the tool answer.</param>
    internal sealed record Resolution(int Release, string Stamp, string Source);

    /// <summary>
    /// Thrown when the release cannot be established. Deliberately not a silent default: the
    /// message names what was seen and how to override it.
    /// </summary>
    internal sealed class UnknownVersionException(string message) : InvalidOperationException(message);

    /// <summary>
    /// The stamp to write into a NEW file, and where it came from.
    ///
    /// Order: explicit override, then the running LabVIEW. Several LabVIEW processes of DIFFERENT
    /// releases are resolved by asking which of them owns the gRPC port actually in use - that is
    /// the connected instance by definition. Anything still ambiguous throws.
    /// </summary>
    /// <param name="connectedPort">
    /// The port the gRPC connection is using, when it is known. Only consulted to break a tie
    /// between running instances of different releases, so passing null costs nothing on the
    /// ordinary one-instance station.
    /// </param>
    public static Resolution Resolve(int? connectedPort = null)
    {
        var running = RunningReleases();

        if (_overrideRelease is { } flag)
            return Overridden(flag, $"--lvversion {flag}", running);

        if (ParseOverride(Environment.GetEnvironmentVariable(OverrideVariable)) is { } fromEnv)
            return Overridden(fromEnv, $"{OverrideVariable}={fromEnv}", running);

        if (running.Count == 0)
            throw new UnknownVersionException(
                "No running LabVIEW was found, so the version to stamp into a new file is unknown. " +
                "This call will not guess: a file stamped for the wrong release fails to open with " +
                $"Error 1125. Start LabVIEW, or set {OverrideVariable}=2025 (or --lvversion 2025).");

        var releases = running.Select(r => r.Release).Distinct().ToList();
        if (releases.Count == 1)
            return new Resolution(releases[0], Stamp(releases[0]),
                                  $"the running LabVIEW {releases[0]} (pid {running[0].Pid})");

        // Several releases are running. The connected one is whichever owns the port in use.
        if (connectedPort is { } port && PortDiscovery.PidOwningPort(port) is { } owner
            && running.FirstOrDefault(r => r.Pid == owner) is { Release: > 0 } connected)
            return new Resolution(connected.Release, Stamp(connected.Release),
                                  $"the LabVIEW {connected.Release} serving port {port} (pid {owner})");

        throw new UnknownVersionException(
            "Several LabVIEW releases are running (" +
            string.Join(", ", running.Select(r => $"{r.Release} pid {r.Pid}")) +
            ") and the one serving the gRPC port could not be identified, so the version to stamp " +
            "is ambiguous. This call will not guess - a file stamped for the wrong release fails " +
            $"to open with Error 1125. Set {OverrideVariable}=<year> or --lvversion <year>.");
    }

    /// <summary>
    /// An override, checked against what is actually running.
    ///
    /// OLDER THAN THE RUNNING LabVIEW IS FINE AND IS THE USEFUL CASE: LabVIEW opens files stamped
    /// for earlier releases, so a 2026 station can deliberately write 2025-compatible files for a
    /// colleague. NEWER IS REFUSED - it recreates the exact defect this mechanism exists to fix,
    /// `Error 1125, File version is later than the current LabVIEW version`, and it would do so
    /// silently at the next `Project:Open`.
    /// </summary>
    private static Resolution Overridden(int release, string source,
                                         List<(int Release, int Pid)> running)
    {
        var newest = running.Count == 0 ? (int?)null : running.Max(r => r.Release);
        if (newest is { } have && release > have)
            throw new UnknownVersionException(
                $"{source} asks for a stamp newer than the LabVIEW that is running ({have}). " +
                "That writes a file this station cannot open - Error 1125, File version is later " +
                "than the current LabVIEW version - which is the very fault the override exists " +
                $"to avoid. Stamping OLDER than the running release is fine; {release} is not.");

        return new Resolution(release, Stamp(release), source);
    }

    /// <summary>
    /// Each running LabVIEW with the release its own executable path names. The PATH is the
    /// source, not the newest installation: that is the whole point - <c>LabViewLocator.Select</c>
    /// answers "newest installed", which is the wrong question here.
    /// </summary>
    private static List<(int Release, int Pid)> RunningReleases()
    {
        var found = new List<(int, int)>();
        foreach (var process in LabViewLocator.RunningInstances())
        {
            string? exe;
            try { exe = process.MainModule?.FileName; }
            catch { continue; }        // a 32-bit module read from a 64-bit host, or access denied

            if (exe is null) continue;
            var folder = Path.GetFileName(Path.GetDirectoryName(exe) ?? "");
            if (LabViewLocator.TryParseRelease(folder, out var release))
                found.Add((release, SafePid(process)));
        }
        return found;
    }

    private static int SafePid(Process process)
    {
        try { return process.Id; }
        catch { return 0; }
    }
}
