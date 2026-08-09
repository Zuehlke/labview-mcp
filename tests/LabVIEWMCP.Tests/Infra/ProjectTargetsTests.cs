using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Reading a project's target out of the `.lvproj`. The XML shapes below are the ones actually
/// found across the 189 example projects of LabVIEW 2026 - one desktop-only project, one with an
/// RT target and no desktop target at all, and one carrying both.
/// </summary>
public class ProjectTargetsParsingTests
{
    private const string Desktop = """
        <?xml version='1.0' encoding='UTF-8'?>
        <Project Type="Project" LVVersion="26008000">
          <Item Name="My Computer" Type="My Computer">
            <Item Name="Main.vi" Type="VI" URL="../Main.vi"/>
            <Item Name="Dependencies" Type="Dependencies"/>
            <Item Name="Build Specifications" Type="Build"/>
          </Item>
        </Project>
        """;

    private const string RealTimeOnly = """
        <?xml version='1.0' encoding='UTF-8'?>
        <Project Type="Project" LVVersion="26008000">
          <Item Name="RT Target" Type="RT Generic">
            <Item Name="RT Analysis Workspace.vi" Type="VI" URL="../RT Analysis Workspace.vi"/>
          </Item>
        </Project>
        """;

    private const string Both = """
        <?xml version='1.0' encoding='UTF-8'?>
        <Project Type="Project" LVVersion="26008000">
          <Item Name="My Computer" Type="My Computer">
            <Item Name="Host.vi" Type="VI" URL="../Host.vi"/>
          </Item>
          <Item Name="RT CompactRIO Target" Type="RT Generic">
            <Item Name="Scan.vi" Type="VI" URL="../Scan.vi"/>
          </Item>
        </Project>
        """;

    [Fact]
    public void A_desktop_only_project_has_no_special_target() =>
        Assert.Null(ProjectTargets.NonDesktopTarget(Desktop));

    [Fact]
    public void An_RT_only_project_reports_its_target() =>
        Assert.Equal("RT Generic", ProjectTargets.NonDesktopTarget(RealTimeOnly));

    [Fact]
    public void A_project_with_both_still_reports_the_non_desktop_one() =>
        Assert.Equal("RT Generic", ProjectTargets.NonDesktopTarget(Both));

    [Fact]
    public void An_FPGA_target_is_reported_the_same_way() =>
        Assert.Equal("FPGA Target", ProjectTargets.NonDesktopTarget(
            """<Project Type="Project"><Item Name="FPGA" Type="FPGA Target"/></Project>"""));

    [Fact]
    public void Build_specifications_are_not_targets() =>
        Assert.Null(ProjectTargets.NonDesktopTarget(
            """
            <Project Type="Project">
              <Item Name="My Computer" Type="My Computer"/>
              <Item Name="Build Specifications" Type="Build"/>
              <Item Name="Dependencies" Type="Dependencies"/>
            </Project>
            """));

    [Fact]
    public void An_unreadable_project_is_not_evidence_of_a_special_target() =>
        Assert.Null(ProjectTargets.NonDesktopTarget("this is not xml <<<"));

    [Fact]
    public void An_empty_project_has_no_target() =>
        Assert.Null(ProjectTargets.NonDesktopTarget("""<Project Type="Project"/>"""));
}

public class ProjectTargetsLookupTests
{
    private static readonly Dictionary<string, string> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\LV\examples\Scan Engine"] = "RT Generic",
        [@"C:\LV\examples\Mathematics\RT Utilities"] = "RT Generic",
    };

    [Fact]
    public void A_VI_in_the_project_folder_inherits_its_target() =>
        Assert.Equal("RT Generic",
            ProjectTargets.For(@"C:\LV\examples\Scan Engine\Programmatic Forcing.vi", Targets));

    [Fact]
    public void A_VI_in_a_subfolder_inherits_it_too() =>
        Assert.Equal("RT Generic", ProjectTargets.For(
            @"C:\LV\examples\Mathematics\RT Utilities\subVIs\Helper.vi", Targets));

    [Fact]
    public void A_VI_elsewhere_does_not() =>
        Assert.Null(ProjectTargets.For(@"C:\LV\examples\Arrays\Build Array.vi", Targets));

    [Fact]
    public void A_sibling_folder_does_not_inherit_by_name_prefix() =>
        Assert.Null(ProjectTargets.For(@"C:\LV\examples\Scan Engine Extras\Other.vi", Targets));

    [Fact]
    public void An_empty_map_decides_nothing() =>
        Assert.Null(ProjectTargets.For(@"C:\LV\examples\Scan Engine\X.vi", new Dictionary<string, string>()));

    [Fact]
    public void Scanning_a_missing_directory_yields_nothing() =>
        Assert.Empty(ProjectTargets.Scan(@"C:\no\such\place\at\all"));

    [Fact]
    public void Scanning_null_yields_nothing() => Assert.Empty(ProjectTargets.Scan(null));
}
