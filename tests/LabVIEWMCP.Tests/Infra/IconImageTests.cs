using System.Buffers.Binary;
using System.IO.Compression;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The icon renderer writes a PNG by hand, so "it produced bytes" proves nothing - a decoder has
/// to get the pixels back out. These tests walk the chunks, verify every CRC, inflate the IDAT and
/// read individual pixels, which is the only way to catch a stream that LabVIEW would silently
/// refuse. No imaging dependency here either, for the same reason there is none in the server.
/// </summary>
public sealed class IconImageTests
{
    private const int Size = IconImage.Size;

    /// <summary>A decoded image plus what the chunk walk found on the way.</summary>
    private sealed record Decoded(IconImage.Rgb[] Pixels, List<string> ChunkTypes, int Width, int Height);

    /// <summary>
    /// Decode an 8-bit truecolour PNG with filter 0 on every scanline - the only kind
    /// <see cref="IconImage"/> writes. Throws on a bad signature, a bad CRC or an unexpected
    /// header, so a malformed stream fails the test that produced it.
    /// </summary>
    private static Decoded Decode(byte[] png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(png.AsSpan(0, 8).SequenceEqual(signature), "PNG signature is wrong");

        var types = new List<string>();
        var idat = new MemoryStream();
        int width = 0, height = 0;
        var at = 8;

        while (at < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);

            var declared = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length, 4));
            Assert.Equal(Crc32(png.AsSpan(at + 4, 4 + length)), declared);

            types.Add(type);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
                    Assert.Equal(8, data[8]);       // bit depth
                    Assert.Equal(2, data[9]);       // truecolour
                    Assert.Equal(0, data[10]);      // deflate
                    Assert.Equal(0, data[11]);      // filter method 0
                    Assert.Equal(0, data[12]);      // no interlace
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
            }

            at += 12 + length;
        }

        Assert.Equal(["IHDR", "IDAT", "IEND"], types);

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
            zlib.CopyTo(inflated);

        var raw = inflated.ToArray();
        Assert.Equal(height * (1 + width * 3), raw.Length);

        var pixels = new IconImage.Rgb[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            Assert.Equal(0, raw[row]);              // filter: none, on every scanline
            for (var x = 0; x < width; x++)
            {
                var p = row + 1 + x * 3;
                pixels[y * width + x] = new IconImage.Rgb(raw[p], raw[p + 1], raw[p + 2]);
            }
        }

        return new Decoded(pixels, types, width, height);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly IconImage.Rgb Blue = new(0x00, 0x5A, 0x9C);
    private static readonly IconImage.Rgb White = new(0xFF, 0xFF, 0xFF);
    private static readonly IconImage.Rgb Black = new(0x00, 0x00, 0x00);

    private static byte[] Render(string? l1 = "DAQ", string? l2 = null, string? l3 = null) =>
        IconImage.Render(l1, l2, l3, Blue, White, Black,
                         IconImage.ReadableOn(Blue), IconImage.ReadableOn(White));

    private static IconImage.Rgb At(Decoded d, int x, int y) => d.Pixels[y * d.Width + x];

    [Fact]
    public void Writes_a_decodable_32x32_png()
    {
        var decoded = Decode(Render());

        Assert.Equal(32, decoded.Width);
        Assert.Equal(32, decoded.Height);
        Assert.Equal(Size * Size, decoded.Pixels.Length);
    }

    [Fact]
    public void Frames_the_icon_and_fills_banner_and_body()
    {
        var decoded = Decode(Render(l1: null));   // no text: only the fills are under test

        Assert.Equal(Black, At(decoded, 0, 0));            // corners are border
        Assert.Equal(Black, At(decoded, 31, 31));
        Assert.Equal(Black, At(decoded, 0, 16));           // left edge
        Assert.Equal(Black, At(decoded, 31, 16));          // right edge

        Assert.Equal(Blue, At(decoded, 16, 5));            // inside the banner
        Assert.Equal(Blue, At(decoded, 1, 9));             // banner reaches its last row
        Assert.Equal(White, At(decoded, 16, 15));          // body below it
        Assert.Equal(White, At(decoded, 16, 30));
    }

    [Fact]
    public void Draws_banner_text_in_the_readable_colour()
    {
        var withText = Decode(Render(l1: "DAQ"));
        var without = Decode(Render(l1: null));

        // On NI blue the readable choice is white, so banner text must appear as white pixels
        // inside the banner - where the empty render has none.
        var lit = CountIn(withText, White, 1, 9);
        Assert.True(lit > 20, $"expected banner glyph pixels, found {lit}");
        Assert.Equal(0, CountIn(without, White, 1, 9));
        Assert.Equal(White, IconImage.ReadableOn(Blue));
        Assert.Equal(Black, IconImage.ReadableOn(White));
    }

    [Fact]
    public void Draws_the_two_body_lines_separately()
    {
        var one = Decode(Render(l1: "DAQ", l2: "3AI"));
        var two = Decode(Render(l1: "DAQ", l2: "3AI", l3: "TDMS"));

        Assert.True(CountIn(one, Black, 12, 18) > 10, "line2 did not draw");
        Assert.Equal(0, CountIn(one, Black, 21, 27));           // line3 absent
        Assert.True(CountIn(two, Black, 21, 27) > 10, "line3 did not draw");
    }

    /// <summary>Count pixels of one colour in rows <paramref name="from"/>..<paramref name="to"/>,
    /// excluding the 1 px border columns so the frame never counts.</summary>
    private static int CountIn(Decoded d, IconImage.Rgb colour, int from, int to)
    {
        var n = 0;
        for (var y = from; y <= to; y++)
            for (var x = 1; x < d.Width - 1; x++)
                if (At(d, x, y) == colour) n++;
        return n;
    }

    [Fact]
    public void Truncates_a_line_that_cannot_fit_rather_than_overflowing()
    {
        // Six characters do not fit; the render must stay inside the frame either way.
        var decoded = Decode(Render(l1: "ABCDEFGH"));

        for (var y = 0; y < Size; y++)
        {
            Assert.Equal(Black, At(decoded, 0, y));
            Assert.Equal(Black, At(decoded, Size - 1, y));
        }
    }

    [Theory]
    [InlineData("005A9C", 0x00, 0x5A, 0x9C)]
    [InlineData("#FF8000", 0xFF, 0x80, 0x00)]
    [InlineData("  ffffff  ", 0xFF, 0xFF, 0xFF)]
    public void Parses_both_colour_spellings(string text, int r, int g, int b)
    {
        var parsed = IconImage.ParseColor(text);

        Assert.NotNull(parsed);
        Assert.Equal(new IconImage.Rgb((byte)r, (byte)g, (byte)b), parsed!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("0x005A9C")]
    [InlineData("blue")]
    [InlineData("GGHHII")]
    public void Rejects_a_colour_it_cannot_read(string text) =>
        Assert.Null(IconImage.ParseColor(text));

    [Fact]
    public void Reports_characters_the_font_cannot_draw()
    {
        Assert.Equal("", IconImage.Unsupported("DAQ 3AI-1.0/2:+#"));
        Assert.Equal("", IconImage.Unsupported("daq"));         // upper-cased, so drawable
        Assert.Equal("ÄÖ", IconImage.Unsupported("ÄÖÄ"));       // de-duplicated
        Assert.Equal("_", IconImage.Unsupported("A_B"));
    }

    [Theory]
    [InlineData(0x00, 0x5A, 0x9C, 0x00, 0x66, 0x99)]   // NI blue -> its web-safe neighbour
    [InlineData(0x00, 0x66, 0x99, 0x00, 0x66, 0x99)]   // already safe: unchanged
    [InlineData(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)]
    [InlineData(0x7F, 0x7F, 0x7F, 0x66, 0x66, 0x66)]   // 127 is nearer 102 than 153
    [InlineData(0xC0, 0x39, 0x2B, 0xCC, 0x33, 0x33)]
    public void Quantises_the_way_labview_does(int r, int g, int b, int qr, int qg, int qb)
    {
        var quantised = IconImage.Quantise(new IconImage.Rgb((byte)r, (byte)g, (byte)b));

        Assert.Equal(new IconImage.Rgb((byte)qr, (byte)qg, (byte)qb), quantised);
        Assert.True(IconImage.IsWebSafe(quantised), "quantising must land inside the cube");
    }

    [Fact]
    public void Recognises_which_colours_survive_a_round_trip()
    {
        Assert.True(IconImage.IsWebSafe(new IconImage.Rgb(0x00, 0x66, 0x99)));
        Assert.True(IconImage.IsWebSafe(new IconImage.Rgb(0x00, 0x00, 0x00)));
        Assert.False(IconImage.IsWebSafe(new IconImage.Rgb(0x00, 0x5A, 0x9C)));
        Assert.False(IconImage.IsWebSafe(new IconImage.Rgb(0x01, 0x00, 0x00)));
    }

    [Fact]
    public void Renders_every_drawable_character_without_leaving_the_frame()
    {
        // A glyph table with a wrong row count would walk off the end of the array; rendering
        // every character in groups of five is the cheapest way to walk the whole table.
        const string drawable = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -./:+#";
        for (var i = 0; i < drawable.Length; i += IconImage.MaxCharsPerLine)
        {
            var chunk = drawable.Substring(i, Math.Min(IconImage.MaxCharsPerLine, drawable.Length - i));
            var decoded = Decode(Render(l1: chunk, l2: chunk, l3: chunk));
            Assert.Equal(Black, At(decoded, 0, 0));
            Assert.Equal(Black, At(decoded, Size - 1, Size - 1));
        }
    }
}
