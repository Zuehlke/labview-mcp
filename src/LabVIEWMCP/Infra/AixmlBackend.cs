namespace LabVIEWMcp.Infra;

/// <summary>
/// Which converter turns AIXML into a VI and back: NI's gRPC service (Nigel) or our own pylabview
/// composer (<see cref="AixmlComposer"/>). Both stay in the tree side by side - the user's request
/// of 2026-10-10 - and nothing switches between them on its own.
///
/// PRECEDENCE, highest first:
///   1. the `backend` argument of the call
///   2. `--aixml-backend` on the server's command line
///   3. the LABVIEWMCP_AIXML_BACKEND environment variable
///   4. pylabview when the LabVIEW in use is OLDER than 2026 - the user's rule of 2026-10-12, which
///      holds even where NI's service exists (LabVIEW 2025 has one). The composer WARNS there
///      (`versionWarning`): its skeleton stamps every VI LabVIEW 2026, which an older LabVIEW may
///      refuse - unmeasured (<see cref="AixmlComposer.WarningForOlderLabView"/>)
///   5. grpc
///
/// THE SERVER DEFAULT REACHES ONLY A TOOL THE CLIENT CALLED ITSELF. About forty places in this
/// server call lvai_generate_vi or the convert tools from C# - the class tools, the test
/// generators, the malleable route - and they depend on what only NI's converter does: class wires
/// it writes past validation, subVIs it resolves because they are loaded. A server-wide default of
/// pylabview must not reroute those in silence, so for them an omitted backend stays grpc.
/// <see cref="DiagnosingTool"/> records which tool the client invoked in <see cref="ClientTool"/>,
/// and a tool takes the server default only when that is its own name.
/// </summary>
internal static class AixmlBackend
{
    public const string Grpc = "grpc";
    public const string PyLabview = "pylabview";
    public const string Variable = "LABVIEWMCP_AIXML_BACKEND";
    public const string Flag = "--aixml-backend";

    public static readonly string[] Names = [Grpc, PyLabview];

    /// <summary>
    /// The first LabVIEW release on which NI's converter is the default. An older LabVIEW in use
    /// makes pylabview the default instead - the user's rule of 2026-10-12.
    /// </summary>
    public const int FirstGrpcRelease = 2026;

