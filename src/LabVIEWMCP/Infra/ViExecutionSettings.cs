using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The settings of VI Properties > Execution, and the LVSR fields that carry them in the file.
///
/// EVERY ROW WAS MEASURED, 2026-11-11 (experiments/exec-settings-2026-11-11, docs/vi-execution-settings.md):
/// a field set alone in a copy of one VI, then LabVIEW asked - over COM where it answers
/// (AllowDebugging, IsReentrant/ReentrancyType, ExecPriority, PreferredExecSystem, RunOnOpen,
/// SuspendOnCall), through its Error List where it does not (inline: "configured with inlining and
/// debugging enabled"), and through a LabVIEW save round trip for the rest (ClearIndMask,
/// AutoHndlMenus, DefaultErrorHandling survived LabVIEW's own save unchanged). The VI Server writes
/// of the same properties moved the same fields.
///
/// THE FILE ROUTE FOR ALL TEN, on purpose: VI Server can write only seven of them (inline, clear
/// indicators and auto handle menus have no property), and one route that sets all ten through
/// the same patch is cheaper to reason about than two that must agree.
/// </summary>
internal static class ViExecutionSettings
{
    /// <summary>One field to write: the LVSR element, its attribute, the value.</summary>
    internal sealed record Field(string Element, string Attribute, string Value);

    /// <summary>What the caller asked for; null leaves a setting as the file has it.</summary>
    internal sealed record Request(
        bool? AllowDebugging = null,
        string? Reentrancy = null,
        bool? Inline = null,
        string? Priority = null,
        string? ExecutionSystem = null,
        bool? AutomaticErrorHandling = null,
        bool? RunWhenOpened = null,
        bool? SuspendWhenCalled = null,
        bool? ClearIndicators = null,
        bool? AutoHandleMenus = null)
    {
        internal bool IsEmpty =>
            AllowDebugging is null && Reentrancy is null && Inline is null && Priority is null
            && ExecutionSystem is null && AutomaticErrorHandling is null && RunWhenOpened is null
            && SuspendWhenCalled is null && ClearIndicators is null && AutoHandleMenus is null;
    }

    /// <summary>The fields to write, what was implied, and why a request was refused.</summary>
    internal sealed record Plan(IReadOnlyList<Field> Fields, IReadOnlyList<string> Implied,
                                IReadOnlyList<string> Problems, Request Effective);

    internal const string NotReentrant = "none";
    internal const string Shared = "shared";
    internal const string Preallocated = "preallocated";
    internal static readonly string[] ReentrancyNames = [NotReentrant, Shared, Preallocated];

    /// <summary>
    /// The dialog's priorities in its own order. The FILE holds 0..5, COM's ExecPriority and VI
    /// Server's Execution:Priority hold 1..6 (measured: a normal VI reads file 1 / COM 2, NI's
    /// subroutine VIs file 5 / COM 6, a VI Server write of 3 lands as file 2).
    /// </summary>
    internal static readonly string[] PriorityNames =
        ["background", "normal", "above normal", "high", "time critical", "subroutine"];

    /// <summary>
    /// The dialog's execution systems after "same as caller". The FILE holds -1 for same as caller
    /// and 0..5 for these; COM's PreferredExecSystem holds 7 and 1..6 (measured by COM writes of
    /// 1, 3, 6, 7 read back from LabVIEW's own saves as 0, 2, 5, -1; a COM write of 0 is refused).
    /// </summary>
    internal static readonly string[] ExecutionSystemNames =
        ["user interface", "standard", "instrument I/O", "data acquisition", "other 1", "other 2"];

    internal const string SameAsCaller = "same as caller";

    /// <summary>A priority name to its file value, or null.</summary>
    internal static int? PriorityFileValue(string name)
    {
        var i = Array.FindIndex(PriorityNames, n => Same(n, name));
        return i < 0 ? null : i;
    }

    /// <summary>An execution system name to its file value (-1 same as caller), or null.</summary>
    internal static int? ExecutionSystemFileValue(string name)
    {
        if (Same(name, SameAsCaller)) return -1;
        var i = Array.FindIndex(ExecutionSystemNames, n => Same(n, name));
        return i < 0 ? null : i;
    }

    internal static string? PriorityName(int comValue) =>
        comValue is >= 1 and <= 6 ? PriorityNames[comValue - 1] : null;

    internal static string? ExecutionSystemName(int comValue) => comValue switch
    {
        7 => SameAsCaller,
        >= 1 and <= 6 => ExecutionSystemNames[comValue - 1],
        _ => null,
    };

