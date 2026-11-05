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
/// The file is CREATED at start-up when it is missing and otherwise rewritten exactly once: the
/// switch to the pylabview default (2026-11-05, <see cref="MigrateDefaultOnce"/>), with a backup.
/// Beyond that it is the user's file, comments included. It is READ ON EVERY CALL (the parse cached by content),
/// so an edit takes effect without restarting the server.
/// </summary>
internal static class ServerSettings
{
    /// <summary>Environment variable that points at another settings file - tests, mostly.</summary>
    public const string PathVariable = "LABVIEWMCP_SETTINGS";

    /// <summary>The key that chooses the AIXML backend.</summary>
    public const string BackendKey = "aixmlBackend";

    /// <summary>The value that leaves the choice to the server: its default, pylabview.</summary>
    public const string Auto = "auto";

    /// <summary>
    /// The marker that the one-time switch to the pylabview default has run on this file - the user's
    /// decision of 2026-11-05. A file without it set to true is switched once at start-up
    /// (<see cref="MigrateDefaultOnce"/>); a new file carries it from the start.
    /// </summary>
    public const string SetDefaultDoneKey = "setDefaultDone";

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
    /// that the file itself shows which values exist and what each one does. A new file names the
    /// default, pylabview, and carries the setDefaultDone marker, so no switch-over ever touches it.
    /// </summary>
    public const string Template = """
        {
          // LabVIEW MCP server settings. Created by the server on its first start; after that the
          // server changes it at most once (setDefaultDone, below), so your edits and these comments
          // stay. Read on every tool call - an edit takes effect without restarting anything.

          // aixmlBackend - which route every tool that reaches LabVIEW takes when a call does not
          // name one itself: the AIXML tools (generate, convert, validate), running and rendering
          // VIs, and the class, test, DQMH, Actor Framework, LMock, typedef and graft tools. NI's
          // own assistant RPCs (lvai_discuss_file, lvai_monitor_*, the search-candidate filters)
          // always use NI's service.
          //
          //   "pylabview"  The DEFAULT. Our own composer and reader (scripts\aixml-pylabview). Needs NO
          //                NI service: VIs are written without LabVIEW; what needs a running LabVIEW
          //                (is it executable, run it, render it) goes over LabVIEW's ActiveX server.
          //   "grpc"       NI's AI service inside LabVIEW (Nigel). Needs LabVIEW 2026 or later with
          //                the AI add-on running. NI's own converter: the reference behaviour.
          //   "auto"       The server's default - today "pylabview".
          //
          // Still stronger than this file, in this order: the `backend` argument of a call, the
          // server's --aixml-backend option, the LABVIEWMCP_AIXML_BACKEND environment variable.
          // Every tool answer says which backend ran (`backend`) and why (`backendSource`).
          // docs\aixml-backends.md has what each backend covers.
          "aixmlBackend": "pylabview",

          // setDefaultDone - set by the server. true: the one-time switch of "aixmlBackend" to
          // "pylabview" (2026-11-05) has been made, and the server will not touch the entry again.
          // A file without it (or with false) gets that switch at the next server start, with a
          // copy of the old file kept as settings.json.bak.
          "setDefaultDone": true

          // labviewExe, labviewPref - OPTIONAL. Which LabVIEW the server works for, and the -pref
          // INI it is started with. Left out, the server takes the running LabVIEW (with whatever
          // -pref it was started with), else the one it last saw running, else the newest
          // installed. Set them when several LabVIEWs run side by side, or to start one with a
          // project's own INI (an lvpm venv, say). The INI's LVAddons.* keys decide where add-ons
          // are found; a key the INI leaves out means LabVIEW's default.
          //   "labviewExe": "C:\\Program Files\\National Instruments\\LabVIEW 2024\\LabVIEW.exe",
          //   "labviewPref": "C:\\git\\MyProject\\.lv-venv\\.lvpm\\labview.ini"
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

    /// <summary>What <see cref="MigrateDefaultOnce"/> did, for the start-up log and the tests.</summary>
    internal enum Migration { NoFile, AlreadyDone, Migrated, Unreadable, Failed }

    /// <summary>
    /// The one-time switch to the pylabview default (the user's decision of 2026-11-05): an EXISTING
    /// settings file whose <c>setDefaultDone</c> is not true gets <c>"aixmlBackend": "pylabview"</c>
    /// and <c>"setDefaultDone": true</c>, whatever the entry said before. The rest of the text -
    /// comments, labviewExe, labviewPref - is kept; the old file is copied to <c>settings.json.bak</c>
    /// first. A file that does not parse is left alone (the resolver refuses it by name anyway), and
    /// a profile that cannot be written does not stop the server.
    /// </summary>
    public static Migration MigrateDefaultOnce(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return Migration.NoFile;
            var text = File.ReadAllText(path);
            using (var doc = JsonDocument.Parse(text, Options))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return Migration.Unreadable;
                if (doc.RootElement.TryGetProperty(SetDefaultDoneKey, out var done) &&
                    done.ValueKind == JsonValueKind.True)
                    return Migration.AlreadyDone;
            }

            var migrated = MigrateText(text);
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path, migrated, new System.Text.UTF8Encoding(false));
            return Migration.Migrated;
        }
        catch (JsonException)
        {
            return Migration.Unreadable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Migration.Failed;
        }
    }

    private const string OldAutoLine =
        "\"auto\"       \"grpc\", except \"pylabview\" when the LabVIEW in use is older than 2026.";

    private static readonly System.Text.RegularExpressions.Regex BackendEntryPattern =
        new("\"" + BackendKey + "\"\\s*:\\s*(\"[^\"]*\"|true|false|null|-?[0-9.eE+-]+)");

    private static readonly System.Text.RegularExpressions.Regex DoneEntryPattern =
        new("\"" + SetDefaultDoneKey + "\"\\s*:\\s*(\"[^\"]*\"|true|false|null|-?[0-9.eE+-]+)");

    /// <summary>
    /// The migrated TEXT: the backend entry set to pylabview (added after the opening brace when
    /// missing), the marker set to true (added right after the backend entry when missing), and the
    /// old template's "auto" comment line - which promised grpc - reworded. Pure, for the tests.
    /// </summary>
    internal static string MigrateText(string text)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        text = text.Replace(OldAutoLine, "\"auto\"       The server's default - today \"pylabview\".");

        var backend = BackendEntryPattern.Match(text);
        if (backend.Success)
        {
            text = text[..backend.Index] + $"\"{BackendKey}\": \"pylabview\"" + text[(backend.Index + backend.Length)..];
            backend = BackendEntryPattern.Match(text);
        }
        else
        {
            var brace = text.IndexOf('{');
            var insert = $"{nl}  \"{BackendKey}\": \"pylabview\"," ;
            text = text[..(brace + 1)] + insert + text[(brace + 1)..];
            backend = BackendEntryPattern.Match(text);
        }

        var done = DoneEntryPattern.Match(text);
        if (done.Success)
            return text[..done.Index] + $"\"{SetDefaultDoneKey}\": true" + text[(done.Index + done.Length)..];

        // after the backend entry, keeping the JSON valid whether a comma followed it or not
        var end = backend.Index + backend.Length;
        var rest = text[end..];
        var hadComma = rest.TrimStart().StartsWith(',');
        var marker = $"{nl}{nl}  // setDefaultDone - set by the server: the one-time switch of \"{BackendKey}\" to{nl}" +
                     $"  // \"pylabview\" (2026-11-05) is made; the server will not touch the entry again.{nl}" +
                     $"  \"{SetDefaultDoneKey}\": true";
        if (hadComma)
        {
            var comma = end + rest.IndexOf(',');
            return text[..(comma + 1)] + marker + "," + text[(comma + 1)..];
        }
        return text[..end] + "," + marker + text[end..];
    }

    /// <summary>What the file says about the backend: the raw value, or why it could not be read.</summary>
    internal sealed record BackendEntry(string Path, string? Value, string? Error);

    // Keyed on the file's TEXT, not its write time: two writes inside one timestamp tick share a
    // stamp, and the release runner's test (template, then the user's edit, 7 ms apart) read the
    // template's "auto" back for "pylabview". The file is a few hundred bytes - reading it is cheap,
    // the parse is what the cache saves.
    private static (string Path, string Text, BackendEntry Entry)? _cache;
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
        string text;
        try
        {
            if (!File.Exists(path)) return new BackendEntry(path, null, null);
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new BackendEntry(path, null, null);
        }
        lock (Gate)
        {
            if (_cache is { } c && c.Path == path && c.Text == text) return c.Entry;
        }
        var entry = Parse(path, text);
        lock (Gate) _cache = (path, text, entry);
        return entry;
    }

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

    private static BackendEntry Parse(string path, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, Options);
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
