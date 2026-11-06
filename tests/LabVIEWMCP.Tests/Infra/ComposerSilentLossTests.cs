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
    /// A multicolumn listbox (rsrc 21791) holding an int32: the only 21791 parts are typed Array,
    /// and taking one - retyped - KILLED LabVIEW on load, even with no items (4 of the 9 composed
    /// examples that crashed, 2026-11-06). The part must be of the control's own type - here the
    /// multicolumn INDICATOR part (rsrc 21798, NumInt32), never the Array-typed control part whose
    /// donor is NI's System Mixed Checkbox.vi.
    /// </summary>
    [Fact]
    public async Task A_multicolumn_listbox_never_takes_a_part_of_another_data_type()
    {
        const string doc = """
            <VI _name="Probe.vi" description="x">
              <Control _name="Multicolumn Listbox" type="int32" uid="4201" uid_parent="root" value="0" outputs="value:" />
            </VI>
            <?lvmcp fpclass uid="4201" class="listbox" rsrc="21791" strings="[[2, 2], [&quot;b&quot;, &quot;d&quot;, &quot;c&quot;, &quot;e&quot;], []]"?>
            """;
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.True(result.Written, result.Message);
        var donor = Assert.Single(result.Report, line => line.StartsWith("Control 'Multicolumn Listbox' <-", StringComparison.Ordinal));
        Assert.DoesNotContain("System Mixed Checkbox", donor);
    }

    [Fact]
    public async Task The_same_chart_without_a_bound_node_still_composes_as_a_plain_cluster()
    {
        var doc = "<VI _name=\"Probe.vi\" description=\"x\">\n" + Chart + "</VI>\n";
        if (await ComposeAsync(doc) is not { } result) return;

        Assert.True(result.Written, result.Message);
    }
}
