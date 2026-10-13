using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// WHICH APPLICATION INSTANCE the coercion sweep opens the VI in. Measured 2026-10-13: with a
/// project active, the helper's old Open VI Reference (no application reference, so the helper's
/// own instance) read NO subVI calls on a caller the project held - while the same call with no
/// project read one, three times in a row. lvai_bind_typedef_constants needs the project, so its
/// name resolution and its verify sweep both came back empty. The helper now opens the VI in the
/// active project's instance, and in its own only when no project is active (the pylv_apply case).
/// </summary>
public sealed class CoercionSweepInstanceTests
{
    private static XElement Helper() =>
        XElement.Load(RepoTree.Path("scripts", TypedefTools.DotsHelperAixmlFileName));

    private static IEnumerable<XElement> Opens(XElement helper) =>
        helper.Descendants("Node").Where(n => (string?)n.Attribute("_name") == "Open VI Reference");

    [Fact]
    public void TheVIIsOpenedInTheActiveProjectsInstanceWhenThereIsOne()
    {
        var helper = Helper();
        var lookup = helper.Descendants("Node").Single(n =>
            ((string?)n.Attribute("fields") ?? "").Contains("Project\\3AActive Project"));
        var app = helper.Descendants("Node").Single(n =>
            (string?)n.Attribute("type") == "{LV.Project}" &&
            (string?)n.Attribute("fields") == "read+Application");
        Assert.Contains($"reference:{lookup.Attribute("uid")!.Value}.proj",
                        (string?)app.Attribute("inputs"));

        var frames = helper.Descendants("CaseFrame").ToList();
        var noError = frames.Single(f => (string?)f.Attribute("selector") == "False");
        var open = Opens(noError).Single();
        Assert.StartsWith("application reference (local):", (string?)open.Attribute("inputs"));
    }

    [Fact]
    public void WithNoProjectTheVIIsOpenedInTheHelpersOwnInstance()
    {
        // pylv_apply calls the sweep with the project CLOSED; that route must keep working.
        var frames = Helper().Descendants("CaseFrame").ToList();
        var noProject = frames.Single(f => (string?)f.Attribute("selector") == "True");
        var open = Opens(noProject).Single();
        Assert.DoesNotContain("application reference", (string?)open.Attribute("inputs"));
    }

    [Fact]
    public void BothRoutesFeedTheSameReferenceTheRestOfTheHelperReads()
    {
        var helper = Helper();
        Assert.Equal(2, Opens(helper).Count());
        var outputs = helper.Elements("Structure")
            .Single(s => (string?)s.Attribute("_name") == "Case Structure")
            .Elements("Tunnel").Select(t => (string?)t.Attribute("outputs")).ToList();
        Assert.Contains("value:24.vi reference", outputs);
        Assert.Contains("value:24.error out", outputs);
    }
}
