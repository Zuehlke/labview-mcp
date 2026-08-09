using System.ComponentModel;
using System.Reflection;
using System.Text;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Serves the AIXML format reference that lives in docs/aixml-reference.md.
///
/// Why this is a tool and not just a file in the repo: a reference nobody reads prevents no
/// mistakes. AIXML has no published schema, and its two nastiest traps fail SILENTLY at
/// authoring time - a `uid.terminal` string names a net rather than pointing at an element,
/// and terminal names are unguessable ("x &gt; y?" has spaces, "x+y" does not). Any client
/// generating AIXML without those rules will produce plausible, wrong XML.
///
/// The document is embedded from docs/, so there is exactly one copy. Editing the markdown
/// and rebuilding updates what the server serves; there is nothing to keep in sync.
/// </summary>
[McpServerToolType]
[McpServerResourceType]
internal sealed class KnowledgeTools
{
    private const string ResourceName = "aixml-reference.md";
    private const string DqmhResourceName = "dqmh-patterns.md";
    private const string LvprojResourceName = "lvproj-structure.md";
    private const string LvlibResourceName = "lvlib-lvclass-structure.md";
    private const string ViServerResourceName = "vi-server-reference.md";
    private const string MethodsResourceName = "vi-server-methods.tsv";
    private const string PropertiesResourceName = "vi-server-properties.tsv";

    /// <summary>
    /// Rows returned when a query matches more than this. A deliberate cap: the two
    /// catalogues are 400 kB together, and the whole point of a query tool is that the
    /// caller never receives the file. Truncation is always reported, never silent.
    /// </summary>
    private const int DefaultLimit = 40;
    private const int MaxLimit = 400;

    /// <summary>The handful of rules that stop a first attempt from being silently wrong.</summary>
    private const string Essentials = """
        AIXML essentials (full detail: call this tool with a section, or 'all')

        1. NETS, NOT POINTERS. A `uid.terminal` string names a *net* (a wire). Every element
           lists which net each of its terminals sits on - inputs for input terminals,
           outputs for outputs. Two terminals are wired iff they name the same net. Fan-out
           = repeat the net string. `inputs` does NOT point at the source element, and an
           element may legitimately name a net after its own uid.
        2. LOOK UP TERMINAL NAMES. They are literal LabVIEW labels: `Increment` -> `x+1`,
           `Greater?` -> `x > y?` (with spaces), `Select` -> `s? t\3Af`, `Or` -> `x .or. y?`.
           Export a VI that already uses the node and copy the string.
        3. ESCAPES. `\3A` = colon, `\2C` = comma, `\0A`/`\0D` = newline - because `:` and `,`
           separate entries in inputs/outputs. XML entities apply on top.
        4. NO LAYOUT. There is no coordinate attribute anywhere. Position, size and diagram
           cleanup cannot be expressed. Keep FreeLabel comments to a few words or they cover
           wires; put prose in the VI `description`.
        5. GENERATED VIs MUST BE SELF-CONTAINED. A `Call` to a project- or library-local
           subVI is rejected as "Unsupported SubVI". Primitives, all structure kinds and the
           whole type system work.
        6. ALWAYS lvai_validate_aixml BEFORE generating. It is cheap and its messages are
           specific.
        """;

    /// <summary>
    /// Past this, a section is big enough that a client spills it to a file - and a file holding
    /// one JSON string is not greppable, so the caller cannot find the paragraph they came for.
    /// Measured: section 8 is 54 kB, and a VI-generator run failed to find a subsection that had
    /// been added to it that same day, then re-derived the fact by exporting a VI.
    /// </summary>
    private const int BigSectionChars = 8_000;

    /// <summary>Past this many hits a lookup is answered with headings, not with passages.</summary>
    private const int FloodThreshold = 25;
    private const int FloodSample = 8;

