using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Several runs of one VI in one lvai_run_vi_and_read_values call (`inputSetsJson`).
///
/// WHY: measured 2026-11-18 over 94 agent transcripts, the tool was called 406 times and 71 % of
/// those calls followed another call to itself - one model turn (about 7 s) per input set, with the
/// same notes and helper paths in every answer. The batch runs the sets in order and answers one
/// compact entry per run; a run that failed keeps its whole answer, because that is the one a
/// caller has to read.
/// </summary>
internal static class RunBatch
{
    /// <summary>
    /// Below the client's patience (about 60 s) with room for one answer to travel. A between-runs
    /// check cannot protect a budget above that limit (docs/workflow-economics.md, accessor slices).
    /// </summary>
    internal const int BudgetSeconds = 40;

    internal static List<JsonObject> ParseInputSets(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { throw new ArgumentException($"inputSetsJson is not valid JSON: {e.Message}"); }
        if (node is not JsonArray array || array.Count == 0)
            throw new ArgumentException(
                "inputSetsJson must be a non-empty JSON array of input objects, e.g. [{\"x\":\"1\"},{\"x\":\"2\"}].");
        var sets = new List<JsonObject>();
        for (var i = 0; i < array.Count; i++)
            sets.Add(array[i] is JsonObject o
                ? (JsonObject)o.DeepClone()
                : throw new ArgumentException(
                    $"inputSetsJson[{i}] is not an object of label -> text. An empty object {{}} runs the VI on its defaults."));
        return sets;
    }

    internal static bool NextRunWouldOverrun(long elapsedMs, long lastRunMs) =>
        elapsedMs + lastRunMs >= BudgetSeconds * 1000L;

    /// <summary>
    /// One run as the batch reports it. A clean run: its inputs and `values` as label -> native
    /// value. A value that carries a problem or kept its XML goes whole under `valueDetails`. A
    /// run that failed (an error answer, helperFailed, a non-zero errorCode) keeps its full answer
    /// under `answer`.
    /// </summary>
    internal static JsonObject CompactRun(int index, JsonObject inputs, JsonObject? answer)
    {
        var run = new JsonObject { ["set"] = index, ["inputs"] = inputs.DeepClone() };
        if (answer is null)
        {
            run["ok"] = false;
            run["error"] = "The run answered no JSON object.";
            return run;
        }

        var failed = answer["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var ok) && !ok
                     || Flag(answer, "helperFailed")
                     || Number(answer, "errorCode") is not (null or 0);
        run["ok"] = !failed;
        if (failed)
        {
            run["answer"] = answer.DeepClone();
            return run;
        }

        var values = new JsonObject();
        var details = new JsonObject();
        if (answer["values"] is JsonObject source)
            foreach (var (name, entry) in source)
            {
                if (entry is JsonObject e && e["problem"] is null && e["xmlKeptBecause"] is null && e["xml"] is null)
                    values[name] = e["value"]?.DeepClone();
                else
                    details[name] = entry?.DeepClone();
            }
        run["values"] = values;
        if (details.Count > 0) run["valueDetails"] = details;
        foreach (var key in new[] { "signals", "disabled", "targetEndedBeforeAbort", "lineBreaksEncoded", "elapsedMs", "runMs" })
            if (answer[key] is { } kept) run[key] = kept.DeepClone();
        return run;
    }

    internal static JsonObject Answer(string viPath, int setCount, JsonArray runs, int? nextSet, long elapsedMs)
    {
        var failed = runs.Count(r => r?["ok"]?.GetValue<bool>() == false);
        var answer = new JsonObject
        {
            ["ok"] = failed == 0 && nextSet is null,
            ["viPath"] = viPath,
            ["sets"] = setCount,
            ["runsDone"] = runs.Count,
            ["runsFailed"] = failed,
            ["runs"] = runs,
            ["elapsedMs"] = elapsedMs,
        };
        if (nextSet is { } next)
        {
            answer["nextSet"] = next;
            answer["next"] = $"Stopped before the client's time limit. Call again with the sets from index {next} on.";
        }
        answer["note"] = "One entry per input set, in order. `values` is label -> native value; a value that " +
                         "kept its XML or has a problem is whole under `valueDetails`; a failed run keeps its " +
                         "full answer under `answer`. A target VI's own error is in its `error out` under values.";
        return answer;
    }

    private static bool Flag(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static long? Number(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
}
