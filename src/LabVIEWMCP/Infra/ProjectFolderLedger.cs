using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Project virtual folders that OUR library tools emptied, so the close sweep can take them out.
///
/// WHY: measured 2026-11-18 on the Bausteine test project. Five VIs listed in a `SubVIs` folder
/// were added to a library with lvai_add_to_library; at the project's next save LabVIEW dropped
/// their loose entries (they belong through the library now) and left `SubVIs` behind EMPTY.
/// The sweep must not remove empty folders in general - a user may keep one on purpose - so
/// only a folder whose every entry was one of the items a library call just took over is
/// recorded here, and only that folder, still empty in the saved file, is removed.
/// </summary>
internal static class ProjectFolderLedger
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> Recorded =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records every folder of <paramref name="projectPath"/> (as read from the file now) whose
    /// entries are all among <paramref name="itemPaths"/>. Answers the folder paths recorded.
    /// </summary>
    internal static IReadOnlyList<string> Record(string projectPath, IEnumerable<string> itemPaths)
    {
        var full = Path.GetFullPath(projectPath);
        var items = new HashSet<string>(itemPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        try
        {
            var root = XDocument.Load(full).Root;
            if (root is null) return found;
            foreach (var folder in root.Descendants("Item").Where(IsFolder))
            {
                var children = folder.Elements("Item").ToList();
                if (children.Count == 0) continue;
                if (children.All(c => (string?)c.Attribute("URL") is { } url
                                      && items.Contains(Resolve(full, url))))
                    found.Add(FolderPath(folder));
            }
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return found;
        }
        if (found.Count > 0)
            Recorded.AddOrUpdate(full, _ => [.. found], (_, set) => { lock (set) set.UnionWith(found); return set; });
        return found;
    }

    /// <summary>
    /// Removes each recorded folder of this project that is EMPTY in the saved file, as the one
    /// self-closing line LabVIEW writes for it, and forgets the records. Answers what was removed.
    /// </summary>
    internal static List<string> RemoveEmptyRecorded(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        var removed = new List<string>();
        if (!Recorded.TryRemove(full, out var folders) || !File.Exists(full)) return removed;
        var (text, style) = LvTextFile.Read(full);
        XElement? root;
        try { root = XDocument.Parse(text).Root; }
        catch (System.Xml.XmlException) { return removed; }
        if (root is null) return removed;

        foreach (var path in folders)
        {
            var folder = root.Descendants("Item").Where(IsFolder).FirstOrDefault(f => FolderPath(f) == path);
            if (folder is null || folder.HasElements) continue;
            var name = (string)folder.Attribute("Name")!;
            var line = new Regex("^[ \\t]*<Item Name=\"" + Regex.Escape(System.Security.SecurityElement.Escape(name)) +
                                 "\" Type=\"Folder\"/>\\r?\\n", RegexOptions.Multiline);
            // a folder of that name may exist twice at different depths; only an unambiguous line is removed
            if (line.Matches(text).Count != 1) continue;
            text = line.Replace(text, "", 1);
            removed.Add(path);
        }
        if (removed.Count > 0) LvTextFile.Write(full, text, style);
        return removed;
    }

    /// <summary>For tests: forget every record.</summary>
    internal static void Reset() => Recorded.Clear();

    private static bool IsFolder(XElement e) => (string?)e.Attribute("Type") == "Folder";

    private static string FolderPath(XElement folder) =>
        string.Join("/", folder.AncestorsAndSelf("Item").Reverse().Select(e => (string?)e.Attribute("Name") ?? ""));

    /// <summary>An item URL is relative to the .lvproj FILE: `../SubVIs/A.vi` is in the project's folder.</summary>
    private static string Resolve(string projectFile, string url) =>
        Path.GetFullPath(Path.Combine(projectFile, url.Replace('/', Path.DirectorySeparatorChar)));
}
