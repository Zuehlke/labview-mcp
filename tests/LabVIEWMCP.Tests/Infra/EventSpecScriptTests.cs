using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards the two helper scripts <c>lvai_generate_vi_with_events</c> drives:
/// <c>pylv-set-event-spec.py</c> and <c>pylv-strip-compiled.py</c>.
///
/// EVERY FRAGMENT IS LIFTED OUT OF A REAL BUNDLE - NI's own untouched
/// UserInterfaceEventPattern.vit for the front panel, and an authored VI's extract for the event
/// structure. Trimmed, never invented: CLAUDE.md records two tools that passed their unit tests
/// and failed on first real use because the fixture was built from the shape the author expected.
///
/// THE FRONT-PANEL FIXTURE IS THE INTERESTING ONE. In NI's template a boolean's NAME and its
/// BUTTON FACE are different strings:
///
///     ddo 4    partID 16 "Button 1"    partID 22 "Command 1"
///     ddo 22   partID 16 "stop"        partID 22 "Stop"
///
/// The event selector spells the NAME. A resolver that matched "some label inside this control"
/// would answer ddo 4 for "Command 1" - a name no control has - and would answer ddo 22 for
/// "Stop" while the control is actually called "stop". That is why the resolver anchors on
/// partID 16, and it is the one thing no single-label fixture could catch.
///
/// The tests run the real scripts through the real bundled interpreter. Both are optional in a
/// fresh checkout, so an absent bundle or script is a SKIP, not a failure.
/// </summary>
public sealed class EventSpecScriptTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var dir in _directories)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private const string Base = "Probe";

    private static string? ScriptPath(string name)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", name);
        return File.Exists(script) ? script : null;
    }

    private static async Task<PyLabview.Run?> RunAsync(string script, params string[] args)
    {
        var bundle = PyLabview.Locate();
        var path = ScriptPath(script);
        if (bundle is null || path is null) return null;
        return await PyLabview.RunAsync(bundle, path, args, 30, CancellationToken.None);
    }

    /// <summary>A boolean control, in the shape pylabview writes one, with both label parts.</summary>
    private static string Control(string ddo, string name, string? face) => $"""
                <SL__arrayElement class="fPDCO" uid="9{ddo}">
                  <objFlags>1</objFlags>
                  <typeDesc>TypeID(1)</typeDesc>
                  <ddo class="stdBool" uid="{ddo}">
                    <objFlags>5</objFlags>
                    <bounds>(16, 16, 57, 76)</bounds>
                    <partsList elements="2">
                      <SL__arrayElement class="label" uid="8{ddo}">
                        <objFlags>1507650</objFlags>
                        <partID>16</partID>
                        <masterPart>9</masterPart>
                        <textRec class="textHair">
                          <mode>17412</mode>
                          <text>"{name}"</text>
                          </textRec>
                        </SL__arrayElement>
        {(face is null ? "" : $"""
                      <SL__arrayElement class="multiLabel" uid="7{ddo}">
                        <objFlags>395638</objFlags>
                        <partID>22</partID>
                        <masterPart>21</masterPart>
                        <textRec class="textHair">
                          <mode>1060</mode>
                          <text>"{face}"</text>
                          </textRec>
                        </SL__arrayElement>
        """)}          </partsList>
                    </ddo>
                  </SL__arrayElement>
        """;

    /// <summary>A bundle with NI's two real controls and one real event structure.</summary>
    private string Bundle(int specs = 1)
    {
        var directory = Path.Combine(Path.GetTempPath(),
                                     "eventspec-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        File.WriteAllText(Path.Combine(directory, $"{Base}_FPHb.xml"), $"""
            <?xml version='1.0' encoding='utf-8'?>
            <FPHb>
              <Section Index="0" Format="inline">
                <zPlaneList elements="2">
            {Control("4", "Button 1", "Command 1")}
            {Control("22", "stop", "Stop")}
                  </zPlaneList>
                </Section>
              </FPHb>
            """);

        var events = string.Concat(Enumerable.Range(0, specs).Select(i => $"""
                          <SL__arrayElement class="EventSpec">
                            <diagramIdx>{i}</diagramIdx>
                            <source>4</source>
                            <regFlags>0</regFlags>
                            <eSource>0</eSource>
                            <type>1073741825</type>
                            <eFlags>1</eFlags>
                            <ddoUID>0</ddoUID>
                            <menuTag />
                            <dynIndex>0</dynIndex>
                            </SL__arrayElement>
            """ + "\n"));

        File.WriteAllText(Path.Combine(directory, $"{Base}_BDHb.xml"), $"""
            <?xml version='1.0' encoding='utf-8'?>
            <BDHb>
              <Section Index="0" Format="inline">
                <SL__arrayElement class="eventStruct" uid="61">
                  <dIdx>0</dIdx>
                  <diagramList elements="1">
                    <SL__arrayElement class="diag" uid="62"/>
                    </diagramList>
                  <selString class="selLabel" uid="71">
                    <objFlags>2359601</objFlags>
                    <partID>80</partID>
                    <textRec class="textHair">
                      <mode>17665</mode>
                      <text>" [0]  "</text>
                      </textRec>
                    </selString>
                  <EventNodeEvents elements="{specs}">
            {events}      </EventNodeEvents>
                  </SL__arrayElement>
                </Section>
              </BDHb>
            """);

        File.WriteAllText(Path.Combine(directory, $"{Base}.xml"), """
            <?xml version='1.0' encoding='utf-8'?>
            <RSRC>
              <LVSR>
                <Section Index="0" Name="Probe.vi" Format="inline">
                  <Execution2 SourceOnly="0" TemplateMask="0" />
                  </Section>
                </LVSR>
              <TM80>
                <Section Index="0" Format="inline"><TypeMap /></Section>
                </TM80>
              <DFDS>
                <Section Index="0" Format="inline"><Data /></Section>
                </DFDS>
              <VICD>
                <!-- Virtual Instrument Compiled Data / VI Code -->
                <Section Index="0" Format="bin" File="Probe_VICD0.bin" />
                </VICD>
              <GCDI>
                <Section Index="0" Format="bin" File="Probe_GCDI.bin" />
                </GCDI>
              <VCTP>
                <Section Index="0" Format="inline"><TypeDesc Type="Boolean" /></Section>
                </VCTP>
              </RSRC>
            """);

        return directory;
    }

    private static string Bd(string directory) =>
        File.ReadAllText(Path.Combine(directory, $"{Base}_BDHb.xml"));

    // ------------------------------------------------------------ set-event-spec

    [Fact]
    public async Task ResolvesAControlByItsOwnedLabelAndWritesTheSpec()
    {
        var directory = Bundle();
        var run = await RunAsync("pylv-set-event-spec.py", directory, Base, "0", "Button 1");
        if (run is null) return;   // no bundled interpreter in this checkout

        Assert.Equal(0, run.ExitCode);
        var heap = Bd(directory);

        // the measured Value Change shape, on the ddo the LABEL names
        Assert.Contains("<source>3</source>", heap);
        Assert.Contains("<type>1073741826</type>", heap);
        Assert.Contains("<eFlags>4</eFlags>", heap);
        Assert.Contains("<ddoUID>4</ddoUID>", heap);
    }

    /// <summary>
    /// The defect that made a correct EventSpec read as an unassigned event case in the IDE. The
    /// cached label must carry the selector text, and it is wrapped in quotes AND contains them.
    /// </summary>
    [Fact]
    public async Task WritesTheCachedFrameLabelAndPointsDIdxAtTheFrame()
    {
        var directory = Bundle();
        var run = await RunAsync("pylv-set-event-spec.py", directory, Base, "0", "Button 1");
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        var heap = Bd(directory);

        Assert.Contains("""<text>" [0] "Button 1": Value Change "</text>""", heap);
        Assert.Contains("<dIdx>0</dIdx>", heap);
        Assert.DoesNotContain("""<text>" [0]  "</text>""", heap);
    }

    /// <summary>
    /// Three calls in a row must REPLACE that label, not append to it. Matching the stored text
    /// "up to the next quote" stops inside the control name, and the measured result was
    /// `" [2] "C": Value Change "B": Value Change "A": Value Change "`.
    /// </summary>
    [Fact]
    public async Task RewritingTheLabelReplacesItRatherThanAppending()
    {
        var directory = Bundle(specs: 2);

        var first = await RunAsync("pylv-set-event-spec.py", directory, Base, "0", "Button 1");
        if (first is null) return;
        Assert.Equal(0, first.ExitCode);

        var second = await RunAsync("pylv-set-event-spec.py", directory, Base, "1", "stop");
        Assert.Equal(0, second!.ExitCode);

        var heap = Bd(directory);
        Assert.Contains("""<text>" [1] "stop": Value Change "</text>""", heap);
        Assert.DoesNotContain("Button 1\": Value Change \"stop", heap);
        // exactly one selector label, not a run-on
        Assert.Equal(1, heap.Split("Value Change \"</text>").Length - 1);
    }

    /// <summary>
    /// THE FIXTURE'S WHOLE POINT. "Command 1" is `Button 1`'s button FACE, not any control's
    /// name, so it must be refused - and the refusal must list the real names, because a
    /// misspelling is the likely cause.
    /// </summary>
    [Fact]
    public async Task RefusesAButtonFaceTextAndListsTheRealNames()
    {
        var directory = Bundle();
        var run = await RunAsync("pylv-set-event-spec.py", directory, Base, "0", "Command 1");
        if (run is null) return;

        Assert.NotEqual(0, run.ExitCode);
        var said = run.StdOut + run.StdErr;
        Assert.Contains("Command 1", said);
        Assert.Contains("Button 1", said);   // the label it should have been asked for
        Assert.Contains("stop", said);

        // and nothing was written
        Assert.Contains("<ddoUID>0</ddoUID>", Bd(directory));
    }

    /// <summary>
    /// `stop` and its face `Stop` differ only in case, and both exist in the fixture. The name is
    /// the lower-case one; a case-insensitive or face-matching resolver would pick the wrong ddo
    /// and register the event on nothing.
    /// </summary>
    [Fact]
    public async Task DistinguishesTheNameFromAFaceThatDiffersOnlyInCase()
    {
        var directory = Bundle();

        var name = await RunAsync("pylv-set-event-spec.py", directory, Base, "0", "stop");
        if (name is null) return;
        Assert.Equal(0, name.ExitCode);
        Assert.Contains("<ddoUID>22</ddoUID>", Bd(directory));

        var face = await RunAsync("pylv-set-event-spec.py", Bundle(), Base, "0", "Stop");
        Assert.NotEqual(0, face!.ExitCode);
    }

    /// <summary>A numeric ddoUID still works - that is how the script was first driven.</summary>
    [Fact]
    public async Task StillAcceptsANumericDdoUid()
    {
        var directory = Bundle();
        var run = await RunAsync("pylv-set-event-spec.py", directory, Base, "0", "4", "Button 1");
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("<ddoUID>4</ddoUID>", Bd(directory));
    }

    /// <summary>
    /// After a round trip the frames survive but EventNodeEvents holds only the Timeout spec, so
    /// the later frames have NO spec at all and one has to be appended - with the element count
    /// following.
    /// </summary>
    [Fact]
    public async Task AppendsASpecForAFrameThatHasNone()
    {
        var directory = Bundle(specs: 1);
        var run = await RunAsync("pylv-set-event-spec.py", directory, Base, "1", "stop");
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        var heap = Bd(directory);
        Assert.Contains("""<EventNodeEvents elements="2">""", heap);
        Assert.Contains("<diagramIdx>1</diagramIdx>", heap);
        Assert.Contains("<ddoUID>22</ddoUID>", heap);
    }

    // ------------------------------------------------------------ strip-compiled

    [Fact]
    public async Task StripsTheCompiledBlocksAndMarksTheViSourceOnly()
    {
        var directory = Bundle();
        var run = await RunAsync("pylv-strip-compiled.py", directory, Base);
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        var main = File.ReadAllText(Path.Combine(directory, $"{Base}.xml"));

        Assert.Contains("""SourceOnly="1" """.TrimEnd(), main);
        Assert.DoesNotContain("<VICD>", main);
        Assert.DoesNotContain("<GCDI>", main);
    }

    /// <summary>
    /// TM80 and DFDS are the DATA SPACE, not compiled code. Dropping them gives a VI that does
    /// not load at all, so the list is the measured set and not "everything that looks generated".
    /// </summary>
    [Fact]
    public async Task LeavesTheDataSpaceBlocksAlone()
    {
        var directory = Bundle();
        var run = await RunAsync("pylv-strip-compiled.py", directory, Base);
        if (run is null) return;

        var main = File.ReadAllText(Path.Combine(directory, $"{Base}.xml"));
        Assert.Contains("<TM80>", main);
        Assert.Contains("<DFDS>", main);
        Assert.Contains("<VCTP>", main);
    }

    /// <summary>Running it twice must not fail - it is idempotent, and says so.</summary>
    [Fact]
    public async Task IsIdempotent()
    {
        var directory = Bundle();
        var first = await RunAsync("pylv-strip-compiled.py", directory, Base);
        if (first is null) return;
        Assert.Equal(0, first.ExitCode);

        var second = await RunAsync("pylv-strip-compiled.py", directory, Base);
        Assert.Equal(0, second!.ExitCode);
        Assert.Contains("already 1", second.StdOut);
    }
}
