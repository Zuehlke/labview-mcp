using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Changing ONE labelled block diagram constant of an existing VI, in place.
///
/// WHY THIS IS A TOOL. A negative control - break one expectation, confirm exactly one failure,
/// restore it - cost TWO full regenerations of the test, about 56 s each, because nothing could
/// change a single constant: measured 2026-09-25 on the third TypedefAfterGDevCon build. The
/// generators label every constant they write (`written 1`, `expected 2`), so the constant is
/// addressable, and `{LV.Constant}` `Value` is writable through VI Server. Measured the same day on
/// one probe VI carrying an int32, a double, a boolean, a string and an enum: every value written
/// was in the saved file on the next export and in the VI's output on the next run - a double
/// written into the int32 and the enum constants is converted by LabVIEW.
///
/// Numeric, boolean, string and enum constants - and since 2026-09-25 ARRAYS and CLUSTERS of those,
/// written as the same AIXML literal the generators author (<c>[[1,Ada],[2,Alan]]</c>). The third
/// ATM build found why that mattered: lvai_generate_test now labels its expectations for exactly
/// this tool, and the first subject it was accepted on returns a 2D string array, which this tool
/// refused. LvXmlLiteral builds LabVIEW's own XML from the literal and the constant's EXPORTED type,
/// and the helper sets it through Unflatten From XML into a variant. A path is still refused: its
/// literal carries AIXML escapes, and nobody has measured what comes back.
/// </summary>
[McpServerToolType]
internal sealed class ConstantTools(LvaiConnection connection)
{
    internal const string HelperAixmlFileName = "lvbd_set_constant.xml";

