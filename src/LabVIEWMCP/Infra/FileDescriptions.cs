using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// lvai_describe_project and lvai_describe_vi WITHOUT NI's service (2026-10-13): the same answer
/// shape as NI's GetDescribe*PromptInfo streams - one message whose `infoJson` carries NI's keys -
/// built from the FILES. A project from its .lvproj and the .lvlib files it lists, a VI from our
/// reader's AIXML (vi2aixml.py).
///
/// What a file cannot say is left empty and NAMED in `warnings`, never guessed: a target's IP
/// address, a VI's panel image. The keys were taken from NI's answers for the same project and VI,
/// read the same day.
/// </summary>
internal static class FileDescriptions
{
    public const string StopReason = "read from the files - pylabview backend, NI's service not used";

    /// <summary>The stream envelope NI's describe tools answer in, around one infoJson.</summary>
    public static string Envelope(JsonObject info, int limit, int maxContentChars = 0)
    {
        var text = info.ToJsonString();
        var message = new JsonObject { ["infoJson"] = text };
        if (maxContentChars > 0)
        {
            var truncated = text.Length > maxContentChars;
            message["infoJson"] = truncated ? text[..maxContentChars] : text;
            message["infoJsonTruncated"] = truncated;
        }
        return Json.Document(new JsonObject
        {
            ["messageCount"] = 1,
            ["limit"] = limit,
            ["stopReason"] = StopReason,
            ["messages"] = new JsonArray(message),
        });
    }

    // ------------------------------------------------------------------ project

