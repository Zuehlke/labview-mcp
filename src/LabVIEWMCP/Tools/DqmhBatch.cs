using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Several DQMH events (lvai_dqmh_new_event `eventsJson`) or handlers (lvai_dqmh_place_handler
/// `handlersJson`) in one call.
///
/// WHY: measured 2026-11-18 over 94 agent transcripts, lvai_dqmh_new_event was called 67 times with
/// 66 % of the calls straight after another one, lvai_dqmh_place_handler 52 times with 71 % - one
/// model turn per event of a module whose events are all known before the first call. Everything
/// here is the pure part: parsing that refuses unknown keys by name, and the compact entry a
/// success is reported as. A failure keeps its whole answer.
/// </summary>
internal static class DqmhBatch
{
    /// <summary>Below the client's 60 s, for a tool that answers in the request itself.</summary>
    internal const int BudgetSeconds = 40;

    internal static bool NextWouldOverrun(long elapsedMs, long lastMs) =>
        elapsedMs + lastMs >= BudgetSeconds * 1000L;

    // ------------------------------------------------------------------ handlers

    internal sealed record Handler(string EventName, string HandlerViPath);

    private static readonly string[] HandlerKeys = ["eventName", "handlerViPath"];

    internal static List<Handler> ParseHandlers(string json)
    {
        var array = ArrayOf(json, "handlersJson", "[{\"eventName\":\"Verify Account\",\"handlerViPath\":\"C:\\\\...\\\\Verify Account Core.vi\"}]");
        var result = new List<Handler>();
        for (var i = 0; i < array.Count; i++)
        {
            var o = ObjectAt(array, i, "handlersJson", HandlerKeys);
            var name = Text(o, "eventName");
            var path = Text(o, "handlerViPath");
            if (name.Length == 0 || path.Length == 0)
                throw new ArgumentException($"handlersJson[{i}] needs both eventName and handlerViPath.");
            if (result.Any(h => string.Equals(h.EventName, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"handlersJson names the event '{name}' twice - a frame takes one handler.");
            result.Add(new Handler(name, path));
        }
        return result;
    }

    internal static JsonObject CompactHandler(Handler handler, JsonObject? answer)
    {
        var entry = new JsonObject { ["event"] = handler.EventName, ["handler"] = handler.HandlerViPath };
        var ok = answer?["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        entry["ok"] = ok;
        if (!ok)
        {
            entry["answer"] = answer?.DeepClone();
            return entry;
        }
        foreach (var key in new[] { "dryRun", "frame", "placement", "connected", "plan", "moduleDataWire", "warnings" })
            if (answer![key] is { } kept) entry[key] = kept.DeepClone();
        return entry;
    }

    internal static JsonObject HandlersAnswer(string moduleName, int requested, JsonArray placed, int? nextHandler,
        bool allOk, bool dryRun, string? mainVi, JsonObject? cleanUp, JsonObject? sourceOnly, int? mainExecState,
        long elapsedMs)
    {
        var finishOk = cleanUp?["ok"]?.GetValue<bool>() != false && sourceOnly?["ok"]?.GetValue<bool>() != false;
        var answer = new JsonObject
        {
            ["ok"] = allOk && nextHandler is null && finishOk,
            ["module"] = moduleName,
            ["handlersRequested"] = requested,
            ["handlersDone"] = placed.Count(e => e?["ok"]?.GetValue<bool>() == true),
            ["handlers"] = placed,
        };
        if (dryRun) answer["dryRun"] = true;
        if (nextHandler is { } next)
        {
            answer["nextHandler"] = next;
            answer["next"] = allOk
                ? $"Stopped before the client's time limit. Call again with the handlers from index {next} on."
                : $"Stopped at the handler that failed (its full answer is under `answer`). Fix it, then call again " +
                  $"with it and the handlers from index {next} on.";
        }
        answer["mainVi"] = mainVi;
        answer["cleanUpDiagram"] = cleanUp;
        answer["sourceOnly"] = sourceOnly;
        answer["mainViExecState"] = mainExecState;
        answer["note"] = dryRun
            ? "Dry run: nothing was changed."
            : mainExecState is 1
                ? "Main.vi is executable. It was laid out and made source only once, after the last handler."
                : "Main.vi is NOT executable after this call. Other frames may still carry #CodeNeeded " +
                  "placeholders - fill every event's frame, then read it again.";
        answer["elapsedMs"] = elapsedMs;
        return answer;
    }

    // ------------------------------------------------------------------ events

    /// <summary>One event of a batch, in the single call's own parameter shapes.</summary>
    internal sealed record Event(string EventName, string ArgumentsJson, string? EventType, string ReplyArgumentsJson,
        string RoundTripBroadcastName, string Description, bool AddTesterButton, string? RequestScoping,
        string? ConsumingLoop);

    private static readonly string[] EventKeys =
        ["eventName", "arguments", "eventType", "replyArguments", "roundTripBroadcastName", "description",
         "addTesterButton", "requestScoping", "consumingLoop"];

    /// <summary>
    /// `arguments` and `replyArguments` are JSON ARRAYS here, not strings holding JSON: a string in
    /// a string costs an escape per quote and was the usual way to get it wrong.
    /// </summary>
    internal static List<Event> ParseEvents(string json)
    {
        var array = ArrayOf(json, "eventsJson",
            "[{\"eventName\":\"Set Gain\",\"arguments\":[{\"name\":\"Gain\",\"type\":\"double\"}]}]");
        var result = new List<Event>();
        for (var i = 0; i < array.Count; i++)
        {
            var o = ObjectAt(array, i, "eventsJson", EventKeys);
            var name = Text(o, "eventName");
            if (name.Length == 0) throw new ArgumentException($"eventsJson[{i}] has no eventName.");
            if (result.Any(e => string.Equals(e.EventName, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"eventsJson names the event '{name}' twice.");
            var tester = true;
            if (o["addTesterButton"] is { } t)
                tester = t is JsonValue tv && tv.TryGetValue<bool>(out var tb)
                    ? tb
                    : throw new ArgumentException($"eventsJson[{i}].addTesterButton must be true or false.");
            result.Add(new Event(name, ArrayText(o, "arguments", i), OptionalText(o, "eventType"),
                ArrayText(o, "replyArguments", i), Text(o, "roundTripBroadcastName"), Text(o, "description"),
                tester, OptionalText(o, "requestScoping"), OptionalText(o, "consumingLoop")));
        }
        return result;
    }

    internal static JsonObject CompactEvent(string eventName, JsonObject? answer)
    {
        var entry = new JsonObject { ["event"] = eventName };
        var ok = answer?["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        entry["ok"] = ok;
        if (!ok)
        {
            entry["answer"] = answer?.DeepClone();
            return entry;
        }
        foreach (var key in new[] { "eventType", "moduleExecutable", "completionNeeded", "mainViExecState",
                                    "testerExecState", "newModuleFiles", "unsavedMembers", "warning", "elapsedMs" })
            if (answer![key] is { } kept && !(kept is JsonArray { Count: 0 })) entry[key] = kept.DeepClone();
        // file NAMES only; a created VI that is not executable keeps its whole entry
        if (answer!["createdFiles"] is JsonArray created)
        {
            entry["createdFiles"] = new JsonArray([.. created.Select(f =>
                (JsonNode?)JsonValue.Create(Path.GetFileName(f?["path"]?.ToString() ?? "")))]);
            var broken = created.Where(f => f?["execState"] is JsonValue s && s.TryGetValue<int>(out var state) && state != 1)
                                .Select(f => f!.DeepClone()).ToArray();
            if (broken.Length > 0) entry["createdNotExecutable"] = new JsonArray(broken);
        }
        return entry;
    }

    internal static JsonObject EventsAnswer(string moduleName, int requested, JsonArray events)
    {
        var done = events.Count(e => e?["ok"]?.GetValue<bool>() == true);
        var answer = new JsonObject
        {
            ["ok"] = done == requested,
            ["module"] = moduleName,
            ["eventsRequested"] = requested,
            ["eventsDone"] = done,
            ["events"] = events,
        };
        if (done < requested)
            answer["next"] = "Stopped at the event that failed - its full answer is under `answer`. The events " +
                             "before it exist; call again with the failed one corrected and those after it.";
        answer["note"] = "One compact entry per event, in order: what Delacor leaves for a person is in each " +
                         "`completionNeeded` (a frame's #CodeNeeded code goes in with lvai_dqmh_place_handler " +
                         "handlersJson).";
        return answer;
    }

    // ------------------------------------------------------------------ shared

    private static JsonArray ArrayOf(string json, string parameter, string example)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { throw new ArgumentException($"{parameter} is not valid JSON: {e.Message}"); }
        return node is JsonArray { Count: > 0 } array
            ? array
            : throw new ArgumentException($"{parameter} must be a non-empty JSON array, e.g. {example}.");
    }

    private static JsonObject ObjectAt(JsonArray array, int i, string parameter, string[] keys)
    {
        if (array[i] is not JsonObject o) throw new ArgumentException($"{parameter}[{i}] is not an object.");
        foreach (var (key, _) in o)
            if (!keys.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"{parameter}[{i}] has an unknown key \"{key}\". Known keys: {string.Join(", ", keys)}.");
        return o;
    }

    private static string Text(JsonObject o, string key) => OptionalText(o, key) ?? "";

    private static string? OptionalText(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    private static string ArrayText(JsonObject o, string key, int i) => o[key] switch
    {
        null => "",
        JsonArray a => a.ToJsonString(),
        _ => throw new ArgumentException(
            $"eventsJson[{i}].{key} must be a JSON array of {{\"name\":...,\"type\":...}} objects, not a string."),
    };
}
