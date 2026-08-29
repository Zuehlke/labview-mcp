using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The CLASS round-trip generator, offline: the sockets it authors and the test diagram it wires.
/// No LabVIEW involved.
///
/// This file carries more weight than most, because the route it describes cannot be checked by
/// running the tool in the session that writes it - a build takes the lvai_* tools away until the
/// client restarts. Everything here is therefore a property that must hold by construction:
/// the socket panes must be NI's accessor layout or the swap breaks the wires, the seed constant
/// must be a path with the label the swap searches for, and the written value must be the SAME
/// constant the assertion compares against.
/// </summary>
public sealed class GenerateClassTestTests
{
    private static TestTools.ClassCase Case(int slot, string field, string type, string value) =>
        new(slot, field, type, value, $"{field} round trip",
            $@"C:\cls\Write {field}.vi", $@"C:\cls\Read {field}.vi", @"C:\cls\Netzteil.lvclass");

    private static XElement Author(params TestTools.ClassCase[] cases) =>
        XElement.Parse(TestTools.ClassTestAixml(@"C:\cls\Test Netzteil.vi", "Netzteil", cases));

    // ------------------------------------------------------------------ the sockets

    [Fact]
    public void WriteSocket_PutsTheClassTerminalsOnNIsAccessorSlots()
    {
        var xml = XElement.Parse(TestTools.SocketAixml("LVMCP ClsW1.vi", "string", write: true));

        var slots = xml.Elements()
            .Where(e => e.Attribute("conIdx") is not null)
            .ToDictionary(e => (string)e.Attribute("_name")!, e => (string)e.Attribute("conIdx")!);

        // NI's wizard puts the class in at 11, the data in at 10 and the class out at 3. A socket
        // that disagrees moves the wires onto other terminals when Replace swaps the node in.
        Assert.Equal("11", slots["obj in"]);
        Assert.Equal("10", slots["value"]);
        Assert.Equal("3", slots["obj out"]);
    }

    [Fact]
    public void ReadSocket_PutsTheDataOutputAtTwo_NotAtTen()
    {
        var xml = XElement.Parse(TestTools.SocketAixml("LVMCP ClsR1.vi", "double", write: false));

        var slots = xml.Elements()
            .Where(e => e.Attribute("conIdx") is not null)
            .ToDictionary(e => (string)e.Attribute("_name")!, e => (string)e.Attribute("conIdx")!);

        Assert.Equal("11", slots["obj in"]);
        Assert.Equal("3", slots["obj out"]);
        Assert.Equal("2", slots["value"]);
    }

    [Fact]
    public void ClassTerminalsAreAlwaysPaths_BecauseAixmlRefusesTheClass()
    {
        foreach (var write in new[] { true, false })
        {
            var xml = XElement.Parse(TestTools.SocketAixml("LVMCP Cls1.vi", "int32", write));
            foreach (var name in new[] { "obj in", "obj out" })
                Assert.Equal("path", (string?)xml.Elements()
                    .First(e => (string?)e.Attribute("_name") == name).Attribute("type"));
        }
    }

    [Fact]
    public void TheDataTerminalCarriesTheFieldsType_NotAVariant()
    {
        // A Variant socket would look interchangeable and is not: the constant is wired while the
        // terminal is still the socket's type, and after Replace a Variant meeting a `string`
        // terminal is a type conflict LabVIEW will not coerce away.
        var xml = XElement.Parse(TestTools.SocketAixml("LVMCP ClsW2.vi", "double", write: true));
        Assert.Equal("double", (string?)xml.Elements()
            .First(e => (string?)e.Attribute("_name") == "value").Attribute("type"));
    }

    // ------------------------------------------------------------------ the test diagram

