using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A Formula Node's UNWIRED variables are terminals too, and the reader must list them as NI's
/// export does - an unwired output is how the code declares a variable it assigns. NI's Cut
/// Length.vi assigns m, n, a, b and d and wires only m, n and d: NI writes
/// `outputs="m:..,n:..,a:,b:,d:.."`. The reader wrote wired ends only, and the composed node's
/// `a=...` was `Undefined variable` - 3 broken composed examples, 2026-11-06, all executable with
/// the variables listed (docs/aixml-backends.md). Reads the installed VI; SKIPS where it or the
/// bundled interpreter is missing.
/// </summary>
public sealed class ReaderFormulaNodeTests
{
    private static string? Installed(params string[] parts) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine([d, .. parts]))
                .FirstOrDefault(File.Exists)
            : null;

    [Fact]
    public async Task A_formula_node_lists_its_unwired_variables_as_NI_writes_them()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null ||
            Installed("examples", "Mathematics", "Geometry", "subVIs", "Cut Length.vi") is not { } vi) return;
        var output = Path.Combine(Path.GetTempPath(), "reader-fbox-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(read.Written, read.Message);
            var text = await File.ReadAllTextAsync(output);

            Assert.Contains("outputs=\"m:127.m,n:127.n,a:,b:,d:127.d\"", text);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }
}