    [McpServerTool(Name = "lvai_set_constant", Destructive = true, OpenWorld = true,
                   Title = "Set one labelled block diagram constant of a VI")]
    [Description("""
        Edit: sets ONE block diagram constant, found by its exact LABEL, and saves the VI in place -
        no regeneration. The cheap NEGATIVE CONTROL for a generated test (its constants are labelled
        `written 1`, `expected 2`): set an expectation wrong, run, confirm one failure, set it back.
        The value is text converted to the constant's type: a number (integer, float, enum - an enum
        also takes an item name), TRUE/FALSE or 1/0, a string; an ARRAY or CLUSTER takes the AIXML
        literal (`[[9,X,Y,1]]`, `[true,5000,src]`) as lvai_generate_test lists under
        `expectedConstants`, members string, bool or numeric. A path, or an enum inside a compound,
        is refused. `verified` comes from a fresh export (`before` and `after` too). A label absent
        or present twice is refused with the labels that exist. CLOSE ANY PROJECT HOLDING THE VI
        FIRST - a copy open in the IDE would not see the change.
        """)]
    public async Task<string> SetConstantAsync(
        [Description(@"Absolute path to the .vi - it is saved in place")] string viPath,
        [Description("The constant's block diagram label, exactly - e.g. 'expected 2'")]
        string constantLabel,
        [Description("The new value as text - '2', '0.5', 'TRUE', 'CH9', or an enum item name. A string may contain line breaks")]
        string value,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_set_constant", async () =>
        {
            var vi = Path.GetFullPath(viPath);
            if (!File.Exists(vi))
                return Json.Error("fileNotFound", $"No VI at '{vi}'.", new { viPath });
            if (string.IsNullOrEmpty(constantLabel))
                return Json.Error("badArguments", "constantLabel is empty.");
            // A LINE BREAK IS FINE SINCE 2026-09-25: lvai_run_vi_and_read_values encodes it for the
            // wire and its typed helper decodes it, so the multi-line message expectations the
            // fourth ATM cold build could not break for a negative control are reachable now.

            // ---- 1. which constant, and of what type - from the VI's own export
            var before = await ExportAsync(vi, timeoutSeconds, ct);
            if (before is null)
                return Json.Error("exportFailed", $"'{Path.GetFileName(vi)}' could not be exported.");
            var (target, refusal) = Find(before, constantLabel);
            if (refusal is not null) return refusal;
            var type = (string?)target!.Attribute("type") ?? "";
            var (kind, text, why) = Convert(type, value);
            if (kind is null)
                return Json.Error("constantTypeNotSettable", why!,
                    new { constantLabel, type });

            // ---- 2. the write
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory", "The scripts folder next to the exe is missing.");
            var helper = await HelperAsync(Path.Combine(scripts, HelperAixmlFileName), timeoutSeconds, ct);
            if (helper.Failure is not null) return helper.Failure;
            var ran = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(
                helper.Vi!, new JsonObject
                {
                    ["vi path"] = vi,
                    ["constant label"] = constantLabel,
                    ["value"] = text,
                    ["kind"] = kind,
                }.ToJsonString(), timeoutSeconds: timeoutSeconds, ct: ct)) as JsonObject;
            var values = ran?["values"] as JsonObject;
            string? Str(string name) => (values?[name] as JsonObject)?["value"]?.GetValue<string>();
            var code = int.TryParse(Str("code"), out var c) ? c : (int?)null;

            // ---- 3. the verdict, from the file
            var after = await ExportAsync(vi, timeoutSeconds, ct);
            var afterValue = after is null ? null
                : (string?)Constants(after).FirstOrDefault(e => Label(e) == constantLabel)?.Attribute("value");
            var verified = afterValue is not null && kind switch
            {
                "Compound" => LvXmlLiteral.Same(type, value, Unescape(afterValue)),
                // a string's export carries AIXML escapes (\0A, \5C) where the caller wrote the
                // characters themselves - compared raw, a backslash or a line break never matched
                "String" => string.Equals(Unescape(afterValue, keepSeparators: false), value,
                                          StringComparison.Ordinal),
                _ => Same(kind, afterValue, text),
            };

            return Json.Document(new JsonObject
            {
                ["ok"] = verified && code is 0,
                ["viPath"] = vi,
                ["constantLabel"] = constantLabel,
                ["type"] = type,
                ["kind"] = kind,
                ["before"] = (string?)target.Attribute("value"),
                ["asked"] = value,
                ["after"] = afterValue,
                ["verified"] = verified,
                ["helperErrorCode"] = code,
                ["helperErrorSource"] = Str("source"),
                ["note"] = verified
                    ? "The constant holds the new value in the saved VI, read back from its export."
                    : "The saved VI does NOT hold the value asked for - read helperErrorCode, and " +
                      "check that no project holding this VI is open in the IDE.",
            });
        }));

    // ------------------------------------------------------------------ pure parts

    /// <summary>Every constant in an export, at any depth - loops and case frames included.</summary>
    internal static IEnumerable<XElement> Constants(XElement vi) =>
        vi.Descendants("Constant");

    private static string? Label(XElement constant) => (string?)constant.Attribute("_name");

    /// <summary>The one constant carrying the label, or the refusal naming the labels there are.</summary>
    internal static (XElement? Constant, string? Refusal) Find(XElement vi, string label)
    {
        var matches = Constants(vi).Where(e => Label(e) == label).ToList();
        if (matches.Count == 1) return (matches[0], null);
        var labels = Constants(vi).Select(Label).Where(l => l is { Length: > 0 })
                                  .Distinct().ToArray();
        return (null, matches.Count == 0
            ? Json.Error("constantNotFound",
                $"No block diagram constant is labelled '{label}'. Labels are exact.",
                new { constantLabel = label, labels })
            : Json.Error("constantAmbiguous",
                $"{matches.Count} constants are labelled '{label}'; the helper could not tell " +
                "which one is meant. Relabel one of them.",
                new { constantLabel = label, labels }));
    }

    /// <summary>
    /// The helper's `kind` and the text it converts, from the constant's AIXML type - or null and
    /// the reason. An enum item NAME becomes its index, because the helper converts numbers.
    /// </summary>
    internal static (string? Kind, string Text, string? Why) Convert(string type, string value)
    {
        var t = type.Trim();
        if (t == "bool") return ("Boolean", value, null);
        if (t == "string") return ("String", value, null);
        if (LvXmlLiteral.IsCompound(t))
        {
            var (xml, why) = LvXmlLiteral.Build(t, value);
            return xml is null
                ? (null, value, $"'{value}' cannot be set on a `{t}` constant: {why}")
                : ("Compound", xml, null);
        }

        var brace = t.IndexOf('{');
        var bare = brace < 0 ? t : t[..brace];
        var numeric = bare is "int8" or "int16" or "int32" or "int64" or "uint8" or "uint16"
            or "uint32" or "uint64" or "single" or "double" or "float" or "extended";
        if (!numeric)
            return (null, value, $"A `{t}` constant is not settable from text - only numeric, " +
                                 "boolean, string and enum constants are.");
        if (brace < 0)
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? ("Digital", value, null)
                : (null, value, $"'{value}' is not a number, and the constant is `{t}`.");

        // an ENUM: `uint16{Off,Voltage,Current}` - an index, or an item name
        var items = t[(brace + 1)..t.LastIndexOf('}')].Split(',');
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return index >= 0 && index < items.Length
                ? ("Digital", value, null)
                : (null, value, $"{index} is not an item of `{t}` - it has {items.Length}.");
        var named = Array.IndexOf(items, value);
        return named >= 0
            ? ("Digital", named.ToString(CultureInfo.InvariantCulture), null)
            : (null, value, $"'{value}' is neither an index nor an item of `{t}`.");
    }

    /// <summary>Whether the exported value is the one written, compared as the kind reads it.</summary>
    internal static bool Same(string kind, string exported, string written) => kind switch
    {
        "Digital" => double.TryParse(exported, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                     && double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                     && a.Equals(b),
        "Boolean" => string.Equals(exported, Truthy(written) ? "true" : "false",
                                   StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(exported, written, StringComparison.Ordinal),
    };

    /// <summary>
    /// An exported literal with its AIXML escapes resolved (<c>\5C</c> to a backslash). Only the
    /// EXPORTED side needs it: the caller's literal is plain text, the way lvai_generate_test
    /// reports it. The separators inside a compound are never escaped, so resolving first cannot
    /// merge two members.
    /// </summary>
    internal static string Unescape(string exported, bool keepSeparators = true) =>
        System.Text.RegularExpressions.Regex.Replace(exported, @"\\([0-9A-Fa-f]{2})",
            m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 16)) is var ch
                 && keepSeparators && ch is ',' or '[' or ']'
                ? m.Value : ch.ToString());

    private static bool Truthy(string text) =>
        text.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Trim() == "1";

    // ------------------------------------------------------------------ plumbing

    private async Task<XElement?> ExportAsync(string vi, int timeoutSeconds, CancellationToken ct)
    {
        var target = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "setconst",
                                  Path.GetFileNameWithoutExtension(vi) + ".xml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
            vi, target, returnContent: true, maxContentChars: 0, timeoutSeconds, refresh: true, ct: ct);
        try
        {
            var xml = (JsonNode.Parse(answer) as JsonObject)?["xml"]?.GetValue<string>();
            return xml is null ? null : XElement.Parse(xml);
        }
        catch (Exception e) when (e is JsonException or System.Xml.XmlException) { return null; }
    }

    private async Task<(string? Vi, string? Failure)> HelperAsync(string aixml, int timeoutSeconds,
                                                                  CancellationToken ct)
    {
        if (!File.Exists(aixml))
            return (null, Json.Error("helperMissing", $"No helper AIXML at '{aixml}'."));
        // ON PYLABVIEW the helper is COMPOSED into helpers-pylabview and run over ActiveX
        if (BackendRoute.OnPyLabview)
            return (await BackendRoute.ComposedHelperAsync(aixml, timeoutSeconds, ct), null);
        var vi = Path.Combine(AixmlBackend.HelperFolder(),
                              Path.ChangeExtension(Path.GetFileName(aixml), ".vi"));
        Directory.CreateDirectory(Path.GetDirectoryName(vi)!);
        if (File.Exists(vi) && File.GetLastWriteTimeUtc(aixml) <= File.GetLastWriteTimeUtc(vi))
            return (vi, null);

        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return (null, Json.Error("helperAixmlInvalid",
                $"The helper AIXML does not validate: {validation.ErrorMessage}"));
        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = vi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        return generation.ErrorCode == 0 && File.Exists(vi)
            ? (vi, null)
            : (null, Json.Error("helperGenerationFailed",
                $"The helper could not be generated: {generation.ErrorMessage}"));
    }
}
