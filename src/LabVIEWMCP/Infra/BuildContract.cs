using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The BUILD CONTRACT: one JSON file the orchestrator writes BEFORE the first agent is spawned,
/// naming every VI of an application with its path, owner, builder and connector pane - terminal
/// names, types, conIdx and connection - and what it calls.
///
/// WHY IT EXISTS - the ATM DQMH cold build of 2026-10-31 (`docs/build-contract.md`). Five agents
/// each settled the interfaces they needed for themselves: sixteen `lvai_vi_terminals` calls to
/// learn names another agent had just chosen, a Caraya suite whose bare `Deposit.vi` call linked
/// to the DQMH module's `Deposit.vi` instead of the loose one (Error 403681, found only when the
/// suite ran), and a main-VI agent that could not start until every callee existed. A contract
/// settles all of that once, up front, where a clash costs a sentence instead of a test run - and
/// it lets the caller's scaffold be authored while its callees are still being built.
///
/// This class is pure: it parses and checks the document and computes the build waves. Reading
/// the BUILT VIs back and comparing them is the tool's job (`lvai_check_build_contract`, mode
/// `verify`), because that needs an export.
/// </summary>
internal static class BuildContract
{
    public const string ErrorClusterType = "cluster{bool.status,int32.code,string.source}";

    /// <summary>Kinds whose pane the house rules govern - a VI this build CREATES.</summary>
    public const string KindNew = "new";

    /// <summary>A supplied panel (exam template) the program is grafted into: no pane rules.</summary>
    public const string KindSupplied = "supplied";

    /// <summary>Made by a scripting tool (DQMH, class providers) or already there: terminals informational.</summary>
    public const string KindExisting = "existing";

    /// <summary>A unit test VI: built by a test generator, no pane rules.</summary>
    public const string KindTest = "test";

    /// <summary>The pattern the generator picks by itself for conIdx up to 19 (6x2x2x2x2x6).</summary>
    public const int WidenedPattern = 4834;

    private static readonly string[] Kinds =[KindNew, KindSupplied, KindExisting, KindTest];
    private static readonly string[] Connections = ["required", "recommended", "optional"];

    private static readonly string[] TopKeys = ["application", "root", "project", "panePattern", "types", "vis"];
    private static readonly string[] ViKeys =
        ["path", "owner", "kind", "builder", "purpose", "panePattern", "inputs", "outputs", "calls"];
    private static readonly string[] TerminalKeys = ["name", "type", "conIdx", "connection", "description"];

    internal sealed record Terminal(string Name, string Type, int? ConIdx, string? Connection, bool IsOutput);

    internal sealed record Vi(
        int Index,
        string Path,
        string FullPath,
        string? Owner,
        string Kind,
        string? Builder,
        int? PanePattern,
        IReadOnlyList<Terminal> Inputs,
        IReadOnlyList<Terminal> Outputs,
        IReadOnlyList<string> Calls)
    {
        public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>`Account Manager.lvlib:Deposit.vi` for an owned VI, else the bare file name.</summary>
        public string QualifiedName => Owner is { Length: > 0 } ? $"{Owner}:{FileName}" : FileName;

        public bool IsClassMember => Owner?.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase) == true;

