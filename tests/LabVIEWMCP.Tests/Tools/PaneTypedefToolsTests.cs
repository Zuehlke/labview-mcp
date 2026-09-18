using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The CONTROL half of the coercion-dot repair, and the verdict that caught its own defect.
///
/// WHAT THESE COVER. Everything here runs with no LabVIEW and no pylabview, because everything
/// here is the part that DECIDES: the argument guards, the file-level verdict, and the repair
/// listing. The LabVIEW half is measured in docs/typedef-disconnect.md section 13a instead - a
/// fixture that pretended to check it would be the "tested against a plausible fixture" failure
/// this repository already records three times.
/// </summary>
public class PaneTypedefToolsTests
{
    // ---------------------------------------------------------------- bindingsJson

    [Fact]
    public void AWellFormedBindingParses()
    {
        var bindings = TypedefTools.ParseBindings(
            """[{"terminal":"Profil","ctlPath":"C:\\p\\Ofenprofil.ctl"}]""");

        var one = Assert.Single(bindings);
        Assert.Equal("Profil", one.Terminal);
        Assert.Equal(@"C:\p\Ofenprofil.ctl", one.CtlPath);
    }

    /// <summary>
    /// THE MEASURED DEFECT CLASS. An unknown key inside a JSON string argument is invisible to the
    /// argument wrapper, which guards MCP arguments only - two agents were measured reaching for a
    /// plausible key on lvai_generate_method_test, having it discarded, and getting ok: true for a
    /// suite that asserted the opposite of what was asked for.
    /// </summary>
    [Fact]
    public void AnUnknownKeyIsRefusedByNameRatherThanDropped()
    {
        var bad = Assert.Throws<ArgumentException>(() => TypedefTools.ParseBindings(
            """[{"terminal":"Profil","ctl":"C:\\p\\Ofenprofil.ctl"}]"""));

        Assert.Contains("'ctl'", bad.Message);
        Assert.Contains("ctlPath", bad.Message);
    }

    [Fact]
    public void AnEmptyOrMalformedListIsRefused()
    {
        Assert.Throws<ArgumentException>(() => TypedefTools.ParseBindings("[]"));
        Assert.Throws<ArgumentException>(() => TypedefTools.ParseBindings("not json"));
        Assert.Throws<ArgumentException>(() => TypedefTools.ParseBindings("""["Profil"]"""));
        Assert.Throws<ArgumentException>(() =>
            TypedefTools.ParseBindings("""[{"terminal":"","ctlPath":"C:\\p\\X.ctl"}]"""));
    }

