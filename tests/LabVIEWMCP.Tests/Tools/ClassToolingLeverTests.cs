using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The five tools built on 2026-09-02 out of a measured HAL-class run, offline.
///
/// Everything here is a property that must hold by CONSTRUCTION, because none of it can be checked
/// by running the tool in the session that writes it: a build takes the lvai_* tools away until the
/// client restarts. So the checks are on the parts that need no LabVIEW - the verdict a `.ctl`
/// earns, the literal a compound type gets, the AIXML a method suite is authored as, and every
/// argument refusal that is supposed to cost a message rather than a half-edited class.
/// </summary>
public sealed class ClassToolingLeverTests
{
    // ================================================================== lvai_describe_ctl

    /// <summary>An extracted `.ctl` as pylabview renders it, with just the attributes we read.</summary>
    private static XElement Ctl(string typeDefVi = "0", string strict = "0",
                                string privateData = "0", string instrument = "Control",
                                string wrappedType = "Refnum", string? label = "DAQmx Task Name")
    {
        var descriptor = new XElement("TypeDesc",
            new XAttribute("Type", wrappedType),
            new XAttribute("RefType", "UsrDefndTag"),
            new XAttribute("TypeName", "NIDAQ"));
        if (label is not null) descriptor.Add(new XAttribute("Label", label));

        return new XElement("RSRC",
            new XElement("LVSR",
                new XElement("Section",
                    new XElement("Execution",
                        new XAttribute("TypeDefVI", typeDefVi),
                        new XAttribute("StrictTypeDefVI", strict)),
                    new XElement("Execution2",
                        new XAttribute("IsPrivateDataForUDClass", privateData)),
                    new XElement("Instrument", new XAttribute("Type", instrument)))),
            new XElement("VCTP",
                new XElement("Section",
                    descriptor,
                    new XElement("TopLevel",
                        new XElement("TypeDesc",
                            new XAttribute("Index", "1"),
                            new XAttribute("FlatTypeID", "0"))))));
    }

    [Fact]
    public void NIsOwnDaqmxTaskControlIsNotATypedefAndIsRefusedAsABindingSource()
    {
        // THE MEASUREMENT THIS TOOL EXISTS FOR, 2026-09-02. Two Replace calls answered
        // `error out = 0`, installed the right types, and bound NOTHING, because
        // `DAQmx Task Name NI_Silver.ctl` and `errclust.llb\Error Cluster.ctl` are both
        // TypeDefVI="0". Success and failure are indistinguishable from the calling side.
        var answer = CtlTools.Describe(Ctl(), @"C:\ctl\DAQmx Task Name NI_Silver.ctl");

        Assert.Equal(0, answer["controlVIType"]!.GetValue<int>());
        Assert.Equal("not a typedef", answer["controlVITypeName"]!.GetValue<string>());
        Assert.False(answer["isTypedef"]!.GetValue<bool>());
        Assert.False(answer["bindable"]!.GetValue<bool>());
        Assert.Contains("not a typedef", answer["whyNotBindable"]!.GetValue<string>());
    }

    [Fact]
    public void APlainTypedefIsBindable()
    {
        var answer = CtlTools.Describe(Ctl(typeDefVi: "1"), @"C:\ctl\Borkenkaefer.ctl");

        Assert.Equal(1, answer["controlVIType"]!.GetValue<int>());
        Assert.True(answer["isTypedef"]!.GetValue<bool>());
        Assert.False(answer["isStrictTypedef"]!.GetValue<bool>());
        Assert.True(answer["bindable"]!.GetValue<bool>());
        Assert.Null(answer["whyNotBindable"]);
    }

    [Fact]
    public void AStrictTypedefReportsAsStrictAndStillBinds()
    {
        var answer = CtlTools.Describe(Ctl(typeDefVi: "1", strict: "1"), @"C:\ctl\PFColorctl.ctl");

        Assert.Equal(2, answer["controlVIType"]!.GetValue<int>());
        Assert.True(answer["isStrictTypedef"]!.GetValue<bool>());
        Assert.True(answer["bindable"]!.GetValue<bool>());
    }

    [Fact]
    public void APrivateDataControlWinsOverTheOtherTwoFlagsAndIsRefused()
    {
        // A private data control carries StrictTypeDefVI as well, so a rule that checked the
        // typedef flags first would call it a strict typedef and send the caller to Replace -
        // which answers Error 1073 on one. The order of the checks IS the correctness here.
        var answer = CtlTools.Describe(Ctl(typeDefVi: "1", strict: "1", privateData: "1"),
                                       @"C:\cls\Netzteil.ctl");

        Assert.Equal(3, answer["controlVIType"]!.GetValue<int>());
        Assert.Equal("class private data", answer["controlVITypeName"]!.GetValue<string>());
        Assert.True(answer["isClassPrivateData"]!.GetValue<bool>());
        Assert.False(answer["bindable"]!.GetValue<bool>());
        Assert.Contains("1073", answer["whyNotBindable"]!.GetValue<string>());
    }

