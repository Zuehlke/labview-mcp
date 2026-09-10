using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A throwaway copy of an AIXML document for VALIDATION, with every <c>&lt;VI&gt;</c> element's
/// <c>_name</c> replaced by a name nothing will ever ask for again.
///
/// WHY. A FAILED validation BURNS THE NAME until LabVIEW restarts. Measured 2026-09-09 end to end:
/// a document named <c>LVMCP Poison Probe.vi</c> was refused with <c>Error 53</c>, the fault was
/// corrected, and <c>ConvertAIXMLToVI</c> for the same <c>_name</c> then answered
///
///     Error 1051 ... A LabVIEW file of that name already exists in memory, or exists within a
///     project library already in memory.    (Method Name: Save:Instrument)
///
/// with <c>viExisted: false</c> - the file had never been on disk, so this is purely LabVIEW's
/// in-memory name registry. In the session that reported it, that cost a subVI its intended name,
/// and it would have blocked the build outright for a VI whose name the task fixed.
///
/// The tool note used to describe the effect and leave the caller to work around it by hand,
/// validating every new document under a scratch name first. Doing it here costs one temp file and
/// removes the trap: the caller's real name is never handed to LabVIEW by the validate path at all.
///
/// SCOPE IS VALIDATION ONLY. Conversion must of course use the real name - that is the point of
/// converting - so nothing here touches <see cref="LabVIEWMcp.Tools.AixmlTools"/>'s convert path.
/// </summary>
internal sealed class ValidationScratch : IDisposable
{
    private readonly string? temporary;

    private ValidationScratch(string path, string? temporary, IReadOnlyList<string> originalNames,
                              string? validatedAs)
    {
        Path = path;
        this.temporary = temporary;
        OriginalNames = originalNames;
        ValidatedAs = validatedAs;
    }

    /// <summary>The path to hand LabVIEW: the scratch copy, or the original when untouched.</summary>
    internal string Path { get; }

    /// <summary>The <c>_name</c> values the document really carries, in document order.</summary>
    internal IReadOnlyList<string> OriginalNames { get; }

    /// <summary>The throwaway name LabVIEW saw, or null when the document was passed through.</summary>
    internal string? ValidatedAs { get; }

    /// <summary>True when a scratch copy was made and the real name was never exposed.</summary>
    internal bool Substituted => temporary is not null;

    /// <summary>
    /// Builds the scratch copy. ANYTHING unreadable is passed through untouched, exactly as
    /// <see cref="SymbolicUids.Prepare"/> does and for the same reason: a file this cannot parse
    /// must still reach LabVIEW so that LabVIEW's own diagnosis is what the caller sees.
    /// </summary>
    internal static ValidationScratch Create(string aixmlPath, bool preserveName)
    {
        if (preserveName) return new ValidationScratch(aixmlPath, null, [], null);

        XDocument document;
        try
        {
            document = XDocument.Load(aixmlPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException
                                    or UnauthorizedAccessException or NotSupportedException
                                    or ArgumentException)
        {
            return new ValidationScratch(aixmlPath, null, [], null);
        }

        var carriers = document.Descendants()
            .Where(e => e.Name.LocalName == "VI" && e.Attribute("_name") is not null)
            .ToList();
        if (document.Root is { } root && root.Name.LocalName == "VI" &&
            root.Attribute("_name") is not null && !carriers.Contains(root))
            carriers.Insert(0, root);

        if (carriers.Count == 0) return new ValidationScratch(aixmlPath, null, [], null);

        var originals = carriers.Select(e => e.Attribute("_name")!.Value).ToList();

        // Unique per call, so a refusal can never burn a name a later call wants - not even this
        // same document's, validated twice.
        var throwaway = $"LVMCP Validate {Guid.NewGuid():N}"[..24] + ".vi";
        foreach (var element in carriers) element.SetAttributeValue("_name", throwaway);

        try
        {
            var folder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "LabVIEWMCP", "validate");
            Directory.CreateDirectory(folder);
            var scratch = System.IO.Path.Combine(folder, $"{Guid.NewGuid():N}.xml");
            document.Save(scratch, SaveOptions.DisableFormatting);
            return new ValidationScratch(scratch, scratch, originals, throwaway);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Could not write the copy - validate the original rather than refusing to validate.
            return new ValidationScratch(aixmlPath, null, originals, null);
        }
    }

    public void Dispose()
    {
        if (temporary is null) return;
        try { File.Delete(temporary); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
