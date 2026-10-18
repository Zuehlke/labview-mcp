using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The clean-up every VI this server has just written goes through, and the save in place - the
/// user's recommendation of 2026-10-12, on both backends: neither NI's converter nor our composer
/// lays a diagram out, and a person opening the VI should not have to press Ctrl+U first.
///
/// THE DIAGRAM IS LAID OUT BY OUR OWN ENGINE since 2026-10-16, the user's decision: the pipeline of
/// <c>lvai_layout_diagram</c> (<see cref="DiagramLayoutTools.LayoutCoreAsync"/>), without its verify
/// pass. Until then it was LabVIEW's <c>{LV.VI}</c> <c>BD.CleanUp</c>, which spread a DQMH Main.vi
/// from 1344 x 805 to 3805 x 1692 px and tore comments off their nodes (docs/diagram-layout.md).
/// The step keeps its name, `cleanUpDiagram`, and says which ran in `engine`. A layout that does not
/// finish puts the VI back as it was generated, from the copy the pipeline keeps.
///
/// THE FRONT PANEL is still LabVIEW's own algorithm (`cleanUpPanel`, below).
///
/// A SAVE BY LabVIEW CAN WRITE COMPILED CODE, which the VIs we write must never carry. So the file
/// is checked afterwards: a VI that had no VICD before and has one now is stripped back to source
/// only with pylabview (pylv-strip-compiled.py), and the answer says so. A VI that already carried
/// compiled code - NI's converter writes it - is left as it was.
/// </summary>
internal sealed class DiagramCleanup(LvaiConnection connection)
{
    /// <summary>What the `engine` field of a `cleanUpDiagram` step names.</summary>
    internal const string Engine = "lvai_layout_diagram";

    /// <summary>
    /// THE FRONT PANEL'S counterpart, the user's request of 2026-10-12 for every VI that is not a
    /// main VI: the algorithm behind Edit - Clean Up Panel. The IDE's own plugin
    /// (resource\plugins\Call Clean Up Panel.vi) first chooses between Nigel and that algorithm in
    /// a dialog, so the helper calls the algorithm directly - Arrange VIWin - Arrange FP.vi under
    /// resource\dialog\QuickDrop, by path, with the VI's reference. It answers a panel holding a
    /// tab control or a splitter with a MODAL One Button Dialog, which stops everything behind it,
    /// so the helper counts both first and leaves such a panel alone (`arranged` false).
    /// </summary>
    internal const string PanelHelperFileName = "lvai_cleanup_panel.xml";

    private sealed record Kind(string Step, string Helper, string What, string Done);

    private static readonly Kind Panel = new("cleanUpPanel", PanelHelperFileName, "front panel",
        "LabVIEW's Clean Up Panel algorithm (Arrange FP) ran and the VI was saved in place");

