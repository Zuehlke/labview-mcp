using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Values across LabVIEW's ActiveX boundary, in the AIXML spelling. The types are the reader's own
/// (experiments/aixml-without-grpc/tests uses every one of them); the conversions were run live over
/// ActiveX on 2026-10-10 - eleven of eleven cases of the composer's suite matched its expectations,
/// enum by name and by index, a cluster and an array among them. These tests pin the conversion
/// rules themselves, so they hold without LabVIEW.
/// </summary>
public sealed class ComValuesTests
{
    private static object? Convert(string text, string type)
    {
        var value = ComValues.FromText(text, type, out var problem);
        Assert.True(problem is null, problem);
        return value;
    }

    private static string Refusal(string text, string type)
    {
        Assert.Null(ComValues.FromText(text, type, out var problem));
        Assert.NotNull(problem);
        return problem!;
    }

    [Fact]
    public void Scalars_take_their_own_dotnet_type()
    {
        Assert.Equal(3.25, Convert("3.25", "double"));
        Assert.Equal(42, Convert("42", "int32"));
        Assert.Equal(7, Convert("7", "uint16"));
        Assert.Equal(true, Convert("TRUE", "bool"));
        Assert.Equal("  hi there  ", Convert("  hi there  ", "string"));
        Assert.Equal(@"C:\data\in.csv", Convert(@"C:\data\in.csv", "path"));
    }

    [Fact]
    public void A_number_that_does_not_fit_its_integer_is_refused_not_wrapped()
    {
        Assert.Contains("whole number", Refusal("2.5", "int32"));
        Assert.Contains("does not fit", Refusal("300", "uint8"));
        Assert.Contains("does not fit", Refusal("-1", "uint16"));
        Assert.Contains("decimal point", Refusal("3,5", "double"));
    }

    /// <summary>A name-else-index fallback turns a typo into item 0; refused instead.</summary>
    [Fact]
    public void An_enum_takes_an_item_name_or_an_index_and_nothing_else()
    {
        Assert.Equal(2, Convert("Auto", "uint16{Off,On,Auto}"));
        Assert.Equal(1, Convert("1", "uint16{Off,On,Auto}"));
        Assert.Contains("neither an item", Refusal("auto", "uint16{Off,On,Auto}"));
        Assert.Contains("neither an item", Refusal("3", "uint16{Off,On,Auto}"));
    }

    [Fact]
    public void An_array_crosses_typed_and_a_cluster_as_a_variant_array()
    {
        Assert.Equal(new[] { 1.5, 2, 3 }, Convert("[1.5,2,3]", "array{double}"));
        Assert.Equal(new[] { 4, 5 }, Convert("[4,5]", "array{int32}"));
        Assert.Equal(Array.Empty<double>(), Convert("[]", "array{double}"));

        // A double[] for a cluster is accepted by LabVIEW and silently ignored - measured in the
        // experiment - so a cluster must be object[], element by element in its own type.
        var cluster = Assert.IsType<object?[]>(Convert("[1.5,2,text]", "cluster{double.x,int32.n,string.t}"));
        Assert.Equal(new object?[] { 1.5, 2, "text" }, cluster);
        Assert.Contains("2 elements", Refusal("[1]", "cluster{double.x,double.y}"));
    }

    [Fact]
    public void What_is_not_covered_is_refused_by_name()
    {
        Assert.Contains("2D array", Refusal("[[1,2],[3,4]]", "array{array{double}}"));
        Assert.Contains("cannot be set over COM", Refusal("", "doublewaveform"));
        Assert.Contains("cannot be set over COM", Refusal("", "ref{LV.VI}"));
    }

    [Fact]
    public void Values_read_back_are_written_as_AIXML_literals()
    {
        Assert.Equal("7.25", ComValues.Format(7.25));
        Assert.Equal("42", ComValues.Format(42));
        Assert.Equal("true", ComValues.Format(true));
        Assert.Equal("[1.5,2,3]", ComValues.Format(new[] { 1.5, 2, 3 }));
        Assert.Equal("[false,0,]", ComValues.Format(new object[] { false, 0, "" }));
        Assert.Equal("[[1,2],[3,4]]", ComValues.Format(new double[,] { { 1, 2 }, { 3, 4 } }));
    }

    [Fact]
    public void Type_spellings_split_at_the_top_level_only()
    {
        Assert.Equal("cluster{double.x,double.y}", ComValues.FieldType("cluster{double.x,double.y}.p"));
        Assert.Equal(["string.name", "cluster{double.x,double.y}.p"],
                     ComValues.SplitTop("string.name,cluster{double.x,double.y}.p"));
        Assert.Equal(["false", "0", ""], ComValues.Literal("[false,0,]"));
        Assert.Null(ComValues.Literal("1,2"));
    }
}
