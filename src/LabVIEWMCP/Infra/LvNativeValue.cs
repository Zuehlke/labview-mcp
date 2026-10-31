using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// LabVIEW's flattened XML of one value, as a native JSON value: a cluster as an object, an array
/// as a list, a boolean as true/false, a number as a number, an enum as its item NAME.
///
/// WHY. lvai_run_vi_and_read_values answered every compound value as `"value": null` beside its
/// flattened XML, and that XML is the largest single item that LabVIEW tool answers put into a
/// session: 10.5 M characters over this repository's transcripts. An error cluster is about 330
/// characters of XML and 45 as `{"status":false,"code":0,"source":""}`. Measured 2026-10-30,
/// `docs/token-economy.md` §4.3.
///
/// ONLY WHAT IS DECODED EXACTLY IS DECODED. A type this does not know - a timestamp, a waveform,
/// a variant, a refnum, a complex number - makes <see cref="TryDecode"/> answer false for the
/// WHOLE value, and the caller then keeps the XML for it. A partial object that silently drops a
/// field would be worse than the XML it replaces.
///
/// Three choices worth knowing:
/// - an integer beyond 2^53 and a non-finite float (NaN, Inf) become their TEXT, because a JSON
///   number cannot carry them exactly;
/// - a float is written as the double LabVIEW's text parses to. LabVIEW prints 5050 as
///   `5049.99999999999999` (ComValues), so compare numbers, never text;
/// - a cluster whose element names are not all present and unique becomes a LIST, in order,
///   because an object would have to invent keys or drop a field.
/// </summary>
internal static class LvNativeValue
{
    private const long MaxExactInteger = 1L << 53;

    private static readonly HashSet<string> SignedTags = ["I8", "I16", "I32", "I64"];
    private static readonly HashSet<string> UnsignedTags = ["U8", "U16", "U32", "U64"];
    private static readonly HashSet<string> FloatTags = ["SGL", "DBL", "EXT"];
    private static readonly HashSet<string> TextTags = ["String", "Path"];
    private static readonly HashSet<string> EnumTags = ["EB", "EW", "EL"];

