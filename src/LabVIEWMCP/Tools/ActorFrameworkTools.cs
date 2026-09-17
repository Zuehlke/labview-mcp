using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Actor Framework message classes.
///
/// WHY THIS IS NOT AIXML. A message class must override <c>Message.lvclass:Do.vi</c>, and a
/// GENERATED override is not executable whatever is on its diagram - measured down to a
/// pass-through with zero nodes, with NI's own child as a control at execState 1. Four explanations
/// were tested and refuted, including <c>NI.ClassItem.Flags</c>. So nothing here authors a diagram.
///
/// WHAT IT DOES INSTEAD is drive NI's own Message Maker, the code behind the IDE's
/// "Actor Framework -> Create Messages for Actor" menu item. <c>Copy Class.vi</c> clones
/// <c>Message Template.lvclass</c> - whose <c>Do.vi</c> is already a real override - and the rest of
/// <c>Build Do.vi</c>'s and <c>Build Send.vi</c>'s chains rewire it onto the actor method and build
/// the Send VI. docs/labview-actor-framework.md has every pane and every trap.
///
/// TWO STEPS OF NI'S OWN CHAINS ARE DELIBERATELY OMITTED, and both for the same reason.
/// <c>FP.Open</c> is skipped because the helper has no window to show; and
/// <c>Replace Actor if Using PPL.vi</c> / <c>Replace Enqueue if Using PPL.vi</c> are skipped because
/// they take a <c>ref{LV.TargetItem}</c>, whose only public source is <c>mxLvGetItem.vi</c> and a
/// <c>NIIM</c> typedef that AIXML cannot build. Omitting them is sound outside packed libraries;
/// A MESSAGE FOR AN ACTOR INSIDE A .lvlibp IS THEREFORE OUT OF SCOPE and says so rather than
/// producing something subtly unwired.
/// </summary>
[McpServerToolType]
internal sealed class ActorFrameworkTools(LvaiConnection connection)
{
    private const string HelperAixmlFileName = "lvai_create_message_class.xml";

    /// <summary>The Actor Framework's own base class, relative to a LabVIEW installation root.</summary>
    private const string ActorRelative = @"vi.lib\ActorFramework\Actor\Actor.lvclass";

    private const string MessageRelative = @"vi.lib\ActorFramework\Message\Message.lvclass";

    private const string TemplateRelative =
        @"resource\Framework\Providers\MessageMakerProvider\_Message Maker\_templates\" +
        @"Message Template\Message Template.lvclass";

    /// <summary>How far up an inheritance chain to look for Actor.lvclass before giving up.</summary>
    private const int MaxAncestorWalk = 16;

