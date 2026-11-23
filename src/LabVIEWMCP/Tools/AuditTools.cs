using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

[McpServerToolType]
internal sealed class AuditTools
{
    [McpServerTool(Name = "lvai_audit_files", ReadOnly = true,
                   Title = "Audit saved LabVIEW files against the house rules, no LabVIEW")]
    [Description("""
        Check, ON DEMAND (no build step - every writing tool already reports its own errors as
        `audit`): reads SAVED .vi/.vit/.vim/.ctl/.lvlib/.lvclass/.lvproj files - existing or
        foreign code, a review - and checks the house rules a file carries, with NO LabVIEW
        (about 20 ms a file). Rules: compiled-code (error: source-only off or a VICD block),
        automatic-error-handling (error: on in a VI), orphaned-vi (error: the .lvlib/.lvclass a VI or ctl
        names as owner, scanned too, does not list it), description (warning: VI, ctl, lvlib,
        lvclass), terminal-style (warning: a panel terminal shown as an icon), source-only-default
        (warning: an lvproj/lvlib/lvclass without NI.LV.All.SourceOnly). It catches what files
        written outside lvai_generate_vi carry - grafted supplied VIs, DQMH scripters, NI providers.
        `ok` is false on any error finding and on any file it could not read (`analysisErrors` -
        never a pass). A polymorphic or global VI has no connector pane and is no error. Fix with
        lvai_set_vi_execution (automatic error handling), lvai_set_vi_descriptions, pylv_apply.
        docs/offline-audit-evaluation.md.
        """)]
    public string AuditFiles(
        [Description("Folders and/or files to audit, one per line (absolute). Folders are searched recursively")]
        string paths,
        [Description("Folder NAMES to skip, one per line (e.g. Scratch). Folders starting with a dot are always skipped")]
        string? excludeFolders = null,
        [Description("Rule ids to leave out, one per line")] string? ignoreRules = null,
        [Description("Also return every file's facts (pane, terminals, owner, flags). Default false")]
        bool includeFacts = false,
        [Description("At most this many findings in the answer; the counts cover all. Default 200")]
        int maxFindings = 200)
    {
        static string[] Lines(string? s) =>
            (s ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var roots = Lines(paths);
        if (roots.Length == 0)
            return Json.Error("badArguments", "paths names nothing - give folders or files, one per line.");
        var missing = roots.Where(r => !File.Exists(r) && !Directory.Exists(r)).ToArray();
        if (missing.Length > 0)
            return Json.Error("fileNotFound", "These paths do not exist: " + string.Join("; ", missing),
                              new { missing });
        var ignored = Lines(ignoreRules);
        var unknown = ignored.Where(i => LvFileAudit.Rules.All(r => r.Id != i)).ToArray();
        if (unknown.Length > 0)
            return Json.Error("badArguments", "Unknown rule id(s): " + string.Join(", ", unknown) +
                              ". Known: " + string.Join(", ", LvFileAudit.Rules.Select(r => r.Id)));

        var watch = Stopwatch.StartNew();
        var report = LvFileAudit.Audit(roots, Lines(excludeFolders), ignored);
        watch.Stop();
        return Json.Document(Answer(report, includeFacts, Math.Max(0, maxFindings), watch.ElapsedMilliseconds));
    }

    internal static JsonObject Answer(LvFileAudit.Report report, bool includeFacts, int maxFindings, long ms)
    {
        var errors = report.Findings.Count(f => f.Severity == "error");
        var byRule = new JsonObject();
        foreach (var g in report.Findings.GroupBy(f => f.Rule).OrderBy(g => g.Key))
            byRule[g.Key] = g.Count();
        var answer = new JsonObject
        {
            ["ok"] = errors == 0 && report.Errors.Count == 0,
            ["filesRead"] = report.Files.Count,
            ["foldersSkipped"] = report.Skipped,
            ["errors"] = errors,
            ["warnings"] = report.Findings.Count - errors,
            ["byRule"] = byRule,
            ["analysisErrors"] = new JsonArray([.. report.Errors.Select(e =>
                (JsonNode)new JsonObject { ["path"] = e.Path, ["error"] = e.Error })]),
            ["findings"] = new JsonArray([.. report.Findings
                .OrderBy(f => f.Severity == "error" ? 0 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(maxFindings)
                .Select(f => (JsonNode)new JsonObject
                {
                    ["path"] = f.Path, ["rule"] = f.Rule, ["severity"] = f.Severity, ["message"] = f.Message,
                })]),
            ["elapsedMs"] = ms,
        };
        if (report.Findings.Count > maxFindings)
            answer["findingsTruncated"] = report.Findings.Count - maxFindings;
        if (includeFacts)
            answer["facts"] = new JsonArray([.. report.Files.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Path,
                ["kind"] = f.Kind,
                ["version"] = f.Version,
                ["sourceOnly"] = f.SourceOnly,
                ["compiledCode"] = f.CompiledCode,
                ["automaticErrorHandling"] = f.AutomaticErrorHandling,
                ["allowDebugging"] = f.AllowDebugging,
                ["description"] = f.HasDescription,
                ["connectorPane"] = f.ConnectorPane,
                ["paneLess"] = f.PaneLess,
                ["iconTerminals"] = f.Kind == "vi" ? f.IconTerminals : null,
                ["dataTypeTerminals"] = f.Kind == "vi" ? f.DataTypeTerminals : null,
                ["owner"] = f.Owner,
                ["members"] = f.Members?.Count,
            })]);
        answer["note"] = report.Errors.Count > 0
            ? $"{report.Errors.Count} file(s) could not be read - an unread file is not a pass; read analysisErrors."
            : errors > 0
                ? "Error findings break a house rule - fix them before the build is done."
                : report.Findings.Count > 0 ? "No rule errors; warnings are listed." : "Every file passes.";
        return answer;
    }
}
