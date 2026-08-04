using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LabVIEWMcp.Grpc;

/// <summary>One LabVIEW installation found on this machine.</summary>
/// <param name="ExePath">Full path to LabVIEW.exe.</param>
/// <param name="Release">The release number parsed from the folder name, e.g. 2026.</param>
/// <param name="Is32Bit">From the PE header, not from the folder — see <see cref="ReadIs32Bit"/>.</param>
/// <param name="FolderName">The raw folder name, for reporting.</param>
internal sealed record LabViewInstall(string ExePath, int Release, bool Is32Bit, string FolderName)
{
    public string Describe() => $"{FolderName} ({(Is32Bit ? "32-bit" : "64-bit")})";
}

/// <summary>
/// Finds and starts LabVIEW without knowing any version number in advance.
///
/// Why not the registry: it lists only the most recently installed release. On a machine with
/// 2023, 2024, 2025 and 2026 side by side it reported 26.x only, and the 64-bit view carried an
/// empty Path. Enumerating the filesystem is the reliable source; the registry is not consulted.
///
/// Selection rule: newest release wins, and where one release exists in both bitnesses the
/// 32-bit build is preferred — it is the one that hosts the lvai gRPC service on this platform.
/// LabVIEW NXG is excluded: different product, no such service.
/// </summary>
internal static class LabViewLocator
{
    /// <summary>Folder names look like "LabVIEW 2026", possibly with a trailing qualifier.</summary>
    private static readonly Regex FolderPattern =
        new(@"^LabVIEW\s+(?<release>\d{2,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Running LabVIEW processes — NOT this MCP server, whose name also starts with "LabVIEW".</summary>
    public static IReadOnlyList<Process> RunningInstances()
    {
        try
        {
            return Process.GetProcessesByName("LabVIEW");
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Every LabVIEW installation under the standard program-files roots.</summary>
    public static IReadOnlyList<LabViewInstall> Discover()
    {
        var found = new List<LabViewInstall>();

        foreach (var root in ProgramFilesRoots())
        {
            var ni = Path.Combine(root, "National Instruments");
            if (!Directory.Exists(ni)) continue;

            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(ni, "LabVIEW*"); }
            catch { continue; }

            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                if (!TryParseRelease(name, out var release)) continue;

                var exe = Path.Combine(folder, "LabVIEW.exe");
                if (!File.Exists(exe)) continue;

                // Fall back to the folder convention only if the PE header is unreadable.
                var is32 = ReadIs32Bit(exe) ?? root.Contains("(x86)", StringComparison.OrdinalIgnoreCase);
                found.Add(new LabViewInstall(exe, release, is32, name));
            }
        }

        return found;
    }

    /// <summary>
    /// Parse the release number, rejecting products that are not LabVIEW proper.
    /// NXG carries a version too ("LabVIEW NXG 5.0") but hosts no lvai service.
    /// </summary>
    internal static bool TryParseRelease(string folderName, out int release)
    {
        release = 0;
        if (folderName.Contains("NXG", StringComparison.OrdinalIgnoreCase)) return false;

        var match = FolderPattern.Match(folderName);
        if (!match.Success) return false;

        return int.TryParse(match.Groups["release"].Value, out release);
    }

    /// <summary>Newest release first; within a release, 32-bit first.</summary>
    internal static LabViewInstall? Select(IEnumerable<LabViewInstall> installs) =>
        installs
            .OrderByDescending(i => i.Release)
            .ThenByDescending(i => i.Is32Bit)
            .FirstOrDefault();

    /// <summary>
    /// Bitness straight from the PE header, so an installation outside the conventional
    /// program-files root is still classified correctly. Null when the file cannot be read
    /// or carries an unexpected machine type.
    /// </summary>
    internal static bool? ReadIs32Bit(string exePath)
    {
        try
        {
            using var stream = File.OpenRead(exePath);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt16() != 0x5A4D) return null;          // "MZ"
            stream.Position = 0x3C;
            stream.Position = reader.ReadInt32();                    // e_lfanew
            if (reader.ReadUInt32() != 0x0000_4550) return null;     // "PE\0\0"

            return reader.ReadUInt16() switch
            {
                0x014C => true,      // IMAGE_FILE_MACHINE_I386
                0x8664 => false,     // IMAGE_FILE_MACHINE_AMD64
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Start the given installation. Returns the process, or null if the start failed.</summary>
    public static Process? Start(LabViewInstall install)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(install.ExePath) { UseShellExecute = true });
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        // Prefer the 32-bit root: on this platform the lvai service lives in 32-bit LabVIEW.
        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
    }
}
