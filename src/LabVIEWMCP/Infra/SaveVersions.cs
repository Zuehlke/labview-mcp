// =============================================================================
// SaveVersions.cs - which LabVIEW version a file was saved in, which version its
// project pins, and the check every tool answer gets: did this call save a file
// newer than it was, or newer than its project says it must be?
// =============================================================================
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// THE GAP BOTH FIELD PROJECTS RANKED FIRST. A project kept for an older LabVIEW - 21.0 for a
/// station running LabVIEW 2021, 18.0 for one running 2018 - is edited in a newer IDE, and every
/// tool that saves a file through LabVIEW writes the IDE's version: lvai_set_vi_icon wrote 26.0
/// into a 21.0 project and LabVIEW 2021 then refused the VI ("VI version (26.0) is newer", the
/// a field project, 2026-10-30); in another every one of nine saving tools wrote 25.0 into an
/// 18.0 project. Nothing in the server knew a save version, and the target LabVIEW found out only
/// when it loaded the file, on the target PC.
///
/// So every tool answer is checked here, in the one place every call passes (DiagnosingTool):
/// the LabVIEW files named in the ARGUMENTS are read before the call, and every LabVIEW file named
/// in the arguments or the ANSWER that was written during the call is read after it. A file saved
/// newer than it was before, or newer than the version its project pins
/// (<c>NI.LV.All.SaveVersion</c>), is reported under `saveVersionCheck` with a warning. Nothing
/// is changed or refused here - the file is already written; the answer says so while the caller
/// can still restore it. An answer with nothing to report is left exactly as it was.
///
/// The version is read from the FILE, no LabVIEW: a resource file's `LVSR` block starts with it
/// (`26008000` = 26.0, measured on files LabVIEW 2026 and 2020 wrote), an XML file carries it as
/// the root element's `LVVersion`.
/// </summary>
internal static class SaveVersions
{
    /// <summary>Resource files: the version is LVSR's first word.</summary>
    internal static readonly string[] ResourceExtensions = [".vi", ".ctl", ".vim", ".vit", ".ctt"];

    /// <summary>XML files: the version is the root element's LVVersion attribute.</summary>
    internal static readonly string[] XmlExtensions = [".lvclass", ".lvlib", ".lvproj"];

    /// <summary>A call naming more files than this is not checked file by file.</summary>
    internal const int MaxFiles = 300;

