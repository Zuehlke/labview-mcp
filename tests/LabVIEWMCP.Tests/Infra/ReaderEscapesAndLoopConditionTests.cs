using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Three reader defects found reading the LabVIEW Icon Editor (488 files) on 2026-10-15:
///
/// - A path holding `&amp;` (`Controls\Path&amp;Icon.ctl`) was not read at all: pylabview was
///   started through `cmd /c`, which split the command line at the ampersand.
/// - KeyDown.vi's Backspace case `"\x08"` was written as a RAW control character, which XML 1.0
///   forbids, so the whole export was unparseable. NI's spelling is `\XX`, and a comma inside the
///   quotes is `\2C` (NI's own `"N-waveforms\2C N-filters"`).
/// - Every While Loop came back `value="stop"`. NI writes Continue if True as a Condition with NO
///   value; in the heap the lTst DCO lacks 0x10000. The composer built every loop as Stop if True,
///   so it now honours the missing value too.
///
/// The VI is composed here and read back, so the test needs no LabVIEW. The same VI was run in
/// LabVIEW 2026 over ActiveX when this was written: it ENDED (a Continue-if-True loop fed FALSE
/// stops after one iteration, the inverted loop would never end), `a,b` selected the `a\2Cb` case
/// and a Backspace character the `\08` case.
/// </summary>
public sealed class ReaderEscapesAndLoopConditionTests
{
    private const string Probe = """
        <VI _name="Reader Escapes Probe.vi" description="Continue-if-True loop and string case items holding a control character and a comma.">
          <Control _name="Key" type="string" uid="4200" uid_parent="root" value="" outputs="value:4200.value" />
          <Indicator _name="Hit" type="int32" uid="4201" uid_parent="root" value="0" inputs="value:4230.value" />
          <Structure _name="Case Structure" uid="4210" uid_parent="root" selectin="4200.value">
            <CaseFrame selector="&quot;\08&quot;" uid="4211" uid_parent="4210" selectout="">
              <Constant type="int32" uid="4212" uid_parent="4211" value="1" outputs="value:4212.value" />
              <Tunnel _id="Out1" uid="4213" uid_parent="4211" inputs="value:4212.value" />
            </CaseFrame>
            <CaseFrame selector="&quot;a\2Cb&quot;" uid="4214" uid_parent="4210" selectout="">
              <Constant type="int32" uid="4215" uid_parent="4214" value="2" outputs="value:4215.value" />
              <Tunnel _id="Out1" uid="4216" uid_parent="4214" inputs="value:4215.value" />
            </CaseFrame>
            <CaseFrame selector="Default" uid="4217" uid_parent="4210" selectout="">
              <Constant type="int32" uid="4218" uid_parent="4217" value="0" outputs="value:4218.value" />
              <Tunnel _id="Out1" uid="4219" uid_parent="4217" inputs="value:4218.value" />
            </CaseFrame>
            <Tunnel _id="Out1" uid="4230" uid_parent="4210" outputs="value:4230.value" />
          </Structure>
          <Structure _name="While Loop" uid="4240" uid_parent="root" count="">
            <Constant type="bool" uid="4241" uid_parent="4240" value="false" outputs="value:4241.value" />
            <Condition uid="4242" uid_parent="4240" inputs="value:4241.value" />
          </Structure>
          <Structure _name="While Loop" uid="4250" uid_parent="root" count="">
            <Constant type="bool" uid="4251" uid_parent="4250" value="true" outputs="value:4251.value" />
            <Condition uid="4252" uid_parent="4250" value="stop" inputs="value:4251.value" />
          </Structure>
        </VI>
        """;

    [Fact]
    public async Task A_path_with_an_ampersand_reads_and_selectors_and_loop_conditions_round_trip()
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return;
        var dir = Path.Combine(Path.GetTempPath(), "reader-esc-" + Guid.NewGuid().ToString("n")[..8], "A&B");
        Directory.CreateDirectory(dir);
        try
        {
            var aixml = Path.Combine(dir, "probe.xml");
            await File.WriteAllTextAsync(aixml, Probe);
            var vi = Path.Combine(dir, "Path&Probe.vi");
            var composed = await AixmlComposer.ComposeAsync(setup, aixml, vi, [], 120, default);
            Assert.True(composed.Written, composed.Message);

            var back = Path.Combine(dir, "back.xml");
            var read = await AixmlComposer.ReadAsync(setup, vi, back, 120, default);
            Assert.True(read.Written, read.Message);

            // XElement.Load throws on a raw control character - the KeyDown.vi failure
            var root = XElement.Load(back);
            Assert.Equal(new[] { "\"\\08\"", "\"a\\2Cb\"", "Default" },
                root.Descendants("CaseFrame").Select(f => (string?)f.Attribute("selector")).ToArray());

            var loops = root.Descendants("Structure").Where(s => (string?)s.Attribute("_name") == "While Loop").ToArray();
            Assert.Equal(2, loops.Length);
            Assert.Null(loops[0].Element("Condition")!.Attribute("value"));            // Continue if True
            Assert.Equal("stop", (string?)loops[1].Element("Condition")!.Attribute("value"));
            Assert.DoesNotContain(root.Nodes().OfType<XComment>(), c => c.Value.Contains("control character"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (IOException) { }
        }
    }
}
