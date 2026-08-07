using System.Buffers.Binary;
using System.Text;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMCP.Tests.Infra;

/// <summary>
/// The exbins reader. The format is a series of PARALLEL arrays - names, PTH0 paths, descriptions
/// at fixed positions - so the risk is not failing to parse but parsing into a MIS-PAIRING, which
/// would attach one example's description to another and look entirely plausible. Hence the
/// alignment assertions, and hence the reader refusing a file whose arrays disagree.
/// </summary>
public class ExternalExampleIndexTests
{
    // ---------- builders that write the format byte for byte ----------

    private static byte[] U32(int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
        return buffer;
    }

    private static IEnumerable<byte> TextSection(params string[] items)
    {
        var bytes = new List<byte>(U32(items.Length));
        foreach (var item in items)
        {
            var payload = Encoding.ASCII.GetBytes(item);
            bytes.AddRange(U32(payload.Length));
            bytes.AddRange(payload);
        }
        return bytes;
    }

    private static IEnumerable<byte> NumberSection(params int[][] rows)
    {
        var bytes = new List<byte>(U32(rows.Length));
        foreach (var row in rows)
        {
            bytes.AddRange(U32(row.Length));
            foreach (var value in row) bytes.AddRange(U32(value));
        }
        return bytes;
    }

    /// <summary>A PTH0 array: tag, size, uint16 type, uint16 parts, then 1-BYTE-prefixed parts.</summary>
    private static IEnumerable<byte> PathSection(params string[] paths)
    {
        var bytes = new List<byte>(U32(paths.Length));
        foreach (var path in paths)
        {
            var parts = path.Split('\\');
            var body = new List<byte> { 0x00, 0x01, (byte)(parts.Length >> 8), (byte)parts.Length };
            foreach (var part in parts)
            {
                var payload = Encoding.ASCII.GetBytes(part);
                body.Add((byte)payload.Length);
                body.AddRange(payload);
            }
            bytes.AddRange("PTH0"u8.ToArray());
            bytes.AddRange(U32(body.Count));
            bytes.AddRange(body);
        }
        return bytes;
    }

    private static byte[] File(string[] names, string[] paths, string[] descriptions,
                               string[]? vocabulary = null, int[][]? keywordRows = null)
    {
        var bytes = new List<byte>();
        bytes.AddRange(TextSection(names));
        bytes.AddRange(PathSection(paths));
        bytes.AddRange(TextSection([.. names.Select(_ => "")]));               // section 2, empty
        bytes.AddRange(NumberSection([.. names.Select(_ => new[] { 1088 })])); // section 3, nav ids
        bytes.AddRange(TextSection(descriptions));
        if (vocabulary is not null && keywordRows is not null)
        {
            bytes.AddRange(TextSection(vocabulary));
            bytes.AddRange(NumberSection(keywordRows));
        }
        return [.. bytes];
    }

    private static readonly string[] Names =
        ["Analog Input - Filtering.vi", "Counter - Continuous Output.vi"];

    private static readonly string[] Paths =
    [
        @"DAQmx\Analog Input\Analog Input - Filtering.vi",
        @"DAQmx\Counter Output\Counter - Continuous Output.vi",
    ];

    private static readonly string[] Descriptions =
    [
        "This example demonstrates how to implement configurable filtering.",
        "This example demonstrates how to continuously generate digital pulses\r\n\tusing a counter.",
    ];

    // ---------- the pairing ----------

    [Fact]
    public void EachExampleKeepsItsOwnPathAndDescription()
    {
        var examples = ExternalExampleIndex.Read(File(Names, Paths, Descriptions));

        Assert.Equal(2, examples.Count);
        Assert.Equal(Paths[0], examples[0].RelativePath);
        Assert.StartsWith("This example demonstrates how to implement", examples[0].Description);
        Assert.Equal(Paths[1], examples[1].RelativePath);
        Assert.Contains("digital pulses", examples[1].Description);
    }

    [Fact]
    public void DescriptionsAreCollapsedToOneLine()
    {
        var examples = ExternalExampleIndex.Read(File(Names, Paths, Descriptions));

        Assert.Equal("This example demonstrates how to continuously generate digital pulses using " +
                     "a counter.", examples[1].Description);
    }

    [Fact]
    public void MarkupIsStrippedTheSameWayTheInViDescriptionsAre()
    {
        // The dfdt examples really do say "in the <b>RT CompactRIO Target</b> folder"; leaving
        // that in would make the two sources read differently in one and the same list.
        var examples = ExternalExampleIndex.Read(File(
            Names, Paths, ["Open the <b>RT CompactRIO Target</b> folder.", "Plain."]));

        Assert.Equal("Open the RT CompactRIO Target folder.", examples[0].Description);
    }

