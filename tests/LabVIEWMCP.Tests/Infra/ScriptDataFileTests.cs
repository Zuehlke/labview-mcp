using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A helper script is only shipped if the data it OPENS is shipped with it.
///
/// WHY THIS EXISTS. `scripts/pylv-conpane.py` resolves its two tables as `<script dir>\..\docs\…`
/// and opens them on every run. `scripts/` and `docs/` are staged into a release by two separate
/// steps, and until v0.9.0 only the first one existed - so a plugin install carried a script whose
/// data files were not there, and the script died on its first call. That is the same
/// embedded-is-not-shipped mistake `CLAUDE.md` already records for documents, one layer further out:
/// a file can be in the repository, in the local build output, and still absent from the artefact
/// people install.
///
/// The release workflow asserts the staged LAYOUT. This asserts the SOURCE relationship, so a table
/// that gets renamed or moved fails here rather than at release time.
/// </summary>
public class ScriptDataFileTests
{
    /// <summary>Scripts that read a data file at run time, and what they read.</summary>
    public static TheoryData<string, string> RunTimeTables => new()
    {
        { "scripts/pylv-conpane.py", "docs/connector-pane-patterns.tsv" },
        { "scripts/pylv-conpane.py", "docs/connector-pane-typecodes.tsv" },
    };

    [Theory]
    [MemberData(nameof(RunTimeTables))]
    public void TheTableAScriptOpensIsInTheRepository(string script, string table)
    {
        Assert.NotNull(Res.FindRepoFile(script));
        Assert.True(Res.FindRepoFile(table) is not null,
            $"{script} opens {table} at run time, and it is not in the repository");
    }

    /// <summary>
    /// The script must reach its tables RELATIVE TO ITSELF, never from the working directory. A
    /// path built from `os.getcwd()` works in every test and fails on an install, where the server
    /// launches the script from wherever the host happens to be.
    /// </summary>
    [Fact]
    public void TheConnectorPaneScriptResolvesItsTablesRelativeToItself()
    {
        var script = Res.FindRepoFile("scripts/pylv-conpane.py");
        Assert.NotNull(script);
        var text = File.ReadAllText(script!);

        Assert.Contains("os.path.dirname(os.path.abspath(__file__))", text);

        foreach (var table in new[] { "connector-pane-patterns.tsv", "connector-pane-typecodes.tsv" })
        {
            var declaration = Regex.Match(
                text, @"os\.path\.join\(HERE,\s*""\.\.""\s*,\s*""docs""\s*,\s*""" + Regex.Escape(table) + @"""\)");
            Assert.True(declaration.Success,
                $"{table} must be resolved as HERE/../docs/{table} - that is the one shape which "
                + "holds in the repository AND in the plugin staging tree, where the script sits at "
                + "bin/scripts/ and the table at bin/docs/");
        }
    }

    /// <summary>
    /// And the release workflow has to stage `docs/` at all. Asserting the workflow text is blunt,
    /// but the alternative is noticing on the next install.
    /// </summary>
    [Fact]
    public void TheReleaseWorkflowStagesDocsBesideTheScripts()
    {
        var workflow = Res.FindRepoFile(".github/workflows/release.yml");
        Assert.NotNull(workflow);
        var text = File.ReadAllText(workflow!);

        Assert.Contains("bin/docs", text);
        Assert.Contains("'docs/*'", text);
    }
}
