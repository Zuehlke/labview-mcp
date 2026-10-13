using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <c>lvai_lunit_add_test_method</c>'s helper carried the race <c>lvai_add_class_method</c>'s had
/// until 2026-10-12: <c>LVClass.Open</c> took its error in from the <c>Controls[]</c> read BEFORE
/// the Replace loop, so the membership step and both saves were free to run before the Replaces.
/// Found 2026-10-13 on the pylabview backend, whose composer schedules a diagram differently from
/// NI's converter: every test method came out with both class terminals still path stand-ins while
/// <c>terminals retyped</c> answered 2. Same assertions as <see cref="ClassMethodRetypeSequenceTests"/>.
/// </summary>
public class LUnitRetypeSequenceTests
{
    private static readonly string Helper = RepoTree.Path("scripts", "lvlu_add_test_method.xml");

    private static XElement Tree() => XElement.Load(Helper);

    private static XElement ByTarget(XElement tree, string target) =>
        tree.Descendants("Node").Single(n => (string?)n.Attribute("target") == target);

    private static string ReplaceLoopUid(XElement tree) =>
        (string)ByTarget(tree, "Replace").Ancestors("Structure")
            .Last(s => (string?)s.Attribute("_name") == "For Loop").Attribute("uid")!;

    [Theory]
    [InlineData("LVClass.Open")]
    [InlineData("AddItemFromMemory")]
    [InlineData("Save.Instrument")]
    [InlineData("Save")]
    public void The_class_chain_waits_for_the_replace_loop(string target)
    {
        var tree = Tree();
        Assert.True(ClassMethodRetypeSequenceTests.DependsOnStructureIn(
            tree, ByTarget(tree, target), ReplaceLoopUid(tree)), target);
    }

    [Fact]
    public void The_replace_error_out_is_wired_and_the_panel_read_back()
    {
        var tree = Tree();
        Assert.DoesNotContain("error out:,", (string?)ByTarget(tree, "Replace").Attribute("outputs") ?? "");
        var names = tree.Descendants("Indicator").Select(i => (string?)i.Attribute("_name")).ToList();
        Assert.Contains("replace error codes", names);
        Assert.Contains("classes after replace", names);
        Assert.Contains("terminals retyped", names);
    }

    [Fact]
    public void Each_replace_reads_its_control_reference_afresh()
    {
        var tree = Tree();
        var loop = ReplaceLoopUid(tree);
        Assert.Single(tree.Descendants("Node")
            .Where(n => (string?)n.Attribute("fields") == "read+Controls[]")
            .Where(n => n.Ancestors("Structure").Any(s => (string?)s.Attribute("uid") == loop)));
    }
}