    [Fact]
    public void AViIsReportedAsNotAControlRatherThanAsABadTypedef()
    {
        var answer = CtlTools.Describe(Ctl(instrument: "VI"), @"C:\vi\Something.vi");

        Assert.False(answer["isControl"]!.GetValue<bool>());
        Assert.False(answer["bindable"]!.GetValue<bool>());
        Assert.Contains("not a control", answer["whyNotBindable"]!.GetValue<string>());
    }

    [Fact]
    public void TheWrappedTypeAndItsDistinguishingAttributesAreReported()
    {
        // `Type="Refnum"` alone does not say WHICH refnum, and the DAQmx task identity lives in
        // RefType/Ident/TypeName. A caller authoring a constant for this terminal needs them.
        var answer = CtlTools.Describe(Ctl(), @"C:\ctl\Task.ctl");

        Assert.Equal("Refnum", answer["wrappedType"]!.GetValue<string>());
        Assert.Equal("DAQmx Task Name", answer["controlLabel"]!.GetValue<string>());
        Assert.Contains("TypeName=NIDAQ", answer["wrappedTypeDetail"]!.GetValue<string>());
    }

    // ================================================================== lvai_bind_class_fields

    /// <summary>A private data cluster as pylabview renders it: field 1 bound, the others bare.</summary>
    private static XElement PrivateData() =>
        new("RSRC",
            new XElement("VCTP",
                new XElement("Section",
                    // FlatTypeID 0: the cluster itself, referenced from TopLevel index 1.
                    new XElement("TypeDesc",
                        new XAttribute("Type", "Cluster"),
                        new XElement("TypeDesc", new XAttribute("TypeID", "1")),
                        new XElement("TypeDesc", new XAttribute("TypeID", "2")),
                        new XElement("TypeDesc", new XAttribute("TypeID", "3"))),
                    new XElement("TypeDesc",
                        new XAttribute("Type", "String"),
                        new XAttribute("Label", "Physical Channel")),
                    new XElement("TypeDesc",
                        new XAttribute("Type", "TypeDef"),
                        new XAttribute("Label", "Task Reference"),
                        new XElement("Label", "NI_Silver.lvlib"),
                        new XElement("Label", "DAQmx Task Name NI_Silver.ctl")),
                    new XElement("TypeDesc",
                        new XAttribute("Type", "NumFloat64"),
                        new XAttribute("Label", "Sample Rate")),
                    new XElement("TopLevel",
                        new XElement("TypeDesc",
                            new XAttribute("Index", "1"),
                            new XAttribute("FlatTypeID", "0"))))));

    [Fact]
    public void ThePrivateDataFieldsAreReadInFieldOrderWithTheirLabels()
    {
        var fields = ClassBindTools.PrivateDataFields.Parse(PrivateData());

        Assert.Null(fields.Unavailable);
        Assert.Equal(["Physical Channel", "Task Reference", "Sample Rate"], fields.Labels);
    }

