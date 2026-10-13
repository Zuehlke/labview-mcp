using System.Globalization;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Values across LabVIEW's ActiveX boundary, in the AIXML spelling on both sides: a control's
/// type as the reader writes it (`double`, `uint16{Off,On}`, `array{int32}`,
/// `cluster{double.x,int32.n}`) and a value as an AIXML literal (`3.5`, `[1,2,3]`, `[false,0,]`).
///
/// Pure functions, so every rule here is tested without LabVIEW. The two rules that cost runs in
/// the experiment (experiments/aixml-without-grpc/lv-com.ps1):
///   * a CLUSTER must cross as a variant array, object[] - a double[] is accepted with no error
///     and silently ignored, the control stays 0;
///   * a 2D array crosses as ONE rectangular array, and enumerating it flattens the rows.
/// What is not covered is refused by name rather than guessed: a waveform, a timestamp, a
/// refnum, a variant, an array of clusters.
/// </summary>
internal static class ComValues
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly HashSet<string> Floats = new(StringComparer.Ordinal)
        { "double", "single", "extended" };

    private static readonly Dictionary<string, (long Min, ulong Max)> Integers = new(StringComparer.Ordinal)
    {
        ["int8"] = (sbyte.MinValue, (ulong)sbyte.MaxValue),
        ["int16"] = (short.MinValue, (ulong)short.MaxValue),
        ["int32"] = (int.MinValue, int.MaxValue),
        ["int64"] = (long.MinValue, long.MaxValue),
        ["uint8"] = (0, byte.MaxValue),
        ["uint16"] = (0, ushort.MaxValue),
        ["uint32"] = (0, uint.MaxValue),
        ["uint64"] = (0, ulong.MaxValue),
    };

    /// <summary>
    /// Text -> the .NET value SetControlValue takes for a control of <paramref name="type"/>, or
    /// null with <paramref name="problem"/> saying why not.
    /// </summary>
    public static object? FromText(string text, string type, out string? problem)
    {
        problem = null;
        type = type.Trim();

        if (type is "string" or "path") return text;

        // An I/O name - a DAQmx channel, a VISA resource - `tag{N}`: its value crosses as text.
        if (type.StartsWith("tag{", StringComparison.Ordinal)) return text;

        if (type == "bool")
        {
            if (text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            problem = $"'{text}' is not a boolean - write true or false";
            return null;
        }

        if (Floats.Contains(type))
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, Invariant, out var d)) return d;
            problem = $"'{text}' is not a number (write it with a decimal point, not a comma)";
            return null;
        }

        if (Integers.TryGetValue(type, out var range)) return Integer(text, type, range, out problem);

        // An enum or ring: the base type with its item names, `uint16{Off,On,Auto}`. An item name
        // is matched exactly, an index is taken as it is - and text that is neither is refused,
        // because falling back to item 0 turns a typo into a valid-looking run.
        if (Braces(type) is { } enumParts && Integers.ContainsKey(enumParts.Head))
        {
            var items = SplitTop(enumParts.Body);
            var named = items.FindIndex(i => string.Equals(i, text, StringComparison.Ordinal));
            if (named >= 0) return named;
            if (int.TryParse(text.Trim(), NumberStyles.Integer, Invariant, out var index)
                && index >= 0 && index < items.Count) return index;
            problem = $"'{text}' is neither an item of {type} nor an index below {items.Count}";
            return null;
        }

        // LabVIEW's OWN XML for an array or a cluster - what NI's run helper takes and what the
        // gRPC route's answers carry, so a tool (or a caller) that writes it reaches either route.
        // Found 2026-10-13: lvai_graft_diagram's rewire helper is handed its name lists that way,
        // and on the ActiveX route they were refused as "not an array literal".
        if (text.TrimStart().StartsWith('<') && Braces(type) is { Head: "array" or "cluster" })
            return FromLabviewXml(text, type, out problem);

        if (Braces(type) is { Head: "array" } array)
            return ArrayFromText(text, array.Body.Trim(), out problem);

        if (Braces(type) is { Head: "cluster" } cluster)
            return ClusterFromText(text, cluster.Body, out problem);

        problem = $"a control of type {type} cannot be set over COM by this server";
        return null;
    }

    private static object? Integer(string text, string type, (long Min, ulong Max) range, out string? problem)
    {
        problem = null;
        if (!decimal.TryParse(text.Trim(), NumberStyles.Float, Invariant, out var value)
            || value != decimal.Truncate(value))
        {
            problem = $"'{text}' is not a whole number, and the control is {type}";
            return null;
        }
        if (value < range.Min || value > range.Max)
        {
            problem = $"{text} does not fit {type}";
            return null;
        }
        // VT_I4 where it fits and VT_R8 above it: LabVIEW coerces a numeric variant into the
        // control's own representation, so the variant only has to carry the value exactly.
        return value is >= int.MinValue and <= int.MaxValue ? (object)(int)value : (double)value;
    }

    private static object? ArrayFromText(string text, string element, out string? problem)
    {
        problem = null;
        if (Literal(text) is not { } items)
        {
            problem = $"'{text}' is not an array literal - write [1,2,3]";
            return null;
        }
        if (element.StartsWith("array{", StringComparison.Ordinal))
        {
            problem = "a 2D array cannot be set over COM by this server yet";
            return null;
        }

        var values = new List<object?>();
        foreach (var item in items)
        {
            values.Add(FromText(item, element, out problem));
            if (problem is not null)
            {
                problem = $"element '{item}': {problem}";
                return null;
            }
        }

        // A TYPED array for scalars, which is what LabVIEW's own ActiveX marshalling hands back.
        return element switch
        {
            "string" or "path" => values.Cast<string>().ToArray(),
            "bool" => values.Cast<bool>().ToArray(),
            _ when Floats.Contains(element) => values.Select(v => Convert.ToDouble(v, Invariant)).ToArray(),
            _ when Integers.ContainsKey(element) => values.Select(v => Convert.ToInt32(v, Invariant)).ToArray(),
            _ => Refuse(out problem, $"an array of {element} cannot be set over COM by this server yet"),
        };
    }

    /// <summary>
    /// LabVIEW's flattened XML (`&lt;Array&gt;`, `&lt;Cluster&gt;`, optionally inside
    /// `&lt;LvVariant&gt;`) -> the value SetControlValue takes for <paramref name="type"/>: a typed
    /// array of scalars, an object[] for a cluster. Element values go through
    /// <see cref="FromText"/>, so they convert exactly as a literal's would.
    /// </summary>
    public static object? FromLabviewXml(string xml, string type, out string? problem)
    {
        problem = null;
        System.Xml.Linq.XElement root;
        try { root = System.Xml.Linq.XElement.Parse(xml.Trim()); }
        catch (System.Xml.XmlException e)
        {
            problem = $"not well-formed XML: {e.Message}";
            return null;
        }
        if (root.Name.LocalName == "LvVariant")
            root = root.Elements().FirstOrDefault(e => e.Name.LocalName != "Name") ?? root;
        return FromXmlElement(root, type.Trim(), out problem);
    }

    private static object? FromXmlElement(System.Xml.Linq.XElement element, string type, out string? problem)
    {
        problem = null;
        if (Braces(type) is { Head: "array" } array)
        {
            if (element.Name.LocalName != "Array")
                return Refuse(out problem, $"<{element.Name.LocalName}> given for {type}");
            var elementType = FieldType(array.Body.Trim());
            if (elementType.StartsWith("array{", StringComparison.Ordinal))
                return Refuse(out problem, "a 2D array cannot be set over COM by this server yet");
            var size = int.TryParse((string?)element.Element("Dimsize"), out var n) ? n : 0;
            var values = new List<object?>();
            foreach (var child in element.Elements()
                         .Where(e => e.Name.LocalName is not ("Name" or "Dimsize")).Take(size))
            {
                values.Add(FromXmlElement(child, elementType, out problem));
                if (problem is not null) return Refuse(out problem, $"array element: {problem}");
            }
            return elementType switch
            {
                "string" or "path" => values.Cast<string>().ToArray(),
                "bool" => values.Cast<bool>().ToArray(),
                _ when Floats.Contains(elementType) => values.Select(v => Convert.ToDouble(v, Invariant)).ToArray(),
                _ when Integers.ContainsKey(elementType) => values.Select(v => Convert.ToInt32(v, Invariant)).ToArray(),
                _ when Braces(elementType) is { Head: "cluster" } => values.ToArray(),
                _ => Refuse(out problem, $"an array of {elementType} cannot be set over COM by this server yet"),
            };
        }
        if (Braces(type) is { Head: "cluster" } cluster)
        {
            if (element.Name.LocalName != "Cluster")
                return Refuse(out problem, $"<{element.Name.LocalName}> given for {type}");
            var fields = SplitTop(cluster.Body);
            var children = element.Elements().Where(e => e.Name.LocalName is not ("Name" or "NumElts")).ToList();
            if (children.Count != fields.Count)
                return Refuse(out problem, $"the cluster has {fields.Count} elements and the XML gives {children.Count}");
            var values = new object?[fields.Count];
            for (var i = 0; i < fields.Count; i++)
            {
                values[i] = FromXmlElement(children[i], FieldType(fields[i]), out problem);
                if (problem is not null) return Refuse(out problem, $"element {i} ({fields[i]}): {problem}");
            }
            return values;
        }
        // a scalar: its <Val>, with LabVIEW's 0/1 for a boolean
        var text = (string?)element.Element("Val") ?? "";
        if (type == "bool") text = text.Trim() is "1" or "TRUE" or "true" ? "true" : "false";
        return FromText(text, type, out problem);
    }

    private static object? ClusterFromText(string text, string body, out string? problem)
    {
        problem = null;
        if (Literal(text) is not { } items)
        {
            problem = $"'{text}' is not a cluster literal - write [1.5,2,text]";
            return null;
        }
        var fields = SplitTop(body);
        if (items.Count != fields.Count)
        {
            problem = $"the cluster has {fields.Count} elements and '{text}' gives {items.Count}";
            return null;
        }

        var values = new object?[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var fieldType = FieldType(fields[i]);
            values[i] = FromText(items[i], fieldType, out problem);
            if (problem is not null)
            {
                problem = $"element {i} ({fields[i]}): {problem}";
                return null;
            }
        }
        return values;   // object[], never a typed array - see the class comment
    }

    private static object? Refuse(out string? problem, string why)
    {
        problem = why;
        return null;
    }

    /// <summary>A .NET value from GetControlValue, written as an AIXML literal.</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", Invariant),
        float f => f.ToString("R", Invariant),
        Array { Rank: 2 } grid => FormatGrid(grid),
        Array items => "[" + string.Join(",", items.Cast<object?>().Select(Format)) + "]",
        IFormattable number => number.ToString(null, Invariant),
        _ => value.ToString() ?? "",
    };

    private static string FormatGrid(Array grid)
    {
        var rows = new StringBuilder("[");
        for (var i = 0; i < grid.GetLength(0); i++)
        {
            if (i > 0) rows.Append(',');
            rows.Append('[');
            for (var j = 0; j < grid.GetLength(1); j++)
            {
                if (j > 0) rows.Append(',');
                rows.Append(Format(grid.GetValue(i, j)));
            }
            rows.Append(']');
        }
        return rows.Append(']').ToString();
    }

    // ------------------------------------------------------------ LabVIEW's flattened XML

    private static readonly Dictionary<string, string> Tags = new(StringComparer.Ordinal)
    {
        ["double"] = "DBL", ["single"] = "SGL", ["extended"] = "EXT",
        ["int8"] = "I8", ["int16"] = "I16", ["int32"] = "I32", ["int64"] = "I64",
        ["uint8"] = "U8", ["uint16"] = "U16", ["uint32"] = "U32", ["uint64"] = "U64",
        ["bool"] = "Boolean", ["string"] = "String", ["path"] = "Path",
    };

    private static readonly Dictionary<string, string> EnumTags = new(StringComparer.Ordinal)
        { ["uint8"] = "EB", ["uint16"] = "EW", ["uint32"] = "EL" };

    /// <summary>
    /// A value read over ActiveX, written as LabVIEW's own Flatten To XML writes it - so the COM
    /// route answers in the SAME shape as the gRPC helper, and every parser written against that
    /// (the class tools read `error out` and their counts out of it) works on both. Null for a type
    /// this does not cover; the caller then has the literal only.
    ///
    /// MEASURED 2026-10-10 against LabVIEW's output for the same VIs and inputs: element names, an
    /// array element's EMPTY name, Dimsize, the one empty template element of an empty array,
    /// NumElts, Boolean as 0/1, I32 as an integer. A DBL is written with 14 decimals and below 0.01
    /// as `1.23400000000000E-3` - LabVIEW's shape, NOT its digits: it printed 5050 as
    /// `5049.99999999999999`, which no exact conversion reproduces, so compare numbers, not text.
    /// An enum's EW/EB/EL with its Choice list is LabVIEW's documented shape, not measured here.
    /// </summary>
    public static System.Xml.Linq.XElement? ToLabviewXml(string name, string type, object? value)
    {
        type = type.Trim();
        static System.Xml.Linq.XElement Scalar(string tag, string name, string text) =>
            new(tag, new System.Xml.Linq.XElement("Name", name), new System.Xml.Linq.XElement("Val", text));

        if (Tags.TryGetValue(type, out var tag))
            return Scalar(tag, name, LabviewText(type, value));

        if (Braces(type) is { } parts && EnumTags.TryGetValue(parts.Head, out var enumTag))
        {
            var element = new System.Xml.Linq.XElement(enumTag, new System.Xml.Linq.XElement("Name", name));
            foreach (var choice in SplitTop(parts.Body))
                element.Add(new System.Xml.Linq.XElement("Choice", choice));
            element.Add(new System.Xml.Linq.XElement("Val", LabviewText("uint16", value)));
            return element;
        }

        if (Braces(type) is { Head: "cluster" } cluster && value is Array fieldsValue)
        {
            var fields = SplitTop(cluster.Body);
            if (fields.Count != fieldsValue.Length) return null;
            var element = new System.Xml.Linq.XElement("Cluster",
                new System.Xml.Linq.XElement("Name", name),
                new System.Xml.Linq.XElement("NumElts", fields.Count));
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                var fieldType = FieldType(field);
                var fieldName = field.Length > fieldType.Length ? field[(fieldType.Length + 1)..] : "";
                if (ToLabviewXml(fieldName, fieldType, fieldsValue.GetValue(i)) is not { } child) return null;
                element.Add(child);
            }
            return element;
        }

        if (Braces(type) is { Head: "array" } array && value is Array { Rank: 1 } items)
        {
            // AN ELEMENT MAY CARRY A LABEL - `array{string.String}`, as the reader writes a composed
            // helper's `node names found` - and the label is a name, not part of the type. Looking
            // `string.String` up as a type dropped the whole array, so the swap tool read no names
            // and listed every socket as not on the diagram, measured 2026-10-11.
            var body = array.Body.Trim();
            var elementType = FieldType(body);
            var elementName = body.Length > elementType.Length ? body[(elementType.Length + 1)..] : "";
            var element = new System.Xml.Linq.XElement("Array",
                new System.Xml.Linq.XElement("Name", name),
                new System.Xml.Linq.XElement("Dimsize", items.Length));
            // AN EMPTY ARRAY STILL CARRIES ONE ELEMENT, with an empty Val, as its type template.
            if (items.Length == 0)
            {
                if (ToLabviewXml(elementName, elementType, null) is not { } template) return null;
                template.Element("Val")?.SetValue("");
                element.Add(template);
            }
            foreach (var item in items)
            {
                if (ToLabviewXml(elementName, elementType, item) is not { } child) return null;
                element.Add(child);
            }
            return element;
        }

        return null;
    }

    private static string LabviewText(string type, object? value)
    {
        if (value is null) return type is "bool" ? "0" : type is "string" or "path" ? "" : "0";
        if (type == "bool") return value is true ? "1" : "0";
        if (Floats.Contains(type))
        {
            var d = Convert.ToDouble(value, Invariant);
            if (d == 0 || Math.Abs(d) >= 0.01 || double.IsNaN(d) || double.IsInfinity(d))
                return d.ToString("F14", Invariant);
            var exponent = (int)Math.Floor(Math.Log10(Math.Abs(d)));
            var mantissa = d / Math.Pow(10, exponent);
            return mantissa.ToString("F14", Invariant) + "E" + exponent.ToString(Invariant);
        }
        return value is IFormattable number ? number.ToString(null, Invariant) : value.ToString() ?? "";
    }

    // ------------------------------------------------------------ the AIXML spellings

    /// <summary>`head{body}` split at the first brace, or null for a type without one.</summary>
    internal static (string Head, string Body)? Braces(string type)
    {
        var open = type.IndexOf('{');
        if (open <= 0 || !type.EndsWith('}')) return null;
        return (type[..open], type[(open + 1)..^1]);
    }

    /// <summary>A cluster field `T.name` -> `T`, where T may itself hold dots inside braces.</summary>
    internal static string FieldType(string field)
    {
        var depth = 0;
        for (var i = field.Length - 1; i >= 0; i--)
        {
            if (field[i] == '}') depth++;
            else if (field[i] == '{') depth--;
            else if (field[i] == '.' && depth == 0) return field[..i];
        }
        return field;
    }

    /// <summary>Split at commas that are not inside braces or brackets.</summary>
    internal static List<string> SplitTop(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{' or '[': depth++; break;
                case '}' or ']': depth--; break;
                case ',' when depth == 0:
                    parts.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>`[a,b,[c,d]]` -> its top-level items; `[]` -> none; anything else -> null.</summary>
    internal static List<string>? Literal(string text)
    {
        var t = text.Trim();
        if (t.Length < 2 || t[0] != '[' || t[^1] != ']') return null;
        var inner = t[1..^1];
        return inner.Length == 0 ? [] : SplitTop(inner).Select(Unescape).ToList();
    }

    private static string Unescape(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\\([0-9A-Fa-f]{2})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}
