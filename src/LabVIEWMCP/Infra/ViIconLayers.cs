// ============================================================================================
// ViIconLayers.cs - reads a .vi's resource file offline and reports whether its icon still
// carries the icon editor's layer data, or has been flattened to pixels.
// ============================================================================================
using System.Buffers.Binary;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Whether a VI's icon still has LAYERS - the template layer <c>ApplyIconToVIs</c> writes and the
/// VI's own layer under it - read straight from the <c>.vi</c> file, no LabVIEW involved.
///
/// WHY. <c>lvai_set_vi_icon</c> FLATTENS an icon: the icon editor's layer data is gone and the next
/// hand edit starts from pixels. Measured 2026-09-25 in a pilot project: <c>ApplyIconToVIs</c> plus
/// a member save took the VI's <c>VITS</c> resource from 157 to 6 791 bytes - an <c>NI_IconEditor</c>
/// tag holding an <c>NI_Library</c> layer - and a later <c>lvai_set_vi_icon</c> on the same member
/// took it back to 157. On a production application's hand-made icons the flattening changed
/// 27-36 % of every icon's pixels and dropped the editable text items. So "does this VI still have
/// layers?" is the question <c>keepOwnLayer</c> exists to protect, and the file answers it.
///
/// WHAT IS READ, AND HOW SURE IT IS. The block table of the LabVIEW resource file (<c>RSRC</c>),
/// then the <c>VITS</c> sections, which hold tagged data uncompressed. The markers are plain ASCII
/// inside them: <c>NI_IconEditor</c> (the tag), one <c>Layer.lvclass</c> per layer (the flattened
/// layer objects), <c>NI_Library</c> (the template layer's name). That reading comes from an offline
/// decoder run over some 4 000 VIs of a production application with no decode errors, and the
/// 157-byte flat state from the measurement above. It is a PROBE, not a parser: it does not decode a
/// layer's pixels, and an answer of "no layer data" on a format it does not know is possible - which
/// is why the tools report it beside their verdict instead of deciding on it.
/// </summary>
internal static class ViIconLayers
{
    /// <summary>What the probe found. <c>Error</c> is set, and everything else empty, when it could not read.</summary>
    internal sealed record Probe(int VitsBytes, bool HasIconEditorData, int LayerCount,
                                 bool HasLibraryLayer, string? Error = null);

    internal static Probe Read(string viPath)
    {
        try
        {
            var vits = Blocks(File.ReadAllBytes(viPath), "VITS");
            var bytes = vits.Sum(s => s.Length);
            var text = Encoding.Latin1.GetString([.. vits.SelectMany(s => s)]);
            return new Probe(bytes, text.Contains("NI_IconEditor", StringComparison.Ordinal),
                Count(text, "Layer.lvclass"), text.Contains("NI_Library", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or InvalidDataException or ArgumentException
                                      or OverflowException)
        {
            return new Probe(0, false, 0, false, e.Message);
        }
    }

    private static int Count(string text, string marker)
    {
        var count = 0;
        for (var at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// The raw sections of one resource type. Layout: the header names the info and data offsets
    /// (bytes 16..31); the info block's own header names the block table; each table entry is a
    /// four-character type, a section count minus one and an offset to that many 20-byte section
    /// records, whose last field is the section's offset into the data area, where a 4-byte length
    /// precedes the bytes.
    /// </summary>
    internal static IReadOnlyList<byte[]> Blocks(byte[] file, string type)
    {
        if (file.Length < 32 || Encoding.ASCII.GetString(file, 0, 6) != "RSRC\r\n")
            throw new InvalidDataException("not a LabVIEW resource file (no RSRC header)");

        int U32(int at) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at, 4)));

        var infoOffset = U32(16);
        var dataOffset = U32(24);
        var tableOffset = infoOffset + U32(infoOffset + 32 + 12);
        var types = U32(tableOffset) + 1;

        var sections = new List<byte[]>();
        for (var i = 0; i < types; i++)
        {
            var entry = tableOffset + 4 + i * 12;
            if (Encoding.Latin1.GetString(file, entry, 4) != type) continue;

            var count = U32(entry + 4) + 1;
            var listOffset = U32(entry + 8);
            for (var s = 0; s < count; s++)
            {
                var record = tableOffset + listOffset + s * 20;
                var at = dataOffset + U32(record + 12);
                var size = U32(at);
                sections.Add(file.AsSpan(at + 4, size).ToArray());
            }
        }
        return sections;
    }
}
