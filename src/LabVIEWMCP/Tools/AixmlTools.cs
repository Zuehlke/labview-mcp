using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The AIXML round-trip: LabVIEW's textual representation of a block diagram.
/// VI -> XML is read-only; XML -> VI and apply-to-VI create or modify real code on disk.
///
/// The format has no published schema (no XSD ships with the addon), so the practical
/// way to author it is: convert a similar VI first, study the output, then modify it.
/// Nodes carry a 'uid' and wires are expressed as "terminal:uid.terminal" references in
/// the inputs/outputs attributes.
/// </summary>
[McpServerToolType]
internal sealed class AixmlTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_convert_vi_to_aixml", ReadOnly = true,
                   Title = "Convert a VI to AIXML")]
    [Description("""
        RPC ConvertVIToAIXML. Serializes an existing VI into LabVIEW's AIXML text format and
        writes it to aiXmlFilePath. Does NOT modify the VI. With returnContent the XML is also
        returned inline, which is normally what you want when reading code.
        This is the reference path for learning the AIXML dialect before generating any.
        CACHED ON DISK, but only for VIs that belong to the LabVIEW INSTALLATION - the examples
        tree, vi.lib, user.lib and every LVAddon, which is to say exactly the example and palette
        VIs you read to learn from. MEASURED over 1677 of them: median 331 ms per export, p99 24 s,
        worst case 93 s, and the cost is loading the VI rather than writing the XML (size and
        duration correlate at r = 0.002). A second read of the same VI is a file copy, and needs no
        running LabVIEW at all.
        YOUR OWN CODE IS NEVER CACHED and needs no flag for that: an export depends on a VI's
        subVIs too, and user code changes one subVI at a time behind a caller whose own timestamp
        never moves. Every answer carries fromCache and a one-line cacheNote, so which of the two
        happened is visible rather than guessed. Pass refresh to re-export a cached VI anyway.
        """)]
    public async Task<string> ConvertViToAixmlAsync(
        [Description(@"Absolute path to the source .vi")] string viPath,
        [Description(@"Absolute path of the .xml file to write")] string aiXmlFilePath,
        [Description("Also return the written XML inline")] bool returnContent = true,
        [Description("Truncate inline content to this many characters (0 = unlimited)")]
        int maxContentChars = 60000,
        [Description("Local budget in seconds")] int timeoutSeconds = 180,
        [Description("Ignore any cached export and ask LabVIEW again")] bool refresh = false,
        CancellationToken ct = default) =>
        await ConvertViToAixmlCoreAsync(viPath, aiXmlFilePath, returnContent, maxContentChars,
                                        timeoutSeconds, refresh, roots: null, ct);

    /// <summary>
    /// The body of <see cref="ConvertViToAixmlAsync"/>, with the installation roots left open.
    ///
    /// The tool method cannot carry that parameter: every argument of an [McpServerTool] method
    /// becomes part of the schema the client sees, and "which directories count as installed
    /// LabVIEW" is not a question to put to a caller. Tests pass their own tree here, so the cache
    /// behaviour is exercised on the code that ships rather than on a static someone has to
    /// remember to reset.
    /// </summary>
    internal async Task<string> ConvertViToAixmlCoreAsync(
        string viPath, string aiXmlFilePath, bool returnContent, int maxContentChars,
        int timeoutSeconds, bool refresh, IReadOnlyList<string>? roots,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // Before the connection is even touched: a hit is two file reads and a copy, so this
            // answers on a machine where LabVIEW is closed or still coming up.
            if (!refresh && AixmlExportStore.TryCopyTo(viPath, aiXmlFilePath, roots))
            {
                var taken = AixmlExportStore.CachedUtc(viPath);
                return Json.Message(
                    new ConvertVIToAIXMLResponse { ErrorCode = 0, ErrorMessage = "No Error" },
                    [.. await FileFactsAsync(aiXmlFilePath, returnContent, maxContentChars, ct),
                     ("fromCache", JsonValue.Create(true)),
                     ("cacheNote", JsonValue.Create(
                         $"served from the export cache, taken {taken:u}; pass refresh to " +
                         "re-export"))]);
            }

            var response = await connection.InvokeAsync((c, t) =>
                c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                {
                    ViPath = viPath,
                    AiXMLFilePath = aiXmlFilePath,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            // Only a clean export is worth keeping. A failed one may still have left a partial
            // file behind, and caching that would serve the failure back for as long as the VI
            // sits untouched.
            var stored = response.ErrorCode == 0 &&
                         AixmlExportStore.Save(viPath, aiXmlFilePath, roots);

            return Json.Message(response,
                [.. await FileFactsAsync(aiXmlFilePath, returnContent, maxContentChars, ct),
                 ("fromCache", JsonValue.Create(false)),
                 ("cacheNote", JsonValue.Create(
                     CacheNote(viPath, response.ErrorCode, stored, roots)))]);
        });

    /// <summary>
    /// One line saying what became of this export, so a caller never has to guess whether the
    /// cache was consulted, skipped or simply unable to help.
    /// </summary>
    private static string CacheNote(
        string viPath, int errorCode, bool stored, IReadOnlyList<string>? roots) =>
        stored ? "exported and written to the cache; the next read of this VI is a file copy"
        : errorCode != 0 ? "not cached: the export failed"
        : AixmlExportStore.IsCacheable(viPath, roots)
            ? "not cached: the entry could not be written"
            : "not cached: this VI is outside the LabVIEW installation, and an export depends on " +
              "its subVIs too - your own code is always re-exported";

    [McpServerTool(Name = "lvai_convert_vis_to_aixml", ReadOnly = true,
                   Title = "Convert several VIs to AIXML in one call")]
    [Description("""
        Several VIs through ConvertVIToAIXML in one call, written into outputDirectory. Use this
        when TRIAGING CANDIDATES - deciding which example or template is the right starting point -
        where the alternative is one tool call per candidate.
        WHAT IT DOES AND DOES NOT PARALLELISE, both measured. Cached exports are served
        CONCURRENTLY: a hit is a file copy in this process with no LabVIEW involved, and concurrent
        file reads are worth about 21x on a cold tree. Anything not cached is exported
        SEQUENTIALLY, because LabVIEW serialises the RPC anyway - six ConvertAIXMLToVI calls issued
        at once took 559 ms against 543 ms one after another, so firing them concurrently buys
        nothing and a slow one would make every other call queue behind it.
        The practical consequence: the first pass over a set of candidates costs what it costs, and
        every later pass is nearly free. Caching rules are those of lvai_convert_vi_to_aixml -
        installation VIs only, never your own code.
        Content is NOT returned inline by default: a batch of full exports is large, and the point
        here is to get the files onto disk cheaply so you can read the ones worth reading. Each
        answer gives that VI's xmlPath.
        """)]
    public async Task<string> ConvertVisToAixmlAsync(
        [Description("Absolute .vi paths, ONE PER LINE")] string viPaths,
        [Description(@"Absolute path of the directory to write the .xml files into")]
        string outputDirectory,
        [Description("Also return each export inline")] bool returnContent = false,
        [Description("Truncate each inline export to this many characters (0 = unlimited)")]
        int maxContentChars = 20000,
        [Description("Local budget in seconds, per VI")] int timeoutSeconds = 180,
        [Description("Ignore cached exports and ask LabVIEW again")] bool refresh = false,
        CancellationToken ct = default) =>
        await ConvertVisToAixmlCoreAsync(viPaths, outputDirectory, returnContent, maxContentChars,
                                         timeoutSeconds, refresh, roots: null, ct);

    /// <summary>The body of <see cref="ConvertVisToAixmlAsync"/>; see the single-VI core for why
    /// the installation roots are a parameter here and not on the tool method.</summary>
    internal async Task<string> ConvertVisToAixmlCoreAsync(
        string viPaths, string outputDirectory, bool returnContent, int maxContentChars,
        int timeoutSeconds, bool refresh, IReadOnlyList<string>? roots,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // NEWLINE separated, deliberately not Rpc.SplitList. That helper also splits on commas
            // and semicolons, and both are legal characters in a Windows path - a VI under
            // `C:\Data\Rev 2, final\` would arrive as two nonexistent paths and be reported as two
            // failures. A line break cannot occur in a path: Path.GetInvalidFileNameChars covers
            // the control characters.
            //
            // Duplicates collapse rather than being exported twice; the same VI named twice in one
            // request is a caller assembling a candidate list, not a request for two exports.
            var paths = viPaths
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var folder = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(folder);

            var outputs = paths.Select(vi => Path.Combine(folder, ExportName(vi))).ToList();
            var stopwatch = Stopwatch.StartNew();

            // Phase 1, CONCURRENT: everything already cached. No connection is touched, so this
            // also answers while LabVIEW is closed or still starting.
            var served = new bool[paths.Count];
            if (!refresh)
                Parallel.For(0, paths.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = ParallelScan.Degree },
                    i => served[i] = TooLong(outputs[i])
                        ? false
                        : AixmlExportStore.TryCopyTo(paths[i], outputs[i], roots));

            // Phase 2, SEQUENTIAL: the rest. LabVIEW serialises these whatever we do.
            var rows = new JsonArray();
            int cached = 0, exported = 0, failed = 0;

            for (var i = 0; i < paths.Count; i++)
            {
                if (served[i]) { cached++; rows.Add(await RowAsync(i, true, 0, "No Error")); continue; }

                if (TooLong(outputs[i]))
                {
                    failed++;
                    rows.Add(await RowAsync(i, false, -1,
                        $"Output path is {outputs[i].Length} characters. LabVIEW answers a long " +
                        "path with a generic 'Error 1 occurred at Write to Text File', so this VI " +
                        "was not attempted - pass a shorter outputDirectory."));
                    continue;
                }

                var response = await connection.InvokeAsync((c, t) =>
                    c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                    {
                        ViPath = paths[i],
                        AiXMLFilePath = outputs[i],
                    }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync,
                    ct);

                if (response.ErrorCode == 0)
                {
                    exported++;
                    AixmlExportStore.Save(paths[i], outputs[i], roots);
                }
                else failed++;

                rows.Add(await RowAsync(i, false, response.ErrorCode, response.ErrorMessage));
            }

            stopwatch.Stop();

            return Json.Document(new JsonObject
            {
                ["requested"] = paths.Count,
                ["fromCache"] = cached,
                ["exported"] = exported,
                ["failed"] = failed,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["outputDirectory"] = folder,
                ["note"] = cached > 0 && exported > 0
                    ? $"{cached} served concurrently from the cache, {exported} exported " +
                      "sequentially through LabVIEW"
                    : cached > 0
                        ? "all served concurrently from the cache; LabVIEW was not involved"
                        : "nothing was cached yet, so every export went through LabVIEW one at a " +
                          "time; a second call over the same VIs is served from disk",
                ["results"] = rows,
            });

            async Task<JsonNode> RowAsync(int i, bool fromCache, int errorCode, string errorMessage)
            {
                var written = File.Exists(outputs[i]);
                var row = new JsonObject
                {
                    ["viPath"] = paths[i],
                    ["xmlWritten"] = written,
                    ["xmlPath"] = written ? outputs[i] : null,
                    ["xmlBytes"] = written ? new FileInfo(outputs[i]).Length : 0,
                    ["fromCache"] = fromCache,
                    ["errorCode"] = errorCode,
                    ["errorMessage"] = errorMessage,
                };

                if (!returnContent || !written) return row;

                var text = await File.ReadAllTextAsync(outputs[i], ct);
                var truncated = maxContentChars > 0 && text.Length > maxContentChars;
                row["xmlTruncated"] = truncated;
                row["xml"] = truncated ? text[..maxContentChars] : text;
                return row;
            }
        });

    /// <summary>
    /// A readable file name that cannot collide. Two examples called <c>Read Data.vi</c> in
    /// different folders are different VIs, so the leaf name alone would have one overwrite the
    /// other; the hash of the full path keeps them apart while the name stays recognisable.
    /// </summary>
    internal static string ExportName(string viPath)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(viPath).ToLowerInvariant())))[..8];

        var stem = Path.GetFileNameWithoutExtension(viPath);
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');

        return $"{stem}.{hash}.xml";
    }

    /// <summary>
    /// MEASURED, and it arrives disguised: past the classic 260-character limit LabVIEW answers
    /// every export with `Error 1 occurred at Write to Text File`, which reads like a broken RPC
    /// rather than a path problem. Checked here so the answer says what is actually wrong.
    /// </summary>
    private static bool TooLong(string path) => path.Length > 250;

    [McpServerTool(Name = "lvai_check_aixml", ReadOnly = true,
                   Title = "Pre-check an AIXML file without LabVIEW")]
    [Description("""
        Checks an AIXML file for the three faults ValidateAIXML was MEASURED not to catch. Needs no
        LabVIEW, no gRPC and no licence - it is pure text analysis, so it costs milliseconds and
        works in CI. Run it before lvai_validate_aixml, not instead of it.
        WHAT IT CATCHES, all measured 2026-09-03 by putting one small VI per case through the real
        validator: a `uid_parent` naming no element - an ERROR, because LabVIEW silently reparents
        that element to the TOP-LEVEL diagram, which moves a node out of the loop it belonged in and
        reports nothing at validate, convert or run; a duplicate `uid`, which LabVIEW silently
        renumbers so the export stops matching your file; and a Ring whose default `value` is not
        among its `values`. It also reports, as INFO only, uids inside LabVIEW's reserved range.
        WHAT IT DOES NOT DO, on purpose: terminal names, types, wiring, cycles and case completeness
        are things only LabVIEW knows, and it checks them well. This is a pre-filter that moves cheap
        failures off the round trip; lvai_validate_aixml is still required.
        """)]
    public Task<string> CheckAixmlAsync(
        [Description(@"Absolute path to the .xml file to check")] string aiXmlFilePath,
        [Description("Also REPAIR what can be repaired unambiguously - a duplicate uid nothing is "
                   + "nested inside, and a uid in LabVIEW's reserved range. A dangling uid_parent "
                   + "and a bad Ring default are never repaired: the author's intent is unknown, "
                   + "and for the parent, putting it on root IS the damage LabVIEW already does")]
        bool fix = false,
        [Description("Where to write the repaired XML. Omit to overwrite aiXmlFilePath in place")]
        string? fixedPath = null,
        CancellationToken ct = default)
    {
        _ = ct;
        if (!File.Exists(aiXmlFilePath))
            return Task.FromResult(Json.Error("badArguments", $"No file at '{aiXmlFilePath}'."));

        string text;
        try { text = File.ReadAllText(aiXmlFilePath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(Json.Error("unreadable",
                $"'{aiXmlFilePath}' could not be read as text."));
        }

        if (!fix)
        {
            var plain = AixmlCheck.Summarise(AixmlCheck.Check(text));
            plain["aiXmlFilePath"] = Path.GetFullPath(aiXmlFilePath);
            return Task.FromResult(Json.Document(plain));
        }

        var repaired = AixmlCheck.Fix(text);
        var answer = AixmlCheck.Summarise(repaired.Remaining);
        answer["aiXmlFilePath"] = Path.GetFullPath(aiXmlFilePath);
        answer["repairs"] =
            new JsonArray([.. repaired.Repairs.Select(r => (JsonNode)r.ToJson())]);

        // NOTHING IS WRITTEN WHEN NOTHING WAS REPAIRED, so a clean file is never rewritten - and
        // therefore never reformatted - by asking for a repair that turned out unnecessary.
        if (repaired.Repairs.Count == 0)
        {
            answer["written"] = false;
            answer["writeNote"] = "Nothing was repairable, so no file was written.";
            return Task.FromResult(Json.Document(answer));
        }

        var target = fixedPath ?? aiXmlFilePath;
        try { File.WriteAllText(target, repaired.Xml); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException
                                        or DirectoryNotFoundException)
        {
            answer["written"] = false;
            answer["writeNote"] = $"Repaired in memory but could not write '{target}': "
                                  + failure.Message;
            return Task.FromResult(Json.Document(answer));
        }

        answer["written"] = true;
        answer["writtenTo"] = Path.GetFullPath(target);
        answer["writeNote"] = "Re-read the file before editing it further: repairing renumbers "
                            + "elements, so uids in your notes may be stale. No wire name changed - "
                            + "a wire name is a token, not a reference to a uid.";
        return Task.FromResult(Json.Document(answer));
    }

    /// <summary>
    /// The path to hand to LabVIEW, repaired if it needed and could be repaired. A repair goes to a
    /// COPY in TEMP and never touches the caller's file - the same discipline
    /// <see cref="SymbolicUids"/> uses, and for the same reason: this sits between an author and
    /// code generation, where a surprise edit is worse than a reported fault.
    /// </summary>
    internal static (string Path, JsonObject? Report) Repaired(string aixmlPath)
    {
        string text;
        try { text = File.ReadAllText(aixmlPath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            return (aixmlPath, null);
        }

        var fixedUp = AixmlCheck.Fix(text);
        if (fixedUp.Repairs.Count == 0) return (aixmlPath, null);

        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "repaired");
        var target = Path.Combine(directory, Path.GetFileName(aixmlPath));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(target, fixedUp.Xml);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            return (aixmlPath, null);   // generate from the original rather than not at all
        }

        return (target, new JsonObject
        {
            ["repaired"] = true,
            ["repairs"] = new JsonArray([.. fixedUp.Repairs.Select(r => (JsonNode)r.ToJson())]),
            ["generatedFrom"] = target,
            ["note"] = "Your file was NOT modified. The faults above are ones LabVIEW accepts "
                     + "silently, so they were repaired into a copy and the VI was generated from "
                     + "that. Apply them to your source with lvai_check_aixml fix:true if you want "
                     + "the two to match.",
        });
    }

    /// <summary>
    /// The pre-check as a sub-answer for the RPC tools. Returns null when the file cannot be read,
    /// which is deliberately NOT an error here: the RPC below will report a missing or unreadable
    /// file in the vocabulary the caller already knows.
    /// </summary>
    internal static JsonObject? PreCheck(string aixmlPath)
    {
        string text;
        try { text = File.ReadAllText(aixmlPath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException) { return null; }

        return AixmlCheck.Summarise(AixmlCheck.Check(text));
    }

    /// <summary>
    /// The pre-check reduced to what belongs in another tool's answer: nothing at all when the file
    /// is clean, so a passing call is not made noisier by a check that found nothing.
    /// </summary>
    internal static JsonNode? PreCheckIfInteresting(string aixmlPath)
    {
        var answer = PreCheck(aixmlPath);
        if (answer is null) return null;
        var interesting = answer["errors"]?.GetValue<int>() > 0
                          || answer["warnings"]?.GetValue<int>() > 0;
        return interesting ? answer : null;
    }

    [McpServerTool(Name = "lvai_validate_aixml", ReadOnly = true, Title = "Validate an AIXML file")]
    [Description("""
        RPC ValidateAIXML. Asks LabVIEW whether an AIXML file is well-formed and semantically
        acceptable, WITHOUT creating anything. Always run this before lvai_convert_aixml_to_vi
        or lvai_apply_aixml_to_vi - it is the cheap failure path.
        Reading the messages: "Unsupported SubVI: X" means the Call target cannot be resolved
        (project-local subVIs and Express VIs never can); "Object terminal not found for
        input" means a misspelled terminal name, or fallout from such a Call.
        lvai_aixml_reference has the authoring rules and a verified terminal-name table.
        """)]
    public async Task<string> ValidateAixmlAsync(
        [Description(@"Absolute path to the .xml file to validate")] string aiXmlFilePath,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // Timed for the same reason the batch export is: without it the only way to find out
            // what a step costs is to bracket the call from outside, which in an agent loop
            // measures model latency - about 7 s per turn - rather than LabVIEW. Measured that
            // way, three calls read 30.4 s while the work was well under a second.
            var symbolic = SymbolicUids.Prepare(aiXmlFilePath);

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.ValidateAIXMLAsync(
                    new ValidateAIXMLRequest { AiXMLFilePath = symbolic.PathForLabview },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            response.ErrorMessage = SymbolicUids.Annotate(response.ErrorMessage, symbolic.Map);
            return Json.Message(response,
                [.. SymbolicFacts(symbolic),
                 // Reported, never blocking: these are faults LabVIEW accepts, so a caller who
                 // wants them accepted is not stopped. It is here because `errorCode 0` from this
                 // RPC reads as "the file is fine", and for a dangling uid_parent it is not.
                 ("preCheck", PreCheckIfInteresting(aiXmlFilePath)),
                 ("elapsedMs", JsonValue.Create(stopwatch.ElapsedMilliseconds))]);
        });

    [McpServerTool(Name = "lvai_convert_aixml_to_vi", Destructive = true, OpenWorld = true,
                   Title = "Create a VI from AIXML (writes a .vi)")]
    [Description("""
        RPC ConvertAIXMLToVI. MUTATING: creates a real .vi file at viPath from an AIXML file,
        overwriting whatever is there. This is LabVIEW code generation.
        With openVI the new VI is also opened in the IDE.
        Validate the XML first (lvai_validate_aixml) and write to a scratch path until the
        output is what you expect.
        BEFORE authoring AIXML call lvai_aixml_reference - the format has no published schema
        and two rules fail silently: a `uid.terminal` string names a NET (wire), not a
        pointer to an element, and fan-out is expressed by repeating that net string; and
        terminal names are literal LabVIEW labels that must be looked up, not guessed
        (`Increment` -> `x+1`, but `Greater?` -> `x > y?` with spaces).
        The generated VI must be self-contained: a Call to a project-local subVI is rejected.
        """)]
    public async Task<string> ConvertAixmlToViAsync(
        [Description(@"Absolute path to the source AIXML .xml file")] string aiXmlFilePath,
        [Description(@"Absolute path of the .vi to create - WILL BE OVERWRITTEN")] string viPath,
        [Description("Open the created VI in the LabVIEW editor")] bool openVI = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 240,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var existedBefore = File.Exists(viPath);

            // This is the number people actually ask for - "how long does generating a VI take" -
            // and it is not obtainable from outside the server: bracketing the call in an agent
            // loop measures the turn, not the generation.
            var symbolic = SymbolicUids.Prepare(aiXmlFilePath);

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
                {
                    AiXMLFilePath = symbolic.PathForLabview,
                    ViPath = viPath,
                    OpenVI = openVI,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            response.ErrorMessage = SymbolicUids.Annotate(response.ErrorMessage, symbolic.Map);
            return Json.Message(response,
                [.. SymbolicFacts(symbolic),
                 ("viPath", JsonValue.Create(Path.GetFullPath(viPath))),
                 ("viExisted", JsonValue.Create(existedBefore)),
                 ("viExistsNow", JsonValue.Create(File.Exists(viPath))),
                 ("viBytes", JsonValue.Create(File.Exists(viPath) ? new FileInfo(viPath).Length : 0)),
                 // THIS PATH MATTERS MOST for the check: it is the documented route for a class
                 // method - convert WITHOUT validating, because ValidateAIXML is stricter than the
                 // converter for a class wire - so nothing else looks at the file at all.
                 ("preCheck", PreCheckIfInteresting(aiXmlFilePath)),
                 ("elapsedMs", JsonValue.Create(stopwatch.ElapsedMilliseconds))]);
        });

    [McpServerTool(Name = "lvai_apply_aixml_to_vi", Destructive = true, OpenWorld = true,
                   Title = "Apply AIXML to an existing VI (modifies it)")]
    [Description("""
        RPC ApplyAIXMLToVI. MUTATING: applies an AIXML description onto an EXISTING VI,
        changing its block diagram. This is the RPC behind LabVIEW's AI code completion.
        There is no undo through this interface - keep a copy of the VI, or work on a copy.
        """)]
    public async Task<string> ApplyAixmlToViAsync(
        [Description(@"Absolute path to the .vi to modify")] string viPath,
        [Description(@"Absolute path to the AIXML .xml describing the change")] string aiXmlFilePath,
        [Description("Local budget in seconds")] int timeoutSeconds = 240,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var before = File.Exists(viPath) ? new FileInfo(viPath).Length : 0;
            var symbolic = SymbolicUids.Prepare(aiXmlFilePath);
            var response = await connection.InvokeAsync((c, t) =>
                c.ApplyAIXMLToVIAsync(new ApplyAIXMLToVIRequest
                {
                    ViPath = viPath,
                    AiXMLFilePath = symbolic.PathForLabview,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            response.ErrorMessage = SymbolicUids.Annotate(response.ErrorMessage, symbolic.Map);
            return Json.Message(response,
                [.. SymbolicFacts(symbolic),
                 ("viBytesBefore", JsonValue.Create(before)),
                 ("viBytesAfter", JsonValue.Create(
                     File.Exists(viPath) ? new FileInfo(viPath).Length : 0)),
                 ("note", JsonValue.Create(
                     "A byte size that did not change may simply mean LabVIEW has the VI open " +
                     "in memory and has not saved it yet."))]);
        });

    /// <summary>
    /// What to add to a response when the source used symbolic uids - nothing at all when it did
    /// not, so an ordinary numbered file's answer keeps exactly the shape it always had.
    /// </summary>
    private static (string, JsonNode?)[] SymbolicFacts(SymbolicUids.Result symbolic)
    {
        if (!symbolic.Rewritten) return [];

        var map = new JsonObject();
        foreach (var pair in symbolic.Map) map[pair.Key] = JsonValue.Create(pair.Value);

        return [
            ("symbolicUids", map),
            ("aiXmlSentToLabview", JsonValue.Create(symbolic.PathForLabview)),
            ("symbolicNote", JsonValue.Create(
                "This source used symbolic uids; they were numbered before LabVIEW saw it. A " +
                "message naming a number refers to the file at aiXmlSentToLabview, and " +
                "symbolicUids gives the symbol each number came from.")),
        ];
    }

    private static async Task<(string, JsonNode?)[]> FileFactsAsync(
        string path, bool includeContent, int maxChars, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [("xmlWritten", JsonValue.Create(false))];

        var info = new FileInfo(path);
        var facts = new List<(string, JsonNode?)>
        {
            ("xmlWritten", JsonValue.Create(true)),
            ("xmlPath", JsonValue.Create(info.FullName)),
            ("xmlBytes", JsonValue.Create(info.Length)),
        };

        if (includeContent)
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var truncated = maxChars > 0 && text.Length > maxChars;
            facts.Add(("xmlTruncated", JsonValue.Create(truncated)));
            facts.Add(("xml", JsonValue.Create(truncated ? text[..maxChars] : text)));
        }

        return [.. facts];
    }
}
