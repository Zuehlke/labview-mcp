using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Everything about a <c>.lvclass</c> that can be done without LabVIEW: the flattened-string
/// codec, the wrapper around the private data control, the field grammar a caller writes, the
/// document itself, and reading one back.
///
/// WHY THIS EXISTS. No RPC creates a class - measured, and now settled: server reflection reports
/// 23 RPCs on <c>lvai.LVAI</c> and not one of them touches a library, a class or a project. So a
/// class is ours to write, and the only part LabVIEW has to contribute is the private data
/// CLUSTER, which AIXML can generate as an ordinary VI. <see cref="Tools.ClassTools"/> composes the
/// two.
///
/// THE COST OF NOT HAVING IT, measured 2026-08-26: building three classes by hand took 18 minutes
/// and about twelve round trips, and most of that went on one silent failure - a private data blob
/// whose length field sat two bytes late. LabVIEW's answer to that was three class entries with
/// every field blank and an error about invalid *paths*, from a Property Node inside NI's own
/// <c>Get library info.vi</c>. Nothing pointed at the blob. Hence <see cref="Wrap"/> is written
/// once, here, with a round trip test that pins it.
///
/// See docs/lvclass-creation.md for the measurements and docs/lvlib-lvclass-structure.md for the
/// census this relies on.
/// </summary>
internal static class LvClass
{
    // ------------------------------------------------------------------ flattened string codec

    /// <summary>
    /// LabVIEW's flattened-string encoding: six bits per character, offset <c>0x21</c>, most
    /// significant bits first. The same scheme carries <c>NI.LVClass.ParentClassLinkInfo</c>,
    /// <c>NI.Lib.Icon</c> and the private data control.
    ///
    /// scripts\pylv-class-privatedata.py carries its own copy so it runs standalone with no build.
    /// That duplication is deliberate and the same arrangement as pylv-conpane.py against
    /// <see cref="ConnectorPane"/>; <c>LvClassCodecTests</c> pins this one against a literal
    /// expectation so the two cannot drift silently.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 5) / 6);
        int bits = 0, held = 0;

        foreach (var b in data)
        {
            bits = (bits << 8) | b;
            held += 8;
            while (held >= 6)
            {
                held -= 6;
                text.Append((char)(((bits >> held) & 0x3F) + 0x21));
            }
        }

        // The final group is padded with zero bits. Decoding it yields no extra byte, because a
        // byte is only emitted once eight bits are held - which is why the round trip closes.
        if (held > 0) text.Append((char)(((bits << (6 - held)) & 0x3F) + 0x21));

