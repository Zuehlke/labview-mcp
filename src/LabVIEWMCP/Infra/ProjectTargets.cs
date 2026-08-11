using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Which examples belong to a project built for something other than the desktop.
///
/// This replaces a path-substring heuristic that was wrong in both directions, measured over the
/// 189 example projects of LabVIEW 2026:
///
///   - `Object-Oriented Programming\Board Testing\Object Design\FPGAChip\Self Test.vi` was
///     excluded as an FPGA example. It is a plain object-oriented example whose class happens to
///     be called FPGAChip, and its project declares `My Computer` and nothing else.
///   - `Scan Engine\Scan Engine.lvproj` declares an `RT Generic` target and was NOT excluded,
///     because nothing in its path says RT.
///
/// A `.lvproj` states its targets outright, so read that instead of guessing from names. Per the
/// containment grammar in docs/lvproj-structure.md a target is a direct child `Item` of the root
/// `Project` element; `My Computer` is the desktop and anything else - `RT Generic`,
/// `FPGA Target`, a cRIO chassis - needs a module this station may not have.
///
/// On this installation exactly two projects qualify, both `RT Generic`, and no project declares
/// an FPGA target at all: the FPGA and Real-Time examples ship WITH those modules, so a station
/// without them has nothing to exclude. That is worth knowing before trusting any rule that
/// claims to find them by name.
/// </summary>
internal static class ProjectTargets
{
    public const string Desktop = "My Computer";

    /// <summary>
    /// The non-desktop target a project declares, or null for an ordinary desktop project.
    /// Takes the file's text so the parsing is testable without a project on disk.
    /// </summary>
    internal static string? NonDesktopTarget(string projectXml)
    {
        XDocument document;
        try { document = XDocument.Parse(projectXml); }
        catch { return null; }   // unreadable is not evidence of a special target

        var project = document.Root?.Name.LocalName == "Project"
            ? document.Root
            : document.Root?.Element("Project");
        if (project is null) return null;

        foreach (var item in project.Elements("Item"))
        {
            var type = item.Attribute("Type")?.Value;
            if (type is null or Desktop) continue;

            // Build specifications and library items are not targets; only a target sits here
            // with a Type of its own, and Dependencies/Build appear one level down.
            if (type is "Dependencies" or "Build") continue;
            return type;
        }
        return null;
    }

    /// <summary>
    /// Every project directory under <paramref name="root"/> that carries a non-desktop target,
    /// mapped to that target's type. A VI is judged by the nearest such directory above it, which
    /// is how example projects are laid out - one folder, one project.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Scan(string? root)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root is null || !Directory.Exists(root)) return found;

        IEnumerable<string> projects;
        try { projects = Directory.EnumerateFiles(root, "*.lvproj", SearchOption.AllDirectories); }
        catch { return found; }

        foreach (var path in projects)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }

            if (NonDesktopTarget(text) is not { } target) continue;

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null) found[directory] = target;
        }
        return found;
    }

    /// <summary>
    /// The non-desktop target governing <paramref name="viPath"/>, walking up from its folder.
    /// Null when every project above it is an ordinary desktop project, or when there is none.
    /// </summary>
    public static string? For(string viPath, IReadOnlyDictionary<string, string> byDirectory)
    {
        if (byDirectory.Count == 0) return null;

        var directory = Path.GetDirectoryName(Path.GetFullPath(viPath));
        while (!string.IsNullOrEmpty(directory))
        {
            if (byDirectory.TryGetValue(directory, out var target)) return target;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
