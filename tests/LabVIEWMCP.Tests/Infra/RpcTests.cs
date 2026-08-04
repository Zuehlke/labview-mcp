using Grpc.Core;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class RpcSplitListTests
{
    [Fact]
    public void Null_and_blank_yield_no_entries()
    {
        Assert.Empty(Rpc.SplitList(null));
        Assert.Empty(Rpc.SplitList(""));
        Assert.Empty(Rpc.SplitList("   "));
    }

    [Fact]
    public void Splits_on_comma_semicolon_and_newlines_and_trims()
    {
        var parts = Rpc.SplitList(" a , b;c\nd\r\ne ");
        Assert.Equal(["a", "b", "c", "d", "e"], parts);
    }

    [Fact]
    public void Drops_empty_segments_from_repeated_separators()
    {
        Assert.Equal(["a", "b"], Rpc.SplitList("a,,,;\n,b"));
    }

    [Fact]
    public void Single_value_without_separator_is_kept()
    {
        Assert.Equal([@"C:\path\My.vi"], Rpc.SplitList(@"C:\path\My.vi"));
    }
}

public class RpcDeadlineTests
{
    [Fact]
    public void Is_in_the_future_by_the_requested_amount()
    {
        var deadline = Rpc.Deadline(30);
        var seconds = (deadline - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(seconds, 25, 31);
    }

    [Fact]
    public void Clamps_non_positive_up_to_one_second()
    {
        foreach (var input in new[] { 0, -5, int.MinValue })
        {
            var seconds = (Rpc.Deadline(input) - DateTime.UtcNow).TotalSeconds;
            Assert.InRange(seconds, 0, 1.5);
        }
    }

    [Fact]
    public void Clamps_absurd_values_down_to_an_hour()
    {
        var seconds = (Rpc.Deadline(int.MaxValue) - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(seconds, 3595, 3601);
    }

    [Fact]
    public void Returns_utc_so_grpc_does_not_shift_it()
    {
        Assert.Equal(DateTimeKind.Utc, Rpc.Deadline(10).Kind);
    }
}

public class RpcParseJsonTests
{
    [Fact]
    public void Blank_input_yields_a_default_message()
    {
        var request = Rpc.ParseJson<MonitorCodeCompletionRequest>(null);
        Assert.Equal("", request.Guid);
        Assert.Empty(request.Suggestions);
    }

    [Fact]
    public void Parses_protobuf_json_including_nested_repeated_fields()
    {
        var request = Rpc.ParseJson<MonitorCodeCompletionRequest>(
            """{"guid":"g1","suggestions":[{"changes":"<VI/>","rationale":"because"}],"errorCode":7}""");

        Assert.Equal("g1", request.Guid);
        Assert.Equal(7, request.ErrorCode);
        var suggestion = Assert.Single(request.Suggestions);
        Assert.Equal("<VI/>", suggestion.Changes);
        Assert.Equal("because", suggestion.Rationale);
    }

    [Fact]
    public void Unknown_field_is_rejected_with_a_helpful_message()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Rpc.ParseJson<MonitorCodeCompletionRequest>("""{"nope":1}"""));
        Assert.Contains("MonitorCodeCompletionRequest", error.Message);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Rpc.ParseJson<MonitorCodeCompletionRequest>("{not json"));
    }
}

public class RpcParseStringMapTests
{
    [Fact]
    public void Blank_input_yields_no_pairs()
    {
        Assert.Empty(Rpc.ParseStringMap(null));
        Assert.Empty(Rpc.ParseStringMap("  "));
    }

    [Fact]
    public void String_values_pass_through()
    {
        var map = Rpc.ParseStringMap("""{"X":"3","Y":"hello"}""").ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("3", map["X"]);
        Assert.Equal("hello", map["Y"]);
    }

    [Fact]
    public void Numbers_and_booleans_are_stringified_so_both_notations_behave_alike()
    {
        var map = Rpc.ParseStringMap("""{"N":42,"F":3.5,"B":true}""")
            .ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("42", map["N"]);
        Assert.Equal("3.5", map["F"]);
        Assert.Equal("true", map["B"]);
    }

    [Fact]
    public void Null_value_becomes_an_empty_string_rather_than_throwing()
    {
        var map = Rpc.ParseStringMap("""{"X":null}""").ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("", map["X"]);
    }

    [Fact]
    public void A_json_array_is_rejected_because_the_map_needs_names()
    {
        var error = Assert.Throws<ArgumentException>(() => Rpc.ParseStringMap("""["a","b"]""").ToList());
        Assert.Contains("JSON object", error.Message);
    }

    [Fact]
    public void Malformed_json_is_rejected_and_names_the_parameter()
    {
        var error = Assert.Throws<ArgumentException>(
            () => Rpc.ParseStringMap("{oops", "inputsJson").ToList());
        Assert.Contains("inputsJson", error.Message);
    }
}

public class RpcGuardTests
{
    [Fact]
    public async Task Passes_through_a_successful_result_untouched()
    {
        Assert.Equal("payload", await Rpc.GuardAsync(() => Task.FromResult("payload")));
    }

