using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The stand-in rewrite behind lvai_generate_vi's XNode step. The mapping is the one measured on
/// LabVIEW 2026 on 2026-11-11 (experiments\xnode-2026-11-11): with it, every one of the eleven
/// terminals landed on the XNode terminal of the same meaning after Replace; with the stand-in's
/// `matched substrings` wired, Replace put that wire on another slot and lost the one it displaced.
/// The document is NI's own export of the swapped VI (the Call as NI writes an XNode).
/// </summary>
public sealed class XNodeCallsTests
{
    private const string NiExport = """
        <VI _name="Regex Mapped Probe.vi" description="d">
          <Control _name="in input string" conIdx="0" connection="required" outputs="value:4200.value" type="string" uid="4200" uid_parent="root" value=""/>
          <Call inputs="input string:4200.value,regular expression:4201.value,offset (0):4203.value,error in:4202.value,ignore case? (F):4204.value,multiline? (F):4205.value" outputs="before match:131.before match,whole match:131.whole match,after match:131.after match,offset past match:131.offset past match,error out:131.error out" target="Match Regular Expression.xnode" uid="131" uid_parent="root"/>
        </VI>
        <?lvmcp exec IsReentrant="1" PooledReentrancy="1"?>
        """;

    [Fact]
    public void The_xnode_call_becomes_its_stand_in_with_the_measured_mapping()
    {
        var r = XNodeCalls.Rewrite(NiExport);
        Assert.Null(r.Refusal);
        var (uid, route) = Assert.Single(r.Calls);
        Assert.Equal("131", uid);
        Assert.Contains("target=\"Match Regular Expression_Execute.vi\"", r.Aixml);
        Assert.Contains("error in (no error):4202.value", r.Aixml);
        Assert.Contains("whole match offset:131.offset past match", r.Aixml);   // the net keeps its name
        Assert.Contains("matched substrings:\"", r.Aixml);                    // last output, unwired
        Assert.DoesNotContain(".xnode", r.Aixml);
        Assert.EndsWith("""<?lvmcp exec IsReentrant="1" PooledReentrancy="1"?>""", r.Aixml.TrimEnd());
        Assert.Equal(Path.Combine("vi.lib", "regexp", "Match Regular Expression.xnode"), route.XNodeFile);
    }

    [Fact]
    public void A_capture_group_output_is_refused_by_name()
    {
        var grown = NiExport.Replace("error out:131.error out", "error out:131.error out,submatch 1:131.s1");
        var r = XNodeCalls.Rewrite(grown);
        Assert.NotNull(r.Refusal);
        Assert.Contains("'submatch 1'", r.Refusal);
        Assert.Contains("matched substrings", r.Refusal);
    }

    [Fact]
    public void Wiring_the_stand_ins_own_array_is_refused()
    {
        var r = XNodeCalls.Rewrite(NiExport.Replace("error out:131.error out", "error out:131.error out,matched substrings:131.m"));
        Assert.NotNull(r.Refusal);
    }

    [Fact]
    public void A_document_without_an_xnode_is_generated_as_it_is()
    {
        var path = Path.Combine(Path.GetTempPath(), $"noxnode-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, "<VI _name=\"A.vi\" description=\"d\"/>");
        try
        {
            var (source, xnodes, refusal) = BulkTools.XNodeStandIn(path);
            Assert.Equal(path, source);
            Assert.Empty(xnodes);
            Assert.Null(refusal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_xnode_document_is_generated_from_a_copy_beside_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xnode-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, NiExport);
        string? source = null;
        try
        {
            (source, var xnodes, var refusal) = BulkTools.XNodeStandIn(path);
            Assert.Null(refusal);
            Assert.Single(xnodes);
            Assert.NotEqual(path, source);
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(source));
            Assert.Contains("Match Regular Expression_Execute.vi", File.ReadAllText(source));
            Assert.Contains("Match Regular Expression.xnode", File.ReadAllText(path));    // the original untouched
        }
        finally
        {
            File.Delete(path);
            if (source is not null && File.Exists(source)) File.Delete(source);
        }
    }

    [Fact]
    public void The_node_form_now_names_the_xnode_call_too()
    {
        const string doc = """
            <VI _name="P.vi" description="d">
              <Node _name="Match Regular Expression" uid="4210" uid_parent="root"/>
            </VI>
            """;
        var f = Assert.Single(AixmlCheck.Check(doc), x => x.Code == "xnodeWrittenAsNode");
        Assert.Contains("Match Regular Expression.xnode", f.Message);
        Assert.Contains("Match Regular Expression_Execute.vi", f.Message);
    }
}
