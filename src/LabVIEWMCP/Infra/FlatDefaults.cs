using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A control's DEFAULT VALUE, read out of the flattened data LabVIEW stores with it.
///
/// WHY. lvai_describe_class reported each private data field's type and nothing about its
/// default, so a class created with `double.Gain=1` could only be confirmed by a test - measured
/// 2026-09-25 on the third TypedefAfterGDevCon build. The default is in the file all along: the
/// class's private data control carries one front-panel object whose `DefaultData` is the whole
/// private data cluster, flattened. Measured on that class: 34 bytes, 26 zeros for the `Config`
/// cluster (string length 4, enum 2, two doubles 16, int32 4) and `3FF0000000000000` - 1.0 - for
/// `Gain`. Big-endian, no padding, no length prefix around the cluster.
///
/// pylabview renders the bytes as MacRoman text with a byte that has no printable form written as
/// the LITERAL six characters `&amp;#xNN;` inside CDATA - the two encoding facts
/// scripts/pylv-decode-terminals.py already handles, mirrored here.
///
/// ONLY THE TYPES WHOSE FLAT LAYOUT IS SETTLED ARE DECODED: integers, floats, booleans, strings,
/// enums, clusters and typedefs of those. Anything else - a path, a refnum, an array, a timestamp -
/// STOPS the walk, because the bytes after it cannot be placed without knowing its length; the
/// fields before it keep their defaults and the rest are reported as not decoded, never guessed.
/// </summary>
internal static class FlatDefaults
{
    private static readonly Regex Entity = new("&#x([0-9A-Fa-f]{2});", RegexOptions.Compiled);

    private static readonly Encoding MacRoman = CreateMacRoman();

    private static Encoding CreateMacRoman()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(10000);
    }

    /// <summary>The payload of a pylabview `DefaultData` element as the bytes LabVIEW stored.</summary>
    internal static byte[] Bytes(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        var bytes = new List<byte>(text.Length);
        var at = 0;
        foreach (Match m in Entity.Matches(text))
        {
            if (m.Index > at) bytes.AddRange(MacRoman.GetBytes(text[at..m.Index]));
            bytes.Add(byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            at = m.Index + m.Length;
        }
        if (at < text.Length) bytes.AddRange(MacRoman.GetBytes(text[at..]));
        return [.. bytes];
    }

    /// <summary>A type the walk cannot place, by name - the reason it stopped.</summary>
    internal sealed class NotDecodable(string type) : Exception($"type '{type}' is not decoded")
    {
        public string Type { get; } = type;
    }

    /// <summary>
    /// One value of the descriptor at <paramref name="pos"/>, advancing it. Throws
    /// <see cref="NotDecodable"/> for a type whose flat length is not settled.
    /// </summary>
    internal static JsonNode? Read(XElement descriptor, List<XElement> flat, byte[] data, ref int pos,
                                   Func<List<XElement>, XElement, XElement> resolve,
                                   Func<XElement, XElement, string?> label)
    {
        var type = (string?)descriptor.Attribute("Type") ?? "";
        switch (type)
        {
            case "TypeDef":
                var inner = descriptor.Elements("TypeDesc").FirstOrDefault()
                            ?? throw new NotDecodable(type);
                return Read(resolve(flat, inner), flat, data, ref pos, resolve, label);

            case "Cluster":
                var cluster = new JsonObject();
                var index = 0;
                foreach (var child in descriptor.Elements("TypeDesc"))
                {
                    var member = resolve(flat, child);
                    var name = label(member, child) ?? $"field {index}";
                    cluster[Unique(cluster, name)] = Read(member, flat, data, ref pos, resolve, label);
                    index++;
                }
                return cluster;

            case "String":
                var length = BinaryPrimitives.ReadInt32BigEndian(Take(data, ref pos, 4));
                if (length < 0 || pos + length > data.Length) throw new NotDecodable(type);
                return MacRoman.GetString(Take(data, ref pos, length));

            case "Boolean": return Take(data, ref pos, 1)[0] != 0;
            case "BooleanU16": return BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref pos, 2)) != 0;

            case "NumInt8": return (sbyte)Take(data, ref pos, 1)[0];
            case "NumInt16": return BinaryPrimitives.ReadInt16BigEndian(Take(data, ref pos, 2));
            case "NumInt32": return BinaryPrimitives.ReadInt32BigEndian(Take(data, ref pos, 4));
            case "NumInt64": return BinaryPrimitives.ReadInt64BigEndian(Take(data, ref pos, 8));
            case "NumUInt8": return Take(data, ref pos, 1)[0];
            case "NumUInt16": return BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref pos, 2));
            case "NumUInt32": return BinaryPrimitives.ReadUInt32BigEndian(Take(data, ref pos, 4));
            case "NumUInt64": return BinaryPrimitives.ReadUInt64BigEndian(Take(data, ref pos, 8));
            case "NumFloat32": return BinaryPrimitives.ReadSingleBigEndian(Take(data, ref pos, 4));
            case "NumFloat64": return BinaryPrimitives.ReadDoubleBigEndian(Take(data, ref pos, 8));

            // An ENUM is its unsigned integer, reported with the item it names.
            case "UnitUInt8": return Enum(descriptor, Take(data, ref pos, 1)[0]);
            case "UnitUInt16": return Enum(descriptor, BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref pos, 2)));
            case "UnitUInt32": return Enum(descriptor, BinaryPrimitives.ReadUInt32BigEndian(Take(data, ref pos, 4)));

            default: throw new NotDecodable(type);
        }
    }

    private static JsonNode Enum(XElement descriptor, ulong value)
    {
        var items = descriptor.Elements("EnumLabel").Select(e => e.Value).ToList();
        return new JsonObject
        {
            ["value"] = value,
            ["item"] = value < (ulong)items.Count ? items[(int)value] : null,
        };
    }

    private static byte[] Take(byte[] data, ref int pos, int count)
    {
        if (count < 0 || pos + count > data.Length)
            throw new NotDecodable("(data shorter than its type)");
        var slice = data[pos..(pos + count)];
        pos += count;
        return slice;
    }

    private static string Unique(JsonObject o, string name)
    {
        var candidate = name;
        for (var n = 2; o.ContainsKey(candidate); n++) candidate = $"{name} ({n})";
        return candidate;
    }
}
