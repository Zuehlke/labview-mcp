// ============================================================================
// PrimitiveTerminalNameCheckTests - lvai_check_aixml names a slipped primitive terminal
// (`.not. x` for Not's `.not. x?`) against the composer's own name tables.
// ============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The field report of 2026-11-14: `.not. x` on Not passed lvai_check_aixml and aixml_lint.py and
/// the composer refused it, one round trip. The tables are the real ones the composer reads, so the
/// tests SKIP where the pylabview bundle is not provisioned.
/// </summary>
public sealed class PrimitiveTerminalNameCheckTests
{
    private const string Slipped = """
        <VI _name="t.vi" description="d">
          <Control _name="b" type="bool" uid="4201" uid_parent="root" value="false" outputs="value:4201.value"/>
          <Node _name="Not" uid="4202" uid_parent="root" inputs="x:4201.value" outputs=".not. x:4202.o"/>
          <Indicator _name="o" type="bool" uid="4203" uid_parent="root" value="false" inputs="value:4202.o"/>
        </VI>
        """;

    [Fact]
    public void A_slipped_terminal_name_is_named_with_the_real_one()
    {
        if (PyLabview.Locate() is null) return;
        // with the bundle there the tables MUST load - a loader that finds nothing skips in silence
        Assert.NotNull(PrimitiveTerminalNames.Of("Not"));

        var finding = Assert.Single(AixmlCheck.Check(Slipped), f => f.Code == "primitiveTerminalNearMiss");

        Assert.Equal(AixmlCheck.Severity.Warning, finding.Severity);
        Assert.Equal("4202", finding.Uid);
        Assert.Contains("did you mean \".not. x?\"", finding.Message);
    }

    /// <summary>The right name, an unwired listed slip and a name the tables lack pass.</summary>
    [Fact]
    public void The_right_name_an_unwired_one_and_an_unknown_one_pass()
    {
        if (PrimitiveTerminalNames.Of("Not") is null) return;

        var right = Slipped.Replace(".not. x:4202.o", ".not. x?:4202.o", StringComparison.Ordinal);
        var unwired = Slipped.Replace("outputs=\".not. x:4202.o\"", "outputs=\".not. x?:4202.o,.not. x:\"", StringComparison.Ordinal);
        var unknown = Slipped.Replace(".not. x:4202.o", "negated:4202.o", StringComparison.Ordinal);

        foreach (var xml in new[] { right, unwired, unknown })
            Assert.DoesNotContain(AixmlCheck.Check(xml), f => f.Code == "primitiveTerminalNearMiss");
    }

    /// <summary>The lint carries the same rule, or the two drift (CLAUDE.md).</summary>
    [Fact]
    public void The_lint_carries_the_same_rule()
    {
        var lint = File.ReadAllText(Path.Combine(RepoTree.Root, "scripts", "aixml_lint.py"));
        Assert.Contains("primitive-terminal-near-miss", lint);
        Assert.Contains("terminal-names-learned.tsv", lint);
    }
}