    [McpServerTool(Name = "lvai_aixml_reference", ReadOnly = true,
                   Title = "AIXML format reference")]
    [Description("""
        The rules for reading and WRITING LabVIEW AIXML. Call this before authoring or editing
        any AIXML - the format has no published schema and its wiring model is
        counter-intuitive, so generating without it produces plausible but wrong XML.
        Without arguments: the essential rules plus a section list (cheap, start here).
        With node: ONLY the passages mentioning that node or term - the right call when you
        want one node's terminal names, e.g. node='Build Waveform', node='Index Array',
        node='graph21703'. Section 8 alone is 54 kB, far past what is worth reading whole.
        With section: that section's full text ('types', 'structures', 'escaping', 'wiring',
        a heading number, or part of a title). With section='all': the whole document.
        """)]
    public static string AixmlReference(
        [Description("Section number, keyword or title fragment; 'all' for everything; omit for the essentials")]
        string? section = null,
        [Description("Node or term to look up, e.g. 'Build Waveform'. Returns only the table " +
                     "rows, code blocks and paragraphs that mention it, each under its heading. " +
                     "Takes precedence over section")]
        string? node = null,
        [Description("Max passages to return (default 40, max 400)")] int limit = DefaultLimit)
    {
        string document;
        try
        {
            document = Load();
        }
        catch (Exception e)
        {
            return Json.Error(e.GetType().Name,
                $"The embedded AIXML reference could not be read: {e.Message}");
        }

        var sections = Split(document);

        if (!string.IsNullOrWhiteSpace(node))
            return Lookup(document, node.Trim(), Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit));

        if (string.IsNullOrWhiteSpace(section))
            return Essentials + Environment.NewLine + Environment.NewLine + Toc(sections);

        if (section.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return document;

        var match = Find(sections, section.Trim()) ?? FindSubsection(document, section.Trim());
        if (match is null)
            // Falling through to the node lookup rather than just listing the sections: the old
            // "No section matched" plus a list of 15 numbers reads as "that content is not here",
            // which was measured sending a caller off to re-derive a documented fact. If the term
            // appears anywhere, show it.
            return Lookup(document, section.Trim(), DefaultLimit);

        return match.Length > BigSectionChars
            ? match + Environment.NewLine + Environment.NewLine +
              $"[{match.Length / 1024} kB. If you came for one node, call this again with " +
              "node='<name>' instead - it returns only the passages that mention it, which is " +
              "searchable where this is not.]"
            : match;
    }

    [McpServerResource(Name = "aixml-reference", UriTemplate = "labview://aixml-reference",
                      MimeType = "text/markdown",
                      Title = "AIXML format reference")]
    [Description("How LabVIEW's AIXML block-diagram format is structured, and how to author it.")]
    public static string AixmlReferenceResource() => Load();

    [McpServerTool(Name = "lvai_dqmh_reference", ReadOnly = true,
                   Title = "DQMH module structure reference")]
    [Description("""
        How a DQMH (Delacor Queued Message Handler) module is built, as it appears in AIXML:
        the fixed set of module VIs, the two-loop Main.vi, the typed request/broadcast event
        clusters, and the naming conventions. Read this before analysing or discussing a
        project that uses DQMH - the two loops sit inside a case frame rather than at the
        diagram root, so a naive tree walk finds nothing.
        Note it also records what CANNOT be generated: every DQMH framework call is a
        project-local subVI, which AIXML generation rejects.
        Without arguments: a section list. With section: that section, or 'all'.
        """)]
    public static string DqmhReference(
        [Description("Section number or title fragment; 'all' for everything; omit for the section list")]
        string? section = null) => Serve(DqmhResourceName, section);

    [McpServerResource(Name = "dqmh-patterns", UriTemplate = "labview://dqmh-patterns",
                      MimeType = "text/markdown",
                      Title = "DQMH module structure reference")]
    [Description("The structure of a DQMH module as seen through AIXML.")]
    public static string DqmhReferenceResource() => Load(DqmhResourceName);

    [McpServerTool(Name = "lvai_lvproj_reference", ReadOnly = true,
                   Title = "LabVIEW project file (.lvproj) reference")]
    [Description("""
        The grammar of a LabVIEW `.lvproj` file: item types, virtual versus auto-populating
        folders, the `URL` forms, property scopes, build specifications, formatting rules and
        recipes for generating one. Derived by census over a corpus of real project files, with
        counts throughout so a rule can be told apart from a single observation.
        Read this before parsing or writing a `.lvproj`. Two traps it documents: a project's
        own library members are NOT listed in the file (the `.lvlib` owns them), and no RPC adds
        a file to a project - `.lvproj` generation is on NI's unsupported list, so any project
        edit is plain XML work outside the AIXML path.
        Without arguments: a section list. With section: that section, or 'all'.
        """)]
    public static string LvprojReference(
        [Description("Section number or title fragment; 'all' for everything; omit for the section list")]
        string? section = null) => Serve(LvprojResourceName, section);