    /// <summary>
    /// The helper pairs names with paths on a PIPE, so one inside a value would shift every later
    /// pair onto the wrong terminal - and the failure would appear only when the helper runs.
    /// </summary>
    [Fact]
    public void APipeInsideAValueIsRefused()
    {
        var bad = Assert.Throws<ArgumentException>(() => TypedefTools.ParseBindings(
            """[{"terminal":"Pro|fil","ctlPath":"C:\\p\\Ofenprofil.ctl"}]"""));

        Assert.Contains("PIPE", bad.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- reading the saved file

    /// <summary>
    /// The shape measured on a real class member: a bound typedef is a TypeDesc whose Label
    /// children name the owning library and the .ctl.
    /// </summary>
    [Fact]
    public void TheCtlNamesAreReadOffTheTypeDescriptorLabels()
    {
        var rsrc = XElement.Parse("""
            <RSRC>
              <VCTP><Section>
                <TypeDesc Type="TypeDef">
                  <Label Text="Ofen.lvlib" />
                  <Label Text="Ofenprofil.ctl" />
                </TypeDesc>
                <TypeDesc Type="Cluster"><Label Text="not a file" /></TypeDesc>
              </Section></VCTP>
            </RSRC>
            """);

        var names = TypedefTools.TypedefCtlNames(rsrc);

        Assert.Contains("Ofenprofil.ctl", names);
        Assert.DoesNotContain("Ofen.lvlib", names);
        Assert.DoesNotContain("not a file", names);
    }

    /// <summary>
    /// THE CONTROL ARM. Without it a function that always answered "found" would pass the test
    /// above, and the verdict would wave through exactly the failure it exists for - a run that
    /// reported `terminals bound: 1` for a file carrying no typedef at all.
    /// </summary>
    [Fact]
    public void AFileWithNoTypedefYieldsNoNames()
    {
        var rsrc = XElement.Parse("""
            <RSRC><VCTP><Section>
              <TypeDesc Type="Cluster"><Label Text="Profil" /></TypeDesc>
            </Section></VCTP></RSRC>
            """);

        Assert.Empty(TypedefTools.TypedefCtlNames(rsrc));
    }

    // ---------------------------------------------------------------- the verdict

    private static JsonObject Describe(string runnerAnswer, SortedSet<string>? onDisk) =>
        (JsonObject)JsonNode.Parse(TypedefTools.DescribePaneBind(
            runnerAnswer,
            [new TypedefTools.PaneBinding("Profil", @"C:\p\Ofenprofil.ctl")],
            @"C:\p\Profil Setzen.vi", @"C:\p\Ofen.lvclass", "helper.vi", "helper.xml",
            helperGenerated: false, retried: false, [], onDisk))!;

    /// <summary>A helper answer shaped like the runner's, reporting one terminal bound.</summary>
    private const string BoundOne = """
        {"values":{
          "terminals bound":{"type":"I32","value":"1"},
          "terminal names seen":{"type":"Array","value":null,
            "xml":"<Array><Name>terminal names seen</Name><Dimsize>1</Dimsize><String><Name></Name><Val>Profil</Val></String></Array>"}
        }}
        """;

    [Fact]
    public void TheVerdictIsTheFileWhenTheCtlIsThere()
    {
        var answer = Describe(BoundOne, new(StringComparer.OrdinalIgnoreCase)
        {
            "Ofenprofil.ctl",
        });

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.True(answer["verified"]!.GetValue<bool>());
        Assert.Empty((JsonArray)answer["typedefsMissingFromSavedFile"]!);
    }

    /// <summary>
    /// THE DEFECT THIS GATE EXISTS FOR, measured 2026-09-18 on a VI with no owning class: the
    /// helper answered `terminals bound: 1` with every error cluster zero and the saved file
    /// carried ZERO typedef objects. Believing the count is how that run reported success.
    /// </summary>
    [Fact]
    public void AReportedBindWithNothingInTheFileIsNotOk()
    {
        var answer = Describe(BoundOne, new(StringComparer.OrdinalIgnoreCase));

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.False(answer["verified"]!.GetValue<bool>());
        Assert.Equal(1, answer["terminalsBound"]!.GetValue<int>());
        Assert.Contains("Ofenprofil.ctl",
            ((JsonArray)answer["typedefsMissingFromSavedFile"]!).Select(n => n!.GetValue<string>()));
    }

    /// <summary>
    /// THE FALSE PASS THE ACCEPTANCE RUN FOUND, 2026-09-18, and the reason the file check is not
    /// the whole verdict. Asked to bind a terminal named `Gibtsnicht` onto a VI whose `Profil` was
    /// ALREADY bound to the same .ctl, the tool answered <c>ok: true</c>, <c>verified: true</c>,
    /// <c>terminalsBound: 0</c>, <c>terminalOnPanel: false</c> - green for a terminal that does not
    /// exist, with both disqualifying facts sitting in the answer and neither gating it. Same shape
    /// as <c>nodesSwapped</c> reporting the REQUEST rather than the outcome.
    ///
    /// The file check cannot tell "I just bound it" from "it was already there", so it never could
    /// have caught this on its own.
    /// </summary>
    [Fact]
    public void ATerminalThatIsNotOnThePanelIsNotAPassEvenWhenTheCtlIsAlreadyInTheFile()
    {
        var answer = (JsonObject)JsonNode.Parse(TypedefTools.DescribePaneBind(
            """
            {"values":{
              "terminals bound":{"type":"I32","value":"0"},
              "terminal names seen":{"type":"Array","value":null,
                "xml":"<Array><Name>terminal names seen</Name><Dimsize>1</Dimsize><String><Name></Name><Val>Profil</Val></String></Array>"}
            }}
            """,
            [new TypedefTools.PaneBinding("Gibtsnicht", @"C:\p\Ofenprofil.ctl")],
            @"C:\p\Profil Setzen.vi", @"C:\p\Ofen.lvclass", "helper.vi", "helper.xml",
            helperGenerated: false, retried: false, [],
            new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Ofenprofil.ctl" }))!;

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.False(answer["verified"]!.GetValue<bool>());
        // The .ctl IS in the file - that half passes, which is exactly why it cannot decide alone.
        Assert.Empty((JsonArray)answer["typedefsMissingFromSavedFile"]!);
        Assert.Contains("Gibtsnicht",
            ((JsonArray)answer["terminalsNotOnPanel"]!).Select(n => n!.GetValue<string>()));
        Assert.Contains("NOT on this VI's panel", answer["note"]!.GetValue<string>());
    }

    /// <summary>
    /// And the helper matching FEWER than were asked for fails too, with the terminals all present
    /// and the .ctl in the file - the third condition, which neither of the other two covers.
    /// </summary>
    [Fact]
    public void MatchingFewerTerminalsThanAskedForIsNotAPass()
    {
        var answer = (JsonObject)JsonNode.Parse(TypedefTools.DescribePaneBind(
            """
            {"values":{
              "terminals bound":{"type":"I32","value":"0"},
              "terminal names seen":{"type":"Array","value":null,
                "xml":"<Array><Name>terminal names seen</Name><Dimsize>1</Dimsize><String><Name></Name><Val>Profil</Val></String></Array>"}
            }}
            """,
            [new TypedefTools.PaneBinding("Profil", @"C:\p\Ofenprofil.ctl")],
            @"C:\p\Profil Setzen.vi", @"C:\p\Ofen.lvclass", "helper.vi", "helper.xml",
            helperGenerated: false, retried: false, [],
            new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Ofenprofil.ctl" }))!;

        Assert.False(answer["verified"]!.GetValue<bool>());
        Assert.Empty((JsonArray)answer["terminalsNotOnPanel"]!);
        Assert.Contains("terminalsBound", answer["note"]!.GetValue<string>());
    }

    /// <summary>
    /// An unreadable file answers null, never true. Gating on a number nobody could read is how a
    /// green answer gets manufactured - the same rule lvai_coercion_dots' `examinedNothing` and
    /// lvai_add_class_method's on-disk verify already encode.
    /// </summary>
    [Fact]
    public void AnUnreadableFileIsNotAPass()
    {
        var answer = Describe(BoundOne, onDisk: null);

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Null(answer["verified"]);
        Assert.Contains("NOTHING is concluded", answer["note"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- the repair listing

    /// <summary>
    /// Both repairs are named, and the CONTROL one is present - its absence is the defect that
    /// sent a real build at lvai_bind_typedef_constants, which finds its target by a constant's
    /// label and cannot reach a front-panel control.
    /// </summary>
    [Fact]
    public void BothRepairsAreNamedWithTheQuestionThatPicksBetweenThem()
    {
        var repairs = TypedefTools.Repairs();

        Assert.Equal(2, repairs.Count);
        var tools = repairs.Select(r => r!["tool"]!.GetValue<string>()).ToArray();
        Assert.Contains("lvai_bind_typedef_constants", tools);
        Assert.Contains("lvai_bind_pane_typedef", tools);
        Assert.All(repairs, r => Assert.False(
            string.IsNullOrWhiteSpace(r!["whenTheSourceIs"]!.GetValue<string>())));
    }

    /// <summary>
    /// A clean sweep names no repair at all: a list of remedies beside "nothing to repair" reads
    /// as something to do.
    /// </summary>
    [Fact]
    public void ACleanSweepCarriesNoRepairList()
    {
        var clean = (JsonObject)JsonNode.Parse(TypedefTools.DescribeDots(
            [("Sub.vi", 0, """
                {"values":{
                  "subvi found":{"type":"String","value":"Sub.vi"},
                  "code":{"type":"I32","value":"0"},
                  "terminal names":{"type":"Array","value":null,
                    "xml":"<Array><Name>terminal names</Name><Dimsize>1</Dimsize><String><Name></Name><Val>x</Val></String></Array>"},
                  "coercion dots":{"type":"Array","value":null,
                    "xml":"<Array><Name>coercion dots</Name><Dimsize>1</Dimsize><Boolean><Name></Name><Val>0</Val></Boolean></Array>"}
                }}
                """)],
            @"C:\p\Caller.vi", "helper.vi", "helper.xml", helperGenerated: false))!;

        Assert.True(clean["clean"]!.GetValue<bool>());
        Assert.Null(clean["repairs"]);
    }
}