    [Fact]
    public void EachCaseGetsItsOwnSocketPair_SoTheSwapCanTellThemApart()
    {
        var root = Author(Case(1, "Hersteller", "string", "Fluke"),
                          Case(2, "Modell", "string", "PS 3010"));

        var targets = root.Elements("Call")
            .Select(c => (string)c.Attribute("target")!)
            .Where(t => t.StartsWith("LVMCP Cls", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(4, targets.Count);
        Assert.Equal(4, targets.Distinct().Count());
        Assert.Contains("LVMCP ClsW1.vi", targets);
        Assert.Contains("LVMCP ClsR2.vi", targets);
    }

    [Fact]
    public void EveryCaseHasItsOwnSeedConstant_AsAPathWithTheLabelTheSwapSearchesFor()
    {
        var root = Author(Case(1, "Hersteller", "string", "Fluke"),
                          Case(2, "Modell", "string", "PS 3010"));

        var seeds = root.Elements("Constant")
            .Where(c => (string?)c.Attribute("type") == "path")
            .Select(c => (string)c.Attribute("_name")!)
            .ToList();

        // lvai_swap_subvis finds a constant by its block diagram label, and AIXML's _name IS that
        // label. A seed the swap cannot find stays a path, and the dynamic dispatch input - a
        // REQUIRED terminal - is then wired with the wrong type.
        Assert.Equal(["objekt 1", "objekt 2"], seeds);
    }

    [Fact]
    public void TheWrittenValueIsTheSameConstantTheAssertionCompares()
    {
        var root = Author(Case(1, "Max Spannung V", "double", "30"));

        var write = root.Elements("Call")
            .First(c => ((string)c.Attribute("target")!).StartsWith("LVMCP ClsW",
                                                                    StringComparison.Ordinal));
        var assertion = root.Elements("Call")
            .First(c => ((string)c.Attribute("target")!).Contains("Assert Equal"));

        // "value:<uid>.value" on the write, "Expected:<uid>.value" on the assertion - the SAME uid.
        var written = Wire(write, "value");
        var expected = Wire(assertion, "Expected");
        Assert.Equal(written, expected);

        // And the expectation is a real constant of the field's type, not a restatement.
        var constant = root.Elements("Constant")
            .First(c => (string)c.Attribute("uid")! == written.Split('.')[0]);
        Assert.Equal("double", (string?)constant.Attribute("type"));
        Assert.Equal("30", (string?)constant.Attribute("value"));
    }

    [Fact]
    public void TheReadSocketTakesItsObjectFromTheWriteSocket()
    {
        var root = Author(Case(1, "Hersteller", "string", "Fluke"));

        var write = root.Elements("Call").First(
            c => (string)c.Attribute("target")! == "LVMCP ClsW1.vi");
        var read = root.Elements("Call").First(
            c => (string)c.Attribute("target")! == "LVMCP ClsR1.vi");

        var produced = ((string)write.Attribute("outputs")!)
            .Split(',').First(o => o.StartsWith("obj out:", StringComparison.Ordinal))[8..];
        Assert.Equal(produced, Wire(read, "obj in"));
    }

    [Fact]
    public void EveryAssertionHangsOffDefineTest_NotOffThePreviousOne()
    {
        // The same defect the plain generator has pinned: chaining them is the tidier diagram and
        // silently loses cases, because a node does not execute with an incoming error.
        var root = Author(Case(1, "Hersteller", "string", "Fluke"),
                          Case(2, "Modell", "string", "PS 3010"),
                          Case(3, "Ausgang aktiv", "bool", "TRUE"));

        var define = root.Elements("Call")
            .First(c => ((string)c.Attribute("target")!).Contains("Define Test"))
            .Attribute("uid")!.Value;

        var assertions = root.Elements("Call")
            .Where(c => ((string)c.Attribute("target")!).Contains("Assert Equal"))
            .ToList();

        Assert.Equal(3, assertions.Count);
        foreach (var assertion in assertions)
            Assert.Equal($"{define}.error out", Wire(assertion, "error in (no error)"));
    }

    // ------------------------------------------------------------------ the cases

    [Fact]
    public void ARepeatedFieldIsRefused()
    {
        var bad = Assert.Throws<ArgumentException>(() => TestTools.ClassCaseRequest.ParseAll(
            """[{"field":"Hersteller","value":"a"},{"field":"Hersteller","value":"b"}]"""));
        Assert.Contains("Hersteller", bad.Message);
    }

    [Fact]
    public void ACaseWithNoValueIsRefused_BecauseARoundTripHasNothingToAssert()
    {
        var bad = Assert.Throws<ArgumentException>(
            () => TestTools.ClassCaseRequest.ParseAll("""[{"field":"Hersteller"}]"""));
        Assert.Contains("value", bad.Message);
    }

    [Fact]
    public void TypeIsOptional_AndSurvivesWhenGiven()
    {
        var cases = TestTools.ClassCaseRequest.ParseAll(
            """[{"field":"Phasenzahl","value":"3","type":"int32","label":"drei Phasen"}]""");
        Assert.Equal("int32", cases[0].Type);
        Assert.Equal("drei Phasen", cases[0].Label);

        var derived = TestTools.ClassCaseRequest.ParseAll("""[{"field":"X","value":"1"}]""");
        Assert.Null(derived[0].Type);
        Assert.Null(derived[0].Label);
    }

    private static string Wire(XElement call, string terminal) =>
        ((string)call.Attribute("inputs")!)
        .Split(',')
        .First(i => i.StartsWith(terminal + ":", StringComparison.Ordinal))[(terminal.Length + 1)..];
}
