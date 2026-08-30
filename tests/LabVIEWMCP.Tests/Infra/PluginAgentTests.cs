using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The plugin ships its own copy of every agent definition, and that copy is NOT free-standing: it
/// differs from the repository's only in the MCP tool prefix. The same server is `labview` when a
/// user registers it directly and `plugin_labview-mcp_labview` when it arrives as a plugin, and an
/// agent's frontmatter `tools:` list names its tools literally - so the wrong flavour registers
/// happily and can then call nothing.
///
/// WHY THIS EXISTS. Measured 2026-08-30: plugin/agents held THREE of the seven definitions, and
/// all three were stale forks. The four agents added on 2026-08-28 and -29 - the class generator
/// and the three unit-test agents - shipped to nobody at all, and the three that did ship were
/// missing rules their .claude/agents counterparts had gained. Nothing compared the two folders,
/// so nothing said so: the release workflow copies plugin/agents verbatim.
///
/// scripts/Sync-PluginAgents.ps1 is now the only writer of plugin/agents. This is the check that
/// it has been run since the last edit to an agent. The release workflow asserts the staged
/// LAYOUT; this asserts the SOURCE relationship, the same division ScriptDataFileTests uses.
/// </summary>
public class PluginAgentTests
{
    private const string LocalPrefix = "mcp__labview__";
    private const string PluginPrefix = "mcp__plugin_labview-mcp_labview__";

    /// <summary>Both agent folders, located from the repository root marker.</summary>
    private static (string Source, string Plugin) Folders()
    {
        var marker = Res.FindRepoFile(".claude-plugin/marketplace.json");
        Assert.True(marker is not null, "cannot find the repository root");
        var repo = Path.GetDirectoryName(Path.GetDirectoryName(marker!))!;
        return (Path.Combine(repo, ".claude", "agents"), Path.Combine(repo, "plugin", "agents"));
    }

    private static string[] Names(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*.md").Select(Path.GetFileName).OfType<string>().Order()];

    /// <summary>Line endings must not decide this - git may hand a checkout either kind.</summary>
    private static string Comparable(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void EveryRepositoryAgentIsShippedInThePluginAndNoneIsOrphaned()
    {
        var (source, plugin) = Folders();
        Assert.NotEmpty(Names(source));
        Assert.Equal(Names(source), Names(plugin));
    }

    [Fact]
    public void ThePluginAgentIsTheRepositoryAgentWithTheToolPrefixRewritten()
    {
        var (source, plugin) = Folders();
        foreach (var name in Names(source))
        {
            var want = File.ReadAllText(Path.Combine(source, name)).Replace(LocalPrefix, PluginPrefix);
            var have = File.ReadAllText(Path.Combine(plugin, name));
            Assert.True(Comparable(want) == Comparable(have),
                $"plugin/agents/{name} is not .claude/agents/{name} with the plugin tool prefix. "
                + "Run scripts/Sync-PluginAgents.ps1 and commit the result.");
        }
    }

    /// <summary>
    /// A half-finished substitution is the failure this catches: one tool left with the local
    /// name is one tool the plugin's agent cannot call, and nothing else would notice.
    /// </summary>
    [Fact]
    public void NoPluginAgentStillNamesALocalToolName()
    {
        var (_, plugin) = Folders();
        foreach (var name in Names(plugin))
        {
            var text = File.ReadAllText(Path.Combine(plugin, name));
            Assert.True(!text.Contains(LocalPrefix, StringComparison.Ordinal),
                $"plugin/agents/{name} still names {LocalPrefix}* tools, which do not exist in a "
                + "plugin install");
            Assert.Contains(PluginPrefix, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// And the release has to stage BOTH flavours: agents/ at the zip root for the plugin loader,
    /// bin/claude/agents/ for scripts/Install-ClaudeAssets.ps1, which is what README section 7
    /// tells a non-plugin install to run. Until 2026-08-30 the zip carried only the first, so that
    /// script found no 'claude' folder beside the exe and threw.
    /// </summary>
    [Fact]
    public void TheReleaseWorkflowStagesBothAgentFlavours()
    {
        var workflow = Res.FindRepoFile(".github/workflows/release.yml");
        Assert.True(workflow is not null, "cannot find the release workflow");
        var text = File.ReadAllText(workflow!);

        Assert.Contains("'plugin/agents'", text, StringComparison.Ordinal);
        Assert.Contains("bin/claude", text, StringComparison.Ordinal);
        Assert.Contains("'.claude/agents'", text, StringComparison.Ordinal);
        Assert.Contains("staging/bin/claude/agents", text, StringComparison.Ordinal);
    }
}
