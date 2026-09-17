using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Adding private data FIELDS to a class that already exists.
///
/// WHY THIS IS A SEPARATE TOOL. `lvai_create_class` is the only other thing here that touches
/// private data and it CREATES: its `overwrite` is refused by default because it would drop the
/// class's members. So "add one field to a finished class" read as unreachable, and the measured
/// cost of believing that was a method written to take a value and NOT store it - a gap in this
/// toolset reported as a property of LabVIEW.
///
/// IT IS THE SAME PROVIDER, on the same route. `Message Maker.lvlib:Add Member Data to Private
/// Data Control.vi` takes a `Target Path`, an `AppInst` and an array of front-panel CONTROL
/// REFERENCES, and does not care how old the class is. `lvai_create_class` hands it a class it
/// made a second earlier; this hands it one with eleven members.
///
/// IT APPENDS - measured 2026-09-17, and that is the fact the whole tool rests on. On a fixture
/// built to the real shape (two fields AND their four wizard accessors) one carrier control took
/// the class from `Alpha, Beta` to `Alpha, Beta, Rechtslauf` with all four members unchanged and
/// `Read Alpha.vi` still `execState 1`. Then on a real 3-field, 9-member actor class: 4 fields,
/// 9 members, everything still executable after a project close. The verify below re-reads the
/// file and gates `ok` on the OLD fields surviving, because "appends" is exactly the property a
/// future LabVIEW could change without telling anyone.
/// </summary>
[McpServerToolType]
internal sealed class ClassFieldTools(LvaiConnection connection)
{
    private const string HelperAixmlFileName = "lvai_add_class_field.xml";

    [McpServerTool(Name = "lvai_add_class_field", Destructive = true, OpenWorld = true,
        Title = "Add private data fields to a LabVIEW class that already exists")]
    [Description("""
        MUTATING: adds private data FIELDS to a `.lvclass` that ALREADY EXISTS, through NI's own
        `Message Maker.lvlib:Add Member Data to Private Data Control.vi` - the same provider
        `lvai_create_class` drives, which does not care how old the class is.
        USE THIS RATHER THAN RE-CREATING THE CLASS. `lvai_create_class`'s `overwrite` would drop
        every member; this leaves them alone. Measured 2026-09-17 on a class with 9 members and
        again on a fixture with 4 wizard accessors: the field is APPENDED, the members are
        untouched, and every VI is still executable after a project close.
        IT ADDS ONLY - there is no remove and no rename here. A field name the class already
        carries is refused before LabVIEW is touched, because NI's provider would take it and you
        would end up with two fields of one name.
        NEEDS A PROJECT OPEN AND ACTIVE: the provider reaches LabVIEW through
        Project:Active Project and answers Error 1055 without one. Pass projectPath and this call
        opens it.
        THE ACCESSORS ARE NOT CREATED - a new field has none. Follow with
        `lvai_create_accessors` passing `fromField` as the new field's index, which is the old
        field count (the answer reports it as `nextFromField`); leaving it at -1 resumes from the
        MEMBER count, which is wrong on a class that also has methods.
        VERIFIED FROM THE SAVED FILE, and `ok` is gated on the OLD fields surviving as well as the
        new ones arriving - "it appends" is a measured property, not a promise NI made.
        """)]
    public async Task<string> AddClassFieldAsync(
        [Description("Absolute path to the .lvclass to extend. It must already exist")]
        string lvclassPath,
        [Description("""
            Fields to ADD, as `<type>.<name>`, comma separated - the same spelling
            `lvai_create_class` takes, e.g. `bool.Rechtslauf` or `string.Hersteller,int32.Baujahr`.
            A name the class already has is refused. Cluster, array and enum fields are not
            supported here, for the same reason they are not there.
            """)]
        string fields,
        [Description("""
            The .lvproj to open first. Without an active project the provider answers Error 1055.
            Omit only when you have opened one yourself. This file is never EDITED here.
            """)]
        string? projectPath = null,
        [Description("""
            Where to keep the generated CARRIER VI, whose controls become the fields. It is KEPT,
            not deleted: LabVIEW adopts the carrier into the open project, and deleting it leaves
            LabVIEW holding a project whose items no longer exist - the same trap
            `lvai_create_class`'s `keepCarrier` documents. Defaults to a per-user temp directory.
            """)]
        string? carrierViPath = null,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // EVERY CHECK BELOW RUNS BEFORE LABVIEW. This changes a class's private data under
            // however many members it already has, and there is no undo for a half-applied one.
            if (!File.Exists(lvclassPath))
                return Json.Error("classMissing",
                    $"No .lvclass at '{lvclassPath}'. This tool extends a class that already " +
                    "exists; lvai_create_class makes one.",
                    new { lvclassPath });

            var classPath = Path.GetFullPath(lvclassPath);
            if (!classPath.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase))
                return Json.Error("notAClass",
                    $"'{classPath}' is not a .lvclass.", new { lvclassPath = classPath });

