using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Giving a placeholder stub the SUBJECT'S REAL TYPES, with every typedef link removed at every
/// depth.
///
/// WHY A STUB NEEDS THIS AT ALL. <see cref="PlaceholderTools"/> clones the subject's pane out of its
/// AIXML export, and AIXML has no typedef in its grammar - so the stub already comes out link-free,
/// measured, and what it loses is FIDELITY: it gets AIXML's *approximation* of the type. Where the
/// approximation is exact nothing here is needed. Where it is not, this installs the real thing.
///
/// THE MECHANISM IS THE FLAG, NOT A DISCONNECT. A typedef is a `.ctl` whose save record carries
/// <c>TypeDefVI</c>; clearing that bit makes it an ordinary custom control, and a
/// <c>{LV.Control} Replace</c> against a non-typedef installs the type and produces NO typedef link
/// - which <see cref="CtlTools"/> already says in the opposite direction, as the trap that makes a
/// failed bind look like a successful one. Clearing the flag is a pylabview edit and needs no
/// LabVIEW at all.
///
/// <c>Discon Typedef</c> IS NOT USED AND MUST NOT BE. Measured 2026-09-18, five runs across three
/// probe shapes and two target files: <c>{LV.Control} Discon Typedef</c> from a helper opened
/// without an application instance TERMINATES LabVIEW, leaves nothing in NI's own crash log, and
/// never writes the target. The same helper shape with <c>Replace</c> in the mutate slot survives.
/// `docs/typedef-disconnect.md` has the table and the three hypotheses it refutes.
///
/// THE RECURSION IS MANDATORY, and that is the whole reason this is more than one Replace.
/// Clearing the flag on the outer `.ctl` disconnects the outer level only: measured as an A/B on
/// one stub, a typedef embedded in the outer typedef's own definition travelled straight through,
/// arriving in the stub as a live <c>class="typeDef"</c> heap object naming the inner `.ctl`. So
/// every `.ctl` of the chain is flattened and rebound from the INSIDE OUT.
///
/// THE VERDICT COMES FROM THE FILE. A flattened `.ctl` keeps STALE <c>Type="TypeDef"</c> entries in
/// its own VCTP type pool that reach nothing, so counting those gives a false alarm; the honest
/// count is <c>class="typeDef"</c> objects in the front panel heap, and this call reads it back off
/// the saved stub rather than trusting any helper's answer.
/// </summary>
[McpServerToolType]
internal sealed class TypedefFlattenTools(LvaiConnection connection)
{
    internal const string TreeHelperFileName = "lvai_typedef_tree.xml";
    internal const string BindHelperFileName = "lvai_typedef_bind.xml";
    internal const string DisconHelperFileName = "lvai_typedef_discon.xml";

    /// <summary>
    /// The two measured ways to get a typedef link off a control. They differ in what they need,
    /// not in what they achieve - both end with the subject's real type and no link.
    ///
    /// <c>Flag</c> copies each `.ctl` of the chain, clears its <c>TypeDefVI</c> bit with pylabview
    /// and installs the copy. It needs NOTHING of the caller: no project, no editor, no IDE
    /// application instance, and it has no way to end the LabVIEW session.
    ///
    /// <c>Disconnect</c> installs the ORIGINAL `.ctl` and then calls
    /// <c>{LV.Control} Discon Typedef</c>. It copies nothing and is faster, and on a CLASS terminal
    /// LabVIEW refuses it itself (<c>Error 1088</c>) rather than needing a guard. It needs a project
    /// open and active AND the VI open in the editor, and missing either ends the LabVIEW session
    /// with nothing in NI's crash log - measured five times.
    /// </summary>
    internal enum FlattenRoute { Flag, Disconnect }

    /// <summary>
    /// How deep the chain may nest before this gives up. A typedef cannot contain itself, so this
    /// is not a cycle guard - it bounds a pathological chain rather than a loop.
    /// </summary>
    private const int MaxDepth = 8;

