using System.Diagnostics;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Query the palette index from the command line, exactly as `lvai_palette_index` does - the
/// counterpart to <see cref="Examples"/>, and for the same two reasons.
///
/// It needs no LabVIEW: the index is a scan of the installation's .mnu files, so this answers "may
/// a Call target X on this station" without an MCP client and without the IDE running.
///
/// It is also how the scan's cost got measured, from the shipped code path rather than from a
/// re-implementation that could quietly disagree with it: about 150 ms to scan, 90 ms from the disk
/// cache, against 55 seconds and 804 ms for the example index. Those numbers are why the two caches
/// are documented as resting on different arguments; see <see cref="Infra.PaletteIndexStore"/>. The
/// cost was simply unknown until this mode existed.
/// </summary>
internal static class Palette
{
    public static int Run(string? query, int? limit, bool refresh)
    {
        if (refresh)
            Console.Error.WriteLine(
                "Rescanning the palette - every .mnu file of the installation and every add-on, " +
                $"about 150 ms. Cache: {PaletteIndexStore.Directory}");

        var started = Stopwatch.StartNew();
        var answer = PaletteTools.PaletteIndexTool(query, limit ?? 40, refresh);
        started.Stop();

        Console.WriteLine(answer);
        Console.Error.WriteLine($"({started.ElapsedMilliseconds} ms)");
        return 0;
    }
}
