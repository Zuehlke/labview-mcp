using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Pointing an Event Structure frame's EVENT DATA NODE at the payload of the user event it handles.
///
/// WHY THIS IS A TOOL. A user event CARRIES DATA and the handler READS it - that is the default
/// shape of the construct, not an advanced one. `ConvertAIXMLToVI` discards the field selection, so
/// until this existed the default shape was the one thing the generator could not produce, and two
/// sessions in a row designed around it: one pulled the payload out of front-panel Local Variables
/// (a different value that looks right) and one built a demo that deliberately never read it.
///
/// THE ROUTE IS THREE CALLS AND THIS IS THE THIRD:
///
///   lvai_generate_vi_with_events   the VI, with every frame registered
///   lvai_wire_dynamic_events       the wire AIXML cannot express, then the user-event spec
///   lvai_set_event_data_fields     this - the data node's rows
///
/// WHAT IT DOES, in one extract/rebuild cycle:
///
///   1. pylv_extract
///   2. pylv-strip-compiled.py   NOT optional. lvai_set_vi_icon - which belongs between steps 2
///                               and 3 - makes LabVIEW compile and SAVE, and pylabview copies
///                               compiled code through unparsed, so without this the rebuild
///                               carries code describing the diagram from before the edit.
///   3. pylv-set-event-data-fields.py, once per Event Data Node
///   4. pylv_rebuild
///   5. exec_state, ON A COPY - see below
///
/// IT VERIFIES ON A COPY, and that is the measurement rather than caution. By the time this runs
/// LabVIEW has loaded the VI (the two calls before it both do), and `pylv_rebuild` writes the file
/// happily while LabVIEW keeps serving its own in-memory copy - so reading the just-written path
/// back confirms the VI you REPLACED. The copy is made beside the original so relative subVI links
/// still resolve, read, and deleted. Measured 2026-09-11: a copy whose internal `_name` differs
/// from its file name loads and runs normally, so the check is sound.
/// </summary>
[McpServerToolType]
internal sealed class EventDataFieldTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_set_event_data_fields", Destructive = true, OpenWorld = true,
                   Title = "Read a user event's payload in its handler frame")]
    [Description("""
        MUTATING: points an Event Structure frame's EVENT DATA NODE at the payload of the user event
        it handles, so the frame READS what the event carried. That is the default shape of a user
        event, and it is the one thing lvai_generate_vi_with_events cannot produce: LabVIEW's
        importer discards an Event Data Node's field selection, because at conversion time the frame
        carries no registration and only Source, Type and Time exist.
        CALL IT THIRD - after lvai_generate_vi_with_events and lvai_wire_dynamic_events, and after
        lvai_set_vi_icon if you want an icon. Earlier is wasted: a regeneration replaces the diagram.
        AUTHOR A PLACEHOLDER FIRST. This takes over an EXISTING wire and never makes one, so the
        AIXML must wire a labelled constant (`_name="Payload Placeholder"`) into the node that
        should consume the field. THE SINK MUST BE A PRIMITIVE OR SUBVI INPUT: a front-panel
        indicator terminal and a tunnel carry no type in the block diagram, and the row takes its
        type from the sink.
        FIELD INDEX: 4 is the FIRST PAYLOAD ITEM - 0 Source, 1 Type, 2 Time, 3 UsrEvtRef, then 4 and
        5 for a two-element cluster payload, and 4 for a scalar one. Measured; an earlier note
        calling 3 "the payload cluster" was wrong and produced an event-refnum terminal.
        TWO FIELDS PER FRAME IS THE CEILING. A converted frame has three rows and the Source row
        carries no field index to rewrite; the other two are repointed and stop showing Type and
        Time. Read anything further off the payload cluster downstream.
        A CONSTANT PLACEHOLDER IS KEPT on the diagram, unwired, which is legal - deleting one was
        measured as `LabVIEW load error code 6: Could not load block diagram`. A NODE placeholder is
        deleted, because an unwired Local Variable is a broken VI. The helper decides from what it
        finds; you do not pass a flag.
        `ok: false` is never a rollback - the .vi on disk carries whatever got as far as it got.
        """)]
    public async Task<string> SetEventDataFieldsAsync(
        [Description("Absolute path to the .vi to edit - it is REBUILT in place")] string viPath,
        [Description("""
            One line per Event Data Node: `<dataNodeUid> <placeholderUid>:<fieldIndex> [...]`,
            for example `4244 4280:4`. The uids are the ones YOUR AIXML wrote - for a constant
            LabVIEW keeps it on an inner object and renumbers the outer one, and the helper resolves
            that, so you never read a heap.
            """)]
        string fields,
        [Description("Read execState back afterwards, from a copy LabVIEW has never loaded")]
        bool verify = true,
        [Description("""
            Where to put the extracted bundle. Defaults to a fresh temp directory, deleted on
            success. Pass a path to keep it - useful when an edit needs debugging.
            """)]
        string? bundleDirectory = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            if (PyLabview.Locate() is not { } bundle)
                return Json.Error("notProvisioned", PyLabview.NotProvisionedMessage());

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory",
                    "No scripts folder next to the exe - lvai_status reports it as " +
                    "scriptsDirectory. The helper scripts this tool drives live there.");

            // Before anything is extracted: a refusal here costs nothing and names the line.
            var reading = EventDataFieldRequests.Read(fields);
            if (reading.Refusal is not null)
                return Json.Error("badArguments", reading.Refusal);

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();
            var keepBundle = bundleDirectory is not null;
            var directory = Path.GetFullPath(bundleDirectory ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "eventfields",
                $"{Path.GetFileNameWithoutExtension(viPath)}-{Environment.ProcessId}-{total.GetHashCode():x}"));

            var extract = await new PyLabviewTools(connection).ExtractAsync(
                viPath, directory, annotate: false, timeoutSeconds, ct: ct);
            steps.Add(Step("extract", extract));
            if (Field(extract, "mainXml") is not { } mainXml)
                return Outcome(false, "extract", steps, total, viPath, directory, true,
                    "pylabview could not read the VI, so nothing was changed.");

            var baseName = Path.GetFileNameWithoutExtension(mainXml);

            // The strip is unconditional, and it is what makes an icon-then-fields order safe:
            // lvai_set_vi_icon leaves compiled code behind, and pylabview would copy it through
            // describing the PRE-edit diagram.
            var strip = await EventStructureTools.RunScriptAsync(
                bundle, scripts, "pylv-strip-compiled.py", [directory, baseName],
                "strip", timeoutSeconds, ct);
            steps.Add(strip);
            if (strip["exitCode"]?.GetValue<int>() != 0)
                return Outcome(false, "strip", steps, total, viPath, directory, true,
                    "Stripping the compiled code failed, so nothing was edited. The bundle is kept.");

            foreach (var request in reading.Requests)
            {
                var step = await EventStructureTools.RunScriptAsync(
                    bundle, scripts, "pylv-set-event-data-fields.py",
                    [directory, baseName, .. request.ScriptArguments],
                    $"fields[{request.DataNodeUid}]", timeoutSeconds, ct);
                steps.Add(step);
                if (step["exitCode"]?.GetValue<int>() != 0)
                    return Outcome(false, $"fields[{request.DataNodeUid}]", steps, total, viPath,
                        directory, true,
                        $"Repointing the rows of Event Data Node {request.DataNodeUid} failed, so " +
                        "the VI was NOT rebuilt and the file on disk is untouched. The script's " +
                        "own message names what it could not find - the usual causes are a " +
                        "placeholder with no wire, or a sink that carries no type because it is a " +
                        "front-panel terminal or a tunnel rather than a primitive input.");
            }

            var rebuild = await new PyLabviewTools(connection).RebuildAsync(
                mainXml, viPath, timeoutSeconds, ct: ct);
            steps.Add(Step("rebuild", rebuild));
            if (Field(rebuild, "ok") is not "true")
                return Outcome(false, "rebuild", steps, total, viPath, directory, true,
                    "Every row was repointed and the rebuild failed, so the .vi on disk is the " +
                    "one from before. The bundle is kept.");

            if (!verify)
                return Outcome(true, null, steps, total, viPath, directory, keepBundle,
                    "Rows repointed and the VI rebuilt. NOT VERIFIED - nothing has asked LabVIEW " +
                    "whether the result is executable, and a wrong field index produces a VI that " +
                    "loads and is eBad. Run lvai_exec_state on a COPY at a path LabVIEW has never " +
                    "loaded, because it is still serving its own in-memory copy of this one.");

            // The copy sits beside the original so relative subVI links still resolve.
            var copy = Path.Combine(Path.GetDirectoryName(viPath)!,
                Path.GetFileNameWithoutExtension(viPath) + ".lvmcp-verify.vi");
            JsonNode? state = null;
            try
            {
                File.Copy(viPath, copy, overwrite: true);
                var answer = await new ExecStateTools(connection).ExecStateAsync(
                    copy, helperAixmlPath: null, helperViPath: null, regenerateHelper: false,
                    timeoutSeconds, ct: ct);
                state = Parsed(answer);
                steps.Add(new JsonObject { ["step"] = "verify (on a copy)", ["answer"] = state });
            }
            catch (IOException bad)
            {
                steps.Add(new JsonObject
                {
                    ["step"] = "verify (on a copy)",
                    ["error"] = $"Could not copy the VI to verify it: {bad.Message}",
                });
            }
            finally
            {
                try { File.Delete(copy); } catch (IOException) { }
            }

            var execState = state?["execState"]?.GetValue<int>();
            if (execState is 1)
                return Outcome(true, null, steps, total, viPath, directory, keepBundle,
                    $"Repointed {reading.Requests.Sum(r => r.Takes.Count)} row(s) over " +
                    $"{reading.Requests.Count} Event Data Node(s), and LabVIEW can run the result. " +
                    "AIXML-export the VI and read `fields=` to see the field NAMES it resolved - " +
                    "that is the only check that the index you gave is the field you meant.");

            return Outcome(false, "verify", steps, total, viPath, directory, keepBundle,
                execState is 0
                    ? "The rows were repointed and the rebuild succeeded - and LabVIEW says the " +
                      "result is NOT EXECUTABLE. The first thing to suspect is the FIELD INDEX: " +
                      "the row now emits whatever field you named, and 3 is the user event's " +
                      "refnum rather than its payload. AIXML-export the VI and read `fields=` - " +
                      "it prints the name LabVIEW resolved, which says immediately whether the " +
                      "index was the one you meant."
                    : "The verify step could not open a reference to the copy, so this says " +
                      "NOTHING about the VI - read its `code` and `source`. `load error code 6, " +
                      "Could not load block diagram` means the edit produced a diagram LabVIEW " +
                      "will not load at all, which is what deleting a constant placeholder does.");
        });

    // ---------------------------------------------------------------- plumbing

    private static JsonObject Step(string name, string answer) => new()
    {
        ["step"] = name,
        ["answer"] = Parsed(answer),
    };

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                  string viPath, string directory, bool keepBundle, string note)
    {
        if (!keepBundle && ok)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }

        return new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["viPath"] = viPath,
            ["bundleDirectory"] = ok && !keepBundle ? null : directory,
            ["steps"] = steps,
            ["totalElapsedMs"] = total.ElapsedMilliseconds,
            ["note"] = note,
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? Parsed(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(answer); }
    }

    private static string? Field(string answer, string key) =>
        Parsed(answer)?[key]?.ToString();
}