    [McpServerTool(Name = "lvai_create_message_class", Destructive = true, OpenWorld = true,
        Title = "Create an Actor Framework message class for one actor method")]
    [Description("""
        MUTATING: creates a complete Actor Framework MESSAGE class for one public method of an
        actor - the class, its private data (one field per parameter of the method), the `Do.vi`
        override that calls the method, and the `Send <Method>.vi` that enqueues it.
        IT AUTHORS NO DIAGRAM. A generated override of Message.lvclass:Do.vi is eBad whatever is on
        it; this drives NI's own Message Maker instead, which clones a template whose Do.vi is
        already a real override. Measured 2026-09-17 on Counter.lvclass:Increment.vi: Do.vi at
        execState 1 calling Counter.lvclass:Increment.vi with Amount wired from the message's own
        private data, and Send Increment.vi at execState 1 with the pane
        `Message Enqueuer` (11, required), `Amount` (10), `Message Priority (Normal)` (7),
        `error in (no error)` (8) -> `error out` (0), `Message Enqueuer out` (3) - byte for byte
        NI's own Send VI shape.
        NEEDS A PROJECT OPEN AND ACTIVE: every step reaches LabVIEW through Project:Active Project
        and answers Error 1055 without one. Pass projectPath and this call opens it.
        THE CLASS IS SAVED AT THE END, and that is not a detail. The Message Maker's changes to the
        CLASS - its private data control and the renamed Send member - live in memory, so a run that
        saves only the two VIs reports 0 at every stage, gives two VIs at execState 1, and leaves
        BOTH eBad after the next project close, with the class on disk still listing
        `Send Template.vi` and no payload field. Measured, then fixed with LVClass.Open + Save.
        VERIFIED FROM THE SAVED FILE: the answer's `verify` block is read back with pylabview, not
        from the run, so `fields` and `members` are what is on disk.
        THE ACTOR MUST DESCEND FROM Actor.lvclass - checked by walking the class file's own parent
        links, which is also how the LabVIEW installation is located, so the template and the
        message parent always come from the same install as the actor.
        """)]
    public async Task<string> CreateMessageClassAsync(
        [Description(@"Absolute path to the actor's .lvclass - it must descend from Actor.lvclass")]
        string actorClassPath,
        [Description("""
            The actor method the message should call, e.g. "Increment" or "Increment.vi". It must
            be a PUBLIC member of the actor class and live beside it on disk. Its non-class,
            non-error inputs become the message's payload, one private data field each.
            """)]
        string actorMethodName,
        [Description("""
            Name for the new message class. Defaults to "<method> Msg", which is NI's own
            convention - `Increment` gives `Increment Msg`.
            """)]
        string? className = null,
        [Description("""
            Folder to create the message class in. Defaults to a folder named after the class
            BESIDE the actor's own folder, which is where NI's wizard puts it. The class lands
            directly in this folder, not in a subfolder of it.
            """)]
        string? directory = null,
        [Description("""
            The .lvproj to open first. The Message Maker reaches LabVIEW through
            Project:Active Project, so without an active project every stage answers Error 1055.
            Omit only when you have opened one yourself.
            """)]
        string? projectPath = null,
        [Description("""
            The message class template to clone. Defaults to NI's `Message Template.lvclass` in the
            actor's own LabVIEW installation. DO NOT pass `Concrete Message Template.lvclass`: it
            ships no `Send Template.vi`, so the Send step answers Error 7 naming a file that was
            never copied, and it needs a different builder chain entirely.
            """)]
        string? templatePath = null,
        [Description("Parent of the new class. Defaults to Actor Framework's Message.lvclass")]
        string? parentClassPath = null,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(actorClassPath))
                throw new FileNotFoundException($"No .lvclass at '{actorClassPath}'.",
                    actorClassPath);

            var actorFull = Path.GetFullPath(actorClassPath);
            var actorFolder = Path.GetDirectoryName(actorFull)
                ?? throw new InvalidOperationException($"'{actorFull}' has no directory.");

