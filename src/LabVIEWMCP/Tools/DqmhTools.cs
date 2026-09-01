using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a DQMH event, as one call instead of fifteen.
///
/// WHY THIS IS A TOOL. The sequence underneath is fixed - start the dialog, read the module ring,
/// author an arguments carrier, fill the fields, verify the target, paste the controls, focus OK,
/// press it - and every step is a round trip. Measured by hand it cost about fifteen tool calls of
/// which almost all the wall clock was model latency; LabVIEW's own share is seconds. That is the
/// shape this repository calls a tool waiting to be written.
///
/// WHY IT DRIVES A DIALOG AT ALL. `Script New Event.vi` cannot be called from a helper: its
/// `Module Info` holds thirteen refnums, and LabVIEW releases the ones a VI opened when that VI
/// stops, so a parse run as its own top-level VI leaves every one of them dead. Delacor's dialog
/// calls parse and script as subVIs of ONE running VI, which is exactly what keeps them alive.
/// docs/dqmh-scripting.md has the measurements.
///
/// THE ONE STEP THAT IS NOT VI SERVER is the final keypress. The OK button is Latch When Released,
/// LabVIEW refuses `Value (Signaling)` on it with Error 1193, and `Mechanical Action` cannot be
/// rewritten while the VI runs (Error 1073). So the dialog has to be brought to the front and sent
/// a SPACE. That makes this tool unsuitable for an unattended run, and it says so in its answer
/// rather than leaving the caller to find out.
/// </summary>
[McpServerToolType]
internal sealed class DqmhTools(LvaiConnection connection)
{
    private const string DialogRelativePath =
        @"project\Delacor\DQMH\Event\Create New DQMH Event.vi";

    /// <summary>Control indices on the dialog's panel, measured over its sixteen controls.</summary>
    private const int ModuleRingIndex = 0;
    private const int OkButtonIndex = 10;

    private static readonly string[] EventTypes =
        ["Request", "Broadcast", "Request and Wait for Reply", "Round Trip"];

    [McpServerTool(Name = "lvai_dqmh_new_event", Destructive = true, OpenWorld = true,
        Title = "Create a DQMH event")]
    [Description("""
        MUTATING, and it TAKES OVER THE SCREEN for a moment: creates a DQMH request or broadcast
        on a module in the active project, with typed arguments, by driving Delacor's own
        Create New DQMH Event dialog.

        A project must be OPEN AND ACTIVE or every step answers Error 1055.

        NOT UNATTENDED-SAFE. The dialog's OK button is a latched boolean that VI Server may not
        write (Error 1193) and whose Mechanical Action cannot be changed while it runs (Error
        1073), so the last step brings the dialog to the front and sends a SPACE keystroke. It
        will steal the foreground for a second. Everything before that is ordinary VI Server.

        THE MODULE IS MATCHED BY NAME, never by index: the dialog's ring is ordered differently
        depending on how it was launched and puts its placeholder LAST, so an index carried from
        anywhere else aims at the wrong module. The choice is confirmed a second way before the
        button is pressed, by reading the dialog's own step 6 text, which names the target module
        in words.
        """)]
    public async Task<string> NewEvent(
        [Description("Module to add the event to, e.g. 'Heater' or 'Heater.lvlib'. Matched " +
                     "against the dialog's own module list, case-insensitively.")]
        string moduleName,
        [Description("Name of the new event, e.g. 'Do Something Else'")]
        string eventName,
        [Description("""
            Arguments as JSON: [{"name":"Channel","type":"string"},{"name":"Gain","type":"double"}].
            The names and types become the event's Argument--cluster.ctl fields, so they are the
            event's public contract. Pass [] for an event with no arguments.
            Types are AIXML type names - string, double, int32, uint32, bool, path and so on.
            """)]
        string argumentsJson,
        [Description("Event type: Request, Broadcast, Request and Wait for Reply, or Round Trip")]
        string eventType = "Request",
        [Description("Event description. Delacor writes it into the message-handling frame's " +
                     "label, where it documents the event on the module's block diagram.")]
        string description = "",
        [Description("Add a button to the module's API tester that fires this event")]
        bool addTesterButton = true,
        [Description("Local budget in seconds; the scripting itself takes tens of seconds")]
        int timeoutSeconds = 600,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                return Json.Error("badArguments", "moduleName is required.");
            if (string.IsNullOrWhiteSpace(eventName))
                return Json.Error("badArguments", "eventName is required.");

            var typeIndex = Array.FindIndex(EventTypes,
                t => string.Equals(t, eventType, StringComparison.OrdinalIgnoreCase));
            if (typeIndex < 0)
                return Json.Error("badArguments",
                    $"'{eventType}' is not a DQMH event type.",
                    new { eventType, accepted = EventTypes });

            if (ParseArguments(argumentsJson) is not { } arguments)
                return Json.Error("badArguments",
                    "argumentsJson must be a JSON array of {\"name\":…,\"type\":…} objects.",
                    new { argumentsJson });

            if (arguments.FirstOrDefault(a => a.Name.Contains('\n') || a.Name.Contains('\r'))
                is { Name.Length: > 0 } broken)
                return Json.Error("badArguments",
                    $"Argument name '{broken.Name}' contains a line break.");

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing",
                    "No scripts folder next to the exe - lvai_status reports it as " +
                    "scriptsDirectory. The DQMH helpers live there.");

