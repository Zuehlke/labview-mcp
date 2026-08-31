using System.Text.Json;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The argument layer that removes "An error occurred invoking 'lvai_describe_vi'." (issue #19).
///
/// THE SCHEMA FIXTURE IS A MEASUREMENT: it is what `tools/list` served for lvai_describe_vi on
/// 2026-08-14, read off the wire, plus the maxContentChars parameter added in the same change. It is
/// a fixture rather than a live read because these tests are about the mechanics of folding and
/// reporting - the wiring that a real schema reaches them is asserted in DiagnosingToolTests.
/// </summary>
public class ToolArgumentsTests
{
    private const string DescribeViSchema = """
        {
          "type": "object",
          "properties": {
            "viPath": { "description": "Absolute path to the .vi file", "type": "string" },
            "viName": { "description": "Optional VI name", "type": ["string", "null"],
                        "default": null },
            "getNodesInfo": { "description": "Include nodes", "type": "boolean", "default": true },
            "maxMessages": { "description": "Max stream messages", "type": "integer",
                             "default": 10 },
            "timeoutSeconds": { "description": "Local budget", "type": "integer", "default": 120 },
            "maxContentChars": { "description": "Truncate infoJson", "type": "integer",
                                 "default": 0 }
          },
          "required": [ "viPath" ]
        }
        """;

    private static JsonElement Schema(string json = DescribeViSchema) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Shape_reads_properties_and_required()
    {
        var (properties, required) = ToolArguments.Shape(Schema());

        Assert.Equal(
            ["viPath", "viName", "getNodesInfo", "maxMessages", "timeoutSeconds", "maxContentChars"],
            properties);
        Assert.Equal(["viPath"], required);
    }

    /// <summary>A tool with no parameters at all - lvai_status - must not trip over an empty schema.</summary>
    [Fact]
    public void Shape_tolerates_an_empty_schema()
    {
        var (properties, required) = ToolArguments.Shape(Schema("""{ "type": "object" }"""));

        Assert.Empty(properties);
        Assert.Empty(required);
    }

    [Theory]
    [InlineData("vi_path")]
    [InlineData("VI_PATH")]
    [InlineData("vi-path")]
    [InlineData("vipath")]
    [InlineData("ViPath")]
    public void Fold_makes_near_misses_equal(string spelling) =>
        Assert.Equal(ToolArguments.Fold("viPath"), ToolArguments.Fold(spelling));

    [Fact]
    public void Fold_keeps_distinct_names_distinct() =>
        Assert.NotEqual(ToolArguments.Fold("viPath"), ToolArguments.Fold("viPaths"));

    /// <summary>The case out of issue #19, and the reason this class exists.</summary>
    [Fact]
    public void Snake_case_is_renamed_to_the_schema_spelling()
    {
        var (properties, _) = ToolArguments.Shape(Schema());

        var renames = ToolArguments.Renames(properties, ["vi_path", "timeout_seconds"]);

        Assert.Equal("viPath", renames["vi_path"]);
        Assert.Equal("timeoutSeconds", renames["timeout_seconds"]);
    }

    [Fact]
    public void A_correctly_spelled_key_is_left_alone()
    {
        var (properties, _) = ToolArguments.Shape(Schema());

        Assert.Empty(ToolArguments.Renames(properties, ["viPath", "getNodesInfo"]));
    }

    /// <summary>
    /// Both spellings supplied: the declared one wins and the stray key is left where it is.
    /// Renaming here would overwrite a value the caller spelled correctly, which is worse than the
    /// silence this whole change is removing - measured, an undeclared key is simply ignored.
    /// </summary>
    [Fact]
    public void A_declared_key_is_never_overwritten_by_its_variant()
    {
        var (properties, _) = ToolArguments.Shape(Schema());

        Assert.Empty(ToolArguments.Renames(properties, ["viPath", "vi_path"]));
    }

    [Fact]
    public void A_key_that_matches_nothing_is_not_renamed()
    {
        var (properties, _) = ToolArguments.Shape(Schema());

        Assert.Empty(ToolArguments.Renames(properties, ["path", "file"]));
    }

    /// <summary>
    /// Two declared names sharing a fold cannot both be the target, so neither is: a guess between
    /// them would be exactly the kind of invisible wrong answer this class replaces.
    /// </summary>
    [Fact]
    public void An_ambiguous_fold_is_not_guessed()
    {
        var ambiguous = Schema("""
            {
              "type": "object",
              "properties": { "viPath": { "type": "string" }, "vi_path": { "type": "string" } },
              "required": [ "viPath" ]
            }
            """);
        var (properties, _) = ToolArguments.Shape(ambiguous);

        Assert.Empty(ToolArguments.Renames(properties, ["VIPATH"]));
    }

    [Fact]
    public void Missing_is_empty_when_the_required_key_is_there()
    {
        var (_, required) = ToolArguments.Shape(Schema());

        Assert.Empty(ToolArguments.Missing(required, ["viPath", "getNodesInfo"]));
    }

    [Fact]
    public void Missing_names_the_required_key_that_is_absent()
    {
        var (_, required) = ToolArguments.Shape(Schema());

        Assert.Equal(["viPath"], ToolArguments.Missing(required, ["getNodesInfo"]));
        Assert.Equal(["viPath"], ToolArguments.Missing(required, supplied: null));
    }

    /// <summary>
    /// The report has to carry three things to be actionable in one turn: what is missing, what the
    /// caller actually sent, and what the accepted names are.
    /// </summary>
    [Fact]
    public void Missing_arguments_reports_missing_received_and_accepted()
    {
        var json = ToolArguments.MissingArguments(
            "lvai_describe_vi", Schema(), ["viPath"], ["vi_path"]);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("badArguments", root.GetProperty("errorKind").GetString());
        Assert.Contains("viPath", root.GetProperty("error").GetString());

        var detail = root.GetProperty("detail");
        Assert.Equal("lvai_describe_vi", detail.GetProperty("tool").GetString());
        Assert.Equal("viPath", detail.GetProperty("missing")[0].GetString());
        Assert.Equal("vi_path", detail.GetProperty("received")[0].GetString());

        var accepted = detail.GetProperty("accepted");
        Assert.Equal("string, required", accepted.GetProperty("viPath").GetString());
        Assert.Equal("integer, default 120", accepted.GetProperty("timeoutSeconds").GetString());
        Assert.Equal("string or null, default null", accepted.GetProperty("viName").GetString());
        Assert.Contains("camelCase", detail.GetProperty("hint").GetString());
    }

    /// <summary>
    /// A wrong TYPE fails in the SDK's binding, not in the tool body, and the message it throws is
    /// exactly what the server would otherwise replace with "An error occurred invoking '...'".
    /// </summary>
    [Fact]
    public void Invocation_problem_keeps_the_exception_message()
    {
        var json = ToolArguments.InvocationProblem(
            "lvai_describe_vi", Schema(), ["viPath", "timeoutSeconds"],
            new ArgumentException("'timeoutSeconds' could not be converted to Int32."));

        using var doc = JsonDocument.Parse(json);
        var detail = doc.RootElement.GetProperty("detail");
        Assert.Contains("could not be converted", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("ArgumentException", detail.GetProperty("exception").GetString());
        Assert.Equal("string, required", detail.GetProperty("accepted").GetProperty("viPath").GetString());
    }

    // ------------------------------------------------------------ the value fold

    /// <summary>
    /// MEASURED 2026-08-26: `inputsJson` is declared a string and its own description shows a JSON
    /// OBJECT, so a client that sends what it was shown is refused by the binder with
    /// `The JSON value could not be converted to System.String`. The parameter was unreachable, and
    /// the diagnostics named the problem clearly without making the call possible. Only a
    /// wanted-a-string failure may trigger the fold - anything else must report, not retry.
    /// </summary>
    [Theory]
    [InlineData("The JSON value could not be converted to System.String. Path: $", true)]
    [InlineData("The JSON value could not be converted to System.Int32. Path: $", false)]
    public void Wants_string_recognises_only_the_string_binding_failure(string message, bool expected) =>
        Assert.Equal(expected, ToolArguments.WantsString(new JsonException(message)));

    [Fact]
    public void Wants_string_ignores_a_failure_that_is_not_a_json_exception() =>
        Assert.False(ToolArguments.WantsString(
            new ArgumentException("System.String was not the problem here.")));

    [Fact]
    public void Stringified_turns_an_object_into_its_own_json_text()
    {
        var supplied = Args("""{"inputsJson":{"VI Path":"C:\\x.vi"},"viPath":"C:\\y.vi"}""");

        var folded = ToolArguments.Stringified(supplied);

        Assert.NotNull(folded);
        Assert.Equal(JsonValueKind.String, folded!["inputsJson"].ValueKind);
        Assert.Equal("""{"VI Path":"C:\\x.vi"}""", folded["inputsJson"].GetString());

        // A value the tool was already happy with is untouched.
        Assert.Equal("C:\\y.vi", folded["viPath"].GetString());
    }

    [Fact]
    public void Stringified_turns_a_number_into_its_digits()
    {
        // `section` on every reference tool: a heading number is the natural thing to pass.
        var folded = ToolArguments.Stringified(Args("""{"section":14}"""));

        Assert.Equal("14", folded!["section"].GetString());
    }

    /// <summary>
    /// Null means "not supplied". Stringifying it would turn an omitted optional argument into the
    /// four-character word `null`, which several tools would then treat as a path.
    /// </summary>
    [Fact]
    public void Stringified_leaves_null_alone_and_reports_nothing_to_do()
    {
        Assert.Null(ToolArguments.Stringified(Args("""{"viName":null,"viPath":"C:\\x.vi"}""")));
        Assert.Null(ToolArguments.Stringified(Args("""{"viPath":"C:\\x.vi"}""")));
        Assert.Null(ToolArguments.Stringified(null));
    }

    /// <summary>
    /// lvai_swap_subvis' shape, reduced to the two parameters that interacted: a JSON-document
    /// argument declared as a string, and a bool. This pairing is what the regression below is about.
    /// </summary>
    private const string SwapSubVisSchema = """
        {
          "type": "object",
          "properties": {
            "viPath": { "type": "string" },
            "constantsJson": { "type": "string" },
            "verify": { "type": "boolean", "default": true },
            "timeoutSeconds": { "type": "integer", "default": 300 }
          },
          "required": [ "viPath" ]
        }
        """;

    [Fact]
    public void StringTyped_takes_string_and_string_union_and_leaves_bool_and_integer_out()
    {
        var stringTyped = ToolArguments.StringTyped(Schema());

        Assert.Contains("viPath", stringTyped);
        Assert.Contains("viName", stringTyped);          // type: ["string", "null"]
        Assert.DoesNotContain("getNodesInfo", stringTyped);
        Assert.DoesNotContain("maxMessages", stringTyped);
    }

    [Fact]
    public void StringTyped_counts_an_untyped_property_as_a_string()
    {
        // The retry exists for parameters the binder wants as text; an untyped property is exactly
        // where that cannot be ruled out, so it stays eligible.
        var stringTyped = ToolArguments.StringTyped(
            Schema("""{"type":"object","properties":{"node":{"description":"no type"}}}"""));

        Assert.Contains("node", stringTyped);
    }

    /// <summary>
    /// THE REGRESSION, measured 2026-08-31. `lvai_swap_subvis` refused every call that passed a valid
    /// `constantsJson` together with an explicit `verify: true`, and `lvai_run_vi_and_read_values` did
    /// the same for `inputsJson` plus `includeRawXml: false`. The document argument arrives as a real
    /// JSON array, the binder wants a string, the retry fires - and it also rewrote `true` into
    /// `"true"`, which the bool binder then rejects. The reported error was the FIRST failure, so it
    /// named the document parameter and never mentioned the bool; the advice that appeared to work
    /// was the misleading "omit verify". A bool alone never reproduced it: nothing triggered a retry.
    /// </summary>
    [Fact]
    public void Stringified_leaves_a_bool_alone_while_folding_the_document_beside_it()
    {
        var supplied = Args("""
            {"viPath":"C:\\x.vi","constantsJson":[{"label":"a","class":"B.lvclass"}],"verify":true}
            """);

        var folded = ToolArguments.Stringified(supplied, ToolArguments.StringTyped(Schema(SwapSubVisSchema)));

        Assert.NotNull(folded);
        Assert.Equal(JsonValueKind.True, folded!["verify"].ValueKind);
        Assert.Equal(JsonValueKind.String, folded["constantsJson"].ValueKind);
        Assert.Equal("""[{"label":"a","class":"B.lvclass"}]""", folded["constantsJson"].GetString());
        Assert.Equal("C:\\x.vi", folded["viPath"].GetString());
    }

    [Fact]
    public void Stringified_reports_nothing_to_do_when_only_a_bool_is_not_a_string()
    {
        // Without the schema filter this returned a folded dictionary with `verify` rewritten to
        // "true", which is what broke the call. There is now nothing to fold, so no retry happens.
        Assert.Null(ToolArguments.Stringified(
            Args("""{"viPath":"C:\\x.vi","verify":false}"""),
            ToolArguments.StringTyped(Schema(SwapSubVisSchema))));
    }

    private static Dictionary<string, JsonElement>? Args(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }
}
