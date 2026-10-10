// What an Actor Framework override looks like and where a hook VI goes on it - the pure half of
// lvai_create_override, lvai_af_add_hook and lvai_af_remove_hook, testable with no LabVIEW.
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// THE OVERRIDE PANES AND THE HOOK SHAPES, as data. Everything here is decided from the shape the
/// caller asks for, the override's measured pane and the hook's own terminals - never from a run -
/// so a wrong plan is refused before LabVIEW is touched.
///
/// WHERE THE FACTS COME FROM. The four AF panes were measured on a pilot project's overrides
/// (2026-09): Pre Launch Init has NO `error in`, Stop Core has no error terminals at all, Handle
/// Error takes `error in (no error)` and returns `stop actor?`, and Actor Core's class output is
/// `Actor out`, typed Actor rather than the child. Handle Last Ack Core's pane was read offline
/// from production code and measured 2026-10-10. The shapes are the four the kit measured by hand:
/// A branch (class terminal tapped beside Call Parent), B series before Call Parent, B-after
/// series after it with the roles swapped, C a branch off a series hook's output.
/// </summary>
internal static class AfOverrides
{
    /// <summary>The AF lifecycle methods an actor overrides, spelled as NI names their files.</summary>
    internal static readonly string[] LifecycleMethods =
        ["Pre Launch Init", "Actor Core", "Stop Core", "Handle Error", "Handle Last Ack Core"];

    /// <summary>Where NI keeps them, relative to a LabVIEW installation root.</summary>
    internal const string ActorFolderRelative = @"vi.lib\ActorFramework\Actor";

