using System.Diagnostics;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The pre-push gate must test THE TREE BEING PUSHED.
///
/// THE DEFECT THESE EXIST FOR, measured 2026-09-11. <c>run-tests.ps1</c> derived the tree under
/// test from its own location (<c>Split-Path -Parent $PSScriptRoot</c>). But <c>core.hooksPath</c>
/// is git config, and config is shared between a repository and every linked worktree - so a push
/// from <c>.claude\worktrees\&lt;name&gt;</c> ran the MAIN checkout's hook script, and the gate
/// tested whatever was checked out at main. It printed <c>Failed: 0, Passed: 1625</c> while the same
/// script run by hand inside the worktree printed <c>1667</c>; the 42 missing tests were exactly the
/// two files the pushed branch added. A green gate for an unrelated tree is worse than no gate.
///
/// WHY THE TESTS BUILD A REAL WORKTREE. A fixture that merely looked like one would have passed
/// against the broken script too: the whole defect lives in how git and the filesystem disagree
/// about what <c>.git</c> is, and in which directory git hands a hook. Nothing short of
/// <c>git worktree add</c>, plus the absolute <c>core.hooksPath</c> this repo's worktrees really
/// carry, reproduces it. This is the repository's own rule that a tool tested against a plausible
/// fixture is not tested - and the reason the fixture below is a throwaway git repo rather than a
/// tree of empty directories.
///
/// The fixtures contain a STUB csproj, never built: these tests check the gate's AIM, and an aim is
/// settled by which project path it resolves, which <c>-ResolveOnly</c> prints without building.
/// Building a real suite inside a test would recurse.
/// </summary>
public sealed class PrePushGateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lvai-prepush").FullName;

    public void Dispose()
    {
        // A worktree's administrative files are read-only on Windows; clear the attribute or the
        // recursive delete throws and every later run inherits the litter.
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* best effort - %TEMP% litter is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- helpers

    private static string? PowerShell =>
        new[] { "pwsh.exe", "pwsh", "powershell.exe", "powershell" }
            .Select(FindOnPath).FirstOrDefault(p => p is not null);

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* an unusable PATH entry is not ours to fix */ }
        }
        return null;
    }

    private static (int Code, string Output) Run(string exe, string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, stdout + stderr);
    }

    private static (int Code, string Output) Git(string workingDirectory, params string[] args)
    {
        var git = FindOnPath("git.exe") ?? FindOnPath("git") ?? "git";
        return Run(git, workingDirectory, args);
    }

    /// <summary>
    /// A throwaway repo with the REAL hook script in it, plus a linked worktree carrying its own
    /// branch - and the ABSOLUTE <c>core.hooksPath</c> that this repository's worktrees actually
    /// have, which is what made the main checkout's script run for a worktree's push.
    /// </summary>
    private (string Main, string Worktree) MakeRepoWithWorktree()
    {
        var main = Path.Combine(_root, "main");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(Path.Combine(main, ".githooks"));
        Directory.CreateDirectory(Path.Combine(main, "tests", "LabVIEWMCP.Tests"));

        File.Copy(RepoTree.Path(".githooks", "run-tests.ps1"),
                  Path.Combine(main, ".githooks", "run-tests.ps1"));
        // Present in both trees, so "found the project" cannot be what distinguishes them - only
        // WHICH tree's copy was resolved.
        File.WriteAllText(Path.Combine(main, "tests", "LabVIEWMCP.Tests", "LabVIEWMCP.Tests.csproj"),
                          "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(main, "CLAUDE.md"), "fixture");

        Assert.Equal(0, Git(main, "init", "-q").Code);
        Git(main, "config", "user.email", "fixture@example.invalid");
        Git(main, "config", "user.name", "fixture");
        Git(main, "config", "commit.gpgsign", "false");
        Assert.Equal(0, Git(main, "add", "-A").Code);
        Assert.Equal(0, Git(main, "commit", "-qm", "fixture").Code);

        Assert.Equal(0, Git(main, "worktree", "add", "-q", worktree, "-b", "feature").Code);

        // The reproduction: an absolute hooks path, in the worktree's own config, pointing at the
        // MAIN checkout. Verbatim what `git config --show-origin --get core.hooksPath` reports in
        // this repository's worktrees (origin: .git/worktrees/<name>/config.worktree).
        Git(main, "config", "core.hooksPath", Path.Combine(main, ".githooks"));
        Git(worktree, "config", "core.hooksPath", Path.Combine(main, ".githooks"));
        return (main, worktree);
    }

    private static string ResolvedTestProject(string output)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.Contains("test project"))
                   ?? throw new Xunit.Sdk.XunitException(
                       "the gate printed no 'test project' line - a wrong tree is then invisible "
                       + "in the output, which is half of what the 2026-09-11 defect was.\n" + output);
        return line[(line.IndexOf(':') + 1)..].Trim();
    }

    // ---------------------------------------------------------------- the tests

    /// <summary>
    /// THE REGRESSION TEST. The main checkout's script, run with a linked worktree as the working
    /// directory - exactly how git invokes a pre-push hook there - must aim at the WORKTREE.
    /// Against the old script this fails: it resolved the main checkout and reported PASS.
    /// </summary>
    [Fact]
    public void The_gate_tests_the_worktree_being_pushed_not_the_checkout_holding_the_script()
    {
        if (PowerShell is null) return;   // no host: nothing to prove, nothing to fake
        var (main, worktree) = MakeRepoWithWorktree();

        var (code, output) = Run(PowerShell, workingDirectory: worktree,
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(main, ".githooks", "run-tests.ps1"), "-ResolveOnly");

        Assert.Equal(0, code);
        var resolved = ResolvedTestProject(output);

        Assert.StartsWith(worktree, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(main, "tests"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of the defect: the wrong tree was INVISIBLE. It was inferable only by
    /// comparing test counts between the hook's output and a manual run. The resolved paths must be
    /// in the output, so a mismatch is readable rather than detective work.
    /// </summary>
    [Fact]
    public void The_gate_prints_the_tree_it_resolved_and_how()
    {
        if (PowerShell is null) return;
        var (main, worktree) = MakeRepoWithWorktree();

        var (_, output) = Run(PowerShell, workingDirectory: worktree,
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(main, ".githooks", "run-tests.ps1"), "-ResolveOnly");

        Assert.Contains("tree under test", output);
        Assert.Contains("test project", output);
        Assert.Contains("resolved by", output);
        Assert.Contains("rev-parse --show-toplevel", output);
        // A linked worktree is the case that used to be silently wrong, so it is named.
        Assert.Contains("worktree", output);
        Assert.Contains("feature", output);
    }

    /// <summary>
    /// The ordinary case must not regress: from a normal checkout - which is what CI pushes and
    /// what <c>release.yml</c> runs the script in - the gate aims at that checkout, and says nothing
    /// about a worktree.
    /// </summary>
    [Fact]
    public void From_a_plain_checkout_the_gate_aims_at_that_checkout()
    {
        if (PowerShell is null) return;
        var (main, _) = MakeRepoWithWorktree();

        var (code, output) = Run(PowerShell, workingDirectory: main,
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(main, ".githooks", "run-tests.ps1"), "-ResolveOnly");

        Assert.Equal(0, code);
        Assert.StartsWith(main, ResolvedTestProject(output), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("linked (branch", output);
    }

    /// <summary>
    /// Git's answer is authoritative, and a tree without the test project is an ERROR - never a
    /// reason to quietly test the script's own tree instead. That silent substitution IS the
    /// original defect, so the fallback must not be reachable while git is answering.
    /// </summary>
    [Fact]
    public void A_pushed_tree_with_no_test_project_fails_rather_than_testing_another_tree()
    {
        if (PowerShell is null) return;
        var (main, worktree) = MakeRepoWithWorktree();

        // The worktree loses the project; the main checkout keeps it. The old script would have
        // found main's copy and happily tested that.
        Directory.Delete(Path.Combine(worktree, "tests"), recursive: true);

        var (code, output) = Run(PowerShell, workingDirectory: worktree,
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(main, ".githooks", "run-tests.ps1"), "-ResolveOnly");

        Assert.NotEqual(0, code);
        Assert.Contains("test project not found", output);
        Assert.Contains(worktree, output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Guards the shape of the fix, not just its behaviour: the primary resolution must come from
    /// git. Re-introducing <c>Split-Path -Parent $PSScriptRoot</c> as the main route is the exact
    /// regression, and it would pass every behavioural test above on a non-worktree machine.
    /// </summary>
    [Fact]
    public void The_gate_resolves_the_tree_from_git_and_keeps_script_location_as_a_fallback_only()
    {
        // COMMENTS STRIPPED FIRST. The script's header quotes the old, wrong line verbatim to
        // explain the defect, and an assertion over the whole file matches that prose instead of
        // what runs - which is how this test failed on its first run against a correct script.
        var code = CodeOf(RepoTree.Path(".githooks", "run-tests.ps1"));

        Assert.Contains("rev-parse --show-toplevel", code);

        // $PSScriptRoot may still appear - as the git-unavailable fallback - but only after git has
        // been consulted, never as the first answer.
        var git = code.IndexOf("rev-parse --show-toplevel", StringComparison.Ordinal);
        var psScriptRoot = code.IndexOf("Split-Path -Parent $PSScriptRoot", StringComparison.Ordinal);
        if (psScriptRoot >= 0)
            Assert.True(git < psScriptRoot,
                "the script derives the tree from its own location before asking git - that is the "
                + "2026-09-11 worktree defect back again.");
    }

    /// <summary>The script with whole-line <c>#</c> comments removed, so assertions see only code.</summary>
    private static string CodeOf(string path) =>
        string.Join(Environment.NewLine,
            File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith('#')));

    /// <summary>
    /// The SDK preflight, measured on this station the same day: <c>C:\Program Files\dotnet</c>
    /// holds a runtime with no <c>sdk\</c> directory and comes FIRST on PATH, while the SDK
    /// (8.0.424) lives per-user in <c>%USERPROFILE%\.dotnet</c>. A bare <c>dotnet test</c> then dies
    /// with "No .NET SDKs were found" and a download link, and the hook aborted a push that way -
    /// a message about installing .NET for what is purely PATH ORDER.
    /// </summary>
    [Fact]
    public void The_gate_diagnoses_a_runtime_only_dotnet_rather_than_relaying_the_sdk_dump()
    {
        var script = File.ReadAllText(RepoTree.Path(".githooks", "run-tests.ps1"));

        Assert.Contains("Find-DotnetWithSdk", script);
        // It must judge on an sdk directory beside the host - the thing actually missing - and know
        // the per-user install is where to look instead.
        Assert.Contains("'sdk'", script);
        Assert.Contains(".dotnet", script);
        Assert.Contains("No .NET SDKs were found", script);
    }
}

/// <summary>
/// <see cref="RepoTree"/> - the same "which tree am I in" question, asked by the tests that assert
/// against repository files rather than by the hook.
///
/// The walk it replaced looked for a DIRECTORY called <c>.git</c>. A linked worktree's <c>.git</c>
/// is a FILE, so that probe walked past it and - because worktrees live at
/// <c>&lt;repo&gt;\.claude\worktrees\&lt;name&gt;</c> - landed on the MAIN checkout. MEASURED
/// 2026-09-11 from a worktree's own build output: it resolved <c>C:\Users\...\labview-mcp</c> and
/// would have linted main's <c>scripts\</c> while the branch's went unchecked. Latent that day
/// (the two agreed), and green either way - the same shape as the pre-push gate defect above.
/// </summary>
public sealed class RepoTreeTests
{
    /// <summary>
    /// The decisive assertion, and the one the old walk fails. These tests were built from
    /// <c>&lt;root&gt;\tests\LabVIEWMCP.Tests\bin\...</c>, so the resolved root must be the tree
    /// whose <c>tests\</c> directory contains this assembly. The main checkout is an ANCESTOR of a
    /// worktree's build output, so an ancestor check would pass for the wrong answer - containment
    /// under <c>&lt;root&gt;\tests</c> is what separates them.
    /// </summary>
    [Fact]
    public void The_resolved_root_is_the_tree_this_assembly_was_built_from()
    {
        var testsDir = Path.Combine(RepoTree.Root, "tests") + Path.DirectorySeparatorChar;
        Assert.StartsWith(testsDir, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_resolved_root_looks_like_this_repository()
    {
        Assert.True(File.Exists(Path.Combine(RepoTree.Root, "CLAUDE.md")));
        Assert.True(Directory.Exists(Path.Combine(RepoTree.Root, "scripts")));
        Assert.True(Directory.Exists(Path.Combine(RepoTree.Root, ".githooks")));
    }

    /// <summary>
    /// It must not depend on <c>.git</c> at all - neither as a file nor as a directory. That is the
    /// one signal whose TYPE changes with how the tree was created, which is what made the old walk
    /// worktree-blind.
    /// </summary>
    [Fact]
    public void Resolution_does_not_depend_on_a_git_directory()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Support", "RepoTree.cs"));
        var walk = source[source.IndexOf("private static string FindRoot", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Directory.Exists(System.IO.Path.Combine(directory, \".git\")", walk);
    }

    [Fact]
    public void Path_composes_under_the_resolved_root() =>
        Assert.Equal(Path.Combine(RepoTree.Root, "scripts", "templates"),
                     RepoTree.Path("scripts", "templates"));
}

/// <summary>
/// The third instance of the same worktree-blindness, found by the noise it made rather than by a
/// failure: <c>Directory.Build.targets</c>' hook-activation guard tested <c>Exists('.git')</c>, and
/// MSBuild's <c>Exists</c> is true for a FILE - which is what a linked worktree's <c>.git</c> is.
/// So the target fired on every build in a worktree, could not create its marker inside a path
/// whose <c>.git</c> is not a directory, and emitted three MSB3371 warnings per build for ever.
/// MEASURED 2026-09-11 on a worktree build, before the condition moved to <c>.git\config</c>.
/// </summary>
public sealed class GitHookActivationTargetTests
{
    private static string Targets => File.ReadAllText(RepoTree.Path("Directory.Build.targets"));

    /// <summary>
    /// The file with every <c>&lt;!-- --&gt;</c> block removed. The rationale comment quotes the old,
    /// wrong condition verbatim, so asserting over the raw text matches the explanation instead of
    /// the live Target - which is exactly how this test failed on its first run against a correct
    /// file.
    /// </summary>
    private static string Markup =>
        System.Text.RegularExpressions.Regex.Replace(
            Targets, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>
    /// The guard must key on something that exists ONLY where <c>.git</c> is a real directory.
    /// <c>Exists('....git')</c> alone is the defect, because it is satisfied by a worktree's
    /// <c>.git</c> file and then nothing downstream can work.
    /// </summary>
    [Fact]
    public void The_guard_distinguishes_a_checkout_from_a_linked_worktree()
    {
        Assert.Contains(@"Exists('$(MSBuildThisFileDirectory).git\config')", Markup);
        Assert.DoesNotContain(@"Exists('$(MSBuildThisFileDirectory).git')", Markup);
    }

    /// <summary>
    /// And the discriminator has to be true of this machine, not just of the docs: a linked
    /// worktree has no <c>.git\config</c>, a normal checkout does. Asserted from whichever kind of
    /// tree the tests are running in, so both shapes are covered across a worktree and CI.
    /// </summary>
    [Fact]
    public void A_worktree_has_no_git_config_beside_it_while_a_checkout_does()
    {
        var dotGit = Path.Combine(RepoTree.Root, ".git");
        if (File.Exists(dotGit))            // a linked worktree: .git is a gitdir: pointer file
        {
            Assert.False(File.Exists(Path.Combine(dotGit, "config")));
            Assert.StartsWith("gitdir:", File.ReadAllText(dotGit).TrimStart());
        }
        else if (Directory.Exists(dotGit))  // a normal checkout (CI, a plain clone)
        {
            Assert.True(File.Exists(Path.Combine(dotGit, "config")));
        }
        // Neither: an exported tree with no git metadata. Nothing to assert.
    }

    /// <summary>
    /// The value written must stay RELATIVE. An absolute one would be inherited by every linked
    /// worktree and point them all at the main checkout's hooks - which is how the pre-push gate
    /// came to run the wrong tree's script in the first place. (The absolute value observed on this
    /// station comes from the worktree tooling's own config.worktree, not from here; this keeps it
    /// that way.)
    /// </summary>
    [Fact]
    public void The_configured_hooks_path_is_relative()
    {
        Assert.Contains("git config core.hooksPath .githooks", Markup);
        Assert.DoesNotContain("git config core.hooksPath \"$(MSBuildThisFileDirectory)", Markup);
    }
}
