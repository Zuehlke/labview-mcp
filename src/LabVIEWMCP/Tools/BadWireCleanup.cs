// ============================================================================================
// BadWireCleanup - the shared last step that clears broken wires a scripted edit left behind
// and says exactly which wires went and which connections, if any, went with them.
// ============================================================================================
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Runs <c>scripts/lvbd_remove_bad_wires.xml</c> on a VI after a scripted edit and reports the
/// result as one answer field, <c>badWireCleanup</c>. Shared by <c>lvai_swap_subvis</c>,
/// <c>lvai_bind_typedef_constants</c> and <c>lvai_add_class_method</c>.
///
/// WHY IT EXISTS. A <c>Replace</c> - of a subVI, a constant or a pane control - re-types the
/// wires it can and leaves the others BROKEN, and a VI with one broken wire reads state 0. Every
/// one of those tools answered green over such a VI, and the remedy was a hand step in the IDE.
/// The user's rule of 2026-10-11: a VI at state 0 with a broken-wire text gets Remove Bad Wires
/// before any rebuild. <c>lvai_af_remove_hook</c> and <c>lvai_graft_diagram</c> already run
/// <c>BD.Remove Bad Wires</c> inside their own helpers; this makes it the default last step of the
/// three Replace-based tools, and adds the half those two do not have - an account of what went.
///
/// WHAT DECIDES <c>ok</c>. Removing a wire can take a real connection with it - two sources on
/// one input are one broken wire, and clearing it leaves the input unwired. So the helper records
/// every sink of a broken wire that HAD a source and reads it back afterwards; one that carries no
/// wire is listed in <c>inputsLeftUnwired</c>, and the calling tool's <c>ok</c> turns false
/// (<c>errorKind: badWireCleanupLostConnection</c>). A wire with no source end is a loose
/// fragment: clearing it loses nothing, so it is reported in <c>brokenWiresRemoved</c> and gates
/// nothing. A cleanup that could not RUN is a warning, not a verdict, because the edit before it
/// may well be sound.
///
/// MEASURED 2026-10-17 through lvai_swap_subvis on both backends - a lost connection reported, a
/// clean diagram left unsaved (CLAUDE.md, "A VI AT STATE 0"). Through lvai_add_class_method and on
/// a class member it is not measured yet.
/// </summary>
internal sealed class BadWireCleanup(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvbd_remove_bad_wires.xml";

    /// <summary>The calling tool's <c>errorKind</c> when the cleanup took a connection away.</summary>
    internal const string LostConnectionKind = "badWireCleanupLostConnection";

    /// <summary>
    /// Where NI's converter writes the helper - the grpc folder of
    /// <see cref="AixmlBackend.HelperFolder"/>; on pylabview the helper is COMPOSED into
    /// <see cref="ComposedHelpers.Folder"/> instead, so neither route runs the other's VI.
    /// </summary>
    internal static string DefaultHelperViPath() =>
        Path.Combine(AixmlBackend.HelperFolder(), "lvbd_remove_bad_wires.vi");

    /// <summary>
    /// Clear the broken wires on <paramref name="viPath"/> and describe the result. Never throws
    /// for a LabVIEW-side failure: the step that ran before it is the caller's verdict, and a
    /// cleanup that cannot run is reported as such.
    /// </summary>
    internal async Task<JsonObject> RunAsync(string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return NotRun("helperMissing", "No scripts folder next to the exe.");
            var aixml = Path.Combine(scripts, HelperAixmlFileName);
            if (!File.Exists(aixml))
                return NotRun("helperMissing", $"No {HelperAixmlFileName} in '{scripts}'.");

            // THE BACKEND OF THE CALLING TOOL, both for building the helper and for running it:
            // on pylabview no step may reach NI's service, so the helper is COMPOSED and run over
            // ActiveX - the pattern DiagramCleanup uses. The fork this came from built it with
            // NI's converter on every route.
            var backend = BackendRoute.Current;
            string helperVi;
            var generated = false;
            if (backend == AixmlBackend.PyLabview)
            {
                var (composed, problem) = await ComposedHelpers.EnsureAsync(HelperAixmlFileName, timeoutSeconds, ct);
                if (composed is null) return NotRun("helperNotGenerated", "Could not compose the helper: " + problem);
                helperVi = composed;
            }
            else
            {
                helperVi = DefaultHelperViPath();
                Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
                if (HelperCache.NeedsRebuild(aixml, helperVi))
                {
                    if (await GenerateAsync(aixml, helperVi, timeoutSeconds, ct) is { } failure)
                        return NotRun("helperNotGenerated", failure);
                    generated = true;
                }
            }

            var full = Path.GetFullPath(viPath);
            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, new JsonObject { ["vi path"] = full }.ToJsonString(),
                includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, backend: backend, ct: ct);
            var values = (Parse(answer) as JsonObject)?["values"] as JsonObject;

            // THE STATE AFTER is read only when something was removed: it is the question this
            // step exists to answer, and on a VI with no broken wire nothing here changed it.
            ExecStateReading? after = null;
            if (values is not null && Scalar(values, "code") == "0" && Lines(values, "Broken Wires").Count > 0)
                after = await new ExecStateTools(connection).ReadAsync(
                    full, helperAixmlPath: null, helperViPath: null, regenerateHelper: false,
                    timeoutSeconds, ct);

            var result = Describe(values, after);
            result["helperGenerated"] = generated;
            result["elapsedMs"] = elapsed.ElapsedMilliseconds;
            return result;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return NotRun("cleanupFailed", failure.Message);
        }
    }

    /// <summary>The field for a cleanup that was deliberately not run.</summary>
    internal static JsonObject Skipped(string reason) =>
        new() { ["ran"] = false, ["skipped"] = true, ["reason"] = reason };

    private static JsonObject NotRun(string kind, string why) =>
        new() { ["ran"] = false, ["skipped"] = false, ["errorKind"] = kind, ["reason"] = why };

    /// <summary>
    /// The helper's reply as this step's answer. Kept apart from the RPC work so the verdict - the
    /// one thing that changes a calling tool's <c>ok</c> - is testable with no LabVIEW.
    /// </summary>
    internal static JsonObject Describe(JsonObject? values, ExecStateReading? after)
    {
        if (values is null)
            return NotRun("helperAnswerUnreadable",
                "The cleanup helper returned nothing readable, so nothing is known about broken wires.");

        var code = Scalar(values, "code");
        var removed = Lines(values, "Broken Wires").Select(FormatWireLine).ToList();
        var unwired = Lines(values, "Inputs Left Unwired").Select(TrimEnd).ToList();
        var unchecked_ = Lines(values, "Inputs Unchecked").Select(TrimEnd).ToList();
        var saved = Scalar(values, "Saved") is "1" or "true" or "TRUE" or "True";

        var result = new JsonObject
        {
            ["ran"] = true,
            ["brokenWires"] = removed.Count,
            ["brokenWiresRemoved"] = Array(removed),
            ["inputsLeftUnwired"] = Array(unwired),
            ["inputsUnchecked"] = unchecked_.Count == 0 ? null : Array(unchecked_),
            ["wiresSeen"] = int.TryParse(Scalar(values, "Wires Seen"), out var seen) ? seen : null,
            ["saved"] = saved,
            ["execStateAfter"] = after?.State,
            ["execStateMeaning"] = after is null ? null : ExecStateTools.Meaning(after.State),
            ["errorCode"] = int.TryParse(code, out var c) ? c : null,
            ["errorSource"] = Scalar(values, "source") is { Length: > 0 } s ? s : null,
            ["errorKind"] = code == "1055" ? "noActiveProject" : null,
        };

        result["note"] = code is not ("0" or null)
            ? (code == "1055"
                ? "NO PROJECT IS ACTIVE - Error 1055. The cleanup edits the copy the IDE's project " +
                  "holds, so nothing was removed and nothing saved."
                : "The cleanup helper reported an error; read errorSource. Whatever it removed in " +
                  "memory was NOT saved unless `saved` says so.")
            : removed.Count == 0
                ? "No broken wire on the diagram - nothing was removed and the VI was not saved."
                : unwired.Count > 0
                    ? $"{removed.Count} broken wire(s) removed and the VI saved, and {unwired.Count} " +
                      "input(s) that a broken wire fed from a source are now UNWIRED - a connection " +
                      "was lost. A broken wire with two sources, or a source of the wrong type, " +
                      "cannot be kept, so the edit before this step did not produce the diagram it " +
                      "meant to. Read brokenWiresRemoved for which ends met."
                    : $"{removed.Count} broken wire(s) removed and the VI saved; no input that had a " +
                      "source lost its wire. A line starting `(no source)` was a loose fragment.";
        return result;
    }

    /// <summary>True when the cleanup took a real connection away - the one case that fails ok.</summary>
    internal static bool LostConnection(JsonObject? cleanup) =>
        cleanup?["inputsLeftUnwired"] is JsonArray { Count: > 0 };

    /// <summary>
    /// Put the cleanup on a calling tool's answer: <c>badWireCleanup</c> always; on a lost
    /// connection <c>ok</c> false and an <c>errorKind</c> unless one is already set; on a cleanup
    /// that could not run, a top-level warning, because a field nobody scrolls to is no warning.
    /// </summary>
    internal static void Attach(JsonObject answer, JsonObject cleanup)
    {
        answer["badWireCleanup"] = cleanup;
        if (LostConnection(cleanup))
        {
            answer["ok"] = false;
            if (answer["errorKind"] is null) answer["errorKind"] = LostConnectionKind;
            answer["badWireCleanupWarning"] =
                "The broken-wire cleanup left " + string.Join(", ",
                    ((JsonArray)cleanup["inputsLeftUnwired"]!).Select(n => n!.GetValue<string>())) +
                " unwired - those inputs had a wire from a source before it. Read badWireCleanup.";
        }
        else if (cleanup["ran"]?.GetValue<bool>() is false && cleanup["skipped"]?.GetValue<bool>() is false
                 || cleanup["errorCode"] is JsonValue code && code.GetValue<int>() != 0)
        {
            answer["badWireCleanupWarning"] =
                "The broken-wire cleanup did not complete, so this answer says nothing about broken " +
                "wires left on the diagram. Read badWireCleanup; lvai_exec_state answers whether the " +
                "VI is executable.";
        }
    }

    /// <summary>
    /// One helper line as the answer prints it. The helper writes every end as
    /// <c>name @ owner; </c>, sources first, then <c>-&gt; </c>, then the sinks, so
    /// <c>x @ Add; -&gt; y @ Multiply; z @ Subtract; </c> becomes
    /// <c>x @ Add -&gt; y @ Multiply, z @ Subtract</c>. A side with no end at all reads
    /// <c>(no source)</c> or <c>(no sink)</c> - the first is a loose fragment.
    /// </summary>
    internal static string FormatWireLine(string raw)
    {
        var arrow = raw.IndexOf("->", StringComparison.Ordinal);
        var (sources, sinks) = arrow < 0 ? (raw, "") : (raw[..arrow], raw[(arrow + 2)..]);
        return $"{Side(sources, "(no source)")} -> {Side(sinks, "(no sink)")}";

        static string Side(string ends, string none)
        {
            var parts = ends.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? none : string.Join(", ", parts);
        }
    }

    private static string TrimEnd(string name) => name.Trim().TrimEnd(';').Trim();

    /// <summary>
    /// A string array the helper returned as flattened XML, honouring <c>Dimsize</c>: an EMPTY
    /// LabVIEW array still flattens one template element with an empty <c>Val</c>, which a plain
    /// element count would report as one entry (see <see cref="LvValuesXml"/>).
    /// </summary>
    internal static List<string> Lines(JsonObject values, string name)
    {
        if ((values[name] as JsonObject)?["xml"]?.GetValue<string>() is not { Length: > 0 } xml) return [];
        XElement root;
        try { root = XElement.Parse(xml); }
        catch (XmlException) { return []; }
        if (root.Element("Dimsize") is { } size && size.Value.Trim() == "0") return [];
        return [.. root.Descendants("Val").Select(v => v.Value).Where(v => v.Trim().Length > 0)];
    }

    private static string? Scalar(JsonObject values, string name) =>
        (values[name] as JsonObject)?["value"]?.GetValue<string>();

    private static JsonArray Array(IEnumerable<string> items) =>
        new([.. items.Select(i => (JsonNode)i!)]);

    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Validate, then convert - the same two steps every cached helper here takes.</summary>
    private async Task<string?> GenerateAsync(string aixml, string helperVi, int timeoutSeconds,
                                              CancellationToken ct)
    {
        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return $"The helper AIXML does not validate: {validation.ErrorMessage}";

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = helperVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        return generation.ErrorCode == 0 && File.Exists(helperVi)
            ? null
            : $"Could not generate the helper VI at '{helperVi}': {generation.ErrorMessage}";
    }
}
