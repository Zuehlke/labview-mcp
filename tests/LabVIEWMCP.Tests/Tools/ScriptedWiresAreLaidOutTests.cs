using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The user's rule of 2026-11-12: a tool that connects wires by VI Server scripting ends with our
/// layout over the VI it wired. Guarded on the source: every tool that runs a wiring helper must
/// also run DiagramCleanup, and place_handler must include the DQMH Main.vi the automatic clean-up
/// otherwise skips.
/// </summary>
public class ScriptedWiresAreLaidOutTests
{
    /// <summary>The scripts that call {LV.Terminal} Connect Wire, and the tools that run them.</summary>
    private static readonly string[] WiringTools =
    [
        "DqmhFrameTools.cs",      // lvbd_connect_by_names, lvbd_splice_by_names, lvdqmh_wire_module_data
        "AfOverrideTools.cs",     // lvai_af_add_hook, lvai_af_remove_hook
        "GraftTools.cs",          // lvbd_graft_rewire
        "BindReferenceTools.cs",  // lvbd_bind_control_refs
        "WireEventTools.cs",      // lvai_wire_dyn_events
    ];

    [Theory]
    [MemberData(nameof(Tools))]
    public void A_tool_that_scripts_wires_lays_the_VI_out(string file)
    {
        var source = File.ReadAllText(RepoTree.Path("src", "LabVIEWMCP", "Tools", file));
        Assert.Contains("new DiagramCleanup(connection).RunAsync(", source);
    }

    public static TheoryData<string> Tools() => new(WiringTools);

    [Fact]
    public void Place_handler_lays_out_the_whole_DQMH_main()
    {
        var source = File.ReadAllText(RepoTree.Path("src", "LabVIEWMCP", "Tools", "DqmhFrameTools.cs"));
        Assert.Contains("includeDqmhMain: true", source);
    }

    /// <summary>
    /// The user's decision of 2026-11-12: the VIs these tools save inside an open project carry
    /// Delacor's or NI's compiled code, and are made source only afterwards.
    /// </summary>
    [Theory]
    [InlineData("DqmhFrameTools.cs")]
    [InlineData("AfOverrideTools.cs")]
    public void A_vi_saved_inside_the_project_is_made_source_only(string file)
    {
        var source = File.ReadAllText(RepoTree.Path("src", "LabVIEWMCP", "Tools", file));
        Assert.Contains("SourceOnlyInProjectAsync(", source);
        Assert.Contains("[\"sourceOnly\"]", source);
    }

    /// <summary>A new script that connects wires has to be named above, with the tool that runs it.</summary>
    [Fact]
    public void Every_wiring_script_is_known()
    {
        var wiring = Directory.EnumerateFiles(RepoTree.Path("scripts"), "*.xml")
            .Where(f => File.ReadAllText(f).Contains("Connect Wire", StringComparison.Ordinal))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
        [
            "lvai_af_add_hook.xml", "lvai_af_remove_hook.xml", "lvai_wire_dyn_events.xml",
            "lvbd_bind_control_refs.xml", "lvbd_connect_by_names.xml", "lvbd_graft_rewire.xml",
            "lvbd_splice_by_names.xml", "lvdqmh_wire_module_data.xml",
        ], wiring);
    }
}