    /// <summary>The canonical lifecycle name for a spelling with or without `.vi`, else null.</summary>
    internal static string? Lifecycle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bare = name.Trim();
        if (bare.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)) bare = bare[..^3];
        return LifecycleMethods.FirstOrDefault(m => string.Equals(m, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The name an override has in LabVIEW's memory: the class's own qualified name plus the file.
    /// A class in a library is `Lib.lvlib:Cls.lvclass:Actor Core.vi`, and CreateOverride's new VI
    /// cannot be opened by any shorter name - `open new vi error`, nothing saved (measured).
    /// </summary>
    internal static string ViNameInMemory(LvClass.ClassInfo info, string fileName) =>
        $"{info.QualifiedName}:{fileName}";

    // ------------------------------------------------------------------ panes

    internal sealed record Terminal(string Name, int ConIdx, bool IsInput);

    /// <summary>
    /// One override's pane. <paramref name="ClassOut"/> is null for Actor Core, whose class output
    /// is `Actor out` typed Actor; <paramref name="ErrorIn"/> is null where the pane has none.
    /// </summary>
    internal sealed record Pane(string Method, IReadOnlyList<Terminal> Terminals, string ClassIn,
        string? ClassOut, string? ErrorIn, string? ErrorOut, bool Measured, string Evidence)
    {
        public JsonObject ToJson() => new()
        {
            ["method"] = Method,
            ["inputs"] = new JsonArray([.. Terminals.Where(t => t.IsInput)
                .Select(t => (JsonNode)$"{t.Name} ({t.ConIdx})")]),
            ["outputs"] = new JsonArray([.. Terminals.Where(t => !t.IsInput)
                .Select(t => (JsonNode)$"{t.Name} ({t.ConIdx})")]),
            ["measured"] = Measured,
            ["evidence"] = Evidence,
        };
    }

    /// <summary>The pane of one AF lifecycle override on a class called <paramref name="className"/>.</summary>
    internal static Pane? PaneFor(string? method, string className)
    {
        var cin = $"{className} in";
        var cout = $"{className} out";
        const string pilot = "measured on a pilot project's overrides, 2026-09";
        return Lifecycle(method) switch
        {
            "Pre Launch Init" => new Pane("Pre Launch Init",
                [new(cin, 11, true), new(cout, 3, false), new("error out", 0, false)],
                cin, cout, null, "error out", true, pilot + " - no error in"),
            "Actor Core" => new Pane("Actor Core",
                [new("error in", 8, true), new(cin, 11, true), new("Actor out", 3, false), new("error out", 0, false)],
                cin, null, "error in", "error out", true, pilot + " - `Actor out` is typed Actor"),
            "Stop Core" => new Pane("Stop Core",
                [new(cin, 11, true), new("final error code", 10, true), new(cout, 3, false)],
                cin, cout, null, null, true, pilot + " - no error terminals"),
            "Handle Error" => new Pane("Handle Error",
                [new("error in (no error)", 8, true), new(cin, 11, true), new(cout, 3, false),
                 new("error out", 0, false), new("stop actor?", 2, false)],
                cin, cout, "error in (no error)", "error out", true, pilot),
            "Handle Last Ack Core" => new Pane("Handle Last Ack Core",
                [new("error in (no error)", 4, true), new("Last Ack", 6, true), new(cin, 7, true),
                 new(cout, 3, false), new("error out", 0, false)],
                cin, cout, "error in (no error)", "error out", true,
                "read offline from production code, then measured 2026-10-10 on a throwaway actor's " +
                "override (lvai_connector_pane: pattern 4812, these five terminals)"),
            _ => null,
        };
    }

    /// <summary>
    /// The pane assumed for an override of a PLAIN class method: the parent's own, which by NI's
    /// convention is class in/out plus the error pair. Assumed, never measured here - the read-back
    /// of the override's panel names is what confirms or refuses it.
    /// </summary>
    internal static Pane PlainPane(string className) => new("(plain class method)",
        [new($"{className} in", 11, true), new("error in", 8, true),
         new($"{className} out", 3, false), new("error out", 0, false)],
        $"{className} in", $"{className} out", "error in", "error out", false,
        "assumed NI convention for a plain parent method - checked against the override's panel");

    // ------------------------------------------------------------------ hook shapes

    internal enum Shape { Branch, BranchFromNode, Series, SeriesAfter }

    internal static Shape? ParseShape(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "branch" or "a" => Shape.Branch,
        "branchfromnode" or "c" => Shape.BranchFromNode,
        "series" or "b" => Shape.Series,
        "seriesafter" or "b-after" => Shape.SeriesAfter,
        _ => null,
    };

    internal static string Name(Shape shape) => shape switch
    {
        Shape.Branch => "branch",
        Shape.BranchFromNode => "branchFromNode",
        Shape.Series => "series",
        _ => "seriesAfter",
    };

    /// <summary>What the caller asked for, with every name already defaulted.</summary>
    internal sealed record HookRequest(
        Shape Shape, Pane Pane, bool? WireError,
        string HookClassIn, string HookClassOut, string HookErrorIn, string HookErrorOut,
        string? SourceHook, string SourceClassOut, string SourceErrorOut,
        bool AllowHandleErrorBeforeCallParent);

    /// <summary>A series insert: the wire leaving <c>Panel</c> is cut, Panel -> Near, Far -> old far end.</summary>
    internal sealed record SeriesWire(string Panel, string Near, string Far);

    internal sealed record PairWire(string From, string To);

    internal sealed record Problem(string Kind, string Message);

    internal sealed class HookPlan
    {
        public List<SeriesWire> Series { get; } = [];
        public List<PairWire> Branch { get; } = [];
        public List<PairWire> Node { get; } = [];
        public List<Problem> Problems { get; } = [];
        public List<string> Warnings { get; } = [];

        /// <summary>The hook terminals the plan wires, inputs and outputs alike.</summary>
        public IEnumerable<string> HookSide =>
            Series.SelectMany(s => new[] { s.Near, s.Far })
                .Concat(Branch.Select(b => b.To)).Concat(Node.Select(n => n.To));

        public int WireCount => Series.Count + Branch.Count + Node.Count;

        public JsonObject ToJson() => new()
        {
            ["series"] = new JsonArray([.. Series.Select(s =>
                (JsonNode)$"cut the wire at '{s.Panel}'; '{s.Panel}' -> hook '{s.Near}'; hook '{s.Far}' -> its old far end")]),
            ["branch"] = new JsonArray([.. Branch.Select(b => (JsonNode)$"'{b.From}' -> hook '{b.To}'")]),
            ["node"] = new JsonArray([.. Node.Select(n => (JsonNode)$"source '{n.From}' -> hook '{n.To}'")]),
            ["wireCount"] = WireCount,
        };
    }

    /// <summary>
    /// The wires for one hook, from the shape and the override's pane alone. Problems that need
    /// no LabVIEW at all are found here: an Actor Core series-after (its class output is typed
    /// Actor), a Handle Error hook before Call Parent, an error wire the pane has no terminal for.
    /// </summary>
    internal static HookPlan Plan(HookRequest r)
    {
        var plan = new HookPlan();
        var pane = r.Pane;
        var method = pane.Method;

        switch (r.Shape)
        {
            case Shape.Branch:
                plan.Branch.Add(new(pane.ClassIn, r.HookClassIn));
                if (r.WireError is true)
                {
                    if (pane.ErrorIn is null)
                        plan.Problems.Add(new("noErrorTerminal",
                            $"{method} has no error input, so a branch hook cannot take its error."));
                    else plan.Branch.Add(new(pane.ErrorIn, r.HookErrorIn));
                }
                if (method == "Handle Error" && r.WireError is true)
                    plan.Warnings.Add("A hook fed from Handle Error's incoming error is skipped at every " +
                        "normal stop: Send Normal Stop arrives there as error 43 (measured). Side work " +
                        "needs its own fresh error chain.");
                if (method == "Pre Launch Init")
                    plan.Warnings.Add("A branch hook on Pre Launch Init has no path to its error out, so " +
                        "a failure in it cannot fail the launch. A creating body belongs in seriesAfter.");
                break;

            case Shape.BranchFromNode:
                if (string.IsNullOrWhiteSpace(r.SourceHook))
                    plan.Problems.Add(new("badArguments",
                        "branchFromNode taps the output of a hook already on the diagram: pass sourceHookName " +
                        "(the series hook's VI name or file name)."));
                plan.Node.Add(new(r.SourceClassOut, r.HookClassIn));
                if (r.WireError is true) plan.Node.Add(new(r.SourceErrorOut, r.HookErrorIn));
                break;

            case Shape.Series:
                if (method == "Handle Error")
                {
                    if (!r.AllowHandleErrorBeforeCallParent)
                        plan.Problems.Add(new("handleErrorHookBeforeCallParent",
                            "A hook in series BEFORE Handle Error's Call Parent sees error 43 at every " +
                            "normal stop (1608 at an emergency stop), so a hook chained on that error does " +
                            "no work then, and it cannot keep the actor alive: the parent returns " +
                            "`stop actor?` TRUE whatever the hook did. Use seriesAfter - the parent has " +
                            "already cleared 43 there and code after it can overrule `stop actor?`. Pass " +
                            "allowHandleErrorBeforeCallParent only to keep an existing shape."));
                    else
                        plan.Warnings.Add("Handle Error hook placed BEFORE Call Parent on request: it must " +
                            "pass 43 and 1608 through untouched, and it is skipped at every normal stop.");
                }
                plan.Series.Add(new(pane.ClassIn, r.HookClassIn, r.HookClassOut));
                AddErrorSeries(plan, r, pane.ErrorIn, r.HookErrorIn, r.HookErrorOut, "input");
                if (method == "Pre Launch Init")
                    plan.Warnings.Add("Pre Launch Init has no error in, so a hook before its Call Parent " +
                        "carries the class only and its error has no path to a failed launch. A body " +
                        "that creates resources belongs in seriesAfter, its error into PLI's error out " +
                        "(measured: error 5000 there came out of Launch).");
                if (method == "Actor Core" && plan.Series.Count > 1)
                    plan.Warnings.Add("An error from a series hook into Actor Core's Call Parent skips " +
                        "Handle Error, stops the actor at once and reaches no caller: Launch reports 0 " +
                        "and the next Send fails with 1556 (measured). Create resources in Pre Launch Init.");
                break;

            case Shape.SeriesAfter:
                if (pane.ClassOut is null)
                    plan.Problems.Add(new("actorOutIsActorTyped",
                        $"{method}'s class output is `Actor out`, typed Actor rather than the child " +
                        "class, so a child-typed hook cannot sit between Call Parent and it. Code after " +
                        "Actor Core's Call Parent takes the class wire from BEFORE Call Parent, in a " +
                        "flat sequence (the kit's shape D), which this tool does not build."));
                else
                {
                    plan.Series.Add(new(pane.ClassOut, r.HookClassOut, r.HookClassIn));
                    AddErrorSeries(plan, r, pane.ErrorOut, r.HookErrorOut, r.HookErrorIn, "output");
                }
                if (method == "Stop Core")
                    plan.Warnings.Add("Stop Core has no error terminals, so the hook's error out goes " +
                        "nowhere: feed it to a log VI or Clear Errors.vi with a branchFromNode hook. " +
                        "(seriesAfter on Stop Core, class only: measured 2026-10-10 - state 1, and the hook " +
                        "ran after its Call Parent at the stop.)");
                if (method == "Handle Error")
                    plan.Warnings.Add("`stop actor?` passes from Call Parent untouched; the hook sees the " +
                        "error the parent returns (code 0 at a Normal Stop: the parent has cleared 43 - " +
                        "measured 2026-10-10).");
                if (method == "Handle Last Ack Core")
                    plan.Warnings.Add("Handle Last Ack Core: the pane is measured, a hook shape on it is " +
                        "UNMEASURED.");
                break;
        }

        if (!pane.Measured)
            plan.Warnings.Add($"The {method} pane used here is not measured: {pane.Evidence}.");
        return plan;
    }

    private static void AddErrorSeries(HookPlan plan, HookRequest r, string? paneError,
                                       string near, string far, string side)
    {
        // DEFAULT: wire the error through when the pane has the terminal. Asked for explicitly on
        // a pane without one is a mistake worth saying, not a silent class-only insert.
        if (r.WireError is false) return;
        if (paneError is null)
        {
            if (r.WireError is true)
                plan.Problems.Add(new("noErrorTerminal",
                    $"{r.Pane.Method} has no error {side}, so wireError cannot be honoured."));
            return;
        }
        plan.Series.Add(new(paneError, near, far));
    }

    /// <summary>
    /// The checks that need the hook's terminals and the override's diagram as read by the dry
    /// run. <paramref name="panelWireEnds"/> maps a panel terminal to the terminal count of the
    /// wire leaving it - 2 is one plain wire, which is the only thing a series insert may cut.
    /// </summary>
    internal static void Check(HookPlan plan, HookRequest r,
        IReadOnlyList<ViTerminals.Terminal> hookInputs, IReadOnlyList<ViTerminals.Terminal> hookOutputs,
        IReadOnlyList<string>? panelNames, IReadOnlyDictionary<string, int>? panelWireEnds)
    {
        var inputs = hookInputs.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var outputs = hookOutputs.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var all = string.Join(", ", inputs.Concat(outputs));

        foreach (var name in plan.HookSide.Distinct(StringComparer.Ordinal))
            if (!inputs.Contains(name) && !outputs.Contains(name))
                plan.Problems.Add(new("hookTerminalMissing",
                    $"The hook has no terminal '{name}'. It has: {all}. Name its class terminals with " +
                    "hookClassIn / hookClassOut and its error pair with hookErrorIn / hookErrorOut."));

        // DIRECTION: a series near/far pair must be one input and one output of the hook, in the
        // order the shape needs, or the insert wires an output into an output.
        var before = r.Shape == Shape.Series;
        foreach (var s in plan.Series)
        {
            var (wantIn, wantOut) = before ? (s.Near, s.Far) : (s.Far, s.Near);
            if (outputs.Contains(wantIn) && !inputs.Contains(wantIn))
                plan.Problems.Add(new("hookTerminalWrongDirection", $"Hook terminal '{wantIn}' is an output; this shape needs an input there."));
            if (inputs.Contains(wantOut) && !outputs.Contains(wantOut))
                plan.Problems.Add(new("hookTerminalWrongDirection", $"Hook terminal '{wantOut}' is an input; this shape needs an output there."));
        }
        foreach (var to in plan.Branch.Select(b => b.To).Concat(plan.Node.Select(n => n.To)))
            if (outputs.Contains(to) && !inputs.Contains(to))
                plan.Problems.Add(new("hookTerminalWrongDirection", $"Hook terminal '{to}' is an output; a branch feeds an input."));

        // §19a ROW 1: one unwired REQUIRED input breaks the override, and only a cold exec state
        // shows it - the helper's wires are the only ones the hook will ever get.
        var fed = plan.HookSide.Where(inputs.Contains).ToHashSet(StringComparer.Ordinal);
        var unfed = hookInputs.Where(t => !fed.Contains(t.Name)).ToList();
        foreach (var t in unfed.Where(t => t.Connection == "required"))
            plan.Problems.Add(new("hookInputUnfed",
                $"The hook's REQUIRED input '{t.Name}' is not fed by any wire this shape makes, and an " +
                "unwired required input breaks the override (measured; only a cold exec state shows it). " +
                "Give the hook a pane whose inputs this shape feeds, or make that input recommended."));
        foreach (var t in unfed.Where(t => t.Connection != "required"))
            plan.Warnings.Add($"Hook input '{t.Name}' stays unwired and runs on its default.");

        if (panelNames is not null)
        {
            var panel = panelNames.ToHashSet(StringComparer.Ordinal);
            foreach (var name in plan.Series.Select(s => s.Panel).Concat(plan.Branch.Select(b => b.From)))
                if (!panel.Contains(name))
                    plan.Problems.Add(new("overrideTerminalMissing",
                        $"The override has no panel terminal '{name}'. It has: {string.Join(", ", panelNames)}. " +
                        "Its class terminals are named after the class; pass classTerminalPrefix if they are not."));
        }

        if (panelWireEnds is not null)
            foreach (var s in plan.Series)
            {
                if (!panelWireEnds.TryGetValue(s.Panel, out var ends)) continue;
                if (ends < 2)
                    plan.Problems.Add(new("panelTerminalNotWired",
                        $"Nothing is wired at '{s.Panel}', so there is no wire to insert the hook into."));
                else if (ends > 2)
                    plan.Problems.Add(new("wireIsBranched",
                        $"The wire at '{s.Panel}' already branches ({ends} ends). A series insert deletes " +
                        "that whole net and reconnects one end only, so every other branch would be cut. " +
                        "Place series hooks first, then branches (the kit's order B, C, A)."));
            }
    }

    /// <summary>
    /// The entry of <paramref name="listed"/> (subVI VI Names as LabVIEW reports them) that means
    /// <paramref name="wanted"/>: the exact spelling, a qualified name ending in it, or the same
    /// file name. Every match is returned so the caller can refuse an ambiguous one.
    /// </summary>
    internal static List<string> MatchSubVi(IEnumerable<string> listed, string wanted)
    {
        var bare = Path.GetFileName(wanted.Trim());
        if (bare.Contains(':')) bare = bare[(bare.LastIndexOf(':') + 1)..];
        if (!bare.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)) bare += ".vi";
        return [.. listed.Where(l =>
            string.Equals(l, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(l, bare, StringComparison.OrdinalIgnoreCase)
            || l.EndsWith(":" + bare, StringComparison.OrdinalIgnoreCase))];
    }
}
