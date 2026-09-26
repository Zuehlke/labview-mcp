using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The scratch names LabVIEW is handed instead of a document's real one.
///
/// VALIDATION USES ONE FIXED NAME, CONVERSION A UNIQUE ONE - since 2026-09-26. NI's validator
/// leaves a VI of the scratch name in memory, and one name per call piled up as 54 unsaved
/// `LVMCP Validate` VIs in LabVIEW's exit dialog. A validation under a name already used was
/// measured to answer exactly as a fresh one; a failed CONVERT does burn its name, so conversion
/// keeps minting.
/// </summary>
public sealed class ValidationScratchTests
{
    private static string Document(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scratch-test-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, $"""<VI _name="{name}" description="d"></VI>""");
        return path;
    }

    [Fact]
    public void Every_validation_gets_the_same_fixed_name()
    {
        var a = Document("Real A.vi");
        var b = Document("Real B.vi");
        try
        {
            using var first = ValidationScratch.Create(a, preserveName: false);
            using var second = ValidationScratch.Create(b, preserveName: false);

            Assert.Equal("LVMCP Validate.vi", first.ValidatedAs);
            Assert.Equal(first.ValidatedAs, second.ValidatedAs);
            Assert.Contains("_name=\"LVMCP Validate.vi\"", File.ReadAllText(first.Path));
            Assert.Equal(["Real A.vi"], first.OriginalNames);   // the real name is only remembered
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void A_conversion_scratch_stays_unique_because_a_failed_convert_burns_its_name()
    {
        var doc = Document("Real.vi");
        try
        {
            using var first = ValidationScratch.Create(doc, preserveName: false,
                                                       prefix: "LVMCP Convert", unique: true);
            using var second = ValidationScratch.Create(doc, preserveName: false,
                                                        prefix: "LVMCP Convert", unique: true);

            Assert.StartsWith("LVMCP Convert ", first.ValidatedAs);
            Assert.NotEqual(first.ValidatedAs, second.ValidatedAs);
        }
        finally { File.Delete(doc); }
    }
}
