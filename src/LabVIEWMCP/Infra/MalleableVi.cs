namespace LabVIEWMcp.Infra;

/// <summary>
/// The guard against generating a MALLEABLE VI (<c>.vim</c>) straight out of AIXML.
///
/// <c>ConvertAIXMLToVI</c> ACCEPTS a <c>viPath</c> ending in <c>.vim</c>, answers
/// <c>errorCode 0</c>, writes a file of a plausible size - and that file is BROKEN,
/// <c>execState 0</c>. Nothing cheap saw it: measured 2026-09-17, <c>lvai_check_aixml</c>,
/// <c>lvai_validate_aixml</c>, the convert and <c>lvai_connector_pane</c> were all green on it,
/// and <c>lvai_generate_vi</c> reported <c>ok: true</c> with <c>paneViolations: 0</c>.
///
/// WHERE THE FAULT IS, settled with NI's own VIMs as controls:
/// <code>
///   same diagram saved as a .vi                                  execState 1  eIdle
///   same diagram saved as a .vim                                 execState 0  eBad
///   that .vi COPIED to a .vim name, nothing else changed         execState 0  eBad
///   NI's 1D Array Last Element.vim, original                     execState 1  eIdle
///   the same VI exported to AIXML and regenerated as a .vim      execState 0  eBad
/// </code>
/// So the diagram is never the variable. LabVIEW decides malleability from the file EXTENSION at
/// load time and then requires the VI to be configured for inlining - and VI properties are
/// exactly what AIXML cannot write. NI's published not-supported list says so twice over: "new
/// polymorphic VIs, new malleable VIs" and "non-default VI properties beyond basic description".
///
/// THE REFUSAL IS THE POINT. This is not a limit anybody needs to rediscover; it is a silent
/// failure that ships a broken VI past every green answer, which is the shape this repository has
/// paid for repeatedly. Refusing BEFORE the RPC also means no broken file is left on disk to be
/// found later and believed.
///
/// ONE IMPLEMENTATION ON PURPOSE. Three copies of one rule drift - <c>AixmlCheck.SafeUidBase</c>
/// and the lint's ceiling disagreed for days while telling readers their compliant files were
/// wrong - so both refusing tools and the document-side warning share this class.
///
/// <c>docs/malleable-vis.md</c> has the four LVSR flags, the eight-arm bisect that shows each one
/// is necessary, and the working route.
/// </summary>
internal static class MalleableVi
{
    internal const string Extension = ".vim";

    /// <summary>True when this output path would be a malleable VI.</summary>
    internal static bool IsMalleableTarget(string? viPath) =>
        !string.IsNullOrWhiteSpace(viPath)
        && Path.GetExtension(viPath).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The refusal text. Long on purpose: it is read at the moment someone is already confused,
    /// and a refusal that does not say where to go only moves the cost.
    /// </summary>
    internal const string Refusal = """
        A .vim TARGET WAS REFUSED, and nothing was written. ConvertAIXMLToVI would have accepted
        it, answered errorCode 0 and produced a BROKEN malleable VI - execState 0, eBad - with
        every cheap check still green.
        WHY: LabVIEW decides malleability from the file EXTENSION at load time and then requires
        the VI to be configured for inlining. VI properties are what AIXML cannot write, which is
        NI's own not-supported list twice over ("new malleable VIs", "non-default VI properties
        beyond basic description"). Measured with NI's own VIM as a control: the same diagram is
        eIdle as a .vi and eBad as a .vim, a pure file copy to a .vim name is eBad, and NI's
        healthy 1D Array Last Element.vim regenerated from its own AIXML export is eBad too.
        THE ROUTE THAT WORKS, four steps:
          1. generate this document to an ordinary .vi and confirm execState 1
          2. pylv_extract it into a bundle
          3. python scripts/pylv-make-malleable.py <bundle>/<Main>.xml --apply
          4. pylv_rebuild the bundle to the .vim path
        Rebuild to a path LabVIEW has NEVER loaded, or it keeps serving its in-memory copy and
        every check afterwards confirms the file you replaced. Verify with lvai_exec_state - it is
        the only cheap thing that sees this failure.
        docs/malleable-vis.md has the four LVSR flags and the bisect showing each one is needed.
        """;
}
