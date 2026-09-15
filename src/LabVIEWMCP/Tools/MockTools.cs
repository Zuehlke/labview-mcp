using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Generating an Astemes LMock mock class, without the IDE gesture LMock's own documentation
/// describes.
///
/// WHY THIS IS A TOOL. LMock documents mock creation as a right-click on an interface in the
/// Project Explorer, which is unreachable from here. But
/// <c>LMock Mock Class Generator.lvclass:LMock Generate Mock Class.vi</c> is an ordinary public
/// member under <c>vi.lib</c> taking PATHS ONLY - source, destination, and an
/// <c>Add to lvproj?</c> flag - and an AIXML <c>Call</c> to it validates with errorCode 0. So a
/// generated helper drives it directly. Measured 2026-09-14: 31 s cold, 2.9 s warm, and the mock
/// it produces differs from the one NI ships with the LMock example only in uid numbering.
///
/// THE WHOLE POINT OF THE TOOL IS THE PRE-FLIGHT, NOT THE CALL. Every LMock failure measured so
/// far arrives as a MODAL DIALOG, and a modal dialog stops the entire gRPC service until a human
/// clicks Continue - which is the one failure an unattended run cannot absorb. Two were hit in a
/// single evaluation session, both needing the user to intervene:
///
///   Error 1055  no active project, raised because `Add to lvproj?` DEFAULTS TO TRUE when left
///               unwired. This tool wires the flag explicitly, always.
///   Error 1704  "Reference refers to a library that is not an interface" - the source was an
///               ordinary .lvclass.
///
/// Both are knowable before LabVIEW is touched: <c>NI.LVClass.IsInterface</c> is a property in the
/// .lvclass file, which is plain XML. So <see cref="Precheck"/> refuses locally and LMock is only
/// ever called in a state it accepts.
///
/// AND `Created Files` IS NOT A RECORD OF WHAT EXISTS. The 1704 run returned two paths in that
/// array and wrote NEITHER - the destination directory was empty. So the array is what LMock set
/// out to write, not what survived, and this tool reports the two separately rather than inferring
/// success from a non-empty list. docs/labview-lmock-mocking.md has every measurement.
/// </summary>
[McpServerToolType]
internal sealed class MockTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lmock_generate_mock.xml";

    [McpServerTool(Name = "lvai_generate_mock_class", Destructive = true, OpenWorld = true,
                   Title = "Generate an LMock mock class for an interface")]
    [Description("""
        MUTATING: creates an Astemes LMock mock class on disk for an INTERFACE - the .lvclass, a
        Create.vi constructor, one override per dynamic dispatch member, and one `When <Method>.vi`
        per member. LMock's own generator does the work, so the result is what its right-click
        entry produces; measured against the mock NI ships with the LMock example, the two differ
        only in uid numbering.
        THE SOURCE MUST BE AN INTERFACE. An ordinary .lvclass is refused HERE, before LabVIEW is
        touched, because LMock answers Error 1704 with a MODAL DIALOG that stops this whole gRPC
        service until someone clicks Continue. NI.LVClass.IsInterface is read straight out of the
        file - plain XML, no LabVIEW, no cost. If the dependency is not an interface, mocking is
        not available until one is extracted, which is a design change to the code under test.
        TO LIST THE MOCK IN A PROJECT, PASS `projectPath` - NOT `addToProject`. The entry is then
        written into the .lvproj as a FILE edit with LabVIEW uninvolved, which is the same route
        lvai_create_class uses and the only one measured to survive. The ACTIVE PROJECT IS CLOSED
        FIRST and has to be: the two requirements are mutually exclusive, because LMock's terminal
        needs the project OPEN and an edit that survives needs it CLOSED.
        `addToProject` IS FALSE BY DEFAULT, and since 2026-09-15 that is not only about the modal
        dialog - IT DOES NOT SURVIVE. Measured: a mock generated with addToProject:true against an
        open, active project wrote all four files and appeared in the .lvproj NOT AT ALL, because
        the next lvai_close_active_project saves LabVIEW's in-memory copy over the file and the
        mock is not in that copy. The same save also deleted an entry for an earlier mock that had
        been added to the file BY HAND, which is what makes the mechanism unambiguous. The answer
        reported ok:true and four filesOnDisk throughout, so nothing anywhere said the project
        entry had been lost.
        The old reason still holds too: LMock's own terminal defaults to TRUE when left unwired,
        and with no active project that is Error 1055 - as a modal dialog that stops this service.
        READ `ok` AND `filesOnDisk`, NOT `filesCreated`. LMock reports the files it set out to
        write; a failed run has been measured returning two paths and writing neither. `ok` is true
        only when the error cluster is clean AND the .lvclass is really there.
        Mock generation is only the first step of a mock-based test. The mock's own VIs are
        project-local class code, so a generated test reaches them through lvai_placeholder_subvi
        plus lvai_swap_subvis, exactly as any other class code. docs/labview-lmock-mocking.md has
        the whole route and every target spelling.
        """)]
    public async Task<string> GenerateMockClassAsync(
        [Description("""
            Absolute path to the .lvclass to mock. MUST be an interface - one whose
            NI.LVClass.IsInterface property is true. An ordinary class is refused here rather than
            by LMock, whose refusal is a modal dialog.
            """)]
        string interfacePath,
        [Description("""
            Absolute path of the mock .lvclass to create. Its directory is created if absent -
            LMock does not create one, and its failure to do so is not reported usefully.
            """)]
        string destinationPath,
        [Description("""
            Ask LMock to list the new class in the ACTIVE project. FALSE by default. LMock's own
            terminal defaults to TRUE when unwired, which is Error 1055 plus a modal dialog when no
            project is active - that is the trap this default exists to close. Pass true only when
            a project is open and active.
            """)]
        bool addToProject = false,
        [Description("""
            The .lvproj to LIST the finished mock in, written as a FILE edit with LabVIEW
            uninvolved - the same route lvai_create_class uses, and the only one measured to
            survive. Use this INSTEAD of addToProject.
            THE ACTIVE PROJECT IS CLOSED FIRST, because it has to be: LabVIEW's save-on-close
            writes its in-memory copy over the file, so an entry written while it holds the
            project is deleted moments later.
            """)]
        string? projectPath = null,
        [Description("Replace an existing destination .lvclass. Refused by default.")]
        bool overwrite = false,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory, because
            the scripts folder next to the exe may be read-only. Generated once and reused.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lmock_generate_mock.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds - LMock costs about 31 s on a cold session")]
        int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (Precheck(interfacePath, destinationPath, overwrite) is { } refusal) return refusal;

            var source = Path.GetFullPath(interfacePath);
            var destination = Path.GetFullPath(destinationPath);

            // LMock does not create the destination directory, and what it does instead is not
            // worth finding out the hard way - every one of its failures is a modal dialog.
            if (Path.GetDirectoryName(destination) is { Length: > 0 } target)
                Directory.CreateDirectory(target);

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

            var helperGenerated = false;
            if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            // Paths cross as STRINGS and are converted on the helper's diagram: a VI Server
            // control value will not coerce a string into a path, and fails before the VI runs.
            var inputs = new JsonObject
            {
                ["Source Class Path"] = source,
                ["Destination Class Path"] = destination,
            }.ToJsonString();

            var wall = System.Diagnostics.Stopwatch.StartNew();
            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs, includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct: ct);

            // THE PROJECT ENTRY IS OURS TO WRITE, exactly as in lvai_create_class - LMock's own
            // addToProject terminal was MEASURED not to survive. Probe, 2026-09-15: a mock
            // generated with addToProject:true against an open, active project wrote all four
            // files, and the very next lvai_close_active_project left NO entry in the .lvproj -
            // it also deleted an entry for an earlier mock that had been put there by hand. The
            // close SAVES LabVIEW's in-memory project over the file, and the mock is not in that
            // copy, so anything the file said about it is gone.
            //
            // That is why the project is CLOSED before the edit rather than after it. The two
            // requirements are mutually exclusive: LMock's terminal needs the project OPEN, and
            // an edit that survives needs it CLOSED.
            JsonNode? projectEntry = null;
            if (projectPath is { Length: > 0 })
                projectEntry = await ListInProjectAsync(projectPath, destination, timeoutSeconds, ct);
            else if (addToProject)
                // SAY IT IN THE ANSWER, not only in the description. `addToProject: true` answers
                // ok:true with four filesOnDisk and leaves NO entry in the .lvproj, so every figure
                // a caller reads says the job is done - and the description is read at the moment
                // someone is already confused, which is after the entry has gone. Reported rather
                // than refused: what was measured is that the entry does not survive a close, not
                // that LMock's terminal does nothing, and a caller who never closes the project is
                // outside what this run covered.
                projectEntry = new JsonObject
                {
                    ["step"] = "projectEntry",
                    ["action"] = "notWritten",
                    ["warning"] = "addToProject was true and projectPath was not given, so NOTHING "
                                + "HERE LISTED THE MOCK in a .lvproj that survives. Measured "
                                + "2026-09-15: a mock generated this way against an open, active "
                                + "project is absent from the file after the next close, because "
                                + "that close saves LabVIEW's in-memory copy over it and the mock "
                                + "is not in that copy - the same save also deleted an entry added "
                                + "by hand. Pass `projectPath` instead; it writes the entry as a "
                                + "file edit after closing the project, which is the only route "
                                + "measured to survive.",
                };

            return Describe(answer, source, destination, addToProject, helperVi, aixml,
                            helperGenerated, wall.ElapsedMilliseconds, projectEntry);
        });

    // ------------------------------------------------------------------ the project entry

    /// <summary>
    /// List the finished mock in a .lvproj, by editing the FILE with LabVIEW uninvolved.
    ///
    /// MEASURED 2026-09-15, which is why this exists at all. A mock generated with
    /// <c>addToProject: true</c> against an open, ACTIVE project wrote all four of its files and
    /// then did not appear in the .lvproj at all: the next <c>lvai_close_active_project</c> SAVED
    /// LabVIEW's in-memory copy over the file, and that copy has no mock in it. The same save also
    /// deleted an entry for an EARLIER mock that had been written into the file by hand, which is
    /// what makes the mechanism unambiguous - it is the save clobbering the file, not anything
    /// removing the entry.
    ///
    /// SO THE PROJECT IS CLOSED FIRST, and the two routes are mutually exclusive rather than
    /// complementary: LMock's own terminal needs the project OPEN, and an edit that survives needs
    /// it CLOSED. A close that fails is reported and the edit is NOT attempted, because writing
    /// into a file LabVIEW still holds is the failure this whole step exists to avoid.
    /// </summary>
    private async Task<JsonNode> ListInProjectAsync(
        string projectPath, string destinationPath, int timeoutSeconds, CancellationToken ct)
    {
        var full = Path.GetFullPath(projectPath);
        if (!File.Exists(full))
            return new JsonObject
            {
                ["step"] = "projectEntry",
                ["action"] = "refused",
                ["projectPath"] = full,
                ["why"] = "No .lvproj at that path. The mock's FILES are written either way - only "
                        + "the project entry was skipped.",
            };

        var closed = await new CloseTools(connection).CloseActiveProjectAsync(
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
            timeoutSeconds: timeoutSeconds, ct: ct);

        // Error 1055 means nothing was active, which is the state this is trying to reach.
        var closeOk = closed.Contains("\"nothingToClose\": true", StringComparison.Ordinal)
                      || closed.Contains("\"closed\": true", StringComparison.Ordinal);
        if (!closeOk)
            return new JsonObject
            {
                ["step"] = "projectEntry",
                ["action"] = "refused",
                ["projectPath"] = full,
                ["why"] = "The active project could not be closed, and an entry written while "
                        + "LabVIEW holds the project is deleted by its own save-on-close. Nothing "
                        + "was written. Close it yourself and add the entry, or re-run this.",
                ["closeAnswer"] = JsonNode.Parse(closed),
            };

        var className = ClassNameFor(destinationPath);
        var url = LvClass.RelativeUrl(full, destinationPath);
        bool added;
        try { added = LvClass.AddToProject(full, className, url); }
        catch (Exception bad) when (bad is IOException or UnauthorizedAccessException
                                           or System.Xml.XmlException)
        {
            return new JsonObject
            {
                ["step"] = "projectEntry",
                ["action"] = "failed",
                ["projectPath"] = full,
                ["why"] = $"The .lvproj could not be rewritten: {bad.Message}",
            };
        }

        return new JsonObject
        {
            ["step"] = "projectEntry",
            ["action"] = added ? "added" : "alreadyListed",
            ["projectPath"] = full,
            ["url"] = url,
            ["projectClosedFirst"] = true,
            ["note"] = "Written as a file edit with LabVIEW uninvolved, after closing the active "
                     + "project - an entry written while LabVIEW holds it is deleted by its own "
                     + "save-on-close, measured 2026-09-15 on exactly this tool.",
        };
    }

    /// <summary>
    /// The class NAME to hand <c>LvClass.AddToProject</c>, which is the file name WITHOUT its
    /// extension.
    ///
    /// That method appends <c>.lvclass</c> itself, because <c>lvai_create_class</c> hands it a bare
    /// class name - so passing <c>Path.GetFileName</c> writes
    /// <c>Name="Mock ISampleSink.lvclass.lvclass"</c>. It did, on the first real call of the
    /// <c>projectPath</c> parameter, while the tool's own answer said <c>action: "added"</c>. The
    /// answer was true and useless; only reading the project file showed it.
    /// </summary>
    internal static string ClassNameFor(string destinationPath) =>
        Path.GetFileNameWithoutExtension(destinationPath);

    // ------------------------------------------------------------------ the pre-flight

    /// <summary>
    /// Everything that can be decided WITHOUT LabVIEW, returned as a ready-made refusal or null.
    ///
    /// This is the tool's reason to exist. Each of these reaches LMock as a modal dialog that
    /// stops the gRPC service, so "let LMock report it" is not an option: by the time the error
    /// cluster comes back a human has already had to click Continue.
    /// </summary>
    internal static string? Precheck(string interfacePath, string destinationPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(interfacePath))
            return Json.Error("interfacePathMissing", "No interface path was given.");
        if (string.IsNullOrWhiteSpace(destinationPath))
            return Json.Error("destinationPathMissing", "No destination path was given.");

        var source = Path.GetFullPath(interfacePath);
        var destination = Path.GetFullPath(destinationPath);

        if (!HasLvclassExtension(source))
            return Json.Error("interfaceNotAnLvclass",
                $"'{source}' is not a .lvclass. LMock mocks an interface, which is a .lvclass " +
                "whose NI.LVClass.IsInterface is true - there is no .lvinterface file type.",
                new { interfacePath = source });

        if (!File.Exists(source))
            return Json.Error("interfaceNotFound", $"No .lvclass at '{source}'.",
                new { interfacePath = source });

        if (!HasLvclassExtension(destination))
            return Json.Error("destinationNotAnLvclass",
                $"'{destination}' is not a .lvclass. The destination names the mock CLASS file to " +
                "create, e.g. ...\\Mock Serial\\Mock Serial.lvclass.",
                new { destinationPath = destination });

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return Json.Error("destinationIsTheSource",
                "The destination is the interface itself. The mock is a NEW class that implements " +
                "the interface; it cannot overwrite it.",
                new { interfacePath = source });

        LvClass.ClassInfo info;
        try
        {
            info = LvClass.Read(source);
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or IOException)
        {
            return Json.Error("interfaceUnreadable",
                $"'{source}' could not be read as a .lvclass: {error.Message}",
                new { interfacePath = source });
        }

        // THE GUARD THIS TOOL EXISTS FOR. Measured 2026-09-14 on Simulated Serial.lvclass, an
        // ordinary class that IMPLEMENTS the interface being mocked: LMock answered Error 1704,
        // "Reference refers to a library that is not an interface", as a modal dialog - and still
        // reported two Created Files, neither of which it had written.
        if (!info.IsInterface)
            return Json.Error("sourceIsNotAnInterface",
                $"'{info.ClassName}.lvclass' is an ordinary class, not an interface, and LMock " +
                "mocks interfaces only. Calling it anyway answers Error 1704 AND raises a modal " +
                "dialog that stops this gRPC service until someone dismisses it, which is why " +
                "this is refused here instead.",
                new
                {
                    interfacePath = source,
                    isInterface = false,
                    ancestors = info.Ancestors,
                    hint = "Mocking needs an interface to stand in for. Extract one from this " +
                           "class and have the class implement it - that is a design change to " +
                           "the code under test, so it is the user's call, not a step this tool " +
                           "can take. lvai_create_interface creates one; a class can only be " +
                           "LINKED to an interface at creation time, so the implementing class " +
                           "has to be recreated with parentInterfaces.",
                });

        if (File.Exists(destination) && !overwrite)
            return Json.Error("destinationExists",
                $"'{destination}' already exists. Pass overwrite to replace it - which drops the " +
                "mock's current members, including any hand-edits to its When VIs.",
                new { destinationPath = destination });

        return null;
    }

    /// <summary>
    /// A .lvclass by extension. Case-insensitive because a path reaches this from a user, a
    /// project file and a tool answer alike, and only one of the three is consistent.
    /// </summary>
    private static bool HasLvclassExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".lvclass", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ reading the result

    /// <summary>
    /// The paths inside the helper's <c>Created Files</c> array, out of LabVIEW's flattened XML.
    ///
    /// <c>Dimsize</c> IS HONOURED RATHER THAN IGNORED, for the reason <see cref="LvValuesXml"/>
    /// records: an EMPTY LabVIEW array still serialises one child element as a type template, so
    /// counting elements would report a phantom file for a run that created none.
    /// </summary>
    internal static IReadOnlyList<string> CreatedFiles(string? arrayXml)
    {
        if (string.IsNullOrWhiteSpace(arrayXml)) return [];

        XElement root;
        try { root = XElement.Parse(arrayXml); }
        catch (XmlException) { return []; }

        var paths = root.Elements("Path")
            .Select(p => p.Element("Val")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        if (int.TryParse((string?)root.Element("Dimsize"), out var declared)
            && declared >= 0 && declared < paths.Count)
            paths = paths.Take(declared).ToList();

        return paths;
    }

    /// <summary>
    /// Turn the runner's payload into this tool's answer. Separated from the RPC work so the
    /// verdict - and above all the created-versus-on-disk split - is unit-testable without
    /// LabVIEW.
    /// </summary>
    internal static string Describe(
        string runnerAnswer, string interfacePath, string destinationPath, bool addToProject,
        string helperVi, string aixml, bool helperGenerated, long? elapsedMs = null,
        JsonNode? projectEntry = null)
    {
        if (Verdict(runnerAnswer) is not { } verdict) return runnerAnswer;
        var (status, code, source, createdXml) = verdict;

        // The HELPER's error cluster decides, not the runner's errorCode: that one is 0 whenever
        // the target merely ran, and its own note says so.
        var raised = status is not null && status != "0";
        var created = CreatedFiles(createdXml);
        var onDisk = created.Where(File.Exists).ToList();
        var missing = created.Where(p => !File.Exists(p)).ToList();

        // `ok` NEEDS THE FILES, NOT THE LIST. Measured: a refused run returned two Created Files
        // and wrote neither, so a non-empty list is not evidence of anything. An incomplete mock
        // is not a success either - a mock missing one override is a class that will not compile
        // at the first call site, so `missing` being empty is part of the verdict rather than a
        // remark beside it.
        var classWritten = File.Exists(destinationPath);
        var ok = !raised && status is not null && classWritten && missing.Count == 0;

        var result = new JsonObject
        {
            ["ok"] = ok,
            ["interfacePath"] = interfacePath,
            ["destinationPath"] = destinationPath,
            ["mockClassWritten"] = classWritten,
            ["addToProject"] = addToProject,
            ["projectEntry"] = projectEntry,
            ["filesCreated"] = new JsonArray([.. created.Select(p => (JsonNode)p!)]),
            ["filesOnDisk"] = new JsonArray([.. onDisk.Select(p => (JsonNode)p!)]),
            ["helperViPath"] = helperVi,
            ["helperAixmlPath"] = Path.GetFullPath(aixml),
            ["helperGenerated"] = helperGenerated,
            ["errorCode"] = int.TryParse(code, out var parsed) ? parsed : 0,
            ["errorSource"] = source,
            ["elapsedMs"] = elapsedMs,
        };

        // Only when the two disagree, so a clean run does not carry a field that is always empty.
        if (missing.Count > 0)
            result["createdButMissing"] = new JsonArray([.. missing.Select(p => (JsonNode)p!)]);

        if (Hint(code, addToProject) is { } hint) result["hint"] = hint;

        result["note"] = ok
            ? "The mock class and its members were written. The mock's own VIs are project-local " +
              "class code, so a generated test reaches them through lvai_placeholder_subvi plus " +
              "lvai_swap_subvis - see docs/labview-lmock-mocking.md."
            : status is null
                ? "The helper returned no error cluster, so nothing can be concluded. Check that " +
                  "the helper VI generated correctly."
                : missing.Count > 0
                    ? "LMock named files it did not write. `filesCreated` is what it set out to " +
                      "produce and `filesOnDisk` is what survived - read the second one. Anything " +
                      "left behind should be cleaned up before retrying."
                    : "LMock raised an error, so the mock is incomplete or absent.";

        return Json.Document(result);
    }

    /// <summary>
    /// What a failing run most likely means. Both codes were measured in one session and both
    /// arrived as modal dialogs - so reaching this hint at all means a human was already
    /// interrupted, and the advice is about never getting here again.
    /// </summary>
    internal static string? Hint(string? code, bool addToProject) => code switch
    {
        "1704" =>
            "Error 1704 is LMock refusing a source that is not an interface. This tool checks " +
            "NI.LVClass.IsInterface before calling, so reaching this means the file changed under " +
            "it, or the check was bypassed.",
        "1055" when addToProject =>
            "Error 1055 is 'Project:Active Project' finding no ACTIVE project, reached because " +
            "addToProject was true. Open the .lvproj and make it active - lvai_open_file reports " +
            "projectBecameActive, and No Error alone does NOT mean a project became active - or " +
            "pass addToProject false and list the class in the project yourself.",
        "1055" =>
            "Error 1055 means LMock went looking for an active project even though addToProject " +
            "was false. That should not happen: the helper wires LMock's `Add to lvproj?` " +
            "terminal explicitly, and the whole point of doing so is that its unwired default is " +
            "TRUE. Check that the helper AIXML still carries that constant.",
        _ => null,
    };

    /// <summary>
    /// The helper's error cluster and its Created Files array, out of the runner's payload. Null
    /// when the payload is not a runner answer at all - a guard failure, or something unparsable -
    /// which the caller passes through untouched rather than dressing up as a run that happened.
    /// </summary>
    private static (string? Status, string? Code, string Source, string? CreatedXml)?
        Verdict(string runnerAnswer)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(runnerAnswer); }
        catch (JsonException) { return null; }

        if (root is not JsonObject payload ||
            (payload.TryGetPropertyValue("ok", out var ok) && ok?.GetValue<bool>() == false))
            return null;

        var values = payload["values"] as JsonObject;
        var error = values?["error out"] as JsonObject;

        return (
            Field(error?["xml"]?.GetValue<string>(), "status"),
            Field(error?["xml"]?.GetValue<string>(), "code"),
            Field(error?["xml"]?.GetValue<string>(), "source") ?? "",
            (values?["Created Files"] as JsonObject)?["xml"]?.GetValue<string>());
    }

    /// <summary>
    /// One named field out of a flattened error cluster. The runner returns a cluster with
    /// <c>value: null</c> and the whole thing in <c>xml</c>, so there is nothing scalar to read -
    /// unlike the single-indicator helpers, whose fields arrive as separate controls.
    /// </summary>
    internal static string? Field(string? clusterXml, string name)
    {
        if (string.IsNullOrWhiteSpace(clusterXml)) return null;

        XElement root;
        try { root = XElement.Parse(clusterXml); }
        catch (XmlException) { return null; }

        return root.Elements()
            .FirstOrDefault(e => (string?)e.Element("Name") == name)
            ?.Element("Val")?.Value;
    }

    // ------------------------------------------------------------------ helper plumbing

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload carrying the two failures that have their own advice.
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
                new
                {
                    aiXmlPath = Path.GetFullPath(aixml),
                    errorCode = validation.ErrorCode,
                    hint = "An 'Unsupported SubVI' naming LMock means LMock is not installed on " +
                           "this station. Its API lives in vi.lib\\Astemes\\LMock.",
                });

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
                         $"'{Path.GetDirectoryName(helperVi)}'. Pass helperViPath somewhere else.",
                    _ => null,
                },
            });
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP for the reason IconTools measured: LabVIEW's Save:Instrument fails with Error 7
    /// when saving a generated VI under %LOCALAPPDATA%, while %TEMP% accepts it.
    /// </summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lmock_generate_mock.vi");
}
