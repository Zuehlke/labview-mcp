using LabVIEWMcp.Grpc;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Infra;

/// <summary>
/// What a tool that drives a whole ROUTE through helper VIs needs to run it on either backend - the
/// DQMH tools first (2026-10-13), then every other tool that still went to NI's service alone.
///
/// The choice is made ONCE per call (<see cref="WithBackendAsync"/>) and inherited by everything
/// the call does (<see cref="AixmlBackend.Inherited"/>), so no inner step can quietly reach NI's
/// service in the middle of a route promised to avoid it. On pylabview a helper is COMPOSED and run
/// over ActiveX, an export is read by our own reader, and a step that has no such route is refused
/// by name (<see cref="RouteException"/>) rather than sent to NI.
/// </summary>
internal static class BackendRoute
{
    /// <summary>Whether this call runs without NI's service.</summary>
    public static bool OnPyLabview => AixmlBackend.Inherited.Value?.Name == AixmlBackend.PyLabview;

    /// <summary>The backend name in force, for an inner call that takes one.</summary>
    public static string Current => OnPyLabview ? AixmlBackend.PyLabview : AixmlBackend.Grpc;

    /// <summary>
    /// A tool's body under the backend it resolved - the call's own `backend`, else the server
    /// default - with `backend` and `backendSource` added to the answer. A
    /// <see cref="RouteException"/> is answered here by its own kind.
    /// </summary>
    public static async Task<string> WithBackendAsync(string? backend, string tool, Func<Task<string>> body)
    {
        if (!AixmlBackend.TryResolve(backend, tool, out var choice, out var bad))
            return AixmlBackend.Refusal(bad);
        // EXPLICIT IN BOTH DIRECTIONS: TryResolve only ever sets the inherited choice, so a call
        // naming grpc inside a flow that already carried pylabview would otherwise stay on it.
        AixmlBackend.Inherited.Value = choice.Name == AixmlBackend.PyLabview
            ? AixmlBackend.Inherited.Value ?? new AixmlBackend.Choice(AixmlBackend.PyLabview, $"inherited from {tool}")
            : null;
        return AixmlTools.WithBackend(await Routed(body), choice);
    }

    /// <summary>
    /// <paramref name="body"/>, with a <see cref="RouteException"/> answered by its own kind.
    /// Needed INSIDE Rpc.GuardAsync too, whose catch-all would otherwise name the exception class.
    /// </summary>
    public static async Task<string> Routed(Func<Task<string>> body)
    {
        try { return await body(); }
        catch (RouteException e)
        {
            return Json.Error(e.Kind, e.Message, new { route = "pylabview (composer + ActiveX)" });
        }
    }