    private static bool Same(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string s) =>
        Regex.Replace(s.Trim().Replace("_", " ").Replace("-", " ").Replace("/", " "), @"\s+", " ")
             .Replace("instrument i o", "instrument io", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turn a request into fields. INLINE IMPLIES what LabVIEW requires of an inlined VI - debugging
    /// off and preallocated reentrancy (its Error List names both, measured) - when the caller did
    /// not say; an explicit contradiction is refused rather than written into an eBad VI.
    /// </summary>
    internal static Plan Resolve(Request r)
    {
        var fields = new List<Field>();
        var implied = new List<string>();
        var problems = new List<string>();

        var debug = r.AllowDebugging;
        var reentrancy = r.Reentrancy?.Trim().ToLowerInvariant();
        if (reentrancy is not null && !ReentrancyNames.Contains(reentrancy))
            problems.Add($"reentrancy '{r.Reentrancy}' is not one of {string.Join(", ", ReentrancyNames)}.");

        if (r.Inline == true)
        {
            if (debug == true)
                problems.Add("inline true with allowDebugging true: LabVIEW breaks such a VI (\"You cannot inline a VI " +
                             "on which you enable debugging\"). Leave allowDebugging out or set it false.");
            else if (debug is null) { debug = false; implied.Add("allowDebugging false - an inlined VI may not be debuggable"); }

            if (reentrancy is NotReentrant or Shared)
                problems.Add($"inline true with reentrancy '{reentrancy}': LabVIEW inlines only a preallocated-clone " +
                             "reentrant VI. Leave reentrancy out or set it 'preallocated'.");
            else if (reentrancy is null) { reentrancy = Preallocated; implied.Add("reentrancy preallocated - inlining needs it"); }
        }

        int? priority = null;
        if (r.Priority is not null && (priority = PriorityFileValue(r.Priority)) is null)
            problems.Add($"priority '{r.Priority}' is not one of {string.Join(", ", PriorityNames)}.");
        int? system = null;
        if (r.ExecutionSystem is not null && (system = ExecutionSystemFileValue(r.ExecutionSystem)) is null)
            problems.Add($"executionSystem '{r.ExecutionSystem}' is not one of {SameAsCaller}, " +
                         $"{string.Join(", ", ExecutionSystemNames)}.");

        if (problems.Count > 0) return new Plan([], implied, problems, r);

        static string B(bool v) => v ? "1" : "0";
        if (debug is { } d) fields.Add(new("Instrument", "DebugCapable", B(d)));
        switch (reentrancy)
        {
            case NotReentrant:
                fields.Add(new("Execution", "IsReentrant", "0"));
                fields.Add(new("Execution", "PooledReentrancy", "0"));
                break;
            case Shared:
                fields.Add(new("Execution", "IsReentrant", "1"));
                fields.Add(new("Execution", "PooledReentrancy", "1"));
                break;
            case Preallocated:
                fields.Add(new("Execution", "IsReentrant", "1"));
                fields.Add(new("Execution", "PooledReentrancy", "0"));
                // what LabVIEW's own save of a preallocated VI wrote, and what pylabview's output
                // of an inlined one needs to load (docs/malleable-vis.md)
                fields.Add(new("Execution", "SaveParallel", "1"));
                break;
        }
        if (r.Inline is { } inline)
        {
            // InlineStg does the work (2 alone broke a non-reentrant VI with LabVIEW's two inline
            // errors; ShouldInline=1 alone left it executable); ShouldInline is set beside it
            // because NI's inlined VIs all carry both
            fields.Add(new("Unknown", "InlineStg", inline ? "2" : "1"));
            fields.Add(new("Execution2", "ShouldInline", B(inline)));
        }
        if (priority is { } p)
        {
            fields.Add(new("Execution", "Priority", p.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            fields.Add(new("Execution", "IsSubroutine", p == 5 ? "1" : "0"));
        }
        if (system is { } s)
            fields.Add(new("Execution", "PrefExecSyst", s.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (r.AutomaticErrorHandling is { } e) fields.Add(new("Execution2", "DefaultErrorHandling", B(e)));
        if (r.RunWhenOpened is { } o) fields.Add(new("Execution", "RunOnOpen", B(o)));
        if (r.SuspendWhenCalled is { } sc) fields.Add(new("Execution", "HasSetBP", B(sc)));
        if (r.ClearIndicators is { } c) fields.Add(new("Execution2", "ClearIndMask", B(c)));
        if (r.AutoHandleMenus is { } m) fields.Add(new("Flags0C", "AutoHndlMenus", B(m)));

        return new Plan(fields, implied, problems,
                        r with { AllowDebugging = debug, Reentrancy = reentrancy });
    }

    /// <summary>
    /// Write the fields into a pylv_extract main XML, each inside ITS element of the LVSR section -
    /// `Priority` or `Unknown` occur elsewhere in a bundle, so a file-wide replace would hit them.
    /// Bytes in, bytes out, as MalleableVi.PatchBundle does.
    /// </summary>
    internal static (string[] Set, string[] NotFound) Patch(string mainXmlPath, IEnumerable<Field> fields)
    {
        var utf8 = new UTF8Encoding(false);
        var text = utf8.GetString(File.ReadAllBytes(mainXmlPath));
        var (newText, set, notFound) = PatchText(text, fields);
        if (set.Length > 0) File.WriteAllBytes(mainXmlPath, utf8.GetBytes(newText));
        return (set, notFound);
    }

    internal static (string Text, string[] Set, string[] NotFound) PatchText(string text, IEnumerable<Field> fields)
    {
        var lvsr = Regex.Match(text, @"<LVSR>.*?</LVSR>", RegexOptions.Singleline);
        if (!lvsr.Success) return (text, [], [.. fields.Select(f => $"{f.Element}.{f.Attribute}")]);
        var section = lvsr.Value;
        List<string> set = [], notFound = [];
        foreach (var f in fields)
        {
            var element = new Regex($@"<{Regex.Escape(f.Element)}\s[^>]*>");
            var tag = element.Match(section);
            var attribute = new Regex($@"(\s{Regex.Escape(f.Attribute)}="")[^""]*("")");
            if (!tag.Success || !attribute.IsMatch(tag.Value)) { notFound.Add($"{f.Element}.{f.Attribute}"); continue; }
            var patched = attribute.Replace(tag.Value, $"${{1}}{f.Value}${{2}}", 1);
            section = section[..tag.Index] + patched + section[(tag.Index + tag.Length)..];
            set.Add($"{f.Element}.{f.Attribute}={f.Value}");
        }
        return (text[..lvsr.Index] + section + text[(lvsr.Index + lvsr.Length)..], [.. set], [.. notFound]);
    }

    /// <summary>The ten settings as a pylv_extract main XML holds them, by the dialog's names.</summary>
    internal static JsonObject Read(string text)
    {
        var lvsr = Regex.Match(text, @"<LVSR>.*?</LVSR>", RegexOptions.Singleline).Value;
        string? A(string element, string attribute)
        {
            var tag = Regex.Match(lvsr, $@"<{Regex.Escape(element)}\s[^>]*>");
            if (!tag.Success) return null;
            var a = Regex.Match(tag.Value, $@"\s{Regex.Escape(attribute)}=""([^""]*)""");
            return a.Success ? a.Groups[1].Value : null;
        }
        static bool? Bit(string? v) => v is null ? null : v != "0";
        static int? Int(string? v) => int.TryParse(v, out var i) ? i : null;

        var reentrant = Bit(A("Execution", "IsReentrant"));
        var pooled = Bit(A("Execution", "PooledReentrancy"));
        var priority = Int(A("Execution", "Priority"));
        var system = Int(A("Execution", "PrefExecSyst"));
        return new JsonObject
        {
            ["allowDebugging"] = Bit(A("Instrument", "DebugCapable")),
            ["reentrancy"] = reentrant is null ? null : reentrant == false ? NotReentrant : pooled == true ? Shared : Preallocated,
            ["inline"] = A("Unknown", "InlineStg") is { } stg ? stg == "2" : null,
            ["priority"] = priority is >= 0 and <= 5 ? PriorityNames[priority.Value] : null,
            ["executionSystem"] = system == -1 ? SameAsCaller : system is >= 0 and <= 5 ? ExecutionSystemNames[system.Value] : null,
            ["automaticErrorHandling"] = Bit(A("Execution2", "DefaultErrorHandling")),
            ["runWhenOpened"] = Bit(A("Execution", "RunOnOpen")),
            ["suspendWhenCalled"] = Bit(A("Execution", "HasSetBP")),
            ["clearIndicators"] = Bit(A("Execution2", "ClearIndMask")),
            ["autoHandleMenus"] = Bit(A("Flags0C", "AutoHndlMenus")),
        };
    }
}
