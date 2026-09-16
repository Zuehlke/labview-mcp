using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The swap helper must reach a subVI call ANYWHERE on the diagram, not only on the top level.
///
/// WHAT WENT WRONG, MEASURED 2026-09-16. The helper collected its candidates from
/// <c>{LV.Diagram} SubVIs[]</c> on the block diagram. That property lists the nodes of the diagram
/// it is asked about and does NOT descend into structures, so every call inside a While Loop or a
/// Case frame was invisible. On a main VI whose six subVI calls all sit inside its event loop, one
/// call swapped the three at the loop's own level and reported the two nested in Case frames under
/// <c>socketsNotOnDiagram</c> — a field whose text says the name is not on the diagram, while the
/// node was plainly there. The caller was left with a VI still pointing at its sockets.
///
/// WHY THE FALLBACK WAS NOT AN ANSWER. The documented alternative — a pylabview
/// <c>{"op":"retarget"}</c> — rewrote the link records and produced a VI LabVIEW then refused to
/// load: <c>Missing subVI &lt;name&gt; in VI &lt;caller&gt;</c>, with <c>callTargets</c> and the AIXML
/// export both green. So neither route reached a nested node, and a generated main VI could not be
/// linked to its own subVIs at all.
///
/// THE FIX is <c>Traverse for GObjects.vi</c>, which walks the whole block diagram. It returns
/// GObject references rather than <c>{LV.SubVI}</c> ones, so each is cast with
/// <c>To More Specific Class</c> before <c>VI Name</c> is read or <c>Replace</c> is invoked — that
/// cast is part of the fix, not decoration, and a helper that traversed without it would fail at
/// the first node.
///
/// This asserts the shipped helper, not a fixture copy of it: the claim is about what
/// <c>scripts\lvai_swap_subvis.xml</c> does, and a fixture would keep passing while the helper
/// drifted back.
/// </summary>
public sealed class SwapTraversalTests
{
    private static string Helper() =>
        File.ReadAllText(RepoTree.Path("scripts", "lvai_swap_subvis.xml"));

    /// <summary>The traversal that descends into loops and case frames is the one in use.</summary>
    [Fact]
    public void CollectsSubVIsByTraversing() =>
        Assert.Contains("Traverse for GObjects.vi", Helper(), StringComparison.Ordinal);

    /// <summary>
    /// The control arm. <c>SubVIs[]</c> is the property that could not see into a structure, so its
    /// absence is what the fix actually consists of; asserting only that Traverse appears would
    /// pass on a helper that read both and still used the shallow list.
    ///
    /// The match is on <c>read+SubVIs[]</c>, the PROPERTY NODE spelling, not on the bare name. The
    /// bare name also occurs in the helper's own description, where it explains why it is not used
    /// — and a whole-file substring check failed on exactly that, which is the same fixture mistake
    /// as asserting against prose instead of against the diagram.
    /// </summary>
    [Fact]
    public void DoesNotCollectSubVIsFromTheTopDiagramOnly() =>
        Assert.DoesNotContain("read+SubVIs[]", Helper(), StringComparison.Ordinal);

    /// <summary>
    /// Traverse hands back GObject references. Without the cast the helper cannot read
    /// <c>VI Name</c> or call <c>Replace</c> on them, so the fix is incomplete without it.
    /// </summary>
    [Fact]
    public void CastsTraversedReferencesToSubVI()
    {
        var helper = Helper();
        Assert.Contains("To More Specific Class", helper, StringComparison.Ordinal);
        Assert.Contains("type=\"ref{LV.SubVI}\"", helper, StringComparison.Ordinal);
    }

    /// <summary>
    /// The traversal is over the BLOCK DIAGRAM. <c>Traverse Target</c> is an enumeration
    /// <c>{FP,BD,Other}</c>; index 1 is BD, and index 0 would walk the front panel and find no
    /// subVI node at all — a wrong constant here gives an empty candidate list, which reads exactly
    /// like the bug this replaces.
    /// </summary>
    [Fact]
    public void TraversesTheBlockDiagram()
    {
        var helper = Helper();
        Assert.Contains("uint16{FP,BD,Other}", helper, StringComparison.Ordinal);
        Assert.Contains("Class Name", helper, StringComparison.Ordinal);
    }
}