    [McpServerResource(Name = "lvproj-structure", UriTemplate = "labview://lvproj-structure",
                      MimeType = "text/markdown",
                      Title = "LabVIEW project file reference")]
    [Description("How a LabVIEW .lvproj file is structured, and how to generate one.")]
    public static string LvprojReferenceResource() => Load(LvprojResourceName);

    [McpServerTool(Name = "lvai_lvlib_reference", ReadOnly = true,
                   Title = "LabVIEW library and class file (.lvlib/.lvclass) reference")]
    [Description("""
        The grammar of a `.lvlib` and a `.lvclass`: the item types, where ACCESS SCOPE is
        recorded and how it is inherited, and how a class names its parent. Read this before
        deciding what is public - no RPC reports access scope, so the library file is the only
        source, and the two scope properties behave differently: on a `.lvlib` the scope sits on
        the FOLDER and its members inherit it, while a `.lvclass` records it per member.
        Also covers the encoded parent-class record, which is where inheritance comes from on
        LabVIEW versions that do not write plain-text Parent items.
        Without arguments: a section list. With section: that section, or 'all'.
        """)]
    public static string LvlibReference(
        [Description("Section number or title fragment; 'all' for everything; omit for the section list")]
        string? section = null) => Serve(LvlibResourceName, section);

    [McpServerResource(Name = "lvlib-lvclass-structure",
                      UriTemplate = "labview://lvlib-lvclass-structure",
                      MimeType = "text/markdown",
                      Title = "LabVIEW library and class file reference")]
    [Description("How .lvlib and .lvclass files record membership, access scope and inheritance.")]
    public static string LvlibReferenceResource() => Load(LvlibResourceName);

    [McpServerTool(Name = "lvai_vi_server_reference", ReadOnly = true,
                   Title = "VI Server methods and properties catalogue")]
    [Description("""
        Look up the exact Invoke Node / Property Node vocabulary for generating a VI that calls
        VI Server: 3078 methods and 6410 properties over 153 classes, with their terminal names.
        Use this whenever you author AIXML containing an Invoke Node or Property Node.
        A method's `target` string CANNOT be derived any other way - method names are binary IDs
        inside a .vi, LabVIEW.exe does not carry them as text, and SearchInfoCache covers palette
        items rather than VI Server. Guessing produces XML that validates and then does nothing.
        Without arguments: how to use it plus the class list. With query and/or cls: matching
        rows only. Combine with lvai_run_vi_as_top_level to reach capabilities no RPC exposes -
        that is how a VI's icon and connector pane are obtained.
        """)]
    public static string ViServerReference(
        [Description("Substring of the method or property name, e.g. 'To HTML', 'Callees'")]
        string? query = null,
        [Description("Class filter, with or without braces: 'LV.VI', '{LV.Application}'")]
        string? cls = null,
        [Description("'methods', 'properties' or 'both' (default)")] string? kind = null,
        [Description("Max rows to return (default 40, max 400)")] int limit = DefaultLimit,
        [Description("Section of the guide document, or 'all'; ignored when query/cls are given")]
        string? section = null)
    {
        var wantMethods = !string.Equals(kind, "properties", StringComparison.OrdinalIgnoreCase);
        var wantProperties = !string.Equals(kind, "methods", StringComparison.OrdinalIgnoreCase);

        // No filter at all: hand back guidance, not 400 kB of data.
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(cls))
            return Serve(ViServerResourceName, section) + Environment.NewLine + Environment.NewLine +
                   ClassOverview();

        string methods, properties;
        try
        {
            methods = Load(MethodsResourceName);
            properties = Load(PropertiesResourceName);
        }
        catch (Exception e)
        {
            return Json.Error(e.GetType().Name,
                $"The embedded VI Server catalogue could not be read: {e.Message}");
        }

        limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var sb = new StringBuilder();
        var total = 0;

        if (wantMethods)
        {
            var (rows, count) = Match(methods, query, cls, limit);
            total += count;
            sb.AppendLine($"METHODS  (class → target → parameters → returns; "
                          + "'reference' and 'error in (no error)' are always available)");
            sb.AppendLine(rows.Count == 0 ? "  (no match)" : string.Join(Environment.NewLine, rows));
            if (count > rows.Count)
                sb.AppendLine($"  ... {count - rows.Count} more method rows match; narrow the query or raise limit");
            sb.AppendLine();
        }

