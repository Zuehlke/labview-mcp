using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class JsonMessageTests
{
    [Fact]
    public void Default_values_are_kept_so_errorCode_zero_is_visible()
    {
        // The whole point: {"errorCode":0} is a RESULT. Omitting it would read as "no field".
        var result = Json.Message(new OpenFileResponse { ErrorCode = 0, ErrorMessage = "" });

        Assert.True(Res.Has(result, "errorCode"));
        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.Equal("", Res.Str(result, "errorMessage"));
    }

    [Fact]
    public void Non_default_values_round_trip()
    {
        var result = Json.Message(new OpenFileResponse { ErrorCode = 1055, ErrorMessage = "not found" });

        Assert.Equal(1055, Res.Int(result, "errorCode"));
        Assert.Equal("not found", Res.Str(result, "errorMessage"));
    }

    [Fact]
    public void Extra_fields_are_merged_alongside_the_message()
    {
        var result = Json.Message(
            new ConvertVIToAIXMLResponse { ErrorMessage = "No Error" },
            ("xmlWritten", JsonValue.Create(true)),
            ("xmlBytes", JsonValue.Create(3044L)));

        Assert.Equal("No Error", Res.Str(result, "errorMessage"));
        Assert.True(Res.Bool(result, "xmlWritten"));
        Assert.Equal(3044, Res.Long(result, "xmlBytes"));
    }

    [Fact]
    public void An_extra_field_may_be_explicitly_null()
    {
        var result = Json.Message(new OpenFileResponse(), ("note", null));
        Assert.True(Res.Has(result, "note"));
        Assert.True(Res.IsNull(result, "note"));
    }

    [Fact]
    public void Repeated_and_map_fields_render_as_arrays_and_objects()
    {
        var response = new RunVIAsTopLevelResponse();
        response.Outputs["Result"] = "7";

        var map = Res.Obj(Json.Message(response))["outputs"]!.AsObject();
        Assert.Equal("7", map["Result"]!.GetValue<string>());
    }

    [Fact]
    public void Node_returns_a_mutable_object_for_composition()
    {
        var node = Json.Node(new GetApplicationConfigurationResponse { Language = "German" });
        Assert.Equal("German", node["language"]!.GetValue<string>());
    }
}

public class JsonStreamTests
{
    [Fact]
    public void Carries_count_limit_stop_reason_and_the_messages()
    {
        var messages = new[]
        {
            new GetDescribeVIPromptInfoResponse { InfoJson = "a" },
            new GetDescribeVIPromptInfoResponse { InfoJson = "b" },
        };

        var result = Json.Stream(messages, "limit reached", 2);

        Assert.Equal(2, Res.Int(result, "messageCount"));
        Assert.Equal(2, Res.Int(result, "limit"));
        Assert.Equal("limit reached", Res.Str(result, "stopReason"));
        Assert.Equal(2, Res.Arr(result, "messages").Count);
        Assert.Equal("a", Res.Arr(result, "messages")[0]!["infoJson"]!.GetValue<string>());
    }

    [Fact]
    public void An_empty_stream_is_a_valid_result_with_count_zero()
    {
        var result = Json.Stream([], "timeout", 5);

        Assert.Equal(0, Res.Int(result, "messageCount"));
        Assert.Equal("timeout", Res.Str(result, "stopReason"));
        Assert.Empty(Res.Arr(result, "messages"));
    }

    /// <summary>
    /// The cap exists because lvai_describe_vi answered 62 091 characters for a VI whose whole AIXML
    /// is 17 532 bytes (issue #19), and infoJson is where that size sits. Same shape as the AIXML
    /// tools' `xml` / `xmlTruncated` pair, so a caller who knows one knows the other.
    /// </summary>
    [Fact]
    public void A_capped_field_is_truncated_and_says_so()
    {
        var messages = new[]
        {
            new GetDescribeVIPromptInfoResponse { InfoJson = new string('x', 100) },
        };

        var result = Json.Stream(messages, "stream completed", 10, 10, "infoJson");

        var message = Res.Arr(result, "messages")[0]!;
        Assert.Equal(10, message["infoJson"]!.GetValue<string>().Length);
        Assert.True(message["infoJsonTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void A_field_under_the_cap_is_whole_and_flagged_false()
    {
        var messages = new[] { new GetDescribeVIPromptInfoResponse { InfoJson = "short" } };

        var result = Json.Stream(messages, "stream completed", 10, 1000, "infoJson");

        var message = Res.Arr(result, "messages")[0]!;
        Assert.Equal("short", message["infoJson"]!.GetValue<string>());
        Assert.False(message["infoJsonTruncated"]!.GetValue<bool>());
    }

    /// <summary>
    /// Zero means unlimited, and it is the default - so the three-argument overload and an explicit
    /// 0 must both leave the message exactly as it arrived, flag included.
    /// </summary>
    [Fact]
    public void A_cap_of_zero_leaves_the_message_untouched()
    {
        var messages = new[] { new GetDescribeVIPromptInfoResponse { InfoJson = new string('x', 99) } };

        var result = Json.Stream(messages, "stream completed", 10, 0, "infoJson");

        var message = Res.Arr(result, "messages")[0]!;
        Assert.Equal(99, message["infoJson"]!.GetValue<string>().Length);
        Assert.Null(message["infoJsonTruncated"]);
    }
}

public class JsonErrorTests
{
    [Fact]
    public void Marks_not_ok_and_names_the_kind()
    {
        var result = Json.Error("rpc", "something broke");

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("rpc", Res.Str(result, "errorKind"));
        Assert.Equal("something broke", Res.Str(result, "error"));
        Assert.True(Res.IsNull(result, "detail"));
    }

    [Fact]
    public void Detail_is_serialised_when_supplied()
    {
        var result = Json.Error("rpc", "broke", new { statusCode = "Internal", attempts = 2 });

        var detail = Res.Obj(result)["detail"]!;
        Assert.Equal("Internal", detail["statusCode"]!.GetValue<string>());
        Assert.Equal(2, detail["attempts"]!.GetValue<int>());
    }
}

public class JsonObjectTests
{
    [Fact]
    public void Serialises_a_plain_object_indented()
    {
        var result = Json.Object(new { port = 49379, via = "listener" });

        Assert.Equal(49379, Res.Int(result, "port"));
        Assert.Equal("listener", Res.Str(result, "via"));
        Assert.Contains("\n", result);
    }
}