    /// <summary>The `cleanUpDiagram` step for one VI: our layout, saved in place. Never throws
    /// for a LabVIEW refusal: the answer's `ok` says whether the diagram was laid out and saved.</summary>
    public async Task<JsonObject> RunAsync(string viPath, string backend, int timeoutSeconds,
                                           CancellationToken ct)
    {
        var full = Path.GetFullPath(viPath);
        var step = new JsonObject { ["step"] = "cleanUpDiagram", ["backend"] = backend, ["engine"] = Engine };
        if (!File.Exists(full)) return Fail(step, "There is no VI at " + full + " to clean up.");
        if (IsDqmhMain(full)) return DqmhSkipped(step);

        var compiledBefore = BulkTools.ContainsCompiledCode(full);
        var answer = JsonNode.Parse(await BackendRoute.WithBackendAsync(backend, "cleanUpDiagram",
            () => new DiagramLayoutTools(connection).LayoutCoreAsync(full, dryRun: false, verify: false,
                                                                     render: false, renderDirectory: null,
                                                                     timeoutSeconds, ct))) as JsonObject;
        foreach (var key in new[] { "before", "planned", "moves", "structuresSized", "tunnelsMoved",
                                    "structuresKeptAsTheyAre", "heldInMemory", "moveErrors", "sizeErrors",
                                    "tunnelErrors", "compiledCodeStripped" })
            if (answer?[key] is { } value) step[key] = value.DeepClone();

        if (answer?["ok"]?.GetValue<bool>() != true)
        {
            // a layout that stopped half way may have saved a half-arranged diagram: the copy the
            // pipeline took before applying is the VI as it was generated
            if (answer?["backupPath"]?.GetValue<string>() is { } backup && File.Exists(backup))
            {
                try { File.Copy(backup, full, overwrite: true); step["restored"] = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { step["restored"] = false; }
            }
            step["layout"] = answer?.DeepClone();
            var why = answer?["note"]?.GetValue<string>() ?? answer?["error"]?.GetValue<string>() ?? "no answer";
            return Fail(step, "Our layout did not finish (" + why + "). The VI is as it was generated" +
                              (step["restored"]?.GetValue<bool>() == false ? ", unless putting it back failed - read restored." : "."));
        }
        if (!compiledBefore && BulkTools.ContainsCompiledCode(full))
            return Fail(step, "The diagram was laid out and saved, and LabVIEW's save wrote COMPILED CODE " +
                              "into a VI that had none; stripping it again failed. Read compiledCodeStripped.");
        step["ok"] = true;
        step["note"] = "Our block diagram layout (lvai_layout_diagram) ran and the VI was saved in place" +
                       (compiledBefore ? " (it carried compiled code before, and still does)." : ", source only.");
        return step;
    }

    private static JsonObject DqmhSkipped(JsonObject step)
    {
        step["ok"] = true;
        step["skipped"] = true;
        step["note"] = "This is a DQMH module's Main.vi, which the automatic clean-up never touches - its " +
                       "layout is Delacor's, and the DQMH tools rely on it (the user's rule of 2026-10-13). " +
                       "lvai_layout_diagram named directly does lay it out.";
        return step;
    }

    /// <summary>The `cleanUpPanel` step for one VI; `skipped` when its panel holds a tab control
    /// or a splitter, which the algorithm would answer with a modal dialog.</summary>
    public Task<JsonObject> RunPanelAsync(string viPath, string backend, int timeoutSeconds,
                                          CancellationToken ct) =>
        RunAsync(Panel, viPath, backend, timeoutSeconds, ct);

    private async Task<JsonObject> RunAsync(Kind kind, string viPath, string backend, int timeoutSeconds,
                                            CancellationToken ct)
    {
        var full = Path.GetFullPath(viPath);
        var step = new JsonObject { ["step"] = kind.Step, ["backend"] = backend };
        if (!File.Exists(full)) return Fail(step, "There is no VI at " + full + " to clean up.");
        if (IsDqmhMain(full)) return DqmhSkipped(step);

        var compiledBefore = BulkTools.ContainsCompiledCode(full);
        var writtenBefore = File.GetLastWriteTimeUtc(full);
        var helperFile = kind.Helper;

        string helperVi;
        if (backend == AixmlBackend.PyLabview)
        {
            var (composed, problem) = await ComposedHelpers.EnsureAsync(helperFile, timeoutSeconds, ct);
            if (composed is null) return Fail(step, "The clean-up helper could not be composed: " + problem);
            helperVi = composed;
        }
        else
        {
            var source = StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, helperFile) : null;
            if (source is null || !File.Exists(source))
                return Fail(step, $"{helperFile} was not found in the scripts folder beside the server.");
            helperVi = Path.Combine(AixmlBackend.HelperFolder(), Path.ChangeExtension(helperFile, ".vi"));
            Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
            if (HelperCache.NeedsRebuild(source, helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    source, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct: ct, measureDiagram: false, backend: backend);
                if (!File.Exists(helperVi))
                {
                    step["helper"] = JsonNode.Parse(built);
                    return Fail(step, "The clean-up helper could not be generated.");
                }
            }
        }

        var run = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(
            helperVi, new JsonObject { ["vi path"] = full }.ToJsonString(),
            timeoutSeconds: timeoutSeconds, backend: backend, ct: ct)) as JsonObject;
        var error = CarayaRunTools.ErrorOut(run?["values"] as JsonObject);
        var code = error?["code"]?.GetValue<int?>();
        var saved = File.GetLastWriteTimeUtc(full) > writtenBefore;
        step["helperErrorCode"] = code;
        step["helperErrorSource"] = error?["source"]?.GetValue<string>();
        step["saved"] = saved;
        if (kind == Panel && code is 0 && Arranged(run?["values"] as JsonObject) is false)
        {
            step["ok"] = true;
            step["skipped"] = true;
            step["note"] = "The front panel holds a tab control or a splitter, on which LabVIEW's " +
                           "arrangement answers with a modal dialog, so it was left as generated.";
            return step;
        }
        if (code is not 0 || !saved)
        {
            step["run"] = run;
            return Fail(step, code is null
                ? $"The clean-up helper's error out could not be read, so nothing says the {kind.What} " +
                  "was cleaned up. The VI is as it was generated."
                : code != 0
                    ? $"LabVIEW answered error {code} to the clean-up; the VI is as it was generated."
                    : "The helper answered no error but the file was not rewritten, so the clean-up " +
                      "did not reach it.");
        }

