using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The one connection an Event Structure needs and AIXML cannot express: the event registration
/// refnum on the DYNAMIC EVENT terminal.
///
/// WHAT MADE IT REACHABLE WAS A CHANGE TO HOW THE DIAGRAM IS AUTHORED, not a new scripting call.
/// Wire the refnum into the structure as an ordinary TUNNEL - which AIXML keeps across a
/// regeneration - and the only thing left is the short hop from that net onto the terminal beside
/// it. That turns "compose a wire across the diagram", which pylabview cannot do and AIXML cannot
/// describe, into "branch an existing net", which is one invoke node.
///
/// SO THE TUNNEL IS THE SHAPE TO AUTHOR - and it is preferred, not required. With it, the tool
/// BRANCHES that net. Without it, it falls back to the `Register For Events` node on the block
/// diagram and wires straight across the loop border, which LabVIEW accepts and answers by
/// creating the tunnel itself - measured 2026-09-11. `sourceFrom` says which happened, and the
/// fallback assumes ONE registration node in the VI, reporting how many it saw.
///
/// `Auto Route? (F)` IS WIRED TRUE IN THE HELPER AND THAT IS THE WHOLE DIFFERENCE BETWEEN A WIRE
/// AND A GHOST. Measured 2026-09-11 as a controlled pair on one VI: with the FALSE default the
/// branch is present in every readable form - the signal's terminal list, the terminal's bit 15, a
/// grown compressedWireTable, a live `Connected Wire` with three ends, `Is Broken?` false, the VI
/// unbroken - and LabVIEW's own renderer draws NOTHING. `Position` and `Bounds` are identical
/// between the two cases, so no property can tell them apart, and `CleanUpWire` changes neither.
/// Only `Print.VI To HTML` sees it, which is why this tool ends by telling the caller to render.
///
/// IT TURNS A BROKEN VI INTO A RUNNING ONE, so the effect is not cosmetic. Controlled pair, both
/// states rebuilt through the same pylabview round trip: the prepared VI with the dynamic terminal
/// unwired is `Execution:State` 0, `eBad`; with the branch it is 1.
/// </summary>
[McpServerToolType]
internal sealed class WireEventTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_wire_dyn_events.xml";

    /// <summary>
    /// The literal both ends of the refnum net answer to, because the name comes from the DATA
    /// TYPE rather than from a label. It is how the tool finds them without an index - and it is
    /// carried by THREE terminals of the structure: the dynamic input, the dynamic output, and the
    /// tunnel. `Is Source?` separates the output; the input and the tunnel are told apart by
    /// `Terminals[]` order, which mirrors the heap's own term list.
    /// </summary>
    internal const string RefnumTerminalName = "Event Registration Refnum";

    [McpServerTool(Name = "lvai_wire_dynamic_events", Destructive = true, OpenWorld = true,
                   Title = "Wire an event registration refnum onto the dynamic event terminal")]
    [Description("""
        MUTATING: branches a VI's event registration refnum onto its Event Structure's DYNAMIC
        EVENT terminal and saves the VI. That connection is the one thing AIXML cannot express, so
        it is what a regeneration loses - and without it a VI with user-event frames does not run
        at all (measured: Execution:State 0 before, 1 after).
        TWO SOURCES, TRIED IN THAT ORDER. Preferred: the structure's own Event Registration Refnum
        TUNNEL, whose net is branched - author that tunnel in AIXML, because a <Tunnel> on a
        <Structure> survives every regeneration and is what makes the whole route repeatable.
        Fallback: the Register For Events node on the block diagram, wired straight across the loop
        border, which LabVIEW accepts and answers by creating the tunnel itself. `sourceFrom` says
        which was used; the fallback assumes ONE registration node and reports the count it saw.
        BOTH ENDS ARE FOUND BY NAME, never by index: the terminal and the tunnel both answer to
        "Event Registration Refnum". The net's SOURCE is then walked to, because offering the
        structure's own tunnel as the source is Error 1062, "Specified objects cannot be wired
        together" - two sinks have no direction between them.
        VERIFY BY RENDERING, and the tool says this in its own answer: with Auto Route? at its
        FALSE default the connection is real and LabVIEW draws no wire, and NO property tells the
        two apart - Position, Bounds and CleanUpWire are all identical. This helper wires
        Auto Route? TRUE, and lvai_render_diagrams is still the only check that sees the result.
        Render from a path LabVIEW has never loaded: a VI can be loaded twice, and a render of the
        just-edited path has been measured showing the PRE-EDIT diagram from the addon instance's
        own copy.
        THE SEARCH IS TWO LEVELS DEEP - the block diagram, and the diagrams of every WhileLoop,
        ForLoop, TimedLoop and CaseStructure on it. That covers a front-panel event loop as NI's
        templates build one. Anything deeper is reported as not found, with the class names the
        search did see.
        It needs a project OPEN AND ACTIVE in the IDE: the VI is opened through
        Application:Project:Active Project so the edit lands in the copy the user is looking at.
        IT ALSO FINISHES A USER-EVENT FRAME, which nothing else can, and that is why a VI with one
        ends this call EXECUTABLE instead of eBad. LabVIEW normalises a user-event EventSpec when
        it LOADS the VI, against the wire present in the FILE at that moment - so the spec
        lvai_generate_vi_with_events writes is necessarily discarded, by the very save this tool
        performs. Afterwards it sticks, so the spec is written AGAIN here and the VI rebuilt:
        measured 2026-09-11 on two VIs, both eBad -> eIdle on exactly that step. Read
        userEventStep; `changed` says whether there was anything to do.
        THAT STEP CLOSES THE ACTIVE PROJECT, saving it, because pylabview writes the file while
        LabVIEW keeps serving its own copy - so without the release the verification reads the VI
        it replaced. It is skipped entirely, project included, when the VI has no unresolved
        user-event frame, and finishUserEvents: false turns it off.
        IT REFUSES MORE THAN ONE USER-EVENT FRAME rather than guessing: dynIndex is the
        registration item's position and only the value 1 is measured, and a wrong one gives a VI
        that loads, compiles and fires the WRONG event.
        """)]
    public async Task<string> WireDynamicEventsAsync(
        [Description("Absolute path of the .vi to wire. It is SAVED in place on success.")]
        string viPath,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory.
            Generated once and reused, and regenerated when the shipped AIXML is newer.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvai_wire_dyn_events.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists and is current")]
        bool regenerateHelper = false,
        [Description("""
            After wiring, write the VI's USER-EVENT EventSpecs again and rebuild - the step that
            can only happen once the wire exists, because LabVIEW discards a user-event spec it
            cannot resolve when it LOADS the VI. On by default: without it a VI with a user-event
            frame is left eBad, which is not a deliverable. It CLOSES THE ACTIVE PROJECT (saving
            it) to release the VI from LabVIEW's memory, because pylabview writes the file happily
            while LabVIEW keeps serving its own copy. Skipped entirely, project included, when the
            VI has no user-event frame to finish.
            """)]
        bool finishUserEvents = true,
        [Description("Local budget in seconds")]
        int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No file at '{viPath}'.", viPath);

            if (!Path.GetExtension(viPath).Equals(".vi", StringComparison.OrdinalIgnoreCase))
                return Json.Error("targetIsNotAVi",
                    $"'{Path.GetFileName(viPath)}' is not a .vi file. This edits a block diagram, " +
                    "so a .ctl or .lvproj has nothing for it to do.",
                    new { viPath = Path.GetFullPath(viPath), extension = Path.GetExtension(viPath) });

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath());
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var stale = IsStale(aixml, helperVi);
            var helperGenerated = false;
            if (regenerateHelper || stale || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            var bytesBefore = new FileInfo(viPath).Length;
            var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
            request.Inputs["VI Path"] = Path.GetFullPath(viPath);

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            var outputs = response.Outputs;
            var found = Flag(outputs, "found");
            var chainCode = Number(outputs, "code");
            var chainSource = Text(outputs, "source");
            var broken = Flag(outputs, "wire broken");
            var sourceFrom = Text(outputs, "source from");
            var wiredBefore = Flag(outputs, "wired before");
            var endsAfter = Dimsize(Text(outputs, "wire ends after"));
            var topClasses = Strings(Text(outputs, "top classes"));
            var terminalNames = Strings(Text(outputs, "terminal names"));
            var registrationNodes = Dimsize(Text(outputs, "registration nodes"));

            var outcome = Classify(found, chainCode, sourceFrom, wiredBefore, endsAfter, broken);

            var payload = Json.Node(response).AsObject();
            payload.Remove("outputs");

            payload["ok"] = JsonValue.Create(outcome.Ok);
            payload["errorKind"] = outcome.Kind is null ? null : JsonValue.Create(outcome.Kind);
            payload["viPath"] = JsonValue.Create(Path.GetFullPath(viPath));
            payload["eventStructureFound"] = found is null ? null : JsonValue.Create(found);
            payload["alreadyWired"] = JsonValue.Create(outcome.AlreadyWired);
            payload["sourceFrom"] = sourceFrom is null ? null : JsonValue.Create(sourceFrom);
            payload["registrationNodesOnDiagram"] =
                registrationNodes is null ? null : JsonValue.Create(registrationNodes);
            payload["wireEndsAfter"] = endsAfter is null ? null : JsonValue.Create(endsAfter);
            payload["wireBroken"] = broken is null ? null : JsonValue.Create(broken);
            payload["helperErrorCode"] = chainCode is null ? null : JsonValue.Create(chainCode);
            payload["helperErrorSource"] = JsonValue.Create(chainSource);
            payload["refnumTerminalName"] = JsonValue.Create(RefnumTerminalName);
            payload["topLevelClasses"] = Array(topClasses);
            payload["eventStructureTerminals"] = Array(terminalNames);
            payload["viBytesBefore"] = JsonValue.Create(bytesBefore);
            payload["viBytesAfter"] = JsonValue.Create(
                File.Exists(viPath) ? new FileInfo(viPath).Length : 0L);
            payload["helperViPath"] = JsonValue.Create(helperVi);
            payload["helperAixmlPath"] = JsonValue.Create(Path.GetFullPath(aixml));
            payload["helperGenerated"] = JsonValue.Create(helperGenerated);
            payload["helperWasStale"] = JsonValue.Create(stale);
            payload["elapsedMs"] = JsonValue.Create(stopwatch.ElapsedMilliseconds);
            payload["note"] = JsonValue.Create(outcome.Note);
            payload["verifyBy"] = JsonValue.Create(VerifyHint);

            // STEP 3. The wire is in the file now, so - and only now - a user-event EventSpec
            // will survive LabVIEW's next load. See FinishUserEventsAsync for the measurement.
            if (finishUserEvents && outcome.Ok)
            {
                var finish = await FinishUserEventsAsync(viPath, timeoutSeconds, ct);
                payload["userEventStep"] = finish;
                payload["viBytesAfter"] = JsonValue.Create(
                    File.Exists(viPath) ? new FileInfo(viPath).Length : 0L);

                if (finish["ok"]?.GetValue<bool>() is false)
                {
                    // The wire went in, so the VI is further along than it was - and it is still
                    // not executable, and that is what `ok` has to report.
                    payload["ok"] = JsonValue.Create(false);
                    payload["errorKind"] = JsonValue.Create("userEventsNotFinished");
                    payload["note"] = JsonValue.Create(
                        "The wire went in, and the user-event frames were NOT finished, so the " +
                        "VI is still not executable. Read userEventStep: it holds each sub-answer " +
                        "whole. Nothing was rolled back.");
                }
                else if (finish["changed"]?.GetValue<bool>() is true)
                {
                    payload["note"] = JsonValue.Create(
                        $"{payload["note"]?.GetValue<string>()} AND the user-event frame(s) were " +
                        "finished: the spec was written again onto the wired file and rebuilt, " +
                        "which is the only order LabVIEW keeps. The active project was CLOSED " +
                        "(and saved) to release the VI - reopen it if you were working in it.");
                }
            }

            if (!outcome.Ok)
            {
                payload["wireEndsAfterXml"] = JsonValue.Create(Text(outputs, "wire ends after"));
                payload["nodeTerminalNamesXml"] =
                    JsonValue.Create(Text(outputs, "node terminal names"));
                payload["terminalIsSourceXml"] =
                    JsonValue.Create(Text(outputs, "terminal is source"));
            }

            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>
    /// STEP 3 of the user-event route: write the spec again, onto the file that now carries the
    /// wire, and rebuild.
    ///
    /// WHY IT CANNOT BE DONE ANY EARLIER. LabVIEW NORMALISES a user-event <c>EventSpec</c> when it
    /// LOADS the VI, against the dynamic-event wire present in the FILE at that moment. The wire
    /// is the one thing AIXML cannot express, so <c>lvai_generate_vi_with_events</c> necessarily
    /// writes that spec into a file without it - and LabVIEW discards it on the next load
    /// (<c>type</c> 1000 -&gt; 0, <c>dynIndex</c> 1 -&gt; 2, label back to
    /// <c>&lt;#2&gt;: Unknown Event (0x0)</c>). The save this tool's own wiring performs IS that
    /// load. So the spec has to be written again afterwards, and then it sticks: measured
    /// 2026-09-11 end to end on two VIs, both eBad -&gt; eIdle on exactly this step, the row
    /// surviving every later LabVIEW save.
    ///
    /// WHY IT CLOSES THE PROJECT. pylabview writes the .vi happily while LabVIEW keeps serving its
    /// own in-memory copy, so a rebuild without releasing the path leaves the verification reading
    /// the VI it REPLACED. Closing the active project is the documented release. It happens only
    /// when there is really something to write, which is why the cheap read comes first and the
    /// script's own <c>RESULT:</c> line decides.
    ///
    /// IT DOES NOT GUESS. The script refuses more than one user-event frame, because
    /// <c>dynIndex</c> is the registration item's position and only the value 1 is measured; a
    /// wrong one gives a VI that loads, compiles and fires the wrong event.
    /// </summary>
    private async Task<JsonObject> FinishUserEventsAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var step = new JsonObject { ["changed"] = false };

        if (PyLabview.Locate() is not { } bundle)
        {
            step["ok"] = true;
            step["skipped"] = "pylabviewNotProvisioned";
            step["note"] = "pylabview is not provisioned, so a user-event frame cannot be "
                + "finished here. If this VI has one it is still eBad - check with "
                + "lvai_exec_state, and see pylv_status.";
            return step;
        }

        if (StatusTools.ScriptsDirectory() is not { } scripts)
        {
            step["ok"] = true;
            step["skipped"] = "noScriptsDirectory";
            step["note"] = "No scripts folder next to the exe - lvai_status reports it as "
                + "scriptsDirectory - so the helper scripts are unreachable.";
            return step;
        }

        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "userevents",
            Path.GetFileNameWithoutExtension(viPath) + "-" + Environment.ProcessId + "-"
            + Guid.NewGuid().ToString("N")[..7]);
        Directory.CreateDirectory(directory);
        var steps = new JsonArray();
        step["bundleDirectory"] = directory;
        step["steps"] = steps;

        // The VI is still loaded in LabVIEW here; READING the file is fine, and it is current
        // because the wiring helper saved it.
        var extractJson = await new PyLabviewTools(connection)
            .ExtractAsync(viPath, directory, annotate: false, timeoutSeconds, ct);
        var extract = JsonNode.Parse(extractJson)?.AsObject();
        steps.Add(new JsonObject { ["step"] = "extract", ["answer"] = extract?.DeepClone() });
        if (extract?["mainXml"]?.GetValue<string>() is not { Length: > 0 } mainXml)
        {
            step["ok"] = false;
            step["note"] = "Could not extract the wired VI, so the user-event spec was not "
                + "written. The wire itself is in the file.";
            return step;
        }

        var baseName = Path.GetFileNameWithoutExtension(mainXml);

        var finish = await EventStructureTools.RunScriptAsync(bundle, scripts,
            "pylv-finish-user-events.py", [directory, baseName], "finishUserEvents",
            timeoutSeconds, ct);
        steps.Add(finish);
        if (finish["exitCode"]?.GetValue<int>() != 0)
        {
            step["ok"] = false;
            step["note"] = "The user-event spec could not be written - read this step's stderr. "
                + "Nothing was rebuilt, so the .vi is the wired-but-unfinished one.";
            return step;
        }

        var stdout = finish["stdout"]?.GetValue<string>() ?? "";
        if (!stdout.Contains("RESULT: changed", StringComparison.Ordinal))
        {
            step["ok"] = true;
            step["note"] = "Nothing to finish: this VI has no unresolved user-event frame. The "
                + "project was left open and no rebuild was needed.";
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            step["bundleDirectory"] = null;
            return step;
        }

        step["changed"] = true;

        // LabVIEW's save added compiled code, and pylabview copies those blocks through UNPARSED -
        // so a rebuild would carry compiled code describing the state before this edit.
        var strip = await EventStructureTools.RunScriptAsync(bundle, scripts,
            "pylv-strip-compiled.py", [directory, baseName], "strip", timeoutSeconds, ct);
        steps.Add(strip);
        if (strip["exitCode"]?.GetValue<int>() != 0)
        {
            step["ok"] = false;
            step["note"] = "Stripping the compiled code failed, so nothing was rebuilt.";
            return step;
        }

        // Release the path. Error 1055 means no project was active, which is the end state wanted.
        var closeJson = await new CloseTools(connection).CloseActiveProjectAsync(
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds,
            ct: ct);
        var close = JsonNode.Parse(closeJson)?.AsObject();
        steps.Add(new JsonObject { ["step"] = "closeProject", ["answer"] = close?.DeepClone() });
        step["projectClosed"] = close?["closed"]?.DeepClone();

        var rebuildJson = await new PyLabviewTools(connection)
            .RebuildAsync(mainXml, viPath, timeoutSeconds, ct);
        var rebuild = JsonNode.Parse(rebuildJson)?.AsObject();
        steps.Add(new JsonObject { ["step"] = "rebuild", ["answer"] = rebuild?.DeepClone() });
        if (rebuild?["ok"]?.GetValue<bool>() is not true)
        {
            step["ok"] = false;
            step["note"] = "The spec was written but the rebuild failed, so the .vi on disk is "
                + "the wired-but-unfinished one. The bundle is kept.";
            return step;
        }

        // The only check that sees an unfinished frame.
        var stateJson = await new ExecStateTools(connection).ExecStateAsync(
            viPath, helperAixmlPath: null, helperViPath: null, regenerateHelper: false,
            timeoutSeconds, ct: ct);
        var state = JsonNode.Parse(stateJson)?.AsObject();
        steps.Add(new JsonObject { ["step"] = "verify", ["answer"] = state?.DeepClone() });
        step["execState"] = state?["execState"]?.DeepClone();

        if (state?["broken"]?.GetValue<bool>() is not false)
        {
            step["ok"] = false;
            step["note"] = "The user-event spec was written and rebuilt, and LabVIEW still says "
                + "the VI is NOT EXECUTABLE. At this point the cause is elsewhere in the diagram: "
                + "read linkerErrors in the verify step.";
            return step;
        }

        step["ok"] = true;
        step["note"] = "The user-event frame(s) are finished and LabVIEW can run the VI.";
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        step["bundleDirectory"] = null;
        return step;
    }

    /// <summary>
    /// The one instruction that cannot be left out of this tool's answer. Every cheap check agrees
    /// with itself here, and the picture is the only one that disagrees when it matters.
    /// </summary>
    internal const string VerifyHint =
        "Render the diagram and LOOK - lvai_render_diagrams - and render a COPY of the saved .vi " +
        "at a path LabVIEW has never loaded. With Auto Route? at its FALSE default the branch is " +
        "connected and invisible, and no property distinguishes the two; and a render of the " +
        "just-edited path has been measured drawing the PRE-EDIT diagram, because the addon's " +
        "application instance holds its own copy of the VI.";

    /// <summary>What happened, classified once so the payload and the note cannot disagree.</summary>
    internal readonly record struct Outcome(bool Ok, string? Kind, bool AlreadyWired, string Note);

    /// <summary>
    /// The verdict. Kept pure and separate from the RPC so it can be tested against the shapes
    /// really measured rather than the shapes it "should" produce.
    ///
    /// `endsBefore` is the number of ends the refnum wire had BEFORE the branch: 2 is the case
    /// this tool exists for, 3 means the hop was already there and the call is a no-op, and 0
    /// means the tunnel carried no wire at all - which is the caller's diagram missing its
    /// precondition rather than a failure here.
    /// </summary>
    internal static Outcome Classify(bool? found, int? code, string? sourceFrom,
                                     bool? wiredBefore, int? endsAfter, bool? broken)
    {
        if (code is null || found is null)
            return new(false, "helperDidNotAnswer", false,
                "The helper ran but its indicators could not be read back - the raw flattened " +
                "XML is returned so you can see what arrived. Nothing can be concluded about " +
                "the VI from this.");

        if (code == 1055)
            return new(false, "noRefnumSourceFound", false,
                "Error 1055, 'Object reference is invalid'. Neither source was there to wire " +
                "from: the structure has no Event Registration Refnum tunnel carrying a wire, " +
                "and no Register For Events node was found on the block diagram either. Author " +
                "the registration into the VI first.");

        if (code == 1062)
            return new(false, "wireDirectionRefused", false,
                "Error 1062, 'Specified objects cannot be wired together'. That is a DIRECTION " +
                "verdict, not a type one: the pair offered had no source between them. It is the " +
                "loud failure this tool's terminal selection is designed to produce rather than " +
                "wiring the wrong thing quietly.");

        if (code == 1057)
            return new(false, "downcastRefused", false,
                "Error 1057, 'Object cannot be cast to the specified type' - an object the search " +
                "took for a structure, a tunnel or a node was none of them. Read topLevelClasses.");

        if (code != 0)
            return new(false, "helperReportedError", false,
                $"The helper's chain reported error {code}; helperErrorSource names the node. " +
                "Nothing was saved after a failing node, so the VI is as it was.");

        if (found == false)
            return new(false, "noEventStructureFound", false,
                "No Event Structure was found on the block diagram or one level inside its loops " +
                "and cases, which is as deep as the search goes. topLevelClasses is what it did " +
                "see; a structure nested deeper needs the wire drawn another way.");

        var via = sourceFrom switch
        {
            "tunnel" => "the structure's own refnum tunnel, whose net was BRANCHED",
            "diagram" => "the Register For Events node on the block diagram, across the loop " +
                         "border - so LabVIEW created the tunnel itself",
            _ => "an unreported source",
        };

        if (wiredBefore == true)
            return new(endsAfter >= 2 && broken != true, null, true,
                "The dynamic event terminal ALREADY carried a wire, so this was a no-op. " +
                "Re-running is safe; that is what makes the tool idempotent.");

        if (endsAfter is null || endsAfter < 2)
            return new(false, "branchDidNotLand", false,
                "The dynamic event terminal carried no wire before the call and carries " +
                $"{(endsAfter is null ? "an unreadable one" : "none")} after it, so the branch " +
                "did not take - even though no node reported an error. This is the check that a " +
                "geometry or property reading cannot make.");

        if (broken == true)
            return new(false, "wireBroken", false,
                "The wire landed and LabVIEW reports it as BROKEN, so the VI will not run. Read " +
                "the diagram before saving anything else.");

        return new(true, null, false,
            $"Wired from {via}. The dynamic event terminal had no wire before and now carries one " +
            $"of {endsAfter} ends which is not broken, and the VI was saved. An end count of 3 is " +
            "a branch of an existing net; 2 is a fresh wire, which is what crossing the loop " +
            "border produces.");
    }

    /// <summary>
    /// The element count of a LabVIEW-flattened 1D array, or null when the text is not one. Used
    /// for counting a wire's ends, which is the only check that distinguishes a branch that landed
    /// from one that did not.
    /// </summary>
    internal static int? Dimsize(string? flattened)
    {
        if (string.IsNullOrWhiteSpace(flattened)) return null;
        try
        {
            var root = XElement.Parse(flattened, LoadOptions.None);
            return root.Elements().FirstOrDefault(e => e.Name.LocalName == "Dimsize") is { } d
                   && int.TryParse(d.Value, out var n) ? n : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The `Val` of every element of a LabVIEW-flattened 1D array, in order. Empty when the text
    /// is not a flattened array - an empty list and an unreadable one are deliberately the same
    /// here, because both mean "nothing to report" for a listing.
    /// </summary>
    internal static IReadOnlyList<string> Strings(string? flattened)
    {
        if (string.IsNullOrWhiteSpace(flattened)) return [];
        try
        {
            var root = XElement.Parse(flattened, LoadOptions.None);
            return root.Elements()
                       .Where(e => e.Name.LocalName != "Name" && e.Name.LocalName != "Dimsize")
                       .Select(e => e.Elements().FirstOrDefault(v => v.Name.LocalName == "Val")
                                     ?.Value ?? string.Empty)
                       .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static JsonArray Array(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(JsonValue.Create(value));
        return array;
    }

    private static string? Text(IDictionary<string, string> outputs, string name) =>
        outputs.TryGetValue(name, out var value) ? value : null;

    private static bool? Flag(IDictionary<string, string> outputs, string name) =>
        Text(outputs, name) is { Length: > 0 } text
            ? text.Trim() is "1" or "T" or "TRUE" or "true" or "True"
            : null;

    private static int? Number(IDictionary<string, string> outputs, string name) =>
        Text(outputs, name) is { Length: > 0 } text && int.TryParse(text.Trim(), out var n)
            ? n : null;

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>Under TEMP, for the reason IconTools records: Save:Instrument fails elsewhere.</summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_wire_dyn_events.vi");

    /// <summary>
    /// Whether the shipped AIXML is newer than the cached helper. An upgrade must not leave a
    /// stale helper answering under output names this tool no longer reads.
    /// </summary>
    internal static bool IsStale(string aixmlPath, string helperViPath)
    {
        if (!File.Exists(helperViPath) || !File.Exists(aixmlPath)) return false;
        return File.GetLastWriteTimeUtc(aixmlPath) > File.GetLastWriteTimeUtc(helperViPath);
    }

    /// <summary>Validate then generate the helper VI; null on success, else an error payload.</summary>
    private async Task<string?> GenerateHelperAsync(
        string aixml, string helperVi, int timeoutSeconds, CancellationToken ct)
    {
        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (validation.ErrorCode != 0)
            return Json.Error("helperAixmlInvalid",
                $"The helper AIXML at '{aixml}' does not validate: {validation.ErrorMessage}",
                new { aiXmlPath = Path.GetFullPath(aixml), errorCode = validation.ErrorCode });

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = helperVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (generation.ErrorCode == 0 && File.Exists(helperVi)) return null;

        return Json.Error("helperGenerationFailed",
            $"Could not generate the helper VI at '{helperVi}': {generation.ErrorMessage}",
            new
            {
                helperViPath = helperVi,
                errorCode = generation.ErrorCode,
                viExistsNow = File.Exists(helperVi),
            });
    }
}
