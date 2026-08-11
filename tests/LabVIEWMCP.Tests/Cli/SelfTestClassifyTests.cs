using LabVIEWMcp.Cli;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

/// <summary>
/// The self-test verdict is only meaningful if it does NOT treat "a string came back" as
/// success: tools report failure as data, so both {"ok":false} and a non-zero protobuf
/// errorCode have to fail.
/// </summary>
public class SelfTestClassifyTests
{
    [Fact]
    public void Guard_failure_is_a_FAIL_and_surfaces_the_error_text()
    {
        var (ok, note) = SelfTest.Classify(
            """{"ok":false,"errorKind":"rpc","error":"Unavailable: no host"}""");

        Assert.False(ok);
        Assert.Contains("Unavailable", note);
    }

    [Fact]
    public void A_non_zero_labview_errorCode_is_a_FAIL()
    {
        var (ok, note) = SelfTest.Classify("""{"errorCode":1055,"errorMessage":"File not found"}""");

        Assert.False(ok);
        Assert.Contains("1055", note);
        Assert.Contains("File not found", note);
    }

    [Fact]
    public void ErrorCode_zero_is_a_PASS_and_shows_the_message()
    {
        var (ok, note) = SelfTest.Classify("""{"errorCode":0,"errorMessage":"No Error"}""");

        Assert.True(ok);
        Assert.Equal("No Error", note);
    }

    [Fact]
    public void A_stream_result_passes_and_reports_count_and_stop_reason()
    {
        var (ok, note) = SelfTest.Classify(
            """{"messageCount":3,"limit":10,"stopReason":"stream completed","messages":[]}""");

        Assert.True(ok);
        Assert.Contains("3 msg", note);
        Assert.Contains("stream completed", note);
    }

    [Fact]
    public void An_ok_true_payload_passes_with_no_note()
    {
        var (ok, note) = SelfTest.Classify("""{"ok":true,"port":49379}""");

        Assert.True(ok);
        Assert.Equal("", note);
    }

    [Fact]
    public void A_stream_result_wins_over_a_trailing_errorMessage()
    {
        // messageCount is checked before errorMessage, so a stream keeps its count note.
        var (ok, note) = SelfTest.Classify(
            """{"messageCount":1,"stopReason":"timeout","errorMessage":"ignored"}""");

        Assert.True(ok);
        Assert.Contains("timeout", note);
        Assert.DoesNotContain("ignored", note);
    }

    [Fact]
    public void Unparsable_output_does_not_crash_the_run()
    {
        var (ok, note) = SelfTest.Classify("this is not json");

        Assert.True(ok);
        Assert.Equal("unparsable payload", note);
    }

    [Fact]
    public void A_json_array_is_tolerated_as_a_bare_pass()
    {
        var (ok, note) = SelfTest.Classify("[1,2,3]");

        Assert.True(ok);
        Assert.Equal("", note);
    }

    [Fact]
    public void An_empty_object_passes()
    {
        var (ok, note) = SelfTest.Classify("{}");

        Assert.True(ok);
        Assert.Equal("", note);
    }
}
