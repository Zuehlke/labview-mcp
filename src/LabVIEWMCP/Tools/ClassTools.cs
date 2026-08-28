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
        you name, and lists it in a project. LabVIEW's OWN project provider VIs do the work -
        `Add Class.lvlib:Add Class to Project (path).vi` and `Message Maker.lvlib:Add Member Data to
        Private Data Control.vi` - which is the whole design, because a private data control is
        COMPILER OUTPUT. Building one from a converted VI gave classes LabVIEW reported normally and
        its compiler refused, for weeks. docs/lvclass-creation.md section 2a has that diagnosis.
        The sequence, all of it returned under `steps`: prepare the .lvproj, open it, generate a
        CARRIER VI whose front-panel controls are the fields, run NI's two providers against it,
        close the project, write the class entry into the .lvproj, then verify FROM THE CLASS FILE.
        A RUNNING, ACTIVE PROJECT IS THE PRECONDITION. The providers reach LabVIEW through
        `Project:Active Project` and answer Error 1055 without one.
        BACK-TO-BACK CALLS ON ONE PROJECT WORK, and no LabVIEW restart is needed between them. That
        was not true until 2026-08-28: the helper leaked the Class refnum NI's provider returns,
        which kept the new class in LabVIEW's memory after the project closed, so the next run could
        not bind the project item to it and created the child as a ROOT class with no error. Closing
        that one reference fixed it. THE PARENT NO LONGER COMES FROM THE PROJECT AT ALL: the helper
        opens it from its path with {LV.Application} LVClass.Open, which needs no project membership
        - probed against a project listing no classes. That removes the whole chain that made this
        fragile (parent must be a project member -> the .lvproj must list it -> the file must be
        written -> the project must be closed to allow writing it). `parent opened` replaces the old
        `parent index`: a boolean, false when a parent was asked for and did not open.
        FIELDS are `<type>.<name>`, comma separated, the same spelling AIXML's cluster grammar uses:
        `string.Manufacturer,int32.Year Of Manufacture,double.Top Speed kmh`. Scalars only - string,
        bool, double, single, timestamp and the int/uint widths. A cluster, array or enum field is
        not supported and is refused by name. Omit `fields` for a class with empty private data.
        INHERITANCE: pass parentClassPath. The parent must already be LISTED IN THE PROJECT - NI's
        provider finds it by searching the active project's classes and, finding nothing, silently
        makes a ROOT class with no error at all. That is checked here rather than trusted, and the
        answer says which of the two causes it was.
        ACCESSORS ARE A SEPARATE CALL: `lvai_create_accessors`, which drives the IDE's own
        "VI for Data Member Access" wizard. AIXML cannot author them - it refuses a class-typed
        terminal (`Control with type=UDClassInst is not supported`).
        Needs a running LabVIEW throughout. No pylabview bundle is involved any more.
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
        [Description("""
            Read the finished .lvclass back and gate `ok` on it: field count, private data size and
            the parent link. FROM THE FILE, not through lvai_describe_project - that check was here
            and reported `ok: false` for a perfectly sound class, because LabVIEW still had the
            project loaded from step 1 and served its cached copy rather than the .lvproj just
            written. Measured 2026-08-28 on a cold run.
            """)]
        bool verify = true,
        [Description("Replace an existing .lvclass. Refused by default - it would drop its members")]
        bool overwrite = false,
        [Description("""
            Milliseconds to wait after the .lvproj has been written and before LabVIEW opens it.
            A MITIGATION FOR A SUSPECTED RACE, not a diagnosis: the file is written and LabVIEW
            reads it milliseconds later, and this run has repeatedly seen LabVIEW open a project
            without a class the file plainly listed. Whether timing causes that is unproven - the
            pause is a parameter so it can be set to 0 and measured against.
            """)]
        int settleMs = 400,
        [Description("""
            Keep the generated carrier VI on disk instead of deleting it with the rest of the work
            directory. TRUE by default, and it is not tidiness that is being traded away: LabVIEW
            adopts the carrier into the project, and deleting it leaves LabVIEW holding a project
            whose items no longer exist. That broken copy survives the close, overwrites the
            .lvproj with itself, and stops `Project:Active Project` answering - which the accessor
            phase reports as Error 1055. The carrier's project ENTRY is stripped from the file
            either way; only a 6 kB VI in %TEMP% is left behind.
            """)]
        bool keepCarrier = true,
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
                // 1. THE PROJECT LABVIEW WORKS IN IS ALWAYS A THROWAWAY, and the user's .lvproj
                //    is never opened at all. NI's providers need SOME active project - they reach
                //    LabVIEW through Project:Active Project - but nothing says it has to be the
                //    one the class belongs to, now that the parent is opened from its path rather
                //    than searched for among the project's classes.
                //
                //    That single change removes a whole family of failures, every one of them
                //    measured on 2026-08-28 and none of them ever explained:
                //      - LabVIEW ADOPTS the carrier VI into whatever project is open. Into a
                //        throwaway, that costs nothing; into the user's, it left an item pointing
                //        at a deleted temp file, visible in the IDE as
                //        `Haus-fields.vi [Warning: has been deleted, renamed or moved]`.
                //      - LabVIEW SAVES that project on close, over the file. When its copy lacked
                //        a class the file listed, the save deleted that entry - a two-class run
                //        produced a .lvproj listing only the second class.
                //      - LabVIEW KEEPS that broken project in memory across the close, which is
                //        what made `Project:Active Project` answer nothing and the accessor phase
                //        report Error 1055.
                //    None of it can happen to a project LabVIEW never sees.
                var userProject = EnsureUserProject(projectPath, classPath, parentClassPath);
                var (projectUsed, _, projectStep) =
                    PrepareProject(null, classPath, className, work, parentClassPath);
                projectStep["userProject"] = userProject;
                projectStep["note"] =
                    "LabVIEW works in a throwaway project so it never opens, adopts into, or saves "
                    + "over the project you keep. The class entry is written into that one "
                    + "afterwards, by this tool, with LabVIEW not involved.";
                steps.Add(projectStep);

                // WHAT THE FILE LISTS BEFORE LABVIEW EVER OPENS IT, remembered so step 4 can put it
                // back. The close in step 4 saves LabVIEW's own copy of the project, and when that
                // copy is missing a class the file had, the save DELETES that entry - measured
                // 2026-08-28 on a two-class run whose .lvproj came out listing only the second
                // class, the first one silently gone. The user caught it; `projectEntry` reported
                // `added` quite happily, because it only ever checked its own entry.
                var listedBefore = userProject is { Length: > 0 }
                    ? ListedClasses(userProject) : [];

                // LET THE WRITE SETTLE BEFORE LABVIEW READS IT. PrepareProject has just written
                // the .lvproj, and LabVIEW opens it in the very next call - on a fast machine that
                // is milliseconds apart. Whether LabVIEW ever reads a half-written or stale-cached
                // file is NOT established; this is a mitigation for a suspected race, not a
                // diagnosis, and it is a parameter so it can be turned off and measured against.
                // What IS established is that LabVIEW sometimes opens a project whose class the
                // file plainly lists - the failure this would explain if the race is real.
                if (settleMs > 0)
                {
                    await Task.Delay(settleMs, ct);
                    steps.Add(new JsonObject
                    {
                        ["step"] = "settle",
                        ["milliseconds"] = settleMs,
                        ["note"] = "Paused after writing the .lvproj and before LabVIEW opened it. "
                                 + "Set settleMs=0 to remove the pause.",
                    });
                }

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
                // ALWAYS generated, even for a class with no fields at all: the helper opens the
                // carrier unconditionally, and an empty path is not something it can open. A
                // fieldless carrier simply has no controls, so the array of references is empty
                // and NI's add-member-data has nothing to add.
                var carrierAixml = Path.Combine(work, $"{className}-fields.xml");
                await File.WriteAllTextAsync(
                    carrierAixml, LvClass.CarrierAixml(className, parsed), ct);
                var carrierPath = Path.Combine(work, $"{className}-fields.vi");

                var carrier = await new BulkTools(connection).GenerateViAsync(
                    carrierAixml, carrierPath, openVI: false, measurePane: false,
                    panePattern: null, timeoutSeconds, ct);
                steps.Add(Step("carrier", carrier));
                if (!File.Exists(carrierPath))
                    return Outcome(false, "carrier", steps, total, classPath, null,
                        "The carrier VI could not be generated, so there are no control " +
                        "references to make fields from. LabVIEW's own message is in the step " +
                        "above - a refused field type shows up here, by name.");

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
                //
                // WHICH ADVICE TO GIVE depends on the .lvproj ON DISK, and getting that wrong
                // costs a whole debugging round. "Add the parent to the project" is the right
                // answer only when the parent really is absent from the file. When the file DOES
                // list it, the parent is missing from LabVIEW's copy alone - see ProjectListsClass.
                if (parentClassPath is { Length: > 0 } && !provider.ParentOpened)
                    return Outcome(false, "provider", steps, total, classPath, null,
                        ProjectListsClass(projectUsed, parentClassPath)
                            ? $"THE CLASS WAS CREATED WITHOUT ITS PARENT. '{parentClassPath}' "
                              + "could not be OPENED - the helper reaches it with LVClass.Open from "
                              + "its path, so project membership is not the issue and neither is "
                              + "LabVIEW's copy of the project. Check the file itself: readable, "
                              + "not locked, a real .lvclass. Delete this class and run again."
                            : $"THE CLASS WAS CREATED WITHOUT ITS PARENT. '{parentClassPath}' "
                              + "could not be opened, and '{projectUsed}' does not list it either. "
                              + "The path is the thing to check now - the parent no longer has to "
                              + "be a project member. Delete this class and run again; "
                              + "lvai_describe_class reports what it actually inherits.");

                // 4. close the THROWAWAY, then write the entry into the user's project.
                //
                //    The close is now hygiene rather than a precondition: it releases the scratch
                //    project so LabVIEW does not carry it into the next call. Whatever LabVIEW
                //    saves into that file is irrelevant - it is deleted with the work directory.
                //
                //    And the entry goes into a file LabVIEW has never opened, so there is nothing
                //    to race with and nothing to overwrite it. The wait-for-release step that used
                //    to guard this is gone with the reason for it.
                var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                    helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
                    timeoutSeconds: timeoutSeconds, ct: ct);
                steps.Add(Step("closeScratchProject", closed));

                steps.Add(userProject is { Length: > 0 }
                    ? AddClassToProject(userProject, classPath, className, listedBefore)
                    : new JsonObject
                    {
                        ["step"] = "projectEntry",
                        ["action"] = "noProject",
                        ["note"] = "No projectPath was given, so the class belongs to no project. "
                                 + "The .lvclass itself is complete.",
                    });

                // 5. the check that means something: the CLASS FILE.
                //
                // NOT a project describe, which is what this used to do and which answered
                // `classesReported: 0` for a class that was perfectly sound - LabVIEW had the
                // project loaded from step 1 and served its cached copy rather than the .lvproj
                // just written. Measured 2026-08-28 on a cold run. The describe was a weak check
                // in the other direction too: it reported `errorCode 0` for the old route's
                // classes, whose private data did not compile.
                //
                // The file answers what was actually asked for - the fields landed, the parent
                // was recorded - and it needs no LabVIEW, so nothing can serve it stale.
                if (!verify)
                    return Outcome(true, null, steps, total, classPath, null,
                        "Written, but NOT verified - verify was false. The provider reported no "
                        + "error, which is not the same as the class file carrying what you asked "
                        + "for; lvai_describe_class reads it back.");

                var info = LvClass.Read(classPath);
                // ClassInfo carries the ANCESTRY, not a single parent: a root class
                // lists only itself, so the first entry that is not this class is the
                // base. The describe tool derives its `inheritsFrom` the same way.
                var inherits = info.Ancestors.FirstOrDefault(
                    a => !string.Equals(a, info.QualifiedName, StringComparison.OrdinalIgnoreCase));
                var wantedParent = parentClassPath is { Length: > 0 }
                    ? Path.GetFileNameWithoutExtension(parentClassPath) + ".lvclass" : null;
                steps.Add(new JsonObject
                {
                    ["step"] = "verify",
                    ["privateDataBytes"] = info.PrivateDataBytes,
                    ["inheritsFrom"] = inherits,
                    ["fieldsAsked"] = parsed.Count,
                    ["fieldsAdded"] = provider.FieldsAdded,
                });

                TryDelete(projectUsed);   // the throwaway; the work directory follows in `finally`

                if (provider.FieldsAdded != parsed.Count)
                    return Outcome(false, "verify", steps, total, classPath, null,
                        $"THE CLASS WAS CREATED but {provider.FieldsAdded} of {parsed.Count} "
                        + "field(s) went in. The carrier VI's controls are what become fields, so "
                        + "a field missing here means a control that was not on its front panel.");

                if (info.PrivateDataBytes <= 0)
                    return Outcome(false, "verify", steps, total, classPath, null,
                        "THE CLASS WAS CREATED and its private data does not decode - "
                        + $"privateDataBytes {info.PrivateDataBytes}. That is what a corrupt or "
                        + "wrongly wrapped blob looks like from the file.");

                if (wantedParent is not null &&
                    !string.Equals(inherits, wantedParent, StringComparison.OrdinalIgnoreCase))
                    return Outcome(false, "verify", steps, total, classPath, null,
                        $"THE CLASS WAS CREATED but inherits from '{inherits}', not "
                        + $"'{wantedParent}'. NI's provider is silent about a parent it could not "
                        + "find - it makes a root class instead.");

                return Outcome(true, null, steps, total, classPath, null,
                    $"Created and verified from the class file: {provider.FieldsAdded} field(s), "
                    + $"{info.PrivateDataBytes} bytes of private data, inherits from "
                    + $"'{inherits}'. The private data control is LabVIEW's own - NI's "
                    + "provider VIs built it - so it carries a real type space and compiles.");
            }
            finally
            {
                // THE CARRIER FILE STAYS. LabVIEW adopts it into the project while the provider
                // runs, and deleting it leaves that project holding an item that no longer exists -
                // seen in the IDE as `Haus-fields.vi [Warning: has been deleted, renamed or moved]`,
                // with no classes beside it. That broken project is what LabVIEW keeps in memory,
                // saves over the .lvproj on the next close (deleting the class entries), and what
                // makes `Project:Active Project` answer nothing, which the accessor phase reports
                // as Error 1055.
                //
                // The item is stripped from the .lvproj FILE either way, so nothing dangles for the
                // user; what is left behind is a 6 kB VI in %TEMP%\LabVIEWMCP\classes\, which the
                // next `--finish-project` or a reboot clears. Litter is the cheaper failure.
                if (keepCarrier is false) TryDeleteDirectory(work);
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
                // SKIP THE CLASS ITSELF. `NI.LVClass.Geneology` lists the class among its own
                // ancestors when there is no parent, so taking Ancestors[0] blindly reported a root
                // class as inheriting from ITSELF - `Haus.lvclass` inheriting from `Haus.lvclass`,
                // caught 2026-08-28 by two independent runs of the class agent. `ancestorSource`
                // did flag the uncertainty, so it was never silently wrong, but a caller reading
                // this field alone would draw a false conclusion. lvai_create_class's own verify
                // step already filtered it out; the two now agree.
                ["inheritsFrom"] = info.Ancestors.FirstOrDefault(
                                       a => !string.Equals(a, info.QualifiedName,
                                                           StringComparison.OrdinalIgnoreCase))
                                   ?? "LabVIEW Object",
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
    /// <summary>
    /// Make sure the project the user asked for EXISTS, and return its full path - without letting
    /// LabVIEW anywhere near it. Creating it is all that happens here; the class entry is written
    /// after the provider run, and the parent needs no entry at all now that it is opened from its
    /// path rather than searched for among an open project's classes.
    /// </summary>
    private static string? EnsureUserProject(string? projectPath, string classPath,
                                             string? parentClassPath)
    {
        if (projectPath is not { Length: > 0 }) return null;

        var full = Path.GetFullPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // An EMPTY project, not one listing the class: LabVIEW opens a modal search dialog for a
        // project item whose file is not there yet, and a modal dialog stops the gRPC service.
        if (!File.Exists(full)) File.WriteAllText(full, LvClass.Project([]));

        // The parent is listed for the READER's benefit - a hierarchy the project does not show is
        // confusing - not because anything in the run needs it there.
        if (parentClassPath is { Length: > 0 } && File.Exists(parentClassPath))
        {
            try
            {
                LvClass.AddToProject(full, Path.GetFileNameWithoutExtension(parentClassPath),
                                     LvClass.RelativeUrl(full, Path.GetFullPath(parentClassPath)));
            }
            catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
            {
                // A project we cannot parse is reported by the entry step later, not here.
            }
        }

        return full;
    }

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
    /// <summary>
    /// Block until LabVIEW has finished with the .lvproj, so our own write cannot be overtaken by
    /// its save.
    ///
    /// `lvai_close_active_project` returning is NOT that guarantee: it runs Save and then Close on
    /// the project, and both are LabVIEW-side operations whose file I/O can still be in flight when
    /// the RPC answers. Write the class entry into that window and LabVIEW's save lands on top of
    /// it - which is exactly how a two-class run produced a .lvproj listing only the second class,
    /// measured 2026-08-28.
    ///
    /// Two conditions, both needed. EXCLUSIVE OPEN says no one else holds a handle right now.
    /// UNCHANGED SIZE AND TIMESTAMP across consecutive polls says the writing has stopped - on its
    /// own, an exclusive open can succeed in the gap between two of LabVIEW's own writes.
    /// </summary>
    private static async Task<JsonObject> WaitForProjectFileAsync(
        string projectPath, int timeoutSeconds, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        // A SHORT BUDGET ON PURPOSE, and not the caller's timeout. Releasing a closed file is a
        // millisecond operation; anything longer means LabVIEW is wedged, and waiting the caller's
        // full 180 s on a wedge turns a recoverable failure into a client timeout that reports
        // nothing at all. Measured the moment this was first wired to timeoutSeconds: LabVIEW hung,
        // the wait sat on the lock, and the whole call died without a single step in the answer.
        var budget = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 5));
        (long Length, DateTime Written)? previous = null;
        var polls = 0;

        while (watch.Elapsed < budget)
        {
            polls++;
            ct.ThrowIfCancellationRequested();

            if (Released(projectPath) is { } sample)
            {
                if (previous == sample)
                    return new JsonObject
                    {
                        ["step"] = "projectFileReleased",
                        ["waitedMs"] = (int)watch.ElapsedMilliseconds,
                        ["polls"] = polls,
                        ["note"] = "LabVIEW has closed the .lvproj and stopped writing to it, so "
                                 + "the class entry can be written without being overwritten by a "
                                 + "save still in flight.",
                    };
                previous = sample;
            }
            else
            {
                previous = null;   // still locked: the stability run starts over
            }

            await Task.Delay(120, ct);
        }

        return new JsonObject
        {
            ["step"] = "projectFileReleased",
            ["waitedMs"] = (int)watch.ElapsedMilliseconds,
            ["polls"] = polls,
            ["timedOut"] = true,
            ["note"] = "The .lvproj never went quiet within the budget - it stayed locked, or "
                     + "something kept writing to it. The class entry is written anyway, because "
                     + "not writing it is the worse failure, but it may be overwritten. Check the "
                     + "file, and `classEntriesRestored` on the next run.",
        };

        // Null while anyone else holds the file; otherwise its size and last-write time.
        static (long Length, DateTime Written)? Released(string path)
        {
            try
            {
                using var exclusive = new FileStream(
                    path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var info = new FileInfo(path);
                return (info.Length, info.LastWriteTimeUtc);
            }
            catch (IOException) { return null; }               // locked by LabVIEW
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Every class the .lvproj lists, as (name without extension, URL). Read before
    /// LabVIEW opens the project so the entries can be put back after it closes it.</summary>
    internal static List<(string Name, string Url)> ListedClasses(string projectPath)
    {
        try
        {
            if (!File.Exists(projectPath)) return [];

            // Parsed, not pattern-matched. A regex over the raw text has to guess at whitespace and
            // attribute order, and the first version of this silently found nothing after
            // LvClass.AddToProject had rewritten the file. XDocument is how AddToProject reads it,
            // so the two agree by construction.
            return System.Xml.Linq.XDocument.Load(projectPath).Descendants("Item")
                .Where(i => (string?)i.Attribute("Type") == "LVClass")
                .Select(i => (Name: (string?)i.Attribute("Name") ?? "",
                              Url: (string?)i.Attribute("URL") ?? ""))
                .Where(c => c.Name.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase)
                            && c.Url.Length > 0)
                .Select(c => (c.Name[..^".lvclass".Length], c.Url))
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (System.Xml.XmlException) { return []; }
    }

    internal static JsonObject AddClassToProject(string projectPath, string classPath,
                                                string className,
                                                IReadOnlyList<(string Name, string Url)> listedBefore)
    {
        try
        {
            // RESTORE FIRST, then add. LabVIEW's close saves its own copy of the project over the
            // file, and a class it never had in memory is deleted by that save - so the entries
            // this run started with are re-asserted before the new one goes in. AddToProject is
            // idempotent, so an entry that survived costs nothing.
            var restored = listedBefore
                .Count(c => !string.Equals(c.Name, className, StringComparison.OrdinalIgnoreCase)
                            && LvClass.AddToProject(projectPath, c.Name, c.Url));

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
                ["classEntriesRestored"] = restored,
                ["note"] = "NI's provider writes the class FILE but does not list it - that is what "
                         + "its New Class Owner input would do, and it is left unwired on purpose. "
                         + "`classEntriesRestored` counts entries LabVIEW's own save had deleted "
                         + "from the .lvproj and this step put back; anything above 0 means the "
                         + "close clobbered the file, which is a known and unexplained behaviour.",
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

    /// <summary>Does the .lvproj ON DISK list this class? Kept for the failure message only.
    /// Project membership stopped being a PRECONDITION when the parent search became
    /// <c>LVClass.Open</c> on the parent's path, but knowing whether the project lists it still
    /// tells the reader which kind of mistake they are looking at.</summary>
    private static bool ProjectListsClass(string projectPath, string classPath)
    {
        try
        {
            // The URL is written relative and its spelling varies with where the class sits, so
            // the file NAME is what is matched - two classes of the same name in one project is
            // not a case LabVIEW allows anyway.
            var name = Path.GetFileName(classPath);
            return File.Exists(projectPath)
                && File.ReadAllText(projectPath)
                       .Contains(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static (int ErrorCode, bool ParentOpened, int FieldsAdded) ReadProviderRun(string answer)
    {
        var values = Parsed(answer)?["values"];
        int Read(string name) =>
            int.TryParse(values?[name]?["value"]?.GetValue<string>(), out var v) ? v : -1;

        // The helper's own error cluster travels as flattened XML like every other non-string
        // value, so the code is dug out of it rather than read off a field.
        var xml = values?["error out"]?["xml"]?.GetValue<string>() ?? "";
        var code = System.Text.RegularExpressions.Regex.Match(xml, @"<Name>code</Name>\s*<Val>(-?\d+)");
        // `parent opened` is a BOOLEAN now, and it travels as flattened XML rather than as a
        // plain value - the helper opens the parent from its path with LVClass.Open and tests the
        // refnum, instead of searching the active project for it and reporting an index.
        var openedXml = values?["parent opened"]?["xml"]?.GetValue<string>() ?? "";
        var opened = System.Text.RegularExpressions.Regex.IsMatch(
            openedXml, @"<Name>parent opened</Name>\s*<Val>1");

        return (code.Success ? int.Parse(code.Groups[1].Value) : -1, opened,
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

        // ONLY THE INPUTS THAT HAVE A VALUE. The runner pairs names and values by POSITION and
        // refuses an empty one outright, because an empty value does not survive its split and
        // would shift every later input onto the wrong control. A control that is not set keeps
        // its own default, which is the empty string either of these wants.
        //
        // Found by a cold run: every earlier test had both a parent and fields, so this path -
        // a root class - was never taken. The same trap is recorded on the accessor helper.
        var inputObject = new JsonObject { ["class path"] = Path.GetFullPath(classPath) };
        if (parentClassPath is { Length: > 0 })
            inputObject["parent class path"] = Path.GetFullPath(parentClassPath);
        if (carrierPath.Length > 0)
            inputObject["carrier vi path"] = carrierPath;

        var inputs = inputObject.ToJsonString();

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
