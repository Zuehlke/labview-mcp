using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Every answer to the client is written with RELAXED escaping since 2026-10-30. The default
/// encoder wrote `&lt;` as `\u003C` - six characters, several tokens - and our answers carry XML,
/// AIXML and paths; measured over every LabVIEW tool answer in this repository's transcripts the
/// default cost 12 % of the characters, 30 % on lvai_run_vi_and_read_values. The output is still
/// JSON that parses to the same strings. `docs/token-economy.md` §4.2.
/// </summary>
public sealed class JsonAnswerEncodingTests
{
    private const string Xml = "<Array>\r\n  <Name>Target Control Labels</Name>\r\n  <Val>Käse & Brot</Val>\r\n</Array>";

    [Fact]
    public void Angle_brackets_ampersands_and_umlauts_go_out_as_themselves()
    {
        var answer = Json.Document(new JsonObject { ["xml"] = Xml, ["path"] = @"C:\Temp\Prüf+Stand.vi" });

        Assert.Contains("<Array>", answer);
        Assert.Contains("Käse & Brot", answer);
        Assert.Contains(@"C:\\Temp\\Prüf+Stand.vi", answer);
        Assert.DoesNotContain("\\u003C", answer);
        Assert.DoesNotContain("\\u0026", answer);
        Assert.DoesNotContain("\\u00FC", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u002B", answer);
    }

    [Fact]
    public void The_answer_is_still_JSON_that_reads_back_to_the_same_strings()
    {
        var answer = Json.Document(new JsonObject { ["xml"] = Xml, ["quote"] = "a \"b\" c" });
        var back = JsonNode.Parse(answer)!.AsObject();

        Assert.Equal(Xml, back["xml"]!.GetValue<string>());
        Assert.Equal("a \"b\" c", back["quote"]!.GetValue<string>());
    }

    [Fact]
    public void Object_and_Error_use_the_same_encoder()
    {
        Assert.Contains("<x/>", Json.Object(new { xml = "<x/>" }));
        Assert.Contains("<x/>", Json.Error("kind", "message <x/>", new { xml = "<x/>" }));
    }

    [Fact]
    public void Answers_are_still_indented()
    {
        Assert.Contains("\n  \"a\": 1", Json.Document(new JsonObject { ["a"] = 1 }).Replace("\r\n", "\n"));
        Assert.True(Json.Answer.WriteIndented);
    }

    /// <summary>
    /// Two implementations of one rule drift - this repository has paid for that already (the UID
    /// base, the `casesJson` key check). So no tool may build its own indented options: every
    /// answer goes through Json.Answer. The on-disk stores (compact, never shown to a model) are
    /// deliberately outside this rule.
    /// </summary>
    [Fact]
    public void No_tool_builds_its_own_indented_serializer_options()
    {
        var offenders = new List<string>();
        var dirs = new[] { RepoTree.Path("src", "LabVIEWMCP", "Tools"), RepoTree.Path("src", "LabVIEWMCP", "Infra") };
        foreach (var file in dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.cs")))
        {
            if (Path.GetFileName(file) == "Json.cs") continue;
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"new\s+(System\.Text\.Json\.)?JsonSerializerOptions\s*(\(\))?\s*\{[^}]*WriteIndented\s*=\s*true"))
                offenders.Add(Path.GetFileName(file));
        }
        Assert.Empty(offenders);
    }
}