    [Fact]
    public void PathComponentsUseTheOneBytePrefixWhileLengthsUseFour()
    {
        // Two prefix widths in one file. Reading the 4-byte lengths as single bytes is what made
        // this look like the .mnu convention at first glance, and would corrupt every string.
        var examples = ExternalExampleIndex.Read(File(Names, Paths, Descriptions));

        Assert.Equal(@"DAQmx\Analog Input\Analog Input - Filtering.vi", examples[0].RelativePath);
    }

    [Fact]
    public void KeywordsComeFromTheInvertedIndex()
    {
        // Section 5 is the vocabulary, section 6 lists per keyword which examples carry it.
        var examples = ExternalExampleIndex.Read(File(
            Names, Paths, Descriptions,
            vocabulary: ["filter", "counter", "9213"],
            keywordRows: [[0], [1], [0, 1]]));

        Assert.Equal(["filter", "9213"], examples[0].Keywords);
        Assert.Equal(["counter", "9213"], examples[1].Keywords);
    }

    [Fact]
    public void TheKeywordSectionsAreOptional()
    {
        // lvexdfd.bin4 stops after six sections; that is normal, not a parse failure.
        var examples = ExternalExampleIndex.Read(File(Names, Paths, Descriptions));

        Assert.Equal(2, examples.Count);
        Assert.All(examples, e => Assert.Empty(e.Keywords));
    }

    // ---------- refusing rather than mis-pairing ----------

    [Fact]
    public void ArraysThatDisagreeYieldNothingRatherThanAGuess()
    {
        // The name must be the last component of its path. When it is not, the arrays are not
        // aligned, and pairing them anyway would give a real example the wrong description.
        var crossed = ExternalExampleIndex.Read(File(
            Names, [Paths[1], Paths[0]], Descriptions));

        Assert.Empty(crossed);
    }

    [Fact]
    public void AShortFileYieldsNothing() =>
        Assert.Empty(ExternalExampleIndex.Read([.. TextSection("Only one section.vi")]));

    [Fact]
    public void GarbageYieldsNothingInsteadOfThrowing() =>
        Assert.Empty(ExternalExampleIndex.Read([1, 2, 3, 4, 5, 6, 7, 8, 9]));

    [Fact]
    public void AFewerDescriptionsThanNamesIsRefused()
    {
        var bytes = new List<byte>();
        bytes.AddRange(TextSection(Names));
        bytes.AddRange(PathSection(Paths));
        bytes.AddRange(TextSection("", ""));
        bytes.AddRange(NumberSection([1088], [1088]));
        bytes.AddRange(TextSection(Descriptions[0]));        // one description for two examples

        Assert.Empty(ExternalExampleIndex.Read([.. bytes]));
    }

    // ---------- the real files, when this machine has them ----------

    /// <summary>
    /// Decoded against all 18 index files on this station. The pairing is only safe because every
    /// name equals the last component of its path and every path resolves to a file that exists;
    /// this re-checks both wherever the station actually has such a file.
    /// </summary>
    [Fact]
    public void RealIndexFilesParseAndTheirPathsResolve()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
        }.Where(r => !string.IsNullOrWhiteSpace(r))
         .Select(r => Path.Combine(r!, "NI", "LVAddons"))
         .Where(Directory.Exists)
         .ToList();
        if (roots.Count == 0) return;                 // no add-ons here; the suite must not need any

        var read = 0;
        foreach (var file in roots.SelectMany(r =>
                     Directory.EnumerateFiles(r, "*.bin4", SearchOption.AllDirectories)))
        {
            var examples = ExternalExampleIndex.Read(System.IO.File.ReadAllBytes(file));
            if (examples.Count == 0) continue;        // an unknown variant is allowed, not fatal
            read++;

            // exbins sits directly inside the examples folder these paths are relative to.
            var examplesFolder = Path.GetDirectoryName(Path.GetDirectoryName(file))!;
            // An exbins index registers .vi AND .lvproj - 528 and 37 across this station.
            Assert.All(examples, e =>
            {
                Assert.True(e.RelativePath.EndsWith(".vi", StringComparison.OrdinalIgnoreCase) ||
                            e.RelativePath.EndsWith(".lvproj", StringComparison.OrdinalIgnoreCase),
                            $"unexpected registration: {e.RelativePath}");
                Assert.True(System.IO.File.Exists(Path.Combine(examplesFolder, e.RelativePath)),
                            $"registered but absent: {e.RelativePath}");
            });
        }
        Assert.True(read > 0, "no exbins index parsed on a station that has LVAddons");
    }
}
