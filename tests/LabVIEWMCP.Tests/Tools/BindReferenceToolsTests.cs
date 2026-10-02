using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_bind_control_references plans from the VI's export before it touches anything, and judges
/// its result from an export afterwards. Both are tested against the exports of the measured
/// 2026-10-02 run: a scaffold with a `WB Ref` stand-in (ref{LV.WebBrowser}) grafted into a panel
/// holding a native Web Browser control, before the bind, and the same graft after it - executable,
/// with a `VI Server Reference` to the control where the stand-in was. Copied out of that run, not
/// shaped to match the parser. The two come from two grafts of one scaffold, so their uids differ;
/// the wiring signature is uid-independent, which is what makes that comparison fair.
/// </summary>
public sealed class BindReferenceToolsTests
{
    private static XElement Fixture(string name) => XElement.Parse(File.ReadAllText(
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "bindrefs", name)));

    private static readonly List<BindReferenceTools.Binding> Measured = [new("WB Ref", "Web Browser Control")];

    [Fact]
    public void Bindings_parse_in_order_and_a_non_object_is_refused()
    {
        var parsed = BindReferenceTools.ParseBindings("""{"WB Ref":"Web Browser Control","B":"C"}""");
        Assert.Equal(["WB Ref", "B"], parsed!.Select(b => b.StandIn));
        Assert.Equal("Web Browser Control", parsed[0].Control);

        Assert.Null(BindReferenceTools.ParseBindings("[]"));
        Assert.Null(BindReferenceTools.ParseBindings("{}"));
        Assert.Null(BindReferenceTools.ParseBindings("""{"WB Ref":3}"""));
        Assert.Null(BindReferenceTools.ParseBindings("not json"));
    }

    [Fact]
    public void The_measured_graft_plans_clean()
    {
        Assert.Null(BindReferenceTools.Plan(Fixture("before bind.xml"), Measured).Refusal);
    }

    [Fact]
    public void A_stand_in_that_is_not_a_refnum_control_is_refused()
    {
        var plan = BindReferenceTools.Plan(Fixture("before bind.xml"), [new("Return Value", "Web Browser Control")]);

        Assert.Equal("bindingRefused", Kind(plan.Refusal));
        Assert.Contains("not a refnum CONTROL", plan.Refusal!);
    }

    [Fact]
    public void A_missing_control_and_an_unwired_stand_in_are_refused_by_name()
    {
        var vi = Fixture("before bind.xml");
        Assert.Contains("no control with this label",
            BindReferenceTools.Plan(vi, [new("WB Ref", "Gibtsnicht")]).Refusal!);

        // unwire the stand-in: there is no sink to give the reference
        vi.Descendants("Node").Single(n => (string?)n.Attribute("target") == "ExecuteJavaScript")
          .SetAttributeValue("inputs", "reference:,error in (no error):91.value,JavaScript:223.value,arg:," +
                                       "Wait for Return Value?:235.value,Return Non-primitive Types?:");
        Assert.Contains("wired to nothing", BindReferenceTools.Plan(vi, Measured).Refusal!);
    }

    [Fact]
    public void The_measured_result_verifies()
    {
        var check = BindReferenceTools.Verify(Fixture("before bind.xml"), Fixture("after bind.xml"), Measured);

        Assert.True(check.Clean, string.Join("; ", check.Mismatches));
        Assert.Contains("WB Ref", check.Matched);
        Assert.Empty(check.Leftover);
    }

    /// <summary>The control arms: a result whose reference feeds the wrong sink, or that kept the stand-in.</summary>
    [Fact]
    public void A_misrouted_reference_or_a_leftover_stand_in_fails()
    {
        var after = Fixture("after bind.xml");
        var invoke = after.Descendants("Node").Single(n => (string?)n.Attribute("target") == "ExecuteJavaScript");
        invoke.SetAttributeValue("inputs", ((string)invoke.Attribute("inputs")!).Replace("reference:323.Web Browser Control", "reference:"));
        var misrouted = BindReferenceTools.Verify(Fixture("before bind.xml"), after, Measured);
        Assert.False(misrouted.Clean);
        Assert.Single(misrouted.Mismatches);

        var kept = Fixture("before bind.xml");   // nothing was bound at all
        var none = BindReferenceTools.Verify(Fixture("before bind.xml"), kept, Measured);
        Assert.False(none.Clean);
        Assert.Equal(["WB Ref"], none.Leftover);
    }

    [Fact]
    public void The_helper_ships_and_opens_the_panel_before_it_creates_the_reference()
    {
        var helper = File.ReadAllText(Path.Combine(RepoTree.Root, "scripts", BindReferenceTools.HelperFileName));

        // Error 53 on a Web Browser control whose panel is closed, measured
        Assert.True(helper.IndexOf("target=\"FP.Open\"", StringComparison.Ordinal)
                    < helper.IndexOf("target=\"Create Control Ref\"", StringComparison.Ordinal));
        // and a 53 right after FP.Open is retried: the browser needs a moment, measured on a cold start
        Assert.Contains("_name=\"not ready\" outputs=\"value:4605.value\" type=\"int32\" uid=\"4605\" uid_parent=\"4600\" value=\"53\"", helper);
        // and the panel is put back the way it was found - even after a failed step, so the write
        // takes the read's own error rather than the chain's
        Assert.Contains("write+Front Panel Window\\3AOpen", helper);
        Assert.Contains("inputs=\"reference:4401.ref,error in (no error):4225.error out", helper);
        Assert.Contains("target=\"BD.Remove Bad Wires\"", helper);
        Assert.Contains("target=\"Save.Instrument\"", helper);
    }

    private static string? Kind(string? refusal) =>
        refusal is null ? null : JsonNode.Parse(refusal)?["errorKind"]?.GetValue<string>();
}
