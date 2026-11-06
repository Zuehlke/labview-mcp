using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Two ways the composer built a DIFFERENT VI than its document described, without a word - found
/// by composing every example of LabVIEW 2026 from the reader's export and loading the result
/// (docs/aixml-backends.md, "The composer over NI's shipping examples", 2026-11-06):
/// 1. a vi2aixml export whose notes say it LOST STRUCTURE (a node it does not read, the wires
///    touching it) composed into a VI missing that node - 30 of the 74 broken composed free VIs,
///    and some 50 that loaded executable while doing less than their original;
/// 2. a chart whose style has no part was composed as a plain cluster, and a Property Node bound to
///    it by `link=` was then `Invalid dotted property` in LabVIEW.
/// Both are refused by name now; each test also pins the case that must still compose.
/// SKIPS where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class ComposerSilentLossTests
{
    private static async Task<AixmlComposer.Result?> ComposeAsync(string document)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || PyLabview.Locate() is null) return null;
        var dir = Path.Combine(Path.GetTempPath(), "silentloss-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            await File.WriteAllTextAsync(xml, document);
            return await AixmlComposer.ComposeAsync(setup, xml, Path.Combine(dir, "Probe.vi"), [], 300, default);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private const string Body = """
          <Control _name="input" type="string" uid="4201" uid_parent="root" value="abc" outputs="value:4201.value" />
          <Node _name="String Length" uid="4202" uid_parent="root" inputs="string:4201.value" outputs="length:4202.length" />
          <Indicator _name="length" type="int32" uid="4203" uid_parent="root" value="0" inputs="value:4202.length" />
        """;

    [Fact]
    public async Task A_reader_export_that_lost_a_node_is_refused_by_name()
    {
        var doc = "<VI _name=\"Probe.vi\" description=\"x\">\n" + Body +
                  "  <!-- vi2aixml: diagram node of class extFunc (uid 37) not read -->\n" +
                  "  <!-- vi2aixml: signal 227 touches an unread terminal -->\n</VI>\n";
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.False(result.Written);
        Assert.True(result.Refused, result.Message);
        Assert.Contains("LOST STRUCTURE", result.Message);
        Assert.Contains("extFunc", result.Message);
    }

    [Fact]
    public async Task A_reader_note_about_a_value_only_still_composes()
    {
        var doc = "<VI _name=\"Probe.vi\" description=\"x\">\n" + Body +
                  "  <!-- vi2aixml: constant 55: value of type LVVariant not decoded -->\n</VI>\n";
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.True(result.Written, result.Message);
    }

    private const string Chart = """
          <Indicator _name="Chart" type="cluster{double.initial PV,double.setpoint}" style="graph21717" uid="4300" uid_parent="root" value="[0,0]" inputs="value:" />
          <Constant _name="k" type="double" uid="4301" uid_parent="root" value="15" outputs="value:4301.value" />
        """;

    [Fact]
    public async Task A_property_node_bound_to_a_chart_composed_as_a_plain_cluster_is_refused()
    {
        var doc = "<VI _name=\"Probe.vi\" description=\"x\">\n" + Chart +
                  "  <Node _name=\"Property Node\" uid=\"4310\" uid_parent=\"root\" fields=\"write+X Scale.Range\\3AMaximum\" " +
                  "link=\"Chart\" type=\"{LV.WaveformChart}\" inputs=\"X Scale.Range\\3AMaximum:4301.value\" />\n</VI>\n";
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.False(result.Written);
        Assert.True(result.Refused, result.Message);
        Assert.Contains("VI Server node is bound to 'Chart'", result.Message);
    }

    /// <summary>
    /// The two Array-typed listbox parts, both cut from NI's System Mixed Checkbox.vi, KILLED
    /// LabVIEW on load whatever control they served - an int32 multicolumn listbox (they were the
    /// only 21791 parts) and an array{int32} listbox (they were its exact type) alike, 2026-11-06.
    /// They are denylisted; a NumInt32 part re-typed serves both and loads. Neither control may be
    /// composed from that donor again.
    /// </summary>
    [Theory]
    [InlineData("int32", "21791", "[[2, 2], [&quot;b&quot;, &quot;d&quot;, &quot;c&quot;, &quot;e&quot;], []]")]
    [InlineData("array{int32}", "21791", "[[5, 2], [&quot;T1&quot;, &quot;Fail&quot;, &quot;T2&quot;, &quot;Fail&quot;, &quot;T3&quot;, &quot;Pass&quot;, &quot;T4&quot;, &quot;Fail&quot;, &quot;T5&quot;, &quot;Pass&quot;], []]")]
    [InlineData("array{int32}", "21792", "[[3, 1], [&quot;a&quot;, &quot;b&quot;, &quot;c&quot;], []]")]
    public async Task A_listbox_is_never_composed_from_the_denylisted_array_parts(string type, string rsrc, string strings)
    {
        var doc = $"""
            <VI _name="Probe.vi" description="x">
              <Control _name="Listbox" type="{type}" uid="4201" uid_parent="root" value="0" outputs="value:" />
            </VI>
            <?lvmcp fpclass uid="4201" class="listbox" rsrc="{rsrc}" strings="{strings}"?>
            """.Replace("value=\"0\"", type.StartsWith("array", StringComparison.Ordinal) ? "value=\"[]\"" : "value=\"0\"");
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.True(result.Written, result.Message);
        var donor = Assert.Single(result.Report, line => line.StartsWith("Control 'Listbox' <-", StringComparison.Ordinal));
        Assert.DoesNotContain("System Mixed Checkbox", donor);
    }

    /// <summary>
    /// NI's Vertical Fill Slide.vi writes {LV.Slide} Fill Color to an indicator the reader exports
    /// as a plain double - no style, no class - so it is composed as a digital numeric, and
    /// LV.Digital has no Fill Color: LabVIEW loaded the node `Invalid property` (2026-11-06; the
    /// trees of the Tree examples and the PID slides the same way). Refused by name now; a member
    /// the plain class has (Visible) still composes.
    /// </summary>
    [Theory]
    [InlineData("Fill Color", true)]
    [InlineData("Visible", false)]
    public async Task A_node_bound_to_a_plain_control_is_refused_only_for_members_the_plain_class_lacks(string property, bool refused)
    {
        var doc = $"""
            <VI _name="Probe.vi" description="x">
              <Indicator _name="Slide" type="double" uid="4201" uid_parent="root" value="0" inputs="value:" />
              <Constant _name="v" type="bool" uid="4202" uid_parent="root" value="true" outputs="value:4202.value" />
              <Node _name="Property Node" uid="4203" uid_parent="root" fields="write+{property}" link="Slide" type="{"{"}LV.Slide{"}"}" inputs="{property}:4202.value" />
            </VI>
            """;
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.Equal(refused, result.Refused);
        if (refused)
            Assert.Contains("composed as a plain LV.Digital", result.Message);
        else
            Assert.True(result.Written, result.Message);
    }

    /// <summary>
    /// 16 of the 36 graph parts with an AIXML type made a probe holding the graph ALONE unloadable
    /// (`Cannot load front panel`, 1013; one killed LabVIEW) - every non-double numeric graph or
    /// chart, dynamic data, the intensity parts - while the double-typed ones load (2026-11-06,
    /// graph_part_probes.py). They are denylisted; such a graph is composed as its plain data type,
    /// and 13 examples whose panel would not load are executable since.
    /// </summary>
    [Fact]
    public async Task A_graph_whose_part_is_denylisted_is_composed_as_its_plain_type()
    {
        const string doc = """
            <VI _name="Probe.vi" description="x">
              <Indicator _name="Unflattened Data" type="array{int16}" style="graph21703" uid="4201" uid_parent="root" value="[]" inputs="value:" />
            </VI>
            """;
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.True(result.Written, result.Message);
        Assert.Contains(result.Report, line => line.Contains("not composed", StringComparison.Ordinal)
                                               && line.Contains("Unflattened Data", StringComparison.Ordinal));
    }

    /// <summary>
    /// A static reference to a graph composed as its plain type is a reference of the plain class:
    /// NI's fuzzy greenhouse example hands its XY graph's reference to FL Plot Variable's `graph
    /// refnum`, and the composed VI was `Wire: Class conflict` (2026-11-06). Refused by name now.
    /// </summary>
    [Fact]
    public async Task A_static_reference_to_a_degraded_graph_is_refused()
    {
        var doc = "<VI _name=\"Probe.vi\" description=\"x\">\n" + Chart +
                  "  <Node _name=\"VI Server Reference\" uid=\"4310\" uid_parent=\"root\" element=\"Chart\" outputs=\"Chart:4310.Chart\" />\n</VI>\n";
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.True(result.Refused, result.Message);
        Assert.Contains("its reference would be of that class", result.Message);
    }

    [Fact]
    public async Task The_same_chart_without_a_bound_node_still_composes_as_a_plain_cluster()
    {
        var doc = "<VI _name=\"Probe.vi\" description=\"x\">\n" + Chart + "</VI>\n";
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.True(result.Written, result.Message);
    }
}
