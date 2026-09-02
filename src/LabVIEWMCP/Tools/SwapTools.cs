using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Repointing subVI nodes and diagram constants through LabVIEW's own <c>Replace</c>, many at a time.
///
/// WHY THIS EXISTS RATHER THAN A LOOP IN THE CALLER. Measured 2026-08-29 building a Caraya suite for
/// a three-class hierarchy: 19 of the run's 44 <c>lvai_run_vi_and_read_values</c> calls were driving
/// this one operation, alternating a list call and a replace call almost every time. That
/// alternation is forced from outside - <c>{LV.Diagram}</c> <c>SubVIs[]</c> re-orders after every
/// <c>Replace</c> and the replaced node's reference dies - so an index read before a swap means a
/// different node after it. Inside one helper run the ordering problem does not arise: the
/// references and their names are collected once, and each swap is addressed by name.
///
/// WHY <c>Replace</c> AND NOT A PYLABVIEW RETARGET. A retarget rewrites the link record only, so it
/// needs the two connector panes to be type-identical and answers <c>Error 7, Bad Linkage</c>
/// otherwise. <c>Replace</c> is the IDE's own "Replace with a VI from disk": it relinks AND
/// **re-types the wires**, which is the single property that lets a generated test reach class code
/// at all. AIXML cannot author a class-typed terminal, so the test is authored against a socket
/// whose class terminals are <c>path</c> stand-ins and the real accessor is swapped in afterwards.
///
/// THE ORDER IS NOT ARBITRARY: nodes first, constants last. With the nodes already swapped the wire
/// into a class constant has a class sink and <c>Replace</c> re-types it; the other way round the
/// wire has a class source and a path sink and simply breaks. The helper enforces it.
/// </summary>
[McpServerToolType]
internal sealed class SwapTools(LvaiConnection connection)
{
    /// <summary>Name of the swap helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_swap_subvis.xml";