            if (DialogPath() is not { } dialogPath)
                return Json.Error("dqmhMissing",
                    "Delacor DQMH is not installed: no Create New DQMH Event.vi under any " +
                    "LabVIEW installation's project\\Delacor\\DQMH\\Event folder.",
                    new { lookedFor = DialogRelativePath });

            var steps = new JsonArray();
            var stopwatch = Stopwatch.StartNew();

            // ---- 1. the dialog ------------------------------------------------------------
            // REUSE one that is already up. Starting a second dialog leaves two on screen
            // competing for the foreground, and the keystroke at the end would reach whichever
            // won - so an existing one is adopted rather than duplicated.
            var alreadyOpen = Win32.FindWindow("Create New DQMH Event") != IntPtr.Zero;
            if (!alreadyOpen)
            {
                if (await RunAsync(scripts, "lvdqmh_dlg_start",
                        new() { ["dialog vi path"] = dialogPath }, timeoutSeconds, ct)
                    is not { } start) return HelperMissing("lvdqmh_dlg_start");
                steps.Add(Step("startDialog", start));
                if (Failed(start) is { } startError)
                    return Fail("dialogWouldNotStart", startError, steps, stopwatch);
            }
            else
            {
                steps.Add(new JsonObject
                {
                    ["step"] = "startDialog",
                    ["reusedExisting"] = true,
                });
            }

            // ---- 2. the module ring, read and matched BY NAME -----------------------------
            // WAIT FOR IT TO SETTLE - see WaitForRingAsync. Not merely for a first entry: the
            // ring can fill in instalments, and a later arrival shifts the index of an earlier
            // one, so a position taken from a half-built list can name the wrong module.
            var (ring, entries, ringReads) = await WaitForRingAsync(
                scripts, dialogPath, timeoutSeconds, ct);
            if (ring is null) return HelperMissing("lvdqmh_ring2");
            var ringStep = Step("readModuleRing", ring);
            ringStep["reads"] = ringReads;
            ringStep["settledEntries"] = entries.Count;
            steps.Add(ringStep);
            var moduleIndex = MatchModule(entries, moduleName);
            if (moduleIndex < 0)
                return Fail("moduleNotInDialog",
                    $"The dialog's module list has no entry matching '{moduleName}'. It is built " +
                    "from the ACTIVE project, so either the module is not in it or the wrong " +
                    "project is open.",
                    steps, stopwatch, new { moduleName, entries });

