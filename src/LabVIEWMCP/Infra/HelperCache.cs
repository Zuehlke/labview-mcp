namespace LabVIEWMcp.Infra;

/// <summary>
/// Whether a generated helper VI is still the one its AIXML source describes.
///
/// A CHANGED HELPER AIXML MUST REGENERATE THE CACHED .vi, and for most of 2026 it did not: every
/// caching site keyed on existence alone - <c>!File.Exists(helperVi)</c> - so editing a helper under
/// <c>scripts\</c> left LabVIEW running the PREVIOUS build's VI, silently, with nothing in any
/// answer to say so. Same shape as the embedded-but-unshipped documents: the file in the repository
/// was not the file in use. Found twice - 2026-08-31 while adding <c>parentInterfaces</c> to
/// <c>lvai_create_class.xml</c>, and 2026-09-07 while adding a per-terminal class path to
/// <c>lvai_add_class_method.xml</c> - both times because new controls simply were not there at run
/// time, and a control that is absent is not an error: the run answers 0 from every stage.
///
/// THE TIMESTAMP IS THE WHOLE CHECK, and it is deliberately narrow. <c>CLAUDE.md</c> warns against
/// deleting this cache to force a rebuild, because validating a helper is what killed LabVIEW three
/// times in one afternoon, and a development loop that regenerates every iteration pays that risk
/// every iteration. An AIXML that has genuinely changed is the one sanctioned case.
/// </summary>
internal static class HelperCache
{
    internal static bool NeedsRebuild(string aixmlPath, string helperViPath) =>
        !File.Exists(helperViPath) ||
        File.GetLastWriteTimeUtc(aixmlPath) > File.GetLastWriteTimeUtc(helperViPath);
}
