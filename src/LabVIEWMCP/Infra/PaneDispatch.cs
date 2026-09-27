using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// What a class member's own saved file says about its connector pane: whether it is DYNAMIC
/// DISPATCH, and which pane terminals carry a TYPEDEF.
///
/// WHY THE FILE AND NOT THE .lvclass. A class records <c>NI.ClassItem.IsStaticMethod</c> for some
/// members and not for others - none of the accessors NI's wizard wrote in the fourth
/// TypedefAfterGDevCon build carried it, so lvai_describe_class answered <c>dynamicDispatch: null</c>
/// for every member and dispatch cost a separate lvai_vi_terminals call per VI.
///
/// THE DISPATCH RULE, MEASURED 2026-09-25: a connector pane terminal whose Flags carry
/// <c>0x8000</c> is a dynamic dispatch terminal. Checked against NI's own <c>IsStaticMethod</c> over
/// a random 150 of the 1 148 class members on this station that record it: 52 dynamic all carried
/// the bit, 97 static none did, one file did not parse. The pane is reached the way LabVIEW reaches
/// it - CONP names a consolidated TypeID, VCTP's TopLevel maps it to a FlatTypeID, and that flat
/// entry is the Function type whose children are the pane slots (scripts/pylv-conpane.py's route).
///
/// THE TYPEDEF HALF, same day, sixth build: nothing reported whether an ACCESSOR's terminal still
/// carries the field's typedef, and the agent searched the file's bytes by hand. A slot's TypeID
/// is a FLAT id within the same section; when that entry is a <c>TypeDef</c>, its
/// <c>&lt;Label Text="….ctl"/&gt;</c> names the file and its inner type's label names the terminal.
/// Measured on `Write Config.vi`: slot 10 -> flat 6 -> TypeDef `Channel Config.ctl`, terminal
/// `Channel Config`; the class and error terminals are Refnum and Cluster and name nothing.
/// </summary>
internal static class PaneDispatch
{
    internal const int DynamicDispatchFlag = 0x8000;

    internal sealed record PaneTypedef(string Terminal, string Typedef);

    internal sealed record Facts(bool? DynamicDispatch, IReadOnlyList<PaneTypedef> Typedefs);

    /// <summary>True/false from an extracted main XML, or null when the pane cannot be found.</summary>
    internal static bool? FromMainXml(XElement root) => Read(root)?.DynamicDispatch;

    /// <summary>The pane's facts from an extracted main XML, or null when there is no pane to read.</summary>
    internal static Facts? Read(XElement root)
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

        var dynamic = false;
        var typedefs = new List<PaneTypedef>();
        foreach (var slot in function.Elements("TypeDesc"))
        {
            var flags = Flags(slot);
            if (flags == 0) continue;                       // an unassigned slot
            if ((flags & DynamicDispatchFlag) != 0) dynamic = true;

            if (!int.TryParse((string?)slot.Attribute("TypeID"), out var id) || id < 0 || id >= types.Count)
                continue;
            var type = types[id];
            if ((string?)type.Attribute("Type") != "TypeDef") continue;
            var ctl = type.Elements("Label").Select(l => (string?)l.Attribute("Text"))
                .LastOrDefault(t => t?.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase) == true);
            if (ctl is null) continue;
            var terminal = (string?)type.Elements("TypeDesc").FirstOrDefault()?.Attribute("Label") ?? "";
            typedefs.Add(new PaneTypedef(terminal, ctl));
        }
        return new Facts(dynamic, typedefs);
    }

    private static int Flags(XElement slot) =>
        (string?)slot.Attribute("Flags") is { } text &&
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
                     System.Globalization.CultureInfo.InvariantCulture, out var flags)
            ? flags : 0;

    /// <summary>Extract one VI with pylabview and read its pane. Null when anything fails.</summary>
    internal static async Task<Facts?> ReadAsync(PyLabview.Bundle bundle, string viPath,
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
            return Read(XElement.Load(main));
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException) { return null; }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }
}
