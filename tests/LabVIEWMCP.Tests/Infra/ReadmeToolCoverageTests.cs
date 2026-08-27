using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// That the README's `## Tools` section names every tool the server actually serves.
///
/// The README is not documentation-in-the-nice-to-have sense here: the build COPIES it next to the
/// exe, so on a binary-only install it is the only overview of what the server can do. A tool absent
/// from it is a capability nobody finds.
///
/// Written because the section had drifted by TEN tools before anyone looked - lvai_create_class,
/// lvai_describe_class, lvai_create_accessors, lvai_generate_vi, lvai_run_vi_and_read_values,
/// lvai_convert_vis_to_aixml, lvai_list_labview_installations, lvai_ensure_labview, lvai_vi_terminals
/// and pylv_apply, spanning several branches. Nothing failed; the header just kept claiming 45 tools
/// while 50 were served. Drift with no symptom is exactly what a test is for.
/// </summary>
public class ReadmeToolCoverageTests
{
    /// <summary>
    /// The same construction DiagnosingToolTests uses: the SDK's own registration is the authority on
    /// what is served, so a tool added without a README row fails here rather than shipping unlisted.
    /// </summary>
    private static ServiceProvider ServedTools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(provider => new LvaiConnection(
            provider.GetRequiredService<ILogger<LvaiConnection>>(), 1));

        services
            .AddMcpServer()
            .WithToolsFromAssembly(typeof(InspectTools).Assembly);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Every_served_tool_is_named_in_the_readme_tools_section()
    {
        var path = Res.FindRepoFile("README.md");
        Assert.NotNull(path);

        var readme = File.ReadAllText(path!);
        var heading = readme.IndexOf("## Tools", StringComparison.Ordinal);
        Assert.True(heading >= 0, "README.md has no '## Tools' section any more");
        var section = readme[heading..];

        using var provider = ServedTools();
        var names = provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // A floor, like the wrapper test: losing the registration wholesale is a failure, adding a
        // tool is not - it is the MISSING ROW below that fails.
        Assert.True(names.Count >= 50, $"only {names.Count} tools registered");

        var missing = names
            .Where(name => !section.Contains($"`{name}`", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "these served tools are absent from the README's '## Tools' section, so a binary-only "
            + "install has no record of them: " + string.Join(", ", missing));
    }

    /// <summary>
    /// And that the section's own headline count is the real one. It said 45 while 50 were served,
    /// which is worse than saying nothing: a reader who counts the rows and trusts the number cannot
    /// tell which is stale.
    /// </summary>
    [Fact]
    public void The_readme_headline_count_matches_the_number_of_tools()
    {
        var path = Res.FindRepoFile("README.md");
        Assert.NotNull(path);

        using var provider = ServedTools();
        var served = provider.GetServices<McpServerTool>().Count();

        Assert.Contains($"**{served} tools over ", File.ReadAllText(path!));
    }
}
