// =============================================================================
// RunningViPropertiesTests.cs - lvai_running_vi's `Control@Property` read: the composed
// lvai_control_props helper's lines, parsed, and the helper itself composing offline.
// =============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// CLD benchmark builds (2026-11-17/18) could not verify an ATM's enabled and disabled buttons or a
/// Boiler's blinking Pilot button: ActiveX reads a control's value, never its properties. The
/// helper lists Disabled, Blinking, Visible and Key Focus of every top-level control; the live
/// read is checked against LabVIEW separately (the helper runs only there).
/// </summary>
public sealed class RunningViPropertiesTests
{
    [Fact]
    public void Parses_the_helpers_lines_by_label()
    {
        var table = LabViewCom.ParseProperties("Deposit\t2\t0\t1\t0\r\nPilot\t0\t1\t1\t0\nEnter\t0\t0\t1\t1\n");

        Assert.Equal("Disabled and Grayed Out", table["Deposit"]["Disabled"]);
        Assert.Equal("TRUE", table["Pilot"]["Blinking"]);
        Assert.Equal("FALSE", table["Pilot"]["Key Focus"]);
        Assert.Equal("TRUE", table["Enter"]["key focus"]);   // property names fold case
        Assert.Equal("Enabled", table["Enter"]["Disabled"]);
    }

    [Fact]
    public void Skips_a_line_that_is_not_five_cells_and_keeps_the_first_of_a_label()
    {
        var table = LabViewCom.ParseProperties("\nbroken\t1\nA\t1\t0\t1\t0\nA\t0\t0\t1\t0\n\t0\t0\t1\t0\n");

        Assert.Equal(["A"], table.Keys);
        Assert.Equal("Disabled", table["A"]["Disabled"]);
    }

    [Fact]
    public async Task The_helper_composes()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return;
        var dir = Directory.CreateTempSubdirectory("lvai-props-helper").FullName;
        try
        {
            var result = await AixmlComposer.ComposeAsync(setup, RepoTree.Path(Path.Combine("scripts", "lvai_control_props.xml")),
                Path.Combine(dir, "lvai_control_props.vi"), [], 300, default);
            Assert.True(result.Written, result.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
