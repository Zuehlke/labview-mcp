// A class member's access scope and virtual folder, read from a .lvclass before a re-add and
// written back after it - by a byte-preserving text edit, with the project closed.
namespace LabVIEWMcp.Infra;

/// <summary>
/// WHY A RE-ADD LOSES THE PLACEMENT. lvai_add_class_method re-runs over an existing member by
/// dropping its entry from the class file and adding the VI again with AddItemFromMemory, which
/// puts it at the class ROOT with PUBLIC scope - a protected method in an `overrides` folder comes
/// back public and loose (measured with labview-mcp 1.8.0-1.8.5). The fix is the same file edit a
/// person makes by hand, done in the project-closed window where an edit survives:
///
/// - SCOPE is two properties, and both are needed. LabVIEW stores a non-public member with
///   <c>NI.ClassItem.MethodScope</c> AND <c>NI.LibItem.Scope</c>; changing MethodScope alone is reset
///   by LabVIEW's next class save (measured by the companion skill's set_scope route, which this
///   ports). So the member's original property LINES are captured verbatim and put back.
/// - THE FOLDER is the chain of <c>Type="Folder"</c> items around the member; the member's block is
///   moved back inside it. UNMEASURED: no run has yet shown LabVIEW loading a class whose member
///   block was moved into a folder by a text edit.
///
/// TEXT, NOT XML, for the reason RemoveMemberEntries gives: an XDocument round trip reformats a
/// file LabVIEW owns. Only whole lines move. Anything not shaped as measured - an item that never
/// closes, a member listed twice - abandons the edit and writes nothing.
/// </summary>
internal static class ClassMemberPlacement
{
    internal const string MethodScopeProperty = "NI.ClassItem.MethodScope";
    internal const string LibScopeProperty = "NI.LibItem.Scope";

    /// <summary>Where a member sat and which scope lines it carried, as captured before the drop.</summary>
    internal sealed record Placement(string Member, IReadOnlyList<string> Folders,
                                     string? MethodScopeLine, string? LibScopeLine)
    {
        /// <summary>The MethodScope value; 1 (or none) is public.</summary>
        public string Scope => MethodScopeLine is { } line
            && System.Text.RegularExpressions.Regex.Match(line, ">(\\d+)<") is { Success: true } m
                ? m.Groups[1].Value : "1";

        public bool NeedsRestore => Folders.Count > 0 || Scope != "1";

        public string Describe() =>
            $"{Member}: scope {ScopeWord(Scope)}, folder " +
            (Folders.Count == 0 ? "(class root)" : string.Join("/", Folders));
    }

    internal static string ScopeWord(string scope) => scope switch
    {
        "1" => "public", "2" => "private", "3" => "protected", "4" => "community", _ => scope,
    };

