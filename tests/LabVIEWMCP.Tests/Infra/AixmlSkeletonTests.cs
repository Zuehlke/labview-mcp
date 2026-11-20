using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The AIXML skeletons agents copy from (scripts/aixml-skeletons). Six were added 2026-11-18 from
/// a count over 762 agent-written AIXML files; each was generated on both backends and run. What
/// is pinned here is what needs no LabVIEW: the files stay free of what our own pre-check refuses,
/// keep the house rules they are meant to teach, and the index names every one of them - an agent
/// reads the index, so a skeleton missing from it does not exist.
/// </summary>
public sealed class AixmlSkeletonTests
{
    private static readonly string[] Added =
    [
        "cluster-array-loop", "text-table-read", "text-table-write",
        "find-by-key", "control-group-disable", "string-command-queue",
        "record-update", "path-beside-vi", "log-append", "error-case",
        "graph-plot-names", "panel-close-event",
    ];

    public static TheoryData<string> AddedSkeletons => new(Added);

    private static string Folder => RepoTree.Path("scripts", "aixml-skeletons");

    private static string Text(string name) => File.ReadAllText(Path.Combine(Folder, name + ".xml"));

    [Theory]
    [MemberData(nameof(AddedSkeletons))]
    public void ASkeletonPassesThePreCheckWithNoErrorAndNoWarning(string name)
    {
        var findings = AixmlCheck.Check(Text(name));

        Assert.DoesNotContain(findings, f => f.Severity is AixmlCheck.Severity.Error or AixmlCheck.Severity.Warning);
    }

    [Theory]
    [MemberData(nameof(AddedSkeletons))]
    public void ASkeletonKeepsTheHouseRulesItTeaches(string name)
    {
        var text = Text(name);
        var vi = XDocument.Parse(text).Root!;

        // an XML comment before or inside <VI> empties the VI or refuses the file
        Assert.DoesNotContain("<!--", text);
        Assert.Equal(name + ".vi", vi.Attribute("_name")!.Value);

        var errorIn = vi.Elements("Control").Single(c => c.Attribute("_name")!.Value == "error in");
        Assert.Equal("required", errorIn.Attribute("connection")!.Value);
        Assert.Equal("11", errorIn.Attribute("conIdx")!.Value);
        Assert.Equal(BuildContract.ErrorClusterType, errorIn.Attribute("type")!.Value);

        var errorOut = vi.Elements("Indicator").Single(c => c.Attribute("_name")!.Value == "error out");
        Assert.Equal("recommended", errorOut.Attribute("connection")!.Value);
        Assert.Equal("15", errorOut.Attribute("conIdx")!.Value);
        // error out is wired - to the chain, or to error in where nothing in the VI can fail
        Assert.NotEmpty(errorOut.Attribute("inputs")!.Value);

        // every output on the pane is recommended, every terminal says its connection
        Assert.All(vi.Elements().Where(e => e.Attribute("conIdx") is not null),
                   e => Assert.NotNull(e.Attribute("connection")));
        Assert.All(vi.Elements("Indicator").Where(i => i.Attribute("conIdx") is not null),
                   i => Assert.Equal("recommended", i.Attribute("connection")!.Value));

        // one diagram comment, short enough for its box
        var comment = vi.Elements("FreeLabel").Single().Attribute("comment")!.Value;
        Assert.InRange(comment.Length, 1, 45);

        // uids from 4200, each once
        var uids = vi.DescendantsAndSelf().Select(e => e.Attribute("uid")?.Value).Where(u => u is not null).ToList();
        Assert.All(uids, u => Assert.True(int.Parse(u!) >= AixmlCheck.SafeUidBase, $"uid {u} is below {AixmlCheck.SafeUidBase}"));
        Assert.Equal(uids.Count, uids.Distinct().Count());
    }

    /// <summary>
    /// Measured 2026-11-18: Release Queue fed straight from Obtain Queue ran before the first
    /// Enqueue - Error 1, nothing handled, and neither validation nor execState saw it.
    /// </summary>
    [Fact]
    public void TheQueueIsReleasedOnlyAfterTheConsumerLoop()
    {
        var vi = XDocument.Parse(Text("string-command-queue")).Root!;
        static string Name(XElement e) => e.Attribute("_name")?.Value ?? "";

        var obtain = vi.Descendants("Node").Single(n => Name(n) == "Obtain Queue");
        var release = vi.Descendants("Node").Single(n => Name(n) == "Release Queue");
        var consumer = vi.Elements("Structure").Single(s => Name(s) == "While Loop");

        var queueIntoRelease = release.Attribute("inputs")!.Value.Split(',')[0];
        Assert.DoesNotContain(obtain.Attribute("uid")!.Value + ".", queueIntoRelease);
        var outTunnels = consumer.Elements("Tunnel").Where(t => t.Attribute("_id")!.Value.StartsWith("Out"))
                                 .Select(t => t.Attribute("uid")!.Value);
        Assert.Contains(outTunnels, uid => queueIntoRelease == $"queue:{uid}.value");
    }

    /// <summary>
    /// Measured 2026-11-20: with the data wired to the graph's TERMINAL, the pylabview build ran the
    /// naming loop first and `Active Plot` 1 answered Error 1077 on a one-plot graph. The data goes
    /// in through `write+Value` on the error chain, before any Active Plot, and every plot field is
    /// the dotted sub-object field with its access prefix.
    /// </summary>
    [Fact]
    public void ThePlotsExistBeforeTheyAreNamed_and_every_plot_field_is_dotted()
    {
        var vi = XDocument.Parse(Text("graph-plot-names")).Root!;
        var graph = vi.Elements("Indicator").Single(i => i.Attribute("_name")!.Value == "Graph");
        Assert.Equal("value:", graph.Attribute("inputs")!.Value);

        var nodes = vi.Descendants("Node").Where(n => n.Attribute("_name")!.Value == "Property Node").ToList();
        var first = nodes.Single(n => n.Parent == vi);
        Assert.StartsWith("write+Value,", first.Attribute("fields")!.Value);

        var fields = nodes.SelectMany(n => n.Attribute("fields")!.Value.Split(',')).ToList();
        Assert.All(fields, f => Assert.Matches("^(read|write)\\+", f));
        Assert.DoesNotContain(fields, f => f.EndsWith("+Plot Name", StringComparison.Ordinal));
        Assert.All(nodes.Where(n => n.Attribute("fields")!.Value.Contains("Plot.Plot Name")),
                   n => Assert.StartsWith("write+Active Plot,", n.Attribute("fields")!.Value));
    }

    [Fact]
    public void PanelClose_is_the_VIs_own_filter_event_with_its_filter_node()
    {
        var vi = XDocument.Parse(Text("panel-close-event")).Root!;
        var frame = vi.Descendants("CaseFrame").Single(f => f.Attribute("selector")!.Value == " Panel Close? ");
        var filter = frame.Elements("Node").Single(n => n.Attribute("_name")!.Value == "Event Filter Node");
        Assert.Equal("Discard?", filter.Attribute("fields")!.Value);
    }

    [Fact]
    public void TheIndexNamesEverySkeletonFile()
    {
        var index = File.ReadAllText(Path.Combine(Folder, "README.md"));

        Assert.All(Directory.GetFiles(Folder, "*.xml").Select(Path.GetFileName),
                   file => Assert.Contains($"`{file}`", index));
    }
}
