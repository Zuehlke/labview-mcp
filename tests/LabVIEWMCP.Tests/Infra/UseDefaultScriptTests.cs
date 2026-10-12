// Guards pylv-set-use-default.py: "Use Default if Unwired" on the case tunnels ONE edit created.
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards <c>pylv-set-use-default.py</c>, the bundle step <c>lvai_wire_dynamic_events</c> runs
/// after wiring the RIGHT side: the new Case Structure output tunnel is unwired in every other
/// frame, which breaks the VI, and the AIXML generator refuses <c>Use Default if Unwired</c> on a
/// Property Node - so the heap flag 0x200000 is set on the <c>selTun</c> instead.
///
/// THE FIXTURE IS TRIMMED OUT OF A REAL BUNDLE, never invented: an extract of a generated VI with
/// an Event Structure in the No Error frame of a Case Structure in a While Loop, taken after
/// Connect Wire had made the right-side tunnels and before the flag was set. The uids, objFlags
/// and nesting (`whileLoop` &gt; `diag` &gt; `select`, inner terminals under each frame's
/// `sRN`) are that file's. Two things are CHANGED on purpose, each to give exactly one filter
/// something to refuse: the pre-existing case output tunnel 4891 has its other-frame terminal
/// UNWIRED (only the baseline excludes it), and the Event Structure's output tunnel 4770 is absent
/// from the baseline and unwired in a frame too (only the owner class excludes it). Neither may be
/// touched - the first because it is the user's, the second because an Event Structure is not a
/// Case Structure.
///
/// Run against the real file, the script's output was byte-identical to the bundle the measured run
/// patched by hand, which is the check this fixture cannot make on its own.
///
/// The tests run the real script through the real bundled interpreter; an absent bundle or script
/// is a SKIP, as in <see cref="EventSpecScriptTests"/>.
/// </summary>
public sealed class UseDefaultScriptTests : IDisposable
{
    private const string Base = "Probe";
    private const string Script = "pylv-set-use-default.py";