    /// <summary>
    /// The LabVIEW release in use: the newest RUNNING instance, else the newest INSTALLED - the one
    /// lvai_ensure_labview would start. Null when neither can be read (no LabVIEW on the machine,
    /// a process that refuses its module path); the older-LabVIEW rule then does not apply.
    /// </summary>
    internal static int? LabViewInUse()
    {
        try
        {
            var running = LabViewVersionStamp.RunningReleases();
            if (running.Count > 0) return running.Max(r => r.Release);
            return global::LabVIEWMcp.Grpc.LabViewLocator.Select(
                global::LabVIEWMcp.Grpc.LabViewLocator.Discover())?.Release;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string OlderLabViewSource(int release) =>
        $"LabVIEW {release} is older than {FirstGrpcRelease} - pylabview is the default there";

    /// <summary>The MCP tool the client invoked in this async flow; set by DiagnosingTool.</summary>
    public static readonly AsyncLocal<string?> ClientTool = new();

    /// <summary>
    /// The backend a tool chose for its WHOLE ROUTE, which every internal call it makes inherits
    /// when that call names none (since 2026-10-11). Without it a tool asked to run on pylabview
    /// had to pass `backend` to each of its thirty-odd inner calls by hand, and one forgotten call
    /// would quietly reach NI's service in the middle of a route promised to avoid it. Set only for
    /// pylabview: an inner call with no backend is grpc anyway. AsyncLocal, so it reaches the
    /// tool's own awaited calls and nothing outside them.
    /// </summary>
    public static readonly AsyncLocal<Choice?> Inherited = new();

    /// <summary>
    /// The folder helper VIs are built in for the backend in force: NI's converter writes
    /// <c>%TEMP%\LabVIEWMCP\helpers</c>, the composer <c>helpers-pylabview</c> - the folder
    /// <see cref="ComposedHelpers"/> uses - so a route on one backend never runs, or overwrites, a
    /// helper the other one built.
    /// </summary>
    public static string HelperFolder() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                     Inherited.Value?.Name == PyLabview ? "helpers-pylabview" : "helpers");

    /// <summary>
    /// What a verification export IS on the route in force, for an answer's wording: NI's
    /// converter on grpc, our reader on pylabview. A note saying "LabVIEW's own export" about the
    /// reader's output claims a second opinion that was never asked for.
    /// </summary>
    public static string ExportName() =>
        Inherited.Value?.Name == PyLabview
            ? "the reader's export of the saved file (vi2aixml.py, not NI's converter)"
            : "LabVIEW's own export";

    private static string? _flagValue;

    /// <summary>The backend named on the command line, already checked by <see cref="Normalise"/>.</summary>
    public static void SetFromCommandLine(string name) => _flagValue = name;

    /// <summary>What was chosen, and which of the four rules chose it - every answer says both.</summary>
    internal sealed record Choice(string Name, string Source);

    /// <summary>A known backend name in its canonical spelling, or null.</summary>
    public static string? Normalise(string? value) =>
        value?.Trim().ToLowerInvariant() is { } v && Names.Contains(v) ? v : null;

    /// <summary>
    /// The backend for a call to <paramref name="tool"/>. False, with the refusal in
    /// <paramref name="error"/>, when a name somewhere in the chain is not a backend - an unknown
    /// value is refused rather than ignored, because falling back to grpc would answer a question
    /// nobody asked.
    /// </summary>
    public static bool TryResolve(string? perCall, string tool, out Choice choice, out string error)
    {
        var resolved = TryResolve(perCall, tool, ClientTool.Value, _flagValue,
                                  Environment.GetEnvironmentVariable(Variable), Inherited.Value,
                                  LabViewInUse, out choice, out error);
        // A route on pylabview hands its choice down: every inner call this tool makes from here on
        // inherits it. Set in the CALLER's async context (this method is synchronous), so it ends
        // with the tool that resolved it.
        if (resolved && choice.Name == PyLabview && Inherited.Value is null)
            Inherited.Value = new Choice(PyLabview, $"inherited from {tool}");
        return resolved;
    }

    /// <summary>The same, with every input explicit - this is what the tests exercise.</summary>
    internal static bool TryResolve(string? perCall, string tool, string? clientTool,
                                    string? flagValue, string? environmentValue,
                                    out Choice choice, out string error) =>
        TryResolve(perCall, tool, clientTool, flagValue, environmentValue, inherited: null,
                   out choice, out error);

    /// <summary>The same, with the inherited choice explicit as well.</summary>
    internal static bool TryResolve(string? perCall, string tool, string? clientTool,
                                    string? flagValue, string? environmentValue, Choice? inherited,
                                    out Choice choice, out string error) =>
        TryResolve(perCall, tool, clientTool, flagValue, environmentValue, inherited,
                   () => null, out choice, out error);

    /// <summary>
    /// The same, with the LabVIEW release in use as well - asked only when every rule above it is
    /// silent, because finding it reads the process table.
    /// </summary>
    internal static bool TryResolve(string? perCall, string tool, string? clientTool,
                                    string? flagValue, string? environmentValue, Choice? inherited,
                                    Func<int?> labviewInUse, out Choice choice, out string error)
    {
        choice = new Choice(Grpc, "default");
        error = "";

        if (!string.IsNullOrWhiteSpace(perCall))
        {
            if (Normalise(perCall) is not { } named)
            {
                error = $"backend '{perCall}' is not one of: {string.Join(", ", Names)}.";
                return false;
            }
            choice = new Choice(named, "call");
            return true;
        }

        if (inherited is not null && !string.Equals(clientTool, tool, StringComparison.Ordinal))
        {
            choice = inherited;
            return true;
        }

        if (!string.Equals(clientTool, tool, StringComparison.Ordinal))
        {
            choice = new Choice(Grpc, "internal call - the server default reaches only a tool the client invoked");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(flagValue))
        {
            choice = new Choice(Normalise(flagValue) ?? Grpc, Flag);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            if (Normalise(environmentValue) is not { } fromEnvironment)
            {
                error = $"{Variable}='{environmentValue}' is not one of: {string.Join(", ", Names)}. " +
                        "Fix the variable in the server's configuration, or pass backend on the call.";
                return false;
            }
            choice = new Choice(fromEnvironment, Variable);
            return true;
        }

        if (labviewInUse() is { } release && release < FirstGrpcRelease)
            choice = new Choice(PyLabview, OlderLabViewSource(release));

        return true;
    }

    /// <summary>What a client is told when it names no backend: the server default, for status tools.</summary>
    public static Choice ServerDefault()
    {
        if (!string.IsNullOrWhiteSpace(_flagValue)) return new Choice(_flagValue, Flag);
        var environment = Environment.GetEnvironmentVariable(Variable);
        if (!string.IsNullOrWhiteSpace(environment))
            return new Choice(Normalise(environment) ?? $"invalid: {environment}", Variable);
        if (LabViewInUse() is { } release && release < FirstGrpcRelease)
            return new Choice(PyLabview, OlderLabViewSource(release));
        return new Choice(Grpc, "default");
    }

    /// <summary>The refusal for an unknown backend, in the shape every tool error has.</summary>
    public static string Refusal(string error) =>
        Json.Error("unknownBackend", error, new { accepted = Names });

    /// <summary>
    /// The parameter text for a tool that runs a whole ROUTE - a test generator, a swap, a runner -
    /// whose inner conversions, runs and helpers all follow the one choice.
    /// </summary>
    public const string RouteParameterDescription = """
        Which route the WHOLE call takes: "grpc" (NI's service, the default) or "pylabview" (no NI
        service at all). On pylabview every step inside this call follows - each VI it writes is
        COMPOSED, each helper composed into %TEMP%\LabVIEWMCP\helpers-pylabview and run over
        LabVIEW's ActiveX server, each export read by our own reader - and a step that has no such
        route yet is refused by name rather than sent to NI's service. LabVIEW must be running. Omit
        it to use the server's default. docs/aixml-backends.md.
        """;

    /// <summary>The parameter text, shared so the four tools describe it alike.</summary>
    public const string ParameterDescription = """
        Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or
        "pylabview" (no NI service at all: our own composer and reader write and read a VI with no
        LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over
        its ActiveX server). Omit it to use the server's default - --aixml-backend or
        LABVIEWMCP_AIXML_BACKEND, else pylabview when the LabVIEW in use is older than 2026, else
        grpc. Every answer names the backend that ran under
        `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.
        """;
}
