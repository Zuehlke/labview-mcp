using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Tools;

/// <summary>Which Delacor scripting layout an installation carries.</summary>
internal enum DqmhGeneration
{
    /// <summary>DQMH up to 6.x: every scripting VI under `project\Delacor\DQMH`, measured on 5.x.</summary>
    Classic,

    /// <summary>
    /// DQMH 7.x (7.1.2.1547 measured, 2026-10-30): the shared scripting VIs moved into
    /// `dqmh_lib_DQMH Scripting Support.lvlib` under `vi.lib\Delacor\Delacor QMH\Libraries`, call
    /// their input `error in`, and Script New Event, Script New Module, Rename, Convert and the
    /// dependent-broadcast check have new panes (docs/dqmh-scripting.md section 10).
    /// </summary>
    V7,
}

/// <summary>
/// The wrapper route for one DQMH tool on THIS station: which generation, which Delacor VIs to
/// open, and which of our wrapper documents to run.
/// </summary>
/// <param name="Helper">The wrapper - `lvdqmh_x` for classic, `lvdqmh_x_dqmh7` for 7.x when
/// that document exists (a wrapper with no Delacor call has one version only).</param>
internal sealed record DqmhRoute(
    DqmhGeneration Generation, string[] Targets, string Helper, string[] SubHelpers)
{
    internal string GenerationName => Generation == DqmhGeneration.V7 ? "7.x" : "classic (up to 6.x)";
}

/// <summary>
/// Choosing between the two DQMH scripting layouts. Both are kept: a station with DQMH 5 or 6
/// runs the wrappers it always ran, byte for byte, and one with DQMH 7 runs their `_dqmh7`
/// twins. The generation is read off the FILES of each LabVIEW installation, never off a version
/// string - Delacor's own scripting decides by what is there, and so do we.
/// </summary>
internal static class DqmhLayout
{
    internal const string Suffix = "_dqmh7";

    /// <summary>Where 7.x keeps the shared scripting VIs, relative to the installation.</summary>
    internal const string Support = @"vi.lib\Delacor\Delacor QMH\Libraries\_Scripting Support\";

    /// <summary>The marker of a 7.x installation: the project parse in its new home.</summary>
    internal const string V7Marker = Support + "Parse Project for DQMH Modules.vi";

    /// <summary>
    /// The 7.x location of a classic target. The shared VIs moved into `_Scripting Support`; the
    /// unit-test wrapper's own copy of Get All Events was renamed; everything else stayed.
    /// </summary>
    internal static string Relocate(string classic)
    {
        var name = Path.GetFileName(classic);
        if (classic.Contains(@"\_DQMH New Unit Test\", StringComparison.OrdinalIgnoreCase)
            && name.Equals("Get All Events in Module.vi", StringComparison.OrdinalIgnoreCase))
            return @"project\Delacor\DQMH\_DQMH New Unit Test\Get All Events in Module (Unit Test).vi";
        return MovedToSupport.Contains(name, StringComparer.OrdinalIgnoreCase) ? Support + name : classic;
    }

    /// <summary>The scripting VIs 7.x moved into `dqmh_lib_DQMH Scripting Support.lvlib`.</summary>
    internal static readonly string[] MovedToSupport =
    [
        "Parse Project for DQMH Modules.vi", "Close Scripting References.vi",
        "Get All Events in Module.vi", "Open Main and Tester References.vi",
        "Find Tester on Disk.vi", "Find RT Tester on Disk.vi",
    ];

    /// <summary>The generation of one installation, or null when it carries no DQMH scripting.</summary>
    internal static DqmhGeneration? GenerationOf(string installRoot)
    {
        if (File.Exists(Path.Combine(installRoot, V7Marker))) return DqmhGeneration.V7;
        return File.Exists(Path.Combine(installRoot,
            @"project\Delacor\DQMH\_DQMH New Event\Parse Project for DQMH Modules.vi"))
            ? DqmhGeneration.Classic : null;
    }

    /// <summary>
    /// The route for a wrapper whose CLASSIC targets are given: the first installation (newest
    /// first, as LabViewLocator orders them) that carries a DQMH layout with every target. Null
    /// when DQMH is not installed, or the installed version lacks one of the VIs.
    /// </summary>
    internal static DqmhRoute? Resolve(
        string scripts, string helper, IReadOnlyList<string> classicTargets, string[]? subHelpers = null)
    {
        foreach (var install in LabViewLocator.Discover())
        {
            var root = Path.GetDirectoryName(install.ExePath) ?? "";
            if (GenerationOf(root) is not { } generation) continue;
            var relative = generation == DqmhGeneration.V7
                ? classicTargets.Select(Relocate).ToArray()
                : [.. classicTargets];
            var paths = relative.Select(t => Path.Combine(root, t)).ToArray();
            if (!paths.All(File.Exists)) continue;
            return new DqmhRoute(generation, paths, HelperFor(scripts, helper, generation),
                [.. (subHelpers ?? []).Select(h => HelperFor(scripts, h, generation))]);
        }
        return null;
    }

    /// <summary>
    /// The route for a wrapper that exists for DQMH 7.x ONLY - Duplicate Event, Duplicate Module,
    /// Add Helper Loop, whose Delacor VIs were read and run on 7.1.2 and never on 5.x. Its targets
    /// are given in their 7.x locations. Null when no installation carries 7.x with all of them;
    /// <paramref name="classicInstalled"/> then says whether a classic DQMH is there instead.
    /// </summary>
    internal static DqmhRoute? ResolveV7Only(string scripts, string helper, IReadOnlyList<string> v7Targets,
        string[]? subHelpers, out bool classicInstalled)
    {
        classicInstalled = false;
        foreach (var install in LabViewLocator.Discover())
        {
            var root = Path.GetDirectoryName(install.ExePath) ?? "";
            var generation = GenerationOf(root);
            if (generation == DqmhGeneration.Classic) classicInstalled = true;
            if (generation != DqmhGeneration.V7) continue;
            var paths = v7Targets.Select(t => Path.Combine(root, t)).ToArray();
            if (!paths.All(File.Exists)) continue;
            return new DqmhRoute(DqmhGeneration.V7, paths, HelperFor(scripts, helper, DqmhGeneration.V7),
                [.. (subHelpers ?? []).Select(h => HelperFor(scripts, h, DqmhGeneration.V7))]);
        }
        return null;
    }

    /// <summary>A wrapper's name for a generation - its `_dqmh7` twin where one ships.</summary>
    internal static string HelperFor(string scripts, string helper, DqmhGeneration generation) =>
        generation == DqmhGeneration.V7 && File.Exists(Path.Combine(scripts, helper + Suffix + ".xml"))
            ? helper + Suffix
            : helper;

    /// <summary>The generation the station would use, for answers and messages.</summary>
    internal static DqmhGeneration? Installed()
    {
        foreach (var install in LabViewLocator.Discover())
            if (GenerationOf(Path.GetDirectoryName(install.ExePath) ?? "") is { } generation)
                return generation;
        return null;
    }

    internal static string MissingMessage(string what) =>
        $"Delacor DQMH is not installed, or this version lacks {what}. Looked for both layouts: " +
        "classic (project\\Delacor\\DQMH, up to 6.x) and 7.x (shared scripting VIs under " +
        "vi.lib\\Delacor\\Delacor QMH\\Libraries\\_Scripting Support).";
}
