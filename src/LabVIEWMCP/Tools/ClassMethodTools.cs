using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Turning a generated VI into a real class METHOD - class-typed pane, dynamic dispatch, membership.
///
/// WHY THIS IS A TOOL. Measured 2026-09-02 building four DAQmx methods on a HAL child class: the
/// retype, the membership and the wire-rule passes cost about <b>105 s of wall clock for 3.3 s
/// inside LabVIEW</b>, and a misdiagnosis around them cost 70 s more for ~1 s of work. The
/// sequence never varies. That is the signature <c>CLAUDE.md</c> names for a step that should be a
/// tool - cheap for LabVIEW, expensive in turns.
///
/// WHAT MAKES A CLASS METHOD AWKWARD. AIXML cannot express a class-typed terminal at all
/// (<c>Control with type=UDClassInst is not supported</c>), so a method VI is authored with
/// <c>path</c> stand-ins and repaired afterwards - and the repair has four traps, each of which
/// looks like success when you get it wrong:
///
/// 1. <b>Convert WITHOUT validating.</b> <c>ValidateAIXML</c> type-checks subVI wiring and refuses
///    a class wire fed from a <c>path</c> stand-in; <c>ConvertAIXMLToVI</c> writes the same file
///    with <c>errorCode 0</c>. So <c>lvai_generate_vi</c>, which validates first, cannot be used -
///    and reaching for it makes the design look impossible rather than the gate look wrong.
/// 2. <b><c>{LV.Control}</c> <c>Replace</c> is a SILENT NO-OP outside the IDE's own application
///    instance.</b> From the addon's local instance it reports terminals retyped with every error
///    cluster zero and changes nothing. The helper therefore opens everything through
///    <c>Project:Active Project -> Application</c>, which needs a project OPEN and ACTIVE.
/// 3. <b><c>Save.Instrument</c> alone does not persist that Replace on a class member.</b> The
///    owning class must be saved in the SAME run. A run that saved only the VI reported success
///    and left the on-disk file unretyped.
/// 4. <b>Membership before the saves, terminals before membership.</b> Saving a VI before it is a
///    member writes it with no owning-library link; LabVIEW then marks the LIBRARY broken and it
///    blocks every VI it owns as <c>Error 1003</c> - including healthy ones.
///
/// AND THE VERIFICATION IS THE POINT, not a flourish. That run reported all four methods working
/// on the strength of <c>Execution:State = 1</c>, and it was wrong: the readings were of a
/// correctly retyped IN-MEMORY copy that had never reached disk. The only check that saw it was
/// <c>pylv_extract</c> on the saved file - <c>class="udClassDDO"</c> for each retyped terminal and
/// no <c>class="stdPath"</c> stand-in left behind - which needs no LabVIEW at all. This tool runs
/// that check itself and refuses to report <c>ok</c> without it.
/// </summary>
[McpServerToolType]
internal sealed class ClassMethodTools(LvaiConnection connection)
{
    /// <summary>The retype-membership-dispatch helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperFileName = "lvai_add_class_method.xml";

    /// <summary>NI's accessor and method layout: 4-2-2-4, class terminals at 11 and 3.</summary>
    internal const int DefaultPanePattern = 4815;

