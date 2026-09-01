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

    /// <summary>
    /// `Event Type`'s position in the dialog's Controls[], harvested 2026-09-01 with
    /// lvdqmh_dlg_probe. The same probe confirmed ModuleRingIndex 0 and OkButtonIndex 10, so all
    /// three are measured rather than assumed:
    ///
    ///   0 Module (Ring)   1 Event Type (Ring)   2 Add Tester Button   3 Enqueue Message VI
    ///   4 Existing Request   5 Custom Enqueue VI   6 Broadcast Argument Source   7 Event Name
    ///   8 Round Trip (Broadcast)   9 Event Description   10 OK   11 Cancel   12 Help
    ///   13 Round Trip (Request)   14 Step 6   15 Context Help
    /// </summary>
    private const int EventTypeRingIndex = 1;

    /// <summary>
    /// The dialog's own label for the broadcast half of a Round Trip. Read off its front panel
    /// (2026-09-01), where the pair shows as `Round Trip (Request)` and `Round Trip (Broadcast)`;
    /// the second is what Script New Event.vi takes as its `Round Trip (Broadcast)` terminal.
    /// </summary>
    private const string RoundTripBroadcastControl = "Round Trip (Broadcast)";

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
        [Description("""
            Reply data, same JSON shape as argumentsJson, for the two types that carry a reply -
            Request and Wait for Reply, and Round Trip. These become the REPLY cluster, a second
            Argument--cluster.ctl, and they are a different set from argumentsJson: those are what
            the caller sends, these are what the module sends back. Leave empty for a reply that
            carries only the error cluster. Refused on Request and Broadcast, which have no reply.
            """)]
        string replyArgumentsJson = "",
        [Description("""
            Round Trip only, and required for it: the name of the BROADCAST half of the pair. A
            Round Trip is a request plus the broadcast that answers it, so it needs two names -
            eventName is the request. Refused on the other three types.
            """)]
        string roundTripBroadcastName = "",
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

            if (ParseArguments(replyArgumentsJson) is not { } replyArguments)
                return Json.Error("badArguments",
                    "replyArgumentsJson must be a JSON array of {\"name\":…,\"type\":…} objects.",
                    new { replyArgumentsJson });

            if (replyArguments.FirstOrDefault(a => a.Name.Contains('\n') || a.Name.Contains('\r'))
                is { Name.Length: > 0 } brokenReply)
                return Json.Error("badArguments",
                    $"Reply argument name '{brokenReply.Name}' contains a line break.");

            var isRoundTrip = IsRoundTrip(typeIndex);
            var carriesReply = CarriesReply(typeIndex);
            if (TypeRuleViolation(typeIndex, replyArguments.Count, roundTripBroadcastName)
                is { } violation)
                return Json.Error("badArguments", violation,
                    new
                    {
                        eventType = EventTypes[typeIndex],
                        replyArgumentsJson,
                        roundTripBroadcastName,
                    });

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing",
                    "No scripts folder next to the exe - lvai_status reports it as " +
                    "scriptsDirectory. The DQMH helpers live there.");

            // FAIL BEFORE DRIVING ANYTHING. The last step is a keystroke and a keystroke needs a
            // desktop that can hold a foreground window; without one the whole chain runs, fills
            // the dialog, and stops one step from the end with a message about Key Focus that
            // points at LabVIEW instead of at the lock screen.
            if (!Win32.DesktopIsInteractive())
                return Json.Error("desktopNotInteractive",
                    "No window holds the foreground, which means the desktop is locked, the " +
                    "screensaver is up, or this session is disconnected. The last step of this " +
                    "tool is a synthesised keystroke and it cannot reach a locked desktop, so " +
                    "nothing was started. Unlock the workstation and call again.");

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
            //
            // BUT WAIT FOR THE PREVIOUS RUN'S DIALOG TO GO FIRST. DQMH keeps the dialog on screen
            // while it finishes scripting, and its arguments window still holds the last event's
            // controls - so adopting it inherits them. Measured 2026-09-01: a Broadcast issued
            // seconds after a Request came out carrying the Request's argument too. The window is
            // gone within a few seconds once the scripting completes, so a short wait removes the
            // race; PasteAsync's surplus check is the backstop for when it does not.
            _ = await WaitForDialogToCloseAsync(ct);

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

            string? replyCarrierVi = null;
            if (replyArguments.Count > 0)
            {
                replyCarrierVi = Path.Combine(HelperDirectory(),
                    $"dqmh_reply_{Sanitise(eventName)}_{Environment.ProcessId}.vi");
                if (await BuildCarrierAsync(replyArguments, replyCarrierVi, timeoutSeconds, ct)
                    is { } replyCarrierError) return replyCarrierError;
                steps.Add(new JsonObject
                {
                    ["step"] = "buildReplyCarrier",
                    ["carrierViPath"] = replyCarrierVi,
                    ["argumentCount"] = replyArguments.Count,
                });
            }

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

            // ---- 5a. REVEAL the reply payload window --------------------------------------
            // WITHOUT THIS THE REPLY FIELDS ARE SILENTLY DROPPED. Measured 2026-09-01: the reply
            // window exists from the start but stays HIDDEN, a paste into it is accepted - the
            // labels read back - and Delacor's scripting then ignores it. The event comes out with
            // its third file, its third .lvlib member, its `wait for reply (T)` input and no
            // `Reply Payload` output at all: five checks of six pass.
            //
            // `Ctrl Val.Set` on Event Type fires no event, and re-signalling the MODULE ring does
            // not reveal it either (fill3 does that twice, the second time with Event Type already
            // set). The dialog reveals the window from Event Type's OWN event case, so the write
            // has to be Value (Signaling) on that control.
            //
            // Only for the types that carry a reply: Request and Broadcast are proven without this
            // step, and there is nothing to gain by changing a path that works.
            //
            // BEFORE ANY PASTE, deliberately - the event case rebuilds both argument windows, so
            // signalling afterwards would throw away everything pasted, and the window names must
            // be looked up after it.
            if (carriesReply)
            {
                if (await RunAsync(scripts, "lvdqmh_dlg_signal", new()
                    {
                        ["dialog vi path"] = dialogPath,
                        ["control index"] = EventTypeRingIndex.ToString(),
                        ["value"] = typeIndex.ToString(),
                    }, timeoutSeconds, ct) is not { } signalled)
                    return HelperMissing("lvdqmh_dlg_signal");
                steps.Add(Step("revealReplyWindow", signalled));

                if (Failed(signalled) is { } signalError)
                    return Fail("replyWindowNotRevealed",
                        "Signalling Event Type failed, so the reply payload window would stay " +
                        "hidden and the reply fields would be dropped without an error. " +
                        "Nothing was pressed.",
                        steps, stopwatch, new { error = signalError });

                // The helper reads the control's own label back, so a wrong index is caught here
                // rather than by its consequences three steps later.
                if (Scalar(signalled, "label") is { } signalLabel
                    && !string.Equals(signalLabel, "Event Type", StringComparison.Ordinal))
                    return Fail("replyWindowNotRevealed",
                        $"Control {EventTypeRingIndex} of the dialog is labelled " +
                        $"\"{signalLabel}\", not \"Event Type\". Controls[] order is not stable " +
                        "across launches, so the index was not used. Nothing was pressed.",
                        steps, stopwatch, new { index = EventTypeRingIndex, label = signalLabel });
            }

            // ---- 5b. the Round Trip's second name -----------------------------------------
            // Ctrl Val.Set, deliberately NOT Value (Signaling): signalling a text field makes the
            // dialog re-run its own event case and rebuild the argument windows, which would
            // throw away anything already pasted.
            if (isRoundTrip)
            {
                if (await RunAsync(scripts, "lvdqmh_dlg_setstring", new()
                    {
                        ["dialog vi path"] = dialogPath,
                        ["control name"] = RoundTripBroadcastControl,
                        ["value"] = roundTripBroadcastName,
                    }, timeoutSeconds, ct) is not { } named)
                    return HelperMissing("lvdqmh_dlg_setstring");
                steps.Add(Step("setRoundTripBroadcastName", named));
                if (Failed(named) is { } nameError)
                    return Fail("roundTripNameNotSet",
                        $"Writing '{RoundTripBroadcastControl}' failed, so the broadcast half of " +
                        "the pair would be scripted unnamed. Nothing was pressed.",
                        steps, stopwatch, new { error = nameError });

                if (Scalar(named, "read back") is { } readBack
                    && !string.Equals(readBack, roundTripBroadcastName, StringComparison.Ordinal))
                    return Fail("roundTripNameNotSet",
                        $"'{RoundTripBroadcastControl}' reads back as \"{readBack}\" rather than " +
                        $"\"{roundTripBroadcastName}\". A write that reports no error is not " +
                        "evidence the value landed. Nothing was pressed.",
                        steps, stopwatch, new { wanted = roundTripBroadcastName, readBack });
            }

            // ---- 6. the REPLY payload window FIRST ----------------------------------------
            // A DIFFERENT WINDOW, and usually a HIDDEN one: Show Arguments Window.vi creates both
            // up front, and the dialog only reveals the reply one when its own event case runs -
            // which it never does here, because Event Type is written with Ctrl Val.Set. Hidden
            // makes no difference to VI Server; it only means the window must be found by an
            // enumeration that does not filter on visibility.
            //
            // ORDER MATTERS, and it is the reply half that has to go first. Measured 2026-09-01:
            // pasting into the hidden reply window LAST left OK unpressable - Key Focus on the OK
            // button read back true and the SPACE did nothing, because key focus is a property of
            // a control WITHIN A PANEL and the panel LabVIEW had made active was the hidden one.
            // Every run that ever worked ended its pastes in the visible Arguments Window, so the
            // reply paste is done first and the proven sequence is left intact.
            string? replyWindow = null;
            if (replyCarrierVi is not null)
            {
                if (ReplyWindowName() is not { } found)
                    return Fail("replyWindowNotFound",
                        "No 'DQMH Reply Payload Window [lvtemporary_*.vi]' window exists, so the " +
                        "reply arguments have nowhere to go and the event would be scripted with " +
                        "an empty reply. Nothing was pressed.",
                        steps, stopwatch, new { eventType = EventTypes[typeIndex] });
                replyWindow = found;

                if (await PasteAsync(scripts, replyCarrierVi, replyWindow, replyArguments,
                        "pasteReplyArguments", "the reply payload window",
                        steps, stopwatch, timeoutSeconds, ct) is { } replyPasteError)
                    return replyPasteError;
            }

            // ---- 6b. the arguments window, addressed by its TEMPORARY name ----------------
            if (ArgumentsWindowName() is not { } argumentsWindow)
                return Fail("argumentsWindowNotFound",
                    "No 'DQMH Arguments Window [lvtemporary_*.vi]' window is open. The dialog " +
                    "opens it, so either the dialog did not start or it was closed.",
                    steps, stopwatch);

            if (arguments.Count > 0
                && await PasteAsync(scripts, carrierVi, argumentsWindow, arguments,
                       "pasteArguments", "the arguments window", steps, stopwatch, timeoutSeconds, ct)
                   is { } pasteError) return pasteError;

            // ---- 7. press OK --------------------------------------------------------------
            if (await PressOkAsync(scripts, dialogPath, timeoutSeconds, ct) is var (pressed, focusSteps))
            {
                foreach (var s in focusSteps) steps.Add(s);
                if (!pressed)
                    return Fail("okNotPressed",
                        "OK did not fire. Either Key Focus never settled on it, or the dialog " +
                        "never came to the foreground, or the keystroke was delivered and the " +
                        "button did not react - the steps say which, and `dialogClosed: false` " +
                        "is the last of the three. Nothing was scripted. The dialog is still " +
                        "open and still filled in, so press OK by hand, or call again once " +
                        "nothing else is competing for the foreground. Do NOT create a different " +
                        "event before dealing with it: the next run would adopt this dialog and " +
                        "inherit the controls already in its arguments window.",
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
                ["replyArguments"] = new JsonArray(replyArguments
                    .Select(a => (JsonNode)new JsonObject
                    {
                        ["name"] = a.Name,
                        ["type"] = a.Type,
                    }).ToArray()),
                ["roundTripBroadcastName"] =
                    isRoundTrip ? roundTripBroadcastName : null,
                ["argumentsWindow"] = argumentsWindow,
                ["replyPayloadWindow"] = replyWindow,
                ["carrierViPath"] = carrierVi,
                ["replyCarrierViPath"] = replyCarrierVi,
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

            // A KEYSTROKE THAT ARRIVED IS NOT A BUTTON THAT FIRED, and this is the check that
            // separates them. Measured 2026-09-01 on a control run: focus settled, the dialog was
            // confirmed frontmost, SPACE was sent, the tool reported ok: true - and no event was
            // created. The dialog simply stayed open. The next call then adopted that dialog,
            // whose arguments window still held the first event's control, and scripted a second
            // event carrying both. One silent miss, two wrong outcomes.
            //
            // Delacor's dialog CLOSES when OK is accepted, so its disappearance is the cheapest
            // available proof that the press took - cheaper than hunting for the module folder,
            // and it needs nothing the tool does not already know.
            var accepted = await WaitForDialogToCloseAsync(ct);
            steps.Add(new JsonObject
            {
                ["step"] = "pressSpace",
                ["attempt"] = attempt,
                ["windowWasFrontmost"] = true,
                ["dialogClosed"] = accepted,
            });
            if (accepted) return (true, steps);

            // Still on screen: the press did nothing. Try again rather than reporting success.
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
    /// <summary>
    /// Which of the four types carries a reply, and which needs a second NAME.
    ///
    /// Measured off Script New Event.vi's connector pane on 2026-09-01 rather than inferred from
    /// DQMH's documentation: besides `Arguments VI` it takes `Reply Payload VI` and a separate
    /// `Round Trip (Broadcast)` string, both `required`. Accepting either where it does not belong
    /// - or ignoring it where it does - scripts without any error and produces an event whose
    /// reply cluster is empty, or a Round Trip whose broadcast half is unnamed. Neither shows up
    /// until someone reads the module weeks later, which is why these are refusals rather than
    /// warnings.
    /// </summary>
    /// <summary>
    /// What an argument window carries against what it should, BOTH WAYS.
    ///
    /// Checking only for what you expect cannot see what you did not expect, and that half is the
    /// one that shipped a wrong event: a Broadcast issued seconds after a Request adopted the
    /// Request's still-open dialog and inherited its control, so it was scripted with two argument
    /// fields instead of one - every expected label present, ok: true, wrong public contract.
    /// </summary>
    internal static (string[] Missing, string[] Surplus) CompareLabels(
        IReadOnlyCollection<string> wanted, IReadOnlyCollection<string> landed) =>
        (wanted.Where(n => !landed.Contains(n, StringComparer.Ordinal)).ToArray(),
         landed.Where(n => !wanted.Contains(n, StringComparer.Ordinal)).ToArray());

    internal static bool CarriesReply(int typeIndex) => typeIndex is 2 or 3;

    internal static bool IsRoundTrip(int typeIndex) => typeIndex is 3;

    /// <summary>
    /// Null when the combination is legal, otherwise the sentence to report. Pure, so the rules
    /// are testable without a dialog, a project or LabVIEW.
    /// </summary>
    internal static string? TypeRuleViolation(
        int typeIndex, int replyArgumentCount, string roundTripBroadcastName)
    {
        var type = typeIndex >= 0 && typeIndex < EventTypes.Length
            ? EventTypes[typeIndex] : "?";
        var named = !string.IsNullOrWhiteSpace(roundTripBroadcastName);

        if (!CarriesReply(typeIndex) && replyArgumentCount > 0)
            return $"'{type}' has no reply, so replyArgumentsJson does not apply. Only " +
                   "'Request and Wait for Reply' and 'Round Trip' carry one.";

        if (!IsRoundTrip(typeIndex) && named)
            return $"roundTripBroadcastName applies to 'Round Trip' only, not '{type}'.";

        if (IsRoundTrip(typeIndex) && !named)
            return "A Round Trip is a request plus the broadcast that answers it, so it needs " +
                   "two names: eventName is the request, roundTripBroadcastName the broadcast. " +
                   "Delacor's Script New Event.vi takes them as separate required terminals.";

        if (named && (roundTripBroadcastName.Contains('\n')
                      || roundTripBroadcastName.Contains('\r')))
            return "roundTripBroadcastName contains a line break.";

        return null;
    }

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

    /// <summary>
    /// Paste one carrier's controls into one of the dialog's temporary windows and VERIFY they
    /// arrived. Shared by the request and the reply halves, which differ only in which window
    /// they target - the check is the point of the routine, not the paste.
    /// </summary>
    private async Task<string?> PasteAsync(
        string scripts, string carrierVi, string windowName, List<Argument> expected,
        string stepName, string windowDescription, JsonArray steps, Stopwatch stopwatch,
        int timeoutSeconds, CancellationToken ct)
    {
        if (await RunAsync(scripts, "lvdqmh_args_paste2", new()
            {
                ["carrier vi path"] = carrierVi,
                ["arguments window vi name"] = windowName,
            }, timeoutSeconds, ct) is not { } pasted)
            return HelperMissing("lvdqmh_args_paste2");
        steps.Add(Step(stepName, pasted));

        var landed = Strings(pasted, "target labels");
        var wanted = expected.Select(a => a.Name).ToArray();
        var (missing, surplus) = CompareLabels(wanted, landed);

        if (missing.Length > 0)
            return Fail("argumentsDidNotLand",
                $"After the paste, {windowDescription} does not carry every control, so the " +
                "event would be scripted with the wrong contract. Nothing was pressed.",
                steps, stopwatch,
                new { window = windowName, expected = wanted, landed, missing });

        // SURPLUS IS AS WRONG AS MISSING, and it is the failure this check was added for.
        // Measured 2026-09-01: a Broadcast created seconds after a Request adopted the Request's
        // dialog - which DQMH keeps open while it finishes scripting - and its arguments window
        // still held the Request's control. The Broadcast came out with `Sollwert` AND `Status`
        // on its pane, a wrong public contract, and the tool answered ok: true because every
        // control it asked for was present. Checking only for what you expect cannot see what
        // you did not.
        if (surplus.Length > 0)
            return Fail("argumentsWindowNotEmpty",
                $"{char.ToUpperInvariant(windowDescription[0])}{windowDescription[1..]} carries " +
                $"control(s) that are not part of this event: {string.Join(", ", surplus)}. They " +
                "are almost certainly left over from a previous event in the same dialog, and " +
                "they would become fields of this event's public cluster. Nothing was pressed. " +
                "Close the Create New DQMH Event dialog and call again.",
                steps, stopwatch,
                new { window = windowName, expected = wanted, landed, surplus });

        return null;
    }

    /// <summary>
    /// Give a dialog left over from a previous run a few seconds to close on its own.
    ///
    /// Deliberately short and deliberately silent: a dialog that is STILL there afterwards is a
    /// legitimate case - a previous run that could not press OK leaves one filled and waiting -
    /// and that one is adopted, as it always was. This only removes the race against a run that
    /// succeeded and whose dialog has not finished going away.
    /// </summary>
    private static async Task<bool> WaitForDialogToCloseAsync(CancellationToken ct)
    {
        for (var i = 0; i < 12; i++)
        {
            if (Win32.FindWindow("Create New DQMH Event") == IntPtr.Zero) return true;
            await Task.Delay(500, ct);
        }
        return false;
    }

    private static string? ArgumentsWindowName() =>
        TemporaryWindowNamed("Arguments Window");

    /// <summary>
    /// The reply half. Its title is "DQMH Reply Payload Window [lvtemporary_N.vi] ...", which
    /// does NOT contain "Arguments Window" - so the two matchers cannot pick each other's window,
    /// and that was checked rather than assumed.
    /// </summary>
    private static string? ReplyWindowName() =>
        TemporaryWindowNamed("Reply Payload Window");

    private static string? TemporaryWindowNamed(string marker)
    {
        // Hidden windows count: the reply one is created up front and only shown when the
        // dialog's own event case runs. See Win32.AllTitles.
        foreach (var title in Win32.AllTitles())
        {
            var at = title.IndexOf("lvtemporary_", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || !title.Contains(marker, StringComparison.OrdinalIgnoreCase))
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

        public static IEnumerable<string> VisibleTitles() => Titles(visibleOnly: true);

        /// <summary>
        /// Every top-level window, INCLUDING HIDDEN ONES.
        ///
        /// The Reply Payload Window needs this and the Arguments Window does not, which is the
        /// whole reason the distinction exists. Measured 2026-09-01: Show Arguments Window.vi
        /// creates both in one call, but the dialog only SHOWS the reply one when its own event
        /// case runs - and the tool sets Event Type with Ctrl Val.Set, which fires no event. So
        /// the window is there, addressable by VI Server, and invisible to an enumeration that
        /// filters on IsWindowVisible.
        /// </summary>
        public static IEnumerable<string> AllTitles() => Titles(visibleOnly: false);

        private static List<string> Titles(bool visibleOnly)
        {
            var titles = new List<string>();
            EnumWindows((window, _) =>
            {
                if ((!visibleOnly || IsWindowVisible(window))
                    && Title(window) is { Length: > 0 } title)
                    titles.Add(title);
                return true;
            }, IntPtr.Zero);
            return titles;
        }

        /// <summary>
        /// Whether ANY window holds the foreground.
        ///
        /// GetForegroundWindow returns NULL when no window can - a locked desktop, a running
        /// screensaver, a disconnected session. Measured 2026-09-01: with the workstation locked
        /// every one of the five Key Focus attempts read back false and SetForegroundWindow
        /// returned false, which the tool reported as "focus never settled" - true, useless, and
        /// it sent the caller looking at LabVIEW. The keystroke route cannot work on a locked
        /// desktop at all, so this is worth saying in one sentence up front.
        /// </summary>
        public static bool DesktopIsInteractive() => GetForegroundWindow() != IntPtr.Zero;

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
