using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <see cref="ClassTools.NextSliceWouldOverrun"/> — when `lvai_create_accessors` should stop slicing.
///
/// WHY THIS IS TESTED RATHER THAN LEFT INLINE. The condition it replaces was
/// `elapsed >= budgetSeconds`, checked BETWEEN slices, and it was wrong twice in production with
/// real cost. First with `budgetSeconds: 100`: two slices of a four-field class totalled 53-80 s
/// against an MCP client that stops waiting near 60 s, so the answer was lost — and the resume that
/// followed rebuilt a field, NI's wizard appended a number, and a corrupted class came back reported
/// as `ok: true`. Then with the DEFAULT 45: a slice starting at 44 s ran ~20 s more and carried the
/// call past 60 s again. Neither constant was safe, because the check ignored how long the NEXT
/// slice would take.
///
/// A lost answer is also not a lost slice — the work keeps running inside LabVIEW after the client
/// gives up — so the caller cannot tell how far it got except by reading the class file. That is
/// what makes an overrun expensive rather than merely slow, and why this errs towards stopping.
/// </summary>
public sealed class AccessorSliceBudgetTests
{
    /// <summary>
    /// The measured case that motivated the change: ~19 s slices against the default 45 s budget.
    /// The first slice must be followed by a second (38 s projected, fits), and the second must NOT
    /// be followed by a third (57 s projected, does not) — which is exactly what the old condition
    /// got wrong, because at 38 s elapsed it saw 38 &lt; 45 and started another slice.
    /// </summary>
    [Fact]
    public void NineteenSecondSlicesStopAfterTheSecond()
    {
        Assert.False(ClassTools.NextSliceWouldOverrun(19_000, 19_000, 45));   // 38 s projected
        Assert.True(ClassTools.NextSliceWouldOverrun(38_000, 19_000, 45));    // 57 s projected
    }

    /// <summary>
    /// The other measured case: one expensive slice. 35 s + 35 s projected is 70 s, so the call must
    /// return after a single slice even though 35 s is well under the 45 s budget.
    /// </summary>
    [Fact]
    public void OneExpensiveSliceStopsImmediately()
        => Assert.True(ClassTools.NextSliceWouldOverrun(35_000, 35_000, 45));

    /// <summary>Cheap slices keep going — the change must not make a small class cost extra calls.</summary>
    [Theory]
    [InlineData(8_000, 8_000)]
    [InlineData(13_500, 7_900)]
    [InlineData(21_000, 11_000)]
    public void CheapSlicesKeepSlicing(long elapsedMs, long lastSliceMs)
        => Assert.False(ClassTools.NextSliceWouldOverrun(elapsedMs, lastSliceMs, 45));

    /// <summary>
    /// The old condition, stated as a test so the difference is explicit rather than asserted in a
    /// comment: there is a band where `elapsed < budget` — so the old check started another slice —
    /// and the projection is already over. Every production overrun lived in this band.
    /// </summary>
    [Theory]
    [InlineData(30_000, 20_000, 45)]   // old: 30 < 45, go.  new: 50 projected, stop.
    [InlineData(44_000, 20_000, 45)]   // the measured failure: 64 s, answer lost
    public void TheOldConditionWouldHaveContinuedWhereThisStops(
        long elapsedMs, long lastSliceMs, int budgetSeconds)
    {
        Assert.True(elapsedMs < budgetSeconds * 1000L);                  // the old check said go
        Assert.True(ClassTools.NextSliceWouldOverrun(elapsedMs, lastSliceMs, budgetSeconds));
    }

    /// <summary>
    /// AND THE LIMIT OF THIS FIX, which is worth pinning because it is easy to over-claim. The
    /// predicate keeps a call inside <c>budgetSeconds</c>; it cannot keep it inside the MCP client's
    /// patience if the budget is set above that. At `budgetSeconds: 100` a call already 53 s in with
    /// a 30 s slice behind it projects to 83 s — under budget, so this says "go", and the client
    /// gives up near 60 s.
    ///
    /// So the earlier `budgetSeconds: 100` recommendation stays retracted, and the reason is now
    /// structural rather than advisory: **a budget above the client's limit is unreachable by any
    /// between-slices check.** The default 45 is what makes the predicate protective.
    /// </summary>
    [Fact]
    public void ABudgetAboveTheClientLimitCannotBeRescuedByThePredicate()
    {
        Assert.False(ClassTools.NextSliceWouldOverrun(53_000, 30_000, 100));   // 83 s projected
        Assert.True(ClassTools.NextSliceWouldOverrun(53_000, 30_000, 45));     // same slices, safe
    }

    /// <summary>
    /// A zero-cost slice must not spin forever: with no time attributed to the last slice the
    /// projection is just the elapsed total, so the budget still terminates the loop. Guards against
    /// a stopwatch that has not advanced on a very fast or cached slice.
    /// </summary>
    [Fact]
    public void AZeroCostSliceStillRespectsTheBudget()
    {
        Assert.False(ClassTools.NextSliceWouldOverrun(10_000, 0, 45));
        Assert.True(ClassTools.NextSliceWouldOverrun(45_000, 0, 45));
    }
}
