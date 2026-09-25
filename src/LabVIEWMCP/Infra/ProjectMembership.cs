using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Which <c>.lvproj</c> lists a given VI - read from the project FILES, no LabVIEW involved.
///
/// WHY. A Call to project-local code resolves once that code is OPEN in LabVIEW (measured
/// 2026-09-25, docs/aixml-call-loaded-vi.md), and the way to open it without leaving it stuck in
/// memory is THROUGH ITS PROJECT: a VI opened loose cannot be released again by lvai_close_vi, and
/// a loaded path answers Error 1357 the next time anyone regenerates it. So a tool that wants to
/// call a subject directly needs the subject's project, and asking the caller for it every time is
/// the round trip the direct route exists to save.
///
/// ONLY A PROJECT THAT LISTS THE FILE COUNTS. A project merely sitting above the VI proves nothing -
/// one folder commonly holds several - and opening the wrong one makes it the active project,
/// which every class tool then writes into. So an <c>Item</c> must resolve to the VI itself, or to
/// an auto-populating folder that holds it. A URL is relative to the project FILE treated as a
/// directory (<c>../Sub/X.vi</c> beside <c>C:\p\P.lvproj</c> is <c>C:\p\Sub\X.vi</c>), measured and
/// recorded in CLAUDE.md for the sweep that resolves the same URLs. A symbolic URL
/// (<c>/&lt;vilib&gt;/…</c>) names an installation file and is skipped.
///
/// A member of a <c>.lvlib</c> or <c>.lvclass</c> is NOT listed in the project - the library owns
/// it - so it is never found here, and the caller falls back rather than guessing.
/// </summary>
internal static class ProjectMembership
{
    /// <summary>How many folders above the VI's own are searched. Beyond that is a guess.</summary>
    internal const int LevelsUp = 3;

    /// <summary>Every project at or up to <see cref="LevelsUp"/> folders above the VI that lists it.</summary>
    internal static IReadOnlyList<string> ProjectsListing(string viPath)
    {
        var target = Normalise(viPath);
        if (target is null) return [];

        var found = new List<string>();
        var folder = Path.GetDirectoryName(target);
        for (var level = 0; level <= LevelsUp && folder is not null; level++)
        {
            string[] projects;
            try { projects = Directory.GetFiles(folder, "*.lvproj"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                projects = [];
            }

            foreach (var project in projects.Order(StringComparer.OrdinalIgnoreCase))
                if (Lists(project, target)) found.Add(project);

            folder = Path.GetDirectoryName(folder);
        }
        return found;
    }

    /// <summary>Does this project file list the VI, directly or through an auto-populating folder?</summary>
    internal static bool Lists(string projectPath, string viPath)
    {
        var target = Normalise(viPath);
        if (target is null) return false;

        XDocument document;
        try { document = XDocument.Load(projectPath); }
        catch (Exception e) when (e is System.Xml.XmlException or IOException
                                    or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var item in document.Descendants("Item"))
        {
            var url = (string?)item.Attribute("URL");
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("/<", StringComparison.Ordinal))
                continue;

            string resolved;
            try
            {
                resolved = Path.GetFullPath(Path.Combine(projectPath, url.Replace('/', '\\')))
                    .TrimEnd('\\');
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException
                                        or NotSupportedException)
            {
                continue;
            }

            if (string.Equals(resolved, target, StringComparison.OrdinalIgnoreCase)) return true;

            // an auto-populating folder lists its contents without naming them
            if ((string?)item.Attribute("Type") == "Folder" &&
                target.StartsWith(resolved + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? Normalise(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException
                                    or NotSupportedException)
        {
            return null;
        }
    }
}