    /// <summary>"26.0" for a file LabVIEW 2026 saved; null when the file cannot be read as one.</summary>
    internal static string? Of(string path)
    {
        try
        {
            var ext = Path.GetExtension(path);
            if (XmlExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                var stamp = (string?)XDocument.Load(path).Root?.Attribute("LVVersion");
                return stamp is { Length: >= 4 } && uint.TryParse(stamp, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var word) ? FromWord(word) : null;
            }
            if (!ResourceExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
            var lvsr = RsrcMap.Blocks(File.ReadAllBytes(path), "LVSR");
            return lvsr.Count > 0 && lvsr[0].Length >= 4
                ? FromWord(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lvsr[0]))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                    or System.Xml.XmlException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// "major.minor" from LabVIEW's version word: the major in BCD in the top byte, the minor in the
    /// next nibble (`26008000` = 26.0, `08608000` = 8.6, `20008000` = 20.0).
    /// </summary>
    internal static string? FromWord(uint word)
    {
        var top = (int)(word >> 24);
        var major = (top >> 4) * 10 + (top & 0xF);
        var minor = (int)(word >> 20) & 0xF;
        return (top >> 4) > 9 || (top & 0xF) > 9 || major == 0 ? null : $"{major}.{minor}";
    }

    /// <summary>"26.0" against "21.0": positive when <paramref name="a"/> is the newer.</summary>
    internal static int Compare(string a, string b)
    {
        static (int, int) Parse(string v)
        {
            var parts = v.Split('.');
            return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0);
        }
        return Parse(a).CompareTo(Parse(b));
    }

    /// <summary>
    /// The version a project pins with <c>NI.LV.All.SaveVersion</c> - "21.0" - or null when it saves
    /// in the editor's version ("Editor version") or says nothing.
    /// </summary>
    internal static string? Pinned(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            var value = root?.Elements("Property")
                .FirstOrDefault(p => (string?)p.Attribute("Name") == "NI.LV.All.SaveVersion")?.Value.Trim();
            return value is { Length: > 0 } && char.IsDigit(value[0]) && value.Contains('.') ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pinned save version that applies to a file: a project listing it, or listing the
    /// library or class that owns it (a member is listed by its owner, not by the project).
    /// </summary>
    internal static (string Project, string Version)? PinFor(string filePath)
    {
        foreach (var project in ProjectMembership.ProjectsListing(filePath))
            if (Pinned(project) is { } pin) return (project, pin);

        foreach (var owner in OwnersOf(filePath))
            foreach (var project in ProjectMembership.ProjectsListing(owner))
                if (Pinned(project) is { } pin) return (project, pin);
        return null;
    }

    /// <summary>
    /// For a file no project lists YET - a VI about to be generated: the pin that EVERY project in its
    /// folder and up to <see cref="ProjectMembership.LevelsUp"/> folders above agrees on. One project
    /// that saves in the editor's version, or two that pin different versions, and there is none.
    /// </summary>
    internal static (string Project, string Version)? PinAbove(string filePath)
    {
        var projects = new List<string>();
        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        for (var level = 0; level <= ProjectMembership.LevelsUp && folder is not null; level++)
        {
            try { projects.AddRange(Directory.GetFiles(folder, "*.lvproj")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            folder = Path.GetDirectoryName(folder);
        }
        if (projects.Count == 0) return null;
        var pins = projects.Select(p => (Project: p, Version: Pinned(p))).ToList();
        return pins.All(x => x.Version is not null) && pins.Select(x => x.Version).Distinct().Count() == 1
            ? (pins[0].Project, pins[0].Version!)
            : null;
    }

    /// <summary>The LabVIEW release year of a save version: "21.0" -> 2021, "9.0" -> 2009; null below that.</summary>
    internal static int? Release(string version) =>
        int.TryParse(version.Split('.')[0], System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture, out var major) && major >= 9 && major <= 99
            ? 2000 + major : null;

    private static IEnumerable<string> OwnersOf(string filePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        for (var level = 0; level <= ProjectMembership.LevelsUp && folder is not null; level++)
        {
            string[] owners;
            try
            {
                owners = [.. Directory.GetFiles(folder, "*.lvlib"), .. Directory.GetFiles(folder, "*.lvclass")];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { owners = []; }
            foreach (var owner in owners.Order(StringComparer.OrdinalIgnoreCase))
                if (ProjectMembership.Lists(owner, filePath)) yield return owner;
            folder = Path.GetDirectoryName(folder);
        }
    }

    // ------------------------------------------------------------------ the per-call check

    /// <summary>
    /// The versions of the files a call names, read before it ran, and their write times then
    /// (<paramref name="WriteTimes"/>: a file listed there counts as written by the call only when
    /// its write time CHANGED).
    /// </summary>
    internal sealed record Before(DateTime StartedUtc, IReadOnlyDictionary<string, string?> Versions,
                                  IReadOnlyDictionary<string, DateTime>? WriteTimes = null);

    /// <summary>Read the version of every LabVIEW file the arguments name, before the call.</summary>
    internal static Before Capture(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var started = DateTime.UtcNow;
        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var writeTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in PathsIn(arguments?.Select(a => a.Value) ?? []).Take(MaxFiles))
            if (File.Exists(path))
            {
                versions[path] = Of(path);
                try { writeTimes[path] = File.GetLastWriteTimeUtc(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        // A FOLDER the arguments name: the write times of the LabVIEW files under it (no versions -
        // reading every file would cost more than the check). Measured 2026-11-23: a dryRun of
        // lvai_save_for_previous over a folder, one second after another call saved its files,
        // was told it had saved them - the answer names the files, and with no write time read
        // before the call only the 2 s margin judged them.
        foreach (var folder in FoldersIn(arguments?.Select(a => a.Value) ?? []))
            foreach (var path in FilesUnder(folder, MaxFiles - writeTimes.Count))
            {
                if (writeTimes.ContainsKey(path)) continue;
                try { writeTimes[path] = File.GetLastWriteTimeUtc(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        return new Before(started, versions, writeTimes);
    }

    /// <summary>Every rooted path to an existing FOLDER in these values, line by line.</summary>
    internal static IEnumerable<string> FoldersIn(IEnumerable<JsonElement> values)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            foreach (var line in (value.GetString() ?? "").Split('\n'))
            {
                var t = line.Trim().Trim('"');
                if (t.Length < 3 || t.Length > 400 || !Path.IsPathRooted(t)) continue;
                try { if (Directory.Exists(t)) found.Add(Path.GetFullPath(t)); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return found;
    }

    private static IEnumerable<string> FilesUnder(string folder, int max)
    {
        if (max <= 0) yield break;
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        var count = 0;
        using var walk = all.GetEnumerator();
        while (count < max)
        {
            try { if (!walk.MoveNext()) yield break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
            var ext = Path.GetExtension(walk.Current);
            if (!ResourceExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) &&
                !XmlExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            count++;
            yield return Path.GetFullPath(walk.Current);
        }
    }

    /// <summary>
    /// The `saveVersionCheck` block for a call's answer, or null when no file the call wrote was
    /// saved newer than it was or newer than its project's pinned version.
    /// </summary>
    internal static JsonObject? Check(Before before, JsonNode? answer,
                                      IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var candidates = new HashSet<string>(before.Versions.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var path in PathsIn(arguments?.Select(a => a.Value) ?? [])) candidates.Add(path);
        foreach (var path in PathsIn(answer)) candidates.Add(path);
        if (candidates.Count is 0 or > MaxFiles) return null;

        // A COLLECTED answer (ResumableCall.JobStartedUtcKey): the job wrote its files before this
        // call began, so what this call read "before" was read AFTER the writes - its write times
        // and versions say nothing. Such files are judged by the job's start and the pin alone.
        var jobStart = ResumableCall.JobStartedUtc(answer);
        var collected = jobStart is { } js && js < before.StartedUtc;
        var since = collected ? jobStart!.Value : before.StartedUtc;

        var findings = new JsonArray();
        foreach (var path in candidates.Order(StringComparer.OrdinalIgnoreCase))
        {
            DateTime written;
            try
            {
                if (!File.Exists(path)) continue;
                written = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            // A FILE THE CALL NAMED IS JUDGED BY ITS OWN WRITE TIME BEFORE THE CALL: unchanged means
            // not written by it. The 2 s margin below alone (for FAT and network shares, which keep
            // coarse write times) blamed a call for the PREVIOUS call's save - measured 2026-11-13,
            // lvai_add_vis_to_project and a lvai_running_vi signal, run within 2 s of
            // lvai_set_vi_icon, each answered "saved by this call" for the VI the icon call had
            // saved. The margin now applies only to files the call did not name before it ran.
            if (!collected && before.WriteTimes is { } times && times.TryGetValue(path, out var then))
            {
                if (written == then) continue;
            }
            else if (written < since.AddSeconds(-2)) continue;
            if (Of(path) is not { } now) continue;

            // A FILE THE CALL SAVED WITHOUT NAMING IT - a typedef's callers - has no version read
            // before the call; the tool that saved it hands that version over in the answer
            // (VersionsBeforeKey). Measured 2026-11-23: lvai_set_representation re-saved a 20.0
            // caller of a typedef as 26.0 and saveVersionCheck named only the .ctl.
            var was = (collected ? null : before.Versions.GetValueOrDefault(path)) ?? HandedOver(answer, path);
            // NOT LISTED YET IS NOT "NO PIN": the composer wrote a new VI at the pin of the project
            // in the folders above it (PinAbove), so the check judges it by the same rule. Measured
            // 2026-11-13: a VI generated into a 21.0 project's folder and given its icon BEFORE it was
            // listed was reported `was 21.0, savedAs 26.0` with no projectSaveVersion at all.
            var listed = PinFor(path);
            var pin = listed ?? PinAbove(path);
            var upgraded = was is not null && Compare(now, was) > 0;
            var pastPin = pin is { } p && Compare(now, p.Version) > 0;
            if (!upgraded && !pastPin) continue;

            var finding = new JsonObject { ["path"] = path, ["savedAs"] = now };
            if (was is not null) finding["was"] = was;
            if (pin is { } pinned)
            {
                finding["projectSaveVersion"] = pinned.Version;
                finding["project"] = pinned.Project;
                // "listed": the project lists the file (or its library/class); "folder": no project
                // lists it yet, and every project in its folder and up to three above pins this.
                finding["pinFrom"] = listed is null ? "folder" : "listed";
            }
            findings.Add(finding);
        }
        if (findings.Count == 0) return null;

        var newest = findings.Select(f => f!["savedAs"]!.GetValue<string>())
            .Aggregate((a, b) => Compare(a, b) >= 0 ? a : b);
        return new JsonObject
        {
            ["files"] = findings,
            ["warning"] = $"{findings.Count} file(s) were saved by this call in a NEWER LabVIEW version than " +
                          "they had, or than their project pins (NI.LV.All.SaveVersion) - see `files`. A LabVIEW " +
                          $"older than {newest} cannot open them. lvai_save_for_previous saves them back to the pin in " +
                          "place (close the project first), or restore them from version control - before they " +
                          "reach the target station. A save through the pinned project does NOT keep the pin " +
                          "(docs/save-version.md §5).",
        };
    }

    /// <summary>
    /// The answer key a tool fills with the versions, read before it saved them, of files the call
    /// did not name - `{ "<path>": "20.0" }`. The check reads it as their `was`, and
    /// <see cref="Annotate"/> takes it out of the answer again.
    /// </summary>
    internal const string VersionsBeforeKey = "savedFileVersionsBefore";

    /// <summary>The version a tool handed over for <paramref name="path"/>, or null.</summary>
    private static string? HandedOver(JsonNode? answer, string path) =>
        answer?[VersionsBeforeKey] is JsonObject map
            ? map.FirstOrDefault(kv => string.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>()
            : null;

    /// <summary>The answer's text with `saveVersionCheck` added, or unchanged when there is nothing to say.</summary>
    internal static string Annotate(string answerText, Before before,
                                    IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        JsonObject? answer;
        try { answer = JsonNode.Parse(answerText) as JsonObject; }
        catch (JsonException) { return answerText; }
        if (answer is null) return answerText;
        var handedOver = answer.ContainsKey(VersionsBeforeKey);
        if (answer.ContainsKey("saveVersionCheck"))
            return handedOver ? Json.Document(Without(answer, VersionsBeforeKey)) : answerText;
        var check = Check(before, answer, arguments);
        if (check is null && !handedOver) return answerText;
        answer.Remove(VersionsBeforeKey);
        if (check is not null) answer["saveVersionCheck"] = check;
        return Json.Document(answer);
    }

    private static JsonObject Without(JsonObject o, string key)
    {
        o.Remove(key);
        return o;
    }

    /// <summary>Every rooted path to a LabVIEW file in these values: strings, lines of strings, JSON inside strings.</summary>
    internal static IEnumerable<string> PathsIn(IEnumerable<JsonElement> values)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values) Walk(value, found, 0);
        return found;
    }

    internal static IEnumerable<string> PathsIn(JsonNode? node)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node is not null)
            try { Walk(JsonDocument.Parse(node.ToJsonString()).RootElement, found, 0); }
            catch (JsonException) { }
        return found;
    }

    private static void Walk(JsonElement value, HashSet<string> found, int depth)
    {
        if (depth > 12 || found.Count > MaxFiles) return;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in value.EnumerateObject()) Walk(p.Value, found, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var e in value.EnumerateArray()) Walk(e, found, depth + 1);
                break;
            case JsonValueKind.String:
                var text = value.GetString() ?? "";
                if (text.Length > 1 && (text[0] is '{' or '['))
                {
                    try { Walk(JsonDocument.Parse(text).RootElement, found, depth + 1); return; }
                    catch (JsonException) { }
                }
                foreach (var line in text.Split('\n'))
                    if (AsLabViewFile(line) is { } path) found.Add(path);
                break;
        }
    }

    private static string? AsLabViewFile(string text)
    {
        var t = text.Trim().Trim('"');
        if (t.Length < 6 || t.Length > 400 || !Path.IsPathRooted(t)) return null;
        var ext = Path.GetExtension(t);
        if (!ResourceExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) &&
            !XmlExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        try { return Path.GetFullPath(t); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException) { return null; }
    }
}