    [Fact]
    public async Task Rpc_failure_becomes_data_not_an_exception()
    {
        var result = await Rpc.GuardAsync(() =>
            throw new RpcException(new Status(StatusCode.Internal, "boom")));

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("rpc", Res.Str(result, "errorKind"));
        Assert.Contains("boom", Res.Str(result, "error"));
        Assert.Equal("Internal", Res.Obj(result)["detail"]!["statusCode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(StatusCode.Unavailable, "LabVIEW running")]
    [InlineData(StatusCode.DeadlineExceeded, "timeoutSeconds")]
    [InlineData(StatusCode.Unimplemented, "lvai_dump_schema")]
    public async Task Known_status_codes_carry_an_actionable_hint(StatusCode code, string expected)
    {
        var result = await Rpc.GuardAsync(() =>
            throw new RpcException(new Status(code, "detail")));

        var hint = Res.Obj(result)["detail"]!["hint"]!.GetValue<string>();
        Assert.Contains(expected, hint);
    }

    [Fact]
    public async Task Unmapped_status_code_has_a_null_hint_rather_than_a_bogus_one()
    {
        var result = await Rpc.GuardAsync(() =>
            throw new RpcException(new Status(StatusCode.PermissionDenied, "nope")));

        Assert.Null(Res.Obj(result)["detail"]!["hint"]);
    }

    [Fact]
    public async Task Cancellation_is_reported_as_a_cancelled_kind()
    {
        var result = await Rpc.GuardAsync(() => throw new OperationCanceledException());
        Assert.Equal("cancelled", Res.Str(result, "errorKind"));
        Assert.False(Res.Bool(result, "ok"));
    }

    [Fact]
    public async Task Ordinary_exceptions_are_reported_under_their_type_name()
    {
        var result = await Rpc.GuardAsync(() => throw new ArgumentException("bad arg"));
        Assert.Equal("ArgumentException", Res.Str(result, "errorKind"));
        Assert.Contains("bad arg", Res.Str(result, "error"));
    }
}

public class RpcCollectTests
{
    private static GetDescribeVIPromptInfoResponse Item(int i) => new() { InfoJson = $"#{i}" };

    [Fact]
    public async Task Reports_stream_completed_when_the_producer_ends_first()
    {
        var reader = new FakeStreamReader<GetDescribeVIPromptInfoResponse>([Item(0), Item(1)]);
        var (items, reason) = await Rpc.CollectAsync(reader, limit: 10, timeoutSeconds: 5, default);

        Assert.Equal(2, items.Count);
        Assert.Equal("stream completed", reason);
    }

    [Fact]
    public async Task Stops_at_the_limit_and_says_so()
    {
        var reader = new FakeStreamReader<GetDescribeVIPromptInfoResponse>(
            Enumerable.Range(0, 20).Select(Item));
        var (items, reason) = await Rpc.CollectAsync(reader, limit: 3, timeoutSeconds: 5, default);

        Assert.Equal(3, items.Count);
        Assert.Equal("limit reached", reason);
    }

    [Fact]
    public async Task A_limit_of_zero_collects_nothing_without_touching_the_stream()
    {
        var reader = new FakeStreamReader<GetDescribeVIPromptInfoResponse>([Item(0)]);
        var (items, reason) = await Rpc.CollectAsync(reader, limit: 0, timeoutSeconds: 5, default);

        Assert.Empty(items);
        Assert.Equal("limit reached", reason);
    }

    [Fact]
    public async Task An_open_ended_stream_times_out_and_keeps_the_partial_result()
    {
        var reader = new FakeStreamReader<GetDescribeVIPromptInfoResponse>(
            [Item(0), Item(1)], hangAtEnd: true);
        var (items, reason) = await Rpc.CollectAsync(reader, limit: 10, timeoutSeconds: 1, default);

        Assert.Equal(2, items.Count);
        Assert.Equal("timeout", reason);
    }

    [Fact]
    public async Task A_grpc_cancellation_is_treated_as_a_timeout_not_an_error()
    {
        var reader = new ThrowingReader(new RpcException(new Status(StatusCode.Cancelled, "cancelled")));
        var (items, reason) = await Rpc.CollectAsync(reader, limit: 5, timeoutSeconds: 5, default);

        Assert.Empty(items);
        Assert.Equal("timeout", reason);
    }

    [Fact]
    public async Task A_real_rpc_error_still_propagates()
    {
        var reader = new ThrowingReader(new RpcException(new Status(StatusCode.Internal, "broken")));
        await Assert.ThrowsAsync<RpcException>(() =>
            Rpc.CollectAsync(reader, limit: 5, timeoutSeconds: 5, default));
    }

    [Fact]
    public async Task An_already_cancelled_caller_token_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var reader = new FakeStreamReader<GetDescribeVIPromptInfoResponse>([Item(0)], hangAtEnd: true);
        var (_, reason) = await Rpc.CollectAsync(reader, limit: 5, timeoutSeconds: 30, cts.Token);

        Assert.Equal("timeout", reason);
    }

    private sealed class ThrowingReader(Exception error) : IAsyncStreamReader<GetDescribeVIPromptInfoResponse>
    {
        public GetDescribeVIPromptInfoResponse Current => throw new InvalidOperationException();
        public Task<bool> MoveNext(CancellationToken cancellationToken) => throw error;
    }
}
