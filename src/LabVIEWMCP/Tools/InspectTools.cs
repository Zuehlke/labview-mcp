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