        public IEnumerable<Terminal> Terminals => Inputs.Concat(Outputs);
    }

    internal sealed record Document(
        string? Application,
        string Root,
        string? Project,
        int? PanePattern,
        IReadOnlyList<Vi> Vis);

    /// <summary>One thing wrong (or worth knowing) about the contract.</summary>
    internal sealed record Finding(string Severity, string Rule, string? Vi, string? Terminal, string Message,
                                   string? Fix = null)
    {
        public JsonObject ToJson()
        {
            var o = new JsonObject { ["rule"] = Rule };
            if (Vi is not null) o["vi"] = Vi;
            if (Terminal is not null) o["terminal"] = Terminal;
            o["message"] = Message;
            if (Fix is not null) o["fix"] = Fix;
            return o;
        }
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// The document, or an <see cref="ArgumentException"/> naming exactly what is wrong. Unknown
    /// keys are refused by name at every level - the repository's rule since a dropped
    /// `expectFieldValue` made a test pin the opposite of what was asked (CLAUDE.md, "casesJson").
    /// </summary>
    public static Document Parse(string json, string contractPath)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }); }
        catch (JsonException e) { throw new ArgumentException($"The contract is not valid JSON: {e.Message}"); }

        if (node is not JsonObject top) throw new ArgumentException("The contract must be a JSON object.");
        RejectUnknown(top, TopKeys, "the contract");

        var contractDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(contractPath)) ?? "";
        var root = Str(top, "root", "the contract") is { Length: > 0 } r
            ? System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(r) ? r : System.IO.Path.Combine(contractDir, r))
            : contractDir;

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        if (top["types"] is { } typesNode)
        {
            if (typesNode is not JsonObject typesObject)
                throw new ArgumentException("\"types\" must be an object mapping a name to an AIXML type literal.");
            foreach (var (name, value) in typesObject)
                types[name] = value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
                    ? s
                    : throw new ArgumentException($"types.\"{name}\" must be a non-empty type literal string.");
        }

        if (top["vis"] is not JsonArray visArray || visArray.Count == 0)
            throw new ArgumentException("\"vis\" must be a non-empty array - one entry per VI of the application.");

        var vis = new List<Vi>();
        for (var i = 0; i < visArray.Count; i++)
        {
            var where = $"vis[{i}]";
            if (visArray[i] is not JsonObject v) throw new ArgumentException($"{where} must be an object.");
            RejectUnknown(v, ViKeys, where);

            var path = Str(v, "path", where);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{where} has no \"path\".");
            where = $"vis[{i}] ({path})";

            var kind = Str(v, "kind", where) ?? KindNew;
            if (!Kinds.Contains(kind))
                throw new ArgumentException($"{where}: kind \"{kind}\" is not one of {string.Join(", ", Kinds)}.");

            var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root, path));

            vis.Add(new Vi(i, path, full, Str(v, "owner", where), kind, Str(v, "builder", where),
                Int(v, "panePattern", where),
                Terminals(v, "inputs", where, types, isOutput: false),
                Terminals(v, "outputs", where, types, isOutput: true),
                Calls(v, where)));
        }

        return new Document(Str(top, "application", "the contract"), root, Str(top, "project", "the contract"),
                            Int(top, "panePattern", "the contract"), vis);
    }

    private static List<Terminal> Terminals(JsonObject v, string key, string where,
                                            IReadOnlyDictionary<string, string> types, bool isOutput)
    {
        if (v[key] is null) return [];
        if (v[key] is not JsonArray array) throw new ArgumentException($"{where}: \"{key}\" must be an array.");

        var list = new List<Terminal>();
        for (var i = 0; i < array.Count; i++)
        {
            var at = $"{where}.{key}[{i}]";
            if (array[i] is not JsonObject t) throw new ArgumentException($"{at} must be an object.");
            RejectUnknown(t, TerminalKeys, at);

            var name = Str(t, "name", at);
            if (string.IsNullOrEmpty(name)) throw new ArgumentException($"{at} has no \"name\".");

            var type = Str(t, "type", at) ?? "";
            if (type.StartsWith('@'))
                type = types.TryGetValue(type[1..], out var named)
                    ? named
                    : throw new ArgumentException($"{at} ('{name}') names type \"{type}\", which \"types\" does not define.");

            list.Add(new Terminal(name, type, Int(t, "conIdx", at), Str(t, "connection", at), isOutput));
        }
        return list;
    }

    private static List<string> Calls(JsonObject v, string where)
    {
        if (v["calls"] is null) return [];
        if (v["calls"] is not JsonArray array) throw new ArgumentException($"{where}: \"calls\" must be an array of VI names.");
        return [.. array.Select((c, i) => c is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : throw new ArgumentException($"{where}.calls[{i}] must be a non-empty string."))];
    }

    private static void RejectUnknown(JsonObject o, string[] accepted, string where)
    {
        foreach (var (key, _) in o)
            if (!accepted.Contains(key))
                throw new ArgumentException(
                    $"{where} carries \"{key}\", which is not a contract key. Accepted: {string.Join(", ", accepted)}.");
    }

    private static string? Str(JsonObject o, string key, string where) =>
        o[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => throw new ArgumentException($"{where}: \"{key}\" must be a string."),
        };

    private static int? Int(JsonObject o, string key, string where) =>
        o[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<int>(out var n) => n,
            _ => throw new ArgumentException($"{where}: \"{key}\" must be an integer."),
        };

    // ------------------------------------------------------------------ checking

    /// <summary>The checks that need no LabVIEW, and the build order.</summary>
    internal sealed record PlanResult(
        IReadOnlyList<Finding> Errors,
        IReadOnlyList<Finding> Warnings,
        IReadOnlyList<IReadOnlyList<Vi>> Waves,
        IReadOnlyDictionary<int, int> PatternOf);

    /// <param name="stationPattern">The station's DefaultConPane, or null when unknown.</param>
    /// <param name="installationVi">Where a file name exists in vi.lib/user.lib/instr.lib, or null.</param>
    public static PlanResult Plan(Document doc, int? stationPattern, Func<string, string?>? installationVi = null)
    {
        var errors = new List<Finding>();
        var warnings = new List<Finding>();
        var patternOf = new Dictionary<int, int>();

        // ---- one file, one entry; one file name, one meaning for a bare call
        foreach (var twice in doc.Vis.GroupBy(v => v.FullPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add(new Finding("error", "duplicatePath", twice.First().Path, null,
                $"{twice.Count()} entries name the same file."));

        foreach (var same in doc.Vis.GroupBy(v => v.FileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var loose = same.Where(v => v.Owner is null).ToList();
            if (loose.Count > 1)
                errors.Add(new Finding("error", "sameFileNameTwice", same.Key, null,
                    $"{loose.Count} loose VIs are called '{same.Key}' ({string.Join(", ", loose.Select(v => v.Path))}). " +
                    "LabVIEW holds one VI per name in memory - the second answers Error 1051.",
                    "rename one of them"));
            else
                warnings.Add(new Finding("warning", "sameFileName", same.Key, null,
                    $"'{same.Key}' is {string.Join(" and ", same.Select(v => v.QualifiedName + " (" + v.Path + ")"))}. " +
                    "A Call by the BARE name can link to the wrong one - measured on the ATM build: a test's " +
                    "`Deposit.vi` linked to the DQMH module's request VI and failed with Error 403681.",
                    RenameLoose(same)));
        }

        // ---- calls: every one resolves to exactly one entry, or is named as external
        var byQualified = doc.Vis.GroupBy(v => v.QualifiedName, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var edges = doc.Vis.ToDictionary(v => v.Index, _ => new HashSet<int>());
        foreach (var vi in doc.Vis)
        {
            foreach (var call in vi.Calls)
            {
                if (Resolve(doc, byQualified, call) is { } hit)
                {
                    if (hit.Count == 1)
                    {
                        if (hit[0].Index == vi.Index)
                            errors.Add(new Finding("error", "callsItself", vi.Path, null, $"'{call}' is this VI."));
                        else edges[vi.Index].Add(hit[0].Index);
                    }
                    else
                        errors.Add(new Finding("error", "ambiguousCall", vi.Path, null,
                            $"'{call}' matches {string.Join(" and ", hit.Select(h => $"{h.QualifiedName} ({h.Path})"))}.",
                            hit.Any(h => h.Owner is null)
                                ? RenameLoose(hit)
                                : "name the callee by its qualified name: " + string.Join(" or ", hit.Select(h => h.QualifiedName))));
                }
                else
                    warnings.Add(new Finding("warning", "externalCall", vi.Path, null,
                        $"'{call}' is not a VI of this contract - taken as an installation or vendor VI and not checked."));
            }
        }

        // ---- the pane of every VI this build creates
        foreach (var vi in doc.Vis)
        {
            var pattern = vi.PanePattern ?? (vi.IsClassMember ? 4815 : doc.PanePattern ?? stationPattern);

            // The generator widens the pane by itself when the AIXML names a conIdx the default
            // pattern lacks - measured 2026-09-25, conIdx up to 19 came out on 4834 (CLAUDE.md).
            // The contract follows it rather than reporting every terminal as off the pane; the
            // ATM build's Find Account.vi and ATM Step.vi are both on 4834 that way.
            if (vi.PanePattern is null && !vi.IsClassMember && pattern is { } station &&
                vi.Kind == KindNew && vi.Terminals.Any(t => t.ConIdx is not null))
            {
                var used = vi.Terminals.Where(t => t.ConIdx is not null).Select(t => t.ConIdx!.Value).ToList();
                if (ConnectorPane.ConIdxNotOnPattern(used, station).Count > 0 &&
                    ConnectorPane.ConIdxNotOnPattern(used, WidenedPattern).Count == 0)
                {
                    pattern = WidenedPattern;
                    warnings.Add(new Finding("warning", "patternWidened", vi.Path, null,
                        $"conIdx {string.Join(", ", ConnectorPane.ConIdxNotOnPattern(used, station))} are not slots " +
                        $"of {station}, so the generator puts this VI on {WidenedPattern}; checked against that.",
                        $"write \"panePattern\": {WidenedPattern} on the entry to say so"));
                }
            }
            if (pattern is { } p) patternOf[vi.Index] = p;

            CheckNames(vi, errors);
            if (vi.Kind == KindNew) CheckNewVi(vi, pattern, errors, warnings);

            if (vi.Owner is null && installationVi?.Invoke(vi.FileName) is { } namesake)
                warnings.Add(new Finding("warning", "installationNamesake", vi.Path, null,
                    $"The installation has a VI of the same name: {namesake}. A bare-name Call may resolve to it.",
                    "rename the VI"));
        }

        // ---- one builder, one output directory (CLAUDE.md, "ONE AGENT, ONE OUTPUT DIRECTORY")
        foreach (var dir in doc.Vis.Where(v => v.Builder is not null && v.Kind is KindNew or KindTest)
                                   .GroupBy(v => System.IO.Path.GetDirectoryName(v.FullPath) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var builders = dir.Select(v => v.Builder!).Distinct(StringComparer.Ordinal).ToList();
            if (builders.Count > 1)
                warnings.Add(new Finding("warning", "sharedOutputDirectory", null, null,
                    $"Builders {string.Join(", ", builders)} all write into '{dir.Key}'. Two agents in one folder have " +
                    "overwritten each other's files before.",
                    "give each builder its own folder"));
        }

        var waves = Waves(doc, edges, errors);
        return new PlanResult(errors, warnings, waves, patternOf);
    }

    /// <summary>
    /// The entries a call names: by qualified name first, then by bare file name. Null when it
    /// names none of them. A bare name matching an owned VI AND a loose one is ambiguous.
    /// </summary>
    internal static IReadOnlyList<Vi>? Resolve(Document doc, IReadOnlyDictionary<string, Vi> byQualified, string call)
    {
        var name = call.Replace(@"\3A", ":", StringComparison.Ordinal).Trim();
        if (name.Contains('\\') || name.Contains('/'))
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(name) ? name : System.IO.Path.Combine(doc.Root, name));
            var byPath = doc.Vis.Where(v => string.Equals(v.FullPath, full, StringComparison.OrdinalIgnoreCase)).ToList();
            return byPath.Count > 0 ? byPath : null;
        }
        if (name.Contains(':')) return byQualified.TryGetValue(name, out var q) ? [q] : null;

        var bare = doc.Vis.Where(v => string.Equals(v.FileName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return bare.Count > 0 ? bare : null;
    }

    /// <summary>
    /// The fix for a loose VI sharing its file name with an owned one: RENAME the loose one. An
    /// AIXML Call names a loose VI by its bare name only - no path spelling resolves (CLAUDE.md,
    /// "A MISS IN THE INDEX IS NOT PROOF") - so it cannot be told from its namesake at all; the
    /// ATM test agent got there only by relinking with pylabview afterwards.
    /// </summary>
    private static string RenameLoose(IEnumerable<Vi> same)
    {
        var list = same.ToList();
        var loose = list.Where(v => v.Owner is null).Select(v => v.Path).ToList();
        var owned = list.Where(v => v.Owner is not null).Select(v => v.QualifiedName).ToList();
        return $"rename {string.Join(", ", loose)} - a Call names a loose VI by its bare name only, so it " +
               "cannot be told from its namesake" +
               (owned.Count > 0 ? $"; call {string.Join(", ", owned)} by that qualified name" : "");
    }

    private static void CheckNames(Vi vi, List<Finding> errors)
    {
        foreach (var twice in vi.Terminals.GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add(new Finding("error", "duplicateTerminalName", vi.Path, twice.Key,
                "Two terminals share one label - LabVIEW refuses a pane where an input and an output carry " +
                "the same name, and a Call cannot address either."));

        foreach (var t in vi.Terminals.Where(t => t.Type.Length == 0))
            errors.Add(new Finding("error", "noType", vi.Path, t.Name, "The terminal has no type literal."));

        foreach (var t in vi.Terminals.Where(t => t.Connection is { } c && !Connections.Contains(c)))
            errors.Add(new Finding("error", "badConnection", vi.Path, t.Name,
                $"connection \"{t.Connection}\" is not one of {string.Join(", ", Connections)}."));
    }

    private static void CheckNewVi(Vi vi, int? pattern, List<Finding> errors, List<Finding> warnings)
    {
        // ---- error in / error out (CLAUDE.md, "EVERY VI WE CREATE CARRIES `error in` AND `error out`")
        var errorIn = vi.Inputs.FirstOrDefault(t => t.Name == "error in");
        var errorOut = vi.Outputs.FirstOrDefault(t => t.Name == "error out");

        if (vi.Inputs.FirstOrDefault(t => t.Name.Equals("error in (no error)", StringComparison.OrdinalIgnoreCase)) is { } nI)
            errors.Add(new Finding("error", "errorInLabel", vi.Path, nI.Name,
                "A VI this build creates labels its input `error in`, not NI's `error in (no error)`.",
                "rename it to `error in`"));
        else if (errorIn is null)
            errors.Add(new Finding("error", "noErrorIn", vi.Path, null, "A VI this build creates carries `error in`."));

        if (errorOut is null)
            errors.Add(new Finding("error", "noErrorOut", vi.Path, null, "A VI this build creates carries `error out`."));

        foreach (var e in new[] { errorIn, errorOut })
            if (e is not null && Normalise(e.Type) != Normalise(ErrorClusterType))
                errors.Add(new Finding("error", "errorClusterType", vi.Path, e.Name,
                    $"type \"{e.Type}\" is not the error cluster.", $"write {ErrorClusterType}"));

        if (errorIn is not null && errorIn.Connection != "required")
            errors.Add(new Finding("error", "errorInNotRequired", vi.Path, errorIn.Name,
                "A VI generated new has `error in` as required (the user's rule of 2026-10-31).",
                "connection: required"));

        // ---- outputs: never required, and an omitted connection MEANS required
        foreach (var o in vi.Outputs)
            if (o.Connection is null or "required")
                errors.Add(new Finding("error", "outputRequired", vi.Path, o.Name,
                    o.Connection is null
                        ? "No connection given - LabVIEW makes such a terminal REQUIRED, and a required output breaks every caller that leaves it unwired."
                        : "A required output breaks every caller that leaves it unwired.",
                    "connection: recommended"));

        foreach (var i in vi.Inputs.Where(i => i.Connection is null))
            warnings.Add(new Finding("warning", "inputConnectionOmitted", vi.Path, i.Name,
                "No connection given - LabVIEW makes the input REQUIRED. Say so explicitly if that is meant."));

        // ---- the pane: every terminal on a slot, inputs left, outputs right, error corners
        var missing = vi.Terminals.Where(t => t.ConIdx is null).ToList();
        foreach (var t in missing)
            errors.Add(new Finding("error", "noConIdx", vi.Path, t.Name,
                "The terminal has no conIdx - a front-panel object off the pane cannot be wired by a Call. " +
                "Leave panel-only controls out of the contract."));

        if (pattern is null)
        {
            warnings.Add(new Finding("warning", "patternUnknown", vi.Path, null,
                "No pane pattern is known (no panePattern, no station default), so the slots were not checked."));
            return;
        }

        var geometry = ConnectorPanePatterns.Find(pattern.Value)?.Geometry;
        if (geometry is null)
        {
            warnings.Add(new Finding("warning", "patternUnmeasured", vi.Path, null,
                $"Pattern {pattern} has no measured geometry here, so the slots were not checked."));
            return;
        }

        var terminals = vi.Terminals.Where(t => t.ConIdx is not null)
            .Select(t => new ConnectorPane.Terminal(t.Name, t.IsOutput, t.ConIdx!.Value)).ToList();
        foreach (var f in ConnectorPane.Review(geometry, terminals))
            (f.Severity == "violation" ? errors : warnings).Add(new Finding(
                f.Severity == "violation" ? "error" : "warning", "pane", vi.Path, f.Terminal,
                $"pattern {pattern}: {f.Problem}", f.Fix));
    }

    /// <summary>
    /// The build order: wave 0 calls nothing of this contract, wave n calls only earlier waves.
    /// Everything in one wave can be built in parallel. A cycle is an error and its members are
    /// left out of the waves.
    /// </summary>
    private static List<IReadOnlyList<Vi>> Waves(Document doc, Dictionary<int, HashSet<int>> edges, List<Finding> errors)
    {
        var level = new Dictionary<int, int>();
        var remaining = doc.Vis.Select(v => v.Index).ToHashSet();
        var waves = new List<IReadOnlyList<Vi>>();

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(i => edges[i].All(level.ContainsKey)).ToList();
            if (ready.Count == 0)
            {
                errors.Add(new Finding("error", "callCycle", null, null,
                    "These VIs call each other in a cycle: " +
                    string.Join(", ", remaining.Select(i => doc.Vis[i].Path)) + "."));
                break;
            }
            foreach (var i in ready) level[i] = waves.Count;
            waves.Add([.. ready.Select(i => doc.Vis[i])]);
            remaining.ExceptWith(ready);
        }
        return waves;
    }

    /// <summary>A type literal without whitespace, for comparing a contract with an export.</summary>
    public static string Normalise(string type) =>
        string.Concat(type.Where(c => !char.IsWhiteSpace(c)));

    // ------------------------------------------------------------------ verifying a built VI

    /// <summary>
    /// The differences between what the contract promises and the pane a built VI carries. Empty
    /// means the VI keeps the contract. <paramref name="builtInputs"/>/<paramref name="builtOutputs"/>
    /// are the PANE terminals of the export (those with a conIdx).
    /// </summary>
    public static List<string> Compare(Vi vi, IReadOnlyList<ViTerminals.Terminal> builtInputs,
                                       IReadOnlyList<ViTerminals.Terminal> builtOutputs)
    {
        var differences = new List<string>();
        var built = builtInputs.Select(t => (t, output: false)).Concat(builtOutputs.Select(t => (t, output: true)))
                               .ToDictionary(x => x.t.Name, x => x, StringComparer.Ordinal);
        var promised = vi.Terminals.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var p in vi.Terminals)
        {
            if (!built.TryGetValue(p.Name, out var b))
            {
                differences.Add($"'{p.Name}' is not on the built pane");
                continue;
            }
            if (b.output != p.IsOutput)
                differences.Add($"'{p.Name}' is built as an {(b.output ? "output" : "input")}, promised as an {(p.IsOutput ? "output" : "input")}");
            if (p.Type.Length > 0 && Normalise(p.Type) != Normalise(b.t.Type))
                differences.Add($"'{p.Name}' type {b.t.Type}, promised {p.Type}");
            if (p.ConIdx is { } c && b.t.ConIdx != c)
                differences.Add($"'{p.Name}' sits on conIdx {b.t.ConIdx}, promised {c}");
            var builtConnection = b.t.Connection ?? "required";
            if (p.Connection is { } pc && builtConnection != pc)
                differences.Add($"'{p.Name}' is {builtConnection}, promised {pc}");
        }

        foreach (var extra in built.Keys.Where(k => !promised.ContainsKey(k)))
            differences.Add($"'{extra}' is on the built pane but not in the contract");

        return differences;
    }
}
