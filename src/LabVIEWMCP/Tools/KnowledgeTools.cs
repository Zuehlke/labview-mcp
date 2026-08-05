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

    [McpServerTool(Name = "lvai_aixml_reference", ReadOnly = true,
                   Title = "AIXML format reference")]
    [Description("""
        The rules for reading and WRITING LabVIEW AIXML. Call this before authoring or editing
        any AIXML - the format has no published schema and its wiring model is
        counter-intuitive, so generating without it produces plausible but wrong XML.
        Without arguments: the essential rules plus a section list (cheap, start here).
        With section: that section's full text ('types', 'structures', 'escaping', 'wiring',
        a heading number, or part of a title). With section='all': the whole document.
        """)]
    public static string AixmlReference(
        [Description("Section number, keyword or title fragment; 'all' for everything; omit for the essentials")]
        string? section = null)
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

        if (string.IsNullOrWhiteSpace(section))
            return Essentials + Environment.NewLine + Environment.NewLine + Toc(sections);

        if (section.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return document;

        var match = Find(sections, section.Trim());
        return match ?? "No section matched \"" + section + "\"." + Environment.NewLine +
               Environment.NewLine + Toc(sections);
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

    // ---------- internals ----------

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
