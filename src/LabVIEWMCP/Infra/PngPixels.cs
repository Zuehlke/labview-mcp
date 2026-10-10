// ============================================================================================
// PngPixels.cs - decodes a PNG to RGBA pixels and compares icons pixel by pixel, with no
// imaging dependency, so an icon read back out of LabVIEW can be checked against what was meant.
// ============================================================================================
using System.Buffers.Binary;
using System.IO.Compression;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Decodes a PNG into RGBA pixels and compares two icons by those pixels.
///
/// WHY PIXELS AND NOT BYTES. LabVIEW writes a read-back icon as an 8-bit PALETTE PNG (colour type 3)
/// while <see cref="IconImage"/> and every design tool write truecolour, so two files holding the
/// same image differ in length and in almost every byte (docs/vi-server-reference.md, "Writing
/// back: setting a VI's icon"). A byte or hash compare reports a difference that is not there.
///
/// A PORT OF A MEASURED SCRIPT, not a fresh design: the icon read-back route of a pilot project
/// verified hundreds of icons with a pure-Python decoder of exactly this scope - colour types 0, 2,
/// 3, 4 and 6, bit depth 8 (1, 2 and 4 for grey and palette), all five row filters, tRNS - and
/// reported 16-bit and interlaced files as errors rather than guessing. Same scope here, same
/// statuses (OK / DIFF / SIZE / ERROR), so a result reads the same in either tool. Dependency-free
/// for the reason IconImage gives: one fixed-purpose decoder is a page of code, and
/// System.Drawing.Common is a Windows-only package.
/// </summary>
internal static class PngPixels
{
    /// <summary>One pixel. Alpha is 255 for every opaque pixel, which is every LabVIEW icon pixel.</summary>
    internal readonly record struct Rgba(byte R, byte G, byte B, byte A)
    {
        public override string ToString() => $"{R:X2}{G:X2}{B:X2}{(A == 255 ? "" : $"/{A:X2}")}";
    }

    /// <summary>A decoded image, row-major.</summary>
    internal sealed record Image(int Width, int Height, int ColourType, int BitDepth, Rgba[] Pixels)
    {
        public Rgba At(int x, int y) => Pixels[y * Width + x];
    }

