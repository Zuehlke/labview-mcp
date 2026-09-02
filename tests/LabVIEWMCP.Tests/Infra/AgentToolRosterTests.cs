using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A tool named after a framework must be granted to that framework's agent.
///
/// WHY THIS EXISTS: the same mistake three times in one afternoon. `lvai_placeholder_subvi`,
/// then `lvai_lunit_add_test_method` and `lvai_run_lunit_tests`, then
/// `lvai_lunit_scaffold_class_tests` were each written into an agent's job and left out of its
/// frontmatter `tools:` list. The agent then reads instructions naming a tool it cannot call. Two of
/// the three cost a whole subagent run to discover — the third was found by writing this test, along
/// with a pre-existing gap nobody had noticed (`lvai_dqmh_new_event`).
///
/// It is deliberately NOT "every tool the prose mentions". That rule has twelve hits across the
/// eight agents, most of them legitimate — an agent may discuss a tool it must NOT use, and the
/// doc-generator explains six it never calls. A rule with that many exceptions needs an allowlist,
/// and an allowlist is where a real omission goes to hide. The naming convention is narrower and
/// exact: a tool with `lunit` in its name has no purpose outside the LUnit agent.
///
/// If a tool ever legitimately falls outside its namesake agent, this test is the right place to
/// argue that in a comment — not a place to add a silent exception.
/// </summary>
public sealed class AgentToolRosterTests
{
    /// <summary>Framework keyword → the agent whose job that framework is.</summary>
    public static TheoryData<string, string> Ownership => new()
    {
        { "lunit", "labview-lunit-unit-test" },
        { "dqmh", "labview-dqmh-module" },
        { "caraya", "labview-caraya-unit-test" },
    };

    [Theory]
    [MemberData(nameof(Ownership))]
    public void EveryToolNamedForAFrameworkIsGrantedToItsAgent(string keyword, string agent)
    {
        using var provider = ServedTools();
        var named = provider.GetServices<McpServerTool>()
            .Select(t => t.ProtocolTool.Name)
            .Where(n => n.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(named);   // a keyword that matches nothing would pass vacuously

        var granted = Granted(agent);
        var missing = named.Where(n => !granted.Contains(n)).ToList();

        Assert.True(missing.Count == 0,
            $"'{agent}' is the agent for {keyword.ToUpperInvariant()}, but its frontmatter " +
            $"`tools:` list does not grant: {string.Join(", ", missing)}. An agent instructed to " +
            "use a tool it cannot call fails in a way that reads as the tool being broken. Add " +
            $"mcp__labview__<name> to line 5 of .claude/agents/{agent}.md and re-run " +
            "scripts/Sync-PluginAgents.ps1.");
    }

    /// <summary>
    /// And the converse, which catches a rename or a typo: every tool an agent claims must exist.
    /// A frontmatter naming a tool the server does not serve grants nothing and says nothing.
    /// </summary>
    [Theory]
    [InlineData("labview-caraya-unit-test")]
    [InlineData("labview-class-generator")]
    [InlineData("labview-doc-generator")]
    [InlineData("labview-dqmh-module")]
    [InlineData("labview-lunit-unit-test")]
    [InlineData("labview-vi-editor")]
    [InlineData("labview-vi-generator")]
    [InlineData("labview-vitester-unit-test")]
    public void EveryToolAnAgentClaimsIsActuallyServed(string agent)
    {
        using var provider = ServedTools();
        var served = provider.GetServices<McpServerTool>()
                             .Select(t => t.ProtocolTool.Name)
                             .ToHashSet(StringComparer.Ordinal);

        var unknown = Granted(agent).Where(n => !served.Contains(n)).ToList();

        Assert.True(unknown.Count == 0,
            $"'{agent}' grants tools the server does not serve: {string.Join(", ", unknown)}. " +
            "A frontmatter entry naming a tool that no longer exists grants nothing and reports " +
            "nothing - the agent simply finds it missing at run time.");
    }

    /// <summary>The `lvai_*` names on the agent's frontmatter line, without the transport prefix.</summary>
    private static HashSet<string> Granted(string agent)
    {
        var path = Res.FindRepoFile(Path.Combine(".claude", "agents", agent + ".md"));
        Assert.NotNull(path);

        // The prefix differs between a directly registered server and the plugin flavour, so match
        // either - see CLAUDE.md, "The plugin ships its OWN copy".
        return [.. Regex.Matches(File.ReadAllText(path!), @"mcp__[a-z_\-]*labview__([a-z_]+)")
                        .Select(m => m.Groups[1].Value)];
    }

    private static ServiceProvider ServedTools()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(typeof(LabVIEWMcp.Infra.Json).Assembly);
        return services.BuildServiceProvider();
    }
}
