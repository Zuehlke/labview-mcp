using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A typedef whose elements are instances of OTHER typedefs.
///
/// THE DEFECT THIS CLOSES. <c>lvai_describe_ctl</c> took <c>VCTP/TopLevel</c> index 1 as the
/// control's own type. Measured 2026-10-23 on a cluster of two ring typedefs and a colour box that
/// LabVIEW 2025 had saved: index 1 pointed at the FIRST RING's typedef, the control's own TypeDef
/// sat at index 3, and the tool answered one `uint16` for a three-element cluster. A file written
/// by <c>lvai_create_typedef</c> lists itself first, which is why every earlier fixture agreed with
/// the old rule. Both shapes below are the VCTP of those two real files, cut down to the type
/// descriptors that matter; labels and the owning library are made generic, the order, the
/// nesting and the label chain (library, then file) are as extracted.
/// docs/frontpanel-picture-controls.md, section 4b.
/// </summary>
public class CtlNestedTypedefTests
{
    private const string Flags = """
        <LVSR><Section>
          <Instrument Type="Control" />
          <Execution TypeDefVI="1" StrictTypeDefVI="0" />
          <Execution2 IsPrivateDataForUDClass="0" />
        </Section></LVSR>
        """;

    /// <summary>The flat types both files share: two ring typedefs, a colour, the own cluster.</summary>
    private const string FlatTypes = """
        <TypeDesc Type="TypeDef" Flag1="0x0">
          <TypeDesc Type="NumUInt16" Nested="True" Label="Style" />
          <Label Text="Lib.lvlib" />
          <Label Text="Style Ring.ctl" />
        </TypeDesc>
        <TypeDesc Type="TypeDef" Flag1="0x0">
          <TypeDesc Type="NumUInt16" Nested="True" Label="Line.Width" />
          <Label Text="Lib.lvlib" />
          <Label Text="Width Ring.ctl" />
        </TypeDesc>
        <TypeDesc Type="NumUInt32" Label="Color" />
        <TypeDesc Type="TypeDef" Flag1="0x0">
          <TypeDesc Type="Cluster" Nested="True" Label="Channel Styles">
            <TypeDesc TypeID="0" />
            <TypeDesc TypeID="1" />
            <TypeDesc TypeID="2" />
          </TypeDesc>
          <Label Text="Lib.lvlib" />
          <Label Text="Channel Styles.ctl" />
        </TypeDesc>
        """;

    /// <summary>As LabVIEW 2025 saved it: the nested typedefs first, the control's own third.</summary>
    private static XElement NestedFirst() => XElement.Parse($"""
        <RSRC>{Flags}<VCTP><Section>{FlatTypes}
          <TopLevel>
            <TypeDesc Index="1" FlatTypeID="0" />
            <TypeDesc Index="2" FlatTypeID="1" />
            <TypeDesc Index="3" FlatTypeID="3" />
          </TopLevel>
        </Section></VCTP></RSRC>
        """);

    /// <summary>As lvai_create_typedef wrote it: the control's own type first.</summary>
    private static XElement OwnFirst() => XElement.Parse($"""
        <RSRC>{Flags}<VCTP><Section>{FlatTypes}
          <TopLevel>
            <TypeDesc Index="1" FlatTypeID="3" />
            <TypeDesc Index="2" FlatTypeID="0" />
            <TypeDesc Index="3" FlatTypeID="1" />
          </TopLevel>
        </Section></VCTP></RSRC>
        """);

    private static void AssertTheThreeElementCluster(System.Text.Json.Nodes.JsonObject answer)
    {
        Assert.Equal("TypeDef", answer["wrappedType"]!.GetValue<string>());
        var fields = answer["fields"]!.AsArray();
        var cluster = Assert.Single(fields)!.AsObject();
        Assert.Equal("Cluster", cluster["type"]!.GetValue<string>());
        Assert.Equal("Channel Styles", cluster["label"]!.GetValue<string>());

        var members = cluster["members"]!.AsArray();
        Assert.Equal(3, members.Count);
        // The FILE, not the library the label chain starts with.
        Assert.Equal("Style Ring.ctl", members[0]!["typedef"]!.GetValue<string>());
        Assert.Equal("Lib.lvlib:Style Ring.ctl", members[0]!["typedefQualified"]!.GetValue<string>());
        Assert.Equal("Style", members[0]!["label"]!.GetValue<string>());
        Assert.Equal("Width Ring.ctl", members[1]!["typedef"]!.GetValue<string>());
        Assert.Equal("Line.Width", members[1]!["label"]!.GetValue<string>());
        Assert.Equal("Color", members[2]!["label"]!.GetValue<string>());
        Assert.Null(members[2]!["typedef"]);
    }

    [Fact]
    public void ANestedTypedefListedFirstIsNotTakenForTheControl()
    {
        var answer = CtlTools.Describe(NestedFirst(), @"C:\lib\Channel Styles.ctl");

        AssertTheThreeElementCluster(answer);
        Assert.False(answer["needsLabviewSave"]!.GetValue<bool>());
    }

    /// <summary>
    /// THE CONTROL ARM: the order lvai_create_typedef writes must still read the same, or the fix
    /// traded one shape for the other.
    /// </summary>
    [Fact]
    public void TheOwnTypedefListedFirstReadsTheSame()
    {
        AssertTheThreeElementCluster(CtlTools.Describe(OwnFirst(), @"C:\lib\Channel Styles.ctl"));
    }

    /// <summary>The file name decides, not its case - Windows paths do not.</summary>
    [Fact]
    public void TheOwnTypedefIsFoundWhateverTheCaseOfThePath()
    {
        AssertTheThreeElementCluster(CtlTools.Describe(NestedFirst(), @"C:\LIB\CHANNEL STYLES.CTL"));
    }

    /// <summary>
    /// A file whose name matches no typedef descriptor - a copy renamed on disk, say - keeps the
    /// old answer rather than inventing one: TopLevel index 1.
    /// </summary>
    [Fact]
    public void WithNoOwnTypedefTheFirstTopLevelEntryIsStillTheAnswer()
    {
        var answer = CtlTools.Describe(NestedFirst(), @"C:\lib\Renamed Copy.ctl");

        var field = Assert.Single(answer["fields"]!.AsArray())!.AsObject();
        Assert.Equal("Style", field["label"]!.GetValue<string>());
    }
}
