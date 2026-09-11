using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Generating a VI whose Event Structure is actually REGISTERED, in one call.
///
/// WHY THIS IS ITS OWN TOOL AND NOT A FLAG ON lvai_generate_vi. It deliberately SKIPS validation.
/// `ValidateAIXML` refuses any document with a static event frame - `Event Structure: One or more
/// event cases have no events defined` - while `ConvertAIXMLToVI` on the same bytes answers
/// `errorCode 0` and writes the VI. Measured on both of NI's shipping design-pattern templates and
/// on documents authored from scratch. So the composition lvai_generate_vi makes (validate, then
/// convert, then check the pane) cannot reach this at all, and a flag that turned its first step
/// off would make its contract mean two different things.
///
/// WHAT THE CONVERSION LOSES, and why the rest of this chain exists. The frames survive - every
/// `CaseFrame` becomes a real frame, and `diagramList`, `dataNodeList` and `filterNodeList` all
/// keep their count - but `EventNodeEvents` is collapsed to a single Timeout spec, so every other
/// frame comes back with an EMPTY selector and LabVIEW calls the VI not executable. Restoring it is
/// mechanical, and this is that mechanism:
///
///   1. read the frames off the AIXML selectors        (EventFrames)
///   2. ConvertAIXMLToVI, without validating
///   3. pylv_extract
///   4. pylv-strip-compiled.py    the generator EMITS compiled code, and pylabview copies it
///                                through unparsed, so it would describe the pre-edit diagram.
///                                Without this the result is `Error 47, Unknown heap`.
///   5. pylv-show-dynamic-events.py  the terminals a Register For Events refnum wires to are
///                                HIDDEN by default, so a generated VI has no connection point
///                                for one. Unwired they are inert, so this is unconditional.
///   6. pylv-set-event-spec.py    once per frame - the EventSpec AND the cached frame label
///   7. pylv_rebuild
///   8. exec_state
///
/// THREE DEFECTS THIS TOOL EXISTS TO MAKE IMPOSSIBLE, all of them measured by hand-driving the
/// route on 2026-09-10 and none of them visible to an AIXML export, a render or `execState`:
///
///   * writing the EventSpec and NOT `selString`, which leaves the IDE showing an event case with
///     no event assigned while the VI runs correctly. Three green checks; only a person reading
///     the IDE found it.
///   * matching that cached label up to the next quote instead of up to `&lt;/text&gt;`. The text
///     contains raw quotes, so three calls appended into one run-on selector.
///   * passing AIXML through a shell, which eats `\3A` as an octal escape and surfaces as an XML
///     parse error somewhere else entirely.
///
/// WHAT IS STILL NOT AUTOMATED, deliberately, because neither is mechanical: the Event Data Node's
/// FIELD SELECTION does not survive the round trip (every node comes back `Source,Type,Time`, so
/// `NewVal` has to be read from the control's terminal instead), and a clipped diagram comment is
/// visible only in a rendered picture. Both are reported as notes rather than silently ignored.
/// </summary>
[McpServerToolType]
internal sealed class EventStructureTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_generate_vi_with_events", Destructive = true, OpenWorld = true,
                   Title = "Generate a VI and register its front-panel events")]
    [Description("""
        MUTATING: generates a VI from AIXML and REGISTERS its Event Structure's front-panel events,
        which lvai_generate_vi cannot do. Use it for any document whose Event Structure has a frame
        other than Timeout.
        IT SKIPS VALIDATION ON PURPOSE. ValidateAIXML refuses every static event frame ("one or
        more event cases have no events defined") while ConvertAIXMLToVI on the same bytes answers
        errorCode 0 and writes the VI - measured on both NI design-pattern templates and on
        documents authored from scratch. So run lvai_check_aixml first if you want a cheap
        pre-filter; nothing here type-checks your wiring, and `ok` says the events are registered
        and LabVIEW can run the result, not that the diagram is right.
        NO EVENT MAPPING ARGUMENT, because the document already carries it: a selector spells its
        control and its trigger (` "Setpoint"\3A Value Change `), and a second mapping would just be
        somewhere for the two to disagree. Frames are registered in document order - position IS
        `diagramIdx`. A `Timeout` frame is left alone; it needs no registration.
        AN UNKNOWN TRIGGER IS REFUSED BY NAME rather than written as a Value Change, and so is a
        document with two Event Structures - `diagramIdx` is a position within ONE structure, so
        mapping two of them would be a guess.
        TWO THINGS IT CANNOT DO FOR YOU, both reported as notes. The Event Data Node's field
        selection does NOT survive conversion - every node comes back `Source,Type,Time`, so a
        frame that needs `NewVal` must read the control's TERMINAL instead, which is what NI's own
        templates do. And a diagram comment too long for its box is cut off in silence; only
        lvai_render_diagrams shows that.
        `ok: false` is never a rollback - the .vi on disk carries whatever got as far as it got, and
        `steps` holds each sub-answer whole.
        """)]
    public async Task<string> GenerateViWithEventsAsync(
        [Description("Absolute path to the source AIXML .xml file")] string aiXmlFilePath,
        [Description("Absolute path of the .vi to create - WILL BE OVERWRITTEN")] string viPath,
        [Description("Read execState back afterwards, which is the only check that sees a frame left unregistered")]
        bool verify = true,
        [Description("""
            Where to put the extracted bundle. Defaults to a fresh temp directory, deleted on
            success. Pass a path to keep it - useful when a spec write needs debugging.
            """)]
        string? bundleDirectory = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(aiXmlFilePath))
                return Json.Error("badArguments", $"No file at aiXmlFilePath '{aiXmlFilePath}'.");

            if (PyLabview.Locate() is not { } bundle)
                return Json.Error("notProvisioned", PyLabview.NotProvisionedMessage());

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory",
                    "No scripts folder next to the exe - lvai_status reports it as " +
                    "scriptsDirectory. The helper scripts this tool drives live there.");

            // 1. the frames, off the selectors. Before anything is written: a refusal here costs
            //    nothing, and every refusal names the frame it is about.
            var reading = EventFrames.Read(aiXmlFilePath);
            if (reading.Refusal is not null)
                return Json.Error(reading.RefusalKind ?? "badArguments", reading.Refusal);

            var toRegister = reading.Frames.Where(f => f.NeedsRegistration).ToList();
            if (toRegister.Count == 0)
                return Json.Error("nothingToRegister", """
                    Every frame of this Event Structure is a Timeout, which needs no registration -
                    so lvai_generate_vi already does everything this tool would, and it validates
                    and checks the connector pane as well. Use that instead.
                    """);

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();
            var keepBundle = bundleDirectory is not null;
            var directory = Path.GetFullPath(bundleDirectory ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "events",
                $"{Path.GetFileNameWithoutExtension(viPath)}-{Environment.ProcessId}-{total.GetHashCode():x}"));

            var frameList = new JsonArray();
            foreach (var frame in reading.Frames)
                frameList.Add(new JsonObject
                {
                    ["diagramIdx"] = frame.Index,
                    ["control"] = frame.Control,
                    ["userEvent"] = frame.UserEvent,
                    ["trigger"] = frame.Trigger,
                });

            // 2. convert, deliberately WITHOUT validating
            var convert = await new AixmlTools(connection).ConvertAixmlToViAsync(
                aiXmlFilePath, viPath, openVI: false, timeoutSeconds, ct: ct);
            steps.Add(Step("convert", convert));
            if (ErrorCode(convert) is not 0)
                return Outcome(false, "convert", steps, frameList, total, viPath, directory, true,
                    "ConvertAIXMLToVI refused the document, so nothing else ran. Note that this " +
                    "tool skips validation, so the message here is the generator's own and may be " +
                    "terser than lvai_validate_aixml would be - run lvai_check_aixml for the " +
                    "faults the validator misses.");

            // 3. extract
            var extract = await new PyLabviewTools(connection).ExtractAsync(
                viPath, directory, annotate: false, timeoutSeconds, ct: ct);
            steps.Add(Step("extract", extract));
            if (Field(extract, "mainXml") is not { } mainXml)
                return Outcome(false, "extract", steps, frameList, total, viPath, directory, true,
                    "The VI was written but pylabview could not read it back, so its events are " +
                    "NOT registered and LabVIEW will call it not executable.");

            // pylabview sanitises the base name, so take it from the file it reported
            var baseName = Path.GetFileNameWithoutExtension(mainXml);

            // 4. the strip. Without it the rebuild carries compiled code describing the diagram
            //    BEFORE the spec writes, and the result answers Error 47, Unknown heap.
            var strip = await RunScriptAsync(bundle, scripts, "pylv-strip-compiled.py",
                [directory, baseName], "strip", timeoutSeconds, ct);
            steps.Add(strip);
            if (strip["exitCode"]?.GetValue<int>() != 0)
                return Outcome(false, "strip", steps, frameList, total, viPath, directory, true,
                    "Stripping the compiled code failed, so the events were not registered. The " +
                    "bundle is kept.");

            // 4b. show the DYNAMIC EVENT TERMINALS, always. They are where a Register For Events
            //     refnum is wired and they are HIDDEN by default, so a generated VI has no
            //     connection point for one until somebody right-clicks the structure in the IDE.
            //     An unwired terminal is inert - NI ships its own templates with them hidden but
            //     present - so this costs nothing and removes a manual gesture from the route.
            //     The user's suggestion of 2026-09-10, measured as a clean A/B: one working VI,
            //     the terminals toggled in the IDE, frame and spec counts equal on both sides, and
            //     the script's output is FLAG-IDENTICAL to the gesture's plus an identical render.
            //     Not gated on an option: a hidden terminal has no upside worth a parameter.
            var show = await RunScriptAsync(bundle, scripts, "pylv-show-dynamic-events.py",
                [directory, baseName], "showDynamicTerminals", timeoutSeconds, ct);
            steps.Add(show);
            if (show["exitCode"]?.GetValue<int>() != 0)
                return Outcome(false, "showDynamicTerminals", steps, frameList, total, viPath,
                    directory, true,
                    "Showing the dynamic event terminals failed, so the events were not " +
                    "registered. The bundle is kept.");

            // 5. one spec per frame, in document order. The script resolves the control's ddoUID
            //    from its LABEL itself, so a renamed or misspelled control fails by name here
            //    rather than registering an event on whatever happened to be at that uid.
            foreach (var frame in toRegister)
            {
                var what = frame.Control ?? $"<{frame.UserEvent}>";
                var step = await RunScriptAsync(bundle, scripts, "pylv-set-event-spec.py",
                    [directory, baseName, frame.Index.ToString(), .. frame.SpecArguments],
                    $"register[{frame.Index}] {what}", timeoutSeconds, ct);
                steps.Add(step);
                if (step["exitCode"]?.GetValue<int>() != 0)
                    return Outcome(false, $"register[{frame.Index}]", steps, frameList, total,
                        viPath, directory, true,
                        $"Registering frame {frame.Index} ('{what}') failed, so the rebuild was " +
                        "NOT run and the .vi on disk still has its events stripped. " +
                        (frame.Control is not null
                            ? "The usual cause is that no front-panel control carries that " +
                              "label - the script lists the labels it did find."
                            : "A user-event frame needs no control, so the cause is in the " +
                              "bundle rather than in a label - read the script's output."));
            }

            // 6. rebuild
            var rebuild = await new PyLabviewTools(connection).RebuildAsync(
                mainXml, viPath, timeoutSeconds, ct: ct);
            steps.Add(Step("rebuild", rebuild));
            if (ErrorCode(rebuild) is not 0 && Field(rebuild, "ok") is not "true")
                return Outcome(false, "rebuild", steps, frameList, total, viPath, directory, true,
                    "Every event registered but the rebuild failed, so the .vi on disk is the " +
                    "unregistered one. The bundle is kept.");

            // 7. verify. execState is the ONLY check in this chain that sees an unregistered
            //    frame: an AIXML export flattens a single-frame structure and omits the selector
            //    entirely, so it cannot be used as the gate.
            JsonObject? verifyStep = null;
            if (verify)
            {
                var state = await new ExecStateTools(connection).ExecStateAsync(
                    viPath, helperAixmlPath: null, helperViPath: null, regenerateHelper: false,
                    timeoutSeconds, ct: ct);
                verifyStep = Step("verify", state);
                steps.Add(verifyStep);
            }

            var broken = Field(steps[^1]?["answer"]?.ToJsonString() ?? "", "broken") == "true";
            var userEvents = toRegister.Count(f => f.UserEvent is not null);

            // A USER-EVENT FRAME CANNOT BE FINISHED BY THIS TOOL ALONE, and saying so here is the
            // difference between a two-call fix and a day of archaeology.
            //
            // MEASURED 2026-09-11, end to end, twice. LabVIEW NORMALISES a user-event EventSpec
            // when it LOADS the VI, against the dynamic-event wire present in the FILE at that
            // moment. The wire is what AIXML cannot express, so at this point in the pipeline it
            // does not exist yet - and LabVIEW therefore throws the spec away on its next load:
            // `type` 1000 -> 0, `dynIndex` 1 -> 2, and the frame label back to
            // `<#2>: Unknown Event (0x0)`. The spec written above is real in the file and inert.
            //
            // So the route is THREE steps and this is the first: generate here, then
            // lvai_wire_dynamic_events (which saves, discarding this spec), then write the spec
            // AGAIN onto the now-wired file and rebuild - at which point LabVIEW keeps it and the
            // VI is executable. Both measured VIs went eBad -> eIdle on exactly that third step.
            if (verify && broken && userEvents > 0)
                return Outcome(false, "verify", steps, frameList, total, viPath, directory,
                    keepBundle,
                    $"The {userEvents} user-event frame(s) are NOT FINISHED, and that is expected " +
                    "at this step rather than a fault in your diagram. A user event fires through " +
                    "the event registration refnum, and the wire from it onto the Event " +
                    "Structure's DYNAMIC EVENT TERMINAL is the one thing AIXML cannot express - so " +
                    "it is not in the file yet, and LabVIEW discards a user-event spec it cannot " +
                    "resolve on its next load. NEXT: ONE CALL - lvai_wire_dynamic_events on " +
                    "this VI. It makes the wire and then finishes the frame itself: the spec is " +
                    "written again onto the wired file and rebuilt, which is the only order " +
                    "LabVIEW keeps, and it reads execState back. The static frames above are " +
                    "already done.");

            if (verify && broken)
                return Outcome(false, "verify", steps, frameList, total, viPath, directory,
                    keepBundle,
                    "Every event registered and the rebuild succeeded - and LabVIEW says the " +
                    "result is NOT EXECUTABLE. Nothing was rolled back. Read linkerErrors in the " +
                    "verify step: at this point the cause is the DIAGRAM rather than the events, " +
                    "because validation was skipped and nothing here type-checks your wiring.");

            return Outcome(true, null, steps, frameList, total, viPath, directory, keepBundle,
                $"Registered {toRegister.Count} event(s) - " +
                $"{toRegister.Count(f => f.Control is not null)} front-panel, " +
                $"{userEvents} user event(s). " +
                "LabVIEW can run the result. " +
                "TWO THINGS THIS DOES NOT TELL YOU. Every Event Data Node came back as " +
                "`Source,Type,Time` - conversion drops the field selection - so a frame needing " +
                "`NewVal` must read the control's terminal instead. And a diagram comment too " +
                "long for its box is cut off in silence: call lvai_render_diagrams and look, " +
                "which is the only check that sees it.");
        });

    // ---------------------------------------------------------------- plumbing

    private static JsonObject Step(string name, string answer) => new()
    {
        ["step"] = name,
        ["answer"] = Parsed(answer),
    };

    /// <summary>Run one helper script over a bundle. Internal because
    /// <see cref="WireEventTools"/> drives the same scripts for the step that can only
    /// happen after the dynamic-event wire exists; a third copy of this is how they
    /// would drift.</summary>
    internal static async Task<JsonObject> RunScriptAsync(
        PyLabview.Bundle bundle, string scriptsDirectory, string script, string[] args,
        string label, int timeoutSeconds, CancellationToken ct)
    {
        var path = Path.Combine(scriptsDirectory, script);
        if (!File.Exists(path))
            return new JsonObject
            {
                ["step"] = label,
                ["exitCode"] = -1,
                ["stderr"] = $"No helper script at '{path}'. It ships under scripts\\ next to the " +
                             "exe; a source checkout has it under scripts\\ in the repository.",
            };

        var run = await PyLabview.RunAsync(bundle, path, args, Rpc.ClampToolWait(timeoutSeconds), ct);
        return new JsonObject
        {
            ["step"] = label,
            ["script"] = script,
            ["exitCode"] = run.ExitCode,
            ["stdout"] = run.StdOut.TrimEnd(),
            ["stderr"] = run.StdErr.Length == 0 ? null : run.StdErr.TrimEnd(),
            ["elapsedMs"] = run.ElapsedMs,
        };
    }

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, JsonArray frames,
                                  Stopwatch total, string viPath, string directory,
                                  bool keepBundle, string note)
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
            ["viExistsNow"] = File.Exists(viPath),
            ["eventFrames"] = frames,
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

    private static int? ErrorCode(string answer) =>
        Parsed(answer)?["errorCode"]?.GetValue<int>();

    private static string? Field(string answer, string key) =>
        Parsed(answer)?[key]?.ToString();
}
