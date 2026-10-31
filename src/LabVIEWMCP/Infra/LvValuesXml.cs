using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Turns the XML that <c>Flatten To XML</c> produces for <c>Ctrl Val.Get All</c> into something
/// a caller can read without knowing LabVIEW's flattened-XML shape.
///
/// The shape, measured on LabVIEW 2026: an <c>&lt;Array&gt;</c> named
/// "Get All Control Values Variant" whose children are one <c>&lt;Cluster&gt;</c> per front-panel
/// object, each holding a <c>&lt;String&gt;</c> field named "Name" (the control's label) and an
/// <c>&lt;LvVariant&gt;</c> field named "Variant Data" wrapping the value in an element named
/// after its type - <c>Boolean</c>, <c>DBL</c>, <c>String</c>, <c>DBLWaveform</c>, <c>Cluster</c>.
///
/// The one trap, and the reason <c>Dimsize</c> is honoured rather than ignored: an EMPTY LabVIEW
/// array still serialises ONE child element as a type template. Measured on a waveform's Y array,
/// which reported <c>Dimsize 0</c> followed by a <c>&lt;DBL&gt;</c> with an empty <c>&lt;Val&gt;</c>.
/// Counting elements instead of reading Dimsize would therefore report a phantom control for a VI
/// that has none.
/// </summary>
internal static class LvValuesXml
{
    /// <summary>
    /// One front-panel object. <paramref name="Scalar"/> is the text of a direct
    /// <c>&lt;Val&gt;</c> child and is null for compound values - a cluster, array or waveform
    /// has no single text form, so those callers need <paramref name="Xml"/>.
    /// </summary>
    internal readonly record struct Value(string Name, string Type, string? Scalar, string Xml,
                                          XElement? Element = null);

    public static IReadOnlyList<Value> Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];

        XElement root;
        try
        {
            root = LvNativeValue.ParseExact(xml);
        }
        catch (XmlException)
        {
            // A caller gets the raw text back instead; failing to parse is not failing the run.
            return [];
        }

        var clusters = root.Elements("Cluster").ToList();
        if (int.TryParse((string?)root.Element("Dimsize"), out var declared)
            && declared >= 0 && declared < clusters.Count)
        {
            clusters = clusters.Take(declared).ToList();
        }

        var values = new List<Value>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var name = cluster.Elements("String")
                .FirstOrDefault(e => (string?)e.Element("Name") == "Name")
                ?.Element("Val")?.Value;

            var variant = cluster.Elements("LvVariant")
                .FirstOrDefault(e => (string?)e.Element("Name") == "Variant Data");

            var payload = variant?.Elements()
                .FirstOrDefault(e => e.Name.LocalName != "Name");

            if (name is null || payload is null) continue;

            values.Add(new Value(
                Name: name,
                Type: payload.Name.LocalName,
                Scalar: payload.Element("Val")?.Value,
                Xml: payload.ToString(),
                Element: payload));
        }
        return values;
    }

    /// <summary>Control label to {type, value, xml}, ready to hand back as a tool result.</summary>
    /// <remarks>
    /// <paramref name="native"/> is what lvai_run_vi_and_read_values gives the client: `value` as
    /// native JSON decoded from the exact element, and `xml` only with <paramref name="includeXml"/>.
    /// The server's own callers take the default and keep the Val text plus the XML.
    /// </remarks>
    public static JsonObject ToJson(IReadOnlyList<Value> values, bool native = false, bool includeXml = true)
    {
        var obj = new JsonObject();
        foreach (var value in values)
        {
            var entry = new JsonObject
            {
                ["type"] = value.Type,
                ["value"] = value.Scalar,
                ["xml"] = value.Xml,
            };
            if (native) LvNativeValue.Fill(entry, value.Element, includeXml);
            obj[value.Name] = entry;
        }
        return obj;
    }
}
