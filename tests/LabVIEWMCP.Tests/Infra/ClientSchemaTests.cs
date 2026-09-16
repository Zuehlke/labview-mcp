using System.Text.Json;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LabVIEWMcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// That no served input schema carries a parameter <c>default</c>, and that removing it costs the
/// reader nothing.
///
/// The defect these guard against is a CLIENT-side refusal - `MCP error -32602 ... "expected":
/// "nonoptional"` on a parameter that has a default and was omitted - so nothing in the server's
/// own answers can detect it. <see cref="ClientSchema"/> carries the measurement; what is testable
/// here is the shape of what goes out.
/// </summary>
public class ClientSchemaTests
{
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Default_is_removed_and_appended_to_the_description()
    {
        var rewritten = ClientSchema.WithoutDefaults(Parse("""
            {"type":"object",
             "properties":{"timeoutSeconds":{"description":"Local budget in seconds",
                                             "type":"integer","default":180}}}
            """));

        var property = rewritten.GetProperty("properties").GetProperty("timeoutSeconds");

        Assert.False(property.TryGetProperty("default", out _));
        Assert.Equal("Local budget in seconds (default: 180)",
                     property.GetProperty("description").GetString());
        // The type is what actually constrains the value and must survive untouched.
        Assert.Equal("integer", property.GetProperty("type").GetString());
    }

    /// <summary>
    /// `required` is real information for the caller and is not what the client was complaining
    /// about - see ClientSchema. Touching it would turn a documentation fix into a contract change.
    /// </summary>
    [Fact]
    public void Required_is_left_alone()
    {
        var rewritten = ClientSchema.WithoutDefaults(Parse("""
            {"type":"object",
             "properties":{"viPath":{"type":"string"},
                           "refresh":{"type":"boolean","default":false}},
             "required":["viPath"]}
            """));

        Assert.Equal(new[] { "viPath" }, rewritten.GetProperty("required")
                                                  .EnumerateArray()
                                                  .Select(x => x.GetString()!).ToArray());
        Assert.False(rewritten.GetProperty("properties").GetProperty("refresh")
                              .TryGetProperty("default", out _));
    }

    [Theory]
    [InlineData("""{"type":"boolean","default":false}""", "(default: false)")]
    [InlineData("""{"type":"string","default":"Tests"}""", "(default: \"Tests\")")]
    [InlineData("""{"type":"string","default":""}""", "(default: empty)")]
    public void The_value_is_readable_in_the_description(string property, string expected)
    {
        var rewritten = ClientSchema.WithoutDefaults(
            Parse("{\"type\":\"object\",\"properties\":{\"p\":" + property + "}}"));

        Assert.Equal(expected,
            rewritten.GetProperty("properties").GetProperty("p").GetProperty("description").GetString());
    }

    /// <summary>
    /// A null default is the `= null` of an optional reference parameter. "(default: null)" on 118
    /// properties is noise, and the type union already says the parameter may be left out - so the
    /// key goes and the description is not touched.
    /// </summary>
    [Fact]
    public void A_null_default_leaves_the_description_alone()
    {
        var rewritten = ClientSchema.WithoutDefaults(Parse("""
            {"type":"object",
             "properties":{"viName":{"description":"Optional VI name",
                                     "type":["string","null"],"default":null}}}
            """));

        var property = rewritten.GetProperty("properties").GetProperty("viName");
        Assert.False(property.TryGetProperty("default", out _));
        Assert.Equal("Optional VI name", property.GetProperty("description").GetString());
    }

    [Fact]
    public void Running_it_twice_changes_nothing_the_second_time()
    {
        var once = ClientSchema.WithoutDefaults(Parse("""
            {"type":"object","properties":{"p":{"description":"d","type":"integer","default":7}}}
            """));

        Assert.Equal(once.GetRawText(), ClientSchema.WithoutDefaults(once).GetRawText());
    }

    /// <summary>
    /// The whole served surface, which is the assertion that actually protects a session: not one
    /// `default` ANYWHERE in any input schema, nested ones included. The transform only rewrites
    /// top-level properties on purpose, so a nested default fails here rather than quietly reaching
    /// a client that would then demand it.
    /// </summary>
    [Fact]
    public void No_served_input_schema_carries_a_default_anywhere()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(provider => new LvaiConnection(
            provider.GetRequiredService<ILogger<LvaiConnection>>(), 1));
        services.AddMcpServer()
                .WithToolsFromAssembly(typeof(InspectTools).Assembly)
                .WithArgumentDiagnostics();

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetServices<McpServerTool>().ToList();
        Assert.True(tools.Count >= 41, $"only {tools.Count} tools registered");

        var offenders = tools
            .Where(tool => Mentions(tool.ProtocolTool.InputSchema, "default"))
            .Select(tool => tool.ProtocolTool.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "input schemas still carrying a `default`: " + string.Join(", ", offenders));
    }

    private static bool Mentions(JsonElement element, string key) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Any(p => p.Name == key || Mentions(p.Value, key)),
        JsonValueKind.Array => element.EnumerateArray().Any(e => Mentions(e, key)),
        _ => false,
    };
}
