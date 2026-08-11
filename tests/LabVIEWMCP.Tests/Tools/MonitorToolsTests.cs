using Grpc.Core;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class MonitorProjectChangesTests
{
    [Fact]
    public async Task Collects_change_events_with_their_update_type()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 2;

        var result = await new MonitorTools(server.Connection)
            .MonitorProjectChangesAsync(maxMessages: 5, timeoutSeconds: 10);

        Assert.Equal(2, Res.Int(result, "messageCount"));
        var first = Res.Arr(result, "messages")[0]!;
        Assert.Equal("Item0.vi", first["itemName"]!.GetValue<string>());
        Assert.Contains("MODIFIED", first["updateType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Honours_the_message_limit()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 10;

        var result = await new MonitorTools(server.Connection)
            .MonitorProjectChangesAsync(maxMessages: 1, timeoutSeconds: 10);

        Assert.Equal(1, Res.Int(result, "messageCount"));
        Assert.Equal("limit reached", Res.Str(result, "stopReason"));
    }

    [Fact]
    public async Task A_quiet_ide_produces_a_timeout_which_is_a_normal_result()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 0;
        server.Service.StreamForever = true;

        var result = await new MonitorTools(server.Connection)
            .MonitorProjectChangesAsync(maxMessages: 5, timeoutSeconds: 1);

        Assert.Equal(0, Res.Int(result, "messageCount"));
        Assert.Equal("timeout", Res.Str(result, "stopReason"));
        Assert.False(Res.Has(result, "ok"));       // a timeout is not an error envelope
    }
}

public class MonitorBidiTests
{
    [Fact]
    public async Task Discuss_vi_receives_the_inbound_item_and_labels_the_direction()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorDiscussViAsync(timeoutSeconds: 10);

        Assert.Equal(1, Res.Int(result, "messageCount"));
        Assert.Contains("LabVIEW -> client", Res.Str(result, "direction"));
        Assert.False(Res.Bool(result, "readyMessageSent"));
        Assert.False(Res.Bool(result, "replySent"));

        var item = Res.Arr(result, "messages")[0]!;
        Assert.Equal("Discussed.vi", item["viName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Palette_search_carries_the_search_string_and_guid_to_echo()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorPaletteSearchesAsync(timeoutSeconds: 10);

        var item = Res.Arr(result, "messages")[0]!;
        Assert.Contains("write file", item["searchString"]!.GetValue<string>());
        Assert.Equal("palette-guid-1", item["guid"]!.GetValue<string>());
    }

    [Fact]
    public async Task Example_search_carries_its_payload()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorExampleSearchesAsync(timeoutSeconds: 10);

        Assert.Equal("example-guid-1",
            Res.Arr(result, "messages")[0]!["guid"]!.GetValue<string>());
    }

    [Fact]
    public async Task Code_completion_carries_the_user_prompt()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorCodeCompletionAsync(timeoutSeconds: 10);

        var item = Res.Arr(result, "messages")[0]!;
        Assert.Equal("add two numbers", item["request"]!.GetValue<string>());
        Assert.False(item["incomplete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Front_panel_cleanup_carries_the_panel_state()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorFrontPanelCleanupAsync(timeoutSeconds: 10);

        Assert.Equal("fp-guid-1", Res.Arr(result, "messages")[0]!["guid"]!.GetValue<string>());
    }

    [Fact]
    public async Task sendReady_puts_a_registration_message_on_the_wire()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorCodeCompletionAsync(sendReady: true, timeoutSeconds: 10);

        Assert.True(Res.Bool(result, "readyMessageSent"));
        await server.Service.WaitForAsync("MonitorCodeCompletion:in");
    }

    [Fact]
    public async Task A_reply_is_written_back_on_the_request_stream()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection).MonitorCodeCompletionAsync(
            replyJson: """
                {"guid":"cc-guid-1","suggestions":[{"changes":"<VI/>","rationale":"adds"}]}
                """,
            timeoutSeconds: 10);

        Assert.True(Res.Bool(result, "replySent"));
        await server.Service.WaitForAsync("MonitorCodeCompletion:in");

        var inbound = server.Service.Last<MonitorCodeCompletionRequest>("MonitorCodeCompletion:in");
        Assert.Equal("cc-guid-1", inbound.Guid);
        var suggestion = Assert.Single(inbound.Suggestions);
        Assert.Equal("<VI/>", suggestion.Changes);
    }

    [Fact]
    public async Task A_palette_reply_round_trips_its_repeated_items()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new MonitorTools(server.Connection).MonitorPaletteSearchesAsync(
            replyJson: """
                {"guid":"palette-guid-1","items":[{"itemGuid":"g1","rationale":"writes files"}]}
                """,
            timeoutSeconds: 10);

        await server.Service.WaitForAsync("MonitorPaletteSearches:in");
        var inbound = server.Service.Last<MonitorPaletteSearchesRequest>("MonitorPaletteSearches:in");
        Assert.Equal("g1", Assert.Single(inbound.Items).ItemGuid);
    }

    [Fact]
    public async Task A_malformed_reply_is_rejected_before_an_event_is_consumed()
    {
        // Parsing happens BEFORE the stream opens, so a bad reply must not eat a real request.
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorCodeCompletionAsync(replyJson: "{not json", timeoutSeconds: 10);

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("ArgumentException", Res.Str(result, "errorKind"));
        Assert.Equal(0, server.Service.CountOf("MonitorCodeCompletion"));
    }

    [Fact]
    public async Task A_reply_for_the_wrong_message_type_is_rejected()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new MonitorTools(server.Connection)
            .MonitorCodeCompletionAsync(replyJson: """{"examples":[]}""", timeoutSeconds: 10);

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal(0, server.Service.CountOf("MonitorCodeCompletion"));
    }

    [Fact]
    public async Task No_reply_is_sent_when_nothing_arrived()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.BidiPushCount = 0;
        server.Service.StreamForever = true;

        var result = await new MonitorTools(server.Connection).MonitorCodeCompletionAsync(
            replyJson: """{"guid":"x"}""", timeoutSeconds: 1);

        Assert.Equal(0, Res.Int(result, "messageCount"));
        Assert.Equal("timeout", Res.Str(result, "stopReason"));
        Assert.False(Res.Bool(result, "replySent"));
    }

    [Fact]
    public async Task A_quiet_monitor_times_out_without_being_an_error()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.BidiPushCount = 0;
        server.Service.StreamForever = true;

        var result = await new MonitorTools(server.Connection)
            .MonitorDiscussViAsync(timeoutSeconds: 1);

        Assert.Equal(0, Res.Int(result, "messageCount"));
        Assert.Equal("timeout", Res.Str(result, "stopReason"));
        Assert.False(Res.Has(result, "ok"));
    }

    [Fact]
    public async Task Collects_several_pushes_when_asked()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.BidiPushCount = 3;

        var result = await new MonitorTools(server.Connection)
            .MonitorPaletteSearchesAsync(maxMessages: 3, timeoutSeconds: 10);

        Assert.Equal(3, Res.Int(result, "messageCount"));
    }

    [Fact]
    public async Task An_rpc_failure_on_the_monitor_is_reported_as_data()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        server.Service.FailWith = StatusCode.Unimplemented;
        server.Service.FailOnMethod = "MonitorDiscussVI";

        var result = await new MonitorTools(server.Connection)
            .MonitorDiscussViAsync(timeoutSeconds: 10);

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("rpc", Res.Str(result, "errorKind"));
    }
}
