using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Running a VI and actually READING ITS RESULT - the second COMPOSED tool, after
/// lvai_set_vi_icon, and for the same kind of reason: the plain RPC cannot do it.
///
/// RunVIAsTopLevel marshals only strings, so a boolean, cluster, array or waveform indicator
/// comes back empty with errorCode 91 AFTER the VI has already run correctly. Every generated
/// VI with a non-string output therefore verifies to an empty answer, and the repository's own
/// rule - "never report success from an empty answer" - leaves the caller building a VI Server
/// harness by hand. This is that harness, shipped: scripts\lvai_run_and_read.xml sets the
/// inputs, runs the target and flattens all its values to XML, so everything returns through a
/// single STRING indicator that RunVIAsTopLevel can carry.
///
/// Measured, and the reason set-run-read must be ONE call: a RunVIAsTopLevel followed by a
/// separate read of the same VI returns the target's DEFAULTS, not the values of that run. The
/// two calls do not share the VI's data space. Details in docs/vi-server-reference.md.
/// </summary>
[McpServerToolType]
internal sealed class RunTools(LvaiConnection connection)
{
    /// <summary>
    /// Name of the helper's AIXML source inside the scripts folder.
    /// THE TYPED ONE IS THE DEFAULT since 2026-09-16. Its predecessor wired the incoming string
    /// straight into Ctrl Val.Set, whose Value terminal is a Variant, so only a STRING control
    /// could be set - and five separate agents each worked around that by generating throwaway
    /// copies of the VI under test with the values baked into the control defaults. The typed
    /// helper asks each control what it is (Class Name: String, Path, Digital, Boolean) and
    /// converts before setting. Measured on one control of each type: path, DBL, I32 and boolean
    /// all set correctly, the I32 from a DBL variant, which is why numerics need one case rather
    /// than one per representation.
    /// </summary>
    internal const string HelperAixmlFileName = "lvai_run_and_read_typed.xml";

    /// <summary>
    /// The string-only predecessor. Still shipped and still reachable through helperAixmlPath,
    /// because it is the fallback if the typed helper ever refuses to generate on a station.
    /// </summary>
    internal const string LegacyHelperAixmlFileName = "lvai_run_and_read.xml";

