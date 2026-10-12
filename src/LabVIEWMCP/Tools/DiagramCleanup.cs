using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;

namespace LabVIEWMcp.Tools;

/// <summary>
/// LabVIEW's own Clean Up Diagram on a VI this server has just written, and the save in place -
/// the user's recommendation of 2026-10-12 for every VI we create, on both backends: neither NI's
/// converter nor our composer lays a diagram out the way LabVIEW's clean-up does, and a person
/// opening the VI should not have to press Ctrl+U first.
///
/// THE CALL IS NI's OWN. <c>{LV.VI}</c> <c>BD.CleanUp</c> followed by <c>Save.Instrument</c> with
/// the path unwired is exactly what Message Maker does on every VI it scripts (Build Do.vi, Build
/// Send.vi, Build Concrete Do.vi), read off their exports. The helper is
/// <c>scripts\lvai_cleanup_diagram.xml</c>: built by NI's converter and run in the add-on's instance
/// on grpc, COMPOSED and run over ActiveX on pylabview - the composer has the part.
///
/// A SAVE BY LabVIEW CAN WRITE COMPILED CODE, which the VIs we write must never carry. So the file
/// is checked afterwards: a VI that had no VICD before and has one now is stripped back to source
/// only with pylabview (pylv-strip-compiled.py), and the answer says so. A VI that already carried
/// compiled code - NI's converter writes it - is left as it was.
/// </summary>
internal sealed class DiagramCleanup(LvaiConnection connection)
{
    internal const string HelperFileName = "lvai_cleanup_diagram.xml";

    /// <summary>The `cleanUpDiagram` step for one VI. Never throws for a LabVIEW refusal: the
    /// answer's `ok` says whether the diagram was cleaned and saved.</summary>
    public async Task<JsonObject> RunAsync(string viPath, string backend, int timeoutSeconds,
                                           CancellationToken ct)
    {
        var full = Path.GetFullPath(viPath);
        var step = new JsonObject { ["step"] = "cleanUpDiagram", ["backend"] = backend };
        if (!File.Exists(full)) return Fail(step, "There is no VI at " + full + " to clean up.");

        var compiledBefore = BulkTools.ContainsCompiledCode(full);
        var writtenBefore = File.GetLastWriteTimeUtc(full);

        string helperVi;
        if (backend == AixmlBackend.PyLabview)
        {
            var (composed, problem) = await ComposedHelpers.EnsureAsync(HelperFileName, timeoutSeconds, ct);
            if (composed is null) return Fail(step, "The clean-up helper could not be composed: " + problem);
            helperVi = composed;
        }
        else
        {
            var source = StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperFileName) : null;
            if (source is null || !File.Exists(source))
                return Fail(step, $"{HelperFileName} was not found in the scripts folder beside the server.");
            helperVi = Path.Combine(AixmlBackend.HelperFolder(), Path.ChangeExtension(HelperFileName, ".vi"));
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
        if (code is not 0 || !saved)
        {
            step["run"] = run;
            return Fail(step, code is null
                ? "The clean-up helper's error out could not be read, so nothing says the diagram " +
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
                return Fail(step, "The diagram was cleaned up and saved, and LabVIEW's save wrote " +
                                  "COMPILED CODE into a VI that had none; stripping it again failed. " +
                                  "Read compiledCodeStripped.");
        }
        step["ok"] = true;
        step["note"] = "LabVIEW's Clean Up Diagram ran and the VI was saved in place" +
                       (compiledBefore ? " (it carried compiled code before, and still does)." : ", source only.");
        return step;
    }

    /// <summary>Extract, pylv-strip-compiled.py, rebuild - the same three steps pylv_apply ends with.</summary>
    private async Task<JsonObject> StripCompiledAsync(string viPath, int timeoutSeconds, CancellationToken ct)
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

    private static JsonObject Fail(JsonObject step, string note)
    {
        step["ok"] = false;
        step["note"] = note;
        return step;
    }
}
