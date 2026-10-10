// ============================================================================================
// IconOwners.cs - reads a .lvclass or plain .lvlib from disk for the icon tools: its kind, its
// member VIs as absolute paths, its icon property and version, and which owner lists a given VI.
// ============================================================================================
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The owner of an icon template - a <c>.lvclass</c> or a plain <c>.lvlib</c> - as its FILE says,
/// with no LabVIEW involved, so the icon tools can refuse a bad request before anything is opened.
///
/// WHY FROM THE FILE. The member list decides what <c>ApplyIconToVIs</c> is asked to touch, and the
/// route that applies it saves every member it is given: a path that is not a member, or a member
/// whose file is missing, fails inside LabVIEW with a bare Error 7 per member and a half-applied
/// class. Both are decidable here.
///
/// A CLASS MEMBER'S URL IS RELATIVE TO THE .lvclass ITSELF TREATED AS A DIRECTORY, and a library's
/// the same way (<c>LvClass.Resolve</c>, <c>LibraryTools.ReadLibrary</c>): <c>../Member.vi</c> beside
/// <c>C:\p\Foo.lvclass</c> is <c>C:\p\Member.vi</c>. Symbolic URLs (<c>/&lt;vilib&gt;/…</c>) are
/// skipped - an installation file is never an icon target here.
///
/// ONLY <c>.vi</c> MEMBERS ARE ICON TARGETS. That is what was measured (a class with six members,
/// 41 members over classes and plain libraries); <c>.ctl</c> and <c>.vim</c> members carry icons too,
/// but whether <c>ApplyIconToVIs</c> reaches them has not been measured, so they are listed as
/// skipped rather than sent. A class nested in a library is the library's ITEM, not a VI of it: its
/// members are the class's, and its icon is its own.
/// </summary>
internal static class IconOwners
{
    internal const string Class = "class";
    internal const string Library = "library";

    /// <summary>One member item of the owner file.</summary>
    internal sealed record Member(string Name, string Path, bool IsVi);

    /// <summary>What the owner file says about itself.</summary>
    internal sealed record Owner(string Path, string Kind, string? LvVersion, string? IconProperty,
                                 IReadOnlyList<Member> Members)
    {
        public IEnumerable<Member> Vis => Members.Where(m => m.IsVi);
    }

    /// <summary>
    /// Kind from extension AND root element. A <c>.lvlib</c> whose root is not <c>Library</c>, or a
    /// <c>.lvclass</c> whose root is not <c>LVClass</c>, is refused: the two use the same grammar and
    /// a mismatch means the file is not what its name says.
    /// </summary>
    internal static Owner Read(string ownerPath)
    {
        var full = System.IO.Path.GetFullPath(ownerPath);
        var extension = System.IO.Path.GetExtension(full).ToLowerInvariant();
        var expectedRoot = extension switch
        {
            ".lvclass" => "LVClass",
            ".lvlib" => "Library",
            _ => throw new InvalidDataException(
                $"'{full}' is neither a .lvclass nor a .lvlib - only those two carry an icon template."),
        };

        var root = XDocument.Load(full).Root
            ?? throw new InvalidDataException($"'{full}' has no root element.");
        if (root.Name.LocalName != expectedRoot)
            throw new InvalidDataException(
                $"'{full}' has root <{root.Name.LocalName}>, not <{expectedRoot}>.");

        // A class's members come from LvClass.Read, the reader every class tool shares, so the two
        // can never disagree about what a class holds. A plain library has no such reader outside
        // LibraryTools (private there), hence the small walk below - same URL rule.
        var members = new List<Member>();
        if (extension == ".lvclass")
            foreach (var m in LvClass.Read(full).Members)
            {
                if (Resolve(full, m.Url) is { } resolved)
                    members.Add(new Member(m.Name, resolved, m.Type == "VI"));
            }
        else
            Walk(root, full, members);

        return new Owner(full, extension == ".lvclass" ? Class : Library,
            (string?)root.Attribute("LVVersion"), Property(root, "NI.Lib.Icon"), members);
    }

    /// <summary>VI items at any depth - a class or library may nest them in virtual folders.</summary>
    private static void Walk(XElement element, string ownerPath, List<Member> members)
    {
        foreach (var item in element.Elements("Item"))
        {
            var type = (string?)item.Attribute("Type");
            if (type == "Folder") { Walk(item, ownerPath, members); continue; }
            if (type != "VI") continue;

            var name = (string?)item.Attribute("Name") ?? "";
            if (Resolve(ownerPath, (string?)item.Attribute("URL")) is not { } path) continue;
            members.Add(new Member(name, path,
                string.Equals(System.IO.Path.GetExtension(path), ".vi", StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>A member URL against the owner file treated as a directory; null for a symbolic URL.</summary>
    private static string? Resolve(string ownerPath, string? url)
    {
        if (url is not { Length: > 0 } || url.Contains('<')) return null;
        try { return System.IO.Path.GetFullPath(System.IO.Path.Combine(ownerPath, url.Replace('/', '\\'))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? Property(XElement parent, string name) => parent
        .Elements("Property")
        .FirstOrDefault(p => (string?)p.Attribute("Name") == name)?.Value;

    /// <summary>
    /// Every <c>.lvclass</c> / <c>.lvlib</c> in the VI's folder and up to
    /// <see cref="ProjectMembership.LevelsUp"/> folders above that LISTS the VI. A file merely nearby
    /// proves nothing - the same rule <see cref="ProjectMembership"/> applies to projects. A class
    /// inside a library lists the VI while the library does not, so normally exactly one answers.
    /// </summary>
    internal static IReadOnlyList<string> OwnersListing(string viPath)
    {
        var target = System.IO.Path.GetFullPath(viPath);
        var found = new List<string>();
        var folder = System.IO.Path.GetDirectoryName(target);
        for (var level = 0; level <= ProjectMembership.LevelsUp && folder is not null; level++)
        {
            string[] candidates;
            try
            {
                candidates = [.. Directory.GetFiles(folder, "*.lvclass"), .. Directory.GetFiles(folder, "*.lvlib")];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                candidates = [];
            }

            foreach (var candidate in candidates.Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (Read(candidate).Members.Any(m =>
                            string.Equals(m.Path, target, StringComparison.OrdinalIgnoreCase)))
                        found.Add(candidate);
                }
                catch (Exception e) when (e is System.Xml.XmlException or IOException
                                            or UnauthorizedAccessException or InvalidDataException)
                {
                    // an unreadable neighbour is not an owner; skipping it is the honest answer
                }
            }
            folder = System.IO.Path.GetDirectoryName(folder);
        }
        return found;
    }
}
