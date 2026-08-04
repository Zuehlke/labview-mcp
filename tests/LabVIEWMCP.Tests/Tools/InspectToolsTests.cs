using Grpc.Core;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class InspectDescribeViTests
{
    [Fact]
    public async Task Maps_every_request_field_onto_the_rpc()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection)
            .DescribeViAsync(@"C:\p\My.vi", "My.vi", getNodesInfo: true);

        var request = server.Service.Last<GetDescribeVIPromptInfoRequest>("GetDescribeVIPromptInfo");
        Assert.Equal(@"C:\p\My.vi", request.ViPath);
        Assert.Equal("My.vi", request.ViName);
        Assert.True(request.GetNodesInfo);
    }

    [Fact]
    public async Task An_omitted_vi_name_is_sent_as_empty_not_null()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection).DescribeViAsync(@"C:\p\My.vi");

        Assert.Equal("", server.Service
            .Last<GetDescribeVIPromptInfoRequest>("GetDescribeVIPromptInfo").ViName);
    }

    [Fact]
    public async Task getNodesInfo_false_is_forwarded()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection)
            .DescribeViAsync(@"C:\p\My.vi", getNodesInfo: false);

        Assert.False(server.Service
            .Last<GetDescribeVIPromptInfoRequest>("GetDescribeVIPromptInfo").GetNodesInfo);
    }

    [Fact]
    public async Task Collects_the_streamed_messages()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 3;
        server.Service.InfoJson = "{\"viName\":\"X\"}";

        var result = await new InspectTools(server.Connection).DescribeViAsync(@"C:\p\My.vi");

        Assert.Equal(3, Res.Int(result, "messageCount"));
        Assert.Equal("stream completed", Res.Str(result, "stopReason"));
        Assert.Contains("viName", Res.Arr(result, "messages")[0]!["infoJson"]!.GetValue<string>());
    }

    [Fact]
    public async Task Honours_maxMessages_and_reports_the_limit()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 10;

        var result = await new InspectTools(server.Connection)
            .DescribeViAsync(@"C:\p\My.vi", maxMessages: 2);

        Assert.Equal(2, Res.Int(result, "messageCount"));
        Assert.Equal(2, Res.Int(result, "limit"));
        Assert.Equal("limit reached", Res.Str(result, "stopReason"));
    }

    [Fact]
    public async Task An_open_ended_stream_times_out_with_partial_results()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 1;
        server.Service.StreamForever = true;

        var result = await new InspectTools(server.Connection)
            .DescribeViAsync(@"C:\p\My.vi", maxMessages: 50, timeoutSeconds: 1);

        Assert.Equal(1, Res.Int(result, "messageCount"));
        Assert.Equal("timeout", Res.Str(result, "stopReason"));
    }

    [Fact]
    public async Task An_rpc_failure_is_reported_as_data()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        server.Service.FailWith = StatusCode.NotFound;
        server.Service.FailOnMethod = "GetDescribeVIPromptInfo";
        server.Service.FailDetail = "VI not found";

        var result = await new InspectTools(server.Connection).DescribeViAsync(@"C:\nope.vi");

        Assert.False(Res.Bool(result, "ok"));
        Assert.Contains("VI not found", Res.Str(result, "error"));
    }
}

public class InspectDescribeProjectTests
{
    [Fact]
    public async Task Maps_project_path_and_name()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection)
            .DescribeProjectAsync(@"C:\p\App.lvproj", "App.lvproj");

        var request = server.Service
            .Last<GetDescribeProjectPromptInfoRequest>("GetDescribeProjectPromptInfo");
        Assert.Equal(@"C:\p\App.lvproj", request.ProjectPath);
        Assert.Equal("App.lvproj", request.ProjectName);
    }

    [Fact]
    public async Task An_omitted_name_is_sent_as_empty()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection).DescribeProjectAsync(@"C:\p\App.lvproj");

        Assert.Equal("", server.Service
            .Last<GetDescribeProjectPromptInfoRequest>("GetDescribeProjectPromptInfo").ProjectName);
    }

    [Fact]
    public async Task Collects_the_stream()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 2;

        var result = await new InspectTools(server.Connection)
            .DescribeProjectAsync(@"C:\p\App.lvproj");

        Assert.Equal(2, Res.Int(result, "messageCount"));
    }
}