    [McpServerTool(Name = "lvai_swap_subvis", Destructive = true, OpenWorld = true,
                   Title = "Repoint many subVI nodes and class constants on one diagram")]
    [Description("""
        MUTATING: repoints subVI nodes and diagram constants on ONE block diagram through LabVIEW's
        own {LV.SubVI} / {LV.Constant} Replace, all in a single run, then saves the VI in place.
        THIS IS HOW A GENERATED VI COMES TO CALL CLASS CODE. AIXML refuses a class-typed terminal, so
        no generated Call can name an accessor and lvai_placeholder_subvi answers `stubRefused`.
        Replace is the IDE's own "Replace with a VI from disk" and RE-TYPES THE WIRES, which a
        pylabview link retarget cannot - so a test authored against sockets whose class terminals are
        `path` stand-ins becomes a test that calls the real accessors statically.
        swapsJson is a JSON ARRAY of nodes to repoint:
          [{"socket":"LVMCP Acc W1.vi","target":"C:\\cls\\Write Hersteller.vi"}]
        constantsJson is a JSON ARRAY of constants to turn into class constants:
          [{"label":"seed 1","class":"C:\\cls\\Netzteil.lvclass"}]
        NODES ARE SWAPPED FIRST AND CONSTANTS LAST, always. A dynamic dispatch input is a REQUIRED
        terminal, so a class chain needs a class value; with the nodes already swapped that wire has
        a class sink and Replace re-types it, where the other order breaks it.
        EVERY SOCKET NAME MUST BE UNIQUE on the diagram, and this refuses duplicates rather than
        letting them through: matching is by VI Name, so two nodes calling the same socket are
        indistinguishable and the wrong subject lands in the wrong case WITH NO ERROR AT ALL. A name
        that is not on the diagram is refused for the same reason - the helper's array search answers
        -1 and Index Array then clamps to element 0, silently swapping a node you did not name.
        NOTHING IS READ BACK FROM A REPLACED OBJECT, because the reference does not survive it -
        Error 1055 - and that error would travel down the wire and stop the save. `verify` therefore
        re-exports the VI through LabVIEW afterwards and reports its call targets; that export is the
        only real proof, and `socketsLeft` above zero means a swap did not land.
        """)]
    public async Task<string> SwapSubVisAsync(
        [Description(@"Absolute path to the .vi to edit - it is SAVED IN PLACE")] string viPath,
        [Description(@"JSON array of {socket, target} node swaps. Omit to swap no nodes.")]
        string? swapsJson = null,
        [Description(@"JSON array of {label, class} constant swaps. Omit to swap no constants.")]
        string? constantsJson = null,
        [Description("Re-export the VI afterwards and report what it now calls")]
        bool verify = true,
        [Description("""
            Keep every sub-answer whole. OFF by default because the full ones are large for no
            benefit: measured 2026-09-01, six swaps in one message returned about 34 kB, most of it
            LabVIEW's entire AIXML export inline plus a flattened per-node value dump. What a caller
            reads - callTargets, nodesSwapped, socketsLeft - is lifted out already. A step that
            FAILED is reported whole regardless of this flag.
            """)]
        bool verbose = false,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description($"The helper's AIXML source; defaults to {HelperAixmlFileName} in scriptsDirectory")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            List<NodeSwap> swaps;
            List<ConstantSwap> constants;
            try
            {
                swaps = NodeSwap.ParseAll(swapsJson);
                constants = ConstantSwap.ParseAll(constantsJson);
            }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            if (swaps.Count == 0 && constants.Count == 0)
                return Json.Error("badArguments",
                    "Neither swapsJson nor constantsJson names anything to do.");

            // A duplicate socket name is the silent-wrong-node case, so it is refused rather than
            // reported afterwards - afterwards there is nothing to see.
            if (swaps.GroupBy(s => s.Socket, StringComparer.OrdinalIgnoreCase)
                     .FirstOrDefault(g => g.Count() > 1) is { } repeated)
                return Json.Error("badArguments",
                    $"'{repeated.Key}' is named {repeated.Count()} times in swapsJson. Matching is " +
                    "by VI Name, so two nodes calling the same socket cannot be told apart and the " +
                    "wrong subject would land in the wrong place with no error. Generate one socket " +
                    "per slot.");

            // A pipe separates the helper's lists and is illegal in a Windows path, so a value
            // carrying one would silently split into two.
            if (swaps.SelectMany(s => new[] { s.Socket, s.Target })
                     .Concat(constants.SelectMany(c => new[] { c.Label, c.ClassPath }))
                     .FirstOrDefault(v => v.Contains('|')) is { } piped)
                return Json.Error("badArguments",
                    $"'{piped}' contains a '|', which is what separates the helper's lists.");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperAixmlFileName) : null);
            if (aixml is null || !File.Exists(aixml))
                return Json.Error("helperMissing",
                    $"The helper's AIXML source could not be located ({HelperAixmlFileName} in the " +
                    "folder lvai_status reports as scriptsDirectory). Pass helperAixmlPath.");

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_swap_subvis.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    aixml, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "helper", ["answer"] = Json.Slim(Parse(built), verbose) });
                if (!File.Exists(helperVi))
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = "helper",
                        ["steps"] = steps,
                        ["note"] = "The swap helper itself could not be generated, so nothing was " +
                                   "changed. Its AIXML is at " + aixml + ".",
                    });
                helperGenerated = true;
            }

            // EMPTY VALUES ARE OMITTED, not sent blank: the runner pairs names and values by
            // position and refuses an empty one outright, because a blank would shift every later
            // input onto the wrong control. An unset control keeps its own (empty) default, which
            // is exactly "swap nothing of this kind".
            var inputs = new JsonObject { ["vi path"] = Path.GetFullPath(viPath) };
            if (swaps.Count > 0)
            {
                inputs["socket names"] = string.Join("|", swaps.Select(s => s.Socket));
                inputs["target paths"] = string.Join("|", swaps.Select(s => s.Target));
            }
            if (constants.Count > 0)
            {
                inputs["constant labels"] = string.Join("|", constants.Select(c => c.Label));
                inputs["constant paths"] = string.Join("|", constants.Select(c => c.ClassPath));
            }

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "swap", ["answer"] = Json.Slim(Parse(answer), verbose) });

            var values = (Parse(answer) as JsonObject)?["values"] as JsonObject;
            var code = Scalar(values, "code");
            var source = Scalar(values, "source");
            var swapped = code == "0";

            // The names the diagram actually had, so an unmatched socket is visible rather than
            // silently swapping element 0.
            var present = Names(values, "node names found");
            var missing = swaps.Select(s => s.Socket)
                               .Where(n => !present.Contains(n, StringComparer.OrdinalIgnoreCase))
                               .ToList();

            JsonNode? targets = null;
            var socketsLeft = -1;
            if (swapped && verify)
            {
                var exportPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                    Path.ChangeExtension(Path.GetFileName(viPath), ".swap-verify.xml"));
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
                // TIMED HERE, BY THE COMPOSER. The callee does not always carry an elapsedMs on
                // this path, so the slimmed sub-answer had none - and a step with no duration
                // cannot be chosen against when picking the next thing to optimise, which is the
                // whole method this repository uses. A composing tool always knows how long its
                // own step took, so it reports it rather than hoping the callee did.
                var verifyWall = Stopwatch.StartNew();
                var exported = await new AixmlTools(connection).ConvertViToAixmlAsync(
                    viPath, exportPath, returnContent: true, maxContentChars: 0, timeoutSeconds,
                    refresh: true, ct);
                steps.Add(new JsonObject
                {
                    ["step"] = "verify",
                    ["elapsedMs"] = verifyWall.ElapsedMilliseconds,
                    ["answer"] = Json.Slim(Parse(exported), verbose),
                });

                if ((Parse(exported) as JsonObject)?["xml"]?.GetValue<string>() is { } xml)
                {
                    socketsLeft = swaps.Count(s => xml.Contains(s.Socket, StringComparison.Ordinal));
                    targets = new JsonArray([.. Targets(xml).Select(t => (JsonNode)t!)]);
                }

                try { File.Delete(exportPath); }
                catch (Exception failure) when (failure is IOException
                                                or UnauthorizedAccessException) { }
            }

            var ok = swapped && missing.Count == 0 && socketsLeft <= 0;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["viPath"] = Path.GetFullPath(viPath),
                ["nodesSwapped"] = swaps.Count,
                ["constantsSwapped"] = constants.Count,
                ["socketsLeft"] = socketsLeft < 0 ? null : socketsLeft,
                ["socketsNotOnDiagram"] = missing.Count == 0
                    ? null : new JsonArray([.. missing.Select(m => (JsonNode)m!)]),
                ["callTargets"] = targets,
                ["helperGenerated"] = helperGenerated,
                ["errorCode"] = code,
                ["errorSource"] = source,
                ["steps"] = steps,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = Note(ok, swapped, missing, socketsLeft, verify),
            });
        });

    private static string Note(bool ok, bool swapped, List<string> missing, int socketsLeft,
                               bool verify)
    {
        if (!swapped)
            return "The helper reported an error and the VI was NOT saved - a Replace that fails " +
                   "leaves its error on the wire, which also stops Save.Instrument. Read the swap " +
                   "step's `source`.";
        if (missing.Count > 0)
            return "The VI was saved, but " + string.Join(", ", missing.Select(m => $"'{m}'")) +
                   " is not among the diagram's subVI names, so the helper's array search answered " +
                   "-1 and Index Array clamped to element 0 - A NODE YOU DID NOT NAME WAS SWAPPED. " +
                   "Regenerate the VI and swap again with names taken from `node names found`.";
        if (socketsLeft > 0)
            return $"{socketsLeft} socket name(s) are STILL in LabVIEW's own export of the VI, so " +
                   "that many swaps did not land. The export is the only proof; read `callTargets`.";
        return verify
            ? "Swapped and verified against LabVIEW's own export - no socket name survives in it, " +
              "and `callTargets` is what the diagram now calls."
            : "Swapped and saved. NOTHING HERE PROVES IT LANDED: verify was false, and a Replace " +
              "cannot be read back from the object it replaced. Export the VI and read target=.";
    }

    /// <summary>Every <c>target="…"</c> in an AIXML export, which is what the diagram now calls.</summary>
    internal static List<string> Targets(string xml) =>
        [.. System.Text.RegularExpressions.Regex
            .Matches(xml, "target=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)];

    internal sealed record NodeSwap(string Socket, string Target)
    {
        public static List<NodeSwap> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            var array = Array(json, "swapsJson");
            return [.. array.Select((entry, i) =>
            {
                var o = Object(entry, i, "swapsJson");
                var socket = Text(o, "socket", i, "swapsJson");
                var target = Text(o, "target", i, "swapsJson");
                return new NodeSwap(socket, Path.GetFullPath(target));
            })];
        }
    }

    internal sealed record ConstantSwap(string Label, string ClassPath)
    {
        public static List<ConstantSwap> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            var array = Array(json, "constantsJson");
            return [.. array.Select((entry, i) =>
            {
                var o = Object(entry, i, "constantsJson");
                var label = Text(o, "label", i, "constantsJson");
                var cls = Text(o, "class", i, "constantsJson");
                return new ConstantSwap(label, Path.GetFullPath(cls));
            })];
        }
    }

    private static JsonArray Array(string json, string argument)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch (JsonException bad)
        { throw new ArgumentException($"{argument} is not JSON: {bad.Message}"); }

        return parsed as JsonArray
            ?? throw new ArgumentException($"{argument} must be a JSON array of objects.");
    }

    private static JsonObject Object(JsonNode? entry, int index, string argument) =>
        entry as JsonObject
        ?? throw new ArgumentException($"{argument}[{index}] is not an object.");

    private static string Text(JsonObject o, string key, int index, string argument) =>
        o[key]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"{argument}[{index}] has no \"{key}\".");

    private static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    /// <summary>The helper returns arrays as flattened XML, so the values are read out of that.</summary>
    private static List<string> Names(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["xml"]?.GetValue<string>() is { } xml
            ? [.. System.Text.RegularExpressions.Regex.Matches(xml, "<Val>([^<]*)</Val>")
                   .Select(m => m.Groups[1].Value)]
            : [];

    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return null; }
    }
}
