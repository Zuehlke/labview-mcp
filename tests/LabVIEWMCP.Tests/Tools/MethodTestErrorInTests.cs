using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The `errorIn` case key of lvai_generate_method_test (2026-11-22). The case "an incoming error
/// passes through a class method" could not be generated: `inputs` refuses the error input, and
/// lvai_generate_test cannot feed a class constant into a dispatch input. A test agent building the
/// Tree demo (C:\temp\Demo-Anpassungen) hand-edited the AIXML instead. Since a class without
/// accessors can only be tested on its pane, error out is often all there is to assert.
/// </summary>
public sealed class MethodTestErrorInTests
{
    private const string Err = "cluster{bool.status,int32.code,string.source}";
    private const string Cls = "ref{UDClassInst}";

    [Theory]
    [InlineData("[true,5000,upstream]", "[true,5000,upstream]")]
    [InlineData(" [ TRUE , -1 , x ] ", "[true,-1, x ]")]
    [InlineData("[false,0,]", "[false,0,]")]
    [InlineData("[true,5000,a,b]", @"[true,5000,a\2Cb]")]
    public void An_error_cluster_literal_is_read_into_its_canonical_spelling(string text, string expected) =>
        Assert.Equal(expected, MethodTestTools.ErrorInLiteral(text));

    [Theory]
    [InlineData("true,5000,x")]
    [InlineData("[yes,5000,x]")]
    [InlineData("[true,five,x]")]
    [InlineData("[true,5000]")]
    [InlineData("")]
    public void Anything_else_is_not_an_error_literal(string text) =>
        Assert.Null(MethodTestTools.ErrorInLiteral(text));

    [Fact]
    public void A_case_carries_errorIn_with_expectErrorCode()
    {
        var parsed = MethodTestTools.MethodCaseRequest.ParseAll("""
            [{"method":"Increment Fruits","errorIn":"[true,5000,upstream]","expectErrorCode":5000}]
            """);

        Assert.Equal("[true,5000,upstream]", Assert.Single(parsed).ErrorIn);
        Assert.Equal(5000, parsed[0].ExpectErrorCode);
    }

    [Fact]
    public void A_bad_literal_is_refused_by_name()
    {
        var why = Assert.Throws<ArgumentException>(() => MethodTestTools.MethodCaseRequest.ParseAll("""
            [{"method":"Increment Fruits","errorIn":"5000","expectErrorCode":5000}]
            """));
        Assert.Contains("errorIn", why.Message);
        Assert.Contains("[true,5000,upstream]", why.Message);
    }

    [Fact]
    public void ErrorIn_beside_setup_is_refused_because_setup_feeds_that_input()
    {
        var why = Assert.Throws<ArgumentException>(() => MethodTestTools.MethodCaseRequest.ParseAll("""
            [{"method":"Increment Fruits","errorIn":"[true,1,x]","expectErrorCode":1,
              "setup":[{"vi":"C:\\x\\Reset.vi"}]}]
            """));
        Assert.Contains("\"setup\"", why.Message);
    }

    [Fact]
    public void A_quoted_number_is_not_the_only_kind_check_errorIn_must_be_a_string()
    {
        var why = Assert.Throws<ArgumentException>(() => MethodTestTools.MethodCaseRequest.ParseAll("""
            [{"method":"Increment Fruits","errorIn":[true,5000,"x"],"expectErrorCode":5000}]
            """));
        Assert.Contains("errorIn", why.Message);
    }

    /// <summary>
    /// The diagram: the method's error input gets a constant carrying the GIVEN error, and a case
    /// without the key still gets `no error`. Built against the Tree demo's real method shape.
    /// </summary>
    [Fact]
    public void The_method_is_fed_the_given_error_and_other_cases_keep_no_error()
    {
        var shape = ViTerminals.Parse(
            "<VI _name=\"Tree.lvclass:Increment Fruits.vi\" description=\"\">" +
            $"<Control _name=\"Tree in\" conIdx=\"11\" connection=\"dynamic\" type=\"{Cls}\" outputs=\"value:4200.v\" uid=\"4200\" uid_parent=\"root\" value=\"\"/>" +
            $"<Control _name=\"error in\" conIdx=\"8\" connection=\"required\" type=\"{Err}\" outputs=\"value:4201.v\" uid=\"4201\" uid_parent=\"root\" value=\"\"/>" +
            $"<Indicator _name=\"Tree out\" conIdx=\"3\" connection=\"dynamic\" type=\"{Cls}\" inputs=\"value:4202.v\" uid=\"4202\" uid_parent=\"root\" value=\"\"/>" +
            $"<Indicator _name=\"error out\" conIdx=\"0\" connection=\"recommended\" type=\"{Err}\" inputs=\"value:4203.v\" uid=\"4203\" uid_parent=\"root\" value=\"\"/>" +
            "</VI>")!;
        var call = MethodTestTools.DirectMethodCall.From(shape, @"Tree.lvclass\3AIncrement Fruits.vi").Call!;

        MethodTestTools.MethodCase Case(int slot, string? errorIn) =>
            new(slot, $"case {slot}", "Increment Fruits", @"C:\cls\Tree\Increment Fruits.vi",
                null, null, null, null, null, null, errorIn is null ? 0 : 5000,
                @"C:\cls\Tree\Tree.lvclass", [], ErrorIn: errorIn);

        var suite = XElement.Parse(MethodTestTools.MethodTestAixml(
            @"C:\cls\Tests\Test Tree Methods.vi", "Tree",
            [Case(1, "[true,5000,upstream]"), Case(2, null)], [call, call], [null, null]));

        var incoming = suite.Elements("Constant").Single(c => (string?)c.Attribute("_name") == "error in 1");
        Assert.Equal("[true,5000,upstream]", (string?)incoming.Attribute("value"));
        var calm = suite.Elements("Constant").Single(c => (string?)c.Attribute("_name") == "no error 2");
        Assert.Equal("[false,0,]", (string?)calm.Attribute("value"));

        // and the constant is what the METHOD's error input is wired to
        var net = ((string)incoming.Attribute("outputs")!).Split(':')[1];
        var methodCalls = suite.Elements("Call")
            .Where(c => (string?)c.Attribute("target") == @"Tree.lvclass\3AIncrement Fruits.vi").ToList();
        Assert.Contains(methodCalls, c => ((string)c.Attribute("inputs")!).Contains($"error in:{net}"));
    }
}