            // ---- 3. the arguments carrier -------------------------------------------------
            var carrierVi = Path.Combine(HelperDirectory(),
                $"dqmh_args_{Sanitise(eventName)}_{Environment.ProcessId}.vi");
            if (await BuildCarrierAsync(arguments, carrierVi, timeoutSeconds, ct)
                is { } carrierError) return carrierError;
            steps.Add(new JsonObject
            {
                ["step"] = "buildArgumentsCarrier",
                ["carrierViPath"] = carrierVi,
                ["argumentCount"] = arguments.Count,
            });

            // ---- 4. fill the dialog -------------------------------------------------------
            var fill = new Dictionary<string, string>
            {
                ["dialog vi path"] = dialogPath,
                ["module index"] = moduleIndex.ToString(),
                ["event type index"] = typeIndex.ToString(),
                ["event name"] = eventName,
                ["add tester button"] = addTesterButton ? "1" : "0",
            };
            // An empty value would misalign the helper's name/value pairing, so a blank
            // description is omitted rather than sent - the control keeps its own default.
            if (description.Length > 0) fill["event description"] = description;

            if (await RunAsync(scripts, "lvdqmh_dlg_fill3", fill, timeoutSeconds, ct)
                is not { } filled) return HelperMissing("lvdqmh_dlg_fill3");
            steps.Add(Step("fillDialog", filled));

            // ---- 5. confirm the target, in words ------------------------------------------
            // The ring is written through Value (Signaling) so the dialog rebuilds step 6, but it
            // does not do so within the same helper run - the text read above is the OLD one.
            // Running the fill a second time is idempotent and returns the updated text.
            if (await RunAsync(scripts, "lvdqmh_dlg_fill3", fill, timeoutSeconds, ct)
                is not { } confirmed) return HelperMissing("lvdqmh_dlg_fill3");
            steps.Add(Step("confirmTarget", confirmed));

            var step6 = Scalar(confirmed, "step 6 text") ?? "";
            var bareModule = BareName(moduleName);
            if (!step6.Contains(bareModule, StringComparison.OrdinalIgnoreCase))
                return Fail("targetNotConfirmed",
                    $"The dialog does not say it will write to '{bareModule}'. Its step 6 reads: " +
                    $"\"{step6}\". Nothing was pressed.",
                    steps, stopwatch, new { moduleName, moduleIndex, entries, step6 });

            // ---- 6. the arguments window, addressed by its TEMPORARY name -----------------
            if (ArgumentsWindowName() is not { } argumentsWindow)
                return Fail("argumentsWindowNotFound",
                    "No 'DQMH Arguments Window [lvtemporary_*.vi]' window is open. The dialog " +
                    "opens it, so either the dialog did not start or it was closed.",
                    steps, stopwatch);

            if (arguments.Count > 0)
            {
                if (await RunAsync(scripts, "lvdqmh_args_paste2", new()
                {
                    ["carrier vi path"] = carrierVi,
                    ["arguments window vi name"] = argumentsWindow,
                }, timeoutSeconds, ct) is not { } pasted) return HelperMissing("lvdqmh_args_paste2");
                steps.Add(Step("pasteArguments", pasted));

                var landed = Strings(pasted, "target labels");
                var missing = arguments.Select(a => a.Name)
                    .Where(n => !landed.Contains(n, StringComparer.Ordinal)).ToArray();
                if (missing.Length > 0)
                    return Fail("argumentsDidNotLand",
                        "The arguments window does not carry every control that was pasted into " +
                        "it, so the event would be scripted with the wrong contract. Nothing was " +
                        "pressed.",
                        steps, stopwatch, new { expected = arguments.Select(a => a.Name), landed, missing });
            }

            // ---- 7. press OK --------------------------------------------------------------
            if (await PressOkAsync(scripts, dialogPath, timeoutSeconds, ct) is var (pressed, focusSteps))
            {
                foreach (var s in focusSteps) steps.Add(s);
                if (!pressed)
                    return Fail("okNotPressed",
                        "Key Focus never settled on the OK button, so no keystroke was sent - " +
                        "sending one anyway would have typed into whatever else had focus. The " +
                        "dialog is still open and still filled in; press OK by hand, or call " +
                        "again once nothing else is competing for the foreground.",
                        steps, stopwatch);
            }

