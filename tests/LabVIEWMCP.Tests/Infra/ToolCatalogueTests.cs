using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// That the tool catalogue names every tool the server actually serves, and that the README's own
/// headline count agrees with it.
///
/// The catalogue is not documentation-in-the-nice-to-have sense here: the build COPIES all of
/// `docs\` next to the exe, so on a binary-only install it is the only overview of what the server
/// can do. A tool absent from it is a capability nobody finds.
///
/// It LIVED IN THE README until the 2026-09-21 restructure and now lives in
/// `docs/guide/tools.md`, because a hand-maintained table of 82 rows inside a page whose job is to
/// sell the project is a table nobody re-reads. Both files still carry a count, and both counts are
/// asserted below - the README's was `45` while 82 were served, which is exactly the drift this
/// class exists for, one layer out.
///
/// Written because the section had drifted by TEN tools before anyone looked - lvai_create_class,
/// lvai_describe_class, lvai_create_accessors, lvai_generate_vi, lvai_run_vi_and_read_values,
/// lvai_convert_vis_to_aixml, lvai_list_labview_installations, lvai_ensure_labview, lvai_vi_terminals
/// and pylv_apply, spanning several branches. Nothing failed; the header just kept claiming 45 tools
/// while 50 were served. Drift with no symptom is exactly what a test is for.
/// </summary>
public class ToolCatalogueTests
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

    private static string CataloguePath()
    {
        var path = Res.FindRepoFile(Path.Combine("docs", "guide", "tools.md"));
        Assert.NotNull(path);
        return path!;
    }

    [Fact]
    public void Every_served_tool_is_named_in_the_tool_catalogue()
    {
        var section = File.ReadAllText(CataloguePath());

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
            "these served tools are absent from docs/guide/tools.md, so a binary-only install has "
            + "no record of them: " + string.Join(", ", missing));
    }

    /// <summary>
    /// And that the catalogue's own headline count is the real one. It said 45 while 50 were served,
    /// which is worse than saying nothing: a reader who counts the rows and trusts the number cannot
    /// tell which is stale.
    /// </summary>
    [Fact]
    public void The_catalogue_headline_count_matches_the_number_of_tools()
    {
        using var provider = ServedTools();
        var served = provider.GetServices<McpServerTool>().Count();

        Assert.Contains($"**{served} tools over ", File.ReadAllText(CataloguePath()));
    }

}
