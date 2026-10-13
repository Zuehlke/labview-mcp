using System.Buffers.Binary;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The block types a LabVIEW resource file lists in its OWN resource map - the map pylabview's
/// readRSRCBlockInfo reads: the header chain followed to the last RSRC header, the block-info list
/// header after it, then one 12-byte header per block type (big-endian throughout).
///
/// WHY NOT A BYTE SEARCH. Asking a VI "do you carry VICD?" by searching its bytes for the four
/// letters answered yes for every VI that CALLS A MALLEABLE VI: each .vim instance is embedded in
/// the caller as a complete VI (a VINS section) with its own resource map, VICD and TM80 included.
/// Measured 2026-10-13 on an LUnit test method - `Pass If Equal.vim` twice, two VICD hits, both
/// inside VINS - which pylv_apply's strip step then could not remove, because the caller had none.
/// </summary>
internal static class RsrcMap
{
    private const int HeaderSize = 32;

    /// <summary>The top-level block types, or null when the bytes are not a resource file this
    /// reader understands - the caller then decides how cautious to be.</summary>
    public static IReadOnlyList<string>? BlockTypes(ReadOnlySpan<byte> file)
    {
        try
        {
            if (file.Length < HeaderSize || !file[..4].SequenceEqual("RSRC"u8)) return null;

            // Every RSRC header points at the next; the last points at itself.
            long current = -1, next = 0;
            for (var guard = 0; current != next; guard++)
            {
                if (guard > 16 || next + HeaderSize > file.Length) return null;
                current = next;
                var offset = BinaryPrimitives.ReadUInt32BigEndian(file[((int)current + 16)..]);
                if (offset < current) return null;
                next = offset;
            }

            var infoBase = (int)current;
            var listHeader = infoBase + HeaderSize;
            if (listHeader + 20 > file.Length) return null;
            var blockInfo = infoBase + (int)BinaryPrimitives.ReadUInt32BigEndian(file[(listHeader + 12)..]);
            if (blockInfo + 4 > file.Length) return null;
            var count = BinaryPrimitives.ReadUInt32BigEndian(file[blockInfo..]) + 1;
            if (count > 4096 || blockInfo + 4 + count * 12 > file.Length) return null;

            var types = new List<string>((int)count);
            for (var i = 0; i < count; i++)
                types.Add(Encoding.ASCII.GetString(file.Slice(blockInfo + 4 + i * 12, 4)));
            return types;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Whether the file's own map lists <paramref name="type"/>. A file the reader cannot
    /// parse falls back to the byte search, so an unreadable file is never called clean.</summary>
    public static bool Lists(ReadOnlySpan<byte> file, string type) =>
        BlockTypes(file) is { } types
            ? types.Contains(type)
            : file.IndexOf(Encoding.ASCII.GetBytes(type)) >= 0;
}
