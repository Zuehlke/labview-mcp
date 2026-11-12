using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// 2026-11-12, DQMH_Retest: lvai_layout_diagrams over a 47-VI folder outlived the client's 60 s,
/// the server saved 40 VIs and the answer with their rows was lost. The call now answers
/// `stillRunning` with progress, and the same call collects the whole answer.
/// </summary>
public class LayoutManyResumableTests
{
    [Fact]
    public void A_folder_expands_to_its_vis_in_order_and_a_missing_line_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "layout-many-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        File.WriteAllText(Path.Combine(root, "B.vi"), "");
        File.WriteAllText(Path.Combine(root, "Sub", "A.vi"), "");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "");
        try
        {
            var targets = DiagramLayoutTools.ResolveTargets(root + "\n" + Path.Combine(root, "B.vi"), out var refusal);
            Assert.Null(refusal);
            Assert.Equal([Path.Combine(root, "B.vi"), Path.Combine(root, "Sub", "A.vi")], targets);

            Assert.Null(DiagramLayoutTools.ResolveTargets(Path.Combine(root, "Missing.vi"), out refusal));
            Assert.Equal("viNotFound", (string?)JsonNode.Parse(refusal!)!["errorKind"]);

            Assert.Null(DiagramLayoutTools.ResolveTargets("  \n ", out refusal));
            Assert.Equal("badArguments", (string?)JsonNode.Parse(refusal!)!["errorKind"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The repeat that collects must be recognised as the same call; a different VI set is not.</summary>
    [Fact]
    public void The_key_ignores_case_and_budgets_but_not_the_work()
    {
        var a = DiagramLayoutTools.LayoutManyKey([@"C:\P\A.vi", @"C:\P\B.vi"], 4, true, true, "pylabview");
        Assert.Equal(a, DiagramLayoutTools.LayoutManyKey([@"c:\p\a.vi", @"C:\P\B.VI"], 4, true, true, "pylabview"));
        Assert.NotEqual(a, DiagramLayoutTools.LayoutManyKey([@"C:\P\A.vi"], 4, true, true, "pylabview"));
        Assert.NotEqual(a, DiagramLayoutTools.LayoutManyKey([@"C:\P\A.vi", @"C:\P\B.vi"], 4, false, true, "pylabview"));
        Assert.NotEqual(a, DiagramLayoutTools.LayoutManyKey([@"C:\P\A.vi", @"C:\P\B.vi"], 4, true, true, "grpc"));
    }

    [Fact]
    public void The_label_names_the_given_lines_and_the_count_not_every_path()
    {
        Assert.Equal(@"lvai_layout_diagrams over 57 VIs: C:\temp\Branch_Retest",
            DiagramLayoutTools.LayoutManyLabel(@"C:\temp\Branch_Retest", 57));
        Assert.Equal(@"lvai_layout_diagrams over 1 VI: C:\P\A.vi and 2 more lines",
            DiagramLayoutTools.LayoutManyLabel("C:\\P\\A.vi\nC:\\P\\B.vi\r\nC:\\P\\C.vi", 1));
    }

    [Fact]
    public void Progress_counts_done_failed_and_in_work()
    {
        var p = new DiagramLayoutTools.LayoutProgress(3);
        p.Started(@"C:\P\A.vi");
        p.Started(@"C:\P\B.vi");
        p.Finished(@"C:\P\A.vi", ok: true);
        p.Finished(@"C:\P\B.vi", ok: false);
        p.Started(@"C:\P\C.vi");

        var j = p.ToJson();
        Assert.Equal(3, (int)j["vis"]!);
        Assert.Equal(2, (int)j["done"]!);
        Assert.Equal(["B.vi"], j["failedSoFar"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["C.vi"], j["inWork"]!.AsArray().Select(n => (string)n!));
    }
}
