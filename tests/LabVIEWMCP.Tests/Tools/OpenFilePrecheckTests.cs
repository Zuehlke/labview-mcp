using System.Text.Json;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <see cref="ActionTools.OpenFilePrecheck"/> - everything <c>lvai_open_file</c> can refuse before
/// LabVIEW is involved.
///
/// WHY THESE EXIST AT ALL. Every one of these mistakes produces the SAME answer from LabVIEW:
/// <c>Error 7, File not found</c>, naming nothing. That sends the reader to the filesystem, which
/// is the one place the fault is not, and it has now cost two sessions:
///
/// - 2026-08-27, a <c>.lvproj</c> passed in <c>viPath</c>: three identical Error 7 answers while
///   <c>lvai_describe_project</c> read the same path with <c>errorCode 0</c>.
/// - 2026-09-14, an UNDECLARED argument name, so every field arrived empty: five Error 7 answers
///   across three real paths, an A/B on the foreground window that refuted a hypothesis nobody
///   needed, and a LabVIEW kill and restart. LabVIEW was never at fault.
///
/// The first case was guarded; the third door had no guard and no test, which is the same gap seen
/// twice. The guards were inside the async body and therefore unreachable from a test - extracting
/// them is what makes this file possible.
/// </summary>
public sealed class OpenFilePrecheckTests
{
    private static JsonElement Refusal(string? json)
    {
        Assert.NotNull(json);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("badArguments", root.GetProperty("errorKind").GetString());
        return root;
    }

    /// <summary>
    /// THE 2026-09-14 CASE. Nothing named, so nothing to open - and this is what an unrecognised
    /// argument name decays into once the binder has dropped it.
    /// </summary>
    [Fact]
    public void Neither_path_given_is_refused()
    {
        var error = Refusal(ActionTools.OpenFilePrecheck(null, null, null, null))
            .GetProperty("error").GetString();

        Assert.Contains("nothing to open", error);
        Assert.Contains("Error 7", error);
    }

    /// <summary>
    /// An EMPTY STRING is the same call. It is worth its own case because it is the one spelling
    /// that never looks like a misspelling, so the argument layer upstream cannot catch it.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("", null)]
    [InlineData(null, "")]
    public void Empty_paths_are_refused_too(string? viPath, string? projectPath) =>
        Assert.Contains("nothing to open",
            Refusal(ActionTools.OpenFilePrecheck(viPath, null, projectPath, null))
                .GetProperty("error").GetString());

    /// <summary>
    /// The refusal has to be actionable in ONE turn, so it repeats what arrived - all four fields,
    /// including the names the caller may have filled in while leaving the paths empty.
    /// </summary>
    [Fact]
    public void The_refusal_reports_what_arrived_and_names_the_right_parameters()
    {
        var detail = Refusal(ActionTools.OpenFilePrecheck(null, "Thing.vi", null, "Proj.lvproj"))
            .GetProperty("detail");

        Assert.Equal("Thing.vi", detail.GetProperty("received").GetProperty("viName").GetString());
        Assert.Equal("Proj.lvproj",
            detail.GetProperty("received").GetProperty("projectName").GetString());

        var hint = detail.GetProperty("hint").GetString();
        Assert.Contains("viPath", hint);
        Assert.Contains("projectPath", hint);
        // The two names that do not exist, said plainly - this is the mistake that started it.
        Assert.Contains("`path`", hint);
        Assert.Contains("`filePath`", hint);
    }

    /// <summary>THE 2026-08-27 CASE: a project file handed in as a VI.</summary>
    [Fact]
    public void A_project_in_the_vi_parameter_is_refused()
    {
        var error = Refusal(ActionTools.OpenFilePrecheck(
                @"C:\temp\RerunProbe\RerunProbe.lvproj", null, null, null))
            .GetProperty("error").GetString();

        Assert.Contains("RerunProbe.lvproj", error);
        Assert.Contains("projectPath", error);
    }

    [Fact]
    public void A_vi_in_the_project_parameter_is_refused()
    {
        var error = Refusal(ActionTools.OpenFilePrecheck(
                null, null, @"C:\temp\RerunProbe\Probe\Touch.vi", null))
            .GetProperty("error").GetString();

        Assert.Contains("Touch.vi", error);
        Assert.Contains("viPath", error);
    }

    /// <summary>
    /// The swap guards must fire BEFORE the nothing-to-open one, or a swapped path - which is a
    /// path, and therefore not "nothing" - would be reported as the wrong mistake.
    /// </summary>
    [Fact]
    public void A_swapped_path_is_reported_as_a_swap_not_as_an_empty_call() =>
        Assert.DoesNotContain("nothing to open",
            Refusal(ActionTools.OpenFilePrecheck(@"C:\x\P.lvproj", null, null, null))
                .GetProperty("error").GetString());

    /// <summary>
    /// The calls that are actually correct pass through untouched. Both real spellings, because a
    /// precheck that refused a working call would be worse than the silence it replaces.
    /// </summary>
    [Theory]
    [InlineData(@"C:\temp\RerunProbe\Probe\Touch.vi", "Touch.vi", null, null)]
    [InlineData(null, null, @"C:\temp\RerunProbe\RerunProbe.lvproj", "RerunProbe.lvproj")]
    [InlineData(@"C:\x\A.vi", "A.vi", @"C:\x\P.lvproj", "P.lvproj")]
    public void A_well_formed_call_is_not_refused(
        string? viPath, string? viName, string? projectPath, string? projectName) =>
        Assert.Null(ActionTools.OpenFilePrecheck(viPath, viName, projectPath, projectName));

    /// <summary>
    /// A path with no extension is not judged: it may be a real file, and refusing it would be a
    /// guess. The swap guards compare extensions precisely because that is all there is to go on.
    /// </summary>
    [Fact]
    public void An_extensionless_path_is_left_alone() =>
        Assert.Null(ActionTools.OpenFilePrecheck(@"C:\x\something", null, null, null));
}