        return text.ToString();
    }

    /// <summary>
    /// The inverse. Whitespace is skipped: LabVIEW writes these properties as one long line, but
    /// nothing guarantees that and a wrapped one must still decode.
    /// </summary>
    public static byte[] Decode(string text)
    {
        var bytes = new List<byte>(text.Length * 6 / 8 + 1);
        int bits = 0, held = 0;

        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' or ' ') continue;
            bits = (bits << 6) | ((ch - 0x21) & 0x3F);
            held += 6;
            if (held >= 8)
            {
                held -= 8;
                bytes.Add((byte)((bits >> held) & 0xFF));
            }
        }

        return [.. bytes];
    }

    // ------------------------------------------------------------------ the private data wrapper

    /// <summary>
    /// The 29 bytes in front of the length field, copied verbatim from a LabVIEW-authored class
    /// (examples\Channels\Event Messenger\...\Circle Message.lvclass, LabVIEW 2026 Q3). Only the
    /// first four are understood - they are the <c>LVVersion</c>. Reusing the rest works; deriving
    /// them was not necessary and has not been attempted.
    /// </summary>
    private static readonly byte[] Header = Convert.FromHexString(
        "26008000000000020005000500000c00400001ffffffff000000010001");

    /// <summary>Four zero bytes close the property, after the .ctl.</summary>
    private static readonly byte[] Trailer = [0, 0, 0, 0];

    /// <summary>Where the u32 big-endian length of the .ctl sits. Getting this wrong is silent.</summary>
    public const int LengthFieldOffset = 29;

    /// <summary>
    /// A <c>.ctl</c>'s bytes as the text of <c>NI.LVClass.FlattenedPrivateDataCTL</c>, XML-escaped
    /// and ready to place in the document. The class file stores the whole control - there is no
    /// <c>.ctl</c> on disk beside a class, which is why <see cref="Document"/> names one that does
    /// not exist.
    /// </summary>
    public static string Wrap(ReadOnlySpan<byte> ctl)
    {
        var blob = new byte[Header.Length + 4 + ctl.Length + Trailer.Length];
        Header.CopyTo(blob, 0);
        blob[LengthFieldOffset + 0] = (byte)(ctl.Length >> 24);
        blob[LengthFieldOffset + 1] = (byte)(ctl.Length >> 16);
        blob[LengthFieldOffset + 2] = (byte)(ctl.Length >> 8);
        blob[LengthFieldOffset + 3] = (byte)ctl.Length;
        ctl.CopyTo(blob.AsSpan(LengthFieldOffset + 4));
        Trailer.CopyTo(blob, blob.Length - Trailer.Length);

        return Escape(Encode(blob));
    }

    /// <summary>
    /// The <c>.ctl</c> back out of a class file's property text, sliced by the length field rather
    /// than by "everything after the header" - the four trailing zeros are not part of the control,
    /// and including them is exactly the mistake that cost 2026-08-26 its afternoon.
    /// </summary>
    /// <summary>
    /// A <c>Type="Bin"</c> property's text as the bytes it stands for: XML-unescaped, then decoded.
    /// Separate from <see cref="Unwrap"/> because the private data property is the only one with the
    /// length-prefixed wrapper - <c>ParentClassLinkInfo</c> and <c>NI.Lib.Icon</c> are plain
    /// flattened data and stop here.
    /// </summary>
    public static byte[] DecodeProperty(string propertyText) => Decode(Unescape(propertyText));

    public static byte[] Unwrap(string propertyText)
    {
        var blob = DecodeProperty(propertyText);
        if (blob.Length < LengthFieldOffset + 4)
            throw new InvalidDataException(
                $"The private data property decodes to {blob.Length} bytes, too short to carry a " +
                "length field. It is not a flattened control.");

        var length = (blob[LengthFieldOffset] << 24) | (blob[LengthFieldOffset + 1] << 16) |
                     (blob[LengthFieldOffset + 2] << 8) | blob[LengthFieldOffset + 3];

        if (length < 0 || LengthFieldOffset + 4 + length > blob.Length)
            throw new InvalidDataException(
                $"The private data length field says {length} bytes but only " +
                $"{blob.Length - LengthFieldOffset - 4} follow it. The header is the wrong size, " +
                "or the text is truncated.");

        return blob[(LengthFieldOffset + 4)..(LengthFieldOffset + 4 + length)];
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// Safe to apply to text an XML parser has ALREADY unescaped, which is how <see cref="Read"/>
    /// reaches it: the codec emits only characters 0x21-0x60, and every entity spelling needs a
    /// letter above that range - `l` in <c>&amp;lt;</c> is 0x6C, `m` in <c>&amp;amp;</c> is 0x6D -
    /// so no entity can occur inside encoded output by accident. That invariant is what lets
    /// <see cref="Unwrap"/> take either form.
    /// </summary>
    private static string Unescape(string text) => text
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&apos;", "'", StringComparison.Ordinal)
        .Replace("&amp;", "&", StringComparison.Ordinal);

    // ------------------------------------------------------------------ the field grammar

    /// <summary>One private data field: an AIXML scalar type name and the label it carries.</summary>
    public sealed record Field(string Type, string Name);

    /// <summary>
    /// The AIXML scalar types a field may use, with the <c>value</c> literal each needs. AIXML
    /// requires <c>value</c> on every Control, and the literal is per-type - so an allowlist is
    /// the honest shape: an unknown type would need a guessed literal, and a wrong literal
    /// generates without complaint (§2 of aixml-reference.md records <c>TRUE</c> running as false).
    ///
    /// VERIFIED end to end on 2026-08-26: string, bool, double, int32, uint32. The remaining widths
    /// are the same shape and are ASSUMED; the tool runs ValidateAIXML first, so LabVIEW rejects a
    /// wrong one by name before anything is written.
    /// </summary>
    private static readonly Dictionary<string, string> Literals = new(StringComparer.Ordinal)
    {
        ["string"] = "",
        // A timestamp's default is the EMPTY literal, the same shape as a string's - counted in 20
        // cached exports, every `type="timestamp"` carrying `value=""`, controls and constants
        // alike. It was left out of this table at first, which refused `timestamp.Timestamp` as a
        // type "this tool has no default literal for" and read as though AIXML could not express
        // one. AIXML can; only the table could not.
        ["timestamp"] = "",
        ["bool"] = "false",
        ["double"] = "0",
        ["single"] = "0",
        ["int8"] = "0",
        ["int16"] = "0",
        ["int32"] = "0",
        ["int64"] = "0",
        ["uint8"] = "0",
        ["uint16"] = "0",
        ["uint32"] = "0",
        ["uint64"] = "0",
    };

    public static IReadOnlyCollection<string> KnownTypes => Literals.Keys;

    /// <summary>
    /// Parse <c>string.Manufacturer, int32.Year Of Manufacture</c> into fields. The separator is a
    /// comma because that is what the AIXML cluster grammar uses, which also means a field NAME
    /// cannot contain one - said here rather than discovered from a malformed type string.
    /// </summary>
    public static List<Field> ParseFields(string? spec)
    {
        var fields = new List<Field>();
        if (string.IsNullOrWhiteSpace(spec)) return fields;

        foreach (var entry in spec.Split(',', StringSplitOptions.TrimEntries |
                                             StringSplitOptions.RemoveEmptyEntries))
        {
            var dot = entry.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0 || dot == entry.Length - 1)
                throw new ArgumentException(
                    $"'{entry}' is not a field. Each one is <type>.<name>, e.g. " +
                    "'string.Manufacturer' - the same spelling AIXML's cluster grammar uses.");

            var type = entry[..dot].Trim();
            var name = entry[(dot + 1)..].Trim();

            if (!Literals.ContainsKey(type))
                throw new ArgumentException(
                    $"'{entry}' has type '{type}', which this tool has no default literal for. " +
                    $"The types it knows are {string.Join(", ", Literals.Keys.Order())}. A cluster, " +
                    "an array or an enum field is not supported here - generate that private data " +
                    "control by hand and use docs/lvclass-creation.md section 1.");

            if (name.Contains('{') || name.Contains('}'))
                throw new ArgumentException(
                    $"'{entry}' has a brace in its field name. The AIXML cluster type string is " +
                    "delimited by braces, so a name cannot carry one.");

            if (fields.Any(f => string.Equals(f.Name, name, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"'{name}' appears twice. LabVIEW allows it in a cluster and nothing good " +
                    "comes of it - an accessor cannot say which one it means.");

            fields.Add(new Field(type, name));
        }

        return fields;
    }

    /// <summary>
    /// The CARRIER: a VI whose front panel holds one control per field, and nothing else.
    ///
    /// NI's <c>Add Member Data to Private Data Control.vi</c> takes an array of front-panel
    /// CONTROL REFERENCES and makes one field out of each, taking its name and its type. So the
    /// fields are expressed as controls on a throwaway VI rather than as a cluster, and this is
    /// the one part of class creation AIXML is good at.
    ///
    /// The cluster this used to author - <c>Cluster of class private data</c>, converted into a
    /// control by flipping flags - is gone with the route that needed it: the result was a class
    /// LabVIEW reported normally and refused to compile, because a private data control's type
    /// space is compiler output. docs/lvclass-creation.md section 2a.
    /// </summary>
    public static string CarrierAixml(string className, IReadOnlyList<Field> fields)
    {
        var controls = string.Join("\n", fields.Select((f, i) =>
            $"""  <Control _name="{f.Name}" outputs="value:" type="{f.Type}" uid="{10 + i}" uid_parent="root" value="{Literals[f.Type]}"/>"""));
        return $"""
                <VI _name="{className}-fields.vi" description="Carrier for the private data fields of {className}.lvclass. Its front-panel controls are handed to NI's Add Member Data to Private Data Control.vi as references\2C so each control's name and type becomes a field. Nothing here runs.">
                {controls}
                </VI>
                """;
    }

    // ------------------------------------------------------------------ the document

    /// <summary>
    /// The class file. Tabs and single-quoted declaration because that is what LabVIEW writes, and
    /// matching it keeps a diff against a file LabVIEW has re-saved readable.
    ///
    /// The properties are the minimum that loads clean, measured: no <c>NI.Lib.Icon</c> and no
    /// <c>NI.LVClass.Geneology</c> are needed, and LabVIEW added neither when it saved three such
    /// classes - it did not rewrite one byte of them.
    /// </summary>
    public static string Document(string className, string privateDataBlob,
                                  string? parentQualifiedName, string? parentUrl)
    {
        var text = new StringBuilder();
        text.Append("<?xml version='1.0' encoding='UTF-8'?>\r\n");
        text.Append("<LVClass LVVersion=\"26008000\">\r\n");
        text.Append("\t<Property Name=\"NI.Lib.SourceVersion\" Type=\"Int\">637566976</Property>\r\n");
        text.Append("\t<Property Name=\"NI.Lib.Version\" Type=\"Str\">1.0.0.0</Property>\r\n");
        text.Append("\t<Property Name=\"NI.LV.All.SourceOnly\" Type=\"Bool\">true</Property>\r\n");
        text.Append("\t<Property Name=\"NI.LVClass.ClassNameVisibleInProbe\" Type=\"Bool\">false</Property>\r\n");
        text.Append($"\t<Property Name=\"NI.LVClass.FlattenedPrivateDataCTL\" Type=\"Bin\">{privateDataBlob}</Property>\r\n");
        text.Append("\t<Property Name=\"NI.LVClass.LowestCompatibleVersion\" Type=\"Str\">1.0.0.0</Property>\r\n");

        if (parentQualifiedName is not null && parentUrl is not null)
        {
            text.Append("\t<Item Name=\"Parent Libraries\" Type=\"Parent Libraries\">\r\n");
            text.Append($"\t\t<Item Name=\"{Xml(parentQualifiedName)}\" Type=\"Parent\" URL=\"{Xml(parentUrl)}\"/>\r\n");
            text.Append("\t</Item>\r\n");
        }

        // Scope 2 is private, which is what LabVIEW gives a private data control. The URL names a
        // .ctl that does not exist on disk and must not be created: the control lives in the
        // property above, and LabVIEW materialises the path only in memory.
        text.Append($"\t<Item Name=\"{Xml(className)}.ctl\" Type=\"Class Private Data\" URL=\"{Xml(className)}.ctl\">\r\n");
        text.Append("\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">2</Property>\r\n");
        text.Append("\t</Item>\r\n");
        text.Append("</LVClass>\r\n");

        return text.ToString();
    }

    private static string Xml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    // ------------------------------------------------------------------ reading one back

    public sealed record Member(string Name, string Url, string Type, string Scope,
                                bool? DynamicDispatch);

    public sealed record ClassInfo(
        string Path, string ClassName, string? ContainingLibrary, string QualifiedName,
        IReadOnlyList<string> Ancestors, string AncestorSource, string? PrivateDataName,
        int PrivateDataBytes, IReadOnlyList<Member> Members);

    /// <summary>
    /// A class file's own account of itself. The reason this is worth a reader at all:
    /// <c>lvai_describe_project</c>'s <c>parent</c> field is the OWNING LIBRARY, not the base class
    /// - measured on NI's own hierarchy, where <c>Circle Message.lvclass</c> derives from
    /// <c>Draw Message.lvclass</c> and reports <c>"parent": "Draw Messages.lvlib"</c>. So nothing
    /// in the gRPC interface reports inheritance, and the file is the only source.
    /// </summary>
    public static ClassInfo Read(string lvclassPath)
    {
        var full = System.IO.Path.GetFullPath(lvclassPath);
        var document = XDocument.Load(full);
        var root = document.Root
            ?? throw new InvalidDataException($"'{full}' has no root element.");

        if (root.Name.LocalName != "LVClass")
            throw new InvalidDataException(
                $"'{full}' has root <{root.Name.LocalName}>, not <LVClass>. A .lvlib and a " +
                ".lvproj use the same grammar with a different root - this reader is for classes.");

        var className = System.IO.Path.GetFileNameWithoutExtension(full);
        var library = Property(root, "NI.Lib.ContainingLib");

        var (ancestors, source) = Ancestors(root);

        var privateData = root.Elements("Item")
            .FirstOrDefault(i => (string?)i.Attribute("Type") == "Class Private Data");

        var blob = Property(root, "NI.LVClass.FlattenedPrivateDataCTL");
        var blobBytes = 0;
        if (blob is not null)
            try { blobBytes = Unwrap(blob).Length; }
            catch (InvalidDataException) { blobBytes = -1; }

        return new ClassInfo(
            full, className, library,
            library is null ? $"{className}.lvclass" : $"{library}:{className}.lvclass",
            ancestors, source,
            (string?)privateData?.Attribute("Name"), blobBytes,
            [.. Members(root)]);
    }

    /// <summary>
    /// The ancestor chain, checking both representations in the order the census established:
    /// plain-text <c>Parent</c> items (LabVIEW 2026 only, 85 of 189 classes) then the encoded
    /// <c>ParentClassLinkInfo</c> (every version at or below 20xxxxxx). No file carried both, and
    /// 71 carried neither, which means <c>LabVIEW Object</c>.
    /// </summary>
    private static (IReadOnlyList<string>, string) Ancestors(XElement root)
    {
        var plain = root.Elements("Item")
            .Where(i => (string?)i.Attribute("Type") == "Parent Libraries")
            .SelectMany(i => i.Elements("Item"))
            .Where(i => (string?)i.Attribute("Type") == "Parent")
            .Select(i => (string?)i.Attribute("Name"))
            .OfType<string>()
            .ToList();

        // The entries are the whole chain, nearest first.
        if (plain.Count > 0) return (plain, "Parent Libraries items (plain text)");

        // Older files: pull the names out of the decoded bytes with a regex rather than parsing the
        // PTH0 record layout, which was not worth reverse-engineering.
        //
        // TAKE THE LAST .lvclass, NOT THE FIRST MATCH. A PTH0 record spells the path out, so the
        // owning LIBRARY comes before the class - lvlib-lvclass-structure.md §3 says the immediate
        // parent is the last `.lvclass` name and the `.lvlib` before it is its owning library.
        // Reported wrongly until 2026-08-26: JKI's Lexer.lvclass came back inheriting from
        // "JKI JSON Serialization.lvlib", which is not a class at all and cannot be a parent.
        foreach (var property in new[] { "NI.LVClass.ParentClassLinkInfo", "NI.LVClass.Geneology" })
        {
            if (Property(root, property) is not { Length: > 0 } encoded) continue;

            var decoded = Encoding.Latin1.GetString(Decode(Unescape(encoded)));
            var names = Regex.Matches(decoded, @"[\w \-.()]{2,}\.lv(?:class|lib)")
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var classes = names
                .Where(n => n.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // The two properties carry different things and must not be read the same way:
            // ParentClassLinkInfo is ONE parent, so its last .lvclass is the answer. Geneology is
            // the WHOLE ancestry, so reporting one name from it would invent a direct parent.
            if (classes.Count > 0)
                return property.EndsWith("ParentClassLinkInfo", StringComparison.Ordinal)
                    ? ([classes[^1]],
                       $"{property} (decoded - one parent, taken as the last .lvclass in the record)")
                    : (classes,
                       $"{property} (decoded - the WHOLE ancestry, and the order is not " +
                       "guaranteed, so which of these is the direct parent is not established " +
                       "here. Only a Parent Libraries item or ParentClassLinkInfo says that.");

            // A record naming no class at all is not an ancestry answer, so say so rather than
            // handing back a library as if it were a parent.
            if (names.Count > 0)
                return ([], $"{property} decoded but named no .lvclass - " +
                            $"only {string.Join(", ", names)}; treat as LabVIEW Object");
        }

        return ([], "no parent recorded - derives from LabVIEW Object");
    }

    /// <summary>
    /// The members with their effective scope. A class records scope PER MEMBER in
    /// <c>NI.ClassItem.MethodScope</c> - unlike a .lvlib, where it sits on the folder and the
    /// children inherit it - so no propagation is needed here. Never read the folder name: the
    /// census found folders called <c>private</c> whose 12 members were all effectively public.
    /// </summary>
    private static IEnumerable<Member> Members(XElement root)
    {
        foreach (var item in Descend(root))
        {
            // `.vi`, `.vim` and `.ctl` members all carry Type="VI" - 4 163 of them in the census -
            // so the kind comes from the extension, not from the attribute.
            if ((string?)item.Attribute("Type") != "VI") continue;

            var scope = Property(item, "NI.ClassItem.MethodScope") ??
                        Property(item, "NI.LibItem.Scope");

            var isStatic = Property(item, "NI.ClassItem.IsStaticMethod");

            yield return new Member(
                (string?)item.Attribute("Name") ?? "",
                (string?)item.Attribute("URL") ?? "",
                System.IO.Path.GetExtension((string?)item.Attribute("Name") ?? "")
                    .TrimStart('.') switch { "ctl" => "control", "vim" => "malleable VI", _ => "VI" },
                scope switch
                {
                    "1" => "public",
                    "2" => "private",
                    "3" => "protected",
                    "4" => "not public (flavour unverified)",
                    null => "public (no scope recorded)",
                    _ => $"unknown ({scope})",
                },
                isStatic is null ? null : isStatic == "false");
        }
    }

    /// <summary>Items at any depth: a class may nest members in virtual folders.</summary>
    private static IEnumerable<XElement> Descend(XElement element)
    {
        foreach (var item in element.Elements("Item"))
        {
            yield return item;
            foreach (var nested in Descend(item)) yield return nested;
        }
    }

    private static string? Property(XElement parent, string name) => parent
        .Elements("Property")
        .FirstOrDefault(p => (string?)p.Attribute("Name") == name)?.Value;

    // ------------------------------------------------------------------ the project entry

    /// <summary>
    /// The target-scope properties LabVIEW writes on `My Computer`, in the alphabetical order
    /// <c>lvproj-structure.md</c> section 9 prescribes and section 6 documents. Written in full
    /// rather than left to defaults: the first version of this generator emitted
    /// <c>server.tcp.enabled</c> alone, and a project LabVIEW has to complete is a project LabVIEW
    /// marks dirty the moment it opens it.
    /// </summary>
    private static readonly (string Name, string Type, string Value)[] TargetProperties =
    [
        ("server.app.propertiesEnabled", "Bool", "true"),
        ("server.control.propertiesEnabled", "Bool", "true"),
        ("server.tcp.enabled", "Bool", "false"),
        ("server.tcp.port", "Int", "0"),
        ("server.tcp.serviceName", "Str", "My Computer/VI Server"),
        ("server.tcp.serviceName.default", "Str", "My Computer/VI Server"),
        ("server.vi.callsEnabled", "Bool", "true"),
        ("server.vi.propertiesEnabled", "Bool", "true"),
        ("specify.custom.address", "Bool", "false"),
    ];

    /// <summary>
    /// A <c>.lvproj</c> listing the classes given. Written rather than asked for, because no RPC
    /// adds a file to a project either - <c>.lvproj</c> generation is on NI's unsupported list, so
    /// this is plain XML work.
    ///
    /// Shaped after the recipe in <c>lvproj-structure.md</c> section 9, which was verified loading
    /// on LabVIEW 2026: two project-scope properties, the nine target-scope ones, then the content
    /// items, then <c>Dependencies</c> and <c>Build Specifications</c>.
    /// </summary>
    public static string Project(IEnumerable<(string Name, string Url)> classes)
    {
        var text = new StringBuilder();
        text.Append("<?xml version='1.0' encoding='UTF-8'?>\r\n");
        text.Append("<Project Type=\"Project\" LVVersion=\"26008000\">\r\n");
        text.Append("\t<Property Name=\"NI.LV.All.SourceOnly\" Type=\"Bool\">false</Property>\r\n");
        text.Append("\t<Property Name=\"NI.Project.Description\" Type=\"Str\"></Property>\r\n");
        text.Append("\t<Item Name=\"My Computer\" Type=\"My Computer\">\r\n");
        foreach (var (name, type, value) in TargetProperties)
            text.Append($"\t\t<Property Name=\"{name}\" Type=\"{type}\">{Xml(value)}</Property>\r\n");
        foreach (var (name, url) in classes)
            text.Append($"\t\t<Item Name=\"{Xml(name)}\" Type=\"LVClass\" URL=\"{Xml(url)}\"/>\r\n");
        text.Append("\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>\r\n");
        text.Append("\t\t<Item Name=\"Build Specifications\" Type=\"Build\"/>\r\n");
        text.Append("\t</Item>\r\n");
        text.Append("</Project>\r\n");
        return text.ToString();
    }

    /// <summary>
    /// Add one class to an existing project, in front of <c>Dependencies</c> so it lands among the
    /// content items rather than after them. Returns false when it is already listed - adding it
    /// twice makes LabVIEW report a conflict, which reads as a broken class.
    ///
    /// PARSED TO DECIDE, EDITED AS TEXT TO WRITE. Round-tripping through <c>XDocument.Save</c> is
    /// what the first version did, and it rewrote the whole file: a BOM appeared, the declaration's
    /// single quotes became double, tabs became two spaces, and every self-closing tag gained a
    /// space. None of that stops LabVIEW loading the project - the load check passed throughout -
    /// but <c>lvproj-structure.md</c> section 8 keeps those rules precisely so a diff against a
    /// LabVIEW-saved file is signal rather than noise, and the second class written into a project
    /// reformatted what the first one wrote. A line insert preserves the style, the line endings
    /// and the BOM of whatever file it is handed, including one LabVIEW wrote itself.
    /// </summary>
    public static bool AddToProject(string projectPath, string className, string relativeUrl)
    {
        var name = $"{className}.lvclass";

        var document = XDocument.Load(projectPath);
        var target = document.Root?.Elements("Item")
            .FirstOrDefault(i => (string?)i.Attribute("Type") == "My Computer")
            ?? throw new InvalidDataException(
                $"'{projectPath}' has no <Item Type=\"My Computer\"> target to add the class to.");

        var already = target.Elements("Item").Any(i =>
            (string?)i.Attribute("Type") == "LVClass" &&
            (string.Equals((string?)i.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals((string?)i.Attribute("URL"), relativeUrl, StringComparison.OrdinalIgnoreCase)));
        if (already) return false;

        var text = File.ReadAllText(projectPath);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newline).ToList();

        // Anchor on Dependencies, then Build Specifications: both are machine-managed and always
        // last under the target, so inserting before either lands among the content items. A
        // LabVIEW-saved Dependencies item has children and is therefore not self-closing, which is
        // why the test is on the Type attribute rather than on the whole element.
        var anchor = lines.FindIndex(l => IsItemOfType(l, "Dependencies"));
        if (anchor < 0) anchor = lines.FindIndex(l => IsItemOfType(l, "Build"));
        if (anchor < 0)
            throw new InvalidDataException(
                $"'{projectPath}' has neither a Dependencies nor a Build Specifications item, so " +
                "there is no anchor to insert before. LabVIEW writes both under every target; a " +
                "project without them was not written by LabVIEW and is not safe to edit blind.");

        var indent = lines[anchor][..(lines[anchor].Length - lines[anchor].TrimStart().Length)];
        lines.Insert(anchor,
            $"{indent}<Item Name=\"{Xml(name)}\" Type=\"LVClass\" URL=\"{Xml(relativeUrl)}\"/>");

        File.WriteAllText(projectPath, string.Join(newline, lines));
        return true;
    }

    private static bool IsItemOfType(string line, string type)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("<Item ", StringComparison.Ordinal) &&
               trimmed.Contains($"Type=\"{type}\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// List plain VIs under the target, inside a virtual folder, and answer how many were added.
    /// Already-listed VIs are skipped by NAME at any depth, so calling this twice is safe.
    ///
    /// WHY IT EXISTS: nothing else writes a VI into a `.lvproj`. `lvai_generate_class_test` produced
    /// a complete, green, verified suite on 2026-08-29 and the user's Project Explorer showed three
    /// classes and no tests at all — *"Die Tests fehlen im Projekt!"*. A test nobody can find from
    /// the project is a test nobody runs.
    ///
    /// THE PROJECT MUST BE CLOSED IN LABVIEW WHEN THIS RUNS. LabVIEW's close SAVES its own copy over
    /// the file, so an edit made while it holds the project open is destroyed by the next close.
    /// That is the caller's job to arrange; this only writes the file.
    /// </summary>
    /// <summary>
    /// Every VI the <c>.lvproj</c> lists, as (file name, URL), at any depth.
    ///
    /// TWO CALLERS, BOTH ABOUT THE SAME FAILURE. Read BEFORE LabVIEW closes a project, it says
    /// what has to be put back afterwards - the close saves LabVIEW's own copy over the file and
    /// drops VI items it never had in memory, which is how five test suites generated one at a
    /// time ended up with a single one listed. Read AFTER the edit, it is the only honest answer
    /// to "is it listed now": <c>AddVisToProject</c>'s count is what was written, and the tidy
    /// pass that follows can still take an entry back out.
    /// </summary>
    public static List<(string Name, string Url)> ListedVis(string projectPath)
    {
        try
        {
            if (!File.Exists(projectPath)) return [];

            // Parsed, not pattern-matched, for the reason ListedClasses gives: a regex has to
            // guess at whitespace and attribute order, and XDocument is how AddVisToProject reads
            // the file, so the two agree by construction.
            return XDocument.Load(projectPath).Descendants("Item")
                .Where(i => (string?)i.Attribute("Type") == "VI")
                .Select(i => (Name: (string?)i.Attribute("Name") ?? "",
                              Url: (string?)i.Attribute("URL") ?? ""))
                .Where(v => v.Name.Length > 0 && v.Url.Length > 0)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (System.Xml.XmlException) { return []; }
    }

    public static int AddVisToProject(string projectPath, string folderName,
                                      IReadOnlyList<(string Name, string Url)> vis)
    {
        if (vis.Count == 0) return 0;

        var document = XDocument.Load(projectPath);
        var target = document.Root?.Elements("Item")
            .FirstOrDefault(i => (string?)i.Attribute("Type") == "My Computer")
            ?? throw new InvalidDataException(
                $"'{projectPath}' has no <Item Type=\"My Computer\"> target to add the VIs to.");

        // At ANY depth: a VI already inside some other folder is listed, and adding it again would
        // give the project two items for one file.
        var listed = target.Descendants("Item")
            .Where(i => (string?)i.Attribute("Type") == "VI")
            .Select(i => (string?)i.Attribute("Name"))
            .Where(n => n is { Length: > 0 })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = vis.Where(v => !listed.Contains(v.Name)).ToList();
        if (missing.Count == 0) return 0;

        var text = File.ReadAllText(projectPath);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newline).ToList();

        var folderOpen = $"<Item Name=\"{Xml(folderName)}\" Type=\"Folder\">";
        var folderIndex = lines.FindIndex(l => l.TrimStart().StartsWith(folderOpen,
                                                                       StringComparison.Ordinal));

        int at;
        string indent;
        if (folderIndex >= 0)
        {
            at = folderIndex + 1;
            indent = Indent(lines[folderIndex]) + "\t";
        }
        else
        {
            // Same anchors as AddToProject: Dependencies and Build Specifications are machine
            // managed and always last, so inserting before either lands among the content items.
            var anchor = lines.FindIndex(l => IsItemOfType(l, "Dependencies"));
            if (anchor < 0) anchor = lines.FindIndex(l => IsItemOfType(l, "Build"));
            if (anchor < 0)
                throw new InvalidDataException(
                    $"'{projectPath}' has neither a Dependencies nor a Build Specifications item, " +
                    "so there is no anchor to insert before.");

            indent = Indent(lines[anchor]);
            lines.Insert(anchor, $"{indent}</Item>");
            lines.Insert(anchor, $"{indent}{folderOpen}");
            at = anchor + 1;
            indent += "\t";
        }

        foreach (var vi in Enumerable.Reverse(missing))
            lines.Insert(at,
                $"{indent}<Item Name=\"{Xml(vi.Name)}\" Type=\"VI\" URL=\"{Xml(vi.Url)}\"/>");

        File.WriteAllText(projectPath, string.Join(newline, lines));
        return missing.Count;

        static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];
    }

    /// <summary>
    /// The <c>URL</c> one LabVIEW file uses to point at another.
    ///
    /// **`../` IS RELATIVE TO THE REFERENCING FILE, NOT ITS DIRECTORY** - the leading `..` pops the
    /// file name. So a class sitting beside its project is <c>../Auto/Auto.lvclass</c>, and a parent
    /// class one folder over is <c>../../Auto/Auto.lvclass</c>. This is why `../X` accounts for
    /// 28 803 of the 29 194 URLs in the census, and it is the single easiest thing to get wrong:
    /// <c>lvproj-structure.md</c> section 5 says so, and NI's own
    /// <c>Circle Message.lvclass</c> writes <c>URL="../../Draw Message/Draw Message.lvclass"</c> for
    /// a parent in the sibling folder.
    ///
    /// THIS SHIPPED WRONG. The first version took a DIRECTORY and produced
    /// <c>Auto/Auto.lvclass</c> with no `../` at all, which resolves one level too deep - inside
    /// the `.lvproj` treated as a folder. LabVIEW is lenient enough to find the class anyway, so the
    /// load check passed and nothing reported it; the user reading the project file did. Hence the
    /// parameter is a FILE path now, and `RelativeUrlTests` pins both shapes.
    ///
    /// Forward slashes are what LabVIEW writes here, in a `.lvproj` and a `.lvclass` alike.
    /// </summary>
    public static string RelativeUrl(string referencingFile, string toPath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(referencingFile))
            ?? throw new ArgumentException(
                $"'{referencingFile}' has no directory, so nothing can be relative to it.",
                nameof(referencingFile));

        // "../" first, because the referencing FILE NAME is the first component `..` removes.
        return "../" + System.IO.Path.GetRelativePath(directory, toPath).Replace('\\', '/');
    }

    /// <summary>
    /// The parent's qualified name as a child must spell it: <c>&lt;owning library&gt;:&lt;class&gt;</c>
    /// when the parent belongs to a library, the bare file name otherwise. Read off the parent file
    /// rather than assumed, because getting it wrong is how a parent link silently fails to resolve.
    /// </summary>
    public static string QualifiedName(string lvclassPath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(lvclassPath);
        try
        {
            var root = XDocument.Load(lvclassPath).Root;
            var library = root is null ? null : Property(root, "NI.Lib.ContainingLib");
            return library is null ? $"{name}.lvclass" : $"{library}:{name}.lvclass";
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return $"{name}.lvclass";
        }
    }
}