    [McpServerTool(Name = "lvai_run_vi_and_read_values", Destructive = true, OpenWorld = true,
                   Title = "Run a VI and read every control and indicator value back")]
    [Description("""
        MUTATING: EXECUTES the VI, with whatever side effects it has.
        Use this instead of lvai_run_vi_as_top_level whenever the VI has ANY output that is not
        a string - boolean, numeric, cluster, array, waveform. That tool returns those empty
        with errorCode 91 after the VI has run; this one returns their real values, because the
        helper reads them through VI Server and flattens them to XML before they cross back.
        Values are returned per control name with a type and, for scalars, a plain text value;
        compound values keep their flattened XML.
        INPUTS ARE STILL WRITTEN AS TEXT, BUT THEY ARE NO LONGER LIMITED TO STRING CONTROLS.
        The helper reads the target's panel, asks each named control what it is, and converts
        before setting - so a path, a double, an integer and a boolean can all be set by passing
        their text ("C:\\data\\in.csv", "12.5", "42", "true"). Measured 2026-09-16 on one control
        of each type.
        AN ENUM OR A TEXT RING takes an item NAME ("withdraw", exact and case-sensitive) or an
        index ("2"). Text that is neither is refused by Ctrl Val.Set BEFORE the run - it used to
        be the case that nothing but a string could be set at all.
        AN ARRAY OR A CLUSTER takes LabVIEW's own XML for the value - exactly what this tool
        returns under a compound control's `xml`, so a value read back can be pasted in as the
        next call's input. A bare <Array> or <Cluster> is wrapped in <LvVariant> for you, and
        the indentation between its tags is folded away; the helper then turns it into a Variant
        that carries its own type, so you never name the type. Since 2026-09-25. Do not write a
        line break as &#10; there - Unflatten From XML decodes no character reference, measured.
        AN EMPTY ARRAY NEEDS ONE ELEMENT AS A TYPE TEMPLATE, as LabVIEW writes it: Dimsize 0 plus
        e.g. <String><Name></Name><Val></Val></String>. Without it the helper cannot tell the type
        (Error 1103), so it is refused here (emptyArrayWithoutTemplate). Measured 2026-10-01.
        A LINE BREAK IN A VALUE IS FINE - a multi-line string, or an XML value with a multi-line
        member: it travels encoded (LF as 0x1E, CR as 0x1D) and the helper decodes it before it
        converts anything; `lineBreaksEncoded` names the inputs it applied to. Only the default
        helper decodes, so with runForMs or the legacy helper a line break is still refused. A
        line break in a control NAME is always refused - names and values are paired by line.
        A control name that matches nothing on the target's panel is Error 1055 and the target
        does NOT run - watch helperFailed, because errorCode is RunVIAsTopLevel's and reads 0.
        Reading is done through a VI REFERENCE, which is released afterwards, so this does not
        burn the target's path for a later lvai_convert_aixml_to_vi.
        """)]
    public async Task<string> RunViAndReadValuesAsync(
        [Description(@"Absolute path to the .vi to run")] string viPath,
        [Description("""
            Control values as a JSON object, e.g. {"file name":"C:\\data\\in.csv","count":"42"}.
            Keys are control labels, values are always TEXT - the helper converts each one to
            whatever type the named control actually is. String, path, numeric, boolean, enum
            and ring controls take plain text; an array or cluster control takes its value as
            LabVIEW XML (<Array>…</Array>, <Cluster>…</Cluster>, or wrapped in <LvVariant>).
            Omit for a VI that needs no inputs.
            """)]
        string? inputsJson = null,
        [Description("""
            Also return the helper's raw flattened XML under valuesXml. Off by default because
            it repeats everything in values; it is returned regardless when parsing yields
            nothing, so a parse failure never costs you the data.
            """)]
        bool includeRawXml = false,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user cache directory,
            because the scripts folder next to the exe may be read-only. Generated once and
            reused; pass regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvai_run_and_read.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds - raise it for long-running VIs")]
        int timeoutSeconds = 300,
        [Description("""
            Run the VI for this many milliseconds, read its front panel, then ABORT it - instead of
            waiting for it to finish. Zero, the default, keeps the original behaviour.
            THIS IS THE ONLY WAY TO LOOK AT A VI THAT NEVER ENDS, which is every event or polling
            loop and therefore every real application. Without it such a VI times out the call and
            keeps running, and the caller has to generate a scriptable copy of their own program to
            test it at all.
            TWO THINGS TO KNOW. The values are a SNAPSHOT at the moment the time elapsed, not a
            result: a loop that has not reached its second state yet shows the first. And Abort is
            not a stop button - it kills the VI where it stands and runs NO cleanup the diagram may
            contain, so a VI that closes files or releases hardware on its normal path does not do
            that here.
            IT ALSO ANSWERS `disabled`: every control's label with its Disabled state at the snapshot
            (0 enabled, 1 disabled, 2 disabled and greyed out) - the one thing a snapshot of values
            cannot show, and nothing can look afterwards because the VI leaves memory. Since
            2026-09-26; null when that read failed.
            IT PICKS ITS OWN HELPER, and a helperAixmlPath naming the untimed one is corrected
            rather than obeyed - the answer says so in `helperOverridden`. Only lvai_run_for_ms.vi
            has a `run for ms` control; lvai_run_and_read.vi waits for the target to finish, so
            aiming this at it while asking for runForMs waits for a VI that never ends and BLOCKS
            THE WHOLE SERVICE until LabVIEW is killed. Measured 2026-09-16; it cost two restarts.
            """)]
        int runForMs = 0,
        [Description("""
            EVENTS TO FIRE while the VI runs, with runForMs only: a JSON ARRAY, in order, e.g.
            [{"control":"Card Simulator","value":"true"},{"control":"User Input","value":"23456"}].
            After runForMs has elapsed each control is written with Value (Signaling) - which fires
            its Value Change event exactly as a click or a keystroke would - and signalGapMs is
            waited after each one; the snapshot comes after the last. Values are text: a boolean
            TRUE/1, a numeric its number, a string its text, a cluster or array LabVIEW XML.
            THIS IS WHAT A START-UP SNAPSHOT CANNOT SEE. The seventh ATM build passed validation,
            execState, 58 unit tests and a runForMs snapshot while its consumer read User Input
            before the user had typed; only driving an event would have shown it.
            A LATCHED BOOLEAN CANNOT BE SIGNALLED - LabVIEW refuses Value (Signaling) on one with
            Error 1193, reported under `signals`. The snapshot is taken either way.
            """)]
        string? signalsJson = null,
        [Description("Wait after each signal, so the target handles it before the next")]
        int signalGapMs = 500,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            List<KeyValuePair<string, string>> signals;
            try { signals = ParseSignals(signalsJson); }
            catch (ArgumentException bad)
            {
                return Json.Error("badArguments", bad.Message, new { signalsJson });
            }
            if (signals.Count > 0 && runForMs <= 0)
                return Json.Error("badArguments",
                    "signalsJson needs runForMs: signals are fired while the VI RUNS, after runForMs " +
                    "has elapsed, and a VI run without runForMs is waited on until it ends.",
                    new { signalCount = signals.Count, runForMs });

            var inputs = Rpc.ParseStringMap(inputsJson, nameof(inputsJson)).ToList();
            var compoundInputs = inputs.Where(i => CompoundValue(i.Value) is not null)
                                       .Select(i => i.Key).ToList();
            if (inputs.FirstOrDefault(i => CompoundValue(i.Value) is { } x && EmptyArrayWithoutTemplate(x))
                    .Key is { } untyped)
                return Json.Error("emptyArrayWithoutTemplate",
                    $"The value for '{untyped}' is an EMPTY array with no element in it. Unflatten From " +
                    "XML takes the element type from an element, so the helper would refuse it with " +
                    "Error 1103 before the VI runs. Write it the way LabVIEW does: Dimsize 0 plus ONE " +
                    "empty element as a type template, e.g. <Array><Name>x</Name><Dimsize>0</Dimsize>" +
                    "<String><Name></Name><Val></Val></String></Array>. Measured 2026-10-01.",
                    new { controlName = untyped });
            inputs = inputs.Select(i => new KeyValuePair<string, string>(
                i.Key, CompoundValue(i.Value) ?? i.Value)).ToList();
            if (inputs.FirstOrDefault(i => Breaks(i.Key)).Key is { } badName)
                return Json.Error("inputContainsNewline",
                    $"The control name '{badName}' contains a line break. The helper pairs names " +
                    "with values by line, so a newline in a name would silently shift every later " +
                    "pair onto the wrong control.",
                    new { controlName = badName });

            var timed = runForMs > 0;

            // A CALLER-SUPPLIED HELPER PATH USED TO DEFEAT runForMs SILENTLY, and what it cost was
            // not a wrong answer but a WEDGED SERVICE. lvai_run_and_read.vi wires
            // `Wait until done` = TRUE, so aiming this call at it while asking for runForMs makes
            // the helper wait for a VI that never ends - and since the gRPC service is what runs
            // the helper, every later lvai_* call then answers DeadlineExceeded until LabVIEW is
            // killed. Measured 2026-09-16 on a top-level UI loop, which is the shape runForMs
            // exists for, so the trap sat exactly where the feature is useful.
            // IT IS NOT A CALLER MISTAKE THAT CAN BE LEFT TO THE CALLER: a client that treats every
            // declared parameter as required sends helperAixmlPath on every call, so the default
            // never applies and runForMs is unreachable through that client.
            // The check is on CONTENT rather than on the file name, because the fact that decides
            // it is whether the helper has a `run for ms` control at all - a renamed copy of the
            // untimed helper has the same defect and a matching name would have missed it.
            string? helperOverriddenBecause = null;
            if (timed && !CanHonourRunForMs(helperAixmlPath))
            {
                helperOverriddenBecause =
                    $"'{Path.GetFileName(helperAixmlPath)}' has no 'run for ms' control, so it " +
                    $"cannot stop the target - it waits for a VI that never ends and blocks this " +
                    $"service. {TimedHelperAixmlFileName} was used instead.";
                helperAixmlPath = null;
                helperViPath = null;   // both, or the timed helper overwrites the untimed VI cache
            }

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath(timed)
                ?? throw new FileNotFoundException(
                    $"The helper's AIXML source could not be located: no scripts folder next to " +
                    $"the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {(timed ? TimedHelperAixmlFileName : HelperAixmlFileName)}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath(timed));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            // A LINE BREAK IN A VALUE TRAVELS ENCODED, when the helper decodes it. Values used to be
            // refused outright - which made a multi-line string unreachable here and, through this
            // tool, in lvai_set_constant: the fourth ATM cold build could not break a multi-line
            // message expectation for its negative control. The typed helper decodes 0x1E to LF and
            // 0x1D to CR before it converts anything; the timed and the legacy helper do not, so
            // for them the refusal stands. Decided from the helper's CONTENT, like runForMs.
            var decodes = DecodesLineBreaks(aixml);
            if (inputs.FirstOrDefault(i => Breaks(i.Value) && !decodes).Key is { } undecodable)
                return Json.Error("inputContainsNewline",
                    $"The value for '{undecodable}' contains a line break, and the helper " +
                    $"'{Path.GetFileName(aixml)}' does not decode one - only " +
                    $"{HelperAixmlFileName} does{(timed ? "; the runForMs helper has not had that change" : "")}. " +
                    "The helper pairs names with values by line, so the break would shift every " +
                    "later pair onto the wrong control.",
                    new { controlName = undecodable });
            if (inputs.FirstOrDefault(i => i.Value.Contains(EncodedLf) || i.Value.Contains(EncodedCr)).Key
                is { } ambiguous)
                return Json.Error("badArguments",
                    $"The value for '{ambiguous}' contains the control character 0x1E or 0x1D, " +
                    "which is how a line break travels to the helper - it would arrive as a line " +
                    "break. Remove it.", new { controlName = ambiguous });
            var encodedInputs = inputs.Where(i => Breaks(i.Value)).Select(i => i.Key).ToList();
            inputs = [.. inputs.Select(i => new KeyValuePair<string, string>(i.Key, Encode(i.Value)))];

            var helperGenerated = false;
            // A helper built from an OLDER AIXML is rebuilt, not reused: the Enum, Ring, Array
            // and Cluster frames went into the AIXML on 2026-09-25, and a cached VI from before
            // that would have kept refusing them with no sign that a newer helper exists.
            if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            // AN EMPTY VALUE MISALIGNS EVERY INPUT AFTER IT, so it is refused rather than sent.
            // The two lists below are joined with newlines and paired BY POSITION inside the
            // helper, where Spreadsheet String To Array does not yield an element for an empty
            // line - so one empty value shortens the Values list and every later name receives
            // its neighbour's value. Measured 2026-08-27: an accessor call passing an empty
            // `virtual folder` in the middle of nine inputs left the two after it unset, the
            // helper kept their defaults, and a request for 2 fields silently built as many as
            // the clock allowed. It went unnoticed for as long as the empty value happened to be
            // LAST, which is exactly the kind of latent fault that surfaces on an unrelated
            // change.
            if (inputs.FirstOrDefault(i => i.Value.Length == 0) is { Key: { Length: > 0 } empty })
                return Json.Error("badArguments",
                    $"Input '{empty}' has an empty value. Names and values are paired by " +
                    "POSITION, and an empty value does not survive the helper's split - it would " +
                    "shift every input after it onto the wrong control. Omit the input instead: a " +
                    "control that is not set keeps its own default.",
                    new { inputName = empty, inputCount = inputs.Count });

            if (signals.Count > 0 && !CanSignal(aixml))
                return Json.Error("badArguments",
                    $"The helper '{Path.GetFileName(aixml)}' has no '{SignalNamesControlName}' " +
                    $"control, so it cannot fire signals. Omit helperAixmlPath to use " +
                    $"{TimedHelperAixmlFileName}, which can.", new { helperAixmlPath = aixml });

            var request = HelperRequest(helperVi, viPath, inputs, timed ? runForMs : 0);
            if (signals.Count > 0)
            {
                request.Inputs[SignalNamesControlName] = string.Join("\n", signals.Select(s => s.Key));
                request.Inputs["Signal Values"] = string.Join("\n", signals.Select(s => s.Value));
                request.Inputs["signal gap ms"] =
                    Math.Max(0, signalGapMs).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            response.Outputs.TryGetValue("values xml", out var valuesXml);
            response.Outputs.TryGetValue("error xml", out var errorXml);

            var values = LvValuesXml.Parse(valuesXml);
            // Withheld only when it would be pure duplication. If nothing parsed, the raw text
            // is the entire result and hiding it would be the empty answer this tool exists
            // to prevent.
            var keepRaw = includeRawXml || values.Count == 0;

            var payload = Json.Node(response).AsObject();
            // The helper's two indicators are re-exposed below as `values`/`valuesXml` and
            // `helperErrorXml`, so leaving the protobuf map in place would ship the whole
            // flattened XML a second time - doubling the payload on exactly the big waveforms
            // this tool exists to return, and quietly breaking includeRawXml's promise.
            payload.Remove("outputs");

            payload["values"] = LvValuesXml.ToJson(values);
            payload["valueCount"] = JsonValue.Create(values.Count);
            payload["valuesXml"] = keepRaw ? JsonValue.Create(valuesXml) : null;
            payload["helperErrorXml"] = JsonValue.Create(errorXml);
            var helperCode = HelperErrorCode(errorXml);
            payload["helperErrorCode"] = helperCode is { } hc ? JsonValue.Create(hc) : null;
            payload["helperFailed"] = JsonValue.Create(helperCode is not null and not 0);
            payload["helperViPath"] = JsonValue.Create(helperVi);
            payload["helperAixmlPath"] = JsonValue.Create(Path.GetFullPath(aixml));
            payload["helperGenerated"] = JsonValue.Create(helperGenerated);
            payload["inputsSent"] = JsonValue.Create(inputs.Count);
            if (compoundInputs.Count > 0)
                payload["compoundInputs"] = new JsonArray(compoundInputs
                    .Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            if (encodedInputs.Count > 0)
                payload["lineBreaksEncoded"] = new JsonArray(encodedInputs
                    .Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            payload["elapsedMs"] = JsonValue.Create(stopwatch.ElapsedMilliseconds);
            payload["runForMs"] = JsonValue.Create(timed ? runForMs : 0);
            if (timed)
            {
                response.Outputs.TryGetValue("disabled labels xml", out var disabledLabels);
                response.Outputs.TryGetValue("disabled xml", out var disabledStates);
                payload["disabled"] = DisabledStates(disabledLabels, disabledStates);
            }
            if (signals.Count > 0)
            {
                response.Outputs.TryGetValue("signal error xml", out var signalErrorXml);
                var signalCode = HelperErrorCode(signalErrorXml);
                payload["signals"] = new JsonObject
                {
                    ["sent"] = new JsonArray(signals.Select(s => (JsonNode?)new JsonObject
                    {
                        ["control"] = s.Key, ["value"] = s.Value,
                    }).ToArray()),
                    ["gapMs"] = Math.Max(0, signalGapMs),
                    ["errorCode"] = signalCode is { } sc ? JsonValue.Create(sc) : null,
                    ["errorXml"] = signalErrorXml,
                    ["note"] = signalCode switch
                    {
                        null or 0 => "Every signal was written; the snapshot was taken after the last gap.",
                        1193 => "Error 1193: a LATCHED boolean cannot be written with Value (Signaling). " +
                                "The signals after it were NOT sent; the snapshot was still taken. Test " +
                                "what that button triggers through the handler's own unit test instead.",
                        1055 or 1026 => $"Error {signalCode}: a signal names no control on the target's panel. " +
                                        "The signals from there on were NOT sent; the snapshot was still taken.",
                        _ => $"Error {signalCode} stopped the signals at that point; the snapshot was " +
                             "still taken. errorXml names the source.",
                    },
                };
            }
            if (helperOverriddenBecause is { } why) payload["helperOverridden"] = JsonValue.Create(why);
            payload["note"] = JsonValue.Create(
                "errorCode here is RunVIAsTopLevel's, NOT the helper's - read helperErrorCode " +
                "for that, and helperFailed when you want one flag. A target VI that itself " +
                "reported an error shows that in its own error out under values." +
                (helperCode is not null and not 0
                    ? $" helperFailed is TRUE ({helperCode}): the helper stopped before the " +
                      "target ran, so `values` is empty and nothing was set. Error 1055 here " +
                      "means a control name matched nothing on the target's panel. Error 91 " +
                      "means a value did not fit its control - on an ENUM or RING, text that is " +
                      "neither one of its item names (exact, case-sensitive) nor a number. A " +
                      "CLASS control cannot be set at all, from text or from XML - measured " +
                      "2026-09-26 - so test a class method with lvai_generate_method_test, whose " +
                      "`seed` builds the object." +
                      (compoundInputs.Count > 0
                          ? " A refusal from Unflatten From XML means the XML given for " +
                            string.Join(", ", compoundInputs) + " is not a LabVIEW value - " +
                            "copy the `xml` this tool returns for that control."
                          : "")
                    : "") +
                (timed
                    ? $" These values are a SNAPSHOT taken {runForMs} ms after the VI started, and " +
                      "the VI was then ABORTED - no cleanup on its diagram ran." +
                      (inputs.Count > 0
                          ? " AND THE TIMED HELPER STILL SETS STRINGS ONLY - it has not been given " +
                            "the typed setter, so a path, numeric or boolean control named here " +
                            "keeps its own default instead."
                          : "")
                    : ""));

            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>
    /// Every front-panel control's <c>Disabled</c> at the snapshot, as <c>[{label, disabled}]</c> -
    /// or null when the helper did not read it (an older helper, or a read that failed on its own
    /// error chain).
    ///
    /// ADDED 2026-09-26, because a snapshot of VALUES could not answer "which controls can the
    /// user operate right now", and nothing can look afterwards: the helper closes its reference
    /// and the VI leaves memory. The sixth ATM build had to generate a scratch probe that ran the
    /// main VI itself to see that six controls read 2. A list rather than a map, because two
    /// controls may carry one label.
    /// </summary>
    internal static JsonArray? DisabledStates(string? labelsXml, string? statesXml)
    {
        static List<string>? Vals(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var root = System.Xml.Linq.XElement.Parse(xml);
                return [.. root.Elements()
                               .Where(e => e.Name.LocalName is not ("Name" or "Dimsize"))
                               .Select(e => (string?)e.Element("Val") ?? "")];
            }
            catch (System.Xml.XmlException) { return null; }
        }

        var labels = Vals(labelsXml);
        var states = Vals(statesXml);
        if (labels is null || states is null || labels.Count != states.Count || labels.Count == 0)
            return null;

        var list = new JsonArray();
        for (var i = 0; i < labels.Count; i++)
            list.Add(new JsonObject
            {
                ["label"] = labels[i],
                ["disabled"] = int.TryParse(states[i], out var d) ? d : null,
            });
        return list;
    }

    private static readonly Regex CompoundRoot = new(
        @"^\s*(?:<\?xml[^>]*\?>\s*)?<(LvVariant|Array|Cluster)[\s>]", RegexOptions.CultureInvariant);

    /// <summary>
    /// A value given as LabVIEW XML, made fit for the helper's Array and Cluster frames - or null
    /// when the value is not XML for a compound, in which case it is sent exactly as given.
    ///
    /// The helper feeds it to Unflatten From XML with a VARIANT as the type, which yields a
    /// variant carrying the value's own type - measured 2026-09-25 on a 2D string array, round
    /// trip exact. Two things have to happen first. The value must be ONE line, because names and
    /// values are paired by line, so indentation between tags is dropped. And the root must be
    /// <c>LvVariant</c>, because that is what Unflatten From XML into a variant reads; a bare
    /// <c>&lt;Array&gt;</c> or <c>&lt;Cluster&gt;</c> - the shape this tool returns under a
    /// control's <c>xml</c> - is wrapped, so a value read back can be passed straight in.
    ///
    /// A LINE BREAK INSIDE A TEXT VALUE IS LEFT IN, so the newline guard refuses it. The first
    /// version wrote it as <c>&amp;#10;</c>, and LabVIEW's Unflatten From XML does NOT decode a
    /// character reference: measured 2026-09-25 on an error cluster, the source came back as the
    /// literal text <c>acceptance&amp;#10;second line</c> with no error anywhere. LabVIEW's own XML
    /// carries a raw line break there, which this wire format cannot.
    ///
    /// ONLY THOSE THREE ROOTS are recognised. A string control may legitimately be given text that
    /// starts with an angle bracket, and rewriting that would change the string.
    /// </summary>
    internal static string? CompoundValue(string value)
    {
        var root = CompoundRoot.Match(value);
        if (!root.Success) return null;

        var xml = value.Trim();
        if (xml.StartsWith("<?xml", StringComparison.Ordinal))
            xml = xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..].TrimStart();
        xml = Regex.Replace(xml, @">[ \t]*(?:\r\n|\r|\n)\s*<", "><");

        return root.Groups[1].Value == "LvVariant"
            ? xml
            : $"<LvVariant><Name>Variant</Name>{xml}</LvVariant>";
    }

    /// <summary>
    /// Whether a compound value holds an array with a dimension of 0 that carries no
    /// element. LabVIEW's own XML for an empty array keeps one element as a type template, and
    /// Unflatten From XML into a variant needs it: without it the helper answers Error 1103.
    /// Measured 2026-10-01 through lvai_graft_diagram, which wrote empty label lists that way.
    /// </summary>
    internal static bool EmptyArrayWithoutTemplate(string xml)
    {
        try
        {
            return System.Xml.Linq.XElement.Parse(xml).DescendantsAndSelf("Array").Any(a =>
                a.Elements("Dimsize").Any(d => d.Value.Trim() == "0")
                && !a.Elements().Any(e => e.Name.LocalName is not ("Name" or "Dimsize")));
        }
        catch (System.Xml.XmlException) { return false; }
    }

    /// <summary>What a line feed and a carriage return travel as between this tool and the helper.</summary>
    internal const string EncodedLf = "\u001E", EncodedCr = "\u001D";

    /// <summary>The constant only a helper that decodes line breaks carries; its presence is the test.</summary>
    internal const string LineBreakMarker = "encoded line feed";

    /// <summary>A value with its line breaks encoded for the wire - the helper reverses it.</summary>
    internal static string Encode(string value) =>
        value.Replace("\r", EncodedCr).Replace("\n", EncodedLf);

    /// <summary>
    /// Whether a helper decodes line breaks, read from its AIXML like <see cref="CanHonourRunForMs"/>.
    /// An unreadable file is treated as NOT decoding: the cost of that guess is a refusal the caller
    /// can read, where the opposite guess would split a value across two controls.
    /// </summary>
    internal static bool DecodesLineBreaks(string helperAixmlPath)
    {
        try
        {
            return File.ReadAllText(helperAixmlPath)
                .Contains($"_name=\"{LineBreakMarker}\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Breaks(string? s) =>
        s is not null && (s.Contains('\n') || s.Contains('\r'));

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload - Error 1051 in particular is unrecoverable without changing the target name.
    /// Same shape as IconTools; the two composed tools fail in the same two ways.
    /// </summary>
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
                hint = generation.ErrorCode switch
                {
                    1051 => "Error 1051 means a VI of that name is already in LabVIEW's memory - " +
                            "and a failed generation leaves the name occupied for the rest of the " +
                            "session. Pass a different helperViPath, or restart LabVIEW.",
                    7 => "Error 7 is LabVIEW refusing to save into " +
                         $"'{Path.GetDirectoryName(helperVi)}'. The directory does exist - this " +
                         "tool creates it - so the location itself is being refused; that has been " +
                         "measured under %LOCALAPPDATA%. Pass helperViPath somewhere else, " +
                         "somewhere under %TEMP% for instance.",
                    _ => null,
                },
            });
    }

    /// <summary>The timed runner's control that takes the signal list; its presence is the test.</summary>
    internal const string SignalNamesControlName = "Signal Names";

    /// <summary>
    /// The signals to fire, in order. A JSON ARRAY rather than an object, because order is the
    /// point - a card goes in before its account number is typed. Refused rather than guessed:
    /// an unknown key, a missing control, and a line break or an empty value anywhere, because
    /// names and values are paired BY LINE inside the helper, as the inputs are.
    /// </summary>
    internal static List<KeyValuePair<string, string>> ParseSignals(string? signalsJson)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(signalsJson)) return list;

        JsonNode? root;
        try { root = JsonNode.Parse(signalsJson); }
        catch (JsonException bad)
        {
            throw new ArgumentException($"signalsJson is not JSON: {bad.Message}");
        }
        if (root is not JsonArray array)
            throw new ArgumentException(
                "signalsJson must be a JSON ARRAY of {\"control\":…,\"value\":…}, in order - an object " +
                "cannot say which signal comes first.");

        foreach (var item in array)
        {
            if (item is not JsonObject entry)
                throw new ArgumentException("Every signal must be an object {\"control\":…,\"value\":…}.");
            if (entry.Select(p => p.Key).FirstOrDefault(k => k is not ("control" or "value")) is { } unknown)
                throw new ArgumentException(
                    $"Unknown key '{unknown}' in a signal; the only keys are control and value.");
            var control = entry["control"]?.ToString();
            var value = entry["value"] is JsonValue v ? v.ToString() : entry["value"]?.ToJsonString();
            if (string.IsNullOrEmpty(control))
                throw new ArgumentException("A signal has no 'control'.");
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException(
                    $"The signal for '{control}' has no value. Names and values are paired by line, " +
                    "so an empty value would shift every later signal onto the wrong control.");
            if (Breaks(control) || Breaks(value))
                throw new ArgumentException(
                    $"The signal for '{control}' contains a line break; names and values are paired by line.");
            list.Add(new(control, value));
        }
        return list;
    }

    /// <summary>Whether a helper can fire signals, read from its AIXML like <see cref="CanHonourRunForMs"/>.</summary>
    internal static bool CanSignal(string helperAixmlPath)
    {
        try
        {
            return File.ReadAllText(helperAixmlPath)
                .Contains($"_name=\"{SignalNamesControlName}\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Name of the timed runner's AIXML source inside the scripts folder.</summary>
    internal const string TimedHelperAixmlFileName = "lvai_run_for_ms.xml";

    /// <summary>The control the timed runner takes its budget on; its presence is the test.</summary>
    internal const string RunForMsControlName = "run for ms";

    /// <summary>
    /// The helper call. <paramref name="runForMs"/> above zero is written into the timed helper's
    /// <c>run for ms</c> control, as TEXT because that control is a string.
    ///
    /// IT WAS NEVER WRITTEN AT ALL until 2026-09-25. <c>runForMs</c> chose the timed helper and
    /// was echoed back in the answer, and the helper then ran for its own default of
    /// <c>"1000"</c> whatever was asked: measured on an event-driven main VI, 2500 and 5000 both
    /// returned after ~1.07 s, while the same helper driven directly with 4000 ran 4.06 s. The
    /// snapshot a caller received was always the first second of the run, which on a slow loop
    /// is exactly the state that has not happened yet.
    /// </summary>
    internal static RunVIAsTopLevelRequest HelperRequest(string helperVi, string viPath,
                                                        IReadOnlyList<KeyValuePair<string, string>> inputs,
                                                        int runForMs)
    {
        var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
        request.Inputs["VI Path"] = Path.GetFullPath(viPath);
        request.Inputs["Input Names"] = string.Join("\n", inputs.Select(i => i.Key));
        request.Inputs["Input Values"] = string.Join("\n", inputs.Select(i => i.Value));
        if (runForMs > 0)
            request.Inputs[RunForMsControlName] =
                runForMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return request;
    }

    /// <summary>
    /// Whether a caller-named helper can honour runForMs at all. A null path means the default
    /// applies and the timed default is already the right one; an unreadable file is treated as
    /// capable, because refusing a helper this cannot read would be worse than the status quo.
    /// </summary>
    internal static bool CanHonourRunForMs(string? helperAixmlPath)
    {
        if (helperAixmlPath is not { Length: > 0 }) return true;
        try
        {
            return File.ReadAllText(helperAixmlPath)
                .Contains($"_name=\"{RunForMsControlName}\"", StringComparison.Ordinal);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string? DefaultHelperAixmlPath(bool timed = false) =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, timed ? TimedHelperAixmlFileName : HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP, not %LOCALAPPDATA%: LabVIEW's Save:Instrument fails there with Error 7 even
    /// though the directory exists. Measured for lvai_set_vi_icon; see IconTools for the detail.
    /// </summary>
    private static string DefaultHelperViPath(bool timed = false) =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers",
            timed ? "lvai_run_for_ms.vi" : "lvai_run_and_read_typed.vi");

    /// <summary>
    /// The helper's OWN error code, read out of the error cluster it flattens to XML.
    /// Worth surfacing separately because `errorCode` on the answer is RunVIAsTopLevel's, and
    /// that is 0 for a run the helper refused: measured 2026-09-16, a control name that matches
    /// nothing on the target's panel gives the typed helper Error 1055 from its Class Name
    /// property node, the target never runs, and the only tells were an empty `values` and a
    /// number buried in `helperErrorXml`. A caller reading errorCode alone would call that a
    /// success.
    /// </summary>
    internal static int? HelperErrorCode(string? errorXml)
    {
        if (errorXml is not { Length: > 0 }) return null;
        var match = Regex.Match(errorXml,
            @"<I32>\s*<Name>code</Name>\s*<Val>(-?\d+)</Val>", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }
}
