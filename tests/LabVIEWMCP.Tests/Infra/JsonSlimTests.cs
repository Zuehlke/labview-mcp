using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// <see cref="Json.Slim"/> — the reducer that keeps a composing tool's answer inside a client's
/// token limit.
///
/// WHY IT IS TESTED AT ALL, given how small it is: the rule it encodes is "a step that FAILED is
/// never reduced", and getting that backwards is invisible in a green run and catastrophic in a
/// failing one — the caller would lose exactly the evidence it needs. Both directions are pinned
/// here rather than left to a live run to discover.
/// </summary>
public sealed class JsonSlimTests
{
    /// <summary>A successful sub-answer with the bulk a real one carries.</summary>
    private static JsonObject Success() => new()
    {
        ["ok"] = true,
        ["errorCode"] = 0,
        ["elapsedMs"] = 1234,
        ["viBytes"] = 9058,
        ["xml"] = new string('x', 40_000),
        ["steps"] = new JsonArray { new JsonObject { ["step"] = "extract" } },
        ["values"] = new JsonObject { ["node names found"] = "…" },
    };

    [Fact]
    public void ASuccessfulAnswerLosesItsBulkAndKeepsItsVerdict()
    {
        var slim = (JsonObject)Json.Slim(Success(), keep: false)!;

        Assert.True(slim["ok"]!.GetValue<bool>());
        Assert.Equal(0, slim["errorCode"]!.GetValue<int>());
        Assert.Equal(1234, slim["elapsedMs"]!.GetValue<int>());
        Assert.Equal(9058, slim["viBytes"]!.GetValue<int>());

        // The three that made the answer unreadable.
        Assert.Null(slim["xml"]);
        Assert.Null(slim["steps"]);
        Assert.Null(slim["values"]);

        Assert.True(slim["slimmed"]!.GetValue<bool>());
    }

    [Fact]
    public void VerboseKeepsEverythingUntouched()
    {
        var original = Success();

        var kept = Json.Slim(original, keep: true);

        Assert.Same(original, kept);
        Assert.NotNull(((JsonObject)kept!)["xml"]);
    }

    /// <summary>
    /// The rule that matters. Each of these is a different way a sub-answer says it failed, and any
    /// of them must keep the whole payload — that is when the caller needs it.
    /// </summary>
    [Theory]
    [InlineData("ok", false)]
    [InlineData("errorCode", 1357)]
    [InlineData("errorKind", "rpc")]
    [InlineData("failedAtStep", "convert")]
    public void AFailedAnswerIsNeverReduced(string key, object marker)
    {
        var failed = Success();
        failed[key] = marker switch
        {
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            _ => JsonValue.Create((string)marker),
        };

        var kept = (JsonObject)Json.Slim(failed, keep: false)!;

        Assert.NotNull(kept["xml"]);
        Assert.NotNull(kept["steps"]);
        Assert.Null(kept["slimmed"]);
    }

    /// <summary>`errorCode: 0` is success, not a failure marker — the commonest value in this repo.</summary>
    [Fact]
    public void ErrorCodeZeroCountsAsSuccess()
    {
        var slim = (JsonObject)Json.Slim(Success(), keep: false)!;

        Assert.True(slim["slimmed"]!.GetValue<bool>());
    }

    [Fact]
    public void ANonObjectAnswerPassesStraightThrough()
    {
        var scalar = JsonValue.Create("not an object");

        Assert.Same(scalar, Json.Slim(scalar, keep: false));
        Assert.Null(Json.Slim(null, keep: false));
    }
}