            stopwatch.Stop();
            return new JsonObject
            {
                ["ok"] = true,
                ["eventName"] = eventName,
                ["eventType"] = EventTypes[typeIndex],
                ["module"] = entries[moduleIndex],
                ["moduleIndex"] = moduleIndex,
                ["confirmedBy"] = step6,
                ["arguments"] = new JsonArray(arguments
                    .Select(a => (JsonNode)new JsonObject
                    {
                        ["name"] = a.Name,
                        ["type"] = a.Type,
                    }).ToArray()),
                ["argumentsWindow"] = argumentsWindow,
                ["carrierViPath"] = carrierVi,
                ["steps"] = steps,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] =
                    "OK was pressed with a synthesised SPACE keystroke, so this run needed the " +
                    "dialog frontmost and is not unattended-safe. Delacor's scripting continues " +
                    "in the background: VERIFY FROM THE FILES before reporting success - the " +
                    "event VI and its Argument--cluster.ctl in the module folder, two more " +
                    "members in the .lvlib (an event with NO arguments still gets its empty " +
                    ".ctl), and Main.vi changed. WHAT Main.vi gained depends on the type: a " +
                    "Request gets an EHL case frame and an MHL one labelled with the description, " +
                    "and their absence means the event is not wired in; a BROADCAST gets only a " +
                    "single unwired Call on the root diagram with a #CodeNeeded comment, because " +
                    "a broadcast is fired BY the module and only its author knows when - so say " +
                    "that the module does not fire it yet. Then close the project and strip any " +
                    "helper VIs LabVIEW adopted into the .lvproj - the file is not evidence of " +
                    "what was adopted until after the close.",
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>
    /// Read the module ring until its contents STOP CHANGING, then return them.
    ///
    /// WAITING FOR "NOT EMPTY" IS NOT ENOUGH, and the difference matters more than it looks. The
    /// dialog parses the project for DQMH modules and does not necessarily publish them all at
    /// once, so a ring that already holds one entry may still be growing - and every entry that
    /// arrives afterwards can shift the POSITION of the one that was matched. An index taken from
    /// a half-built list then names a different module than the name it was matched on, which is
    /// precisely the failure the name matching exists to prevent.
    ///
    /// So the list is read repeatedly and only accepted once two consecutive reads agree AND it
    /// holds something. Polling rather than a fixed sleep because the parse scales with the
    /// project: driven by hand this wait came free from the round trip between two tool calls,
    /// and in-process it does not exist at all - the first run of this tool read the ring as [""]
    /// and reported the module missing.
    ///
    /// Returns (null, []) when the helper is missing, and whatever it last saw on timeout - which
    /// the caller reports along with the name it wanted, so a genuinely empty project reads as
    /// "no such module" with the evidence attached rather than as a hang.
    /// </summary>
    private async Task<(IReadOnlyList<LvValuesXml.Value>? Values, List<string> Entries, int Reads)>
        WaitForRingAsync(string scripts, string dialogPath, int timeoutSeconds,
            CancellationToken ct)
    {
        IReadOnlyList<LvValuesXml.Value>? last = null;
        List<string>? previous = null;
        var entries = new List<string>();
        var reads = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var ring = await RunAsync(scripts, "lvdqmh_ring2", new()
            {
                ["dialog vi path"] = dialogPath,
                ["control index"] = ModuleRingIndex.ToString(),
            }, timeoutSeconds, ct);
            if (ring is null) return (null, entries, reads);

            last = ring;
            entries = Strings(ring, "entries");
            reads++;

            var settled = previous is not null && previous.SequenceEqual(entries, StringComparer.Ordinal);
            if (settled && entries.Any(e => e.Length > 0)) return (ring, entries, reads);

            previous = entries;
            await Task.Delay(400, ct);
        }
        return (last, entries, reads);
    }

    // ------------------------------------------------------------------ the OK keystroke

    /// <summary>
    /// Focus the OK button and press SPACE. Returns whether the key was sent, plus a step per
    /// attempt.
    ///
    /// THE RETRY IS NOT DEFENSIVE PADDING. Key Focus returns error 0 while doing nothing whenever
    /// the window is not frontmost, and measured 2026-09-01 it took three foreground-then-focus
    /// attempts in a row before the read-back reported true, with nothing differing between them.
    /// So the focus is written, READ BACK, and only once it reads true is a keystroke sent -
    /// otherwise the SPACE lands in whatever else holds focus.
    /// </summary>
    private async Task<(bool Pressed, List<JsonNode> Steps)> PressOkAsync(
        string scripts, string dialogPath, int timeoutSeconds, CancellationToken ct)
    {
        var steps = new List<JsonNode>();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Win32.Foreground(Win32.FindWindow("Create New DQMH Event"));
            await Task.Delay(250, ct);

            var focus = await RunAsync(scripts, "lvdqmh_dlg_keyfocus", new()
            {
                ["dialog vi path"] = dialogPath,
                ["control index"] = OkButtonIndex.ToString(),
            }, timeoutSeconds, ct);
            if (focus is null) return (false, steps);

            var settled = Scalar(focus, "focus after write") is "1" or "true" or "TRUE";
            steps.Add(new JsonObject
            {
                ["step"] = "focusOk",
                ["attempt"] = attempt,
                ["label"] = Scalar(focus, "label"),
                ["focusSettled"] = settled,
            });
            if (!settled) continue;

            // Foreground and keystroke in ONE step: anything that starts a process in between
            // moves the foreground away again, and the key is then delivered elsewhere.
            var window = Win32.FindWindow("Create New DQMH Event");
            if (window == IntPtr.Zero) return (false, steps);
            Win32.Foreground(window);
            await Task.Delay(250, ct);
            var frontmost = Win32.IsForeground(window);
            if (!frontmost)
            {
                // NO KEYSTROKE unless the dialog actually has the foreground. Windows refuses
                // SetForegroundWindow to a process that is not itself frontmost, and an MCP
                // server never is - so this fails routinely rather than exceptionally. Measured
                // 2026-09-01: the first tool run reported windowWasFrontmost false, sent SPACE
                // anyway, returned ok:true, and created nothing. Sending it here would type into
                // whatever the user has in front of them.
                steps.Add(new JsonObject
                {
                    ["step"] = "pressSpace",
                    ["attempt"] = attempt,
                    ["skipped"] = "the dialog did not come to the foreground",
                });
                continue;
            }

            Win32.PressSpace();
            steps.Add(new JsonObject
            {
                ["step"] = "pressSpace",
                ["attempt"] = attempt,
                ["windowWasFrontmost"] = true,
            });
            return (true, steps);
        }
        return (false, steps);
    }

    // ------------------------------------------------------------------ the arguments carrier

    internal readonly record struct Argument(string Name, string Type);

    internal static List<Argument>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return null;
            var result = new List<Argument>();
            foreach (var item in array)
            {
                if (item is not JsonObject o) return null;
                var name = o["name"]?.GetValue<string>();
                var type = o["type"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) return null;
                result.Add(new Argument(name, type));
            }
            return result;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Generate a VI whose front panel holds one control per argument. Delacor's
    /// Script Arguments Cluster.vi reads that panel, so the control names and types become the
    /// event's cluster fields. This is the carrier-VI pattern lvai_create_class uses for private
    /// data, and the one part of event creation AIXML is genuinely good at.
    /// </summary>
    private async Task<string?> BuildCarrierAsync(
        List<Argument> arguments, string carrierVi, int timeoutSeconds, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(carrierVi)!);
        var aixml = Path.ChangeExtension(carrierVi, ".xml");

        var sb = new StringBuilder();
        sb.Append("<VI _name=\"").Append(Path.GetFileName(carrierVi)).Append('"');
        sb.Append(" description=\"Arguments carrier generated by lvai_dqmh_new_event. Its ")
          .Append("front panel holds one control per event argument and nothing else\\3B the ")
          .Append("controls are copied into Delacor's arguments window\\2C where the names and ")
          .Append("types become the event's Argument--cluster.ctl fields.\">\n");
        var uid = 10;
        foreach (var a in arguments)
        {
            sb.Append("  <Control _name=\"").Append(Escape(a.Name))
              .Append("\" type=\"").Append(Escape(a.Type))
              .Append("\" uid=\"").Append(uid)
              .Append("\" uid_parent=\"root\" value=\"").Append(DefaultFor(a.Type))
              .Append("\" outputs=\"value:")
              .Append(uid).Append(".value\"/>\n");
            uid++;
        }
        sb.Append("</VI>\n");
        await File.WriteAllTextAsync(aixml, sb.ToString(), new UTF8Encoding(false), ct);

        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return Json.Error("carrierAixmlInvalid",
                "The generated arguments carrier does not validate. The most likely cause is an " +
                "argument type AIXML does not know - it takes AIXML type names such as string, " +
                "double, int32, bool, path.",
                new { errorMessage = validation.ErrorMessage, aiXmlPath = aixml });

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = carrierVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (generation.ErrorCode != 0 || !File.Exists(carrierVi))
            return Json.Error("carrierGenerationFailed",
                $"Could not generate the arguments carrier: {generation.ErrorMessage}",
                new { carrierViPath = carrierVi, errorCode = generation.ErrorCode });

        return null;
    }

    // ------------------------------------------------------------------ running the helpers

    /// <summary>
    /// Run one shipped helper through lvai_run_and_read, which is what makes its cluster and
    /// array indicators readable - RunVIAsTopLevel returns those empty with Error 91.
    /// Returns null when the helper's AIXML is missing.
    /// </summary>
    private async Task<IReadOnlyList<LvValuesXml.Value>?> RunAsync(
        string scripts, string helperName, Dictionary<string, string> inputs,
        int timeoutSeconds, CancellationToken ct)
    {
        var aixml = Path.Combine(scripts, helperName + ".xml");
        if (!File.Exists(aixml)) return null;

        var helperVi = Path.Combine(HelperDirectory(), helperName + ".vi");
        if (!File.Exists(helperVi) && await EnsureAsync(aixml, helperVi, timeoutSeconds, ct) is false)
            return null;

        var wrapperAixml = Path.Combine(scripts, "lvai_run_and_read.xml");
        var wrapperVi = Path.Combine(HelperDirectory(), "lvai_run_and_read.vi");
        if (!File.Exists(wrapperVi)
            && await EnsureAsync(wrapperAixml, wrapperVi, timeoutSeconds, ct) is false)
            return null;

        var request = new RunVIAsTopLevelRequest { ViPath = wrapperVi };
        request.Inputs["VI Path"] = helperVi;
        request.Inputs["Input Names"] = string.Join("\n", inputs.Keys);
        request.Inputs["Input Values"] = string.Join("\n", inputs.Values);

        var response = await connection.InvokeAsync((c, t) =>
            c.RunVIAsTopLevelAsync(request,
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        response.Outputs.TryGetValue("values xml", out var valuesXml);
        return LvValuesXml.Parse(valuesXml);
    }

    private async Task<bool> EnsureAsync(
        string aixml, string vi, int timeoutSeconds, CancellationToken ct)
    {
        if (!File.Exists(aixml)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(vi)!);
        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = vi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        return generation.ErrorCode == 0 && File.Exists(vi);
    }

    // ------------------------------------------------------------------ reading helper output

    private static string? Scalar(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        values.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Scalar;

    /// <summary>Every &lt;Val&gt; of an array indicator, in order.</summary>
    private static List<string> Strings(IReadOnlyList<LvValuesXml.Value> values, string name)
    {
        var xml = values.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Xml;
        var result = new List<string>();
        if (string.IsNullOrEmpty(xml)) return result;
        foreach (var line in xml.Split('\n'))
        {
            var open = line.IndexOf("<Val>", StringComparison.Ordinal);
            if (open < 0) continue;
            var close = line.IndexOf("</Val>", open, StringComparison.Ordinal);
            if (close < 0) continue;
            result.Add(System.Net.WebUtility.HtmlDecode(
                line[(open + 5)..close]));
        }
        return result;
    }

    /// <summary>The error cluster of a helper run, or null when it reported none.</summary>
    private static string? Failed(IReadOnlyList<LvValuesXml.Value> values)
    {
        var error = values.FirstOrDefault(v =>
            string.Equals(v.Name, "error out", StringComparison.OrdinalIgnoreCase)).Xml;
        if (string.IsNullOrEmpty(error)) return null;
        return error.Contains("<Name>status</Name>") && ValueAfter(error, "status") is "1"
            ? error
            : null;
    }

    private static string? ValueAfter(string xml, string name)
    {
        var at = xml.IndexOf($"<Name>{name}</Name>", StringComparison.Ordinal);
        if (at < 0) return null;
        var open = xml.IndexOf("<Val>", at, StringComparison.Ordinal);
        if (open < 0) return null;
        var close = xml.IndexOf("</Val>", open, StringComparison.Ordinal);
        return close < 0 ? null : xml[(open + 5)..close];
    }

    // ------------------------------------------------------------------ small helpers

    /// <summary>
    /// Which ring entry is this module. Matched on the BARE name so 'Heater' and 'Heater.lvlib'
    /// both work, and never positionally: the ring's order depends on how the dialog was launched
    /// and its placeholder sits LAST, so a remembered index aims at the wrong module.
    /// </summary>
    internal static int MatchModule(List<string> entries, string wanted)
    {
        var bare = BareName(wanted);
        for (var i = 0; i < entries.Count; i++)
        {
            // The placeholder is a real ring entry and must never be selectable. Matching it
            // would hand back an index that selects no module while letting the run continue -
            // caught later by the step-6 check, but only by luck of the wording.
            if (IsPlaceholder(entries[i])) continue;
            if (string.Equals(BareName(entries[i]), bare, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>The ring's "&lt;Select a Module&gt;" row - angle-bracketed, and never a module.</summary>
    private static bool IsPlaceholder(string entry) =>
        entry.StartsWith('<') && entry.EndsWith('>');

    internal static string BareName(string name) =>
        name.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    private static string Sanitise(string name) =>
        string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-'));

    /// <summary>
    /// The `value` a control of this type will accept as its default.
    ///
    /// AN EMPTY VALUE IS NOT UNIVERSAL, which is what the first run of this tool discovered:
    /// a double control emitted with value="" is refused - `Error 53, Unrecognized or unsupported
    /// attribute set in Control with UID 11`, which names the control rather than the attribute
    /// and reads like a bad type. Only strings and paths take an empty default; a numeric needs a
    /// number and a boolean needs a boolean. The hand-written carriers this tool replaces already
    /// had value="0" on their numeric controls, so the rule was in the working examples - it just
    /// did not survive the move into C#.
    /// </summary>
    internal static string DefaultFor(string type) => type.ToLowerInvariant() switch
    {
        "string" or "path" => "",
        "bool" or "boolean" => "false",
        _ => "0",
    };

    /// <summary>AIXML attribute escaping - a colon and a backslash carry meaning in this dialect.</summary>
    internal static string Escape(string value) => value
        .Replace("\\", "\\5C").Replace(":", "\\3A")
        .Replace("&", "&amp;").Replace("\"", "&quot;")
        .Replace("<", "&lt;").Replace(">", "&gt;");

    private static string HelperDirectory() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers");

    private static string? DialogPath() =>
        LabViewLocator.Discover()
            .Select(i => Path.Combine(Path.GetDirectoryName(i.ExePath) ?? "", DialogRelativePath))
            .FirstOrDefault(File.Exists);

    private static string? ArgumentsWindowName()
    {
        foreach (var title in Win32.VisibleTitles())
        {
            var at = title.IndexOf("lvtemporary_", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || !title.Contains("Arguments Window", StringComparison.OrdinalIgnoreCase))
                continue;
            var end = title.IndexOf(".vi", at, StringComparison.OrdinalIgnoreCase);
            if (end > at) return title[at..(end + 3)];
        }
        return null;
    }

    private static JsonObject Step(string name, IReadOnlyList<LvValuesXml.Value> values) => new()
    {
        ["step"] = name,
        ["values"] = LvValuesXml.ToJson(values),
    };

    private static string HelperMissing(string helper) =>
        Json.Error("helperMissing",
            $"The helper '{helper}.xml' is not in the scripts folder beside the exe, or it could " +
            "not be generated. The DQMH helpers ship with the build; a source checkout has them " +
            "under scripts/.",
            new { helper });

    private static string Fail(string kind, string message, JsonArray steps,
        Stopwatch stopwatch, object? detail = null)
    {
        stopwatch.Stop();
        var payload = JsonNode.Parse(Json.Error(kind, message, detail))!.AsObject();
        payload["steps"] = steps;
        payload["elapsedMs"] = stopwatch.ElapsedMilliseconds;
        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ------------------------------------------------------------------ the window layer

    /// <summary>
    /// The only part of this tool that is not VI Server. Kept in one place, and deliberately
    /// small: find a window by exact title, raise it, and send one SPACE.
    /// </summary>
    private static class Win32
    {
        private delegate bool EnumProc(IntPtr window, IntPtr param);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr w);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr w, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr w);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr w);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr w);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr w, int cmd);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr w, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const int SwShow = 5;
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        private const byte VkSpace = 0x20;
        private const uint KeyUp = 0x0002;

        public static IEnumerable<string> VisibleTitles()
        {
            var titles = new List<string>();
            EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && Title(window) is { Length: > 0 } title)
                    titles.Add(title);
                return true;
            }, IntPtr.Zero);
            return titles;
        }

        public static IntPtr FindWindow(string exactTitle)
        {
            var found = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window)
                    && string.Equals(Title(window), exactTitle, StringComparison.Ordinal))
                {
                    found = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Raise a window from a process that is not itself frontmost.
        ///
        /// A BARE SetForegroundWindow IS NOT ENOUGH. Windows only grants the foreground to a
        /// process that already has it, and an MCP server never does - measured 2026-09-01, the
        /// call returned and the dialog stayed behind. Attaching this thread's input queue to the
        /// current foreground thread for the duration of the call is the documented way around
        /// it: for that moment the two threads share input state, so the request comes from a
        /// thread that is allowed to make it. ShowWindow(SW_SHOW) first, in case the window is
        /// minimised, since a minimised window cannot take focus at all.
        /// </summary>
        public static void Foreground(IntPtr window)
        {
            if (window == IntPtr.Zero) return;

            ShowWindow(window, SwShow);

            var foreground = GetForegroundWindow();
            if (foreground == window) return;

            var us = GetCurrentThreadId();
            var them = GetWindowThreadProcessId(foreground, out _);
            var attached = them != 0 && them != us && AttachThreadInput(us, them, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached) AttachThreadInput(us, them, false);
            }
        }

        public static bool IsForeground(IntPtr window) => GetForegroundWindow() == window;

        public static void PressSpace()
        {
            keybd_event(VkSpace, 0, 0, IntPtr.Zero);
            Thread.Sleep(60);
            keybd_event(VkSpace, 0, KeyUp, IntPtr.Zero);
        }

        private static string Title(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0) return "";
            var buffer = new StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }
    }
}
