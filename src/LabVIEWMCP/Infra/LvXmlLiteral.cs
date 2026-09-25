using System.Globalization;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// An AIXML value literal of an ARRAY or CLUSTER type - <c>[[1,Ada],[2,Alan]]</c> against
/// <c>array.2{string}</c>, <c>[true,5000,x]</c> against the error cluster - turned into the XML
/// LabVIEW's own Flatten To XML writes for that value, wrapped in <c>LvVariant</c>.
///
/// WHY. A helper VI can set a compound value only through Unflatten From XML with a VARIANT as
/// its type, which yields a variant carrying the value's own type - measured 2026-09-25 on a 2D
/// string array and an error cluster. The literal is what a caller already has: the generators
/// author it, and lvai_generate_test lists every expectation in that form under
/// <c>expectedConstants</c>, so a negative control on an array expectation is one edit of the text
/// it was handed. The TYPE comes from the constant's own export, so nothing is guessed.
///
/// Scalars inside are string, bool and the numeric types. An enum, a path, a refnum, a variant or
/// a timestamp inside a compound is refused by name: each has an XML shape this has not been
/// measured against, and a guessed one would write a wrong value in silence.
///
/// The literal grammar is AIXML's: a compound is <c>[</c>…<c>]</c> with comma-separated members,
/// and a comma inside a string cannot be written - AIXML never escapes one.
/// </summary>
internal static class LvXmlLiteral
{
    internal abstract record Shape;
    internal sealed record Scalar(string Type) : Shape;
    internal sealed record ArrayOf(int Rank, Shape Element) : Shape;
    internal sealed record ClusterOf(IReadOnlyList<(string Name, Shape Shape)> Fields) : Shape;

    private static readonly Dictionary<string, string> Elements = new(StringComparer.Ordinal)
    {
        ["string"] = "String", ["bool"] = "Boolean",
        ["int8"] = "I8", ["int16"] = "I16", ["int32"] = "I32", ["int64"] = "I64",
        ["uint8"] = "U8", ["uint16"] = "U16", ["uint32"] = "U32", ["uint64"] = "U64",
        ["single"] = "SGL", ["float"] = "SGL", ["double"] = "DBL", ["extended"] = "EXT",
    };

    /// <summary>Whether an AIXML type is an array or a cluster at its top level.</summary>
    internal static bool IsCompound(string type)
    {
        var t = type.Trim();
        return t.StartsWith("array", StringComparison.Ordinal) || t.StartsWith("cluster{", StringComparison.Ordinal);
    }

    /// <summary>The one-line LvVariant XML for the literal, or null and the reason.</summary>
    internal static (string? Xml, string? Why) Build(string type, string literal)
    {
        try
        {
            var shape = ParseType(type.Trim());
            var value = ParseLiteral(literal.Trim());
            var element = Emit(shape, value, "");
            var variant = new XElement("LvVariant", new XElement("Name", "Variant"), element);
            return (variant.ToString(SaveOptions.DisableFormatting), null);
        }
        catch (FormatException e) { return (null, e.Message); }
    }

    /// <summary>Whether two literals of one type hold the same value - numbers compared as numbers.</summary>
    internal static bool Same(string type, string a, string b)
    {
        try
        {
            return Equal(ParseType(type.Trim()), ParseLiteral(a.Trim()), ParseLiteral(b.Trim()));
        }
        catch (FormatException) { return false; }
    }

    // ------------------------------------------------------------------ the type

