using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a LabVIEW class, and reading one back.
///
/// WHY A TOOL AND NOT A RECIPE. Building three classes by hand on 2026-08-26 took 18 minutes and
/// about twelve round trips, and the sequence is fixed from end to end - AIXML the cluster, extract,
/// patch, rebuild, wrap, write the document, load-check. Nothing in the middle needs a decision, so
/// nothing in the middle needs a turn.
///
/// THE LOAD CHECK IS THE POINT, the same way the pane measurement is the point of
/// <c>lvai_generate_vi</c>. Every step of that sequence answered `ok` for a class LabVIEW then
/// refused: the private data blob's length field sat two bytes late, `pylv_rebuild` reported
/// success, the encoder's own round trip closed, and LabVIEW's answer was three class entries with
/// every field blank plus an error message about invalid *paths* from inside NI's own
/// `Get library info.vi`. The only signal that means anything is a project describe coming back
/// with a non-empty libraryName, so this tool will not report success without it.
///
/// WHAT `lvai_create_class` DELIBERATELY DOES NOT DO: member VIs. AIXML refuses a class-typed
/// terminal by name - `Control with type=UDClassInst is not supported` - so no accessor,
/// constructor, dynamic dispatch method or override can be AUTHORED.
///
/// ACCESSORS ARE NOW REACHABLE ANYWAY, through `lvai_create_accessors`, and the distinction is
/// worth keeping straight: nothing here authors them either. That tool calls LabVIEW's OWN wizard
/// body, `MemberVICreation.lvlib:CLSUIP_CreateNewAccessor.vi`, which is ordinary G code in the
/// LVClass project provider and reads `Protected="0"`. The generated helper only supplies three
/// refnums and reads the results back, so it never touches a class wire - which is what keeps it
/// inside AIXML's type grammar. docs/lvclass-creation.md section 5.1 has the whole route and the
/// four traps in it.
/// </summary>
[McpServerToolType]
internal sealed class ClassTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private const string CreateClassHelperAixmlFileName = "lvai_create_class.xml";

    // ---------------------------------------------------------------- create

    [McpServerTool(Name = "lvai_create_class", Destructive = true, OpenWorld = true,
                   Title = "Create a .lvclass with its private data in one call")]
    [Description("""
        MUTATING: creates a real `.lvclass` on disk, with a private data control carrying the fields
        you name, and optionally lists it in a project. This is LabVIEW class generation - there is
        no RPC for it, so the class file is written here and only the private data CLUSTER comes
        from LabVIEW.
        The sequence, all of it returned under `steps`: author the cluster as AIXML, validate it,
        generate it as a VI, pylv_extract that VI, patch it into a class private data control,
        pylv_rebuild it as a .ctl, wrap it into the class file's flattened property, write the
        document, then LOAD-CHECK it through lvai_describe_project.
        THE LOAD CHECK IS WHY THIS IS ONE CALL. Every earlier step reports success for a class
        LabVIEW then refuses to load, and LabVIEW's complaint names paths rather than the class - so
        `ok` here is false unless a project describe comes back with a non-empty libraryName for the
        new class. Pass verify=false only when LabVIEW is unreachable and you accept an unverified
        file.
        FIELDS are `<type>.<name>`, comma separated, the same spelling AIXML's cluster grammar uses:
        `string.Manufacturer,int32.Year Of Manufacture,double.Top Speed kmh`. Scalars only - string,
        bool, double, single and the int/uint widths. A cluster, array or enum field is not supported
        and is refused by name. Omit `fields` for a class with empty private data.
        INHERITANCE: pass parentClassPath and the parent's qualified name is read off that file and
        written as a `Parent Libraries` item. Note that NOTHING in the gRPC interface confirms a
        parent link resolved - `lvai_describe_project`'s `parent` is the owning library, not the base
        class - so use lvai_describe_class to read back what was written.
        THE PRIVATE DATA CONTROL DOES NOT LOAD, and that is the first thing to know: the class file
        is written and LabVIEW reports it, but the IDE's Error list refuses the control - "Front
        panel control contains a data type with a type definition" - and every accessor built
        against it breaks with it. `ok` is false and `failedAtStep` is `privateData` when so. The
        cause is measured: a control's type space (VCTP/TM80/DFDS) differs from the generated VI's
        and cannot be synthesised from outside LabVIEW yet. USE THIS FOR THE CLASS SHELL and take
        the private data control from the IDE; docs/lvclass-creation.md section 2a has the
        transplant recipe, which is verified, and the full layout for anyone lifting the limit.
        MEMBER VIs ARE OUT OF REACH and this tool does not pretend otherwise: AIXML refuses a
        class-typed terminal (`Control with type=UDClassInst is not supported`), so accessors,
        constructors and dynamic dispatch methods cannot be generated. Use LabVIEW's own
        "VI for Data Member Access" for those. docs/lvclass-creation.md section 3 has the detail.
        Needs a running LabVIEW for the cluster generation and the load check, and the pylabview
        bundle for the rest.
        """)]
    public async Task<string> CreateClassAsync(
        [Description("Class name without the extension, e.g. Auto")] string className,
        [Description("Folder to create <className>.lvclass in; created if absent")]
        string directory,
        [Description("""
            Private data fields as `<type>.<name>`, comma separated, e.g.
            `string.Manufacturer,int32.Year Of Manufacture`. Omit for empty private data.
            """)]
        string? fields = null,
        [Description("Absolute path to the parent .lvclass this one derives from")]
        string? parentClassPath = null,
        [Description("""
            A .lvproj to list the class in - created if absent, extended if it exists. When omitted
            a throwaway project is used for the load check and deleted afterwards.
            """)]
        string? projectPath = null,
        [Description("Load-check the result through lvai_describe_project and gate `ok` on it")]
        bool verify = true,
        [Description("Replace an existing .lvclass. Refused by default - it would drop its members")]
        bool overwrite = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // ---- arguments, all of it before anything is written -------------------------
            if (string.IsNullOrWhiteSpace(className))
                return Json.Error("badArguments", "className is empty.");

            if (className.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                className.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase))
                return Json.Error("badArguments",
                    $"className '{className}' is not a bare class name. Pass 'Auto', not " +
                    "'Auto.lvclass' and not a path.");

            List<LvClass.Field> parsed;
            try { parsed = LvClass.ParseFields(fields); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            // pylabview is NO LONGER a precondition here. It was, while the private data control
            // was built by converting a generated VI; LabVIEW's own provider VIs need none of it.
            if (StatusTools.ScriptsDirectory() is null)
                return Json.Error("noScriptsDirectory",
                    "No scripts folder next to the exe - lvai_status reports it as " +
                    $"scriptsDirectory. {CreateClassHelperAixmlFileName} lives there.");

            if (parentClassPath is { Length: > 0 } && !File.Exists(parentClassPath))
                return Json.Error("badArguments",
                    $"No parent class at '{parentClassPath}'. A parent that cannot be read would " +
                    "be written into the child as an unresolvable link, which LabVIEW reports as a " +
                    "broken class rather than a missing file.");

            var classDirectory = Path.GetFullPath(directory);
            var classPath = Path.Combine(classDirectory, $"{className}.lvclass");

            if (File.Exists(classPath) && !overwrite)
                return Json.Error("alreadyExists",
                    $"'{classPath}' already exists. Creating it again would write a document with " +
                    "no members, dropping every VI the existing class owns. Pass overwrite=true if " +
                    "that is what you want, or use lvai_describe_class to see what is there.");

            // ---- the sequence -------------------------------------------------------------
            //
            // NI'S OWN PROVIDER VIs DO THE WORK, and that is the whole design. A class private
            // data control is compiler output: its type space and data-space offsets describe a
            // control, not the VI an AIXML cluster produces. Converting one into the other by
            // flipping flags gave a class LabVIEW reported normally and refused to compile, and
            // every accessor built against it broke with it. LabVIEW does it correctly in about
            // 350 ms. docs/lvclass-creation.md section 2a has what the old route would have had
            // to synthesise, and why it was abandoned.
            var total = Stopwatch.StartNew();
            var steps = new JsonArray();
            var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "classes",
                $"{className}-{Environment.ProcessId}-{total.GetHashCode():x}");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(classDirectory);

            try
            {
                // 1. the project, which must exist and be ACTIVE before the provider runs: the
                //    helper reaches LabVIEW through Project:Active Project, and finds a parent
                //    class only among the classes that project has open.
                var (projectUsed, scratch, projectStep) =
                    PrepareProject(projectPath, classPath, className, work, parentClassPath);
                steps.Add(projectStep);

                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectUsed, Path.GetFileName(projectUsed),
                    timeoutSeconds, ct);
                steps.Add(Step("openProject", opened));
                if (ErrorCode(opened) is not 0)
                    return Outcome(false, "openProject", steps, total, classPath, null,
                        "The project could not be opened, so no project is active and NI's class " +
                        "provider has nothing to work in. Nothing was written.");

                // 2. the carrier: one front-panel control per field, which is what NI's
                //    add-member-data takes references to. No carrier means no private data.
                var carrierPath = "";
                if (parsed.Count > 0)
                {
                    var carrierAixml = Path.Combine(work, $"{className}-fields.xml");
                    await File.WriteAllTextAsync(
                        carrierAixml, LvClass.CarrierAixml(className, parsed), ct);
                    carrierPath = Path.Combine(work, $"{className}-fields.vi");

                    var carrier = await new BulkTools(connection).GenerateViAsync(
                        carrierAixml, carrierPath, openVI: false, measurePane: false,
                        panePattern: null, timeoutSeconds, ct);
                    steps.Add(Step("carrier", carrier));
                    if (!File.Exists(carrierPath))
                        return Outcome(false, "carrier", steps, total, classPath, null,
                            "The carrier VI could not be generated, so there are no control " +
                            "references to make fields from. LabVIEW's own message is in the step " +
                            "above - a refused field type shows up here, by name.");
                }

                // 3. NI's Add Class + Add Member Data, in one helper run
                var helperRun = await RunCreateClassHelperAsync(
                    classPath, parentClassPath, carrierPath, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "provider", ["answer"] = Parsed(helperRun) });

                var provider = ReadProviderRun(helperRun);
                if (provider.ErrorCode is not 0 || !File.Exists(classPath))
                    return Outcome(false, "provider", steps, total, classPath, null,
                        "NI's class provider did not create the class. Its own error is in the " +
                        "provider step. Error 1055 there means no project was active after all.");

                // A parent that was asked for and not found is SILENT in NI's VI: Search 1D Array
                // answers -1, Index Array yields an invalid refnum, and the class is created with
                // no parent and no error. Measured - so it is checked here rather than trusted.
                if (parentClassPath is { Length: > 0 } && provider.ParentIndex < 0)
                    return Outcome(false, "provider", steps, total, classPath, null,
                        $"THE CLASS WAS CREATED WITHOUT ITS PARENT. '{parentClassPath}' was not " +
                        "among the classes of the open project, so NI's provider silently made a " +
                        "root class. Add the parent to the project first, delete this class, and " +
                        "run again - lvai_describe_class reports what it actually inherits.");

                // 4. the project entry. LabVIEW holds the .lvproj open, so it is closed first -
                //    editing underneath it means the next save writes the old contents back.
                if (!scratch)
                {
                    var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                        helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
                        timeoutSeconds: timeoutSeconds, ct: ct);
                    steps.Add(Step("closeProject", closed));
                }
                steps.Add(AddClassToProject(projectUsed, classPath, className));

                // 10. the load check
                if (!verify)
                    return Outcome(true, null, steps, total, classPath, null,
                        "Written, but NOT load-checked - verify was false. Nothing here says " +
                        "LabVIEW will accept this class; a bad private data blob answers `ok` at " +
                        "every step above and fails only on load.");

                var describe = await new InspectTools(connection).DescribeProjectAsync(
                    projectUsed, projectName: null, maxMessages: 4, timeoutSeconds, ct);
                var verdict = Loaded(describe, classPath);
                steps.Add(new JsonObject
                {
                    ["step"] = "loadCheck",
                    ["projectPath"] = projectUsed,
                    ["projectWasScratch"] = scratch,
                    ["classesReported"] = verdict.ClassesReported,
                    ["loaded"] = verdict.Loaded,
                    ["libraryName"] = verdict.LibraryName,
                    ["privateDataItem"] = verdict.PrivateDataItem,
                    ["answer"] = Parsed(describe),
                });

                if (scratch) TryDelete(projectUsed);

                return verdict.Loaded
                    ? Outcome(true, null, steps, total, classPath, verdict,
                        $"Created and load-checked: LabVIEW reports the class by name with its "
                        + $"private data control, and {provider.FieldsAdded} field(s) went in. "
                        + "The control is LabVIEW's OWN - NI's provider VIs built it - so it "
                        + "carries a real type space and compiles. Inheritance is NOT confirmed "
                        + "by this check; read it back with lvai_describe_class.")
                    : Outcome(false, "loadCheck", steps, total, classPath, verdict,
                        "THE FILE WAS WRITTEN and LabVIEW will not load it. A class reported with " +
                        "a blank libraryName means the private data blob was rejected; LabVIEW's " +
                        "own message names paths and is misleading. The .lvclass is left in place " +
                        "so it can be inspected - delete it before trying again, because LabVIEW " +
                        "now holds this path in memory.");
            }
            finally
            {
                TryDeleteDirectory(work);
            }
        });

    // ---------------------------------------------------------------- describe

    [McpServerTool(Name = "lvai_describe_class", ReadOnly = true, OpenWorld = false,
                   Title = "Read a .lvclass: its ancestry, members and access scope")]
    [Description("""
        Read-only, and needs NO running LabVIEW: a `.lvclass` is plain XML on disk, so this parses
        it directly.
        THE REASON IT EXISTS IS INHERITANCE. `lvai_describe_project` reports a class's `parent` as
        its OWNING LIBRARY, not its base class - measured on NI's own hierarchy, where
        `Circle Message.lvclass` derives from `Draw Message.lvclass` and reports
        `"parent": "Draw Messages.lvlib"`. Nothing in the 23-RPC gRPC interface reports inheritance
        or access scope, so the file is the only source and this is the only reader.
        It checks BOTH representations, in the order the census established: plain-text
        `Parent Libraries` items (LabVIEW 2026 writes these) then the encoded
        `NI.LVClass.ParentClassLinkInfo` for older files. No file carried both; a class with neither
        derives from `LabVIEW Object`, which is reported as such rather than as an empty answer.
        `ancestorSource` says which representation the answer came from, because the encoded one is
        recovered by regex over decoded bytes and its ORDER is not guaranteed.
        Members come back with their effective scope from `NI.ClassItem.MethodScope` - per member,
        no propagation, unlike a `.lvlib` where the scope sits on the folder. Folder names are
        ignored on purpose: the census found folders called `private` whose members were all public.
        `privateDataBytes` is the size of the control inside the class file. `-1` means the
        flattened property is there but does not decode, which is what a corrupt or wrongly wrapped
        blob looks like - and the state in which LabVIEW reports the class with every field blank.
        """)]
    public Task<string> DescribeClassAsync(
        [Description("Absolute path to the .lvclass to read")] string lvclassPath,
        CancellationToken ct = default) =>
        Rpc.GuardAsync(() =>
        {
            if (!File.Exists(lvclassPath))
                return Task.FromResult(Json.Error("badArguments",
                    $"No file at lvclassPath '{lvclassPath}'."));

            LvClass.ClassInfo info;
            try { info = LvClass.Read(lvclassPath); }
            catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
            {
                return Task.FromResult(Json.Error("notAClassFile", e.Message));
            }

            var members = new JsonArray();
            foreach (var member in info.Members)
                members.Add(new JsonObject
                {
                    ["name"] = member.Name,
                    ["url"] = member.Url,
                    ["kind"] = member.Type,
                    ["scope"] = member.Scope,
                    ["dynamicDispatch"] = member.DynamicDispatch,
                });

            var ancestors = new JsonArray();
            foreach (var ancestor in info.Ancestors) ancestors.Add(ancestor);

            return Task.FromResult(new JsonObject
            {
                ["ok"] = true,
                ["classPath"] = info.Path,
                ["className"] = info.ClassName,
                ["qualifiedName"] = info.QualifiedName,
                ["containingLibrary"] = info.ContainingLibrary,
                ["ancestors"] = ancestors,
                ["ancestorSource"] = info.AncestorSource,
                ["inheritsFrom"] = info.Ancestors.Count > 0 ? info.Ancestors[0] : "LabVIEW Object",
                ["privateDataItem"] = info.PrivateDataName,
                ["privateDataBytes"] = info.PrivateDataBytes,
                ["memberCount"] = info.Members.Count,
                ["members"] = members,
                ["note"] = info.PrivateDataBytes switch
                {
                    -1 => "The flattened private data property does not decode. That is what " +
                          "LabVIEW answers with a class reported by a blank libraryName - and its " +
                          "own error message blames paths, so trust this line instead.",
                    0 => "No private data recorded, so the class has no data members.",
                    _ => "Read from the file, not from LabVIEW - so it reflects what is on disk " +
                         "even if the IDE holds a different copy in memory.",
                },
            }.ToJsonString(Indented));
        });

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// The project the load check runs against: the caller's, or a throwaway one. A throwaway is
    /// used rather than skipping the check, because a project describe is the only answer that
    /// means anything - and it is deleted afterwards so nothing is left beside the class.
    /// </summary>
    /// <summary>
    /// The project NI's provider will work in. It is prepared BEFORE the class exists, so the
    /// class itself is deliberately not listed yet - a project naming a file that is not there
    /// sends LabVIEW hunting for it on open, and that is a modal dialog which stops the whole
    /// gRPC service. <see cref="AddClassToProject"/> adds it afterwards.
    ///
    /// A PARENT, though, must be listed and open before the run: NI's VI looks for it among the
    /// active project's classes, and answers -1 rather than an error when it is absent.
    /// </summary>
    private static (string Path, bool Scratch, JsonObject Step) PrepareProject(
        string? projectPath, string classPath, string className, string work,
        string? parentClassPath)
    {
        if (projectPath is not { Length: > 0 })
        {
            var scratchPath = Path.Combine(work, $"{className}-loadcheck.lvproj");
            File.WriteAllText(scratchPath, LvClass.Project(ParentEntry(scratchPath)));
            return (scratchPath, true, new JsonObject
            {
                ["step"] = "project",
                ["action"] = "scratch",
                ["projectPath"] = scratchPath,
                ["note"] = "No projectPath was given, so a throwaway project was written for the " +
                           "run and the load check, and is deleted afterwards. The class itself " +
                           "is not listed in any project you keep.",
            });
        }

        var full = Path.GetFullPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var url = LvClass.RelativeUrl(full, classPath);

        if (!File.Exists(full))
        {
            File.WriteAllText(full, LvClass.Project(ParentEntry(full)));
            return (full, false, new JsonObject
            {
                ["step"] = "project",
                ["action"] = "created",
                ["projectPath"] = full,
                ["url"] = url,
                ["note"] = "The class is added after it exists, not now - a project pointing at a " +
                           "missing file makes LabVIEW open a modal search dialog.",
            });
        }

        // An EXISTING project only needs the parent making sure of; the class comes later.
        if (parentClassPath is { Length: > 0 })
        {
            var parentName = Path.GetFileNameWithoutExtension(parentClassPath);
            try { LvClass.AddToProject(full, parentName, LvClass.RelativeUrl(full, Path.GetFullPath(parentClassPath))); }
            catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
            {
                return (full, false, new JsonObject
                {
                    ["step"] = "project",
                    ["action"] = "failed",
                    ["projectPath"] = full,
                    ["note"] = $"The parent could not be listed in the project: {e.Message}",
                });
            }
        }

        return (full, false, new JsonObject
        {
            ["step"] = "project",
            ["action"] = "prepared",
            ["projectPath"] = full,
            ["url"] = url,
            ["note"] = parentClassPath is { Length: > 0 }
                ? "The parent class was made sure of; the new class is added after it exists."
                : "The new class is added after it exists.",
        });

        (string Name, string Url)[] ParentEntry(string project) =>
            parentClassPath is { Length: > 0 }
                ? [($"{Path.GetFileNameWithoutExtension(parentClassPath)}.lvclass",
                    LvClass.RelativeUrl(project, Path.GetFullPath(parentClassPath)))]
                : [];
    }

    /// <summary>Add the finished class to the project, once the file really is on disk.</summary>
    private static JsonObject AddClassToProject(string projectPath, string classPath, string className)
    {
        try
        {
            var url = LvClass.RelativeUrl(projectPath, classPath);
            var added = LvClass.AddToProject(projectPath, className, url);

            // LabVIEW adopts every VI it has open when it saves the project, so the run's own
            // helper and carrier end up listed in the user's project. Measured on the first live
            // run of this route: three stray VIs, one of them from an earlier session. They are
            // stripped here rather than left for the reader to notice.
            var (tidied, removed) = StripHelperItems(File.ReadAllText(projectPath), projectPath);
            if (removed > 0) File.WriteAllText(projectPath, tidied);

            return new JsonObject
            {
                ["step"] = "projectEntry",
                ["action"] = added ? "added" : "alreadyListed",
                ["projectPath"] = projectPath,
                ["url"] = url,
                ["strayVisRemoved"] = removed,
                ["note"] = "NI's provider writes the class FILE but does not list it - that is what "
                         + "its New Class Owner input would do, and it is left unwired on purpose.",
            };
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        {
            return new JsonObject
            {
                ["step"] = "projectEntry",
                ["action"] = "failed",
                ["projectPath"] = projectPath,
                ["note"] = $"The class was created but could not be listed: {e.Message}",
            };
        }
    }

    private static (int ErrorCode, int ParentIndex, int FieldsAdded) ReadProviderRun(string answer)
    {
        var values = Parsed(answer)?["values"];
        int Read(string name) =>
            int.TryParse(values?[name]?["value"]?.GetValue<string>(), out var v) ? v : -1;

        // The helper's own error cluster travels as flattened XML like every other non-string
        // value, so the code is dug out of it rather than read off a field.
        var xml = values?["error out"]?["xml"]?.GetValue<string>() ?? "";
        var code = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>code</Name>\s*<Val>(-?\d+)");
        return (code.Success ? int.Parse(code.Groups[1].Value) : -1, Read("parent index"),
                Read("fields added"));
    }

    private async Task<string> RunCreateClassHelperAsync(
        string classPath, string? parentClassPath, string carrierPath, int timeoutSeconds,
        CancellationToken ct)
    {
        var aixml = StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, CreateClassHelperAixmlFileName) : null;
        if (aixml is null || !File.Exists(aixml))
            return Json.Error("noHelperAixml",
                $"The helper's AIXML source could not be found ({CreateClassHelperAixmlFileName} " +
                "in the scripts folder next to the exe; lvai_status reports it as " +
                "scriptsDirectory).");

        var helperVi = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers",
                                    "lvai_create_class.vi");
        Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
        if (!File.Exists(helperVi) &&
            await GenerateAccessorHelperAsync(aixml, helperVi, timeoutSeconds, ct) is { } failure)
            return failure;

        var inputs = new JsonObject
        {
            ["class path"] = Path.GetFullPath(classPath),
            ["parent class path"] = parentClassPath is { Length: > 0 }
                ? Path.GetFullPath(parentClassPath) : "",
            ["carrier vi path"] = carrierPath,
        }.ToJsonString();

        return await new RunTools(connection).RunViAndReadValuesAsync(
            helperVi, inputs, includeRawXml: false, helperViPath: null, helperAixmlPath: null,
            regenerateHelper: false, timeoutSeconds, ct);
    }


    /// <summary>
    /// Did LabVIEW actually load the class? A class it refused still appears in the `classes`
    /// array - with every field of `library` blank - so the presence of an entry proves nothing
    /// and only a non-empty libraryName does.
    /// </summary>
    private sealed record LoadVerdict(bool Loaded, int ClassesReported, string? LibraryName,
                                      string? PrivateDataItem);

    private static LoadVerdict Loaded(string describeAnswer, string classPath)
    {
        if (Parsed(describeAnswer) is not JsonObject answer ||
            answer["messages"] is not JsonArray messages)
            return new LoadVerdict(false, 0, null, null);

        var wanted = Path.GetFullPath(classPath);
        var reported = 0;

        foreach (var message in messages)
        {
            if (message?["infoJson"]?.GetValue<string>() is not { Length: > 0 } infoJson) continue;

            JsonNode? info;
            try { info = JsonNode.Parse(infoJson); }
            catch (JsonException) { continue; }

            foreach (var target in info?["targets"] as JsonArray ?? [])
                foreach (var entry in target?["classes"] as JsonArray ?? [])
                {
                    reported++;
                    if (entry?["library"] is not JsonObject library) continue;

                    var path = library["libraryPath"]?.GetValue<string>();
                    var name = library["libraryName"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;
                    if (!string.Equals(Path.GetFullPath(path), wanted, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var privateData = (library["items"] as JsonArray ?? [])
                        .FirstOrDefault(i => i?["type"]?.GetValue<string>() ==
                                             "Class Private Data Control")
                        ?["name"]?.GetValue<string>();

                    return new LoadVerdict(true, reported, name, privateData);
                }
        }

        return new LoadVerdict(false, reported, null, null);
    }

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                  string classPath, LoadVerdict? verdict, string note)
    {
        total.Stop();
        var result = new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["classPath"] = Path.GetFullPath(classPath),
            ["classExistsNow"] = File.Exists(classPath),
            ["classBytes"] = File.Exists(classPath) ? new FileInfo(classPath).Length : 0,
        };
        if (verdict is not null)
        {
            result["loaded"] = verdict.Loaded;
            result["libraryName"] = verdict.LibraryName;
        }
        result["steps"] = steps;
        result["totalElapsedMs"] = total.ElapsedMilliseconds;
        result["note"] = note;
        return result.ToJsonString(Indented);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* debris in temp is not worth failing a created class over */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static JsonObject Step(string name, string answer) => new()
    {
        ["step"] = name,
        ["errorCode"] = ErrorCode(answer),
        ["answer"] = Parsed(answer),
    };

    private static JsonNode? Parsed(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static int? ErrorCode(string answer) =>
        Parsed(answer) is JsonObject o && o.TryGetPropertyValue("errorCode", out var code)
            ? code?.GetValue<int>() : null;

    private static bool Succeeded(string answer) =>
        Parsed(answer) is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    /// <summary>
    /// Did Rpc.GuardAsync turn an exception into data? Those answers carry `ok: false` and no
    /// errorCode, so they are indistinguishable from a refusal unless asked about separately - and
    /// the two need opposite advice.
    /// </summary>
    private static bool Guarded(string answer) =>
        Parsed(answer) is JsonObject o && o.TryGetPropertyValue("ok", out var ok) &&
        ok?.GetValue<bool>() == false;

    private static string? Field(string answer, string key) =>
        Parsed(answer) is JsonObject o ? o[key]?.GetValue<string>() : null;

    /// <summary>Name of the accessor helper's AIXML source inside the scripts folder.</summary>
    internal const string AccessorHelperAixmlFileName = "lvai_create_accessors.xml";

    /// <summary>How `accessUi` spells the wizard's own uint16 enum, in its own order.</summary>
    private static readonly string[] AccessUiNames = ["Read", "Write", "R/W"];

    [McpServerTool(Name = "lvai_create_accessors", Destructive = true, OpenWorld = true,
                   Title = "Create the accessor VIs for a class's private data fields")]
    [Description("""
        MUTATING: creates Read and/or Write accessor VIs for EVERY private data field of a
        .lvclass, saves each one beside the class, and writes the member list into the class file.
        This is LabVIEW's own wizard, not a reimplementation: it calls
        MemberVICreation.lvlib:CLSUIP_CreateNewAccessor.vi, the body behind the IDE's
        "New >> VI for Data Member Access", so the VIs are correct by construction. Verified by a
        pylabview diff against wizard-made accessors for another class - same front-panel heap
        including both udClassDDO objects, same class refnums, same connector pane pattern, same
        .lvclass item block.
        NEITHER AIXML NOR PYLABVIEW CAN DO THIS. An accessor needs a front-panel control whose type
        is the class; AIXML refuses to author one, and the heap object it needs (udClassDDO) is of a
        class pylabview never writes from scratch. docs/lvclass-creation.md has both measurements.
        PRECONDITION: the class must belong to a project that is ACTIVE in the IDE, because the
        class reference is found among Project:Active Project's classes. `classIndex` -1 in the
        answer means the path was not among them - check the project is open and that lvclassPath
        spells the same path LabVIEW reports.
        accessUi = "R/W" creates BOTH accessors per field in one wizard call, which is the default
        and halves the work. dynamicDispatch true gives dynamic dispatch terminals.
        The answer lists every VI created with the path it was saved to, so which field an accessor
        belongs to is reported rather than assumed; fieldIndex is the field's REAL position in the
        cluster, not its position within this call's slice.
        A CLASS WITH MANY FIELDS NEEDS SEVERAL CALLS, and fieldCount DEFAULTS TO 2 for that reason.
        The per-field cost is not constant: Save All This Library checks the whole library every time,
        so it went 11.5 s with an empty class, about 20 s at 6 members and past 30 s at 12 - which
        means three fields fit the FIRST call and two fit any of them, against a client that gives up
        near 60 s. A 7-field class is therefore three calls: fromField 0 with fieldCount 3, then
        3 and 5 with 2. Do not compute the next offset yourself - every answer carries nextFromField,
        read off the class file, and the answer's `fieldCount` is the cluster's real size, so a caller
        can see how many calls are left. The library is saved after EVERY field, so an interrupted
        call leaves a consistent partial class rather than orphan VIs the class file does not mention.
        A TIMED-OUT CALL HAS USUALLY DONE ITS WORK. The client gives up near 60 s while the helper
        keeps going, and because the library is saved after every field the class is left consistent -
        measured repeatedly: 6 VIs and 6 members, 12 and 12, 4 and 4, never a mismatch. So on
        "Request timed out", do not retry the same slice: read the class with lvai_describe_class and
        continue from memberCount / 2. Every successful answer also carries membersBefore,
        membersAfter and nextFromField, all read off the class file rather than from the run.
        VERIFY IMMEDIATELY - lvai_describe_class reads the .lvclass file and needs no LabVIEW, so
        the check survives whatever the IDE does next. This used to read "expect LabVIEW to go down
        a few minutes after the run", and that is no longer what happens: the helper left the
        project reference and the IDE application reference open, and closing both changed the
        outcome. Before: LabVIEW left about three minutes after ONE run. After: three runs back to
        back, twice, with the process still up half an hour later.
        The leak is reduced rather than gone, so do not read this as a clean bill of health. The
        session log still carries `DestroyPlatformEvent failed with MgErr 42`, roughly 13 per run
        against 15 for a single run before. Every faulting site is NI's own code, and the minidump
        count means nothing - a three-node VI with no VI Server in it also produces three.
        RUN IT ONCE PER CLASS ON A CLEAN MEMORY. An earlier failed run leaves accessors in memory
        unsaved, and then the next run names them "Read X 2.vi" and the class file never gets its
        member list. Restart LabVIEW or close the project first.
        """)]
    public async Task<string> CreateAccessorsAsync(
        [Description(@"Absolute path to the .lvclass whose fields need accessors")]
        string lvclassPath,
        [Description("Dynamic dispatch terminals (the wizard's 'Dynamic'). False gives static")]
        bool dynamicDispatch = true,
        [Description("""Which accessors to create: "Read", "Write" or "R/W" (default, both)""")]
        string accessUi = "R/W",
        [Description("Give the accessors error in / error out terminals")]
        bool includeErrorTerminals = true,
        [Description("Expose the accessors through Property Nodes")]
        bool makeAvailableThroughPropertyNodes = false,
        [Description("Virtual folder inside the class to put them in; empty for the class root")]
        string virtualFolderName = "",
        [Description("""
            First private data field to build accessors for, counting from 0. Together with
            fieldCount this bounds one call to a SLICE of the cluster, which is how a class with
            many fields gets built without any single call outliving the MCP client's patience.
            MEASURED: a 7-field class needs about 70 s and the client gives up near 60 s, twice
            leaving 12 of 14 VIs on disk. Two calls of 4 and 3 fields fit with room to spare.
            """)]
        int fromField = 0,
        [Description("""
            How many fields to build from fromField. TWO by default, and the number is measured
            rather than chosen: the per-field library save gets slower as the class grows, because
            Save All This Library checks the whole library every time. On one 7-field class the
            cost per field went 11.5 s with an empty class, about 20 s at 6 members, past 30 s at
            12 - so three fields fit only the FIRST call and two is the honest default. A larger
            number is harmless in itself (Array Subset clamps past the end); it may just not come
            back inside the client's patience.
            """)]
        int fieldCount = 2,
        [Description("""
            Strip helper items out of the .lvproj on disk when the run is done. OFF BY DEFAULT, and
            the reason is the worst regression this tool has had: it rewrites the project FILE while
            LabVIEW still holds that project OPEN, and LabVIEW does not survive it.
            MEASURED as an A/B/A on 2026-08-26, three accessor runs each time from the same reset
            state. The build from before this option existed: three runs ok, 14/10/10 members,
            LabVIEW stable four minutes. The build with it on: three runs FAILED, 0 members, LabVIEW
            gone in twenty seconds. The old build again, immediately after: ok again, stable. Then
            the same new build with tidyProject FALSE: 14 members, LabVIEW alive. So it is this
            option, not the machine, and not the Auto Dispose Ref edit that was reverted first while
            chasing it.
            Turn it on ONLY when LabVIEW does not have the project open - after --finish-project has
            stopped LabVIEW, for instance. It is otherwise safe and idempotent: it edits only items
            whose URL points into the helpers directory.
            """)]
        bool tidyProject = false,
        [Description("""
            Also SAVE and CLOSE the active project first, which flushes the in-memory helper item
            so it can be stripped. MEASURED TO HAVE KILLED LABVIEW when done immediately after an
            accessor run - eight BadLinkerObjs assertions naming the class private data control,
            and the process gone two seconds later. Off by default for that reason; if you want it,
            restart LabVIEW between the run and the close.
            """)]
        bool closeProject = false,
        [Description("""
            The .lvproj to tidy. Omit and the nearest one at or above the class directory is used;
            the answer says which file was chosen and how many items were removed.
            """)]
        string? projectPath = null,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory. Generated
            once and reused; pass regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvai_create_accessors.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 600,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(lvclassPath))
                throw new FileNotFoundException($"No .lvclass at '{lvclassPath}'.", lvclassPath);

            if (fromField < 0 || fieldCount < 1)
                return Json.Error("badArguments",
                    $"fromField must be 0 or more and fieldCount at least 1; got {fromField} " +
                    $"and {fieldCount}.");

            var accessIndex = Array.FindIndex(AccessUiNames,
                n => string.Equals(n, accessUi, StringComparison.OrdinalIgnoreCase));
            if (accessIndex < 0)
                return Json.Error("badArguments",
                    $"accessUi '{accessUi}' is not one of the wizard's three choices.",
                    new { accepted = AccessUiNames });

            // The private data control's name comes from the class file rather than from a
            // convention: it is usually <ClassName>.ctl but the class item is what LabVIEW honours,
            // and the helper opens it at the SYNTHETIC path <class>.lvclass\<that name>, which
            // never exists on disk.
            var info = LvClass.Read(lvclassPath);
            if (info.PrivateDataName is not { Length: > 0 } pdcName)
                return Json.Error("noPrivateDataItem",
                    $"'{lvclassPath}' declares no Class Private Data item, so it has no fields to " +
                    "make accessors for.",
                    new { className = info.ClassName });

            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, AccessorHelperAixmlFileName) : null)
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {AccessorHelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_create_accessors.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateAccessorHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            // Every value crosses as a STRING - that is the runner's contract on the way in - so
            // the flags are digits here and are converted on the helper's diagram.
            var inputObject = new JsonObject
            {
                ["class path"] = Path.GetFullPath(lvclassPath),
                ["pdc name"] = pdcName,
                ["vi type"] = dynamicDispatch ? "0" : "1",
                ["access ui"] = accessIndex.ToString(),
                ["include error terminals"] = includeErrorTerminals ? "1" : "0",
                ["property nodes"] = makeAvailableThroughPropertyNodes ? "1" : "0",
                ["from field"] = fromField.ToString(),
                ["take fields"] = fieldCount.ToString(),
            };

            // Only when it has content: an empty value would shift every later input onto the
            // wrong control, and the helper's own default for this one is already empty.
            if (virtualFolderName.Length > 0) inputObject["virtual folder"] = virtualFolderName;

            var inputs = inputObject.ToJsonString();

            // Counted BEFORE and AFTER off the class file, because that is the only record that
            // survives a client timeout: the work completes and is saved per field, but the answer
            // is lost, so a caller needs to be able to see how far it got and resume.
            var membersBefore = LvClass.Read(lvclassPath).Members.Count;

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs, includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct);

            var verdict = DescribeAccessorRun(answer, lvclassPath, pdcName, helperVi, aixml, fromField,
                membersBefore,
                helperGenerated);

            // Only tidy a run that produced something. A failed run has nothing to hand back and
            // closing the project underneath the caller would only make the next attempt harder.
            if (!tidyProject || !Succeeded(verdict)) return verdict;

            return await TidyProjectAsync(verdict, lvclassPath, helperVi, projectPath,
                closeProject, timeoutSeconds, ct);
        });

    /// <summary>
    /// Take the helper back out of the project. LabVIEW adds it as a real top-level item while the
    /// run is in the project's application instance, and the in-memory item is invisible on disk
    /// until something saves - at which point it is written with a relative URL climbing out to
    /// %TEMP%, measured as
    /// <c>URL="../../../../Users/…/Temp/LabVIEWMCP/helpers/lvai_create_accessors.vi"</c>.
    ///
    /// So the order matters and is not the obvious one: SAVE and close first, deliberately
    /// flushing the item to disk, and only then strip it. Stripping before the save would edit a
    /// file that does not carry the item yet, and LabVIEW would write it on the next save anyway.
    /// </summary>
    private async Task<string> TidyProjectAsync(
        string verdict, string lvclassPath, string helperVi, string? projectPath,
        bool closeProject, int timeoutSeconds, CancellationToken ct)
    {
        // NOT BY DEFAULT, and the reason is measured. Saving and closing the project immediately
        // after an accessor run KILLED LabVIEW on 2026-08-26: eight
        // `BadLinkerObjs.cpp(276) … LinkIdentity "Bus.lvclass:Bus.ctl" … is NOT a bad subObj`
        // assertions, all executing lvai_close_active_project.vi, at 14:53:13.44 - and the process
        // was gone at 14:53:15. The three accessor runs before it had survived ten minutes. Each
        // operation is fine on its own; back to back they are not.
        var close = closeProject
            ? await new CloseTools(connection).CloseActiveProjectAsync(
                helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds,
                ct)
            : null;

        // RELEASING THE HELPER FROM MEMORY IS NOT REACHABLE, and the attempt was removed rather
        // than left in to fail every run. lvai_run_and_read.vi opens the helper by path and runs it
        // through a VI reference, so the helper never gets a front-panel WINDOW; lvai_close_vi
        // works by writing that window's State, and answers Error 1149 because there is none.
        // Measured 2026-08-26 on a helper the project HAD adopted, so membership was not the
        // problem. The catalogue has no unload method either, so what is left is the on-disk strip
        // below plus closing the project discarding changes - or restarting LabVIEW.
        var project = projectPath ?? FindProjectNear(lvclassPath);
        var removed = 0;
        string? tidyError = null;

        if (project is { Length: > 0 } && File.Exists(project))
        {
            try
            {
                var (stripped, count) = StripHelperItems(File.ReadAllText(project), project);
                removed = count;
                if (removed > 0) File.WriteAllText(project, stripped);
            }
            catch (Exception failure) { tidyError = failure.Message; }
        }
        else tidyError = $"No .lvproj found at or above '{Path.GetDirectoryName(lvclassPath)}'.";

        if (Parsed(verdict) is not JsonObject root) return verdict;
        root["projectTidied"] = tidyError is null;
        root["projectPath"] = project;
        root["helperItemsRemoved"] = removed;
        root["projectClosed"] = close is not null && Parsed(close) is JsonObject c &&
                                c["closed"]?.GetValue<bool>() == true;
        root["tidyError"] = tidyError;
        root["tidyNote"] = removed > 0
            ? $"Stripped {removed} helper item(s) from the .lvproj on disk."
            : "Nothing to strip: the .lvproj on disk carries no helper item. LabVIEW does add the " +
              "helper as an IN-MEMORY project item, and the Project Explorer shows it - but it " +
              "only reaches the file when something SAVES the project. Close the project in the " +
              "IDE discarding changes, or let the next run of this tool strip it. Do NOT use " +
              "closeProject to force the save: that combination killed LabVIEW when measured.";
        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// Remove the project items that should never have been there, and say how many.
    ///
    /// TWO KINDS, and the second was found the hard way. The first is a VI under the helpers
    /// directory - anchored on the PATH rather than the name, because a user VI called
    /// <c>lvai_something.vi</c> beside the project must survive, and the only reliable
    /// discriminator is that our helpers live under <c>LabVIEWMCP/helpers</c>. LabVIEW writes
    /// project URLs with forward slashes even on Windows, which is what makes that match work.
    ///
    /// The second is any item, of ANY type, whose file is not there. LabVIEW adds every VI that
    /// <c>ConvertAIXMLToVI</c> generates while a project is active to that project's tree - which
    /// includes <c>lvai_create_class</c>'s scratch <c>&lt;Class&gt;-privatedata.vi</c>, generated
    /// under %TEMP% and deleted straight after. Measured 2026-08-27, where a run that created 40
    /// classes left the Project Explorer showing forty
    /// <c>Load&lt;n&gt;-privatedata.vi [Warning: has been deleted, renamed...]</c> rows on top of
    /// forty <c>Load&lt;n&gt;.lvclass</c> items whose directories had been removed. Nothing here
    /// listed those VIs; LabVIEW did.
    ///
    /// A URL resolves against the FILE that carries it treated as a directory, so the leading
    /// <c>..</c> pops the .lvproj's own name rather than a directory - the rule from
    /// lvproj-structure.md section 5, and getting it wrong here would delete live items.
    /// </summary>
    internal static (string Text, int Removed) StripHelperItems(
        string projectXml, string? projectPath = null)
    {
        // BOTH of our temp trees, not just helpers/: a class run's carrier VI lives under
        // classes/<work>/ and LabVIEW adopts it exactly the same way. Caught here as well as by
        // the dangling pass below, because the work directory is deleted only after this runs -
        // measured, a Reihenhaus-fields.vi that was still on disk survived a tidy that looked at
        // nothing but helpers/.
        const string helperItem =
            "<Item Name=\"[^\"]*\\.vi\" Type=\"VI\" URL=\"[^\"]*LabVIEWMCP/(?:helpers|classes)/[^\"]*\"\\s*/>";

        var removed = System.Text.RegularExpressions.Regex.Matches(projectXml, helperItem).Count;
        var text = removed == 0
            ? projectXml
            : System.Text.RegularExpressions.Regex.Replace(
                projectXml, "\\r?\\n\\s*" + helperItem, "");

        if (projectPath is not { Length: > 0 }) return (text, removed);

        // Now the dangling ones. Only a SELF-CLOSING item carries a URL and no children, which is
        // every item a generator writes; a container with nested items is left alone.
        var dangling = 0;
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "\\r?\\n[ \\t]*<Item Name=\"(?<name>[^\"]*)\" Type=\"(?<type>[^\"]*)\" " +
            "URL=\"(?<url>[^\"]*)\"\\s*/>",
            match =>
            {
                var url = match.Groups["url"].Value.Replace('\\', '/');
                string resolved;
                try { resolved = Path.GetFullPath(Path.Combine(projectPath, url)); }
                catch (Exception failure)
                    when (failure is ArgumentException or PathTooLongException)
                {
                    return match.Value;
                }

                if (File.Exists(resolved) || System.IO.Directory.Exists(resolved))
                    return match.Value;

                dangling++;
                return "";
            });

        return (text, removed + dangling);
    }

    /// <summary>The nearest .lvproj at or above a class file, or null when there is no single one.</summary>
    private static string? FindProjectNear(string lvclassPath)
    {
        var directory = new FileInfo(Path.GetFullPath(lvclassPath)).Directory;
        for (var i = 0; i < 4 && directory is not null; i++, directory = directory.Parent)
            if (directory.GetFiles("*.lvproj") is { Length: 1 } single) return single[0].FullName;

        return null;
    }

    /// <summary>
    /// Turn the runner's indicator map into the accessor verdict. `ok` is false whenever the
    /// helper's own error cluster is set OR the class was not found, because a run that created
    /// nothing still reports errorCode 0 from the runner itself.
    /// </summary>
    internal static string DescribeAccessorRun(
        string runnerAnswer, string lvclassPath, string pdcName, string helperVi, string aixml,
        int fromField, int membersBefore,
        bool helperGenerated)
    {
        if (Parsed(runnerAnswer) is not JsonObject root)
            return Json.Error("unreadableRunnerAnswer",
                "The runner's answer could not be parsed as JSON.", new { runnerAnswer });

        var values = root["values"] as JsonObject;
        var classIndex = int.TryParse(Value(values, "class index"), out var ci) ? ci : -1;
        var fieldCount = int.TryParse(Value(values, "field count"), out var fc) ? fc : 0;
        var status = Value(values, "status") is "1" or "true";
        var code = int.TryParse(Value(values, "code"), out var c) ? c : 0;
        var source = Value(values, "source") ?? "";

        var created = new JsonArray();
        var readNames = Flattened(values, "read vi names");
        var readPaths = Flattened(values, "read saved paths");
        var writeNames = Flattened(values, "write vi names");
        var writePaths = Flattened(values, "write saved paths");
        for (var i = 0; i < Math.Max(readNames.Count, writeNames.Count); i++)
            created.Add(new JsonObject
            {
                ["fieldIndex"] = fromField + i,
                ["readVi"] = i < readNames.Count ? readNames[i] : null,
                ["readPath"] = i < readPaths.Count ? readPaths[i] : null,
                ["writeVi"] = i < writeNames.Count ? writeNames[i] : null,
                ["writePath"] = i < writePaths.Count ? writePaths[i] : null,
            });

        var ok = !status && classIndex >= 0 && created.Count > 0;
        return Json.Object(new
        {
            ok,
            classPath = Path.GetFullPath(lvclassPath),
            privateDataItem = pdcName,
            classIndex,
            fieldCount,
            accessorsCreated = created.Count,
            created,
            // Only when the lookup failed, and then it is the whole diagnosis: a -1 is either an
            // empty list (no project, or none loaded yet) or a path that differs from LabVIEW's
            // spelling, and those need opposite fixes.
            classPathsSeen = classIndex >= 0 ? null
                : new JsonArray([.. Flattened(values, "class paths").Select(p => (JsonNode)p!)]),
            // Straight off the class file, so these two agree with reality even when the run's own
            // arrays came back short. accessorsCreated says what the ANSWER carried; these say what
            // the CLASS actually holds.
            membersBefore,
            membersAfter = MembersOnDisk(lvclassPath),
            nextFromField = MembersOnDisk(lvclassPath) / 2,
            errorCode = code,
            errorSource = source.Length == 0 ? null : source,
            helperViPath = helperVi,
            helperAixmlPath = Path.GetFullPath(aixml),
            helperGenerated,
            hint = AccessorHint(classIndex, code, source, created.Count,
                Flattened(values, "class paths").Count),
            note = created.Count == 0 ? null :
                "Verify with lvai_describe_class: the member list is written into the .lvclass by " +
                "Save All This Library, which runs once per field - so membersAfter above is the " +
                "class file's own count, not a prediction, and nextFromField is where to resume.",
        });
    }

    /// <summary>
    /// The failures worth naming, each measured while this route was being found. Everything else is
    /// left to the error source, which names the node.
    ///
    /// The split on <paramref name="classPathsSeen"/> came last and is the one that saves the most
    /// time: a -1 with an EMPTY class list and a -1 with a populated one need opposite fixes, and
    /// telling someone whose project is simply not active to "check the path" sends them hunting a
    /// spelling that was correct all along.
    /// </summary>
    private static string? AccessorHint(
        int classIndex, int code, string source, int created, int classPathsSeen) =>
        classIndex < 0 && classPathsSeen == 0
            // Measured 2026-08-27 and then CORRECTED the same day. First reading: "a timed-out call
            // de-activates the project, because the helper's closes never run." Round 7 timed out too
            // and the next call went straight through, so that mechanism is wrong. What differed is
            // that round 6's project had never been properly opened - only loaded by a READ tool,
            // which reads a project without leaving one active. Hence the wording below blames the
            // opening, not the timeout, and offers the same cheap fix either way.
            ? "No class came back at all, so no project is ACTIVE - this is not a path problem, and " +
              "checking the spelling will waste your time. The measured cause is a project that was " +
              "never properly opened: a read tool such as lvai_describe_project loads a project " +
              "without leaving one active. Open it with lvai_open_file passing projectPath AND " +
              "projectName - not viPath, which silently takes a .lvproj as a VI - then resume from " +
              "the nextFromField in this answer. Any work an earlier timed-out slice did is already " +
              "saved, so do not repeat that slice."
        : classIndex < 0
            ? "classIndex -1 with classPathsSeen populated IS a path mismatch: compare lvclassPath " +
              "against the spellings listed there. The match is exact and case-sensitive, and " +
              @"LabVIEW has been observed reporting C:\Temp for one class and C:\temp for its " +
              "siblings in the same project."
        : code == 43
            // Measured twice: 43 comes from Save All This Library reaching a member LabVIEW cannot
            // place. The cause is almost never this run - it is an EARLIER failed run whose VIs are
            // still in memory unsaved and unnamed, which is also why fresh accessors come out with
            // a " 2" suffix. A restart of LabVIEW, or closing the project, clears it.
            ? "Error 43 is 'operation cancelled': LabVIEW wanted to prompt for a save path and " +
              "could not. The usual cause is an EARLIER failed run whose accessor VIs are still in " +
              "memory with no path - Save All This Library saves every member and trips over them. " +
              "A ' 2' suffix on a created VI name is the same symptom. Close the project (or " +
              "restart LabVIEW) so nothing unsaved is left, delete the half-made VIs, and run once."
        : code == 1055
            // Not "no active project", which is what this said first: 1055 is the GENERIC invalid
            // object reference, and the measured cause here was an Index Array fed -1 from a failed
            // class lookup - the reference was never obtained, so everything downstream reports it.
            ? "Error 1055 is an invalid object reference. It is a downstream symptom whenever the " +
              "class lookup failed, so read classIndex and classPathsSeen first; only if those are " +
              "sound is the reference itself the problem."
        : created == 0 && code == 0
            ? "No accessor came back and no error was reported, which usually means the private " +
              "data cluster has no fields. Check lvai_describe_class privateDataBytes."
            : null;

    /// <summary>
    /// One string-array indicator as a flat list. The runner returns compound values as flattened
    /// XML, so the elements are read out of that rather than from a `value` field, which is empty
    /// for anything that is not a scalar.
    /// </summary>
    private static List<string> Flattened(JsonObject? values, string name)
    {
        var result = new List<string>();
        if (values?[name] is not JsonObject entry) return result;
        if (entry["xml"]?.GetValue<string>() is not { Length: > 0 } xml) return result;

        // <Array><Name>…</Name><Dimsize>n</Dimsize><String><Name/><Val>…</Val></String>…
        //
        // DIMSIZE DECIDES, not the number of <Val> elements. LabVIEW flattens an EMPTY array with
        // one element still present, as the type prototype - so counting <Val>s reports one
        // accessor created where none was. Measured on the first real run of this tool: fieldCount
        // 0 and accessorsCreated 1, every field of it blank. The hand-written test XML had a
        // matching Dimsize and could not catch it.
        var declared = System.Text.RegularExpressions.Regex.Match(xml, @"<Dimsize>(\d+)</Dimsize>");
        var limit = declared.Success && int.TryParse(declared.Groups[1].Value, out var n)
            ? n : int.MaxValue;
        if (limit == 0) return result;

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(
                     xml, @"<Val>(?<v>.*?)</Val>",
                     System.Text.RegularExpressions.RegexOptions.Singleline).Cast<System.Text.RegularExpressions.Match>())
        {
            if (result.Count >= limit) break;
            result.Add(System.Net.WebUtility.HtmlDecode(match.Groups["v"].Value));
        }

        return result;
    }

    /// <summary>
    /// How many member VIs the class file lists right now. Read from disk on purpose: it is the one
    /// number that is true whether or not the run's answer arrived.
    /// </summary>
    private static int MembersOnDisk(string lvclassPath)
    {
        try { return LvClass.Read(lvclassPath).Members.Count; }
        catch (Exception failure) when (failure is IOException or InvalidDataException) { return -1; }
    }

    /// <summary>One indicator's plain value out of the runner's `values` map, or null.</summary>
    private static string? Value(JsonObject? values, string name) =>
        values?[name] is JsonObject entry ? entry["value"]?.GetValue<string>() : null;

    /// <summary>Validate then generate the accessor helper. Null on success, else an error payload.</summary>
    private async Task<string?> GenerateAccessorHelperAsync(
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
}
