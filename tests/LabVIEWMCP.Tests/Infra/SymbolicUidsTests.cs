using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Symbolic uids sit between an author and code generation, so the tests that matter are the
/// safety ones rather than the feature ones. A wrong number here does not fail - it produces a VI
/// that validates, runs, and is wired to the wrong terminal. Three properties are therefore pinned
/// explicitly: untouched passthrough, injectivity, and no collision with numbers already used.
/// </summary>
public sealed class SymbolicUidsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-symbolic-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string xml)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, xml);
        return path;
    }

    private static XDocument Load(string path) => XDocument.Load(path);

    private static XElement ByName(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute("_name") == name);

    // ---- passthrough ----

    [Fact]
    public void Leaves_a_file_without_symbols_completely_alone()
    {
        var path = Write("plain.xml", """
            <VI _name="X.vi">
              <Constant outputs="value:10.value" type="int32" uid="10" uid_parent="root" value="5"/>
              <Indicator _name="out" inputs="value:10.value" type="int32" uid="20" uid_parent="root"/>
            </VI>
            """);
        var before = File.ReadAllBytes(path);

        var result = SymbolicUids.Prepare(path);

        Assert.False(result.Rewritten);
        Assert.Equal(path, result.PathForLabview);     // the very same file, not a copy
        Assert.Empty(result.Map);
        Assert.Equal(before, File.ReadAllBytes(path)); // and untouched on disk
    }

    [Fact]
    public void Hands_malformed_xml_to_labview_rather_than_reporting_its_own_parse_error()
    {
        // LabVIEW's diagnosis of broken AIXML is the one authors know. Adding this feature must
        // not change the error message of files that never used it.
        var path = Write("broken.xml", "<VI _name=\"X.vi\"><Constant uid=\"10\"></VI>");

        var result = SymbolicUids.Prepare(path);

        Assert.False(result.Rewritten);
        Assert.Equal(path, result.PathForLabview);
    }

    // ---- numbering ----

    [Fact]
    public void Hands_a_path_that_does_not_exist_to_labview_rather_than_throwing()
    {
        // Regression, found by 13 existing tests breaking at once: they pass paths that were
        // never created, and the tool used to forward them so LabVIEW could report the problem.
        // Reading the file first turned that into a DirectoryNotFoundException raised before
        // LabVIEW was asked - a worse message for callers not using symbolic uids at all.
        var missing = Path.Combine(_dir, "nowhere", "gone.xml");

        var result = SymbolicUids.Prepare(missing);

        Assert.False(result.Rewritten);
        Assert.Equal(missing, result.PathForLabview);
        Assert.Empty(result.Map);
    }

    [Fact]
    public void Numbers_symbols_above_every_number_already_in_the_file()
    {
        var path = Write("mixed.xml", """
            <VI _name="X.vi">
              <Constant outputs="value:500.value" type="int32" uid="500" uid_parent="root" value="5"/>
              <Node _name="Increment" inputs="x:500.value" outputs="x+1:inc.x+1" uid="inc" uid_parent="root"/>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);

        Assert.True(result.Rewritten);
        Assert.True(result.Map["inc"] > 500,
            $"'inc' got {result.Map["inc"]}, which collides with the 500 already in the file");
    }

    [Fact]
    public void Gives_every_distinct_symbol_a_distinct_number()
    {
        var path = Write("many.xml", """
            <VI _name="X.vi">
              <Constant outputs="value:a.value" type="int32" uid="a" uid_parent="root" value="1"/>
              <Constant outputs="value:b.value" type="int32" uid="b" uid_parent="root" value="2"/>
              <Node _name="Add" inputs="x:a.value,y:b.value" outputs="x+y:sum.x+y" uid="sum" uid_parent="root"/>
              <Indicator _name="out" inputs="value:sum.x+y" type="int32" uid="c" uid_parent="root"/>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);

        Assert.Equal(4, result.Map.Count);
        Assert.Equal(result.Map.Count, result.Map.Values.Distinct().Count());
    }

    // ---- substitution ----

    [Fact]
    public void Rewrites_net_references_wherever_they_legally_appear()
    {
        var path = Write("nets.xml", """
            <VI _name="X.vi">
              <Constant outputs="value:n.value" type="int32" uid="n" uid_parent="root" value="5"/>
              <Structure _name="For Loop" count="" maxin="n.value" maxout="" uid="loop" uid_parent="root">
                <Tunnel _id="In1" inputs="value:n.value" mode="index" outputs="value:t.value" uid="t" uid_parent="loop"/>
              </Structure>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);
        var document = Load(result.PathForLabview);
        var n = result.Map["n"];
        var loop = result.Map["loop"];

        var structure = document.Descendants("Structure").Single();
        Assert.Equal($"{n}.value", (string?)structure.Attribute("maxin"));
        Assert.Equal(loop.ToString(), (string?)structure.Attribute("uid"));

        var tunnel = document.Descendants("Tunnel").Single();
        Assert.Equal($"value:{n}.value", (string?)tunnel.Attribute("inputs"));
        Assert.Equal(loop.ToString(), (string?)tunnel.Attribute("uid_parent"));
    }

    [Fact]
    public void Rewrites_every_entry_of_a_comma_separated_list()
    {
        var path = Write("list.xml", """
            <VI _name="X.vi">
              <Constant outputs="value:a.value" type="int32" uid="a" uid_parent="root" value="1"/>
              <Constant outputs="value:b.value" type="int32" uid="b" uid_parent="root" value="2"/>
              <Node _name="Add" inputs="x:a.value,y:b.value" outputs="x+y:s.x+y" uid="s" uid_parent="root"/>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);
        var add = ByName(Load(result.PathForLabview), "Add");

        Assert.Equal($"x:{result.Map["a"]}.value,y:{result.Map["b"]}.value",
                     (string?)add.Attribute("inputs"));
    }

    [Fact]
    public void Leaves_root_alone_because_it_is_the_format_s_own_name()
    {
        var path = Write("root.xml", """
            <VI _name="X.vi">
              <Constant outputs="value:a.value" type="int32" uid="a" uid_parent="root" value="1"/>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);

        Assert.DoesNotContain("root", result.Map.Keys);
        Assert.Equal("root",
            (string?)Load(result.PathForLabview).Descendants("Constant").Single()
                .Attribute("uid_parent"));
    }

    [Fact]
    public void Never_touches_an_attribute_that_carries_prose()
    {
        // A description mentioning "read.data" is documentation, not a wire.
        var path = Write("prose.xml", """
            <VI _name="X.vi" description="Wires read.data into the output.">
              <Constant _name="read" description="feeds read.data" outputs="value:read.value"
                        type="string" uid="read" uid_parent="root" value="read.data"/>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);
        var document = Load(result.PathForLabview);
        var constant = document.Descendants("Constant").Single();

        Assert.Equal("Wires read.data into the output.",
                     (string?)document.Root!.Attribute("description"));
        Assert.Equal("feeds read.data", (string?)constant.Attribute("description"));
        Assert.Equal("read.data", (string?)constant.Attribute("value"));
        Assert.Equal("read", (string?)constant.Attribute("_name"));
        // ...while the net reference beside them IS rewritten
        Assert.Equal($"value:{result.Map["read"]}.value", (string?)constant.Attribute("outputs"));
    }

    [Fact]
    public void Leaves_no_symbol_behind_in_the_file_labview_receives()
    {
        var path = Write("complete.xml", """
            <VI _name="X.vi">
              <Control _name="in" conIdx="0" outputs="value:src.value" type="string" uid="src" uid_parent="root" value=""/>
              <Node _name="String Length" inputs="string:src.value" outputs="length:len.length" uid="len" uid_parent="root"/>
              <Indicator _name="out" conIdx="4" inputs="value:len.length" type="int32" uid="sink" uid_parent="root"/>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);
        var text = File.ReadAllText(result.PathForLabview);

        foreach (var symbol in result.Map.Keys)
            Assert.DoesNotContain($"\"{symbol}\"", text);
        Assert.DoesNotContain("src.", text);
        Assert.DoesNotContain("len.", text);
    }

    [Fact]
    public void Refuses_a_uid_that_is_neither_a_number_nor_a_legal_symbol()
    {
        var path = Write("bad.xml", """
            <VI _name="X.vi">
              <Constant outputs="value:a.b.value" type="int32" uid="a.b" uid_parent="root" value="1"/>
            </VI>
            """);

        var error = Assert.Throws<FormatException>(() => SymbolicUids.Prepare(path));

        Assert.Contains("a.b", error.Message);
        Assert.Contains("Dots and colons are reserved", error.Message);
    }

    // ---- messages back ----

    [Fact]
    public void Puts_the_symbol_back_into_a_message_naming_a_uid()
    {
        var map = new Dictionary<string, int> { ["read"] = 501, ["loop"] = 502 };

        Assert.Equal("Object terminal not found for uid=\"read\"",
                     SymbolicUids.Annotate("Object terminal not found for uid=\"501\"", map));
        Assert.Equal("uid loop has no terminal",
                     SymbolicUids.Annotate("uid 502 has no terminal", map));
    }

    [Fact]
    public void Leaves_bare_numbers_alone_so_an_error_code_survives()
    {
        // Turning "-200220" or "Error 1357" into a symbol name would be a worse failure than
        // leaving a number untranslated, so only an explicit uid is rewritten.
        var map = new Dictionary<string, int> { ["read"] = 1357 };

        Assert.Equal("Error 1357 occurred: a file from that path is already in memory",
            SymbolicUids.Annotate(
                "Error 1357 occurred: a file from that path is already in memory", map));
    }

    [Fact]
    public void Returns_a_message_unchanged_when_nothing_was_symbolic()
    {
        var empty = new Dictionary<string, int>();

        Assert.Equal("For Loop: N is not wired",
                     SymbolicUids.Annotate("For Loop: N is not wired", empty));
    }
}
