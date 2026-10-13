// ============================================================================================
// OwnerHousekeeping.cs - the file-side half of the class housekeeping tools: the CP1252 check a
// description must pass before LabVIEW sees it, and offline readers for what a .lvclass, .lvlib
// or .vi says on disk afterwards (description, members, folders, owning library, links, icon).
// ============================================================================================
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Text LabVIEW stores ANSI. A class's description, a library's and a VI's are all held in the
/// Windows-1252 code page: measured 2026-10-11/12 on 15 classes, an arrow (U+2192) written
/// through <c>{LV.LVClassLibrary}</c> <c>Description</c> came back as "?" - with no error from the
/// write, the read-back or the save. So the check runs BEFORE LabVIEW, names every character that
/// would be lost, and suggests an ASCII spelling where one is obvious.
///
/// CONTROL CHARACTERS ARE REFUSED TOO, except TAB, CR and LF. Two of them (0x1E, 0x1D) are how a
/// line break travels to the typed run helper, so letting one through would arrive as a line
/// break; the rest have no business in a description.
/// </summary>
internal static class AnsiText
{
    /// <summary>One character that would not survive, with where it is and what to write instead.</summary>
    internal sealed record Offender(int Index, string Character, string CodePoint, string? Suggestion,
                                    string Reason);

    private static readonly Encoding Cp1252 = CreateCp1252();

