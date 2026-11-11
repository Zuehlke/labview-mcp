using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Two authoring habits the user forum reported (2026-11-11), flagged before LabVIEW is asked. The
/// fixtures are the probe documents of that day (experiments\forum-2026-11-11): `Less?` against a
/// constant labelled "Zero", and an error cluster unbundled for `status` before a boolean function -
/// where the measured alternative (the error wire straight into Not / Or / a stop terminal) ran.
/// </summary>
public sealed class AixmlCheckForumTests
{
    private const string LessThanZero = """
        <VI _name="P.vi" description="d">
          <Control _name="x" outputs="value:4200.value" type="double" uid="4200" uid_parent="root" value="0"/>
          <Constant _name="Zero" outputs="value:4201.value" type="double" uid="4201" uid_parent="root" value="0"/>
          <Node _name="Less?" inputs="x:4200.value,y:4201.value" outputs="x &lt; y?:4202.x &lt; y?" uid="4202" uid_parent="root"/>
        </VI>
        """;

    [Fact]
    public void Less_than_a_zero_constant_names_the_primitive()
    {
        var f = Assert.Single(AixmlCheck.Check(LessThanZero), x => x.Code == "primitiveForConstant");
        Assert.Equal(AixmlCheck.Severity.Warning, f.Severity);
        Assert.Contains("`Less Than 0?`", f.Message);
        Assert.Contains("x:4200.value", f.Message);
    }

    [Fact]
    public void Zero_on_the_x_side_is_the_mirrored_primitive()
    {
        var doc = LessThanZero.Replace("x:4200.value,y:4201.value", "x:4201.value,y:4200.value");
        Assert.Contains("`Greater Than 0?`", Assert.Single(AixmlCheck.Check(doc), x => x.Code == "primitiveForConstant").Message);
    }

    [Fact]
    public void A_non_zero_constant_or_a_string_is_left_alone()
    {
        Assert.DoesNotContain(AixmlCheck.Check(LessThanZero.Replace("value=\"0\"/>\n  <Node", "value=\"5\"/>\n  <Node")
                                                           .Replace("uid=\"4201\" uid_parent=\"root\" value=\"0\"", "uid=\"4201\" uid_parent=\"root\" value=\"5\"")),
                              x => x.Code == "primitiveForConstant");
    }

    [Fact]
    public void Adding_a_constant_one_is_increment()
    {
        var doc = LessThanZero.Replace("_name=\"Less?\"", "_name=\"Add\"").Replace("value=\"0\"/>\n  <Node", "value=\"1\"/>\n  <Node")
                              .Replace("uid=\"4201\" uid_parent=\"root\" value=\"0\"", "uid=\"4201\" uid_parent=\"root\" value=\"1\"");
        Assert.Contains("`Increment`", Assert.Single(AixmlCheck.Check(doc), x => x.Code == "primitiveForConstant").Message);
    }

    private const string StatusThroughNot = """
        <VI _name="P.vi" description="d">
          <Control _name="error in" outputs="value:4200.value" type="cluster{bool.status,int32.code,string.source}" uid="4200" uid_parent="root" value="[false,0,]"/>
          <Node _name="Unbundle By Name" fields="status" inputs="input cluster:4200.value" outputs="status:4242.status" uid="4242" uid_parent="root"/>
          <Node _name="Not" inputs="x:4242.status" outputs=".not. x?:4243.not" uid="4243" uid_parent="root"/>
        </VI>
        """;

    [Fact]
    public void An_unbundled_status_into_not_names_the_error_wire()
    {
        var f = Assert.Single(AixmlCheck.Check(StatusThroughNot), x => x.Code == "errorStatusUnbundled");
        Assert.Contains("`Not`", f.Message);
        Assert.Contains("4200.value", f.Message);
    }

    [Fact]
    public void A_status_that_also_feeds_something_else_is_left_alone()
    {
        var doc = StatusThroughNot.Replace("</VI>",
            "  <Indicator _name=\"failed\" inputs=\"value:4242.status\" type=\"bool\" uid=\"4250\" uid_parent=\"root\" value=\"false\"/>\n</VI>");
        Assert.DoesNotContain(AixmlCheck.Check(doc), x => x.Code == "errorStatusUnbundled");
    }
}
