// =============================================================================
// LibraryRemoveCallersTests.cs - lvai_remove_from_library refuses a VI that other
// files of the project call, from the files, before LabVIEW is asked.
// =============================================================================
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Measured 2026-11-27 on LabVIEW 2026: disconnecting a library member that another member calls
/// left the caller modified in memory, and the helper's close raised the modal "Save changes before
/// closing?" - the call waited 601 s behind it and removed nothing. The fixture is a real VI
/// (`Launcher.vi`) whose link records name `Launch Root Actor.vi`.
/// </summary>
public sealed class LibraryRemoveCallersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lvmcp-remove-callers-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly string _caller;
    private readonly string _callee;

    public LibraryRemoveCallersTests()
    {
        Directory.CreateDirectory(_dir);
        _project = Path.Combine(_dir, "P.lvproj");
        File.WriteAllText(_project, "<?xml version='1.0' encoding='UTF-8'?>\r\n<Project Type=\"Project\" LVVersion=\"26008000\"/>\r\n");
        _caller = Path.Combine(_dir, "Launcher.vi");
        File.Copy(OwnerHousekeepingTests.Fixture("Launcher.vi"), _caller);
        // the callee is only ever NAMED here; a second copy of the fixture stands in for its file
        _callee = Path.Combine(_dir, "Launch Root Actor.vi");
        File.Copy(OwnerHousekeepingTests.Fixture("Launcher.vi"), _callee);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_vi_another_file_of_the_project_calls_is_named_with_its_caller()
    {
        var blocking = LibraryTools.CallersBlockingRemoval([_callee], _project);

        var callers = Assert.Single(blocking).Value!.AsArray();
        Assert.Equal(_caller, Assert.Single(callers)!.GetValue<string>());
    }

    [Fact]
    public void A_caller_that_leaves_in_the_same_call_does_not_block()
    {
        Assert.Empty(LibraryTools.CallersBlockingRemoval([_callee, _caller], _project));
    }

    [Fact]
    public void A_vi_nothing_calls_is_not_blocked()
    {
        Assert.Empty(LibraryTools.CallersBlockingRemoval([_caller], _project));
    }
}