public class InspectSearchInfoCacheTests
{
    [Fact]
    public async Task Splits_the_search_terms_and_tag_filters()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection)
            .SearchInfoCacheAsync("file, write\nTDMS", "io;stream");

        var request = server.Service.Last<SearchInfoCacheRequest>("SearchInfoCache");
        Assert.Equal(["file", "write", "TDMS"], request.SearchTerms);
        Assert.Equal(["io", "stream"], request.TagFilters);
    }

    [Fact]
    public async Task Forwards_paging_and_case_sensitivity()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection)
            .SearchInfoCacheAsync("x", caseSensitive: true, limit: 5, offset: 10);

        var request = server.Service.Last<SearchInfoCacheRequest>("SearchInfoCache");
        Assert.True(request.CaseSensitive);
        Assert.Equal(5, request.Limit);
        Assert.Equal(10, request.Offset);
    }

    [Fact]
    public async Task Omitted_tag_filters_send_an_empty_list()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection).SearchInfoCacheAsync("x");

        Assert.Empty(server.Service.Last<SearchInfoCacheRequest>("SearchInfoCache").TagFilters);
    }

    [Fact]
    public async Task An_empty_result_set_is_a_normal_outcome_not_an_error()
    {
        // Measured against real LabVIEW: an unpopulated cache legitimately returns nothing.
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 0;

        var result = await new InspectTools(server.Connection).SearchInfoCacheAsync("nothing");

        Assert.Equal(0, Res.Int(result, "messageCount"));
        Assert.Equal("stream completed", Res.Str(result, "stopReason"));
        Assert.False(Res.Has(result, "ok"));      // not an error envelope
    }

    [Fact]
    public async Task Returns_the_cache_payloads()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 1;

        var result = await new InspectTools(server.Connection).SearchInfoCacheAsync("x");

        Assert.Contains("hit", Res.Arr(result, "messages")[0]!["infoJson"]!.GetValue<string>());
    }
}

public class InspectLookupInfoCacheItemsTests
{
    [Fact]
    public async Task Splits_guids_and_forwards_the_detail_flags()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection).LookupInfoCacheItemsAsync(
            "g1,g2\ng3", getStandardInfo: false, getNodesInfo: true, getPrototype: true);

        var request = server.Service.Last<LookupInfoCacheItemsRequest>("LookupInfoCacheItems");
        Assert.Equal(["g1", "g2", "g3"], request.Guids);
        Assert.False(request.GetStandardInfo);
        Assert.True(request.GetNodesInfo);
        Assert.True(request.GetPrototype);
    }

    [Fact]
    public async Task Defaults_ask_for_standard_info_and_the_connector_pane()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new InspectTools(server.Connection).LookupInfoCacheItemsAsync("g1");

        var request = server.Service.Last<LookupInfoCacheItemsRequest>("LookupInfoCacheItems");
        Assert.True(request.GetStandardInfo);
        Assert.True(request.GetPrototype);
        Assert.False(request.GetNodesInfo);
    }

    [Fact]
    public async Task An_empty_guid_list_still_issues_the_call()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new InspectTools(server.Connection).LookupInfoCacheItemsAsync("");

        Assert.Empty(server.Service.Last<LookupInfoCacheItemsRequest>("LookupInfoCacheItems").Guids);
        Assert.True(Res.Has(result, "messageCount"));
    }
}

public class InspectFilterTests
{
    [Fact]
    public async Task Palette_filter_splits_guids_and_returns_the_resolved_items()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new InspectTools(server.Connection)
            .FilterPaletteSearchCandidatesAsync("guid-a, guid-b");

        Assert.Equal(["guid-a", "guid-b"], server.Service
            .Last<FilterPaletteSearchCandidatesRequest>("FilterPaletteSearchCandidates").ItemGuids);

        var items = Res.Arr(result, "items");
        Assert.Equal(2, items.Count);
        Assert.Equal("guid-a", items[0]!["id"]!.GetValue<string>());
        Assert.Equal("Programming>>File IO", items[0]!["paletteHierarchy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Example_filter_splits_paths_and_returns_descriptions()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new InspectTools(server.Connection)
            .FilterExampleSearchCandidatesAsync(@"C:\a.vi" + "\n" + @"C:\b.vi");

        Assert.Equal([@"C:\a.vi", @"C:\b.vi"], server.Service
            .Last<FilterExampleSearchCandidatesRequest>("FilterExampleSearchCandidates").ExamplePaths);

        var examples = Res.Arr(result, "examples");
        Assert.Equal(2, examples.Count);
        Assert.Equal(@"C:\a.vi", examples[0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_empty_input_yields_an_empty_result_rather_than_an_error()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new InspectTools(server.Connection)
            .FilterPaletteSearchCandidatesAsync("");

        Assert.Empty(Res.Arr(result, "items"));
    }
}
