using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <see cref="RunTools.CanHonourRunForMs"/> — which helper `lvai_run_vi_and_read_values` may use
/// when `runForMs` is asked for.
///
/// WHAT THIS PREVENTS, MEASURED 2026-09-16. The tool ships two runners. `lvai_run_and_read.vi`
/// wires `Wait until done` = TRUE and waits for the target to finish; `lvai_run_for_ms.vi` starts
/// it, waits a budget, reads the front panel and ABORTS. The selection was
/// `helperAixmlPath ?? DefaultHelperAixmlPath(timed)`, so a caller-supplied path won over the
/// timed default — and pointing the call at the untimed helper while asking for `runForMs` makes
/// the helper wait for a VI that never ends. Because the gRPC service is what runs the helper,
/// that does not time out one call: every later `lvai_*` call answers `DeadlineExceeded` until
/// LabVIEW is killed and restarted. It cost two restarts on a top-level ATM UI loop, which is
/// exactly the shape `runForMs` exists for, so the trap sat where the feature is useful.
///
/// WHY IT IS NOT LEFT TO THE CALLER. A client that treats every declared parameter as required
/// sends `helperAixmlPath` on every call. Through such a client the default never applies, so
/// `runForMs` was unreachable — the parameter was declared, documented, and could not work.
///
/// WHY CONTENT AND NOT THE FILE NAME. The fact that decides it is whether the helper has a
/// `run for ms` control at all. A renamed copy of the untimed helper has the identical defect and
/// a name match would have let it through.
/// </summary>
public sealed class RunForMsHelperTests
{
    /// <summary>No path means the default applies, and the timed default is already correct.</summary>
    [Fact]
    public void NoHelperPathIsCapable()
    {
        Assert.True(RunTools.CanHonourRunForMs(null));
        Assert.True(RunTools.CanHonourRunForMs(""));
    }

    /// <summary>
    /// The shipped timed runner is accepted. This reads the real file rather than a fixture: the
    /// check is a claim about what `scripts\lvai_run_for_ms.xml` contains, and a fixture asserting
    /// the string it was written from would pass while the shipped helper drifted away from it.
    /// </summary>
    [Fact]
    public void ShippedTimedHelperIsCapable()
    {
        var path = Path.Combine(RepoTree.Root, "scripts", RunTools.TimedHelperAixmlFileName);
        Assert.True(File.Exists(path), $"no timed helper at {path}");
        Assert.True(RunTools.CanHonourRunForMs(path));
    }

    /// <summary>
    /// The control arm, and the half that matters: the shipped UNTIMED runner must be rejected.
    /// A guard that accepted everything would pass the test above and still wedge the service.
    /// </summary>
    [Fact]
    public void ShippedUntimedHelperIsNotCapable()
    {
        var path = Path.Combine(RepoTree.Root, "scripts", RunTools.HelperAixmlFileName);
        Assert.True(File.Exists(path), $"no untimed helper at {path}");
        Assert.False(RunTools.CanHonourRunForMs(path));
    }

    /// <summary>A renamed copy of the untimed helper is rejected too — the name never decides.</summary>
    [Fact]
    public void RenamedUntimedHelperIsNotCapable()
    {
        var source = Path.Combine(RepoTree.Root, "scripts", RunTools.HelperAixmlFileName);
        var copy = Path.Combine(Path.GetTempPath(),
            $"lvai_my_own_timed_runner_{Guid.NewGuid():N}.xml");
        File.Copy(source, copy);
        try { Assert.False(RunTools.CanHonourRunForMs(copy)); }
        finally { File.Delete(copy); }
    }

    /// <summary>
    /// A path this cannot read is treated as capable. Refusing a helper on a failed read would
    /// turn a permissions problem into a silently substituted helper, which is worse than the
    /// status quo it replaces.
    /// </summary>
    [Fact]
    public void UnreadablePathIsTreatedAsCapable() =>
        Assert.True(RunTools.CanHonourRunForMs(
            Path.Combine(Path.GetTempPath(), $"no such helper {Guid.NewGuid():N}.xml")));
}