    internal static Shape ParseType(string t)
    {
        if (t.StartsWith("array", StringComparison.Ordinal))
        {
            var brace = t.IndexOf('{');
            if (brace < 0 || !t.EndsWith('}')) throw new FormatException($"`{t}` is not an array type.");
            var head = t[..brace];
            var rank = head == "array" ? 1
                : head.StartsWith("array.", StringComparison.Ordinal) &&
                  int.TryParse(head[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0 ? r
                : throw new FormatException($"`{head}` is not an array rank.");
            return new ArrayOf(rank, ParseType(t[(brace + 1)..^1]));
        }
        if (t.StartsWith("cluster{", StringComparison.Ordinal) && t.EndsWith('}'))
        {
            var fields = new List<(string, Shape)>();
            foreach (var field in SplitTopLevel(t[8..^1], '{', '}'))
            {
                // `<type>.<name>`: the type ends at its closing brace when it has one, at the first
                // dot otherwise - a field NAME may itself contain dots and spaces.
                var closing = field.StartsWith("array", StringComparison.Ordinal) ||
                              field.StartsWith("cluster{", StringComparison.Ordinal)
                    ? MatchingBrace(field) : -1;
                var dot = closing >= 0 ? closing + 1 : field.IndexOf('.');
                if (dot <= 0 || dot >= field.Length || field[dot] != '.')
                    throw new FormatException($"Cluster field `{field}` has no `.name`.");
                fields.Add((field[(dot + 1)..], ParseType(field[..dot])));
            }
            return new ClusterOf(fields);
        }
        if (Elements.ContainsKey(t)) return new Scalar(t);
        throw new FormatException(
            $"`{t}` inside an array or cluster is not settable - only string, bool and numeric " +
            "members are. An enum, path, refnum, variant or timestamp has an XML shape nobody has " +
            "measured here, and a guess would write a wrong value in silence.");
    }

    private static int MatchingBrace(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        throw new FormatException($"`{s}` has an unbalanced brace.");
    }

    private static List<string> SplitTopLevel(string s, char open, char close)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == open) depth++;
            else if (s[i] == close) depth--;
            else if (s[i] == ',' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    // ------------------------------------------------------------------ the literal

    /// <summary>A literal as a tree: a list for <c>[…]</c>, a string for a scalar.</summary>
    internal static object ParseLiteral(string s)
    {
        if (!s.StartsWith('[')) return s;
        if (!s.EndsWith(']')) throw new FormatException($"`{s}` opens a bracket it does not close.");
        var inner = s[1..^1];
        if (inner.Length == 0) return new List<object>();
        return SplitTopLevel(inner, '[', ']').Select(p => ParseLiteral(p.Trim())).ToList();
    }

    // ------------------------------------------------------------------ the XML

    private static XElement Emit(Shape shape, object value, string name) => shape switch
    {
        Scalar s => new XElement(Elements[s.Type], new XElement("Name", name),
                                 new XElement("Val", ScalarText(s, value))),
        ClusterOf c => EmitCluster(c, value, name),
        ArrayOf a => EmitArray(a, value, name),
        _ => throw new FormatException("unknown shape"),
    };

    private static string ScalarText(Scalar s, object value)
    {
        if (value is not string text)
            throw new FormatException($"A `{s.Type}` member was given a bracketed value.");
        if (s.Type == "string") return text;
        if (s.Type == "bool")
            return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" ? "1"
                 : text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" ? "0"
                 : throw new FormatException($"'{text}' is not a boolean.");
        if (text.Length == 0) return "0";
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? text
            : throw new FormatException($"'{text}' is not a number, and the member is `{s.Type}`.");
    }

    private static XElement EmitCluster(ClusterOf c, object value, string name)
    {
        var members = value as List<object>
            ?? throw new FormatException("A cluster needs a bracketed value, [a,b,…].");
        if (members.Count != c.Fields.Count)
            throw new FormatException(
                $"The cluster has {c.Fields.Count} fields and the value gives {members.Count}.");
        var element = new XElement("Cluster", new XElement("Name", name),
                                   new XElement("NumElts", c.Fields.Count));
        for (var i = 0; i < c.Fields.Count; i++)
            element.Add(Emit(c.Fields[i].Shape, members[i], c.Fields[i].Name));
        return element;
    }

    private static XElement EmitArray(ArrayOf a, object value, string name)
    {
        var dims = new List<int>();
        var leaves = new List<object>();
        Flatten(value, a.Rank, dims, 0, leaves);
        var element = new XElement("Array", new XElement("Name", name));
        foreach (var d in dims) element.Add(new XElement("Dimsize", d));
        if (leaves.Count == 0)
            element.Add(Template(a.Element));   // LabVIEW writes ONE element as a type template
        else
            foreach (var leaf in leaves) element.Add(Emit(a.Element, leaf, ""));
        return element;
    }

    /// <summary>Row-major leaves of an N-D literal, with each dimension's size; ragged is refused.</summary>
    private static void Flatten(object value, int rank, List<int> dims, int depth, List<object> leaves)
    {
        if (depth == rank) { leaves.Add(value); return; }
        var list = value as List<object>
            ?? throw new FormatException($"The array literal is not {rank}-dimensional.");
        if (dims.Count == depth) dims.Add(list.Count);
        else if (dims[depth] != list.Count)
            throw new FormatException("The array literal is ragged - every row must be the same length.");
        foreach (var item in list) Flatten(item, rank, dims, depth + 1, leaves);
        // an empty outer dimension leaves the inner sizes unknown; LabVIEW writes them as 0
        for (var d = dims.Count; d < rank && list.Count == 0; d++) dims.Add(0);
    }

    private static XElement Template(Shape shape) => shape switch
    {
        Scalar s => new XElement(Elements[s.Type], new XElement("Name", ""), new XElement("Val", "")),
        ClusterOf c => new XElement("Cluster", new XElement("Name", ""), new XElement("NumElts", c.Fields.Count),
                                    c.Fields.Select(f => Rename(Template(f.Shape), f.Name))),
        ArrayOf a => new XElement("Array", new XElement("Name", ""),
                                  Enumerable.Range(0, a.Rank).Select(_ => new XElement("Dimsize", 0)),
                                  Template(a.Element)),
        _ => throw new FormatException("unknown shape"),
    };

    private static XElement Rename(XElement e, string name)
    {
        e.Element("Name")!.Value = name;
        return e;
    }

    // ------------------------------------------------------------------ comparison

    private static bool Equal(Shape shape, object a, object b) => shape switch
    {
        Scalar s when a is string x && b is string y => s.Type switch
        {
            "string" => x == y,
            "bool" => Truthy(x) == Truthy(y),
            _ => double.TryParse(x.Length == 0 ? "0" : x, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) &&
                 double.TryParse(y.Length == 0 ? "0" : y, NumberStyles.Float, CultureInfo.InvariantCulture, out var q) &&
                 p.Equals(q),
        },
        ClusterOf c when a is List<object> x && b is List<object> y =>
            x.Count == c.Fields.Count && y.Count == c.Fields.Count &&
            c.Fields.Select((f, i) => Equal(f.Shape, x[i], y[i])).All(v => v),
        ArrayOf r when a is List<object> x && b is List<object> y =>
            x.Count == y.Count &&
            x.Zip(y).All(p => Equal(r.Rank == 1 ? r.Element : r with { Rank = r.Rank - 1 }, p.First, p.Second)),
        _ => false,
    };

    private static bool Truthy(string s) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
}
