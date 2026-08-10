using System.ComponentModel;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>Read-only introspection: describe VIs and projects, query the info cache.</summary>
[McpServerToolType]
internal sealed class InspectTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_describe_vi", ReadOnly = true, Title = "Describe a VI")]
    [Description("""
        RPC GetDescribeVIPromptInfo (server streaming). Returns a JSON description of a VI,
        including its AIXML representation under 'viXml' and, with getNodesInfo, the block
        diagram nodes. This is the primary way to READ LabVIEW code as text.
        The VI does not need to be open. Large VIs can take a while on first touch because
        LabVIEW has to load them.
        """)]
    public async Task<string> DescribeViAsync(
        [Description(@"Absolute path to the .vi file, e.g. C:\path\To\My.vi")] string viPath,
        [Description("Optional VI name; usually leave empty and let the path speak")]
        string? viName = null,
        [Description("Include block-diagram node information (bigger payload)")]
        bool getNodesInfo = true,
        [Description("Max stream messages to collect")] int maxMessages = 10,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // Opened AND drained inside InvokeAsync so a stale channel is re-discovered: a
            // stream cannot be handed a client up front and survive a LabVIEW restart. Safe to
            // retry because this is a read.
            return await connection.InvokeAsync(async (client, t) =>
            {
                using var call = client.GetDescribeVIPromptInfo(new GetDescribeVIPromptInfoRequest
                {
                    ViPath = viPath,
                    ViName = viName ?? "",
                    GetNodesInfo = getNodesInfo,
                }, deadline: Rpc.Deadline(timeoutSeconds + 15), cancellationToken: t);

                var (items, reason) = await Rpc.CollectAsync(
                    call.ResponseStream, maxMessages, timeoutSeconds, t);
                return Json.Stream(items, reason, maxMessages);
            }, ct);
        });

    [McpServerTool(Name = "lvai_vi_terminals", ReadOnly = true,
                   Title = "Terminal names a Call to this VI needs")]
    [Description("""
        The exact terminal names for putting a `Call` to some VI in AIXML - read out of that VI's
        own export, so no guessing and no hunting for another VI that already calls it.
        USE THIS after lvai_palette_index gives you a VI: that tool says a VI may be Called, this
        one says what its terminals are called, and they are not derivable. Measured on
        `Read Delimited Spreadsheet.vi`: `max characters/row  (no limit\3A0)` has TWO spaces,
        `delimiter (\\t)` has a doubled backslash, and the wrapper is polymorphic so a Call also
        needs an `instance`. Returns a ready-to-paste `Call` element.
        For a POLYMORPHIC VI it lists every instance with its own Call - note the attribute
        shuffle: the wrapper name is `target`, the instance name goes in `instance`.
        Read-only, and it does NOT burn the path for a later lvai_convert_aixml_to_vi - measured:
        exporting a VI leaves it regenerable, only lvai_open_file does not.
        The last line of the answer says whether the export came from the disk cache or from
        LabVIEW, the same way lvai_convert_vi_to_aixml reports it; pass refresh to force a
        re-export when you suspect a stale entry.
        """)]
    public async Task<string> ViTerminalsAsync(
        [Description(@"Absolute path to the .vi whose terminals you need")] string viPath,
        [Description("Re-export even when a cached export exists")] bool refresh = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            // Under the cache root, not %TEMP%: this file exists only to be parsed, and keeping
            // everything the server writes in one always-present place is worth more than the
            // habit of using TEMP for scratch. Measured safe - see CacheDirectory.Scratch.
            var scratch = Path.Combine(CacheDirectory.Scratch,
                $"terminals-{Path.GetFileNameWithoutExtension(viPath)}.xml");
            Directory.CreateDirectory(CacheDirectory.Scratch);

            // Through the export cache, both ways. This tool used to call the RPC directly and so
            // never touched the cache at all - measured by watching the cache directory during a
            // VI-generator run: the run produced no entries, because a terminal lookup was the only
            // export it did. That is backwards. A palette VI's terminals are the single most
            // repeated read in the whole workflow - every generator run looks up the same handful -
            // and it was the one read paying a full LabVIEW export every time.
            var fromCache = !refresh && AixmlExportStore.TryCopyTo(viPath, scratch);

            // Where the export came from, in the caller's words rather than only in the file
            // timestamps. Three VI-generator runs reported this omission independently - the tool
            // used the cache but said nothing about it, so a hit and a fresh export looked
            // identical and the only way to tell them apart was to watch the cache directory from
            // outside. That is exactly the invisible-invalidation complaint this repository makes
            // about everything else, so it does not get to stand here.
            string provenance;

            if (fromCache)
            {
                provenance = AixmlExportStore.CachedUtc(viPath) is { } taken
                    ? $"Export served from the cache, taken {taken:yyyy-MM-dd HH:mm}Z - no LabVIEW " +
                      "round trip. Pass refresh to re-export."
                    : "Export served from the cache - no LabVIEW round trip. Pass refresh to re-export.";
            }
            else
            {
                var response = await connection.InvokeAsync((c, t) =>
                    c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                    {
                        ViPath = viPath,
                        AiXMLFilePath = scratch,
                    }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

                if (response.ErrorCode != 0)
                    return Json.Error("exportFailed",
                        $"Could not export '{viPath}': {response.ErrorMessage}",
                        new { viPath, errorCode = response.ErrorCode });

                // Only installation VIs are cacheable; Save decides that and reports false for the
                // rest, which is not a failure - but it IS the difference between "next read is
                // free" and "next read costs this again", so the caller is told which.
                provenance = AixmlExportStore.Save(viPath, scratch)
                    ? "Exported from LabVIEW and written to the cache; the next read of this VI is " +
                      "a file copy."
                    : "Exported from LabVIEW, NOT cached - only VIs inside the LabVIEW installation " +
                      "are, because an export depends on subVIs a per-VI key cannot see.";
            }

            // The RPC writes the file; the content is not on the response.
            var xml = File.Exists(scratch) ? await File.ReadAllTextAsync(scratch, ct) : null;

            var parsed = ViTerminals.Parse(xml);
            if (parsed is null)
                return Json.Error("exportUnreadable",
                    "The export reported success but no readable AIXML was written.",
                    new { viPath, exportPath = scratch, xmlBytes = xml?.Length ?? 0 });

            // A childless <VI> is the documented silent failure: the diagram was withheld, not
            // absent. Saying "0 terminals" here would be the empty answer this tool exists to
            // prevent.
            if (parsed.Inputs.Count == 0 && parsed.Outputs.Count == 0 && parsed.Instances.Count == 0)
                return Json.Error("noTerminalsFound",
                    $"'{parsed.ViName}' exported with no controls, indicators or instances. For a " +
                    "200-byte export that means the diagram was not readable - password-protected " +
                    "or otherwise withheld - not that the VI has no terminals.",
                    new { viPath, viName = parsed.ViName, xmlBytes = xml?.Length ?? 0 });

            return ViTerminals.Render(parsed) + Environment.NewLine + Environment.NewLine +
                   provenance;
        });

    [McpServerTool(Name = "lvai_describe_project", ReadOnly = true, Title = "Describe a LabVIEW project")]
    [Description("""
        RPC GetDescribeProjectPromptInfo (server streaming). Returns a JSON description of a
        .lvproj - its items, hierarchy and targets. Use this before describing individual VIs
        to find out what a project actually contains.
        """)]
    public async Task<string> DescribeProjectAsync(
        [Description(@"Absolute path to the .lvproj file")] string projectPath,
        [Description("Optional project name; usually leave empty")] string? projectName = null,
        [Description("Max stream messages to collect")] int maxMessages = 10,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            return await connection.InvokeAsync(async (client, t) =>
            {
                using var call = client.GetDescribeProjectPromptInfo(
                    new GetDescribeProjectPromptInfoRequest
                    {
                        ProjectPath = projectPath,
                        ProjectName = projectName ?? "",
                    }, deadline: Rpc.Deadline(timeoutSeconds + 15), cancellationToken: t);

                var (items, reason) = await Rpc.CollectAsync(
                    call.ResponseStream, maxMessages, timeoutSeconds, t);
                return Json.Stream(items, reason, maxMessages);
            }, ct);
        });

    [McpServerTool(Name = "lvai_search_info_cache", ReadOnly = true, Title = "Search the LabVIEW info cache")]
    [Description("""
        RPC SearchInfoCache (server streaming). Full-text search over LabVIEW's info cache
        (palette items, examples, help). Returns matches as JSON in 'infoJson'.
        NOTE: observed returning an empty list on a station whose cache is not populated -
        an empty result is not necessarily an error. Feed resulting GUIDs into
        lvai_lookup_info_cache_items for detail.
        """)]
    public async Task<string> SearchInfoCacheAsync(
        [Description("Search terms, comma or newline separated")] string searchTerms,
        [Description("Optional tag filters, comma separated")] string? tagFilters = null,
        [Description("Case sensitive matching")] bool caseSensitive = false,
        [Description("Max hits requested from the server (0 = server default)")] int limit = 20,
        [Description("Result offset for paging")] int offset = 0,
        [Description("Max stream messages to collect")] int maxMessages = 10,
        [Description("Local budget in seconds")] int timeoutSeconds = 60,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var request = new SearchInfoCacheRequest
            {
                CaseSensitive = caseSensitive,
                Limit = limit,
                Offset = offset,
            };
            request.SearchTerms.AddRange(Rpc.SplitList(searchTerms));
            request.TagFilters.AddRange(Rpc.SplitList(tagFilters));

            return await connection.InvokeAsync(async (client, t) =>
            {
                using var call = client.SearchInfoCache(
                    request, deadline: Rpc.Deadline(timeoutSeconds + 15), cancellationToken: t);

                var (items, reason) = await Rpc.CollectAsync(
                    call.ResponseStream, maxMessages, timeoutSeconds, t);
                return Json.Stream(items, reason, maxMessages);
            }, ct);
        });

    [McpServerTool(Name = "lvai_lookup_info_cache_items", ReadOnly = true,
                   Title = "Look up info cache items by GUID")]
    [Description("""
        RPC LookupInfoCacheItems (server streaming). Detail lookup for known cache GUIDs.
        getPrototype returns the CONNECTOR PANE of a palette VI - that is what you need to
        wire a node correctly when generating AIXML.
        """)]
    public async Task<string> LookupInfoCacheItemsAsync(
        [Description("Cache item GUIDs, comma or newline separated")] string guids,
        [Description("Include standard item info")] bool getStandardInfo = true,
        [Description("Include block-diagram node info")] bool getNodesInfo = false,
        [Description("Include the connector pane / prototype")] bool getPrototype = true,
        [Description("Max stream messages to collect")] int maxMessages = 20,
        [Description("Local budget in seconds")] int timeoutSeconds = 60,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var request = new LookupInfoCacheItemsRequest
            {
                GetStandardInfo = getStandardInfo,
                GetNodesInfo = getNodesInfo,
                GetPrototype = getPrototype,
            };
            request.Guids.AddRange(Rpc.SplitList(guids));

            return await connection.InvokeAsync(async (client, t) =>
            {
                using var call = client.LookupInfoCacheItems(
                    request, deadline: Rpc.Deadline(timeoutSeconds + 15), cancellationToken: t);

                var (items, reason) = await Rpc.CollectAsync(
                    call.ResponseStream, maxMessages, timeoutSeconds, t);
                return Json.Stream(items, reason, maxMessages);
            }, ct);
        });

    [McpServerTool(Name = "lvai_filter_palette_search_candidates", ReadOnly = true,
                   Title = "Filter palette search candidates")]
    [Description("""
        RPC FilterPaletteSearchCandidates (unary). Given palette item GUIDs, returns the
        displayable details LabVIEW keeps for them: title, description, icon, palette
        hierarchy and help path. This is the resolver behind LabVIEW's AI palette search.
        """)]
    public async Task<string> FilterPaletteSearchCandidatesAsync(
        [Description("Palette item GUIDs, comma or newline separated")] string itemGuids,
        [Description("Local budget in seconds")] int timeoutSeconds = 60,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var request = new FilterPaletteSearchCandidatesRequest();
            request.ItemGuids.AddRange(Rpc.SplitList(itemGuids));

            var response = await connection.InvokeAsync((c, t) =>
                c.FilterPaletteSearchCandidatesAsync(
                    request, deadline: Rpc.Deadline(timeoutSeconds),
                    cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_filter_example_search_candidates", ReadOnly = true,
                   Title = "Filter example search candidates")]
    [Description("""
        RPC FilterExampleSearchCandidates (unary). Given example VI paths, returns each
        example's description as LabVIEW knows it.
        """)]
    public async Task<string> FilterExampleSearchCandidatesAsync(
        [Description("Example VI paths, comma or newline separated")] string examplePaths,
        [Description("Local budget in seconds")] int timeoutSeconds = 60,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var request = new FilterExampleSearchCandidatesRequest();
            request.ExamplePaths.AddRange(Rpc.SplitList(examplePaths));

            var response = await connection.InvokeAsync((c, t) =>
                c.FilterExampleSearchCandidatesAsync(
                    request, deadline: Rpc.Deadline(timeoutSeconds),
                    cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });
}
