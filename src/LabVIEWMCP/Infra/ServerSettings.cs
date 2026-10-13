using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The server's own settings file, <c>%USERPROFILE%\.labviewmcp\settings.json</c> - the user's
/// decision of 2026-10-13: the AIXML backend is chosen by an entry that is ALWAYS in a file, with
/// every allowed value and what it means written beside it, instead of an environment variable
/// nobody sees until they go looking.
///
/// Beside the cache (<see cref="CacheDirectory.DefaultRoot"/>) and for the same reason: a server
/// started by the Claude desktop app inherits that app's filesystem redirection under
/// %LOCALAPPDATA%, while %USERPROFILE% is the same folder for every host. And not beside the exe,
/// because a plugin update replaces that folder and would take the user's choice with it.
///
/// The file is CREATED at start-up when it is missing and never rewritten afterwards - it is the
/// user's file from then on, comments included. It is READ ON EVERY CALL (cached by write time),
/// so an edit takes effect without restarting the server.
/// </summary>
internal static class ServerSettings
{
    /// <summary>Environment variable that points at another settings file - tests, mostly.</summary>
    public const string PathVariable = "LABVIEWMCP_SETTINGS";

    /// <summary>The key that chooses the AIXML backend.</summary>
    public const string BackendKey = "aixmlBackend";

    /// <summary>The value that leaves the choice to the server: grpc, or pylabview on a LabVIEW older than 2026.</summary>
    public const string Auto = "auto";

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

    /// <summary>
    /// What a new settings file holds. The comments ARE the documentation - the user's requirement
    /// that the file itself shows which values exist and what each one does. `auto` is today's
    /// behaviour, so creating the file changes nothing until someone edits it.
    /// </summary>
    public const string Template = """
        {
          // LabVIEW MCP server settings. Created by the server on its first start; the server never
          // rewrites this file, so your edits and these comments stay. Read on every tool call - an
          // edit takes effect without restarting anything.

          // aixmlBackend - which route every tool that reaches LabVIEW takes when a call does not
          // name one itself: the AIXML tools (generate, convert, validate), running and rendering
          // VIs, and the class, test, DQMH, Actor Framework, LMock, typedef and graft tools. NI's
          // own assistant RPCs (lvai_discuss_file, lvai_monitor_*, the search-candidate filters)
          // always use NI's service.
          //
          //   "grpc"       NI's AI service inside LabVIEW (Nigel). Needs LabVIEW 2026 or later with
          //                the AI add-on running. NI's own converter: the reference behaviour.
          //   "pylabview"  Our own composer and reader (scripts\aixml-pylabview). Needs NO NI service:
          //                VIs are written without LabVIEW; what needs a running LabVIEW (is it
          //                executable, run it, render it) goes over LabVIEW's ActiveX server.
          //   "auto"       "grpc", except "pylabview" when the LabVIEW in use is older than 2026.
          //
          // Still stronger than this file, in this order: the `backend` argument of a call, the
          // server's --aixml-backend option, the LABVIEWMCP_AIXML_BACKEND environment variable.
          // Every tool answer says which backend ran (`backend`) and why (`backendSource`).
          // docs\aixml-backends.md has what each backend covers.
          "aixmlBackend": "auto"
        }
        """;

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Write <see cref="Template"/> to <paramref name="path"/> when no file is there. True when it
    /// was created; false when one existed or it could not be written (a read-only profile must not
    /// stop the server - the backend then resolves as if the file said "auto").
    /// </summary>
    public static bool EnsureExists(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(Template.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What the file says about the backend: the raw value, or why it could not be read.</summary>
    internal sealed record BackendEntry(string Path, string? Value, string? Error);

    private static (string Path, DateTime Stamp, BackendEntry Entry)? _cache;
    private static readonly object Gate = new();

    /// <summary>
    /// The `aixmlBackend` entry of the settings file. A missing file or a missing key is no choice
    /// (Value null, Error null). A file that does not parse, or a value that is not a string, is an
    /// ERROR - refused rather than ignored, as an unknown backend anywhere else is, because falling
    /// back to grpc would answer a question nobody asked.
    /// </summary>
    public static BackendEntry ReadBackend(string? path = null)
    {
        path ??= FilePath;
        DateTime stamp;
        try
        {
            if (!File.Exists(path)) return new BackendEntry(path, null, null);
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new BackendEntry(path, null, null);
        }
        lock (Gate)
        {
            if (_cache is { } c && c.Path == path && c.Stamp == stamp) return c.Entry;
        }
        var entry = Parse(path);
        lock (Gate) _cache = (path, stamp, entry);
        return entry;
    }

    private static BackendEntry Parse(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new BackendEntry(path, null, $"{path} is not a JSON object.");
            if (!doc.RootElement.TryGetProperty(BackendKey, out var value))
                return new BackendEntry(path, null, null);
            if (value.ValueKind != JsonValueKind.String)
                return new BackendEntry(path, null,
                    $"\"{BackendKey}\" in {path} must be a string: \"grpc\", \"pylabview\" or \"auto\".");
            return new BackendEntry(path, value.GetString(), null);
        }
        catch (JsonException e)
        {
            return new BackendEntry(path, null, $"{path} is not valid JSON ({e.Message}).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new BackendEntry(path, null, null);
        }
    }
}
