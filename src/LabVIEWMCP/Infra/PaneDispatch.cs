using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Whether a class member is DYNAMIC DISPATCH, read off the member's own saved file.
///
/// WHY THE FILE AND NOT THE .lvclass. A class records <c>NI.ClassItem.IsStaticMethod</c> for some
/// members and not for others - none of the accessors NI's wizard wrote in the fourth
/// TypedefAfterGDevCon build carried it, so lvai_describe_class answered <c>dynamicDispatch: null</c>
/// for every member and dispatch cost a separate lvai_vi_terminals call per VI.
///
/// THE RULE, MEASURED 2026-09-25: a connector pane terminal whose Flags carry <c>0x8000</c> is a
/// dynamic dispatch terminal. Checked against NI's own <c>IsStaticMethod</c> over a random 150 of
/// the 1 148 class members on this station that record it: 52 dynamic all carried the bit, 97
/// static none did, one file did not parse. The pane is reached the way LabVIEW reaches it - CONP
/// names a consolidated TypeID, VCTP's TopLevel maps it to a FlatTypeID, and that flat entry is the
/// Function type whose children are the pane slots (the same route scripts/pylv-conpane.py takes).
/// </summary>
internal static class PaneDispatch
{
    internal const int DynamicDispatchFlag = 0x8000;

    /// <summary>True/false from an extracted main XML, or null when the pane cannot be found.</summary>
    internal static bool? FromMainXml(XElement root)
    {
        var conpId = (string?)root.Element("CONP")?.Descendants("TypeDesc").FirstOrDefault()
                                   ?.Attribute("TypeID");
        var section = root.Element("VCTP")?.Element("Section");
        if (conpId is null || section is null) return null;

        var flat = (string?)section.Element("TopLevel")?.Elements("TypeDesc")
            .FirstOrDefault(t => (string?)t.Attribute("Index") == conpId)?.Attribute("FlatTypeID");
        if (!int.TryParse(flat, out var flatId)) return null;

        var types = section.Elements("TypeDesc").ToList();
        if (flatId < 0 || flatId >= types.Count) return null;
        var function = types[flatId];
        if ((string?)function.Attribute("Type") != "Function") return null;

        return function.Elements("TypeDesc").Any(slot =>
            (string?)slot.Attribute("Flags") is { } text &&
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
                         System.Globalization.CultureInfo.InvariantCulture, out var flags) &&
            (flags & DynamicDispatchFlag) != 0);
    }

    /// <summary>Extract one VI with pylabview and read its pane. Null when anything fails.</summary>
    internal static async Task<bool?> ReadAsync(PyLabview.Bundle bundle, string viPath,
                                                int timeoutSeconds, CancellationToken ct)
    {
        if (!File.Exists(viPath)) return null;
        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "dispatch", Path.GetRandomFileName());
        Directory.CreateDirectory(scratch);
        try
        {
            var main = Path.Combine(scratch, "m.xml");
            var run = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", viPath, "-m", main], timeoutSeconds, ct);
            if (run.ExitCode != 0 || !File.Exists(main)) return null;
            return FromMainXml(XElement.Load(main));
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException) { return null; }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }
}