    /// <summary>A step of the pylabview route that cannot be taken, with its errorKind.</summary>
    internal sealed class RouteException(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    /// <summary>
    /// The errorKind of a run answer that is a REFUSAL rather than a run - on the ActiveX route a
    /// broken helper, a control the panel lacks or a LabVIEW that is not running all answer
    /// without any `values`, and a caller that only reads `error out` out of `values` would read
    /// that as a clean run. Null for an answer that carries values.
    /// </summary>
    public static string? RunRefusal(string answer)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(answer) is System.Text.Json.Nodes.JsonObject o
                   && o["values"] is null && o["errorKind"]?.GetValue<string>() is { } kind
                ? kind : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// The helper VI to run: on pylabview the composed one (refused by name if it cannot be);
    /// on grpc <paramref name="niVi"/>, rebuilt by <paramref name="generateOnGrpc"/> when stale or
    /// asked for - which answers null on success or the tool's own error answer.
    /// </summary>
    public static async Task<(string Vi, string? Failure, bool Generated)> HelperAsync(
        string aixml, string niVi, bool regenerate, Func<Task<string?>> generateOnGrpc,
        int timeoutSeconds, CancellationToken ct)
    {
        if (OnPyLabview)
            return (await ComposedHelperAsync(aixml, timeoutSeconds, ct,
                                              Path.GetFileName(niVi)), null, false);
        if (!regenerate && !HelperCache.NeedsRebuild(aixml, niVi)) return (niVi, null, false);
        if (Path.GetDirectoryName(niVi) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        return await generateOnGrpc() is { } failure ? (niVi, failure, false) : (niVi, null, true);
    }

    public static AixmlComposer.Setup Setup() =>
        AixmlComposer.Locate(out var missing) ?? throw new RouteException("backendNotAvailable", missing);

    /// <summary>
    /// The helper VI to run for <paramref name="aixmlPath"/> on the pylabview route - composed into
    /// %TEMP%\LabVIEWMCP\helpers-pylabview - or a refusal naming why it cannot be.
    /// </summary>
    public static async Task<string> ComposedHelperAsync(
        string aixmlPath, int timeoutSeconds, CancellationToken ct, string? viName = null)
    {
        var (vi, problem) = await ComposedHelpers.EnsurePathAsync(aixmlPath, viName, timeoutSeconds, ct);
        return vi ?? throw new RouteException("helperNotComposable", problem!);
    }

    /// <summary>
    /// A VI's AIXML as text - NI's ConvertVIToAIXML on grpc, our reader (vi2aixml.py) on
    /// pylabview. Null text with the error code and message when the export failed.
    /// </summary>
    public static async Task<(string? Aixml, int ErrorCode, string Message)> ExportAsync(
        LvaiConnection connection, string vi, string tag, int timeoutSeconds, CancellationToken ct)
    {
        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", $"{tag}.{Guid.NewGuid():N}.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        try
        {
            if (OnPyLabview)
            {
                var read = await AixmlComposer.ReadAsync(Setup(), vi, export, timeoutSeconds, ct);
                return read.Written && File.Exists(export)
                    ? (File.ReadAllText(export), 0, "No Error")
                    : (null, read.ErrorCode, read.Message);
            }
            var exported = await connection.InvokeAsync((c, t) =>
                c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                {
                    ViPath = vi,
                    AiXMLFilePath = export,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return exported.ErrorCode == 0 && File.Exists(export)
                ? (File.ReadAllText(export), 0, exported.ErrorMessage)
                : (null, exported.ErrorCode, exported.ErrorMessage);
        }
        finally
        {
            if (File.Exists(export)) File.Delete(export);
        }
    }

    /// <summary>
    /// AIXML -> VI for a document a tool authored itself (not a helper): NI's ValidateAIXML and
    /// ConvertAIXMLToVI on grpc, the composer on pylabview, where its refusal is the validation.
    /// <paramref name="searchFolders"/> are where the composer looks for Call targets, beside the
    /// VI's own folder. Null on success, otherwise a sentence naming what failed.
    /// </summary>
    public static async Task<string?> ConvertAsync(
        LvaiConnection connection, string aixml, string vi, int timeoutSeconds, CancellationToken ct,
        bool validateFirst = true, IReadOnlyList<string>? searchFolders = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vi))!);
        if (OnPyLabview)
        {
            // a helper may call another helper: those are composed into the helper folder, and
            // the scripts folder holds the rest
            string[] where = [.. searchFolders ?? [], ComposedHelpers.Folder,
                              .. StatusTools.ScriptsDirectory() is { } scripts ? new[] { scripts } : []];
            var composed = await AixmlComposer.ComposeAsync(Setup(), aixml, vi, where, timeoutSeconds, ct);
            return composed.Written && File.Exists(vi) ? null
                : $"the pylabview composer {(composed.Refused ? "refuses" : "failed on")} " +
                  $"{Path.GetFileName(aixml)}: {composed.Message}";
        }
        if (validateFirst)
        {
            var validation = await connection.InvokeAsync((c, t) =>
                c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            if (validation.ErrorCode != 0)
                return $"'{aixml}' does not validate: {validation.ErrorMessage}";
        }
        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = vi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        return generation.ErrorCode == 0 && File.Exists(vi) ? null
            : $"could not generate '{vi}': {generation.ErrorMessage} (error {generation.ErrorCode})";
    }
}