            // The method is named, not pathed, because it must be a MEMBER - and a member's file
            // sits beside the class. Accepting either spelling costs one line and removes a whole
            // class of "did you mean Increment or Increment.vi" round trips.
            var methodBase = actorMethodName.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)
                ? actorMethodName[..^3] : actorMethodName;
            if (methodBase.Length == 0)
                return Json.Error("badArguments",
                    "actorMethodName is empty; pass the method's name, e.g. \"Increment\".");

            var methodFile = methodBase + ".vi";
            var methodPath = Path.Combine(actorFolder, methodFile);

            var actorInfo = LvClass.Read(actorFull);
            if (!actorInfo.Members.Any(m =>
                    string.Equals(m.Name, methodFile, StringComparison.OrdinalIgnoreCase)))
                return Json.Error("notAClassMember",
                    $"'{methodFile}' is not a member of '{actorInfo.ClassName}'. A message is " +
                    "built from the actor's own public method, so the method has to be one.",
                    new JsonObject
                    {
                        ["classPath"] = actorFull,
                        ["members"] = new JsonArray(
                            [.. actorInfo.Members.Select(m => (JsonNode)m.Name!)]),
                    });

            if (!File.Exists(methodPath))
                return Json.Error("methodFileMissing",
                    $"'{actorInfo.ClassName}' lists '{methodFile}' but there is no file at " +
                    $"'{methodPath}'. The Message Maker reads the method's front panel, so the " +
                    "file has to be there.",
                    new { methodPath });

            // WALKING THE PARENT LINKS DOES TWO JOBS AT ONCE: it proves the class really is an
            // actor, and the Actor.lvclass it lands on names the installation - so the template and
            // the message parent come from the SAME LabVIEW as the actor rather than from a guess.
            if (ActorInstallationRoot(actorFull) is not { } installRoot)
                return Json.Error("notAnActor",
                    $"'{actorInfo.ClassName}' does not descend from " +
                    "Actor Framework.lvlib:Actor.lvclass, so a message for it would have nothing " +
                    "to deliver to. Create it with lvai_create_class and parentClassPath pointing " +
                    "at vi.lib\\ActorFramework\\Actor\\Actor.lvclass first.",
                    new JsonObject
                    {
                        ["classPath"] = actorFull,
                        ["ancestors"] = new JsonArray(
                            [.. actorInfo.Ancestors.Select(a => (JsonNode)a!)]),
                    });

            var name = className is { Length: > 0 } ? className : $"{methodBase} Msg";
            var destination = Path.GetFullPath(directory is { Length: > 0 }
                ? directory
                : Path.Combine(Path.GetDirectoryName(actorFolder) ?? actorFolder, name));

            var newClassPath = Path.Combine(destination, name + ".lvclass");
            if (File.Exists(newClassPath))
                return Json.Error("messageClassExists",
                    $"'{newClassPath}' already exists. Copy Class.vi does not overwrite, and " +
                    "LabVIEW answers Error 1357 for a path it already holds in memory. Delete the " +
                    "folder with the project CLOSED, or pass a different className or directory.",
                    new { newClassPath });

            var template = templatePath is { Length: > 0 }
                ? Path.GetFullPath(templatePath) : Path.Combine(installRoot, TemplateRelative);
            if (!File.Exists(template))
                return Json.Error("templateMissing",
                    $"No message template at '{template}'.", new { templatePath = template });

            var parent = parentClassPath is { Length: > 0 }
                ? Path.GetFullPath(parentClassPath) : Path.Combine(installRoot, MessageRelative);
            if (!File.Exists(parent))
                return Json.Error("messageParentMissing",
                    $"No Message.lvclass at '{parent}'.", new { parentClassPath = parent });

            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperAixmlFileName) : null)
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_create_message_class.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            var steps = new JsonArray();
            var helperGenerated = false;
            if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct) is { } failure)
                    return failure;
                helperGenerated = true;
            }

            if (projectPath is { Length: > 0 })
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct: ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = Read(opened) });
                if ((Read(opened) as JsonObject)?["projectBecameActive"]?.GetValue<bool>() is false)
                    return Json.Error("projectDidNotBecomeActive",
                        "The project did not become active, and every Message Maker step reaches " +
                        "LabVIEW through Project:Active Project - they would all answer Error " +
                        "1055. The measured cause is LabVIEW not having the foreground.",
                        new JsonObject { ["steps"] = steps });
            }

            Directory.CreateDirectory(destination);

            // Every value crosses as a STRING - that is the runner's contract on the way in.
            var inputs = new JsonObject
            {
                ["destination directory"] = destination,
                ["class name"] = name,
                ["template path"] = template,
                ["parent class path"] = parent,
                ["actor class path"] = actorFull,
                ["actor method path"] = methodPath,
                ["actor method name"] = methodBase,
            };

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);

            steps.Add(new JsonObject { ["step"] = "messageMaker", ["answer"] = Read(answer) });

            var values = Values(answer);

            // ONE STAGE PER NI VI, IN ORDER, so a failure names the step that produced it rather
            // than the run as a whole. Reading them all and reporting the FIRST non-zero is what
            // turns "it failed" into "Copy Class refused because the class exists".
            foreach (var (stage, label) in Stages)
                if (StageCode(values, stage) is { } code && code != 0)
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = label,
                        ["errorCode"] = code,
                        ["errorSource"] = StageSource(values, stage),
                        ["classPath"] = newClassPath,
                        ["helperViPath"] = helperVi,
                        ["helperAixmlPath"] = Path.GetFullPath(aixml),
                        ["helperGenerated"] = helperGenerated,
                        ["steps"] = steps,
                        ["note"] = "The Message Maker stopped at this step. Nothing later ran, so " +
                            "the class on disk is half-built - delete its folder with the project " +
                            "CLOSED before calling again, because Copy Class.vi does not overwrite.",
                    });

            // ASK THE FILE, NOT THE RUN. Every stage reporting 0 is exactly what the unsaved-class
            // defect also produced, so the payload field and the member list are read back off disk.
            var verify = new JsonObject();
            var verified = File.Exists(newClassPath);
            if (verified)
            {
                var info = LvClass.Read(newClassPath);
                verify["members"] = new JsonArray(
                    [.. info.Members.Select(m => (JsonNode)m.Name!)]);
                verify["inheritsFrom"] = info.BaseClass?.Name;
                verify["privateDataBytes"] = info.PrivateDataBytes;

                // THE PAYLOAD FIELD IS THE CHECK THAT MATTERS. Its absence is exactly what the
                // unsaved-class defect produced while every stage still reported 0, so it is read
                // back off the file rather than inferred from `payload control count`.
                var read = await ClassBindTools.PrivateDataFields.ReadAsync(
                    info.Path, timeoutSeconds, ct: ct);
                if (read.Unavailable is { } why) verify["fieldsNote"] = why;
                else
                    verify["fields"] = new JsonArray(
                        [.. (read.Types ?? []).Select(f => (JsonNode)new JsonObject
                        {
                            ["label"] = f.Label, ["type"] = f.Type, ["detail"] = f.Detail,
                        })]);

                verify["source"] = "the saved .lvclass, read with pylabview - no LabVIEW involved";
            }

            return Json.Document(new JsonObject
            {
                ["ok"] = verified,
                ["classPath"] = newClassPath,
                ["sendMethodPath"] = Scalar(values, "new send method"),
                ["doMethodPath"] = Path.Combine(destination, "Do.vi"),
                ["payloadControlCount"] = Scalar(values, "payload control count"),
                ["actorClassPath"] = actorFull,
                ["actorMethodPath"] = methodPath,
                ["templatePath"] = template,
                ["parentClassPath"] = parent,
                ["helperViPath"] = helperVi,
                ["helperAixmlPath"] = Path.GetFullPath(aixml),
                ["helperGenerated"] = helperGenerated,
                ["verify"] = verified ? verify : null,
                ["steps"] = steps,
                ["note"] = verified
                    ? "Created and verified from the class file. The class is SAVED, so it survives " +
                      "the next project close - a run that saved only the VIs was measured leaving " +
                      "both eBad afterwards. Check the two VIs with lvai_exec_state: every " +
                      "file-level check here is green for a class whose members do not compile."
                    : "Every stage reported 0 but no .lvclass is at the expected path, so nothing " +
                      "can be verified. Treat this as a failure.",
            });
        });

    /// <summary>The helper's stages, in the order its diagram runs them.</summary>
    private static readonly (string Indicator, string Label)[] Stages =
    [
        ("copy class error", "copyClass"),
        ("read member data error", "copyMemberDataFromTarget"),
        ("add member data error", "addMemberDataToPrivateDataControl"),
        ("replace class constant error", "replaceClassConstant"),
        ("replace method error", "replaceActorMethod"),
        ("build do error", "buildDo"),
        ("create send error", "createSendMethod"),
        ("build send error", "buildSend"),
        ("save class error", "saveClass"),
    ];

    /// <summary>
    /// The LabVIEW installation the actor's own ancestry lands in, by walking parent links up to
    /// <c>Actor.lvclass</c>. Null when the chain never reaches it - which is the same thing as
    /// "this is not an actor", so the caller gets one answer for two questions.
    /// </summary>
    private static string? ActorInstallationRoot(string lvclassPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = lvclassPath;

        for (var depth = 0; depth < MaxAncestorWalk; depth++)
        {
            if (!seen.Add(Path.GetFullPath(current))) return null;

            ClassInfoOrNull(current, out var info);
            if (info?.BaseClass?.ResolvedPath is not { Length: > 0 } parent) return null;

            var full = Path.GetFullPath(parent);
            if (full.EndsWith(ActorRelative, StringComparison.OrdinalIgnoreCase))
                return full[..^(ActorRelative.Length + 1)];

            if (!File.Exists(full)) return null;
            current = full;
        }

        return null;
    }

    private static void ClassInfoOrNull(string path, out LvClass.ClassInfo? info)
    {
        try { info = LvClass.Read(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or InvalidOperationException) { info = null; }
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

        if (generation.ErrorCode == 0 && File.Exists(helperVi)) return null;

        return Json.Error("helperGenerationFailed",
            $"Could not generate the helper VI at '{helperVi}': {generation.ErrorMessage}",
            new { helperViPath = helperVi, errorCode = generation.ErrorCode });
    }

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static JsonObject? Values(string answer) =>
        (Read(answer) as JsonObject)?["values"] as JsonObject;

    private static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    private static int? StageCode(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private static string? StageSource(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = Regex.Match(xml, "<Name>source</Name>\\s*<Val>(.*?)</Val>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }
}
