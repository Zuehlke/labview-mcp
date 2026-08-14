using System.Buffers.Binary;
using System.IO.Compression;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Draws a 32x32 LabVIEW icon and encodes it as a PNG, with no imaging dependency.
///
/// This exists because of a measurement, not for elegance. Profiling one whole VI generation
/// (41 tool calls, 455 s) showed the icon costing 21.1 s, of which only 8.6 s was
/// lvai_set_vi_icon actually reaching VI Server - the other 12.5 s was an agent composing and
/// running a PowerShell call that drew the bitmap through System.Drawing. Producing the image
/// server-side removes that step and one whole tool call with it, and a tool call is worth about
/// 11 s of wall clock in a generation session. Numbers in docs/aixml-reference.md.
///
/// Dependency-free is deliberate. System.Drawing.Common is Windows-only and a package; a PNG
/// encoder for one fixed size is about eighty lines, and IconTools already reads PNG headers by
/// hand for exactly the same reason. Text is drawn from an embedded 5x7 bitmap font rather than
/// a system typeface: at 32 px an anti-aliased TrueType glyph turns to mush, and a bitmap font
/// is what LabVIEW icons have always used.
/// </summary>
internal static class IconImage
{
    internal const int Size = 32;

    /// <summary>5 px glyph + 1 px gap inside a 30 px writable width: 6n-1 &lt;= 30.</summary>
    internal const int MaxCharsPerLine = 5;

    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;

    /// <summary>
    /// The character order of <see cref="GlyphRows"/>. Uppercase only - a 5x7 cell has no room
    /// for descenders, so lowercase input is upper-cased rather than rendered badly.
    /// </summary>
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -./:+#";

    /// <summary>
    /// Seven rows per glyph, in <see cref="Alphabet"/> order. Bit 4 (0b10000) is the leftmost
    /// pixel, so a row reads left to right as written.
    /// </summary>
    private static readonly byte[] GlyphRows =
    [
        0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11,   // A
        0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E,   // B
        0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E,   // C
        0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E,   // D
        0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F,   // E
        0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10,   // F
        0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0E,   // G
        0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11,   // H
        0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F,   // I
        0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C,   // J
        0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11,   // K
        0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F,   // L
        0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11,   // M
        0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11,   // N
        0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E,   // O
        0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10,   // P
        0x0E, 0x11, 0x11, 0x11, 0x15, 0x13, 0x0D,   // Q
        0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11,   // R
        0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E,   // S
        0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,   // T
        0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E,   // U
        0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04,   // V
        0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11,   // W
        0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11,   // X
        0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04,   // Y
        0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F,   // Z
        0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E,   // 0
        0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E,   // 1
        0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F,   // 2
        0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E,   // 3
        0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02,   // 4
        0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E,   // 5
        0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E,   // 6
        0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08,   // 7
        0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E,   // 8
        0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C,   // 9
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,   // space
        0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00,   // -
        0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x06,   // .
        0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10,   // /
        0x00, 0x06, 0x06, 0x00, 0x06, 0x06, 0x00,   // :
        0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00,   // +
        0x0A, 0x1F, 0x0A, 0x0A, 0x0A, 0x1F, 0x0A,   // #
    ];

