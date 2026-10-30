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
        THE SEARCH IS AT ANY DEPTH: NI's Traverse for GObjects.vi walks the whole block diagram
        for the EventStructure and, for the fallback, the RegisterForEvents node - so an Event
        Structure in a Case frame inside a While Loop (the Actor Framework helper-loop shape) is
        found, where the earlier two-level search missed it. eventStructuresFound is the count;
        with more than one the FIRST is wired and the note says so.
        It needs a project OPEN AND ACTIVE in the IDE: the VI is opened through
        Application:Project:Active Project so the edit lands in the copy the user is looking at.
        IT ALSO FINISHES A USER-EVENT FRAME, which nothing else can, and that is why a VI with one
        ends this call EXECUTABLE instead of eBad. LabVIEW normalises a user-event EventSpec when
        it LOADS the VI, against the wire present in the FILE at that moment - so the spec
        lvai_generate_vi_with_events writes is necessarily discarded, by the very save this tool
        performs. Afterwards it sticks, so the spec is written AGAIN here and the VI rebuilt:
        measured 2026-09-11 on two VIs, both eBad -> eIdle on exactly that step. Read
        userEventStep; `changed` says whether there was anything to do.
        THE BUNDLE STEP CLOSES THE ACTIVE PROJECT, saving it, because pylabview writes the file
        while LabVIEW keeps serving its own copy - so without the release the verification reads
        the VI it replaced. It is ONE round trip for both jobs (user-event specs, use-default
        flags), and it is skipped entirely, project included, when neither has anything to do.
        projectClosed says whether it happened and projectClosedFor why. finishUserEvents: false
        turns off the user-event half.
        SEVERAL USER-EVENT FRAMES ARE FINISHED TOO (since 2026-10-10): dynIndex is the event's
        position among the registration's items, lvai_generate_vi_with_events writes it per frame,
        and this tool reads it off the VI BEFORE its wiring save destroys it, then writes each
        frame back. A VI generated before that date wrote dynIndex 1 on every frame and is refused
        rather than guessed - a wrong dynIndex gives a VI that loads, compiles and fires the WRONG
        event; generate it again.
        IT ALSO WIRES THE RIGHT SIDE (wireRightSide, default true): the structure's SOURCE-side
        Event Registration Refnum terminal to the event registration refnum input of Unregister
        For Events, found at any depth by Traverse (class Function, exact, with a sink of that
        lower-case name), Auto Wire and Auto Route TRUE, LabVIEW making the tunnels. Read
        rightSide: no Unregister node is {skipped: "noUnregisterForEvents"}, more than one is
        refused and named with the count, an input already wired is a no-op, and a helper that
        stopped before searching (no active project) is {skipped: "notReached"}. It runs on its own
        error branch, so a right-side failure does not lose the left wire.
        A TUNNEL THAT WIRE CREATES ON AN ENCLOSING CASE STRUCTURE IS UNWIRED IN THE OTHER FRAMES
        AND BREAKS THE VI, so "Use Default if Unwired" is set on it - in the pylabview bundle,
        because AIXML refuses that property ("Invalid property"). ONLY tunnels this call created:
        the VI is extracted BEFORE the helper runs and only selTun uids absent from that baseline
        are touched; if any baseline tunnel is missing afterwards the uids are not comparable and
        nothing is patched. useDefaultSet names the tunnels.
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
        [Description("""
            Also wire the RIGHT side: the Event Structure's source-side Event Registration Refnum
            terminal to Unregister For Events, then set Use Default if Unwired on any Case
            Structure output tunnel that wire created. On by default. Costs one extra pylabview
            extract up front (the baseline the new tunnels are told apart by).
            """)]
        bool wireRightSide = true,
        [Description("Local budget in seconds")]
        int timeoutSeconds = 300,
        CancellationToken ct = default,
        [Description(AixmlBackend.RouteParameterDescription + """
             On pylabview the helper is COMPOSED and run over ActiveX, and every later step
            inherits the backend. A VI the composer wrote needs none of this - it is wired and its
            user events registered in the compose - and is answered as already wired.
            """)]
        string? backend = null) =>
        await BackendRoute.WithBackendAsync(backend, "lvai_wire_dynamic_events", () => Rpc.GuardAsync(async () =>
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
            if (BackendRoute.OnPyLabview)
                helperVi = await BackendRoute.ComposedHelperAsync(aixml, timeoutSeconds, ct,
                                                                  Path.GetFileName(helperVi));
            else if (regenerateHelper || stale || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            var bytesBefore = new FileInfo(viPath).Length;

            // The VI AS GENERATED, read before the wiring save below - that save is the load at
            // which LabVIEW discards every user-event spec, and with it which frame handles which
            // user event. Several user-event frames are finished from this record (step 3).
            var before = finishUserEvents ? await ExtractBeforeWiringAsync(viPath, timeoutSeconds, ct) : null;

            // THE BASELINE FOR "CREATED BY THIS CALL". Use Default if Unwired may only go onto a
            // tunnel the right-side wire made, never onto one the user already had - and the only
            // record of "already had" is the file before the helper saves over it. Taken while
            // LabVIEW holds the VI, which is fine for a READ.
            (Baseline? baseline, JsonObject baselineStep) = wireRightSide
                ? await ExtractBaselineAsync(viPath, timeoutSeconds, ct)
                : ((Baseline?)null, new JsonObject { ["skipped"] = "wireRightSideFalse" });

            var stopwatch = Stopwatch.StartNew();
            IDictionary<string, string> outputs;
            JsonObject payload;
            if (BackendRoute.OnPyLabview)
            {
                // over ActiveX: the run answers typed values; the indicators are read back into
                // the same name -> text map NI's RunVIAsTopLevel gives - a scalar's text, an
                // array's or cluster's flattened XML, which is what the parsers below read
                var run = await new RunTools(connection).RunViAndReadValuesAsync(helperVi,
                    new JsonObject
                    {
                        ["VI Path"] = Path.GetFullPath(viPath),
                        // text, as on grpc; the helper wires the right side unless this reads FALSE
                        ["wire right side"] = wireRightSide ? "TRUE" : "FALSE",
                    }.ToJsonString(),
                    timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct);
                stopwatch.Stop();
                if (BackendRoute.RunRefusal(run) is { } refusal)
                    return Json.Error(refusal, "The composed wiring helper did not run over ActiveX.",
                        new { helperViPath = helperVi, answer = JsonNode.Parse(run) });
                outputs = OutputsOf(JsonNode.Parse(run) as JsonObject);
                payload = new JsonObject { ["errorCode"] = 0, ["errorMessage"] = "No Error", ["via"] = "ActiveX" };
            }
            else
            {
                var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
                request.Inputs["VI Path"] = Path.GetFullPath(viPath);
                // Text, because the runner's inputs are strings; the helper wires the right side
                // unless this reads FALSE.
                request.Inputs["wire right side"] = wireRightSide ? "TRUE" : "FALSE";
                var response = await connection.InvokeAsync((c, t) =>
                    c.RunVIAsTopLevelAsync(request,
                        deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
                stopwatch.Stop();
                outputs = response.Outputs;
                payload = Json.Node(response).AsObject();
                payload.Remove("outputs");
            }
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
            var eventStructures = Number(outputs, "event structures");

            // Over ActiveX the helper's code IS read back - the 1055 of the Project:Active Project
            // read with no project open, everything after it skipped and the class list empty -
            // where NI's run reads none (Error 91). Given as NI's shape, it gets NI's verdict,
            // noActiveProject, not noRefnumSourceFound about a diagram nobody read (2026-10-16).
            var classifiedCode = BackendRoute.OnPyLabview && chainCode == 1055
                                 && topClasses.All(string.IsNullOrWhiteSpace) ? null : chainCode;
            var outcome = Classify(found, classifiedCode, sourceFrom, wiredBefore, endsAfter, broken,
                                   chainSource, topClasses, eventStructures);

            var right = ClassifyRightSide(wireRightSide,
                unregisterNodes: Number(outputs, "unregister nodes"),
                sourceFound: Flag(outputs, "right source found"),
                wiredBefore: Flag(outputs, "right wired before"),
                attempted: Flag(outputs, "right attempted"),
                code: Number(outputs, "right code"),
                endsAfter: Number(outputs, "right wire ends"),
                broken: Flag(outputs, "right wire broken"),
                reached: outcome.Kind is not ("noActiveProject" or "helperDidNotAnswer"));

            payload["ok"] = JsonValue.Create(outcome.Ok);
            payload["errorKind"] = outcome.Kind is null ? null : JsonValue.Create(outcome.Kind);
            payload["viPath"] = JsonValue.Create(Path.GetFullPath(viPath));
            payload["eventStructureFound"] = found is null ? null : JsonValue.Create(found);
            payload["eventStructuresFound"] =
                eventStructures is null ? null : JsonValue.Create(eventStructures);
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
            payload["readBeforeWiring"] = JsonValue.Create(before is not null);
            payload["note"] = JsonValue.Create(outcome.Note);
            payload["verifyBy"] = JsonValue.Create(VerifyHint);

            // STEP 3. The wire is in the file now, so - and only now - a user-event EventSpec
            // will survive LabVIEW's next load. See FinishInBundleAsync for the measurement.
            //
            // GATED ON EVIDENCE OF THE WIRE, NOT ON `outcome.Ok` - measured 2026-09-11, and
            // gating it on `ok` made this whole automation DEAD IN THE COMMON CASE. This helper
            // very often ends `helperDidNotAnswer`: RunVIAsTopLevel cannot read its indicators
            // back through a variant and returns Error 91, which is an artefact of the read-back
            // and says nothing about the VI - the tool's own note has said so all along. The wire
            // was really there in that state (`wireEndsAfter` 3, the file rewritten), so step 3
            // must still run. `endsAfter >= 3` IS the evidence: a source plus two sinks is the
            // branch this tool makes. Unit tests could not see this; only the end-to-end run did.
            // The Error 91 was found 2026-10-13: it hit every bool and int32 indicator, and they
            // are strings (Flatten To XML) since then - see ParseFlag. The gate stays.
            var wired = outcome.Ok || endsAfter >= 3;

            payload["rightSide"] = right.ToJson(
                unregisterNodes: Number(outputs, "unregister nodes"),
                wiredBefore: Flag(outputs, "right wired before"),
                attempted: Flag(outputs, "right attempted"),
                code: Number(outputs, "right code"),
                errorSource: Text(outputs, "right source"),
                endsAfter: Number(outputs, "right wire ends"),
                broken: Flag(outputs, "right wire broken"));
            payload["baselineStep"] = baselineStep;
            payload["useDefaultSet"] = new JsonArray();
            payload["projectClosed"] = JsonValue.Create(false);

            // ONE ROUND TRIP FOR BOTH BUNDLE JOBS. The user-event spec needs the left wire in the
            // file; Use Default if Unwired needs the right one. Either alone is reason enough to
            // pay for extract, strip, project close, rebuild and execState - and paying twice
            // would close the project twice and rebuild a file LabVIEW has not yet reloaded.
            var doUserEvents = finishUserEvents && wired;
            var doUseDefault = baseline is not null && wired && right.MayHaveCreatedTunnels;
            if (doUserEvents || doUseDefault)
            {
                var finish = await FinishInBundleAsync(viPath, doUserEvents,
                    doUseDefault ? baseline : null, before, timeoutSeconds, ct);
                payload["userEventStep"] = finish;
                payload["useDefaultSet"] = finish["useDefaultSet"]?.DeepClone() ?? new JsonArray();
                payload["projectClosed"] = JsonValue.Create(
                    finish["projectClosed"]?.GetValue<bool>() is true);
                payload["projectClosedFor"] = finish["projectClosedFor"]?.DeepClone();
                payload["viBytesAfter"] = JsonValue.Create(
                    File.Exists(viPath) ? new FileInfo(viPath).Length : 0L);

                if (finish["ok"]?.GetValue<bool>() is false)
                {
                    // The wire went in, so the VI is further along than it was - and it is still
                    // not executable, and that is what `ok` has to report.
                    payload["ok"] = JsonValue.Create(false);
                    payload["errorKind"] = JsonValue.Create(
                        finish["errorKind"]?.GetValue<string>() ?? "userEventsNotFinished");
                    payload["note"] = JsonValue.Create(
                        "The wire went in, and the bundle step did NOT finish: " +
                        $"{finish["note"]?.GetValue<string>()} Read userEventStep: it holds each " +
                        "sub-answer whole. Nothing was rolled back." +
                        (finish["projectClosed"]?.GetValue<bool>() is true
                            ? " The active project WAS closed (and saved) on the way."
                            : ""));
                }
                else if (finish["changed"]?.GetValue<bool>() is true)
                {
                    var executable = finish["execState"]?.GetValue<int>() == 1;

                    // A VERIFIED execState OUTRANKS an unreadable helper output. If the frames are
                    // finished and LabVIEW can run the VI, the deliverable is good and `ok` should
                    // say so - `helperDidNotAnswer` only ever meant "the diagnostics did not come
                    // back", and this is stronger evidence than those diagnostics would have been.
                    if (executable && !outcome.Ok)
                    {
                        payload["ok"] = JsonValue.Create(true);
                        payload["errorKind"] = null;
                        payload["okFrom"] = JsonValue.Create("userEventStep.execState");
                    }

                    payload["note"] = JsonValue.Create(
                        $"{payload["note"]?.GetValue<string>()} " +
                        BundleStepNote(finish["userEventsChanged"]?.GetValue<bool>() is true,
                                       finish["useDefaultChanged"]?.GetValue<bool>() is true,
                                       executable));
                }
            }

            // A right side that was ATTEMPTED and did not land is a failure of this call even when
            // the left side is fine - a skipped or refused one is not, it is reported. Decided
            // BEFORE the clean-up, which runs only on a VI this call left good.
            if (right.Failed && payload["ok"]?.GetValue<bool>() is true)
            {
                payload["ok"] = JsonValue.Create(false);
                payload["errorKind"] = JsonValue.Create("rightSideNotWired");
            }
            if (right.Reason is not "notRequested")
                payload["note"] = JsonValue.Create(
                    $"{payload["note"]?.GetValue<string>()} " +
                    $"{(right.Failed ? "RIGHT SIDE" : "Right side")}: {right.Note}");

            // THE CLEAN-UP, our layout, on the finished VI - the end of the route that
            // lvai_generate_vi_with_events hands over to for user events, so the VI is cleaned up
            // whichever tool finishes it. After every pylabview rebuild above: it is a LabVIEW save.
            if (payload["ok"]?.GetValue<bool>() == true)
            {
                var cleaned = await new DiagramCleanup(connection).RunAsync(
                    viPath, AixmlBackend.Inherited.Value?.Name ?? AixmlBackend.Grpc, timeoutSeconds, ct);
                payload["cleanUpDiagram"] = cleaned;
                if (cleaned["ok"]?.GetValue<bool>() != true)
                    payload["note"] = JsonValue.Create($"{payload["note"]?.GetValue<string>()} The block " +
                        "diagram was NOT cleaned up: " + cleaned["note"]?.GetValue<string>());

                // AND THE SIZE, since this is where a user-event VI is finished: the generator
                // returns at its verify step for such a VI, before its own size step, so the main
                // VI of every producer/consumer build came back unmeasured (the gRPC cold build of
                // LayoutCheck, 2026-10-16). The chain needs the AIXML, which this tool never sees.
                var size = await new BulkTools(connection).DiagramSizeStepAsync(
                    null, viPath, timeoutSeconds, ct,
                    overCom: AixmlBackend.Inherited.Value?.Name == AixmlBackend.PyLabview);
                payload["diagramSize"] = size;
                if (size["note"]?.GetValue<string>() is { Length: > 0 } sizeNote)
                    payload["note"] = JsonValue.Create($"{payload["note"]?.GetValue<string>()}{sizeNote}");
            }

            if (before is { } b)
                try { Directory.Delete(b.Directory, recursive: true); } catch (IOException) { }
            if (baseline is not null)
            {
                try { Directory.Delete(baseline.Directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            if (!outcome.Ok)
            {
                payload["wireEndsAfterXml"] = JsonValue.Create(Text(outputs, "wire ends after"));
                payload["nodeTerminalNamesXml"] =
                    JsonValue.Create(Text(outputs, "node terminal names"));
                payload["terminalIsSourceXml"] =
                    JsonValue.Create(Text(outputs, "terminal is source"));
            }

            return payload.ToJsonString(Json.Answer);
        }));

    /// <summary>
    /// STEP 3 of the user-event route: write the spec again, onto the file that now carries the
    /// wire, and rebuild - and, in the same round trip, Use Default if Unwired on the case
    /// tunnels the right-side wire created.
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
    /// SEVERAL USER-EVENT FRAMES are finished from <paramref name="before"/>, the VI as generated:
    /// <c>dynIndex</c> is the event's position among the registration's items (measured
    /// 2026-10-10), the generation step writes it per frame, and the wiring save destroys it - so
    /// the script reads it there. Without that record it still refuses more than one, because a
    /// wrong <c>dynIndex</c> gives a VI that loads, compiles and fires the wrong event.
    ///
    /// AND IT IS ALSO WHERE USE DEFAULT IF UNWIRED IS SET, because that is a bundle edit too and a
    /// second round trip would close the project and rebuild twice. AIXML refuses the property on
    /// a Property Node ("Invalid property") and {LV.Tunnel}'s generic SetProperty is unmeasured;
    /// the heap flag 0x200000 on a <c>selTun</c> is what a run measured working. The script only
    /// touches a Case Structure output tunnel whose uid is ABSENT from <paramref name="baseline"/>,
    /// the extract taken before the helper ran - so a tunnel the user already had is never changed,
    /// even one broken the same way - and it refuses outright when a baseline tunnel is gone
    /// afterwards, because then the uids are not comparable.
    /// </summary>
    private async Task<JsonObject> FinishInBundleAsync(
        string viPath, bool userEvents, Baseline? baseline,
        (string Directory, string BaseName)? before, int timeoutSeconds, CancellationToken ct)
    {
        var step = new JsonObject
        {
            ["changed"] = false,
            ["userEventsChanged"] = false,
            ["useDefaultChanged"] = false,
            ["useDefaultSet"] = new JsonArray(),
            ["projectClosed"] = false,
        };

        if (PyLabview.Locate() is not { } bundle)
        {
            step["ok"] = true;
            step["skipped"] = "pylabviewNotProvisioned";
            step["note"] = "pylabview is not provisioned, so neither a user-event frame nor a "
                + "case tunnel's Use Default if Unwired can be finished here. If this VI needs "
                + "either it is still eBad - check with lvai_exec_state, and see pylv_status.";
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
            step["errorKind"] = "extractFailed";
            step["note"] = "Could not extract the wired VI, so nothing was written into it. The "
                + "wire itself is in the file.";
            return step;
        }

        var baseName = Path.GetFileNameWithoutExtension(mainXml);
        var closedFor = new JsonArray();

        string[] userEventArguments = before is { } b
            ? [directory, baseName, "--before", b.Directory, b.BaseName]
            : [directory, baseName];
        if (userEvents)
        {
            var finish = await EventStructureTools.RunScriptAsync(bundle, scripts,
                "pylv-finish-user-events.py", userEventArguments, "finishUserEvents",
                timeoutSeconds, ct);
            steps.Add(finish);
            if (finish["exitCode"]?.GetValue<int>() != 0)
            {
                step["ok"] = false;
                step["errorKind"] = "userEventsNotFinished";
                step["note"] = "The user-event spec could not be written - read this step's "
                    + "stderr. Nothing was rebuilt, so the .vi is the wired-but-unfinished one.";
                return step;
            }
            if ((finish["stdout"]?.GetValue<string>() ?? "")
                .Contains("RESULT: changed", StringComparison.Ordinal))
            {
                step["userEventsChanged"] = true;
                closedFor.Add("userEvents");
            }
        }

        if (baseline is not null)
        {
            var useDefault = await EventStructureTools.RunScriptAsync(bundle, scripts,
                "pylv-set-use-default.py",
                [directory, baseName, "--before", baseline.Directory, baseline.BaseName],
                "useDefault", timeoutSeconds, ct);
            steps.Add(useDefault);
            if (useDefault["exitCode"]?.GetValue<int>() != 0)
            {
                step["ok"] = false;
                step["errorKind"] = "useDefaultNotSet";
                step["note"] = "Setting Use Default if Unwired failed - read this step's stderr. "
                    + "Nothing was rebuilt, so the .vi is the wired one without the flag.";
                return step;
            }

            var (result, uids) = ParseUseDefault(useDefault["stdout"]?.GetValue<string>());
            step["useDefaultResult"] = result;
            var set = new JsonArray();
            foreach (var uid in uids) set.Add(JsonValue.Create(uid));
            step["useDefaultSet"] = set;
            if (result == "changed")
            {
                step["useDefaultChanged"] = true;
                closedFor.Add("useDefault");
            }
            else if (result == "refused")
                step["useDefaultNote"] = "REFUSED: a tunnel of the baseline is gone after the "
                    + "edit, so 'absent from the baseline' no longer means 'created by this call' "
                    + "and nothing was patched. If a new case tunnel is unwired in a frame the VI "
                    + "stays broken - set Use Default if Unwired on it in the IDE.";
        }

        if (closedFor.Count == 0)
        {
            step["ok"] = true;
            step["note"] = "Nothing to finish: no unresolved user-event frame and no new case "
                + "tunnel unwired in a frame. The project was left open and no rebuild was needed.";
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
            step["errorKind"] = "stripFailed";
            step["note"] = "Stripping the compiled code failed, so nothing was rebuilt.";
            return step;
        }

        // Release the path. Error 1055 means no project was active, which is the end state wanted.
        var closeJson = await new CloseTools(connection).CloseActiveProjectAsync(
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds: timeoutSeconds,
            ct: ct);
        var close = JsonNode.Parse(closeJson)?.AsObject();
        steps.Add(new JsonObject { ["step"] = "closeProject", ["answer"] = close?.DeepClone() });
        step["projectClosed"] = close?["closed"]?.GetValue<bool>() is true;
        step["projectClosedFor"] = closedFor;

        var rebuildJson = await new PyLabviewTools(connection)
            .RebuildAsync(mainXml, viPath, timeoutSeconds, ct);
        var rebuild = JsonNode.Parse(rebuildJson)?.AsObject();
        steps.Add(new JsonObject { ["step"] = "rebuild", ["answer"] = rebuild?.DeepClone() });
        if (rebuild?["ok"]?.GetValue<bool>() is not true)
        {
            step["ok"] = false;
            step["errorKind"] = "rebuildFailed";
            step["note"] = "The bundle was edited but the rebuild failed, so the .vi on disk is "
                + "the wired-but-unfinished one. The bundle is kept.";
            return step;
        }

        // The only check that sees an unfinished frame or an unwired case tunnel.
        var stateJson = await new ExecStateTools(connection).ExecStateAsync(
            viPath, helperAixmlPath: null, helperViPath: null, regenerateHelper: false,
            timeoutSeconds, ct: ct);
        var state = JsonNode.Parse(stateJson)?.AsObject();
        steps.Add(new JsonObject { ["step"] = "verify", ["answer"] = state?.DeepClone() });
        step["execState"] = state?["execState"]?.DeepClone();

        if (state?["broken"]?.GetValue<bool>() is not false)
        {
            step["ok"] = false;
            step["errorKind"] = step["userEventsChanged"]?.GetValue<bool>() is true
                ? "userEventsNotFinished" : "notExecutableAfterUseDefault";
            step["note"] = "The bundle was edited and rebuilt, and LabVIEW still says the VI is "
                + "NOT EXECUTABLE. At this point the cause is elsewhere in the diagram: read "
                + "errors and linkerErrors in the verify step.";
            return step;
        }

        step["ok"] = true;
        step["note"] = "The bundle step is finished and LabVIEW can run the VI.";
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        step["bundleDirectory"] = null;
        return step;
    }

    /// <summary>The extract taken BEFORE the helper runs: what "created by this call" is measured against.</summary>
    internal sealed record Baseline(string Directory, string BaseName);

    /// <summary>
    /// Extract the VI as it is on disk before the helper saves over it. Auxiliary: a failure here
    /// costs the Use Default step and nothing else, so it is reported and never thrown.
    /// </summary>
    private async Task<(Baseline?, JsonObject)> ExtractBaselineAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var step = new JsonObject();
        if (PyLabview.Locate() is null)
        {
            step["skipped"] = "pylabviewNotProvisioned";
            return (null, step);
        }

        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "wiredyn-baseline",
            Path.GetFileNameWithoutExtension(viPath) + "-" + Environment.ProcessId + "-"
            + Guid.NewGuid().ToString("N")[..7]);
        try
        {
            Directory.CreateDirectory(directory);
            var json = await new PyLabviewTools(connection)
                .ExtractAsync(viPath, directory, annotate: false, timeoutSeconds, ct);
            var answer = JsonNode.Parse(json)?.AsObject();
            if (answer?["mainXml"]?.GetValue<string>() is { Length: > 0 } mainXml)
            {
                step["ok"] = true;
                step["directory"] = directory;
                return (new Baseline(directory, Path.GetFileNameWithoutExtension(mainXml)), step);
            }
            step["ok"] = false;
            step["answer"] = answer?.DeepClone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            step["ok"] = false;
            step["error"] = ex.Message;
        }

        step["note"] = "The baseline extract failed, so Use Default if Unwired will NOT be set "
            + "on any tunnel: without it a tunnel this call made cannot be told from one the "
            + "user already had.";
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        return (null, step);
    }

    /// <summary>
    /// The RESULT and the USE_DEFAULT_SET line of <c>pylv-set-use-default.py</c>. The script
    /// prints RESULT LAST, so the last such line wins.
    /// </summary>
    internal static (string? Result, IReadOnlyList<string> Uids) ParseUseDefault(string? stdout)
    {
        string? result = null;
        var uids = new List<string>();
        foreach (var raw in (stdout ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("RESULT:", StringComparison.Ordinal))
                result = line["RESULT:".Length..].Trim();
            else if (line.StartsWith("USE_DEFAULT_SET:", StringComparison.Ordinal))
            {
                uids.Clear();
                uids.AddRange(line["USE_DEFAULT_SET:".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        return (result, uids);
    }

    /// <summary>The sentence appended to the note when the bundle step changed the file.</summary>
    internal static string BundleStepNote(bool userEvents, bool useDefault, bool executable)
    {
        var what = (userEvents, useDefault) switch
        {
            (true, true) => "the user-event frame(s) were finished AND Use Default if Unwired " +
                            "was set on the case tunnel(s) the right-side wire created",
            (true, false) => "the user-event frame(s) were finished: the spec was written again " +
                             "onto the wired file and rebuilt, which is the only order LabVIEW keeps",
            (false, true) => "Use Default if Unwired was set on the case tunnel(s) the " +
                             "right-side wire created (useDefaultSet)",
            _ => "the bundle was rebuilt",
        };
        return $"AND {what}" +
               (executable ? ", and execState reads 1 - the VI is EXECUTABLE" : "") +
               ". The active project was CLOSED (and saved) to release the VI - reopen it if " +
               "you were working in it.";
    }

    /// <summary>The right side's verdict, classified once so the payload and the note agree.</summary>
    internal readonly record struct RightSide(string Status, string? Reason, bool Failed,
                                              bool MayHaveCreatedTunnels, string Note)
    {
        public JsonObject ToJson(int? unregisterNodes, bool? wiredBefore, bool? attempted,
                                 int? code, string? errorSource, int? endsAfter, bool? broken)
        {
            var json = new JsonObject { ["status"] = Status };
            if (Reason is not null)
                json[Status is "refused" ? "refused" : "skipped"] = Reason;
            json["unregisterNodes"] = unregisterNodes is null ? null : JsonValue.Create(unregisterNodes);
            json["wiredBefore"] = wiredBefore is null ? null : JsonValue.Create(wiredBefore);
            json["attempted"] = attempted is null ? null : JsonValue.Create(attempted);
            json["errorCode"] = code is null ? null : JsonValue.Create(code);
            json["errorSource"] = string.IsNullOrEmpty(errorSource) ? null : errorSource;
            json["wireEndsAfter"] = endsAfter is null ? null : JsonValue.Create(endsAfter);
            json["wireBroken"] = broken is null ? null : JsonValue.Create(broken);
            json["note"] = Note;
            return json;
        }
    }

    /// <summary>
    /// The right-side verdict from the helper's indicators. Pure, so it can be tested against the
    /// shapes the helper produces. `attempted` is the helper's own Case selector: requested, exactly
    /// one Unregister node, its input unwired, and the structure's source terminal found.
    /// `reached` is false when the LEFT side stopped before the helper searched anything (no
    /// active project, no answer): then every right-side indicator holds its default, and a 0
    /// Unregister nodes would be read as a statement about the diagram.
    /// </summary>
    internal static RightSide ClassifyRightSide(bool requested, int? unregisterNodes,
                                                bool? sourceFound, bool? wiredBefore,
                                                bool? attempted, int? code, int? endsAfter,
                                                bool? broken, bool reached = true)
    {
        if (!requested)
            return new("skipped", "notRequested", false, false,
                "wireRightSide was false, so the right side was not touched." +
                (unregisterNodes == 1 && wiredBefore == false
                    ? " The Unregister For Events node's input stays UNWIRED, and that input is " +
                      "required - the VI is not executable until it is wired."
                    : ""));

        if (!reached)
            return new("skipped", "notReached", false, false,
                "the helper stopped before it searched the diagram (see errorKind), so nothing is " +
                "known about the right side.");

        if (attempted is null)
            return new("unknown", null, false, true,
                "The helper's right-side indicators did not come back, so whether it was wired is " +
                "unknown. The bundle step still looks for case tunnels this call created; render " +
                "the diagram to see the right side.");

        if (attempted == true)
        {
            if (code is null)
                return new("unknown", null, false, true,
                    "Connect Wire ran for the right side and its error code did not come back.");
            if (code != 0)
                return new("failed", null, true, true,
                    $"Connect Wire for the right side answered error {code} (rightSide." +
                    "errorSource names the node). The left side was still saved - the right side " +
                    "runs on its own error branch.");
            if (endsAfter is null or < 2)
                return new("didNotLand", null, true, true,
                    "Connect Wire for the right side reported no error and the Unregister input " +
                    "carries no wire afterwards.");
            if (broken == true)
                return new("broken", null, true, true,
                    "The right-side wire landed and LabVIEW reports it as BROKEN.");
            return new("wired", null, false, true,
                $"wired from the structure's source Event Registration Refnum terminal to " +
                $"Unregister For Events, a wire of {endsAfter} ends; LabVIEW made the tunnels on " +
                "the way out.");
        }

        if (unregisterNodes == 0)
            return new("skipped", "noUnregisterForEvents", false, false,
                "no Unregister For Events node (no node of class Function with a sink named " +
                "'event registration refnum') anywhere on the block diagram, so there was " +
                "nothing to wire the right side to.");
        if (unregisterNodes > 1)
            return new("refused", "multipleUnregisterForEvents", false, false,
                $"REFUSED: {unregisterNodes} nodes of class Function carry a sink named " +
                "'event registration refnum' (Unregister For Events; Flush Event Queue is " +
                "spelled the same way, unmeasured), and which one belongs to this structure " +
                "is not something this tool decides. Wire the right side by hand.");
        if (wiredBefore == true)
            return new("alreadyWired", null, false, false,
                "the Unregister node's input ALREADY carried a wire, so the right side was a " +
                "no-op.");
        if (sourceFound == false)
            return new("skipped", "noRightSourceTerminal", false, false,
                "the Event Structure has no SOURCE terminal named Event Registration Refnum, so " +
                "there was nothing to wire from.");
        return new("skipped", "notAttempted", false, false,
            "the helper did not attempt the right side and its indicators do not say why.");
    }

    /// <summary>
    /// The VI AS GENERATED, extracted before the wiring save: each user-event frame's
    /// <c>dynIndex</c> is still the generation step's there, and the save destroys it. Reading a
    /// file LabVIEW holds is fine. Null when pylabview is not provisioned or the extract fails -
    /// the finishing step then refuses several frames as it always did, rather than guessing.
    /// </summary>
    private async Task<(string Directory, string BaseName)?> ExtractBeforeWiringAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        if (PyLabview.Locate() is null) return null;
        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "userevents",
            Path.GetFileNameWithoutExtension(viPath) + "-before-" + Environment.ProcessId + "-"
            + Guid.NewGuid().ToString("N")[..7]);
        Directory.CreateDirectory(directory);
        var json = await new PyLabviewTools(connection)
            .ExtractAsync(viPath, directory, annotate: false, timeoutSeconds, ct);
        if (JsonNode.Parse(json)?["mainXml"]?.GetValue<string>() is { Length: > 0 } mainXml)
            return (directory, Path.GetFileNameWithoutExtension(mainXml));
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        return null;
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
    /// The measured signature of "no project is active": the helper's error chain names a Property
    /// Node, its own error code is 1055 - or did not arrive, as it never did while the helper's
    /// `code` indicator was an int32 (before 2026-10-13) - and the class list is empty or one empty
    /// string. A failure further down the chain leaves that list POPULATED, because it is filled
    /// four nodes after the project hop - which is what makes the emptiness the discriminator,
    /// and what keeps a 1055 from the refnum search (noRefnumSourceFound) out of this verdict.
    /// </summary>
    internal static bool LooksLikeNoActiveProject(
        int? code, string? helperErrorSource, IReadOnlyList<string>? topClasses)
    {
        if (code is not null and not 1055) return false;
        if (helperErrorSource is null ||
            !helperErrorSource.Contains("Property Node", StringComparison.OrdinalIgnoreCase))
            return false;
        return topClasses is null
            || topClasses.Count == 0
            || topClasses.All(string.IsNullOrWhiteSpace);
    }

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
                                     bool? wiredBefore, int? endsAfter, bool? broken,
                                     string? helperErrorSource = null,
                                     IReadOnlyList<string>? topClasses = null,
                                     int? eventStructures = null)
    {
        // MEASURED 2026-09-12 with the project deliberately closed: errorCode 91 out of the RPC,
        // helperErrorSource "Property Node in lvai_wire_dyn_events.vi", every array output empty,
        // the VI byte-identical, 28 ms. The helper's FIRST node is a {LV.Application}
        // `Project:Active Project` read, which answers Error 1055 when no project is active, and
        // everything downstream is skipped - which is why the class list comes back empty.
        //
        // The source string alone does NOT identify it: the helper has some twenty Property Nodes
        // and LabVIEW names them all "Property Node in <vi>". So the EMPTY CLASS LIST is the
        // discriminator and this verdict is an inference, which the note says outright.
        if (LooksLikeNoActiveProject(code, helperErrorSource, topClasses))
            return new(false, "noActiveProject", false,
                "The helper could not reach the IDE's application instance, and the overwhelmingly " +
                "likely cause is that NO PROJECT IS ACTIVE - open the VI's .lvproj with " +
                "lvai_open_file and call this again. This tool reaches the VI through " +
                "Application -> Project:Active Project, which answers Error 1055 with no project " +
                "open, and that read is the FIRST node in the chain, so nothing downstream ran and " +
                "every list here is empty. Your VI has not been read, let alone changed: a " +
                "registrationNodesOnDiagram of 0 and an empty eventStructureTerminals are this " +
                "failure talking, NOT a verdict that the diagram lacks an Event Structure. " +
                "Inferred from the empty class list rather than read off the message - the helper " +
                "has some twenty Property Nodes and LabVIEW names them all alike.");

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
                "took for a structure, a tunnel or a node was none of them. Read topLevelClasses " +
                "and eventStructuresFound.");

        if (code != 0)
            return new(false, "helperReportedError", false,
                $"The helper's chain reported error {code}; helperErrorSource names the node. " +
                "Nothing was saved after a failing node, so the VI is as it was.");

        if (found == false)
            return new(false, "noEventStructureFound", false,
                "No Event Structure was found anywhere on the block diagram: the search is NI's " +
                "Traverse for GObjects.vi with Class Name EventStructure, which walks every " +
                "structure at any depth. topLevelClasses lists what the TOP-LEVEL diagram holds. " +
                "If the VI plainly has one, suspect the copy that was searched before the depth - " +
                "the VI is opened in the active project's application instance.");

        // Traverse lists every Event Structure; the wiring below takes the FIRST. Which one that
        // is has not been measured, so a VI with several says so rather than looking settled.
        var several = eventStructures > 1
            ? $" NOTE: {eventStructures} Event Structures were found and only the FIRST that " +
              "Traverse for GObjects listed was wired - which one that is has not been measured, " +
              "so render the diagram to see which."
            : "";

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
                "Re-running is safe; that is what makes the tool idempotent." + several);

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
            "border produces." + several);
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

    /// <summary>
    /// A run over ActiveX as NI's RunVIAsTopLevel outputs: each indicator by name, a scalar as its
    /// text, an array or cluster as LabVIEW's flattened XML - the shape the parsers here read.
    /// </summary>
    internal static Dictionary<string, string> OutputsOf(JsonObject? run)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (run?["values"] is not JsonObject values) return map;
        foreach (var (name, node) in values)
        {
            if (node is not JsonObject v) continue;
            var type = (string?)v["type"] ?? "";
            var text = type is "Array" or "Cluster" ? (string?)v["xml"] : v["value"]?.ToString();
            if (text is not null) map[name] = text;
        }
        return map;
    }

    private static string? Text(IDictionary<string, string> outputs, string name) =>
        outputs.TryGetValue(name, out var value) ? value : null;

    private static bool? Flag(IDictionary<string, string> outputs, string name) =>
        ParseFlag(Text(outputs, name));

    private static int? Number(IDictionary<string, string> outputs, string name) =>
        ParseNumber(Text(outputs, name));

    /// <summary>
    /// A boolean out of the helper. Every boolean and number indicator of the helper is a STRING
    /// holding LabVIEW's Flatten To XML of the value (&lt;Boolean&gt;…&lt;Val&gt;TRUE&lt;/Val&gt;…):
    /// measured 2026-10-13, RunVIAsTopLevel answers Error 91 at Variant To Data for a bool or int32
    /// indicator and returns it EMPTY, so `found`, `code` and the whole right side always read null
    /// - the reason `rightSide` said "unknown" on every run and a re-run said helperDidNotAnswer.
    /// A bare "TRUE"/"1" is still read, for an older helper.
    /// </summary>
    internal static bool? ParseFlag(string? text) =>
        FlattenedScalar(text) is { Length: > 0 } value
            ? value is "1" or "T" or "TRUE" or "true" or "True"
            : null;

    /// <summary>A number out of the helper; see <see cref="ParseFlag"/>.</summary>
    internal static int? ParseNumber(string? text) =>
        FlattenedScalar(text) is { Length: > 0 } value && int.TryParse(value, out var n) ? n : null;

    /// <summary>The &lt;Val&gt; of a flattened scalar, or the trimmed text when it is not XML.</summary>
    private static string? FlattenedScalar(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('<')) return trimmed;
        try
        {
            return XElement.Parse(trimmed).Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Val")?.Value.Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>Under TEMP, for the reason IconTools records: Save:Instrument fails elsewhere.</summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(AixmlBackend.HelperFolder(), "lvai_wire_dyn_events.vi");

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
