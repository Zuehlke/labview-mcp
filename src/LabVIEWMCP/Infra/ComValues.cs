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
        if (text.TrimStart().StartsWith('<') && (ArrayShape(type) is not null || Braces(type) is { Head: "cluster" }))
            return FromLabviewXml(text, type, out problem);

        // A 2D (or nD) array - `array.2{string}` as the reader writes it - crosses as ONE
        // rectangular .NET array, T[,]. It was refused until 2026-10-15, and the DQMH ATM build's
        // Find Account.vi, which takes the accounts table, could only be run on the grpc route.
        if (ArrayShape(type) is { Rank: > 1 } grid)
            return GridFromText(text, grid.Rank, grid.Element, out problem);

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
    /// An array type's rank and element type: `array{T}` is 1, `array.2{T}` - the reader's spelling
    /// of a 2D array - is 2, and a nested `array{array{T}}` counts each level. An element's label
    /// (`array{string.String}`) is dropped. Null for anything that is not an array.
    /// </summary>
    internal static (int Rank, string Element)? ArrayShape(string type)
    {
        if (Braces(type.Trim()) is not { } parts) return null;
        int rank;
        if (parts.Head == "array") rank = 1;
        else if (parts.Head.StartsWith("array.", StringComparison.Ordinal)
                 && int.TryParse(parts.Head["array.".Length..], NumberStyles.None, Invariant, out var r) && r >= 1)
            rank = r;
        else return null;
        var element = FieldType(parts.Body.Trim());
        return ArrayShape(element) is { } inner ? (rank + inner.Rank, inner.Element) : (rank, element);
    }

    /// <summary>The .NET element type a rectangular array of <paramref name="element"/> is built of, or null.</summary>
    private static Type? GridElementType(string element) => element switch
    {
        "string" or "path" => typeof(string),
        "bool" => typeof(bool),
        _ when Floats.Contains(element) => typeof(double),
        _ when Integers.ContainsKey(element) => typeof(int),
        _ => null,
    };

    /// <summary>
    /// Values in row-major order plus the size of each dimension -> a rectangular T[,...] of
    /// <paramref name="element"/>, or null with <paramref name="problem"/>.
    /// </summary>
    private static Array? Grid(IReadOnlyList<object?> flat, int[] dims, string element, out string? problem)
    {
        problem = null;
        if (GridElementType(element) is not { } clr)
            return (Array?)Refuse(out problem, $"a {dims.Length}D array of {element} cannot be set over COM by this server yet");
        var grid = Array.CreateInstance(clr, dims);
        var index = new int[dims.Length];
        foreach (var value in flat)
        {
            grid.SetValue(clr == typeof(double) ? Convert.ToDouble(value, Invariant)
                          : clr == typeof(int) ? Convert.ToInt32(value, Invariant) : value, index);
            for (var d = dims.Length - 1; d >= 0; d--)
            {
                if (++index[d] < dims[d]) break;
                index[d] = 0;
            }
        }
        return grid;
    }

    /// <summary>`[[a,b],[c,d]]` for a rank-<paramref name="rank"/> array: nested literals, rectangular.</summary>
    private static object? GridFromText(string text, int rank, string element, out string? problem)
    {
        problem = null;
        var dims = new int[rank];
        var seen = new bool[rank];
        var flat = new List<object?>();
        string? Walk(string literal, int depth)
        {
            if (Literal(literal) is not { } items)
                return $"'{literal}' is not an array literal - a {rank}D array is written [[1,2],[3,4]]";
            // the FIRST list at each depth sets that dimension; every other one must match it
            if (!seen[depth]) { seen[depth] = true; dims[depth] = items.Count; }
            else if (items.Count != dims[depth])
                return $"the array is not rectangular: {items.Count} elements where {dims[depth]} were expected";
            foreach (var item in items)
            {
                if (depth < rank - 1)
                {
                    if (Walk(item, depth + 1) is { } why) return why;
                }
                else
                {
                    flat.Add(FromText(item, element, out var p));
                    if (p is not null) return $"element '{item}': {p}";
                }
            }
            return null;
        }

        if (Walk(text, 0) is { } refusal) return Refuse(out problem, refusal);
        return Grid(flat, dims, element, out problem);
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
        // LabVIEW writes an nD array as ONE <Array> with a Dimsize per dimension, then every
        // element in row-major order - the shape the gRPC route's answers carry for Find Account's
        // accounts table
        if (ArrayShape(type) is { Rank: > 1 } shape)
        {
            if (element.Name.LocalName != "Array")
                return Refuse(out problem, $"<{element.Name.LocalName}> given for {type}");
            var sizes = element.Elements("Dimsize").Select(d => int.TryParse(d.Value, out var n) ? n : -1).ToArray();
            if (sizes.Length != shape.Rank || sizes.Any(n => n < 0))
                return Refuse(out problem, $"a {shape.Rank}D array needs {shape.Rank} <Dimsize> elements, the XML gives {sizes.Length}");
            var count = sizes.Aggregate(1, (a, n) => a * n);
            var flat = new List<object?>();
            foreach (var child in element.Elements()
                         .Where(e => e.Name.LocalName is not ("Name" or "Dimsize")).Take(count))
            {
                flat.Add(FromXmlElement(child, shape.Element, out problem));
                if (problem is not null) return Refuse(out problem, $"array element: {problem}");
            }
            if (flat.Count != count)
                return Refuse(out problem, $"the Dimsizes give {count} elements and the XML holds {flat.Count}");
            return Grid(flat, sizes, shape.Element, out problem);
        }
        if (Braces(type) is { Head: "array" } array)
        {
            if (element.Name.LocalName != "Array")
                return Refuse(out problem, $"<{element.Name.LocalName}> given for {type}");
            var elementType = FieldType(array.Body.Trim());
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

        if (ArrayShape(type) is { Rank: > 1 } shape && value is Array grid && grid.Rank == shape.Rank)
        {
            // a Dimsize per dimension, then the elements row-major - which is .NET's enumeration
            // order of a rectangular array - unnamed, as LabVIEW writes an array's elements
            var element = new System.Xml.Linq.XElement("Array", new System.Xml.Linq.XElement("Name", name));
            for (var d = 0; d < grid.Rank; d++)
                element.Add(new System.Xml.Linq.XElement("Dimsize", grid.GetLength(d)));
            if (grid.Length == 0)
            {
                if (ToLabviewXml("", shape.Element, null) is not { } template) return null;
                template.Element("Val")?.SetValue("");
                element.Add(template);
            }
            foreach (var item in grid)
            {
                if (ToLabviewXml("", shape.Element, item) is not { } child) return null;
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

    /// <summary>
    /// `[a,b,[c,d]]` -> its top-level items; `[]` -> none; anything else -> null.
    ///
    /// AN ITEM IN DOUBLE QUOTES IS TAKEN VERBATIM - `["1,2\r\n3,4","5,6"]` is two strings. AIXML
    /// writes a comma inside a value as `\2C`, which still works, but a caller passing strings
    /// naturally quotes them, and the quotes were not structure: the commas inside split the item,
    /// and Parse CSV Pieces.vi was run on the wrong pieces with no problem named (the fourth CSV
    /// reader cold build, 2026-10-27). A quote counts only where an item STARTS and where the
    /// item ENDS - followed by a comma, a closing bracket or the end - so `5" pipe` stays a string
    /// with a quote in it.
    /// </summary>
    internal static List<string>? Literal(string text)
    {
        var t = text.Trim();
        if (t.Length < 2 || t[0] != '[' || t[^1] != ']') return null;
        var inner = t[1..^1];
        return inner.Length == 0 ? [] : SplitItems(inner).Select(i => i.Quoted ? i.Text : Unescape(i.Text)).ToList();
    }

    /// <summary>
    /// Top-level items of a literal's inside, quote-aware (see <see cref="Literal"/>): a quoted
    /// item comes back without its quotes and flagged, and a quoted element of a NESTED list is
    /// skipped whole, so a comma or bracket inside it is not structure at this level either.
    /// </summary>
    internal static List<(string Text, bool Quoted)> SplitItems(string text)
    {
        var items = new List<(string, bool)>();
        var i = 0;
        while (true)
        {
            var k = i;
            while (k < text.Length && text[k] == ' ') k++;
            if (k < text.Length && text[k] == '"' && QuoteEnd(text, k, nested: false) is { } close)
            {
                items.Add((text[(k + 1)..close], true));
                var after = close + 1;
                while (after < text.Length && text[after] == ' ') after++;
                if (after >= text.Length) return items;
                i = after + 1;          // past the comma QuoteEnd found
                continue;
            }

            var depth = 0;
            var j = i;
            var atElement = false;      // just after '[', '{' or a nested ',' - where a quote may open
            for (; j < text.Length; j++)
            {
                var c = text[j];
                if (atElement && c == ' ') continue;
                if (atElement && c == '"' && depth > 0 && QuoteEnd(text, j, nested: true) is { } end)
                {
                    j = end;
                    atElement = false;
                    continue;
                }
                atElement = false;
                if (c is '[' or '{') { depth++; atElement = true; }
                else if (c is ']' or '}') depth--;
                else if (c == ',' && depth == 0) break;
                else if (c == ',') atElement = true;
            }
            items.Add((text[i..j], false));
            if (j >= text.Length) return items;
            i = j + 1;
        }
    }

    /// <summary>
    /// The index of the quote closing the one at <paramref name="open"/>: the first later quote
    /// followed, past spaces, by a comma or the end - or, inside a nested list, a closing bracket.
    /// Null when there is none, and the quote is then an ordinary character.
    /// </summary>
    private static int? QuoteEnd(string text, int open, bool nested)
    {
        for (var q = text.IndexOf('"', open + 1); q >= 0; q = text.IndexOf('"', q + 1))
        {
            var a = q + 1;
            while (a < text.Length && text[a] == ' ') a++;
            if (a >= text.Length || text[a] == ',' || (nested && text[a] is ']' or '}')) return q;
        }
        return null;
    }

    private static string Unescape(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\\([0-9A-Fa-f]{2})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}