    private sealed class Item(string name, string type, int start, Item? parent)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public int Start { get; } = start;
        public int End { get; set; } = -1;
        public Item? Parent { get; } = parent;
        public List<Item> Children { get; } = [];
    }

    /// <summary>The item tree of a class file, or null when the shape is not what was measured.</summary>
    private static List<Item>? Parse(string[] lines)
    {
        var top = new List<Item>();
        var stack = new Stack<Item>();
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("<Item ", StringComparison.Ordinal))
            {
                var name = Attribute(t, "Name");
                var type = Attribute(t, "Type");
                if (name is null || type is null) return null;
                var item = new Item(name, type, i, stack.Count > 0 ? stack.Peek() : null);
                (stack.Count > 0 ? stack.Peek().Children : top).Add(item);
                if (t.EndsWith("/>", StringComparison.Ordinal)) item.End = i;
                else stack.Push(item);
            }
            else if (t.StartsWith("</Item>", StringComparison.Ordinal))
            {
                if (stack.Count == 0) return null;
                stack.Pop().End = i;
            }
        }
        return stack.Count == 0 ? top : null;
    }

    private static string? Attribute(string line, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, $"\\b{name}=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static IEnumerable<Item> All(IEnumerable<Item> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in All(item.Children)) yield return child;
        }
    }

    private static List<Item> MembersNamed(List<Item> tree, string member) =>
        [.. All(tree).Where(i => i.Type == "VI" && string.Equals(i.Name, member, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The placement of each named member (file names, e.g. "Start.vi"), read from the class text.
    /// A member the file does not list, or lists twice, is left out.
    /// </summary>
    internal static List<Placement> Read(string classText, IEnumerable<string> members)
    {
        var lines = classText.Split('\n');
        if (Parse(lines) is not { } tree) return [];
        var found = new List<Placement>();
        foreach (var member in members)
        {
            var hits = MembersNamed(tree, member);
            if (hits.Count != 1) continue;
            var item = hits[0];
            var folders = new List<string>();
            for (var p = item.Parent; p is not null; p = p.Parent)
                if (p.Type == "Folder") folders.Insert(0, p.Name);
            string? method = null, lib = null;
            for (var i = item.Start + 1; i < item.End; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith($"<Property Name=\"{MethodScopeProperty}\"", StringComparison.Ordinal)) method = t;
                if (t.StartsWith($"<Property Name=\"{LibScopeProperty}\"", StringComparison.Ordinal)) lib = t;
            }
            found.Add(new Placement(item.Name, folders, method, lib));
        }
        return found;
    }

    /// <summary>
    /// The class text with each placement put back: scope lines restored, the block moved into its
    /// folder. Returns the new text and one note per member; the text is null when nothing could be
    /// changed safely, and then nothing must be written.
    /// </summary>
    internal static (string? Text, List<string> Notes) Restore(string classText, IEnumerable<Placement> placements)
    {
        var notes = new List<string>();
        var newline = classText.Contains("\r\n", StringComparison.Ordinal) ? "\r" : "";
        var lines = classText.Split('\n').ToList();
        var changed = false;

        foreach (var placement in placements.Where(p => p.NeedsRestore))
        {
            if (Parse([.. lines]) is not { } tree) return (null, ["the class file did not parse; nothing written"]);
            var hits = MembersNamed(tree, placement.Member);
            if (hits.Count != 1)
            {
                notes.Add($"{placement.Member}: listed {hits.Count} times after the re-add; left alone");
                continue;
            }
            var item = hits[0];
            var indent = Indent(lines[item.Start]);
            var block = lines.GetRange(item.Start, item.End - item.Start + 1)
                .Select(l => Dedent(l.TrimEnd('\r'), indent)).ToList();

            // ---- scope: the original two lines, nothing else of LabVIEW's scope state
            if (block.Count == 1)
            {
                // A self-closing entry has nowhere to hold a property: open it.
                var open = block[0];
                block = [open[..open.LastIndexOf("/>", StringComparison.Ordinal)].TrimEnd() + ">", "</Item>"];
            }
            block.RemoveAll(l => IsProperty(l, MethodScopeProperty) || IsProperty(l, LibScopeProperty));
            foreach (var line in new[] { placement.MethodScopeLine, placement.LibScopeLine }.OfType<string>())
                InsertProperty(block, "\t" + line);
            if (block.Count == 2) block = [block[0][..^1] + "/>"];  // no property left: close it again

            // ---- folder: lift the block out, find the folder chain, put it inside
            lines.RemoveRange(item.Start, item.End - item.Start + 1);
            var at = item.Start;
            var depthIndent = indent;
            var folderNote = "class root";
            if (placement.Folders.Count > 0)
            {
                var reparsed = Parse([.. lines]);
                var folder = reparsed is null ? null : FindFolder(reparsed, placement.Folders);
                if (folder is null)
                    folderNote = $"folder {string.Join("/", placement.Folders)} NOT FOUND - left at the class root";
                else
                {
                    var folderIndent = Indent(lines[folder.Start]);
                    if (folder.End == folder.Start)
                    {
                        var open = lines[folder.Start].TrimEnd('\r');
                        lines[folder.Start] = open[..open.LastIndexOf("/>", StringComparison.Ordinal)].TrimEnd() + ">" + newline;
                        lines.Insert(folder.Start + 1, folderIndent + "</Item>" + newline);
                        folder.End = folder.Start + 1;
                    }
                    at = folder.End;
                    depthIndent = folderIndent + "\t";
                    folderNote = string.Join("/", placement.Folders);
                }
            }
            lines.InsertRange(at, block.Select(l => depthIndent + l + newline));
            changed = true;
            notes.Add($"{placement.Member}: scope {ScopeWord(placement.Scope)}, folder {folderNote}");
        }

        return (changed ? string.Join("\n", lines) : null, notes);
    }

    private static Item? FindFolder(List<Item> tree, IReadOnlyList<string> chain)
    {
        IEnumerable<Item> level = tree;
        Item? found = null;
        foreach (var name in chain)
        {
            found = level.FirstOrDefault(i => i.Type == "Folder" && string.Equals(i.Name, name, StringComparison.Ordinal));
            if (found is null) return null;
            level = found.Children;
        }
        return found;
    }

    private static bool IsProperty(string line, string name) =>
        line.TrimStart().StartsWith($"<Property Name=\"{name}\"", StringComparison.Ordinal);

    /// <summary>Insert a property line in the alphabetical place LabVIEW keeps them in.</summary>
    private static void InsertProperty(List<string> block, string line)
    {
        var name = Attribute(line, "Name") ?? "";
        var at = block.Count - 1;                       // before the closing </Item>
        for (var i = 1; i < block.Count - 1; i++)
            if (Attribute(block[i], "Name") is { } other && block[i].TrimStart().StartsWith("<Property", StringComparison.Ordinal)
                && string.CompareOrdinal(other, name) > 0)
            {
                at = i;
                break;
            }
        block.Insert(at, line);
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart('\t').Length)];

    private static string Dedent(string line, string indent) =>
        line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line.TrimStart('\t');
}
