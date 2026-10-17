// =============================================================================
// WindowShot.cs - a picture of one window (PrintWindow, nothing brought to the front),
// encoded as PNG with no imaging dependency, so a caller can SEE a dialog it cannot read.
// =============================================================================
using System.Buffers.Binary;
using System.IO.Compression;

namespace LabVIEWMcp.Infra;

/// <summary>An RGB picture, row-major, top row first, 3 bytes per pixel.</summary>
internal sealed record WindowImage(int Width, int Height, byte[] Rgb);

/// <summary>
/// Screenshots of dialogs. WHY: LabVIEW draws its own dialogs (`LVDChild`) with no child
/// controls, so neither their text nor their buttons can be read through Win32. A PNG the caller
/// can look at is the only way to know which dialog is up - measured 2026-10-10, when a
/// screen copy of an untitled dialog's rectangle captured the terminal on top of it instead.
/// So the picture is taken with PrintWindow (PW_RENDERFULLCONTENT), which renders the window
/// itself whatever covers it, and nothing is brought to the front.
/// </summary>
internal static class WindowShot
{
    public static string DefaultDirectory() => Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "dialogs");

    /// <summary>Capture and save; the path, or null when the window could not be captured.</summary>
    public static string? Save(ILabViewMachine machine, MachineWindow window, string? directory = null, string? tag = null)
    {
        try
        {
            var image = machine.Capture(window.Handle);
            if (image is null) return null;
            var dir = directory ?? DefaultDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                $"{machine.Now:yyyyMMdd-HHmmss-fff}_{window.ProcessId}_{window.Handle}{(tag is null ? "" : "_" + tag)}.png");
            File.WriteAllBytes(path, EncodePng(image));
            return path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------- PNG, truecolour, filter 0 ----------

    public static byte[] EncodePng(WindowImage image)
    {
        var stride = image.Width * 3;
        var raw = new byte[image.Height * (1 + stride)];
        for (var y = 0; y < image.Height; y++)
        {
            raw[y * (1 + stride)] = 0;
            Array.Copy(image.Rgb, y * stride, raw, y * (1 + stride) + 1, stride);
        }

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), image.Height);
        ihdr[8] = 8;
        ihdr[9] = 2;

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
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
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
