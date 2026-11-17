using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The server's OPTIONAL settings file, <c>%USERPROFILE%\.labviewmcp\settings.json</c>. It holds
/// only <c>labviewExe</c> and <c>labviewPref</c> (<see cref="LabViewContext"/>), both optional; the
/// server never creates or rewrites it.
///
/// It chose the AIXML backend from 2026-10-13 (<c>aixmlBackend</c>, created at start-up with a
/// one-time switch marker <c>setDefaultDone</c>) until the user removed that switch on 2026-11-17:
/// the backend is now pylabview unless a call, <c>--aixml-backend</c> or LABVIEWMCP_AIXML_BACKEND
/// names another (<see cref="AixmlBackend"/>). Those two keys are removed from an old file at start-up
/// (<see cref="RemoveRetiredKeys"/>).
///
/// Beside the cache (<see cref="CacheDirectory.DefaultRoot"/>) and for the same reason: a server
/// started by the Claude desktop app inherits that app's filesystem redirection under
/// %LOCALAPPDATA%, while %USERPROFILE% is the same folder for every host.
/// </summary>
internal static class ServerSettings
{
    /// <summary>Environment variable that points at another settings file - tests, mostly.</summary>
    public const string PathVariable = "LABVIEWMCP_SETTINGS";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".labviewmcp", "settings.json");

    /// <summary>The settings file in force, read on every call like <see cref="CacheDirectory.Root"/>.</summary>
    public static string FilePath
    {
        get
        {
            var over = Environment.GetEnvironmentVariable(PathVariable);
            return string.IsNullOrWhiteSpace(over) ? DefaultPath : over;
        }
    }

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// A string entry of the settings file, or null when the file, the key or a string value is
    /// missing or the file does not parse - for the optional entries (<c>labviewExe</c>,
    /// <c>labviewPref</c>), whose absence is the ordinary case and never an error.
    /// </summary>
    public static string? ReadString(string key, string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Options);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(key, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The two keys of the removed backend switch (2026-10-13 to 2026-11-17).</summary>
    internal static readonly string[] RetiredKeys = ["aixmlBackend", "setDefaultDone"];

    /// <summary>What <see cref="RemoveRetiredKeys"/> did, for the start-up log and the tests.</summary>
    internal enum Cleanup { NoFile, NothingToDo, Rewritten, Deleted, Unreadable, Failed }

    /// <summary>
    /// The start-up clean-up of the removed backend switch (the user's decision of 2026-11-17): a
    /// settings file that still carries <c>aixmlBackend</c> or <c>setDefaultDone</c> loses both.
    /// What else it holds (<c>labviewExe</c>, <c>labviewPref</c>, any other entry) is written back
    /// as plain JSON - the old comments described the removed switch; a file left with no entry is
    /// DELETED. Either way the old file is kept as <c>settings.json.bak</c> first. A file that does
    /// not parse is left alone, and a profile that cannot be written does not stop the server.
    /// Runs on every start and does nothing once the keys are gone.
    /// </summary>
    public static Cleanup RemoveRetiredKeys(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return Cleanup.NoFile;
            var text = File.ReadAllText(path);
            if (System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: Options)
                    is not System.Text.Json.Nodes.JsonObject root)
                return Cleanup.Unreadable;
            if (!RetiredKeys.Any(root.ContainsKey)) return Cleanup.NothingToDo;

            foreach (var key in RetiredKeys) root.Remove(key);
            File.Copy(path, path + ".bak", overwrite: true);
            if (root.Count == 0)
            {
                File.Delete(path);
                return Cleanup.Deleted;
            }
            var json = Json.Document(root);
            File.WriteAllText(path, json.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) +
                                    Environment.NewLine, new System.Text.UTF8Encoding(false));
            return Cleanup.Rewritten;
        }
        catch (JsonException)
        {
            return Cleanup.Unreadable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Cleanup.Failed;
        }
    }
}
