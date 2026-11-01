using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// <c>lvai_check_build_contract</c> - the gate around the BUILD CONTRACT (<see cref="BuildContract"/>,
/// `docs/build-contract.md`): before the first agent, the contract is checked without LabVIEW and
/// turned into build waves and Call skeletons; after each wave, the built VIs are read back and
/// compared with it, so a deviation is caught before any caller is built against it.
/// </summary>
[McpServerToolType]
internal sealed class BuildContractTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_check_build_contract", ReadOnly = true,
                   Title = "Check a build contract, or the VIs built against it")]
    [Description("""
        Build: check the BUILD CONTRACT of a multi-VI application - one JSON file naming every VI
        with its path, owner, builder agent, connector pane (terminal names, types, conIdx,
        connection) and what it calls. The orchestrator writes it BEFORE spawning any agent and
        hands each agent the file; agents implement against it instead of settling interfaces
        themselves. Format and workflow: docs/build-contract.md.
        mode=plan (no LabVIEW): refuses unknown keys, then checks the house rules for every VI the
        build creates (error in required + error out, error cluster type, no required output,
        every terminal on a slot of its pattern, error corners), duplicate and clashing file
        names, calls that resolve to no or several VIs (a bare `Deposit.vi` beside
        `Account Manager.lvlib:Deposit.vi`), installation namesakes, two builders in one folder,
        and cycles. Answers the build WAVES (what can be built in parallel, in order) and, for the
        names in `callSkeletonsFor`, a ready `<Call>` per VI so a caller needs no lvai_vi_terminals.
        mode=verify (after a wave): exports each built VI named in `vis` (default: all that exist)
        and compares its pane with the contract - missing or extra terminals, type, conIdx,
        connection, direction. `ok` is false on any difference or on a named VI not yet built.
        """)]
    public async Task<string> CheckBuildContractAsync(
        [Description("Absolute path to the contract JSON (build-contract.json)")] string contractPath,
        [Description("plan (default): check the contract, no LabVIEW. verify: compare built VIs with it")]
        string mode = "plan",
        [Description("verify only: the VIs to compare, one per line, as contract paths or names. " +
                     "Empty: every contract VI that exists on disk")]
        string? vis = null,
        [Description("plan only: VI names or paths, one per line, whose Call skeleton to return")]
        string? callSkeletonsFor = null,
        [Description("Local budget in seconds per exported VI (verify)")] int timeoutSeconds = 180,
        [Description(AixmlBackend.ParameterDescription)] string? backend = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(contractPath))
            return Json.Error("fileNotFound", $"No contract at '{contractPath}'.", new { contractPath });

        BuildContract.Document doc;
        try { doc = BuildContract.Parse(await File.ReadAllTextAsync(contractPath, ct), contractPath); }
        catch (ArgumentException e) { return Json.Error("badContract", e.Message, new { contractPath }); }

        return mode switch
        {
            "plan" => Plan(doc, contractPath, callSkeletonsFor),
            "verify" => await VerifyAsync(doc, contractPath, vis, timeoutSeconds, backend, ct),
            _ => Json.Error("badArguments", $"mode \"{mode}\" is not plan or verify.", new { mode }),
        };
    }

    // ------------------------------------------------------------------ plan

    private static string Plan(BuildContract.Document doc, string contractPath, string? callSkeletonsFor)
    {
        var station = StationPaneDefault.Read();
        var result = BuildContract.Plan(doc, station.Pattern, InstallationNamesake);

        var answer = new JsonObject
        {
            ["ok"] = result.Errors.Count == 0,
            ["mode"] = "plan",
            ["contractPath"] = contractPath,
            ["root"] = doc.Root,
            ["vis"] = doc.Vis.Count,
            ["stationPanePattern"] = station.Pattern,
            ["errors"] = new JsonArray([.. result.Errors.Select(f => (JsonNode)f.ToJson())]),
            ["warnings"] = new JsonArray([.. result.Warnings.Select(f => (JsonNode)f.ToJson())]),
            ["waves"] = new JsonArray([.. result.Waves.Select((wave, i) => (JsonNode)new JsonObject
            {
                ["wave"] = i,
                ["vis"] = new JsonArray([.. wave.Select(v => (JsonNode)JsonValue.Create(
                    v.Builder is null ? v.Path : $"{v.Path} [{v.Builder}]")!)]),
            })]),
        };

        if (!string.IsNullOrWhiteSpace(callSkeletonsFor))
        {
            var byQualified = doc.Vis.GroupBy(v => v.QualifiedName, StringComparer.OrdinalIgnoreCase)
                                     .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var skeletons = new JsonObject();
            foreach (var name in Lines(callSkeletonsFor))
            {
                var hit = BuildContract.Resolve(doc, byQualified, name);
                skeletons[name] = hit is { Count: 1 }
                    ? ViTerminals.CallSkeleton(ToResult(hit[0]))
                    : hit is null ? "not a VI of this contract" : "ambiguous - name it by its qualified name";
            }
            answer["callSkeletons"] = skeletons;
        }

        answer["note"] = result.Errors.Count == 0
            ? "Hand each builder the contract path and its wave. Build wave by wave; after each, run " +
              "mode=verify on that wave before building the next."
            : "Fix the errors in the contract before any agent builds against it.";
        return Json.Document(answer);
    }

    /// <summary>
    /// The contract entry as the terminal list an export would give, so the one Call-skeleton
    /// writer serves both. Only pane terminals - a contract has no others.
    /// </summary>
    private static ViTerminals.Result ToResult(BuildContract.Vi vi) =>
        new(vi.QualifiedName,
            [.. vi.Inputs.Select(t => new ViTerminals.Terminal(t.Name, t.Type, t.ConIdx ?? 0, t.Connection))],
            [.. vi.Outputs.Select(t => new ViTerminals.Terminal(t.Name, t.Type, t.ConIdx ?? 0, t.Connection))],
            [], null);

    /// <summary>
    /// Where an installation VI of this bare name lives, from the palette index (cached on disk),
    /// or null. A VI only inside an `.llb` container off every palette is not seen.
    /// </summary>
    private static string? InstallationNamesake(string fileName)
    {
        try
        {
            return PaletteIndex.Build().Vis
                .FirstOrDefault(v => string.Equals(v.Name, fileName, StringComparison.OrdinalIgnoreCase))
                ?.PaletteFile;
        }
        catch (Exception)
        {
            return null; // no installation here - the check is advisory
        }
    }

    // ------------------------------------------------------------------ verify

    private async Task<string> VerifyAsync(BuildContract.Document doc, string contractPath, string? vis,
                                           int timeoutSeconds, string? backend, CancellationToken ct)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_check_build_contract", out var choice, out var badBackend))
            return AixmlBackend.Refusal(badBackend);

        List<BuildContract.Vi> selected;
        var named = Lines(vis).ToList();
        if (named.Count == 0)
            selected = [.. doc.Vis];
        else
        {
            var byQualified = doc.Vis.GroupBy(v => v.QualifiedName, StringComparer.OrdinalIgnoreCase)
                                     .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            selected = [];
            foreach (var name in named)
            {
                var hit = BuildContract.Resolve(doc, byQualified, name);
                if (hit is not { Count: 1 })
                    return Json.Error("badArguments",
                        $"'{name}' in vis is {(hit is null ? "not a VI of this contract" : "ambiguous - name it by its qualified name")}.",
                        new { name });
                selected.Add(hit[0]);
            }
        }

        var results = new JsonArray();
        int matched = 0, differing = 0, notBuilt = 0, unreadable = 0, notCompared = 0;

        foreach (var vi in selected)
        {
            var entry = new JsonObject { ["vi"] = vi.Path };
            if (!File.Exists(vi.FullPath))
            {
                // Without a list, an unbuilt VI is the next wave's - listed, not a failure.
                entry["status"] = "notBuilt";
                if (named.Count > 0) notBuilt++;
                results.Add(entry);
                continue;
            }

            // A supplied panel and a VI the contract describes no terminals for: existence is all.
            if (vi.Kind == BuildContract.KindSupplied || !vi.Terminals.Any())
            {
                entry["status"] = "exists";
                notCompared++;
                results.Add(entry);
                continue;
            }

            InspectTools.TerminalRead read;
            try { read = await InspectTools.ReadTerminalsAsync(connection, vi.FullPath, false, timeoutSeconds, choice, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                read = InspectTools.TerminalRead.Failed(e.Message);
            }

            if (read.Parsed is not { } parsed)
            {
                entry["status"] = "unreadable";
                entry["error"] = ErrorNode(read.Error);
                unreadable++;
                results.Add(entry);
                continue;
            }

            var differences = BuildContract.Compare(vi, parsed.PaneInputs, parsed.PaneOutputs);
            if (differences.Count == 0)
            {
                entry["status"] = "match";
                matched++;
            }
            else
            {
                entry["status"] = "differs";
                entry["differences"] = new JsonArray([.. differences.Select(d => (JsonNode)JsonValue.Create(d)!)]);
                differing++;
            }
            results.Add(entry);
        }

        var answer = new JsonObject
        {
            ["ok"] = differing == 0 && notBuilt == 0 && unreadable == 0,
            ["mode"] = "verify",
            ["contractPath"] = contractPath,
            ["matched"] = matched,
            ["differs"] = differing,
            ["notBuilt"] = results.Count(r => r!["status"]!.GetValue<string>() == "notBuilt"),
            ["unreadable"] = unreadable,
            ["existsOnly"] = notCompared,
            ["results"] = results,
        };
        if (differing > 0)
            answer["note"] = "Fix the VI, or - if the contract was wrong - change the contract and tell every builder " +
                             "whose VIs call this one. Do not build the next wave against a differing VI.";
        return AixmlBackend.Named(Json.Document(answer), choice);
    }

    /// <summary>An export's error answer as JSON when it is JSON, else as its text.</summary>
    private static JsonNode? ErrorNode(string? error)
    {
        try { return JsonNode.Parse(error ?? ""); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(error); }
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