        if (wantProperties)
        {
            var (rows, count) = Match(properties, query, cls, limit);
            total += count;
            sb.AppendLine("PROPERTIES  (class → property → access as configured in the source VI, "
                          + "NOT a statement about writability)");
            sb.AppendLine(rows.Count == 0 ? "  (no match)" : string.Join(Environment.NewLine, rows));
            if (count > rows.Count)
                sb.AppendLine($"  ... {count - rows.Count} more property rows match; narrow the query or raise limit");
        }

        if (total == 0)
            return $"Nothing matched query=\"{query}\" cls=\"{cls}\"." + Environment.NewLine +
                   "Method names carry a category prefix in two interchangeable spellings " +
                   "('Print VI To HTML' and 'Print.VI To Printer' both occur and both import), " +
                   "so try a shorter fragment." + Environment.NewLine + Environment.NewLine +
                   ClassOverview();

        return sb.ToString().TrimEnd();
    }

    [McpServerResource(Name = "vi-server-reference", UriTemplate = "labview://vi-server-reference",
                      MimeType = "text/markdown",
                      Title = "VI Server catalogue guide")]
    [Description("How to reach VI Server from a generated VI, and what the two catalogues contain.")]
    public static string ViServerReferenceResource() => Load(ViServerResourceName);

    // ---------- internals ----------

    /// <summary>
    /// Filter a catalogue TSV. Returns the capped rows plus how many matched in total, so the
    /// caller can say what it dropped instead of pretending the list was complete.
    /// </summary>
    private static (List<string> Rows, int Total) Match(string tsv, string? query, string? cls,
                                                       int limit)
    {
        var wantClass = NormalizeClass(cls);
        var result = Collect(tsv, query, wantClass, exact: true, limit);

        // A bare 'LV.VI' must mean the VI class, not every class whose name merely contains
        // that text. The catalogue is sorted by class and '}' sorts after every letter, so
        // {LV.VIRefnum} precedes {LV.VI}: a loose match plus the row cap would fill up with
        // near-misses and bury the exact class that was asked for. Substring is the fallback.
        if (result.Total == 0 && wantClass is not null)
            result = Collect(tsv, query, wantClass, exact: false, limit);

        return result;
    }

    private static (List<string> Rows, int Total) Collect(string tsv, string? query,
                                                         string? wantClass, bool exact, int limit)
    {
        var rows = new List<string>();
        var total = 0;
        var first = true;

        foreach (var line in tsv.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            if (first) { first = false; continue; }          // header

            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var rowClass = line[..tab];

            if (wantClass is not null &&
                !(exact
                    ? rowClass.Equals(wantClass, StringComparison.OrdinalIgnoreCase)
                    : rowClass.Contains(wantClass.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)))
                continue;

            // The name is the second column; matching the whole line would hit parameter
            // names and return rows whose method has nothing to do with the query.
            var rest = line[(tab + 1)..];
            var tab2 = rest.IndexOf('\t');
            var name = tab2 < 0 ? rest : rest[..tab2];

            if (!string.IsNullOrWhiteSpace(query) &&
                !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            total++;
            if (rows.Count < limit) rows.Add("  " + line);
        }
        return (rows, total);
    }

    /// <summary>Accept 'LV.VI', '{LV.VI}' and 'lv.vi' alike; null when no filter was given.</summary>
    private static string? NormalizeClass(string? cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return null;
        var t = cls.Trim();
        if (!t.StartsWith('{')) t = "{" + t;
        if (!t.EndsWith('}')) t += "}";
        return t;
    }

    /// <summary>The classes that carry the most entries — enough to aim a second query.</summary>
    private static string ClassOverview()
    {
        try
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var resource in new[] { MethodsResourceName, PropertiesResourceName })
                foreach (var line in Load(resource).Replace("\r\n", "\n").Split('\n'))
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || line.StartsWith("class\t", StringComparison.Ordinal)) continue;
                    var key = line[..tab];
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }

            var sb = new StringBuilder($"Classes: {counts.Count}. The largest, by entry count:");
            sb.AppendLine();
            foreach (var (name, n) in counts.OrderByDescending(p => p.Value).Take(15))
                sb.AppendLine($"  {name}  ({n})");
            sb.Append("Pass any of these as cls, e.g. cls='LV.VI'.");
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"(class overview unavailable: {e.Message})";
        }
    }

    /// <summary>Shared section-serving used by every document tool.</summary>
    private static string Serve(string resourceName, string? section)
    {
        string document;
        try
        {
            document = Load(resourceName);
        }
        catch (Exception e)
        {
            return Json.Error(e.GetType().Name,
                $"The embedded document '{resourceName}' could not be read: {e.Message}");
        }

        var sections = Split(document);

        if (string.IsNullOrWhiteSpace(section)) return Toc(sections);
        if (section.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return document;

        return Find(sections, section.Trim())
               ?? $"No section matched \"{section}\"." + Environment.NewLine +
                  Environment.NewLine + Toc(sections);
    }

    internal static string Load(string resourceName = ResourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Split on level-2 headings, keeping each heading with its body.</summary>
    internal static List<(string Heading, string Body)> Split(string document)
    {
        var result = new List<(string, string)>();
        var lines = document.Replace("\r\n", "\n").Split('\n');
        var heading = "(preamble)";
        var body = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (body.Length > 0) result.Add((heading, body.ToString().TrimEnd()));
                heading = line[3..].Trim();
                body.Clear();
                body.AppendLine(line);
            }
            else
            {
                body.AppendLine(line);
            }
        }
        if (body.Length > 0) result.Add((heading, body.ToString().TrimEnd()));
        return result;
    }

    /// <summary>
    /// Resolve a section by number, title fragment, or one of a few convenience keywords
    /// that describe what someone is actually looking for rather than the heading wording.
    /// </summary>
    internal static string? Find(List<(string Heading, string Body)> sections, string query)
    {
        foreach (var (heading, body) in sections)
            if (heading.StartsWith(query + ".", StringComparison.OrdinalIgnoreCase) ||
                heading.Equals(query, StringComparison.OrdinalIgnoreCase))
                return body;

        var keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wiring"] = "core model",
            ["nets"] = "core model",
            ["net"] = "core model",
            ["types"] = "type grammar",
            ["type"] = "type grammar",
            ["escaping"] = "escaping",
            ["escapes"] = "escaping",
            ["structures"] = "structures",
            ["loop"] = "structures",
            ["case"] = "structures",
            ["event"] = "structures",
            ["terminals"] = "multi-terminal",
            ["nodes"] = "multi-terminal",
            ["elements"] = "elements",
            ["skeleton"] = "document skeleton",
            ["limits"] = "can and cannot",
            ["errors"] = "known failure",
            ["failures"] = "known failure",
            ["workflow"] = "workflow",
        };
        var needle = keywords.TryGetValue(query, out var mapped) ? mapped : query;

        foreach (var (heading, body) in sections)
            if (heading.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return body;

        return null;
    }

    /// <summary>
    /// A `###` subsection by title. <see cref="Split"/> only cuts on `##`, so a subsection was
    /// invisible to `section=` even though the tool description promises "part of a title" -
    /// measured: `section='Polymorphic subVI calls'` answered "No section matched" for a
    /// subsection of exactly that name.
    /// </summary>
    internal static string? FindSubsection(string document, string query)
    {
        var lines = document.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("### ", StringComparison.Ordinal)) continue;
            if (!lines[i][4..].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            var end = i + 1;
            while (end < lines.Length &&
                   !lines[end].StartsWith("## ", StringComparison.Ordinal) &&
                   !lines[end].StartsWith("### ", StringComparison.Ordinal)) end++;
            return string.Join(Environment.NewLine, lines[i..end]).TrimEnd();
        }
        return null;
    }

    /// <summary>
    /// Every passage mentioning <paramref name="needle"/>, each carrying enough around it to be
    /// usable on its own. What "enough" means differs by what was hit, and getting this wrong
    /// makes the result worthless rather than merely terse:
    ///
    /// - a TABLE ROW comes back with its header row, because `| `Build Waveform` | `waveform`,
    ///   `t0` | ... |` says nothing about which column is which;
    /// - a line inside a FENCED BLOCK returns the whole block, since half an XML snippet cannot
    ///   be copied;
    /// - everything else returns the line.
    ///
    /// Each passage is labelled with the heading it sits under, so the caller can follow up with
    /// section= if they want the surroundings.
    /// </summary>
    internal static string Lookup(string document, string needle, int limit)
    {
        var lines = document.Replace("\r\n", "\n").Split('\n');

        // Pass 1: fence extents, so a hit inside one can return the block whole.
        var fenceStart = new int[lines.Length];
        var fenceEnd = new int[lines.Length];
        Array.Fill(fenceStart, -1);
        var open = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            if (open < 0) { open = i; continue; }
            for (var k = open; k <= i; k++) { fenceStart[k] = open; fenceEnd[k] = i; }
            open = -1;
        }

        // Pass 2: the heading each line sits under, and the header row of the table it is in.
        var heading = new string[lines.Length];
        var header = new int[lines.Length];
        Array.Fill(header, -1);
        var section = "";
        var current = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (fenceStart[i] < 0)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal)) { section = line[3..].Trim(); current = -1; }
                else if (line.StartsWith("### ", StringComparison.Ordinal)) { section = line[4..].Trim(); current = -1; }
                // A header row is a table line whose successor is the |---|---| rule.
                else if (line.StartsWith('|') && i + 1 < lines.Length &&
                         lines[i + 1].StartsWith('|') && lines[i + 1].Contains("---")) current = i;
                else if (!line.StartsWith('|')) current = -1;
            }
            heading[i] = section;
            header[i] = current;
        }

        var passages = new List<string>();
        var headings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;

            string body;
            if (fenceStart[i] >= 0)
                body = string.Join(Environment.NewLine,
                    lines[fenceStart[i]..(fenceEnd[i] + 1)]);
            else if (lines[i].StartsWith('|') && header[i] >= 0 && header[i] != i)
                body = string.Join(Environment.NewLine,
                    [lines[header[i]], lines[header[i] + 1], lines[i]]);
            else
                body = lines[i];

            var passage = (heading[i].Length > 0 ? $"[{heading[i]}]" + Environment.NewLine : "") + body;
            if (!seen.Add(passage)) continue;          // a fence hit on several lines is one block
            total++;
            passages.Add(passage);
            if (heading[i].Length > 0 && !headings.Contains(heading[i])) headings.Add(heading[i]);
        }

        if (total == 0)
            return $"Nothing in the AIXML reference mentions \"{needle}\"." + Environment.NewLine +
                   "Terminal names are literal LabVIEW labels, so try a shorter fragment - " +
                   "'Waveform' rather than 'Build Waveform (t0)'. If the node genuinely is not " +
                   "documented, export a VI that already uses it (section 8 says which are not " +
                   "covered) and copy the terminal strings from there." + Environment.NewLine +
                   Environment.NewLine + Toc(Split(document));

        // A term that is everywhere - 'error in (no error)' hit 100 passages - is not answered by
        // dumping the first 40 of them. Measured: that filled a caller's context and it still had
        // to fetch a section afterwards. Show a few, and name the headings so the next call can
        // be aimed.
        var flooded = total > FloodThreshold;
        var shown = Math.Min(passages.Count, flooded ? FloodSample : limit);

        var sb = new StringBuilder();
        sb.AppendLine($"{total} passage(s) in the AIXML reference mention \"{needle}\":");
        if (flooded)
        {
            sb.AppendLine();
            sb.AppendLine($"That term is everywhere - showing {shown}. It appears under these " +
                          "headings; ask for one of them with section= instead:");
            foreach (var h in headings.Take(12)) sb.AppendLine($"  {h}");
            if (headings.Count > 12) sb.AppendLine($"  ... and {headings.Count - 12} more");
        }

        foreach (var passage in passages.Take(shown))
        {
            sb.AppendLine();
            sb.AppendLine(passage);
        }
        if (total > shown)
            sb.AppendLine($"{Environment.NewLine}  ... {total - shown} more; " +
                          "narrow the term, name a section, or raise limit");
        return sb.ToString().TrimEnd();
    }

    private static string Toc(List<(string Heading, string Body)> sections)
    {
        var sb = new StringBuilder("Sections (pass one as `section`):");
        sb.AppendLine();
        foreach (var (heading, _) in sections)
            if (heading != "(preamble)")
                sb.AppendLine($"  {heading}");
        return sb.ToString().TrimEnd();
    }
}