    [McpServerTool(Name = "lvai_flatten_typedefs", Destructive = true, OpenWorld = true,
                   Title = "Give a stub the subject's real types with no typedef links")]
    [Description("""
        MUTATING: installs the SUBJECT's real terminal types onto a placeholder stub, with every
        typedef link removed at every depth, and verifies it from the saved file.
        WHEN YOU NEED IT: after lvai_placeholder_subvi, when the subject's pane carries typedefs and
        the stub's AIXML-approximated type is not good enough. lvai_placeholder_subvi calls this for
        you unless you pass flattenTypedefs false, so a direct call is for repairing a stub that
        already exists.
        HOW IT WORKS, and it is NOT `Discon Typedef`. A typedef is a `.ctl` with TypeDefVI set in
        its save record. Each `.ctl` of the chain is copied into a cache directory, that bit is
        cleared with pylabview - no LabVIEW - and the copy is then installed with
        {LV.Control} Replace, which against a NON-typedef installs the type and creates no link.
        THE COPIES DO NOT GO BESIDE THE STUB. Measured 2026-09-18 as an A/B seconds apart in one
        LabVIEW: a Replace whose SOURCE .ctl sits under user.lib answers error 1154, and the same
        content outside the installation answers 0. Nothing is lost by moving them out - the copy
        is not a typedef, so the Replace leaves no link and the stub does not reference it.
        MEASURED 2026-09-18: {LV.Control} Discon Typedef from a generated helper KILLS LabVIEW, five
        times, with nothing in NI's crash log and the target never written. Do not reach for it.
        THE RECURSION IS THE POINT. Clearing the flag on the outer `.ctl` alone leaves a typedef
        embedded in its definition, which then arrives in the stub as a live typeDef heap object -
        measured as an A/B. So the chain is flattened and rebound from the inside out.
        THE VERDICT IS `typedefObjectsInStub`, read back off the SAVED stub: 0 is the pass. It
        counts `class="typeDef"` objects in the front panel heap, NOT `Type="TypeDef"` entries in
        the type pool - a flattened `.ctl` keeps stale pool entries that reach nothing.
        AN ARRAY IS WALKED INTO as well, since 2026-09-18: `Controls[]` is not declared on
        {LV.Array}, so the helper reads `Array Element` beside it and picks on `Class Name`, and an
        array is then a container with exactly one child at index 0. Measured on an array of a
        typedef enum, which had been named in `notDescended` and is now flattened like any other.
        The flattened copies are CACHED under a hash of the source's CONTENT, so a second stub over
        the same typedef costs one Replace and editing the typedef produces a different copy rather
        than a stale hit.
        A CHILD WHOSE PROPERTY READ FAILED IS REPORTED, NOT SKIPPED. The tree helper returns a read
        code per control, because only the last iteration's error could reach its error out: a
        control that failed came back as a plausible row with an empty path while the VI answered 0,
        and keying on the empty path skipped it in silence. Measured 2026-09-18.
        `subjectTypedefObjects` IS THE SUBJECT ASKED AGAIN FROM ITS FILE, with no LabVIEW involved,
        and it exists to kill a GREEN NO-OP. Everything else here comes through VI Server, and a
        LabVIEW that has been through many open/Replace/Discon cycles serves a STALE in-memory copy
        whose typedef links are already gone - the walk then finds nothing and the call reports
        success for having nothing to do. A file that holds typedef objects while the walk found NONE
        is now `errorKind: subjectTypedefsNotSeen`, not an ok.
        THE TWO COUNTS ARE IN DIFFERENT UNITS and only that one comparison is a verdict:
        `typedefSites` counts the OUTERMOST typedef per branch, `subjectTypedefObjects` every
        instance at every depth, so 1 against 2 is the ordinary shape of a typedef inside a typedef.
        """)]
    public async Task<string> FlattenTypedefsAsync(
        [Description("Absolute path to the stub whose terminals should carry the real types")]
        string viPath,
        [Description("""
            Absolute path to the SUBJECT - the VI whose pane the stub was cloned from, and the only
            place the typedef identities still exist. The stub itself carries none, which is exactly
            why it cannot be its own source.
            """)]
        string subjectPath,
        [Description("""
            Which measured route to use, "flag" (the default) or "disconnect". They reach the same
            end - the subject's real type with no link at any depth - and differ in what they need.
            "flag" copies each `.ctl` of the chain and clears its TypeDefVI bit with pylabview. It
            needs NO project, NO editor and NO IDE application instance, and it cannot end the
            LabVIEW session. It writes copies into a cache.
            "disconnect" installs the ORIGINAL `.ctl` and then calls {LV.Control} Discon Typedef. It
            copies nothing, is faster, and on a CLASS terminal LabVIEW refuses it itself with Error
            1088 instead of needing a guard of ours. It REQUIRES a project open and active AND the
            stub open in the editor - this call opens it for you - and missing either kills LabVIEW
            with nothing in NI's crash log, measured five times.
            THE COST OF "disconnect" IS THAT IT OPENS THE STUB IN THE EDITOR, which puts its path in
            LabVIEW's memory - so a later regeneration of that stub can answer Error 1357 until the
            VI is closed or LabVIEW restarts. The answer says so in `stubOpenedInEditor`.
            """)]
        string route = "flag",
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");
            if (!File.Exists(subjectPath))
                return Json.Error("badArguments", $"No file at subjectPath '{subjectPath}'.");
            if (ParseRoute(route) is not { } parsed)
                return Json.Error("badArguments",
                    $"route '{route}' is not one of \"flag\" or \"disconnect\".");

            var answer = await FlattenIntoAsync(viPath, subjectPath, parsed, timeoutSeconds, ct);
            return Json.Document(answer);
        });

    /// <summary>
    /// The route name as data. Refused by name rather than folded onto a default, because silently
    /// running the other route is the difference between needing a project and not.
    /// </summary>
    internal static FlattenRoute? ParseRoute(string? route) => route?.Trim().ToLowerInvariant() switch
    {
        null or "" or "flag" => FlattenRoute.Flag,
        "disconnect" => FlattenRoute.Disconnect,
        _ => null,
    };

    /// <summary>
    /// The engine, shared with <see cref="PlaceholderTools"/>. Never throws for a LabVIEW-side
    /// failure: it reports one, because a stub whose types could not be improved is still a usable
    /// stub and turning that into a hard failure would lose the placeholder the caller asked for.
    /// </summary>
    internal async Task<JsonObject> FlattenIntoAsync(
        string stubPath, string subjectPath, FlattenRoute route, int timeoutSeconds,
        CancellationToken ct)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var answer = new JsonObject
        {
            ["stub"] = stubPath,
            ["subject"] = subjectPath,
            ["route"] = route == FlattenRoute.Disconnect ? "disconnect" : "flag",
        };

        var folder = FlatCopyFolder();

        var helpers = route == FlattenRoute.Disconnect
            ? new[] { TreeHelperFileName, BindHelperFileName, DisconHelperFileName }
            : [TreeHelperFileName, BindHelperFileName];
        if (await EnsureHelpersAsync(helpers, timeoutSeconds, ct) is { } helperProblem)
            return Fail(answer, "helperUnavailable", helperProblem, elapsed);

        var subjectChildren = await ListAsync(subjectPath, "", timeoutSeconds, ct);
        if (subjectChildren is null)
            return Fail(answer, "subjectUnreadable",
                $"The control tree of '{subjectPath}' could not be read, so which terminals carry " +
                "typedefs is unknown. Nothing was changed.", elapsed);

        var skipped = new JsonArray();
        var sites = await CollectSitesAsync(
            subjectPath, subjectChildren, skipped, timeoutSeconds, ct);
        if (sites.Problem is { } collectProblem)
            return Fail(answer, sites.Kind ?? "subjectUnreadable", collectProblem, elapsed);

        var typedefTerminals = sites.Sites;
        if (skipped.Count > 0) answer["skipped"] = skipped;
        answer["typedefSites"] = typedefTerminals.Count;
        answer["typedefTerminals"] =
            typedefTerminals.Count(s => s.IndexPath.Length == 0);

        // THE SUBJECT IS ASKED AGAIN, FROM ITS FILE, and this is the only thing standing between a
        // stale LabVIEW and a green answer that did nothing. Everything above came out of VI Server,
        // so a subject whose in-memory copy has lost its typedef links reports no sites and the call
        // then congratulates itself on having nothing to do. The count below needs no LabVIEW at all.
        var subjectObjects = await TypedefObjectsAsync(subjectPath, timeoutSeconds, ct);
        answer["subjectTypedefObjects"] = subjectObjects;
        if (subjectObjects < 0)
            answer["subjectTypedefObjectsNote"] =
                "The subject's file could not be read back, so the cross-check below did NOT run: " +
                "a walk that found nothing is unconfirmed here.";

        if (typedefTerminals.Count == 0)
        {
            // The two numbers are in DIFFERENT UNITS and only this one comparison is a verdict.
            // `typedefSites` counts the OUTERMOST typedef on each branch - the walk stops there,
            // because the routes handle what is nested inside it themselves - while the file count
            // is every typedef INSTANCE at every depth. So 1 site against 2 objects is the ordinary
            // shape of a typedef inside a typedef, and only "the file has some, the walk has none"
            // means the two disagree about whether there is anything here at all.
            if (SubjectDisagreesWithItsFile(typedefTerminals.Count, subjectObjects))
                return Fail(answer, "subjectTypedefsNotSeen",
                    $"The walk found NO typedef on '{subjectPath}', and its file holds " +
                    $"{subjectObjects} typedef object(s). Nothing was changed, and this is reported " +
                    "as a failure rather than as success-with-nothing-to-do because those two look " +
                    "identical from the outside. Two causes are known. A LabVIEW instance that has " +
                    "been through many open/Replace/Discon cycles serves a STALE in-memory copy of " +
                    "the subject in which the links are already gone - measured twice, and a cold " +
                    "instance reads them again, so restart LabVIEW and call again. Or the typedef " +
                    "sits inside a container this walk does not enter: clusters and arrays are " +
                    "walked, anything else is not.", elapsed);

            answer["ok"] = true;
            answer["flattened"] = new JsonArray();
            answer["note"] =
                "The subject's pane carries no typedef terminals, so the stub's cloned types are " +
                "already what the subject has. Nothing was changed - and the subject's FILE agrees " +
                "there was nothing to do, which is what separates this from a stale LabVIEW.";
            answer["elapsedMs"] = elapsed.ElapsedMilliseconds;
            return answer;
        }

        // The stub's own controls, so a terminal is matched by LABEL rather than by position. The
        // orders do line up today - the stub is cloned from the subject's export in pane order -
        // but a positional match would fail silently the day they stop lining up, and this repo has
        // paid for that shape before with {LV.Diagram} SubVIs[] re-ordering after a Replace.
        var stubChildren = await ListAsync(stubPath, "", timeoutSeconds, ct);
        if (stubChildren is null)
            return Fail(answer, "stubUnreadable",
                $"The control tree of '{stubPath}' could not be read. Nothing was changed.",
                elapsed);

        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notDescended = new JsonArray();
        var flattened = new JsonArray();
        var failures = 0;

        if (route == FlattenRoute.Disconnect)
            return await DisconnectRouteAsync(
                stubPath, typedefTerminals, stubChildren, answer, skipped, elapsed,
                timeoutSeconds, ct);

        foreach (var terminal in typedefTerminals)
        {
            var entry = new JsonObject
            {
                ["terminal"] = terminal.RootLabel,
                ["control"] = terminal.Label,
                ["indexPath"] = terminal.IndexPath,
                ["typedef"] = terminal.TypedefPath,
            };

            if (StubSiteFor(terminal, stubChildren) is not { } site)
            {
                entry["ok"] = false;
                entry["problem"] = "notOnStub";
                entry["detail"] =
                    $"The stub has no terminal labelled '{terminal.RootLabel}', so there is nothing " +
                    "to install the type onto. The stub and the subject have drifted apart - " +
                    "regenerate it with lvai_placeholder_subvi refresh.";
                failures++;
                flattened.Add(entry);
                continue;
            }

            var flat = await FlattenCtlAsync(
                terminal.TypedefPath, folder, cache, notDescended, 0, timeoutSeconds, ct);
            if (flat.Path is null)
            {
                entry["ok"] = false;
                entry["problem"] = flat.Problem ?? "flattenFailed";
                entry["detail"] = flat.Detail;
                failures++;
                flattened.Add(entry);
                continue;
            }

            entry["flatCopy"] = flat.Path;
            entry["reusedFromCache"] = flat.Reused;

            if (await BindAsync(stubPath, site.IndexPath, site.Index, flat.Path, timeoutSeconds, ct)
                is { } bindProblem)
            {
                entry["ok"] = false;
                entry["problem"] = "bindFailed";
                entry["detail"] = bindProblem;
                failures++;
            }
            else
            {
                entry["ok"] = true;
            }

            flattened.Add(entry);
        }

        answer["flattened"] = flattened;
        if (notDescended.Count > 0) answer["notDescended"] = notDescended;

        // The only check that counts, and it reads the SAVED file. Every step above reported
        // through a helper's error cluster, and this repository's standing lesson is that a green
        // session reading says nothing about what reached disk.
        var remaining = await TypedefObjectsAsync(stubPath, timeoutSeconds, ct);
        answer["typedefObjectsInStub"] = remaining;
        answer["ok"] = failures == 0 && remaining == 0;
        answer["elapsedMs"] = elapsed.ElapsedMilliseconds;
        answer["note"] = remaining switch
        {
            0 when failures == 0 =>
                "Every typedef terminal now carries the subject's real type and the saved stub " +
                "holds no typeDef object at any depth.",
            0 =>
                $"{failures} terminal(s) could not be flattened, but the saved stub holds no " +
                "typeDef object - the ones that failed kept their AIXML-approximated type.",
            < 0 =>
                "The stub could not be read back, so whether a typedef survives is UNKNOWN. " +
                "Check with pylv_extract and count class=\"typeDef\" in the *_FPHb.xml file.",
            _ =>
                $"{remaining} typeDef object(s) SURVIVE in the saved stub. Something in the chain " +
                "was not flattened - check `notDescended` for a container the walk could not " +
                "enter; clusters and arrays are both walked, anything else is not.",
        };
        return answer;
    }

    /// <summary>
    /// The disconnect route: install the ORIGINAL `.ctl` on each typedef terminal, open the stub in
    /// the editor, then walk what it now carries and disconnect every typedef, outermost first.
    ///
    /// NOTHING IS COPIED. The `Replace` brings the real type in WITH its links, nested ones
    /// included, and the disconnects then take the links off again - which is why this needs no
    /// cache and no `.ctl` of its own.
    ///
    /// THE EDITOR OPEN IS A PRECONDITION, NOT A CONVENIENCE. Measured 2026-09-18 on one file in one
    /// session: `Discon Typedef` through the IDE's application instance on a VI that was NOT open in
    /// the editor ended LabVIEW, and the same call on the same file after <c>lvai_open_file</c>
    /// answered 0. NI's own Quick Drop shortcut has the same constraint - it acts on controls
    /// selected in an open VI.
    ///
    /// IT IS NOT RECURSIVE, so the walk addresses each level itself. Disconnecting the outer control
    /// leaves one embedded in its definition linked - measured twice.
    /// </summary>
    private async Task<JsonObject> DisconnectRouteAsync(
        string stubPath, List<TypedefSite> typedefTerminals, IReadOnlyList<Child> stubChildren,
        JsonObject answer, JsonArray skipped, System.Diagnostics.Stopwatch elapsed,
        int timeoutSeconds, CancellationToken ct)
    {
        var installed = new JsonArray();
        var failures = 0;

        foreach (var terminal in typedefTerminals)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new JsonObject
            {
                ["terminal"] = terminal.RootLabel,
                ["control"] = terminal.Label,
                ["indexPath"] = terminal.IndexPath,
                ["typedef"] = terminal.TypedefPath,
            };

            if (StubSiteFor(terminal, stubChildren) is not { } site)
            {
                entry["ok"] = false;
                entry["problem"] = "notOnStub";
                entry["detail"] =
                    $"The stub has no terminal labelled '{terminal.RootLabel}'. It and the subject " +
                    "have drifted apart - regenerate it with lvai_placeholder_subvi refresh.";
                failures++;
            }
            else if (await BindAsync(stubPath, site.IndexPath, site.Index, terminal.TypedefPath,
                                     timeoutSeconds, ct) is { } problem)
            {
                entry["ok"] = false;
                entry["problem"] = "installFailed";
                entry["detail"] = problem;
                failures++;
            }
            else
            {
                entry["ok"] = true;
            }

            installed.Add(entry);
        }

        answer["installed"] = installed;
        if (skipped.Count > 0) answer["skipped"] = skipped;

        var open = Read(await new ActionTools(connection).OpenFileAsync(
            viPath: stubPath, viName: Path.GetFileName(stubPath), projectPath: null,
            projectName: null, checkActive: false, timeoutSeconds: timeoutSeconds, ct: ct));
        var opened = open?["errorCode"]?.GetValue<int>() is 0;
        answer["stubOpenedInEditor"] = opened;
        answer["stubOpenedNote"] =
            "This route opens the stub in the LabVIEW editor, because Discon Typedef on a VI that " +
            "is not open there ends the session. The cost is that the stub's path is now in " +
            "LabVIEW's memory, so regenerating THIS stub can answer Error 1357 until it is closed " +
            "or LabVIEW restarts. The flag route does not open anything.";

        if (!opened)
            return Fail(answer, "stubNotOpened",
                "The stub could not be opened in the editor, and Discon Typedef on a VI that is " +
                "not open there has been measured ending the LabVIEW session - so nothing was " +
                "disconnected. The open answer follows: " + (open?.ToJsonString() ?? "none"),
                elapsed);

        // Breadth first, which is outermost first - the order measured to work. Disconnecting the
        // outer control does not disturb the indices of what is nested inside it.
        var disconnected = new JsonArray();
        var pending = new Queue<string>();
        pending.Enqueue("");
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var indexPath = pending.Dequeue();
            var children = await ListAsync(stubPath, indexPath, timeoutSeconds, ct);
            if (children is null)
                return Fail(answer, "stubUnreadable",
                    $"The stub's controls at index path '{indexPath}' could not be listed, so what " +
                    "it carries is unknown. Some links may already be off.", elapsed);

            foreach (var child in children)
            {
                if (child.ReadCode != 0)
                    return Fail(answer, "childUnreadable",
                        $"Reading control {child.Index} of the stub at index path '{indexPath}' " +
                        $"answered error {child.ReadCode}, so whether it carries a typedef is " +
                        "unknown and disconnecting blind is not safe here.", elapsed);

                var here = new JsonObject
                {
                    ["indexPath"] = indexPath,
                    ["index"] = child.Index,
                    ["control"] = child.Label,
                };

                // A class terminal answers Is Typedef? non-zero and LabVIEW refuses the call on it
                // with Error 1088 - correctly, because a class terminal is a UDClassInst refnum with
                // no typedef link to remove. Skipping keeps the answer readable rather than carrying
                // an error that is the right outcome.
                if (child.IsTypedef && IsClassPrivateData(child.TypedefPath))
                {
                    here["skipped"] = "classTerminal";
                    disconnected.Add(here);
                    continue;
                }

                if (child.IsTypedef && child.TypedefPath.Length > 0)
                {
                    here["typedef"] = child.TypedefPath;
                    if (await DisconAsync(stubPath, indexPath, child.Index, timeoutSeconds, ct)
                        is { } problem)
                    {
                        here["ok"] = false;
                        here["detail"] = problem;
                        failures++;
                    }
                    else
                    {
                        here["ok"] = true;
                    }

                    disconnected.Add(here);
                }

                // Descended into whether or not it was a typedef: a disconnected control keeps its
                // children, and one of them may be linked in its own right. An ARRAY is descended
                // into as well - its single element is reached through {LV.Array} Array Element
                // rather than Controls[], which the helper does for us, so from here an array is
                // just a container with exactly one child at index 0.
                if (IsDescendable(child.ClassName))
                    pending.Enqueue(
                        indexPath.Length == 0 ? child.Index.ToString() : $"{indexPath}|{child.Index}");
            }
        }

        answer["disconnected"] = disconnected;

        var remaining = await TypedefObjectsAsync(stubPath, timeoutSeconds, ct);
        answer["typedefObjectsInStub"] = remaining;
        answer["ok"] = failures == 0 && remaining == 0;
        answer["elapsedMs"] = elapsed.ElapsedMilliseconds;
        answer["note"] = remaining switch
        {
            0 when failures == 0 =>
                "Every typedef terminal carries the subject's real type and the saved stub holds no " +
                "typeDef object at any depth. Nothing was copied.",
            0 => $"{failures} step(s) failed, but the saved stub holds no typeDef object.",
            < 0 =>
                "The stub could not be read back, so whether a typedef survives is UNKNOWN.",
            _ =>
                $"{remaining} typeDef object(s) SURVIVE in the saved stub - check `disconnected` " +
                "for a step that failed. Clusters and arrays are both walked into; a container of " +
                "any other kind is not.",
        };
        return answer;
    }

    /// <summary>Disconnect one control; null on success, a sentence on failure.</summary>
    private async Task<string?> DisconAsync(
        string filePath, string indexPath, int childIndex, int timeoutSeconds, CancellationToken ct)
    {
        var inputs = new JsonObject
        {
            ["vi path"] = Path.GetFullPath(filePath),
            ["child index"] = childIndex.ToString(),
        };
        if (indexPath.Length > 0) inputs["index path"] = indexPath;

        var values = ValuesOf(await new RunTools(connection).RunViAndReadValuesAsync(
            HelperViPath(DisconHelperFileName), inputs.ToJsonString(), includeRawXml: false,
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds,
            ct: ct));
        if (values is null) return "the disconnect helper did not answer.";

        var code = values["code"]?["value"]?.GetValue<string>();
        if (code is null or "0") return null;

        var source = values["source"]?["value"]?.GetValue<string>() ?? "";
        return code is "1055"
            ? "Error 1055 - no project is ACTIVE, which is the only route to the IDE's application " +
              "instance. Open one with lvai_open_file and call again."
            : $"error {code} from {source}".Trim();
    }

    /// <summary>
    /// One place on the subject that carries a typedef, at any depth.
    /// <paramref name="RootLabel"/> is the PANE TERMINAL this sits under - the only part of the
    /// address that has to be translated for the stub.
    /// </summary>
    private sealed record TypedefSite(
        string RootLabel, string IndexPath, int Index, string Label, string TypedefPath);

    /// <summary>
    /// Every typedef on the subject, at EVERY depth - not just the pane terminals.
    ///
    /// WHY THE DEPTH MATTERS, and why this replaced a one-level read. A pane whose terminal is a
    /// PLAIN cluster that merely CONTAINS a typedef reported nothing: the entry condition looked at
    /// `Panel -> Controls[]` and the engine took its list from the subject's root children, so the
    /// whole flatten never ran and the stub silently kept AIXML's approximation. Both routes handle
    /// nesting perfectly well once the outer terminal is itself a typedef; it was the way IN that
    /// missed. Measured on `Variants Subject.vi`, whose `Plain` control is exactly that shape.
    ///
    /// IT DOES NOT DESCEND PAST A TYPEDEF. What is inside one is handled by flattening its `.ctl`
    /// (the flag route) or by the walk over the finished stub (the disconnect route), and walking it
    /// here as well would address the same control twice.
    /// </summary>
    private async Task<(List<TypedefSite> Sites, string? Problem, string? Kind)> CollectSitesAsync(
        string subjectPath, IReadOnlyList<Child> rootChildren, JsonArray skipped,
        int timeoutSeconds, CancellationToken ct)
    {
        var sites = new List<TypedefSite>();
        var pending = new Queue<(string IndexPath, string RootLabel, IReadOnlyList<Child> Children)>();
        pending.Enqueue(("", "", rootChildren));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (indexPath, inheritedRoot, children) = pending.Dequeue();

            foreach (var child in children)
            {
                if (child.ReadCode != 0)
                    return (sites,
                        $"Reading control {child.Index} of '{subjectPath}'" +
                        (indexPath.Length > 0 ? $" at index path '{indexPath}'" : "") +
                        $" answered error {child.ReadCode}, so which controls carry typedefs is not " +
                        "established. Nothing was changed - an empty path from a failed read is " +
                        "indistinguishable from a control that simply is not a typedef.",
                        "terminalUnreadable");

                var rootLabel = indexPath.Length == 0 ? child.Label : inheritedRoot;

                if (child.IsTypedef && child.TypedefPath.Length > 0)
                {
                    // A CLASS TERMINAL ANSWERS `Is Typedef?` NON-ZERO, and flattening one would be
                    // the worst thing this tool could do: it would install the class's private data
                    // as a plain CLUSTER and the terminal would stop being a class at all. Both
                    // halves of the check are applied and the skip is NAMED rather than passed over.
                    if (IsClassPrivateData(child.TypedefPath))
                    {
                        skipped.Add(new JsonObject
                        {
                            ["terminal"] = child.Label,
                            ["indexPath"] = indexPath,
                            ["typedef"] = child.TypedefPath,
                            ["reason"] =
                                "This is a LabVIEW class terminal, not a typedef anyone binds - it " +
                                "reports Is Typedef? as class private data, and it is a UDClassInst " +
                                "refnum with no typedef link to remove. Flattening it would replace " +
                                "the class with a plain cluster, so it is left alone.",
                        });
                        continue;
                    }

                    sites.Add(new TypedefSite(
                        rootLabel, indexPath, child.Index, child.Label, child.TypedefPath));
                    continue;
                }

                if (!IsDescendable(child.ClassName)) continue;

                var nextPath = indexPath.Length == 0
                    ? child.Index.ToString()
                    : $"{indexPath}|{child.Index}";
                var next = await ListAsync(subjectPath, nextPath, timeoutSeconds, ct);
                if (next is null)
                    return (sites,
                        $"The controls of '{subjectPath}' at index path '{nextPath}' could not be " +
                        "listed, so what is nested there is unknown.", "subjectUnreadable");

                pending.Enqueue((nextPath, rootLabel, next));
            }
        }

        return (sites, null, null);
    }

    /// <summary>
    /// Where a subject site sits on the STUB.
    ///
    /// Only the FIRST segment is translated, and that is deliberate: the pane terminals are matched
    /// by LABEL because their order need not agree, while everything below a terminal came out of
    /// the same AIXML type string in the same field order, so those indices carry over unchanged.
    /// </summary>
    private static (string IndexPath, int Index)? StubSiteFor(
        TypedefSite site, IReadOnlyList<Child> stubChildren)
    {
        if (IndexOnStub(stubChildren, site.RootLabel) is not { } root) return null;
        if (site.IndexPath.Length == 0) return ("", root);

        var segments = site.IndexPath.Split('|');
        segments[0] = root.ToString();
        return (string.Join("|", segments), site.Index);
    }

    /// <summary>The index of the stub control with this label, matched by NAME rather than position.</summary>
    private static int? IndexOnStub(IReadOnlyList<Child> stubChildren, string label)
    {
        for (var i = 0; i < stubChildren.Count; i++)
            if (string.Equals(stubChildren[i].Label, label, StringComparison.Ordinal))
                return i;
        return null;
    }

    /// <summary>
    /// A flattened copy of one `.ctl` in <c>LV_MCP</c>, with every typedef inside it flattened and
    /// rebound first. Cached under a hash of the SOURCE'S CONTENT, so editing the typedef produces
    /// a different copy rather than a stale hit.
    /// </summary>
    private async Task<(string? Path, bool Reused, string? Problem, string? Detail)> FlattenCtlAsync(
        string ctlPath, string folder, Dictionary<string, string> cache, JsonArray notDescended,
        int depth, int timeoutSeconds, CancellationToken ct)
    {
        if (depth > MaxDepth)
            return (null, false, "tooDeep",
                $"The typedef chain is nested more than {MaxDepth} levels at '{ctlPath}'. That is " +
                "far past anything measured, so this stops rather than looping.");

        if (!File.Exists(ctlPath))
            return (null, false, "typedefMissing",
                $"'{ctlPath}' does not exist. LabVIEW reported it as this control's Typedef:Path, " +
                "so the typedef has moved since the subject was saved.");

        if (cache.TryGetValue(ctlPath, out var already)) return (already, true, null, null);

        var flatPath = Path.Combine(folder, $"LVMCP Flat {ContentHash(ctlPath)}.ctl");
        if (File.Exists(flatPath))
        {
            cache[ctlPath] = flatPath;
            return (flatPath, true, null, null);
        }

        // THE PLAN IS READ FROM THE ORIGINAL, NOT FROM THE COPY, and that one choice is the
        // difference between this working and not.
        //
        // A copy moved out of its own directory can no longer RESOLVE the `.ctl` it embeds -
        // LabVIEW finds a nested typedef beside the file that references it - so every property
        // read on a nested control of the copy answers `Error 7, File not found`. Measured
        // 2026-09-18 against both candidate locations, which is what made the question look like a
        // choice between putting the copies beside the original or in a cache.
        //
        // It is neither. All that is needed from the nested level is WHICH `.ctl` sits at WHICH
        // index, and the original answers that with everything resolved. The Replace that follows
        // does not need the old binding to resolve either, because it is replacing that control
        // outright. So the copy is never read from, the names stay free of the original's, and the
        // cache keeps working.
        var plan = new List<(string IndexPath, int Index, string Label, string TypedefPath)>();
        var pending = new Queue<string>();
        pending.Enqueue("");
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var indexPath = pending.Dequeue();
            var children = await ListAsync(ctlPath, indexPath, timeoutSeconds, ct);
            if (children is null)
                return (null, false, "chainUnreadable",
                    $"The controls of '{ctlPath}'" +
                    (indexPath.Length > 0 ? $" at index path '{indexPath}'" : "") +
                    " could not be listed, so what this typedef embeds is unknown and a flattened " +
                    "copy could not be trusted.");

            foreach (var child in children)
            {
                // A row whose own read failed tells us nothing - its path and class name come back
                // EMPTY, which is indistinguishable from "not a typedef" and from "not a cluster"
                // unless the code is consulted. Stopping is right rather than continuing: carrying
                // on would produce a copy that silently keeps the typedef this call exists to
                // remove, which is exactly how the first version of this tool passed its own
                // verification while installing nothing.
                if (child.ReadCode != 0)
                    return (null, false, "childUnreadable",
                        $"Reading control {child.Index} of '{ctlPath}'" +
                        (indexPath.Length > 0 ? $" at index path '{indexPath}'" : "") +
                        $" answered error {child.ReadCode}, so whether it carries a typedef is " +
                        "unknown and a flattened copy could not be trusted.");

                if (child.IsTypedef && child.TypedefPath.Length > 0 &&
                    IsClassPrivateData(child.TypedefPath))
                {
                    // Same guard as the terminal level: a class nested inside a cluster reports
                    // Is Typedef? non-zero, and installing its private data as a cluster would
                    // silently change the type rather than merely fail to improve it.
                    notDescended.Add(new JsonObject
                    {
                        ["file"] = ctlPath,
                        ["control"] = child.Label,
                        ["typedef"] = child.TypedefPath,
                        ["reason"] =
                            "A LabVIEW class nested inside this type. Flattening it would replace " +
                            "the class with a plain cluster, so it is left as it is.",
                    });
                    continue;
                }

                if (child.IsTypedef && child.TypedefPath.Length > 0)
                {
                    // No descent past it: the recursion flattens what is inside it, and after the
                    // rebind this control is already flat all the way down.
                    plan.Add((indexPath, child.Index, child.Label, child.TypedefPath));
                    continue;
                }

                // An ARRAY is a container like a cluster here: the helper reaches its single
                // element through {LV.Array} Array Element instead of Controls[], so from this
                // side it is one child at index 0.
                if (IsDescendable(child.ClassName))
                {
                    pending.Enqueue(
                        indexPath.Length == 0 ? child.Index.ToString() : $"{indexPath}|{child.Index}");
                }
            }
        }

        // Only now is anything written. A plan that could not be read leaves no half-made copy
        // behind for a later run to find in the cache and reuse.
        try { Directory.CreateDirectory(folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, false, "folderNotWritable",
                $"'{folder}' could not be created: {error.Message}.");
        }

        File.Copy(ctlPath, flatPath, overwrite: true);

        if (await ClearTypedefFlagAsync(flatPath, timeoutSeconds, ct) is { } flagProblem)
        {
            TryDelete(flatPath);
            return (null, false, "flagNotCleared", flagProblem);
        }

        foreach (var step in plan)
        {
            ct.ThrowIfCancellationRequested();

            var inner = await FlattenCtlAsync(
                step.TypedefPath, folder, cache, notDescended, depth + 1, timeoutSeconds, ct);
            if (inner.Path is null)
            {
                TryDelete(flatPath);
                return (null, false, inner.Problem, inner.Detail);
            }

            if (await BindAsync(flatPath, step.IndexPath, step.Index, inner.Path,
                                timeoutSeconds, ct) is { } problem)
            {
                TryDelete(flatPath);
                return (null, false, "innerBindFailed",
                    $"'{step.Label}' inside '{ctlPath}' could not be rebound: {problem}");
            }
        }

        cache[ctlPath] = flatPath;
        return (flatPath, false, null, null);
    }

    /// <summary>
    /// Clear <c>TypeDefVI</c> and <c>StrictTypeDefVI</c> on a saved `.ctl`, and rename its save
    /// record so the copy does not claim the original's VI name. Pure pylabview - no LabVIEW.
    ///
    /// The two flags are cleared INDEPENDENTLY rather than as one pair: a plain typedef carries
    /// <c>TypeDefVI="1" StrictTypeDefVI="0"</c>, and a pattern matching only the strict pair would
    /// silently leave it alone and answer as though it had worked.
    /// </summary>
    private async Task<string?> ClearTypedefFlagAsync(
        string ctlPath, int timeoutSeconds, CancellationToken ct)
    {
        var bundle = Path.Combine(
            Path.GetTempPath(), "LabVIEWMCP", "flatten",
            Path.GetFileNameWithoutExtension(ctlPath) + "-" + Environment.ProcessId);
        try
        {
            var py = new PyLabviewTools(connection);
            var extract = Read(await py.ExtractAsync(ctlPath, bundle, annotate: false,
                                                     Math.Min(timeoutSeconds, 45), ct));
            if (extract?["mainXml"]?.GetValue<string>() is not { Length: > 0 } mainXml ||
                !File.Exists(mainXml))
                return $"'{ctlPath}' could not be extracted, so its typedef flag cannot be " +
                       "cleared. pylv_extract answered: " + (extract?.ToJsonString() ?? "nothing");

            var text = await File.ReadAllTextAsync(mainXml, ct);

            // StrictTypeDefVI is cleared with the same pass but is NOT what decides it: a plain
            // typedef carries TypeDefVI="1" StrictTypeDefVI="0", so keying on the strict pair would
            // pass over one of the two kinds while answering as though it had worked.
            if (!text.Contains("TypeDefVI=\"1\"", StringComparison.Ordinal))
                return $"'{ctlPath}' carries no TypeDefVI=\"1\" in its save record, so the file is " +
                       "not a typedef - while LabVIEW reported this control's Typedef:Path as " +
                       "pointing at it. Refusing to install a copy whose provenance is unclear.";

            var patched = text
                .Replace("StrictTypeDefVI=\"1\"", "StrictTypeDefVI=\"0\"", StringComparison.Ordinal)
                .Replace("TypeDefVI=\"1\"", "TypeDefVI=\"0\"", StringComparison.Ordinal);

            // The copy still claims the ORIGINAL's VI name in its save record, and two files
            // claiming one name is the shape behind Error 1051. The name is taken from the Section
            // element rather than derived from the source path, because by this point the caller
            // has already copied the file and the original name is only in the record.
            patched = System.Text.RegularExpressions.Regex.Replace(
                patched,
                "(<Section\\b[^>]*\\bName=\")[^\"]*(\")",
                "$1" + Path.GetFileName(ctlPath).Replace("$", "$$") + "$2",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(5));

            await File.WriteAllTextAsync(mainXml, patched, new UTF8Encoding(false), ct);

            var rebuild = Read(await py.RebuildAsync(mainXml, ctlPath,
                                                     Math.Min(timeoutSeconds, 45), ct));
            if (rebuild?["ok"]?.GetValue<bool>() is not true)
                return $"'{ctlPath}' could not be rebuilt after clearing its typedef flag. " +
                       "pylv_rebuild answered: " + (rebuild?.ToJsonString() ?? "nothing");

            return null;
        }
        finally
        {
            try { if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>One container's children, or null when the probe could not run.</summary>
    private async Task<IReadOnlyList<Child>?> ListAsync(
        string filePath, string indexPath, int timeoutSeconds, CancellationToken ct)
    {
        var helper = HelperViPath(TreeHelperFileName);

        // An EMPTY value is refused by the runner - it pairs names and values by position, so an
        // empty one would shift every later input onto the wrong control. The root listing
        // therefore OMITS the input and lets the control keep its own empty default.
        var inputs = new JsonObject { ["vi path"] = Path.GetFullPath(filePath) };
        if (indexPath.Length > 0) inputs["index path"] = indexPath;

        var values = ValuesOf(await new RunTools(connection).RunViAndReadValuesAsync(
            helper, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
            helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct));
        if (values is null) return null;

        var labels = TypedefTools.StringArray(values, "labels");
        var isTypedef = TypedefTools.BoolArray(values, "is typedef");
        var paths = TypedefTools.StringArray(values, "typedef paths");
        var classes = TypedefTools.StringArray(values, "class names");
        var readCodes = TypedefTools.StringArray(values, "read codes");

        var children = new List<Child>();
        for (var i = 0; i < labels.Count; i++)
            children.Add(new Child(
                i,
                labels[i],
                i < isTypedef.Count && isTypedef[i],
                i < paths.Count ? paths[i] : "",
                i < classes.Count ? classes[i] : "",
                i < readCodes.Count && int.TryParse(readCodes[i], out var code) ? code : 0));
        return children;
    }

    /// <summary>Install one `.ctl`; null on success, a sentence on failure.</summary>
    private async Task<string?> BindAsync(
        string filePath, string indexPath, int childIndex, string sourceCtl,
        int timeoutSeconds, CancellationToken ct)
    {
        var inputs = new JsonObject
        {
            ["vi path"] = Path.GetFullPath(filePath),
            ["child index"] = childIndex.ToString(),
            ["source ctl path"] = Path.GetFullPath(sourceCtl),
        };
        if (indexPath.Length > 0) inputs["index path"] = indexPath;

        var values = ValuesOf(await new RunTools(connection).RunViAndReadValuesAsync(
            HelperViPath(BindHelperFileName), inputs.ToJsonString(), includeRawXml: false,
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds,
            ct: ct));
        if (values is null) return "the bind helper did not answer.";

        var code = values["code"]?["value"]?.GetValue<string>();
        if (code is null or "0") return null;

        var source = values["source"]?["value"]?.GetValue<string>() ?? "";
        return $"error {code} from {source}".Trim();
    }

    /// <summary>
    /// <c>class="typeDef"</c> objects in the saved file's front panel heap. -1 means the file could
    /// not be read, which is reported as unknown rather than as a pass.
    /// </summary>
    private async Task<int> TypedefObjectsAsync(string viPath, int timeoutSeconds,
                                                CancellationToken ct)
    {
        var bundle = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "flatten",
                                  "verify-" + Environment.ProcessId);
        try
        {
            var extract = Read(await new PyLabviewTools(connection).ExtractAsync(
                viPath, bundle, annotate: false, Math.Min(timeoutSeconds, 45), ct));
            if (extract?["ok"]?.GetValue<bool>() is not true || !Directory.Exists(bundle)) return -1;

            var total = 0;
            foreach (var file in Directory.EnumerateFiles(bundle, "*FPHb.xml"))
                total += Occurrences(await File.ReadAllTextAsync(file, ct), "class=\"typeDef\"");
            return total;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return -1;
        }
        finally
        {
            try { if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Both helper VIs, generated once into the per-user helper cache.</summary>
    private async Task<string?> EnsureHelpersAsync(
        IEnumerable<string> names, int timeoutSeconds, CancellationToken ct)
    {
        if (StatusTools.ScriptsDirectory() is not { } scripts)
            return "The helper scripts directory could not be located - lvai_status reports it as " +
                   "scriptsDirectory.";

        foreach (var name in names)
        {
            var aixml = Path.Combine(scripts, name);
            if (!File.Exists(aixml)) return $"'{aixml}' is missing from the scripts directory.";

            var helper = HelperViPath(name);
            if (Path.GetDirectoryName(helper) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            if (!HelperCache.NeedsRebuild(aixml, helper)) continue;

            var convert = Read(await new AixmlTools(connection)
                .ConvertAixmlToViAsync(aixml, helper, false, timeoutSeconds, ct: ct));
            if (convert?["errorCode"]?.GetValue<int>() is not 0)
                return $"'{name}' could not be generated: " +
                       (convert?.ToJsonString() ?? "no answer");
        }

        return null;
    }

    /// <summary>
    /// Whether a reported <c>Typedef:Path</c> names a class's private data control rather than an
    /// ordinary `.ctl`. Both halves are checked because either alone would be a guess: a path
    /// inside a `.lvclass` is the synthetic form LabVIEW reports for class private data, and
    /// anything that is not a `.ctl` at all is not something this route may install.
    /// </summary>
    internal static bool IsClassPrivateData(string typedefPath) =>
        typedefPath.Contains(".lvclass", StringComparison.OrdinalIgnoreCase) ||
        !typedefPath.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether what LabVIEW said about the subject and what the subject's FILE holds disagree about
    /// there being anything to do at all.
    ///
    /// THE TWO NUMBERS ARE IN DIFFERENT UNITS, so this is NOT an equality. <paramref name="sites"/>
    /// counts the OUTERMOST typedef on each branch, because the walk stops there and both routes
    /// handle what is nested inside it themselves; <paramref name="objectsInFile"/> counts every
    /// typedef INSTANCE at every depth. One site against two objects is the ordinary shape of a
    /// typedef inside a typedef, and refusing that would refuse the fixture this feature was built
    /// on. Only "the file has some and the walk found none" says the two disagree about the
    /// question rather than about the counting.
    ///
    /// A NEGATIVE <paramref name="objectsInFile"/> means the file could not be read, which is not
    /// evidence of anything and must not become an accusation.
    ///
    /// WHY IT EXISTS: everything the walk knows came through VI Server, which answers from
    /// LabVIEW's IN-MEMORY copy. Reproduced on demand 2026-09-18 - a VI opened in the editor, its
    /// file then replaced on disk with one carrying three typedefs, and the walk still read the
    /// memory copy's none. Without this the call answers `ok` for having nothing to do, which is
    /// indistinguishable from having done it.
    /// </summary>
    internal static bool SubjectDisagreesWithItsFile(int sites, int objectsInFile) =>
        sites == 0 && objectsInFile > 0;

    /// <summary>
    /// Whether a control's VI Server <c>Class Name</c> is one the tree helper can walk into.
    ///
    /// The two are reached by DIFFERENT properties and that is why this list is short rather than
    /// generous: <c>Controls[]</c> is declared on <c>{LV.Cluster}</c>, <c>{LV.Panel}</c>,
    /// <c>{LV.ConnectorPane}</c>, <c>{LV.RadioButtonsControl}</c> and <c>{LV.WaveformData}</c> and
    /// NOT on <c>{LV.Array}</c>, whose single child comes back from <c>Array Element</c> as ONE
    /// reference with no label. The helper reads both forms and picks on <c>Class Name</c>, so from
    /// here an array is simply a container with exactly one child at index 0.
    ///
    /// AN ARRAY WAS NAMED IN <c>notDescended</c> UNTIL 2026-09-18 and is walked now - measured on a
    /// subject carrying an array of a typedef enum, which the helper reported as
    /// <c>is typedef</c> true pointing at the <c>.ctl</c>. Anything else is still passed over
    /// silently, which is right while nothing has been measured about it.
    /// </summary>
    internal static bool IsDescendable(string className) =>
        string.Equals(className, "Cluster", StringComparison.Ordinal) ||
        string.Equals(className, "Array", StringComparison.Ordinal);

    /// <summary>
    /// Where the flattened copies live, and it is NOT <c>user.lib\LV_MCP</c> beside the stubs.
    ///
    /// MEASURED 2026-09-18 as a clean A/B in one LabVIEW session - same helper, same stub, seconds
    /// apart, differing only in where the SOURCE `.ctl` sat: a source under
    /// <c>user.lib\LV_MCP</c> answers <c>error 1154</c> from <c>{LV.Control} Replace</c>, and the
    /// same content outside the installation answers 0. So a flattened copy must not be written
    /// there, and the first version of this tool did exactly that and could install nothing.
    ///
    /// Nothing is lost by moving them out. The stub has to be in <c>user.lib\LV_MCP</c> because it
    /// is resolved as a `Call` target BY BARE NAME; a flattened `.ctl` is only ever a Replace
    /// source, and since the copy is not a typedef the Replace leaves NO link behind - so the stub
    /// does not reference it afterwards and it is not a dependency of anything.
    /// </summary>
    internal static string FlatCopyFolder() =>
        Path.Combine(CacheDirectory.Root, "flat-typedefs");

    private static string HelperViPath(string aixmlFileName) =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers",
                     Path.ChangeExtension(aixmlFileName, ".vi"));

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ContentHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..10].ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static JsonObject Fail(JsonObject answer, string kind, string detail,
                                   System.Diagnostics.Stopwatch elapsed)
    {
        answer["ok"] = false;
        answer["errorKind"] = kind;
        answer["error"] = detail;
        answer["elapsedMs"] = elapsed.ElapsedMilliseconds;
        return answer;
    }

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static JsonObject? ValuesOf(string runnerAnswer)
    {
        if (Read(runnerAnswer) is not JsonObject root) return null;
        if (root["helperFailed"]?.GetValue<bool>() is true) return null;
        return root["values"] as JsonObject;
    }

    /// <summary>One control of a container, as the tree helper reports it.</summary>
    private sealed record Child(
        int Index, string Label, bool IsTypedef, string TypedefPath, string ClassName,
        int ReadCode);
}
