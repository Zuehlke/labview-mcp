namespace LabVIEWMcp.Infra;

/// <summary>
/// A path to a VI that lives INSIDE an <c>.llb</c> CONTAINER FILE, spelled the way LabVIEW itself
/// writes it: the path runs through the <c>.llb</c> as if it were a folder -
/// <c>...\DAQmx\configure\task.llb\DAQmx Clear Task.vi</c>. Most <c>.llb</c>s in LabVIEW 2026 are
/// directories and need nothing; the rest are files - every DAQmx <c>.llb</c>, JKI EasyXML,
/// VI Tester - and <see cref="File.Exists"/> answers false for any member of one.
///
/// That false used to be the whole answer: lvai_vi_terminals refused such a path with
/// <c>FileNotFoundException</c> on both backends, so a DAQmx VI's terminal names had to come from
/// an NI example that calls it. Measured 2026-10-15: NI's ConvertVIToAIXML takes the
/// through-the-.llb path as it is (<c>DAQmx Clear Task.vi</c>, <c>errorCode 0</c>, full export),
/// and vi2aixml.py materializes the member through resolve.py before reading it.
///
/// This is the C# mirror of <c>resolve.llb_split</c>: only the FIRST container counts, because an
/// <c>.llb</c> inside an <c>.llb</c> does not occur. Whether the member is really in the container
/// is left to the reader that opens it - deciding it here would mean parsing the container format.
/// </summary>
internal static class LlbPath
{
    /// <summary>
    /// The container <c>.llb</c> FILE and the member's path inside it, or null when
    /// <paramref name="path"/> does not run through one.
    /// </summary>
    public static (string Container, string Member)? Split(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var parts = full.Split(Path.DirectorySeparatorChar);
        for (var i = 1; i < parts.Length - 1; i++)
        {
            if (!parts[i].EndsWith(".llb", StringComparison.OrdinalIgnoreCase)) continue;
            var container = string.Join(Path.DirectorySeparatorChar, parts[..(i + 1)]);
            if (File.Exists(container))
                return (container, string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]));
        }
        return null;
    }

    /// <summary>An ordinary file, or a path through an existing <c>.llb</c> container file.</summary>
    public static bool Exists(string path) => File.Exists(path) || Split(path) is not null;
}