    internal sealed class PngException(string message) : Exception(message);

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// "WxH" from the IHDR chunk, or null for anything that is not a PNG. Reads 24 bytes, so it is
    /// the cheap check a tool runs on a caller's file before LabVIEW is touched.
    /// </summary>
    internal static (int Width, int Height)? Size(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[24];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
                return null;
            if (!head.AsSpan(0, 8).SequenceEqual(Signature)) return null;
            return (BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static Image Decode(string path) => Decode(File.ReadAllBytes(path));

    internal static Image Decode(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature))
            throw new PngException("not a PNG");

        int width = 0, height = 0, depth = 0, colour = -1, interlace = 0;
        var haveHeader = false;
        byte[]? palette = null, transparency = null;
        using var idat = new MemoryStream();

        var at = 8;
        while (at + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at, 4));
            var type = System.Text.Encoding.ASCII.GetString(data, at + 4, 4);
            if (length < 0 || at + 12 + length > data.Length)
                throw new PngException($"chunk {type} runs past the end of the file");
            var body = data.AsSpan(at + 8, length);

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4, 4));
                    depth = body[8];
                    colour = body[9];
                    interlace = body[12];
                    haveHeader = true;
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": transparency = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            if (type == "IEND") break;
            at += 12 + length;
        }

        if (!haveHeader) throw new PngException("no IHDR");
        if (interlace != 0) throw new PngException("interlaced PNG not supported");

        var channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || depth is not (1 or 2 or 4 or 8) || (depth < 8 && colour is not (0 or 3)))
            throw new PngException($"colour type {colour} / bit depth {depth} not supported");
        if (colour == 3 && palette is null) throw new PngException("palette image without PLTE");
        if (width <= 0 || height <= 0) throw new PngException($"bad size {width}x{height}");

        var bpp = Math.Max(1, channels * depth / 8);
        var stride = (width * channels * depth + 7) / 8;

        byte[] raw;
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            raw = output.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw new PngException($"IDAT does not inflate: {e.Message}");
        }
        if (raw.Length < height * (stride + 1))
            throw new PngException($"image data is {raw.Length} bytes, {height * (stride + 1)} needed");

        var rows = Unfilter(raw, height, stride, bpp);

        // A grey or truecolour tRNS names ONE transparent colour (16-bit samples, even at depth 8).
        int? greyKey = colour == 0 && transparency is { Length: >= 2 }
            ? BinaryPrimitives.ReadUInt16BigEndian(transparency) : null;
        (int, int, int)? rgbKey = colour == 2 && transparency is { Length: >= 6 }
            ? (BinaryPrimitives.ReadUInt16BigEndian(transparency),
               BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)),
               BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)))
            : null;

        var pixels = new Rgba[width * height];
        for (var y = 0; y < height; y++)
        {
            var line = rows[y];
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = colour switch
                {
                    3 => PaletteEntry(palette!, transparency, Sample(line, x, depth)),
                    0 => Grey(Sample(line, x, depth), depth, greyKey),
                    2 => Truecolour(line[3 * x], line[3 * x + 1], line[3 * x + 2], rgbKey),
                    4 => new Rgba(line[2 * x], line[2 * x], line[2 * x], line[2 * x + 1]),
                    _ => new Rgba(line[4 * x], line[4 * x + 1], line[4 * x + 2], line[4 * x + 3]),
                };
            }
        }
        return new Image(width, height, colour, depth, pixels);
    }

    private static byte[][] Unfilter(byte[] raw, int height, int stride, int bpp)
    {
        var rows = new byte[height][];
        var previous = new byte[stride];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[at];
            var line = raw.AsSpan(at + 1, stride).ToArray();
            at += 1 + stride;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? line[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                line[i] = filter switch
                {
                    0 => line[i],
                    1 => (byte)(line[i] + a),
                    2 => (byte)(line[i] + b),
                    3 => (byte)(line[i] + (a + b) / 2),
                    4 => (byte)(line[i] + Paeth(a, b, c)),
                    _ => throw new PngException($"bad filter type {filter}"),
                };
            }
            rows[y] = line;
            previous = line;
        }
        return rows;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>A sample of 1, 2, 4 or 8 bits, leftmost pixel in the high bits.</summary>
    private static int Sample(byte[] line, int x, int depth)
    {
        if (depth == 8) return line[x];
        var perByte = 8 / depth;
        var mask = (1 << depth) - 1;
        return (line[x / perByte] >> (8 - depth * (x % perByte + 1))) & mask;
    }

    private static Rgba PaletteEntry(byte[] palette, byte[]? transparency, int index)
    {
        if (index * 3 + 2 >= palette.Length)
            throw new PngException($"palette index {index} outside a {palette.Length / 3}-entry PLTE");
        var alpha = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
        return new Rgba(palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2], alpha);
    }

    private static Rgba Grey(int value, int depth, int? key)
    {
        var alpha = key == value ? (byte)0 : (byte)255;
        var v = (byte)(value * 255 / ((1 << depth) - 1));
        return new Rgba(v, v, v, alpha);
    }

    private static Rgba Truecolour(byte r, byte g, byte b, (int, int, int)? key) =>
        new(r, g, b, key == (r, g, b) ? (byte)0 : (byte)255);

    // ------------------------------------------------------------------ comparing

    /// <summary>The outcome of one comparison, in the statuses the measured script printed.</summary>
    internal sealed record Comparison(string Status, int Differing, int Total, string Detail,
                                      IReadOnlyList<string> FirstDifferences);

    internal const string Ok = "OK", Diff = "DIFF", SizeMismatch = "SIZE", Error = "ERROR";

    /// <summary>
    /// Compare two PNG files by decoded pixels. <paramref name="tolerance"/> is the per-channel
    /// difference still counted as equal; <paramref name="ignoreAlpha"/> compares RGB only.
    /// </summary>
    internal static Comparison Compare(string actualPath, string expectedPath, int tolerance = 0,
                                       bool ignoreAlpha = false)
    {
        Image actual, expected;
        try
        {
            actual = Decode(actualPath);
            expected = Decode(expectedPath);
        }
        catch (Exception e) when (e is PngException or IOException or UnauthorizedAccessException)
        {
            return new Comparison(Error, 0, 0, e.Message, []);
        }
        return Compare(actual, expected, tolerance, ignoreAlpha);
    }

    internal static Comparison Compare(Image actual, Image expected, int tolerance = 0,
                                       bool ignoreAlpha = false)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
            return new Comparison(SizeMismatch, 0, actual.Pixels.Length,
                $"{actual.Width}x{actual.Height} vs {expected.Width}x{expected.Height}", []);

        var differing = 0;
        var first = new List<string>();
        for (var i = 0; i < actual.Pixels.Length; i++)
        {
            if (Same(actual.Pixels[i], expected.Pixels[i], tolerance, ignoreAlpha)) continue;
            differing++;
            if (first.Count < 8)
                first.Add($"({i % actual.Width}, {i / actual.Width}) {actual.Pixels[i]} expected {expected.Pixels[i]}");
        }
        return new Comparison(differing == 0 ? Ok : Diff, differing, actual.Pixels.Length,
            $"{differing} px differ of {actual.Pixels.Length}", first);
    }

    private static bool Same(Rgba a, Rgba b, int tolerance, bool ignoreAlpha) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance &&
        Math.Abs(a.B - b.B) <= tolerance && (ignoreAlpha || Math.Abs(a.A - b.A) <= tolerance);

    // ------------------------------------------------------------------ the template layer

    /// <summary>
    /// Whether a pixel of an owner's icon is part of the template LabVIEW lays over a member.
    /// MEASURED 2026-09-25: <c>ApplyIconToVIs</c> lays the class icon over each member's own icon
    /// and WHITE IS TRANSPARENT, so the member keeps its own glyph wherever the owner's icon is
    /// white. A fully transparent pixel is no template either.
    /// </summary>
    internal static bool IsTemplatePixel(Rgba p) =>
        p.A != 0 && !(p.R == 0xFF && p.G == 0xFF && p.B == 0xFF);

    /// <summary>
    /// How many of the owner's template pixels the member does NOT show. Zero means the template
    /// landed on the member. Both images should be LabVIEW read-backs: both have then been through
    /// the same colour quantisation, so a design PNG's off-cube colour does not count as a miss.
    /// </summary>
    internal static (int Missing, int TemplatePixels) TemplateMissing(Image member, Image owner)
    {
        if (member.Width != owner.Width || member.Height != owner.Height) return (-1, 0);
        int missing = 0, template = 0;
        for (var i = 0; i < owner.Pixels.Length; i++)
        {
            if (!IsTemplatePixel(owner.Pixels[i])) continue;
            template++;
            if (!Same(member.Pixels[i], owner.Pixels[i], 0, ignoreAlpha: true)) missing++;
        }
        return (missing, template);
    }

    /// <summary>
    /// Pixels that changed between two read-backs of the same VI, split into those under the owner's
    /// template and those outside it. A re-applied template is expected to change only the first
    /// kind; any of the second kind means the VI's own layer changed.
    /// </summary>
    internal static (int Changed, int OutsideTemplate) ChangedPixels(Image before, Image after, Image owner)
    {
        if (before.Width != after.Width || before.Height != after.Height ||
            owner.Width != after.Width || owner.Height != after.Height) return (-1, -1);
        int changed = 0, outside = 0;
        for (var i = 0; i < after.Pixels.Length; i++)
        {
            if (Same(before.Pixels[i], after.Pixels[i], 0, ignoreAlpha: false)) continue;
            changed++;
            if (!IsTemplatePixel(owner.Pixels[i])) outside++;
        }
        return (changed, outside);
    }

    /// <summary>Distinct colours outside LabVIEW's web-safe cube, which it will shift on the way in.</summary>
    internal static IReadOnlyList<string> NonWebSafeColours(Image image) =>
        [.. image.Pixels.Where(p => p.A != 0 && !IconImage.IsWebSafe(new IconImage.Rgb(p.R, p.G, p.B)))
                        .Select(p => $"{p.R:X2}{p.G:X2}{p.B:X2}").Distinct().Order(StringComparer.Ordinal)];
}
