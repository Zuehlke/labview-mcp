using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.GrpcReflection;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;
using Pbr = Google.Protobuf.Reflection;

namespace LabVIEWMcp.Tools;

[McpServerToolType]
internal sealed class StatusTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [McpServerTool(Name = "lvai_status", ReadOnly = true, Title = "LabVIEW gRPC connection status")]
    [Description("""
        Show whether LabVIEW's embedded AI gRPC server is reachable: the discovered port,
        how it was found, and the service list reported by server reflection. Start here -
        every other lvai_* tool depends on this connection. The port is ephemeral and
        changes on each LabVIEW restart, so it is re-discovered automatically.
        Also reports scriptsDirectory: the absolute path of the helper scripts shipped next to
        this server's exe. Use it instead of a relative path - it works from any working
        directory, and from a binary-only install with no repository checkout.
        `labviewHealth` counts the `DWarn` entries in NI's OWN crash log, which is where
        LabVIEW records faults - its crash handler means Windows Error Reporting never sees
        them, so an empty event log is not an alibi. A high count is a PRIOR, not a diagnosis:
        measured 2026-09-03, an instance carrying 200 of them answered every RPC normally while
        refusing class work with Error 1073 and Error 1562, and a restart cured both. Check it
        when a call fails in a way its arguments do not explain.
        """)]
    public async Task<string> StatusAsync(CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // Through InvokeAsync, never GetClientAsync. This tool's own description promises
            // that a LabVIEW restart is re-discovered automatically, and only InvokeAsync drops
            // a stale channel and re-scans. Measured: with the direct call, every lvai_status
            // after a LabVIEW restart returned "Unavailable: Error connecting to subchannel"
            // and kept doing so - the one tool that claimed to heal itself was the one that
            // could not, and a unary tool had to be called first to unstick it.
            var config = await connection.InvokeAsync((c, t) =>
                c.GetApplicationConfigurationAsync(new GetApplicationConfigurationRequest(),
                    deadline: Rpc.Deadline(15), cancellationToken: t).ResponseAsync, ct);

            var services = new JsonArray();
            string? reflectionError = null;
            try
            {
                foreach (var name in await ListServicesAsync(ct)) services.Add(name);
            }
            catch (Exception e)
            {
                reflectionError = $"{e.GetType().Name}: {e.Message}";
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["address"] = connection.Address,
                ["port"] = connection.Port,
                ["discoveredVia"] = connection.DiscoveredVia,
                ["applicationLanguage"] = config.Language,
                ["scriptsDirectory"] = ScriptsDirectory(),
                ["claudeAssetsDirectory"] = ClaudeAssetsDirectory(),
                // The add-on build the export cache is keyed to. Reported because a dropped cache
                // is otherwise invisible: the first read after an upgrade just takes longer, which
                // looks like a slow VI rather than an invalidation that was supposed to happen.
                ["aiAddonFingerprint"] = LvaiVersion.Compute(),
                ["aiAddonRecorded"] = LvaiVersion.Recorded(),
                ["services"] = services,
                ["reflectionError"] = reflectionError,
                // A LabVIEW that is running and answering can still be too sick to work.
                ["labviewHealth"] = Health(),
            }.ToJsonString(Indented);
        });

    /// <summary>
    /// What NI's own crash log says about this LabVIEW instance, counted rather than parsed.
    ///
    /// WHY THIS IS IN `lvai_status`. Measured 2026-09-03: a LabVIEW that answered every RPC
    /// normally was nevertheless refusing work - `lvai_bind_class_fields` failed 4/4 with
    /// `Error 1073` on `Move` in three different project configurations, INCLUDING one where no
    /// project held the class at all, and the accessor wizard answered `Error 1562`. After a
    /// restart the identical calls succeeded. The instance had arrived unhealthy: NI's log
    /// already carried 200 `DWarn` entries timestamped before the session began. Diagnosing
    /// that cost about 240 s of wall clock for 2.9 s inside LabVIEW.
    ///
    /// READ NI'S LOG, NOT THE WINDOWS EVENT LOG. LabVIEW installs its own crash handler, so
    /// Windows Error Reporting never sees these - an empty Application log is not an alibi.
    ///
    /// COUNTED, NOT DIAGNOSED, and the difference matters: a `DWarn` is not proof of anything
    /// and a high count is not a verdict. It is a cheap prior. `_cur.txt` is overwritten on the
    /// next start, so the count is for THIS instance's lifetime plus whatever it inherited.
    /// </summary>
    internal static JsonObject Health()
    {
        var temp = Path.GetTempPath();
        string? log = null;
        try
        {
            log = Directory.EnumerateFiles(temp, "LabVIEW_*_cur.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        if (log is null)
            return new JsonObject
            {
                ["logFound"] = false,
                ["note"] = "No LabVIEW_*_cur.txt in %TEMP%. That is the normal state for an "
                           + "instance that has never had a fault worth logging.",
            };

        string text;
        try
        {
            // SHARED, because LabVIEW HOLDS THIS FILE OPEN. `_cur.txt` is the log of the running
            // instance and it is written to as faults happen, so File.ReadAllText answers
            // "being used by another process" on exactly the station this check exists for -
            // caught by its own unit test on 2026-09-03, before it ever shipped.
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return new JsonObject
            {
                ["logFound"] = true,
                ["logPath"] = log,
                ["note"] = "The log could not be read: " + failure.Message,
            };
        }

        var warnings = Count(text, "DWarn");
        var lastSignature = text.Split(['\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.Contains("DWarn", StringComparison.Ordinal))?.Trim();

        return new JsonObject
        {
            ["logFound"] = true,
            ["logPath"] = log,
            ["logWrittenUtc"] = File.GetLastWriteTimeUtc(log).ToString("O"),
            ["dwarnCount"] = warnings,
            // COUNT AND SIZE BOTH, because the count alone was ambiguous: measured 2026-09-03, the
            // log was rewritten during a run - logWrittenUtc moved by an hour - while dwarnCount
            // stayed at exactly 200 and the last signature was byte-identical. Whether LabVIEW
            // rotates, caps or rewrites in place is unknown; a length makes the movement visible
            // instead of leaving it to be reconstructed afterwards.
            ["logBytes"] = text.Length,
            ["lastDwarn"] = lastSignature,
            ["looksDegraded"] = warnings >= 50,
            ["note"] = warnings == 0
                ? "No DWarn entries. Nothing here suggests a degraded instance."
                : warnings >= 50
                    ? $"{warnings} DWarn entries. THIS IS A PRIOR, NOT A DIAGNOSIS - but an "
                      + "instance in this state has been measured refusing work while still "
                      + "answering every RPC: Error 1073 on a private-data export with no "
                      + "project holding the class, and Error 1562 from the accessor wizard, "
                      + "both cured by a restart. If a class-editing call fails for no reason "
                      + "the project state explains, restart LabVIEW before hunting further."
                    : $"{warnings} DWarn entries. Low enough to be ordinary; read the log if a "
                      + "call fails in a way the arguments do not explain.",
        };
    }

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

    /// <summary>
    /// Absolute path of the scripts/ folder copied next to the exe at build time, or null when
    /// it is absent. Reported by lvai_status so an agent never has to guess a relative path:
    /// the MCP server's working directory is whatever the client chose, which is not the
    /// repository root, and a binary-only install has no repository at all.
    /// </summary>
    internal static string? ScriptsDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scripts");
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// Absolute path of the Claude Code assets copied next to the exe - the documentation agent,
    /// the tool allow-list and CLAUDE.md - or null when absent. The embedded documents are served
    /// by tools, but an AGENT has to exist as a file where Claude Code looks for it, so a
    /// binary-only install needs this path plus scripts\Install-ClaudeAssets.ps1.
    /// </summary>
    internal static string? ClaudeAssetsDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "claude");
        return Directory.Exists(path) ? path : null;
    }

    [McpServerTool(Name = "lvai_get_application_configuration", ReadOnly = true,
                   Title = "Get LabVIEW application configuration")]
    [Description("""
        RPC GetApplicationConfiguration. Returns the LabVIEW application configuration
        (currently just the UI language). Cheapest possible liveness probe.
        """)]
    public async Task<string> GetApplicationConfigurationAsync(CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.GetApplicationConfigurationAsync(
                    new GetApplicationConfigurationRequest(),
                    deadline: Rpc.Deadline(15), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_dump_schema", ReadOnly = true, Title = "Dump the served gRPC schema")]
    [Description("""
        Ask the running LabVIEW server for its own schema via gRPC server reflection and
        render it. Use this to detect schema drift: the interface is private and NI can
        change it between LabVIEW versions, so this is the authoritative list of what the
        installed version actually supports - not what this MCP server was built against.
        format: 'summary' (services, rpcs, message fields) or 'json' (raw FileDescriptorProto).
        """)]
    public async Task<string> DumpSchemaAsync(
        [Description("'summary' (default) or 'json'")] string format = "summary",
        [Description("Optional: also write the rendered output to this file path")]
        string? outputPath = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var descriptors = new List<Pbr.FileDescriptorProto>();
            foreach (var service in await ListServicesAsync(ct))
            {
                if (service.StartsWith("grpc.", StringComparison.Ordinal)) continue; // stock services
                foreach (var blob in await FilesForSymbolAsync(service, ct))
                {
                    var fd = Pbr.FileDescriptorProto.Parser.ParseFrom(blob);
                    if (descriptors.All(d => d.Name != fd.Name)) descriptors.Add(fd);
                }
            }

            var rendered = format.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? "[" + string.Join(",", descriptors.Select(d => Json.Node(d).ToJsonString())) + "]"
                : SchemaRenderer.RenderSummary(descriptors);

            string? written = null;
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                await File.WriteAllTextAsync(outputPath, rendered, ct);
                written = Path.GetFullPath(outputPath);
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["port"] = connection.Port,
                ["fileCount"] = descriptors.Count,
                ["writtenTo"] = written,
                ["schema"] = rendered,
            }.ToJsonString(Indented);
        });

    // ---------- reflection plumbing ----------

    private async Task<List<string>> ListServicesAsync(CancellationToken ct)
    {
        var reflection = await connection.GetReflectionClientAsync(ct);
        using var call = reflection.ServerReflectionInfo(
            deadline: Rpc.Deadline(20), cancellationToken: ct);

        await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" }, ct);
        await call.RequestStream.CompleteAsync();

        var names = new List<string>();
        while (await call.ResponseStream.MoveNext(ct))
        {
            var response = call.ResponseStream.Current;
            if (response.ErrorResponse is { } err)
                throw new InvalidOperationException(
                    $"reflection error {err.ErrorCode}: {err.ErrorMessage}");
            if (response.ListServicesResponse is { } list)
                names.AddRange(list.Service.Select(s => s.Name));
        }
        return names;
    }

    private async Task<List<byte[]>> FilesForSymbolAsync(string symbol, CancellationToken ct)
    {
        var reflection = await connection.GetReflectionClientAsync(ct);
        using var call = reflection.ServerReflectionInfo(
            deadline: Rpc.Deadline(20), cancellationToken: ct);

        await call.RequestStream.WriteAsync(
            new ServerReflectionRequest { FileContainingSymbol = symbol }, ct);
        await call.RequestStream.CompleteAsync();

        var blobs = new List<byte[]>();
        while (await call.ResponseStream.MoveNext(ct))
        {
            var response = call.ResponseStream.Current;
            if (response.ErrorResponse is { } err)
                throw new InvalidOperationException(
                    $"reflection error {err.ErrorCode}: {err.ErrorMessage}");
            if (response.FileDescriptorResponse is { } files)
                blobs.AddRange(files.FileDescriptorProto.Select(b => b.ToByteArray()));
        }
        return blobs;
    }

}