    private static Encoding CreateCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback,
                                    DecoderFallback.ExceptionFallback);
    }

    /// <summary>The decoder for text read back out of a .vi file's own resources.</summary>
    internal static Encoding Windows1252 => Cp1252;

    /// <summary>
    /// Plain-ASCII spellings for the characters that turn up in hand-written descriptions. Not a
    /// transliteration table: a character with no entry is still refused, with no suggestion.
    /// </summary>
    private static readonly Dictionary<int, string> Suggestions = new()
    {
        [0x2192] = "->", [0x2190] = "<-", [0x2194] = "<->", [0x21D2] = "=>", [0x21D0] = "<=",
        [0x21D4] = "<=>", [0x2191] = "^", [0x2193] = "v",
        [0x2264] = "<=", [0x2265] = ">=", [0x2260] = "!=", [0x2248] = "~", [0x2261] = "==",
        [0x221E] = "inf", [0x221A] = "sqrt", [0x2212] = "-", [0x2010] = "-", [0x2011] = "-",
        [0x2012] = "-", [0x2015] = "-", [0x2044] = "/", [0x2215] = "/", [0x2032] = "'",
        [0x2033] = "\"", [0x2009] = " ", [0x200A] = " ", [0x2002] = " ", [0x2003] = " ",
        [0x202F] = " ", [0x200B] = "", [0xFEFF] = "",
        [0x0394] = "delta", [0x2206] = "delta", [0x03A9] = "Ohm", [0x2126] = "Ohm",
        [0x03B1] = "alpha", [0x03B2] = "beta", [0x03B3] = "gamma", [0x03B4] = "delta",
        [0x03B5] = "epsilon", [0x03BB] = "lambda", [0x03BC] = "u", [0x03C0] = "pi",
        [0x03C3] = "sigma", [0x03C4] = "tau", [0x03C9] = "omega",
        [0x2713] = "OK", [0x2714] = "OK", [0x2717] = "x", [0x2718] = "x",
        [0x2070] = "0", [0x2074] = "4", [0x2075] = "5", [0x2080] = "0", [0x2081] = "1",
        [0x2082] = "2", [0x2083] = "3",
    };

    /// <summary>Every character of <paramref name="text"/> that LabVIEW would not store as given.</summary>
    internal static IReadOnlyList<Offender> Problems(string text)
    {
        var offenders = new List<Offender>();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var character = rune.ToString();
            string? reason = null;

            if (value is '\t' or '\r' or '\n') { }
            else if (value < 0x20 || value == 0x7F)
                reason = "a control character - only TAB, CR and LF are allowed";
            else if (value is >= 0x80 and <= 0x9F)
                reason = "a C1 control character, not a printable Windows-1252 character";
            else if (!Encodable(character))
                reason = "not in Windows-1252 (CP1252); LabVIEW would store it as '?'";

            if (reason is not null)
                offenders.Add(new Offender(index, character, $"U+{value:X4}",
                    Suggestions.TryGetValue(value, out var s) ? s : null, reason));
            index += rune.Utf16SequenceLength;
        }
        return offenders;
    }

    /// <summary>
    /// The text with every offender replaced by its suggestion, or null when at least one has
    /// none - a partial rewrite would still be refused, so it is not offered.
    /// </summary>
    internal static string? Suggested(string text, IReadOnlyList<Offender> offenders)
    {
        if (offenders.Count == 0 || offenders.Any(o => o.Suggestion is null)) return null;
        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var o in offenders.OrderBy(o => o.Index))
        {
            sb.Append(text, cursor, o.Index - cursor).Append(o.Suggestion);
            cursor = o.Index + o.Character.Length;
        }
        return sb.Append(text, cursor, text.Length - cursor).ToString();
    }

    private static bool Encodable(string character)
    {
        try { Cp1252.GetBytes(character); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    /// <summary>
    /// The text with every line break written CRLF, which is what LabVIEW itself writes on
    /// Windows: read from NI's own files, a multi-line class description is stored CRLF in the
    /// .lvclass and a VI description CRLF in the .vi's STRG resource.
    /// </summary>
    internal static string CrLf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    /// <summary>
    /// The text with every line break written LF - what a CLASS or LIBRARY description must be
    /// SENT as. LabVIEW's own {LV.LVClassLibrary}/{LV.Library} Save writes each LF as CRLF, and it
    /// does so to a CRLF as well: measured 2026-10-13, a description sent CRLF reached the
    /// .lvclass and the .lvlib as CR CR LF, which an XML reader hands back as a blank line per
    /// break. The file still ends up CRLF, as LabVIEW writes it; the VI route keeps <see cref="CrLf"/>.
    /// </summary>
    internal static string Lf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// The form two descriptions are compared in: line breaks as LF. An XML reader hands every
    /// CRLF back as LF anyway (XML end-of-line handling), so comparing raw strings would report a
    /// multi-line description as wrong on every read-back.
    /// </summary>
    internal static string Comparable(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
}

/// <summary>One item of a .lvclass or .lvlib, as the file lists it.</summary>
/// <param name="Folders">The virtual folders it sits in, outermost first; empty at the root.</param>
/// <param name="ParentName">The item element directly around it, or null at the root.</param>
/// <param name="ParentType">That element's Type - "Folder", or "Property Definition" for an accessor.</param>
internal sealed record OwnerItem(string Name, string Type, string? Url, string? ResolvedPath,
                                 IReadOnlyList<string> Folders, string? ParentName, string? ParentType);

/// <summary>A virtual folder of a .lvclass or .lvlib, with the folders around it.</summary>
internal sealed record OwnerFolder(string Name, IReadOnlyList<string> Outer)
{
    public string Path => string.Join("/", Outer.Append(Name));
}

/// <summary>
/// What a <c>.lvclass</c> or <c>.lvlib</c> says on disk: its description, its members and its
/// virtual folders. Read as XML, no LabVIEW involved - the source every housekeeping tool verifies
/// against after LabVIEW has saved.
///
/// A URL is relative to the owner file ITSELF treated as a directory (a sibling reads
/// <c>../X.vi</c>), the convention this repository has measured for both file kinds. A LabVIEW
/// SYMBOLIC url (<c>/&lt;vilib&gt;/…</c>) is kept but never resolved.
///
/// <c>Parent Libraries</c> is NOT walked: its children are the class's parents and implemented
/// interfaces, not members.
/// </summary>
internal sealed record OwnerFile(string Path, bool IsClass, string? Description,
                                 IReadOnlyList<OwnerItem> Items, IReadOnlyList<OwnerFolder> Folders)
{
    internal const string DescriptionProperty = "NI.Lib.Description";

    public static OwnerFile Read(string ownerPath)
    {
        var full = System.IO.Path.GetFullPath(ownerPath);
        var root = XDocument.Load(full).Root
            ?? throw new System.Xml.XmlException($"'{full}' has no root element.");

        var description = root.Elements("Property")
            .FirstOrDefault(p => (string?)p.Attribute("Name") == DescriptionProperty)?.Value;

        var items = new List<OwnerItem>();
        var folders = new List<OwnerFolder>();

        void Walk(XElement element, List<string> chain, string? parentName, string? parentType)
        {
            foreach (var item in element.Elements("Item"))
            {
                var name = (string?)item.Attribute("Name") ?? "";
                var type = (string?)item.Attribute("Type") ?? "";
                if (type == "Parent Libraries") continue;

                if (type == "Folder")
                {
                    folders.Add(new OwnerFolder(name, [.. chain]));
                    Walk(item, [.. chain, name], name, type);
                    continue;
                }

                var url = (string?)item.Attribute("URL");
                items.Add(new OwnerItem(name, type, url, Resolve(full, url), [.. chain],
                                        parentName, parentType));
                if (item.Elements("Item").Any()) Walk(item, chain, name, type);
            }
        }

        Walk(root, [], null, null);
        return new OwnerFile(full, root.Name.LocalName == "LVClass"
                                   || full.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase),
                             description, items, folders);
    }

    private static string? Resolve(string ownerPath, string? url)
    {
        if (url is not { Length: > 0 } || url.Contains('<')) return null;
        try { return System.IO.Path.GetFullPath(System.IO.Path.Combine(ownerPath, url.Replace('/', '\\'))); }
        catch (ArgumentException) { return null; }
    }

    public OwnerItem? Item(string name) =>
        Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal))
        ?? Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public OwnerItem? ItemAt(string path) =>
        Items.FirstOrDefault(i => i.ResolvedPath is { } p
            && string.Equals(p, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<OwnerFolder> FoldersNamed(string name) =>
        [.. Folders.Where(f => string.Equals(f.Name, name, StringComparison.Ordinal))];
}

/// <summary>
/// What a <c>.vi</c> or <c>.ctl</c> says about itself in its resource file, read without LabVIEW
/// through the block table <see cref="ViIconLayers.Blocks"/> already walks.
///
/// - <c>STRG</c> holds the VI description: a big-endian 32-bit length, then the text in CP1252,
///   line breaks CRLF. Read off NI's examples (2026-10-12): e.g. 41 bytes, length 37, for
///   "Return the &lt;b&gt;Cost&lt;/b&gt; of a Beverage." A VI with no description may have no
///   block at all, which reads as empty.
/// - <c>LIBN</c> names the library chain that owns the file: a 32-bit count, then that many
///   length-prefixed names. A class member's reads one name, its <c>.lvclass</c>; a loose VI has
///   no block. Read off NI's examples the same day: a class member and a class-owned typedef both
///   carried it, a top-level VI did not.
/// - <c>LIvi</c>, <c>LIfp</c>, <c>LIbd</c> are the link records, where a caller names what it
///   links to as length-prefixed strings - which is what <see cref="Links"/> looks for.
/// </summary>
internal static class ViFile
{
    internal static string Description(string viPath)
    {
        var block = ViIconLayers.Blocks(File.ReadAllBytes(viPath), "STRG").FirstOrDefault();
        if (block is null || block.Length < 4) return "";
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(block), (uint)(block.Length - 4));
        return AnsiText.Windows1252.GetString(block, 4, length);
    }

    internal static IReadOnlyList<string> Owners(string viPath)
    {
        var block = ViIconLayers.Blocks(File.ReadAllBytes(viPath), "LIBN").FirstOrDefault();
        if (block is null || block.Length < 4) return [];
        var count = BinaryPrimitives.ReadUInt32BigEndian(block);
        var names = new List<string>();
        var at = 4;
        for (var i = 0; i < count && at < block.Length; i++)
        {
            int length = block[at];
            if (at + 1 + length > block.Length) break;
            names.Add(Encoding.Latin1.GetString(block, at + 1, length));
            at += 1 + length;
        }
        return names;
    }

    /// <summary>
    /// Whether the file's link records name <paramref name="fileName"/> as a whole length-prefixed
    /// string - so <c>Drink Type.ctl</c> does not match a link to <c>My Drink Type.ctl</c>. A
    /// HEURISTIC, and said so wherever it is used: two files of the same name in different folders
    /// are not told apart, and the link format itself is not decoded.
    /// </summary>
    internal static bool Links(string viPath, string fileName)
    {
        var file = File.ReadAllBytes(viPath);
        var needle = Encoding.Latin1.GetBytes(fileName);
        if (needle.Length is 0 or > 255) return false;
        foreach (var type in new[] { "LIvi", "LIfp", "LIbd" })
            foreach (var block in ViIconLayers.Blocks(file, type))
                for (var from = 0; from < block.Length;)
                {
                    var hit = block.AsSpan(from).IndexOf(needle);
                    if (hit < 0) break;
                    var at = from + hit;
                    if (at > 0 && block[at - 1] == needle.Length) return true;
                    from = at + 1;
                }
        return false;
    }

    /// <summary>A hash of the icon PIXELS - the icl8, icl4 and ICON resources - for a before/after compare.</summary>
    internal static string IconHash(string viPath)
    {
        var file = File.ReadAllBytes(viPath);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var type in new[] { "icl8", "icl4", "ICON" })
            foreach (var block in ViIconLayers.Blocks(file, type))
            {
                sha.AppendData(Encoding.ASCII.GetBytes(type));
                sha.AppendData(block);
            }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}

