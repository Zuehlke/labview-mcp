namespace LabVIEWMcp.Tests.Support;

/// <summary>
/// Locates the working tree the running tests were BUILT FROM, for the handful of tests that
/// assert against repository files (the shipped <c>scripts\</c> helpers, the agent definitions).
///
/// WHY THIS IS NOT A ONE-LINER. The obvious walk - climb from
/// <see cref="AppContext.BaseDirectory"/> until a directory has a <c>.git</c> - is WRONG in a
/// linked worktree, and wrong in the direction that produces a green test about somebody else's
/// code. A linked worktree's <c>.git</c> is a FILE holding a <c>gitdir:</c> pointer, not a
/// directory, so a <c>Directory.Exists</c> probe walks straight past it. This repository keeps its
/// worktrees at <c>&lt;repo&gt;\.claude\worktrees\&lt;name&gt;</c>, so the next <c>.git</c> the walk
/// meets is a real directory: the MAIN checkout's.
///
/// MEASURED 2026-09-11, replicating the old walk from a worktree's test output directory: it landed
/// on <c>C:\Users\...\labview-mcp</c> and would have linted main's <c>scripts\</c> while the branch
/// under test went unchecked. The two trees' helpers happened to agree that day, so nothing failed
/// and nothing reported it - exactly the shape of the pre-push gate defect fixed in the same pass
/// (see <c>.githooks\run-tests.ps1</c>, "the worktree trap"), where the hook tested main and printed
/// PASS for a branch it had never built.
///
/// So match on the repository's own CONTENT instead of on <c>.git</c>. <c>CLAUDE.md</c> beside a
/// <c>scripts\</c> directory identifies this repo and is present in a worktree, a normal clone and
/// a CI checkout alike - and, unlike <c>.git</c>, is not a different kind of filesystem object
/// depending on how the tree was created.
/// </summary>
internal static class RepoTree
{
    /// <summary>The root of the working tree these tests were built from.</summary>
    internal static string Root { get; } = FindRoot();

    /// <summary>A path inside that tree, e.g. <c>RepoTree.Path("scripts")</c>.</summary>
    internal static string Path(params string[] parts) =>
        System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static string FindRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(System.IO.Path.Combine(directory, "scripts"))
                && File.Exists(System.IO.Path.Combine(directory, "CLAUDE.md")))
                return directory;

            var parent = System.IO.Path.GetDirectoryName(directory);
            if (parent == directory) break;   // reached a filesystem root
            directory = parent;
        }

        // No marker anywhere above us. Returning the build output is the least surprising
        // answer: the caller's own Test-Path/File.Exists then fails, naming a path, instead of
        // this silently substituting a different tree.
        return AppContext.BaseDirectory;
    }
}