    [Fact]
    public void AlreadyBoundFieldsAreRecognisedAndNameTheirCtl()
    {
        // This is what turns "the helper said error 0" into "the file says it bound". Only field 1
        // is a TypeDef, and its .ctl name is the LAST Label child - the first names the library.
        var fields = ClassBindTools.PrivateDataFields.Parse(PrivateData());

        Assert.Equal([1], fields.BoundTypedefs);
        Assert.Equal("DAQmx Task Name NI_Silver.ctl", fields.TypedefName(1));
        Assert.Null(fields.TypedefName(0));
        Assert.Null(fields.TypedefName(99));   // out of range must not throw
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"field\":\"x\"}")]                                  // an object, not an array
    [InlineData("[{\"field\":\"Task Reference\"}]")]                   // no ctlPath
    [InlineData("[{\"ctlPath\":\"C:\\\\x.ctl\"}]")]                    // neither field nor index
    public void AMalformedBindingIsRefusedBeforeAnythingIsTouched(string json) =>
        // pylv_apply refuses a bad operation before the extract for the same reason: a typo should
        // cost a message, never a half-edited class.
        Assert.Throws<ArgumentException>(() => ClassBindTools.BindingRequest.ParseAll(json));

    [Fact]
    public void TwoBindingsForTheSameFieldAreRefused()
    {
        var json = """
            [{"field":"Task Reference","ctlPath":"C:\\a.ctl"},
             {"field":"Task Reference","ctlPath":"C:\\b.ctl"}]
            """;
        var bad = Assert.Throws<ArgumentException>(
            () => ClassBindTools.BindingRequest.ParseAll(json));
        Assert.Contains("same field", bad.Message);
    }

    [Fact]
    public void AFieldMayBeNamedOrNumbered()
    {
        var parsed = ClassBindTools.BindingRequest.ParseAll("""
            [{"field":"Task Reference","ctlPath":"C:\\a.ctl"},
             {"fieldIndex":2,"ctlPath":"C:\\b.ctl"}]
            """);

        Assert.Equal("Task Reference", parsed[0].Field);
        Assert.Null(parsed[0].FieldIndex);
        Assert.Equal(2, parsed[1].FieldIndex);
    }

    // ================================================================== lvai_add_class_method

    [Fact]
    public void AMethodWithNoClassTerminalsIsRefusedWithTheReasonSpelledOut()
    {
        // Without classTerminals the VI is not a class method at all - it would be made a member
        // with a `path` pane, which links, compiles and is useless.
        var bad = Assert.Throws<ArgumentException>(() => ClassMethodTools.MethodRequest.ParseAll(
            """[{"vi":"C:\\cls\\Initialize.vi"}]"""));
        Assert.Contains("classTerminals", bad.Message);
    }

    [Fact]
    public void TheViPathIsDerivedFromTheAixmlWhenOnlyOneIsGiven()
    {
        var parsed = ClassMethodTools.MethodRequest.ParseAll(
            """[{"aixml":"C:\\x\\Initialize.xml","classTerminals":["obj in","obj out"]}]""");

        Assert.Equal(@"C:\x\Initialize.vi", parsed[0].Vi);
        Assert.Equal(@"C:\x\Initialize.xml", parsed[0].Aixml);
    }

    [Fact]
    public void DispatchTerminalsMayBeNamesOrIndicesButNamesAreNotNumbers()
    {
        var byName = ClassMethodTools.MethodRequest.ParseAll(
            """[{"vi":"C:\\a.vi","classTerminals":["obj in"],"dispatchTerminals":["obj in"]}]""");
        Assert.Equal(["obj in"], byName[0].DispatchTerminals);
        Assert.Null(byName[0].DispatchTerminalIndices);

        var byIndex = ClassMethodTools.MethodRequest.ParseAll(
            """[{"vi":"C:\\a.vi","classTerminals":["obj in"],"dispatchTerminalIndices":[11,3]}]""");
        Assert.Equal([11, 3], byIndex[0].DispatchTerminalIndices);

        // A name in the numeric field is the likely slip, and it must not be silently dropped.
        Assert.Throws<ArgumentException>(() => ClassMethodTools.MethodRequest.ParseAll(
            """[{"vi":"C:\\a.vi","classTerminals":["obj in"],"dispatchTerminalIndices":["obj in"]}]"""));
    }

    [Fact]
    public void AStaticMemberIsExpressedByNamingNoDispatchTerminals()
    {
        // Not every member dispatches - an LUnit test method is a static one - so "no dispatch
        // terminals" has to be a legal request rather than an omission.
        var parsed = ClassMethodTools.MethodRequest.ParseAll(
            """[{"vi":"C:\\a.vi","classTerminals":["obj in","obj out"]}]""");

        Assert.Null(parsed[0].DispatchTerminals);
        Assert.Null(parsed[0].DispatchTerminalIndices);
    }

    [Fact]
    public void TwoMethodsCannotNameTheSameVi()
    {
        var bad = Assert.Throws<ArgumentException>(() => ClassMethodTools.MethodRequest.ParseAll(
            """
            [{"vi":"C:\\a.vi","classTerminals":["obj in"]},
             {"vi":"C:\\a.vi","classTerminals":["obj in"]}]
            """));
        Assert.Contains("same .vi", bad.Message);
    }

    // ================================================================== lvai_generate_method_test

    private static MethodTestTools.MethodCase ErrorCase(int slot, string method, int code) =>
        new(slot, $"{method} reports {code}", method, $@"C:\cls\{method}.vi",
            null, null, null, null, null, null, code, @"C:\cls\Daq.lvclass");

    private static MethodTestTools.MethodCase WireCase(int slot, string method, string field) =>
        new(slot, $"{field} survives {method}", method, $@"C:\cls\{method}.vi",
            field, $@"C:\cls\Write {field}.vi", field, $@"C:\cls\Read {field}.vi",
            "double", "10.0", null, @"C:\cls\Daq.lvclass");

    private static XElement Suite(params MethodTestTools.MethodCase[] cases) =>
        XElement.Parse(MethodTestTools.MethodTestAixml(@"C:\cls\Test Daq Methods.vi", "Daq", cases));

    [Fact]
    public void AMethodSocketCarriesTheClassSlotsAndARealErrorPair()
    {
        var xml = XElement.Parse(MethodTestTools.MethodSocketAixml("LVMCP Mth1.vi"));

        var slots = xml.Elements()
            .Where(e => e.Attribute("conIdx") is not null)
            .ToDictionary(e => (string)e.Attribute("_name")!, e => (string)e.Attribute("conIdx")!);

        // 11 in and 3 out are NI's accessor slots, which is where the real method's wires are, so
        // {LV.SubVI} Replace lands them on the same terminals.
        Assert.Equal("11", slots["obj in"]);
        Assert.Equal("3", slots["obj out"]);

        // The class terminals are paths because AIXML refuses UDClassInst; the ERROR pair is real,
        // because it is what the error-code assertion reads.
        Assert.Equal("path", (string?)xml.Elements()
            .First(e => (string?)e.Attribute("_name") == "obj in").Attribute("type"));
        Assert.Equal("cluster{bool.status,int32.code,string.source}", (string?)xml.Elements()
            .First(e => (string?)e.Attribute("_name") == "error out").Attribute("type"));
    }

    [Fact]
    public void TheMethodsOwnErrorIsFedAConstantAndNeverTheCarayaChain()
    {
        // THE PROPERTY THAT MAKES THE SUITE HONEST. A method under test is expected to fail with no
        // hardware. Chaining its error into Caraya's chain would fail every assertion after it and
        // report failures the test itself caused.
        var xml = Suite(ErrorCase(1, "Initialize", -200099));

        var call = xml.Elements("Call")
            .First(e => ((string?)e.Attribute("target"))!.Contains("Mth1"));
        var errorIn = ((string)call.Attribute("inputs")!)
            .Split(',').First(p => p.StartsWith("error in", StringComparison.Ordinal));

        // It must come from a Constant, not from the Define Test node's error out.
        var sourceUid = errorIn.Split(':')[1].Split('.')[0];
        var source = xml.Elements().First(e => (string?)e.Attribute("uid") == sourceUid);
        Assert.Equal("Constant", source.Name.LocalName);
        Assert.Equal("cluster{bool.status,int32.code,string.source}",
                     (string?)source.Attribute("type"));
    }

    [Fact]
    public void AnErrorCodeCaseUnbundlesTheCodeAndComparesItToTheExpectedConstant()
    {
        var xml = Suite(ErrorCase(1, "Initialize", -200099));

        var unbundle = xml.Elements("Node")
            .Single(e => (string?)e.Attribute("_name") == "Unbundle By Name");
        Assert.Equal("code", (string?)unbundle.Attribute("fields"));

        // The expected value is a constant carrying exactly the number asked for - a digit lost
        // here is a test that passes against the wrong code.
        Assert.Contains(xml.Elements("Constant"),
            c => (string?)c.Attribute("type") == "int32"
                 && (string?)c.Attribute("value") == "-200099");
    }

    [Fact]
    public void AWireSurvivalCaseReadsTheFieldBackOffTheObjectTheMethodReturned()
    {
        // The whole point of the shape: reading it off the object that went IN would pass even if
        // the method dropped the wire and rebuilt it.
        var xml = Suite(WireCase(1, "Start", "Timeout"));

        var method = xml.Elements("Call")
            .First(e => ((string?)e.Attribute("target"))!.Contains("Mth1"));
        var read = xml.Elements("Call")
            .First(e => ((string?)e.Attribute("target"))!.Contains("MthR1"));

        var methodUid = (string)method.Attribute("uid")!;
        Assert.Contains($"obj in:{methodUid}.obj out", (string)read.Attribute("inputs")!);
    }

    [Fact]
    public void TheWrittenConstantIsAlsoTheExpectedValue()
    {
        // Reused rather than restated, so the two cannot drift apart - the same rule the accessor
        // round-trip generator follows.
        var xml = Suite(WireCase(1, "Start", "Timeout"));

        var write = xml.Elements("Call")
            .First(e => ((string?)e.Attribute("target"))!.Contains("MthW1"));
        var writtenUid = ((string)write.Attribute("inputs")!)
            .Split(',').First(p => p.StartsWith("value:", StringComparison.Ordinal))
            .Split(':')[1].Split('.')[0];

        var assertion = xml.Elements("Call")
            .First(e => ((string?)e.Attribute("target"))!.Contains("Assert Equal"));
        Assert.Contains($"Expected:{writtenUid}.value", (string)assertion.Attribute("inputs")!);
    }

    [Fact]
    public void EveryCaseGetsItsOwnSocketsAndItsOwnClassConstant()
    {
        // lvai_swap_subvis matches BY NAME, so two cases sharing a socket would be
        // indistinguishable and the wrong method would land in the wrong case with no error.
        var xml = Suite(ErrorCase(1, "Initialize", -200099), WireCase(2, "Start", "Timeout"));

        var targets = xml.Elements("Call")
            .Select(e => (string)e.Attribute("target")!)
            .Where(t => t.StartsWith("LVMCP", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(targets.Count, targets.Distinct().Count());

        var seeds = xml.Elements("Constant")
            .Where(c => ((string?)c.Attribute("_name"))?.StartsWith("Seed") is true)
            .Select(c => (string)c.Attribute("_name")!)
            .ToList();
        Assert.Equal(["Seed1", "Seed2"], seeds);
        Assert.All(xml.Elements("Constant")
                      .Where(c => ((string?)c.Attribute("_name"))?.StartsWith("Seed") is true),
                   // The seed is authored as a PATH and found later by its label - AIXML's _name
                   // becomes the block diagram label, which is the only handle the swap has.
                   c => Assert.Equal("path", (string?)c.Attribute("type")));
    }

    [Fact]
    public void ACaseCarryingBothShapesProducesTwoAssertions()
    {
        var both = new MethodTestTools.MethodCase(
            1, "Close clears the task and reports invalid task", "Close", @"C:\cls\Close.vi",
            "Timeout", @"C:\cls\Write Timeout.vi", "Timeout", @"C:\cls\Read Timeout.vi",
            "double", "10.0", -200088, @"C:\cls\Daq.lvclass");

        var xml = Suite(both);
        var assertions = xml.Elements("Call")
            .Count(e => ((string?)e.Attribute("target"))!.Contains("Assert Equal"));
        Assert.Equal(2, assertions);
    }

    [Fact]
    public void TheAssertionsAreMergedIntoOneErrorOut()
    {
        var xml = Suite(ErrorCase(1, "Initialize", -200099), ErrorCase(2, "Close", -200088));

        // n assertions need n-1 merges, and the indicator must hang off the last of them.
        Assert.Single(xml.Elements("Node"), e => (string?)e.Attribute("_name") == "Merge Errors");
        var errorOut = xml.Elements("Indicator")
            .Single(e => (string?)e.Attribute("_name") == "error out");
        var merge = xml.Elements("Node").Single(e => (string?)e.Attribute("_name") == "Merge Errors");
        Assert.Contains($"{(string)merge.Attribute("uid")!}.error out",
                        (string)errorOut.Attribute("inputs")!);
    }

    [Fact]
    public void TheSuiteNameIsTheTestVisOwnNameNotTheClasss()
    {
        // Caraya writes this string into the report as <testsuite name="…">. Deriving it from the
        // class made every suite of one class report under the same name - measured 2026-08-29.
        var xml = Suite(ErrorCase(1, "Initialize", -200099));

        Assert.Contains(xml.Elements("Constant"),
            c => (string?)c.Attribute("_name") == "Label (VI Title)"
                 && (string?)c.Attribute("value") == "Test Daq Methods");
    }

    [Theory]
    [InlineData("[]")]                                                     // nothing to test
    [InlineData("""[{"method":"Start"}]""")]                               // asserts nothing
    [InlineData("""[{"method":"Start","writeField":"Timeout"}]""")]        // field with no value
    [InlineData("""[{"expectErrorCode":-1}]""")]                           // no method
    public void ACaseThatAssertsNothingIsRefused(string json) =>
        Assert.Throws<ArgumentException>(() => MethodTestTools.MethodCaseRequest.ParseAll(json));

    [Fact]
    public void ReadFieldDefaultsToTheFieldThatWasWritten()
    {
        var parsed = MethodTestTools.MethodCaseRequest.ParseAll(
            """[{"method":"Start","writeField":"Timeout","value":"10.0"}]""");

        Assert.Equal("Timeout", parsed[0].WriteField);
        Assert.Null(parsed[0].ReadField);      // resolved to WriteField by the caller, not here
        Assert.Equal("10.0", parsed[0].Value);
    }
}
