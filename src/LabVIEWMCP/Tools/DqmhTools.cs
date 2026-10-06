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
/// Creating a DQMH event with NO dialog and NO keystroke: DqmhHeadless.NewEventAsync calls
/// Delacor's Parse Project for DQMH Modules.vi and Script New Event.vi as loaded subVIs of ONE
/// generated wrapper, which keeps Module Info's refnums alive (docs/dqmh-scripting.md section 9c).
///
/// THE DIALOG ROUTE IS GONE since 2026-10-06. Until then `useDialog: true` drove Delacor's Create
/// New DQMH Event dialog and pressed its latched OK button with a synthesised SPACE; the headless
/// route covers all four event types, so the dialog route was a second way to do the same thing
/// with a keystroke in it, and was removed on the user's instruction ("keine Doppelspurigkeit").
/// docs/dqmh-scripting.md section 6 keeps the measurements as history.
///
/// This class also holds what the other DQMH tools share: the arguments carrier, running a helper
/// through the typed run helper, reading its output, and the window layer DqmhDialogWatch uses.
/// </summary>
[McpServerToolType]
internal sealed class DqmhTools(LvaiConnection connection)
{
    private static readonly string[] EventTypes =
        ["Request", "Broadcast", "Request and Wait for Reply", "Round Trip"];

    [McpServerTool(Name = "lvai_dqmh_new_event", Destructive = true, OpenWorld = true,
        Title = "Create a DQMH event")]
    [Description("""
        MUTATING: creates a DQMH event - Request, Broadcast, Request and Wait for Reply or Round
        Trip - with typed arguments on a module in the ACTIVE project.

        WITH NO DIALOG AND NO KEYSTROKE, so it is safe unattended: a generated wrapper
        calls Delacor's own Parse Project for DQMH Modules.vi and Script New Event.vi as loaded
        subVIs of ONE caller, which keeps Module Info's refnums alive (docs/dqmh-scripting.md
        section 9). A dry run first finds the module and its folder, and the checks Delacor's
        dialog would answer with a MODAL - blank or invalid names, reserved or duplicate argument
        labels, an event name already used as a file or as a Main.vi case - are made here and
        refused by name. The answer lists the files written with each VI's exec state and
        confirms Main.vi is still executable. Names may be bare or as Delacor spells them.

        A project must be OPEN AND ACTIVE.
        """)]
    public async Task<string> NewEvent(
        [Description("Module to add the event to, e.g. 'Heater' or 'Heater.lvlib'. Matched " +
                     "against the project's DQMH modules, case-insensitively.")]
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

            return await new DqmhHeadless(connection).NewEventAsync(
                this, scripts, moduleName, eventName, typeIndex, EventTypes[typeIndex],
                arguments, replyArguments, roundTripBroadcastName, description,
                addTesterButton, timeoutSeconds, ct);
        });

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
    internal async Task<string?> BuildCarrierAsync(
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
        // 4200 and up: a uid inside LabVIEW's reserved range costs a DWarn per element
        // (CLAUDE.md, "THE UID BASE IS 4200").
        var uid = 4200;
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
    internal async Task<IReadOnlyList<LvValuesXml.Value>?> RunAsync(
        string scripts, string helperName, Dictionary<string, string> inputs,
        int timeoutSeconds, CancellationToken ct) =>
        (await RunDetailedAsync(scripts, helperName, inputs, timeoutSeconds, ct)).Values;

    /// <summary>
    /// <see cref="RunAsync"/> plus the RUN HELPER's own error, which is the only sign that an
    /// input could not be set - the target then never ran and every indicator reads its default.
    ///
    /// THE TYPED HELPER since 2026-10-06. This used the string-only `lvai_run_and_read.vi`, whose
    /// `Ctrl Val.Set` accepts a string variant on a STRING control only. Every dialog-route helper
    /// takes strings, so nothing showed it - until the headless event wrapper, with an enum, two
    /// booleans and two paths, answered with every value missing, which read as "the project holds
    /// no DQMH module". `lvai_run_and_read_typed.vi` converts each value to the control's type,
    /// and is what lvai_run_vi_and_read_values has used since 2026-09-16.
    /// </summary>
    internal async Task<(IReadOnlyList<LvValuesXml.Value>? Values, string? HelperError)> RunDetailedAsync(
        string scripts, string helperName, Dictionary<string, string> inputs,
        int timeoutSeconds, CancellationToken ct)
    {
        var aixml = Path.Combine(scripts, helperName + ".xml");
        if (!File.Exists(aixml)) return (null, null);

        var helperVi = Path.Combine(HelperDirectory(), helperName + ".vi");
        if (!File.Exists(helperVi) && await EnsureAsync(aixml, helperVi, timeoutSeconds, ct: ct) is false)
            return (null, null);
        return await RunViDetailedAsync(scripts, helperVi, inputs, timeoutSeconds, ct);
    }

    /// <summary>
    /// Run ANY VI on disk through the typed run helper - Delacor's own headless VIs need no
    /// wrapper of ours, only this.
    /// </summary>
    internal async Task<(IReadOnlyList<LvValuesXml.Value>? Values, string? HelperError)> RunViDetailedAsync(
        string scripts, string targetVi, Dictionary<string, string> inputs,
        int timeoutSeconds, CancellationToken ct)
    {
        var helperVi = targetVi;
        var wrapperAixml = Path.Combine(scripts, RunTools.HelperAixmlFileName);
        var wrapperVi = Path.Combine(HelperDirectory(),
            Path.ChangeExtension(RunTools.HelperAixmlFileName, ".vi"));
        if (HelperCache.NeedsRebuild(wrapperAixml, wrapperVi)
            && await EnsureAsync(wrapperAixml, wrapperVi, timeoutSeconds, ct: ct) is false)
            return (null, null);

        var request = new RunVIAsTopLevelRequest { ViPath = wrapperVi };
        request.Inputs["VI Path"] = helperVi;
        request.Inputs["Input Names"] = string.Join("\n", inputs.Keys);
        request.Inputs["Input Values"] = string.Join("\n", inputs.Values.Select(RunTools.Encode));

        var response = await connection.InvokeAsync((c, t) =>
            c.RunVIAsTopLevelAsync(request,
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        response.Outputs.TryGetValue("values xml", out var valuesXml);
        response.Outputs.TryGetValue("error xml", out var errorXml);
        var helperFailed = !string.IsNullOrEmpty(errorXml)
                           && ValueAfter(errorXml, "status") is "1";
        return (LvValuesXml.Parse(valuesXml), helperFailed ? errorXml : null);
    }

    internal async Task<bool> EnsureAsync(
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

    internal static string? Scalar(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        values.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Scalar;

    /// <summary>Every &lt;Val&gt; of an array indicator, in order.</summary>
    internal static List<string> Strings(IReadOnlyList<LvValuesXml.Value> values, string name)
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
    internal static string? Failed(IReadOnlyList<LvValuesXml.Value> values)
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

    internal static string BareName(string name) =>
        name.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

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

    internal static string HelperDirectory() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers");

    // ------------------------------------------------------------------ the window layer

    /// <summary>
    /// The only part of the DQMH tools that is not VI Server, used by DqmhDialogWatch to answer
    /// LabVIEW's "Save changes before closing?" modal: find a window by exact title, raise it, and
    /// send one SPACE.
    /// </summary>
    internal static class Win32
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
