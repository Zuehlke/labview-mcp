using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The offline audit (<see cref="LvFileAudit"/>) run on what ONE tool call just wrote, so that a
/// defect shows at the tool that caused it (the user's decision of 2026-11-22). Since 2026-11-23
/// it is THE check of a build: the closing <c>lvai_audit_files</c> is on demand only.
///
/// It must never cost more than it saves, so it is REPORT-ONLY and QUIET:
/// - only ERROR rules (compiled code, automatic error handling, orphaned file). Warnings - a
///   missing description on a DQMH scaffold VI or a placeholder - would be noise on every call;
///   <c>lvai_audit_files</c>, run on demand, reports them;
/// - `ok` and every other field of the answer stay as the tool wrote them; an `audit` field is
///   added only when there is an error finding, so a clean call's answer is unchanged;
/// - whether a VI RUNS is not its business. A deliberately unfinished VI (DQMH scripting leaves
///   several) passes as long as it is source-only, has automatic error handling off and is listed
///   by its owner; executability stays with <c>lvai_exec_state</c>;
/// - a VI whose source-only strip is DEFERRED (<see cref="DeferredSourceOnly"/>) is not reported
///   for compiled code: the next project close removes it;
/// - a file it cannot parse is skipped here; <c>lvai_audit_files</c> reports it.
///
/// Which files: every existing LabVIEW file named anywhere in the answer and written since the call
/// began, plus the members of a written library or class written since then too (a DQMH module
/// answer names the library, not its fifty files). An owner is read even when not written, so the
/// orphan rule can see whether it lists the file.
/// </summary>
internal static class InlineAudit
{
    /// <summary>Clock slack: file times are coarse and the write can land just before the start stamp.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    private const int MaxFiles = 400;

    /// <summary>The answer with an `audit` field added, or null when there is nothing to report.</summary>
    internal static string? Annotate(string answer, DateTime startUtc)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.TrimStart()[0] != '{') return null;
        JsonObject root;
        try { root = JsonNode.Parse(answer) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return null; }

        var findings = Findings(Written(root, startUtc - Slack));
        if (findings.Count == 0) return null;

        root["audit"] = new JsonObject
        {
            ["errors"] = findings.Count,
            ["findings"] = new JsonArray(findings.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Path,
                ["rule"] = f.Rule,
                ["message"] = f.Message,
            }).ToArray()),
            ["note"] = "Offline audit of the files this call wrote (errors only; `ok` is not changed by it). " +
                       "Fix it now - no closing audit of the build will report it again.",
        };
        return root.ToJsonString(Json.Answer);
    }

    internal static List<LvFileAudit.Finding> Findings(IReadOnlyCollection<string> written)
    {
        if (written.Count == 0) return [];
        var facts = new Dictionary<string, LvFileAudit.Facts>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in written) TryRead(p, facts);
        // Owners too, written or not: the orphan rule only judges a file whose owner it has read.
        foreach (var owner in facts.Values.Select(f => f.Owner).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            if (File.Exists(owner)) TryRead(owner, facts);

        var writtenSet = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
        return LvFileAudit.Check(facts.Values.ToList(), [])
            .Where(f => f.Severity == "error" && writtenSet.Contains(f.Path))
            .Where(f => !(f.Rule == "compiled-code" && DeferredSourceOnly.IsPending(f.Path)))
            .ToList();
    }

    internal static List<string> Written(JsonNode root, DateTime sinceUtc)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Strings(root))
        {
            if (found.Count >= MaxFiles) break;
            if (!LooksLikeFile(s, out var full) || !IsFresh(full, sinceUtc)) continue;
            found.Add(full);
        }
        // Members of a written owner that were written by this call as well.
        foreach (var owner in found.Where(IsOwnerFile).ToList())
        {
            try
            {
                foreach (var m in LvFileAudit.ReadXml(owner).Members ?? [])
                    if (found.Count < MaxFiles && File.Exists(m) && IsFresh(m, sinceUtc)) found.Add(Path.GetFullPath(m));
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { }
        }
        return found.ToList();
    }

    private static bool IsOwnerFile(string p) =>
        p.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeFile(string s, out string full)
    {
        full = "";
        if (s.Length is < 5 or > 1024 || s.Contains('\n') || s.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        // .lvproj is not audited inline: its only rule is a warning.
        if (!LvFileAudit.FileExtensions.Contains(Path.GetExtension(s), StringComparer.OrdinalIgnoreCase)
            || s.EndsWith(".lvproj", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(s)) return false;
            full = Path.GetFullPath(s);
            return File.Exists(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsFresh(string path, DateTime sinceUtc)
    {
        try { return File.GetLastWriteTimeUtc(path) >= sinceUtc; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryRead(string path, Dictionary<string, LvFileAudit.Facts> into)
    {
        var full = Path.GetFullPath(path);
        if (into.ContainsKey(full)) return;
        try { into[full] = LvFileAudit.Read(full); }
        catch (Exception e) when (e is InvalidDataException or IOException or OverflowException
                                      or ArgumentOutOfRangeException or System.Xml.XmlException
                                      or UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, v) in o)
                    foreach (var s in Strings(v)) yield return s;
                break;
            case JsonArray a:
                foreach (var v in a)
                    foreach (var s in Strings(v)) yield return s;
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                yield return s;
                break;
        }
    }
}
