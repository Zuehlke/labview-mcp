using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The Execution-page table. The fixture is the LVSR record of a real generated VI (Scale By Two.vi,
/// pylv_extract, 2026-11-11), cut to the elements the table touches plus a DECOY outside LVSR -
/// `Priority` and `Unknown` occur elsewhere in a bundle, and a file-wide replace would hit them.
/// </summary>
public sealed class ViExecutionSettingsTests
{
    private const string Bundle = """
        <?xml version="1.0" encoding="utf-8"?>
        <RSRC>
          <LVSR>
            <Section Index="0" Name="Scale By Two.vi" Format="inline">
              <Execution State="1" Priority="1" PrefExecSyst="-1" IsReentrant="0" PooledReentrancy="0" HasSetBP="0" RunOnOpen="0" IsSubroutine="0" SaveParallel="0" />
              <Execution2 InlinableDiagram="1" SourceOnly="1" InlineIfPossible="0" ShouldInline="0" ClearIndMask="0" DefaultErrorHandling="0" />
              <Instrument Type="Standard" DebugCapable="1" InStBit13="0" />
              <Flags0C AutoHndlMenus="1" />
              <Unknown Flags10="3" InlineStg="1" Field8C="4294967295" />
            </Section>
          </LVSR>
          <OTHR><Section><Unknown InlineStg="7" Priority="9" /></Section></OTHR>
        </RSRC>
        """;

    private static string Apply(ViExecutionSettings.Request r)
    {
        var plan = ViExecutionSettings.Resolve(r);
        Assert.Empty(plan.Problems);
        var (text, _, notFound) = ViExecutionSettings.PatchText(Bundle, plan.Fields);
        Assert.Empty(notFound);
        return text;
    }

    [Fact]
    public void The_unpatched_record_reads_as_LabVIEWs_defaults()
    {
        var s = ViExecutionSettings.Read(Bundle);
        Assert.Equal("none", (string?)s["reentrancy"]);
        Assert.Equal("normal", (string?)s["priority"]);
        Assert.Equal("same as caller", (string?)s["executionSystem"]);
        Assert.True((bool?)s["allowDebugging"]);
        Assert.False((bool?)s["inline"]);
        Assert.True((bool?)s["autoHandleMenus"]);
    }

    [Fact]
    public void Inline_implies_what_LabVIEW_requires_and_touches_nothing_outside_LVSR()
    {
        var plan = ViExecutionSettings.Resolve(new(Inline: true));
        Assert.Equal(2, plan.Implied.Count);
        var text = Apply(new(Inline: true));
        var s = ViExecutionSettings.Read(text);
        Assert.True((bool?)s["inline"]);
        Assert.False((bool?)s["allowDebugging"]);
        Assert.Equal("preallocated", (string?)s["reentrancy"]);
        Assert.Contains("""<Unknown InlineStg="7" Priority="9" />""", text);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(null, "shared")]
    [InlineData(null, "none")]
    public void Inline_with_a_contradiction_is_refused(bool? debug, string? reentrancy)
    {
        var plan = ViExecutionSettings.Resolve(new(Inline: true, AllowDebugging: debug, Reentrancy: reentrancy));
        Assert.NotEmpty(plan.Problems);
        Assert.Empty(plan.Fields);
    }

    [Fact]
    public void Shared_reentrancy_is_both_bits()
    {
        var s = ViExecutionSettings.Read(Apply(new(Reentrancy: "shared")));
        Assert.Equal("shared", (string?)s["reentrancy"]);
    }

    [Theory]
    [InlineData("background", 0)]
    [InlineData("normal", 1)]
    [InlineData("Time Critical", 4)]
    [InlineData("subroutine", 5)]
    public void Priority_names_map_to_the_measured_file_values(string name, int file)
    {
        Assert.Equal(file, ViExecutionSettings.PriorityFileValue(name));
        Assert.Equal(ViExecutionSettings.PriorityNames[file], ViExecutionSettings.PriorityName(file + 1));
    }

    [Fact]
    public void Subroutine_sets_the_subroutine_bit_too()
    {
        var text = Apply(new(Priority: "subroutine"));
        Assert.Contains("""Priority="5" """, text);
        Assert.Contains("""IsSubroutine="1" """, text);
    }

    [Theory]
    [InlineData("same as caller", -1, 7)]
    [InlineData("user interface", 0, 1)]
    [InlineData("instrument I/O", 2, 3)]
    [InlineData("instrument io", 2, 3)]
    [InlineData("other 2", 5, 6)]
    public void Execution_systems_map_to_file_and_COM_values(string name, int file, int com)
    {
        Assert.Equal(file, ViExecutionSettings.ExecutionSystemFileValue(name));
        Assert.Equal(ViExecutionSettings.ExecutionSystemName(com),
                     file < 0 ? "same as caller" : ViExecutionSettings.ExecutionSystemNames[file]);
    }

    [Fact]
    public void The_four_settings_COM_cannot_reach_are_written_to_their_measured_fields()
    {
        var text = Apply(new(AutomaticErrorHandling: true, ClearIndicators: true, AutoHandleMenus: false,
                             SuspendWhenCalled: true, RunWhenOpened: true));
        var s = ViExecutionSettings.Read(text);
        Assert.True((bool?)s["automaticErrorHandling"]);
        Assert.True((bool?)s["clearIndicators"]);
        Assert.False((bool?)s["autoHandleMenus"]);
        Assert.True((bool?)s["suspendWhenCalled"]);
        Assert.True((bool?)s["runWhenOpened"]);
        Assert.Contains("""HasSetBP="1" """, text);
        Assert.Contains("""InStBit13="0" """, text);
    }

    [Fact]
    public void Unknown_names_are_refused_by_name()
    {
        var plan = ViExecutionSettings.Resolve(new(Priority: "urgent", ExecutionSystem: "gpu", Reentrancy: "pooled"));
        Assert.Equal(3, plan.Problems.Count);
    }

    [Fact]
    public void A_record_without_the_field_is_reported_not_half_patched()
    {
        var plan = ViExecutionSettings.Resolve(new(ClearIndicators: true));
        var (_, set, notFound) = ViExecutionSettings.PatchText(Bundle.Replace(" ClearIndMask=\"0\"", ""), plan.Fields);
        Assert.Empty(set);
        Assert.Equal(["Execution2.ClearIndMask"], notFound);
    }
}