/// <summary>
/// Which files a call rewrote, by comparing write time and length before and after. The honest
/// answer to "what did that save touch": a tool that claims to save ONE file is checked against
/// every file under the folder it works in, not against what LabVIEW reported.
/// </summary>
internal sealed class FileStamps
{
    internal const int MaxFiles = 20_000;

    private readonly Dictionary<string, (DateTime Time, long Length)> _stamps;

    /// <summary>False when a root held more than <see cref="MaxFiles"/> files and was not read whole.</summary>
    public bool Complete { get; }

    private FileStamps(Dictionary<string, (DateTime, long)> stamps, bool complete)
    {
        _stamps = stamps;
        Complete = complete;
    }

    public static FileStamps Take(IEnumerable<string> roots)
    {
        var stamps = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            try
            {
                foreach (var file in files)
                {
                    if (stamps.Count >= MaxFiles) { complete = false; break; }
                    var info = new FileInfo(file);
                    stamps[info.FullName] = (info.LastWriteTimeUtc, info.Length);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; }
        }
        return new FileStamps(stamps, complete);
    }

    /// <summary>Files that are new, gone, or carry a different write time or length.</summary>
    public IReadOnlyList<string> ChangedSince(FileStamps before)
    {
        var changed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, stamp) in _stamps)
            if (!before._stamps.TryGetValue(path, out var old) || old != stamp) changed.Add(path);
        foreach (var path in before._stamps.Keys)
            if (!_stamps.ContainsKey(path)) changed.Add(path);
        return [.. changed];
    }

    internal static string Stamp(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);
}
