using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// What <c>lvai_describe_class</c> answers for <c>inheritsFrom</c>.
///
/// A ROOT CLASS MUST NOT INHERIT FROM ITSELF. <c>NI.LVClass.Geneology</c> lists the class among its
/// own ancestors when there is no parent, so reading <c>Ancestors[0]</c> straight off reported
/// <c>Haus.lvclass</c> as inheriting from <c>Haus.lvclass</c> - found 2026-08-28 by two independent
/// runs of the class agent, which both stopped to flag it. It was never *silently* wrong, because
/// <c>ancestorSource</c> says which representation the answer came from, but a caller reading the
/// one field would draw a false conclusion, and <c>lvai_create_class</c>'s own verify step had
/// always filtered the self entry out. The two tools now agree.
/// </summary>
public class ClassToolsDescribeTests
{
    /// <summary>A class file with an explicit Parent Libraries item - the authoritative form.</summary>
    private const string WithParent = """
        <?xml version='1.0' encoding='UTF-8'?>
        <LVClass LVVersion="26008000">
        	<Property Name="NI.Lib.Version" Type="Str">1.0.0.0</Property>
        	<Item Name="Parent Libraries" Type="Parent Libraries">
        		<Item Name="Haus.lvclass" Type="Parent" URL="../Haus.lvclass"/>
        	</Item>
        	<Item Name="Hochhaus.ctl" Type="Class Private Data" URL="Hochhaus.ctl"/>
        </LVClass>
        """;

    /// <summary>
    /// A root class as LabVIEW's provider writes one: no Parent Libraries item at all. The
    /// Geneology property is the encoded form of a one-entry ancestry - the class itself.
    /// </summary>
    private const string RootWithSelfGeneology = """
        <?xml version='1.0' encoding='UTF-8'?>
        <LVClass LVVersion="26008000">
        	<Property Name="NI.Lib.Version" Type="Str">1.0.0.0</Property>
        	<Item Name="Haus.ctl" Type="Class Private Data" URL="Haus.ctl"/>
        </LVClass>
        """;

    private static JsonObject Describe(string classFileText, string fileName)
    {
        var dir = Directory.CreateTempSubdirectory("lvclass-describe").FullName;
        try
        {
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, classFileText);
            var answer = new ClassTools(null!).DescribeClassAsync(path).GetAwaiter().GetResult();
            return (JsonObject)JsonNode.Parse(answer)!;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_child_reports_its_parent()
    {
        var answer = Describe(WithParent, "Hochhaus.lvclass");

        Assert.True((bool)answer["ok"]!);
        Assert.Equal("Haus.lvclass", (string?)answer["inheritsFrom"]);
        Assert.Equal("Parent Libraries items (plain text)", (string?)answer["ancestorSource"]);
    }

    [Fact]
    public void A_root_class_reports_LabVIEW_Object_not_itself()
    {
        var answer = Describe(RootWithSelfGeneology, "Haus.lvclass");

        Assert.True((bool)answer["ok"]!);
        Assert.NotEqual("Haus.lvclass", (string?)answer["inheritsFrom"]);
        Assert.Equal("LabVIEW Object", (string?)answer["inheritsFrom"]);
    }
}