    /// <summary>
    /// Every conIdx an authored document puts on the connector pane, or null when the file cannot
    /// be read or parsed - in which case the later steps report that properly and this one stays
    /// out of the way.
    /// </summary>
    internal static List<int>? AuthoredConIdx(string aixmlPath)
    {
        try
        {
            var root = XElement.Load(aixmlPath);
            return [.. root.DescendantsAndSelf()
                .Where(e => e.Name.LocalName is "Control" or "Indicator")
                .Select(e => (string?)e.Attribute("conIdx"))
                .Where(v => v is { Length: > 0 })
                .Select(v => int.TryParse(v, out var n) ? n : -1)
                .Where(n => n >= 0)];
        }
        catch (IOException) { return null; }
        catch (System.Xml.XmlException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// How many <c>path</c> terminals the authored document declares that are NOT class stand-ins -
    /// that is, the method's own genuine path parameters. The heap check downstream counts every
    /// <c>stdPath</c> object it finds, and without this number it reads each of these as a stand-in
    /// the repair failed to convert.
    ///
    /// MEASURED 2026-09-17, and the case was already known and left unfixed once: the
    /// <c>VerifyFailureDetail</c> comment records "a method asking to retype one of three
    /// <c>path</c> stand-ins, so two were legitimately left" from 2026-09-07, and the remedy then
    /// was only to stop that branch throwing. `Drucken.vi` - two class terminals plus a `Datei`
    /// input that is a real path - duly answered <c>ok: false</c> with <c>pathStandInsLeft: 1</c>
    /// while the file was correct and ran. Same shape as `nodesSwapped` and `dwarnCount`: a remedy
    /// written into a comment is not a fix.
    ///
    /// Null when the document cannot be read, and then nothing is gated on the count.
    /// </summary>
    internal static int? GenuinePathTerminals(string aixmlPath,
                                              IReadOnlyCollection<ClassTerminal> classTerminals) =>
        ExpectedPathObjects(aixmlPath, classTerminals)?.Total;

    /// <summary>
    /// The <c>stdPath</c> objects the saved front-panel heap SHOULD hold, split by where they sit.
    /// <see cref="TopLevel"/> counts the method's own genuine <c>path</c> terminals;
    /// <see cref="InsideClusters"/> counts <c>path</c> ELEMENTS inside a cluster or array type
    /// literal of any Control or Indicator.
    /// </summary>
    internal sealed record PathExpectation(int TopLevel, int InsideClusters)
    {
        public int Total => TopLevel + InsideClusters;
    }

    /// <summary>
    /// What <see cref="GenuinePathTerminals"/> totals. A PATH INSIDE A CLUSTER IS A HEAP OBJECT OF
    /// ITS OWN: the heap check counts the substring <c>class="stdPath"</c>, and a cluster
    /// element is written as <c>&lt;SL__arrayElement class="stdPath"&gt;</c> nested in the
    /// cluster's <c>stdClust</c> - read 2026-10-12 off NI's own `Find Broken VIs in Project.vi`
    /// (vi.lib\Utility\EditLVProj) with the bundled pylabview, whose array of clusters carries a
    /// `Path` element exactly that way. So a method with a <c>cluster{path.protocol}</c> control -
    /// a typedef cluster whose class element is authored as a path stand-in, retyped afterwards by
    /// lvai_bind_pane_typedef - answered `ok: false`, "stand-ins still on the pane", for a correct
    /// VI, three times in one field run. This tool retypes TOP-LEVEL terminals only, so such an
    /// element is expected to remain a path here, whatever it stands in for.
    ///
    /// Null when the document cannot be read, and then nothing is gated on the count.
    /// </summary>
    internal static PathExpectation? ExpectedPathObjects(
        string aixmlPath, IReadOnlyCollection<ClassTerminal> classTerminals)
    {
        try
        {
            var standIns = classTerminals.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var terminals = XElement.Load(aixmlPath).DescendantsAndSelf()
                .Where(e => e.Name.LocalName is "Control" or "Indicator")
                .ToList();

            var topLevel = terminals
                .Where(e => (string?)e.Attribute("type") == "path")
                .Count(e => (string?)e.Attribute("_name") is { } name && !standIns.Contains(name));
            var inside = terminals
                .Select(e => (string?)e.Attribute("type") ?? "")
                .Where(t => t != "path")
                .Sum(PathsInTypeLiteral);
            return new PathExpectation(topLevel, inside);
        }
        catch (IOException) { return null; }
        catch (System.Xml.XmlException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// How many <c>path</c> elements an AIXML type literal holds that become front-panel heap
    /// objects - descending into <c>cluster{TYPE.Name,...}</c> and <c>array{TYPE}</c> /
    /// <c>array.N{TYPE}</c> only (docs/aixml-reference.md section 5). A refnum's payload
    /// (<c>ref{Queue}{path}</c>) is a TYPE, not a control on the panel, and an enum's braces hold
    /// labels, so neither is entered; a FIELD NAME that happens to read `path`
    /// (<c>string.path</c>) is not a type and is not counted.
    /// </summary>
    internal static int PathsInTypeLiteral(string type)
    {
        type = type.Trim();
        var head = type;
        var stop = type.IndexOfAny(['{', '.', ',', '}']);
        if (stop >= 0) head = type[..stop];

        if (head == "path") return 1;
        if (head is not ("array" or "cluster")) return 0;

        var open = type.IndexOf('{');
        if (open < 0) return 0;
        // array.N{...}: anything between the head and the brace must be the dimension infix.
        if (open != head.Length &&
            !(head == "array" && type[head.Length] == '.' &&
              type[(head.Length + 1)..open].All(char.IsDigit)))
            return 0;

        var close = MatchingBrace(type, open);
        if (close < 0) return 0;
        var inner = type[(open + 1)..close];

        return head == "array"
            ? PathsInTypeLiteral(inner)
            : TopLevelSplit(inner).Sum(PathsInTypeLiteral);
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>A cluster's element list split at its depth-0 commas.</summary>
    private static IEnumerable<string> TopLevelSplit(string inner)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '{') depth++;
            else if (inner[i] == '}') depth--;
            else if (inner[i] == ',' && depth == 0)
            {
                yield return inner[start..i];
                start = i + 1;
            }
        }
        if (start < inner.Length) yield return inner[start..];
    }

    /// <summary>The four numbers to write for a class member on this pattern, read off its own
    /// measured geometry rather than from a table in a comment.</summary>
    internal static string PaneAdvice(int pattern)
    {
        var geometry = ConnectorPanePatterns.Find(pattern)?.Geometry;
        if (geometry is null) return $"pattern {pattern} has no measured geometry.";

        var left = geometry.LeftEdge;
        var right = geometry.RightEdge;
        if (left.Count == 0 || right.Count == 0) return $"pattern {pattern} has no left/right edge.";

        return $"class wire in {left[0].ConIdx}, more inputs "
            + string.Join("/", left.Skip(1).Take(left.Count - 2).Select(s => s.ConIdx))
            + $", error in {left[^1].ConIdx}; class wire out {right[0].ConIdx}, more outputs "
            + string.Join("/", right.Skip(1).Take(right.Count - 2).Select(s => s.ConIdx))
            + $", error out {right[^1].ConIdx}.";
    }

    [McpServerTool(Name = "lvai_add_class_method", Destructive = true, OpenWorld = true,
                   Title = "Make generated VIs into dynamic dispatch class methods")]
    [Description("""
        MUTATING: turns AIXML-generated VIs into real class METHODS - class-typed connector pane,
        DYNAMIC DISPATCH, and class membership - for many methods in ONE call.
        THIS IS THE STEP AIXML CANNOT DO. A class-typed terminal is `Control with type=UDClassInst
        is not supported`, so a method is authored with `path` stand-ins and repaired here. Measured
        2026-09-02: doing it by hand cost ~105 s of wall clock for 3.3 s inside LabVIEW.
        Each method is either an `aixml` file or a `vi` that already exists (repaired in place).
        The AIXML is VALIDATED FIRST and the verdict CLASSIFIED: a refusal naming a class type is
        the documented case where ValidateAIXML is stricter than ConvertAIXMLToVI, and is converted
        anyway; anything else STOPS, because the converter writes a broken diagram from it and the
        fault then surfaces as Error 1003 at RUN time with every file-level check green - measured
        2026-09-07 on three overrides that all reported ok. `validateFirst: false` restores the old
        convert-blind behaviour.
        RE-RUNNING OVER AN EXISTING MEMBER USED TO DESTROY THE CLASS, and this tool said it was
        SAFE until 2026-09-14. Measured on a minimal probe - one field, its two wizard accessors,
        one method with no subVI calls: a single re-run left EVERY member eBad ON DISK, accessors
        the call never named included, with privateDataBytes grown 5454 -> 5466. It survived a
        LabVIEW restart, and the call reported ok: true, terminalsRetyped: 2, verifiedOnDisk: true
        throughout. The pylabview conpane rebuild was the obvious suspect and is NOT the cause: the
        same re-run with panePattern 0, which skips that step, broke it identically.
        SO A RE-RUN NOW DROPS THE CLASS'S ENTRY FIRST, in the project-closed window, and takes the
        first-run path that is measured clean - reported as the `dropExistingMembers` step. That
        needs projectPath: editing the class file while LabVIEW holds the project open is undone by
        LabVIEW's own save. Without it, a re-run is REFUSED (`memberAlreadyListedWithoutProject`)
        rather than allowed to proceed with a green answer.
        A LOADED CALLER STILL BREAKS A RE-RUN: when a loaded VI (an override hook, any caller)
        calls the member, the re-add answers Error 56003 AFTER the entry was dropped (measured
        1.7.2-1.8.5). So before anything changes, a re-run asks the project's instance for the
        member's callers in memory and refuses with `memberHasLoadedCallers`, naming them (this
        check is NOT yet measured; `checkLoadedCallers: false` skips it). And a re-add puts the
        member at the class root as public; `keepPlacement` (default) writes its old scope and
        virtual folder back in a project-closed window afterwards (`restorePlacement` step; the
        scope lines follow a measured route, the folder move is UNMEASURED).
        AddItemFromMemory's 56002 (already a loose project item) and 1004 (already a member) are
        still tolerated and still reported as `memberAlreadyExisted`, gated on the .lvclass itself
        listing the VI, because 1004 is also what a wrong `Name` input produces.
        A METHOD PASSED AS AN EXISTING `vi` IS NOT DROPPED when the VI names the class as its owner
        (LIBN): it keeps that link, so a drop made AddItemFromMemory answer 1004 and left the class
        file WITHOUT the entry (measured 2026-10-12). Such a method takes the tolerated path
        (`keepExistingMembers`); one the class file no longer lists is refused (`memberOrphaned`) -
        pass its aixml. Each terminal's Replace error and its class read back off the panel are in
        the `retype` step, and the verify also requires the class file to list the VI.
        methodsJson is a JSON ARRAY:
          [{"aixml":"C:\\x\\Initialize.xml","vi":"C:\\cls\\Initialize.vi",
            "classTerminals":["obj in","obj out"],"dispatchTerminals":["obj in","obj out"]}]
        `classTerminals` are the pane terminals to retype to the class - found BY NAME, never by
        index, because Controls[] order is not portable. `dispatchTerminals` (names) or
        `dispatchTerminalIndices` (conIdx numbers) are the ones set to dynamic dispatch; names are
        resolved from the VI's own AIXML export. Omit both for a STATIC member.
        A TERMINAL MAY CARRY A DIFFERENT CLASS. Write it as an object instead of a name:
          "classTerminals":["obj in","obj out",{"terminal":"Engine in","class":"C:\x\Engine.lvclass"}]
        That is NI's own interface shape - `Lever.lvclass:Pry.vi` takes a `Pryable` - and it works
        on an INTERFACE too: this tool does not care that the .lvclass carries IsInterface.
        THE PROJECT MUST BE OPEN AND ACTIVE for the repair, and CLOSED for the conversion. Pass
        projectPath and this call sequences both - closed for every convert and pane repair, then
        opened once for every retype. Getting that backwards is Error 56002 on one side and a
        silent no-op on the other.
        VERIFIED FROM THE SAVED FILE, with pylabview and no LabVIEW: each retyped terminal must show
        as `class="udClassDDO"` in the front panel heap and no `stdPath` stand-in may remain beyond
        the method's own path terminals and the `path` ELEMENTS inside its cluster or array
        controls (`cluster{path.protocol}`) - those are counted from the AIXML, reported as
        `pathsInsideClusters`, and left for lvai_bind_pane_typedef where they stand in for a class. A run
        that reported four working methods from `Execution:State = 1` was reading an in-memory copy
        that never reached disk; this check is what caught it, so `ok` is false without it.
        EACH ADDED METHOD THEN HAS ITS BROKEN WIRES CLEARED (`removeBadWires`, default true), with
        the project still open and active: a retype that could not re-type a wire leaves it broken
        and the method at state 0. The removed wires are listed per method under
        `badWireCleanup`; a sink that lost its wire from a source makes `ok` false
        (`errorKind: badWireCleanupLostConnection`, the methods named in
        `methodsWithLostConnections`). UNMEASURED on a class member: whether Save.Instrument alone
        persists a Remove Bad Wires there is not established - read the method COLD afterwards.
        """)]
    public async Task<string> AddClassMethodAsync(
        [Description(@"Absolute path to the .lvclass the methods belong to")] string lvclassPath,
        [Description("JSON array of methods: aixml and/or vi, classTerminals, dispatchTerminals")]
        string methodsJson,
        [Description("""
            The .lvproj to sequence. CLOSED for the conversions, then OPENED for the repairs and
            left open. Omit only when you are driving that sequence yourself - with no project
            active the helper answers Error 1055 and nothing is retyped.
            """)]
        string? projectPath = null,
        [Description("""
            The connector pane pattern to stamp before the repair. 4815 is NI's accessor layout and
            the default; ConvertAIXMLToVI takes no pattern, so a new VI otherwise carries the
            station default from LabVIEW.ini. Pass 0 to leave the pane alone.
            """)]
        int panePattern = DefaultPanePattern,
        [Description("Read each saved file back and confirm the terminals really are class-typed")]
        bool verify = true,
        [Description("""
            Validate each method's AIXML before converting it, and stop on a fault that is NOT the
            class-wire strictness. On by default. Turn it off only when the classifier wrongly
            calls a class-wire refusal a real fault - and report that, because the classifier then
            needs the message adding to it.
            """)]
        bool validateFirst = true,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default,
        [Description(AixmlBackend.ParameterDescription + """
             On pylabview NO STEP touches NI's service: each method is COMPOSED from its AIXML (the
            path stand-ins compose like any path), the pane repaired by pylabview as before, and the
            retype-and-membership helper COMPOSED and run over LabVIEW's ActiveX server, where
            {LV.Control} Replace, AddItemFromMemory and both saves work as in the add-on's instance.
            A pylabview "validation" is a scratch compose, not NI's wiring check. helperViPath is
            ignored there. LabVIEW must already be running.
            """)]
        string? backend = null,
        [Description("""
            Lay out each method's block diagram with our layout engine (lvai_layout_diagram's)
            once it is a verified member, and save it in place (the cleanUpDiagram step). Reported,
            never gating: a layout that does not finish leaves the method as converted and still ok.
            """)]
        bool cleanUpDiagram = true,
        [Description("""
            After each method is added and verified, clear the BROKEN wires left on its diagram
            (BD.Remove Bad Wires, in the IDE's instance, project still open) and list each under the
            method's `badWireCleanup`. Default true. A lost connection fails `ok`. Runs before the
            layout, so the layout sees the cleaned diagram.
            """)]
        bool removeBadWires = true,
        [Description("""
            On a re-run, ask the project's instance first whether any LOADED VI calls a member
            about to be dropped, and refuse (`memberHasLoadedCallers`) before anything changes.
            Default true; turn it off only when the check itself cannot run and you have unhooked
            every caller by hand.
            """)]
        bool checkLoadedCallers = true,
        [Description("""
            On a re-run, put each re-added member's access scope and virtual folder back - the add
            puts it at the class root as public. Default true; a project close/open cycle is added
            only when a member was non-public or in a folder.
            """)]
        bool keepPlacement = true)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_add_class_method", out var choice, out var badBackend))
            return AixmlBackend.Refusal(badBackend);
        return AixmlTools.WithBackend(await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(lvclassPath))
                return Json.Error("badArguments", $"No .lvclass at '{lvclassPath}'.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            List<MethodRequest> methods;
            try { methods = MethodRequest.ParseAll(methodsJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            foreach (var method in methods)
            {
                if (method.Aixml is { } aixml && !File.Exists(aixml))
                    return Json.Error("badArguments",
                        $"No AIXML at '{aixml}' for method '{Path.GetFileName(method.Vi)}'.");
                if (method.Aixml is null && !File.Exists(method.Vi))
                    return Json.Error("badArguments",
                        $"'{method.Vi}' does not exist and no \"aixml\" was given to make it from.");
                foreach (var terminal in method.ClassTerminals)
                    if (terminal.ClassPath is { } other && !File.Exists(other))
                        return Json.Error("badArguments",
                            $"Terminal '{terminal.Name}' of " +
                            $"'{Path.GetFileName(method.Vi)}' names a class at '{other}', and " +
                            "there is no file there. A Replace against a missing path is refused " +
                            "by LabVIEW rather than reported, so this is checked first.");
            }

            // A RE-RUN OVER A LISTED MEMBER NEEDS THE MEMBER'S AIXML. Measured 2026-10-10 on a
            // wizard accessor passed as `vi` only: the entry was dropped, the re-add of the
            // unchanged file answered AddItemFromMemory 1004, and the class was saved WITHOUT
            // the member - its scope and folder gone with it. The drop below is only safe when
            // the VI is regenerated from AIXML, so a `vi`-only re-run is refused before anything
            // changes.
            if (ViOnlyReruns(methods, MembersAlreadyListed(Path.GetFullPath(lvclassPath),
                    methods.Select(m => m.Vi))) is { Count: > 0 } viOnly)
                return Json.Error("rerunNeedsAixml",
                    "The class already lists " + string.Join(", ", viOnly) + ", and no `aixml` was " +
                    "given to regenerate it. A re-run drops the member's entry first, and re-adding " +
                    "the unchanged file was measured failing with Error 1004 and leaving the member " +
                    "out of the class. Nothing was changed.",
                    new
                    {
                        lvclassPath = Path.GetFullPath(lvclassPath),
                        members = viOnly,
                        hint = "Pass the method's AIXML together with `vi`, or leave the member alone - " +
                               "a member the class already lists needs no re-add. A wizard accessor has " +
                               "no AIXML: rebuild it with lvai_create_accessors instead.",
                    });

            var classPath = Path.GetFullPath(lvclassPath);
            var total = Stopwatch.StartNew();
            var prologue = new JsonArray();

            // ---- The helper.
            var source = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperFileName) : null);
            if (source is null || !File.Exists(source))
                return Json.Error("helperMissing",
                    $"The helper's AIXML source could not be located ({HelperFileName} in the " +
                    "folder lvai_status reports as scriptsDirectory). Pass helperAixmlPath.");

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                AixmlBackend.HelperFolder(), "lvai_add_class_method.vi"));
            Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
            // NOT `!File.Exists` - see Infra/HelperCache. This site kept running the helper
            // built before the per-terminal class path was added to the AIXML, which is not an
            // error anywhere: the missing control simply keeps its default.
            if (choice.Name == AixmlBackend.PyLabview)
            {
                // COMPOSED and run over ActiveX: Replace, AddItemFromMemory and the saves work from
                // LabVIEW's main instance, which is the IDE's own.
                var (composed, problem) = await ComposedHelpers.EnsureAsync(HelperFileName, timeoutSeconds, ct);
                if (composed is null) return Json.Error("helperNotComposable", problem!);
                helperVi = composed;
            }
            else if (regenerateHelper || HelperCache.NeedsRebuild(source, helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    source, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct: ct, measureDiagram: false);
                prologue.Add(new JsonObject { ["step"] = "helper", ["answer"] = Read(built) });
                if (!File.Exists(helperVi))
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = "helper",
                        ["steps"] = prologue,
                        ["note"] = "The helper could not be generated, so nothing was changed. " +
                                   "Its AIXML is at " + source + ".",
                    });
            }

            var results = new JsonArray();
            var perMethodSteps = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
            var ready = new List<MethodRequest>();
            string? stoppedAt = null;

            // ---- PHASE ONE, PROJECT CLOSED. A convert or a pane repair while the project is open
            //      gets the VI adopted as a loose project item, and the membership step then
            //      answers Error 56002.
            // A RE-RUN OVER AN EXISTING MEMBER DESTROYS THE CLASS, so the entry is dropped first
            // and the ordinary first-run path handles it. Measured 2026-09-14 on a minimal probe -
            // one double field, its two wizard accessors, and one method with no subVI calls:
            //
            //   create + accessors + add        privateDataBytes 5454, every member eIdle
            //   ONE re-run of the same method   privateDataBytes 5466, and `Read Value.vi` -
            //                                   an accessor the call never named - eBad, error 8
            //
            // On disk, not in memory: it survived a full LabVIEW kill and restart. The tool
            // reported `ok: true`, `terminalsRetyped: 2`, `verifiedOnDisk: true` throughout, and
            // the method's own AIXML export was perfect - nothing anywhere said the class had
            // been damaged.
            //
            // THE pylabview CONPANE REBUILD WAS THE OBVIOUS SUSPECT AND IS NOT THE CAUSE: the
            // same probe re-run with panePattern 0, which skips that step entirely, broke the
            // accessor just the same. What is left is this path - AddItemFromMemory answering
            // 1004 for an item the class already lists, which the tolerance filter below makes
            // survivable but which evidently still mutates what the following class save writes.
            // `memberAlreadyExisted` is the only input that separates the clean run from the
            // destructive one, so that is what this removes.
            //
            // BUT ONLY FOR A METHOD THAT IS ABOUT TO BE WRITTEN AGAIN. A method passed as an
            // existing `vi` that already names this class as its owner (its LIBN block) keeps that
            // link through the drop, LabVIEW loads it as a member again, and AddItemFromMemory
            // answers 1004 against a class file that no longer lists it - the chain stops and the
            // class file is left WITHOUT the entry. Measured 2026-10-12, a `{"vi":...}` repair
            // re-run on a fresh class; a project close did not write the entry back. Such a method
            // keeps its entry and takes the tolerated already-a-member path: the VI and the class
            // agree about the membership, which is not the shape that destroyed a class on
            // 2026-09-14 (a freshly CONVERTED file, which names no owner, under an entry the class
            // still listed).
            var keptMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var method in methods.Where(m => m.Aixml is null))
            {
                var vi = Path.GetFullPath(method.Vi);
                if (NamesClassAsOwner(await OwningLibrariesAsync(vi, timeoutSeconds, ct), classPath))
                    keptMembers.Add(vi);
            }
            // AN ORPHAN - the VI names the class, the class file does not list it - is what the
            // pre-fix drop left behind. AddItemFromMemory cannot repair it: measured 2026-10-12,
            // twice, it answers 1004 (tolerated), every save answers 0, and the class file still
            // does not list the VI, which stays eBad. A recompose can, so say so before touching
            // anything.
            var orphans = keptMembers.Where(vi => !IsAlreadyMember(classPath, vi)).ToList();
            if (orphans.Count > 0)
                return Json.Error("memberOrphaned",
                    string.Join(", ", orphans.Select(Path.GetFileName)) + " names " +
                    Path.GetFileName(classPath) + " as its owner (LIBN), and the class file does " +
                    "not list it, so LabVIEW loads it as a member the class does not have - eBad. " +
                    "AddItemFromMemory cannot repair that: it answers 1004 and the class save " +
                    "writes no entry. Nothing was changed.",
                    new
                    {
                        lvclassPath = classPath,
                        orphans,
                        hint = "Call again with the method's \"aixml\": the convert writes the VI " +
                               "afresh with no owner and the first-run path makes it a member. " +
                               "This state is what a `vi`-only re-run left behind before 2026-10-12.",
                    });
            var toRewrite = methods.Where(m => !keptMembers.Contains(Path.GetFullPath(m.Vi))).ToList();
            var alreadyListed = MembersAlreadyListed(classPath, toRewrite.Select(m => m.Vi));
            if (keptMembers.Count > 0)
                prologue.Add(new JsonObject
                {
                    ["step"] = "keepExistingMembers",
                    ["kept"] = new JsonArray([.. keptMembers.Select(p => (JsonNode)Path.GetFileName(p)!)]),
                    ["why"] = "Passed as an existing `vi` that already names this class as its " +
                              "owner (LIBN, read from the file). Dropping its class entry would " +
                              "make AddItemFromMemory answer 1004 and leave the class file without " +
                              "it, so the membership is treated as existing instead.",
                });

            var needsConvert = methods.Any(m => m.Aixml is not null) || panePattern > 0;

            // A RUNNING counter, not three literals. `dropExistingMembers` and `openProject` were
            // both hardcoded `order: 2` - in the one place where the fix's correctness depends on
            // the drop happening INSIDE the project-closed window, the report said the two steps
            // were simultaneous. The drop is conditional, so the number cannot be a literal either:
            // without it, openProject really is the second step.
            var order = 0;

            // A LOADED CALLER TURNS A RE-RUN INTO 56003 AFTER THE ENTRY IS GONE. Measured with
            // 1.7.2-1.8.5: re-running over a method an override hook (or any loaded VI) calls
            // dropped the member entry and then failed the re-add with Error 56003, leaving the
            // method outside its class. Closing the project does not unload such a caller, so the
            // only safe moment to ask is now, before anything changes - in the project's instance.
            if (alreadyListed.Count > 0 && projectPath is { Length: > 0 } && checkLoadedCallers)
            {
                var (step, refusal) = await LoadedCallersAsync(classPath, alreadyListed,
                    Path.GetFullPath(projectPath), timeoutSeconds, ct);
                step["order"] = ++order;
                prologue.Add(step);
                if (refusal is not null) return refusal;
            }

            // WHERE THE MEMBERS SAT, read before the drop: the re-add puts each one back at the
            // class root as PUBLIC, and this is what puts scope and folder back afterwards.
            var placements = alreadyListed.Count > 0 && keepPlacement
                ? ClassMemberPlacement.Read(File.ReadAllText(classPath), alreadyListed)
                    .Where(p => p.NeedsRestore).ToList()
                : [];

            if (projectPath is { Length: > 0 } && (needsConvert || alreadyListed.Count > 0))
            {
                var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                    helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
                    timeoutSeconds: timeoutSeconds, ct: ct, backend: choice.Name);
                prologue.Add(new JsonObject
                {
                    ["order"] = ++order,
                    ["step"] = "closeProject",
                    ["answer"] = Read(closed),
                    ["note"] = "Error 1055 here means no project was active, which is the state " +
                               "this step is trying to reach.",
                });

                // A LOOSE PROJECT ENTRY FOR A VI ABOUT TO BECOME A MEMBER GOES FIRST. A method
                // generated with its class loaded - how it calls its own accessors with no stub,
                // measured 2026-09-25 - is generated with the project open, so LabVIEW's save
                // listed it as a loose item, and that build's agent had to remove the line by hand
                // before this call. Same closed window, same reason, as the class-file edit below.
                var looseRemoved = LvClass.RemoveLooseViEntries(
                    projectPath, methods.Select(m => Path.GetFullPath(m.Vi)));
                if (looseRemoved.Count > 0)
                    prologue.Add(new JsonObject
                    {
                        ["order"] = ++order,
                        ["step"] = "dropLooseProjectEntries",
                        ["removed"] = new JsonArray([.. looseRemoved.Select(n => (JsonNode)n!)]),
                        ["why"] = "The VI was listed in the project as a loose item - LabVIEW's " +
                                  "save adopts a VI generated with the project open - and it is " +
                                  "about to be listed through its class instead.",
                    });

                // STILL CLOSED, AND THIS IS THE ONLY WINDOW IT FITS IN - editing the class file
                // while LabVIEW holds the project open is undone by LabVIEW's own save, silently.
                var dropped = RemoveMemberEntries(classPath, toRewrite.Select(m => Path.GetFullPath(m.Vi)));

                // RemoveMemberEntries changes NOTHING when the file is not the shape it was
                // measured against - which is right, but it would leave us walking into exactly
                // the path this exists to avoid. Stop instead of proceeding hopefully.
                if (alreadyListed.Count > 0 && dropped.Count == 0)
                    return Json.Error("memberEntryCouldNotBeDropped",
                        "The class lists " + string.Join(", ", alreadyListed) +
                        " and the entry could not be removed, so this call would take the re-run " +
                        "path that has been measured leaving every member of the class eBad on " +
                        "disk. Nothing was changed.",
                        new
                        {
                            lvclassPath = classPath,
                            alreadyListed,
                            hint = "The remover leaves a .lvclass alone unless it matches the " +
                                   "one-item-per-block shape it was measured against. Remove the " +
                                   "<Item> block for that VI by hand with the project CLOSED, or " +
                                   "recreate the class.",
                        });

                if (dropped.Count > 0)
                    prologue.Add(new JsonObject
                    {
                        ["order"] = ++order,
                        ["step"] = "dropExistingMembers",
                        ["thenWhat"] = "these are re-added by the member step, so the run takes " +
                                       "the FIRST-RUN path that is measured clean",
                        ["why"] = "A re-run over a member the class still lists was measured " +
                                  "leaving every member eBad on disk, accessors included, with " +
                                  "privateDataBytes grown - and reporting ok: true throughout.",
                        ["removed"] = new JsonArray([.. dropped.Select(n => (JsonNode)n!)]),
                    });
            }
            else if (alreadyListed.Count > 0)
            {
                // No project to close, so the entry cannot be dropped safely. Say so rather than
                // walking into the destructive path with a green answer.
                return Json.Error("memberAlreadyListedWithoutProject",
                    "The class already lists " + string.Join(", ", alreadyListed) +
                    ", and re-running over an existing member has been measured DESTROYING the " +
                    "class - every member eBad on disk, accessors included, while the call " +
                    "reports ok: true. The entry has to be dropped first, and that is only safe " +
                    "with the project closed, so pass projectPath and this call will do it.",
                    new
                    {
                        lvclassPath = classPath,
                        alreadyListed,
                        hint = "Pass projectPath so the class entry can be dropped in the " +
                               "project-closed window, which turns the re-run into a first run.",
                    });
            }

            foreach (var method in methods)
            {
                var viPath = Path.GetFullPath(method.Vi);
                var steps = new JsonArray();
                perMethodSteps[viPath] = steps;
                Directory.CreateDirectory(Path.GetDirectoryName(viPath)!);

                if (method.Aixml is { } aixml)
                {
                    // THE PANE IS CHECKED BEFORE ANYTHING IS WRITTEN. A class member is re-paned
                    // onto `panePattern` further down, so a conIdx that does not exist there is a
                    // fault in the AUTHORING - and it used to surface only after validate and
                    // convert had both passed and the .vi was on disk, as the pylabview script's
                    // stderr `pattern 4815 has no slot [15]`. Measured 2026-09-14: the numbers came
                    // from `lvai_connector_pane` with no argument, which answers the STATION
                    // default (4833 here) and is the right answer for a plain VI and the wrong one
                    // for a class member. Nothing in the chain said which was being authored.
                    if (panePattern > 0 && AuthoredConIdx(aixml) is { Count: > 0 } authored)
                    {
                        var offPane = ConnectorPane.ConIdxNotOnPattern(authored, panePattern);
                        if (offPane.Count > 0)
                        {
                            steps.Add(new JsonObject
                            {
                                ["step"] = "panePreCheck",
                                ["panePattern"] = panePattern,
                                ["conIdxNotOnPattern"] = new JsonArray(
                                    [.. offPane.Select(i => (JsonNode)i!)]),
                                ["why"] = $"A class member is put on pattern {panePattern}, NI's "
                                    + "accessor layout - not on the station default that "
                                    + "lvai_connector_pane answers with no argument. Ask it for "
                                    + $"`pattern: {panePattern}` when authoring a class method.",
                                ["writeThese"] = PaneAdvice(panePattern),
                            });
                            results.Add(Failed(method, viPath, "panePreCheck", steps, null));
                            stoppedAt ??= "panePreCheck";
                            continue;
                        }
                    }

                    // The validator is STRICTER than the generator for a class wire, which is why
                    // this call converts without it. But "stricter" is not "useless": it is also
                    // the only thing that sees an ORDINARY wiring mistake, and skipping it
                    // outright is what let three overrides ship as `ok: true` and then answer
                    // Error 1003 at run time - measured 2026-09-07, with every file-level check
                    // green. So it runs, and its verdict is CLASSIFIED rather than obeyed.
                    if (validateFirst)
                    {
                        var check = await new AixmlTools(connection).ValidateAixmlAsync(
                            aixml, timeoutSeconds, backend: choice.Name, ct: ct);
                        var message = (Read(check) as JsonObject)?["errorMessage"]?
                            .GetValue<string>() ?? "";
                        // A COMPOSER refusal is never the class-wire strictness: the composer does
                        // not type-check wiring at all, it refuses what it cannot BUILD. Measured
                        // 2026-10-12: `Sensor.lvclass lists no member 'Read Value.vi'` mentions
                        // `.lvclass`, was waved through as strictness, and the convert then
                        // refused the same document a second time.
                        var composerRefused = (Read(check) as JsonObject)?["errorKind"]?
                            .GetValue<string>() == "composerRefused";
                        var classWire = Code(check) != 0 && !composerRefused
                                        && IsClassTypeComplaint(message);
                        steps.Add(new JsonObject
                        {
                            ["step"] = "preValidate",
                            ["answer"] = Read(check),
                            ["verdict"] = Code(check) == 0 ? "clean"
                                : classWire ? "classWireStrictness" : "realFault",
                            ["note"] = Code(check) == 0
                                ? "The AIXML validates on its own, so nothing here is being waved through."
                                : classWire
                                    ? "Refused for a CLASS-TYPED wire, which is the documented case " +
                                      "where ValidateAIXML is stricter than ConvertAIXMLToVI. " +
                                      "Converting anyway - that is what this tool is for."
                                    : "Refused for something that is NOT a class-typed wire, so it " +
                                      "is an ordinary fault the converter will happily write into a " +
                                      "broken diagram. Stopped here instead: the measured symptom is " +
                                      "Error 1003 at run time with every file-level check green.",
                        });

                        if (Code(check) != 0 && !classWire)
                        {
                            results.Add(Failed(method, viPath, "preValidate", steps, new JsonObject
                            {
                                ["errorMessage"] = message,
                                ["hint"] = "Fix the AIXML and call again. If this really is the " +
                                           "class-wire case and the classifier missed it, pass " +
                                           "validateFirst: false - and say so, because the " +
                                           "classifier then needs the message adding to it.",
                            }));
                            stoppedAt ??= "preValidate";
                            continue;
                        }
                    }

                    var convert = await new AixmlTools(connection).ConvertAixmlToViAsync(
                        aixml, viPath, openVI: false, timeoutSeconds, checkExecutable: false,
                        backend: choice.Name, ct: ct);
                    steps.Add(new JsonObject { ["step"] = "convert", ["answer"] = Read(convert) });
                    if (Code(convert) != 0 || !File.Exists(viPath))
                    {
                        results.Add(Failed(method, viPath, "convert", steps, null));
                        stoppedAt ??= "convert";
                        continue;
                    }
                }

                if (panePattern > 0)
                {
                    // The pane PATTERN, not the assignment: no terminal moves, so no caller changes.
                    var pane = await new BulkTools(connection).PyApplyAsync(
                        viPath,
                        new JsonArray { new JsonObject
                            { ["op"] = "conpane", ["pattern"] = panePattern } }.ToJsonString(),
                        closeProject: false, verify: false, bundleDirectory: null,
                        timeoutSeconds, ct: ct);
                    steps.Add(new JsonObject { ["step"] = "conpane", ["answer"] = Read(pane) });
                    if ((Read(pane) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                    {
                        results.Add(Failed(method, viPath, "conpane", steps, null));
                        stoppedAt ??= "conpane";
                        continue;
                    }
                }

                ready.Add(method);
            }

            // ---- PHASE TWO, PROJECT OPEN AND ACTIVE, and left that way.
            if (projectPath is { Length: > 0 } && ready.Count > 0)
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct: ct, backend: choice.Name);
                prologue.Add(new JsonObject
                {
                    ["order"] = ++order,
                    ["step"] = "openProject",
                    ["answer"] = Read(opened),
                    ["note"] = "Without this the helper's Replace is a SILENT no-op - it reports " +
                               "terminals retyped and changes nothing.",
                });
            }

            var added = 0;
            var lostConnections = new List<string>();
            foreach (var method in ready)
            {
                var viPath = Path.GetFullPath(method.Vi);
                var steps = perMethodSteps[viPath];

                // Dispatch terminals may be named rather than numbered. The VI's own export is the
                // authority on which conIdx a name sits at - the AIXML the caller wrote may have
                // been stamped onto a different pattern since.
                var indices = method.DispatchTerminalIndices;
                if (indices is null && method.DispatchTerminals is { Count: > 0 } names)
                {
                    var (resolved, note) = await ResolveConIdxAsync(viPath, names, timeoutSeconds, ct: ct,
                                                                    backend: choice.Name);
                    if (resolved is null)
                    {
                        results.Add(Failed(method, viPath, "dispatchTerminals", steps,
                            new JsonObject { ["why"] = note }));
                        stoppedAt ??= "dispatchTerminals";
                        continue;
                    }
                    indices = resolved;
                    steps.Add(new JsonObject
                    {
                        ["step"] = "dispatchTerminals",
                        ["resolved"] = new JsonArray([.. indices.Select(i => (JsonNode)i)]),
                        ["note"] = note,
                    });
                }

                // Asked from the FILE before the helper runs, so the helper can tell a benign
                // re-add from a genuinely wrong Name input. Costs no LabVIEW.
                // OR the VI itself names the class: then LabVIEW loads it as a member whatever the
                // class file says, and 1004 is the no-op it is for a listed one (keepExistingMembers).
                var memberExists = IsAlreadyMember(classPath, viPath) || keptMembers.Contains(viPath);
                var inputs = HelperInputs(method, viPath, classPath, indices, memberExists);
                var run = await new RunTools(connection).RunViAndReadValuesAsync(
                    helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds,
                    backend: choice.Name, ct: ct);
                steps.Add(new JsonObject { ["step"] = "member", ["answer"] = Read(run) });

                var values = (Read(run) as JsonObject)?["values"] as JsonObject;
                var retyped = int.TryParse(Scalar(values, "terminals retyped"), out var r) ? r : -1;
                // 56002 and 1004 are filtered inside the helper when `member exists` was passed,
                // so `add member error` reads 0 on a re-run and this is the only thing that says
                // the member was already there.
                var alreadyMember = Scalar(values, "member already existed") is "1" or "true";
                var stages = new[] { "open vi error", "class open error", "add member error",
                                     "wire rule error", "save vi error", "save class error" };
                var failedStage = stages.FirstOrDefault(s => StageCode(values, s) is not (0 or null));

                // PER TERMINAL, AND READ BACK - since 2026-10-12. The helper used to count names
                // FOUND as "retyped" with the Replace's own error out unwired, so a refused Replace
                // and a Replace that changed nothing both read `terminals retyped: 2`, error 0, and
                // only the on-disk verify saw that one stand-in had stayed a path.
                var retype = RetypeReport(method, values);
                steps.Add(new JsonObject { ["step"] = "retype", ["answer"] = retype.Report });

                if (failedStage is not null || retyped != method.ClassTerminals.Count
                    || retype.StillPathInMemory.Count > 0)
                {
                    results.Add(Failed(method, viPath, "member", steps, new JsonObject
                    {
                        ["terminalsRetyped"] = retyped,
                        ["terminalsWanted"] = method.ClassTerminals.Count,
                        ["failedStage"] = failedStage,
                        ["stageErrorCode"] = failedStage is null ? 0 : StageCode(values, failedStage),
                        ["replaceErrorCodes"] = retype.Report["replaceErrorCodes"]?.DeepClone(),
                        ["stillPathInMemory"] = new JsonArray(
                            [.. retype.StillPathInMemory.Select(n => (JsonNode)n!)]),
                        ["terminalNamesSeen"] = new JsonArray(
                            [.. Names(values, "terminal names seen").Select(n => (JsonNode)n)]),
                        ["hint"] = failedStage is null && retype.Hint is { } why
                            ? why
                            : Hint(failedStage, retyped, method),
                    }));
                    stoppedAt ??= "member";
                    continue;
                }

                // The check that catches an in-memory-only repair. No LabVIEW.
                JsonObject? evidence = null;
                var verified = true;
                if (verify)
                {
                    // A `path` TERMINAL IS NOT ALWAYS A STAND-IN. The heap check counts every
                    // `stdPath` object, and for weeks that was read as "stand-ins left over" - so
                    // a method with a genuine `path` PARAMETER always answered ok: false. Measured
                    // 2026-09-17 on `Drucken.vi`, three payload inputs one of which is a file
                    // path: `pathStandInsLeft: 1` with both class terminals correctly retyped, the
                    // member added, the class saved and `execState 1` afterwards. The control arm
                    // is the sibling method with no path payload, which answered 0 in the same run.
                    // The expectation comes from the AUTHORING AIXML, which is the only thing that
                    // knows which path terminals were meant to be stand-ins.
                    // A path ELEMENT inside a cluster control is a `stdPath` heap object too, and
                    // this tool never retypes one - see ExpectedPathObjects.
                    var expectedPaths = method.Aixml is { } authored
                        ? ExpectedPathObjects(authored, method.ClassTerminals)
                        : null;
                    var genuinePaths = expectedPaths?.Total;
                    evidence = await VerifyOnDiskAsync(viPath, method.ClassTerminals.Count,
                                                       genuinePaths, expectedPaths?.InsideClusters,
                                                       timeoutSeconds, ct: ct);
                    // AND THE CLASS FILE LISTS IT. A method can come out class-typed on disk and
                    // still not be a member: measured 2026-10-12, an orphan re-run answered ok
                    // with both terminals udClassDDO while the .lvclass named no such item and the
                    // VI read eBad. Plain XML, no LabVIEW.
                    var listed = IsAlreadyMember(classPath, viPath);
                    evidence["listedInClassFile"] = listed;
                    // REPORTED, not gated: measured source-only on both backends (2026-10-12) - the
                    // composed or converted VI goes through pylv_apply's strip and LabVIEW's own
                    // save keeps it so - but the rule is that it is read, not assumed.
                    var blocks = ClassTools.CompiledBlocks(viPath);
                    evidence["sourceOnly"] = blocks.Count == 0;
                    evidence["compiledBlocks"] = new JsonArray([.. blocks.Select(b => (JsonNode)b!)]);
                    verified = listed
                               && evidence["classTypedTerminals"]?.GetValue<int>()
                               == method.ClassTerminals.Count
                               // NOT GATED when the expectation is unknown - a method handed over
                               // as an existing `vi` has no AIXML to count, and demanding 0 there
                               // would re-create the false negative for exactly the callers who
                               // cannot see why. `pathStandInsLeft` is still reported.
                               && (genuinePaths is not { } want
                                   || evidence["pathStandInsLeft"]?.GetValue<int>() == want);
                    steps.Add(new JsonObject { ["step"] = "verify", ["answer"] = evidence });
                }

                if (!verified)
                {
                    results.Add(Failed(method, viPath, "verify", steps,
                                       VerifyFailureDetail(evidence!, method.ClassTerminals.Count)));
                    stoppedAt ??= "verify";
                    continue;
                }

                // THE PROJECT IS STILL OPEN AND ACTIVE HERE - phase two opened it for the member
                // step - which is the only window in which Remove Bad Wires reaches the copy the
                // project holds. The member and its class are already saved, so a cleanup that
                // cannot run loses nothing that was there before it.
                var cleanup = removeBadWires
                    ? await new BadWireCleanup(connection).RunAsync(viPath, timeoutSeconds, ct)
                    : BadWireCleanup.Skipped("removeBadWires was false.");

                // AFTER the verify and the bad-wire cleanup, so the file it reads is the converted,
                // cleaned one; the clean-up's own save keeps the class typing, which the class save
                // has already committed.
                bool? cleanedUp = null;
                if (cleanUpDiagram)
                {
                    var cleaned = await new DiagramCleanup(connection).RunAsync(
                        viPath, choice.Name, timeoutSeconds, ct);
                    steps.Add(cleaned);
                    cleanedUp = cleaned["ok"]?.GetValue<bool>() == true
                                && cleaned["skipped"]?.GetValue<bool>() != true;
                }

                added++;
                var methodResult = new JsonObject
                {
                    ["method"] = Path.GetFileNameWithoutExtension(viPath),
                    ["vi"] = viPath,
                    ["ok"] = true,
                    ["diagramCleanedUp"] = cleanedUp,
                    ["terminalsRetyped"] = retyped,
                    ["dynamicDispatchTerminals"] = new JsonArray([.. (indices ?? []).Select(i => (JsonNode)i)]),
                    ["verifiedOnDisk"] = verify,
                    ["sourceOnly"] = ClassTools.CompiledBlocks(viPath).Count == 0,
                    ["memberAlreadyExisted"] = alreadyMember,
                    ["pathsInsideClusters"] = evidence?["pathsInsideClusters"]?.DeepClone(),
                    ["pathsInsideClustersNote"] = evidence?["pathsInsideClustersNote"]?.DeepClone(),
                    ["steps"] = steps,
                };
                BadWireCleanup.Attach(methodResult, cleanup);
                if (BadWireCleanup.LostConnection(cleanup)) lostConnections.Add(viPath);
                results.Add(methodResult);
            }

            // ---- PHASE THREE, only for a re-run that moved a non-public or foldered member: close,
            //      put scope and folder back in the class file, open again. The edit has to sit in
            //      a project-closed window for the same reason the drop does.
            JsonObject? placementStep = null;
            var addedNames = results.OfType<JsonObject>()
                .Where(r => r["ok"]?.GetValue<bool>() == true)
                .Select(r => Path.GetFileName(r["vi"]?.GetValue<string>() ?? ""))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toRestore = placements.Where(p => addedNames.Contains(p.Member)).ToList();
            if (projectPath is { Length: > 0 } && toRestore.Count > 0)
            {
                placementStep = await RestorePlacementAsync(classPath, Path.GetFullPath(projectPath),
                    toRestore, timeoutSeconds, ct);
                placementStep["order"] = ++order;
                prologue.Add(placementStep);
            }

            return Json.Document(new JsonObject
            {
                // A LOST CONNECTION IS NOT OK, although the member WAS added: the method's diagram
                // is not the one its AIXML describes, and a green answer would send the caller on.
                ["ok"] = added == methods.Count && lostConnections.Count == 0,
                ["errorKind"] = lostConnections.Count > 0 ? BadWireCleanup.LostConnectionKind : null,
                ["methodsWithLostConnections"] = lostConnections.Count == 0 ? null
                    : new JsonArray([.. lostConnections.Select(v => (JsonNode)v!)]),
                ["placementRestored"] = placementStep?["restored"]?.DeepClone(),
                ["lvclassPath"] = classPath,
                ["methodsAsked"] = methods.Count,
                ["methodsAdded"] = added,
                ["failedAtStep"] = stoppedAt,
                ["projectLeftOpen"] = projectPath is { Length: > 0 } && ready.Count > 0,
                ["prologue"] = prologue,
                ["methods"] = results,
                ["elapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = lostConnections.Count > 0
                    ? "Every listed method is a member, but the broken-wire cleanup left an input " +
                      "unwired on " + string.Join(", ", lostConnections.Select(Path.GetFileName)) +
                      " - a connection a broken wire carried from a source is gone. Read that " +
                      "method's badWireCleanup for which ends met, fix the AIXML, and re-run."
                    : added == methods.Count
                    ? (verify
                        ? "Every method was confirmed class-typed in the SAVED file, not in memory."
                        : "verify was off, so nothing here proves the repair reached disk - the " +
                          "measured failure mode is an in-memory copy that reads as healthy.")
                    : "A method that failed at `member` left the class saved and the VI a member; " +
                      "one that failed at `convert` or `conpane` never reached the class at all.",
            });
        }), choice);
    }

    // ------------------------------------------------------------------ re-run guards

    internal const string CallersHelper = "lvai_member_callers";

    /// <summary>
    /// The A3 guard: which LOADED VIs in the project's application instance call the members a
    /// re-run is about to drop. The project is opened first when it is not the active one -
    /// opening it loads the class, and with it every member that calls the method, which is
    /// exactly the set that answers 56003 when the re-add reopens it.
    /// Returns the step to report and, when callers exist or the check could not run, the refusal.
    /// </summary>
    private async Task<(JsonObject Step, string? Refusal)> LoadedCallersAsync(
        string classPath, IReadOnlyList<string> members, string projectPath,
        int timeoutSeconds, CancellationToken ct)
    {
        var step = new JsonObject { ["step"] = "checkLoadedCallers" };
        var names = QualifiedMemberNames(classPath, members);
        step["members"] = new JsonArray([.. names.Select(n => (JsonNode)n)]);
        string Unchecked(string why) => Json.Error("loadedCallersUnchecked",
            why + " A loaded caller makes the re-add fail with Error 56003 AFTER the member entry " +
            "was dropped, so the re-run was stopped before anything changed.",
            new JsonObject
            {
                ["lvclassPath"] = classPath,
                ["members"] = step["members"]!.DeepClone(),
                ["hint"] = "Pass checkLoadedCallers: false only when every caller has been unhooked.",
            });

        if (StatusTools.ScriptsDirectory() is not { } scripts)
            return (step, Unchecked("The scripts folder is missing, so the check could not run."));

        var actions = new ActionTools(connection);
        var (active, _, activePath) = await actions.ProjectIsActiveAsync(timeoutSeconds, ct);
        if (active is not true || !string.Equals(activePath is null ? null : Path.GetFullPath(activePath),
                projectPath, StringComparison.OrdinalIgnoreCase))
        {
            var opened = JsonNode.Parse(await actions.OpenFileAsync(viPath: null, viName: null,
                projectPath: projectPath, projectName: Path.GetFileName(projectPath), checkActive: true,
                timeoutSeconds, ct: ct));
            step["openedProject"] = opened?["projectBecameActive"]?.DeepClone();
            if (opened?["projectBecameActive"]?.GetValue<bool>() is not true)
                return (step, Unchecked("The project could not be made active to look for callers."));
        }

        var steps = new JsonArray();
        var headless = new DqmhHeadless(connection);
        if (await headless.EnsureWrapperAsync(scripts, CallersHelper, [], projectPath, steps, timeoutSeconds, ct)
            is { } wrapperError)
            return (step, Unchecked($"The callers helper could not be built: {wrapperError}"));
        var (values, helperError) = await new DqmhTools(connection).RunDetailedAsync(scripts, CallersHelper,
            new() { ["Member Names"] = DqmhFrameTools.StringArrayXml("Member Names", names) }, timeoutSeconds, ct);
        if (values is null || helperError is not null || DqmhTools.Failed(values) is { Length: > 0 })
            return (step, Unchecked("The callers helper failed: " + (helperError ?? DqmhTools.Failed(values ?? []))));

        var loaded = DqmhHeadless.Listed(values, "Loaded Members");
        var callers = GroupCallers(DqmhHeadless.Listed(values, "Callers"));
        step["loadedMembers"] = DqmhHeadless.Array(loaded);
        step["callers"] = CallersJson(callers);
        if (callers.Count == 0) return (step, null);

        return (step, Json.Error("memberHasLoadedCallers",
            "Loaded VIs call " + string.Join(", ", callers.Keys) + ". Re-adding a member a loaded VI " +
            "calls fails with Error 56003 AFTER its entry was dropped from the class, so nothing was " +
            "changed.",
            new JsonObject
            {
                ["lvclassPath"] = classPath,
                ["callers"] = CallersJson(callers),
                ["hint"] = "Unhook each caller first - lvai_af_remove_hook for an override hook, or " +
                           "lvai_swap_subvis onto a placeholder (lvai_placeholder_subvi) for any caller - " +
                           "then re-run, then hook or swap back. A caller of the same pane can also simply " +
                           "be swapped onto a NEW member that carries the new behaviour.",
            }));
    }

    /// <summary>The library-qualified in-memory name of each member, from the class file.</summary>
    internal static List<string> QualifiedMemberNames(string classPath, IEnumerable<string> members)
    {
        string qualified;
        try { qualified = LvClass.Read(classPath).QualifiedName; }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            qualified = Path.GetFileName(classPath);
        }
        return [.. members.Select(m => $"{qualified}:{Path.GetFileName(m)}")];
    }

    /// <summary>`member|caller` pairs from the helper, grouped by member, callers de-duplicated.</summary>
    internal static SortedDictionary<string, List<string>> GroupCallers(IEnumerable<string> pairs)
    {
        var grouped = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var bar = pair.IndexOf('|');
            if (bar <= 0 || bar == pair.Length - 1) continue;
            var (member, caller) = (pair[..bar], pair[(bar + 1)..]);
            if (!grouped.TryGetValue(member, out var list)) grouped[member] = list = [];
            if (!list.Contains(caller, StringComparer.Ordinal)) list.Add(caller);
        }
        return grouped;
    }

    private static JsonObject CallersJson(SortedDictionary<string, List<string>> callers)
    {
        var json = new JsonObject();
        foreach (var (member, list) in callers) json[member] = new JsonArray([.. list.Select(c => (JsonNode)c)]);
        return json;
    }

    /// <summary>
    /// Close, put each member's scope lines and folder back in the class file, open again. The
    /// edit itself is ClassMemberPlacement.Restore; this is only the project-closed window around it.
    /// </summary>
    private async Task<JsonObject> RestorePlacementAsync(string classPath, string projectPath,
        IReadOnlyList<ClassMemberPlacement.Placement> placements, int timeoutSeconds, CancellationToken ct)
    {
        var step = new JsonObject
        {
            ["step"] = "restorePlacement",
            ["wanted"] = new JsonArray([.. placements.Select(p => (JsonNode)p.Describe())]),
            ["measured"] = "scope lines: the set_scope route (both properties, project closed); " +
                           "moving a member into a folder by a file edit: UNMEASURED",
        };
        var closed = await new CloseTools(connection).CloseActiveProjectAsync(
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
            timeoutSeconds: timeoutSeconds, ct: ct);
        step["closeProject"] = Read(closed);

        var (text, notes) = ClassMemberPlacement.Restore(File.ReadAllText(classPath), placements);
        step["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)n)]);
        var restored = false;
        if (text is not null)
        {
            try
            {
                File.WriteAllText(classPath, text);
                restored = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                step["writeError"] = e.Message;
            }
        }
        step["restored"] = restored;

        var opened = await new ActionTools(connection).OpenFileAsync(viPath: null, viName: null,
            projectPath: projectPath, projectName: Path.GetFileName(projectPath), checkActive: true,
            timeoutSeconds, ct: ct);
        step["openProject"] = Read(opened);
        step["thenWhat"] = restored
            ? "Read the members back with lvai_describe_class (scope) and check them cold with " +
              "lvai_exec_state after the next project close."
            : "The class file was left as the re-add wrote it: set scope and folder by hand.";
        return step;
    }

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// The saved file's own account of the repair: one <c>udClassDDO</c> per class-typed terminal
    /// and no <c>stdPath</c> stand-in left over.
    ///
    /// THIS IS THE ONLY CHECK THAT CAUGHT THE 2026-09-02 DEFECT. <c>Execution:State = 1</c>, a
    /// describe, and an AIXML export all read healthy while the retyped copy sat in memory and the
    /// file on disk still carried paths. It also costs nothing: pylabview reads the file with no
    /// LabVIEW running at all.
    /// </summary>
    private static async Task<JsonObject> VerifyOnDiskAsync(string viPath, int expected,
                                                            int? genuinePaths,
                                                            int? pathsInsideClusters,
                                                            int timeoutSeconds, CancellationToken ct)
    {
        var evidence = await ReadHeapAsync(viPath, expected, genuinePaths, timeoutSeconds, ct);
        AnnotatePathsInsideClusters(evidence, pathsInsideClusters);
        return evidence;
    }

    /// <summary>
    /// Report the path elements inside cluster controls on their own. They are part of
    /// `expectedPathStandInsLeft` - the heap counts them as `stdPath` like any other - and are
    /// named apart because they are the ones a LATER step may still have to resolve.
    /// </summary>
    internal static void AnnotatePathsInsideClusters(JsonObject evidence, int? pathsInsideClusters)
    {
        evidence["pathsInsideClusters"] = pathsInsideClusters;
        if (pathsInsideClusters is > 0)
            evidence["pathsInsideClustersNote"] =
                $"{pathsInsideClusters} `path` element(s) sit INSIDE a cluster or array control " +
                "and are counted as expected: this tool retypes top-level class terminals only. " +
                "If such an element stands in for a class inside a typedef cluster, " +
                "lvai_bind_pane_typedef is the step that resolves it.";
    }

    private static async Task<JsonObject> ReadHeapAsync(string viPath, int expected,
                                                        int? genuinePaths,
                                                        int timeoutSeconds, CancellationToken ct)
    {
        var bundle = PyLabview.Locate();
        if (bundle is null)
            return new JsonObject
            {
                ["ran"] = false,
                ["why"] = PyLabview.NotProvisionedMessage(),
                ["classTypedTerminals"] = -1,
                ["pathStandInsLeft"] = -1,
                ["expectedPathStandInsLeft"] = genuinePaths,
            };

        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "method",
                                   Path.GetRandomFileName());
        Directory.CreateDirectory(scratch);
        try
        {
            var main = Path.Combine(scratch, "m.xml");
            var run = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", viPath, "-m", main], Rpc.ClampToolWait(timeoutSeconds), ct);
            if (run.ExitCode != 0)
                return new JsonObject
                {
                    ["ran"] = false,
                    ["why"] = $"pylabview exited {run.ExitCode}.",
                    ["classTypedTerminals"] = -1,
                    ["pathStandInsLeft"] = -1,
                    ["expectedPathStandInsLeft"] = genuinePaths,
                };

            // The front panel heap is the sidecar, not the main file.
            var heap = Directory.GetFiles(scratch, "*_FPHb.xml").FirstOrDefault();
            var text = heap is null ? "" : await File.ReadAllTextAsync(heap, ct);

            return new JsonObject
            {
                ["ran"] = true,
                ["frontPanelHeap"] = heap is null ? null : Path.GetFileName(heap),
                ["classTypedTerminals"] = Count(text, "class=\"udClassDDO\""),
                ["pathStandInsLeft"] = Count(text, "class=\"stdPath\""),
                // How many `stdPath` objects are LEGITIMATE - the method's own path parameters,
                // counted from the authoring AIXML. null when there was none to count, and then
                // nothing is gated on the number.
                ["expectedPathStandInsLeft"] = genuinePaths,
                ["expected"] = expected,
                ["source"] = "the saved .vi, read with pylabview - no LabVIEW was involved",
            };
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The owning libraries a saved VI names for itself - its <c>LIBN</c> block, read with pylabview
    /// and no LabVIEW - or null when the file cannot be read. A class member names its class
    /// (<c>Probe.lvclass</c>); a VI freshly composed or converted names none.
    ///
    /// WHY IT MATTERS HERE. Dropping a member's entry from the .lvclass turns a re-run into a first
    /// run only when the VI is about to be WRITTEN AGAIN without that link - the `aixml` case. A
    /// method passed as an existing `vi` keeps its LIBN, so LabVIEW loads it as a member of the
    /// class again and AddItemFromMemory answers 1004 against a class file that no longer lists it.
    /// Measured 2026-10-12 on a fresh class: the chain stopped at `add member error 1004` and LEFT
    /// THE CLASS FILE WITHOUT THE ENTRY, the method an orphan naming its class - and a project
    /// close did not write it back.
    /// </summary>
    internal static async Task<List<string>?> OwningLibrariesAsync(string viPath, int timeoutSeconds,
                                                                   CancellationToken ct)
    {
        var bundle = PyLabview.Locate();
        if (bundle is null || !File.Exists(viPath)) return null;

        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "method",
                                   Path.GetRandomFileName());
        Directory.CreateDirectory(scratch);
        try
        {
            var main = Path.Combine(scratch, "m.xml");
            var run = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", viPath, "-m", main], Rpc.ClampToolWait(timeoutSeconds), ct);
            if (run.ExitCode != 0 || !File.Exists(main)) return null;
            return OwningLibrariesFromMainXml(await File.ReadAllTextAsync(main, ct));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The <c>&lt;Library&gt;</c> names in a pylabview main XML's LIBN block, outermost first.</summary>
    internal static List<string> OwningLibrariesFromMainXml(string mainXml)
    {
        try
        {
            return [.. XDocument.Parse(mainXml).Descendants("LIBN")
                .SelectMany(b => b.Descendants("Library")).Select(l => l.Value.Trim())
                .Where(v => v.Length > 0)];
        }
        catch (System.Xml.XmlException) { return []; }
    }

    /// <summary>
    /// Does the VI name this class among its owners? Any LIBN entry, not a position: a class inside
    /// a library has not been measured, and which end of the list it sits at is not known.
    /// </summary>
    internal static bool NamesClassAsOwner(IReadOnlyList<string>? libraries, string classPath) =>
        libraries is { Count: > 0 }
        && libraries.Any(l => string.Equals(l, Path.GetFileName(classPath),
                                            StringComparison.OrdinalIgnoreCase));

    private static int Count(string text, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    // ------------------------------------------------------------------ conIdx from names

    /// <summary>
    /// Which <c>conIdx</c> each named terminal actually sits at, from the VI's own AIXML export.
    ///
    /// NOT FROM THE AIXML THE CALLER WROTE. <c>ConvertAIXMLToVI</c> takes no pane pattern, so the
    /// VI carries the station default until the pane repair stamps another - and the same number
    /// means opposite edges on 4815 and 4833. The export is what the file says now.
    /// </summary>
    private async Task<(List<int>? Indices, string Note)> ResolveConIdxAsync(
        string viPath, IReadOnlyList<string> names, int timeoutSeconds, CancellationToken ct,
        string? backend = null)
    {
        // unset means the route's own choice - inherited when the calling tool runs on
        // pylabview, grpc otherwise
        backend ??= AixmlBackend.Inherited.Value?.Name ?? AixmlBackend.Grpc;
        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            Path.ChangeExtension(Path.GetFileName(viPath), ".conidx.xml"));
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
            viPath, export, returnContent: true, maxContentChars: 200000,
            timeoutSeconds: timeoutSeconds, refresh: true, backend: backend, ct: ct);
        if ((Read(answer) as JsonObject)?["xml"]?.GetValue<string>() is not { } xml)
            return (null, "The VI could not be exported, so a terminal NAME cannot be turned into " +
                          "a conIdx. Pass dispatchTerminalIndices instead.");

        XElement root;
        try { root = XElement.Parse(xml); }
        catch (System.Xml.XmlException ex) { return (null, $"The export did not parse: {ex.Message}"); }

        var byName = root.Elements()
            .Where(e => e.Attribute("conIdx") is not null && e.Attribute("_name") is not null)
            .ToDictionary(e => (string)e.Attribute("_name")!,
                          e => (string)e.Attribute("conIdx")!, StringComparer.OrdinalIgnoreCase);

        var indices = new List<int>();
        foreach (var name in names)
        {
            if (!byName.TryGetValue(name, out var text) || !int.TryParse(text, out var index))
                return (null, $"'{name}' is not a terminal on this VI's connector pane. It carries: " +
                              string.Join(", ", byName.Select(p => $"{p.Key}@{p.Value}")));
            indices.Add(index);
        }
        return (indices, "conIdx read off the VI's own export, not off the authoring AIXML.");
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Is this validator refusal the documented CLASS-WIRE STRICTNESS, or an ordinary fault?
    ///
    /// The distinction is the whole value of running the validator at all here. Measured
    /// 2026-09-01: <c>lvai_validate_aixml</c> refuses a class-typed wire with
    /// <c>Error 53 ... the type of the source is Test Case.lvclass ... the type of the sink is
    /// file path</c> while <c>ConvertAIXMLToVI</c> writes the same file with <c>errorCode 0</c>.
    /// That case must NOT block - authoring against stand-ins and repairing afterwards is what
    /// this tool exists for. Everything else must, because the converter writes a broken diagram
    /// and the fault then surfaces as Error 1003 at RUN time, long after every file-level check
    /// has passed it.
    ///
    /// Deliberately conservative: an unrecognised message counts as a real fault. Waving one
    /// through is the failure this method was added to stop, and the caller has
    /// <c>validateFirst: false</c> when the classifier is wrong.
    /// </summary>
    internal static bool IsClassTypeComplaint(string message) =>
        message.Contains(".lvclass", StringComparison.OrdinalIgnoreCase)
        || message.Contains("UDClassInst", StringComparison.OrdinalIgnoreCase)
        || message.Contains("LabVIEW Object", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static int Code(string answer) =>
        (Read(answer) as JsonObject)?["errorCode"]?.GetValue<int>() ?? -1;

    private static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    private static int? StageCode(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    /// <summary>What the helper says about each terminal it was asked to retype.</summary>
    internal sealed record Retype(JsonObject Report, List<string> StillPathInMemory, string? Hint);

    /// <summary>
    /// Each requested terminal's Replace error code and its class as the panel shows it AFTER the
    /// loop - read back from a fresh <c>Controls[]</c>, not from the reference Replace returned.
    ///
    /// WHY BOTH. Measured 2026-10-12 on the pylabview backend (C:\Temp\BackendCompare): a method
    /// answered `terminals retyped: 2` with every stage at error 0, and the SAVED file still had
    /// `Sensor out` as a path. The helper counted names FOUND and left the Replace's error out
    /// unwired, so a refused Replace, and a Replace that answers 0 and changes nothing (CLAUDE.md
    /// records that shape outside the IDE's own instance), were indistinguishable from success
    /// until the on-disk verify. This separates the three: a code, an in-memory no-op, or a retype
    /// that reached memory and not the file - which the verify still answers.
    ///
    /// A helper from before these indicators existed reports none of them; then nothing is gated
    /// here and `readBack` says so.
    /// </summary>
    internal static Retype RetypeReport(MethodRequest method, JsonObject? values)
    {
        var codes = Names(values, "replace error codes")
            .Select(t => int.TryParse(t, out var c) ? c : (int?)null).ToList();
        var classes = Names(values, "classes after replace");
        var names = Names(values, "names after replace");
        var after = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(classes.Count, names.Count); i++)
            after.TryAdd(names[i], classes[i]);

        var terminals = new JsonArray();
        var stillPath = new List<string>();
        var refused = new List<string>();
        var missing = new List<string>();
        for (var i = 0; i < method.ClassTerminals.Count; i++)
        {
            var name = method.ClassTerminals[i].Name;
            var code = i < codes.Count ? codes[i] : null;
            var cls = after.TryGetValue(name, out var c) ? c : null;
            terminals.Add(new JsonObject
            {
                ["terminal"] = name,
                ["replaceErrorCode"] = code,
                ["classAfterReplace"] = cls,
            });
            if (code is -1) missing.Add(name);
            else if (code is { } bad && bad != 0) refused.Add($"'{name}' (Error {bad})");
            if (string.Equals(cls, "Path", StringComparison.Ordinal)) stillPath.Add(name);
        }

        var readBack = classes.Count > 0;
        string? hint = null;
        if (refused.Count > 0)
            hint = "LabVIEW REFUSED the Replace for " + string.Join(", ", refused) + ". Until " +
                   "2026-10-12 this code was discarded and the terminal still counted as retyped.";
        else if (missing.Count > 0)
            hint = "Not on the panel: " + string.Join(", ", missing.Select(n => $"'{n}'")) +
                   ". Terminals are matched BY NAME - compare with terminalNamesSeen.";
        else if (stillPath.Count > 0)
            hint = "Replace answered NO ERROR for " +
                   string.Join(", ", stillPath.Select(n => $"'{n}'")) + " and the panel, read " +
                   "back after the loop, still shows a Path - the retype never happened in memory, " +
                   "so no save could persist it. The shape CLAUDE.md records for this is Replace " +
                   "running outside the IDE's own application instance; close the project " +
                   "(lvai_close_active_project) and call again, and say so if it repeats.";

        return new Retype(new JsonObject
        {
            ["terminals"] = terminals,
            ["replaceErrorCodes"] = new JsonArray([.. codes.Select(x => (JsonNode?)x)]),
            ["readBack"] = readBack,
            ["note"] = readBack
                ? "Each terminal's class is read off the panel AFTER the Replace loop, before " +
                  "any save. `Path` there means the Replace did nothing in memory."
                : "This helper reports no read-back (an older build of it), so only the on-disk " +
                  "verify judges the retype.",
        }, stillPath, hint);
    }

    /// <summary>
    /// The elements of an array indicator, from LabVIEW's flattened XML. HONOURS <c>Dimsize</c>:
    /// an EMPTY array is still written with one template element (`<Dimsize>0</Dimsize>` and a
    /// `<String><Val></Val></String>`), which read as one empty name until 2026-10-12.
    /// </summary>
    internal static List<string> Names(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return [];
        var all = System.Text.RegularExpressions.Regex.Matches(xml, "<Val>([^<]*)</Val>")
            .Select(m => m.Groups[1].Value).ToList();
        var size = System.Text.RegularExpressions.Regex.Match(xml, "<Dimsize>(\\d+)</Dimsize>");
        return size.Success && int.TryParse(size.Groups[1].Value, out var n) && n < all.Count
            ? all.Take(n).ToList()
            : all;
    }

    /// <summary>
    /// Why the on-disk check failed, as a FRESH object.
    ///
    /// NOT <c>evidence</c> itself: that node is already attached to <c>steps</c>, and
    /// System.Text.Json refuses a node that has a parent. Passing it on threw
    /// <c>InvalidOperationException: The node already has a parent</c> - which turned the one
    /// branch that exists to report a repair that never reached disk into an exception with no
    /// method list, no steps and no hint. Measured 2026-09-07, on the first call that ever took it:
    /// a method asking to retype one of three <c>path</c> stand-ins, so two were legitimately left.
    /// </summary>
    internal static JsonObject VerifyFailureDetail(JsonObject evidence, int expected)
    {
        var typed = evidence["classTypedTerminals"]?.GetValue<int>() ?? -1;
        var left = evidence["pathStandInsLeft"]?.GetValue<int>() ?? -1;
        var want = evidence["expectedPathStandInsLeft"]?.GetValue<int?>();
        var listed = evidence["listedInClassFile"]?.GetValue<bool>();
        var inside = evidence["pathsInsideClusters"]?.GetValue<int?>();
        return new JsonObject
        {
            ["classTypedTerminals"] = typed,
            ["pathStandInsLeft"] = left,
            ["expectedPathStandInsLeft"] = want,
            ["pathsInsideClusters"] = inside,
            ["expected"] = expected,
            ["listedInClassFile"] = listed,
            ["ran"] = evidence["ran"]?.GetValue<bool>() ?? true,
            ["hint"] = listed is false && typed == expected
                ? "Every terminal is class-typed in the saved file, but the .lvclass does NOT list "
                  + "this VI, so it is not a member - LabVIEW loads it eBad. Call again with the "
                  + "method's \"aixml\" and projectPath: the convert writes the VI afresh and the "
                  + "first-run path makes it a member."
                : typed == -1
                ? "The check could not run at all, so this says nothing about the file - see `ran` "
                  + "and `why` in the verify step."
                // COMPARED AGAINST THE EXPECTATION, not against zero. A method may declare genuine
                // `path` parameters of its own, and counting those as leftover stand-ins is the
                // false negative this hint used to produce. An UNKNOWN expectation still reads as
                // zero HERE - the historical wording is the best guess available and the number is
                // printed beside it - it just does not gate `ok` any more.
                : left > (want ?? 0)
                    ? $"{left - (want ?? 0)} `path` stand-in(s) are still on the pane"
                      + (want is { } p and > 0
                            ? $", over the {p} the document declares as real path terminals"
                              + (inside is { } c and > 0
                                    ? $" ({c} of them elements inside cluster or array controls)"
                                    : "")
                            : "")
                      + ". Either a terminal was left out of classTerminals, or its name is "
                      + "spelled differently there than on the pane - compare with `terminal "
                      + "names seen` in the member step. If the `retype` step read every requested "
                      + "terminal back off `Path`, the retype reached MEMORY and not the saved "
                      + "file - close the project (lvai_close_active_project) and call again."
                    : $"{typed} of {expected} terminals are class-typed in the SAVED file. The "
                      + "retype may have stayed in memory: that is the 2026-09-02 failure this "
                      + "check exists for.",
        };
    }

    /// <summary>
    /// The helper's controls, by name. Extracted from the call site because two of the three rules
    /// below are invisible there and were each shipped broken once.
    ///
    /// AN EMPTY VALUE IS REFUSED BY THE RUNNER, so a control with nothing to say is OMITTED rather
    /// than set to "". Values are paired with names BY POSITION, an empty one does not survive the
    /// helper's split, and every later input would land on the wrong control - hence the refusal.
    /// A STATIC member has no dispatch indices, so this is not an edge case but the whole reason
    /// static members did not work: measured 2026-09-07 on an interface's own <c>Pry.vi</c> shape,
    /// the run answered <c>badArguments ... has an empty value</c> and stopped at <c>member</c>,
    /// after the pane had already been rebuilt.
    ///
    /// AND THE PATHS ARE PER TERMINAL, in the same order as the names, because a terminal may be
    /// typed on a class other than the method's own.
    /// </summary>
    internal static JsonObject HelperInputs(MethodRequest method, string viPath, string classPath,
                                            List<int>? indices, bool memberExists = false)
    {
        var inputs = new JsonObject
        {
            ["vi path"] = viPath,
            ["class path"] = classPath,
            ["class terminal names"] = string.Join("|", method.ClassTerminals.Select(t => t.Name)),
            ["terminal class paths"] =
                string.Join("|", method.ClassTerminals.Select(t => t.ClassPath ?? classPath)),
            ["vi name in memory"] = Path.GetFileName(viPath),
            // Gates the helper's tolerance of 56002 and 1004 at AddItemFromMemory. Not a
            // convenience: 1004 also means "the Name input was a path where a bare name belongs",
            // so tolerating it unconditionally would mask a real bug.
            ["member exists"] = memberExists ? "1" : "0",
        };
        if (indices is { Count: > 0 })
            inputs["dispatch terminal indices"] = string.Join("|", indices);
        return inputs;
    }

    /// <summary>
    /// Does the <c>.lvclass</c> already list this VI as a member? Read from the FILE, which is
    /// plain XML, so this costs no LabVIEW at all.
    ///
    /// WHY IT IS ASKED BEFORE THE HELPER RUNS. <c>AddItemFromMemory</c> answers <c>56002</c> when
    /// the VI is already a loose project item and <c>1004</c> when it is already a member, and
    /// both are no-ops that must not abort the retype-and-save chain. But <c>1004</c> is also what
    /// a wrong <c>Name</c> input produces - §3.0 records it for a full path where a bare name
    /// belongs - so the two are told apart by asking the class file, not by trusting the code.
    ///
    /// Matched on the item's own <c>Name</c> attribute rather than on the URL, because a member's
    /// URL is relative and its spelling varies (<c>../Describe.vi</c> for a member in a subfolder).
    /// </summary>
    internal static bool IsAlreadyMember(string classPath, string viPath)
    {
        string text;
        try { text = File.ReadAllText(classPath); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        var name = Path.GetFileName(viPath);
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("<Item", StringComparison.Ordinal)) continue;
            if (line.Contains($"Name=\"{name}\"", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Which of <paramref name="viPaths"/> the class file ALREADY lists as members, by file name.
    ///
    /// Read from the `.lvclass` rather than asked of LabVIEW, for the reason every other check in
    /// this file is: the file is the authority and it costs nothing. It is deliberately the same
    /// name-only comparison <see cref="RemoveMemberEntries"/> makes, so the two can never disagree
    /// about what is about to be dropped.
    /// </summary>
    internal static IReadOnlyList<string> MembersAlreadyListed(
        string classPath, IEnumerable<string> viPaths)
    {
        var wanted = new HashSet<string>(
            viPaths.Select(p => Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];

        string text;
        try { text = File.ReadAllText(classPath); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        return [.. wanted
            .Where(name => text.Contains($"<Item Name=\"{name}\"", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>The file names of methods given as `vi` only that the class already lists.</summary>
    internal static List<string> ViOnlyReruns(IEnumerable<MethodRequest> methods, IReadOnlyList<string> alreadyListed) =>
        [.. methods.Where(m => m.Aixml is null)
            .Select(m => Path.GetFileName(m.Vi))
            .Where(n => alreadyListed.Contains(n, StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// Delete the <c>&lt;Item … Type="VI"&gt;</c> blocks naming these VIs from a <c>.lvclass</c>,
    /// returning the names actually removed. Nothing is written when nothing matched.
    ///
    /// WHY A TOOL HAS TO DO THIS, rather than tolerating the error. Re-generating a method the class
    /// ALREADY lists cannot work any other way: <c>AddItemFromMemory</c> refuses an item the project
    /// already holds - 56002 or 1004 - and never establishes the owning-library link inside the .vi,
    /// which the convert has by then already overwritten. Measured 2026-09-08 in BOTH directions: the
    /// run with the refusal filtered out reported <c>memberAlreadyExisted: true</c> with both saves
    /// executing and <c>isClassMember</c> still FALSE, while dropping the entry first and letting the
    /// ordinary add path run gave <c>isClassMember: true</c> on every method. The filter makes the
    /// breakage VISIBLE; only this makes the re-add actually happen.
    ///
    /// IT MUST RUN WITH THE PROJECT CLOSED, and the caller is responsible for that. This edits the
    /// file behind LabVIEW's back; with the project open LabVIEW holds the class in memory and writes
    /// its own copy back over this on the next save, so the removal would silently not have happened.
    ///
    /// A LABVIEW RESTART IS NOT NEEDED, and that was worth measuring rather than assuming: probed
    /// 2026-09-08 by stripping one entry with the project closed and re-adding the method WITHOUT
    /// restarting, which answered <c>isClassMember: true</c>. The project close is the effective
    /// part - the manual repairs that preceded this tool restarted LabVIEW as well, and the restart
    /// was never the ingredient that mattered.
    ///
    /// TEXT, NOT XML. An <c>XDocument</c> round trip would reformat a file LabVIEW owns and diff
    /// noisily against everything else in it. Only whole lines are dropped here; every other byte,
    /// line ending included, survives.
    ///
    /// IT FAILS SAFE. Anything not matching the flat shape measured on real class files - a nested
    /// item inside a VI block, a block that never closes - abandons the edit and writes NOTHING,
    /// because a half-stripped class file is worse than the state this is repairing.
    ///
    /// The LabVIEW-side alternative, opening the class and calling <c>RemoveItem</c>, was not chosen:
    /// it needs the project OPEN, which is the opposite of what the convert needs, so it would buy a
    /// second open/close cycle per call in exchange for replacing an edit already measured to work.
    /// </summary>
    internal static IReadOnlyList<string> RemoveMemberEntries(
        string classPath, IEnumerable<string> viPaths)
    {
        var wanted = new HashSet<string>(
            viPaths.Select(p => Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];

        string text;
        LvTextFile.Style style;
        try { (text, style) = LvTextFile.Read(classPath); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        // Split on '\n' only, so each element keeps any '\r' it had and the join is byte-exact.
        // The LINE ENDINGS were therefore always kept; what was lost until 2026-10-12 was the BOM,
        // which File.ReadAllText strips and File.WriteAllText never writes.
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        var removed = new List<string>();
        string? dropping = null;

        foreach (var line in lines)
        {
            if (dropping is not null)
            {
                // Nested item: not the shape this was measured against, so change nothing at all.
                if (line.Contains("<Item", StringComparison.Ordinal)) return [];
                if (line.Contains("</Item>", StringComparison.Ordinal))
                {
                    removed.Add(dropping);
                    dropping = null;
                }
                continue;
            }

            var opensViItem = line.Contains("<Item", StringComparison.Ordinal)
                              && line.Contains("Type=\"VI\"", StringComparison.Ordinal);
            var match = opensViItem
                ? wanted.FirstOrDefault(
                    n => line.Contains($"Name=\"{n}\"", StringComparison.OrdinalIgnoreCase))
                : null;

            if (match is null)
            {
                kept.Add(line);
                continue;
            }

            // A self-closing entry is the whole block; there is no closing line to wait for.
            if (line.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
            {
                removed.Add(match);
                continue;
            }
            dropping = match;
        }

        // An unterminated block means the file is not what we parsed. Leave it alone.
        if (dropping is not null || removed.Count == 0) return [];

        try { LvTextFile.Write(classPath, string.Join("\n", kept), style); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        return removed;
    }

    private static JsonObject Failed(MethodRequest method, string viPath, string step,
                                     JsonArray steps, JsonObject? detail) =>
        new()
        {
            ["method"] = Path.GetFileNameWithoutExtension(viPath),
            ["vi"] = viPath,
            ["ok"] = false,
            ["failedAtStep"] = step,
            ["detail"] = detail,
            ["steps"] = steps,
        };

    /// <summary>What a stage failure most often means, in one sentence per measured cause.</summary>
    private static string Hint(string? stage, int retyped, MethodRequest method) => stage switch
    {
        "open vi error" => "The VI could not be opened in the IDE's instance. Error 1055 means no " +
                           "project is active - pass projectPath.",
        "class open error" => "LVClass.Open failed. Error 1055 again points at no active project.",
        "add member error" => "AddItemFromMemory failed. 56002 means the VI is already a LOOSE " +
                              "PROJECT ITEM - it was open when the VI was converted. 1004 means " +
                              "either that the VI is already a MEMBER, or that `Name` got a full " +
                              "path where a bare name belongs. Both no-op cases are tolerated " +
                              "automatically when the .lvclass already lists the VI, or when the " +
                              "VI file itself names the class as its owner (LIBN); seeing 1004 " +
                              "HERE therefore means neither was read, so check the spelling of the " +
                              "VI's file name against the class's Item entries.",
        "wire rule error" => "SetWireRule failed. A conIdx that is not on this pane's pattern is " +
                             "the usual cause; check with lvai_connector_pane.",
        "save vi error" => "Save.Instrument failed - most often the file is read-only or held.",
        "save class error" => "The class could not be saved, so the retype did NOT persist even " +
                              "though the VI itself saved.",
        _ => $"No stage reported an error, but {retyped} of {method.ClassTerminals.Count} " +
             "terminals were retyped. Terminals are matched BY NAME: check the names in " +
             "`terminalNamesSeen` against `classTerminals`.",
    };

    // ------------------------------------------------------------------ the request

    /// <summary>
    /// One pane terminal to retype, and the class it should carry. <c>ClassPath</c> is null for the
    /// ordinary case - the method's own class - and set when a terminal is typed on a DIFFERENT
    /// class, which is what NI's own interfaces do: <c>Lever.lvclass:Pry.vi</c> takes a
    /// <c>Pryable</c>. Measured 2026-09-07; before this the tool retyped every terminal to
    /// <c>lvclassPath</c>, so that shape needed a hand-built helper.
    /// </summary>
    internal sealed record ClassTerminal(string Name, string? ClassPath);

    internal sealed record MethodRequest(string Vi, string? Aixml,
                                         List<ClassTerminal> ClassTerminals,
                                         List<string>? DispatchTerminals,
                                         List<int>? DispatchTerminalIndices)
    {
        public static List<MethodRequest> ParseAll(string json)
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"methodsJson is not JSON: {ex.Message}. It is a JSON ARRAY, e.g. " +
                    "[{\"aixml\":\"C:\\\\x\\\\Initialize.xml\",\"vi\":\"C:\\\\cls\\\\Initialize.vi\"," +
                    "\"classTerminals\":[\"obj in\",\"obj out\"]}].");
            }

            if (parsed is not JsonArray array || array.Count == 0)
                throw new ArgumentException("methodsJson must be a non-empty JSON array of objects.");

            var all = new List<MethodRequest>();
            foreach (var element in array)
            {
                if (element is not JsonObject o)
                    throw new ArgumentException("Every entry in methodsJson must be an object.");

                var vi = o["vi"]?.GetValue<string>();
                var aixml = o["aixml"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(vi))
                {
                    if (string.IsNullOrWhiteSpace(aixml))
                        throw new ArgumentException("Every method needs a \"vi\" path.");
                    vi = Path.ChangeExtension(aixml, ".vi");
                }

                var terminals = Terminals(o["classTerminals"], Path.GetFileName(vi));
                if (terminals.Count == 0)
                    throw new ArgumentException(
                        $"'{Path.GetFileName(vi)}' names no \"classTerminals\". These are the pane " +
                        "terminals to retype to the class, e.g. [\"obj in\",\"obj out\"] - without " +
                        "them the VI is not a class method at all.");

                var dispatchNames = o["dispatchTerminals"] is null ? null : Strings(o["dispatchTerminals"]);
                List<int>? dispatchIndices = null;
                if (o["dispatchTerminalIndices"] is JsonArray raw)
                {
                    dispatchIndices = [];
                    foreach (var n in raw)
                    {
                        if (n?.GetValueKind() is not JsonValueKind.Number)
                            throw new ArgumentException(
                                "dispatchTerminalIndices holds conIdx NUMBERS. For names, use " +
                                "\"dispatchTerminals\".");
                        dispatchIndices.Add(n.GetValue<int>());
                    }
                }

                all.Add(new MethodRequest(Path.GetFullPath(vi),
                                          string.IsNullOrWhiteSpace(aixml) ? null : Path.GetFullPath(aixml),
                                          terminals, dispatchNames, dispatchIndices));
            }

            if (all.Select(m => m.Vi).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Count)
                throw new ArgumentException("Two methods name the same .vi path.");

            return all;
        }

        private static List<string> Strings(JsonNode? node) =>
            node is not JsonArray array
                ? []
                : [.. array.Where(n => n is not null).Select(n => n!.GetValue<string>())];

        /// <summary>
        /// A terminal is either a bare NAME - retyped to the method's own class, the common case -
        /// or an object naming the class it should carry instead:
        /// <c>{"terminal":"Engine in","class":"C:\Vehicle\Engine\Engine.lvclass"}</c>.
        /// </summary>
        private static List<ClassTerminal> Terminals(JsonNode? node, string vi)
        {
            if (node is not JsonArray array) return [];

            var all = new List<ClassTerminal>();
            foreach (var entry in array)
            {
                switch (entry)
                {
                    case null:
                        continue;
                    case JsonValue value when value.GetValueKind() is JsonValueKind.String:
                        all.Add(new ClassTerminal(value.GetValue<string>(), null));
                        continue;
                    case JsonObject o:
                        var name = o["terminal"]?.GetValue<string>() ?? o["name"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name))
                            throw new ArgumentException(
                                $"A classTerminals entry of '{vi}' is an object with no " +
                                "\"terminal\". Write either \"obj in\" or " +
                                "{\"terminal\":\"Engine in\",\"class\":\"...\\Engine.lvclass\"}.");
                        var cls = o["class"]?.GetValue<string>() ?? o["lvclass"]?.GetValue<string>();
                        all.Add(new ClassTerminal(name,
                            string.IsNullOrWhiteSpace(cls) ? null : Path.GetFullPath(cls)));
                        continue;
                    default:
                        throw new ArgumentException(
                            $"A classTerminals entry of '{vi}' is neither a name nor an object.");
                }
            }
            return all;
        }
    }
}
