// ============================================================================================
// PngPixelsTests.cs - the icon PNG decoder and pixel compare: every colour type and row filter
// the measured Python script handled, and the template-layer checks built on top of them.
// ============================================================================================
using System.Buffers.Binary;
using System.IO.Compression;
using LabVIEWMcp.Infra;
using Xunit;
using Rgba = LabVIEWMcp.Infra.PngPixels.Rgba;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The decoder's whole reason to exist is that LabVIEW writes a read-back icon as a PALETTE PNG
/// while the icons sent to it are truecolour - so the central test is that the two encodings of the
/// same picture compare equal. Images are encoded here by hand, with every row filter, because a
/// decoder that only ever met filter 0 (all IconImage writes) has not been tested.
/// </summary>
public sealed class PngPixelsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-png-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Save(string name, byte[] png)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, png);
        return path;
    }

    private static readonly Rgba White = new(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Rgba Blue = new(0x00, 0x66, 0x99, 0xFF);
    private static readonly Rgba Black = new(0x00, 0x00, 0x00, 0xFF);

    /// <summary>A 32x32 icon: blue banner rows 0..9, a black glyph block, white elsewhere.</summary>
    private static Rgba[] Icon(Rgba? glyph = null)
    {
        var pixels = new Rgba[32 * 32];
        for (var i = 0; i < pixels.Length; i++)
        {
            int x = i % 32, y = i / 32;
            pixels[i] = y < 10 ? Blue : x is >= 12 and < 20 && y is >= 14 and < 22 ? glyph ?? Black : White;
        }
        return pixels;
    }

    // ---------------------------------------------------------------- encoders for the tests

    private static byte[] Chunk(string type, byte[] data)
    {
        var block = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(block, data.Length);
        for (var i = 0; i < 4; i++) block[4 + i] = (byte)type[i];
        data.CopyTo(block, 8);
        // the decoder does not check CRCs (neither did the script it ports), so zeros are fine
        return block;
    }

    private static byte[] Png(int width, int height, int depth, int colour, byte[][] rows,
                              byte[]? plte = null, byte[]? trns = null, int[]? filters = null)
    {
        // Filter each row as asked, so the decoder has to undo it.
        var bpp = Math.Max(1, (colour switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 }) * depth / 8);
        using var raw = new MemoryStream();
        var previous = new byte[rows[0].Length];
        for (var y = 0; y < rows.Length; y++)
        {
            var f = filters?[y % filters.Length] ?? 0;
            raw.WriteByte((byte)f);
            var line = rows[y];
            for (var i = 0; i < line.Length; i++)
            {
                int a = i >= bpp ? line[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                var predictor = f switch
                {
                    1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Math.Abs(a + b - c - a) <= Math.Abs(a + b - c - b) && Math.Abs(a + b - c - a) <= Math.Abs(a + b - c - c)
                        ? a : Math.Abs(a + b - c - b) <= Math.Abs(a + b - c - c) ? b : c,
                    _ => 0,
                };
                raw.WriteByte((byte)(line[i] - predictor));
            }
            previous = line;
        }

        using var z = new MemoryStream();
        using (var zlib = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colour;

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.Write(Chunk("IHDR", ihdr));
        if (plte is not null) png.Write(Chunk("PLTE", plte));
        if (trns is not null) png.Write(Chunk("tRNS", trns));
        png.Write(Chunk("IDAT", z.ToArray()));
        png.Write(Chunk("IEND", []));
        return png.ToArray();
    }

    private static byte[] Truecolour(Rgba[] pixels, int[]? filters = null) =>
        Png(32, 32, 8, 2, [.. Enumerable.Range(0, 32).Select(y =>
            pixels.Skip(y * 32).Take(32).SelectMany(p => new[] { p.R, p.G, p.B }).ToArray())],
            filters: filters);

    /// <summary>The shape LabVIEW writes a read-back in: 8-bit palette.</summary>
    private static byte[] Palette(Rgba[] pixels, int[]? filters = null, byte[]? trns = null)
    {
        var colours = pixels.Distinct().ToList();
        var plte = colours.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
        return Png(32, 32, 8, 3, [.. Enumerable.Range(0, 32).Select(y =>
            pixels.Skip(y * 32).Take(32).Select(p => (byte)colours.IndexOf(p)).ToArray())],
            plte, trns, filters);
    }

    // ---------------------------------------------------------------- decoding

    [Fact]
    public void A_palette_and_a_truecolour_encoding_of_one_icon_compare_equal()
    {
        var a = Save("rgb.png", Truecolour(Icon()));
        var b = Save("pal.png", Palette(Icon()));

        var result = PngPixels.Compare(a, b);

        // The one fact the whole read-back route rests on: different bytes, same pixels.
        Assert.NotEqual(File.ReadAllBytes(a), File.ReadAllBytes(b));
        Assert.Equal(PngPixels.Ok, result.Status);
        Assert.Equal(0, result.Differing);
        Assert.Equal(1024, result.Total);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_row_filter_is_undone(int filter)
    {
        var plain = PngPixels.Decode(Truecolour(Icon()));
        var filtered = PngPixels.Decode(Truecolour(Icon(), [filter]));
        var paletted = PngPixels.Decode(Palette(Icon(), [filter, 0, 2]));

        Assert.Equal(plain.Pixels, filtered.Pixels);
        Assert.Equal(plain.Pixels, paletted.Pixels);
    }

    [Fact]
    public void IconImage_output_decodes_to_the_colours_it_was_drawn_with()
    {
        var png = IconImage.Render("DAQ", "AI", null, new IconImage.Rgb(0x00, 0x66, 0x99),
            new IconImage.Rgb(0xFF, 0xFF, 0xFF), new IconImage.Rgb(0, 0, 0),
            new IconImage.Rgb(0xFF, 0xFF, 0xFF), new IconImage.Rgb(0, 0, 0));

        var image = PngPixels.Decode(png);

        Assert.Equal((32, 32), (image.Width, image.Height));
        Assert.Equal(Black, image.At(0, 0));                    // the frame
        Assert.Equal(Blue, image.At(1, 1));                     // banner corner, no glyph there
        Assert.Equal(White, image.At(1, 30));                   // body
    }

    [Fact]
    public void A_one_bit_grey_image_decodes_with_its_samples_scaled()
    {
        // 8 px wide: 10101010 -> white, black, ...
        var png = Png(8, 1, 1, 0, [[0b1010_1010]]);

        var image = PngPixels.Decode(png);

        Assert.Equal(White, image.At(0, 0));
        Assert.Equal(Black, image.At(1, 0));
    }

    [Fact]
    public void A_palette_tRNS_entry_becomes_alpha_and_ignoreAlpha_overlooks_it()
    {
        var opaque = Save("opaque.png", Palette(Icon()));
        // index 0 is the first distinct colour (blue): mark it fully transparent
        var clear = Save("clear.png", Palette(Icon(), trns: [0x00]));

        Assert.Equal(0, PngPixels.Decode(clear).At(0, 0).A);
        Assert.Equal(PngPixels.Diff, PngPixels.Compare(clear, opaque).Status);
        Assert.Equal(PngPixels.Ok, PngPixels.Compare(clear, opaque, ignoreAlpha: true).Status);
    }

    [Fact]
    public void Interlaced_and_sixteen_bit_files_are_errors_not_guesses()
    {
        var interlaced = Truecolour(Icon());
        interlaced[8 + 8 + 12] = 1;                              // IHDR interlace byte
        Assert.Throws<PngPixels.PngException>(() => PngPixels.Decode(interlaced));

        var deep = Truecolour(Icon());
        deep[8 + 8 + 8] = 16;                                    // IHDR bit depth
        Assert.Throws<PngPixels.PngException>(() => PngPixels.Decode(deep));

        Assert.Throws<PngPixels.PngException>(() => PngPixels.Decode([1, 2, 3]));
    }

    [Fact]
    public void Size_reads_the_header_and_refuses_what_is_not_a_PNG()
    {
        Assert.Equal((32, 32), PngPixels.Size(Save("ok.png", Truecolour(Icon()))));
        Assert.Null(PngPixels.Size(Save("no.png", "not a png at all, just text"u8.ToArray())));
    }

    // ---------------------------------------------------------------- comparing

    [Fact]
    public void One_changed_pixel_is_one_difference_and_is_named()
    {
        var changed = Icon();
        changed[5 * 32 + 7] = White;

        var result = PngPixels.Compare(PngPixels.Decode(Truecolour(changed)), PngPixels.Decode(Truecolour(Icon())));

        Assert.Equal(PngPixels.Diff, result.Status);
        Assert.Equal(1, result.Differing);
        Assert.StartsWith("(7, 5) FFFFFF expected 006699", result.FirstDifferences[0]);
    }

    [Fact]
    public void Tolerance_counts_a_near_colour_as_equal()
    {
        var near = Icon(new Rgba(0x03, 0x00, 0x00, 0xFF));
        var a = PngPixels.Decode(Truecolour(near));
        var b = PngPixels.Decode(Truecolour(Icon()));

        Assert.Equal(PngPixels.Diff, PngPixels.Compare(a, b).Status);
        Assert.Equal(PngPixels.Ok, PngPixels.Compare(a, b, tolerance: 3).Status);
    }

    [Fact]
    public void Different_sizes_are_SIZE_and_an_unreadable_file_is_ERROR()
    {
        var small = PngPixels.Decode(Png(8, 1, 1, 0, [[0]]));
        Assert.Equal(PngPixels.SizeMismatch, PngPixels.Compare(small, PngPixels.Decode(Truecolour(Icon()))).Status);

        var bad = Save("bad.png", [1, 2, 3]);
        Assert.Equal(PngPixels.Error, PngPixels.Compare(bad, Save("ok.png", Truecolour(Icon()))).Status);
    }

    // ---------------------------------------------------------------- the template layer

    [Fact]
    public void A_member_that_shows_every_template_pixel_misses_none()
    {
        // The owner's icon: the banner only, white below. The member: the same banner over its
        // own glyph - what ApplyIconToVIs measured to produce, white being transparent.
        var owner = PngPixels.Decode(Truecolour(Icon(White)));
        var member = PngPixels.Decode(Truecolour(Icon()));

        var (missing, template) = PngPixels.TemplateMissing(member, owner);

        Assert.Equal(0, missing);
        Assert.Equal(320, template);                             // 10 banner rows x 32
    }

    [Fact]
    public void A_member_without_the_banner_misses_the_template()
    {
        var owner = PngPixels.Decode(Truecolour(Icon(White)));
        var flat = Icon();
        for (var i = 0; i < 320; i++) flat[i] = White;

        Assert.Equal(320, PngPixels.TemplateMissing(PngPixels.Decode(Truecolour(flat)), owner).Missing);
    }

    [Fact]
    public void Changed_pixels_are_split_into_under_and_outside_the_template()
    {
        var owner = PngPixels.Decode(Truecolour(Icon(White)));
        var before = PngPixels.Decode(Truecolour(Icon()));

        // A new banner colour only: every change is under the template.
        var rebannered = Icon();
        for (var i = 0; i < 320; i++) rebannered[i] = new Rgba(0xCC, 0x33, 0x00, 0xFF);
        Assert.Equal((320, 0), PngPixels.ChangedPixels(before, PngPixels.Decode(Truecolour(rebannered)), owner));

        // A changed glyph: the VI's own layer moved, which keepOwnLayer must report.
        var reglyphed = PngPixels.Decode(Truecolour(Icon(new Rgba(0x99, 0x00, 0x00, 0xFF))));
        Assert.Equal((64, 64), PngPixels.ChangedPixels(before, reglyphed, owner));
    }

    [Fact]
    public void Off_cube_colours_are_listed_once_each()
    {
        var image = PngPixels.Decode(Truecolour(Icon(new Rgba(0x1A, 0x5F, 0xA0, 0xFF))));

        Assert.Equal(["1A5FA0"], PngPixels.NonWebSafeColours(image));
        Assert.Empty(PngPixels.NonWebSafeColours(PngPixels.Decode(Truecolour(Icon()))));
    }
}
