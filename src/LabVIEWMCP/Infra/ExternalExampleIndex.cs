using System.Buffers.Binary;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>One example registered through an external index file.</summary>
internal sealed record ExternalExample(
    string RelativePath, string Description, IReadOnlyList<string> Keywords);

/// <summary>
/// Reader for the Example Finder's external index files - `exbins\*.bin4`, and the older `*.bin3`
/// under &lt;LabVIEW&gt;\examples\exbins.
///
/// Why this exists: most examples carry their metadata inside the .vi (see <see cref="ExampleIndex"/>),
/// but some register here instead and carry NO in-VI block at all. NI-DAQmx is the important case -
/// 56 examples, none of them findable by scanning VIs. Leaving them out meant a query for "DAQmx"
/// returned nothing while 56 working examples sat on disk.
///
/// FORMAT, decoded 2026-08-07 and verified against all 18 index files on this station (13 distinct
/// .bin4 plus 4 .bin3 - `.bin3` and `.bin4` are the SAME layout). The file is a series of PARALLEL
/// ARRAYS, each introduced by a big-endian uint32 count, followed by that many records:
///
///   section 0  text   the bare file name, e.g. "Analog Input - Filtering.vi"
///   section 1  PTH0   the path RELATIVE TO THE examples FOLDER, e.g.
///                     DAQmx\Analog Input\Analog Input - Filtering.vi
///   section 2  text   empty in every file measured
///   section 3  nums   navigation node ids into the dtree category tree (see docs/example-corpus.md)
///   section 4  text   the description
///   section 5  text   the keyword vocabulary of this file
///   section 6  nums   per keyword, the indices of the examples carrying it - may be absent
///
/// A record is a uint32 length followed by that many bytes; a PTH0 record is the tag, a uint32
/// size, a uint16 type, a uint16 component count and then ONE-BYTE-prefixed components. Note the
/// two different prefix widths in one file - reading the 4-byte lengths as single bytes is what
/// made this look like the .mnu convention at first glance.
///
/// Sections 0, 1 and 4 are index-aligned, which is what makes the pairing safe rather than guessed:
/// across all 18 files every name equals the last component of its path, all 451 rows resolve to a
/// file that exists on disk, and the descriptions match their example's subject.
///
/// A file that does not fit this shape yields NOTHING and is reported by name, never a partial or
/// mis-paired read: attaching the wrong description to a real example is worse than omitting it.
/// </summary>
internal static class ExternalExampleIndex
{
    private static readonly byte[] Pth0 = "PTH0"u8.ToArray();

    private enum Kind { Text, Path, Numbers }

    /// <summary>
    /// The examples one index file registers, or an empty list when it does not fit the format.
    /// Paths are relative to the examples folder the file's `exbins` directory sits in.
    /// </summary>
    public static IReadOnlyList<ExternalExample> Read(byte[] bytes)
    {
        var sections = new List<(Kind Kind, List<string> Text, List<int[]> Numbers)>();
        var offset = 0;

        while (offset + 4 <= bytes.Length && sections.Count < 8)
        {
            if (!TryReadSection(bytes, ref offset, out var section)) break;
            sections.Add(section);
        }

        // Names, paths and descriptions must all be there and agree, or this is not the format.
        if (sections.Count < 5) return [];
        if (sections[1].Kind != Kind.Path) return [];
        if (sections[0].Kind != Kind.Text || sections[4].Kind != Kind.Text) return [];

        var names = sections[0].Text;
        var paths = sections[1].Text;
        var descriptions = sections[4].Text;
        if (paths.Count != names.Count || descriptions.Count != names.Count) return [];

        // The alignment check that makes the pairing a measurement rather than an assumption.
        for (var i = 0; i < names.Count; i++)
            if (!paths[i].EndsWith(names[i], StringComparison.OrdinalIgnoreCase))
                return [];

        var keywords = Keywords(sections, names.Count);

        var examples = new List<ExternalExample>(names.Count);
        for (var i = 0; i < names.Count; i++)
            examples.Add(new ExternalExample(
                paths[i], Collapse(descriptions[i]),
                keywords.TryGetValue(i, out var list) ? list : []));

        return examples;
    }

