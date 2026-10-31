using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Which connector-pane rule lvai_generate_vi applies - the user's rule of 2026-10-31: a VI
/// generated COMPLETELY NEW gets a required input, a VI that already exists keeps every flag, and
/// what a tool authors for itself (helpers, carriers, class methods on their way into a class,
/// all generated without measuring the diagram) is the author's.
/// </summary>
public sealed class PanePolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pane-policy-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_deliverable_whose_file_does_not_exist_yet_is_a_new_VI() =>
        Assert.Equal(AixmlCheck.PanePolicy.NewVi,
            BulkTools.PanePolicyFor(Path.Combine(_dir, "Scale.vi"), deliverable: true));

    [Fact]
    public void A_VI_that_already_exists_keeps_its_flags_even_as_a_deliverable()
    {
        var vi = Path.Combine(_dir, "Existing.vi");
        File.WriteAllText(vi, "an existing VI");
        Assert.Equal(AixmlCheck.PanePolicy.KeepAsIs, BulkTools.PanePolicyFor(vi, deliverable: true));
        Assert.Equal(AixmlCheck.PanePolicy.KeepAsIs, BulkTools.PanePolicyFor(vi, deliverable: false));
    }

    [Fact]
    public void A_new_helper_or_carrier_is_authored_not_ruled() =>
        Assert.Equal(AixmlCheck.PanePolicy.Authored,
            BulkTools.PanePolicyFor(Path.Combine(_dir, "Carrier.vi"), deliverable: false));
}
