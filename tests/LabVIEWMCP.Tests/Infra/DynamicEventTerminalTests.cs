using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards <c>pylv-show-dynamic-events.py</c>, which <c>lvai_generate_vi_with_events</c> runs
/// unconditionally so a generated Event Structure has somewhere to wire a
/// <c>Register For Events</c> refnum.
///
/// THE FIXTURE IS THE MEASUREMENT, lifted verbatim out of a real extract rather than written
/// from the shape the flags "should" have. CLAUDE.md records two tools that passed their unit
/// tests and failed on first real use for exactly that reason, so both sides of this test are
/// numbers observed on disk:
///
///     term wrapping an eventDynDCO   0x800040  ->  0x000040     bit 23 cleared
///     term wrapping the eventTimeOut 0x000040      0x000040     never hidden
///     term wrapping a selTun         0x400040      0x400040     a tunnel, untouched
///     eventStruct objFlags           0x054A80  ->  0x014280     0x040800 cleared
///
/// Taken 2026-09-10 as a clean A/B: one WORKING VI copied, the terminals toggled in the IDE,
/// saved, nothing else changed - frame count 4 and EventSpec count 4 on both sides, so the
/// single variable really was the toggle. An earlier attempt at the same pair was
/// UNATTRIBUTABLE because it was measured on a BROKEN VI, where LabVIEW's save pruned five
/// unregistered frames at the same moment. The numbers were identical and proved nothing.
///
/// The three untouched rows matter as much as the changed ones: a script that cleared bit 23
/// everywhere would also unhide nothing (the timeout terminal is already visible) but WOULD
/// corrupt the two <c>selTun</c> flags, whose 0x400000 sits one bit away.
///
/// The tests run the real script through the real bundled interpreter. Both are optional in a
/// fresh checkout, so an absent bundle or script makes each test return early rather than fail -
/// the same shape <c>EventSpecScriptTests</c> uses.
/// </summary>
public sealed class DynamicEventTerminalTests : IDisposable
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
    private const string Script = "pylv-show-dynamic-events.py";

    private string Bundle(string eventStructure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LVMCPDynTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        File.WriteAllText(Path.Combine(directory, $"{Base}_BDHb.xml"), $"""
            <?xml version='1.0' encoding='utf-8'?>
            <BDHb>
              <Section Index="0" Format="inline">
            {eventStructure}    </Section>
              </BDHb>
            """);
        return directory;
    }

    /// <summary>The real header, trimmed of termBounds/typeDesc noise but not of its flags.</summary>
    private const string RealStructure = """
                <SL__arrayElement class="eventStruct" uid="61">
                  <objFlags>346752</objFlags>
                  <termList elements="5">
                    <SL__arrayElement class="term" uid="4292">
                      <objFlags>8388672</objFlags>
                      <dco class="eventDynDCO" uid="4287">
                        <objFlags>65536</objFlags>
                        </dco>
                      </SL__arrayElement>
                    <SL__arrayElement class="term" uid="4294">
                      <objFlags>8388672</objFlags>
                      <dco class="eventDynDCO" uid="4289">
                        <objFlags>1</objFlags>
                        </dco>
                      </SL__arrayElement>
                    <SL__arrayElement class="term" uid="4298">
                      <objFlags>64</objFlags>
                      <dco class="eventTimeOut" uid="4295">
                        </dco>
                      </SL__arrayElement>
                    <SL__arrayElement class="term" uid="103">
                      <objFlags>4194368</objFlags>
                      <dco class="selTun" uid="4302">
                        <objFlags>3147777</objFlags>
                        </dco>
                      </SL__arrayElement>
                    <SL__arrayElement class="term" uid="4240">
                      <objFlags>4194368</objFlags>
                      <dco class="selTun" uid="4414">
                        <objFlags>2099201</objFlags>
                        </dco>
                      </SL__arrayElement>
                    </termList>
                  <bounds>(22, 46, 146, 445)</bounds>
                  <diagramList elements="1">
                    <SL__arrayElement class="diag" uid="62"/>
                    </diagramList>
                  </SL__arrayElement>
        """;

    private static string? ScriptPath()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", Script);
        return File.Exists(script) ? script : null;
    }

    private static async Task<PyLabview.Run?> RunAsync(string directory)
    {
        var bundle = PyLabview.Locate();
        var script = ScriptPath();
        if (bundle is null || script is null) return null;
        return await PyLabview.RunAsync(bundle, script, [directory, Base], 30,
                                        CancellationToken.None);
    }

    private string Bd(string directory) =>
        File.ReadAllText(Path.Combine(directory, $"{Base}_BDHb.xml"));

    private static int FlagsOfTerm(string bd, string uid)
    {
        var at = bd.IndexOf($"class=\"term\" uid=\"{uid}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"term {uid} is not in the fixture");
        var open = bd.IndexOf("<objFlags>", at, StringComparison.Ordinal) + "<objFlags>".Length;
        var close = bd.IndexOf("</objFlags>", open, StringComparison.Ordinal);
        return int.Parse(bd[open..close]);
    }

    private static int FlagsOfStructure(string bd)
    {
        var at = bd.IndexOf("class=\"eventStruct\"", StringComparison.Ordinal);
        var open = bd.IndexOf("<objFlags>", at, StringComparison.Ordinal) + "<objFlags>".Length;
        var close = bd.IndexOf("</objFlags>", open, StringComparison.Ordinal);
        return int.Parse(bd[open..close]);
    }

    [Fact]
    public async Task ClearsTheHiddenBitOnEveryDynamicEventTerminal()
    {
        var directory = Bundle(RealStructure);
        var run = await RunAsync(directory);
        if (run is null) return;   // no bundled interpreter in this checkout

        Assert.Equal(0, run.ExitCode);
        var bd = Bd(directory);
        Assert.Equal(0x000040, FlagsOfTerm(bd, "4292"));
        Assert.Equal(0x000040, FlagsOfTerm(bd, "4294"));
        Assert.Contains("dynamic event terminals shown", run.StdOut);
    }

    [Fact]
    public async Task ClearsTheStructuresOwnFlags()
    {
        var directory = Bundle(RealStructure);
        if (await RunAsync(directory) is null) return;

        // 0x054A80 -> 0x014280, measured against the IDE's own toggle
        Assert.Equal(0x014280, FlagsOfStructure(Bd(directory)));
    }

    [Fact]
    public async Task LeavesTheTimeoutAndTunnelTerminalsAlone()
    {
        var directory = Bundle(RealStructure);
        if (await RunAsync(directory) is null) return;

        var bd = Bd(directory);
        Assert.Equal(0x000040, FlagsOfTerm(bd, "4298"));   // eventTimeOut, never hidden
        Assert.Equal(0x400040, FlagsOfTerm(bd, "103"));    // selTun, 0x400000 must survive
        Assert.Equal(0x400040, FlagsOfTerm(bd, "4240"));
    }

    [Fact]
    public async Task IsIdempotent()
    {
        var directory = Bundle(RealStructure);
        if (await RunAsync(directory) is null) return;
        var once = Bd(directory);

        var run = await RunAsync(directory);

        Assert.Equal(0, run!.ExitCode);
        Assert.Contains("already shown", run.StdOut);
        Assert.Equal(once, Bd(directory));
    }

    [Fact]
    public async Task SaysSoWhenThereIsNoEventStructure()
    {
        var directory = Bundle("""
                <SL__arrayElement class="sRN" uid="7">
                  <objFlags>16384</objFlags>
                  </SL__arrayElement>
        """);

        var run = await RunAsync(directory);
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("no Event Structure", run.StdOut);
    }
}