    /// <summary>A .lvproj in NI's describe-project shape.</summary>
    public static JsonObject DescribeProject(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        var info = new JsonObject
        {
            ["projectName"] = Path.GetFileName(full),
            ["projectPath"] = full,
            ["description"] = "",
            ["symbols"] = new JsonArray(),
            ["targets"] = new JsonArray(),
            ["errorCode"] = 0,
            ["errorMessage"] = "",
        };
        var warnings = new JsonArray();
        XElement root;
        try { root = XDocument.Load(full).Root ?? throw new System.Xml.XmlException("empty document"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            info["errorCode"] = 5000;
            info["errorMessage"] = $"The project file could not be read: {e.Message}";
            return info;
        }

        info["description"] = Property(root, "NI.Project.Description") ?? "";
        var folder = full;   // a URL is relative to the FILE, treated as a directory
        var targets = (JsonArray)info["targets"]!;
        foreach (var target in root.Elements("Item"))
        {
            var type = (string?)target.Attribute("Type") ?? "";
            var t = new JsonObject
            {
                ["targetName"] = (string?)target.Attribute("Name") ?? "",
                ["targetType"] = type,
                // "My Computer" is the machine this server runs on; every other target's OS is
                // a property of hardware the file does not describe
                ["operatingSystem"] = type == "My Computer" && OperatingSystem.IsWindows() ? "Windows NT" : "",
                ["targetAddress"] = "",
                ["symbols"] = new JsonArray(),
                ["owner"] = "",
                ["targetClass"] = "",
                ["mode"] = "",
                ["buildSpecifications"] = new JsonArray(),
                ["libraries"] = new JsonArray(),
                ["classes"] = new JsonArray(),
                ["vis"] = new JsonArray(),
                ["otherFiles"] = new JsonArray(),
                ["missingFiles"] = new JsonArray(),
                ["resourceName"] = "",
                ["topLevelTimingSource"] = "",
                ["ioItems"] = new JsonArray(),
                ["clocks"] = new JsonArray(),
                ["daqmxTasks"] = new JsonArray(),
                ["daqmxGlobalChannels"] = new JsonArray(),
                ["daqmxScales"] = new JsonArray(),
            };
            WalkTarget(target, folder, t);
            targets.Add(t);
        }
        warnings.Add("targetAddress is not in the .lvproj; NI's answer reads it from the running target.");
        info["warnings"] = warnings;
        return info;
    }

    private static void WalkTarget(XElement element, string projectFolder, JsonObject target)
    {
        foreach (var item in element.Elements("Item"))
        {
            var type = (string?)item.Attribute("Type") ?? "";
            var name = (string?)item.Attribute("Name") ?? "";
            if (type == "Dependencies") continue;
            if (type == "Build")
            {
                foreach (var spec in item.Elements("Item"))
                    ((JsonArray)target["buildSpecifications"]!).Add(new JsonObject
                    {
                        ["name"] = (string?)spec.Attribute("Name") ?? "",
                        ["type"] = (string?)spec.Attribute("Type") ?? "",
                    });
                continue;
            }
            if (type == "Folder") { WalkTarget(item, projectFolder, target); continue; }

            var path = Resolve(projectFolder, (string?)item.Attribute("URL"));
            if (path is not null && !File.Exists(path) && !Directory.Exists(path))
            {
                ((JsonArray)target["missingFiles"]!).Add(new JsonObject { ["name"] = name, ["path"] = path });
                continue;
            }
            switch (type)
            {
                case "Library":
                    ((JsonArray)target["libraries"]!).Add(DescribeLibrary(path ?? "", name));
                    break;
                case "LVClass":
                    ((JsonArray)target["classes"]!).Add(new JsonObject
                    {
                        ["name"] = name, ["path"] = path ?? "", ["type"] = "Class",
                    });
                    break;
                case "VI":
                    ((JsonArray)target["vis"]!).Add(new JsonObject
                    {
                        ["name"] = name, ["path"] = path ?? "", ["type"] = ItemType(type, name),
                    });
                    break;
                default:
                    ((JsonArray)target["otherFiles"]!).Add(new JsonObject
                    {
                        ["name"] = name, ["path"] = path ?? "", ["type"] = type,
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// A .lvlib as NI lists it: every item flattened, with its effective SCOPE - an item's own
    /// NI.LibItem.Scope, else its folder's, else public at the root (docs/lvlib-lvclass-structure.md).
    /// </summary>
    internal static JsonObject DescribeLibrary(string libraryPath, string name)
    {
        var library = new JsonObject
        {
            ["libraryName"] = name,
            ["libraryPath"] = libraryPath,
            ["description"] = "",
            ["items"] = new JsonArray(),
            ["missingItems"] = new JsonArray(),
            ["parent"] = "",
            ["friends"] = new JsonArray(),
            ["variables"] = new JsonArray(),
        };
        XElement root;
        try { root = XDocument.Load(libraryPath).Root!; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or System.Xml.XmlException or ArgumentException)
        {
            library["readError"] = e.Message;
            return library;
        }
        library["description"] = HtmlText(Property(root, "NI.Lib.Description"));
        var folder = libraryPath;   // relative to the FILE, treated as a directory

        void Walk(XElement element, string inherited)
        {
            foreach (var item in element.Elements("Item"))
            {
                var type = (string?)item.Attribute("Type") ?? "";
                var itemName = (string?)item.Attribute("Name") ?? "";
                var scope = ScopeName(Property(item, "NI.LibItem.Scope")) ?? inherited;
                var path = Resolve(folder, (string?)item.Attribute("URL"));
                if (type == "Variable")
                {
                    ((JsonArray)library["variables"]!).Add(new JsonObject { ["name"] = itemName, ["scope"] = scope });
                    continue;
                }
                if (path is not null && !File.Exists(path) && !Directory.Exists(path))
                    ((JsonArray)library["missingItems"]!).Add(new JsonObject { ["name"] = itemName, ["path"] = path });
                ((JsonArray)library["items"]!).Add(new JsonObject
                {
                    ["name"] = itemName,
                    ["path"] = path ?? "",
                    ["type"] = ItemType(type, itemName),
                    ["scope"] = scope,
                });
                if (type == "Folder") Walk(item, scope);
            }
        }

        Walk(root, "public");
        return library;
    }

    /// <summary>NI's item type words: a folder is a Directory, a VI an Instrument, a .ctl a Control.</summary>
    internal static string ItemType(string type, string name) => type switch
    {
        "Folder" => "Directory",
        "VI" when name.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase) => "Control",
        "VI" => "Instrument",
        "LVClass" => "Class",
        _ => type,
    };

    internal static string? ScopeName(string? value) => value switch
    {
        "1" => "public",
        "2" => "private",
        "3" => "community",
        "4" => "scope 4",
        _ => null,
    };

    /// <summary>A URL as LabVIEW writes it - relative to the FILE, as if it were a directory.</summary>
    private static string? Resolve(string baseFile, string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Contains('<')) return null;   // a symbolic URL
        try
        {
            return url.Length > 2 && url[1] == ':' ? Path.GetFullPath(url.Replace('/', '\\'))
                : Path.GetFullPath(Path.Combine(baseFile, url.Replace('/', '\\')));
        }
        catch (ArgumentException) { return null; }
    }

    private static string? Property(XElement element, string name) =>
        element.Elements("Property").FirstOrDefault(p => (string?)p.Attribute("Name") == name)?.Value;

    private static string HtmlText(string? html) =>
        string.IsNullOrEmpty(html) ? "" : System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Trim();

    // ------------------------------------------------------------------ VI

    /// <summary>A VI in NI's describe-VI shape, from the reader's AIXML of it.</summary>
    public static JsonObject DescribeVi(string viPath, string? aixml, int errorCode, string errorMessage)
    {
        var info = new JsonObject
        {
            ["viName"] = "",
            ["viPath"] = Path.GetFullPath(viPath),
            ["viXml"] = aixml ?? "",
            ["viImage"] = "",
            ["controlsIndicators"] = "",
            ["subvisInfo"] = "",
            ["owningProjectPath"] = "",
            ["owningProjectName"] = "",
            ["errorCode"] = errorCode,
            ["errorMessage"] = errorMessage,
            ["warnings"] = new JsonArray(),
        };
        if (aixml is null) return info;

        XElement root;
        try { root = XDocument.Parse(aixml).Root!; }
        catch (System.Xml.XmlException e)
        {
            info["errorCode"] = 1;
            info["errorMessage"] = $"The reader's AIXML could not be parsed: {e.Message}";
            return info;
        }
        info["viName"] = (string?)root.Attribute("_name") ?? Path.GetFileName(viPath);
        info["controlsIndicators"] = ControlsIndicators(root);
        info["subvisInfo"] = string.Join("\n", root.Descendants("Call")
            .Select(c => (string?)c.Attribute("target") ?? (string?)c.Attribute("_name"))
            .OfType<string>().Distinct(StringComparer.Ordinal));
        if (OwningProject(Path.GetFullPath(viPath)) is { } project)
        {
            info["owningProjectPath"] = project;
            info["owningProjectName"] = Path.GetFileName(project);
        }
        ((JsonArray)info["warnings"]!).Add(
            "viImage is empty: the pylabview backend does not render the panel here (lvai_render_diagrams does).");
        return info;
    }

    /// <summary>NI's text form: `Controls:` then `Indicators:`, one label per line, indented.</summary>
    internal static string ControlsIndicators(XElement root)
    {
        static IEnumerable<string> Names(XElement r, string kind) =>
            r.Elements(kind).Select(e => (string?)e.Attribute("_name")).OfType<string>();
        var text = new StringBuilder("Controls:\n");
        foreach (var name in Names(root, "Control")) text.Append("  ").Append(name).Append('\n');
        text.Append("\nIndicators:\n");
        foreach (var name in Names(root, "Indicator")) text.Append("  ").Append(name).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// The nearest .lvproj, up to three folders above the VI, that lists the VI ITSELF. A VI
    /// listed only through its library is not owned that way, and NI's answer leaves that case
    /// empty too (measured on a DQMH module member).
    /// </summary>
    internal static string? OwningProject(string viPath)
    {
        var folder = Path.GetDirectoryName(viPath);
        for (var depth = 0; depth < 4 && folder is not null; depth++, folder = Path.GetDirectoryName(folder))
        {
            IEnumerable<string> projects;
            try { projects = Directory.EnumerateFiles(folder, "*.lvproj"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var project in projects)
            {
                try
                {
                    var listed = XDocument.Load(project).Descendants("Item")
                        .Select(i => Resolve(project, (string?)i.Attribute("URL")))
                        .Any(p => string.Equals(p, viPath, StringComparison.OrdinalIgnoreCase));
                    if (listed) return project;
                }
                catch (Exception e) when (e is IOException or System.Xml.XmlException) { }
            }
        }
        return null;
    }
}
