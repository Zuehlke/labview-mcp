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

    /// <summary>The tool's answer for a class file already on disk.</summary>
    private static JsonObject DescribeAt(string path) =>
        (JsonObject)JsonNode.Parse(
            new ClassTools(null!).DescribeClassAsync(path).GetAwaiter().GetResult())!;

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

    /// <summary>
    /// A .lvclass carrying nothing but the one flag that separates an interface from a class.
    /// </summary>
    private static string Sibling(bool isInterface) => $"""
        <?xml version='1.0' encoding='UTF-8'?>
        <LVClass LVVersion="26008000">
        	<Property Name="NI.LVClass.IsInterface" Type="Bool">{(isInterface ? "true" : "false")}</Property>
        </LVClass>
        """;

    /// <summary>
    /// TWO INTERFACES AND NO BASE CLASS, which is `Test Valve` of docs/cold-build-valverig.md. The
    /// tool answered `inheritsFrom: "ILoggable.lvclass"` - an interface, picked because it is
    /// second in a list nobody ordered - and the honest answer is `LabVIEW Object`, with both
    /// links named under `parentLinks`.
    /// </summary>
    [Fact]
    public void Two_interfaces_do_not_become_an_inheritance()
    {
        var dir = Directory.CreateTempSubdirectory("lvclass-interfaces").FullName;
        try
        {
            foreach (var name in new[] { "ILoggable", "IOpenable" })
            {
                Directory.CreateDirectory(Path.Combine(dir, name));
                File.WriteAllText(Path.Combine(dir, name, name + ".lvclass"), Sibling(true));
            }
            Directory.CreateDirectory(Path.Combine(dir, "Test Valve"));
            var path = Path.Combine(dir, "Test Valve", "Test Valve.lvclass");
            File.WriteAllText(path, """
                <?xml version='1.0' encoding='UTF-8'?>
                <LVClass LVVersion="26008000">
                	<Item Name="Parent Libraries" Type="Parent Libraries">
                		<Item Name="ILoggable.lvclass" Type="Parent" URL="../../ILoggable/ILoggable.lvclass"/>
                		<Item Name="IOpenable.lvclass" Type="Parent" URL="../../IOpenable/IOpenable.lvclass"/>
                	</Item>
                	<Item Name="Test Valve.ctl" Type="Class Private Data" URL="Test Valve.ctl"/>
                </LVClass>
                """);

            var answer = DescribeAt(path);

            Assert.Equal("LabVIEW Object", (string?)answer["inheritsFrom"]);
            Assert.True((bool)answer["parentKindsAreComplete"]!);

            var links = (JsonArray)answer["parentLinks"]!;
            Assert.Equal(2, links.Count);
            Assert.All(links, l => Assert.Equal("interface", (string?)l!["kind"]));
            // Nothing is lost: the names are still there, they are simply no longer misread.
            Assert.Equal(["ILoggable.lvclass", "IOpenable.lvclass"],
                         links.Select(l => (string?)l!["name"]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The mixed case, and the one that reaches NI's own shipping examples: an interface FIRST and
    /// the real base class second. `Caller A.lvclass` reports `Abstraction.lvclass` over
    /// `Actor.lvclass` this way, and `Flathead.lvclass` reports `Lever` over `Rotating Tool`.
    /// </summary>
    [Fact]
    public void An_interface_listed_first_is_not_the_base_class()
    {
        var dir = Directory.CreateTempSubdirectory("lvclass-mixed").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Lever"));
            File.WriteAllText(Path.Combine(dir, "Lever", "Lever.lvclass"), Sibling(true));
            Directory.CreateDirectory(Path.Combine(dir, "Rotating Tool"));
            File.WriteAllText(Path.Combine(dir, "Rotating Tool", "Rotating Tool.lvclass"),
                              Sibling(false));

            Directory.CreateDirectory(Path.Combine(dir, "Flathead"));
            var path = Path.Combine(dir, "Flathead", "Flathead.lvclass");
            File.WriteAllText(path, """
                <?xml version='1.0' encoding='UTF-8'?>
                <LVClass LVVersion="26008000">
                	<Item Name="Parent Libraries" Type="Parent Libraries">
                		<Item Name="Lever.lvclass" Type="Parent" URL="../../Lever/Lever.lvclass"/>
                		<Item Name="Rotating Tool.lvclass" Type="Parent" URL="../../Rotating Tool/Rotating Tool.lvclass"/>
                	</Item>
                	<Item Name="Flathead.ctl" Type="Class Private Data" URL="Flathead.ctl"/>
                </LVClass>
                """);

            var answer = DescribeAt(path);

            Assert.Equal("Rotating Tool.lvclass", (string?)answer["inheritsFrom"]);
            Assert.True((bool)answer["parentKindsAreComplete"]!);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The old fixture's parent does not exist on disk, so its kind cannot be settled - and the
    /// link is reported anyway, because answering `LabVIEW Object` over a parent the file plainly
    /// lists would hide a real one. `parentKindsAreComplete` is what marks it as a guess.
    /// </summary>
    [Fact]
    public void An_unopenable_parent_is_still_named_and_flagged()
    {
        var answer = Describe(WithParent, "Hochhaus.lvclass");

        Assert.Equal("Haus.lvclass", (string?)answer["inheritsFrom"]);
        Assert.False((bool)answer["parentKindsAreComplete"]!);
        Assert.Equal("unresolved", (string?)((JsonArray)answer["parentLinks"]!)[0]!["kind"]);
    }
}