        // COMPILED CODE A LabVIEW SAVE BROUGHT IN goes back out - only when the file had none
        // before, so a VI that came with it (NI's converter) is not changed here.
        if (!compiledBefore && BulkTools.ContainsCompiledCode(full))
        {
            var strip = await StripCompiledAsync(full, timeoutSeconds, ct);
            step["compiledCodeStripped"] = strip;
            if (BulkTools.ContainsCompiledCode(full))
                return Fail(step, $"The {kind.What} was cleaned up and saved, and LabVIEW's save wrote " +
                                  "COMPILED CODE into a VI that had none; stripping it again failed. " +
                                  "Read compiledCodeStripped.");
        }
        step["ok"] = true;
        step["note"] = kind.Done +
                       (compiledBefore ? " (it carried compiled code before, and still does)." : ", source only.");
        return step;
    }

    /// <summary>
    /// A DQMH module's Main.vi: named Main.vi, beside the two event clusters every module folder
    /// carries. The DQMH tools never clean up at all; this guards every other route to the same file.
    /// </summary>
    internal static bool IsDqmhMain(string viPath)
    {
        if (!string.Equals(Path.GetFileName(viPath), "Main.vi", StringComparison.OrdinalIgnoreCase))
            return false;
        var folder = Path.GetDirectoryName(viPath);
        return folder is not null &&
               File.Exists(Path.Combine(folder, "Request Events--cluster.ctl")) &&
               File.Exists(Path.Combine(folder, "Broadcast Events--cluster.ctl"));
    }

    /// <summary>The panel helper's `arranged` boolean, read off either backend's answer; null when
    /// it is not there.</summary>
    internal static bool? Arranged(JsonObject? values)
    {
        if (values?["arranged"] is not JsonObject entry) return null;
        var text = (entry["literal"] ?? entry["value"])?.ToString()?.Trim().ToLowerInvariant();
        return text switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => entry["xml"]?.GetValue<string>() is { } xml
                ? xml.Contains("<Val>1</Val>") || xml.Contains("<Val>TRUE</Val>")
                : null,
        };
    }

    /// <summary>Extract, pylv-strip-compiled.py, rebuild - the same three steps pylv_apply ends with.</summary>
    internal async Task<JsonObject> StripCompiledAsync(string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var answer = new JsonObject();
        if (PyLabview.Locate() is not { } bundle || StatusTools.ScriptsDirectory() is not { } scripts)
        {
            answer["ok"] = false;
            answer["note"] = "pylabview or the scripts folder is not available.";
            return answer;
        }
        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "cleanup", Path.GetRandomFileName());
        try
        {
            var pylv = new PyLabviewTools(connection);
            var extract = JsonNode.Parse(await pylv.ExtractAsync(viPath, directory, annotate: false,
                                                                 timeoutSeconds, ct: ct));
            if (extract?["mainXml"]?.GetValue<string>() is not { } mainXml)
            {
                answer["ok"] = false;
                answer["extract"] = extract?.DeepClone();
                return answer;
            }
            var strip = await EventStructureTools.RunScriptAsync(bundle, scripts, "pylv-strip-compiled.py",
                [directory, Path.GetFileNameWithoutExtension(mainXml)], "strip", timeoutSeconds, ct);
            if (strip["exitCode"]?.GetValue<int>() != 0)
            {
                answer["ok"] = false;
                answer["strip"] = strip;
                return answer;
            }
            var rebuild = JsonNode.Parse(await pylv.RebuildAsync(mainXml, viPath, timeoutSeconds, ct: ct));
            answer["ok"] = rebuild?["ok"]?.GetValue<bool>() == true && !BulkTools.ContainsCompiledCode(viPath);
            if (answer["ok"]?.GetValue<bool>() != true) answer["rebuild"] = rebuild?.DeepClone();
            return answer;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    internal const string FanRouteScript = "lvbd_fan_route.py";

    /// <summary>
    /// Extract, lvbd_fan_route.py, rebuild: each constant fan's wires get staggered geometry of our
    /// own - two or more constants into one node, which LabVIEW's Clean Up Wire lays out one wire
    /// at a time and may cross (Find Account's column constants, 2026-10-18). Runs AFTER the
    /// layout's save, whose Clean Up Wire would replace it, on a VI LabVIEW does not hold; the
    /// VI is rebuilt only when a table changed. A VI carrying compiled code is left alone, as every
    /// pylabview edit in this repository leaves it (CLAUDE.md, "close the project").
    /// </summary>
    internal async Task<JsonObject> FanRouteAsync(string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var answer = new JsonObject();
        if (BulkTools.ContainsCompiledCode(viPath))
        {
            answer["ok"] = true;
            answer["skipped"] = "The VI carries compiled code; its wires are left as LabVIEW routed them.";
            return answer;
        }
        if (PyLabview.Locate() is not { } bundle || StatusTools.ScriptsDirectory() is not { } scripts)
        {
            answer["ok"] = false;
            answer["note"] = "pylabview or the scripts folder is not available.";
            return answer;
        }
        var directory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "fanroute", Path.GetRandomFileName());
        try
        {
            var pylv = new PyLabviewTools(connection);
            var extract = JsonNode.Parse(await pylv.ExtractAsync(viPath, directory, annotate: false,
                                                                 timeoutSeconds, ct: ct));
            if (extract?["mainXml"]?.GetValue<string>() is not { } mainXml)
            {
                answer["ok"] = false;
                answer["extract"] = extract?.DeepClone();
                return answer;
            }
            var heap = Path.Combine(directory, Path.GetFileNameWithoutExtension(mainXml) + "_BDHb.xml");
            if (!File.Exists(heap))
            {
                answer["ok"] = true;
                answer["wiresRewritten"] = 0;
                answer["note"] = "No block-diagram heap in the file.";
                return answer;
            }
            var run = await EventStructureTools.RunScriptAsync(bundle, scripts, FanRouteScript, [heap], "fanRoute",
                                                               timeoutSeconds, ct);
            if (run["exitCode"]?.GetValue<int>() != 0)
            {
                answer["ok"] = false;
                answer["fanRoute"] = run;
                return answer;
            }
            var (fans, rewritten) = ParseFanRoute(run["stdout"]?.GetValue<string>());
            answer["fans"] = fans;
            answer["wiresRewritten"] = rewritten;
            if (rewritten == 0)
            {
                answer["ok"] = true;
                return answer;
            }
            var rebuild = JsonNode.Parse(await pylv.RebuildAsync(mainXml, viPath, timeoutSeconds, ct: ct));
            answer["ok"] = rebuild?["ok"]?.GetValue<bool>() == true && !BulkTools.ContainsCompiledCode(viPath);
            if (answer["ok"]?.GetValue<bool>() != true) answer["rebuild"] = rebuild?.DeepClone();
            return answer;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The script's first line: "fans N, wires rewritten M".</summary>
    internal static (int Fans, int Rewritten) ParseFanRoute(string? stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stdout ?? "", @"fans (\d+), wires rewritten (\d+)");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
    }

    private static JsonObject Fail(JsonObject step, string note)
    {
        step["ok"] = false;
        step["note"] = note;
        return step;
    }
}