    /// <summary>
    /// Sections 5 and 6 as an inverted index: section 5 is the vocabulary, section 6 lists per
    /// keyword the example indices carrying it. Both are optional - lvexdfd.bin4 stops at 6
    /// sections - so their absence is normal, not a parse failure.
    /// </summary>
    private static Dictionary<int, List<string>> Keywords(
        List<(Kind Kind, List<string> Text, List<int[]> Numbers)> sections, int exampleCount)
    {
        var byExample = new Dictionary<int, List<string>>();
        if (sections.Count < 7) return byExample;
        if (sections[5].Kind != Kind.Text || sections[6].Kind != Kind.Numbers) return byExample;
        if (sections[5].Text.Count != sections[6].Numbers.Count) return byExample;

        for (var k = 0; k < sections[5].Text.Count; k++)
        {
            var keyword = sections[5].Text[k].Trim();
            if (keyword.Length == 0) continue;

            foreach (var index in sections[6].Numbers[k])
            {
                if (index < 0 || index >= exampleCount) continue;
                if (!byExample.TryGetValue(index, out var list))
                    byExample[index] = list = [];
                if (!list.Contains(keyword, StringComparer.OrdinalIgnoreCase)) list.Add(keyword);
            }
        }
        return byExample;
    }

    private static bool TryReadSection(
        byte[] bytes, ref int offset,
        out (Kind Kind, List<string> Text, List<int[]> Numbers) section)
    {
        section = (Kind.Text, [], []);

        var count = ReadUInt32(bytes, offset);
        if (count is 0 or > 20000) return false;
        var cursor = offset + 4;

        var kind = DetectKind(bytes, cursor);
        var text = new List<string>();
        var numbers = new List<int[]>();

        for (var i = 0; i < count; i++)
        {
            switch (kind)
            {
                case Kind.Path:
                    if (!TryReadPath(bytes, ref cursor, out var path)) return false;
                    text.Add(path);
                    break;

                case Kind.Text:
                    if (cursor + 4 > bytes.Length) return false;
                    var length = (int)ReadUInt32(bytes, cursor);
                    cursor += 4;
                    if (length < 0 || length > bytes.Length - cursor) return false;
                    text.Add(Encoding.Latin1.GetString(bytes, cursor, length));
                    cursor += length;
                    break;

                default:
                    if (cursor + 4 > bytes.Length) return false;
                    var n = (int)ReadUInt32(bytes, cursor);
                    cursor += 4;
                    if (n < 0 || n > 64 || cursor + 4 * n > bytes.Length) return false;
                    var values = new int[n];
                    for (var v = 0; v < n; v++) values[v] = (int)ReadUInt32(bytes, cursor + 4 * v);
                    cursor += 4 * n;
                    numbers.Add(values);
                    break;
            }
        }

        offset = cursor;
        section = (kind, text, numbers);
        return true;
    }

    /// <summary>
    /// What the records of a section are, decided from the first one. A PTH0 announces itself; a
    /// string record is a length followed by that many mostly-printable bytes; anything else is a
    /// list of numbers. Section 3's first record is a count of 2 followed by two large integers,
    /// whose high bytes are zero - that is what the printability test separates.
    /// </summary>
    private static Kind DetectKind(byte[] bytes, int offset)
    {
        if (offset + 4 <= bytes.Length && bytes.AsSpan(offset, 4).SequenceEqual(Pth0))
            return Kind.Path;
        if (offset + 4 > bytes.Length) return Kind.Numbers;

        var length = ReadUInt32(bytes, offset);
        if (length == 0) return Kind.Text;                       // an empty string is still text
        if (length > bytes.Length - offset - 4 || length > 100000) return Kind.Numbers;

        var sample = Math.Min((int)length, 40);
        var printable = 0;
        for (var i = 0; i < sample; i++)
        {
            var b = bytes[offset + 4 + i];
            if (b is >= 9 and <= 13 or >= 32 and < 127 or >= 160) printable++;
        }
        return printable >= sample * 0.9 ? Kind.Text : Kind.Numbers;
    }

    private static bool TryReadPath(byte[] bytes, ref int offset, out string path)
    {
        path = "";
        if (offset + 12 > bytes.Length) return false;

        var size = (int)ReadUInt32(bytes, offset + 4);
        if (size < 0 || size > bytes.Length - offset - 8) return false;

        var end = offset + 8 + size;
        var components = (bytes[offset + 10] << 8) | bytes[offset + 11];
        var cursor = offset + 12;

        var parts = new List<string>(components);
        for (var i = 0; i < components; i++)
        {
            if (cursor >= bytes.Length) return false;
            int length = bytes[cursor];
            if (cursor + 1 + length > bytes.Length) return false;
            parts.Add(Encoding.Latin1.GetString(bytes, cursor + 1, length));
            cursor += 1 + length;
        }

        offset = end;
        path = string.Join('\\', parts);
        return true;
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    /// <summary>
    /// One line of running text. These descriptions carry newlines, tabs AND markup - the dfdt
    /// examples say "in the &lt;b&gt;RT CompactRIO Target&lt;/b&gt; folder" - so they are stripped
    /// the same way the in-VI descriptions are, or the two sources would read differently.
    /// </summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        var inTag = false;

        foreach (var c in text)
        {
            if (c == '<') { inTag = true; continue; }
            if (inTag) { if (c == '>') inTag = false; continue; }

            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