    /// <summary>What the new output tunnel reads before: plain output (0x801) + LabVIEW's 0x100000.</summary>
    private const int NewOutputFlags = 1050625;

    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var dir in _directories)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<PyLabview.Run?> RunAsync(params string[] args)
    {
        var bundle = PyLabview.Locate();
        var path = Path.Combine(AppContext.BaseDirectory, "scripts", Script);
        if (bundle is null || !File.Exists(path)) return null;
        return await PyLabview.RunAsync(bundle, path, args, 30, CancellationToken.None);
    }

    private static string Term(string uid, int flags, string dcoUid) => $"""
                                <SL__arrayElement class="term" uid="{uid}">
                                  <objFlags>{flags}</objFlags>
                                  <dco uid="{dcoUid}" />
                                  </SL__arrayElement>
        """;

    private static string Tunnel(string termUid, string uid, int flags, params string[] terms) => $"""
                      <SL__arrayElement class="term" uid="{termUid}">
                        <objFlags>4194368</objFlags>
                        <dco class="selTun" uid="{uid}">
                          <objFlags>{flags}</objFlags>
                          <termList elements="{terms.Length}">
        {string.Concat(terms.Select(t => $"                    <SL__arrayElement uid=\"{t}\" />\n"))}                      </termList>
                          <typeDesc>TypeID(53)</typeDesc>
                          </dco>
                        </SL__arrayElement>
        """;

    /// <summary>
    /// The heap. <paramref name="created"/> includes the two tunnels Connect Wire made (528 in,
    /// 560 out) - false is the BASELINE, the same VI before the edit. <paramref name="extra"/>
    /// adds one more pre-existing tunnel uid, for the refusal case.
    /// </summary>
    private static string Heap(bool created, bool otherFrameWired = false, string? extra = null)
    {
        var tunnels = Tunnel("4341", "4891", 2049, "4323", "4329", "4341")
            + (created
                ? Tunnel("537", "528", 2048, "530", "533", "537")
                  + Tunnel("570", "560", NewOutputFlags, "562", "566", "570")
                : "")
            + (extra is null ? "" : Tunnel("9" + extra, extra, 2048, "8" + extra, "7" + extra, "9" + extra));

        return $"""
            <?xml version='1.0' encoding='utf-8'?>
            <BDHb>
              <SL__rootObject class="oHExt" uid="1">
                <root class="diag" uid="3">
                  <zPlaneList elements="1">
                    <SL__arrayElement class="whileLoop" uid="4300">
                      <diagramList elements="1">
                        <SL__arrayElement class="diag" uid="4747">
                          <zPlaneList elements="1">
                            <SL__arrayElement class="select" uid="4310">
                              <objFlags>0</objFlags>
                              <termList elements="3">
            {tunnels}
                                </termList>
                              <diagramList elements="2">
                                <SL__arrayElement class="diag" uid="4320">
                                  <zPlaneList elements="1">
                                    <SL__arrayElement class="eventStruct" uid="4330">
                                      <termList elements="1">
            {(created ? Tunnel("4338", "4770", 2049, "4333", "4337", "4338") : "")}
                                        </termList>
                                      <diagramList elements="2">
                                        <SL__arrayElement class="diag" uid="4331">
                                          <nodeList elements="1">
                                            <SL__arrayElement class="sRN" uid="4790">
                                              <termList elements="1">
            {Term("4333", 4358144, "4770")}
                                                </termList>
                                              </SL__arrayElement>
                                            </nodeList>
                                          </SL__arrayElement>
                                        <SL__arrayElement class="diag" uid="4332">
                                          <nodeList elements="1">
                                            <SL__arrayElement class="sRN" uid="4791">
                                              <termList elements="1">
            {Term("4337", 4325376, "4770")}
                                                </termList>
                                              </SL__arrayElement>
                                            </nodeList>
                                          </SL__arrayElement>
                                        </diagramList>
                                      </SL__arrayElement>
                                    </zPlaneList>
                                  <nodeList elements="1">
                                    <SL__arrayElement class="sRN" uid="4778">
                                      <termList elements="3">
            {Term("4323", 4358144, "4891")}
            {(created ? Term("562", 4358144, "560") + Term("530", 4325376, "528") : "")}
                                        </termList>
                                      </SL__arrayElement>
                                    </nodeList>
                                  </SL__arrayElement>
                                <SL__arrayElement class="diag" uid="4325">
                                  <nodeList elements="1">
                                    <SL__arrayElement class="sRN" uid="4784">
                                      <termList elements="3">
            {Term("4329", 4325376, "4891")}
            {(created ? Term("566", otherFrameWired ? 4358144 : 4325376, "560") + Term("533", 4325376, "528") : "")}
                                        </termList>
                                      </SL__arrayElement>
                                    </nodeList>
                                  </SL__arrayElement>
                                </diagramList>
                              </SL__arrayElement>
                            </zPlaneList>
                          </SL__arrayElement>
                        </diagramList>
                      </SL__arrayElement>
                    </zPlaneList>
                  </root>
                </SL__rootObject>
              </BDHb>
            """;
    }

    /// <summary>(after bundle, baseline bundle), each holding one BDHb.</summary>
    private (string After, string Before) Bundles(bool otherFrameWired = false,
                                                  string? extraInBaseline = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "usedefault-" + Guid.NewGuid().ToString("n")[..8]);
        var after = Path.Combine(root, "after");
        var before = Path.Combine(root, "before");
        Directory.CreateDirectory(after);
        Directory.CreateDirectory(before);
        _directories.Add(root);
        File.WriteAllText(Path.Combine(after, $"{Base}_BDHb.xml"),
            Heap(created: true, otherFrameWired).Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(before, $"{Base}_BDHb.xml"),
            Heap(created: false, extra: extraInBaseline).Replace("\r\n", "\n"));
        return (after, before);
    }

    private static string Heap(string bundle) => File.ReadAllText(Path.Combine(bundle, $"{Base}_BDHb.xml"));

    private static string FlagsOf(string heap, string selTun)
    {
        var at = heap.IndexOf($"<dco class=\"selTun\" uid=\"{selTun}\">", StringComparison.Ordinal);
        Assert.True(at >= 0, $"selTun {selTun} not in the heap");
        var open = heap.IndexOf("<objFlags>", at, StringComparison.Ordinal) + "<objFlags>".Length;
        return heap[open..heap.IndexOf("</objFlags>", open, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task SetsUseDefaultOnTheNewCaseOutputTunnelAndNothingElse()
    {
        var (after, before) = Bundles();
        var run = await RunAsync(after, Base, "--before", before, Base);
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("RESULT: changed", run.StdOut);
        Assert.Contains("USE_DEFAULT_SET: 560", run.StdOut);

        var heap = Heap(after);
        Assert.Equal((NewOutputFlags | 0x200000).ToString(), FlagsOf(heap, "560")); // 0x300801
        Assert.Equal("2048", FlagsOf(heap, "528"));   // the new INPUT tunnel: an input needs no value
        Assert.Equal("2049", FlagsOf(heap, "4891"));  // the user's, broken the same way, untouched
        Assert.Equal("2049", FlagsOf(heap, "4770"));  // an Event Structure's, untouched
    }

    [Fact]
    public async Task CheckModeReportsTheCandidateAndWritesNothing()
    {
        var (after, before) = Bundles();
        var bytes = File.ReadAllBytes(Path.Combine(after, $"{Base}_BDHb.xml"));
        var run = await RunAsync(after, Base, "--before", before, Base, "--check");
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("USE_DEFAULT_CANDIDATES: 560", run.StdOut);
        Assert.EndsWith("RESULT: would-change", run.StdOut.TrimEnd());
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(after, $"{Base}_BDHb.xml")));
    }

    [Fact]
    public async Task ASecondRunIsNothingToDo()
    {
        var (after, before) = Bundles();
        var first = await RunAsync(after, Base, "--before", before, Base);
        if (first is null) return;
        var second = await RunAsync(after, Base, "--before", before, Base);

        Assert.Equal(0, second!.ExitCode);
        Assert.EndsWith("RESULT: nothing-to-do", second.StdOut.TrimEnd());
    }

    [Fact]
    public async Task ATunnelWiredInEveryFrameNeedsNothing()
    {
        var (after, before) = Bundles(otherFrameWired: true);
        var run = await RunAsync(after, Base, "--before", before, Base);
        if (run is null) return;

        Assert.EndsWith("RESULT: nothing-to-do", run.StdOut.TrimEnd());
        Assert.Equal(NewOutputFlags.ToString(), FlagsOf(Heap(after), "560"));
    }

    /// <summary>
    /// "New" is "uid absent from the baseline", which holds only while LabVIEW kept every existing
    /// uid. A wire addition removes no tunnel, so a baseline tunnel missing afterwards means the
    /// two are not comparable - and then EVERYTHING would look new. The script must refuse.
    /// </summary>
    [Fact]
    public async Task ABaselineTunnelMissingAfterTheEditIsARefusalThatPatchesNothing()
    {
        var (after, before) = Bundles(extraInBaseline: "6100");
        var bytes = File.ReadAllBytes(Path.Combine(after, $"{Base}_BDHb.xml"));
        var run = await RunAsync(after, Base, "--before", before, Base);
        if (run is null) return;

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("REFUSED", run.StdOut);
        Assert.Contains("6100", run.StdOut);
        Assert.EndsWith("RESULT: refused", run.StdOut.TrimEnd());
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(after, $"{Base}_BDHb.xml")));
    }

    [Fact]
    public async Task WithoutABaselineItRefusesToRunAtAll()
    {
        var (after, _) = Bundles();
        var run = await RunAsync(after, Base);
        if (run is null) return;

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("--before is required", run.StdOut + run.StdErr);
    }
}