    /// <summary>An 8-bit-per-channel colour. No alpha: LabVIEW icons are opaque.</summary>
    internal readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>
    /// Parse "RRGGBB" or "#RRGGBB". Returns null for anything else, so a caller can report the
    /// offending string rather than silently drawing black.
    /// </summary>
    internal static Rgb? ParseColor(string? text)
    {
        if (text is null) return null;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6) return null;
        return byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? new Rgb(r, g, b)
            : null;
    }

    /// <summary>
    /// Black or white, whichever stays legible on <paramref name="background"/>. Chosen from the fill rather than
    /// exposed as two more parameters, because the one thing a caller must not be able to do is
    /// ask for an icon whose text cannot be read. Rec. 601 luma, threshold at the midpoint.
    /// </summary>
    internal static Rgb ReadableOn(Rgb background) =>
        (299 * background.R + 587 * background.G + 114 * background.B) / 1000 < 128
            ? new Rgb(0xFF, 0xFF, 0xFF)
            : new Rgb(0x00, 0x00, 0x00);

    /// <summary>
    /// The six values LabVIEW quantises each channel to. Measured, not assumed - see
    /// vi-server-reference.md, "Writing back: setting a VI's icon": an icon read back out of a VI
    /// has every channel in this set, 189 of 1 024 pixels having moved on a non-web-safe image.
    /// </summary>
    private static readonly byte[] WebSafe = [0x00, 0x33, 0x66, 0x99, 0xCC, 0xFF];

    /// <summary>Whether LabVIEW will store this colour unchanged.</summary>
    internal static bool IsWebSafe(Rgb c) =>
        WebSafe.Contains(c.R) && WebSafe.Contains(c.G) && WebSafe.Contains(c.B);

    /// <summary>What LabVIEW will turn this colour into, so a caller can be told up front.</summary>
    internal static Rgb Quantise(Rgb c) => new(Nearest(c.R), Nearest(c.G), Nearest(c.B));

    private static byte Nearest(byte channel)
    {
        var best = WebSafe[0];
        foreach (var candidate in WebSafe)
            if (Math.Abs(candidate - channel) < Math.Abs(best - channel)) best = candidate;
        return best;
    }

    /// <summary>Whether the font can draw this character, after upper-casing.</summary>
    internal static bool Supports(char c) => Alphabet.IndexOf(char.ToUpperInvariant(c)) >= 0;

    /// <summary>
    /// Everything in <paramref name="text"/> the font cannot draw, de-duplicated, so the tool can
    /// warn about it instead of quietly leaving gaps.
    /// </summary>
    internal static string Unsupported(string? text) =>
        text is null ? "" : new string(text.Where(c => !Supports(c)).Distinct().ToArray());

    /// <summary>
    /// A 32x32 PNG: 1 px border, a coloured banner across the top carrying
    /// <paramref name="line1"/> in <paramref name="bannerText"/>, and up to two more lines below
    /// it on <paramref name="background"/>. Lines longer than <see cref="MaxCharsPerLine"/> are
    /// truncated - the caller is expected to have warned already.
    /// </summary>
    internal static byte[] Render(
        string? line1, string? line2, string? line3,
        Rgb banner, Rgb background, Rgb border, Rgb bannerText, Rgb bodyText)
    {
        var pixels = new Rgb[Size * Size];
        Array.Fill(pixels, background);

        // Banner rows 1..9: nine rows is a 7 px glyph with a pixel of air above and below.
        for (var y = 1; y <= 9; y++)
            for (var x = 1; x < Size - 1; x++)
                pixels[y * Size + x] = banner;

        // Border last would be wrong only if the banner overwrote it; it does not, but drawing
        // the frame after the fill keeps the corners exact whatever the fill does later.
        for (var i = 0; i < Size; i++)
        {
            pixels[i] = border;                          // top
            pixels[(Size - 1) * Size + i] = border;      // bottom
            pixels[i * Size] = border;                   // left
            pixels[i * Size + Size - 1] = border;        // right
        }

        DrawLine(pixels, line1, 2, bannerText);
        DrawLine(pixels, line2, 12, bodyText);
        DrawLine(pixels, line3, 21, bodyText);

        return Encode(pixels);
    }

    /// <summary>Draw one centred line with its top row at <paramref name="top"/>.</summary>
    private static void DrawLine(Rgb[] pixels, string? text, int top, Rgb colour)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var chars = text.Trim().ToUpperInvariant();
        if (chars.Length > MaxCharsPerLine) chars = chars[..MaxCharsPerLine];

        var width = chars.Length * (GlyphWidth + 1) - 1;
        var x0 = (Size - width) / 2;

        for (var i = 0; i < chars.Length; i++)
        {
            var index = Alphabet.IndexOf(chars[i]);
            if (index < 0) continue;                      // reported by Unsupported(), not drawn

            for (var row = 0; row < GlyphHeight; row++)
            {
                var bits = GlyphRows[index * GlyphHeight + row];
                for (var col = 0; col < GlyphWidth; col++)
                {
                    if ((bits & (1 << (GlyphWidth - 1 - col))) == 0) continue;
                    var x = x0 + i * (GlyphWidth + 1) + col;
                    var y = top + row;
                    if (x > 0 && x < Size - 1 && y > 0 && y < Size - 1)
                        pixels[y * Size + x] = colour;
                }
            }
        }
    }

    /// <summary>
    /// Encode as 8-bit truecolour PNG: signature, IHDR, one IDAT, IEND. Each scanline is
    /// prefixed with filter type 0 (none) - at 32x32 a filter would save bytes nobody counts.
    /// </summary>
    private static byte[] Encode(Rgb[] pixels)
    {
        var raw = new byte[Size * (1 + Size * 3)];
        var at = 0;
        for (var y = 0; y < Size; y++)
        {
            raw[at++] = 0;                                // filter: none
            for (var x = 0; x < Size; x++)
            {
                var p = pixels[y * Size + x];
                raw[at++] = p.R;
                raw[at++] = p.G;
                raw[at++] = p.B;
            }
        }

        using var deflated = new MemoryStream();
        // PNG's IDAT is a zlib stream (RFC 1950), which is exactly what ZLibStream writes -
        // DeflateStream would emit a raw deflate stream and every decoder would reject it.
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), Size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), Size);
        ihdr[8] = 8;      // bit depth
        ihdr[9] = 2;      // colour type 2: truecolour RGB
        ihdr[10] = 0;     // compression: deflate
        ihdr[11] = 0;     // filter method 0
        ihdr[12] = 0;     // no interlace

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", deflated.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.Write(length);

        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed.AsSpan(4));
        target.Write(typed);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        target.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