            List<LvClass.Field> parsed;
            try { parsed = LvClass.ParseFields(fields); }
            catch (ArgumentException e) { return Json.Error("badArguments", e.Message, new { fields }); }

            if (parsed.Count == 0)
                return Json.Error("badArguments",
                    "`fields` names no field, so this call asks for nothing.", new { fields });

            var duplicateInRequest = parsed.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateInRequest is not null)
                return Json.Error("badArguments",
                    $"`fields` names '{duplicateInRequest.Key}' more than once.", new { fields });

            // ASK THE FILE WHAT IS ALREADY THERE. NI's provider does not check, so a repeated name
            // would give the class two fields called the same thing - which nothing downstream
            // reports and the wizard then turns into two accessors of one name.
            var before = await ClassBindTools.PrivateDataFields.ReadAsync(classPath, timeoutSeconds, ct: ct);
            if (before.Unavailable is { Length: > 0 } && before.Labels.Count == 0)
                return Json.Error("privateDataUnreadable",
                    "The class's private data could not be read, so this call cannot tell whether " +
                    $"a field name is already taken: {before.Unavailable}",
                    new { lvclassPath = classPath });

            var taken = parsed.Where(f => before.Labels.Any(
                    l => string.Equals(l, f.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Name).ToArray();
            if (taken.Length > 0)
                return Json.Error("fieldAlreadyPresent",
                    $"'{Path.GetFileName(classPath)}' already has a field called " +
                    $"{string.Join(", ", taken.Select(t => $"'{t}'"))}. NI's provider would add a " +
                    "second one of that name; this refuses instead, because nothing downstream " +
                    "reports the duplicate and the accessor wizard turns it into two VIs.",
                    new JsonObject
                    {
                        ["lvclassPath"] = classPath,
                        ["taken"] = new JsonArray([.. taken.Select(t => (JsonNode)t)]),
                        ["fieldsAlreadyThere"] =
                            new JsonArray([.. before.Labels.Select(l => (JsonNode)l)]),
                    });

            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperAixmlFileName) : null)
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_add_class_field.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder)
                Directory.CreateDirectory(helperFolder);

            var steps = new JsonArray();
            var helperGenerated = false;
            if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct) is { } failure)
                    return failure;
                helperGenerated = true;
            }

            // THE CARRIER IS THE ARGUMENT. NI's provider takes control REFERENCES, so the fields
            // are expressed as a VI whose front panel carries one control each - the one part of
            // this AIXML is genuinely good at.
            var className = Path.GetFileNameWithoutExtension(classPath);
            var carrierVi = Path.GetFullPath(carrierViPath ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "carriers",
                $"{className}-add-{DateTime.UtcNow:yyyyMMddHHmmss}.vi"));
            if (Path.GetDirectoryName(carrierVi) is { Length: > 0 } carrierFolder)
                Directory.CreateDirectory(carrierFolder);
            var carrierAixml = Path.ChangeExtension(carrierVi, ".xml");
            await File.WriteAllTextAsync(
                carrierAixml, LvClass.CarrierAixml(className, parsed), ct);

            var carrier = await new BulkTools(connection).GenerateViAsync(
                carrierAixml, carrierVi, openVI: false, measurePane: false,
                timeoutSeconds: timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "carrier", ["answer"] = Read(carrier) });
            if (!File.Exists(carrierVi))
                return Json.Error("carrierNotGenerated",
                    "The carrier VI could not be generated, so there are no control references to " +
                    "hand the provider and nothing would be added.",
                    new JsonObject { ["carrierViPath"] = carrierVi, ["steps"] = steps });

            if (projectPath is { Length: > 0 })
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct: ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = Read(opened) });
                if ((Read(opened) as JsonObject)?["projectBecameActive"]?.GetValue<bool>() is false)
                    return Json.Error("projectDidNotBecomeActive",
                        "The project did not become active, and the provider reaches LabVIEW " +
                        "through Project:Active Project - it would answer Error 1055. The measured " +
                        "cause is LabVIEW not having the foreground.",
                        new JsonObject { ["steps"] = steps });
            }

            var membersBefore = MemberCount(classPath);

            var inputs = new JsonObject
            {
                ["class path"] = classPath,
                ["carrier path"] = carrierVi,
            };
            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "addMemberData", ["answer"] = Read(answer) });

            if (ErrorCode(Values(answer)) is { } code && code != 0)
                return Json.Document(new JsonObject
                {
                    ["ok"] = false,
                    ["errorKind"] = "providerFailed",
                    ["errorCode"] = code,
                    ["errorSource"] = ErrorSource(Values(answer)),
                    ["lvclassPath"] = classPath,
                    ["carrierViPath"] = carrierVi,
                    ["steps"] = steps,
                    ["note"] = code == 1
                        ? "Error 1 from LabVIEW Class:Open means the provider could not open the " +
                          "class. Measured cause: another class with the SAME QUALIFIED NAME is " +
                          "already in LabVIEW's memory - a copy of this class under a different " +
                          "path, typically. It is not a path-spelling problem."
                        : "The provider reported an error, so assume nothing was added and read " +
                          "the class's fields before trying again.",
                });

            // ASK THE FILE. The run reporting 0 is also what a provider that reached a different
            // copy of the class would produce - and "it appends" is a measured property, so the
            // OLD fields are checked as carefully as the new ones.
            var verify = new JsonObject();
            var verified = false;
            try
            {
                var after = await ClassBindTools.PrivateDataFields.ReadAsync(
                    classPath, timeoutSeconds, ct: ct);
                var missingNew = parsed.Where(f => !after.Labels.Any(
                        l => string.Equals(l, f.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(f => f.Name).ToArray();
                var lostOld = before.Labels.Where(l => !after.Labels.Any(
                        a => string.Equals(a, l, StringComparison.OrdinalIgnoreCase))).ToArray();
                var membersAfter = MemberCount(classPath);

                verified = missingNew.Length == 0 && lostOld.Length == 0
                    && membersBefore is not null && membersAfter == membersBefore;

                verify["fieldsBefore"] =
                    new JsonArray([.. before.Labels.Select(l => (JsonNode)l)]);
                verify["fieldsAfter"] = new JsonArray([.. after.Labels.Select(l => (JsonNode)l)]);
                verify["fieldsNotAdded"] =
                    new JsonArray([.. missingNew.Select(n => (JsonNode)n)]);
                verify["fieldsLost"] = new JsonArray([.. lostOld.Select(n => (JsonNode)n)]);
                verify["membersBefore"] = membersBefore;
                verify["membersAfter"] = membersAfter;
                verify["source"] =
                    "the saved .lvclass, read with pylabview and as XML - no LabVIEW involved";
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException)
            {
                verify["note"] = $"The class could not be re-read: {e.Message}";
            }

            return Json.Document(new JsonObject
            {
                ["ok"] = verified,
                ["lvclassPath"] = classPath,
                ["fieldsAdded"] = new JsonArray([.. parsed.Select(f =>
                    (JsonNode)new JsonObject { ["name"] = f.Name, ["type"] = f.Type })]),
                ["nextFromField"] = before.Labels.Count,
                ["carrierViPath"] = carrierVi,
                ["helperViPath"] = helperVi,
                ["helperAixmlPath"] = Path.GetFullPath(aixml),
                ["helperGenerated"] = helperGenerated,
                ["verify"] = verify,
                ["steps"] = steps,
                ["note"] = verified
                    ? "Added and verified from the saved class file: the new field(s) are there, " +
                      "every old one survived, and the member count did not move. The new field " +
                      "has NO accessors - call lvai_create_accessors with fromField set to " +
                      "`nextFromField` above, because -1 resumes from the MEMBER count and that is " +
                      "wrong on a class that also has methods. Then check the class with " +
                      "lvai_exec_state after a project close."
                    : "The provider reported 0, but the saved class does not look right - read " +
                      "verify.fieldsNotAdded, verify.fieldsLost and the member counts. A lost " +
                      "field means the provider REPLACED rather than appended, which would be a " +
                      "change in measured behaviour and is worth reporting.",
            });
        });

    /// <summary>
    /// How many members the class lists, read straight out of the `.lvclass` XML. It is here as a
    /// SECOND witness beside the field list: the failure this tool exists to avoid is losing
    /// members, and a field reader cannot see one go. Null when the file will not parse, which is
    /// reported rather than counted as unchanged.
    ///
    /// FILTERED ON <c>Type="VI"</c> rather than on having a URL, because a class's PARENT LINK is
    /// an <c>&lt;Item Type="Parent"&gt;</c> that carries one too - counting it would still detect a
    /// lost member, but the number printed beside it would not be the member count it claims to be.
    /// </summary>
    private static int? MemberCount(string classPath)
    {
        try
        {
            return XDocument.Load(classPath).Descendants("Item")
                .Count(i => i.Attribute("Type")?.Value == "VI");
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return null;
        }
    }

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

        return generation.ErrorCode != 0
            ? Json.Error("helperNotGenerated",
                $"The helper VI could not be generated: {generation.ErrorMessage}",
                new { helperViPath = helperVi, errorCode = generation.ErrorCode })
            : null;
    }

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static JsonObject? Values(string answer) =>
        (Read(answer) as JsonObject)?["values"] as JsonObject;

    private static int? ErrorCode(JsonObject? values)
    {
        if ((values?["error out"] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml)
            return null;
        var match = Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private static string? ErrorSource(JsonObject? values)
    {
        if ((values?["error out"] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml)
            return null;
        var match = Regex.Match(xml, "<Name>source</Name>\\s*<Val>(.*?)</Val>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }
}
