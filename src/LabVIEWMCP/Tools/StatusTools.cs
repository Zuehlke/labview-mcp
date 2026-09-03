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
        LabVIEW records faults - its crash handler means Windows Error Reporting never sees them,
        so an empty event log is not an alibi. USE IT AFTER A FAILURE, NEVER AS A GATE BEFORE
        WORK: measured 2026-09-03 over two runs, it was wrong in BOTH directions - an instance at
        200 completed a whole cold class build without incident, and an instance reading 0
        crashed inside ConvertAIXMLToVI minutes later. What it is good for is the retrospective
        question: a class-editing call has failed for no reason the project state explains, so did
        the count move, and does a restart cure it? The log is reset at start, so the count covers
        this instance only.
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
    /// and a high count is not a verdict. `_cur.txt` is overwritten at start - it carries ONE
    /// `#Date:` header - so the count is for this instance's lifetime and nothing earlier.
    ///
    /// IT HAS BEEN WRONG IN BOTH DIRECTIONS, measured 2026-09-03 over two runs: 200 was followed by
    /// a flawless cold build, and 0 - a log reset minutes before - was followed by a crash inside
    /// `ConvertAIXMLToVI`. Treat it as a record of what has already happened, useful AFTER an
    /// unexplained failure, and never as a gate before starting work.
    ///
    /// A caution for anyone reading the file directly: comparing two SIZES across a restart looks
    /// like evidence of appending and is not - the second reading is a different, fast-growing
    /// file. Count the `#Date:` headers instead. An agent drew exactly that wrong conclusion here.
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

        // SATURATED AT 200, so the number is a FLOOR once it gets there. Measured 2026-09-03
        // across four captures of this log: three read exactly 200 at three different file sizes
        // (1.94, 1.98 and 2.00 MB) while others read 16 and 146. A count that stops dead at a
        // round number while the file keeps growing is a cap, not a coincidence - and reporting
        // "200" as a magnitude invites the reader to compare two saturated logs and conclude
        // nothing changed. NI documents no such limit; this is inferred from the four readings and
        // is labelled as inference rather than fact.
        const int knownCap = 200;
        var saturated = warnings >= knownCap;

        return new JsonObject
        {
            ["logFound"] = true,
            ["logPath"] = log,
            ["logWrittenUtc"] = File.GetLastWriteTimeUtc(log).ToString("O"),
            ["dwarnCount"] = warnings,
            ["dwarnCountSaturated"] = saturated,
            // COUNT AND SIZE BOTH, because the count alone was ambiguous: measured 2026-09-03, the
            // log was rewritten during a run - logWrittenUtc moved by an hour - while dwarnCount
            // stayed at exactly 200 and the last signature was byte-identical. Whether LabVIEW
            // rotates, caps or rewrites in place is unknown; a length makes the movement visible
            // instead of leaving it to be reconstructed afterwards.
            ["logBytes"] = text.Length,
            ["lastDwarn"] = lastSignature,
            // SIGNATURE, NOT JUST COUNT. `0xECE53844 DestroyPlatformEvent` is LabVIEW
            // failing to release an OS event handle during its own housekeeping, and it is
            // measured harmless: runs 10, 11 and 12 produced 17, 26 and 18 of them with no
            // crash, no restart and every artefact correct. Counting them made this field
            // read `true` through a completely clean run, which is worse than not having it.
            // A log carrying ONLY that signature is not degraded however many there are.
            ["looksDegraded"] = warnings >= 50 && HasSignatureOtherThanBenignTeardown(text),
            // `looksDegraded` HAS NOW BEEN WRONG IN BOTH DIRECTIONS, and saying so is the
            // point of this field rather than a caveat on it. Measured 2026-09-03 over two runs:
            // an instance at 200 completed a full cold class build, a typedef binding, ten
            // accessors and five suite runs without incident; an instance at 0 - a log reset
            // minutes earlier - CRASHED during lvai_create_class. So the count records trouble
            // that HAS happened; it does not predict trouble that WILL. Keep using it the way
            // the note says - as something to check AFTER an unexplained failure - and do not
            // gate work on it.
            ["note"] = warnings == 0
                ? "No DWarn entries in this instance's log. That is NOT a promise of health: an "
                  + "instance reading 0 has been measured crashing minutes later, because the log "
                  + "is reset at start and records only what has already gone wrong."
                : saturated
                    ? $"AT LEAST {knownCap} DWarn entries - the count is SATURATED and is a "
                      + "FLOOR, not a magnitude. Measured 2026-09-03: three captures read "
                      + "exactly 200 at three different file sizes (1.94, 1.98, 2.00 MB) "
                      + "while others read 16 and 146. So two saturated logs cannot be "
                      + "compared with each other, and a count that is NOT RISING is no "
                      + "evidence that nothing new went wrong - read `lastDwarn` instead. "
                      + "NI documents no such limit; this is inferred from four readings. "
                      + "A RECORD OF WHAT HAS ALREADY GONE WRONG, not a prediction - an "
                      + "instance at this level has also been measured completing a whole "
                      + "cold class build without incident. Its use is RETROSPECTIVE: when "
                      + "a class-editing call fails for no reason the project state "
                      + "explains - Error 1073 on a private-data export, Error 1562 from "
                      + "the accessor wizard - restart LabVIEW before hunting further. Do "
                      + "not gate work on this number."
                : warnings >= 50
                    ? $"{warnings} DWarn entries. A RECORD OF WHAT HAS ALREADY GONE WRONG, "
                      + "not a prediction - an instance at this level has also been measured "
                      + "completing a whole cold class build without incident. Its use is "
                      + "RETROSPECTIVE: when a class-editing call fails for no reason the project "
                      + "state explains - Error 1073 on a private-data export, Error 1562 from the "
                      + "accessor wizard - check whether the count moved and restart LabVIEW "
                      + "before hunting further. Do not gate work on this number."
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


    /// <summary>
    /// True when the log carries any DWarn signature OTHER than the benign event-handle teardown.
    ///
    /// WHY THIS EXISTS. `0xECE53844 DestroyPlatformEvent failed with MgErr 42` is LabVIEW failing
    /// to release an OS event handle during housekeeping, marked `NOT InExec` - not our code, and
    /// measured harmless across three full cold class builds (17, 26 and 18 events; no crash, no
    /// restart, every artefact correct). Before this, `looksDegraded` counted them and read `true`
    /// through an entirely clean run, which teaches a reader to ignore the field.
    ///
    /// It is deliberately a DENY-LIST OF ONE. The signatures that have preceded real deaths -
    /// `OMAutoClasses`, `BadLinkerObjs`, `HeapObjMapImpl` - keep counting, and so does anything
    /// new, because an unrecognised signature is the case where a warning is worth most.
    /// </summary>
    internal static bool HasSignatureOtherThanBenignTeardown(string text)
    {
        const string benign = "0xECE53844";
        // LF spelled numerically, because an escape in this position has now been eaten in
        // transit twice by the shell that wrote the patch.
        foreach (var line in text.Split((char)10))
        {
            if (!line.Contains("DWarn", StringComparison.Ordinal)) continue;
            if (!line.Contains(benign, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