    /// <summary>Decodes the XML text of one value; false when any part of it is not covered.</summary>
    public static bool TryDecode(string? xml, out JsonNode? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(xml)) return false;
        XElement element;
        try { element = ParseExact(xml); }
        catch (XmlException) { return false; }
        return TryDecode(element, out value);
    }

    public static bool TryDecode(XElement element, out JsonNode? value)
    {
        value = null;
        var tag = element.Name.LocalName;
        var val = element.Element("Val")?.Value;

        if (tag == "Boolean")
        {
            if (val is "0" or "1") { value = JsonValue.Create(val == "1"); return true; }
            if (bool.TryParse(val, out var b)) { value = JsonValue.Create(b); return true; }
            return false;
        }
        if (SignedTags.Contains(tag))
        {
            if (!long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
            value = n is > MaxExactInteger or < -MaxExactInteger ? JsonValue.Create(val) : JsonValue.Create(n);
            return true;
        }
        if (UnsignedTags.Contains(tag))
        {
            if (!ulong.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
            value = n > MaxExactInteger ? JsonValue.Create(val) : JsonValue.Create((long)n);
            return true;
        }
        if (FloatTags.Contains(tag))
        {
            if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
            value = double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(val);
            return true;
        }
        if (TextTags.Contains(tag))
        {
            if (val is null) return false;
            value = JsonValue.Create(val);
            return true;
        }
        if (EnumTags.Contains(tag))
        {
            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return false;
            var choices = element.Elements("Choice").Select(c => c.Value).ToList();
            value = index >= 0 && index < choices.Count ? JsonValue.Create(choices[index]) : JsonValue.Create(index);
            return true;
        }
        if (tag == "Cluster") return TryCluster(element, out value);
        if (tag == "Array") return TryArray(element, out value);
        return false;
    }

    /// <summary>The value elements of a container: every child but its Name, NumElts and Dimsize.</summary>
    private static List<XElement> Members(XElement container) =>
        container.Elements().Where(e => e.Name.LocalName is not ("Name" or "NumElts" or "Dimsize")).ToList();

    private static bool TryCluster(XElement cluster, out JsonNode? value)
    {
        value = null;
        var members = Members(cluster);
        var decoded = new List<(string? Name, JsonNode? Value)>(members.Count);
        foreach (var m in members)
        {
            if (!TryDecode(m, out var v)) return false;
            decoded.Add((m.Element("Name")?.Value, v));
        }
        var names = decoded.Select(d => d.Name).ToList();
        var keyed = names.All(n => n is { Length: > 0 })
                    && names.Distinct(StringComparer.Ordinal).Count() == names.Count;
        if (keyed)
        {
            var obj = new JsonObject();
            foreach (var (name, v) in decoded) obj[name!] = v;
            value = obj;
        }
        else
        {
            var list = new JsonArray();
            foreach (var (_, v) in decoded) list.Add(v);
            value = list;
        }
        return true;
    }

    /// <summary>
    /// An array: one Dimsize per dimension, then the elements in row-major order. An EMPTY array
    /// still carries ONE template element (LvValuesXml), so the element count comes from Dimsize,
    /// never from the children.
    /// </summary>
    private static bool TryArray(XElement array, out JsonNode? value)
    {
        value = null;
        var dims = new List<int>();
        foreach (var d in array.Elements("Dimsize"))
        {
            if (!int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
                return false;
            dims.Add(n);
        }
        if (dims.Count == 0) return false;
        var total = dims.Aggregate(1L, (a, b) => a * b);
        var members = Members(array);
        if (total > members.Count) return false;

        var flat = new List<JsonNode?>((int)total);
        for (var i = 0; i < total; i++)
        {
            if (!TryDecode(members[i], out var v)) return false;
            flat.Add(v);
        }
        var position = 0;
        value = Shape(flat, dims, 0, ref position);
        return true;
    }

    private static JsonArray Shape(List<JsonNode?> flat, List<int> dims, int level, ref int position)
    {
        var result = new JsonArray();
        for (var i = 0; i < dims[level]; i++)
        {
            if (level == dims.Count - 1) result.Add(flat[position++]);
            else result.Add(Shape(flat, dims, level + 1, ref position));
        }
        return result;
    }

    /// <summary>
    /// Parses LabVIEW's flattened XML WITHOUT end-of-line normalization. An XML parser turns every
    /// CR and CRLF in text into LF, so a string that left LabVIEW as `a\nb\r\nc\rd` came back as
    /// `a\nb\nc\nd` - measured 2026-10-31 on both backends with LabVIEW's raw output beside it.
    /// XmlTextReader with Normalization off keeps the text exactly as LabVIEW wrote it.
    /// </summary>
    public static XElement ParseExact(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = new XmlTextReader(text)
        {
            Normalization = false,
            DtdProcessing = DtdProcessing.Prohibit,
        };
        return XElement.Load(reader, LoadOptions.None);
    }

    /// <summary>
    /// One run-answer entry as the client gets it: `value` decoded from the EXACT element - parsed
    /// with ParseExact, or the in-memory element the ActiveX route built - and the XML string left
    /// out unless <paramref name="includeXml"/>. An element that cannot be decoded keeps its XML
    /// and says so in `xmlKeptBecause`. Never decode from the entry's `xml` STRING: XElement's
    /// ToString writes every line end in text as CRLF, so that string has already lost them.
    /// </summary>
    public static void Fill(JsonObject entry, XElement? element, bool includeXml)
    {
        if (element is not null && TryDecode(element, out var native))
        {
            entry["value"] = native;
            if (!includeXml) entry.Remove("xml");
        }
        else if (entry["xml"] is not null && !includeXml)
        {
            entry["xmlKeptBecause"] = "this value's type is not decoded to native JSON (a timestamp, " +
                                      "waveform, variant, refnum or complex number), so its XML is the value.";
        }
    }
}
